using System.Net;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Flowbit.Tests.CallActivityHarness;

namespace Flowbit.Tests;

/// <summary>
/// What each call failure does: a boundary catches it, a child outcome faults the parent,
/// and a failure before the child exists or an invalid output opens an incident.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class CallActivityFailureTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => harness.RetireLeftoverJobsAsync();

    private readonly CallActivityHarness harness = new(fixture);

    [Fact]
    public async Task A_child_fault_takes_the_catch_all_boundary_with_a_scalar_diagnostic()
    {
        var childKey = Key("child");
        await harness.PublishAsync(FaultingChild(childKey));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            withBoundary: true,
            errorVariable: "callError",
            variables: [Process("callError", WorkflowVariableTypes.String)]));

        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);

        var detail = await harness.DetailAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, detail.Status);
        Assert.Equal(BoundaryEnd, detail.CurrentNodeId);
        var diagnostic = await harness.VariableAsync(parentId, "callError");
        Assert.Contains(CallFailureCodes.ChildFaulted, diagnostic);
        Assert.Contains(detail.History, item => item.Note == InstanceHistoryNotes.CallFailed);
        var call = Assert.Single((await harness.CallsAsync(parentId)).Items);
        Assert.Equal(CallInvocationStates.Failed, call.State);
        Assert.Equal(CallResultOutcomes.Faulted, call.Outcome);
        Assert.Equal(CallFailureCodes.ChildFaulted, call.FailureCode);
        Assert.Equal("child.rejected", call.ChildFaultCode);
        Assert.False(call.CapacityReserved);

        await using var db = fixture.CreateDbContext();
        var visit = await db.NodeExecutions.AsNoTracking()
            .SingleAsync(item => item.InstanceId == parentId && item.NodeId == Call);
        Assert.Equal(NodeExecutionRecordStatuses.Faulted, visit.Status);
        Assert.Equal(NodeExecutionCompletionReasons.BoundaryCaught, visit.CompletionReason);
    }

    [Fact]
    public async Task A_child_fault_with_no_boundary_faults_the_parent_with_the_call_code_at_the_call_node()
    {
        var childKey = Key("child");
        await harness.PublishAsync(FaultingChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));

        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);

        var detail = await harness.DetailAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Faulted, detail.Status);
        Assert.Equal(CallFailureCodes.ChildFaulted, detail.Fault!.Code);
        Assert.Contains("rejected", detail.Fault.Description);
        var token = Assert.Single(detail.ExecutionPositions);
        Assert.Equal(Call, token.NodeId);
        Assert.Equal(ExecutionTokenRecordStatuses.Faulted, token.TokenStatus);
        Assert.Equal(ExecutionTokenTerminationReasons.CallFailed, token.TerminationReason);

        await using var db = fixture.CreateDbContext();
        var visit = await db.NodeExecutions.AsNoTracking()
            .SingleAsync(item => item.InstanceId == parentId && item.NodeId == Call);
        Assert.Equal(NodeExecutionRecordStatuses.Faulted, visit.Status);
        Assert.Equal(NodeExecutionCompletionReasons.CallFailed, visit.CompletionReason);
        Assert.Equal(CallFailureCodes.ChildFaulted, visit.ErrorCode);
    }

    [Fact]
    public async Task A_fault_propagates_up_through_nested_calls_by_durable_delivery()
    {
        var leafKey = Key("leaf");
        await harness.PublishAsync(FaultingChild(leafKey));
        var middleKey = Key("middle");
        await harness.PublishAsync(Parent(middleKey, leafKey));
        var parent = await harness.PublishAsync(Parent(Key("top"), middleKey));

        var rootId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(rootId);

        var detail = await harness.DetailAsync(rootId);
        Assert.Equal(WorkflowInstanceStatuses.Faulted, detail.Status);
        Assert.Equal(CallFailureCodes.ChildFaulted, detail.Fault!.Code);
    }

    [Fact]
    public async Task Cancelling_a_child_on_its_own_faults_the_parent_or_takes_its_boundary()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var plain = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var caught = await harness.PublishAsync(Parent(Key("caught"), childKey, withBoundary: true));

        var plainId = await harness.StartAsync(plain.Id);
        await harness.DrainAsync(plainId);
        var plainChild = (await harness.InvocationAsync(plainId)).ChildInstanceId!.Value;
        using (var cancelled = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{plainChild}/cancel"))
        {
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        }
        await harness.DrainAsync(plainId);
        var plainDetail = await harness.DetailAsync(plainId);
        Assert.Equal(WorkflowInstanceStatuses.Faulted, plainDetail.Status);
        Assert.Equal(CallFailureCodes.ChildCancelled, plainDetail.Fault!.Code);

        var caughtId = await harness.StartAsync(caught.Id);
        await harness.DrainAsync(caughtId);
        var caughtChild = (await harness.InvocationAsync(caughtId)).ChildInstanceId!.Value;
        using (var cancelled = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{caughtChild}/cancel"))
        {
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        }
        await harness.DrainAsync(caughtId);
        var caughtDetail = await harness.DetailAsync(caughtId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, caughtDetail.Status);
        Assert.Equal(BoundaryEnd, caughtDetail.CurrentNodeId);
    }

    [Fact]
    public async Task A_missing_target_opens_an_incident_whose_retry_succeeds_once_the_cause_is_fixed()
    {
        var childKey = Key("child");
        var parentDraft = Parent(Key("parent"), childKey);
        var child = await harness.PublishAsync(ImmediateChild(childKey));
        var parent = await harness.PublishAsync(parentDraft);

        var parentId = await harness.StartAsync(parent.Id);
        var invocation = await harness.InvocationAsync(parentId);
        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallStart);

        // The family loses its default between entry and creation.
        await harness.RemoveDefaultAsync(childKey);

        await harness.ProcessAsync(jobId);

        var blocked = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Queued, blocked.State);
        Assert.Null(blocked.ChildInstanceId);
        Assert.Equal(CallFailureCodes.TargetUnavailable, blocked.FailureCode);
        var incident = await harness.OpenIncidentAsync(jobId);
        Assert.NotNull(incident);
        Assert.Equal(CallFailureCodes.TargetUnavailable, incident!.Type);
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);
        await AssertNoDescendantsAsync(parentId);

        await harness.RestoreDefaultAsync(child.Id);

        await harness.RetryIncidentAsync(incident.Id);
        await harness.DrainAsync(parentId);

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
        var settled = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Completed, settled.State);
        Assert.Null(settled.FailureCode);
    }

    [Fact]
    public async Task A_failure_before_the_child_exists_takes_the_boundary_when_there_is_one()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(childKey));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            withBoundary: true,
            errorVariable: "callError",
            variables: [Process("callError", WorkflowVariableTypes.String)]));

        var parentId = await harness.StartAsync(parent.Id);
        await harness.RemoveDefaultAsync(childKey);

        await harness.DrainAsync(parentId);

        var detail = await harness.DetailAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, detail.Status);
        Assert.Equal(BoundaryEnd, detail.CurrentNodeId);
        Assert.Contains(CallFailureCodes.TargetUnavailable, await harness.VariableAsync(parentId, "callError"));
        var settled = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Failed, settled.State);
        Assert.False(settled.CapacityReserved);
        Assert.Null(settled.ChildInstanceId);
    }

    [Fact]
    public async Task A_business_key_conflict_never_attaches_the_existing_instance()
    {
        var childKey = Key("child");
        var child = WaitingChild(childKey, startInputs: [Input("orderId", WorkflowVariableTypes.String)]);
        child.FlowNodes.Single(node => node.Id == Start).BusinessKey = new BusinessKeyModel
        {
            Variable = "orderId",
            Uniqueness = BusinessKeyUniqueness.Active
        };
        await harness.PublishAsync(child);
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            startInputs: [Input("order", WorkflowVariableTypes.String)],
            inputs: [Mapping("order", "orderId", WorkflowVariableTypes.String)]));

        var first = await harness.StartAsync(parent.Id, Values(("order", "A-1")));
        await harness.DrainAsync(first);
        var owner = (await harness.InvocationAsync(first)).ChildInstanceId;

        var second = await harness.StartAsync(parent.Id, Values(("order", "A-1")));
        await harness.DrainAsync(second);

        var blocked = await harness.InvocationAsync(second);
        Assert.Equal(CallInvocationStates.Queued, blocked.State);
        Assert.Null(blocked.ChildInstanceId);
        Assert.Equal(CallFailureCodes.BusinessKeyConflict, blocked.FailureCode);
        Assert.Contains(owner!.Value.ToString(), blocked.FailureDescription);
        await AssertNoDescendantsAsync(second);
    }

    [Fact]
    public async Task A_missing_required_output_keeps_the_child_completed_and_fails_the_call()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(childKey, variables: [Process("approved")]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            variables: [Process("managerApproved")],
            outputs: [Mapping("approved", "managerApproved", WorkflowVariableTypes.Boolean)]));

        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);

        var invocation = await harness.InvocationAsync(parentId);
        var child = await harness.DetailAsync(invocation.ChildInstanceId!.Value);
        Assert.Equal(WorkflowInstanceStatuses.Completed, child.Status);
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);
        Assert.Equal(CallInvocationStates.Running, invocation.State);
        Assert.Equal(CallFailureCodes.OutputInvalid, invocation.FailureCode);
        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallResume);
        var incident = await harness.OpenIncidentAsync(jobId);
        Assert.Equal(CallFailureCodes.OutputInvalid, incident!.Type);
        Assert.Equal(CallInvocationStates.ResultReady, Assert.Single((await harness.CallsAsync(parentId)).Items).State);

        // Retrying validates the same frozen result, so it can only fail again here.
        await harness.RetryIncidentAsync(incident.Id);
        await harness.DrainAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);
        Assert.NotNull(await harness.OpenIncidentAsync(jobId));

        // Cancelling the parent is the way out, and it leaves the completed child alone.
        using var cancelled = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/cancel");
        Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        await harness.DrainAsync(parentId);
        Assert.Equal(CallInvocationStates.Cancelled, (await harness.InvocationAsync(parentId)).State);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(invocation.ChildInstanceId!.Value)).Status);
    }

    [Fact]
    public async Task An_invalid_output_takes_the_boundary_when_there_is_one_and_writes_nothing()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(
            childKey,
            variables: [Process("count", WorkflowVariableTypes.Number, nullable: false, defaultValue: 5)]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            withBoundary: true,
            errorVariable: "callError",
            variables:
            [
                Process("total", WorkflowVariableTypes.Number, validation: "total > 100"),
                Process("callError", WorkflowVariableTypes.String)
            ],
            outputs: [Mapping("count", "total")]));

        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);

        var detail = await harness.DetailAsync(parentId);
        Assert.Equal(BoundaryEnd, detail.CurrentNodeId);
        Assert.Contains(CallFailureCodes.OutputInvalid, await harness.VariableAsync(parentId, "callError"));
        Assert.Equal("null", await harness.VariableAsync(parentId, "total"));
    }

    [Fact]
    public async Task Optional_mappings_omit_a_missing_source_and_pass_a_present_null_on()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(
            childKey,
            startInputs:
            [
                Input("note", WorkflowVariableTypes.Json, required: false),
                new VariableModel
                {
                    Id = 77,
                    Name = "channel",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = System.Text.Json.JsonSerializer.SerializeToElement("web")
                }
            ],
            variables: [Process("result", WorkflowVariableTypes.Json)]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            startInputs: [Input("channelSource", WorkflowVariableTypes.String, required: false)],
            variables:
            [
                Process("noteSource", WorkflowVariableTypes.Json),
                Process("target", WorkflowVariableTypes.Json),
                Process("untouched", WorkflowVariableTypes.String, nullable: false, defaultValue: "kept")
            ],
            inputs:
            [
                Mapping("noteSource", "note", WorkflowVariableTypes.Json, required: false),
                Mapping("channelSource", "channel", WorkflowVariableTypes.String, required: false)
            ],
            outputs:
            [
                Mapping("result", "target", WorkflowVariableTypes.Json, required: false)
            ]));

        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);

        var invocation = await harness.InvocationAsync(parentId);
        var childId = invocation.ChildInstanceId!.Value;
        Assert.Equal("null", await harness.VariableAsync(childId, "note"));
        Assert.Equal("\"web\"", await harness.VariableAsync(childId, "channel"));
        Assert.Equal("null", await harness.VariableAsync(parentId, "target"));
        Assert.Equal("\"kept\"", await harness.VariableAsync(parentId, "untouched"));
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
    }

    [Fact]
    public async Task A_snapshot_over_the_size_limit_is_invalid()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(
            childKey,
            startInputs: [Input("blob", WorkflowVariableTypes.String, isArray: true)]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            startInputs: [Input("large", WorkflowVariableTypes.String, isArray: true)],
            inputs: [Mapping("large", "blob", WorkflowVariableTypes.String, isArray: true)]));

        // Many small strings, because a single large one would exceed the variable index.
        var parentId = await harness.StartAsync(
            parent.Id,
            Values(("large", Enumerable.Repeat(new string('x', 1000), 1100).ToArray())));

        var invocation = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Queued, invocation.State);
        Assert.False(invocation.CapacityReserved);
        Assert.Equal(CallFailureCodes.InputInvalid, invocation.FailureCode);
        Assert.Null(invocation.InputValuesJson);
        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallStart);
        Assert.Equal(WorkflowJobStatuses.Incident, await harness.JobStatusAsync(jobId));
    }

    private async Task AssertNoDescendantsAsync(long rootId)
    {
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowInstances.AsNoTracking()
            .AnyAsync(instance => instance.CallRootInstanceId == rootId));
    }
}
