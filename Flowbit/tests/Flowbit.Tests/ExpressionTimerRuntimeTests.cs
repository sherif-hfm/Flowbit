using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class ExpressionTimerRuntimeTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // A Friday afternoon; calendars below work Monday to Friday, 09:00 to 17:00 UTC.
    private static readonly DateTimeOffset FridayAfternoon = new(2026, 11, 6, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ACatchAndABoundaryAreScheduledFromTheActivationsValuesAndKeepTheirDueTimes()
    {
        var suffix = Guid.NewGuid().ToString("N");
        await using var factory = FixedClockFactory();
        using var client = factory.CreateClient();
        var workflowId = await CreateWorkflowAsync(client, ReviewModel(
            $"expression-review-{suffix}",
            "${contractEnd}",
            "if(urgent, 'PT4H', 'P2D')"));

        var instanceId = await StartAsync(client, workflowId, new
        {
            contractEnd = "2026-12-01T09:30:00+01:00",
            urgent = true
        });

        var boundary = Assert.Single(await ReadTimersAsync(instanceId));
        Assert.Equal(FridayAfternoon.AddHours(4), boundary.NextDueAt);
        Assert.Equal(TimerScheduleKinds.Duration, boundary.ScheduleKind);
        Assert.Equal("if(urgent, 'PT4H', 'P2D')", boundary.ScheduleExpression);
        Assert.Equal(boundary.NextDueAt, await ReadJobDueAtAsync(boundary.Id));

        var task = Assert.Single(await ReadTasksAsync(instanceId));
        using (var take = await SendAsync(client, HttpMethod.Post, $"/api/user-tasks/{task.Id}/flows/201", new TakeFlowRequest(null)))
        {
            Assert.True(take.StatusCode == HttpStatusCode.OK, await take.Content.ReadAsStringAsync());
        }

        var wait = (await ReadTimersAsync(instanceId)).Single(timer => timer.TimerNodeId == 3);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 8, 30, 0, TimeSpan.Zero), wait.NextDueAt);
        Assert.Equal(TimerScheduleKinds.Date, wait.ScheduleKind);
        Assert.Equal("${contractEnd}", wait.ScheduleExpression);

        // A later change to the variable does not move the saved due time.
        using (var update = await SendAsync(
                   client,
                   HttpMethod.Patch,
                   $"/api/instances/{instanceId}/variables",
                   new UpdateInstanceVariablesRequest(
                       [new InstanceVariableWriteDto("contractEnd", JsonSerializer.SerializeToElement("2027-03-01T00:00:00Z"))],
                       "Corrected contract end",
                       null),
                   "operator",
                   "admin"))
        {
            Assert.True(update.StatusCode == HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        }

        Assert.Equal(
            new DateTimeOffset(2026, 12, 1, 8, 30, 0, TimeSpan.Zero),
            (await ReadTimersAsync(instanceId)).Single(timer => timer.TimerNodeId == 3).NextDueAt);
    }

    [Fact]
    public async Task AScriptWriteEarlierInTheTransitionIsVisibleAndTheWaitSetSharesItsActivationInstant()
    {
        var suffix = Guid.NewGuid().ToString("N");
        // Each clock reading is a second later than the last, so timers that
        // read the clock separately would not be exactly their durations apart.
        await using var factory = ClockFactory(new TickingClock(FridayAfternoon));
        using var client = factory.CreateClient();
        var workflowId = await CreateWorkflowAsync(client, ScriptThenWaitModel($"expression-script-{suffix}"));

        var instanceId = await StartAsync(client, workflowId, new { urgent = true });

        var timers = await ReadTimersAsync(instanceId);
        var wait = timers.Single(timer => timer.TimerNodeId == 3).NextDueAt;
        // The clock moved before the activation, which then counted the
        // script's PT1H rather than the declared default P3D.
        Assert.True(wait.AddHours(-1) > FridayAfternoon);
        Assert.Equal(wait.AddHours(1), timers.Single(timer => timer.TimerNodeId == 5).NextDueAt);
        Assert.Equal(wait.AddHours(2), timers.Single(timer => timer.TimerNodeId == 7).NextDueAt);
    }

    [Fact]
    public async Task AnUnusableResultRollsBackTheTransitionAndNamesNoValue()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(fixture.Client, HandOverModel($"expression-rollback-{suffix}"));
        var instanceId = await StartAsync(fixture.Client, workflowId, new { });
        var first = Assert.Single(await ReadTasksAsync(instanceId));

        using var take = await SendAsync(
            fixture.Client,
            HttpMethod.Post,
            $"/api/user-tasks/{first.Id}/flows/201",
            new TakeFlowRequest(new Dictionary<string, JsonElement>
            {
                ["sla"] = JsonSerializer.SerializeToElement("secret-value-123")
            }));

        Assert.Equal(HttpStatusCode.BadRequest, take.StatusCode);
        var body = await take.Content.ReadAsStringAsync();
        Assert.Contains(
            "Timer boundary event #5 timer.timeDuration cannot be scheduled: the expression gave text that is not a positive fixed-unit ISO-8601 duration.",
            body);
        Assert.DoesNotContain("secret-value-123", body);
        // The first boundary, the second task, and the submitted value were rolled back with it.
        Assert.Empty(await ReadTimersAsync(instanceId));
        var task = Assert.Single(await ReadTasksAsync(instanceId));
        Assert.Equal(first.Id, task.Id);
        Assert.Equal(UserTaskStatuses.Active, task.Status);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.InstanceVariables.AnyAsync(variable => variable.InstanceId == instanceId && variable.VariableName == "sla"));
    }

    [Fact]
    public async Task AnEvaluationFailureIsLoggedOnceWithoutTheValue()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var logs = new CapturingSink();
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(logs)));
        using var client = factory.CreateClient();
        // NCalc's own message for this failure repeats the text it could not convert.
        var workflowId = await CreateWorkflowAsync(client, HandOverModel($"expression-log-{suffix}", "sla * 2"));
        var instanceId = await StartAsync(client, workflowId, new { });
        var first = Assert.Single(await ReadTasksAsync(instanceId));

        using var take = await SendAsync(
            client,
            HttpMethod.Post,
            $"/api/user-tasks/{first.Id}/flows/201",
            new TakeFlowRequest(new Dictionary<string, JsonElement>
            {
                ["sla"] = JsonSerializer.SerializeToElement("secret-value-789")
            }));

        Assert.Equal(HttpStatusCode.BadRequest, take.StatusCode);
        var body = await take.Content.ReadAsStringAsync();
        Assert.Contains(
            "Timer boundary event #5 timer.timeDuration cannot be scheduled: the expression could not be evaluated (FormatException).",
            body);
        Assert.DoesNotContain("secret-value-789", body);
        var warning = Assert.Single(logs.Events, item =>
            item.MessageTemplate.Text.StartsWith("{Timer} could not be scheduled", StringComparison.Ordinal));
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.Null(warning.Exception);
        Assert.Equal("Timer boundary event #5 timer.timeDuration", Scalar(warning, "Timer"));
        Assert.Equal(instanceId, Scalar(warning, "InstanceId"));
        Assert.IsType<long>(Scalar(warning, "TokenId"));
        Assert.Equal("the expression could not be evaluated (FormatException)", Scalar(warning, "Problem"));
        Assert.DoesNotContain(logs.Events, item =>
            item.Level >= LogEventLevel.Warning
            && (item.RenderMessage().Contains("secret-value-789", StringComparison.Ordinal)
                || item.Exception?.ToString().Contains("secret-value-789", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task AMissingValueFailsTheStartAndNamesTheVariable()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(fixture.Client, ReviewModel(
            $"expression-missing-{suffix}",
            "${contractEnd}",
            "fallbackSla"));

        using var start = await SendAsync(
            fixture.Client,
            HttpMethod.Post,
            "/api/instances",
            StartRequest(workflowId, new { contractEnd = "2026-12-01T09:30:00Z", urgent = true }));

        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
        Assert.Contains(
            "Timer boundary event #4 timer.timeDuration cannot be scheduled: variable 'fallbackSla' has no value.",
            await start.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnOffsetFreeDateTimeValueFailsActivation()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowId = await CreateWorkflowAsync(fixture.Client, ReviewModel(
            $"expression-offset-{suffix}",
            "${contractEnd}",
            "if(urgent, 'PT4H', 'P2D')"));
        var instanceId = await StartAsync(fixture.Client, workflowId, new
        {
            contractEnd = "2026-12-01T09:30:00",
            urgent = true
        });
        var task = Assert.Single(await ReadTasksAsync(instanceId));

        using var take = await SendAsync(fixture.Client, HttpMethod.Post, $"/api/user-tasks/{task.Id}/flows/201", new TakeFlowRequest(null));

        Assert.Equal(HttpStatusCode.BadRequest, take.StatusCode);
        Assert.Contains(
            "Timer catch event #3 timer.timeDate cannot be scheduled: the expression gave text that is not an ISO-8601 timestamp with an explicit UTC offset.",
            await take.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnExpressionDurationCountsWorkingTimeInItsLiteralCalendar()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var calendar = await CreateCalendarAsync($"expression-calendar-{suffix}");
        await using var factory = FixedClockFactory();
        using var client = factory.CreateClient();
        var model = ReviewModel($"expression-calendar-{suffix}", "${contractEnd}", "if(urgent, 'PT2H', 'P1D')");
        model.FlowNodes.Single(node => node.Id == 4).Timer!.Calendar = calendar.Name;
        var workflowId = await CreateWorkflowAsync(client, model);

        var urgent = await StartAsync(client, workflowId, new { contractEnd = "2026-12-01T09:30:00Z", urgent = true });
        var routine = await StartAsync(client, workflowId, new { contractEnd = "2026-12-01T09:30:00Z", urgent = false });

        // Two working hours: one on Friday, one on Monday. One working day after Friday 16:00 is Monday 16:00.
        var urgentTimer = Assert.Single(await ReadTimersAsync(urgent));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), urgentTimer.NextDueAt);
        Assert.Equal(calendar.Name, urgentTimer.ScheduleCalendar);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 9, 16, 0, 0, TimeSpan.Zero),
            Assert.Single(await ReadTimersAsync(routine)).NextDueAt);

        var weeks = ReviewModel($"expression-calendar-weeks-{suffix}", "${contractEnd}", "if(urgent, 'P1W', 'P1D')");
        weeks.FlowNodes.Single(node => node.Id == 4).Timer!.Calendar = calendar.Name;
        var weeksId = await CreateWorkflowAsync(client, weeks);
        using var start = await SendAsync(
            client,
            HttpMethod.Post,
            "/api/instances",
            StartRequest(weeksId, new { contractEnd = "2026-12-01T09:30:00Z", urgent = true }));
        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
        Assert.Contains(
            $"Timer boundary event #4 timer.timeDuration cannot be scheduled: business calendar '{calendar.Name}' cannot count the duration it gave; use whole working days and working time, such as P5D or P1DT4H.",
            await start.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnInitiallyTrueInterruptingConditionalBoundaryCapturesTheHostBeforeItsTimersAreEvaluated()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = ReviewModel($"expression-conditional-{suffix}", "${contractEnd}", "fallbackSla");
        model.FlowNodes.Single(node => node.Id == 1).Variables.Add(new VariableModel
        {
            Id = 13,
            Name = "go",
            DataType = WorkflowVariableTypes.Boolean,
            Required = true
        });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 7,
            Name = "Go now",
            Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent,
            AttachedToRef = 2,
            Conditional = new ConditionalDefinitionModel { Condition = "go == true" }
        });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 6 });
        var workflowId = await CreateWorkflowAsync(fixture.Client, model);

        var instanceId = await StartAsync(fixture.Client, workflowId, new
        {
            contractEnd = "2026-12-01T09:30:00Z",
            urgent = true,
            go = true
        });

        Assert.Empty(await ReadTimersAsync(instanceId));
    }

    [Fact]
    public async Task AStagedHostSchedulesItsBoundaryFromTheInstanceVariablesAndRetriesAFailureWithoutValues()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var scheduled = await CreateWorkflowAsync(
            fixture.Client,
            StagedScriptModel($"expression-staged-{suffix}", "if(urgent, 'PT4H', 'P2D')"));
        var before = DateTimeOffset.UtcNow;
        var first = await StartAsync(fixture.Client, scheduled, new { urgent = true, fallbackSla = "ignored" });
        Assert.Empty(await ReadTimersAsync(first));

        await ProcessJobAsync(await ReadAsyncBeforeJobIdAsync(first));

        var boundary = Assert.Single(await ReadTimersAsync(first));
        Assert.Equal("if(urgent, 'PT4H', 'P2D')", boundary.ScheduleExpression);
        Assert.InRange(boundary.NextDueAt, before.AddHours(4), DateTimeOffset.UtcNow.AddHours(4));

        var failing = await CreateWorkflowAsync(
            fixture.Client,
            StagedScriptModel($"expression-staged-fail-{suffix}", "missingSla"));
        var second = await StartAsync(fixture.Client, failing, new { urgent = true, fallbackSla = "secret-value-456" });
        var jobId = await ReadAsyncBeforeJobIdAsync(second);

        await ProcessJobAsync(jobId);

        Assert.Empty(await ReadTimersAsync(second));
        await using var db = fixture.CreateDbContext();
        var job = await db.WorkflowJobs.AsNoTracking().SingleAsync(candidate => candidate.Id == jobId);
        Assert.Equal(WorkflowJobStatuses.Retry, job.Status);
        Assert.Equal(1, job.AttemptCount);
        var attempt = await db.WorkflowJobAttempts.AsNoTracking().SingleAsync(candidate => candidate.JobId == jobId);
        Assert.Equal("job_execution_failed", attempt.FailureCode);
        Assert.Equal(
            "Timer boundary event #4 timer.timeDuration cannot be scheduled: variable 'missingSla' has no value.",
            attempt.FailureDescription);

        // The retry would otherwise stay runnable in the shared database, where other tests lease jobs.
        using (var cancel = await SendAsync(fixture.Client, HttpMethod.Post, $"/api/instances/{second}/cancel", null, "operator", "admin"))
        {
            Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
        }
        await using var after = fixture.CreateDbContext();
        Assert.Equal(
            WorkflowJobStatuses.Cancelled,
            await after.WorkflowJobs.AsNoTracking().Where(candidate => candidate.Id == jobId).Select(candidate => candidate.Status).SingleAsync());
    }

    [Fact]
    public async Task AnAlreadyDueBoundaryInterruptsAStagedServiceTaskWhoseCallStillRuns()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var pastDeadline = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        fixture.ServiceInvocations.Reset();
        var call = fixture.ServiceInvocations.BlockNext("/send-reminder");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var workflowId = await CreateWorkflowAsync(fixture.Client, StagedServiceModel($"expression-due-service-{suffix}"));
            var instanceId = await StartAsync(fixture.Client, workflowId, new { deadline = "2020-01-01T00:00:00Z" });

            // The staged service saves its boundary, already due, before its call runs.
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var lease = await LeaseJobAsync(scope.ServiceProvider, await ReadAsyncBeforeJobIdAsync(instanceId));
            var processing = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
                .ProcessAsync(lease, timeout.Token);
            await call.WaitUntilEnteredAsync(timeout.Token);
            try
            {
                var boundary = Assert.Single(await ReadTimersAsync(instanceId));
                Assert.Equal(pastDeadline, boundary.NextDueAt);
                await using (var db = fixture.CreateDbContext())
                {
                    var job = await db.WorkflowJobs.AsNoTracking()
                        .SingleAsync(candidate => candidate.TimerSubscriptionId == boundary.Id, timeout.Token);
                    Assert.Equal(pastDeadline, job.DueAt);
                }

                // The Worker fires the boundary while the call is still running.
                await ProcessJobAsync(await ReadTimerJobIdAsync(boundary.Id));
            }
            finally
            {
                call.Release();
            }
            await processing.WaitAsync(timeout.Token);

            // The call ran once, outside a transaction; its late result was
            // discarded and the boundary path was taken.
            var invocation = Assert.Single(fixture.ServiceInvocations.Snapshot());
            Assert.False(invocation.HasTransaction);
            var followUp = Assert.Single(await ReadTasksAsync(instanceId));
            Assert.Equal(5, followUp.NodeId);
            Assert.Equal(UserTaskStatuses.Active, followUp.Status);
            await using var after = fixture.CreateDbContext();
            Assert.False(await after.InstanceVariables.AnyAsync(
                variable => variable.InstanceId == instanceId && variable.VariableName == "sent",
                timeout.Token));
            Assert.False(await after.WorkflowIncidents.AnyAsync(
                incident => incident.InstanceId == instanceId,
                timeout.Token));
        }
        finally
        {
            call.Release();
            fixture.ServiceInvocations.Reset();
        }
    }

    [Fact]
    public async Task AVersionSwitchKeepsAnOpenExpressionTimerOnlyForTheSameAuthoredText()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = ReviewModel($"expression-switch-{suffix}", "${contractEnd}", "if(urgent, 'PT4H', 'P2D')");
        var sourceId = await CreateWorkflowAsync(fixture.Client, model);
        var instanceId = await StartAsync(fixture.Client, sourceId, new { contractEnd = "2026-12-01T09:30:00Z", urgent = true });
        var saved = Assert.Single(await ReadTimersAsync(instanceId)).NextDueAt;

        model.FlowNodes.Single(node => node.Id == 4).Timer!.TimeDuration = "if(urgent, 'PT5H', 'P2D')";
        var changed = await PreviewAsync(instanceId, await CreateVersionAsync(sourceId, model));
        Assert.False(changed.Compatible);
        Assert.Contains(changed.Blockers, blocker => blocker.Code == WorkflowVersionCompatibilityCodes.OpenTimerContractChanged);

        model.FlowNodes.Single(node => node.Id == 4).Timer!.TimeDuration = "${if(urgent, 'PT4H', 'P2D')}";
        var wrapped = await PreviewAsync(instanceId, await CreateVersionAsync(sourceId, model));
        Assert.False(wrapped.Compatible);

        model.FlowNodes.Single(node => node.Id == 4).Timer!.TimeDuration = "if(urgent, 'PT4H', 'P2D')";
        var sameId = await CreateVersionAsync(sourceId, model);
        var same = await PreviewAsync(instanceId, sameId);
        Assert.True(same.Compatible, JsonSerializer.Serialize(same.Blockers));
        using (var change = await SendAsync(
                   fixture.Client,
                   HttpMethod.Post,
                   $"/api/instances/{instanceId}/version-change",
                   new ChangeInstanceVersionRequest(sameId, sourceId, same.ExpectedUpdatedAt, "Same timer contract"),
                   "operator",
                   "admin"))
        {
            Assert.True(change.StatusCode == HttpStatusCode.OK, await change.Content.ReadAsStringAsync());
        }

        var kept = Assert.Single(await ReadTimersAsync(instanceId));
        Assert.Equal(saved, kept.NextDueAt);
        Assert.Equal("if(urgent, 'PT4H', 'P2D')", kept.ScheduleExpression);
    }

    [Fact]
    public async Task ReactivationWarnsAboutAnAlreadyDueBoundary()
    {
        var suffix = Guid.NewGuid().ToString("N");
        await using var factory = FixedClockFactory();
        using var client = factory.CreateClient();
        var model = DeadlineModel($"expression-reactivation-due-{suffix}", "${deadline}");
        var workflowId = await CreateWorkflowAsync(client, model);
        var instanceId = await StartAsync(client, workflowId, new { deadline = "2026-11-01T09:00:00Z" });
        await CompleteReviewAsync(client, instanceId);

        var preview = await PreviewReactivationAsync(client, instanceId);

        Assert.True(preview.CanReactivate, JsonSerializer.Serialize(preview.Blockers));
        Assert.Contains(preview.Targets, target => target.NodeId == 2);
        var warning = Assert.Single(preview.Warnings, issue => issue.Code == "timer_boundary_already_due");
        Assert.Equal(
            "Timer boundary 'Review deadline' is already due, so it interrupts user task 'Review' as soon as the Worker runs.",
            warning.Message);
        Assert.Equal(2, warning.NodeId);
    }

    [Fact]
    public async Task ReactivationRefusesATargetWhoseBoundaryCannotBeResolved()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = DeadlineModel($"expression-reactivation-broken-{suffix}", "PT1H");
        var boundary = model.FlowNodes.Single(node => node.Id == 3);
        boundary.Timer = new TimerDefinitionModel { TimeDuration = "sla" };
        model.Variables.Add(new VariableModel
        {
            Id = 20,
            Name = "sla",
            DataType = WorkflowVariableTypes.String,
            DefaultValue = JsonSerializer.SerializeToElement("PT1H")
        });
        // After the review, a script stores a value the boundary cannot use.
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 6,
            Name = "Break the SLA",
            Type = BpmnFlowNodeTypes.ScriptTask,
            ScriptFormat = ScriptFormats.NCalc,
            Assignments = [new AssignmentModel { Variable = "sla", Expression = "'broken'" }]
        });
        model.SequenceFlows.Single(flow => flow.Id == 201).TargetRef = 6;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 4 });
        var workflowId = await CreateWorkflowAsync(fixture.Client, model);
        var instanceId = await StartAsync(fixture.Client, workflowId, new { deadline = "2030-01-01T09:00:00Z" });
        await CompleteReviewAsync(fixture.Client, instanceId);

        var preview = await PreviewReactivationAsync(fixture.Client, instanceId);

        Assert.False(preview.CanReactivate);
        Assert.Empty(preview.Targets);
        var blocker = Assert.Single(preview.Blockers, issue => issue.Code == "timer_boundary_unresolvable");
        Assert.Equal(
            "Timer boundary 'Review deadline' on user task 'Review' cannot be scheduled: the expression gave text that is not a positive fixed-unit ISO-8601 duration.",
            blocker.Message);

        using var commit = await SendAsync(
            fixture.Client,
            HttpMethod.Post,
            $"/api/instances/{instanceId}/reactivation",
            new ReactivateInstanceRequest(2, workflowId, preview.ExpectedUpdatedAt, "Try again"),
            "operator",
            "admin");
        Assert.Equal(HttpStatusCode.Conflict, commit.StatusCode);
    }

    [Fact]
    public async Task TheCatalogExamplesValidateTheirInputsAndScheduleFromThem()
    {
        var suffix = Guid.NewGuid().ToString("N");
        await using var factory = FixedClockFactory();
        using var client = factory.CreateClient();

        var contract = Example("examples/timers/07-contract-end-wait.json", suffix);
        var contractId = await CreateWorkflowAsync(client, contract);
        using (var offsetFree = await SendAsync(
                   client,
                   HttpMethod.Post,
                   "/api/instances",
                   StartRequest(contractId, new { contractEnd = "2026-12-01T09:30:00" })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, offsetFree.StatusCode);
            Assert.Contains("Variable 'contractEnd' failed validation", await offsetFree.Content.ReadAsStringAsync());
        }
        var waiting = await StartAsync(client, contractId, new { contractEnd = "2026-12-01T09:30:00+01:00" });
        Assert.Equal(
            new DateTimeOffset(2026, 12, 1, 8, 30, 0, TimeSpan.Zero),
            Assert.Single(await ReadTimersAsync(waiting)).NextDueAt);

        var urgency = await CreateWorkflowAsync(client, Example("examples/timers/08-urgency-review-deadline.json", suffix));
        Assert.Equal(
            FridayAfternoon.AddMinutes(2),
            Assert.Single(await ReadTimersAsync(await StartAsync(client, urgency, new { urgent = true }))).NextDueAt);

        // The script writes P2D; two working days after Friday 16:00 is Tuesday 16:00.
        var sla = await CreateWorkflowAsync(client, Example("examples/timers/09-working-day-sla-from-number.json", suffix));
        var timer = Assert.Single(await ReadTimersAsync(await StartAsync(client, sla, new { slaDays = 2 })));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 16, 0, 0, TimeSpan.Zero), timer.NextDueAt);
        Assert.Equal("examples.weekdays", timer.ScheduleCalendar);
    }

    private static WorkflowModel Example(string path, string suffix)
    {
        var model = JsonSerializer.Deserialize<WorkflowModel>(ExampleWorkflowData.Read(path))!;
        model.Id = $"{model.Id}-{suffix}";
        return model;
    }

    private static WorkflowModel ReviewModel(string workflowKey, string date, string duration) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Variables =
                [
                    new VariableModel { Id = 10, Name = "contractEnd", DataType = WorkflowVariableTypes.DateTime, Required = true },
                    new VariableModel { Id = 11, Name = "urgent", DataType = WorkflowVariableTypes.Boolean, Required = true },
                    new VariableModel { Id = 12, Name = "fallbackSla", DataType = WorkflowVariableTypes.String, Required = false }
                ]
            },
            new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Wait until contract end",
                Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent,
                Timer = new TimerDefinitionModel { TimeDate = date }
            },
            new FlowNodeModel
            {
                Id = 4,
                Name = "Review deadline",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 2,
                CancelActivity = true,
                Timer = new TimerDefinitionModel { TimeDuration = duration }
            },
            new FlowNodeModel { Id = 5, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 6, Name = "Escalated", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Approve", SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
            new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 6 }
        ]
    };

    private static WorkflowModel ScriptThenWaitModel(string workflowKey) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "wait",
                DataType = WorkflowVariableTypes.String,
                DefaultValue = JsonSerializer.SerializeToElement("P3D")
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Variables = [new VariableModel { Id = 10, Name = "urgent", DataType = WorkflowVariableTypes.Boolean, Required = true }]
            },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Choose the wait",
                Type = BpmnFlowNodeTypes.ScriptTask,
                ScriptFormat = ScriptFormats.NCalc,
                Assignments = [new AssignmentModel { Variable = "wait", Expression = "if(urgent, 'PT1H', 'P1D')" }]
            },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Wait",
                Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent,
                Timer = new TimerDefinitionModel { TimeDuration = "wait" }
            },
            new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel
            {
                Id = 5,
                Name = "Wait too long",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 3,
                CancelActivity = true,
                Timer = new TimerDefinitionModel { TimeDuration = "'PT2H'" }
            },
            new FlowNodeModel { Id = 6, Name = "Gave up", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel
            {
                Id = 7,
                Name = "Last call",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 3,
                CancelActivity = true,
                Timer = new TimerDefinitionModel { TimeDuration = "PT3H" }
            },
            new FlowNodeModel { Id = 8, Name = "Closed", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 },
            new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 },
            new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 8 }
        ]
    };

    private static WorkflowModel HandOverModel(string workflowKey, string deadline = "sla") => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Prepare", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 3, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel
            {
                Id = 4,
                Name = "Reminder",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 3,
                CancelActivity = true,
                Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
            },
            new FlowNodeModel
            {
                Id = 5,
                Name = "Deadline",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 3,
                CancelActivity = true,
                Timer = new TimerDefinitionModel { TimeDuration = deadline }
            },
            new FlowNodeModel { Id = 6, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 7, Name = "Reminded", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 8, Name = "Late", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel
            {
                Id = 201,
                Name = "Hand over",
                SourceRef = 2,
                TargetRef = 3,
                Variables = [new VariableModel { Id = 20, Name = "sla", DataType = WorkflowVariableTypes.String, Required = true }]
            },
            new SequenceFlowModel { Id = 301, Name = "Finish", SourceRef = 3, TargetRef = 6 },
            new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 7 },
            new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 8 }
        ]
    };

    private static WorkflowModel StagedScriptModel(string workflowKey, string duration) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "done",
                DataType = WorkflowVariableTypes.Boolean,
                DefaultValue = JsonSerializer.SerializeToElement(false)
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Variables =
                [
                    new VariableModel { Id = 10, Name = "urgent", DataType = WorkflowVariableTypes.Boolean, Required = true },
                    new VariableModel { Id = 11, Name = "fallbackSla", DataType = WorkflowVariableTypes.String, Required = false },
                    new VariableModel { Id = 12, Name = "missingSla", DataType = WorkflowVariableTypes.String, Required = false }
                ]
            },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Record",
                Type = BpmnFlowNodeTypes.ScriptTask,
                AsyncBefore = true,
                ScriptFormat = ScriptFormats.NCalc,
                Assignments = [new AssignmentModel { Variable = "done", Expression = "true" }]
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel
            {
                Id = 4,
                Name = "Too slow",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 2,
                CancelActivity = true,
                Timer = new TimerDefinitionModel { TimeDuration = duration }
            },
            new FlowNodeModel { Id = 5, Name = "Timed out", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 }
        ]
    };

    // A staged REST call guarded by a deadline that comes from start data.
    private static WorkflowModel StagedServiceModel(string workflowKey) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Variables = [new VariableModel { Id = 10, Name = "deadline", DataType = WorkflowVariableTypes.DateTime, Required = true }]
            },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Send reminder",
                Type = BpmnFlowNodeTypes.ServiceTask,
                AsyncBefore = true,
                Service = new ServiceTaskModel
                {
                    Url = "https://tests.local/send-reminder",
                    Method = "POST",
                    OutputMappings =
                    [
                        new ServiceOutputMappingModel
                        {
                            Variable = "sent",
                            Path = "sent",
                            DataType = WorkflowVariableTypes.Boolean,
                            IsArray = false,
                            Required = true
                        }
                    ]
                }
            },
            new FlowNodeModel { Id = 3, Name = "Sent", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel
            {
                Id = 4,
                Name = "Deadline passed",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 2,
                CancelActivity = true,
                Timer = new TimerDefinitionModel { TimeDate = "${deadline}" }
            },
            new FlowNodeModel { Id = 5, Name = "Follow up", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 6, Name = "Followed up", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 },
            new SequenceFlowModel { Id = 501, Name = "Done", SourceRef = 5, TargetRef = 6 }
        ]
    };

    private static WorkflowModel DeadlineModel(string workflowKey, string date) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Variables = [new VariableModel { Id = 10, Name = "deadline", DataType = WorkflowVariableTypes.DateTime, Required = true }]
            },
            new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Review deadline",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 2,
                CancelActivity = true,
                Timer = new TimerDefinitionModel { TimeDate = date }
            },
            new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 5, Name = "Late", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Approve", SourceRef = 2, TargetRef = 4 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 }
        ]
    };

    private async Task CompleteReviewAsync(HttpClient client, long instanceId)
    {
        var task = Assert.Single(await ReadTasksAsync(instanceId));
        using var take = await SendAsync(client, HttpMethod.Post, $"/api/user-tasks/{task.Id}/flows/201", new TakeFlowRequest(null));
        Assert.True(take.StatusCode == HttpStatusCode.OK, await take.Content.ReadAsStringAsync());
        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            WorkflowInstanceStatuses.Completed,
            await db.WorkflowInstances.Where(instance => instance.Id == instanceId).Select(instance => instance.Status).SingleAsync());
    }

    private static async Task<InstanceReactivationPreviewDto> PreviewReactivationAsync(HttpClient client, long instanceId)
    {
        using var response = await SendAsync(client, HttpMethod.Get, $"/api/instances/{instanceId}/reactivation", null, "operator", "admin");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<InstanceReactivationPreviewDto>(JsonOptions))!;
    }

    private async Task ProcessJobAsync(long jobId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var lease = await LeaseJobAsync(scope.ServiceProvider, jobId);
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
    }

    private async Task<WorkflowJobLeaseRecord> LeaseJobAsync(IServiceProvider services, long jobId)
    {
        await using (var promote = fixture.CreateDbContext())
        {
            // Due a minute ago, so the job is due even when the database clock runs behind this machine's.
            Assert.Equal(1, await promote.WorkflowJobs
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, int.MaxValue)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        }

        var leases = await services.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"expression-timer-test:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 1,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        return lease;
    }

    private async Task<long> ReadTimerJobIdAsync(long timerSubscriptionId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.AsNoTracking()
            .Where(job => job.TimerSubscriptionId == timerSubscriptionId)
            .Select(job => job.Id)
            .SingleAsync();
    }

    private async Task<long> ReadAsyncBeforeJobIdAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.AsNoTracking()
            .Where(job => job.InstanceId == instanceId && job.Kind == WorkflowJobKinds.AsyncBefore)
            .Select(job => job.Id)
            .SingleAsync();
    }

    private WebApplicationFactory<Program> FixedClockFactory() => ClockFactory(new FixedClock(FridayAfternoon));

    private WebApplicationFactory<Program> ClockFactory(TimeProvider clock) =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(clock);
        }));

    private static object? Scalar(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out var value) && value is ScalarValue scalar
            ? scalar.Value
            : null;

    private async Task<BusinessCalendarDto> CreateCalendarAsync(string name)
    {
        using var response = await SendAsync(fixture.Client, HttpMethod.Post, "/api/business-calendars", BusinessCalendarApiTests.Weekdays(name));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BusinessCalendarDto>(JsonOptions))!;
    }

    private static async Task<long> CreateWorkflowAsync(HttpClient client, WorkflowModel model)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    private async Task<long> CreateVersionAsync(long sourceId, WorkflowModel model)
    {
        using var response = await SendAsync(
            fixture.Client, HttpMethod.Put, $"/api/workflows/{sourceId}", new UpdateWorkflowRequest(model, true));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    private async Task<InstanceVersionChangePreviewDto> PreviewAsync(long instanceId, long targetWorkflowId)
    {
        using var response = await SendAsync(
            fixture.Client,
            HttpMethod.Post,
            $"/api/instances/{instanceId}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(targetWorkflowId),
            "operator",
            "admin");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<InstanceVersionChangePreviewDto>(JsonOptions))!;
    }

    private static StartInstanceRequest StartRequest(long workflowId, object variables) =>
        new(
            workflowId,
            null,
            null,
            JsonSerializer.SerializeToElement(variables, JsonOptions)
                .EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone()));

    private static async Task<long> StartAsync(HttpClient client, long workflowId, object variables)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/api/instances", StartRequest(workflowId, variables));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    private async Task<UserTaskEntity[]> ReadTasksAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.AsNoTracking()
            .Where(task => task.InstanceId == instanceId)
            .OrderBy(task => task.Id)
            .ToArrayAsync();
    }

    private async Task<TimerSubscriptionEntity[]> ReadTimersAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.TimerSubscriptions.AsNoTracking()
            .Where(timer => timer.InstanceId == instanceId)
            .OrderBy(timer => timer.Id)
            .ToArrayAsync();
    }

    private async Task<DateTimeOffset> ReadJobDueAtAsync(long timerSubscriptionId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.AsNoTracking()
            .Where(job => job.TimerSubscriptionId == timerSubscriptionId)
            .Select(job => job.DueAt)
            .SingleAsync();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body = null,
        string user = "expression-timer-user",
        params string[] roles)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        ApiTestAuth.Authorize(request, user, roles);
        return await client.SendAsync(request);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TickingClock(DateTimeOffset start) : TimeProvider
    {
        private long readings;

        public override DateTimeOffset GetUtcNow() =>
            start.AddSeconds(Interlocked.Increment(ref readings));
    }

    // Receives the API host's Serilog events, which it reads from its services.
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> events = new();

        public IReadOnlyCollection<LogEvent> Events => events.ToArray();

        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }
}
