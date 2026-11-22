using System.Net;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The loop guard of section 5 of the EVENT-04B plan: every hop of a loop through signals
/// passes a broadcast, which stores the thrower's count plus one, and every place where the
/// work changes tokens hands the count over, so a loop stops at the limit. A person, a
/// message, and a timer start a new chain.
/// </summary>
public sealed partial class SignalThrowRuntimeTests
{
    private const string GuardNamespace = "Workflow.Async";
    private const string GuardKey = "MaxConsecutiveAutomaticActivations";

    /// <summary>
    /// The branch a signal starts enters a task whose interrupting conditional boundary is
    /// already true, and that boundary leads to a throw of the same signal. The capture on
    /// entry continues the entering token's chain, so the loop is counted.
    /// </summary>
    [Fact]
    public async Task LoopGuard_CountsALoopThroughAConditionalBoundaryThatIsTrueOnEntry()
    {
        var signal = UniqueSignal();
        var model = new WorkflowModel
        {
            Id = $"signal-entry-loop-{Guid.NewGuid():N}",
            Name = "Signal loop through a boundary that is true on entry",
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1, Name = "armed", DataType = WorkflowVariableTypes.Boolean,
                    DefaultValue = JsonSerializer.SerializeToElement(true)
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Hold", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 3, Name = "Pinged", Type = BpmnFlowNodeTypes.SignalBoundaryEvent, AttachedToRef = 2,
                    CancelActivity = false, Signal = new SignalEventModel { Name = signal }
                },
                new FlowNodeModel { Id = 7, Name = "Park", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 8, Name = "Armed", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = 7,
                    CancelActivity = true, Conditional = new ConditionalDefinitionModel { Condition = "armed == true" }
                },
                Throw(4, "Ping again", signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.ThisInstance }),
                new FlowNodeModel { Id = 5, Name = "Pinged end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 6, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 9, Name = "Parked end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 6, Name = "Done" },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 7 },
                new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 9, Name = "Leave" },
                new SequenceFlowModel { Id = 801, SourceRef = 8, TargetRef = 4 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 }
            ]
        };
        var instance = await StartAsync(await CreateAsync(model));

        await WithChainLimitAsync(3, async () =>
        {
            await ProcessDeliveryJobsAsync((await ThrowApiAsync(new ThrowSignalRequest(signal, InstanceId: instance.Id))).Id);
            await RunThrownBroadcastsAsync(instance.Id);
        });

        var thrown = await BroadcastsThrownByAsync(instance.Id);
        Assert.Equal([1, 2, 3], thrown.Select(item => item.AutomaticActivationCount).Order().ToArray());
        await AssertChainLimitRefusalAsync(thrown.Single(item => item.AutomaticActivationCount == 3).Id, 3);
    }

    /// <summary>Two families that catch and throw each other's signal stop at the limit.</summary>
    [Fact]
    public async Task LoopGuard_StopsTwoInstancesThatThrowToEachOther()
    {
        var ping = UniqueSignal();
        var pong = UniqueSignal();
        var keyA = $"signal-ping-{Guid.NewGuid():N}";
        var keyB = $"signal-pong-{Guid.NewGuid():N}";
        // Each names the other, so A is saved as a draft, B published, and then A published.
        var draftA = await CreateDraftAsync(EchoModel(keyA, ping, pong,
            new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = keyB }));
        var b = await StartAsync(await CreateAsync(EchoModel(keyB, pong, ping,
            new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = keyA })));
        using (var publish = await SendAsync(HttpMethod.Post, $"/api/workflows/{draftA}/publish"))
        {
            Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());
        }

        var a = await StartAsync(draftA);

        await WithChainLimitAsync(3, async () =>
        {
            await ProcessDeliveryJobsAsync((await ThrowApiAsync(new ThrowSignalRequest(ping, keyA))).Id);
            await RunThrownBroadcastsAsync(a.Id, b.Id);
        });

        var fromA = await BroadcastsThrownByAsync(a.Id);
        var fromB = await BroadcastsThrownByAsync(b.Id);
        Assert.Equal([1, 3], fromA.Select(item => item.AutomaticActivationCount).ToArray());
        Assert.Equal([2], fromB.Select(item => item.AutomaticActivationCount).ToArray());
        // Broadcast 3 reaches B, whose next throw would be the fourth hop.
        await AssertChainLimitRefusalAsync(fromA.Last().Id, 3);
    }

    /// <summary>
    /// A person between two throws starts a new chain, so a loop that waits for a user action
    /// at every round never reaches the limit, however many rounds it runs.
    /// </summary>
    [Fact]
    public async Task LoopGuard_AUserActionInTheLoopStartsANewChain()
    {
        var signal = UniqueSignal();
        var model = ConfirmLoopModel(signal);
        var instance = await StartAsync(await CreateAsync(model));

        await WithChainLimitAsync(2, async () =>
        {
            var api = await ThrowApiAsync(new ThrowSignalRequest(signal, InstanceId: instance.Id));
            Assert.Equal(0, (await BroadcastAsync(api.Id)).AutomaticActivationCount);
            await ProcessDeliveryJobsAsync(api.Id);
            for (var round = 0; round < 5; round++)
            {
                await TakeAsync(instance.Id, 7, 701);
                var thrown = (await BroadcastsThrownByAsync(instance.Id)).Last();
                Assert.Equal(1, thrown.AutomaticActivationCount);
                await ProcessDeliveryJobsAsync(thrown.Id);
            }
        });

        var all = await BroadcastsThrownByAsync(instance.Id);
        Assert.Equal(5, all.Count);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.SignalDeliveries.AnyAsync(item =>
            all.Select(broadcast => broadcast.Id).Contains(item.BroadcastId) && item.Status == SignalDeliveryStatuses.Failed));
    }

    /// <summary>
    /// A monitor answers every signal another instance throws after a user action. Each answer
    /// continues the chain of the signal it answers, never the monitor's earlier answers.
    /// </summary>
    [Fact]
    public async Task LoopGuard_AMonitorAnsweringManySignalsIsNeverRefused()
    {
        var ask = UniqueSignal();
        var answer = UniqueSignal();
        var monitorKey = $"signal-monitor-{Guid.NewGuid():N}";
        var monitor = await StartAsync(await CreateAsync(EchoModel(monitorKey, ask, answer,
            new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows })));
        var sender = SenderModel(ask, monitorKey, payloadVariable: null);
        var senderId = await CreateAsync(sender);

        await WithChainLimitAsync(3, async () =>
        {
            for (var round = 0; round < 10; round++)
            {
                var asking = await StartAsync(senderId, new Dictionary<string, JsonElement>
                {
                    ["v"] = JsonSerializer.SerializeToElement(round)
                });
                await TakeAsync(asking.Id, 2, 201);
                await ProcessDeliveryJobsAsync(Assert.Single(await BroadcastsThrownByAsync(asking.Id)).Id);
            }
        });

        var answers = await BroadcastsThrownByAsync(monitor.Id);
        Assert.Equal(10, answers.Count);
        Assert.All(answers, item => Assert.Equal(2, item.AutomaticActivationCount));
        foreach (var item in answers)
        {
            await ProcessDeliveryJobsAsync(item.Id);
        }
    }

    /// <summary>A delivered message between two throws starts a new chain.</summary>
    [Fact]
    public async Task LoopGuard_AMessageInTheLoopStartsANewChain()
    {
        var signal = UniqueSignal();
        var model = ConfirmLoopModel(signal);
        var confirm = model.FlowNodes.Single(node => node.Id == 7);
        confirm.Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent;
        confirm.Message = new MessageCatchModel
        {
            ClientId = "loop-client",
            ClientSecret = "loop-secret",
            HeaderName = "X-Loop",
            HeaderValue = "go"
        };
        model.SequenceFlows.Single(flow => flow.Id == 701).Name = string.Empty;
        var instance = await StartAsync(await CreateAsync(model));

        await WithChainLimitAsync(2, async () =>
        {
            await ProcessDeliveryJobsAsync((await ThrowApiAsync(new ThrowSignalRequest(signal, InstanceId: instance.Id))).Id);
            for (var round = 0; round < 3; round++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{instance.Id}/message")
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Client-Id", "loop-client");
                request.Headers.Add("X-Client-Secret", "loop-secret");
                request.Headers.Add("X-Loop", "go");
                using var response = await fixture.Client.SendAsync(request);
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                var thrown = (await BroadcastsThrownByAsync(instance.Id)).Last();
                Assert.Equal(1, thrown.AutomaticActivationCount);
                await ProcessDeliveryJobsAsync(thrown.Id);
            }
        });

        Assert.Equal(3, (await BroadcastsThrownByAsync(instance.Id)).Count);
    }

    /// <summary>A timer between two throws starts a new chain.</summary>
    [Fact]
    public async Task LoopGuard_ATimerInTheLoopStartsANewChain()
    {
        var signal = UniqueSignal();
        var model = ConfirmLoopModel(signal);
        var confirm = model.FlowNodes.Single(node => node.Id == 7);
        confirm.Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent;
        confirm.Timer = new TimerDefinitionModel { TimeDuration = "PT1H" };
        model.SequenceFlows.Single(flow => flow.Id == 701).Name = string.Empty;
        var instance = await StartAsync(await CreateAsync(model));

        await WithChainLimitAsync(2, async () =>
        {
            await ProcessDeliveryJobsAsync((await ThrowApiAsync(new ThrowSignalRequest(signal, InstanceId: instance.Id))).Id);
            for (var round = 0; round < 3; round++)
            {
                await ProcessInstanceJobsAsync(instance.Id, WorkflowJobKinds.Timer);
                var thrown = (await BroadcastsThrownByAsync(instance.Id)).Last();
                Assert.Equal(1, thrown.AutomaticActivationCount);
                await ProcessDeliveryJobsAsync(thrown.Id);
            }
        });

        Assert.Equal(3, (await BroadcastsThrownByAsync(instance.Id)).Count);
    }

    /// <summary>
    /// Values a signal delivers raise a conditional boundary on another task of the receiver,
    /// interrupting or not; the boundary's branch continues the signal's chain.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoopGuard_AConditionalBoundaryRaisedByDeliveredValuesContinuesTheChain(bool interrupting)
    {
        var ping = UniqueSignal();
        var pong = UniqueSignal();
        var receiver = ConditionalReceiverModel(ping, pong);
        receiver.FlowNodes.Single(node => node.Id == 6).CancelActivity = interrupting;
        var target = await StartAsync(await CreateAsync(receiver));
        var sender = SenderModel(ping, receiver.Id, payloadVariable: "v");
        var go = await StartAsync(await CreateAsync(sender), new Dictionary<string, JsonElement>
        {
            ["v"] = JsonSerializer.SerializeToElement(5)
        });

        await TakeAsync(go.Id, 2, 201);
        await ProcessDeliveryJobsAsync(Assert.Single(await BroadcastsThrownByAsync(go.Id)).Id);

        var answered = Assert.Single(await BroadcastsThrownByAsync(target.Id));
        Assert.Equal(2, answered.AutomaticActivationCount);
        Assert.Equal(interrupting ? [] : new[] { 5 }, await ActiveTaskNodesAsync(target.Id));
        await ProcessDeliveryJobsAsync(answered.Id);
    }

    /// <summary>
    /// Values a signal delivers release a conditional catch on another token, in both delivery
    /// modes; the released token continues the greater of its own chain and the signal's.
    /// </summary>
    [Theory]
    [InlineData(ConditionalEventDeliveryModes.Atomic)]
    [InlineData(ConditionalEventDeliveryModes.DurableAsync)]
    public async Task LoopGuard_AConditionalCatchReleasedByDeliveredValuesContinuesTheChain(string deliveryMode)
    {
        var ping = UniqueSignal();
        var pong = UniqueSignal();
        var receiver = ConditionalReceiverModel(ping, pong);
        // The task #5 and its boundary #6 become a conditional catch #5 that leads to the throw.
        receiver.FlowNodes.RemoveAll(node => node.Id is 5 or 6 or 9);
        receiver.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5, Name = "Positive", Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
            Conditional = new ConditionalDefinitionModel { Condition = "v > 0", DeliveryMode = deliveryMode }
        });
        receiver.SequenceFlows.RemoveAll(flow => flow.Id is 501 or 601);
        receiver.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 7 });
        var target = await StartAsync(await CreateAsync(receiver));
        var sender = SenderModel(ping, receiver.Id, payloadVariable: "v");
        var go = await StartAsync(await CreateAsync(sender), new Dictionary<string, JsonElement>
        {
            ["v"] = JsonSerializer.SerializeToElement(5)
        });

        await TakeAsync(go.Id, 2, 201);
        await ProcessDeliveryJobsAsync(Assert.Single(await BroadcastsThrownByAsync(go.Id)).Id);
        if (deliveryMode == ConditionalEventDeliveryModes.DurableAsync)
        {
            Assert.Empty(await BroadcastsThrownByAsync(target.Id));
            await ProcessInstanceJobsAsync(target.Id, WorkflowJobKinds.ConditionalWake);
        }

        var answered = Assert.Single(await BroadcastsThrownByAsync(target.Id));
        Assert.True(answered.AutomaticActivationCount >= 2,
            $"The released catch threw with count {answered.AutomaticActivationCount}.");
        await ProcessDeliveryJobsAsync(answered.Id);
    }

    /// <summary>
    /// A script after the delivery writes the value that wakes a conditional boundary; the
    /// boundary's branch continues the chain of the token that ran the script.
    /// </summary>
    [Fact]
    public async Task LoopGuard_AScriptAfterTheDeliveryContinuesTheChainIntoTheEventItWakes()
    {
        var ping = UniqueSignal();
        var pong = UniqueSignal();
        var receiver = ConditionalReceiverModel(ping, pong);
        var wait = receiver.FlowNodes.Single(node => node.Id == 3);
        wait.Signal!.OutputMappings = [];
        receiver.FlowNodes.Add(new FlowNodeModel
        {
            Id = 10, Name = "Set v", Type = BpmnFlowNodeTypes.ScriptTask,
            Assignments = [new AssignmentModel { Variable = "v", Expression = "7" }]
        });
        receiver.SequenceFlows.Single(flow => flow.Id == 301).TargetRef = 10;
        receiver.SequenceFlows.Add(new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 4 });
        var target = await StartAsync(await CreateAsync(receiver));
        var sender = SenderModel(ping, receiver.Id, payloadVariable: null);
        var go = await StartAsync(await CreateAsync(sender), new Dictionary<string, JsonElement>
        {
            ["v"] = JsonSerializer.SerializeToElement(1)
        });

        await TakeAsync(go.Id, 2, 201);
        await ProcessDeliveryJobsAsync(Assert.Single(await BroadcastsThrownByAsync(go.Id)).Id);
        // A delivery's routing runs automatic activities as Worker jobs, so the script waits for one.
        Assert.Empty(await BroadcastsThrownByAsync(target.Id));
        await ProcessInstanceJobsAsync(target.Id, WorkflowJobKinds.AsyncBefore);

        int scriptCount;
        await using (var db = fixture.CreateDbContext())
        {
            scriptCount = await db.ExecutionTokens.AsNoTracking()
                .Where(item => item.InstanceId == target.Id && item.NodeId == 4)
                .Select(item => item.AutomaticActivationCount).SingleAsync();
        }

        // Delivered with 1, the script's job counts 2, and the branch it wakes throws with 3.
        var answered = Assert.Single(await BroadcastsThrownByAsync(target.Id));
        Assert.Equal(2, scriptCount);
        Assert.Equal(scriptCount + 1, answered.AutomaticActivationCount);
        await ProcessDeliveryJobsAsync(answered.Id);
    }

    /// <summary>
    /// The token a signal released calls a child, and the child throws: the child starts at
    /// the calling token's count, after the call's own activation.
    /// </summary>
    [Fact]
    public async Task LoopGuard_AChildCalledAfterADeliveryContinuesTheChain()
    {
        var calls = new CallActivityHarness(fixture);
        var ping = UniqueSignal();
        var pong = UniqueSignal();
        var childKey = CallActivityHarness.Key("signal-child");
        var child = CallActivityHarness.ImmediateChild(childKey);
        child.FlowNodes.Add(Throw(4, "Answer", pong, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows }));
        child.SequenceFlows.Single().TargetRef = 4;
        child.SequenceFlows.Add(new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = CallActivityHarness.End });
        await calls.PublishAsync(child);
        var parent = CallActivityHarness.Parent(CallActivityHarness.Key("signal-parent"), childKey);
        parent.FlowNodes.Add(new FlowNodeModel
        {
            Id = 6, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
            Signal = new SignalEventModel { Name = ping }
        });
        parent.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 6;
        parent.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = CallActivityHarness.Call });
        var parentId = await calls.StartAsync((await calls.PublishAsync(parent)).Id);
        try
        {
            var sender = SenderModel(ping, parent.Id, payloadVariable: null);
            var go = await StartAsync(await CreateAsync(sender), new Dictionary<string, JsonElement>
            {
                ["v"] = JsonSerializer.SerializeToElement(1)
            });
            await TakeAsync(go.Id, 2, 201);
            await ProcessDeliveryJobsAsync(Assert.Single(await BroadcastsThrownByAsync(go.Id)).Id);
            await calls.DrainAsync(parentId);

            long childId;
            await using (var db = fixture.CreateDbContext())
            {
                childId = await db.WorkflowInstances.AsNoTracking()
                    .Where(item => item.CallRootInstanceId == parentId).Select(item => item.Id).SingleAsync();
            }

            // Delivered with 1, the call's entry counts 2, and the child's throw stores 3.
            var answered = Assert.Single(await BroadcastsThrownByAsync(childId));
            Assert.Equal(3, answered.AutomaticActivationCount);
            await ProcessDeliveryJobsAsync(answered.Id);
        }
        finally
        {
            await calls.RetireLeftoverJobsAsync();
        }
    }

    /// <summary>
    /// A child whose own automatic steps raised its chain ends, and the parent throws after
    /// the call: the parent continues the greater of its own count and the child's.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoopGuard_AParentThrowingAfterItsChildEndsContinuesTheChildsChain(bool multiInstance)
    {
        var calls = new CallActivityHarness(fixture);
        var signal = UniqueSignal();
        var childKey = CallActivityHarness.Key("signal-busy-child");
        var child = CallActivityHarness.ImmediateChild(childKey);
        // Three automatic tasks that each wait for the Worker raise the child's count by three.
        child.SequenceFlows.Clear();
        var previous = CallActivityHarness.Start;
        for (var id = 10; id < 13; id++)
        {
            child.FlowNodes.Add(new FlowNodeModel { Id = id, Name = $"Step {id}", Type = BpmnFlowNodeTypes.Task, AsyncBefore = true });
            child.SequenceFlows.Add(new SequenceFlowModel { Id = previous * 100 + 1, SourceRef = previous, TargetRef = id });
            previous = id;
        }

        child.SequenceFlows.Add(new SequenceFlowModel { Id = previous * 100 + 1, SourceRef = previous, TargetRef = CallActivityHarness.End });
        await calls.PublishAsync(child);
        var parent = CallActivityHarness.Parent(CallActivityHarness.Key("signal-caller"), childKey);
        parent.FlowNodes.Add(Throw(6, "After the call", signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows }));
        parent.SequenceFlows.Single(flow => flow.Id == 201).TargetRef = 6;
        parent.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = CallActivityHarness.End });
        if (multiInstance)
        {
            parent.Variables.Add(new VariableModel
            {
                Id = 30, Name = "items", DataType = WorkflowVariableTypes.String, IsArray = true,
                DefaultValue = JsonSerializer.SerializeToElement(new[] { "one" })
            });
            parent.Variables.Add(new VariableModel
            {
                Id = 31, Name = "results", DataType = WorkflowVariableTypes.Json, DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
            });
            parent.FlowNodes.Single(node => node.Id == CallActivityHarness.Call).MultiInstance = new MultiInstanceModel
            {
                Mode = MultiInstanceModes.Parallel,
                Source = MultiInstanceSources.Collection,
                CollectionVariable = "items",
                CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
                OutputVariables = [],
                ResultVariable = "results"
            };
        }

        var parentId = await calls.StartAsync((await calls.PublishAsync(parent)).Id);
        try
        {
            await calls.DrainAsync(parentId);

            int childFinal;
            await using (var db = fixture.CreateDbContext())
            {
                var childId = await db.WorkflowInstances.AsNoTracking()
                    .Where(item => item.CallRootInstanceId == parentId).Select(item => item.Id).SingleAsync();
                childFinal = await db.ExecutionTokens.AsNoTracking()
                    .Where(item => item.InstanceId == childId).MaxAsync(item => item.AutomaticActivationCount);
                Assert.Equal(WorkflowInstanceStatuses.Completed, await db.WorkflowInstances.AsNoTracking()
                    .Where(item => item.Id == parentId).Select(item => item.Status).SingleAsync());
            }

            Assert.True(childFinal >= 4, $"The child ended at count {childFinal}.");
            var thrown = Assert.Single(await BroadcastsThrownByAsync(parentId));
            Assert.True(thrown.AutomaticActivationCount > childFinal,
                $"The parent threw with count {thrown.AutomaticActivationCount} after a child that ended at {childFinal}.");
        }
        finally
        {
            await calls.RetireLeftoverJobsAsync();
        }
    }

    /// <summary>
    /// The aggregate of an automatic multi-instance body wakes a conditional boundary on
    /// another branch; the boundary's branch continues the body token's chain.
    /// </summary>
    [Fact]
    public async Task LoopGuard_AMultiInstanceBodysWritesContinueTheBodysChain()
    {
        var calls = new CallActivityHarness(fixture);
        var ping = UniqueSignal();
        var pong = UniqueSignal();
        var receiver = ConditionalReceiverModel(ping, pong);
        receiver.Variables.Add(new VariableModel
        {
            Id = 2, Name = "amounts", DataType = WorkflowVariableTypes.Number, IsArray = true,
            DefaultValue = JsonSerializer.SerializeToElement(new[] { 1 })
        });
        receiver.Variables.Add(new VariableModel { Id = 3, Name = "results", DataType = WorkflowVariableTypes.Json, DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) });
        receiver.FlowNodes.Single(node => node.Id == 3).Signal!.OutputMappings = [];
        // The body writes an empty aggregate when it starts and the full one when it completes.
        receiver.FlowNodes.Single(node => node.Id == 6).Conditional!.Condition = "Len(results) > 0";
        receiver.FlowNodes.Add(new FlowNodeModel
        {
            Id = 10, Name = "Scale", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
            MultiInstance = new MultiInstanceModel
            {
                Mode = MultiInstanceModes.Parallel,
                Source = MultiInstanceSources.Collection,
                CollectionVariable = "amounts",
                CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
                OutputVariables = [new MultiInstanceOutputVariableModel { Name = "scaledAmount", DataType = WorkflowVariableTypes.Number }],
                ResultVariable = "results"
            },
            Assignments = [new AssignmentModel { Variable = "scaledAmount", Expression = "[mi.item] * 2" }]
        });
        receiver.SequenceFlows.Single(flow => flow.Id == 301).TargetRef = 10;
        receiver.SequenceFlows.Add(new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 4 });
        var target = await StartAsync(await CreateAsync(receiver));
        try
        {
            var sender = SenderModel(ping, receiver.Id, payloadVariable: null);
            var go = await StartAsync(await CreateAsync(sender), new Dictionary<string, JsonElement>
            {
                ["v"] = JsonSerializer.SerializeToElement(1)
            });
            await TakeAsync(go.Id, 2, 201);
            await ProcessDeliveryJobsAsync(Assert.Single(await BroadcastsThrownByAsync(go.Id)).Id);
            await calls.DrainAsync(target.Id);

            int bodyCount;
            await using (var db = fixture.CreateDbContext())
            {
                bodyCount = await db.ExecutionTokens.AsNoTracking()
                    .Where(item => item.InstanceId == target.Id && item.NodeId == 4)
                    .Select(item => item.AutomaticActivationCount).SingleAsync();
            }

            // Delivered with 1, the body's asyncBefore entry counts 2, and the branch that its
            // aggregate wakes throws with the body's count plus one.
            var answered = Assert.Single(await BroadcastsThrownByAsync(target.Id));
            Assert.True(bodyCount >= 2, $"The body token ended at count {bodyCount}.");
            Assert.Equal(bodyCount + 1, answered.AutomaticActivationCount);
            await ProcessDeliveryJobsAsync(answered.Id);
        }
        finally
        {
            await calls.RetireLeftoverJobsAsync();
        }
    }

    // Models and helpers of the loop tests

    /// <summary>
    /// Task #2 with a non-interrupting boundary #3 for <paramref name="caught"/>, whose branch
    /// throws <paramref name="thrown"/> through #4 with <paramref name="scope"/> and ends at #5.
    /// </summary>
    private static WorkflowModel EchoModel(string key, string caught, string thrown, SignalThrowScopeModel scope) => new()
    {
        Id = key,
        Name = "Signal echo",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Hold", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel
            {
                Id = 3, Name = "Heard", Type = BpmnFlowNodeTypes.SignalBoundaryEvent, AttachedToRef = 2,
                CancelActivity = false, Signal = new SignalEventModel { Name = caught }
            },
            Throw(4, "Echo", thrown, scope),
            new FlowNodeModel { Id = 5, Name = "Echoed", Type = BpmnFlowNodeTypes.EndEvent },
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

    /// <summary>
    /// Task #2 with a non-interrupting boundary #3; its branch waits at #7 (a user task whose
    /// action 701 leads on), then throws the same signal to this instance through #4.
    /// </summary>
    private static WorkflowModel ConfirmLoopModel(string signal)
    {
        var model = EchoModel($"signal-confirm-loop-{Guid.NewGuid():N}", signal, signal,
            new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.ThisInstance });
        model.FlowNodes.Add(new FlowNodeModel { Id = 7, Name = "Confirm", Type = BpmnFlowNodeTypes.UserTask });
        model.SequenceFlows.Single(flow => flow.Id == 301).TargetRef = 7;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 4, Name = "Confirm" });
        return model;
    }

    /// <summary>
    /// A parallel split into signal catch #3 for <paramref name="ping"/>, which maps v, and task
    /// #5 with a non-interrupting conditional boundary #6 (v &gt; 0) that throws
    /// <paramref name="pong"/> to all workflows through #7.
    /// </summary>
    private static WorkflowModel ConditionalReceiverModel(string ping, string pong) => new()
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

    private async Task<long> CreateDraftAsync(WorkflowModel model)
    {
        using var draft = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, false));
        Assert.True(draft.StatusCode == HttpStatusCode.Created, await draft.Content.ReadAsStringAsync());
        return (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<WorkflowDetailDto>(draft.Content, JsonOptions))!.Id;
    }

    private async Task WithChainLimitAsync(int limit, Func<Task> body)
    {
        var previous = await SetEngineSettingAsync(GuardNamespace, GuardKey, limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            await body();
        }
        finally
        {
            await RestoreEngineSettingAsync(GuardNamespace, GuardKey, previous);
        }
    }

    /// <summary>Delivers every open broadcast the instances threw until none is left.</summary>
    private async Task RunThrownBroadcastsAsync(params long[] instanceIds)
    {
        for (var round = 0; round < 20; round++)
        {
            var open = new List<SignalBroadcastEntity>();
            foreach (var instanceId in instanceIds)
            {
                open.AddRange((await BroadcastsThrownByAsync(instanceId))
                    .Where(item => item.Status == SignalBroadcastStatuses.Delivering));
            }

            if (open.Count == 0)
            {
                return;
            }

            foreach (var broadcast in open.OrderBy(item => item.Id))
            {
                await ProcessDeliveryJobsAsync(broadcast.Id);
            }
        }

        Assert.Fail("The signals kept circulating after 20 rounds.");
    }

    private async Task AssertChainLimitRefusalAsync(long broadcastId, int limit)
    {
        await using var db = fixture.CreateDbContext();
        var refused = await db.SignalDeliveries.AsNoTracking().SingleAsync(item => item.BroadcastId == broadcastId);
        Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.DeliveryRejected),
            (refused.Status, refused.ResultCode));
        Assert.Contains($"Workflow.Async.MaxConsecutiveAutomaticActivations ({limit})", refused.ResultDescription);
        // The wait that refused keeps listening.
        Assert.True(await db.SignalSubscriptions.AnyAsync(item =>
            item.Id == refused.SubscriptionId && item.Status == SignalSubscriptionStatuses.Active));
    }

    private async Task<SignalBroadcastEntity> BroadcastAsync(long id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SignalBroadcasts.AsNoTracking().SingleAsync(item => item.Id == id);
    }
}
