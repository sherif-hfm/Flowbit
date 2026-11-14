using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed partial class AutomaticMultiInstanceCoordinatorTests
{
    [Fact]
    public async Task Attempt_defaults_are_fresh_while_staged_inputs_and_flow_info_stay_frozen()
    {
        var f = new Fixture();
        f.Item = f.Item with { StagedInput = null };
        f.Definition.FlowNodes[0].MultiInstance!.OutputVariables![0].DefaultValue = JsonSerializer.SerializeToElement("${sys.jobAttempt}");
        f.BodyFlowInfo = new([103], [new(103, SequenceFlowRuntimeView.Empty, new(7, null))]);
        f.Job = f.Job with { AttemptCount = 1 };
        var first = await f.Coordinator.CaptureItemContextAsync(1, 6, f.ActivationId, f.Fence,
            f.Definition, new Dictionary<string, JsonElement>(), DateTimeOffset.UnixEpoch, default);
        first.FlowInfo!.SetSummary(SequenceFlowRuntimeSummary.Empty(103));
        f.Job = f.Job with { AttemptCount = 2, LeaseGeneration = 2 };
        f.SharedValues["later"] = JsonSerializer.SerializeToElement(99);
        var second = await f.Coordinator.CaptureItemContextAsync(1, 6, f.ActivationId,
            f.Fence with { LeaseGeneration = 2 }, f.Definition, new Dictionary<string, JsonElement>(), DateTimeOffset.UtcNow, default);
        Assert.Equal(1, first.LocalVariables["local"].GetInt32());
        Assert.Equal(2, second.LocalVariables["local"].GetInt32());
        Assert.Equal(7, second.FlowInfo!.GetSummary(103).Traversals.Count);
        Assert.False(second.Values.ContainsKey("later"));
        Assert.Equal(1, f.StageSaves);
        Assert.Equal(1, f.SharedReads);
    }

    [Theory]
    [InlineData(100L)]
    [InlineData(null)]
    public async Task Shared_publication_links_the_retained_visit_and_tolerates_pruned_audit(long? visitId)
    {
        var f = new Fixture();
        f.Item = f.Item with { NodeExecutionId = visitId };
        await f.SaveCandidateAsync(7, shared: true);
        Assert.True(await f.PublishAsync());
        Assert.Equal(visitId, Assert.Single(f.PublishedWrites).NodeExecutionId);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.PublishAsync());
        Assert.Equal(1, f.Writes);
    }

    [Fact]
    public async Task Call_transport_stages_and_publishes_without_generic_item_job_or_message_acknowledgement()
    {
        var f = CallFixture();
        var fence = new AutomaticMultiInstanceItemFence(f.Fence, 12);
        var context = await f.Coordinator.CaptureItemContextAsync(1, 6, f.ActivationId, fence, f.Definition,
            new Dictionary<string, JsonElement>
            {
                ["sys.jobAttempt"] = JsonSerializer.SerializeToElement(99),
                ["sys.user"] = JsonSerializer.SerializeToElement("caller"),
                ["sys.claim.secret"] = JsonSerializer.SerializeToElement("private")
            }, DateTimeOffset.UtcNow, default);
        Assert.Equal("callActivity", context.Values["sys.user"].GetString());
        Assert.Empty(context.Values["sys.roles"].EnumerateArray());
        Assert.DoesNotContain("sys.jobAttempt", context.Values.Keys);
        Assert.DoesNotContain("sys.jobId", context.Values.Keys);
        Assert.DoesNotContain("sys.claim.secret", context.Values.Keys);
        Assert.DoesNotContain("private", f.Item.StagedInput!.Value.GetRawText());
        ResumeCall(f);
        var candidate = new AutomaticMultiInstanceCandidate(
            new Dictionary<string, JsonElement> { ["local"] = JsonSerializer.SerializeToElement(7) },
            new Dictionary<string, JsonElement> { ["sharedOutput"] = JsonSerializer.SerializeToElement(7) },
            new Dictionary<string, long> { ["sharedOutput"] = 1 }, new Dictionary<string, JsonElement>());
        Assert.True(await f.Coordinator.SaveCandidateAsync(1, 6, f.ActivationId, fence, f.Definition, candidate, default));
        Assert.False(await f.Coordinator.SaveCandidateAsync(1, 6, f.ActivationId, fence, f.Definition, candidate, default));
        Assert.True(await f.Coordinator.PublishCandidateAsync(1, 6, f.ActivationId, fence, f.Definition,
            new("caller", ["admin"], new Dictionary<string, string> { ["secret"] = "private" }),
            f.Template with { NodeType = "callActivity" }, default));
        Assert.Equal("callActivity", f.PublishedActor!.User);
        Assert.Empty(f.PublishedActor.Roles);
        Assert.Empty(f.PublishedActor.Claims);
        Assert.Equal(0, f.JobCompletions);
        Assert.Equal(1, f.Writes);
        Assert.Equal("multiInstanceComplete", Assert.Single(f.Enqueued).Kind);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.Coordinator.PublishCandidateAsync(1, 6,
            f.ActivationId, fence, f.Definition, new("caller", [], new Dictionary<string, string>()),
            f.Template with { NodeType = "callActivity" }, default));
        Assert.Equal(1, f.Writes);
    }

    [Theory]
    [InlineData("invocation")]
    [InlineData("body")]
    [InlineData("item")]
    [InlineData("activation")]
    [InlineData("lease")]
    [InlineData("message")]
    [InlineData("result")]
    [InlineData("wrapper")]
    public async Task Call_fence_rejects_stale_or_foreign_ownership(string mismatch)
    {
        var f = CallFixture();
        ResumeCall(f);
        switch (mismatch)
        {
            case "invocation": f.Invocation = f.Invocation! with { State = "completed" }; break;
            case "body": f.Invocation = f.Invocation! with { MultiInstanceExecutionId = 99 }; break;
            case "item": f.Invocation = f.Invocation! with { AutomaticMultiInstanceItemId = 99 }; break;
            case "activation": f.CurrentActivation = Guid.NewGuid(); break;
            case "lease": f.LeaseAlive = false; break;
            case "message": f.Job = f.Job with { InstanceId = 99 }; break;
            case "result": f.CallResult = f.CallResult! with { InvocationId = 99 }; break;
            case "wrapper": f.Job = f.Job with { Kind = "multiInstanceItem" }; break;
        }
        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.Coordinator.SaveStageAsync(1, 6, f.ActivationId,
            new AutomaticMultiInstanceItemFence(f.Fence, 12), JsonSerializer.SerializeToElement(new { }), default));
        Assert.Equal(0, f.StageSaves);
    }

    private static Fixture CallFixture()
    {
        var f = new Fixture();
        f.Body = f.Body with { ActivityKind = "callActivity" };
        f.Item = f.Item with { StagedInput = null };
        f.Definition.FlowNodes[0].Type = "callActivity";
        f.Definition.FlowNodes[0].CallActivity = new() { OutputMappings = [new() { SourceVariable = "value", Variable = "sharedOutput", DataType = "number" }] };
        f.Job = f.Job with { Kind = "callStart", NodeType = "callActivity", CallInvocationId = 12,
            MultiInstanceExecutionId = null, AutomaticMultiInstanceItemId = null };
        f.Invocation = JsonSerializer.SerializeToElement(new
        {
            Id = 12L, ParentInstanceId = 2L, ParentTokenId = 3L, ParentDefinitionId = 4L, ParentNodeId = 5,
            f.ActivationId, MultiInstanceExecutionId = 1L, AutomaticMultiInstanceItemId = 6L, ItemIndex = 0, State = "queued"
        }).Deserialize<CallInvocationRecord>();
        return f;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Call_failure_captures_the_boundary_without_acknowledging_the_adapter_transport(bool delivered)
    {
        var f = CallFixture();
        if (delivered) ResumeCall(f);
        var lifecycle = new CallLifecycle();
        Assert.True(await f.Coordinator.CaptureItemFailureAsync(1, 6, f.ActivationId,
            new AutomaticMultiInstanceItemFence(f.Fence, 12), new(0, "call_failed", 6),
            new("caller", [], new Dictionary<string, string>()), f.Template with { NodeType = "callActivity" }, lifecycle, default));
        Assert.Equal("failed", f.Item.Status);
        Assert.Equal(0, f.JobCompletions);
        Assert.Equal(new[] { "close", "abandon" }, lifecycle.Events);
        Assert.Equal("multiInstanceComplete", Assert.Single(f.Enqueued).Kind);
        // The failed item's own invocation settles first, so abandoning the body's calls
        // never sends a cancel message to its finished child.
        Assert.Equal(new[] { delivered ? "running" : "queued" }, f.Settled);
        Assert.Equal(CallInvocationStates.Failed, f.Invocation!.State);
    }

    private sealed class CallLifecycle : IAutomaticMultiInstanceLifecycle
    {
        public List<string> Events { get; } = [];
        public Task InstallHostTimersAsync(AutomaticMultiInstanceRecord body, CancellationToken ct) => throw new NotSupportedException();
        public Task DrainVariableNotificationsAsync(AutomaticMultiInstanceRecord body, CancellationToken ct) => throw new NotSupportedException();
        public Task CloseHostSubscriptionsAsync(AutomaticMultiInstanceRecord body, CancellationToken ct) { Events.Add("close"); return Task.CompletedTask; }
        public Task AbandonItemCallsAsync(AutomaticMultiInstanceRecord body, CancellationToken ct) { Events.Add("abandon"); return Task.CompletedTask; }
        public Task ContinueAsync(AutomaticMultiInstanceRecord body, AutomaticMultiInstanceCompletion completion, CancellationToken ct) => throw new NotSupportedException();
    }

    private static void ResumeCall(Fixture f)
    {
        f.Invocation = f.Invocation! with { State = "running", ChildInstanceId = 20, ChildDefinitionId = 21 };
        f.Job = f.Job with { Kind = "callResume", InstanceId = 20, TokenId = 22, WorkflowDefinitionId = 21,
            ActivationId = Guid.NewGuid(), NodeId = 99, NodeType = "endEvent" };
        f.CallResult = new(20, 12, "completed", new(new Dictionary<string, JsonElement>(), []), null, null, null, DateTimeOffset.UtcNow);
    }
}
