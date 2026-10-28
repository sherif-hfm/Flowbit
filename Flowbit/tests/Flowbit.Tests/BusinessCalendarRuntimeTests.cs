extern alias FlowbitWorker;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using TimerStartReconciliationService = FlowbitWorker::Flowbit.Worker.TimerStartReconciliationService;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class BusinessCalendarRuntimeTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // A Friday afternoon. The calendars below work Monday to Friday, 09:00 to 17:00 UTC.
    private static readonly DateTimeOffset FridayAfternoon = new(2026, 11, 6, 16, 0, 0, TimeSpan.Zero);

    private static readonly BusinessCalendar Weekdays = BusinessCalendarTests.Calendar(
        "09:00-17:00",
        zone: TimeZoneInfo.FindSystemTimeZoneById("Etc/UTC"));

    [Fact]
    public async Task TimersAndDueDatesThatNameACalendarCountItsWorkingTime()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var calendar = await CreateCalendarAsync($"runtime-{suffix}");
        await using var factory = FixedClockFactory();
        using var client = factory.CreateClient();
        var workflowId = await CreateWorkflowAsync(client, ReviewModel($"calendar-runtime-{suffix}", calendar.Name));

        var instanceId = await StartAsync(client, workflowId);

        var task = Assert.Single(await ReadTasksAsync(instanceId));
        Assert.Equal(Count(task.CreatedAt, "PT10H"), task.DueAt);
        Assert.NotEqual(task.CreatedAt.AddHours(10), task.DueAt);
        var boundary = Assert.Single(await ReadTimersAsync(instanceId));
        // One working day after Friday 16:00 is Monday 16:00.
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 16, 0, 0, TimeSpan.Zero), boundary.NextDueAt);
        Assert.Equal(calendar.Name, boundary.ScheduleCalendar);
        Assert.Equal(boundary.NextDueAt, await ReadJobDueAtAsync(boundary.Id));

        using (var take = await SendAsync(client, HttpMethod.Post, $"/api/user-tasks/{task.Id}/flows/201", new TakeFlowRequest(null)))
        {
            Assert.True(take.StatusCode == HttpStatusCode.OK, await take.Content.ReadAsStringAsync());
        }

        // Two working hours: one on Friday, one on Monday.
        var wait = (await ReadTimersAsync(instanceId)).Single(timer => timer.TimerNodeId == 4);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), wait.NextDueAt);
        Assert.Equal(calendar.Name, wait.ScheduleCalendar);
        Assert.Equal(wait.NextDueAt, await ReadJobDueAtAsync(wait.Id));
    }

    [Fact]
    public async Task EditingACalendarKeepsSavedDueTimes()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var calendar = await CreateCalendarAsync($"runtime-edit-{suffix}");
        await using var factory = FixedClockFactory();
        using var client = factory.CreateClient();
        var workflowId = await CreateWorkflowAsync(client, WaitModel($"calendar-edit-{suffix}", calendar.Name));
        var first = await StartAsync(client, workflowId);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero),
            Assert.Single(await ReadTimersAsync(first)).NextDueAt);

        var weekday = BusinessCalendarApiTests.Periods("10:00-18:00");
        using (var update = await SendAsync(
                   fixture.Client,
                   HttpMethod.Put,
                   $"/api/business-calendars/{calendar.Id}",
                   new UpdateBusinessCalendarRequest(
                       "Etc/UTC",
                       new BusinessCalendarWeekDto(weekday, weekday, weekday, weekday, weekday),
                       null,
                       null,
                       calendar.UpdatedAt)))
        {
            Assert.True(update.StatusCode == HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        }

        // The saved due time stays; a timer that activates now uses the edited hours.
        Assert.Equal(
            new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero),
            Assert.Single(await ReadTimersAsync(first)).NextDueAt);
        var second = await StartAsync(client, workflowId);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 6, 18, 0, 0, TimeSpan.Zero),
            Assert.Single(await ReadTimersAsync(second)).NextDueAt);
    }

    [Theory]
    [InlineData(MultiInstanceModes.Parallel)]
    [InlineData(MultiInstanceModes.Sequential)]
    public async Task MultiInstanceChildrenGetTheWorkingTimeDueDateOfTheirExecution(string mode)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var calendar = await CreateCalendarAsync($"runtime-mi-{suffix}");
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        model.Id = $"calendar-mi-{suffix}";
        model.Name = $"Calendar MI {suffix}";
        var task = model.FlowNodes.Single(node => node.Id == 2);
        task.MultiInstance!.Mode = mode;
        task.DueDateExpression = "PT10H";
        task.DueDateCalendar = calendar.Name;
        var workflowId = await CreateWorkflowAsync(fixture.Client, model);
        var instanceId = await StartAsync(fixture.Client, workflowId, "starter", "Requester");

        using (var vote = await SendAsync(
                   fixture.Client,
                   HttpMethod.Post,
                   $"/api/instances/{instanceId}/flows/204",
                   new TakeFlowRequest(new Dictionary<string, JsonElement>
                   {
                       ["voters"] = JsonSerializer.SerializeToElement(new[] { "alice", "bob", "carol" })
                   }),
                   "manager",
                   "Manager"))
        {
            Assert.True(vote.StatusCode == HttpStatusCode.OK, await vote.Content.ReadAsStringAsync());
        }

        var children = (await ReadTasksAsync(instanceId)).Where(row => row.MultiInstanceExecutionId != null).ToArray();
        Assert.Equal(3, children.Length);
        Assert.All(children, child => Assert.Equal(Count(children[0].CreatedAt, "PT10H"), child.DueAt));
    }

    [Fact]
    public async Task AMissingCalendarFailsATimerAndLeavesADueDateEmpty()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var calendar = await CreateCalendarAsync($"runtime-missing-{suffix}");
        var model = ReviewModel($"calendar-missing-{suffix}", calendar.Name);
        model.FlowNodes.RemoveAll(node => node.Id is 3 or 6);
        model.SequenceFlows.RemoveAll(flow => flow.Id == 301);
        var workflowId = await CreateWorkflowAsync(fixture.Client, model);
        var first = await StartAsync(fixture.Client, workflowId);

        // Deleting the row directly bypasses the guard that refuses to delete a calendar in use.
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var delete = new NpgsqlCommand(
                         """DELETE FROM flowbit.business_calendars WHERE "Id" = @id""",
                         connection))
        {
            delete.Parameters.AddWithValue("id", calendar.Id);
            Assert.Equal(1, await delete.ExecuteNonQueryAsync());
        }

        var second = await StartAsync(fixture.Client, workflowId);
        Assert.Null(Assert.Single(await ReadTasksAsync(second)).DueAt);

        var task = Assert.Single(await ReadTasksAsync(first));
        using var take = await SendAsync(fixture.Client, HttpMethod.Post, $"/api/user-tasks/{task.Id}/flows/201", new TakeFlowRequest(null));
        Assert.Equal(HttpStatusCode.BadRequest, take.StatusCode);
        Assert.Contains(
            $"Timer event #4 cannot be scheduled: business calendar '{calendar.Name}' does not exist.",
            await take.Content.ReadAsStringAsync());
        Assert.Equal(UserTaskStatuses.Active, Assert.Single(await ReadTasksAsync(first)).Status);
    }

    [Fact]
    public async Task ATimersCalendarIsPartOfItsVersionSwitchContractButADueDateCalendarIsNot()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var first = await CreateCalendarAsync($"runtime-switch-a-{suffix}");
        var second = await CreateCalendarAsync($"runtime-switch-b-{suffix}");
        var model = ReviewModel($"calendar-switch-{suffix}", first.Name);
        var sourceId = await CreateWorkflowAsync(fixture.Client, model);
        var instanceId = await StartAsync(fixture.Client, sourceId);

        model.FlowNodes.Single(node => node.Id == 2).DueDateCalendar = second.Name;
        var dueDateOnly = await CreateVersionAsync(sourceId, model);
        var compatible = await PreviewAsync(instanceId, dueDateOnly);
        Assert.True(compatible.Compatible, JsonSerializer.Serialize(compatible.Blockers));

        model.FlowNodes.Single(node => node.Id == 3).Timer!.Calendar = second.Name;
        var timerChanged = await CreateVersionAsync(sourceId, model);
        var blocked = await PreviewAsync(instanceId, timerChanged);
        Assert.False(blocked.Compatible);
        Assert.Contains(
            blocked.Blockers,
            blocker => blocker.Code is "attached_timer_contract_changed" or "open_timer_contract_changed");
    }

    [Fact]
    public async Task ATimerStartDurationCountsWorkingTimeFromScheduleActivation()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var calendar = await CreateCalendarAsync($"runtime-start-{suffix}");
        var workflowKey = $"calendar-timer-start-{suffix}";
        await using (var context = fixture.CreateDbContext())
        using (var cache = new MemoryCache(new MemoryCacheOptions()))
        {
            await new WorkflowDefinitionRepository(context, cache).AddAsync(
                "Calendar timer start",
                new WorkflowModel
                {
                    Id = workflowKey,
                    Name = "Calendar timer start",
                    FlowNodes =
                    [
                        new FlowNodeModel
                        {
                            Id = 1,
                            Name = "Scheduled start",
                            Type = BpmnFlowNodeTypes.TimerStartEvent,
                            Timer = new TimerDefinitionModel { TimeDuration = "P1D", Calendar = calendar.Name }
                        }
                    ]
                },
                true,
                CancellationToken.None);
        }

        using var telemetry = new WorkerTelemetry();
        var service = new TimerStartReconciliationService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new WorkerOptions { TimerStartReconcileBatchSize = 1000 },
            TimeProvider.System,
            telemetry,
            NullLogger<TimerStartReconciliationService>.Instance);
        var reconcile = typeof(TimerStartReconciliationService).GetMethod(
                "ReconcileAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ReconcileAsync was not found.");
        await Assert.IsAssignableFrom<Task>(reconcile.Invoke(service, [CancellationToken.None]));

        await using var verification = fixture.CreateDbContext();
        var definition = await verification.WorkflowDefinitions.AsNoTracking()
            .SingleAsync(row => row.WorkflowKey == workflowKey);
        var subscription = await verification.TimerSubscriptions.AsNoTracking()
            .SingleAsync(row => row.WorkflowKey == workflowKey);
        Assert.Equal(calendar.Name, subscription.ScheduleCalendar);
        Assert.Equal(Count(definition.DefaultActivatedAt!.Value, "P1D"), subscription.NextDueAt);
        Assert.True(await verification.WorkflowDefinitionBusinessCalendars.AnyAsync(reference =>
            reference.WorkflowDefinitionId == definition.Id));
    }

    [Fact]
    public async Task ATimerStartThatCannotBeScheduledWaitsInAnIncidentThatARetrySchedulesAgain()
    {
        var suffix = Guid.NewGuid().ToString("N");
        // One working hour a week cannot count 30,000 working days within the 36,525 days a calendar searches.
        var calendar = await CreateCalendarAsync(new CreateBusinessCalendarRequest(
            $"runtime-monday-{suffix}",
            "Etc/UTC",
            new BusinessCalendarWeekDto(Monday: BusinessCalendarApiTests.Periods("09:00-10:00"))));
        var workflowKey = $"calendar-unschedulable-{suffix}";
        var workflowId = await CreateWorkflowAsync(fixture.Client, TimerStartModel(workflowKey, "P30000D", calendar.Name));
        var logger = new CapturingLogger<TimerStartReconciliationService>();

        await ReconcileTimerStartsAsync(logger);
        await ReconcileTimerStartsAsync(logger);

        long jobId;
        long incidentId;
        await using (var db = fixture.CreateDbContext())
        {
            var subscription = await db.TimerSubscriptions.AsNoTracking().SingleAsync(row => row.WorkflowKey == workflowKey);
            Assert.Equal(TimerSubscriptionStatuses.Paused, subscription.Status);
            Assert.Equal(calendar.Name, subscription.ScheduleCalendar);
            var job = await db.WorkflowJobs.AsNoTracking().SingleAsync(row => row.TimerSubscriptionId == subscription.Id);
            Assert.Equal(WorkflowJobKinds.TimerStart, job.Kind);
            Assert.Equal(WorkflowJobStatuses.Incident, job.Status);
            Assert.Null(job.ScheduledOccurrenceAt);
            var incident = await db.WorkflowIncidents.AsNoTracking().SingleAsync(row => row.JobId == job.Id);
            Assert.Equal(WorkflowIncidentTypes.TimerScheduleFailed, incident.Type);
            Assert.Equal(WorkflowIncidentStatuses.Open, incident.Status);
            Assert.Equal("Timer start #1 cannot be scheduled.", incident.Summary);
            Assert.Contains("needs more working time than the calendar has within 36,525 days", incident.Details);
            jobId = job.Id;
            incidentId = incident.Id;
        }

        // One error when the incident opened, and none while it waits.
        Assert.Single(logger.Errors(workflowId));

        // Once the calendar works every day it can count the duration, and a retry schedules the timer start again.
        var allDay = BusinessCalendarApiTests.Periods("00:00-24:00");
        using (var updated = await SendAsync(
                   fixture.Client,
                   HttpMethod.Put,
                   $"/api/business-calendars/{calendar.Id}",
                   new UpdateBusinessCalendarRequest(
                       "Etc/UTC",
                       new BusinessCalendarWeekDto(allDay, allDay, allDay, allDay, allDay, allDay, allDay),
                       null,
                       null,
                       calendar.UpdatedAt)))
        {
            Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
            Assert.NotNull(await jobs.RetryIncidentAsync(incidentId, "operator", DateTimeOffset.UtcNow, CancellationToken.None));
        }

        // Until the retried job runs, a pass leaves its subscription alone.
        await ReconcileTimerStartsAsync(logger);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(
                [WorkflowJobStatuses.Queued],
                await db.WorkflowJobs.AsNoTracking()
                    .Where(row => row.WorkflowDefinitionId == workflowId)
                    .Select(row => row.Status)
                    .ToListAsync());
        }

        await ProcessJobAsync(jobId);
        await ReconcileTimerStartsAsync(logger);

        await using var verification = fixture.CreateDbContext();
        var subscriptions = await verification.TimerSubscriptions.AsNoTracking()
            .Where(row => row.WorkflowKey == workflowKey)
            .OrderBy(row => row.Id)
            .ToListAsync();
        Assert.Equal([TimerSubscriptionStatuses.Cancelled, TimerSubscriptionStatuses.Active], subscriptions.Select(row => row.Status));
        var definition = await verification.WorkflowDefinitions.AsNoTracking().SingleAsync(row => row.Id == workflowId);
        var everyDay = BusinessCalendarTests.Calendar(
            "00:00-24:00",
            "00:00-24:00",
            "00:00-24:00",
            zone: TimeZoneInfo.FindSystemTimeZoneById("Etc/UTC"));
        Assert.True(TimerDefinitionRules.TryParseWorkingDuration("P30000D", out var duration));
        Assert.True(everyDay.TryAdd(definition.DefaultActivatedAt!.Value, duration, out var due, out _));
        Assert.Equal(due, subscriptions[1].NextDueAt);
        var occurrence = await verification.WorkflowJobs.AsNoTracking().SingleAsync(row => row.TimerSubscriptionId == subscriptions[1].Id);
        Assert.Equal(due, occurrence.ScheduledOccurrenceAt);
        Assert.Equal(WorkflowJobStatuses.Completed, (await verification.WorkflowJobs.AsNoTracking().SingleAsync(row => row.Id == jobId)).Status);
        Assert.Equal(
            WorkflowIncidentStatuses.Resolved,
            (await verification.WorkflowIncidents.AsNoTracking().SingleAsync(row => row.Id == incidentId)).Status);
        Assert.Single(logger.Errors(workflowId));
    }

    [Fact]
    public async Task AScheduleIncidentEndsWhenAnotherVersionBecomesTheDefault()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var calendar = await CreateCalendarAsync(new CreateBusinessCalendarRequest(
            $"runtime-monday-default-{suffix}",
            "Etc/UTC",
            new BusinessCalendarWeekDto(Monday: BusinessCalendarApiTests.Periods("09:00-10:00"))));
        var workflowKey = $"calendar-unschedulable-default-{suffix}";
        var workflowId = await CreateWorkflowAsync(fixture.Client, TimerStartModel(workflowKey, "P30000D", calendar.Name));
        await ReconcileTimerStartsAsync(new CapturingLogger<TimerStartReconciliationService>());

        var laterId = await CreateVersionAsync(workflowId, new WorkflowModel
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
        });
        using (var setDefault = await SendAsync(fixture.Client, HttpMethod.Post, $"/api/workflows/{laterId}/set-default"))
        {
            Assert.True(setDefault.IsSuccessStatusCode, await setDefault.Content.ReadAsStringAsync());
        }

        await ReconcileTimerStartsAsync(new CapturingLogger<TimerStartReconciliationService>());

        await using var verification = fixture.CreateDbContext();
        var subscription = await verification.TimerSubscriptions.AsNoTracking().SingleAsync(row => row.WorkflowKey == workflowKey);
        Assert.Equal(TimerSubscriptionStatuses.Cancelled, subscription.Status);
        var job = await verification.WorkflowJobs.AsNoTracking().SingleAsync(row => row.TimerSubscriptionId == subscription.Id);
        Assert.Equal(WorkflowJobStatuses.Cancelled, job.Status);
        var incident = await verification.WorkflowIncidents.AsNoTracking().SingleAsync(row => row.JobId == job.Id);
        Assert.Equal(WorkflowIncidentStatuses.Resolved, incident.Status);
        Assert.StartsWith("system:", incident.ResolvedBy);
    }

    private static WorkflowModel TimerStartModel(string workflowKey, string duration, string calendar) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Scheduled start",
                Type = BpmnFlowNodeTypes.TimerStartEvent,
                Timer = new TimerDefinitionModel { TimeDuration = duration, Calendar = calendar }
            },
            new FlowNodeModel { Id = 2, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
    };

    private async Task ReconcileTimerStartsAsync(ILogger<TimerStartReconciliationService> logger)
    {
        using var telemetry = new WorkerTelemetry();
        var service = new TimerStartReconciliationService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new WorkerOptions { TimerStartReconcileBatchSize = 1000 },
            TimeProvider.System,
            telemetry,
            logger);
        var reconcile = typeof(TimerStartReconciliationService).GetMethod(
                "ReconcileAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ReconcileAsync was not found.");
        await Assert.IsAssignableFrom<Task>(reconcile.Invoke(service, [CancellationToken.None]));
    }

    private async Task ProcessJobAsync(long jobId)
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

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"calendar-test:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 0,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
    }

    private static WorkflowModel ReviewModel(string workflowKey, string calendar) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Review",
                Type = BpmnFlowNodeTypes.UserTask,
                DueDateExpression = "PT10H",
                DueDateCalendar = calendar
            },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Escalate after one working day",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 2,
                Timer = new TimerDefinitionModel { TimeDuration = "P1D", Calendar = calendar }
            },
            new FlowNodeModel
            {
                Id = 4,
                Name = "Wait two working hours",
                Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent,
                Timer = new TimerDefinitionModel { TimeDuration = "PT2H", Calendar = calendar }
            },
            new FlowNodeModel { Id = 5, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 6, Name = "Escalated", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Wait", SourceRef = 2, TargetRef = 4 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 6 },
            new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 }
        ]
    };

    private static WorkflowModel WaitModel(string workflowKey, string calendar) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Wait two working hours",
                Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent,
                Timer = new TimerDefinitionModel { TimeDuration = "PT2H", Calendar = calendar }
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
        ]
    };

    private static DateTimeOffset Count(DateTimeOffset start, string duration)
    {
        Assert.True(TimerDefinitionRules.TryParseWorkingDuration(duration, out var working));
        Assert.True(Weekdays.TryAdd(start, working, out var due, out var problem), problem);
        return due;
    }

    private WebApplicationFactory<Program> FixedClockFactory() =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedClock(FridayAfternoon));
        }));

    private Task<BusinessCalendarDto> CreateCalendarAsync(string name) =>
        CreateCalendarAsync(BusinessCalendarApiTests.Weekdays(name));

    private async Task<BusinessCalendarDto> CreateCalendarAsync(CreateBusinessCalendarRequest request)
    {
        using var response = await SendAsync(fixture.Client, HttpMethod.Post, "/api/business-calendars", request);
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

    private static async Task<long> StartAsync(
        HttpClient client,
        long workflowId,
        string user = "calendar-starter",
        params string[] roles)
    {
        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(workflowId, null, null, null),
            user,
            roles);
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
        string user = "settings-admin",
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> entries = [];

        // Errors about one workflow definition; passes also reconcile other tests' timer starts.
        public IReadOnlyList<string> Errors(long workflowDefinitionId)
        {
            lock (entries)
            {
                return entries
                    .Where(entry => entry.Level == LogLevel.Error
                        && entry.Message.Contains($"workflow definition {workflowDefinitionId} ", StringComparison.Ordinal))
                    .Select(entry => entry.Message)
                    .ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (entries)
            {
                entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
