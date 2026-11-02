using System.Net;
using System.Net.Http.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Flowbit.Tests.CallActivityHarness;

namespace Flowbit.Tests;

/// <summary>
/// The normal call path against real PostgreSQL: creation, delivery, identity, and repeated visits.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class CallActivityLifecycleTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => harness.RetireLeftoverJobsAsync();

    private readonly CallActivityHarness harness = new(fixture);

    [Fact]
    public async Task A_child_that_finishes_at_once_delivers_its_result_and_the_parent_continues()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(
            childKey,
            startInputs: [Input("amount")],
            variables: [Process("approved", nullable: false, defaultValue: true)]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            startInputs: [Input("requestedAmount")],
            variables: [Process("managerApproved")],
            inputs: [Mapping("requestedAmount", "amount")],
            outputs: [Mapping("approved", "managerApproved", WorkflowVariableTypes.Boolean)]));

        var parentId = await harness.StartAsync(parent.Id, Values(("requestedAmount", 250)));
        var waiting = await harness.DetailAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Running, waiting.Status);
        var position = Assert.Single(waiting.ExecutionPositions, item => item.TokenStatus == ExecutionTokenRecordStatuses.Active);
        Assert.Equal(Call, position.NodeId);
        Assert.Equal(ExecutionTokenWaitStates.CallStart, position.WaitState);
        Assert.Equal(CallInvocationStates.Queued, position.Call!.State);
        Assert.True(position.Call.CapacityReserved);
        Assert.Null(position.Call.ChildInstanceId);

        var invocation = await harness.InvocationAsync(parentId);
        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallStart);

        // The child finished in the creation transaction, so the result awaits delivery.
        invocation = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Running, invocation.State);
        var childId = invocation.ChildInstanceId!.Value;
        var child = await harness.DetailAsync(childId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, child.Status);
        Assert.Equal("callActivity", child.StartedBy);
        Assert.Equal("250", await harness.VariableAsync(childId, "amount"));
        Assert.Equal(parentId, child.ParentCall!.ParentInstanceId);
        Assert.Equal(parentId, child.ParentCall.RootInstanceId);
        Assert.Equal(1, child.ParentCall.Depth);
        Assert.Equal(CallInvocationStates.ResultReady, child.ParentCall.State);
        var running = await harness.CallsAsync(parentId);
        Assert.Equal(CallInvocationStates.ResultReady, Assert.Single(running.Items).State);

        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallResume);

        var finished = await harness.DetailAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, finished.Status);
        Assert.Equal("true", await harness.VariableAsync(parentId, "managerApproved"));
        var settled = Assert.Single((await harness.CallsAsync(parentId)).Items);
        Assert.Equal(CallInvocationStates.Completed, settled.State);
        Assert.Equal(CallResultOutcomes.Completed, settled.Outcome);
        Assert.Equal(childId, settled.ChildInstanceId);
        Assert.Equal(1, settled.ChildDefinitionVersion);
        Assert.False(settled.CapacityReserved);
        Assert.Contains(finished.History, item => item.Note == InstanceHistoryNotes.CallQueued);
        Assert.Contains(finished.History, item => item.Note == InstanceHistoryNotes.CallChildStarted);
        Assert.Contains(finished.History, item => item.Note == InstanceHistoryNotes.CallCompleted);
    }

    [Fact]
    public async Task The_parent_rests_while_a_child_waits_and_continues_as_the_system_actor()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(
            childKey,
            variables: [Process("decision", WorkflowVariableTypes.String)],
            actionVariables: []));
        var key = Key("parent");
        var model = Parent(key, childKey, variables: [Process("who", WorkflowVariableTypes.String)]);
        model.FlowNodes.Single(node => node.Id == End).Type = BpmnFlowNodeTypes.UserTask;
        model.FlowNodes.Single(node => node.Id == End).Name = "Review";
        model.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 301, SourceRef = End, TargetRef = 6, Name = "Done" });
        var parent = await harness.PublishAsync(model);

        var parentId = await harness.StartAsync(parent.Id, user: "requester");
        var invocation = await harness.InvocationAsync(parentId);
        Assert.Equal("requester", invocation.CausedBy);
        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallStart);

        invocation = await harness.InvocationAsync(parentId);
        var childId = invocation.ChildInstanceId!.Value;
        var waiting = await harness.DetailAsync(parentId);
        var position = Assert.Single(waiting.ExecutionPositions, item => item.TokenStatus == ExecutionTokenRecordStatuses.Active);
        Assert.Null(position.WaitState);
        Assert.Equal(CallInvocationStates.Running, position.Call!.State);
        Assert.Equal(childId, position.Call.ChildInstanceId);

        using (var acted = await harness.TakeChildActionAsync(childId))
        {
            Assert.True(acted.IsSuccessStatusCode, await acted.Content.ReadAsStringAsync());
        }

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(childId)).Status);
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);
        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallResume);

        var after = await harness.DetailAsync(parentId);
        Assert.Equal(End, after.CurrentNodeId);
        var delivered = Assert.Single(after.History, item => item.Note == InstanceHistoryNotes.CallCompleted);
        Assert.Equal("callActivity", delivered.PerformedBy);
    }

    [Fact]
    public async Task Each_visit_to_a_call_in_a_loop_gets_its_own_invocation_and_child()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(childKey));
        var key = Key("parent");
        var model = Parent(key, childKey);
        model.FlowNodes.Single(node => node.Id == End).Type = BpmnFlowNodeTypes.UserTask;
        model.FlowNodes.Single(node => node.Id == End).Name = "Again?";
        model.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 301, SourceRef = End, TargetRef = Call, Name = "Again" });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 302, SourceRef = End, TargetRef = 6, Name = "Done" });
        var parent = await harness.PublishAsync(model);

        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{parentId}/flows/301")
        {
            Content = JsonContent.Create(new { })
        };
        ApiTestAuth.Authorize(request, "reviewer", "reviewer");
        using (var again = await fixture.Client.SendAsync(request))
        {
            Assert.True(again.IsSuccessStatusCode, await again.Content.ReadAsStringAsync());
        }

        await harness.DrainAsync(parentId);

        var calls = await harness.CallsAsync(parentId);
        Assert.Equal(2, calls.TotalCount);
        Assert.Equal(2, calls.Items.Select(call => call.ChildInstanceId).Distinct().Count());
        Assert.Equal(2, calls.Items.Select(call => call.ActivationId).Distinct().Count());
        Assert.All(calls.Items, call => Assert.Equal(CallInvocationStates.Completed, call.State));

        // Newest first, so a page reads the latest visit first.
        Assert.True(calls.Items[0].InvocationId > calls.Items[1].InvocationId);
        Assert.Equal(End, (await harness.DetailAsync(parentId)).CurrentNodeId);
    }

    [Fact]
    public async Task A_parents_other_branch_keeps_running_while_its_call_waits()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var key = Key("parent");
        var model = new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = Start,
            FlowNodes =
            [
                new FlowNodeModel { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 10, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel
                {
                    Id = 11,
                    Name = "Call",
                    Type = BpmnFlowNodeTypes.CallActivity,
                    CallActivity = new CallActivityModel { WorkflowKey = childKey }
                },
                new FlowNodeModel { Id = 12, Name = "Task", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 13, Name = "Join", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 14, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 11 },
                new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 12 },
                new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 13 },
                new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = 13, Name = "Finish" },
                new SequenceFlowModel { Id = 1301, SourceRef = 13, TargetRef = 14 }
            ]
        };
        var parent = await harness.PublishAsync(model);

        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);

        await using (var db = fixture.CreateDbContext())
        {
            var task = await db.UserTasks.AsNoTracking().SingleAsync(item => item.InstanceId == parentId);
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/user-tasks/{task.Id}/flows/1201")
            {
                Content = JsonContent.Create(new { })
            };
            ApiTestAuth.Authorize(request, "worker", "worker");
            using var finished = await fixture.Client.SendAsync(request);
            Assert.True(finished.IsSuccessStatusCode, await finished.Content.ReadAsStringAsync());
        }

        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);

        using (var acted = await harness.TakeChildActionAsync(invocation.ChildInstanceId!.Value))
        {
            Assert.True(acted.IsSuccessStatusCode, await acted.Content.ReadAsStringAsync());
        }

        await harness.DrainAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
    }

    [Fact]
    public async Task Nested_calls_finish_from_the_deepest_child_up()
    {
        var leafKey = Key("leaf");
        await harness.PublishAsync(ImmediateChild(leafKey));
        var middleKey = Key("middle");
        await harness.PublishAsync(Parent(middleKey, leafKey));
        var parent = await harness.PublishAsync(Parent(Key("top"), middleKey));

        var rootId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(rootId);

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(rootId)).Status);
        await using var db = fixture.CreateDbContext();
        var descendants = await db.WorkflowInstances.AsNoTracking()
            .Where(instance => instance.CallRootInstanceId == rootId)
            .OrderBy(instance => instance.CallDepth)
            .ToListAsync();
        Assert.Equal([1, 2], descendants.Select(instance => instance.CallDepth));
        Assert.All(descendants, instance => Assert.Equal(WorkflowInstanceStatuses.Completed, instance.Status));
    }

    [Fact]
    public async Task A_terminate_end_inside_a_child_completes_only_that_child()
    {
        var childKey = Key("child");
        var child = new WorkflowModel
        {
            Id = childKey,
            Name = childKey,
            InitialEventId = Start,
            FlowNodes =
            [
                new FlowNodeModel { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = End, Name = "Stop", Type = BpmnFlowNodeTypes.TerminateEndEvent }
            ],
            SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = End }]
        };
        await harness.PublishAsync(child);
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));

        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
        var call = Assert.Single((await harness.CallsAsync(parentId)).Items);
        Assert.Equal(CallResultOutcomes.Completed, call.Outcome);
    }
}
