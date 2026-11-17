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
/// Throws a signal. Exactly one of <see cref="WorkflowKey"/> and
/// <see cref="AllWorkflows"/> states the scope.
/// </summary>
public sealed record ThrowSignalRequest(
    string? Name,
    string? WorkflowKey = null,
    bool AllWorkflows = false,
    JsonElement? Payload = null,
    string? IdempotencyKey = null);

public sealed record SignalRecipientCountDto(string WorkflowKey, int Count);

public sealed record SignalBroadcastSummaryDto(
    long Id,
    string Name,
    string? WorkflowKey,
    bool AllWorkflows,
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

public sealed record SignalBroadcastDetailDto(
    long Id,
    string Name,
    string? WorkflowKey,
    bool AllWorkflows,
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
