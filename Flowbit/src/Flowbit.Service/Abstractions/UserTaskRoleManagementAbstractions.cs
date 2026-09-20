using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Scoped service behind waiting-task role-policy reads and replacements.
/// It owns permission checking, waiting-scope loading, replacement validation,
/// policy comparison, DTO mapping, audit assembly, and transaction ownership
/// for normal user-task and multi-instance executions. It does not capture
/// entry-time roles, evaluate runtime action authorization, or own
/// management-list status parsing.
/// </summary>
public interface IUserTaskRoleManagementService
{
    Task<UserTaskRolePolicyDto?> GetUserTaskRolesAsync(
        long taskId, ActorContext actor, CancellationToken cancellationToken);

    Task<UserTaskRolePolicyDto?> GetMultiInstanceRolesAsync(
        long executionId, ActorContext actor, CancellationToken cancellationToken);

    Task<UserTaskRolesChangeAckDto?> ChangeUserTaskRolesAsync(
        long taskId, ChangeUserTaskRolesRequest request,
        ActorContext actor, CancellationToken cancellationToken);

    Task<UserTaskRolesChangeAckDto?> ChangeMultiInstanceRolesAsync(
        long executionId, ChangeUserTaskRolesRequest request,
        ActorContext actor, CancellationToken cancellationToken);
}
