namespace Flowbit.Shared.Dtos;

/// <summary>
/// A previously visited, top-level ordinary user task that can receive a fresh
/// activation when a terminal workflow instance is reactivated.
/// </summary>
public sealed record InstanceReactivationTargetDto(
    int NodeId,
    string NodeName,
    string? NodeExternalId,
    DateTimeOffset LastVisitedAt);

/// <summary>A structured finding produced by instance-reactivation validation.</summary>
public sealed record InstanceReactivationIssueDto(
    string Code,
    string Message,
    int? NodeId = null,
    long? StateId = null);

/// <summary>
/// The result of a non-mutating check for reactivating a completed or cancelled
/// workflow instance at one of its eligible prior user tasks.
/// </summary>
public sealed record InstanceReactivationPreviewDto(
    long InstanceId,
    long WorkflowId,
    string Status,
    bool CanReactivate,
    IReadOnlyList<InstanceReactivationTargetDto> Targets,
    IReadOnlyList<InstanceReactivationIssueDto> Blockers,
    IReadOnlyList<InstanceReactivationIssueDto> Warnings,
    DateTimeOffset ExpectedUpdatedAt);

/// <summary>
/// Request payload for atomically reactivating a completed or cancelled workflow
/// instance at a previously visited user task.
/// </summary>
public sealed record ReactivateInstanceRequest(
    int TargetNodeId,
    long ExpectedWorkflowId,
    DateTimeOffset ExpectedUpdatedAt,
    string Reason);
