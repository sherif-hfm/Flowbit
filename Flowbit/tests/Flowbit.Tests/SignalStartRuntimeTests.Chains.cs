using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// EVENT-04C cases that span more than one delivery: a workflow throw with each scope kind, the chain count across
/// versions and user actions, a widening loop, durable work behind a start, a job that runs after its delivery
/// committed, a start rolled back after its instance was written, logs, two races, and retention.
/// </summary>
public sealed partial class SignalStartRuntimeTests
{
    // Freeze: a workflow throw reaches a start only through a family or every family.

    [Theory]
    [InlineData(SignalThrowScopeKinds.ThisInstance, 0)]
    [InlineData(SignalThrowScopeKinds.ThisWorkflow, 0)]
    [InlineData(SignalThrowScopeKinds.Workflow, 1)]
    [InlineData(SignalThrowScopeKinds.BusinessKey, 0)]
    [InlineData(SignalThrowScopeKinds.OwnBusinessKey, 0)]
    [InlineData(SignalThrowScopeKinds.AllWorkflows, 1)]
    public async Task WorkflowThrow_ReachesAStartOnlyThroughAFamilyOrEveryFamily(string kind, int expectedStarts)
    {
        var signal = UniqueSignal();
        var target = KeyedTargetModel(signal);
        await CreateAsync(target);
        var scope = new SignalThrowScopeModel { Kind = kind };
        if (kind is SignalThrowScopeKinds.Workflow or SignalThrowScopeKinds.BusinessKey or SignalThrowScopeKinds.OwnBusinessKey)
        {
            scope.WorkflowKey = target.Id;
        }
        if (kind == SignalThrowScopeKinds.BusinessKey)
        {
            scope.BusinessKeyVariable = "orderNumber";
        }
        var thrower = ThrowerModel(signal, scope, withCatch: kind == SignalThrowScopeKinds.ThisInstance);
        var throwerId = await CreateAsync(thrower);

        var orderNumber = $"K-{kind}-{Guid.NewGuid():N}"[..24];
        var instance = await StartWithAsync(throwerId, new() { ["orderNumber"] = JsonSerializer.SerializeToElement(orderNumber) });
        long broadcastId;
        await using (var db = fixture.CreateDbContext())
        {
            var broadcast = await db.SignalBroadcasts.AsNoTracking().SingleAsync(item => item.ThrowerInstanceId == instance);
            Assert.Equal((SignalThrowerKinds.Instance, expectedStarts), (broadcast.ThrownByKind, broadcast.StartDeliveryCount));
            broadcastId = broadcast.Id;
        }
        Assert.Equal(expectedStarts, (await DeliveriesAsync(broadcastId, SignalDeliveryKinds.Start)).Count);

        await ProcessDeliveryJobsAsync(broadcastId);
        await using (var db = fixture.CreateDbContext())
        {
            var started = await db.WorkflowInstances.AsNoTracking().Where(item => item.WorkflowKey == target.Id).ToListAsync();
            Assert.Equal(expectedStarts, started.Count);
            if (expectedStarts == 1)
            {
                Assert.Equal(orderNumber, await db.InstanceVariableCurrentValues.AsNoTracking()
                    .Where(item => item.InstanceId == started[0].Id && item.VariableName == "recallNumber")
                    .Select(item => item.ValueJson.RootElement.GetString()).SingleAsync());
            }
        }
    }

    // Loop guard

