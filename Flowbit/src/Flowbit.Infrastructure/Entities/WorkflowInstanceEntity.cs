namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowInstanceEntity
{
    public long Id { get; set; }

    public long WorkflowDefinitionId { get; set; }

    public string WorkflowKey { get; set; } = string.Empty;

    public string? IdempotencyKey { get; set; }

    public string? BusinessKey { get; set; }

    public string? BusinessKeyUniqueness { get; set; }

    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }

    public string Status { get; set; } = "running";

    public string? StartedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset? HistoryPrunedAt { get; set; }

    /// <summary>Whether retained evidence establishes recording from instance creation.</summary>
    public bool NodeActivityFromCreation { get; set; } = true;

    /// <summary>First actual retention deletion of node activity; other categories do not set it.</summary>
    public DateTimeOffset? NodeActivityPrunedAt { get; set; }

    /// <summary>
    /// The root token created by the most recent reactivation; null when the instance
    /// was never reactivated. Tokens from this id onward belong to the current run.
    /// Unlike the reactivation history entry, retention never removes it.
    /// </summary>
    public long? LastReactivationTokenId { get; set; }

    /// <summary>
    /// The root of this instance's call tree; null for a root or a legacy instance. A
    /// plain column rather than a foreign key, so creating a nested child never waits on
    /// the root's row lock. Immutable after insertion, like <see cref="CallDepth"/>.
    /// </summary>
    public long? CallRootInstanceId { get; set; }

    /// <summary>The number of calls between this instance and its root; 0 for a root.</summary>
    public int CallDepth { get; set; }

    public List<InstanceVariableEntity> Variables { get; set; } = [];

    public List<InstanceVariableCurrentValueEntity> CurrentVariableValues { get; set; } = [];

    public List<InstanceVariableUpdateAuditEntity> VariableUpdates { get; set; } = [];

    public List<InstanceVariableUpdateBatchItemEntity> VariableUpdateBatchItems { get; set; } = [];

    public List<InstanceHistoryEntity> History { get; set; } = [];

    public List<ExecutionTokenEntity> Tokens { get; set; } = [];

    public List<NodeExecutionEntity> NodeExecutions { get; set; } = [];

    public List<UserTaskEntity> UserTasks { get; set; } = [];

    public List<MultiInstanceExecutionEntity> MultiInstanceExecutions { get; set; } = [];

    public List<GatewayExecutionEntity> GatewayExecutions { get; set; } = [];

    public List<ComplexGatewayStateEntity> ComplexGatewayStates { get; set; } = [];

    public List<SequenceFlowOccurrenceEntity> SequenceFlowOccurrences { get; set; } = [];

    public List<SequenceFlowSummaryEntity> SequenceFlowSummaries { get; set; } = [];

    public List<MessageDeliveryReceiptEntity> MessageDeliveryReceipts { get; set; } = [];

    public List<WorkflowJobEntity> Jobs { get; set; } = [];

    public List<WorkflowIncidentEntity> Incidents { get; set; } = [];

    public List<TimerSubscriptionEntity> TimerSubscriptions { get; set; } = [];

    public List<ConditionalBoundarySubscriptionEntity> ConditionalBoundarySubscriptions { get; set; } = [];

    public List<WorkflowInstanceVersionChangeEntity> VersionChanges { get; set; } = [];

    public List<WorkflowInstanceVersionChangeBatchItemEntity> VersionChangeBatchItems { get; set; } = [];

    public List<AdministrativeActionBatchItemEntity> AdministrativeActionBatchItems { get; set; } = [];
}
