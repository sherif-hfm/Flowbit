using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class MultiInstanceExecutionEntity
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public long TokenId { get; set; }
    public ExecutionTokenEntity? Token { get; set; }
    public int NodeId { get; set; }
    public string ActivityKind { get; set; } = "userTask";
    public long? WorkflowDefinitionId { get; set; }
    public Guid? ActivationId { get; set; }
    public long? InputSnapshotId { get; set; }
    public WorkflowJobSnapshotEntity? InputSnapshot { get; set; }
    public string? AggregateScope { get; set; }
    public string? AggregateSharedKey { get; set; }
    public long? AggregateValueVersion { get; set; }
    public int? MaxInstances { get; set; }
    public int? EffectiveConcurrency { get; set; }
    public string? Phase { get; set; }
    public string? AdmissionBlockedReason { get; set; }
    public int FailedCount { get; set; }
    public int ResultBytes { get; set; }
    public long ControlGeneration { get; set; }
    public long? PinnedChildDefinitionId { get; set; }
    public int? PinnedChildStartEventId { get; set; }
    public string? PinState { get; set; }
    public JsonDocument? CompletionJson { get; set; }
    public List<MultiInstanceActivityItemEntity> ActivityItems { get; set; } = [];
    public long? RolePolicyId { get; set; }
    public UserTaskRolePolicyEntity? RolePolicy { get; set; }
    public string Mode { get; set; } = "parallel";
    public string Source { get; set; } = "collection";
    public bool OnePerActor { get; set; }
    public string ResultVariable { get; set; } = string.Empty;
    public string Status { get; set; } = MultiInstanceExecutionStatuses.Active;
    public int TotalCount { get; set; }
    public int CompletedCount { get; set; }
    public int CancelledCount { get; set; }
    public int? WinningFlowId { get; set; }
    public string? CompletionReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<MultiInstanceFlowCountEntity> FlowCounts { get; set; } = [];
    public List<UserTaskEntity> UserTasks { get; set; } = [];
    public List<NodeExecutionEntity> NodeExecutions { get; set; } = [];
}

public static class MultiInstanceExecutionStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Interrupted = "interrupted";
    public const string Cancelled = "cancelled";
}
