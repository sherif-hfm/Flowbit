using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed partial class AutomaticMultiInstanceCoordinator
{
    /// <summary>
    /// Admits call items from the reserved dispatch wake. Each admitted item gets a reserved
    /// invocation with no inputs and its <c>callStart</c>, which stages and starts the child;
    /// admission itself reads and locks no shared row. Reaching the root's open-call limit is
    /// back-pressure, not a failure: the pending items stay pending with no invocation, and
    /// this job parks with the core's bounded backoff. Only a body with nothing in flight
    /// whose backoff has reached its maximum opens a <c>call_capacity_exceeded</c> incident,
    /// because nothing of its own would free capacity.
    /// </summary>
    private async Task<AutomaticMultiInstanceDispatchResult> DispatchCallsAsync(AutomaticMultiInstanceRecord body,
        WorkflowJobRecord job, WorkflowJobFence fence, AutomaticMultiInstanceProgressRecord progress,
        AutomaticCallAdmission admission, long availableJobs, CancellationToken cancellationToken)
    {
        if (callActivities is null)
            throw new InvalidOperationException("Call admission requires call-activity storage.");
        RequireCallAdmission(body, admission);
        // The limit is a soft threshold shared by concurrent parents: counted, never locked.
        var reserved = await callActivities.CountReservedUnsettledAsync(admission.InvocationTemplate.RootInstanceId, cancellationToken);
        var rootAvailable = Math.Max(0, admission.MaxOpenInvocationsPerRoot - reserved);
        if (rootAvailable == 0)
        {
            if (!await repository.SetAdmissionBlockedReasonAsync(body.Id, body.ActivationId, "callCapacity", cancellationToken))
                throw new WorkflowConflictException("The automatic body no longer owns its dispatch reservation.");
            var delay = DispatchBackoff(fence.LeaseGeneration);
            if (delay == TimeSpan.FromSeconds(60) && progress.ActiveCount == 0)
            {
                if (await jobs.OpenIncidentAsync(fence, CallFailureCodes.CapacityExceeded,
                        "Automatic call admission is blocked by the root's open-call limit with no item in flight.",
                        $"Root instance #{admission.InvocationTemplate.RootInstanceId} has {reserved} open calls; the limit is {admission.MaxOpenInvocationsPerRoot}. Let other calls under this root finish, or raise WorkflowCallActivities:MaxOpenInvocationsPerRoot, then retry admission.",
                        cancellationToken) is null)
                    throw new WorkflowConflictException("The automatic dispatch lease was lost before its call-capacity incident.");
                return new("callCapacityIncident", 0, null);
            }
            if (job.Status != WorkflowJobStatuses.ResultReady
                && !await jobs.SaveResultReadyAsync(fence,
                    new WorkflowJobResultRecord(JsonSerializer.SerializeToElement(new { kind = WorkflowJobKinds.MultiInstanceDispatch, waitingFor = "callCapacity" }), null, null, null),
                    cancellationToken))
                throw new WorkflowConflictException("The automatic dispatch lease was lost before its wait was saved.");
            if (!await jobs.ReleaseResultReadyLeaseAsync(fence, delay, cancellationToken))
                throw new WorkflowConflictException("The automatic dispatch lease was lost before its durable wake was saved.");
            return new("callCapacity", 0, delay);
        }

        var window = Math.Min(progress.PendingCount, Math.Max(0, body.EffectiveConcurrency - progress.ActiveCount));
        var jobsAvailable = Math.Max(0, availableJobs);
        var count = (int)Math.Min(window, Math.Min(rootAvailable, jobsAvailable));
        // A limit that admits only part of the window keeps this job waiting, as a full one
        // does: nothing else retries admission until one of this body's own items settles,
        // and a child's human work can take days.
        var heldBack = count < window
            ? rootAvailable <= jobsAvailable ? "callCapacity" : "queueCapacity"
            : null;
        if (heldBack is null) await CompleteDispatchAsync(fence, cancellationToken);
        if (!await repository.SetAdmissionBlockedReasonAsync(body.Id, body.ActivationId, heldBack, cancellationToken))
            throw new WorkflowConflictException("The automatic body no longer owns its admission window.");
        if (count == 0) return new("admitted", 0, null);
        var admitted = await repository.AdmitAsync(body.Id, body.ActivationId, count, cancellationToken);
        foreach (var item in admitted)
        {
            // One stable invocation per item: the unique item key refuses a second one.
            var invocation = await callActivities.AddInvocationAsync(admission.InvocationTemplate with
            {
                ParentNodeExecutionId = item.NodeExecutionId,
                Inputs = null,
                CapacityReserved = true,
                FailureCode = null,
                FailureDescription = null,
                MultiInstanceExecutionId = body.Id,
                AutomaticMultiInstanceItemId = item.Id
            }, cancellationToken);
            await jobs.EnqueueAsync(admission.CallStartTemplate with
            {
                Kind = WorkflowJobKinds.CallStart, Phase = WorkflowJobKinds.CallStart,
                QueueClass = WorkflowJobClasses.Control, CallInvocationId = invocation.Id,
                MultiInstanceExecutionId = body.Id, AutomaticMultiInstanceItemId = null,
                MultiInstanceControlGeneration = null
            }, cancellationToken);
        }
        if (heldBack is null) return new("admitted", admitted.Count, null);
        // Items are now in flight, so this wait never opens the nothing-in-flight incident.
        // Each item publication replaces this job with a fresh dispatch.
        var retryAfter = DispatchBackoff(fence.LeaseGeneration);
        if (job.Status != WorkflowJobStatuses.ResultReady
            && !await jobs.SaveResultReadyAsync(fence,
                new WorkflowJobResultRecord(JsonSerializer.SerializeToElement(new { kind = WorkflowJobKinds.MultiInstanceDispatch, waitingFor = heldBack }), null, null, null),
                cancellationToken))
            throw new WorkflowConflictException("The automatic dispatch lease was lost before its wait was saved.");
        if (!await jobs.ReleaseResultReadyLeaseAsync(fence, retryAfter, cancellationToken))
            throw new WorkflowConflictException("The automatic dispatch lease was lost before its durable wake was saved.");
        return new(heldBack, admitted.Count, retryAfter);
    }

    /// <summary>
    /// Fences a call item phase and returns its body and item: a <c>callStart</c> that owns a
    /// queued invocation with no child, or (with <paramref name="requireDelivery"/>) the
    /// <c>callResume</c> of a running invocation whose result is saved.
    /// </summary>
    public async Task<(AutomaticMultiInstanceRecord Body, AutomaticMultiInstanceItemRecord Item)> RequireCallItemAsync(
        long executionId, long itemId, Guid activationId, AutomaticMultiInstanceItemFence fence, bool requireDelivery,
        CancellationToken cancellationToken)
    {
        if (fence.CallInvocationId is null)
            throw new ArgumentException("A call item fence names its invocation.", nameof(fence));
        var (body, item, _) = await RequireItemFenceAsync(executionId, itemId, activationId, fence, cancellationToken, requireDelivery);
        return (body, item);
    }

    private static void RequireCallAdmission(AutomaticMultiInstanceRecord body, AutomaticCallAdmission admission)
    {
        var invocation = admission.InvocationTemplate;
        var start = admission.CallStartTemplate;
        if (body.ActivityKind != BpmnFlowNodeTypes.CallActivity || body.PinnedChildDefinitionId is null
            || invocation.ParentInstanceId != body.InstanceId || invocation.ParentTokenId != body.TokenId
            || invocation.ActivationId != body.ActivationId || invocation.ParentNodeId != body.NodeId
            || invocation.ParentDefinitionId != body.WorkflowDefinitionId
            || admission.MaxOpenInvocationsPerRoot < 1)
            throw new WorkflowConflictException("The automatic call admission does not own this body.");
        RequireTemplate(body, start);
    }

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

/// <summary>
/// What admitting a call item needs from the engine: the invocation every item copies (its
/// parent identity, root, depth, target family, and contract), the <c>callStart</c> template,
/// and the root's open-call limit.
/// </summary>
public sealed record AutomaticCallAdmission(
    CallInvocationCreateRecord InvocationTemplate,
    WorkflowJobCreateRecord CallStartTemplate,
    int MaxOpenInvocationsPerRoot);
