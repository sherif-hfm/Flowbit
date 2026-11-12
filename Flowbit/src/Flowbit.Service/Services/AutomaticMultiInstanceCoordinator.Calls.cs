using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed partial class AutomaticMultiInstanceCoordinator
{
    // Invocation writes are serialized by the parent's instance/token locks.
    // Read the child-owned immutable result without acquiring the child instance.
    private async Task RequireCallItemFenceAsync(AutomaticMultiInstanceRecord body,
        AutomaticMultiInstanceItemRecord? item, WorkflowJobRecord? job, long invocationId,
        WorkflowJobFence fence, bool requireDelivery, CancellationToken ct)
    {
        var invocation = callActivities is null ? null : await callActivities.GetInvocationAsync(invocationId, ct);
        var token = runtime is null ? null : await runtime.GetExecutionTokenAsync(body.TokenId, false, ct);
        var instance = runtime is null ? null : await runtime.GetInstanceAsync(body.InstanceId, ct);
        // ItemIndex is optional read-side enrichment. Ownership comes from the
        // stable item FK; the locked item supplies its authoritative index.
        if (body.ActivityKind != BpmnFlowNodeTypes.CallActivity || item is null || item.Status != "active"
            || item.MultiInstanceExecutionId != body.Id || item.InstanceId != body.InstanceId
            || invocation is null || CallInvocationStates.IsSettled(invocation.State)
            || invocation.ParentInstanceId != body.InstanceId || invocation.ParentTokenId != body.TokenId
            || invocation.ParentDefinitionId != body.WorkflowDefinitionId || invocation.ParentNodeId != body.NodeId
            || invocation.ActivationId != body.ActivationId || invocation.MultiInstanceExecutionId != body.Id
            || invocation.AutomaticMultiInstanceItemId != item.Id
            || instance?.Status != WorkflowInstanceStatuses.Running || instance.WorkflowDefinitionId != body.WorkflowDefinitionId
            || token?.Status != ExecutionTokenRecordStatuses.Active || token.InstanceId != body.InstanceId
            || token.NodeId != body.NodeId || token.ActivationId != body.ActivationId
            || job is null || job.CallInvocationId != invocation.Id || !MatchesLease(job, fence)
            || !await jobs.IsLeaseAliveAsync(fence, ct))
            throw new WorkflowConflictException("The automatic call invocation or message fence is stale.");

        if (job.Kind == WorkflowJobKinds.CallStart && !requireDelivery
            && invocation.State == CallInvocationStates.Queued && invocation.ChildInstanceId is null
            && job.InstanceId == body.InstanceId && job.TokenId == body.TokenId
            && job.WorkflowDefinitionId == body.WorkflowDefinitionId && job.ActivationId == body.ActivationId
            && job.NodeId == body.NodeId && job.NodeType == body.ActivityKind)
            return;

        if (job.Kind == WorkflowJobKinds.CallResume && invocation.State == CallInvocationStates.Running
            && invocation.ChildInstanceId == job.InstanceId && invocation.ChildDefinitionId == job.WorkflowDefinitionId
            && invocation.ChildInstanceId is { } childId
            && await callActivities!.GetResultAsync(childId, ct) is { } result
            && result.InvocationId == invocation.Id && result.ChildInstanceId == childId)
            return;

        throw new WorkflowConflictException("The automatic call transport does not own this item phase.");
    }
}
