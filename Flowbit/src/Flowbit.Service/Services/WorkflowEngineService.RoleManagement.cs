using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService
{
    private sealed record RoleManagementScope(
        WorkflowInstanceRecord Instance,
        ExecutionTokenRecord Token,
        FlowNodeModel Node,
        WorkflowModel Definition,
        UserTaskRecord? Task,
        MultiInstanceExecutionRecord? Execution,
        UserTaskRolePolicyRecord Policy,
        int ActiveCount,
        int PendingCount);

    public async Task<UserTaskRolePolicyDto?> GetUserTaskRolesAsync(
        long taskId, ActorContext actor, CancellationToken cancellationToken)
    {
        var scope = await LoadRoleManagementScopeAsync(taskId, null, actor, false, cancellationToken);
        return scope is null ? null : ToRoleManagementDto(scope);
    }

    public async Task<UserTaskRolePolicyDto?> GetMultiInstanceRolesAsync(
        long executionId, ActorContext actor, CancellationToken cancellationToken)
    {
        var scope = await LoadRoleManagementScopeAsync(null, executionId, actor, false, cancellationToken);
        return scope is null ? null : ToRoleManagementDto(scope);
    }

    public Task<UserTaskRolesChangeAckDto?> ChangeUserTaskRolesAsync(
        long taskId, ChangeUserTaskRolesRequest request, ActorContext actor, CancellationToken cancellationToken) =>
        ChangeWaitingRolesAsync(taskId, null, request, actor, cancellationToken);

    public Task<UserTaskRolesChangeAckDto?> ChangeMultiInstanceRolesAsync(
        long executionId, ChangeUserTaskRolesRequest request, ActorContext actor, CancellationToken cancellationToken) =>
        ChangeWaitingRolesAsync(null, executionId, request, actor, cancellationToken);

    private async Task<UserTaskRolesChangeAckDto?> ChangeWaitingRolesAsync(
        long? taskId,
        long? executionId,
        ChangeUserTaskRolesRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedRolePolicyId <= 0)
            throw new WorkflowDomainException("expectedRolePolicyId is required.");
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason?.Length > 1000)
            throw new WorkflowDomainException("The role-change reason cannot exceed 1000 characters.");

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var scope = await LoadRoleManagementScopeAsync(taskId, executionId, actor, true, cancellationToken);
        if (scope is null) return null;
        var desired = ValidateManagedRoleReplacement(scope, request);
        if (SameManagedRolePolicy(scope.Policy, desired))
            return new UserTaskRolesChangeAckDto(false, ToRoleManagementDto(scope));
        if (scope.Policy.Id != request.ExpectedRolePolicyId)
            throw new WorkflowConflictException("The task roles changed; refresh and review them before trying again.");

        var updated = await runtime.ReplaceUserTaskRolePolicyAsync(
            scope.Instance.Id, taskId, executionId, desired, cancellationToken);
        var payload = new Dictionary<string, JsonElement>
        {
            ["previousRolePolicyId"] = JsonSerializer.SerializeToElement(scope.Policy.Id),
            ["newRolePolicyId"] = JsonSerializer.SerializeToElement(updated.Id),
            ["previousRoles"] = JsonSerializer.SerializeToElement(scope.Policy.Roles),
            ["newRoles"] = JsonSerializer.SerializeToElement(updated.Roles),
            ["previousFlowRoles"] = JsonSerializer.SerializeToElement(scope.Policy.OutgoingFlowRoles),
            ["newFlowRoles"] = JsonSerializer.SerializeToElement(updated.OutgoingFlowRoles),
            ["multiInstanceExecutionId"] = JsonSerializer.SerializeToElement(executionId),
            ["affectedTaskCount"] = JsonSerializer.SerializeToElement(scope.ActiveCount + scope.PendingCount),
            ["performedByRoles"] = JsonSerializer.SerializeToElement(NormalizeRoles(actor.Roles).Order()),
            ["reason"] = JsonSerializer.SerializeToElement(reason)
        };
        if (taskId is long normalTaskId)
        {
            await runtime.AddUserTaskHistoryAsync(
                scope.Instance.Id, scope.Token.Id, normalTaskId, null, null, scope.Node.Id,
                NormalizeUser(actor.User), payload, "taskRolesChanged", cancellationToken);
        }
        else
        {
            await runtime.AddTokenHistoryAsync(
                scope.Instance.Id, scope.Token.Id, null, scope.Node.Id, scope.Node.Id,
                NormalizeUser(actor.User), payload, "taskRolesChanged", cancellationToken);
        }
        await runtime.TouchInstanceAsync(scope.Instance.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UserTaskRolesChangeAckDto(true, ToRoleManagementDto(scope with { Policy = updated }));
    }

    private async Task<RoleManagementScope?> LoadRoleManagementScopeAsync(
        long? taskId,
        long? executionId,
        ActorContext actor,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var initialTask = taskId is long id
            ? await runtime.GetUserTaskAsync(id, false, cancellationToken) : null;
        var initialExecution = executionId is long miId
            ? await runtime.GetMultiInstanceAsync(miId, false, cancellationToken) : null;
        if (taskId is not null && initialTask is null || executionId is not null && initialExecution is null)
            return null;
        var instanceId = initialTask?.InstanceId ?? initialExecution!.InstanceId;
        var instance = forUpdate
            ? await runtime.GetInstanceForUpdateAsync(instanceId, false, cancellationToken)
            : await runtime.GetInstanceAsync(instanceId, cancellationToken);
        if (instance is null) return null;
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        EnsureTaskRoleManager(workflow.Definition, actor);
        if (instance.Status != WorkflowInstanceStatuses.Running)
            throw new WorkflowConflictException("The workflow instance is no longer running.");
        if (initialTask?.MultiInstanceExecutionId is long containingExecution)
            throw new WorkflowConflictException(
                $"This task belongs to multi-instance execution #{containingExecution}; manage roles on that execution instead.");

        var tokenId = initialTask?.TokenId ?? initialExecution!.TokenId;
        var token = await runtime.GetExecutionTokenAsync(tokenId, forUpdate, cancellationToken);
        var execution = executionId is long currentExecutionId
            ? await runtime.GetMultiInstanceAsync(currentExecutionId, forUpdate, cancellationToken) : null;
        var task = taskId is long currentTaskId
            ? await runtime.GetUserTaskAsync(currentTaskId, forUpdate, cancellationToken) : null;
        var nodeId = task?.NodeId ?? execution?.NodeId;
        if (token is null || token.InstanceId != instance.Id
            || token.Status != ExecutionTokenRecordStatuses.Active || nodeId != token.NodeId
            || taskId is not null && (task is null || task.TokenId != token.Id
                || task.InstanceId != instance.Id || !IsOpenRoleTask(task.Status))
            || executionId is not null && (execution is null || execution.TokenId != token.Id
                || execution.InstanceId != instance.Id || execution.Status != MultiInstanceRecordStatuses.Active))
        {
            throw new WorkflowConflictException("The task activation is no longer waiting.");
        }
        var policyId = task?.RolePolicyId ?? execution?.RolePolicyId;
        var policy = task?.RolePolicy ?? execution?.RolePolicy;
        if (policy is null && policyId is long rolePolicyId)
            policy = await runtime.GetRolePolicyAsync(rolePolicyId, cancellationToken);
        if (policy is null || policy.InstanceId != instance.Id || policy.NodeId != token.NodeId)
            throw new WorkflowConflictException("The waiting task role policy is unavailable.");
        var node = GetFlowNode(workflow.Definition, token.NodeId);
        if (node.Type != BpmnFlowNodeTypes.UserTask)
            throw new WorkflowConflictException("The activation is not a user task.");
        var tasks = execution is not null
            ? await runtime.ListExecutionTasksAsync(execution.Id, cancellationToken)
            : new[] { task! };
        var active = tasks.Count(item => item.Status == UserTaskRecordStatuses.Active);
        var pending = tasks.Count(item => item.Status == UserTaskRecordStatuses.Pending);
        if (active + pending == 0)
            throw new WorkflowConflictException("There are no waiting tasks in this activation.");
        return new RoleManagementScope(instance, token, node, workflow.Definition, task, execution, policy, active, pending);
    }

    private static void EnsureTaskRoleManager(WorkflowModel definition, ActorContext actor)
    {
        var roles = NormalizeRoles(actor.Roles);
        if (!(definition.TaskRoleManagementRoles ?? []).Any(roles.Contains))
            throw new WorkflowForbiddenException("You are not allowed to manage task roles for this workflow.");
    }

    private static bool IsOpenRoleTask(string status) =>
        status is UserTaskRecordStatuses.Active or UserTaskRecordStatuses.Pending;

    private static string NormalizeManagedTaskStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" or "active" => "active",
        "pending" => "pending",
        "open" => "open",
        _ => throw new WorkflowDomainException("Task status must be active, pending, or open.")
    };

    private static ResolvedUserTaskRolePolicy ValidateManagedRoleReplacement(
        RoleManagementScope scope, ChangeUserTaskRolesRequest request)
    {
        if (request.Roles is null || request.Flows is null)
            throw new WorkflowDomainException("roles and flows are required arrays; use an empty roles array for unrestricted access.");
        var roles = UserTaskRolePolicyResolver.NormalizeManagedRoles(request.Roles);
        var selectableIds = scope.Definition.SequenceFlows
            .Where(flow => flow.SourceRef == scope.Node.Id && flow.IsSelectable && !flow.IsDefault)
            .Select(flow => flow.Id).ToHashSet();
        if (request.Flows.Any(flow => flow is null)
            || request.Flows.Select(flow => flow.FlowId).Distinct().Count() != request.Flows.Count
            || !selectableIds.SetEquals(request.Flows.Select(flow => flow.FlowId)))
            throw new WorkflowDomainException("flows must contain each selectable outgoing flow exactly once and no other flows.");
        var outgoing = scope.Policy.OutgoingFlowRoles.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var flow in request.Flows)
        {
            if (flow.Roles is null)
                throw new WorkflowDomainException($"roles is required for flow #{flow.FlowId}.");
            outgoing[flow.FlowId] = UserTaskRolePolicyResolver.NormalizeManagedRoles(flow.Roles);
        }
        return new ResolvedUserTaskRolePolicy(roles, outgoing);
    }

    private static bool SameManagedRolePolicy(UserTaskRolePolicyRecord current, ResolvedUserTaskRolePolicy desired) =>
        NormalizeRoles(current.Roles).SetEquals(desired.Roles)
        && current.OutgoingFlowRoles.Count == desired.OutgoingFlowRoles.Count
        && current.OutgoingFlowRoles.All(pair => desired.OutgoingFlowRoles.TryGetValue(pair.Key, out var roles)
            && NormalizeRoles(pair.Value).SetEquals(roles));

    private static UserTaskRolePolicyDto ToRoleManagementDto(RoleManagementScope scope) => new(
        scope.Instance.Id, scope.Token.Id, scope.Node.Id, scope.Node.Name,
        scope.Task?.Id, scope.Execution?.Id, scope.Policy.Id, scope.Policy.Roles,
        scope.Definition.SequenceFlows.Where(flow => flow.SourceRef == scope.Node.Id && flow.IsSelectable && !flow.IsDefault)
            .OrderBy(flow => flow.Id)
            .Select(flow => new UserTaskFlowRolesDto(flow.Id, flow.Name ?? $"Flow #{flow.Id}",
                scope.Policy.OutgoingFlowRoles.GetValueOrDefault(flow.Id)
                    ?? throw new WorkflowConflictException($"The role policy for flow #{flow.Id} is unavailable.")))
            .ToArray(), scope.ActiveCount, scope.PendingCount);
}
