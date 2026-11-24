using System.Text.Json;

namespace Flowbit.Shared.Dtos;

public static class SignalBroadcastStatuses
{
    public const string Delivering = "delivering";
    public const string Completed = "completed";
    public const string CompletedWithIssues = "completedWithIssues";

    /// <summary>
    /// No delivery is pending, and the broadcast's expiry ended at least one (EVENT-04E).
    /// Only a broadcast thrown with <c>expiresAfter</c> can have it.
    /// </summary>
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } =
        [Delivering, Completed, CompletedWithIssues, Expired, Cancelled];

    public static bool IsFinished(string status) =>
        status is Completed or CompletedWithIssues or Expired or Cancelled;
}

public static class SignalDeliveryStatuses
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } =
        [Pending, Delivered, Skipped, Failed, Cancelled];
}

/// <summary>The result codes a delivery ends with.</summary>
public static class SignalDeliveryResultCodes
{
    public const string Caught = "caught";
    public const string Interrupted = "interrupted";
    public const string Branched = "branched";
    public const string Superseded = "superseded";
    public const string SubscriptionEnded = "subscription_ended";
    public const string Stale = "stale";
    public const string PayloadInvalid = "signal_payload_invalid";
    public const string DeliveryRejected = "signal_delivery_rejected";
    public const string TokenLimit = "signal_token_limit";
    public const string DeliveryError = "delivery_error";
    public const string LeaseExhausted = "lease_exhausted";
    public const string BroadcastCancelled = "broadcast_cancelled";

    /// <summary>
    /// The broadcast's expiry passed before the delivery ran: a skipped delivery that wrote
    /// nothing, created no instance, and left its wait open.
    /// </summary>
    public const string BroadcastExpired = "broadcast_expired";

    /// <summary>A start delivery created an instance.</summary>
    public const string Started = "started";

    /// <summary>A start delivery was skipped because an instance already has its business key.</summary>
    public const string BusinessKeyConflict = "business_key_conflict";
}

/// <summary>What a frozen recipient is: a wait of an instance, or the start of a workflow family.</summary>
public static class SignalDeliveryKinds
{
    public const string Wait = "wait";
    public const string Start = "start";

    public static IReadOnlyList<string> All { get; } = [Wait, Start];
}

public static class SignalThrowerKinds
{
    public const string User = "user";
    public const string Client = "client";

    /// <summary>A workflow instance's signal throw or end event.</summary>
    public const string Instance = "instance";
}

public static class SignalConstraints
{
    /// <summary>The largest payload, counted as the bytes PostgreSQL prints for the stored jsonb.</summary>
    public const int MaxPayloadBytes = 262_144;
    public const int MaxIdempotencyKeyLength = 300;
    public const int MaxReasonLength = 1000;
    public const int MaxResultDescriptionLength = 1000;

    /// <summary>The most families a broadcast lists in its recipients.</summary>
    public const int MaxRecipientFamilies = 50;

    /// <summary>The most rows the subscription summary returns.</summary>
    public const int MaxSummaryRows = 200;

    /// <summary>The most active subscriptions instance detail lists.</summary>
    public const int MaxInstanceSubscriptions = 50;
}

/// <summary>
/// Throws a signal. The request states exactly one scope: a <see cref="WorkflowKey"/>,
/// that family with a <see cref="BusinessKey"/>, <see cref="AllWorkflows"/>, or an
/// <see cref="InstanceId"/> alone.
/// </summary>
/// <param name="Name">The signal name: 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.</param>
/// <param name="WorkflowKey">The workflow family whose waits receive the signal. Required with businessKey; not combined with allWorkflows or instanceId.</param>
/// <param name="AllWorkflows">True to reach the waits of every workflow family. Not combined with workflowKey, businessKey, or instanceId.</param>
/// <param name="Payload">An optional JSON object of at most 256 KiB that the receiving events' output mappings read.</param>
/// <param name="IdempotencyKey">An optional key, unique per thrower, that makes a repeated request return the first broadcast.</param>
/// <param name="BusinessKey">Limits the signal to the instance of workflowKey with this business key. Trimmed, then compared exactly and case-sensitively; 1 to 300 characters.</param>
/// <param name="InstanceId">Limits the signal to the waits of one instance. States the whole scope.</param>
/// <param name="ExpiresAfter">An optional fixed ISO-8601 duration from PT1S to P365D, such as PT10M or P1D. The broadcast expires that long after the database's time at the throw; deliveries that have not run by then are skipped with broadcast_expired and their waits stay open. Null or absent means the broadcast never expires.</param>
public sealed record ThrowSignalRequest(
    string? Name,
    string? WorkflowKey = null,
    bool AllWorkflows = false,
    JsonElement? Payload = null,
    string? IdempotencyKey = null,
    string? BusinessKey = null,
    long? InstanceId = null,
    string? ExpiresAfter = null);

/// <summary>The recipients of a broadcast in one workflow family.</summary>
/// <param name="WorkflowKey">The family.</param>
/// <param name="Count">Its recipients: waits and its start, if any.</param>
/// <param name="StartCount">1 when the broadcast starts the family, otherwise 0. Entries stored before signal starts existed read as 0.</param>
public sealed record SignalRecipientCountDto(string WorkflowKey, int Count, int StartCount = 0);

