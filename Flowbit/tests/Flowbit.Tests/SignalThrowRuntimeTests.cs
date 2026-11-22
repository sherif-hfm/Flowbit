using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Signal throw and end events (EVENT-04B): a throw writes its broadcast in the step's
/// transaction, reaches the waits its scope names, records its thrower, fails its step when
/// refused, and continues the automatic chain that bounds signals circulating between
/// workflows, also through conditional events and call activities.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed partial class SignalThrowRuntimeTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ThrowToThisInstance_InterruptsAnotherBranchOfTheSameInstance()
    {
        var signal = UniqueSignal();
        var model = OrderModel(signal);
        var order = await StartAsync(await CreateAsync(model));
        var other = await StartAsync(await DefinitionIdAsync(model.Id));

        await TakeAsync(order.Id, 7, 701);

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(order.Id));
        Assert.Equal(
            (SignalThrowerKinds.Instance, order.Id.ToString(), 8, "Announce the cancellation", 1, (long?)order.Id, 1),
            (broadcast.ThrownByKind, broadcast.ThrownBy, broadcast.ThrowerNodeId, broadcast.ThrowerNodeName,
             broadcast.AutomaticActivationCount, broadcast.InstanceId, broadcast.TotalDeliveryCount));
        Assert.Equal("""{"reason":"Customer request"}""", Compact(broadcast.PayloadJson));
        Assert.Null(broadcast.IdempotencyKey);

        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.InstanceHistory.AsNoTracking()
                .SingleAsync(item => item.InstanceId == order.Id && item.Note == InstanceHistoryNotes.SignalThrown);
            Assert.Equal((8, 9, (long?)null), (row.FromStepId, row.ToStepId, row.ActionId));
            var payload = row.Payload!;
            Assert.Equal(broadcast.Id, payload.RootElement.GetProperty("broadcastId").GetInt64());
            Assert.Equal("throw", payload.RootElement.GetProperty("eventKind").GetString());
            Assert.Equal(SignalThrowScopeKinds.ThisInstance, payload.RootElement.GetProperty("scopeKind").GetString());
            Assert.Equal(1, payload.RootElement.GetProperty("recipientCount").GetInt32());
            Assert.Equal(NodeExecutionCompletionReasons.Normal, await db.NodeExecutions.AsNoTracking()
                .Where(item => item.InstanceId == order.Id && item.NodeId == 8)
                .Select(item => item.CompletionReason).SingleAsync());
        }

        await ProcessDeliveryJobsAsync(broadcast.Id);

        Assert.Equal(new[] { 5 }, await ActiveTaskNodesAsync(order.Id));
        Assert.Equal(new[] { 3, 7 }, await ActiveTaskNodesAsync(other.Id));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal("Customer request", (await db.InstanceVariableCurrentValues.AsNoTracking()
                .SingleAsync(item => item.InstanceId == order.Id && item.VariableName == "stopReason"))
                .ValueJson.RootElement.GetString());
            var received = await db.InstanceHistory.AsNoTracking()
                .SingleAsync(item => item.InstanceId == order.Id && item.Note == InstanceHistoryNotes.SignalReceived);
            var payload = received.Payload!;
            Assert.Equal(order.Id, payload.RootElement.GetProperty("thrownByInstanceId").GetInt64());
            Assert.Equal(8, payload.RootElement.GetProperty("thrownByNodeId").GetInt32());
            Assert.Equal(1, await db.ExecutionTokens.AsNoTracking()
                .Where(item => item.InstanceId == order.Id && item.NodeId == 5)
                .Select(item => item.AutomaticActivationCount).SingleAsync());
        }
    }

    [Fact]
    public async Task SignalEndEvent_ThrowsToTheInstanceWithABusinessKeyInAnotherFamily()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        var receiverId = await CreateAsync(receiver);
        var first = await StartAsync(receiverId, OrderNumber("ORD-1"));
        var second = await StartAsync(receiverId, OrderNumber("ORD-2"));
        var thrower = EndThrowerModel(signal, new SignalThrowScopeModel
        {
            Kind = SignalThrowScopeKinds.BusinessKey,
            WorkflowKey = receiver.Id,
            BusinessKeyVariable = "orderNumber"
        });
        var decision = await StartAsync(await CreateAsync(thrower), OrderNumber(" ORD-1 "));

        await TakeAsync(decision.Id, 2, 201);

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(decision.Id));
        Assert.Equal((receiver.Id, "ORD-1", 1, 3), (broadcast.WorkflowKey, broadcast.BusinessKey, broadcast.TotalDeliveryCount, broadcast.ThrowerNodeId));
        Assert.Equal($$"""{"orderInstance":{{decision.Id}}}""", Compact(broadcast.PayloadJson));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(WorkflowInstanceStatuses.Completed, await db.WorkflowInstances.AsNoTracking()
                .Where(item => item.Id == decision.Id).Select(item => item.Status).SingleAsync());
            var row = await db.InstanceHistory.AsNoTracking()
                .SingleAsync(item => item.InstanceId == decision.Id && item.Note == InstanceHistoryNotes.SignalThrown);
            Assert.Equal((3, 3), (row.FromStepId, row.ToStepId));
            Assert.Equal(NodeExecutionCompletionReasons.NormalEnd, await db.NodeExecutions.AsNoTracking()
                .Where(item => item.InstanceId == decision.Id && item.NodeId == 3)
                .Select(item => item.CompletionReason).SingleAsync());
        }

        await ProcessDeliveryJobsAsync(broadcast.Id);

        Assert.Equal(new[] { 3 }, await ActiveTaskNodesAsync(first.Id));
        await using var check = fixture.CreateDbContext();
        Assert.True(await check.SignalSubscriptions.AnyAsync(item =>
            item.InstanceId == second.Id && item.Status == SignalSubscriptionStatuses.Active));
    }

    [Fact]
    public async Task RefusedThrow_FailsTheActionAndLeavesNothing()
    {
        var signal = UniqueSignal();
        var model = OrderModel(signal);
        model.Variables.Add(new VariableModel { Id = 9, Name = "note", DataType = WorkflowVariableTypes.String, Nullable = true });
        model.FlowNodes.Single(node => node.Id == 8).Signal!.Payload!.Add(
            new SignalPayloadEntryModel { Name = "note", Variable = "note", Required = true });
        var order = await StartAsync(await CreateAsync(model));

        using var response = await TakeResponseAsync(order.Id, 7, 701);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "Signal throw event #8 cannot send payload entry 'note': variable 'note' is null.",
            await response.Content.ReadAsStringAsync());
        Assert.Empty(await BroadcastsThrownByAsync(order.Id));
        Assert.Equal(new[] { 3, 7 }, await ActiveTaskNodesAsync(order.Id));
    }

    [Fact]
    public async Task RecipientLimit_RefusesTheThrowAndDeletesWhatItFroze()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        var receiverId = await CreateAsync(receiver);
        await StartAsync(receiverId, OrderNumber("ORD-1"));
        await StartAsync(receiverId, OrderNumber("ORD-2"));
        var thrower = EndThrowerModel(signal, new SignalThrowScopeModel
        {
            Kind = SignalThrowScopeKinds.Workflow,
            WorkflowKey = receiver.Id
        });
        var decision = await StartAsync(await CreateAsync(thrower), OrderNumber("ORD-9"));

        var previous = await SetEngineSettingAsync("Signals", "MaxSubscriptionsPerBroadcast", "1");
        try
        {
            using var response = await TakeResponseAsync(decision.Id, 2, 201);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(
                "Signal end event #3 would reach more than 1 waiting subscriptions",
                await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await RestoreEngineSettingAsync("Signals", "MaxSubscriptionsPerBroadcast", previous);
        }

        Assert.Empty(await BroadcastsThrownByAsync(decision.Id));
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.SignalDeliveries.AnyAsync(delivery =>
            !db.SignalBroadcasts.Any(broadcast => broadcast.Id == delivery.BroadcastId)));
        Assert.Equal(WorkflowInstanceStatuses.Running, await db.WorkflowInstances.AsNoTracking()
            .Where(item => item.Id == decision.Id).Select(item => item.Status).SingleAsync());
    }

    [Fact]
    public async Task WorkflowBudget_RefusesAThrowWhileItsBroadcastsDeliverAndLeavesTheApiBudgetAlone()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        var receiverId = await CreateAsync(receiver);
        await StartAsync(receiverId, OrderNumber("ORD-1"));
        var thrower = EndThrowerModel(signal, new SignalThrowScopeModel
        {
            Kind = SignalThrowScopeKinds.Workflow,
            WorkflowKey = receiver.Id
        });
        var throwerId = await CreateAsync(thrower);
        var first = await StartAsync(throwerId, OrderNumber("ORD-A"));
        var second = await StartAsync(throwerId, OrderNumber("ORD-B"));
        int open;
        await using (var db = fixture.CreateDbContext())
        {
            open = await db.SignalBroadcasts.CountAsync(item =>
                item.Status == SignalBroadcastStatuses.Delivering && item.ThrownByKind == SignalThrowerKinds.Instance);
        }

        var previous = await SetEngineSettingAsync("Signals", "MaxOpenWorkflowBroadcasts", (open + 1).ToString());
        SignalBroadcastDetailDto apiThrow;
        try
        {
            await TakeAsync(first.Id, 2, 201);
            using var refused = await TakeResponseAsync(second.Id, 2, 201);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains(
                $"Signal end event #3 was not thrown: {open + 1} signal broadcasts thrown by workflows are still delivering; see Signals.MaxOpenWorkflowBroadcasts.",
                await refused.Content.ReadAsStringAsync());
            apiThrow = await ThrowApiAsync(new ThrowSignalRequest(signal, receiver.Id));
        }
        finally
        {
            await RestoreEngineSettingAsync("Signals", "MaxOpenWorkflowBroadcasts", previous);
        }

        Assert.Empty(await BroadcastsThrownByAsync(second.Id));
        await ProcessDeliveryJobsAsync(Assert.Single(await BroadcastsThrownByAsync(first.Id)).Id);
        await ProcessDeliveryJobsAsync(apiThrow.Id);
    }
    [Fact]
    public async Task LoopGuard_StopsASignalThatABranchThrowsBackToItsOwnBoundary()
    {
        var signal = UniqueSignal();
        var model = new WorkflowModel
        {
            Id = $"signal-loop-{Guid.NewGuid():N}",
            Name = "Signal loop",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Hold", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 3, Name = "Pinged", Type = BpmnFlowNodeTypes.SignalBoundaryEvent, AttachedToRef = 2,
                    CancelActivity = false, Signal = new SignalEventModel { Name = signal }
                },
                Throw(4, "Ping again", signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.ThisInstance }),
                new FlowNodeModel { Id = 5, Name = "Pinged end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 6, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 6, Name = "Done" },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 }
            ]
        };
        var instance = await StartAsync(await CreateAsync(model));

        var previous = await SetEngineSettingAsync("Workflow.Async", "MaxConsecutiveAutomaticActivations", "3");
        try
        {
            var first = await ThrowApiAsync(new ThrowSignalRequest(signal, InstanceId: instance.Id));
            await ProcessDeliveryJobsAsync(first.Id);
            for (var round = 0; round < 10; round++)
            {
                var open = (await BroadcastsThrownByAsync(instance.Id))
                    .Where(item => item.Status == SignalBroadcastStatuses.Delivering)
                    .ToArray();
                if (open.Length == 0)
                {
                    break;
                }

                foreach (var broadcast in open)
                {
                    await ProcessDeliveryJobsAsync(broadcast.Id);
                }
            }
        }
        finally
        {
            await RestoreEngineSettingAsync("Workflow.Async", "MaxConsecutiveAutomaticActivations", previous);
        }

        var thrown = await BroadcastsThrownByAsync(instance.Id);
        Assert.Equal([1, 2, 3], thrown.Select(item => item.AutomaticActivationCount).Order().ToArray());
        var last = thrown.Single(item => item.AutomaticActivationCount == 3);
        await using var db = fixture.CreateDbContext();
        var refused = await db.SignalDeliveries.AsNoTracking().SingleAsync(item => item.BroadcastId == last.Id);
        Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.DeliveryRejected),
            (refused.Status, refused.ResultCode));
        Assert.Contains("Workflow.Async.MaxConsecutiveAutomaticActivations (3)", refused.ResultDescription);
        Assert.True(await db.SignalSubscriptions.AnyAsync(item =>
            item.InstanceId == instance.Id && item.Status == SignalSubscriptionStatuses.Active));
    }

    [Fact]
    public async Task ConditionalBoundaryWokenByADeliveredSignal_ContinuesTheSignalsChain()
    {
        var ping = UniqueSignal();
        var pong = UniqueSignal();
        var receiver = new WorkflowModel
        {
            Id = $"signal-conditional-{Guid.NewGuid():N}",
            Name = "Signal conditional",
            InitialEventId = 1,
            Variables = [new VariableModel { Id = 1, Name = "v", DataType = WorkflowVariableTypes.Number, Nullable = true }],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel
                {
                    Id = 3, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
                    Signal = new SignalEventModel
                    {
                        Name = ping,
                        OutputMappings =
                        [
                            new MessageOutputMappingModel
                            {
                                Variable = "v", Path = "v", DataType = WorkflowVariableTypes.Number, IsArray = false, Required = true
                            }
                        ]
                    }
                },
                new FlowNodeModel { Id = 4, Name = "Caught", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 5, Name = "Hold", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 6, Name = "Positive", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = 5,
                    CancelActivity = false, Conditional = new ConditionalDefinitionModel { Condition = "v > 0" }
                },
                Throw(7, "Answer", pong, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows }),
                new FlowNodeModel { Id = 8, Name = "Answered", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 9, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 5 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 9, Name = "Done" },
                new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 7 },
                new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 8 }
            ]
        };
        var target = await StartAsync(await CreateAsync(receiver));
        var sender = SenderModel(ping, receiver.Id, payloadVariable: "v");
        var go = await StartAsync(await CreateAsync(sender), new Dictionary<string, JsonElement>
        {
            ["v"] = JsonSerializer.SerializeToElement(5)
        });

        await TakeAsync(go.Id, 2, 201);
        var sent = Assert.Single(await BroadcastsThrownByAsync(go.Id));
        Assert.Equal(1, sent.AutomaticActivationCount);
        await ProcessDeliveryJobsAsync(sent.Id);

        var answered = Assert.Single(await BroadcastsThrownByAsync(target.Id));
        Assert.Equal((pong, 2), (answered.SignalName, answered.AutomaticActivationCount));
    }

    [Fact]
    public async Task SignalEndEventBehindAFork_ReachesNoSiblingBranch()
    {
        var signal = UniqueSignal();
        var model = ForkModel(signal, end: true, catchFirst: true);
        var instance = await StartAsync(await CreateAsync(model));

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(instance.Id));
        Assert.Equal((0, SignalBroadcastStatuses.Completed), (broadcast.TotalDeliveryCount, broadcast.Status));
        await using var db = fixture.CreateDbContext();
        Assert.True(await db.SignalSubscriptions.AnyAsync(item =>
            item.InstanceId == instance.Id && item.Status == SignalSubscriptionStatuses.Active));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task IntermediateThrowBehindAFork_ReachesOnlyABranchRoutedBeforeIt(bool catchFirst, int recipients)
    {
        var signal = UniqueSignal();
        var instance = await StartAsync(await CreateAsync(ForkModel(signal, end: false, catchFirst)));

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(instance.Id));
        Assert.Equal(recipients, broadcast.TotalDeliveryCount);
        await ProcessDeliveryJobsAsync(broadcast.Id);
    }

    [Fact]
    public async Task DefinitionWithOnlyThrowEvents_RunsNoSignalStatementInATransitionWithoutAThrow()
    {
        var signal = UniqueSignal();
        var thrower = EndThrowerModel(signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows });
        thrower.FlowNodes.Add(new FlowNodeModel { Id = 4, Name = "Skipped", Type = BpmnFlowNodeTypes.EndEvent });
        thrower.SequenceFlows.Add(new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4, Name = "Skip" });
        var instance = await StartAsync(await CreateAsync(thrower), OrderNumber("ORD-5"));

        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        try
        {
            await TakeAsync(instance.Id, 2, 202);
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
    public async Task BroadcastList_FiltersByThrowerKind()
    {
        var signal = UniqueSignal();
        var model = OrderModel(signal);
        var order = await StartAsync(await CreateAsync(model));
        await TakeAsync(order.Id, 7, 701);
        await ThrowApiAsync(new ThrowSignalRequest(signal, model.Id));

        async Task<string[]> KindsAsync(string query)
        {
            using var response = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts?name={signal}{query}");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var page = (await response.Content.ReadFromJsonAsync<PagedResult<SignalBroadcastSummaryDto>>(JsonOptions))!;
            return page.Items.Select(item => item.ThrownByKind).Order(StringComparer.Ordinal).ToArray();
        }

        Assert.Equal([SignalThrowerKinds.Instance, SignalThrowerKinds.User], await KindsAsync(string.Empty));
        Assert.Equal([SignalThrowerKinds.Instance], await KindsAsync("&thrownByKind=instance"));
        Assert.Equal([SignalThrowerKinds.User], await KindsAsync("&thrownByKind=user&thrownByKind=client"));
        using var detail = await SendAsync(HttpMethod.Get,
            $"/api/signal-broadcasts/{Assert.Single(await BroadcastsThrownByAsync(order.Id)).Id}");
        var dto = (await detail.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
        Assert.Equal(((long?)order.Id, (int?)8, "Announce the cancellation"),
            (dto.ThrownByInstanceId, dto.ThrownByNodeId, dto.ThrownByNodeName));
        using var unknown = await SendAsync(HttpMethod.Get, "/api/signal-broadcasts?thrownByKind=robot");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        await using var db = fixture.CreateDbContext();
        foreach (var id in await db.SignalBroadcasts.Where(item => item.SignalName == signal).Select(item => item.Id).ToListAsync())
        {
            await ProcessDeliveryJobsAsync(id);
        }
    }

    [Fact]
    public async Task Publication_RequiresANamedFamilyToExistButADraftSavesWithoutIt()
    {
        var thrower = EndThrowerModel(UniqueSignal(), new SignalThrowScopeModel
        {
            Kind = SignalThrowScopeKinds.Workflow,
            WorkflowKey = $"missing-family-{Guid.NewGuid():N}"
        });
        using var draft = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(thrower, false));
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        var created = (await draft.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;

        using var publish = await SendAsync(HttpMethod.Post, $"/api/workflows/{created.Id}/publish");

        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        Assert.Contains(
            $"Signal end event #3 throws to workflow '{thrower.Signal3Key()}', which has no definition.",
            await publish.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Migration_ChecksTheThrowerSeedsTheBudgetAndGuardsItsDowngrade()
    {
        const string previous = "20261120144115_AddSignalBroadcastScope";
        const string migration = "20261121134938_AddSignalThrowEvents";
        var databaseName = "signal_throw_migration_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
        await ExecuteSqlAsync(adminBuilder.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        try
        {
            await MigrateToAsync(connectionString, migration);
            const string insert = """
                INSERT INTO flowbit.signal_broadcasts
                    ("SignalName", "WorkflowKey", "InstanceId", "Status", "TotalDeliveryCount", "DeliveredCount",
                     "SkippedCount", "FailedCount", "CancelledCount", "RecipientsJson", "RecipientsTruncated",
                     "ThrownByKind", "ThrownBy", "ThrownByRolesJson", "CompletedAt",
                     "ThrowerInstanceId", "ThrowerNodeId", "ThrowerNodeName", "AutomaticActivationCount", "IdempotencyKey")
                VALUES ('a.b', 'orders', 7, 'completed', 0, 0, 0, 0, 0, '[]', false, {0}, '7', '[]', now(), {1}, {2}, {3}, {4}, {5})
                """;
            // An instance thrower names its instance and node and has no idempotency key.
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                ExecuteSqlAsync(connectionString, string.Format(insert, "'instance'", "NULL", "8", "'Throw'", "1", "NULL")));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                ExecuteSqlAsync(connectionString, string.Format(insert, "'instance'", "7", "8", "'Throw'", "1", "'key'")));
            // A user or client thrower names neither and starts a new chain.
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                ExecuteSqlAsync(connectionString, string.Format(insert, "'user'", "7", "NULL", "NULL", "0", "NULL")));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                ExecuteSqlAsync(connectionString, string.Format(insert, "'user'", "NULL", "NULL", "NULL", "2", "NULL")));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                ExecuteSqlAsync(connectionString, string.Format(insert, "'robot'", "NULL", "NULL", "NULL", "0", "NULL")));
            await ExecuteSqlAsync(connectionString, string.Format(insert, "'user'", "NULL", "NULL", "NULL", "0", "NULL"));
            await ExecuteSqlAsync(connectionString, string.Format(insert, "'client'", "NULL", "NULL", "NULL", "0", "'client-key'"));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                ExecuteSqlAsync(connectionString, string.Format(insert, "'client'", "7", "8", "'Throw'", "0", "NULL")));
            await ExecuteSqlAsync(connectionString, string.Format(insert, "'instance'", "7", "8", "'Throw'", "2", "NULL"));
            Assert.Equal("10000", await ScalarAsync(connectionString,
                "SELECT \"Value\" FROM flowbit.engine_settings WHERE \"Namespace\" = 'Signals' AND \"Key\" = 'MaxOpenWorkflowBroadcasts'"));

            var error = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(connectionString, previous));
            Assert.Contains("thrown by workflow instances", error.ToString(), StringComparison.Ordinal);

            await ExecuteSqlAsync(connectionString, "DELETE FROM flowbit.signal_broadcasts WHERE \"ThrownByKind\" = 'instance'");
            await MigrateToAsync(connectionString, previous);
            Assert.Null(await ScalarAsync(connectionString,
                "SELECT \"Value\" FROM flowbit.engine_settings WHERE \"Namespace\" = 'Signals' AND \"Key\" = 'MaxOpenWorkflowBroadcasts'"));
            await MigrateToAsync(connectionString, migration);
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await ExecuteSqlAsync(adminBuilder.ConnectionString, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
        }
    }

    [Fact]
    public async Task BacklogGauge_EqualsThePendingDeliveriesAfterEachPage()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        var receiverId = await CreateAsync(receiver);
        await StartAsync(receiverId, OrderNumber("ORD-1"));
        await StartAsync(receiverId, OrderNumber("ORD-2"));
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(signal, receiver.Id));

        async Task AssertGaugeAsync()
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var backlog = await scope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>()
                .GetBacklogAsync(CancellationToken.None);
            await using var db = fixture.CreateDbContext();
            var open = db.SignalBroadcasts.Where(item => item.Status == SignalBroadcastStatuses.Delivering).Select(item => item.Id);
            var pending = await db.SignalDeliveries.CountAsync(item =>
                item.Status == SignalDeliveryStatuses.Pending && open.Contains(item.BroadcastId));
            Assert.Equal(pending, backlog.PendingDeliveries);
        }

        await AssertGaugeAsync();
        await ProcessDeliveryJobsAsync(broadcast.Id);
        await AssertGaugeAsync();
    }

    private static async Task MigrateToAsync(string connectionString, string targetMigration)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(context)
            .MigrateAsync(targetMigration);
    }

    private static async Task ExecuteSqlAsync(string connectionString, string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync()) as string;
    }
    // Models

    private static string UniqueSignal() => $"test.throw-{Guid.NewGuid():N}";

    private static FlowNodeModel Throw(
        int id,
        string name,
        string signal,
        SignalThrowScopeModel scope,
        List<SignalPayloadEntryModel?>? payload = null,
        string type = BpmnFlowNodeTypes.IntermediateSignalThrowEvent) => new()
        {
            Id = id,
            Name = name,
            Type = type,
            Signal = new SignalEventModel { Name = signal, Scope = scope, Payload = payload ?? [] }
        };

    /// <summary>
    /// An order splits into the shipment task #3, whose interrupting signal boundary #4 leads to
    /// Stop the shipment #5, and the decision task #7, whose action 701 leads to throw #8, which
    /// throws to this instance with the payload reason from cancelReason.
    /// </summary>
    private static WorkflowModel OrderModel(string signal) => new()
    {
        Id = $"signal-order-{Guid.NewGuid():N}",
        Name = "Signal order",
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1, Name = "cancelReason", DataType = WorkflowVariableTypes.String,
                DefaultValue = JsonSerializer.SerializeToElement("Customer request")
            },
            new VariableModel { Id = 2, Name = "stopReason", DataType = WorkflowVariableTypes.String, Nullable = true }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway },
            new FlowNodeModel { Id = 3, Name = "Prepare the shipment", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel
            {
                Id = 4, Name = "Order cancelled", Type = BpmnFlowNodeTypes.SignalBoundaryEvent, AttachedToRef = 3,
                CancelActivity = true,
                Signal = new SignalEventModel
                {
                    Name = signal,
                    OutputMappings =
                    [
                        new MessageOutputMappingModel
                        {
                            Variable = "stopReason", Path = "reason", DataType = WorkflowVariableTypes.String,
                            IsArray = false, Required = false
                        }
                    ]
                }
            },
            new FlowNodeModel { Id = 5, Name = "Stop the shipment", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 6, Name = "Stopped", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 7, Name = "Decide", Type = BpmnFlowNodeTypes.UserTask },
            Throw(8, "Announce the cancellation", signal,
                new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.ThisInstance },
                [new SignalPayloadEntryModel { Name = "reason", Variable = "cancelReason", Required = true }]),
            new FlowNodeModel { Id = 9, Name = "Cancelled", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 10, Name = "Shipped", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 7 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 10, Name = "Ship" },
            new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 },
            new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6, Name = "Close" },
            new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 8, Name = "Cancel the order" },
            new SequenceFlowModel { Id = 801, SourceRef = 8, TargetRef = 9 }
        ]
    };

    /// <summary>A family with active business keys whose start #1 leads to signal catch #2, then task #3.</summary>
    private static WorkflowModel ReceiverModel(string signal) => new()
    {
        Id = $"signal-receiver-{Guid.NewGuid():N}",
        Name = "Signal receiver",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent,
                Variables = [new VariableModel { Id = 11, Name = "orderNumber", DataType = WorkflowVariableTypes.String, Required = true }],
                BusinessKey = new BusinessKeyModel { Variable = "orderNumber", Uniqueness = BusinessKeyUniqueness.Active }
            },
            new FlowNodeModel
            {
                Id = 2, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
                Signal = new SignalEventModel { Name = signal }
            },
            new FlowNodeModel { Id = 3, Name = "Stop", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4, Name = "Close" }
        ]
    };

    /// <summary>Start #1 with input orderNumber, task #2, and signal end event #3 reached by action 201.</summary>
    private static WorkflowModel EndThrowerModel(string signal, SignalThrowScopeModel scope) => new()
    {
        Id = $"signal-end-{Guid.NewGuid():N}",
        Name = "Signal end",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent,
                Variables = [new VariableModel { Id = 11, Name = "orderNumber", DataType = WorkflowVariableTypes.String, Required = true }]
            },
            new FlowNodeModel { Id = 2, Name = "Decide", Type = BpmnFlowNodeTypes.UserTask },
            Throw(3, "Recall announced", signal, scope,
                [new SignalPayloadEntryModel { Name = "orderInstance", Source = SignalPayloadSources.InstanceId }],
                BpmnFlowNodeTypes.SignalEndEvent)
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Recall" }
        ]
    };

    /// <summary>Start #1 (optional number input v), task #2, throw #3 to one family, end #4.</summary>
    private static WorkflowModel SenderModel(string signal, string workflowKey, string? payloadVariable) => new()
    {
        Id = $"signal-sender-{Guid.NewGuid():N}",
        Name = "Signal sender",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent,
                Variables = [new VariableModel { Id = 11, Name = "v", DataType = WorkflowVariableTypes.Number }]
            },
            new FlowNodeModel { Id = 2, Name = "Go", Type = BpmnFlowNodeTypes.UserTask },
            Throw(3, "Send", signal,
                new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = workflowKey },
                payloadVariable is null ? [] : [new SignalPayloadEntryModel { Name = "v", Variable = payloadVariable }]),
            new FlowNodeModel { Id = 4, Name = "Sent", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Go" },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
        ]
    };

    /// <summary>
    /// A parallel split #2 into signal catch #3 and either signal end event #5 or throw #5 and end #6,
    /// both for this instance. <paramref name="catchFirst"/> gives the catch's flow the lower id.
    /// </summary>
    private static WorkflowModel ForkModel(string signal, bool end, bool catchFirst)
    {
        var catchFlow = catchFirst ? 202 : 203;
        var throwFlow = catchFirst ? 203 : 202;
        var model = new WorkflowModel
        {
            Id = $"signal-fork-{Guid.NewGuid():N}",
            Name = "Signal fork",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel
                {
                    Id = 3, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
                    Signal = new SignalEventModel { Name = signal }
                },
                new FlowNodeModel { Id = 4, Name = "Caught", Type = BpmnFlowNodeTypes.EndEvent },
                Throw(5, "Announce", signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.ThisInstance },
                    type: end ? BpmnFlowNodeTypes.SignalEndEvent : BpmnFlowNodeTypes.IntermediateSignalThrowEvent)
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = catchFlow, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = throwFlow, SourceRef = 2, TargetRef = 5 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };
        if (!end)
        {
            model.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = "Announced", Type = BpmnFlowNodeTypes.EndEvent });
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 });
        }

        return model;
    }

    private static Dictionary<string, JsonElement> OrderNumber(string value) => new()
    {
        ["orderNumber"] = JsonSerializer.SerializeToElement(value)
    };

    private static string? Compact(string? json) =>
        json is null ? null : JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

    // HTTP and database helpers

    private async Task<long> CreateAsync(WorkflowModel model)
    {
        using var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return (await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!.Id;
    }

    private async Task<long> DefinitionIdAsync(string workflowKey)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowDefinitions.Where(item => item.WorkflowKey == workflowKey)
            .Select(item => item.Id).SingleAsync();
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId, Dictionary<string, JsonElement>? variables = null)
    {
        using var start = await SendAsync(HttpMethod.Post, "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, variables));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        return (await start.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private async Task<SignalBroadcastDetailDto> ThrowApiAsync(ThrowSignalRequest request)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
    }

    private async Task<List<SignalBroadcastEntity>> BroadcastsThrownByAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SignalBroadcasts.AsNoTracking()
            .Where(item => item.ThrowerInstanceId == instanceId)
            .OrderBy(item => item.Id)
            .ToListAsync();
    }

    private async Task<int[]> ActiveTaskNodesAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.Where(task => task.InstanceId == instanceId && task.Status == UserTaskStatuses.Active)
            .Select(task => task.NodeId).OrderBy(id => id).ToArrayAsync();
    }

    private async Task TakeAsync(long instanceId, int nodeId, int flowId)
    {
        using var action = await TakeResponseAsync(instanceId, nodeId, flowId);
        Assert.True(action.StatusCode == HttpStatusCode.OK, await action.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> TakeResponseAsync(long instanceId, int nodeId, int flowId)
    {
        long taskId;
        await using (var db = fixture.CreateDbContext())
        {
            taskId = await db.UserTasks.Where(task => task.InstanceId == instanceId && task.NodeId == nodeId
                && task.Status == UserTaskStatuses.Active).Select(task => task.Id).FirstAsync();
        }

        return await SendAsync(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/{flowId}", new TakeFlowRequest(null));
    }

    private async Task<string?> SetEngineSettingAsync(string ns, string key, string value)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleOrDefaultAsync(item => item.Namespace == ns && item.Key == key);
        var previous = setting?.Value;
        if (setting is null)
        {
            db.EngineSettings.Add(new EngineSettingEntity { Namespace = ns, Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return previous;
    }

    private async Task RestoreEngineSettingAsync(string ns, string key, string? previous)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleAsync(item => item.Namespace == ns && item.Key == key);
        if (previous is null)
        {
            db.EngineSettings.Remove(setting);
        }
        else
        {
            setting.Value = previous;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    private async Task ProcessDeliveryJobsAsync(long broadcastId)
    {
        for (var round = 0; round < 20; round++)
        {
            await using (var db = fixture.CreateDbContext())
            {
                var open = await db.SignalBroadcastJobs.AsNoTracking()
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

            await LeaseAndProcessOneAsync();
        }

        Assert.Fail($"The delivery jobs of signal broadcast #{broadcastId} did not finish.");
    }

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

            await LeaseAndProcessOneAsync();
        }

        Assert.Fail($"The {kind} jobs of instance #{instanceId} did not finish.");
    }

    private async Task LeaseAndProcessOneAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
            new WorkflowJobLeaseRequest($"signal-throw-test-{Guid.NewGuid():N}", 1, 1, 1, TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        foreach (var lease in leases)
        {
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
                .ProcessAsync(lease, CancellationToken.None);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, "signal-throw", ["admin"]);
        return await fixture.Client.SendAsync(request);
    }
}

internal static class SignalThrowTestModelExtensions
{
    /// <summary>The workflow key the signal end event #3 of a thrower model names.</summary>
    public static string? Signal3Key(this WorkflowModel model) =>
        model.FlowNodes.Single(node => node.Id == 3).Signal?.Scope?.WorkflowKey;
}
