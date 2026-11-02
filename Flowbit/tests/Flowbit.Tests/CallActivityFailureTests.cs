using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
        // Nothing is left to deliver, so the incident does not stay open forever.
        Assert.Null(await harness.OpenIncidentAsync(jobId));
        Assert.Equal(WorkflowJobStatuses.Cancelled, await harness.JobStatusAsync(jobId));

        await harness.DrainAsync(parentId);
        Assert.Equal(CallInvocationStates.Cancelled, (await harness.InvocationAsync(parentId)).State);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(invocation.ChildInstanceId!.Value)).Status);
    }

    // A fault's description may hold as many characters as the call's stored text, and the
    // wording in front of it pushes that over. Cutting by UTF-16 length would split a pair for
    // one of the two paddings, which the database driver refuses, wedging the call.
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task A_child_fault_description_at_the_limit_is_cut_between_characters(int padding, bool withBoundary)
    {
        var limit = CallActivityConstraints.MaxFailureDescriptionLength;
        var childKey = Key("child");
        var child = FaultingChild(childKey);
        child.FlowNodes.Single(node => node.Id == End).ErrorDescription =
            new string('x', padding) + string.Concat(Enumerable.Repeat("\U0001F600", limit - padding));
        await harness.PublishAsync(child);
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            withBoundary: withBoundary,
            errorVariable: withBoundary ? "callError" : null,
            variables: withBoundary ? [Process("callError", WorkflowVariableTypes.String)] : null));

        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);

        var invocation = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Failed, invocation.State);
        Assert.StartsWith("Child instance #", invocation.FailureDescription);
        AssertWholeCharacters(invocation.FailureDescription!, limit);
        var detail = await harness.DetailAsync(parentId);
        if (!withBoundary)
        {
            Assert.Equal(WorkflowInstanceStatuses.Faulted, detail.Status);
            AssertWholeCharacters(detail.Fault!.Description!, limit);
            return;
        }

        Assert.Equal(WorkflowInstanceStatuses.Completed, detail.Status);
        var diagnostic = JsonSerializer.Deserialize<string>((await harness.VariableAsync(parentId, "callError"))!)!;
        Assert.StartsWith($"{CallFailureCodes.ChildFaulted}: Child instance #", diagnostic);
        AssertWholeCharacters(diagnostic, limit);
    }

    [Fact]
    public async Task A_boundary_diagnostic_stays_within_the_limit_with_its_code_in_front()
    {
        var limit = CallActivityConstraints.MaxFailureDescriptionLength;
        var childKey = Key("child");
        var child = FaultingChild(childKey);
        child.FlowNodes.Single(node => node.Id == End).ErrorDescription = new string('x', limit);
        await harness.PublishAsync(child);
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            withBoundary: true,
            errorVariable: "callError",
            variables: [Process("callError", WorkflowVariableTypes.String)]));

        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
        var diagnostic = JsonSerializer.Deserialize<string>((await harness.VariableAsync(parentId, "callError"))!)!;
        Assert.Equal(limit, diagnostic.Length);
        Assert.StartsWith($"{CallFailureCodes.ChildFaulted}: Child instance #", diagnostic);
        Assert.Equal(limit, (await harness.InvocationAsync(parentId)).FailureDescription!.Length);
    }

    private static void AssertWholeCharacters(string text, int limit)
    {
        Assert.Equal(limit, text.EnumerateRunes().Count());
        new UTF8Encoding(false, throwOnInvalidBytes: true).GetByteCount(text);
        Assert.EndsWith("\U0001F600", text);
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

    // Only a definition's effective values hold a shared alias, so reading the instance's own
    // rows failed every such rule, and a boundary then took a valid result down the error path.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_target_rule_that_reads_a_shared_alias_delivers_a_valid_result(bool withBoundary)
    {
        var (parentId, _, _) = await StartUnderASharedLimitAsync(limit: 10, withBoundary);

        await harness.DrainAsync(parentId);

        var detail = await harness.DetailAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, detail.Status);
        Assert.Equal(End, detail.CurrentNodeId);
        Assert.Equal("5", await harness.VariableAsync(parentId, "total"));
        Assert.Equal(CallInvocationStates.Completed, (await harness.InvocationAsync(parentId)).State);
    }

    [Fact]
    public async Task A_target_rule_that_reads_a_shared_alias_still_refuses_an_invalid_result()
    {
        var (parentId, _, _) = await StartUnderASharedLimitAsync(limit: 3, withBoundary: true);

        await harness.DrainAsync(parentId);

        var detail = await harness.DetailAsync(parentId);
        Assert.Equal(BoundaryEnd, detail.CurrentNodeId);
        Assert.Equal("null", await harness.VariableAsync(parentId, "total"));
        var diagnostic = await harness.VariableAsync(parentId, "callError");
        Assert.Contains(CallFailureCodes.OutputInvalid, diagnostic);
        Assert.Contains("total <= approvalLimit", diagnostic);
    }

    [Fact]
    public async Task A_retry_reads_the_shared_alias_again_and_delivers_once_the_limit_allows_it()
    {
        var (parentId, sharedKey, revision) = await StartUnderASharedLimitAsync(limit: 3, withBoundary: false);
        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);
        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallResume);
        var incident = await harness.OpenIncidentAsync(jobId);
        Assert.Equal(CallFailureCodes.OutputInvalid, incident!.Type);
        Assert.Equal("null", await harness.VariableAsync(parentId, "total"));

        using (var raised = await harness.SendAsync(
                   HttpMethod.Put,
                   $"/api/shared-variables/{Uri.EscapeDataString(sharedKey)}/value",
                   new UpdateSharedVariableValueRequest(JsonSerializer.SerializeToElement(10), revision)))
        {
            Assert.True(raised.IsSuccessStatusCode, await raised.Content.ReadAsStringAsync());
        }

        await harness.RetryIncidentAsync(incident.Id);
        await harness.ProcessAsync(jobId);

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
        Assert.Equal("5", await harness.VariableAsync(parentId, "total"));
    }

    private async Task<(long ParentId, string SharedKey, long Revision)> StartUnderASharedLimitAsync(
        int limit,
        bool withBoundary)
    {
        var sharedKey = $"tests.call.{Guid.NewGuid():N}";
        long revision;
        using (var created = await harness.SendAsync(
                   HttpMethod.Post,
                   "/api/shared-variables",
                   new CreateSharedVariableRequest(
                       sharedKey,
                       WorkflowVariableTypes.Number,
                       IsArray: false,
                       Nullable: false,
                       HasValue: true,
                       JsonSerializer.SerializeToElement(limit))))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            revision = (await created.Content.ReadFromJsonAsync<SharedVariableDto>())!.Revision;
        }

        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(childKey, startInputs: [Input("total")]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            withBoundary,
            errorVariable: withBoundary ? "callError" : null,
            startInputs: [Input("amount")],
            variables:
            [
                Process("total", WorkflowVariableTypes.Number, validation: "total <= approvalLimit"),
                Process("callError", WorkflowVariableTypes.String),
                new VariableModel
                {
                    Id = 40,
                    Name = "approvalLimit",
                    Scope = VariableScopes.Shared,
                    SharedKey = sharedKey,
                    Access = SharedVariableAccessModes.Read,
                    DataType = WorkflowVariableTypes.Number
                }
            ],
            inputs: [Mapping("amount", "total")],
            outputs: [Mapping("total", "total")]));
        return (await harness.StartAsync(parent.Id, Values(("amount", 5))), sharedKey, revision);
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

    [Fact]
    public async Task An_incident_on_a_nested_call_counts_on_the_call_above_it_once()
    {
        var leafKey = Key("leaf");
        await harness.PublishAsync(ImmediateChild(leafKey));
        var middleKey = Key("middle");
        await harness.PublishAsync(Parent(middleKey, leafKey));
        var parent = await harness.PublishAsync(Parent(Key("top"), middleKey));
        await harness.RemoveDefaultAsync(leafKey);

        var rootId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(rootId);

        var call = Assert.Single((await harness.CallsAsync(rootId)).Items);
        Assert.Equal(CallInvocationStates.Running, call.State);
        Assert.Equal(1, call.OpenIncidentCount);
        var nested = Assert.Single((await harness.CallsAsync(call.ChildInstanceId!.Value)).Items);
        Assert.Equal(CallInvocationStates.Queued, nested.State);
        Assert.Null(nested.ChildInstanceId);
        Assert.Equal(1, nested.OpenIncidentCount);
    }

    [Fact]
    public async Task A_call_message_incident_names_the_instance_its_message_acts_on()
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
        var childId = invocation.ChildInstanceId!.Value;
        var incident = await harness.OpenIncidentAsync(await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallResume));

        // A result belongs to the finished child that sent it, and acts on the parent.
        var listed = Assert.Single(await IncidentsAsync(childId));
        Assert.Equal(incident!.Id, listed.Id);
        Assert.Equal(childId, listed.InstanceId);
        Assert.Equal(parentId, listed.CallTargetInstanceId);
        Assert.Equal(parentId, (await IncidentDetailAsync(incident.Id)).Summary.CallTargetInstanceId);
    }

    [Fact]
    public async Task An_incident_outside_a_call_message_names_no_other_instance()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.RemoveDefaultAsync(childKey);
        await harness.DrainAsync(parentId);

        // A call's start job acts on the instance that owns it.
        var listed = Assert.Single(await IncidentsAsync(parentId));
        Assert.Equal(parentId, listed.InstanceId);
        Assert.Null(listed.CallTargetInstanceId);

        using var raw = await harness.SendAsync(HttpMethod.Get, $"/api/incidents/{listed.Id}");
        Assert.DoesNotContain("callTargetInstanceId", await raw.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<IncidentSummaryDto>> IncidentsAsync(long instanceId)
    {
        using var response = await harness.SendAsync(HttpMethod.Get, $"/api/incidents?instanceId={instanceId}&status=open");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagedResult<IncidentSummaryDto>>())!.Items;
    }

    private async Task<IncidentDetailDto> IncidentDetailAsync(long incidentId)
    {
        using var response = await harness.SendAsync(HttpMethod.Get, $"/api/incidents/{incidentId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IncidentDetailDto>())!;
    }

    private async Task AssertNoDescendantsAsync(long rootId)
    {
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowInstances.AsNoTracking()
            .AnyAsync(instance => instance.CallRootInstanceId == rootId));
    }
}
