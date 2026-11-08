using System.Text.Json.Serialization;

namespace Flowbit.Shared.Dtos;

/// <summary>Case-sensitive lifecycle event names. Only <see cref="Subscribable"/> types may be subscribed to.</summary>
public static class LifecycleEventTypes
{
    public const string InstanceStarted = "instance.started";
    public const string InstanceCompleted = "instance.completed";
    public const string InstanceFaulted = "instance.faulted";
    public const string InstanceCancelled = "instance.cancelled";
    public const string InstanceReactivated = "instance.reactivated";
    public const string TaskCreated = "task.created";
    public const string TaskAssigned = "task.assigned";
    public const string TaskCompleted = "task.completed";
    public const string TaskCancelled = "task.cancelled";
    public const string DelegationGranted = "delegation.granted";
    public const string IncidentOpened = "incident.opened";

    /// <summary>Queued only by the test route; never matched by a subscription's event set.</summary>
    public const string WebhookTest = "webhook.test";

    public static readonly IReadOnlyList<string> Subscribable =
    [
        InstanceStarted, InstanceCompleted, InstanceFaulted, InstanceCancelled, InstanceReactivated,
        TaskCreated, TaskAssigned, TaskCompleted, TaskCancelled, DelegationGranted, IncidentOpened
    ];
}

public static class WebhookSubscriptionStates
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Archived = "archived";
}

public static class WebhookPauseReasons
{
    public const string Administrator = "administrator";

    /// <summary>The receiver answered 410 Gone.</summary>
    public const string ReceiverGone = "receiverGone";

    /// <summary>The receiver failed for 72 hours of continuous backoff without a success.</summary>
    public const string ReceiverUnavailable = "receiverUnavailable";
}

public static class WebhookDeliveryStates
{
    public const string Pending = "pending";
    public const string RetryScheduled = "retryScheduled";
    public const string Delivering = "delivering";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Discarded = "discarded";

    public static readonly IReadOnlyList<string> All =
        [Pending, RetryScheduled, Delivering, Succeeded, Failed, Discarded];
}

public static class WebhookSigningKeyStatuses
{
    public const string Current = "current";
    public const string Previous = "previous";
    public const string Revoked = "revoked";
    public const string Expired = "expired";
    public const string Retired = "retired";
}

/// <summary>
/// Safe outcome and failure codes for attempts and deliveries. They never carry remote text,
/// destination paths, or exception messages.
/// </summary>
public static class WebhookOutcomeCodes
{
    public const string Succeeded = "succeeded";
    public const string HttpRetryable = "http_retryable";
    public const string ReceiverRejected = "receiver_rejected";
    public const string ReceiverGone = "receiver_gone";
    public const string RedirectRefused = "redirect_refused";
    public const string Timeout = "timeout";
    public const string NetworkError = "network_error";
    public const string TlsError = "tls_error";
    public const string DestinationDenied = "destination_denied";
    public const string SigningUnavailable = "signing_unavailable";
    public const string ResponseHeadersTooLarge = "response_headers_too_large";
    public const string AbortedBeforeSend = "abortedBeforeSend";
    public const string OutcomeUnknown = "outcomeUnknown";
    public const string AttemptsExhausted = "attempts_exhausted";
    public const string DeadlineExceeded = "deadline_exceeded";
    public const string RetryAfterBeyondDeadline = "retry_after_beyond_deadline";

    /// <summary>
    /// The codes a failed delivery can carry in <c>FailureCode</c>: every final outcome that is neither a success nor
    /// retried, plus the three budget codes that end a generation. Retryable outcomes never become failure codes.
    /// </summary>
    public static readonly IReadOnlyList<string> FailureCodes =
    [
        ReceiverRejected, ReceiverGone, RedirectRefused, TlsError, DestinationDenied, SigningUnavailable,
        ResponseHeadersTooLarge, AttemptsExhausted, DeadlineExceeded, RetryAfterBeyondDeadline
    ];
}

public sealed record LifecycleEventFieldDto(string Name, string Type, bool Nullable, string Description);

public sealed record LifecycleEventTypeDto(
    string Type,
    string Description,
    bool Subscribable,
    IReadOnlyList<LifecycleEventFieldDto> DataFields);

public sealed record LifecycleEventCaptureDto(bool Enabled, DateTimeOffset ChangedAt, string? ChangedBy);

/// <summary>The event catalog, schema version, envelope fields, and the read-only database capture switch.</summary>
public sealed record WebhookEventCatalogDto(
    int SchemaVersion,
    IReadOnlyList<LifecycleEventFieldDto> EnvelopeFields,
    IReadOnlyList<LifecycleEventTypeDto> EventTypes,
    LifecycleEventCaptureDto Capture);

/// <summary>
/// One immutable delivery configuration. <c>EndpointUrl</c> may carry a receiver token in its path or query and
/// is returned only to settings administrators; <c>Origin</c> is the scheme, host, and port that is safe to log.
/// Like every webhook DTO that holds a full URL or a secret, directly or through a nested record, its string form
/// is only the type name, so logging it cannot disclose either.
/// </summary>
public sealed record WebhookDestinationDto(
    long RevisionId,
    int ConfigurationRevision,
    string EndpointUrl,
    string Origin,
    IReadOnlyList<string> EventTypes,
    string? WorkflowKey,
    DateTimeOffset CreatedAt,
    string CreatedBy)
{
    public override string ToString() => nameof(WebhookDestinationDto);
}

