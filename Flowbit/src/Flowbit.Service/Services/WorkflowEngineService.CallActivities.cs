using System.Text.Json;
using Microsoft.Extensions.Logging;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

// Durable call activity lifecycle. A transaction locks the runtime rows of at most one
// existing instance; the only other instance it may touch is a child it is creating,
// which no other transaction can see yet. Jobs that cross instances are messages: each
// belongs to its sender's rows, and only processing it locks the receiver.
public sealed partial class WorkflowEngineService
{
    private const string CallActivityActorName = "callActivity";

    private sealed record CallFailure(string Code, string Description, string? ChildFaultCode = null)
    {
        /// <summary>The bounded scalar text an attached boundary's error variable receives.</summary>
        public string Diagnostic => $"{Code}: {Description}";
    }

    private ICallActivityRepository CallActivities =>
        callActivities
        ?? throw new InvalidOperationException("Call-activity storage is not configured.");

    private CallActivityOptions CallLimits => callActivityOptions ?? new CallActivityOptions();

    // The child and the parent's continuation never inherit a caller's identity, so a
    // child's approver or message client cannot reach the parent's sys.* context.
    private static ActorContext CallActivityActor() =>
        new(
            CallActivityActorName,
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Starts a visit to a call. It returns null when the token now rests on the call, and
    /// a failed outcome when the call failed before admission and a boundary catches it.
    /// </summary>
    private async Task<TaskExecutionOutcome?> EnterCallActivityAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        IReadOnlyDictionary<string, JsonElement> stored,
        CancellationToken cancellationToken)
    {
        // Routing can visit a token that already rests here; a visit owns one invocation.
        if (token.WaitState is not null
            || await CallActivities.GetForActivationAsync(token.Id, token.ActivationId, cancellationToken) is not null)
        {
            return null;
        }

        EnsureTokenCanEnterWait(token);
        var contract = node.CallActivity
            ?? throw new WorkflowDomainException($"Call activity #{node.Id} has no callActivity configuration.");
        var rootInstanceId = instance.CallRootInstanceId ?? instance.Id;
        var childDepth = instance.CallDepth + 1;
        var inputs = CaptureCallInputs(contract, stored);
        var failure = inputs.Failure
            ?? await CheckCallAdmissionAsync(rootInstanceId, childDepth, cancellationToken);

        var invocation = await CallActivities.AddInvocationAsync(
            new CallInvocationCreateRecord(
                instance.Id,
                token.Id,
                token.ActivationId,
                node.Id,
                token.CurrentNodeExecutionId,
                rootInstanceId,
                childDepth,
                instance.WorkflowDefinitionId,
                contract.WorkflowKey,
                contract,
                inputs.Snapshot,
                CapacityReserved: failure is null,
                actor.User,
                failure?.Code,
                failure?.Description),
            cancellationToken);

        if (failure is not null
            && SelectErrorBoundary(definition, node.Id, TaskExecutionOutcome.Fail(failure.Diagnostic)).Boundary is not null)
        {
            await CallActivities.SettleAsync(
                invocation.Id,
                CallInvocationStates.Failed,
                failure.Code,
                failure.Description,
                null,
                cancellationToken);
            await AddCallHistoryAsync(instance.Id, token.Id, node.Id, InstanceHistoryNotes.CallFailed, actor, invocation, failure, cancellationToken);
            return TaskExecutionOutcome.Fail(failure.Diagnostic);
        }

        // The job carries the fixed actor because BuildJobCreate snapshots the actor it is
        // given; a rejected entry needs an incident, and it must not take a reservation.
        var job = await EnqueueInstanceJobAsync(
            BuildJobCreate(
                instance,
                token,
                node,
                WorkflowJobKinds.CallStart,
                WorkflowJobKinds.CallStart,
                CallActivityActor(),
                dueAt: timeProvider.GetUtcNow()) with
            {
                CallInvocationId = invocation.Id
            },
            cancellationToken,
            token,
            countAutomaticActivation: true,
            blockedBy: failure);
        if (!await runtime.SetExecutionTokenWaitAsync(
                token.Id,
                token.ActivationId,
                ExecutionTokenWaitStates.CallStart,
                job.Id,
                null,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The execution token changed while its call-start job was being created.");
        }

        await AddCallHistoryAsync(instance.Id, token.Id, node.Id, InstanceHistoryNotes.CallQueued, actor, invocation, failure, cancellationToken);
        logger.LogInformation(
            "Call invocation {InvocationId} queued on instance {InstanceId} (root {RootInstanceId}, depth {Depth}, reserved {Reserved}, failure {FailureCode}).",
            invocation.Id,
            instance.Id,
            rootInstanceId,
            childDepth,
            failure is null,
            failure?.Code);
        return null;
    }

    // Counts see every reservation this transaction made, because each is written at once.
    private async Task<CallFailure?> CheckCallAdmissionAsync(
        long rootInstanceId,
        int childDepth,
        CancellationToken cancellationToken)
    {
        if (childDepth > CallLimits.MaxDepth)
        {
            return new CallFailure(
                CallFailureCodes.DepthExceeded,
                $"Call depth {childDepth} exceeds the limit of {CallLimits.MaxDepth}.");
        }

        var open = await CallActivities.CountReservedUnsettledAsync(rootInstanceId, cancellationToken);
        return open >= CallLimits.MaxOpenInvocationsPerRoot
            ? new CallFailure(
                CallFailureCodes.CapacityExceeded,
                $"Root instance #{rootInstanceId} already has {open} open calls; the limit is {CallLimits.MaxOpenInvocationsPerRoot}.")
            : null;
    }

    private static (CallValueSnapshot? Snapshot, CallFailure? Failure) CaptureCallInputs(
        CallActivityModel contract,
        IReadOnlyDictionary<string, JsonElement> stored)
    {
        return CaptureMappedValues(
            contract.InputMappings,
            stored,
            CallFailureCodes.InputInvalid,
            "input");
    }

    /// <summary>
    /// Copies whole variables through mappings. A required source must be present and not
    /// null; an optional missing source is recorded as missing, and a present null is
    /// passed on for the destination's own rules to judge.
    /// </summary>
    private static (CallValueSnapshot? Snapshot, CallFailure? Failure) CaptureMappedValues(
        IReadOnlyList<CallVariableMappingModel> mappings,
        IReadOnlyDictionary<string, JsonElement> source,
        string failureCode,
        string direction)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var mapping in mappings)
        {
            if (!source.TryGetValue(mapping.SourceVariable, out var value)
                || value.ValueKind == JsonValueKind.Undefined)
            {
                if (mapping.Required)
                {
                    return (null, new CallFailure(
                        failureCode,
                        $"Required {direction} source '{mapping.SourceVariable}' has no value."));
                }

                missing.Add(mapping.Variable);
                continue;
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                if (mapping.Required)
                {
                    return (null, new CallFailure(
                        failureCode,
                        $"Required {direction} source '{mapping.SourceVariable}' is null."));
                }
            }
            else if (!TypedOutputValueValidator.IsValid(value, mapping.DataType, mapping.IsArray))
            {
                return (null, new CallFailure(
                    failureCode,
                    $"{char.ToUpperInvariant(direction[0])}{direction[1..]} source '{mapping.SourceVariable}' is not {TypedOutputValueValidator.DescribeExpected(mapping.DataType, mapping.IsArray)}."));
            }

            values[mapping.Variable] = value.Clone();
        }

