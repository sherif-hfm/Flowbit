namespace Flowbit.Shared.Dtos;

/// <summary>A selectable action's effective roles for one waiting activation.</summary>
public sealed record UserTaskFlowRolesDto(int FlowId, string Name, IReadOnlyList<string> Roles);

public sealed record UserTaskRolePolicyDto(
    long InstanceId,
    long TokenId,
    int NodeId,
    string NodeName,
    long? UserTaskId,
    long? MultiInstanceExecutionId,
    long RolePolicyId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<UserTaskFlowRolesDto> Flows,
    int ActiveTaskCount,
    int PendingTaskCount);

public sealed record ChangeUserTaskFlowRolesRequest(int FlowId, IReadOnlyList<string> Roles);

/// <summary>Replaces every editable role list for the specified waiting activation.</summary>
public sealed record ChangeUserTaskRolesRequest(
    long ExpectedRolePolicyId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<ChangeUserTaskFlowRolesRequest> Flows,
    string? Reason);

public sealed record UserTaskRolesChangeAckDto(bool Changed, UserTaskRolePolicyDto Policy);
