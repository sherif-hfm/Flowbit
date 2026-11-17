namespace Flowbit.Service.Models;

public static class SignalSubscriptionStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}

public static class SignalBroadcastJobReasons
{
    public const string Throw = "throw";
    public const string Retry = "retry";
}

/// <summary>Engine settings that govern signals.</summary>
public static class SignalSettings
{
    public const string RequiredRoleKey = "Signals.RequiredRole";
    public const string DefaultRequiredRole = "admin";
    public const string MaxSubscriptionsPerBroadcastKey = "Signals.MaxSubscriptionsPerBroadcast";
    public const int DefaultMaxSubscriptionsPerBroadcast = 10_000;
    public const int MaxSubscriptionsPerBroadcastCap = 100_000;
    public const string MaxOpenBroadcastsKey = "Signals.MaxOpenBroadcasts";
    public const int DefaultMaxOpenBroadcasts = 20;
    public const int MaxOpenBroadcastsCap = 1_000;

    /// <summary>
    /// Reads a positive whole number: a missing or invalid value gives the default,
    /// and a larger one is capped.
    /// </summary>
    public static int ParseLimit(string? value, int fallback, int cap) =>
        int.TryParse(value?.Trim(), out var parsed) && parsed > 0
            ? Math.Min(parsed, cap)
            : fallback;

    /// <summary>The comma-separated roles, with <c>admin</c> for a missing or blank value.</summary>
    public static IReadOnlyList<string> ParseRoles(string? value)
    {
        var roles = (string.IsNullOrWhiteSpace(value) ? DefaultRequiredRole : value)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return roles.Length == 0 ? [DefaultRequiredRole] : roles;
    }
}

public sealed record SignalSubscriptionCreateRecord
{
    public required long InstanceId { get; init; }
    public required long WorkflowDefinitionId { get; init; }
    public required string WorkflowKey { get; init; }
    public required long TokenId { get; init; }
    public required Guid ActivationId { get; init; }
    public required int SignalNodeId { get; init; }
    public required string SignalNodeName { get; init; }
    public int? AttachedToNodeId { get; init; }
    public required int OutgoingFlowId { get; init; }
    public required string SignalName { get; init; }
    public required bool CancelActivity { get; init; }
}

public sealed record SignalSubscriptionRecord(
    long Id,
    long InstanceId,
    long WorkflowDefinitionId,
    string WorkflowKey,
    long TokenId,
    Guid ActivationId,
    int SignalNodeId,
    string SignalNodeName,
    int? AttachedToNodeId,
    int OutgoingFlowId,
    string SignalName,
    bool CancelActivity,
    long Occurrence,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt)
{
    public bool IsBoundary => AttachedToNodeId is not null;
}

public sealed record SignalSubscriptionCountRecord(string SignalName, string WorkflowKey, int Count);

public sealed record NewSignalBroadcastRecord(
    string SignalName,
    string? WorkflowKey,
    string? PayloadJson,
    string ThrownByKind,
    string ThrownBy,
    string ThrownByRolesJson,
    string? ThrownByClaimsJson,
    string? IdempotencyKey,
    byte[]? RequestHash,
    DateTimeOffset CreatedAt);

public sealed record SignalBroadcastRecord(
    long Id,
    string SignalName,
    string? WorkflowKey,
    string? PayloadJson,
    string Status,
    int TotalDeliveryCount,
    int DeliveredCount,
    int SkippedCount,
    int FailedCount,
    int CancelledCount,
    string RecipientsJson,
    bool RecipientsTruncated,
    string ThrownByKind,
    string ThrownBy,
    string ThrownByRolesJson,
    string? ThrownByClaimsJson,
    string? IdempotencyKey,
    byte[]? RequestHash,
    string? CancelledBy,
    string? CancellationReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelledAt)
{
    public int PendingCount =>
        Math.Max(0, TotalDeliveryCount - DeliveredCount - SkippedCount - FailedCount - CancelledCount);
}

public sealed record SignalBroadcastCountsRecord(
    int Pending,
    int Delivered,
    int Skipped,
    int Failed,
    int Cancelled);

/// <summary>The recipients a throw froze for one workflow definition.</summary>
public sealed record SignalRecipientDefinitionRecord(
    long WorkflowDefinitionId,
    string WorkflowKey,
    int Count);

public sealed record SignalDeliveryRecord(
    long Id,
    long BroadcastId,
    long SubscriptionId,
    long InstanceId,
    long WorkflowDefinitionId,
    string WorkflowKey,
    long TokenId,
    Guid ActivationId,
    int SignalNodeId,
    int? AttachedToNodeId,
    bool CancelActivity,
    string Status,
    int AttemptCount,
    string? ResultCode,
    string? ResultDescription,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

public sealed record SignalBroadcastQuery(
    string? Name,
    string? WorkflowKey,
    string? Status,
    string? ThrownByKind,
    string? ThrownBy,
    int Page,
    int PageSize);

public sealed record SignalDeliveryQuery(
    long BroadcastId,
    string? Status,
    long? InstanceId,
    int Page,
    int PageSize);

/// <summary>The job payload of a <c>signalBroadcastDeliver</c> job.</summary>
public sealed record SignalBroadcastJobPayload(long BroadcastId, long WorkflowDefinitionId);

/// <summary>The delivery backlog the Worker samples: pending deliveries and the oldest unfinished broadcast.</summary>
public sealed record SignalBacklogRecord(long PendingDeliveries, DateTimeOffset? OldestOpenBroadcastAt, DateTimeOffset ObservedAt);

/// <summary>How one delivery ended, as the executor reports it.</summary>
public sealed record SignalDeliveryOutcome(string Status, string? ResultCode, string? ResultDescription);