        var snapshot = new CallValueSnapshot(values, missing);
        var size = JsonSerializer.SerializeToUtf8Bytes(values).Length;
        return size > CallActivityConstraints.MaxSnapshotBytes
            ? (null, new CallFailure(
                failureCode,
                $"The {direction} snapshot is {size} bytes; the limit is {CallActivityConstraints.MaxSnapshotBytes}."))
            : (snapshot, null);
    }

    /// <summary>
    /// Creates and routes the child in one transaction with the parent's locks. A separate
    /// dispatch job would add a created-but-unrouted child that cancellation, variable
    /// edits, and the diagram would all have to handle.
    /// </summary>
    private async Task ProcessCallStartJobAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CancellationToken cancellationToken)
    {
        var invocationId = RequireInvocationId(lease.Job);
        var seed = await CallActivities.GetInvocationAsync(invocationId, cancellationToken)
            ?? throw new WorkflowJobInvariantException($"Call invocation #{invocationId} no longer exists.");

        CallFailure? failure;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // The family lock comes first, the order version changes and timer starts
            // already use; a routing transaction must never acquire one later.
            await definitions.LockFamilyForStartAsync(seed.TargetWorkflowKey, cancellationToken);
            var (instance, token, job) = await LockCallStartAsync(lease, fence, cancellationToken);
            var invocation = await RequireQueuedInvocationAsync(invocationId, token, cancellationToken);

            failure = await TryCreateCallChildAsync(instance, token, invocation, cancellationToken);
            if (failure is null)
            {
                if (!await runtime.ClearExecutionTokenWaitAsync(
                        token.Id,
                        token.ActivationId,
                        ExecutionTokenWaitStates.CallStart,
                        job.Id,
                        null,
                        cancellationToken))
                {
                    throw new WorkflowConflictException(
                        "The call-start job no longer owns the token wait.");
                }

                var linked = await CallActivities.GetInvocationAsync(invocationId, cancellationToken)
                    ?? invocation;
                await AddCallHistoryAsync(
                    instance.Id,
                    token.Id,
                    invocation.ParentNodeId,
                    InstanceHistoryNotes.CallChildStarted,
                    CallActivityActor(),
                    linked,
                    null,
                    cancellationToken);
                if (!await jobs.CompleteAsync(fence, cancellationToken))
                {
                    throw new WorkflowConflictException(
                        "The call-start job lease was lost before completion.");
                }

                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                var queueAge = timeProvider.GetUtcNow() - lease.Job.CreatedAt;
                CallActivityRuntimeTelemetry.RecordStartQueueAge(queueAge);
                logger.LogInformation(
                    "Call invocation {InvocationId} started child instance {ChildInstanceId} (definition {ChildDefinitionId}) after {QueueSeconds:F1}s in the queue.",
                    invocationId,
                    linked.ChildInstanceId,
                    linked.ChildDefinitionId,
                    queueAge.TotalSeconds);
                return;
            }
        }

        // Nothing from the failed attempt persists, including the child.
        unitOfWork.DiscardChanges();
        await ApplyCallStartFailureAsync(lease, fence, invocationId, failure, cancellationToken);
    }

    private async Task<(WorkflowInstanceRecord Instance, ExecutionTokenRecord Token, WorkflowJobRecord Job)> LockCallStartAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CancellationToken cancellationToken)
    {
        var instance = await GetInstanceForJobUpdateAsync(lease.Job.InstanceId!.Value, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
        var token = await runtime.GetExecutionTokenAsync(lease.Job.TokenId!.Value, true, cancellationToken);
        var job = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow job no longer exists.");
        EnsureJobFence(job, fence);
        return (instance, RequireFencedToken(instance, job, token), job);
    }

    private async Task<CallInvocationRecord> RequireQueuedInvocationAsync(
        long invocationId,
        ExecutionTokenRecord token,
        CancellationToken cancellationToken)
    {
        var invocation = await CallActivities.GetInvocationAsync(invocationId, cancellationToken)
            ?? throw new WorkflowJobInvariantException($"Call invocation #{invocationId} no longer exists.");
        if (invocation.State != CallInvocationStates.Queued)
        {
            throw new WorkflowConflictException(
                $"Call invocation #{invocationId} is no longer queued.");
        }

        if (invocation.ParentTokenId != token.Id
            || invocation.ActivationId != token.ActivationId
            || invocation.ParentNodeId != token.NodeId)
        {
            throw new WorkflowJobInvariantException(
                $"Call invocation #{invocationId} does not match its waiting token.");
        }

        return invocation;
    }

    private async Task<CallFailure?> TryCreateCallChildAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        CallInvocationRecord invocation,
        CancellationToken cancellationToken)
    {
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, invocation.ParentNodeId);
        var contract = invocation.Contract
            ?? throw new WorkflowJobInvariantException(
                $"Call invocation #{invocation.Id} has no captured contract.");

        var inputs = invocation.Inputs;
        if (inputs is null)
        {
            // A retry after a failed entry still needs its inputs, and the cause may have
            // been corrected since.
            var stored = await LoadInstanceVariablesAsync(instance.Id, cancellationToken);
            var captured = CaptureCallInputs(contract, stored);
            if (captured.Failure is not null)
            {
                return captured.Failure;
            }

            inputs = captured.Snapshot!;
            await CallActivities.SetInputsAsync(invocation.Id, inputs, cancellationToken);
        }

        // An admitted retry reuses its slot; it must never compare itself to the limit.
        if (!invocation.CapacityReserved)
        {
            var admission = await CheckCallAdmissionAsync(
                invocation.RootInstanceId,
                invocation.ChildDepth,
                cancellationToken);
            if (admission is not null)
            {
                return admission;
            }

            if (!await CallActivities.ReserveAsync(invocation.Id, cancellationToken))
            {
                throw new WorkflowConflictException(
                    $"Call invocation #{invocation.Id} changed while it was being admitted.");
            }
        }

        // The default can have changed since the parent was published. A failed check
        // fails this invocation; it never selects another version or drops a mapping.
        var target = await definitions.GetDefaultByWorkflowKeyAsync(invocation.TargetWorkflowKey, cancellationToken);
        var problems = CallTargetReadiness.FindCallTargetProblems(workflow.Definition, node, target?.Definition);
        if (problems.Count > 0)
        {
            return new CallFailure(
                problems[0].Code,
                string.Join(" ", problems.Select(problem => problem.Message)));
        }

        var start = GetFlowNode(target!.Definition, target.Definition.InitialEventId!.Value);
        var values = inputs.Values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        try
        {
            ValidateVariableValues(start.Variables, values);
        }
        catch (WorkflowDomainException error)
        {
            return new CallFailure(CallFailureCodes.InputInvalid, error.Message);
        }

        try
        {
            _ = await StartInstanceCoreAsync(
                null,
                invocation.TargetWorkflowKey,
                CallActivityActor(),
                start.Id,
                values,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                cancellationToken,
                joinAmbientTransaction: true,
                forceDurableActivities: true,
                callLink: new CallChildLinkRecord(
                    invocation.Id,
                    invocation.RootInstanceId,
                    invocation.ChildDepth));
            return null;
        }
        catch (BusinessKeyConflictException error)
        {
            return new CallFailure(
                CallFailureCodes.BusinessKeyConflict,
                $"{error.Message} It is owned by instance #{error.ExistingInstanceId}, which a call never attaches to.");
        }
        catch (WorkflowDomainException error)
        {
            return new CallFailure(CallFailureCodes.StartFailed, error.Message);
        }
    }

    private async Task ApplyCallStartFailureAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        long invocationId,
        CallFailure failure,
        CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var (instance, token, _) = await LockCallStartAsync(lease, fence, cancellationToken);
        var invocation = await RequireQueuedInvocationAsync(invocationId, token, cancellationToken);
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, invocation.ParentNodeId);
        var outcome = TaskExecutionOutcome.Fail(failure.Diagnostic);
        var actor = CallActivityActor();
        if (SelectErrorBoundary(workflow.Definition, node.Id, outcome).Boundary is null)
        {
            await CallActivities.RecordBlockedAsync(
                invocationId,
                failure.Code,
                failure.Description,
                cancellationToken);
            if (await jobs.OpenIncidentAsync(
                    fence,
                    failure.Code,
                    $"Call activity #{node.Id} could not start its child.",
                    LimitJobFailure(failure.Description),
                    cancellationToken) is null)
            {
                throw new WorkflowConflictException(
                    "The call-start job lease was lost before its incident was created.");
            }

            WorkflowJobRuntimeTelemetry.RecordIncident();
        }
        else
        {
            await CallActivities.SettleAsync(
                invocationId,
                CallInvocationStates.Failed,
                failure.Code,
                failure.Description,
                null,
                cancellationToken);
            await AddCallHistoryAsync(instance.Id, token.Id, node.Id, InstanceHistoryNotes.CallFailed, actor, invocation, failure, cancellationToken);
            if (!await runtime.ClearExecutionTokenWaitAsync(
                    token.Id,
                    token.ActivationId,
                    ExecutionTokenWaitStates.CallStart,
                    lease.Job.Id,
                    null,
                    cancellationToken))
            {
                throw new WorkflowConflictException(
                    "The call-start job no longer owns the token wait.");
            }

            await ContinueAfterCallAsync(
                instance,
                token,
                workflow.Definition,
                actor,
                outcome,
                WorkflowJobKinds.CallStart,
                null,
                cancellationToken);
            if (!await jobs.CompleteAsync(fence, cancellationToken))
            {
                throw new WorkflowConflictException(
                    "The call-start job lease was lost before completion.");
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Call invocation {InvocationId} failed before its child existed: {FailureCode}.",
            invocationId,
            failure.Code);
    }

    /// <summary>
    /// Sets a terminal status and tells the parent's call about it. Every path that ends
    /// an instance comes through here, because a token reaching an end event says nothing
    /// about the rest of the instance.
    /// </summary>
    private async Task SetInstanceTerminalStatusAsync(
        long instanceId,
        string status,
        CancellationToken cancellationToken)
    {
        await runtime.SetInstanceStatusAsync(instanceId, status, cancellationToken);
        if (callActivities is null
            || await runtime.GetCallIdentityAsync(instanceId, cancellationToken) is not { RootInstanceId: not null })
        {
            return;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var invocation = await callActivities.GetByChildAsync(instanceId, cancellationToken)
            ?? throw new WorkflowCallChainException(instanceId, "it has no incoming invocation.");

        // A settled invocation was abandoned by its parent, so nothing is left to deliver.
        if (CallInvocationStates.IsSettled(invocation.State))
        {
            return;
        }

        var child = await runtime.GetInstanceAsync(instanceId, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow instance disappeared at its terminal commit.");
        var token = await runtime.GetExecutionTokenAsync(child.ActiveTokenId, false, cancellationToken)
            ?? throw new WorkflowConflictException("The execution token disappeared at its terminal commit.");
        var result = await CaptureCallResultAsync(invocation, child, status, cancellationToken);
        await callActivities.AddResultAsync(result, cancellationToken);

        // This goes on the child's own rows, without the parent's job cap, so a busy
        // parent cannot roll back a child's completion.
        await jobs.EnqueueAsync(
            BuildCallMessageJob(
                child.Id,
                child.WorkflowDefinitionId,
                child.WorkflowKey,
                token,
                WorkflowJobKinds.CallResume,
                invocation.Id),
            cancellationToken);
        logger.LogInformation(
            "Call invocation {InvocationId} captured child instance {ChildInstanceId} outcome {Outcome} ({FailureCode}).",
            invocation.Id,
            child.Id,
            result.Outcome,
            result.FailureCode);
    }

    private async Task<CallResultRecord> CaptureCallResultAsync(
        CallInvocationRecord invocation,
        WorkflowInstanceRecord child,
        string status,
        CancellationToken cancellationToken)
    {
        var capturedAt = timeProvider.GetUtcNow();
        if (status == WorkflowInstanceStatuses.Faulted)
        {
            var reason = child.FaultDescription ?? child.FaultCode ?? "the child instance faulted";
            return new CallResultRecord(
                child.Id,
                invocation.Id,
                CallResultOutcomes.Faulted,
                null,
                CallFailureCodes.ChildFaulted,
                $"Child instance #{child.Id} faulted: {reason}",
                child.FaultCode,
                capturedAt);
        }

        if (status == WorkflowInstanceStatuses.Cancelled)
        {
            return new CallResultRecord(
                child.Id,
                invocation.Id,
                CallResultOutcomes.Cancelled,
                null,
                CallFailureCodes.ChildCancelled,
                $"Child instance #{child.Id} was cancelled.",
                null,
                capturedAt);
        }

        // Only the mapped values are kept, never the child's other variables. A bad source
        // fails the call here; it must not roll back the child's own valid completion.
        var stored = await LoadInstanceVariablesAsync(child.Id, cancellationToken);
        var captured = CaptureMappedValues(
            invocation.Contract?.OutputMappings ?? [],
            stored,
            CallFailureCodes.OutputInvalid,
            "output");
        return captured.Failure is { } failure
            ? new CallResultRecord(
                child.Id,
                invocation.Id,
                CallResultOutcomes.Faulted,
                null,
                failure.Code,
                failure.Description,
                null,
                capturedAt)
            : new CallResultRecord(
                child.Id,
                invocation.Id,
                CallResultOutcomes.Completed,
                captured.Snapshot,
                null,
                null,
                null,
                capturedAt);
    }

    /// <summary>
    /// Delivers a child's frozen result to its parent. A settled invocation or a finished
    /// parent completes the job as stale, without writing variables or routing.
    /// </summary>
    private async Task ProcessCallResumeJobAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CancellationToken cancellationToken)
    {
        var invocationId = RequireInvocationId(lease.Job);
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var seed = await CallActivities.GetInvocationAsync(invocationId, cancellationToken)
            ?? throw new WorkflowJobInvariantException($"Call invocation #{invocationId} no longer exists.");
        var instance = await GetInstanceForJobUpdateAsync(seed.ParentInstanceId, cancellationToken)
            ?? throw new WorkflowJobInvariantException($"The parent of call invocation #{invocationId} no longer exists.");
        var token = await runtime.GetExecutionTokenAsync(seed.ParentTokenId, true, cancellationToken);
        var job = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow job no longer exists.");
        EnsureJobFence(job, fence);

        var invocation = await CallActivities.GetInvocationAsync(invocationId, cancellationToken)
            ?? throw new WorkflowJobInvariantException($"Call invocation #{invocationId} no longer exists.");
        if (!CallInvocationStates.IsSettled(invocation.State)
            && instance.Status == WorkflowInstanceStatuses.Running)
        {
            if (token is null
                || token.Status != ExecutionTokenRecordStatuses.Active
                || token.ActivationId != invocation.ActivationId
                || token.NodeId != invocation.ParentNodeId)
            {
                throw new WorkflowJobInvariantException(
                    $"Unsettled call invocation #{invocationId} no longer matches its parent token.");
            }

            var delivered = await DeliverCallResultAsync(
                fence,
                instance,
                token,
                invocation,
                cancellationToken);
            if (!delivered)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        if (!await jobs.CompleteAsync(fence, cancellationToken))
        {
            throw new WorkflowConflictException(
                "The call-resume job lease was lost before completion.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Returns false when the delivery opened an incident and the parent keeps waiting.
    private async Task<bool> DeliverCallResultAsync(
        WorkflowJobFence fence,
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        CallInvocationRecord invocation,
        CancellationToken cancellationToken)
    {
        var childInstanceId = invocation.ChildInstanceId
            ?? throw new WorkflowJobInvariantException(
                $"Call invocation #{invocation.Id} has a result job but no child.");
        var result = await CallActivities.GetResultAsync(childInstanceId, cancellationToken)
            ?? throw new WorkflowJobInvariantException(
                $"Call invocation #{invocation.Id} has a result job but no result.");
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var definition = workflow.Definition;
        var node = GetFlowNode(definition, invocation.ParentNodeId);
        var actor = CallActivityActor();
        var tokenInstance = instance with
        {
            ActiveTokenId = token.Id,
            CurrentStepId = token.NodeId,
            CurrentNodeExecutionId = token.CurrentNodeExecutionId
        };

        var failure = result.Outcome == CallResultOutcomes.Completed
            ? await ValidateCallOutputsAsync(tokenInstance, node, definition, actor, result, cancellationToken)
            : new CallFailure(
                result.FailureCode ?? CallFailureCodes.StartFailed,
                result.FailureDescription ?? $"Child instance #{childInstanceId} did not complete.",
                result.ChildFaultCode);

        if (failure is null)
        {
            var outputs = result.Outputs?.Values
                ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            await WriteVariablesAsync(
                definition,
                instance.WorkflowDefinitionId,
                instance.Id,
                outputs,
                node.Id,
                token.CurrentNodeExecutionId,
                actor,
                cancellationToken);
            if (!await CallActivities.SettleAsync(
                    invocation.Id,
                    CallInvocationStates.Completed,
                    null,
                    null,
                    null,
                    cancellationToken))
            {
                throw new WorkflowConflictException(
                    $"Call invocation #{invocation.Id} was settled during delivery.");
            }

            await ContinueAfterCallAsync(
                instance,
                token,
                definition,
                actor,
                TaskExecutionOutcome.Ok(),
                WorkflowJobKinds.CallResume,
                CallHistoryPayload(invocation, null),
                cancellationToken);
            return true;
        }

        var outcome = TaskExecutionOutcome.Fail(failure.Diagnostic);
        var hasBoundary = SelectErrorBoundary(definition, node.Id, outcome).Boundary is not null;
        var isChildOutcome = failure.Code is CallFailureCodes.ChildFaulted or CallFailureCodes.ChildCancelled;
        if (!hasBoundary && !isChildOutcome)
        {
            // Faults are reserved for child outcomes, because a faulted parent cannot be
            // reactivated. Retrying re-validates the same frozen result.
            await CallActivities.RecordBlockedAsync(
                invocation.Id,
                failure.Code,
                failure.Description,
                cancellationToken);
            if (await jobs.OpenIncidentAsync(
                    fence,
                    failure.Code,
                    $"Call activity #{node.Id} could not deliver its result.",
                    LimitJobFailure(failure.Description),
                    cancellationToken) is null)
            {
                throw new WorkflowConflictException(
                    "The call-resume job lease was lost before its incident was created.");
            }

            WorkflowJobRuntimeTelemetry.RecordIncident();
            return false;
        }

        if (!await CallActivities.SettleAsync(
                invocation.Id,
                CallInvocationStates.Failed,
                failure.Code,
                failure.Description,
                failure.ChildFaultCode,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                $"Call invocation #{invocation.Id} was settled during delivery.");
        }

        await AddCallHistoryAsync(instance.Id, token.Id, node.Id, InstanceHistoryNotes.CallFailed, actor, invocation, failure, cancellationToken);
        if (hasBoundary)
        {
            await ContinueAfterCallAsync(
                instance,
                token,
                definition,
                actor,
                outcome,
                WorkflowJobKinds.CallResume,
                null,
                cancellationToken);
            return true;
        }

        if (!await runtime.FaultExecutionTokenAtNodeAsync(
                token.Id,
                token.ActivationId,
                failure.Code,
                failure.Description,
                ExecutionTokenTerminationReasons.CallFailed,
                NodeExecutionCompletionReasons.CallFailed,
                ToNodeExecutionActor(actor),
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The execution token changed while its call failure was being recorded.");
        }

        await FaultInstanceAsync(instance.Id, token.Id, actor, cancellationToken);
        return true;
    }

    /// <summary>
    /// Checks a frozen result against the parent's process variables, over the complete
    /// overlay, before any of it is written.
    /// </summary>
    private async Task<CallFailure?> ValidateCallOutputsAsync(
        WorkflowInstanceRecord instance,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        CallResultRecord result,
        CancellationToken cancellationToken)
    {
        var outputs = result.Outputs?.Values ?? new Dictionary<string, JsonElement>();
        if (outputs.Count == 0)
        {
            return null;
        }

        var targets = new List<VariableModel>(outputs.Count);
        foreach (var pair in outputs)
        {
            var target = definition.Variables.FirstOrDefault(variable => string.Equals(
                variable.Name,
                pair.Key,
                StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return new CallFailure(
                    CallFailureCodes.OutputInvalid,
                    $"Output target '{pair.Key}' is not a declared process variable.");
            }

            targets.Add(target);
            if (pair.Value.ValueKind == JsonValueKind.Null)
            {
                if (!target.Nullable)
                {
                    return new CallFailure(
                        CallFailureCodes.OutputInvalid,
                        $"Output target '{target.Name}' is not nullable, but the result is null.");
                }
            }
            else if (!TypedOutputValueValidator.IsValid(pair.Value, target.DataType, target.IsArray))
            {
                return new CallFailure(
                    CallFailureCodes.OutputInvalid,
                    $"Output target '{target.Name}' must be {TypedOutputValueValidator.DescribeExpected(target.DataType, target.IsArray)}.");
            }
        }

        var stored = await LoadInstanceVariablesAsync(instance.Id, cancellationToken);
        var context = WithContext(stored, actor, instance, definition, node);
        try
        {
            ValidateResolvedVariableRules(
                targets,
                outputs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                context);
        }
        catch (WorkflowDomainException error)
        {
            return new CallFailure(CallFailureCodes.OutputInvalid, error.Message);
        }

        return null;
    }

    // Normal routing owns the continuation, so a conditional interrupt raised by the output
    // batch still wins over the normal flow.
    private async Task ContinueAfterCallAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        WorkflowModel definition,
        ActorContext actor,
        TaskExecutionOutcome outcome,
        string phase,
        Dictionary<string, JsonElement>? historyPayload,
        CancellationToken cancellationToken)
    {
        var flowInfo = await LoadSequenceFlowInfoAsync(instance.Id, definition, cancellationToken);
        var resumed = await ResolvePassThroughAsync(
            instance,
            definition,
            actor,
            flowInfo,
            token.Id,
            cancellationToken,
            new PassThroughResume(token.ActivationId, phase, true, outcome)
            {
                HistoryPayload = historyPayload
            },
            forceDurableActivities: true);
        await EnsureMultiInstanceInitializedAsync(resumed, definition, actor, cancellationToken);
        _ = await ApplyUserTaskOwnershipInheritanceAsync(resumed, definition, cancellationToken);
    }

    /// <summary>
    /// Settles the calls of tokens being cancelled, in the parent's transaction, so no later
    /// result can change the parent. Stopping the descendants goes through durable jobs, so
    /// no transaction locks two existing instances and a parent's cancel never waits for a
    /// child's transaction.
    /// </summary>
    private async Task AbandonCallsForTokensAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        CancellationToken cancellationToken)
    {
        if (callActivities is null)
        {
            return;
        }

        var unsettled = await callActivities.ListUnsettledForTokensAsync(instanceId, tokenIds, cancellationToken);
        foreach (var invocation in unsettled)
        {
            if (!await callActivities.SettleAsync(
                    invocation.Id,
                    CallInvocationStates.Cancelled,
                    null,
                    null,
                    null,
                    cancellationToken))
            {
                continue;
            }

            var token = await runtime.GetExecutionTokenAsync(invocation.ParentTokenId, false, cancellationToken)
                ?? throw new WorkflowConflictException("The call's execution token disappeared while it was abandoned.");
            await AddCallHistoryAsync(
                instanceId,
                token.Id,
                invocation.ParentNodeId,
                InstanceHistoryNotes.CallCancelled,
                CallActivityActor(),
                invocation,
                null,
                cancellationToken);

            // A queued call has no child, and cancelling its callStart is enough. The job
            // skips the instance's job cap, so a parent at its cap can still be cancelled.
            if (invocation.ChildInstanceId is null)
            {
                continue;
            }

            var parentDefinition = await GetWorkflowAsync(invocation.ParentDefinitionId, cancellationToken);
            await jobs.EnqueueAsync(
                BuildCallMessageJob(
                    instanceId,
                    invocation.ParentDefinitionId,
                    parentDefinition.WorkflowKey,
                    token,
                    WorkflowJobKinds.CallCancel,
                    invocation.Id),
                cancellationToken);
            logger.LogInformation(
                "Call invocation {InvocationId} abandoned; child instance {ChildInstanceId} will be cancelled.",
                invocation.Id,
                invocation.ChildInstanceId);
        }
    }

    /// <summary>
    /// Cancels one child and lets its own abandoned calls queue the next level. It bypasses
    /// the fence and the child's cancelRoles, because the decision was authorized on the parent.
    /// </summary>
    private async Task ProcessCallCancelJobAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CancellationToken cancellationToken)
    {
        var invocationId = RequireInvocationId(lease.Job);
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var invocation = await CallActivities.GetInvocationAsync(invocationId, cancellationToken)
            ?? throw new WorkflowJobInvariantException($"Call invocation #{invocationId} no longer exists.");
        var childInstanceId = invocation.ChildInstanceId
            ?? throw new WorkflowJobInvariantException(
                $"Call invocation #{invocationId} has a cancel job but no child.");
        var child = await runtime.GetInstanceForUpdateAsync(
            childInstanceId,
            false,
            cancellationToken,
            bypassCallFence: true)
            ?? throw new WorkflowJobInvariantException($"Child instance #{childInstanceId} no longer exists.");
        var job = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow job no longer exists.");
        EnsureJobFence(job, fence);

        if (child.Status == WorkflowInstanceStatuses.Running)
        {
            var actor = CallActivityActor();
            await CancelLockedInstanceAsync(child.Id, actor, cancellationToken);
            await runtime.AddTokenHistoryAsync(
                child.Id,
                child.ActiveTokenId,
                null,
                child.CurrentStepId,
                child.CurrentStepId,
                actor.User,
                CallHistoryPayload(invocation, null),
                InstanceHistoryNotes.CallCancelled,
                cancellationToken);
            var cancelAge = timeProvider.GetUtcNow() - lease.Job.CreatedAt;
            CallActivityRuntimeTelemetry.RecordCancelAge(cancelAge);
            logger.LogInformation(
                "Child instance {ChildInstanceId} cancelled for abandoned call invocation {InvocationId} of parent {ParentInstanceId} after {CancelSeconds:F1}s.",
                child.Id,
                invocationId,
                invocation.ParentInstanceId,
                cancelAge.TotalSeconds);
        }

        if (!await jobs.CompleteAsync(fence, cancellationToken))
        {
            throw new WorkflowConflictException(
                "The call-cancel job lease was lost before completion.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // A call message is one sent row, with the node type its sender holds. Node job policies
    // are invalid on a call, so it always uses the default retries on the control queue.
    private WorkflowJobCreateRecord BuildCallMessageJob(
        long ownerInstanceId,
        long ownerDefinitionId,
        string ownerWorkflowKey,
        ExecutionTokenRecord ownerToken,
        string kind,
        long invocationId) =>
        new()
        {
            InstanceId = ownerInstanceId,
            WorkflowDefinitionId = ownerDefinitionId,
            WorkflowKey = ownerWorkflowKey,
            TokenId = ownerToken.Id,
            CallInvocationId = invocationId,
            ActivationId = ownerToken.ActivationId,
            AutomaticActivationCount = ownerToken.AutomaticActivationCount,
            NodeId = ownerToken.NodeId,
            NodeName = ownerToken.NodeName,
            NodeType = ownerToken.NodeType,
            Kind = kind,
            QueueClass = WorkflowJobClasses.Control,
            Phase = kind,
            DueAt = timeProvider.GetUtcNow(),
            Priority = DefaultJobPriority,
            MaxAttempts = DefaultRetryDelays.Count + 1,
            FailureHandling = WorkflowJobFailureHandling.BoundaryFirst,
            RetryDelays = DefaultRetryDelays,
            Payload = JsonSerializer.SerializeToElement(
                new DurableJobPayload(
                    CallActivityActorName,
                    [],
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    null,
                    null))
        };

    private static long RequireInvocationId(WorkflowJobRecord job) =>
        job.CallInvocationId
        ?? throw new WorkflowJobInvariantException($"Call job #{job.Id} names no invocation.");

    private Task AddCallHistoryAsync(
        long instanceId,
        long tokenId,
        int nodeId,
        string note,
        ActorContext actor,
        CallInvocationRecord invocation,
        CallFailure? failure,
        CancellationToken cancellationToken)
    {
        CallActivityRuntimeTelemetry.RecordEvent(note, failure?.Code);
        return runtime.AddTokenHistoryAsync(
            instanceId,
            tokenId,
            null,
            nodeId,
            nodeId,
            actor.User,
            CallHistoryPayload(invocation, failure),
            note,
            cancellationToken,
            actor.ActingFor,
            actor.DelegationId,
            actorClaims: actor.AuditClaims);
    }

    // Identifiers and the safe failure text only, never a mapped value.
    private static Dictionary<string, JsonElement> CallHistoryPayload(
        CallInvocationRecord invocation,
        CallFailure? failure)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["invocationId"] = JsonSerializer.SerializeToElement(invocation.Id),
            ["parentInstanceId"] = JsonSerializer.SerializeToElement(invocation.ParentInstanceId),
            ["parentNodeId"] = JsonSerializer.SerializeToElement(invocation.ParentNodeId),
            ["targetWorkflowKey"] = JsonSerializer.SerializeToElement(invocation.TargetWorkflowKey)
        };
        if (invocation.ChildInstanceId is long childInstanceId)
        {
            payload["childInstanceId"] = JsonSerializer.SerializeToElement(childInstanceId);
        }

        if (invocation.ChildDefinitionId is long childDefinitionId)
        {
            payload["childDefinitionId"] = JsonSerializer.SerializeToElement(childDefinitionId);
        }

        if (failure is not null)
        {
            payload["failureCode"] = JsonSerializer.SerializeToElement(failure.Code);
            payload["failureDescription"] = JsonSerializer.SerializeToElement(failure.Description);
        }

        return payload;
    }
}
