using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Flowbit.Infrastructure.Repositories;

public sealed partial class WorkflowRuntimeRepository
{
    public async Task<UserTaskRolePolicyRecord?> GetRolePolicyAsync(long id, CancellationToken cancellationToken)
    {
        var policies = await GetRolePoliciesAsync([id], cancellationToken);
        return policies.GetValueOrDefault(id);
    }

    public async Task<IReadOnlyDictionary<long, UserTaskRolePolicyRecord>> GetRolePoliciesAsync(
        IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return new Dictionary<long, UserTaskRolePolicyRecord>();
        var entities = await dbContext.UserTaskRolePolicies.AsNoTracking()
            .Where(policy => ids.Contains(policy.Id)).ToListAsync(cancellationToken);
        return entities.ToDictionary(policy => policy.Id, ToRecord);
    }

    private async Task<UserTaskRolePolicyEntity> CreateRolePolicyAsync(
        WorkflowInstanceEntity instance, CurrentNodeSnapshot node, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var resolved = node.RolePolicy;
        if (resolved is null)
        {
            // Older persistence callers supply a static node snapshot. Preserve their
            // authored static contract without ever interpreting dynamic references here.
            var workflow = dbContext.WorkflowDefinitions.Local
                .SingleOrDefault(definition => definition.Id == instance.WorkflowDefinitionId)
                ?? await dbContext.WorkflowDefinitions.AsNoTracking().SingleAsync(
                    definition => definition.Id == instance.WorkflowDefinitionId, cancellationToken);
            var flows = workflow.Definition.SequenceFlows
                .Where(flow => flow.SourceRef == node.Id && flow.IsSelectable && !flow.IsDefault)
                .ToArray();
            var authoredNode = workflow.Definition.FlowNodes.SingleOrDefault(candidate => candidate.Id == node.Id);
            if (!string.IsNullOrWhiteSpace(authoredNode?.RolesVariable)
                || flows.Any(flow => !string.IsNullOrWhiteSpace(flow.RolesVariable)))
                throw new WorkflowDomainException("Variable-based task roles must be resolved before task creation.");
            resolved = new ResolvedUserTaskRolePolicy(node.Roles,
                flows.ToDictionary(flow => flow.Id,
                    flow => (IReadOnlyList<string>)(flow.Roles?.ToArray() ?? [])));
        }
        var entity = NewRolePolicy(instance.Id, instance.WorkflowDefinitionId, node.Id, resolved, now);
        dbContext.UserTaskRolePolicies.Add(entity);
        return entity;
    }

    private static UserTaskRolePolicyEntity NewRolePolicy(
        long instanceId, long workflowDefinitionId, int nodeId,
        ResolvedUserTaskRolePolicy policy, DateTimeOffset now) => new()
        {
            InstanceId = instanceId,
            WorkflowDefinitionId = workflowDefinitionId,
            NodeId = nodeId,
            Roles = policy.Roles.ToList(),
            OutgoingFlowRolesJson = JsonDocument.Parse(JsonSerializer.Serialize(policy.OutgoingFlowRoles)),
            CreatedAt = now
        };

    private static UserTaskRolePolicyRecord ToRecord(UserTaskRolePolicyEntity entity) => new(
        entity.Id, entity.InstanceId, entity.WorkflowDefinitionId, entity.NodeId,
        entity.Roles.ToArray(),
        entity.OutgoingFlowRolesJson.RootElement.EnumerateObject().ToDictionary(
            property => int.Parse(property.Name, System.Globalization.CultureInfo.InvariantCulture),
            property => (IReadOnlyList<string>)property.Value.EnumerateArray()
                .Select(role => role.GetString()!).ToArray()),
        entity.CreatedAt);

    public async Task<UserTaskRolePolicyRecord> ReplaceUserTaskRolePolicyAsync(
        long instanceId, long? taskId, long? executionId,
        ResolvedUserTaskRolePolicy policy, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Role changes require the caller's locked transaction.");
        if ((taskId is null) == (executionId is null))
            throw new ArgumentException("Exactly one normal task or multi-instance execution is required.");
        var instance = dbContext.WorkflowInstances.Local.SingleOrDefault(row => row.Id == instanceId)
            ?? throw new InvalidOperationException("The instance must be locked before changing roles.");
        MultiInstanceExecutionEntity? execution = null;
        List<UserTaskEntity> tasks;
        if (executionId is long miId)
        {
            execution = dbContext.MultiInstanceExecutions.Local.SingleOrDefault(row => row.Id == miId)
                ?? throw new InvalidOperationException("The multi-instance execution must be locked first.");
            if (execution.InstanceId != instanceId || execution.Status != MultiInstanceExecutionStatuses.Active)
                throw new WorkflowConflictException("The multi-instance execution is no longer active.");
            tasks = await dbContext.UserTasks.FromSqlInterpolated($"SELECT * FROM flowbit.user_tasks WHERE \"InstanceId\" = {instanceId} AND \"MultiInstanceExecutionId\" = {miId} AND \"Status\" IN ('active', 'pending') ORDER BY \"Id\" FOR UPDATE")
                .ToListAsync(cancellationToken);
        }
        else
        {
            tasks = await dbContext.UserTasks.FromSqlInterpolated($"SELECT * FROM flowbit.user_tasks WHERE \"InstanceId\" = {instanceId} AND \"Id\" = {taskId!.Value} AND \"MultiInstanceExecutionId\" IS NULL AND \"Status\" IN ('active', 'pending') ORDER BY \"Id\" FOR UPDATE")
                .ToListAsync(cancellationToken);
            if (tasks.Count != 1)
                throw new WorkflowConflictException("The user task is no longer active or pending.");
        }
        var now = DateTimeOffset.UtcNow;
        var nodeId = execution?.NodeId ?? tasks[0].NodeId;
        var replacement = NewRolePolicy(instance.Id, instance.WorkflowDefinitionId, nodeId, policy, now);
        dbContext.UserTaskRolePolicies.Add(replacement);
        if (execution is not null)
        {
            execution.RolePolicy = replacement;
            execution.UpdatedAt = now;
        }
        foreach (var task in tasks)
        {
            task.RolePolicy = replacement;
            task.Roles = policy.Roles.ToList();
            task.UpdatedAt = now;
        }
        instance.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(replacement);
    }
}