/// <summary>A broadcast in a list, without its payload.</summary>
/// <param name="Id">The broadcast ID.</param>
/// <param name="Name">The signal name.</param>
/// <param name="WorkflowKey">The family the scope names, or the family of the scope's instance; null for a broadcast to all workflows.</param>
/// <param name="AllWorkflows">True when the broadcast addressed every workflow family.</param>
/// <param name="BusinessKey">The trimmed business key a business-key scope names; otherwise null.</param>
/// <param name="InstanceId">The instance an instance scope names; otherwise null.</param>
/// <param name="Status">delivering, completed, completedWithIssues, expired, or cancelled.</param>
/// <param name="TotalDeliveryCount">The recipients the throw froze.</param>
/// <param name="PendingCount">Deliveries not yet finished.</param>
/// <param name="DeliveredCount">Deliveries that reached their wait.</param>
/// <param name="SkippedCount">Deliveries whose wait had ended or whose instance had changed, including those the expiry ended.</param>
/// <param name="FailedCount">Deliveries that failed.</param>
/// <param name="CancelledCount">Deliveries cancelled with the broadcast.</param>
/// <param name="ThrownBy">The user's actor identity, the managed client's ID, or the throwing instance's ID as text.</param>
/// <param name="ThrownByKind">user, client, or instance.</param>
/// <param name="CreatedAt">When the signal was thrown.</param>
/// <param name="UpdatedAt">The last change to the counters or status.</param>
/// <param name="CompletedAt">When the broadcast finished or was cancelled; null while delivering.</param>
/// <param name="ThrownByInstanceId">The throwing instance when thrownByKind is instance; otherwise null.</param>
/// <param name="ThrownByNodeId">The throw or end event that threw the signal when thrownByKind is instance; otherwise null.</param>
/// <param name="ThrownByNodeName">That event's name when it threw; otherwise null.</param>
/// <param name="StartCount">How many of the recipients are starts of a workflow family; each creates an instance when it is delivered.</param>
/// <param name="ExpiresAt">When the broadcast expires, computed by the database at the throw; null when it never expires. Deliveries that have not run by then are skipped with broadcast_expired.</param>
/// <param name="ExpiredCount">How many of the skipped deliveries the expiry ended.</param>
public sealed record SignalBroadcastSummaryDto(
    long Id,
    string Name,
    string? WorkflowKey,
    bool AllWorkflows,
    string? BusinessKey,
    long? InstanceId,
    string Status,
    int TotalDeliveryCount,
    int PendingCount,
    int DeliveredCount,
    int SkippedCount,
    int FailedCount,
    int CancelledCount,
    string ThrownBy,
    string ThrownByKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    long? ThrownByInstanceId = null,
    int? ThrownByNodeId = null,
    string? ThrownByNodeName = null,
    int StartCount = 0,
    DateTimeOffset? ExpiresAt = null,
    int ExpiredCount = 0);

/// <summary>One broadcast with its counts, thrower, payload, and recipients by workflow family.</summary>
/// <param name="Id">The broadcast ID.</param>
/// <param name="Name">The signal name.</param>
/// <param name="WorkflowKey">The family the scope names, or the family of the scope's instance; null for a broadcast to all workflows.</param>
/// <param name="AllWorkflows">True when the broadcast addressed every workflow family.</param>
/// <param name="BusinessKey">The trimmed business key a business-key scope names; otherwise null.</param>
/// <param name="InstanceId">The instance an instance scope names; otherwise null.</param>
/// <param name="Status">delivering, completed, completedWithIssues, expired, or cancelled.</param>
/// <param name="TotalDeliveryCount">The recipients the throw froze.</param>
/// <param name="PendingCount">Deliveries not yet finished.</param>
/// <param name="DeliveredCount">Deliveries that reached their wait.</param>
/// <param name="SkippedCount">Deliveries whose wait had ended or whose instance had changed, including those the expiry ended.</param>
/// <param name="FailedCount">Deliveries that failed; they can be retried until the broadcast's expiry passes.</param>
/// <param name="CancelledCount">Deliveries cancelled with the broadcast.</param>
/// <param name="ThrownBy">The user's actor identity, the managed client's ID, or the throwing instance's ID as text.</param>
/// <param name="ThrownByKind">user, client, or instance.</param>
/// <param name="ThrownByRoles">The user's roles when it threw; empty for a client or an instance.</param>
/// <param name="Payload">The JSON object the throw sent, or null.</param>
/// <param name="RecipientsByWorkflowKey">Recipients per workflow family, most first, at most 50.</param>
/// <param name="RecipientsTruncated">True when more than 50 families received the signal.</param>
/// <param name="CancelledBy">Who cancelled the broadcast; null for a managed client when someone else cancelled it.</param>
/// <param name="CancellationReason">The cancellation reason; null for a managed client when someone else cancelled it.</param>
/// <param name="CreatedAt">When the signal was thrown.</param>
/// <param name="UpdatedAt">The last change to the counters or status; the fence of a retry.</param>
/// <param name="CompletedAt">When the broadcast finished or was cancelled; null while delivering.</param>
/// <param name="CancelledAt">When it was cancelled.</param>
/// <param name="ThrownByInstanceId">The throwing instance when thrownByKind is instance; otherwise null.</param>
/// <param name="ThrownByNodeId">The throw or end event that threw the signal when thrownByKind is instance; otherwise null.</param>
/// <param name="ThrownByNodeName">That event's name when it threw; otherwise null.</param>
/// <param name="StartCount">How many of the recipients are starts of a workflow family; each creates an instance when it is delivered.</param>
/// <param name="ExpiresAt">When the broadcast expires, computed by the database at the throw; null when it never expires. Deliveries that have not run by then are skipped with broadcast_expired.</param>
/// <param name="ExpiredCount">How many of the skipped deliveries the expiry ended.</param>
public sealed record SignalBroadcastDetailDto(
    long Id,
    string Name,
    string? WorkflowKey,
    bool AllWorkflows,
    string? BusinessKey,
    long? InstanceId,
    string Status,
    int TotalDeliveryCount,
    int PendingCount,
    int DeliveredCount,
    int SkippedCount,
    int FailedCount,
    int CancelledCount,
    string ThrownBy,
    string ThrownByKind,
    IReadOnlyList<string> ThrownByRoles,
    JsonElement? Payload,
    IReadOnlyList<SignalRecipientCountDto> RecipientsByWorkflowKey,
    bool RecipientsTruncated,
    string? CancelledBy,
    string? CancellationReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt,
    long? ThrownByInstanceId = null,
    int? ThrownByNodeId = null,
    string? ThrownByNodeName = null,
    int StartCount = 0,
    DateTimeOffset? ExpiresAt = null,
    int ExpiredCount = 0);

