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
/// One signal name that a workflow family starts on, while the family's published default
/// has a signal start with it. Written only by the operations that change the family's
/// default, under the family's exclusive lock, and updated in place for a name the old and
/// the new default both start on. A throw scoped to the family or to every family freezes
/// one start delivery from each row it reads and share-locks the row, so an operation that
/// changes the default waits for a throw in flight.
/// </summary>
public sealed class SignalStartSubscriptionEntity
{
    public long Id { get; set; }
    public string WorkflowKey { get; set; } = string.Empty;
    public string SignalName { get; set; } = string.Empty;
    public long WorkflowDefinitionId { get; set; }
    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }
    public int SignalNodeId { get; set; }
    public string SignalNodeName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One thrown signal with its frozen recipient count and outcome counters. Its
/// deliveries are frozen in the throw's transaction. Its scope is every family
/// (<see cref="WorkflowKey"/> null), one family, the instance with one
/// <see cref="BusinessKey"/> in a family, or one <see cref="InstanceId"/>, whose family
/// <see cref="WorkflowKey"/> then records. <see cref="InstanceId"/> is a plain column:
/// a foreign-key check would make the throw wait for a transaction holding the instance.
/// </summary>
public sealed class SignalBroadcastEntity
{
    public long Id { get; set; }
    public string SignalName { get; set; } = string.Empty;
    public string? WorkflowKey { get; set; }
    public string? BusinessKey { get; set; }
    public long? InstanceId { get; set; }
    public string? PayloadJson { get; set; }
    public string Status { get; set; } = "delivering";
    public int TotalDeliveryCount { get; set; }

    /// <summary>How many of <see cref="TotalDeliveryCount"/> are starts of a workflow family.</summary>
    public int StartDeliveryCount { get; set; }
    public int DeliveredCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public int CancelledCount { get; set; }

    /// <summary>
    /// The skipped deliveries whose result is <c>broadcast_expired</c>; part of
    /// <see cref="SkippedCount"/>.
    /// </summary>
    public int ExpiredCount { get; set; }

    /// <summary>
    /// When the broadcast expires, computed by the database's clock at the throw; null means
    /// never. It never changes after the throw, so every check reads it without a lock.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    public string RecipientsJson { get; set; } = "[]";
    public bool RecipientsTruncated { get; set; }
    public string ThrownByKind { get; set; } = string.Empty;
    public string ThrownBy { get; set; } = string.Empty;
    public string ThrownByRolesJson { get; set; } = "[]";
    public string? ThrownByClaimsJson { get; set; }
    public string? IdempotencyKey { get; set; }
    public byte[]? RequestHash { get; set; }

    /// <summary>
    /// The instance whose throw or end event threw the signal, when <see cref="ThrownByKind"/> is
    /// <c>instance</c>. A plain column, as <see cref="InstanceId"/> is.
    /// </summary>
    public long? ThrowerInstanceId { get; set; }
    public int? ThrowerNodeId { get; set; }
    public string? ThrowerNodeName { get; set; }

    /// <summary>
    /// The automatic chain count a delivery gives its receiving token: the thrower's count plus
    /// one for a throw from a workflow, and 0 for a throw through the API.
    /// </summary>
    public int AutomaticActivationCount { get; set; }
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
/// One frozen recipient of a broadcast: a wait, or the start of a workflow family. A wait
/// names its subscription, instance, and token by plain ids: a foreign key would make the
/// throw wait for every recipient whose transaction holds its instance and token locks. A
/// start names none of them; its instance is the one it created, once it is delivered.
/// </summary>
public sealed class SignalDeliveryEntity
{
    public long Id { get; set; }
    public long BroadcastId { get; set; }
    public SignalBroadcastEntity? Broadcast { get; set; }

    /// <summary><c>wait</c> or <c>start</c>.</summary>
    public string Kind { get; set; } = "wait";
    public long? SubscriptionId { get; set; }
    public long? InstanceId { get; set; }
    public long WorkflowDefinitionId { get; set; }
    public string WorkflowKey { get; set; } = string.Empty;
    public long? TokenId { get; set; }
    public Guid? ActivationId { get; set; }
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
