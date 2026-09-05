using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService
{
    private static ResolvedUserTaskRolePolicy ResolveTaskRolePolicy(
        WorkflowModel definition,
        FlowNodeModel node,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        var roles = UserTaskRolePolicyResolver.ResolveRoles(
            definition, node.RolesVariable, node.Roles, values);
        var flows = definition.SequenceFlows
            .Where(flow => flow.SourceRef == node.Id && flow.IsSelectable && !flow.IsDefault)
            .ToDictionary(flow => flow.Id, flow => UserTaskRolePolicyResolver.ResolveRoles(
                definition, flow.RolesVariable, flow.Roles, values));
        return new ResolvedUserTaskRolePolicy(roles, flows);
    }

    private static IReadOnlyList<string> EffectiveFlowRoles(
        UserTaskRecord task, SequenceFlowModel flow) =>
        EffectiveFlowRoles(task.RolePolicy, task.RolePolicyId, flow);

    private static IReadOnlyList<string> EffectiveFlowRoles(
        MultiInstanceExecutionRecord execution, SequenceFlowModel flow) =>
        EffectiveFlowRoles(execution.RolePolicy, execution.RolePolicyId, flow);

    private static IReadOnlyList<string> EffectiveFlowRoles(
        UserTaskRolePolicyRecord? policy, long? policyId, SequenceFlowModel flow)
    {
        if (policy is not null && policy.OutgoingFlowRoles.TryGetValue(flow.Id, out var roles))
            return roles;

        // Only pre-cutover historical records and legacy repository adapters may
        // lack a policy. A persisted policy must never silently fall back to the
        // authored permission, including when its requested flow is missing.
        if (policy is not null || policyId is not null || !string.IsNullOrWhiteSpace(flow.RolesVariable))
            throw new WorkflowDomainException("The user task's saved action-role policy is unavailable.");
        return flow.Roles;
    }

    private static IReadOnlyList<string> EffectiveExecutionRoles(
        MultiInstanceExecutionRecord execution, FlowNodeModel node)
    {
        if (execution.RolePolicy is not null) return execution.RolePolicy.Roles;
        if (execution.RolePolicyId is not null || !string.IsNullOrWhiteSpace(node.RolesVariable))
            throw new WorkflowDomainException("The multi-instance task's saved role policy is unavailable.");
        return node.Roles;
    }

    private static void EnsureTaskRoleAllowed(UserTaskRecord task, ActorContext actor)
    {
        if (!RoleAllowed(task.Roles, NormalizeRoles(actor.Roles)))
            throw new WorkflowDomainException("The actor does not have a role permitted for this user task.");
    }
}
