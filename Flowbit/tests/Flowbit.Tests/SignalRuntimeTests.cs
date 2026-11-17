using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// A signal catch subscribes when a token arrives; a throw freezes the active subscriptions
/// in one statement; the Worker delivers each one in its own transaction, mapping the
/// payload and moving the token, exactly once.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class SignalRuntimeTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Catch_SubscribesAndADeliveredSignalMapsThePayloadAndContinues()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        var instance = await StartAsync(model);

        var subscription = Assert.Single(await SubscriptionsAsync(instance.Id));
        Assert.Equal((signal, SignalSubscriptionStatuses.Active, 2, (int?)null, true),
            (subscription.SignalName, subscription.Status, subscription.SignalNodeId,
             subscription.AttachedToNodeId, subscription.CancelActivity));

        var broadcast = await ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = 7 })));
        Assert.Equal((SignalBroadcastStatuses.Delivering, 1, 1),
            (broadcast.Status, broadcast.TotalDeliveryCount, broadcast.PendingCount));
        Assert.Equal(model.Id, Assert.Single(broadcast.RecipientsByWorkflowKey).WorkflowKey);

        await ProcessDeliveryJobsAsync(broadcast.Id);

        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Completed, 1, 0), (finished.Status, finished.DeliveredCount, finished.PendingCount));
        var delivery = Assert.Single((await DeliveriesAsync(broadcast.Id)).Items);
        Assert.Equal((SignalDeliveryStatuses.Delivered, SignalDeliveryResultCodes.Caught),
            (delivery.Status, delivery.ResultCode));

        await using var db = fixture.CreateDbContext();
        Assert.Equal(3, await db.UserTasks.Where(task => task.InstanceId == instance.Id
            && task.Status == UserTaskStatuses.Active).Select(task => task.NodeId).SingleAsync());
        var value = await db.InstanceVariableCurrentValues.AsNoTracking()
            .SingleAsync(variable => variable.InstanceId == instance.Id && variable.VariableName == "policyVersion");
        Assert.Equal(7, value.ValueJson.RootElement.GetInt32());
        var history = await db.InstanceHistory.AsNoTracking()
            .SingleAsync(item => item.InstanceId == instance.Id && item.Note == InstanceHistoryNotes.SignalReceived);
        Assert.Equal((2, 3, "signal"), (history.FromStepId, history.ToStepId, history.PerformedBy));
        var visit = await db.NodeExecutions.AsNoTracking()
            .SingleAsync(item => item.InstanceId == instance.Id && item.NodeId == 2);
        Assert.Equal((NodeExecutionStatuses.Completed, NodeExecutionCompletionReasons.SignalReceived),
            (visit.Status, visit.CompletionReason));
        Assert.Equal(SignalSubscriptionStatuses.Completed,
            (await db.SignalSubscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id)).Status);
    }

    [Fact]
    public async Task Throw_ReachesOnlySubscriptionsThatExistWhenItIsThrown()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        await CreateAsync(model);

        var early = await ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = 1 })));
        Assert.Equal((SignalBroadcastStatuses.Completed, 0), (early.Status, early.TotalDeliveryCount));

        var instance = await StartExistingAsync(model.Id);
        await ProcessDeliveryJobsAsync(early.Id);
        Assert.Equal(SignalSubscriptionStatuses.Active, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);
    }

    [Fact]
    public async Task PayloadThatFailsAMapping_FailsOnlyThatDeliveryAndLeavesTheWait()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        var instance = await StartAsync(model);

        var broadcast = await ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = "seven" })));
        await ProcessDeliveryJobsAsync(broadcast.Id);

        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.CompletedWithIssues, 1), (finished.Status, finished.FailedCount));
        var delivery = Assert.Single((await DeliveriesAsync(broadcast.Id)).Items);
        Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.PayloadInvalid),
            (delivery.Status, delivery.ResultCode));
        Assert.Contains("policyVersion", delivery.ResultDescription);
        Assert.Equal(SignalSubscriptionStatuses.Active, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.InstanceVariableCurrentValues.AnyAsync(variable =>
            variable.InstanceId == instance.Id && variable.VariableName == "policyVersion"));
    }

    [Fact]
    public async Task InconsistentSubscription_FailsOnlyItsDeliveryWithoutRetrying()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        var broken = await StartAsync(model);
        var healthy = await StartExistingAsync(model.Id);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = 4 })));

        // An active subscription whose activation no longer matches its token breaks an invariant.
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE flowbit.signal_subscriptions SET \"ActivationId\" = {Guid.NewGuid()} WHERE \"InstanceId\" = {broken.Id}");
        }
        await ProcessDeliveryJobsAsync(broadcast.Id);

        var deliveries = (await DeliveriesAsync(broadcast.Id)).Items;
        var failed = Assert.Single(deliveries, delivery => delivery.InstanceId == broken.Id);
        Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.DeliveryError, 1),
            (failed.Status, failed.ResultCode, failed.AttemptCount));
        Assert.Contains("is active, but its token is not waiting", failed.ResultDescription);
        Assert.Equal(SignalDeliveryResultCodes.Caught,
            Assert.Single(deliveries, delivery => delivery.InstanceId == healthy.Id).ResultCode);
        Assert.Equal(SignalBroadcastStatuses.CompletedWithIssues, (await GetBroadcastAsync(broadcast.Id)).Status);
        await using var check = fixture.CreateDbContext();
        Assert.False(await check.WorkflowIncidents.AnyAsync(incident => incident.InstanceId == broken.Id));
    }

    [Fact]
    public async Task InterruptingBoundary_CancelsTheHostAndContinuesWithTheMappedValue()
    {
        var signal = UniqueSignal();
        var instance = await StartAsync(BoundaryModel(signal, cancelActivity: true));
        var subscription = Assert.Single(await SubscriptionsAsync(instance.Id));
        Assert.Equal((2, 3, true), (subscription.AttachedToNodeId, subscription.SignalNodeId, subscription.CancelActivity));

        var broadcast = await ThrowAsync(new ThrowSignalRequest(
            signal, null, AllWorkflows: true, Payload: JsonSerializer.SerializeToElement(new { reason = "lot 7" })));
        await ProcessDeliveryJobsAsync(broadcast.Id);

        Assert.Equal(SignalDeliveryResultCodes.Interrupted, Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).ResultCode);
        Assert.Equal(5, await ActiveTaskNodeAsync(instance.Id));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(UserTaskStatuses.Cancelled, await db.UserTasks
            .Where(task => task.InstanceId == instance.Id && task.NodeId == 2).Select(task => task.Status).SingleAsync());
        Assert.Equal("lot 7", (await db.InstanceVariableCurrentValues.SingleAsync(variable =>
            variable.InstanceId == instance.Id && variable.VariableName == "recallReason")).ValueJson.RootElement.GetString());
        Assert.Equal(SignalSubscriptionStatuses.Completed, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);
        Assert.Equal(NodeExecutionCompletionReasons.SignalReceived, await db.NodeExecutions
            .Where(visit => visit.InstanceId == instance.Id && visit.NodeId == 2)
            .Select(visit => visit.CompletionReason).SingleAsync());
        Assert.Equal(NodeExecutionCompletionReasons.SignalReceived, await db.NodeExecutions
            .Where(visit => visit.InstanceId == instance.Id && visit.NodeId == 3)
            .Select(visit => visit.CompletionReason).SingleAsync());
        Assert.True(await db.InstanceHistory.AnyAsync(item =>
            item.InstanceId == instance.Id && item.Note == InstanceHistoryNotes.SignalReceived && item.FromStepId == 3));
    }

    [Fact]
    public async Task NonInterruptingBoundary_AddsABranchPerSignalAndStaysSubscribedUntilTheHostEnds()
    {
        var signal = UniqueSignal();
        var instance = await StartAsync(BoundaryModel(signal, cancelActivity: false));

        foreach (var reason in new[] { "first", "second" })
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(
                signal, null, AllWorkflows: true, Payload: JsonSerializer.SerializeToElement(new { reason })));
            await ProcessDeliveryJobsAsync(broadcast.Id);
            Assert.Equal(SignalDeliveryResultCodes.Branched,
                Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).ResultCode);
        }

        var subscription = Assert.Single(await SubscriptionsAsync(instance.Id));
        Assert.Equal((SignalSubscriptionStatuses.Active, 2L), (subscription.Status, subscription.Occurrence));
        await using (var db = fixture.CreateDbContext())
        {
            var active = await db.UserTasks
                .Where(task => task.InstanceId == instance.Id && task.Status == UserTaskStatuses.Active)
                .Select(task => task.NodeId).OrderBy(nodeId => nodeId).ToListAsync();
            Assert.Equal([2, 5, 5], active);
        }

        // Finishing the host ends its boundary's wait.
        await TakeAsync(instance.Id, 2, 201);
        Assert.Equal(SignalSubscriptionStatuses.Cancelled, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);
    }

    [Fact]
    public async Task NonInterruptingBoundary_AtTheTokenLimitFailsAndStaysSubscribed()
    {
        var signal = UniqueSignal();
        var instance = await StartAsync(BoundaryModel(signal, cancelActivity: false));
        string? previous;
        await using (var db = fixture.CreateDbContext())
        {
            previous = await db.EngineSettings
                .Where(setting => setting.Namespace == "Workflow.Gateway" && setting.Key == "MaxActiveTokens")
                .Select(setting => setting.Value).SingleOrDefaultAsync();
        }

        await SetMaxActiveTokensAsync("1");
        try
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(
                signal, null, AllWorkflows: true, Payload: JsonSerializer.SerializeToElement(new { reason = "full" })));
            await ProcessDeliveryJobsAsync(broadcast.Id);

            var delivery = Assert.Single((await DeliveriesAsync(broadcast.Id)).Items);
            Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.TokenLimit),
                (delivery.Status, delivery.ResultCode));
            var subscription = Assert.Single(await SubscriptionsAsync(instance.Id));
            Assert.Equal((SignalSubscriptionStatuses.Active, 0L), (subscription.Status, subscription.Occurrence));
            Assert.Equal(2, await ActiveTaskNodeAsync(instance.Id));
        }
        finally
        {
            await SetMaxActiveTokensAsync(previous);
        }
    }

    [Fact]
    public async Task BoundaryOnASignalCatch_InterruptsTheCatchAndEndsItsWait()
    {
        var signal = UniqueSignal();
        var recall = UniqueSignal();
        var model = CatchModel(signal);
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5,
            Name = "Recall",
            Type = BpmnFlowNodeTypes.SignalBoundaryEvent,
            AttachedToRef = 2,
            Signal = new SignalEventModel { Name = recall }
        });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 4 });
        var instance = await StartAsync(model);
        Assert.Equal(2, (await SubscriptionsAsync(instance.Id)).Count(item => item.Status == SignalSubscriptionStatuses.Active));

        var broadcast = await ThrowAsync(new ThrowSignalRequest(recall, model.Id));
        await ProcessDeliveryJobsAsync(broadcast.Id);

        Assert.Equal(SignalDeliveryResultCodes.Interrupted, Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).ResultCode);
        var subscriptions = await SubscriptionsAsync(instance.Id);
        Assert.Equal(SignalSubscriptionStatuses.Cancelled, subscriptions.Single(item => item.SignalNodeId == 2).Status);
        Assert.Equal(SignalSubscriptionStatuses.Completed, subscriptions.Single(item => item.SignalNodeId == 5).Status);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, await db.WorkflowInstances
            .Where(item => item.Id == instance.Id).Select(item => item.Status).SingleAsync());
    }

    [Fact]
    public async Task VersionChange_BlocksAnActiveHostThatGainsASignalBoundary()
    {
        var signal = UniqueSignal();
        var withoutBoundary = BoundaryModel(signal, cancelActivity: true);
        withoutBoundary.FlowNodes.RemoveAll(node => node.Id == 3);
        withoutBoundary.SequenceFlows.RemoveAll(flow => flow.SourceRef == 3);
        withoutBoundary.FlowNodes.RemoveAll(node => node.Id is 5 or 6);
        withoutBoundary.SequenceFlows.RemoveAll(flow => flow.SourceRef == 5);
        var v1 = await CreateAsync(withoutBoundary);
        var instance = await StartDefinitionAsync(v1.Id);

        var withBoundary = BoundaryModel(signal, cancelActivity: true);
        withBoundary.Id = withoutBoundary.Id;
        var v2 = await NewVersionAsync(v1.Id, withBoundary);
        var preview = await PreviewVersionChangeAsync(instance.Id, v2.Id);
        Assert.Contains(preview.Blockers, issue =>
            issue.Code == WorkflowVersionCompatibilityCodes.AttachedSignalBoundaryContractChanged && issue.NodeId == 2);
    }

    [Fact]
    public async Task InterruptingBoundaryOnAnAsyncBeforeTask_CancelsItsJobAndContinues()
    {
        var signal = UniqueSignal();
        var model = BoundaryModel(signal, cancelActivity: true);
        var host = model.FlowNodes.Single(node => node.Id == 2);
        host.Type = BpmnFlowNodeTypes.Task;
        host.Name = "Archive";
        host.AsyncBefore = true;
        model.SequenceFlows.Single(flow => flow.Id == 201).Name = null;
        var instance = await StartAsync(model);
        Assert.Equal(SignalSubscriptionStatuses.Active, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);
        await using (var jobsDb = fixture.CreateDbContext())
        {
            // Keeps the host's job from being leased before the delivery job.
            Assert.Equal(1, await jobsDb.WorkflowJobs.Where(job => job.InstanceId == instance.Id && job.NodeId == 2)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddHours(1))));
        }

        var broadcast = await ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { reason = "stop" })));
        await ProcessDeliveryJobsAsync(broadcast.Id);

        Assert.Equal(SignalDeliveryResultCodes.Interrupted, Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).ResultCode);
        Assert.Equal(5, await ActiveTaskNodeAsync(instance.Id));
        await using var db = fixture.CreateDbContext();
        var hostJobs = await db.WorkflowJobs
            .Where(job => job.InstanceId == instance.Id && job.NodeId == 2).Select(job => job.Status).ToListAsync();
        Assert.NotEmpty(hostJobs);
        Assert.All(hostJobs, status => Assert.Equal(WorkflowJobStatuses.Cancelled, status));
    }

    [Fact]
    public async Task TerminateEnd_EndsTheSignalWaitOfAnotherBranch()
    {
        var signal = UniqueSignal();
        var model = new WorkflowModel
        {
            Id = $"signal-terminate-{Guid.NewGuid():N}",
            Name = "Signal terminate",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 7, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 8, Name = "Stop everything", Type = BpmnFlowNodeTypes.TerminateEndEvent },
                new FlowNodeModel
                {
                    Id = 9,
                    Name = "Wait for the policy",
                    Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
                    Signal = new SignalEventModel { Name = signal, OutputMappings = [] }
                },
                new FlowNodeModel { Id = 10, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 7 },
                new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 2 },
                new SequenceFlowModel { Id = 702, SourceRef = 7, TargetRef = 9 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 8, Name = "Stop" },
                new SequenceFlowModel { Id = 901, SourceRef = 9, TargetRef = 10 }
            ]
        };
        var instance = await StartAsync(model);
        Assert.Equal(SignalSubscriptionStatuses.Active, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);

        await TakeAsync(instance.Id, 2, 201);

        Assert.Equal(SignalSubscriptionStatuses.Cancelled, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, await db.WorkflowInstances
            .Where(item => item.Id == instance.Id).Select(item => item.Status).SingleAsync());
    }

    [Fact]
    public async Task Throw_RefusesMoreRecipientsOrOpenBroadcastsThanTheLimitsAndALargePayload()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        var workflow = await CreateAsync(model);
        await StartDefinitionAsync(workflow.Id);
        await StartDefinitionAsync(workflow.Id);

        var previousRecipients = await SetEngineSettingAsync("Signals", "MaxSubscriptionsPerBroadcast", "1");
        try
        {
            using var tooMany = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", new ThrowSignalRequest(signal, model.Id));
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
            Assert.Contains("more than 1 waiting subscriptions", await tooMany.Content.ReadAsStringAsync());
            await using var db = fixture.CreateDbContext();
            Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.SignalName == signal));
        }
        finally
        {
            await SetEngineSettingAsync("Signals", "MaxSubscriptionsPerBroadcast", previousRecipients);
        }

        var open = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = 1 })));
        Assert.Equal(SignalBroadcastStatuses.Delivering, open.Status);
        int openCount;
        await using (var db = fixture.CreateDbContext())
        {
            openCount = await db.SignalBroadcasts.CountAsync(item => item.Status == SignalBroadcastStatuses.Delivering);
        }
        var previousOpen = await SetEngineSettingAsync("Signals", "MaxOpenBroadcasts", openCount.ToString());
        try
        {
            using var oneMore = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", new ThrowSignalRequest(signal, model.Id));
            Assert.Equal(HttpStatusCode.Conflict, oneMore.StatusCode);
            Assert.Contains("still delivering", await oneMore.Content.ReadAsStringAsync());
        }
        finally
        {
            await SetEngineSettingAsync("Signals", "MaxOpenBroadcasts", previousOpen);
            using var cancel = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{open.Id}/cancel", new { });
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        // Counted as PostgreSQL prints the stored jsonb: 262,144 bytes of text plus the
        // braces, key, quotes, colon, and space are one object over the limit.
        var large = JsonSerializer.SerializeToElement(new { text = new string('a', SignalConstraints.MaxPayloadBytes) });
        using var tooLarge = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
            new ThrowSignalRequest(signal, model.Id, Payload: large));
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Contains($"at most {SignalConstraints.MaxPayloadBytes} bytes", await tooLarge.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Retention_RemovesAnOldFinishedBroadcastAndKeepsOneStillDelivering()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        await StartAsync(model);
        var finished = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), null, AllWorkflows: true));
        Assert.Equal(SignalBroadcastStatuses.Completed, finished.Status);
        var delivering = await ThrowAsync(new ThrowSignalRequest(signal, model.Id,
            Payload: JsonSerializer.SerializeToElement(new { version = 1 })));
        Assert.Equal(SignalBroadcastStatuses.Delivering, delivering.Status);
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE flowbit.signal_broadcasts
                SET "CreatedAt" = now() - interval '10 days',
                    "CompletedAt" = CASE WHEN "CompletedAt" IS NULL THEN NULL ELSE now() - interval '10 days' END
                WHERE "Id" IN ({finished.Id}, {delivering.Id})
                """);
        }

        await SetRetentionAsync(RetentionCategories.SignalBroadcasts, 1);
        try
        {
            await using var retentionSource = new RetentionDataSource(fixture.ConnectionString);
            var retention = new RetentionRepository(retentionSource, TimeProvider.System);
            // A run already in progress keeps the policies it started with, so finish it and run once more.
            for (var run = 0; run < 2; run++)
            {
                await retention.RequestRunAsync("tests", CancellationToken.None);
                for (var tick = 0; tick < 400; tick++)
                {
                    var result = await retention.ProcessBatchAsync(new RetentionExecutionOptions
                    {
                        WorkerId = "signal-retention-test", BatchSize = 100, MaxRunnableJobs = 1_000_000, MaxQueueLagSeconds = 3600
                    }, CancellationToken.None);
                    if (!result.HasActiveRun) break;
                }
            }
        }
        finally
        {
            await SetRetentionAsync(RetentionCategories.SignalBroadcasts, null);
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.Id == finished.Id));
            Assert.True(await db.SignalBroadcasts.AnyAsync(item => item.Id == delivering.Id));
            Assert.Equal(1, await db.SignalDeliveries.CountAsync(item => item.BroadcastId == delivering.Id));
        }

        using var cancel = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{delivering.Id}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
    }

    private async Task SetRetentionAsync(string category, int? days)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE flowbit.retention_policies SET "RetentionDays" = @days, "Revision" = "Revision" + 1, "IsInitialized" = TRUE
            WHERE "Category" = @category
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("days", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)days ?? DBNull.Value });
        command.Parameters.AddWithValue("category", category);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>Sets an engine setting, or removes it for null, and returns its previous value.</summary>
    private async Task<string?> SetEngineSettingAsync(string settingNamespace, string key, string? value)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleOrDefaultAsync(item =>
            item.Namespace == settingNamespace && item.Key == key);
        var previous = setting?.Value;
        if (value is null)
        {
            if (setting is not null) db.EngineSettings.Remove(setting);
        }
        else if (setting is null)
        {
            db.EngineSettings.Add(new EngineSettingEntity { Namespace = settingNamespace, Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }
        await db.SaveChangesAsync();
        return previous;
    }

    private async Task SetMaxActiveTokensAsync(string? value)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleOrDefaultAsync(item =>
            item.Namespace == "Workflow.Gateway" && item.Key == "MaxActiveTokens");
        if (value is null)
        {
            if (setting is not null) db.EngineSettings.Remove(setting);
        }
        else if (setting is null)
        {
            db.EngineSettings.Add(new EngineSettingEntity { Namespace = "Workflow.Gateway", Key = "MaxActiveTokens", Value = value });
        }
        else
        {
            setting.Value = value;
        }
        await db.SaveChangesAsync();
    }

    internal static WorkflowModel BoundaryModel(string signal, bool cancelActivity) => new()
    {
        Id = $"signal-boundary-{Guid.NewGuid():N}",
        Name = "Signal boundary",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Recall announced",
                Type = BpmnFlowNodeTypes.SignalBoundaryEvent,
                AttachedToRef = 2,
                CancelActivity = cancelActivity,
                Signal = new SignalEventModel
                {
                    Name = signal,
                    OutputMappings =
                    [
                        new MessageOutputMappingModel
                        {
                            Variable = "recallReason",
                            Path = "reason",
                            DataType = WorkflowVariableTypes.String,
                            IsArray = false,
                            Required = false
                        }
                    ]
                }
            },
            new FlowNodeModel { Id = 4, Name = "Approved", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 5, Name = "Handle the recall", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 6, Name = "Recalled", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 4, Name = "Approve" },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
            new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6, Name = "Done" }
        ]
    };

    [Fact]
    public async Task Throw_WithAnIdempotencyKeyReplaysAndRefusesADifferentRequest()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        await CreateAsync(model);
        var key = $"key-{Guid.NewGuid():N}";
        var payload = JsonSerializer.SerializeToElement(new { version = 3, note = "a" });

        using var first = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
            new ThrowSignalRequest(signal, model.Id, Payload: payload, IdempotencyKey: key));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var created = (await first.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;

        // The same payload with its members in another order is the same request.
        using var replay = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
            new ThrowSignalRequest(signal, model.Id,
                Payload: JsonSerializer.SerializeToElement(new { note = "a", version = 3 }), IdempotencyKey: key));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(created.Id, (await replay.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!.Id);

        using var different = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
            new ThrowSignalRequest(signal, model.Id,
                Payload: JsonSerializer.SerializeToElement(new { version = 4 }), IdempotencyKey: key));
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
    }

    [Theory]
    [InlineData("Policy-Changed", null, false, "lowercase")]
    [InlineData(null, null, false, "Signal name is required")]
    [InlineData("valid.name", null, false, "allWorkflows")]
    [InlineData("valid.name", "some-family", true, "allWorkflows")]
    [InlineData("valid.name", "no-such-family-xyz", false, "No workflow definition")]
    [InlineData("valid.name", " ", true, "cannot be blank")]
    [InlineData("valid.name", "", false, "cannot be blank")]
    public async Task Throw_RefusesAnInvalidNameOrScope(string? name, string? workflowKey, bool allWorkflows, string expected)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
            new ThrowSignalRequest(name, workflowKey, allWorkflows));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InstanceCancellation_CancelsTheSubscriptionAndALaterDeliveryIsSkipped()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        var instance = await StartAsync(model);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = 2 })));

        using (var cancel = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/cancel", new { }))
        {
            Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
        }

        Assert.Equal(SignalSubscriptionStatuses.Cancelled, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var delivery = Assert.Single((await DeliveriesAsync(broadcast.Id)).Items);
        Assert.Equal((SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.SubscriptionEnded),
            (delivery.Status, delivery.ResultCode));
        Assert.Equal(SignalBroadcastStatuses.Completed, (await GetBroadcastAsync(broadcast.Id)).Status);
    }

    [Fact]
    public async Task Definition_RefusesAnUppercaseNameAndASharedTarget()
    {
        var upper = CatchModel("Policy");
        using (var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(upper, false)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Signal catch event #2 signal.name must be 1 to 100 lowercase letters",
                await response.Content.ReadAsStringAsync());
        }

        var missingFlow = CatchModel(UniqueSignal());
        missingFlow.SequenceFlows.RemoveAll(flow => flow.SourceRef == 2);
        using (var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(missingFlow, false)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Signal catch event #2 must have exactly one outgoing sequence flow.",
                await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task TimerBoundaryOnACatch_CancelsTheSubscriptionWhenItFires()
    {
        var signal = UniqueSignal();
        var instance = await StartAsync(CatchWithBoundariesModel(signal));
        Assert.Equal(SignalSubscriptionStatuses.Active, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);

        await ProcessInstanceJobsAsync(instance.Id, WorkflowJobKinds.TimerBoundary);

        Assert.Equal(6, await ActiveTaskNodeAsync(instance.Id));
        Assert.Equal(SignalSubscriptionStatuses.Cancelled, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);
    }

    [Fact]
    public async Task ConditionalBoundaryOnACatch_TakesTheWaitWhenTheMappedValuesMakeItTrue()
    {
        var signal = UniqueSignal();
        var model = CatchWithBoundariesModel(signal);
        var interrupted = await StartAsync(model);
        var continued = await StartExistingAsync(model.Id);

        var high = await ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = 7 })));
        await ProcessDeliveryJobsAsync(high.Id);
        var deliveries = (await DeliveriesAsync(high.Id)).Items;
        Assert.All(deliveries, delivery => Assert.Equal(
            (SignalDeliveryStatuses.Delivered, SignalDeliveryResultCodes.Superseded),
            (delivery.Status, delivery.ResultCode)));
        Assert.Equal(8, await ActiveTaskNodeAsync(interrupted.Id));
        Assert.Equal(8, await ActiveTaskNodeAsync(continued.Id));
        Assert.All(await SubscriptionsAsync(interrupted.Id),
            subscription => Assert.Equal(SignalSubscriptionStatuses.Cancelled, subscription.Status));

        var other = await StartExistingAsync(model.Id);
        var low = await ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = 3 })));
        Assert.Equal(1, low.TotalDeliveryCount);
        await ProcessDeliveryJobsAsync(low.Id);
        Assert.Equal(SignalDeliveryResultCodes.Caught, Assert.Single((await DeliveriesAsync(low.Id)).Items).ResultCode);
        Assert.Equal(3, await ActiveTaskNodeAsync(other.Id));
    }

    [Fact]
    public async Task Cancel_CancelsPendingDeliveriesAndLeavesTheWaits()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        var first = await StartAsync(model);
        var second = await StartExistingAsync(model.Id);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = 5 })));
        Assert.Equal(2, broadcast.TotalDeliveryCount);

        using (var cancel = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/cancel",
                   new CancelSignalBroadcastRequest("Wrong version")))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            var cancelled = (await cancel.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
            Assert.Equal((SignalBroadcastStatuses.Cancelled, 2, "Wrong version"),
                (cancelled.Status, cancelled.CancelledCount, cancelled.CancellationReason));
        }

        using (var again = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/cancel", new { }))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        await ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.All((await DeliveriesAsync(broadcast.Id)).Items, delivery => Assert.Equal(
            (SignalDeliveryStatuses.Cancelled, SignalDeliveryResultCodes.BroadcastCancelled),
            (delivery.Status, delivery.ResultCode)));
        Assert.Equal(SignalSubscriptionStatuses.Active, Assert.Single(await SubscriptionsAsync(first.Id)).Status);
        Assert.Equal(SignalSubscriptionStatuses.Active, Assert.Single(await SubscriptionsAsync(second.Id)).Status);
    }

    [Fact]
    public async Task RecountWaitingForACancel_KeepsTheCancelledStatus()
    {
        var signal = UniqueSignal();
        var first = await StartAsync(CatchModel(signal));
        var second = await StartAsync(CatchModel(signal));
        var broadcast = await ThrowAsync(new ThrowSignalRequest(
            signal, AllWorkflows: true, Payload: JsonSerializer.SerializeToElement(new { version = 1 })));
        Assert.Equal(2, broadcast.TotalDeliveryCount);
        await using (var backlogScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var backlog = await backlogScope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>()
                .GetBacklogAsync(CancellationToken.None);
            Assert.True(backlog.PendingDeliveries >= 2);
            Assert.NotNull(backlog.OldestOpenBroadcastAt);
        }

        long secondDefinitionId;
        long firstJobId;
        await using (var db = fixture.CreateDbContext())
        {
            var firstDefinitionId = await db.WorkflowInstances.Where(item => item.Id == first.Id)
                .Select(item => item.WorkflowDefinitionId).SingleAsync();
            secondDefinitionId = await db.WorkflowInstances.Where(item => item.Id == second.Id)
                .Select(item => item.WorkflowDefinitionId).SingleAsync();
            firstJobId = await db.SignalBroadcastJobs
                .Where(link => link.BroadcastId == broadcast.Id && link.WorkflowDefinitionId == firstDefinitionId)
                .Select(link => link.JobId!.Value).SingleAsync();
            await db.WorkflowJobs.Where(job => job.Id == firstJobId).ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Priority, 1_000_000)
                .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        // A cancel in progress, as CancelAsync does it: the broadcast's row is locked, the
        // second workflow's delivery is cancelled, and nothing is committed yet.
        await using var cancelling = fixture.CreateDbContext();
        await using var cancel = await cancelling.Database.BeginTransactionAsync();
        await cancelling.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM flowbit.signal_broadcasts WHERE "Id" = {broadcast.Id} FOR UPDATE""");
        var now = DateTimeOffset.UtcNow;
        await cancelling.SignalDeliveries
            .Where(delivery => delivery.BroadcastId == broadcast.Id
                && delivery.WorkflowDefinitionId == secondDefinitionId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(delivery => delivery.Status, SignalDeliveryStatuses.Cancelled)
                .SetProperty(delivery => delivery.ResultCode, SignalDeliveryResultCodes.BroadcastCancelled)
                .SetProperty(delivery => delivery.CompletedAt, now)
                .SetProperty(delivery => delivery.UpdatedAt, now));
        await cancelling.SignalBroadcasts.Where(item => item.Id == broadcast.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, SignalBroadcastStatuses.Cancelled)
                .SetProperty(item => item.CancelledBy, "signal-runtime")
                .SetProperty(item => item.CancelledAt, now)
                .SetProperty(item => item.CompletedAt, now)
                .SetProperty(item => item.UpdatedAt, now));

        // The first workflow's job delivers, then waits for the broadcast's row to recount.
        WorkflowJobLeaseRecord lease;
        await using (var leaseScope = fixture.Factory.Services.CreateAsyncScope())
        {
            lease = Assert.Single(await leaseScope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>()
                .LeaseRunnableAsync(
                    new WorkflowJobLeaseRequest($"signal-runtime-test-{Guid.NewGuid():N}", 1, 1, 1, TimeSpan.FromMinutes(2)),
                    CancellationToken.None));
        }
        Assert.Equal(firstJobId, lease.Job.Id);
        var processing = Task.Run(async () =>
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
                .ProcessAsync(lease, CancellationToken.None);
        });
        await WaitForLockWaitOnBroadcastsAsync(processing);

        await cancel.CommitAsync();
        await processing;

        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Cancelled, 1, 1, 0),
            (finished.Status, finished.DeliveredCount, finished.CancelledCount, finished.PendingCount));
        Assert.NotNull(finished.CompletedAt);
    }

    /// <summary>Waits until another session is blocked on a lock in a statement on signal_broadcasts.</summary>
    private async Task WaitForLockWaitOnBroadcastsAsync(Task processing)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (processing.IsCompleted)
            {
                await processing;
                Assert.Fail("The delivery job finished without waiting for the broadcast's row.");
            }

            await using var db = fixture.CreateDbContext();
            var waiting = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM pg_stat_activity
                WHERE wait_event_type = 'Lock'
                  AND query LIKE '%signal_broadcasts%'
                  AND pid <> pg_backend_pid()
                """).SingleAsync();
            if (waiting > 0)
            {
                return;
            }
            await Task.Delay(100);
        }

        Assert.Fail("The delivery job never waited for the broadcast's row.");
    }

    [Fact]
    public async Task RetryFailed_QueuesTheFailedDeliveriesAgainOnlyWithTheCurrentFence()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        await StartAsync(model);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = "bad" })));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var failed = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal(SignalBroadcastStatuses.CompletedWithIssues, failed.Status);

        using (var missing = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed", new { }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Contains("expectedUpdatedAt", await missing.Content.ReadAsStringAsync());
        }

        using (var smallest = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts/{broadcast.Id}/deliveries?pageSize=0"))
        {
            Assert.Equal(HttpStatusCode.OK, smallest.StatusCode);
            Assert.Equal(1, (await smallest.Content.ReadFromJsonAsync<PagedResult<SignalDeliveryDto>>(JsonOptions))!.PageSize);
        }

        using (var stale = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
                   new RetrySignalDeliveriesRequest(failed.UpdatedAt.AddSeconds(-1))))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }

        using (var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
                   new RetrySignalDeliveriesRequest(failed.UpdatedAt)))
        {
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            var reopened = (await retry.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
            Assert.Equal((SignalBroadcastStatuses.Delivering, 1), (reopened.Status, reopened.PendingCount));
        }

        await ProcessDeliveryJobsAsync(broadcast.Id);
        var delivery = Assert.Single((await DeliveriesAsync(broadcast.Id)).Items);
        Assert.Equal((SignalDeliveryStatuses.Failed, 2), (delivery.Status, delivery.AttemptCount));
        await using var db = fixture.CreateDbContext();
        var links = await db.SignalBroadcastJobs.AsNoTracking()
            .Where(link => link.BroadcastId == broadcast.Id).OrderBy(link => link.Id).ToListAsync();
        Assert.Equal([SignalBroadcastJobReasons.Throw, SignalBroadcastJobReasons.Retry], links.Select(link => link.Reason));
        Assert.Equal("signal-runtime", links[1].RequestedBy);
    }

    [Fact]
    public async Task RetryFailed_DeliversThroughTheVersionTheInstanceRunsNow()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        var v1 = await CreateAsync(model);
        var instance = await StartDefinitionAsync(v1.Id);
        // No payload, so v1's required mapping fails the delivery.
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var failed = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal(SignalBroadcastStatuses.CompletedWithIssues, failed.Status);

        var relaxed = CatchModel(signal);
        relaxed.Id = model.Id;
        relaxed.FlowNodes.Single(node => node.Id == 2).Signal!.OutputMappings[0].Required = false;
        var v2 = await NewVersionAsync(v1.Id, relaxed);
        var preview = await PreviewVersionChangeAsync(instance.Id, v2.Id);
        using (var change = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/version-change",
                   new ChangeInstanceVersionRequest(v2.Id, preview.ExpectedSourceWorkflowId, preview.ExpectedUpdatedAt, "Relax the mapping")))
        {
            Assert.True(change.IsSuccessStatusCode, await change.Content.ReadAsStringAsync());
        }

        // The retry needs no job of v1, which a later deletion could have removed.
        using (var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
                   new RetrySignalDeliveriesRequest(failed.UpdatedAt)))
        {
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        }
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(v2.Id, await db.SignalDeliveries.Where(delivery => delivery.BroadcastId == broadcast.Id)
                .Select(delivery => delivery.WorkflowDefinitionId).SingleAsync());
            Assert.Equal(v2.Id, await db.SignalBroadcastJobs
                .Where(link => link.BroadcastId == broadcast.Id && link.Reason == SignalBroadcastJobReasons.Retry)
                .Select(link => link.WorkflowDefinitionId).SingleAsync());
        }

        await ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.Equal(SignalDeliveryResultCodes.Caught, Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).ResultCode);
        Assert.Equal(3, await ActiveTaskNodeAsync(instance.Id));
    }

    [Fact]
    public async Task ManagedClient_SeesAndChangesOnlyItsOwnBroadcastsWithoutDescriptions()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        await StartAsync(model);
        var clientId = $"signal-client-{Guid.NewGuid():N}"[..40];
        string secret;
        using (var create = await SendAsync(HttpMethod.Post, "/api/shared-variable-clients",
                   new CreateSharedVariableClientRequest(clientId, "Signal test client",
                       [SharedVariableClientScopes.SignalsThrow, SharedVariableClientScopes.SignalsRead])))
        {
            Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
            secret = (await create.Content.ReadFromJsonAsync<CreateSharedVariableClientResult>(JsonOptions))!.ClientSecret;
        }

        var adminBroadcast = await ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = "bad" })));
        using var clientThrow = await SendAsClientAsync(HttpMethod.Post, "/api/signal-broadcasts", clientId, secret,
            new ThrowSignalRequest(UniqueSignal(), model.Id));
        Assert.Equal(HttpStatusCode.Accepted, clientThrow.StatusCode);
        var own = (await clientThrow.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
        Assert.Equal((clientId, SignalThrowerKinds.Client), (own.ThrownBy, own.ThrownByKind));

        using (var list = await SendAsClientAsync(HttpMethod.Get, "/api/signal-broadcasts?pageSize=200", clientId, secret))
        {
            var page = (await list.Content.ReadFromJsonAsync<PagedResult<SignalBroadcastSummaryDto>>(JsonOptions))!;
            Assert.Equal(own.Id, Assert.Single(page.Items).Id);
        }
        using (var other = await SendAsClientAsync(HttpMethod.Get, $"/api/signal-broadcasts/{adminBroadcast.Id}", clientId, secret))
        {
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        }
        using (var cancelOther = await SendAsClientAsync(HttpMethod.Post,
                   $"/api/signal-broadcasts/{adminBroadcast.Id}/cancel", clientId, secret, new { }))
        {
            Assert.Equal(HttpStatusCode.NotFound, cancelOther.StatusCode);
        }
        using (var summary = await SendAsClientAsync(HttpMethod.Get, "/api/signal-subscriptions/summary", clientId, secret))
        {
            Assert.True(summary.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                summary.StatusCode.ToString());
        }

        // A client's own failed delivery carries the code without the engine's description.
        var listen = await StartExistingAsync(model.Id);
        Assert.NotNull(listen);
        using var clientFailed = await SendAsClientAsync(HttpMethod.Post, "/api/signal-broadcasts", clientId, secret,
            new ThrowSignalRequest(signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = "bad" })));
        var failing = (await clientFailed.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
        await ProcessDeliveryJobsAsync(failing.Id);
        using (var deliveries = await SendAsClientAsync(HttpMethod.Get,
                   $"/api/signal-broadcasts/{failing.Id}/deliveries", clientId, secret))
        {
            var page = (await deliveries.Content.ReadFromJsonAsync<PagedResult<SignalDeliveryDto>>(JsonOptions))!;
            Assert.NotEmpty(page.Items);
            Assert.All(page.Items, delivery =>
            {
                Assert.Equal(SignalDeliveryResultCodes.PayloadInvalid, delivery.ResultCode);
                Assert.Null(delivery.ResultDescription);
            });
        }
        Assert.NotNull((await DeliveriesAsync(failing.Id)).Items[0].ResultDescription);

        // A user who cancels the client's broadcast stays unnamed to the client.
        await StartExistingAsync(model.Id);
        using var clientPending = await SendAsClientAsync(HttpMethod.Post, "/api/signal-broadcasts", clientId, secret,
            new ThrowSignalRequest(signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = 3 })));
        var pending = (await clientPending.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
        Assert.Equal(SignalBroadcastStatuses.Delivering, pending.Status);
        using (var cancel = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{pending.Id}/cancel",
                   new CancelSignalBroadcastRequest("Paused for the audit")))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }
        using (var seen = await SendAsClientAsync(HttpMethod.Get, $"/api/signal-broadcasts/{pending.Id}", clientId, secret))
        {
            var cancelled = (await seen.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
            Assert.Equal((SignalBroadcastStatuses.Cancelled, null, null),
                (cancelled.Status, cancelled.CancelledBy, cancelled.CancellationReason));
            Assert.NotNull(cancelled.CancelledAt);
        }
        var asUser = await GetBroadcastAsync(pending.Id);
        Assert.Equal(("signal-runtime", "Paused for the audit"), (asUser.CancelledBy, asUser.CancellationReason));
    }

    [Fact]
    public async Task Throw_RequiresTheSignalRoleOrScopeAndOneKindOfCredential()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        await CreateAsync(model);
        var request = new ThrowSignalRequest(signal, model.Id);

        using (var clerk = new HttpRequestMessage(HttpMethod.Post, "/api/signal-broadcasts"))
        {
            clerk.Content = JsonContent.Create(request, options: JsonOptions);
            ApiTestAuth.Authorize(clerk, "signal-clerk", ["clerk"]);
            using var response = await fixture.Client.SendAsync(clerk);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var readerId = $"signal-reader-{Guid.NewGuid():N}"[..40];
        string readerSecret;
        using (var create = await SendAsync(HttpMethod.Post, "/api/shared-variable-clients",
                   new CreateSharedVariableClientRequest(readerId, "Signal reader", [SharedVariableClientScopes.SignalsRead])))
        {
            Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
            readerSecret = (await create.Content.ReadFromJsonAsync<CreateSharedVariableClientResult>(JsonOptions))!.ClientSecret;
        }
        using (var reader = await SendAsClientAsync(HttpMethod.Post, "/api/signal-broadcasts", readerId, readerSecret, request))
        {
            Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        }
        using (var list = await SendAsClientAsync(HttpMethod.Get, "/api/signal-broadcasts", readerId, readerSecret))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        }

        using var both = new HttpRequestMessage(HttpMethod.Post, "/api/signal-broadcasts");
        both.Content = JsonContent.Create(request, options: JsonOptions);
        ApiTestAuth.Authorize(both, "signal-runtime", ["admin"]);
        both.Headers.Add("X-Client-Id", readerId);
        both.Headers.Add("X-Client-Secret", readerSecret);
        using var bothResponse = await fixture.Client.SendAsync(both);
        Assert.Equal(HttpStatusCode.BadRequest, bothResponse.StatusCode);
    }

    [Fact]
    public async Task VersionChange_RebindsAnOpenSubscriptionAndBlocksAChangedSignal()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        var v1 = await CreateAsync(model);
        var instance = await StartDefinitionAsync(v1.Id);

        var same = CatchModel(signal);
        same.Id = model.Id;
        same.FlowNodes.Single(node => node.Id == 2).Signal!.OutputMappings[0].Required = false;
        var v2 = await NewVersionAsync(v1.Id, same);
        var preview = await PreviewVersionChangeAsync(instance.Id, v2.Id);
        Assert.True(preview.Compatible, string.Join("; ", preview.Blockers.Select(blocker => blocker.Message)));
        Assert.Contains(preview.Warnings, warning => warning.Code == WorkflowVersionCompatibilityCodes.SignalMappingContractChanged);

        var renamed = CatchModel(UniqueSignal());
        renamed.Id = model.Id;
        var v3 = await NewVersionAsync(v1.Id, renamed);
        var blocked = await PreviewVersionChangeAsync(instance.Id, v3.Id);
        Assert.False(blocked.Compatible);
        Assert.Contains(blocked.Blockers, blocker => blocker.Code == WorkflowVersionCompatibilityCodes.OpenSignalSubscriptionContractChanged);

        using (var change = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/version-change",
                   new ChangeInstanceVersionRequest(v2.Id, preview.ExpectedSourceWorkflowId, preview.ExpectedUpdatedAt, "Relax the mapping")))
        {
            Assert.True(change.IsSuccessStatusCode, await change.Content.ReadAsStringAsync());
        }

        Assert.Equal(v2.Id, Assert.Single(await SubscriptionsAsync(instance.Id)).WorkflowDefinitionId);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        // Under v2 the version mapping is optional, so a payload without it is caught.
        Assert.Equal(SignalDeliveryResultCodes.Caught, Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).ResultCode);
        Assert.Equal(3, await ActiveTaskNodeAsync(instance.Id));
    }

    [Fact]
    public async Task WorkflowWithoutSignalEvents_RunsNoSignalStatementInATransition()
    {
        var model = CatchModel(UniqueSignal());
        model.FlowNodes.RemoveAll(node => node.Id == 2);
        model.SequenceFlows.RemoveAll(flow => flow.SourceRef == 2);
        model.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 3;
        var instance = await StartAsync(model);

        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        try
        {
            await TakeAsync(instance.Id, 3, 301);
            var statements = fixture.CommandCounter.ReaderCommandTexts
                .Concat(fixture.CommandCounter.NonQueryCommandTexts)
                .ToArray();
            Assert.NotEmpty(statements);
            Assert.DoesNotContain(statements, text => text.Contains("signal_", StringComparison.Ordinal));
        }
        finally
        {
            fixture.CommandCounter.Reset();
        }
    }

    [Fact]
    public async Task Throw_DoesNotWaitForARecipientWhoseInstanceIsLocked()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        var instance = await StartAsync(model);

        await using var holder = fixture.CreateDbContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        // Hold the instance and its tokens as a transition does.
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM flowbit.workflow_instances WHERE \"Id\" = {instance.Id} FOR UPDATE");
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM flowbit.execution_tokens WHERE \"InstanceId\" = {instance.Id} FOR UPDATE");

        var throwTask = ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = 4 })));
        var finished = await Task.WhenAny(throwTask, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(throwTask, finished);
        var broadcast = await throwTask;
        Assert.Equal(1, broadcast.TotalDeliveryCount);

        await held.RollbackAsync();
        await ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.Equal(SignalDeliveryResultCodes.Caught, Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).ResultCode);
    }

    [Fact]
    public async Task DeliveryRacingInstanceCancellation_HasOneConsistentOutcome()
    {
        var signal = UniqueSignal();
        var model = CatchModel(signal);
        var instance = await StartAsync(model);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = 4 })));

        // Both wait behind a held instance lock, then race when it is released.
        await using var holder = fixture.CreateDbContext();
        var held = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM flowbit.workflow_instances WHERE \"Id\" = {instance.Id} FOR UPDATE");
        var delivery = ProcessDeliveryJobsAsync(broadcast.Id);
        var cancel = SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/cancel", new { });
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await held.RollbackAsync();
        await held.DisposeAsync();
        await delivery;
        using (var cancelled = await cancel)
        {
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        }

        var outcome = Assert.Single((await DeliveriesAsync(broadcast.Id)).Items);
        await using var db = fixture.CreateDbContext();
        var signalled = await db.InstanceHistory.AnyAsync(item =>
            item.InstanceId == instance.Id && item.Note == InstanceHistoryNotes.SignalReceived);
        if (outcome.ResultCode == SignalDeliveryResultCodes.Caught)
        {
            Assert.True(signalled);
        }
        else
        {
            Assert.Equal((SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.SubscriptionEnded),
                (outcome.Status, outcome.ResultCode));
            Assert.False(signalled);
        }
        Assert.Equal(WorkflowInstanceStatuses.Cancelled,
            await db.WorkflowInstances.Where(item => item.Id == instance.Id).Select(item => item.Status).SingleAsync());
        Assert.Equal(SignalSubscriptionStatuses.Completed == Assert.Single(await SubscriptionsAsync(instance.Id)).Status,
            outcome.ResultCode == SignalDeliveryResultCodes.Caught);
    }

    private static string UniqueSignal() => $"test.signal-{Guid.NewGuid():N}";

    /// <summary>
    /// The catch hosts an interrupting timer boundary (node 5 to task 6) and an interrupting
    /// conditional boundary on the mapped value (node 7 to task 8).
    /// </summary>
    private static WorkflowModel CatchWithBoundariesModel(string signal)
    {
        var model = CatchModel(signal);
        model.Variables =
        [
            new VariableModel
            {
                Id = 1, Name = "policyVersion", DataType = WorkflowVariableTypes.Number, Nullable = true
            }
        ];
        model.FlowNodes.AddRange(
        [
            new FlowNodeModel
            {
                Id = 5, Name = "Timed out", Type = BpmnFlowNodeTypes.TimerBoundaryEvent, AttachedToRef = 2,
                CancelActivity = true, Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
            },
            new FlowNodeModel { Id = 6, Name = "Handle timeout", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel
            {
                Id = 7, Name = "High version", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = 2,
                CancelActivity = true, Conditional = new ConditionalDefinitionModel { Condition = "policyVersion > 5" }
            },
            new FlowNodeModel { Id = 8, Name = "Escalate", Type = BpmnFlowNodeTypes.UserTask }
        ]);
        model.SequenceFlows.AddRange(
        [
            new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 },
            new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 4, Name = "Close" },
            new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 8 },
            new SequenceFlowModel { Id = 801, SourceRef = 8, TargetRef = 4, Name = "Close" }
        ]);
        return model;
    }

    internal static WorkflowModel CatchModel(string signal) => new()
    {
        Id = $"signal-runtime-{Guid.NewGuid():N}",
        Name = "Signal runtime",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Wait for the policy",
                Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
                Signal = new SignalEventModel
                {
                    Name = signal,
                    OutputMappings =
                    [
                        new MessageOutputMappingModel
                        {
                            Variable = "policyVersion",
                            Path = "version",
                            DataType = WorkflowVariableTypes.Number,
                            IsArray = false,
                            Required = true
                        }
                    ]
                }
            },
            new FlowNodeModel { Id = 3, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4, Name = "Done" }
        ]
    };

    private async Task<WorkflowDetailDto> CreateAsync(WorkflowModel model)
    {
        using var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return (await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;
    }

    private async Task<InstanceDetailDto> StartAsync(WorkflowModel model) =>
        await StartDefinitionAsync((await CreateAsync(model)).Id);

    private async Task<InstanceDetailDto> StartExistingAsync(string workflowKey)
    {
        await using var db = fixture.CreateDbContext();
        var id = await db.WorkflowDefinitions.Where(item => item.WorkflowKey == workflowKey)
            .Select(item => item.Id).SingleAsync();
        return await StartDefinitionAsync(id);
    }

    private async Task<InstanceDetailDto> StartDefinitionAsync(long workflowId)
    {
        using var start = await SendAsync(HttpMethod.Post, "/api/instances?detail=full", new StartInstanceRequest(workflowId, null, null, null));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        return (await start.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private async Task<SignalBroadcastDetailDto> ThrowAsync(ThrowSignalRequest request)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
    }

    private async Task<SignalBroadcastDetailDto> GetBroadcastAsync(long id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts/{id}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
    }

    private async Task<PagedResult<SignalDeliveryDto>> DeliveriesAsync(long id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts/{id}/deliveries");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResult<SignalDeliveryDto>>(JsonOptions))!;
    }

    private async Task<List<SignalSubscriptionEntity>> SubscriptionsAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SignalSubscriptions.AsNoTracking()
            .Where(item => item.InstanceId == instanceId).OrderBy(item => item.Id).ToListAsync();
    }

    /// <summary>
    /// Leases and processes the broadcast's open delivery jobs. Another class's leftover job
    /// that is leased first is processed too, as the Worker would.
    /// </summary>
    internal async Task ProcessDeliveryJobsAsync(long broadcastId)
    {
        for (var round = 0; round < 20; round++)
        {
            List<long> open;
            await using (var db = fixture.CreateDbContext())
            {
                open = await db.SignalBroadcastJobs.AsNoTracking()
                    .Where(link => link.BroadcastId == broadcastId && link.JobId != null)
                    .Join(db.WorkflowJobs, link => link.JobId, job => job.Id, (_, job) => job)
                    .Where(job => job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry)
                    .Select(job => job.Id)
                    .ToListAsync();
                if (open.Count == 0)
                {
                    return;
                }

                foreach (var job in await db.WorkflowJobs.Where(job => open.Contains(job.Id)).ToListAsync())
                {
                    job.Priority = 1_000_000;
                    job.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                }
                await db.SaveChangesAsync();
            }

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                new WorkflowJobLeaseRequest(
                    $"signal-runtime-test-{Guid.NewGuid():N}",
                    MaxCount: 1,
                    MaxActivityCount: 1,
                    MaxPerInstance: 1,
                    LeaseDuration: TimeSpan.FromMinutes(2)),
                CancellationToken.None);
            foreach (var lease in leases)
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
                    .ProcessAsync(lease, CancellationToken.None);
            }
        }

        Assert.Fail($"The delivery jobs of signal broadcast #{broadcastId} did not finish.");
    }

    private async Task<WorkflowDetailDto> NewVersionAsync(long workflowId, WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/api/workflows/{workflowId}", new UpdateWorkflowRequest(model, true));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;
    }

    private async Task<InstanceVersionChangePreviewDto> PreviewVersionChangeAsync(long instanceId, long targetWorkflowId)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(targetWorkflowId));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<InstanceVersionChangePreviewDto>(JsonOptions))!;
    }

    private async Task<int> ActiveTaskNodeAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.Where(task => task.InstanceId == instanceId && task.Status == UserTaskStatuses.Active)
            .Select(task => task.NodeId).SingleAsync();
    }

    private async Task TakeAsync(long instanceId, int nodeId, int flowId)
    {
        long taskId;
        await using (var db = fixture.CreateDbContext())
        {
            taskId = await db.UserTasks.Where(task => task.InstanceId == instanceId && task.NodeId == nodeId
                && task.Status == UserTaskStatuses.Active).Select(task => task.Id).FirstAsync();
        }
        using var action = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/{flowId}", new TakeFlowRequest(null));
        Assert.True(action.StatusCode == HttpStatusCode.OK, await action.Content.ReadAsStringAsync());
    }

    /// <summary>Makes the instance's open jobs of one kind due and processes them.</summary>
    private async Task ProcessInstanceJobsAsync(long instanceId, string kind)
    {
        for (var round = 0; round < 10; round++)
        {
            await using (var db = fixture.CreateDbContext())
            {
                var open = await db.WorkflowJobs
                    .Where(job => job.InstanceId == instanceId && job.Kind == kind
                        && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry))
                    .ToListAsync();
                if (open.Count == 0)
                {
                    return;
                }
                foreach (var job in open)
                {
                    job.Priority = 1_000_000;
                    job.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                }
                await db.SaveChangesAsync();
            }

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                new WorkflowJobLeaseRequest($"signal-runtime-test-{Guid.NewGuid():N}", 1, 1, 1, TimeSpan.FromMinutes(2)),
                CancellationToken.None);
            foreach (var lease in leases)
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
                    .ProcessAsync(lease, CancellationToken.None);
            }
        }

        Assert.Fail($"The {kind} jobs of instance #{instanceId} did not finish.");
    }

    private async Task<HttpResponseMessage> SendAsClientAsync(
        HttpMethod method, string path, string clientId, string secret, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        request.Headers.Add("X-Client-Id", clientId);
        request.Headers.Add("X-Client-Secret", secret);
        return await fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, "signal-runtime", ["admin"]);
        return await fixture.Client.SendAsync(request);
    }
}
