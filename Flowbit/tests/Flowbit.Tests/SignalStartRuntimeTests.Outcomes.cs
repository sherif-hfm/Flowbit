using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// EVENT-04C start outcomes that need a version, a fault, a lost lease, or a held lock: the row of a start that
/// tried another default, a conflict and an unexpected error while starting, a lost final lease, a family without a
/// default, a cancel during a start, the routes that cannot start a signal start, version changes that add or remove
/// one, work after the start that runs as a job, and the summary's cut.
/// </summary>
public sealed partial class SignalStartRuntimeTests
{
    /// <summary>
    /// A start that fails on the default of its delivery names that version and its start event, which its
    /// description is about, not the version the throw froze.
    /// </summary>
    [Fact]
    public async Task FailedStartAfterADefaultChange_NamesTheVersionAndEventItTried()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var v1 = await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(signal, model.Id, Payload: Payload(new { recallNumber = "R-41" })));
        var renumbered = RecallModel(signal, model.Id);
        renumbered.FlowNodes[0].Id = 7;
        renumbered.SequenceFlows[0].SourceRef = 7;
        var v2 = await NewVersionAsync(v1, renumbered);
        await SetDefaultAsync(v2);
        Assert.Equal(v1, Assert.Single(await DeliveriesAsync(broadcast.Id)).WorkflowDefinitionId);

        await ProcessDeliveryJobsAsync(broadcast.Id);

        var failed = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.PayloadInvalid, v2, 7, (long?)null),
            (failed.Status, failed.ResultCode, failed.WorkflowDefinitionId, failed.SignalNodeId, failed.InstanceId));
        Assert.Equal("Required variable 'product' is missing.", failed.ResultDescription);
    }

    /// <summary>
    /// Routing from a signal start runs script and service tasks as Worker jobs, so a script that fails with the
    /// business key in its own error never reaches the delivery: the start is delivered, and the script waits.
    /// </summary>
    [Fact]
    public async Task AScriptAfterTheStart_RunsAsAJobSoItsErrorNeverReachesTheDelivery()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal, businessKey: true);
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 4, Name = "Check the recall", Type = BpmnFlowNodeTypes.ScriptTask,
            ScriptFormat = ScriptFormats.JavaScript,
            Script = "throw new Error('unknown recall ' + execution.getVariable('recallNumber'));"
        });
        model.SequenceFlows[0].TargetRef = 4;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 2 });
        await CreateAsync(model);

        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-SECRET-9", product = new { name = "Kettle" } })));
        await ProcessDeliveryJobsAsync(broadcast.Id);

        var delivery = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryStatuses.Delivered, SignalDeliveryResultCodes.Started, (string?)null),
            (delivery.Status, delivery.ResultCode, delivery.ResultDescription));
        var instanceId = delivery.InstanceId!.Value;
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.AsNoTracking().SingleAsync(item => item.InstanceId == instanceId && item.NodeId == 4);
            Assert.Equal((WorkflowJobKinds.AsyncBefore, WorkflowJobStatuses.Queued), (job.Kind, job.Status));
        }

        // The failing script is not this test's subject; cancelling the instance retires its job.
        using var cancel = await SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/cancel", new { });
        Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A conflict while starting fails the start with signal_delivery_rejected and the version it tried, creates
    /// nothing, and Retry failed starts it once.
    /// </summary>
    [Fact]
    public async Task ConflictWhileStarting_FailsAndARetryStartsOnce()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var v1 = await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-42", product = new { name = "Lamp" } })));

        await using (var host = FaultingRuntimeHost((method, args) =>
            method.Name == nameof(IWorkflowRuntimeRepository.AddTokenHistoryAsync)
                && args.Contains(InstanceHistoryNotes.SignalReceived)
                ? new WorkflowConflictException("Injected conflict while starting.")
                : null))
        {
            await ProcessDeliveryJobsAsync(broadcast.Id, host.Services);
        }

        var failed = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal(
            (SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.DeliveryRejected, "Injected conflict while starting.", v1, (long?)null),
            (failed.Status, failed.ResultCode, failed.ResultDescription, failed.WorkflowDefinitionId, failed.InstanceId));
        Assert.Equal(0, await FamilyInstanceCountAsync(model.Id));

        var finished = await BroadcastAsync(broadcast.Id);
        using (var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
                   new RetrySignalDeliveriesRequest(finished.UpdatedAt)))
        {
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
        }
        await ProcessDeliveryJobsAsync(broadcast.Id);

        Assert.Equal(SignalDeliveryResultCodes.Started, Assert.Single(await DeliveriesAsync(broadcast.Id)).ResultCode);
        Assert.Equal(1, await FamilyInstanceCountAsync(model.Id));
    }

    /// <summary>
    /// An unexpected error on the last attempt of the delivery job fails the start with delivery_error and creates
    /// nothing. The job allows one attempt here; an earlier attempt rethrows, so the Worker schedules a retry.
    /// </summary>
    [Fact]
    public async Task UnexpectedErrorOnTheLastAttempt_FailsTheStartWithDeliveryError()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-43", product = new { name = "Lamp" } })));
        await using (var db = fixture.CreateDbContext())
        {
            var jobIds = await db.SignalBroadcastJobs.AsNoTracking()
                .Where(link => link.BroadcastId == broadcast.Id)
                .Select(link => link.JobId!.Value)
                .ToListAsync();
            foreach (var job in await db.WorkflowJobs.Where(item => jobIds.Contains(item.Id)).ToListAsync())
            {
                job.MaxAttempts = 1;
            }
            await db.SaveChangesAsync();
        }

        await using (var host = FaultingRuntimeHost((method, args) =>
            method.Name == nameof(IWorkflowRuntimeRepository.AddTokenHistoryAsync)
                && args.Contains(InstanceHistoryNotes.SignalReceived)
                ? new InvalidOperationException("Injected unexpected failure.")
                : null))
        {
            await ProcessDeliveryJobsAsync(broadcast.Id, host.Services);
        }

        var failed = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal(
            (SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.DeliveryError, (long?)null),
            (failed.Status, failed.ResultCode, failed.InstanceId));
        Assert.Equal("The delivery failed unexpectedly on every attempt; see the Worker log.", failed.ResultDescription);
        Assert.Equal(0, await FamilyInstanceCountAsync(model.Id));
        Assert.Equal(SignalBroadcastStatuses.CompletedWithIssues, (await BroadcastAsync(broadcast.Id)).Status);
    }

    /// <summary>
    /// When the delivery job's final attempt loses its lease, the pending start fails with lease_exhausted and
    /// creates nothing; Retry failed then starts it once.
    /// </summary>
    [Fact]
    public async Task LostFinalLease_FailsThePendingStartAndARetryStartsOnce()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-44", product = new { name = "Fan" } })));

        await using (var db = fixture.CreateDbContext())
        {
            var jobId = await db.SignalBroadcastJobs.AsNoTracking()
                .Where(link => link.BroadcastId == broadcast.Id)
                .Select(link => link.JobId!.Value)
                .SingleAsync();
            var job = await db.WorkflowJobs.SingleAsync(item => item.Id == jobId);
            var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            job.Status = WorkflowJobStatuses.Running;
            job.AttemptCount = job.MaxAttempts;
            job.WorkerId = $"lost-signal-start-{Guid.NewGuid():N}";
            job.LeaseToken = Guid.NewGuid();
            job.LeaseGeneration = 1;
            job.LeaseExpiresAt = expiredAt;
            job.HeartbeatAt = expiredAt.AddMinutes(-1);
            job.StartedAt = expiredAt.AddMinutes(-2);
            db.WorkflowJobAttempts.Add(new WorkflowJobAttemptEntity
            {
                JobId = jobId,
                AttemptNumber = job.MaxAttempts,
                Status = WorkflowJobAttemptStatuses.Running,
                WorkerId = job.WorkerId,
                LeaseGeneration = 1,
                StartedAt = job.StartedAt.Value
            });
            await db.SaveChangesAsync();
        }

        // Leasing reconciles exhausted final leases first.
        await LeaseAndProcessOneAsync();

        var failed = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.LeaseExhausted, (long?)null),
            (failed.Status, failed.ResultCode, failed.InstanceId));
        Assert.Equal(0, await FamilyInstanceCountAsync(model.Id));
        var finished = await BroadcastAsync(broadcast.Id);
        Assert.Equal(SignalBroadcastStatuses.CompletedWithIssues, finished.Status);

        using (var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
                   new RetrySignalDeliveriesRequest(finished.UpdatedAt)))
        {
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
        }
        await ProcessDeliveryJobsAsync(broadcast.Id);

        Assert.Equal(SignalDeliveryResultCodes.Started, Assert.Single(await DeliveriesAsync(broadcast.Id)).ResultCode);
        Assert.Equal(1, await FamilyInstanceCountAsync(model.Id));
    }

    /// <summary>
    /// When the family has no published default by the time the delivery runs, the start is skipped and keeps the
    /// version the throw froze.
    /// </summary>
    [Fact]
    public async Task DefaultChangedToNone_SkipsTheStart()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var v1 = await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-45", product = new { name = "Fan" } })));

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionRepository>()
                .SetDefaultAsync(v1, false, CancellationToken.None));
        }
        Assert.Empty(await StartRowsAsync(model.Id));

        await ProcessDeliveryJobsAsync(broadcast.Id);

        var skipped = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.SubscriptionEnded, (long?)null, v1),
            (skipped.Status, skipped.ResultCode, skipped.InstanceId, skipped.WorkflowDefinitionId));
        Assert.Equal(0, await FamilyInstanceCountAsync(model.Id));
    }

    /// <summary>
    /// A cancel that arrives while a start holds its delivery row waits for the start, then leaves the started
    /// instance and its delivered row as they are.
    /// </summary>
    [Fact]
    public async Task CancelDuringAStart_WaitsForItAndLeavesTheStartedInstance()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal, businessKey: true);
        await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-HELD", product = new { name = "Kettle" } })));
        var deliveryId = Assert.Single(await DeliveriesAsync(broadcast.Id)).Id;

        // Another transaction holds an uncommitted claim on the key, so the start waits after it has locked its
        // delivery row.
        await using var blocker = new NpgsqlConnection(fixture.ConnectionString);
        await blocker.OpenAsync();
        await using var held = await blocker.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO flowbit.workflow_business_key_claims
                ("WorkflowKey", "BusinessKey", "IsPermanent", "ActiveInstanceId", "LastInstanceId")
            VALUES (@key, 'R-HELD', false, NULL, NULL)
            """, blocker, held))
        {
            insert.Parameters.AddWithValue("key", model.Id);
            await insert.ExecuteNonQueryAsync();
        }

        var delivering = ProcessDeliveryJobsAsync(broadcast.Id);
        await WaitUntilLockedAsync("signal_deliveries", deliveryId);
        var cancelling = SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/cancel",
            new CancelSignalBroadcastRequest("Not needed"));
        await WaitUntilLockedAsync("signal_broadcasts", broadcast.Id);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(cancelling.IsCompleted, "The cancel did not wait for the start that holds its delivery.");

        await held.RollbackAsync();
        await delivering;
        using (var cancel = await cancelling)
        {
            Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
        }

        var delivery = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryStatuses.Delivered, SignalDeliveryResultCodes.Started), (delivery.Status, delivery.ResultCode));
        Assert.NotNull(delivery.InstanceId);
        Assert.Equal(1, await FamilyInstanceCountAsync(model.Id));
    }

    /// <summary>
    /// No request selects a signal start: a user start finds no default start event and refuses the signal start by
    /// id, the message-start route finds no message start, and a call cannot target a family whose only entry is a
    /// signal start.
    /// </summary>
    [Fact]
    public async Task SystemOnly_NoUserStartMessageStartOrCallStartsASignalStart()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var definitionId = await CreateAsync(model);

        using (var plain = await SendAsync(HttpMethod.Post, "/api/instances", new StartInstanceRequest(definitionId, null, null, null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
            Assert.Contains("Workflow has no default start event.", await plain.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        using (var byId = await SendAsync(HttpMethod.Post, "/api/instances", new StartInstanceRequest(definitionId, null, 1, null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, byId.StatusCode);
            Assert.Contains("Flow node #1 is not a start event.", await byId.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        using (var message = new HttpRequestMessage(HttpMethod.Post, $"/api/workflows/{Uri.EscapeDataString(model.Id)}/message-start"))
        {
            message.Content = JsonContent.Create(new { recallNumber = "R-46" });
            message.Headers.Add("X-Client-Id", "anyone");
            message.Headers.Add("X-Client-Secret", "anything");
            using var response = await fixture.Client.SendAsync(message);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("has no message start event", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var caller = new WorkflowModel
        {
            Id = $"signal-start-caller-{Guid.NewGuid():N}",
            Name = "Caller",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2, Name = "Write a notice", Type = BpmnFlowNodeTypes.CallActivity,
                    CallActivity = new CallActivityModel { WorkflowKey = model.Id }
                },
                new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
            ]
        };
        using var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(caller, true));
        Assert.False(create.IsSuccessStatusCode, "A call to a family without a plain start was published.");
        Assert.Contains($"target '{model.Id}' has no default plain start event.", await create.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await FamilyInstanceCountAsync(model.Id));
    }

    /// <summary>
    /// A running instance changes to a version that removes its signal start, or adds a second one, without a
    /// blocker, and neither version moves the family's start subscription.
    /// </summary>
    [Fact]
    public async Task VersionChange_ThatRemovesOrAddsASignalStartHasNoBlocker()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var v1 = await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-47", product = new { name = "Kettle" } })));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var instanceId = Assert.Single(await DeliveriesAsync(broadcast.Id)).InstanceId!.Value;

        var removed = RecallModel(signal, model.Id);
        removed.FlowNodes[0] = new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent };
        removed.InitialEventId = 1;
        var withoutStart = await NewVersionAsync(v1, removed);

        var added = RecallModel(signal, model.Id);
        added.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5, Name = "Recall extended", Type = BpmnFlowNodeTypes.SignalStartEvent,
            Signal = new SignalEventModel { Name = UniqueSignal() }
        });
        added.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 2 });
        var withSecondStart = await NewVersionAsync(v1, added);

        foreach (var target in new[] { withoutStart, withSecondStart })
        {
            using var preview = await SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/version-change/preview",
                new PreviewInstanceVersionChangeRequest(target));
            Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
            var result = (await preview.Content.ReadFromJsonAsync<InstanceVersionChangePreviewDto>(JsonOptions))!;
            Assert.Empty(result.Blockers);
            Assert.True(result.Compatible);
        }

        var row = Assert.Single(await StartRowsAsync(model.Id));
        Assert.Equal((signal, v1), (row.SignalName, row.WorkflowDefinitionId));
    }

    /// <summary>
    /// The summary merges waits and starts before it cuts the list, so every row it returns counts both, whatever
    /// the limit. Family a waits once; family B waits once and starts on the name.
    /// </summary>
    [Fact]
    public async Task Summary_CutsTheMergedWaitsAndStartsAtTheLimit()
    {
        var signal = UniqueSignal();
        var suffix = Guid.NewGuid().ToString("N");
        var waitsOnly = WaiterModel(signal);
        waitsOnly.Id = $"a-signal-summary-{suffix}";
        var waitsAndStarts = WaiterModel(signal);
        waitsAndStarts.Id = $"B-signal-summary-{suffix}";
        waitsAndStarts.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5, Name = "Started", Type = BpmnFlowNodeTypes.SignalStartEvent, Signal = new SignalEventModel { Name = signal }
        });
        waitsAndStarts.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 3 });
        await StartAsync(await CreateAsync(waitsOnly));
        await StartAsync(await CreateAsync(waitsAndStarts));
        var expected = new Dictionary<string, (int Waits, int Starts)>
        {
            [waitsOnly.Id] = (1, 0),
            [waitsAndStarts.Id] = (1, 1)
        };

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var subscriptions = scope.ServiceProvider.GetRequiredService<ISignalSubscriptionRepository>();
        var filter = SignalBroadcastScope.SummaryFilter(null, null, null, out var problem);
        Assert.Null(problem);
        foreach (var limit in new[] { 1, 2, 3 })
        {
            var rows = await subscriptions.SummarizeActiveAsync(signal, filter!, limit, CancellationToken.None);
            Assert.Equal(Math.Min(limit, 2), rows.Count);
            Assert.All(rows, row => Assert.Equal(expected[row.WorkflowKey], (row.Count, row.StartCount)));
        }

        // A business-key or instance filter counts no start.
        var waiting = await DefinitionIdAsync(waitsAndStarts.Id);
        await using var db = fixture.CreateDbContext();
        var instanceId = await db.WorkflowInstances.Where(item => item.WorkflowDefinitionId == waiting).Select(item => item.Id).SingleAsync();
        var byInstance = SignalBroadcastScope.SummaryFilter(null, null, instanceId, out _);
        var only = Assert.Single(await subscriptions.SummarizeActiveAsync(signal, byInstance!, 10, CancellationToken.None));
        Assert.Equal((waitsAndStarts.Id, 1, 0), (only.WorkflowKey, only.Count, only.StartCount));
    }

    // Helpers

    /// <summary>A host whose runtime repository throws the given fault, or forwards every call.</summary>
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> FaultingRuntimeHost(
        Func<MethodInfo, object?[], Exception?> fault) =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IWorkflowRuntimeRepository>(provider =>
            {
                var proxy = DispatchProxy.Create<IWorkflowRuntimeRepository, FaultingProxy<IWorkflowRuntimeRepository>>();
                var faulting = (FaultingProxy<IWorkflowRuntimeRepository>)(object)proxy;
                faulting.Inner = provider.GetRequiredService<WorkflowRuntimeRepository>();
                faulting.Fault = fault;
                return proxy;
            })));

    /// <summary>Waits until another transaction holds a row lock on the row with the given id.</summary>
    private async Task WaitUntilLockedAsync(string table, long id)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var connection = new NpgsqlConnection(fixture.ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var probe = new NpgsqlCommand(
                $"SELECT 1 FROM flowbit.{table} WHERE \"Id\" = @id FOR UPDATE NOWAIT", connection, transaction);
            probe.Parameters.AddWithValue("id", id);
            try
            {
                await probe.ExecuteScalarAsync();
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                return;
            }

            await transaction.RollbackAsync();
            await Task.Delay(100);
        }

        Assert.Fail($"No transaction locked {table} #{id}.");
    }

    /// <summary>Forwards every call to <see cref="Inner"/> unless <see cref="Fault"/> returns an exception to throw.</summary>
    public class FaultingProxy<T> : DispatchProxy where T : class
    {
        public T Inner { get; set; } = null!;

        public Func<MethodInfo, object?[], Exception?> Fault { get; set; } = (_, _) => null;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (Fault(method!, args ?? []) is { } fault)
            {
                throw fault;
            }

            try
            {
                return method!.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