/// <summary>
/// One recipient of a broadcast: a wait of an instance, or the start of a workflow family.
/// A managed API client receives the result code without <see cref="ResultDescription"/>,
/// which can quote a validation expression of the receiving workflow.
/// </summary>
/// <param name="Id">The delivery ID.</param>
/// <param name="BroadcastId">The broadcast it belongs to.</param>
/// <param name="InstanceId">The waiting instance for a wait. For a start, the instance it created once it is delivered, and null before that or when it created none.</param>
/// <param name="WorkflowKey">The recipient's workflow family.</param>
/// <param name="WorkflowDefinitionId">The version of the wait, or the version that listened for a start. A start that ran names the version it started or tried to start; one skipped because the family no longer starts on the name keeps the version that listened.</param>
/// <param name="SignalNodeId">The signal catch, boundary, or start event; for a start, of that version.</param>
/// <param name="AttachedToNodeId">A boundary's host; otherwise null.</param>
/// <param name="CancelActivity">Whether a boundary interrupts its host; false for a start.</param>
/// <param name="Status">pending, delivered, skipped, failed, or cancelled.</param>
/// <param name="ResultCode">How the delivery ended, such as caught, started, or business_key_conflict; null while pending.</param>
/// <param name="ResultDescription">Why a delivery failed or was skipped; never returned to a managed client.</param>
/// <param name="AttemptCount">How many times the delivery was tried.</param>
/// <param name="CreatedAt">When the throw froze it.</param>
/// <param name="UpdatedAt">The last change.</param>
/// <param name="CompletedAt">When it finished; null while pending.</param>
/// <param name="Kind">wait or start.</param>
public sealed record SignalDeliveryDto(
    long Id,
    long BroadcastId,
    long? InstanceId,
    string WorkflowKey,
    long WorkflowDefinitionId,
    int SignalNodeId,
    int? AttachedToNodeId,
    bool CancelActivity,
    string Status,
    string? ResultCode,
    string? ResultDescription,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string Kind = SignalDeliveryKinds.Wait);

public sealed record CancelSignalBroadcastRequest(string? Reason = null);

/// <summary>
/// Queues a broadcast's failed deliveries again. <see cref="ExpectedUpdatedAt"/> is required:
/// the broadcast's <c>updatedAt</c> as last read.
/// </summary>
public sealed record RetrySignalDeliveriesRequest(DateTimeOffset? ExpectedUpdatedAt);

/// <summary>What a signal reaches in one workflow family now.</summary>
/// <param name="Name">The signal name.</param>
/// <param name="WorkflowKey">The family.</param>
/// <param name="Count">Its active waits.</param>
/// <param name="StartCount">1 when the family's published default starts on the signal, otherwise 0; always 0 with a businessKey or instanceId filter, which names existing instances.</param>
public sealed record SignalSubscriptionCountDto(string Name, string WorkflowKey, int Count, int StartCount = 0);

public sealed record SignalSubscriptionSummaryDto(
    IReadOnlyList<SignalSubscriptionCountDto> Items,
    bool Truncated);

/// <summary>An active wait for a signal, shown on instance detail.</summary>
public sealed record SignalSubscriptionDto(
    long Id,
    string Name,
    int SignalNodeId,
    string SignalNodeName,
    int? AttachedToNodeId,
    bool CancelActivity,
    long TokenId,
    DateTimeOffset CreatedAt);