    /// <summary>
    /// An instance of an older version throws to its own family, whose newer default starts on the name: the throw
    /// starts one instance of the default, which continues the broadcast's count.
    /// </summary>
    [Fact]
    public async Task LoopGuard_AnOlderVersionThrowingToItsFamilyStartsTheNewDefaultWhichContinuesTheCount()
    {
        var signal = UniqueSignal();
        var older = new WorkflowModel
        {
            Id = $"signal-start-family-{Guid.NewGuid():N}",
            Name = "Older thrower",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 3, Name = "Announce", Type = BpmnFlowNodeTypes.SignalEndEvent,
                    Signal = new SignalEventModel
                    {
                        Name = signal, Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.ThisWorkflow }, Payload = []
                    }
                }
            ],
            SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }, new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Announce" }]
        };
        var v1 = await CreateAsync(older);
        var throwing = await StartAsync(v1);

        var newer = KeyedTargetModel(signal, older.Id);
        newer.FlowNodes[0].BusinessKey = null;
        newer.FlowNodes[0].Signal!.OutputMappings = [];
        var v2 = await NewVersionAsync(v1, newer);
        await SetDefaultAsync(v2);

        // The user action starts a new chain, so the throw stores 1.
        await TakeAsync(throwing.Id, 2, 201);
        await using var db = fixture.CreateDbContext();
        var broadcast = await db.SignalBroadcasts.AsNoTracking().SingleAsync(item => item.ThrowerInstanceId == throwing.Id);
        Assert.Equal((1, 1), (broadcast.StartDeliveryCount, broadcast.AutomaticActivationCount));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var delivery = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryResultCodes.Started, v2), (delivery.ResultCode, delivery.WorkflowDefinitionId));
        var instance = await db.WorkflowInstances.AsNoTracking().SingleAsync(item => item.Id == delivery.InstanceId);
        Assert.Equal(v2, instance.WorkflowDefinitionId);
        Assert.Equal(1, await db.ExecutionTokens.AsNoTracking()
            .Where(token => token.InstanceId == instance.Id).OrderBy(token => token.Id)
            .Select(token => token.AutomaticActivationCount).FirstAsync());
    }

    /// <summary>
    /// A start continues the count of the workflow throw that started it, and a user action in the started instance
    /// starts a new chain before its own throw.
    /// </summary>
    [Fact]
    public async Task LoopGuard_AUserActionBetweenTheStartAndTheThrowStartsANewChain()
    {
        var first = UniqueSignal();
        var second = UniqueSignal();
        var last = KeyedTargetModel(second);
        last.FlowNodes[0].BusinessKey = null;
        last.FlowNodes[0].Signal!.OutputMappings = [];
        await CreateAsync(last);
        var middle = new WorkflowModel
        {
            Id = $"signal-start-middle-{Guid.NewGuid():N}",
            Name = "Middle",
            InitialEventId = null,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Started", Type = BpmnFlowNodeTypes.SignalStartEvent, Signal = new SignalEventModel { Name = first } },
                new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 3, Name = "Pass it on", Type = BpmnFlowNodeTypes.SignalEndEvent,
                    Signal = new SignalEventModel
                    {
                        Name = second, Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = last.Id }, Payload = []
                    }
                }
            ],
            SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }, new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Pass" }]
        };
        await CreateAsync(middle);
        var kick = EchoStartModel(UniqueSignal(), first, middle.Id);
        kick.InitialEventId = 1;
        kick.FlowNodes[0] = new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent };
        var kickId = await CreateAsync(kick);

        var kicker = await StartAsync(kickId);
        await using var db = fixture.CreateDbContext();
        var thrown = await db.SignalBroadcasts.AsNoTracking().SingleAsync(item => item.ThrowerInstanceId == kicker.Id);
        Assert.Equal(1, thrown.AutomaticActivationCount);
        await ProcessDeliveryJobsAsync(thrown.Id);
        var started = Assert.Single(await DeliveriesAsync(thrown.Id)).InstanceId!.Value;
        Assert.Equal(1, await db.ExecutionTokens.AsNoTracking()
            .Where(token => token.InstanceId == started).OrderBy(token => token.Id)
            .Select(token => token.AutomaticActivationCount).FirstAsync());

        await TakeAsync(started, 2, 201);
        var passed = await db.SignalBroadcasts.AsNoTracking().SingleAsync(item => item.ThrowerInstanceId == started);
        Assert.Equal((1, 1), (passed.AutomaticActivationCount, passed.StartDeliveryCount));
        await ProcessDeliveryJobsAsync(passed.Id);
        Assert.Equal(SignalDeliveryResultCodes.Started, Assert.Single(await DeliveriesAsync(passed.Id)).ResultCode);
    }

    /// <summary>
    /// Two families start on one name and throw a second, and a third starts on the second and throws the first to
    /// every family, so the loop widens and the chain count alone would allow 1,000 steps. A low
    /// Signals.MaxOpenWorkflowBroadcasts refuses a throw, and defaults without the starts end the loop with skipped
    /// deliveries.
    /// </summary>
    [Fact]
    public async Task LoopGuard_AWideningLoopStopsAtTheOpenBroadcastLimitAndEndsWhenTheDefaultsStopListening()
    {
        var wide = UniqueSignal();
        var narrow = UniqueSignal();
        var joiner = EchoStartModel(narrow, wide, "unused");
        joiner.FlowNodes[1].Signal!.Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows };
        var joinerId = await CreateAsync(joiner);
        var left = EchoStartModel(wide, narrow, joiner.Id);
        var leftId = await CreateAsync(left);
        var right = EchoStartModel(wide, narrow, joiner.Id);
        var rightId = await CreateAsync(right);

        int openBefore;
        await using (var db = fixture.CreateDbContext())
        {
            openBefore = await db.SignalBroadcasts.CountAsync(item =>
                item.Status == SignalBroadcastStatuses.Delivering && item.ThrownByKind == SignalThrowerKinds.Instance);
        }
        var previous = await SetEngineSettingAsync("Signals", "MaxOpenWorkflowBroadcasts", (openBefore + 3).ToString());
        try
        {
            var kick = await ThrowApiAsync(new ThrowSignalRequest(wide, AllWorkflows: true));
            Assert.Equal(2, kick.StartCount);

            var refused = false;
            for (var round = 0; round < 20 && !refused; round++)
            {
                if (!await ProcessOpenStartDeliveriesAsync(wide, narrow)) break;
                await using var db = fixture.CreateDbContext();
                refused = await RefusedDeliveriesOf(db, wide, narrow).AnyAsync();
            }
            Assert.True(refused, "No throw of the widening loop was refused.");
            await using (var db = fixture.CreateDbContext())
            {
                var refusal = await RefusedDeliveriesOf(db, wide, narrow).FirstAsync();
                Assert.Equal(SignalDeliveryResultCodes.DeliveryRejected, refusal.ResultCode);
                Assert.Contains("Signals.MaxOpenWorkflowBroadcasts", refusal.ResultDescription, StringComparison.Ordinal);
                Assert.Null(refusal.InstanceId);
            }

            // Defaults without the starts end it: the frozen starts that remain are skipped.
            foreach (var (id, model) in new[] { (leftId, left), (rightId, right), (joinerId, joiner) })
            {
                await SetDefaultAsync(await NewVersionAsync(id, PlainModel(model.Id)));
            }
            for (var round = 0; round < 20; round++)
            {
                if (!await ProcessOpenStartDeliveriesAsync(wide, narrow)) break;
            }
            await using (var db = fixture.CreateDbContext())
            {
                Assert.False(await db.SignalBroadcasts.AnyAsync(item =>
                    (item.SignalName == wide || item.SignalName == narrow) && item.Status == SignalBroadcastStatuses.Delivering));
                Assert.Empty(await db.SignalStartSubscriptions.Where(item => item.SignalName == wide || item.SignalName == narrow).ToListAsync());
            }
        }
        finally
        {
            await RestoreEngineSettingAsync("Signals", "MaxOpenWorkflowBroadcasts", previous);
        }
    }

    // Start delivery

    /// <summary>
    /// The flow leaving a signal start is traversed with the kind signalStart: a gateway right behind the start reads it
    /// through FlowInfo in the start's own routing.
    /// </summary>
    [Fact]
    public async Task TraversalKind_TheFlowLeavingASignalStartIsTraversedAsSignalStart()
    {
        var signal = UniqueSignal();
        var model = new WorkflowModel
        {
            Id = $"signal-start-traversal-{Guid.NewGuid():N}",
            Name = "Traversal kind",
            InitialEventId = null,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Started", Type = BpmnFlowNodeTypes.SignalStartEvent, Signal = new SignalEventModel { Name = signal } },
                new FlowNodeModel { Id = 2, Name = "How did it start?", Type = BpmnFlowNodeTypes.ExclusiveGateway },
                new FlowNodeModel { Id = 3, Name = "Started by signal", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 4, Name = "Started otherwise", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 5, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201, SourceRef = 2, TargetRef = 3, Name = "Signal",
                    Condition = "FlowInfo(101, 'traversals.last.kind') == 'signalStart'", ConditionPriority = 1
                },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4, Name = "Otherwise", IsDefault = true },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5, Name = "Done" },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5, Name = "Done" }
            ]
        };
        await CreateAsync(model);

        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(signal, model.Id));
        await ProcessDeliveryJobsAsync(broadcast.Id);

        var instanceId = Assert.Single(await DeliveriesAsync(broadcast.Id)).InstanceId!.Value;
        Assert.Equal(new[] { 3 }, await ActiveTaskNodesAsync(instanceId));
    }

    /// <summary>A script task with asyncBefore behind the start becomes a queued job, which the Worker then runs.</summary>
    [Fact]
    public async Task AsyncWork_AScriptBehindTheStartIsQueuedAndRunsAsAJob()
    {
        var signal = UniqueSignal();
        var model = new WorkflowModel
        {
            Id = $"signal-start-async-{Guid.NewGuid():N}",
            Name = "Start with durable work",
            InitialEventId = null,
            Variables =
            [
                new VariableModel
                {
                    Id = 1, Name = "noted", DataType = WorkflowVariableTypes.String, IsArray = false,
                    DefaultValue = JsonSerializer.SerializeToElement("no")
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Started", Type = BpmnFlowNodeTypes.SignalStartEvent, Signal = new SignalEventModel { Name = signal } },
                new FlowNodeModel
                {
                    Id = 2, Name = "Note it", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
                    Assignments = [new AssignmentModel { Variable = "noted", Expression = "'yes'" }]
                },
                new FlowNodeModel { Id = 3, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4, Name = "Done" }
            ]
        };
        await CreateAsync(model);

        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(signal, model.Id));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var instanceId = Assert.Single(await DeliveriesAsync(broadcast.Id)).InstanceId!.Value;
        Assert.Empty(await ActiveTaskNodesAsync(instanceId));
        long jobId;
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.AsNoTracking().SingleAsync(item => item.InstanceId == instanceId && item.NodeId == 2);
            Assert.Equal(WorkflowJobStatuses.Queued, job.Status);
            jobId = job.Id;
        }

        for (var round = 0; round < 10 && (await ActiveTaskNodesAsync(instanceId)).Length == 0; round++)
        {
            await using (var db = fixture.CreateDbContext())
            {
                var job = await db.WorkflowJobs.SingleAsync(item => item.Id == jobId);
                job.Priority = 1_000_000;
                job.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                await db.SaveChangesAsync();
            }
            await LeaseAndProcessOneAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var state = await db.WorkflowJobs.AsNoTracking().Where(item => item.Id == jobId).Select(item => item.Status).SingleAsync();
            var incidents = await db.WorkflowIncidents.AsNoTracking().Where(item => item.InstanceId == instanceId)
                .Select(item => item.Summary + " " + item.Details).ToListAsync();
            Assert.True((await ActiveTaskNodesAsync(instanceId)).SequenceEqual([3]),
                $"The task is not active; job {state}; incidents: {string.Join(" | ", incidents)}");
        }
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal("yes", await db.InstanceVariableCurrentValues.AsNoTracking()
                .Where(item => item.InstanceId == instanceId && item.VariableName == "noted")
                .Select(item => item.ValueJson.RootElement.GetString()).SingleAsync());
        }
    }

    // Exactly once

    /// <summary>
    /// The delivery commits, and its job runs afterwards, as after a crash between the two: the job finds nothing
    /// pending and starts nothing more.
    /// </summary>
    [Fact]
    public async Task ExactlyOnce_AJobThatRunsAfterItsDeliveryCommittedStartsNothingMore()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-X", product = new { name = "Lamp" } })));
        var pending = Assert.Single(await DeliveriesAsync(broadcast.Id));
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISignalDeliveryExecutor>()
                .DeliverSignalAsync(pending.Id, CancellationToken.None);
        }

        await ProcessDeliveryJobsAsync(broadcast.Id);

        Assert.Equal(1, await FamilyInstanceCountAsync(model.Id));
        var delivery = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal(SignalDeliveryResultCodes.Started, delivery.ResultCode);
        Assert.Equal(SignalBroadcastStatuses.Completed, (await BroadcastAsync(broadcast.Id)).Status);
    }

    /// <summary>
    /// A throw refused during the start's routing fails the delivery after the instance and its business-key claim
    /// were written; the transaction rolls both back, so a retry starts the instance with the same key.
    /// </summary>
    [Fact]
    public async Task Rollback_AStartRefusedAfterItsInstanceWasWrittenLeavesNoInstanceAndNoClaim()
    {
        // A waiting instance and a thrower whose broadcast to it stays delivering, which fills the budget below.
        var waited = UniqueSignal();
        var waiter = WaiterModel(waited);
        await StartAsync(await CreateAsync(waiter));
        var filler = new WorkflowModel
        {
            Id = $"signal-start-filler-{Guid.NewGuid():N}",
            Name = "Filler",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                PassItOn(2, waited, waiter.Id)
            ],
            SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
        };
        var fillerInstance = await StartWithAsync(await CreateAsync(filler), null);

        // The start ends in a signal end event that throws to the waiter family.
        var signal = UniqueSignal();
        var model = KeyedTargetModel(signal);
        model.FlowNodes = [model.FlowNodes[0], PassItOn(2, waited, waiter.Id)];
        model.SequenceFlows = [model.SequenceFlows[0]];
        await CreateAsync(model);

        long fillerBroadcast;
        int open;
        await using (var db = fixture.CreateDbContext())
        {
            fillerBroadcast = await db.SignalBroadcasts.AsNoTracking()
                .Where(item => item.ThrowerInstanceId == fillerInstance).Select(item => item.Id).SingleAsync();
            open = await db.SignalBroadcasts.CountAsync(item =>
                item.Status == SignalBroadcastStatuses.Delivering && item.ThrownByKind == SignalThrowerKinds.Instance);
        }
        Assert.True(open >= 1);
        var previous = await SetEngineSettingAsync("Signals", "MaxOpenWorkflowBroadcasts", open.ToString());
        SignalBroadcastDetailDto broadcast;
        try
        {
            broadcast = await ThrowApiAsync(new ThrowSignalRequest(signal, model.Id, Payload: Payload(new { recallNumber = "R-ROLLBACK" })));
            await ProcessDeliveryJobsAsync(broadcast.Id);
        }
        finally
        {
            await RestoreEngineSettingAsync("Signals", "MaxOpenWorkflowBroadcasts", previous);
        }

        var failed = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.DeliveryRejected, (long?)null),
            (failed.Status, failed.ResultCode, failed.InstanceId));
        Assert.Contains("Signals.MaxOpenWorkflowBroadcasts", failed.ResultDescription, StringComparison.Ordinal);
        Assert.Equal(0, await FamilyInstanceCountAsync(model.Id));
        await ProcessDeliveryJobsAsync(fillerBroadcast);

        // The retry claims the same key, which the failed start never kept.
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

    /// <summary>A signal end event that throws the signal to the named family, with no payload.</summary>
    private static FlowNodeModel PassItOn(int id, string signal, string workflowKey) => new()
    {
        Id = id, Name = "Pass it on", Type = BpmnFlowNodeTypes.SignalEndEvent,
        Signal = new SignalEventModel
        {
            Name = signal, Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = workflowKey }, Payload = []
        }
    };

    // Business key

    [Fact]
    public async Task BusinessKey_NoLogLineContainsTheKey()
    {
        var signal = UniqueSignal();
        var model = KeyedTargetModel(signal);
        await CreateAsync(model);
        var businessKey = $"PRIVATE-{Guid.NewGuid():N}";

        // Another host on the same database whose loggers all write to the capture.
        var logs = new StartLogCapture();
        await using var host = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerFactory>(logs)));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        async Task<long> ThrowWithHostAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/signal-broadcasts")
            {
                Content = JsonContent.Create(new ThrowSignalRequest(signal, model.Id, Payload: Payload(new { recallNumber = businessKey })), options: JsonOptions)
            };
            ApiTestAuth.Authorize(request, "signal-start", ["admin"]);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!.Id;
        }

        var first = await ThrowWithHostAsync();
        await ProcessDeliveryJobsAsync(first, host.Services);
        var second = await ThrowWithHostAsync();
        await ProcessDeliveryJobsAsync(second, host.Services);

        Assert.Equal(SignalDeliveryResultCodes.Started, Assert.Single(await DeliveriesAsync(first)).ResultCode);
        var skipped = Assert.Single(await DeliveriesAsync(second));
        Assert.Equal(SignalDeliveryResultCodes.BusinessKeyConflict, skipped.ResultCode);
        Assert.DoesNotContain(businessKey, skipped.ResultDescription!, StringComparison.Ordinal);
        var flowbit = logs.Entries.Where(entry => entry.StartsWith("Flowbit.", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(flowbit);
        Assert.DoesNotContain(flowbit, entry => entry.Contains(businessKey, StringComparison.Ordinal));
    }

    // Races

    /// <summary>A throw that reaches a wait and a start does not wait while another transaction holds the waiting instance's row.</summary>
    [Fact]
    public async Task Race_AThrowDoesNotWaitForAWaitingInstanceThatAnotherTransactionHolds()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        await CreateAsync(model);
        var waiting = await StartAsync(await CreateAsync(WaiterModel(signal)));

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
                         "SELECT 1 FROM flowbit.workflow_instances WHERE \"Id\" = @id FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("id", waiting.Id);
            await command.ExecuteScalarAsync();
        }

        var thrown = ThrowApiAsync(new ThrowSignalRequest(
            signal, AllWorkflows: true, Payload: Payload(new { recallNumber = "R-L", product = new { name = "Fan" } })));
        var finished = await Task.WhenAny(thrown, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(ReferenceEquals(finished, thrown), "The throw waited for the locked instance.");
        var broadcast = await thrown;
        Assert.Equal((2, 1), (broadcast.TotalDeliveryCount, broadcast.StartCount));

        await transaction.RollbackAsync();
        await ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.All(await DeliveriesAsync(broadcast.Id), delivery => Assert.Equal(SignalDeliveryStatuses.Delivered, delivery.Status));
    }

    /// <summary>
    /// Two starts of one family hold its shared lock together, and a default change waits until both have finished.
    /// </summary>
    [Fact]
    public async Task Race_TwoStartsHoldTheFamilyTogetherAndADefaultChangeWaitsForBoth()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var v1 = await CreateAsync(model);
        var v2 = await NewVersionAsync(v1, RecallModel(signal, model.Id));

        await using var firstScope = fixture.Factory.Services.CreateAsyncScope();
        await using var secondScope = fixture.Factory.Services.CreateAsyncScope();
        await using var first = await firstScope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginTransactionAsync(CancellationToken.None);
        await firstScope.ServiceProvider.GetRequiredService<IWorkflowDefinitionRepository>()
            .LockFamilyForStartAsync(model.Id, CancellationToken.None);
        await using var second = await secondScope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginTransactionAsync(CancellationToken.None);
        var secondLock = secondScope.ServiceProvider.GetRequiredService<IWorkflowDefinitionRepository>()
            .LockFamilyForStartAsync(model.Id, CancellationToken.None);
        Assert.True(ReferenceEquals(await Task.WhenAny(secondLock, Task.Delay(TimeSpan.FromSeconds(10))), secondLock),
            "The second start waited for the first.");
        await secondLock;

        var change = SetDefaultAsync(v2);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(change.IsCompleted, "The default change did not wait for the starts.");
        await first.CommitAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(change.IsCompleted, "The default change did not wait for the second start.");
        await second.CommitAsync(CancellationToken.None);
        await change;
        Assert.Equal(v2, Assert.Single(await StartRowsAsync(model.Id)).WorkflowDefinitionId);
    }

    // Retention

    [Fact]
    public async Task Retention_RemovesAnOldFinishedBroadcastWithAStartDeliveryAndKeepsTheInstance()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-OLD", product = new { name = "Kettle" } })));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var instanceId = Assert.Single(await DeliveriesAsync(broadcast.Id)).InstanceId!.Value;
        await using (var db = fixture.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE flowbit.signal_broadcasts
                SET "CreatedAt" = now() - interval '10 days', "CompletedAt" = now() - interval '10 days'
                WHERE "Id" = {broadcast.Id}
                """);
        }

        await SetSignalRetentionAsync(1);
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
                        WorkerId = "signal-start-retention-test", BatchSize = 100, MaxRunnableJobs = 1_000_000, MaxQueueLagSeconds = 3600
                    }, CancellationToken.None);
                    if (!result.HasActiveRun) break;
                }
            }
        }
        finally
        {
            await SetSignalRetentionAsync(null);
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.Id == broadcast.Id));
            Assert.False(await db.SignalDeliveries.AnyAsync(item => item.BroadcastId == broadcast.Id));
            Assert.True(await db.WorkflowInstances.AnyAsync(item => item.Id == instanceId));
        }
    }

    // Models and helpers

    /// <summary>
    /// Signal start #1 on the signal with the required string mapping recallNumber, which is the business key, then
    /// task #2 and end #3.
    /// </summary>
    private static WorkflowModel KeyedTargetModel(string signal, string? key = null) => new()
    {
        Id = key ?? $"signal-start-target-{Guid.NewGuid():N}",
        Name = "Keyed target",
        InitialEventId = null,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1, Name = "Started", Type = BpmnFlowNodeTypes.SignalStartEvent,
                Signal = new SignalEventModel { Name = signal, OutputMappings = [Mapping("recallNumber", "recallNumber", required: true)] },
                BusinessKey = new BusinessKeyModel { Variable = "recallNumber", Uniqueness = BusinessKeyUniqueness.Active }
            },
            new FlowNodeModel { Id = 2, Name = "Handle", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Done" }
        ]
    };

    /// <summary>
    /// Start #1 with the business key orderNumber, signal throw #2 with the scope, sending orderNumber as recallNumber,
    /// then (for this instance) catch #3 on the signal, task #4, and end #5.
    /// </summary>
    private static WorkflowModel ThrowerModel(string signal, SignalThrowScopeModel scope, bool withCatch)
    {
        var nodes = new List<FlowNodeModel>
        {
            new()
            {
                Id = 1, Name = "Order", Type = BpmnFlowNodeTypes.StartEvent,
                Variables = [new VariableModel { Id = 1, Name = "orderNumber", DataType = WorkflowVariableTypes.String, IsArray = false, Required = true }],
                BusinessKey = new BusinessKeyModel { Variable = "orderNumber", Uniqueness = BusinessKeyUniqueness.Active }
            },
            new()
            {
                Id = 2, Name = "Announce", Type = BpmnFlowNodeTypes.IntermediateSignalThrowEvent,
                Signal = new SignalEventModel
                {
                    Name = signal, Scope = scope, Payload = [new SignalPayloadEntryModel { Name = "recallNumber", Variable = "orderNumber" }]
                }
            },
            new() { Id = 4, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new() { Id = 5, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        };
        var flows = new List<SequenceFlowModel>
        {
            new() { Id = 101, SourceRef = 1, TargetRef = 2 },
            new() { Id = 401, SourceRef = 4, TargetRef = 5, Name = "Done" }
        };
        if (withCatch)
        {
            nodes.Add(new FlowNodeModel { Id = 3, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent, Signal = new SignalEventModel { Name = signal } });
            flows.Add(new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 });
            flows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 });
        }
        else
        {
            flows.Add(new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 4 });
        }

        return new WorkflowModel
        {
            Id = $"signal-start-thrower-{Guid.NewGuid():N}",
            Name = "Thrower",
            InitialEventId = 1,
            FlowNodes = nodes,
            SequenceFlows = flows
        };
    }

    /// <summary>A plain start #1 straight to end #2, under the given family key.</summary>
    private static WorkflowModel PlainModel(string key) => new()
    {
        Id = key,
        Name = "Plain",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
    };

    private static IQueryable<Flowbit.Infrastructure.Entities.SignalDeliveryEntity> RefusedDeliveriesOf(
        AppDbContext db, string first, string second) =>
        db.SignalDeliveries.AsNoTracking()
            .Where(item => item.Status == SignalDeliveryStatuses.Failed
                && db.SignalBroadcasts.Any(broadcast => broadcast.Id == item.BroadcastId
                    && (broadcast.SignalName == first || broadcast.SignalName == second)));

    private async Task<long> StartWithAsync(long workflowId, Dictionary<string, JsonElement>? variables)
    {
        using var start = await SendAsync(HttpMethod.Post, "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, variables));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        return (await start.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!.Id;
    }

    private async Task SetSignalRetentionAsync(int? days)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE flowbit.retention_policies SET "RetentionDays" = @days, "Revision" = "Revision" + 1, "IsInitialized" = TRUE
            WHERE "Category" = @category
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("days", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)days ?? DBNull.Value });
        command.Parameters.AddWithValue("category", RetentionCategories.SignalBroadcasts);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>Records every log line, its category first, with its structured values, whatever its level.</summary>
    private sealed class StartLogCapture : ILoggerFactory
    {
        private readonly ConcurrentQueue<string> entries = new();

        public IReadOnlyList<string> Entries => entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(" ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                    : string.Empty;
                entries.Enqueue($"{category} {formatter(state, exception)} {values} {exception}");
            }
        }
    }
}
