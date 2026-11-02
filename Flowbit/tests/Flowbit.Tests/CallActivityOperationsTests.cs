using System.Net;
using System.Net.Http.Json;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Flowbit.Tests.CallActivityHarness;

namespace Flowbit.Tests;

/// <summary>
/// What operators can do around a call: version changes, reactivation, deletion, caller
/// previews, and the start restriction.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class CallActivityOperationsTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => harness.RetireLeftoverJobsAsync();

    private readonly CallActivityHarness harness = new(fixture);

    [Fact]
    public async Task Unsettled_calls_block_a_version_change_on_both_sides_until_they_settle()
    {
        var childKey = Key("child");
        var child = await harness.PublishAsync(WaitingChild(childKey));
        var childNext = await harness.PublishAsync(WaitingChild(childKey));
        var parentKey = Key("parent");
        var parent = await harness.PublishAsync(Parent(parentKey, childKey));
        var parentNext = await harness.PublishAsync(Parent(parentKey, childKey));
        var parentId = await harness.StartAsync(parent.Id);

        // Queued, with no child yet.
        Assert.Contains(
            WorkflowVersionCompatibilityCodes.UnsettledOutgoingCall,
            await VersionBlockersAsync(parentId, parentNext.Id));

        await harness.DrainAsync(parentId);
        var childId = (await harness.InvocationAsync(parentId)).ChildInstanceId!.Value;
        Assert.Contains(
            WorkflowVersionCompatibilityCodes.UnsettledOutgoingCall,
            await VersionBlockersAsync(parentId, parentNext.Id));
        Assert.Contains(
            WorkflowVersionCompatibilityCodes.UnsettledIncomingCall,
            await VersionBlockersAsync(childId, childNext.Id));
        Assert.Equal(child.Id, (await harness.InvocationAsync(parentId)).ChildDefinitionId);
    }

    [Fact]
    public async Task Unsettled_calls_refuse_direct_version_changes_and_leave_batch_items_ineligible()
    {
        var childKey = Key("child");
        var child = await harness.PublishAsync(WaitingChild(childKey));
        var childNext = await harness.PublishAsync(WaitingChild(childKey));
        var parentKey = Key("parent");
        var parent = await harness.PublishAsync(Parent(parentKey, childKey));
        var parentNext = await harness.PublishAsync(Parent(parentKey, childKey));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var childId = (await harness.InvocationAsync(parentId)).ChildInstanceId!.Value;

        var before = await harness.DetailAsync(parentId);
        using (var direct = await harness.SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{parentId}/version-change",
                   new ChangeInstanceVersionRequest(parentNext.Id, parent.Id, before.UpdatedAt, "Move the parent")))
        {
            Assert.Equal(HttpStatusCode.Conflict, direct.StatusCode);
        }

        var childBefore = await harness.DetailAsync(childId);
        using (var directChild = await harness.SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{childId}/version-change",
                   new ChangeInstanceVersionRequest(childNext.Id, child.Id, childBefore.UpdatedAt, "Move the child")))
        {
            Assert.Equal(HttpStatusCode.Conflict, directChild.StatusCode);
        }

        var parentItem = await harness.PrepareVersionBatchItemAsync(parent.Id, parentNext.Id, parentId);
        Assert.Equal(InstanceVersionChangeBatchItemStatuses.Ineligible, parentItem.Status);
        Assert.Contains(parentItem.Blockers, item => item.Code == WorkflowVersionCompatibilityCodes.UnsettledOutgoingCall);

        var childItem = await harness.PrepareVersionBatchItemAsync(child.Id, childNext.Id, childId);
        Assert.Equal(InstanceVersionChangeBatchItemStatuses.Ineligible, childItem.Status);
        Assert.Contains(childItem.Blockers, item => item.Code == WorkflowVersionCompatibilityCodes.UnsettledIncomingCall);

        Assert.Equal(parent.Id, (await harness.DetailAsync(parentId)).Workflow.Id);
        Assert.Equal(child.Id, (await harness.DetailAsync(childId)).Workflow.Id);
    }

    [Fact]
    public async Task A_child_is_pinned_to_the_default_when_its_job_runs_not_when_the_parent_entered()
    {
        var childKey = Key("child");
        var first = await harness.PublishAsync(ImmediateChild(childKey));
        var second = await harness.PublishAsync(ImmediateChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        var invocation = await harness.InvocationAsync(parentId);
        Assert.Null(invocation.ChildDefinitionId);

        using (var promoted = await harness.SendAsync(HttpMethod.Post, $"/api/workflows/{second.Id}/set-default"))
        {
            Assert.True(promoted.IsSuccessStatusCode, await promoted.Content.ReadAsStringAsync());
        }

        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallStart);
        var pinned = await harness.InvocationAsync(parentId);
        Assert.Equal(second.Id, pinned.ChildDefinitionId);
        Assert.NotEqual(first.Id, pinned.ChildDefinitionId);
        Assert.Equal(Start, pinned.ChildStartEventId);

        var call = Assert.Single((await harness.CallsAsync(parentId)).Items);
        Assert.Equal(2, call.ChildDefinitionVersion);
    }

    [Fact]
    public async Task A_default_that_stopped_matching_fails_the_invocation_instead_of_picking_another_version()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(childKey, startInputs: [Input("amount")]));
        var incompatible = await harness.PublishAsync(ImmediateChild(childKey, startInputs: [Input("renamed")]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            startInputs: [Input("requestedAmount")],
            inputs: [Mapping("requestedAmount", "amount")]));
        var parentId = await harness.StartAsync(parent.Id, Values(("requestedAmount", 5)));
        var invocation = await harness.InvocationAsync(parentId);

        // Nothing stops the default from changing; the caller preview warns about it.
        using (var promoted = await harness.SendAsync(HttpMethod.Post, $"/api/workflows/{incompatible.Id}/set-default"))
        {
            Assert.True(promoted.IsSuccessStatusCode, await promoted.Content.ReadAsStringAsync());
        }

        await harness.DrainAsync(parentId);

        var blocked = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Queued, blocked.State);
        Assert.Equal(CallFailureCodes.TargetIncompatible, blocked.FailureCode);
        Assert.Contains("amount", blocked.FailureDescription);
    }

    [Fact]
    public async Task A_linked_child_cannot_be_reactivated_and_a_root_waits_for_its_descendants()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var key = Key("parent");
        var model = Parent(key, childKey);
        model.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = "Before", Type = BpmnFlowNodeTypes.UserTask });
        model.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 6;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = Call, Name = "Go" });
        var parent = await harness.PublishAsync(model);
        var parentId = await harness.StartAsync(parent.Id);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{parentId}/flows/601")
        {
            Content = JsonContent.Create(new { })
        };
        ApiTestAuth.Authorize(request, "worker", "worker");
        using (var moved = await fixture.Client.SendAsync(request))
        {
            Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
        }

        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);
        var childId = invocation.ChildInstanceId!.Value;

        using (var cancelled = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/cancel"))
        {
            Assert.True(cancelled.IsSuccessStatusCode);
        }

        // The descendant has not stopped yet and its cancel job is still open.
        var waiting = await ReactivationAsync(parentId);
        Assert.False(waiting.CanReactivate);
        Assert.Contains(waiting.Blockers, item => item.Code == "running_call_descendants");
        Assert.Contains(waiting.Blockers, item => item.Code == "unresolved_durable_work");

        await harness.DrainAsync(parentId);

        var stopped = await ReactivationAsync(parentId);
        Assert.DoesNotContain(stopped.Blockers, item => item.Code == "running_call_descendants");
        Assert.DoesNotContain(stopped.Blockers, item => item.Code == "unsettled_calls");
        Assert.True(stopped.CanReactivate, string.Join(", ", stopped.Blockers.Select(item => item.Code)));

        var childPreview = await ReactivationAsync(childId);
        Assert.False(childPreview.CanReactivate);
        Assert.Contains(childPreview.Blockers, item => item.Code == "linked_call_child");

        using var reactivated = await harness.SendAsync(
            HttpMethod.Post,
            $"/api/instances/{parentId}/reactivation",
            new ReactivateInstanceRequest(6, stopped.WorkflowId, stopped.ExpectedUpdatedAt, "Try again."));
        Assert.True(reactivated.IsSuccessStatusCode, await reactivated.Content.ReadAsStringAsync());

        var repeat = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{parentId}/flows/601")
        {
            Content = JsonContent.Create(new { })
        };
        ApiTestAuth.Authorize(repeat, "worker", "worker");
        using (var movedAgain = await fixture.Client.SendAsync(repeat))
        {
            Assert.True(movedAgain.IsSuccessStatusCode, await movedAgain.Content.ReadAsStringAsync());
        }

        // A future call is a fresh visit; the old invocation and child stay closed.
        var fresh = await harness.InvocationAsync(parentId);
        Assert.NotEqual(invocation.Id, fresh.Id);
        Assert.NotEqual(invocation.ActivationId, fresh.ActivationId);
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(childId)).Status);
    }

    [Fact]
    public async Task A_definition_with_invocations_cannot_be_deleted()
    {
        var childKey = Key("child");
        var child = await harness.PublishAsync(ImmediateChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);

        using (var parentDelete = await harness.SendAsync(HttpMethod.Delete, $"/api/workflows/{parent.Id}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, parentDelete.StatusCode);
        }

        // The child definition has no instance of its own yet, only the invocation that pinned it.
        await harness.DrainAsync(parentId);
        await using var db = fixture.CreateDbContext();
        Assert.True(await db.CallActivityExecutions.AnyAsync(call => call.ChildDefinitionId == child.Id));
        using var childDelete = await harness.SendAsync(HttpMethod.Delete, $"/api/workflows/{child.Id}");
        Assert.Equal(HttpStatusCode.Conflict, childDelete.StatusCode);
    }

    [Fact]
    public async Task The_callers_route_previews_current_candidate_and_deletion_results()
    {
        var childKey = Key("child");
        var compatible = await harness.PublishAsync(ImmediateChild(childKey, startInputs: [Input("amount")]));
        var incompatible = await harness.PublishAsync(ImmediateChild(childKey, startInputs: [Input("renamed")]));
        var parentKey = Key("parent");
        var parent = await harness.PublishAsync(Parent(
            parentKey,
            childKey,
            startInputs: [Input("requestedAmount")],
            inputs: [Mapping("requestedAmount", "amount")]));

        var current = await CallersAsync(childKey);
        Assert.Equal(CallersPreviewModes.Current, current.Mode);
        Assert.Equal(compatible.Id, current.EffectiveTargetId);
        Assert.False(current.NoDefault);
        Assert.Null(current.DeletionAllowed);
        var caller = Assert.Single(current.Callers.Items);
        Assert.Equal(parent.Id, caller.WorkflowId);
        Assert.Equal(Call, caller.NodeId);
        Assert.True(caller.Compatible);

        var candidate = await CallersAsync(childKey, $"candidateId={incompatible.Id}");
        Assert.Equal(CallersPreviewModes.Candidate, candidate.Mode);
        Assert.Equal(incompatible.Id, candidate.EffectiveTargetId);
        var failing = Assert.Single(candidate.Callers.Items);
        Assert.False(failing.Compatible);
        Assert.Contains(CallFailureCodes.TargetIncompatible, failing.ReasonCodes);

        // Deleting the default promotes the highest remaining version, the incompatible one.
        var deletion = await CallersAsync(childKey, $"deletingId={compatible.Id}");
        Assert.Equal(CallersPreviewModes.Deletion, deletion.Mode);
        Assert.True(deletion.DeletionAllowed);
        Assert.Equal(incompatible.Id, deletion.EffectiveTargetId);
        Assert.False(deletion.NoDefault);
        Assert.False(Assert.Single(deletion.Callers.Items).Compatible);

        // A running instance of the default blocks deletion, so the default stays as it is.
        var running = await harness.StartAsync(compatible.Id, Values(("amount", 1)));
        var blocked = await CallersAsync(childKey, $"deletingId={compatible.Id}");
        Assert.False(blocked.DeletionAllowed);
        Assert.NotEmpty(blocked.DeletionBlockers);
        Assert.Equal(compatible.Id, blocked.EffectiveTargetId);
        Assert.True(Assert.Single(blocked.Callers.Items).Compatible);
        Assert.NotEqual(0, running);
    }

    [Fact]
    public async Task Deleting_the_only_default_previews_no_default_with_every_caller_unavailable()
    {
        var childKey = Key("child");
        var child = await harness.PublishAsync(ImmediateChild(childKey));
        await harness.PublishAsync(Parent(Key("parent"), childKey));

        var preview = await CallersAsync(childKey, $"deletingId={child.Id}");

        Assert.True(preview.DeletionAllowed);
        Assert.True(preview.NoDefault);
        Assert.Null(preview.EffectiveTargetId);
        var caller = Assert.Single(preview.Callers.Items);
        Assert.False(caller.Compatible);
        Assert.Equal([CallFailureCodes.TargetUnavailable], caller.ReasonCodes);
    }

    [Fact]
    public async Task The_callers_route_rejects_bad_selectors_and_returns_metadata_on_an_empty_page()
    {
        var childKey = Key("child");
        var child = await harness.PublishAsync(ImmediateChild(childKey));
        var draft = await PublishDraftAsync(ImmediateChild(childKey));
        var other = await harness.PublishAsync(ImmediateChild(Key("other")));

        await AssertStatusAsync(HttpStatusCode.BadRequest, $"/api/workflows/{childKey}/callers?candidateId={child.Id}&deletingId={child.Id}");
        await AssertStatusAsync(HttpStatusCode.BadRequest, $"/api/workflows/{childKey}/callers?candidateId={draft.Id}");
        await AssertStatusAsync(HttpStatusCode.BadRequest, $"/api/workflows/{childKey}/callers?deletingId={draft.Id}");
        await AssertStatusAsync(HttpStatusCode.NotFound, $"/api/workflows/{childKey}/callers?candidateId={other.Id}");
        await AssertStatusAsync(HttpStatusCode.NotFound, $"/api/workflows/{childKey}/callers?deletingId=999999999");

        var empty = await CallersAsync(childKey, "page=4&pageSize=5");
        Assert.Empty(empty.Callers.Items);
        Assert.Equal(child.Id, empty.EffectiveTargetId);
        Assert.Equal(4, empty.Callers.Page);

        // Without the administrator role the definition routes' filter refuses the read.
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workflows/{childKey}/callers");
        ApiTestAuth.Authorize(request, "reader", "reader");
        request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        using var forbidden = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

    }

    [Fact]
    public async Task Publishing_a_caller_checks_its_target_while_drafts_can_come_first()
    {
        var childKey = Key("child");
        var key = Key("parent");

        // A draft parent can exist before its child does.
        var draft = await PublishDraftAsync(Parent(key, childKey));
        using (var refused = await harness.SendAsync(HttpMethod.Post, $"/api/workflows/{draft.Id}/publish"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains(childKey, await refused.Content.ReadAsStringAsync());
        }

        await harness.PublishAsync(ImmediateChild(childKey));
        using var published = await harness.SendAsync(HttpMethod.Post, $"/api/workflows/{draft.Id}/publish");
        Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("idempotency")]
    [InlineData("requiredInput")]
    public async Task A_target_a_call_cannot_start_is_refused_at_publication(string flaw)
    {
        var childKey = Key("child");
        var child = ImmediateChild(childKey, startInputs: flaw == "requiredInput" ? [Input("amount")] : null);
        if (flaw == "roles")
        {
            child.FlowNodes.Single(node => node.Id == Start).Roles = ["requester"];
        }

        if (flaw == "idempotency")
        {
            child.FlowNodes.Single(node => node.Id == Start).Idempotency = new IdempotencyModel
            {
                HeaderName = "Idempotency-Key",
                Variable = "requestKey"
            };
        }

        await harness.PublishAsync(child);
        using var response = await harness.SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(Parent(Key("parent"), childKey), true));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_call_only_start_refuses_public_starts_and_accepts_a_call()
    {
        var childKey = Key("child");
        var model = ImmediateChild(childKey);
        model.FlowNodes.Single(node => node.Id == Start).CallOnly = true;
        var child = await harness.PublishAsync(model);
        Assert.Contains("\"callOnly\":true", System.Text.Json.JsonSerializer.Serialize(child.Definition));

        using (var byId = await harness.SendAsync(
                   HttpMethod.Post,
                   "/api/instances",
                   new StartInstanceRequest(child.Id, null, null, null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, byId.StatusCode);
            Assert.Contains("call activity", await byId.Content.ReadAsStringAsync());
        }

        using (var byKey = await harness.SendAsync(
                   HttpMethod.Post,
                   "/api/instances",
                   new StartInstanceRequest(null, childKey, null, null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, byKey.StatusCode);
        }

        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
    }

    [Fact]
    public async Task A_child_task_keeps_its_own_permissions_and_a_relation_grants_none()
    {
        var childKey = Key("child");
        var child = WaitingChild(childKey);
        child.FlowNodes.Single(node => node.Id == ChildTask).Roles = ["approver"];
        await harness.PublishAsync(child);
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var childId = (await harness.InvocationAsync(parentId)).ChildInstanceId!.Value;

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{childId}/flows/{ChildFlow}")
        {
            Content = JsonContent.Create(new { })
        };
        ApiTestAuth.Authorize(request, "outsider", "outsider");
        using var denied = await fixture.Client.SendAsync(request);
        Assert.False(denied.IsSuccessStatusCode);

        var read = new HttpRequestMessage(HttpMethod.Get, $"/api/instances/{parentId}/calls");
        ApiTestAuth.Authorize(read, "outsider", "outsider");
        using var calls = await fixture.Client.SendAsync(read);
        Assert.Equal(HttpStatusCode.OK, calls.StatusCode);
        var body = await calls.Content.ReadAsStringAsync();
        Assert.DoesNotContain("inputValues", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outputValues", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_calls_route_handles_missing_ids_and_anonymous_callers_and_pages_deterministically()
    {
        using (var missing = await harness.SendAsync(HttpMethod.Get, "/api/instances/999999999/calls"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using (var anonymous = await fixture.Client.GetAsync("/api/instances/1/calls"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var leaf = await harness.PublishAsync(ImmediateChild(Key("plain")));
        var plain = await harness.StartAsync(leaf.Id);
        var none = await harness.CallsAsync(plain);
        Assert.Empty(none.Items);
        Assert.Equal(0, none.TotalCount);
        Assert.Null((await harness.DetailAsync(plain)).Calls);
        Assert.Null((await harness.DetailAsync(plain)).ParentCall);
    }

    [Fact]
    public async Task An_outside_instance_pays_no_extra_query_for_calls_at_its_terminal_commit()
    {
        var leaf = await harness.PublishAsync(ImmediateChild(Key("plain")));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        var plain = await harness.StartAsync(leaf.Id);
        Assert.NotEqual(0, plain);
        Assert.DoesNotContain(
            fixture.CommandCounter.ReaderCommandTexts,
            text => text.Contains("call_activity_", StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<string>> VersionBlockersAsync(long instanceId, long targetId)
    {
        using var response = await harness.SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instanceId}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(targetId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<InstanceVersionChangePreviewDto>();
        return preview!.Blockers.Select(item => item.Code).ToArray();
    }

    private async Task<InstanceReactivationPreviewDto> ReactivationAsync(long instanceId)
    {
        using var response = await harness.SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}/reactivation");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InstanceReactivationPreviewDto>())!;
    }

    private async Task<CallersPreviewDto> CallersAsync(string workflowKey, string? query = null)
    {
        using var response = await harness.SendAsync(
            HttpMethod.Get,
            $"/api/workflows/{workflowKey}/callers" + (query is null ? string.Empty : "?" + query));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CallersPreviewDto>())!;
    }

    private async Task<WorkflowDetailDto> PublishDraftAsync(WorkflowModel definition)
    {
        using var response = await harness.SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(definition, false));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WorkflowDetailDto>())!;
    }

    private async Task AssertStatusAsync(HttpStatusCode expected, string path)
    {
        using var response = await harness.SendAsync(HttpMethod.Get, path);
        Assert.True(response.StatusCode == expected, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }
}
