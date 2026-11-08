namespace Flowbit.Infrastructure.Entities;

/// <summary>
/// One committed lifecycle transition that matched at least one active target. Written only by the capture
/// triggers (and the webhook test function); <see cref="Body"/> holds the exact signed bytes.
/// </summary>
public sealed class LifecycleEventEntity
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string SourceTable { get; set; } = string.Empty;
    public long? SourceId { get; set; }
    public string? WorkflowKey { get; set; }
    public long? WorkflowDefinitionId { get; set; }
    public int? WorkflowVersion { get; set; }

    // Immutable references rather than foreign keys: delivery must survive runtime
    // retention, and capture must never wait on another instance's row lock.
    public long? InstanceId { get; set; }
    public long? UserTaskId { get; set; }
    public byte[] Body { get; set; } = [];
    public List<string> RecipientUserIds { get; set; } = [];

    // Captured subscription revisions. No foreign key: an uncommitted capture
    // holds no fence, so revisions are retained indefinitely in this release.
    public long[] TargetRevisionIds { get; set; } = [];
    public DateTimeOffset? FanoutCompletedAt { get; set; }
}

public sealed class WebhookSubscriptionEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public long? CurrentRevisionId { get; set; }
    public WebhookSubscriptionRevisionEntity? CurrentRevision { get; set; }
    public string State { get; set; } = "paused";
    public string? PauseReason { get; set; }
    public int MaxConcurrency { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTimeOffset? ArchivedAt { get; set; }
    public string? ArchivedBy { get; set; }
    public DateTimeOffset? ArchiveSettledAt { get; set; }
}

/// <summary>An immutable delivery configuration that captures and deliveries keep referring to.</summary>
public sealed class WebhookSubscriptionRevisionEntity
{
    public long Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public WebhookSubscriptionEntity? Subscription { get; set; }
    public int ConfigurationRevision { get; set; }
    public string EndpointUrl { get; set; } = string.Empty;
    public List<string> EventTypes { get; set; } = [];
    public string? WorkflowKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>Receiver health written by the dispatcher and by resume, kept off the management revision.</summary>
public sealed class WebhookSubscriptionDispatchStateEntity
{
    public Guid SubscriptionId { get; set; }
    public WebhookSubscriptionEntity? Subscription { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? BackoffStartedAt { get; set; }
    public DateTimeOffset? BackoffUntil { get; set; }
    public int BackoffSeconds { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public string? LastOutcome { get; set; }
    public int? LastHttpStatus { get; set; }
    public DateTimeOffset? LastOutcomeAt { get; set; }
    public DateTimeOffset? LastAcquiredAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WebhookSigningKeyEntity
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public WebhookSubscriptionEntity? Subscription { get; set; }

    /// <summary>The Data Protection payload of the 32-byte secret; cleared after removal.</summary>
    public byte[]? ProtectedSecret { get; set; }
    public string Status { get; set; } = "current";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset? OverlapExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public DateTimeOffset? RemoveSecretNotBefore { get; set; }
    public DateTimeOffset? SecretRemovedAt { get; set; }
}

public sealed class WebhookDeliveryEntity
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public LifecycleEventEntity? Event { get; set; }
    public Guid SubscriptionId { get; set; }
    public WebhookSubscriptionEntity? Subscription { get; set; }
    public long RevisionId { get; set; }
    public WebhookSubscriptionRevisionEntity? DestinationRevision { get; set; }
    public bool IsTest { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? WorkflowKey { get; set; }
    public long? InstanceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string State { get; set; } = "pending";
    public DateTimeOffset DueAt { get; set; }
    public int RetryGeneration { get; set; }
    public DateTimeOffset? GenerationStartedAt { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
    public int GenerationAttempts { get; set; }
    public int LifetimeAttempts { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public long LeaseGeneration { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>The state a released lease returns to: pending or retryScheduled.</summary>
    public string? LeasedFromState { get; set; }
    public string? LastOutcome { get; set; }
    public int? LastHttpStatus { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WebhookDeliveryAttemptEntity
{
    public long Id { get; set; }
    public Guid DeliveryId { get; set; }
    public WebhookDeliveryEntity? Delivery { get; set; }
    public int AttemptNumber { get; set; }
    public int RetryGeneration { get; set; }
    public long LeaseGeneration { get; set; }
    public Guid[] SigningKeyIds { get; set; } = [];
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset? SendIntentAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Outcome { get; set; }
    public int? HttpStatus { get; set; }
    public int? DurationMilliseconds { get; set; }
}

/// <summary>
/// Append-only management audit. It never holds keys, signatures, payloads, URL paths or queries, or remote
/// error text.
/// </summary>
public sealed class WebhookManagementAuditEntity
{
    public long Id { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public Guid? SubscriptionId { get; set; }
    public Guid? DeliveryId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? ChangesJson { get; set; }
}

/// <summary>The certificate-wrapped Data Protection key ring that protects subscriber secrets.</summary>
public sealed class WebhookDataProtectionKeyEntity
{
    public int Id { get; set; }
    public string? FriendlyName { get; set; }
    public string Xml { get; set; } = string.Empty;
}
