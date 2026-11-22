using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The throw's transaction (section 4 of the EVENT-04B plan): what one routing pass freezes,
/// what a failed step leaves, the five places where a token enters a signal end event, the
/// races of the release gate, and the records a throw writes.
/// </summary>
public sealed partial class SignalThrowRuntimeTests
{
    // Same transaction

    /// <summary>
    /// An automatic task with asyncBefore in front of the throw or end event moves the throw
    /// into a later transaction, so a wait on a parallel branch exists in every flow order.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task AnAsyncBeforeTaskInFrontOfTheThrowReachesAParallelCatchInEveryOrder(bool end, bool catchFirst)
    {
        var signal = UniqueSignal();
        var model = ForkModel(signal, end, catchFirst);
        model.FlowNodes.Add(new FlowNodeModel { Id = 7, Name = "Defer", Type = BpmnFlowNodeTypes.Task, AsyncBefore = true });
        model.SequenceFlows.Single(flow => flow.TargetRef == 5).TargetRef = 7;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 5 });
        var instance = await StartAsync(await CreateAsync(model));
        Assert.Empty(await BroadcastsThrownByAsync(instance.Id));

        await ProcessInstanceJobsAsync(instance.Id, WorkflowJobKinds.AsyncBefore);

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(instance.Id));
        Assert.Equal(1, broadcast.TotalDeliveryCount);
        await ProcessDeliveryJobsAsync(broadcast.Id);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, await db.WorkflowInstances.AsNoTracking()
            .Where(item => item.Id == instance.Id).Select(item => item.Status).SingleAsync());
    }

    /// <summary>A signal end event throws in the step that moves its token there, before a later branch exists.</summary>
    [Fact]
    public async Task SignalEndEventBehindAFork_ReachesNoSiblingBranchInTheOtherOrderEither()
    {
        var signal = UniqueSignal();
        var instance = await StartAsync(await CreateAsync(ForkModel(signal, end: true, catchFirst: false)));

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(instance.Id));
        Assert.Equal((0, SignalBroadcastStatuses.Completed), (broadcast.TotalDeliveryCount, broadcast.Status));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Running, await db.WorkflowInstances.AsNoTracking()
            .Where(item => item.Id == instance.Id).Select(item => item.Status).SingleAsync());
    }

    /// <summary>The token that leaves a host cancels its boundaries before it throws, so they are not frozen.</summary>
    [Fact]
    public async Task TheHostThatTheTokenLeftIsNotFrozen()
    {
        var signal = UniqueSignal();
        var model = new WorkflowModel
        {
            Id = $"signal-left-host-{Guid.NewGuid():N}",
            Name = "Signal from a host's own action",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Hold", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 3, Name = "Heard", Type = BpmnFlowNodeTypes.SignalBoundaryEvent, AttachedToRef = 2,
                    CancelActivity = true, Signal = new SignalEventModel { Name = signal }
                },
                Throw(4, "Announce", signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.ThisInstance }),
                new FlowNodeModel { Id = 5, Name = "End", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 6, Name = "Heard end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 4, Name = "Go" },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 6 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 }
            ]
        };
        var instance = await StartAsync(await CreateAsync(model));

        await TakeAsync(instance.Id, 2, 201);

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(instance.Id));
        Assert.Equal((0, SignalBroadcastStatuses.Completed), (broadcast.TotalDeliveryCount, broadcast.Status));
    }

    /// <summary>A wait frozen by a throw and then cancelled by a terminate end event is skipped by its delivery.</summary>
    [Fact]
    public async Task AWaitATerminateEndEventCancelsAfterTheThrowIsSkipped()
    {
        var signal = UniqueSignal();
        var model = ForkModel(signal, end: false, catchFirst: true);
        model.FlowNodes.Single(node => node.Id == 6).Type = BpmnFlowNodeTypes.TerminateEndEvent;
        var instance = await StartAsync(await CreateAsync(model));

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(instance.Id));
        Assert.Equal(1, broadcast.TotalDeliveryCount);
        await ProcessDeliveryJobsAsync(broadcast.Id);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, await db.WorkflowInstances.AsNoTracking()
            .Where(item => item.Id == instance.Id).Select(item => item.Status).SingleAsync());
        var delivery = await db.SignalDeliveries.AsNoTracking().SingleAsync(item => item.BroadcastId == broadcast.Id);
        Assert.Equal(SignalDeliveryStatuses.Skipped, delivery.Status);
    }

    // Atomicity

    /// <summary>
    /// A step that fails after a throw rolls the throw back: no broadcast, delivery, job,
    /// metric, or log line remains. The failure here is a second throw in the same step that
    /// cannot read its business key. Retrying the step after the cause is fixed throws once.
    /// </summary>
    [Fact]
    public async Task AStepThatFailsAfterTheThrowLeavesNothingAndItsRetryThrowsOnce()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        await StartAsync(await CreateAsync(receiver), OrderNumber("ORD-ATOMIC"));
        var model = new WorkflowModel
        {
            Id = $"signal-atomic-{Guid.NewGuid():N}",
            Name = "Signal then failure",
            InitialEventId = 1,
            Variables =
            [
                new VariableModel { Id = 1, Name = "target", DataType = WorkflowVariableTypes.String, Nullable = true }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Decide", Type = BpmnFlowNodeTypes.UserTask },
                Throw(3, "Announce", signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = receiver.Id }),
                Throw(4, "Announce by key", UniqueSignal(), new SignalThrowScopeModel
                {
                    Kind = SignalThrowScopeKinds.BusinessKey, WorkflowKey = receiver.Id, BusinessKeyVariable = "target"
                }),
                new FlowNodeModel { Id = 5, Name = "End", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 6, Name = "Again", Type = BpmnFlowNodeTypes.UserTask, AsyncAfter = true }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Go" },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 6, Name = "Later" },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 },
                new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 3, Name = "Go" }
            ]
        };
        var workflowId = await CreateAsync(model);
        var direct = await StartAsync(workflowId);

        using (var response = await TakeResponseAsync(direct.Id, 2, 201))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Signal throw event #4 cannot read a business key from variable 'target'",
                await response.Content.ReadAsStringAsync());
        }

        Assert.Empty(await BroadcastsThrownByAsync(direct.Id));
        Assert.DoesNotContain(fixture.ErrorBoundaryLogs.SignalLinesForInstance(direct.Id),
            line => line.Message.Contains("threw signal broadcast", StringComparison.Ordinal));
        Assert.Equal(new[] { 2 }, await ActiveTaskNodesAsync(direct.Id));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.SignalName == signal));
        }

        // The same failure in a Worker job: its attempts roll back, and once the cause is fixed
        // the retried step throws exactly once.
        var queued = await StartAsync(workflowId);
        await TakeAsync(queued.Id, 2, 202);
        await TakeAsync(queued.Id, 6, 601);
        await ProcessInstanceJobsAsync(queued.Id, WorkflowJobKinds.AsyncAfter);
        long incidentId;
        await using (var db = fixture.CreateDbContext())
        {
            var incident = await db.WorkflowIncidents.AsNoTracking()
                .SingleAsync(item => item.InstanceId == queued.Id && item.Status == WorkflowIncidentStatuses.Open);
            incidentId = incident.Id;
            var job = await db.WorkflowJobs.AsNoTracking().SingleAsync(item => item.Id == incident.JobId);
            Assert.True(job.AttemptCount > 1, $"The job ran {job.AttemptCount} time(s).");
            Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.SignalName == signal));
        }

        using (var patch = await SendAsync(HttpMethod.Patch, $"/api/instances/{queued.Id}/variables",
                   new UpdateInstanceVariablesRequest([new InstanceVariableWriteDto("target", JsonSerializer.SerializeToElement("ORD-ATOMIC"))], "Fix", null)))
        {
            Assert.True(patch.IsSuccessStatusCode, await patch.Content.ReadAsStringAsync());
        }

        using (var retry = await SendAsync(HttpMethod.Post, $"/api/incidents/{incidentId}/retry"))
        {
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
        }

        await ProcessInstanceJobsAsync(queued.Id, WorkflowJobKinds.AsyncAfter);
        var thrown = await BroadcastsThrownByAsync(queued.Id);
        Assert.Equal(new int?[] { 3, 4 }, thrown.Select(item => item.ThrowerNodeId).ToArray());
        Assert.Equal(2, fixture.ErrorBoundaryLogs.SignalLinesForInstance(queued.Id)
            .Count(line => line.Message.Contains("threw signal broadcast", StringComparison.Ordinal)));
        foreach (var broadcast in thrown)
        {
            await ProcessDeliveryJobsAsync(broadcast.Id);
        }
    }

    // End event

    /// <summary>
    /// Each of the five places where a token enters an end event throws a signal end event's
    /// signal once, with the count the token's update left plus one, and the instance completes.
    /// </summary>
    [Theory]
    [InlineData("userAction", 1)]
    [InlineData("multiInstance", 1)]
    [InlineData("message", 1)]
    [InlineData("automatic", 1)]
    [InlineData("job", 2)]
    [InlineData("apiSignal", 1)]
    [InlineData("workflowSignal", 2)]
    public async Task SignalEndEvent_ThrowsOnceFromEachPlaceWithTheTokensCountPlusOne(string place, int expected)
    {
        var caught = UniqueSignal();
        var model = new WorkflowModel
        {
            Id = $"signal-end-{place}-{Guid.NewGuid():N}",
            Name = "Signal end from " + place,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                Throw(3, "Announce", UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows },
                    type: BpmnFlowNodeTypes.SignalEndEvent)
            ],
            SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
        };
        var into = new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 };
        switch (place)
        {
            case "userAction":
                model.FlowNodes.Add(new FlowNodeModel { Id = 2, Name = "Decide", Type = BpmnFlowNodeTypes.UserTask });
                into.Name = "Go";
                model.SequenceFlows.Add(into);
                break;
            case "multiInstance":
                model.FlowNodes.Add(new FlowNodeModel
                {
                    Id = 2, Name = "Vote", Type = BpmnFlowNodeTypes.UserTask,
                    MultiInstance = new MultiInstanceModel
                    {
                        Mode = MultiInstanceModes.Parallel,
                        Source = MultiInstanceSources.Cardinality,
                        CardinalityExpression = "1",
                        ResultVariable = "votes"
                    }
                });
                model.Variables =
                [
                    new VariableModel
                    {
                        Id = 1, Name = "votes", DataType = WorkflowVariableTypes.Json,
                        DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
                    }
                ];
                into.Name = "Agree";
                into.CompletionCondition = "CountFlow(201) >= 1";
                into.CompletionPriority = 1;
                model.SequenceFlows.Add(into);
                model.SequenceFlows.Add(new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 3, IsDefault = true, IsSelectable = false });
                break;
            case "message":
                model.FlowNodes.Add(new FlowNodeModel
                {
                    Id = 2, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
                    Message = new MessageCatchModel { ClientId = "end-client", ClientSecret = "end-secret", HeaderName = "X-End", HeaderValue = "go" }
                });
                model.SequenceFlows.Add(into);
                break;
            case "automatic":
                model.FlowNodes.Add(new FlowNodeModel { Id = 2, Name = "Pass", Type = BpmnFlowNodeTypes.Task });
                model.SequenceFlows.Add(into);
                break;
            case "job":
                model.FlowNodes.Add(new FlowNodeModel { Id = 2, Name = "Defer", Type = BpmnFlowNodeTypes.Task, AsyncBefore = true });
                model.SequenceFlows.Add(into);
                break;
            default:
                model.FlowNodes.Add(new FlowNodeModel
                {
                    Id = 2, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
                    Signal = new SignalEventModel { Name = caught }
                });
                model.SequenceFlows.Add(into);
                break;
        }

        var instance = await StartAsync(await CreateAsync(model));
        switch (place)
        {
            case "userAction":
            case "multiInstance":
                await TakeAsync(instance.Id, 2, 201);
                break;
            case "message":
                using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{instance.Id}/message")
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                })
                {
                    request.Headers.Add("X-Client-Id", "end-client");
                    request.Headers.Add("X-Client-Secret", "end-secret");
                    request.Headers.Add("X-End", "go");
                    using var response = await fixture.Client.SendAsync(request);
                    Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                }

                break;
            case "job":
                await ProcessInstanceJobsAsync(instance.Id, WorkflowJobKinds.AsyncBefore);
                break;
            case "apiSignal":
                await ProcessDeliveryJobsAsync((await ThrowApiAsync(new ThrowSignalRequest(caught, InstanceId: instance.Id))).Id);
                break;
            case "workflowSignal":
                var sender = SenderModel(caught, model.Id, payloadVariable: null);
                var go = await StartAsync(await CreateAsync(sender), new Dictionary<string, JsonElement>
                {
                    ["v"] = JsonSerializer.SerializeToElement(1)
                });
                await TakeAsync(go.Id, 2, 201);
                await ProcessDeliveryJobsAsync(Assert.Single(await BroadcastsThrownByAsync(go.Id)).Id);
                break;
        }

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(instance.Id));
        Assert.Equal((expected, 3), (broadcast.AutomaticActivationCount, broadcast.ThrowerNodeId));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, await db.WorkflowInstances.AsNoTracking()
            .Where(item => item.Id == instance.Id).Select(item => item.Status).SingleAsync());
        var row = await db.InstanceHistory.AsNoTracking()
            .SingleAsync(item => item.InstanceId == instance.Id && item.Note == InstanceHistoryNotes.SignalThrown);
        Assert.Equal((3, 3, (long?)null), (row.FromStepId, row.ToStepId, row.ActionId));
        Assert.Equal("end", row.Payload!.RootElement.GetProperty("eventKind").GetString());
    }

    /// <summary>A child that ends at a signal end event completes normally and returns to its parent.</summary>
    [Fact]
    public async Task SignalEndEvent_InAChildCompletesItAndReturnsToTheParent()
    {
        var calls = new CallActivityHarness(fixture);
        var childKey = CallActivityHarness.Key("signal-end-child");
        var child = CallActivityHarness.ImmediateChild(childKey);
        child.FlowNodes.RemoveAll(node => node.Id == CallActivityHarness.End);
        child.FlowNodes.Add(Throw(CallActivityHarness.End, "Announce", UniqueSignal(),
            new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows }, type: BpmnFlowNodeTypes.SignalEndEvent));
        await calls.PublishAsync(child);
        var parentId = await calls.StartAsync((await calls.PublishAsync(
            CallActivityHarness.Parent(CallActivityHarness.Key("signal-end-parent"), childKey))).Id);
        try
        {
            await calls.DrainAsync(parentId);
            await using var db = fixture.CreateDbContext();
            var childId = await db.WorkflowInstances.AsNoTracking()
                .Where(item => item.CallRootInstanceId == parentId).Select(item => item.Id).SingleAsync();
            Assert.Single(await BroadcastsThrownByAsync(childId));
            Assert.Equal(WorkflowInstanceStatuses.Completed, await db.WorkflowInstances.AsNoTracking()
                .Where(item => item.Id == childId).Select(item => item.Status).SingleAsync());
            Assert.Equal(WorkflowInstanceStatuses.Completed, await db.WorkflowInstances.AsNoTracking()
                .Where(item => item.Id == parentId).Select(item => item.Status).SingleAsync());
        }
        finally
        {
            await calls.RetireLeftoverJobsAsync();
        }
    }

    // Races, the release gate

    /// <summary>
    /// The throw freezes its recipients without a lock, so it does not wait for a transaction
    /// that holds a recipient's instance row, whatever its scope.
    /// </summary>
    [Theory]
    [InlineData(SignalThrowScopeKinds.ThisWorkflow)]
    [InlineData(SignalThrowScopeKinds.Workflow)]
    [InlineData(SignalThrowScopeKinds.BusinessKey)]
    [InlineData(SignalThrowScopeKinds.OwnBusinessKey)]
    [InlineData(SignalThrowScopeKinds.AllWorkflows)]
    public async Task Race_AThrowDoesNotWaitForARecipientsLockedInstanceRow(string kind)
    {
        var signal = UniqueSignal();
        var family = ReceiverModel(signal);
        // The family both waits and, from its task #3, throws; the recipient is another of its instances.
        family.FlowNodes.Add(Throw(5, "Announce", signal, kind switch
        {
            SignalThrowScopeKinds.ThisWorkflow => new SignalThrowScopeModel { Kind = kind },
            SignalThrowScopeKinds.Workflow => new SignalThrowScopeModel { Kind = kind, WorkflowKey = family.Id },
            SignalThrowScopeKinds.BusinessKey => new SignalThrowScopeModel { Kind = kind, WorkflowKey = family.Id, BusinessKeyVariable = "target" },
            SignalThrowScopeKinds.OwnBusinessKey => new SignalThrowScopeModel { Kind = kind, WorkflowKey = family.Id },
            _ => new SignalThrowScopeModel { Kind = kind }
        }));
        family.Variables =
        [
            new VariableModel { Id = 1, Name = "target", DataType = WorkflowVariableTypes.String, DefaultValue = JsonSerializer.SerializeToElement("ORD-RACE-1") }
        ];
        family.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = "Announced", Type = BpmnFlowNodeTypes.EndEvent });
        family.FlowNodes.Add(new FlowNodeModel { Id = 7, Name = "Announcer", Type = BpmnFlowNodeTypes.UserTask });
        family.FlowNodes.Add(new FlowNodeModel { Id = 8, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway });
        family.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 8;
        family.SequenceFlows.Add(new SequenceFlowModel { Id = 801, SourceRef = 8, TargetRef = 2 });
        family.SequenceFlows.Add(new SequenceFlowModel { Id = 802, SourceRef = 8, TargetRef = 7 });
        family.SequenceFlows.Add(new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 5, Name = "Announce" });
        family.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 });
        var workflowId = await CreateAsync(family);
        var recipient = await StartAsync(workflowId, OrderNumber("ORD-RACE-1"));
        // The own business key scope reaches the instance with the thrower's own key, so that one throws.
        var thrower = kind == SignalThrowScopeKinds.OwnBusinessKey
            ? recipient
            : await StartAsync(workflowId, OrderNumber("ORD-RACE-2"));
        var held = kind == SignalThrowScopeKinds.OwnBusinessKey
            ? await StartAsync(workflowId, OrderNumber("ORD-RACE-3"))
            : recipient;

        await using (var hold = await HoldInstanceRowAsync(held.Id))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var task = TakeResponseAsync(thrower.Id, 7, 701);
            var finished = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, timeout.Token));
            Assert.True(finished == task, "The throw waited for a transaction that held a recipient's instance row.");
            using var response = await task;
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(thrower.Id));
        Assert.True(broadcast.TotalDeliveryCount >= 1);
        await ProcessDeliveryJobsAsync(broadcast.Id);
    }

    /// <summary>Two instances that throw to each other at the same moment both commit.</summary>
    [Fact]
    public async Task Race_TwoInstancesThrowingToEachOtherAtOnceBothCommit()
    {
        var signal = UniqueSignal();
        // Hold #2 listens, through its boundary #3, for the signal the family throws from Go #7.
        var model = new WorkflowModel
        {
            Id = $"signal-mutual-{Guid.NewGuid():N}",
            Name = "Signal mutual",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 10, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 2, Name = "Hold", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 3, Name = "Heard", Type = BpmnFlowNodeTypes.SignalBoundaryEvent, AttachedToRef = 2,
                    CancelActivity = false, Signal = new SignalEventModel { Name = signal }
                },
                new FlowNodeModel { Id = 5, Name = "Heard end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 6, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 7, Name = "Go", Type = BpmnFlowNodeTypes.UserTask },
                Throw(8, "Tell the others", signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.ThisWorkflow }),
                new FlowNodeModel { Id = 9, Name = "Told", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 2 },
                new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 7 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 6, Name = "Done" },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 8, Name = "Go" },
                new SequenceFlowModel { Id = 801, SourceRef = 8, TargetRef = 9 }
            ]
        };
        var workflowId = await CreateAsync(model);
        var first = await StartAsync(workflowId);
        var second = await StartAsync(workflowId);

        var responses = await Task.WhenAll(TakeResponseAsync(first.Id, 7, 701), TakeResponseAsync(second.Id, 7, 701));
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }
        }

        foreach (var instance in new[] { first, second })
        {
            var broadcast = Assert.Single(await BroadcastsThrownByAsync(instance.Id));
            Assert.Equal(2, broadcast.TotalDeliveryCount);
            await ProcessDeliveryJobsAsync(broadcast.Id);
        }
    }

    /// <summary>
    /// The delivery to the throwing instance runs after the throw commits, and it and a user
    /// completing the host it interrupts have exactly one winner.
    /// </summary>
    [Fact]
    public async Task Race_TheDeliveryToTheThrowerAndAUserCompletingTheHostHaveOneWinner()
    {
        var signal = UniqueSignal();
        var order = await StartAsync(await CreateAsync(OrderModel(signal)));
        await TakeAsync(order.Id, 7, 701);
        var broadcast = Assert.Single(await BroadcastsThrownByAsync(order.Id));
        Assert.Equal(new[] { 3 }, await ActiveTaskNodesAsync(order.Id));

        var delivery = ProcessDeliveryJobsAsync(broadcast.Id);
        var action = TakeResponseAsync(order.Id, 3, 301);
        await Task.WhenAll(delivery, action);
        using var response = await action;

        await using var db = fixture.CreateDbContext();
        var outcome = await db.SignalDeliveries.AsNoTracking().SingleAsync(item => item.BroadcastId == broadcast.Id);
        if (response.IsSuccessStatusCode)
        {
            Assert.Equal(SignalDeliveryStatuses.Skipped, outcome.Status);
            Assert.Empty(await ActiveTaskNodesAsync(order.Id));
        }
        else
        {
            Assert.Equal(SignalDeliveryStatuses.Delivered, outcome.Status);
            Assert.Equal(new[] { 5 }, await ActiveTaskNodesAsync(order.Id));
        }
    }

    // Records

    /// <summary>
    /// Neither the throw's row nor the receipt is a user action, so a task with claim
    /// inheritance after both is claimed by the last person who acted.
    /// </summary>
    [Fact]
    public async Task ClaimInheritance_IgnoresTheThrowAndReceiptRows()
    {
        var signal = UniqueSignal();
        var model = new WorkflowModel
        {
            Id = $"signal-claims-{Guid.NewGuid():N}",
            Name = "Signal claims",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Prepare", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 3, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
                    Signal = new SignalEventModel { Name = signal }
                },
                Throw(4, "Relay", UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows }),
                new FlowNodeModel
                {
                    Id = 5, Name = "Finish", Type = BpmnFlowNodeTypes.UserTask,
                    RequiresClaim = true, ClaimMode = ClaimModes.Previous
                },
                new FlowNodeModel { Id = 6, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Ready" },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6, Name = "Done" }
            ]
        };
        var instance = await StartAsync(await CreateAsync(model));
        long taskId;
        await using (var db = fixture.CreateDbContext())
        {
            taskId = await db.UserTasks.Where(item => item.InstanceId == instance.Id && item.NodeId == 2)
                .Select(item => item.Id).SingleAsync();
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/201"))
        {
            request.Content = JsonContent.Create(new TakeFlowRequest(null), options: JsonOptions);
            ApiTestAuth.Authorize(request, "alice-preparer", ["admin"]);
            using var response = await fixture.Client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        await ProcessDeliveryJobsAsync((await ThrowApiAsync(new ThrowSignalRequest(signal, InstanceId: instance.Id))).Id);

        await using var check = fixture.CreateDbContext();
        Assert.True(await check.InstanceHistory.AnyAsync(item =>
            item.InstanceId == instance.Id && item.Note == InstanceHistoryNotes.SignalThrown && item.PerformedBy == "signal"));
        Assert.Equal("alice-preparer", await check.UserTasks.AsNoTracking()
            .Where(item => item.InstanceId == instance.Id && item.NodeId == 5)
            .Select(item => item.ClaimedBy).SingleAsync());
        await ProcessDeliveryJobsAsync(Assert.Single(await BroadcastsThrownByAsync(instance.Id)).Id);
    }

    /// <summary>The throw's outgoing flow records a traversal of kind signalThrown, and no action.</summary>
    [Fact]
    public async Task FlowEvidence_RecordsTheThrowsOutgoingFlowAsASignalThrownTraversal()
    {
        var signal = UniqueSignal();
        var model = OrderModel(signal);
        model.Variables.Add(new VariableModel { Id = 3, Name = "seen", DataType = WorkflowVariableTypes.Number, Nullable = true });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 11, Name = "Count", Type = BpmnFlowNodeTypes.ScriptTask,
            Assignments = [new AssignmentModel { Variable = "seen", Expression = "FlowInfo(801, 'traversals.count')" }]
        });
        model.SequenceFlows.Single(flow => flow.Id == 801).TargetRef = 11;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 9 });
        var order = await StartAsync(await CreateAsync(model));

        await TakeAsync(order.Id, 7, 701);

        await using var db = fixture.CreateDbContext();
        var occurrence = await db.SequenceFlowOccurrences.AsNoTracking()
            .SingleAsync(item => item.InstanceId == order.Id && item.SequenceFlowId == 801);
        Assert.Equal((InstanceHistoryNotes.SignalThrown, true), (occurrence.Kind, occurrence.IsTraversal));
        Assert.False(occurrence.IsAction);
        Assert.Equal(1, (await db.InstanceVariableCurrentValues.AsNoTracking()
            .SingleAsync(item => item.InstanceId == order.Id && item.VariableName == "seen")).ValueJson.RootElement.GetInt32());
        await ProcessDeliveryJobsAsync(Assert.Single(await BroadcastsThrownByAsync(order.Id)).Id);
    }

    /// <summary>Neither the throw's information line nor a refusal's warning holds a business key.</summary>
    [Fact]
    public async Task Logs_NameIdsAndCausesButNoBusinessKey()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        await StartAsync(await CreateAsync(receiver), OrderNumber("ORD-LOG-SECRET"));
        var thrower = EndThrowerModel(signal, new SignalThrowScopeModel
        {
            Kind = SignalThrowScopeKinds.BusinessKey,
            WorkflowKey = receiver.Id,
            BusinessKeyVariable = "orderNumber"
        });
        var throwerId = await CreateAsync(thrower);
        var thrown = await StartAsync(throwerId, OrderNumber("ORD-LOG-SECRET"));
        await TakeAsync(thrown.Id, 2, 201);
        var refused = await StartAsync(throwerId, OrderNumber(new string('s', 301)));
        using (var response = await TakeResponseAsync(refused.Id, 2, 201))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var success = Assert.Single(fixture.ErrorBoundaryLogs.SignalLinesForInstance(thrown.Id),
            line => line.Message.Contains("threw signal broadcast", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, success.Level);
        var warning = Assert.Single(fixture.ErrorBoundaryLogs.SignalLinesForInstance(refused.Id),
            line => line.Message.Contains("did not throw its signal", StringComparison.Ordinal));
        Assert.Equal((LogLevel.Warning, "business_key"), (warning.Level, warning.Fields["Cause"] as string));
        foreach (var line in new[] { success, warning })
        {
            Assert.DoesNotContain("ORD-LOG-SECRET", line.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("sss", line.Message, StringComparison.Ordinal);
        }

        await ProcessDeliveryJobsAsync(Assert.Single(await BroadcastsThrownByAsync(thrown.Id)).Id);
    }

    // Cost

    /// <summary>
    /// A definition whose only signal events throw runs no subscription statement: the only
    /// statement that names the subscription table is the throw's own freeze.
    /// </summary>
    [Fact]
    public async Task DefinitionWithOnlyThrowEvents_TouchesSubscriptionsOnlyThroughTheFreeze()
    {
        var thrower = EndThrowerModel(UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows });
        var instance = await StartAsync(await CreateAsync(thrower), OrderNumber("ORD-COST"));

        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        try
        {
            await TakeAsync(instance.Id, 2, 201);
            var statements = fixture.CommandCounter.ReaderCommandTexts
                .Concat(fixture.CommandCounter.NonQueryCommandTexts)
                .Where(text => text.Contains("signal_subscriptions", StringComparison.Ordinal))
                .ToArray();
            var freeze = Assert.Single(statements);
            Assert.Contains("INSERT INTO flowbit.signal_deliveries", freeze, StringComparison.Ordinal);
        }
        finally
        {
            fixture.CommandCounter.Reset();
        }
    }

    /// <summary>A definition without signal events issues no signal statement in a transition.</summary>
    [Fact]
    public async Task DefinitionWithoutSignalEvents_RunsNoSignalStatement()
    {
        var plain = EndThrowerModel(UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows });
        plain.FlowNodes.RemoveAll(node => node.Id == 3);
        plain.FlowNodes.Add(new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent });
        var instance = await StartAsync(await CreateAsync(plain), OrderNumber("ORD-PLAIN-COST"));

        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        try
        {
            await TakeAsync(instance.Id, 2, 201);
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

    // Versions

    /// <summary>
    /// A running instance moves to a version that changes a throw's name, scope, and payload
    /// with no blocker, and its next throw uses the target's configuration. Its nullable
    /// `stopReason`, which a signal boundary maps, is still null and blocks nothing.
    /// </summary>
    [Fact]
    public async Task VersionChange_OfAThrowsNameScopeAndPayloadHasNoBlocker()
    {
        var signal = UniqueSignal();
        var model = OrderModel(signal);
        var source = await CreateAsync(model);
        var order = await StartAsync(source);
        var renamed = UniqueSignal();
        var throwNode = model.FlowNodes.Single(node => node.Id == 8);
        throwNode.Signal = new SignalEventModel
        {
            Name = renamed,
            Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows },
            Payload = [new SignalPayloadEntryModel { Name = "id", Source = SignalPayloadSources.InstanceId }]
        };
        long target;
        using (var update = await SendAsync(HttpMethod.Put, $"/api/workflows/{source}", new UpdateWorkflowRequest(model, true)))
        {
            Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
            target = (await update.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!.Id;
        }

        DateTimeOffset updatedAt;
        await using (var db = fixture.CreateDbContext())
        {
            updatedAt = await db.WorkflowInstances.AsNoTracking().Where(item => item.Id == order.Id)
                .Select(item => item.UpdatedAt).SingleAsync();
            Assert.Equal(JsonValueKind.Null, (await db.InstanceVariableCurrentValues.AsNoTracking()
                .SingleAsync(item => item.InstanceId == order.Id && item.VariableName == "stopReason"))
                .ValueJson.RootElement.ValueKind);
        }

        using (var preview = await SendAsync(HttpMethod.Post, $"/api/instances/{order.Id}/version-change/preview",
                   new PreviewInstanceVersionChangeRequest(target)))
        {
            Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
            var result = (await preview.Content.ReadFromJsonAsync<InstanceVersionChangePreviewDto>(JsonOptions))!;
            Assert.True(result.Compatible, string.Join(" ", result.Blockers.Select(issue => issue.Message)));
        }

        using (var change = await SendAsync(HttpMethod.Post, $"/api/instances/{order.Id}/version-change",
                   new ChangeInstanceVersionRequest(target, source, updatedAt, "Rename the signal")))
        {
            Assert.True(change.IsSuccessStatusCode, await change.Content.ReadAsStringAsync());
        }

        await TakeAsync(order.Id, 7, 701);

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(order.Id));
        Assert.Equal((renamed, (string?)null), (broadcast.SignalName, broadcast.WorkflowKey));
        Assert.Equal($$"""{"id":{{order.Id}}}""", Compact(broadcast.PayloadJson));
    }

    private async Task<RowHold> HoldInstanceRowAsync(long instanceId)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
                         "SELECT 1 FROM flowbit.workflow_instances WHERE \"Id\" = @id FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("id", instanceId);
            await command.ExecuteScalarAsync();
        }

        return new RowHold(connection, transaction);
    }

    private sealed class RowHold(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
