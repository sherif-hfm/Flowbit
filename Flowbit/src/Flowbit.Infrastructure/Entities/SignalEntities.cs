namespace Flowbit.Infrastructure.Entities;

/// <summary>
/// One active wait for a signal: the waiting token of a signal catch, or the host
/// token of a signal boundary. Written only under the instance lock, in the
/// transaction that starts or ends the wait.
/// </summary>
public sealed class SignalSubscriptionEntity
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public long WorkflowDefinitionId { get; set; }
    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }
    public string WorkflowKey { get; set; } = string.Empty;
    public long TokenId { get; set; }
    public ExecutionTokenEntity? Token { get; set; }
    public Guid ActivationId { get; set; }
    public int SignalNodeId { get; set; }
    public string SignalNodeName { get; set; } = string.Empty;
    public int? AttachedToNodeId { get; set; }
    public int OutgoingFlowId { get; set; }
    public string SignalName { get; set; } = string.Empty;
    public bool CancelActivity { get; set; }
    public long Occurrence { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// One thrown signal with its frozen recipient count and outcome counters. Its
/// deliveries are frozen in the throw's transaction.
/// </summary>
public sealed class SignalBroadcastEntity
{
    public long Id { get; set; }
    public string SignalName { get; set; } = string.Empty;
    public string? WorkflowKey { get; set; }
    public string? PayloadJson { get; set; }
    public string Status { get; set; } = "delivering";
    public int TotalDeliveryCount { get; set; }
    public int DeliveredCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public int CancelledCount { get; set; }
    public string RecipientsJson { get; set; } = "[]";
    public bool RecipientsTruncated { get; set; }
    public string ThrownByKind { get; set; } = string.Empty;
    public string ThrownBy { get; set; } = string.Empty;
    public string ThrownByRolesJson { get; set; } = "[]";
    public string? ThrownByClaimsJson { get; set; }
    public string? IdempotencyKey { get; set; }
    public byte[]? RequestHash { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public List<SignalDeliveryEntity> Deliveries { get; set; } = [];
    public List<SignalBroadcastJobEntity> JobLinks { get; set; } = [];
}

/// <summary>
/// One frozen recipient of a broadcast. It names its subscription, instance, and
/// token by plain ids: a foreign key would make the throw wait for every
/// recipient whose transaction holds its instance and token locks.
/// </summary>
public sealed class SignalDeliveryEntity
{
    public long Id { get; set; }
    public long BroadcastId { get; set; }
    public SignalBroadcastEntity? Broadcast { get; set; }
    public long SubscriptionId { get; set; }
    public long InstanceId { get; set; }
    public long WorkflowDefinitionId { get; set; }
    public string WorkflowKey { get; set; } = string.Empty;
    public long TokenId { get; set; }
    public Guid ActivationId { get; set; }
    public int SignalNodeId { get; set; }
    public int? AttachedToNodeId { get; set; }
    public bool CancelActivity { get; set; }
    public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; }
    public string? ResultCode { get; set; }
    public string? ResultDescription { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// A delivery job of a broadcast: one per represented workflow definition for the
/// throw, and again for each retry of failed deliveries.
/// </summary>
public sealed class SignalBroadcastJobEntity
{
    public long Id { get; set; }
    public long BroadcastId { get; set; }
    public SignalBroadcastEntity? Broadcast { get; set; }
    public long WorkflowDefinitionId { get; set; }
    public long? JobId { get; set; }
    public WorkflowJobEntity? Job { get; set; }
    public long OriginalJobId { get; set; }
    public string Reason { get; set; } = "throw";
    public string RequestedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