public sealed record WebhookDestinationRevisionDto(
    WebhookDestinationDto Destination,
    bool IsCurrent,
    long OutstandingDeliveries)
{
    public override string ToString() => nameof(WebhookDestinationRevisionDto);
}

/// <summary>Signing-key metadata. Secret material is never returned after the one-time create or rotate response.</summary>
public sealed record WebhookSigningKeyDto(
    Guid Id,
    string Status,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? OverlapExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevokedBy,
    DateTimeOffset? RetiredAt);

public sealed record WebhookReceiverHealthDto(
    int ConsecutiveFailures,
    DateTimeOffset? BackoffStartedAt,
    DateTimeOffset? BackoffUntil,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastOutcome,
    int? LastHttpStatus,
    DateTimeOffset? LastOutcomeAt);

/// <summary>Outstanding work only; settled deliveries are not counted so the list stays cheap at any volume.</summary>
public sealed record WebhookQueueCountsDto(
    long Pending,
    long RetryScheduled,
    long Delivering,
    long Failed);

public sealed record WebhookSubscriptionDto(
    Guid Id,
    string Name,
    string? Description,
    string State,
    string? PauseReason,
    int MaxConcurrency,
    long Revision,
    WebhookDestinationDto Destination,
    IReadOnlyList<WebhookSigningKeyDto> SigningKeys,
    WebhookReceiverHealthDto Health,
    WebhookQueueCountsDto Queue,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset UpdatedAt,
    string UpdatedBy,
    DateTimeOffset? ArchivedAt,
    string? ArchivedBy,
    DateTimeOffset? ArchiveSettledAt)
{
    public override string ToString() => nameof(WebhookSubscriptionDto);
}

public sealed record WebhookSubscriptionDetailDto(
    WebhookSubscriptionDto Subscription,
    IReadOnlyList<WebhookDestinationRevisionDto> Revisions)
{
    public override string ToString() => nameof(WebhookSubscriptionDetailDto);
}

public sealed record CreateWebhookSubscriptionRequest(
    string Name,
    string? Description,
    string EndpointUrl,
    IReadOnlyList<string> EventTypes,
    string? WorkflowKey,
    int? MaxConcurrency)
{
    public override string ToString() => nameof(CreateWebhookSubscriptionRequest);
}

public sealed record UpdateWebhookSubscriptionRequest(
    string Name,
    string? Description,
    string EndpointUrl,
    IReadOnlyList<string> EventTypes,
    string? WorkflowKey,
    int? MaxConcurrency,
    long ExpectedRevision)
{
    public override string ToString() => nameof(UpdateWebhookSubscriptionRequest);
}

public sealed record WebhookRevisionRequest(long ExpectedRevision);

public sealed record RotateWebhookSecretRequest(long ExpectedRevision, int? OverlapSeconds);

/// <summary>
/// The one-time secret returned by create and rotate. Receivers verify Standard Webhooks signatures with
/// <c>Secret</c>, a <c>whsec_</c>-prefixed Base64 value. No route returns it again.
/// </summary>
public sealed record WebhookSecretResultDto(WebhookSubscriptionDto Subscription, Guid KeyId, string Secret)
{
    public override string ToString() => nameof(WebhookSecretResultDto);
}

public sealed record WebhookTestResultDto(Guid DeliveryId, Guid EventId, string State);

/// <summary>
/// Delivery-list filters shared by the bulk retry and discard routes of one subscription. An unknown member is
/// refused rather than ignored, so a misspelled filter cannot widen a bulk action.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WebhookBulkDeliveryRequest(
    string? EventType,
    string? WorkflowKey,
    long? InstanceId,
    long? RevisionId,
    string? FailureCode,
    DateTimeOffset? From,
    DateTimeOffset? To);

public sealed record WebhookBulkDeliveryResultDto(int Matched, int Changed, int Skipped, bool HasMore);

public sealed record WebhookDeliveryDto(
    Guid Id,
    Guid EventId,
    Guid SubscriptionId,
    string SubscriptionName,
    long RevisionId,
    int ConfigurationRevision,
    string DestinationOrigin,
    string EventType,
    bool IsTest,
    string? WorkflowKey,
    long? InstanceId,
    DateTimeOffset OccurredAt,
    string State,
    DateTimeOffset? DueAt,
    int RetryGeneration,
    int GenerationAttempts,
    int LifetimeAttempts,
    string? LastOutcome,
    int? LastHttpStatus,
    string? FailureCode,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? SettledAt,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One delivery with its exact external body and captured destination. Internal recipient identifiers are
/// never included.
/// </summary>
public sealed record WebhookDeliveryDetailDto(
    WebhookDeliveryDto Delivery,
    string EndpointUrl,
    string Body,
    string SubscriptionState,
    DateTimeOffset? GenerationStartedAt,
    DateTimeOffset? DeadlineAt,
    int MaxAttemptsPerGeneration,
    bool CanRetry,
    bool CanDiscard)
{
    public override string ToString() => nameof(WebhookDeliveryDetailDto);
}

public sealed record WebhookDeliveryAttemptDto(
    long Id,
    int AttemptNumber,
    int RetryGeneration,
    long LeaseGeneration,
    IReadOnlyList<Guid> SigningKeyIds,
    DateTimeOffset AcquiredAt,
    DateTimeOffset? SendIntentAt,
    DateTimeOffset? CompletedAt,
    string? Outcome,
    int? HttpStatus,
    int? DurationMilliseconds);

public sealed record WebhookDeliveryActionResultDto(Guid Id, string State, long Revision, int RetryGeneration);
