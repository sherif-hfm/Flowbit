using System.Text.Json;

namespace Flowbit.Shared.Dtos;

public static class SignalBroadcastStatuses
{
    public const string Delivering = "delivering";
    public const string Completed = "completed";
    public const string CompletedWithIssues = "completedWithIssues";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } =
        [Delivering, Completed, CompletedWithIssues, Cancelled];

    public static bool IsFinished(string status) =>
        status is Completed or CompletedWithIssues or Cancelled;
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
}

public static class SignalThrowerKinds
{
    public const string User = "user";
    public const string Client = "client";
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
public sealed record ThrowSignalRequest(
    string? Name,
    string? WorkflowKey = null,
    bool AllWorkflows = false,
    JsonElement? Payload = null,
    string? IdempotencyKey = null,
    string? BusinessKey = null,
    long? InstanceId = null);

public sealed record SignalRecipientCountDto(string WorkflowKey, int Count);

/// <summary>A broadcast in a list, without its payload.</summary>
/// <param name="Id">The broadcast ID.</param>
/// <param name="Name">The signal name.</param>
/// <param name="WorkflowKey">The family the scope names, or the family of the scope's instance; null for a broadcast to all workflows.</param>
/// <param name="AllWorkflows">True when the broadcast addressed every workflow family.</param>
/// <param name="BusinessKey">The trimmed business key a business-key scope names; otherwise null.</param>
/// <param name="InstanceId">The instance an instance scope names; otherwise null.</param>
/// <param name="Status">delivering, completed, completedWithIssues, or cancelled.</param>
/// <param name="TotalDeliveryCount">The recipients the throw froze.</param>
/// <param name="PendingCount">Deliveries not yet finished.</param>
/// <param name="DeliveredCount">Deliveries that reached their wait.</param>
/// <param name="SkippedCount">Deliveries whose wait had ended or whose instance had changed.</param>
/// <param name="FailedCount">Deliveries that failed.</param>
/// <param name="CancelledCount">Deliveries cancelled with the broadcast.</param>
/// <param name="ThrownBy">The user's actor identity, or the managed client's ID.</param>
/// <param name="ThrownByKind">user or client.</param>
/// <param name="CreatedAt">When the signal was thrown.</param>
/// <param name="UpdatedAt">The last change to the counters or status.</param>
/// <param name="CompletedAt">When the broadcast finished or was cancelled; null while delivering.</param>
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
    DateTimeOffset? CompletedAt);

/// <summary>One broadcast with its counts, thrower, payload, and recipients by workflow family.</summary>
/// <param name="Id">The broadcast ID.</param>
/// <param name="Name">The signal name.</param>
/// <param name="WorkflowKey">The family the scope names, or the family of the scope's instance; null for a broadcast to all workflows.</param>
/// <param name="AllWorkflows">True when the broadcast addressed every workflow family.</param>
/// <param name="BusinessKey">The trimmed business key a business-key scope names; otherwise null.</param>
/// <param name="InstanceId">The instance an instance scope names; otherwise null.</param>
/// <param name="Status">delivering, completed, completedWithIssues, or cancelled.</param>
/// <param name="TotalDeliveryCount">The recipients the throw froze.</param>
/// <param name="PendingCount">Deliveries not yet finished.</param>
/// <param name="DeliveredCount">Deliveries that reached their wait.</param>
/// <param name="SkippedCount">Deliveries whose wait had ended or whose instance had changed.</param>
/// <param name="FailedCount">Deliveries that failed; they can be retried.</param>
/// <param name="CancelledCount">Deliveries cancelled with the broadcast.</param>
/// <param name="ThrownBy">The user's actor identity, or the managed client's ID.</param>
/// <param name="ThrownByKind">user or client.</param>
/// <param name="ThrownByRoles">The user's roles when it threw; empty for a client.</param>
/// <param name="Payload">The JSON object the throw sent, or null.</param>
/// <param name="RecipientsByWorkflowKey">Recipients per workflow family, most first, at most 50.</param>
/// <param name="RecipientsTruncated">True when more than 50 families received the signal.</param>
/// <param name="CancelledBy">Who cancelled the broadcast; null for a managed client when someone else cancelled it.</param>
/// <param name="CancellationReason">The cancellation reason; null for a managed client when someone else cancelled it.</param>
/// <param name="CreatedAt">When the signal was thrown.</param>
/// <param name="UpdatedAt">The last change to the counters or status; the fence of a retry.</param>
/// <param name="CompletedAt">When the broadcast finished or was cancelled; null while delivering.</param>
/// <param name="CancelledAt">When it was cancelled.</param>
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
    DateTimeOffset? CancelledAt);

/// <summary>
/// One recipient of a broadcast. A managed API client receives the result code
/// without <see cref="ResultDescription"/>, which can quote a validation expression
/// of the receiving workflow.
/// </summary>
public sealed record SignalDeliveryDto(
    long Id,
    long BroadcastId,
    long InstanceId,
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
    DateTimeOffset? CompletedAt);

public sealed record CancelSignalBroadcastRequest(string? Reason = null);

/// <summary>
/// Queues a broadcast's failed deliveries again. <see cref="ExpectedUpdatedAt"/> is required:
/// the broadcast's <c>updatedAt</c> as last read.
/// </summary>
public sealed record RetrySignalDeliveriesRequest(DateTimeOffset? ExpectedUpdatedAt);

public sealed record SignalSubscriptionCountDto(string Name, string WorkflowKey, int Count);

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
