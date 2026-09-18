using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Scoped instance detail and execution projection service. It reuses the
/// caller's scoped runtime/definition repositories so projection reads see
/// flushed state inside any ambient transaction, and it never creates,
/// commits, disposes, or suppresses that transaction.
/// </summary>
internal sealed class WorkflowInstanceProjectionService(
    IWorkflowRuntimeRepository runtime,
    IWorkflowDefinitionRepository definitions,
    IInstanceVariableUpdateRepository? variableUpdates = null,
    IWorkflowVariableStore? workflowVariables = null) : IWorkflowInstanceProjectionService
{
    private static readonly JsonSerializerOptions InstanceVariableUpdateJsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<InstanceDetailDto?> GetDetailAsync(long instanceId, CancellationToken cancellationToken)
    {
        var instance = await runtime.GetInstanceAsync(instanceId, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var variables = await runtime.ListVariablesAsync(instanceId, cancellationToken);
        var history = await runtime.ListHistoryAsync(instanceId, cancellationToken);
        var versionChanges = await BuildVersionChangeAuditDtosAsync(instanceId, cancellationToken);
        IReadOnlyList<InstanceVariableUpdateAuditDto> variableUpdateAudits = variableUpdates is null
            ? []
            : await BuildVariableUpdateAuditDtosAsync(instanceId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        var projection = await BuildExecutionAsync(
            instance,
            includeHistory: true,
            cancellationToken);
        var multiProgress = projection.MultiInstances
            .FirstOrDefault(progress =>
                progress.Status == MultiInstanceRecordStatuses.Active
                && projection.ExecutionPositions.Any(position =>
                    position.TokenId == instance.ActiveTokenId
                    && position.MultiInstanceExecutionId == progress.ExecutionId))
            ?? projection.MultiInstances.FirstOrDefault(progress =>
                progress.Status == MultiInstanceRecordStatuses.Active);
        var workSummaries = await runtime.GetUserTaskWorkSummariesAsync([instanceId], cancellationToken);
        var userTasks = workSummaries.TryGetValue(instanceId, out var workSummary)
            ? RuntimeProjectionMapper.ToUserTaskWorkSummary(workSummary)
            : null;
        var sharedVariableMetadata = workflowVariables is null
            ? []
            : await workflowVariables.DescribeBindingsAsync(
                workflow.Definition,
                cancellationToken);

        return new InstanceDetailDto(
            instance.Id,
            RuntimeProjectionMapper.ToRuntimeWorkflowDetail(workflow),
            instance.CurrentStepId,
            node.Name,
            node.ExternalId,
            instance.Status,
            instance.BusinessKey,
            instance.BusinessKeyUniqueness,
            instance.StartedBy,
            instance.CreatedAt,
            instance.UpdatedAt,
            variables.Select(v => new InstanceVariableDto(
                v.Id,
                v.VariableName,
                v.SourceActionId,
                v.SetBy,
                v.Value,
                v.SetAt)
            {
                ActingFor = v.ActingFor,
                DelegationId = v.DelegationId,
                InstanceVariableUpdateAuditId = v.InstanceVariableUpdateAuditId
            }).ToList(),
            history.Select(h => new InstanceHistoryDto(
                h.Id,
                h.TokenId,
                h.UserTaskId,
                h.MultiInstanceExecutionId,
                h.ItemIndex,
                h.ActionId,
                h.FromStepId,
                h.ToStepId,
                h.PerformedBy,
                h.Payload,
                h.Note,
                h.PerformedAt)
            {
                ActingFor = h.ActingFor,
                DelegationId = h.DelegationId,
                ActorClaims = h.ActorClaims,
                Reason = h.Reason,
                AdministrativeActionBatchId = h.AdministrativeActionBatchId
            }).ToList(),
            multiProgress,
            userTasks,
            RuntimeProjectionMapper.ToFault(instance.Status, instance.FaultCode, instance.FaultDescription, node.Name))
        {
            ExecutionPositions = projection.ExecutionPositions,
            MultiInstances = projection.MultiInstances,
            GatewayExecutions = projection.GatewayExecutions,
            ComplexGatewayStates = projection.ComplexGatewayStates,
            Completion = projection.Completion,
            VersionChanges = versionChanges,
            VariableUpdates = variableUpdateAudits,
            SharedVariables = sharedVariableMetadata,
            FinishedAt = instance.FinishedAt,
            HistoryPrunedAt = instance.HistoryPrunedAt
        };
    }

    public async Task<InstanceExecutionProjection> BuildExecutionAsync(
        WorkflowInstanceRecord instance,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        var tokens = includeHistory
            ? await runtime.ListExecutionTokensAsync(instance.Id, null, cancellationToken)
            : await runtime.ListCurrentExecutionTokensAsync(
                instance.Id,
                instance.ActiveTokenId,
                cancellationToken);
        var tasks = includeHistory
            ? await runtime.ListUserTasksAsync(instance.Id, null, cancellationToken)
            : await runtime.ListCurrentUserTasksAsync(instance.Id, cancellationToken);
        var multiExecutions = includeHistory
            ? await runtime.ListMultiInstancesAsync(instance.Id, null, cancellationToken)
            : await runtime.ListCurrentMultiInstancesAsync(instance.Id, cancellationToken);

        var taskByTokenAndNode = tasks
            .GroupBy(task => (task.TokenId, task.NodeId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(task => task.Status == UserTaskRecordStatuses.Active ? 0
                        : task.Status == UserTaskRecordStatuses.Pending ? 1 : 2)
                    .ThenByDescending(task => task.UpdatedAt)
                    .ThenByDescending(task => task.Id)
                    .First());
        var multiByTokenAndNode = multiExecutions
            .GroupBy(execution => (execution.TokenId, execution.NodeId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(execution => execution.Status == MultiInstanceRecordStatuses.Active ? 0 : 1)
                    .ThenByDescending(execution => execution.UpdatedAt)
                    .ThenByDescending(execution => execution.Id)
                    .First());

        var positions = tokens
            .Where(token => token.Status != ExecutionTokenRecordStatuses.Merged)
            .OrderBy(token => token.Id)
            .Select(token =>
            {
                taskByTokenAndNode.TryGetValue((token.Id, token.NodeId), out var task);
                multiByTokenAndNode.TryGetValue((token.Id, token.NodeId), out var multi);
                return new ExecutionPositionDto(
                    token.Id,
                    token.NodeId,
                    token.NodeName,
                    token.NodeExternalId,
                    token.NodeType,
                    token.Status,
                    token.ArrivedViaFlowId,
                    token.TerminationReason,
                    task?.Id,
                    multi?.Id,
                    token.ActivationId == Guid.Empty ? null : token.ActivationId,
                    token.WaitState,
                    token.WaitingJobId,
                    token.WaitingTimerSubscriptionId);
            })
            .ToList();

        var progressById = await GetMultiInstanceProgressAsync(
            multiExecutions.Select(execution => execution.Id).ToList(),
            cancellationToken);
        var multiProgress = multiExecutions
            .OrderBy(execution => execution.Id)
            .Select(execution => progressById.GetValueOrDefault(execution.Id))
            .Where(progress => progress is not null)
            .Cast<MultiInstanceProgressDto>()
            .ToList();

        var gatewayExecutions = includeHistory
            ? await runtime.ListGatewayExecutionsAsync(
                instance.Id, null, cancellationToken)
            : await runtime.ListCurrentGatewayExecutionsAsync(
                instance.Id, cancellationToken);
        var branches = includeHistory
            ? await runtime.ListGatewayBranchesForInstanceAsync(
                instance.Id, false, cancellationToken)
            : await runtime.ListGatewayBranchesForExecutionsAsync(
                gatewayExecutions.Select(execution => execution.Id).ToArray(),
                cancellationToken);
        var branchExecutionIds = branches.ToDictionary(branch => branch.Id, branch => branch.ExecutionId);
        var branchesByExecution = branches
            .GroupBy(branch => branch.ExecutionId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var gatewayDtos = gatewayExecutions
            .OrderBy(execution => execution.Id)
            .Select(execution =>
            {
                var executionBranches = branchesByExecution.GetValueOrDefault(execution.Id) ?? [];
                return new GatewayExecutionDto(
                    execution.Id,
                    execution.GatewayNodeId,
                    execution.GatewayType,
                    execution.Direction,
                    execution.Phase,
                    execution.Cycle,
                    execution.SelectedFlowIds,
                    execution.ParentBranchId is long parentBranchId
                        ? branchExecutionIds.GetValueOrDefault(parentBranchId)
                        : null,
                    execution.Status,
                    execution.CompletionReason,
                    execution.InterruptingNodeId,
                    execution.InterruptingTokenId,
                    executionBranches.Count,
                    executionBranches.Count(branch =>
                        branch.Status == GatewayBranchRecordStatuses.Active),
                    executionBranches.Count(branch =>
                        branch.Status == GatewayBranchRecordStatuses.Completed),
                    executionBranches.Count(branch =>
                        branch.Status == GatewayBranchRecordStatuses.Merged),
                    executionBranches.Count(branch =>
                        branch.Status == GatewayBranchRecordStatuses.Interrupted),
                    executionBranches.Count(branch =>
                        branch.Status == GatewayBranchRecordStatuses.Cancelled),
                    execution.CreatedAt,
                    execution.UpdatedAt,
                    execution.CompletedAt);
            })
            .ToList();
        var complexStateDtos = (await runtime.ListComplexGatewayStatesAsync(
                instance.Id, cancellationToken))
            .Select(state => new ComplexGatewayStateDto(
                state.GatewayNodeId,
                state.Phase,
                state.Cycle,
                state.ContributingFlowIds,
                state.RemainingFlowIds,
                state.DrainingTokenIds,
                state.ActiveExecutionId,
                state.UpdatedAt))
            .ToList();

        CompletionInfoDto? completion = null;
        if (instance.Status == WorkflowInstanceStatuses.Completed)
        {
            var terminal = tokens
                .Where(token => token.Status == ExecutionTokenRecordStatuses.Completed
                                && token.TerminationReason is
                                    ExecutionTokenTerminationReasons.NormalEnd
                                    or ExecutionTokenTerminationReasons.TerminateEnd)
                .OrderByDescending(token => token.UpdatedAt)
                .ThenByDescending(token =>
                    token.TerminationReason == ExecutionTokenTerminationReasons.TerminateEnd)
                .ThenByDescending(token => token.Id)
                .FirstOrDefault();
            if (terminal is not null)
            {
                completion = new CompletionInfoDto(
                    terminal.TerminationReason == ExecutionTokenTerminationReasons.TerminateEnd
                        ? WorkflowCompletionKinds.Terminate
                        : WorkflowCompletionKinds.Normal,
                    terminal.Id,
                    terminal.NodeId,
                    terminal.NodeName,
                    terminal.NodeExternalId,
                    terminal.UpdatedAt);
            }
        }

        return new InstanceExecutionProjection(
            positions,
            multiProgress,
            gatewayDtos,
            complexStateDtos,
            completion);
    }

    public async Task<MultiInstanceProgressDto?> GetMultiInstanceProgressAsync(
        long executionId,
        CancellationToken cancellationToken)
    {
        var progress = await GetMultiInstanceProgressAsync([executionId], cancellationToken);
        return progress.GetValueOrDefault(executionId);
    }

    public async Task<IReadOnlyDictionary<long, MultiInstanceProgressDto>> GetMultiInstanceProgressAsync(
        IReadOnlyCollection<long> executionIds,
        CancellationToken cancellationToken)
    {
        var records = await runtime.GetMultiInstanceProgressAsync(executionIds, cancellationToken);
        return records.ToDictionary(pair => pair.Key, pair => RuntimeProjectionMapper.ToProgress(pair.Value));
    }

    private async Task<IReadOnlyList<InstanceVariableUpdateAuditDto>>
        BuildVariableUpdateAuditDtosAsync(
            long instanceId,
            CancellationToken cancellationToken)
    {
        var records = await variableUpdates!.ListByInstanceAsync(
            instanceId,
            cancellationToken);
        return records.Select(record => new InstanceVariableUpdateAuditDto(
            record.Id,
            record.InstanceId,
            record.WorkflowDefinitionId,
            record.PerformedBy,
            record.PerformedByRoles,
            record.Reason,
            record.Result.Deserialize<
                IReadOnlyList<InstanceVariableUpdateOutcomeDto>>(
                    InstanceVariableUpdateJsonOptions) ?? [],
            record.PerformedAt,
            record.IdempotencyKey,
            record.BatchId,
            record.BatchItemId)).ToArray();
    }

    private async Task<IReadOnlyList<InstanceVersionChangeAuditDto>>
        BuildVersionChangeAuditDtosAsync(
            long instanceId,
            CancellationToken cancellationToken)
    {
        var records = await runtime.ListVersionChangesAsync(
            instanceId,
            cancellationToken);
        if (records.Count == 0)
        {
            return [];
        }

        var workflowIds = records
            .SelectMany(record => new[]
            {
                record.SourceWorkflowDefinitionId,
                record.TargetWorkflowDefinitionId
            })
            .Distinct()
            .ToArray();
        var workflows = await definitions.GetManyAsync(workflowIds, cancellationToken);
        var result = new List<InstanceVersionChangeAuditDto>(records.Count);
        foreach (var record in records.OrderByDescending(change => change.ChangedAt)
                     .ThenByDescending(change => change.Id))
        {
            if (!workflows.TryGetValue(record.SourceWorkflowDefinitionId, out var source)
                || !workflows.TryGetValue(record.TargetWorkflowDefinitionId, out var target))
            {
                throw new InvalidOperationException(
                    $"Version-change audit #{record.Id} references a missing workflow definition.");
            }
            result.Add(RuntimeProjectionMapper.ToVersionChangeAudit(record, source, target));
        }

        return result;
    }

    private async Task<WorkflowDefinitionRecord> GetWorkflowAsync(long id, CancellationToken cancellationToken) =>
        await definitions.GetAsync(id, cancellationToken)
        ?? throw new WorkflowDomainException($"Workflow definition #{id} was not found.");

    private static FlowNodeModel GetFlowNode(WorkflowModel definition, int nodeId) =>
        definition.FlowNodes.SingleOrDefault(n => n.Id == nodeId)
        ?? throw new WorkflowDomainException($"Flow node #{nodeId} was not found in workflow '{definition.Name}'.");
}
