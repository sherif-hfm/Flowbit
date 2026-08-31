using System.Text;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService
{
    public async Task<InstanceReactivationPreviewDto?> PreviewInstanceReactivationAsync(
        long id,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        EnsureValidReactivationInstanceId(id);
        _ = actor;
        await LoadSettingsAsync(cancellationToken);

        var instance = await runtime.GetInstanceAsync(id, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        var workflow = await GetWorkflowAsync(
            instance.WorkflowDefinitionId,
            cancellationToken);
        var assessment = await AssessReactivationAsync(
            instance,
            workflow,
            cancellationToken);

        return new InstanceReactivationPreviewDto(
            instance.Id,
            instance.WorkflowDefinitionId,
            instance.Status,
            assessment.CanReactivate,
            assessment.Targets,
            assessment.Blockers,
            assessment.Warnings,
            instance.UpdatedAt);
    }

    public async Task<InstanceDetailDto?> ReactivateInstanceAsync(
        long id,
        ReactivateInstanceRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        EnsureValidReactivationInstanceId(id);
        if (request is null)
        {
            throw new WorkflowDomainException("A reactivation request is required.");
        }
        if (request.TargetNodeId <= 0)
        {
            throw new WorkflowDomainException("TargetNodeId must be greater than zero.");
        }
        if (request.ExpectedWorkflowId <= 0)
        {
            throw new WorkflowDomainException("ExpectedWorkflowId must be greater than zero.");
        }
        if (request.ExpectedUpdatedAt == default)
        {
            throw new WorkflowDomainException("ExpectedUpdatedAt is required.");
        }
        var reason = NormalizeReactivationReason(request.Reason);
        await LoadSettingsAsync(cancellationToken);

        // Preserve normal not-found behavior without opening a transaction for
        // an id that does not exist. Every mutable fact is reloaded under the
        // instance lock below.
        if (await runtime.GetInstanceAsync(id, cancellationToken) is null)
        {
            return null;
        }

        await using (var transaction = await unitOfWork.BeginTransactionAsync(
                         cancellationToken))
        {
            var instance = await runtime.GetInstanceForUpdateAsync(
                    id,
                    lockActiveUserTask: true,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The workflow instance no longer exists.");

            EnsureTerminalReactivationStatus(instance.Status);
            if (instance.WorkflowDefinitionId != request.ExpectedWorkflowId)
            {
                throw new WorkflowConflictException(
                    "The workflow instance version changed after preview; refresh and preview again.");
            }
            if (instance.UpdatedAt != request.ExpectedUpdatedAt)
            {
                throw new WorkflowConflictException(
                    "The workflow instance changed after preview; refresh and preview again.");
            }

            var workflow = await GetWorkflowAsync(
                instance.WorkflowDefinitionId,
                cancellationToken);
            var assessment = await AssessReactivationAsync(
                instance,
                workflow,
                cancellationToken);
            var target = assessment.Targets.SingleOrDefault(candidate =>
                candidate.NodeId == request.TargetNodeId);
            if (!assessment.CanReactivate || target is null)
            {
                throw new WorkflowConflictException(
                    DescribeReactivationConflict(
                        assessment,
                        request.TargetNodeId));
            }

            var businessKey = await runtime.ReacquireBusinessKeyAsync(
                instance.Id,
                cancellationToken);
            if (!businessKey.Acquired)
            {
                var owner = businessKey.ConflictingInstanceId is long ownerId
                    ? $" by active instance #{ownerId}"
                    : string.Empty;
                throw new WorkflowConflictException(
                    $"The business key is already owned{owner}; the instance cannot be reactivated.");
            }

            var targetNode = GetFlowNode(
                workflow.Definition,
                request.TargetNodeId);
            var sourceStatus = instance.Status;
            var sourceTokenId = instance.ActiveTokenId;
            var sourceNodeId = instance.CurrentStepId;

            await runtime.SetInstanceStatusAsync(
                instance.Id,
                WorkflowInstanceStatuses.Running,
                cancellationToken);

            var instanceVariables = await LoadInstanceVariablesAsync(
                instance.Id,
                cancellationToken);
            var effectiveVariables = workflowVariables is null
                ? instanceVariables
                : await workflowVariables.MergeEffectiveValuesAsync(
                    workflow.Definition,
                    instanceVariables,
                    SharedAccessScope(
                        workflow.Id,
                        workflow.Definition,
                        targetNode.Id),
                    cancellationToken);
            var activationInstance = instance with
            {
                Status = WorkflowInstanceStatuses.Running,
                CurrentStepId = targetNode.Id,
                FaultCode = null,
                FaultDescription = null,
                CurrentNodeExecutionId = null
            };
            var targetContext = WithContext(
                effectiveVariables,
                actor,
                activationInstance,
                workflow.Definition,
                targetNode);
            var token = await runtime.AddExecutionTokenAsync(
                instance.Id,
                ToSnapshot(targetNode, targetContext, instance.Id),
                gatewayBranchId: null,
                arrivedViaFlowId: null,
                ToNodeExecutionActor(actor),
                cancellationToken,
                automaticActivationCount: 0,
                automaticActivationStateIds: []);

            var newTask = await runtime.GetActiveUserTaskAsync(
                    instance.Id,
                    forUpdate: false,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "Reactivation did not create the requested active user task.");
            if (newTask.TokenId != token.Id
                || newTask.NodeId != targetNode.Id
                || newTask.NodeExecutionId is null
                || token.CurrentNodeExecutionId != newTask.NodeExecutionId)
            {
                throw new WorkflowConflictException(
                    "Reactivation did not create a consistent user-task execution.");
            }

            var auditPayload = new Dictionary<string, JsonElement>
            {
                ["sourceStatus"] = JsonSerializer.SerializeToElement(sourceStatus),
                ["sourceTokenId"] = JsonSerializer.SerializeToElement(sourceTokenId),
                ["sourceNodeId"] = JsonSerializer.SerializeToElement(sourceNodeId),
                ["targetNodeId"] = JsonSerializer.SerializeToElement(targetNode.Id),
                ["targetNodeName"] = JsonSerializer.SerializeToElement(targetNode.Name),
                ["newTokenId"] = JsonSerializer.SerializeToElement(token.Id),
                ["newUserTaskId"] = JsonSerializer.SerializeToElement(newTask.Id),
                ["newNodeExecutionId"] = JsonSerializer.SerializeToElement(
                    newTask.NodeExecutionId.Value),
                ["workflowId"] = JsonSerializer.SerializeToElement(workflow.Id),
                ["workflowVersion"] = JsonSerializer.SerializeToElement(workflow.Version),
                ["operator"] = JsonSerializer.SerializeToElement(NormalizeUser(actor.User)),
                ["operatorRoles"] = JsonSerializer.SerializeToElement(
                    SnapshotRoles(actor.Roles)),
                ["reason"] = JsonSerializer.SerializeToElement(reason)
            };
            await runtime.AddTokenHistoryAsync(
                instance.Id,
                token.Id,
                actionId: null,
                sourceNodeId,
                targetNode.Id,
                actor.User,
                auditPayload,
                InstanceHistoryNotes.InstanceReactivated,
                cancellationToken,
                actor.ActingFor,
                actor.DelegationId,
                reason);

            var running = await runtime.GetInstanceAsync(
                    instance.Id,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The workflow instance disappeared during reactivation.");
            if (!string.Equals(
                    running.Status,
                    WorkflowInstanceStatuses.Running,
                    StringComparison.Ordinal))
            {
                throw new WorkflowConflictException(
                    "The workflow instance did not enter the running state.");
            }

            var flowInfo = await LoadSequenceFlowInfoAsync(
                instance.Id,
                workflow.Definition,
                cancellationToken);
            running = await ResolvePassThroughAsync(
                running,
                workflow.Definition,
                actor,
                flowInfo,
                token.Id,
                cancellationToken);
            running = await ApplyUserTaskOwnershipInheritanceAsync(
                running,
                workflow.Definition,
                cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            await EnsureStableReactivationPostconditionAsync(
                instance.Id,
                token.Id,
                targetNode.Id,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Reactivated terminal workflow instance {InstanceId} from {SourceStatus} at user task #{TargetNodeId} with token {TokenId} and task {UserTaskId} by {Actor}.",
                instance.Id,
                sourceStatus,
                targetNode.Id,
                token.Id,
                newTask.Id,
                actor.User ?? "anonymous");
        }

        return await BuildDetailAsync(id, cancellationToken)
            ?? throw new WorkflowConflictException(
                "The workflow instance no longer exists after reactivation.");
    }

    private async Task<ReactivationAssessment> AssessReactivationAsync(
        WorkflowInstanceRecord instance,
        WorkflowDefinitionRecord workflow,
        CancellationToken cancellationToken)
    {
        var globalBlockers = new List<InstanceReactivationIssueDto>();
        var targetBlockers = new List<InstanceReactivationIssueDto>();
        if (!IsReactivatableTerminalStatus(instance.Status))
        {
            globalBlockers.Add(new InstanceReactivationIssueDto(
                "status_not_reactivatable",
                string.Equals(
                    instance.Status,
                    WorkflowInstanceStatuses.Faulted,
                    StringComparison.OrdinalIgnoreCase)
                    ? "Faulted instances cannot be reactivated."
                    : "Only completed or cancelled instances can be reactivated."));
        }

        var runtimeState = await runtime.GetReactivationRuntimeStateAsync(
            instance.Id,
            cancellationToken);
        AddRuntimeStateBlockers(runtimeState, globalBlockers);
        await AddComplexStateBlockersAsync(
            instance.Id,
            workflow,
            runtimeState,
            globalBlockers,
            cancellationToken);
        var businessKey = await runtime.AssessBusinessKeyReacquisitionAsync(
            instance.Id,
            cancellationToken);
        if (!businessKey.Acquired)
        {
            globalBlockers.Add(businessKey.ClaimMissing
                ? new InstanceReactivationIssueDto(
                    "business_key_claim_missing",
                    "The instance's business-key claim is missing and cannot be restored safely.")
                : new InstanceReactivationIssueDto(
                    "business_key_conflict",
                    businessKey.ConflictingInstanceId is long ownerId
                        ? $"The business key is owned by instance #{ownerId}."
                        : "The business key cannot be reacquired safely."));
        }

        var visits = await runtime.ListReactivationTargetVisitsAsync(
            instance.Id,
            workflow.Id,
            cancellationToken);
        if (visits.Count == 0)
        {
            targetBlockers.Add(new InstanceReactivationIssueDto(
                "missing_execution_evidence",
                "No completed or cancelled top-level user-task execution from the current workflow version is available."));
        }

        var currentVariables = await LoadInstanceVariablesAsync(
            instance.Id,
            cancellationToken);
        var targets = new List<InstanceReactivationTargetDto>();
        foreach (var visit in visits
                     .OrderByDescending(candidate => candidate.CompletedAt ?? candidate.UpdatedAt)
                     .ThenByDescending(candidate => candidate.NodeExecutionId))
        {
            var node = workflow.Definition.FlowNodes.SingleOrDefault(candidate =>
                candidate.Id == visit.NodeId);
            if (node is null
                || !BpmnFlowNodeTypes.IsUserTask(node.Type)
                || node.MultiInstance is not null)
            {
                targetBlockers.Add(new InstanceReactivationIssueDto(
                    "target_not_ordinary_user_task",
                    $"Previously visited node #{visit.NodeId} is no longer an ordinary user task in this workflow version.",
                    visit.NodeId));
                continue;
            }
            if (node.AsyncBefore)
            {
                targetBlockers.Add(new InstanceReactivationIssueDto(
                    "target_async_before",
                    $"User task '{node.Name}' uses asyncBefore and cannot be a reactivation target.",
                    node.Id));
                continue;
            }

            var unsafeTopology = FindUnsafeReactivationTopology(
                workflow.Definition,
                node.Id);
            if (unsafeTopology is not null)
            {
                targetBlockers.Add(new InstanceReactivationIssueDto(
                    "target_unsafe_topology",
                    unsafeTopology,
                    node.Id));
                continue;
            }

            var conditionalBlocker = EvaluateReactivationConditionalBoundaries(
                workflow,
                node,
                currentVariables);
            if (conditionalBlocker is not null)
            {
                targetBlockers.Add(conditionalBlocker);
                continue;
            }

            targets.Add(new InstanceReactivationTargetDto(
                node.Id,
                node.Name,
                node.ExternalId,
                visit.CompletedAt ?? visit.UpdatedAt));
        }

        if (targets.Count == 0)
        {
            globalBlockers.Add(new InstanceReactivationIssueDto(
                "no_eligible_targets",
                "No previously visited user task is currently safe to reactivate."));
        }

        // When at least one target is eligible, omitted prior visits are not an
        // instance-wide blocker. Keep their diagnostic reasons for the all-
        // ineligible case, where they explain why no target can be selected.
        var blockers = targets.Count > 0
            ? globalBlockers.ToArray()
            : globalBlockers.Concat(targetBlockers).ToArray();
        var warnings = new[]
        {
            new InstanceReactivationIssueDto(
                "retained_state",
                "Current variables, FlowInfo evidence, history, receipts, and terminal runtime rows are retained."),
            new InstanceReactivationIssueDto(
                "repeated_side_effects",
                "Continuing from an earlier task can repeat downstream external side effects; reactivation does not undo prior work.")
        };
        return new ReactivationAssessment(
            targets,
            blockers,
            warnings,
            globalBlockers.Count == 0 && targets.Count > 0);
    }

    private static void AddRuntimeStateBlockers(
        WorkflowReactivationRuntimeStateRecord state,
        ICollection<InstanceReactivationIssueDto> blockers)
    {
        if (state.ActiveExecutionTokenCount > 0
            || state.OpenUserTaskCount > 0
            || state.OpenNodeExecutionCount > 0
            || state.ActiveMultiInstanceExecutionCount > 0
            || state.ActiveGatewayExecutionCount > 0
            || state.ActiveGatewayBranchCount > 0)
        {
            blockers.Add(new InstanceReactivationIssueDto(
                "active_runtime_artifacts",
                "The terminal instance still has active tokens, tasks, node executions, multi-instance work, or gateway scope rows."));
        }
        if (state.NonTerminalWorkflowJobCount > 0
            || state.OpenIncidentCount > 0
            || state.ActiveOrPausedTimerSubscriptionCount > 0
            || state.ActiveConditionalBoundarySubscriptionCount > 0)
        {
            blockers.Add(new InstanceReactivationIssueDto(
                "unresolved_durable_work",
                "The terminal instance still has unresolved jobs, incidents, timers, or conditional boundary subscriptions."));
        }
        foreach (var stateId in state.NonResetComplexGatewayStateIds)
        {
            blockers.Add(new InstanceReactivationIssueDto(
                "complex_gateway_not_reset",
                $"Complex Gateway state #{stateId} is not fully reset.",
                StateId: stateId));
        }
        foreach (var tokenId in state.RetainedComplexLineageTokenIds)
        {
            blockers.Add(new InstanceReactivationIssueDto(
                "complex_lineage_retained",
                $"Terminal token #{tokenId} retains Complex Gateway lineage markers.",
                StateId: tokenId));
        }
    }

    private async Task AddComplexStateBlockersAsync(
        long instanceId,
        WorkflowDefinitionRecord workflow,
        WorkflowReactivationRuntimeStateRecord runtimeState,
        ICollection<InstanceReactivationIssueDto> blockers,
        CancellationToken cancellationToken)
    {
        var alreadyBlocked = runtimeState.NonResetComplexGatewayStateIds.ToHashSet();
        var states = await runtime.ListComplexGatewayStatesAsync(
            instanceId,
            cancellationToken);
        foreach (var state in states)
        {
            if (alreadyBlocked.Contains(state.Id))
            {
                continue;
            }
            var expectedIncoming = IncomingFlows(
                    workflow.Id,
                    workflow.Definition,
                    state.GatewayNodeId)
                .Select(flow => flow.Id)
                .Order()
                .ToArray();
            var actualRemaining = state.RemainingFlowIds.Order().ToArray();
            var reset = string.Equals(
                            state.Phase,
                            ComplexGatewayStateRecordPhases.WaitingForStart,
                            StringComparison.Ordinal)
                        && state.AutomaticActivationCount == 0
                        && state.ContributingFlowIds.Count == 0
                        && state.ActivationDrainStateIds.Count == 0
                        && state.DrainingTokenIds.Count == 0
                        && state.ActiveExecutionId is null
                        && expectedIncoming.SequenceEqual(actualRemaining);
            if (!reset)
            {
                blockers.Add(new InstanceReactivationIssueDto(
                    "complex_gateway_not_reset",
                    $"Complex Gateway state #{state.Id} is not reset to its authored incoming-flow contract.",
                    state.GatewayNodeId,
                    state.Id));
            }
        }
    }

    private InstanceReactivationIssueDto? EvaluateReactivationConditionalBoundaries(
        WorkflowDefinitionRecord workflow,
        FlowNodeModel target,
        IReadOnlyDictionary<string, JsonElement> currentVariables)
    {
        var boundaries = workflow.Definition.FlowNodes
            .Where(node =>
                BpmnFlowNodeTypes.IsConditionalBoundary(node.Type)
                && node.AttachedToRef == target.Id)
            .OrderBy(node => node.Id)
            .ToArray();
        if (boundaries.Length == 0)
        {
            return null;
        }
        if (conditionalEventPlans is null)
        {
            return new InstanceReactivationIssueDto(
                "conditional_boundary_unavailable",
                $"Conditional boundary validation is unavailable for user task '{target.Name}'.",
                target.Id);
        }

        var plan = conditionalEventPlans.GetOrAdd(
            workflow.Id,
            workflow.Definition);
        foreach (var boundary in boundaries)
        {
            if (!plan.EventsByNodeId.TryGetValue(boundary.Id, out var entry)
                || !entry.IsBoundary)
            {
                return new InstanceReactivationIssueDto(
                    "conditional_boundary_unavailable",
                    $"Conditional boundary #{boundary.Id} has no valid dependency plan.",
                    target.Id);
            }
            try
            {
                if (SequenceFlowConditionEvaluator.Evaluate(
                        entry.Condition,
                        ConditionalParameters(entry, currentVariables)))
                {
                    return new InstanceReactivationIssueDto(
                        "conditional_boundary_true",
                        $"Conditional boundary '{boundary.Name}' is currently true for user task '{target.Name}'.",
                        target.Id,
                        boundary.Id);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Could not safely evaluate conditional boundary {BoundaryNodeId} while previewing reactivation of user task {TargetNodeId}.",
                    boundary.Id,
                    target.Id);
                return new InstanceReactivationIssueDto(
                    "conditional_boundary_evaluation_failed",
                    $"Conditional boundary '{boundary.Name}' could not be evaluated safely.",
                    target.Id,
                    boundary.Id);
            }
        }
        return null;
    }

    private static string? FindUnsafeReactivationTopology(
        WorkflowModel definition,
        int targetNodeId)
    {
        var outgoing = definition.SequenceFlows
            .GroupBy(flow => flow.SourceRef)
            .ToDictionary(
                group => group.Key,
                group => group.Select(flow => flow.TargetRef).Distinct().ToArray());
        var incomingFlows = definition.SequenceFlows
            .GroupBy(flow => flow.TargetRef)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(flow => flow.Id).ToArray());
        var outgoingFlows = definition.SequenceFlows
            .GroupBy(flow => flow.SourceRef)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(flow => flow.Id).ToArray());
        var scopeSplits = definition.FlowNodes
            .Where(node =>
                BpmnFlowNodeTypes.IsScopeProducingGateway(node.Type)
                && incomingFlows.GetValueOrDefault(node.Id, []).Length == 1
                && outgoingFlows.GetValueOrDefault(node.Id, []).Length >= 2)
            .ToArray();

        foreach (var hazard in definition.FlowNodes.OrderBy(node => node.Id))
        {
            IReadOnlyCollection<int>? requiredFreshSplits = null;
            string? hazardLabel = null;
            var incoming = incomingFlows.GetValueOrDefault(hazard.Id, []);
            var exits = outgoingFlows.GetValueOrDefault(hazard.Id, []);

            if (BpmnFlowNodeTypes.IsScopedInterrupt(hazard.Type))
            {
                requiredFreshSplits = hazard.GatewayRef is int gatewayRef
                    ? [gatewayRef]
                    : [];
                hazardLabel = $"scoped interrupt #{hazard.Id}";
            }
            else if (hazard.JoinCancellation?.GatewayRef is int cancellationRef)
            {
                requiredFreshSplits = [cancellationRef];
                hazardLabel = $"cancelling join #{hazard.Id}";
            }
            else if (BpmnFlowNodeTypes.IsScopeProducingGateway(hazard.Type)
                     && incoming.Length >= 2
                     && exits.Length == 1)
            {
                requiredFreshSplits = scopeSplits
                    .Where(split => incoming.All(flow =>
                        IsStructurallyDownstreamBeforeNode(
                            outgoing,
                            split.Id,
                            flow.SourceRef,
                            hazard.Id)))
                    .Select(split => split.Id)
                    .ToArray();
                hazardLabel = $"synchronizing merge #{hazard.Id}";
            }

            if (requiredFreshSplits is null)
            {
                continue;
            }
            if (CanReachWithoutCrossing(
                    outgoing,
                    targetNodeId,
                    hazard.Id,
                    requiredFreshSplits))
            {
                return $"Reactivation at this task can reach {hazardLabel} without first creating its required gateway split scope.";
            }
        }
        return null;
    }

    private static bool CanReachWithoutCrossing(
        IReadOnlyDictionary<int, int[]> outgoing,
        int sourceNodeId,
        int targetNodeId,
        IReadOnlyCollection<int> barriers)
    {
        var barrierSet = barriers.ToHashSet();
        var visited = new HashSet<int> { sourceNodeId };
        var queue = new Queue<int>();
        queue.Enqueue(sourceNodeId);
        while (queue.TryDequeue(out var current))
        {
            if (!outgoing.TryGetValue(current, out var targets))
            {
                continue;
            }
            foreach (var target in targets)
            {
                if (target == targetNodeId)
                {
                    return true;
                }
                if (barrierSet.Contains(target))
                {
                    continue;
                }
                if (visited.Add(target))
                {
                    queue.Enqueue(target);
                }
            }
        }
        return false;
    }

    private static bool IsStructurallyDownstreamBeforeNode(
        IReadOnlyDictionary<int, int[]> outgoing,
        int splitNodeId,
        int candidateNodeId,
        int stopNodeId)
    {
        if (candidateNodeId == splitNodeId)
        {
            return true;
        }
        var visited = new HashSet<int> { splitNodeId, stopNodeId };
        var queue = new Queue<int>();
        queue.Enqueue(splitNodeId);
        while (queue.TryDequeue(out var current))
        {
            if (!outgoing.TryGetValue(current, out var targets))
            {
                continue;
            }
            foreach (var target in targets)
            {
                if (target == stopNodeId)
                {
                    continue;
                }
                if (target == candidateNodeId)
                {
                    return true;
                }
                if (visited.Add(target))
                {
                    queue.Enqueue(target);
                }
            }
        }
        return false;
    }

    private async Task EnsureStableReactivationPostconditionAsync(
        long instanceId,
        long tokenId,
        int targetNodeId,
        CancellationToken cancellationToken)
    {
        var status = await runtime.GetInstanceStatusAsync(
            instanceId,
            cancellationToken);
        var tokens = await runtime.ListExecutionTokensAsync(
            instanceId,
            ExecutionTokenRecordStatuses.Active,
            cancellationToken);
        var activeTasks = await runtime.ListUserTasksAsync(
            instanceId,
            UserTaskRecordStatuses.Active,
            cancellationToken);
        var pendingTasks = await runtime.ListUserTasksAsync(
            instanceId,
            UserTaskRecordStatuses.Pending,
            cancellationToken);
        var runtimeState = await runtime.GetReactivationRuntimeStateAsync(
            instanceId,
            cancellationToken);
        var task = activeTasks.Count == 1
            ? await runtime.GetActiveUserTaskAsync(
                instanceId,
                forUpdate: false,
                cancellationToken)
            : null;
        var token = tokens.SingleOrDefault();
        if (!string.Equals(
                status,
                WorkflowInstanceStatuses.Running,
                StringComparison.Ordinal)
            || tokens.Count != 1
            || activeTasks.Count != 1
            || pendingTasks.Count != 0
            || runtimeState.ActiveExecutionTokenCount != 1
            || runtimeState.OpenUserTaskCount != 1
            || runtimeState.OpenNodeExecutionCount != 1
            || runtimeState.ActiveMultiInstanceExecutionCount != 0
            || runtimeState.ActiveGatewayExecutionCount != 0
            || runtimeState.ActiveGatewayBranchCount != 0
            || runtimeState.NonResetComplexGatewayStateIds.Count != 0
            || runtimeState.RetainedComplexLineageTokenIds.Count != 0
            || token is null
            || task is null
            || token.Id != tokenId
            || token.NodeId != targetNodeId
            || task.TokenId != tokenId
            || task.NodeId != targetNodeId
            || task.MultiInstanceExecutionId is not null
            || token.GatewayBranchId is not null
            || token.WaitState is not null
            || token.WaitingJobId is not null
            || token.WaitingTimerSubscriptionId is not null
            || token.CurrentNodeExecutionId is null
            || task.NodeExecutionId != token.CurrentNodeExecutionId)
        {
            throw new WorkflowConflictException(
                "Reactivation did not produce exactly one stable active user task at the requested node.");
        }
    }

    private static string DescribeReactivationConflict(
        ReactivationAssessment assessment,
        int targetNodeId)
    {
        var relevant = assessment.Blockers
            .Where(issue => issue.NodeId is null || issue.NodeId == targetNodeId)
            .Take(5)
            .Select(issue => $"{issue.Code}: {issue.Message}")
            .ToArray();
        return relevant.Length == 0
            ? $"User task #{targetNodeId} is no longer an eligible reactivation target."
            : "The workflow instance cannot be reactivated: "
              + string.Join("; ", relevant);
    }

    private static bool IsReactivatableTerminalStatus(string status) =>
        string.Equals(
            status,
            WorkflowInstanceStatuses.Completed,
            StringComparison.Ordinal)
        || string.Equals(
            status,
            WorkflowInstanceStatuses.Cancelled,
            StringComparison.Ordinal);

    private static void EnsureTerminalReactivationStatus(string status)
    {
        if (!IsReactivatableTerminalStatus(status))
        {
            throw new WorkflowConflictException(
                string.Equals(
                    status,
                    WorkflowInstanceStatuses.Faulted,
                    StringComparison.OrdinalIgnoreCase)
                    ? "Faulted workflow instances cannot be reactivated."
                    : "Only completed or cancelled workflow instances can be reactivated.");
        }
    }

    private static void EnsureValidReactivationInstanceId(long id)
    {
        if (id <= 0)
        {
            throw new WorkflowDomainException(
                "The workflow instance id must be greater than zero.");
        }
    }

    private static string NormalizeReactivationReason(string? raw)
    {
        var reason = raw?.Trim() ?? string.Empty;
        var length = reason.EnumerateRunes().Count();
        if (length is < 1 or > 1000)
        {
            throw new WorkflowDomainException(
                "Reason must contain between 1 and 1000 Unicode characters.");
        }
        return reason;
    }

    private sealed record ReactivationAssessment(
        IReadOnlyList<InstanceReactivationTargetDto> Targets,
        IReadOnlyList<InstanceReactivationIssueDto> Blockers,
        IReadOnlyList<InstanceReactivationIssueDto> Warnings,
        bool CanReactivate);
}
