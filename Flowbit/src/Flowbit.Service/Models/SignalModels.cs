using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Models;

/// <summary>The four ways a throw can name its recipients.</summary>
public enum SignalBroadcastScopeKind
{
    AllWorkflows,
    Family,
    BusinessKey,
    Instance
}

/// <summary>
/// Whom a broadcast addresses: every family (no field set), one family
/// (<see cref="WorkflowKey"/>), the instance with one business key in a family
/// (<see cref="WorkflowKey"/> and <see cref="BusinessKey"/>), or one instance
/// (<see cref="InstanceId"/>). An instance scope learns its instance's family when the
/// throw resolves it (<see cref="WithWorkflowKey"/>); the family is recorded on the
/// broadcast but never selects recipients. <see cref="BusinessKey"/> is always the
/// trimmed key that <see cref="BusinessKeyRules"/> accepted.
/// </summary>
public sealed record SignalBroadcastScope
{
    private SignalBroadcastScope(string? workflowKey, string? businessKey, long? instanceId)
    {
        WorkflowKey = workflowKey;
        BusinessKey = businessKey;
        InstanceId = instanceId;
    }

    public string? WorkflowKey { get; }

    public string? BusinessKey { get; }

    public long? InstanceId { get; }

    public SignalBroadcastScopeKind Kind =>
        InstanceId is not null ? SignalBroadcastScopeKind.Instance
        : BusinessKey is not null ? SignalBroadcastScopeKind.BusinessKey
        : WorkflowKey is null ? SignalBroadcastScopeKind.AllWorkflows
        : SignalBroadcastScopeKind.Family;

    public static SignalBroadcastScope AllWorkflows { get; } = new(null, null, null);

    public static SignalBroadcastScope Family(string workflowKey) =>
        new(Required(workflowKey, nameof(workflowKey)), null, null);

    /// <summary>A business-key scope; <paramref name="businessKey"/> must already be normalized.</summary>
    public static SignalBroadcastScope ForBusinessKey(string workflowKey, string businessKey) =>
        new(Required(workflowKey, nameof(workflowKey)), Required(businessKey, nameof(businessKey)), null);

    public static SignalBroadcastScope ForInstance(long instanceId) =>
        instanceId > 0
            ? new(null, null, instanceId)
            : throw new ArgumentOutOfRangeException(nameof(instanceId), instanceId, "An instance ID is positive.");

    /// <summary>The same instance scope with its instance's family, read when the signal is thrown.</summary>
    public SignalBroadcastScope WithWorkflowKey(string workflowKey) =>
        Kind == SignalBroadcastScopeKind.Instance
            ? new(Required(workflowKey, nameof(workflowKey)), null, InstanceId)
            : throw new InvalidOperationException("Only an instance scope learns its family after it is built.");

    /// <summary>
    /// Builds the scope of a throw request, or returns the problem with its fields, in the
    /// order the API reference documents. <paramref name="allWorkflows"/> false counts as
    /// absent.
    /// </summary>
    public static SignalBroadcastScope? FromRequest(
        string? workflowKey,
        bool allWorkflows,
        string? businessKey,
        long? instanceId,
        out string? problem)
    {
        if (workflowKey is not null && string.IsNullOrWhiteSpace(workflowKey))
        {
            problem = "workflowKey cannot be blank; omit it and set allWorkflows to true to reach every workflow.";
            return null;
        }

        string? key = null;
        if (businessKey is not null)
        {
            switch (BusinessKeyRules.Normalize(businessKey, out var normalized))
            {
                case BusinessKeyProblem.Blank:
                    problem = "businessKey cannot be blank; omit it to reach every instance of the workflow family.";
                    return null;
                case BusinessKeyProblem.TooLong:
                    problem = TooLongMessage;
                    return null;
            }
            key = normalized;
        }

        if (instanceId is < 1)
        {
            problem = PositiveInstanceMessage;
            return null;
        }

        if (instanceId is long id)
        {
            if (workflowKey is not null || allWorkflows || key is not null)
            {
                problem = "instanceId states the whole scope; do not combine it with workflowKey, allWorkflows, or businessKey.";
                return null;
            }

            problem = null;
            return ForInstance(id);
        }

        if (key is not null && workflowKey is null)
        {
            problem = BusinessKeyNeedsFamilyMessage;
            return null;
        }

        if ((workflowKey is null) == !allWorkflows)
        {
            problem = "A signal must name one workflowKey, set allWorkflows to true, or name one instanceId.";
            return null;
        }

        problem = null;
        return workflowKey is null ? AllWorkflows
            : key is null ? Family(workflowKey)
            : ForBusinessKey(workflowKey, key);
    }

    /// <summary>
    /// Reads the filters of the subscription summary. A blank value means no filter, as
    /// for the summary's other parameters, and the summary has no allWorkflows.
    /// </summary>
    public static SignalSubscriptionFilter? SummaryFilter(
        string? workflowKey,
        string? businessKey,
        long? instanceId,
        out string? problem)
    {
        var family = string.IsNullOrWhiteSpace(workflowKey) ? null : workflowKey;
        string? key = null;
        if (!string.IsNullOrWhiteSpace(businessKey))
        {
            if (BusinessKeyRules.Normalize(businessKey, out var normalized) == BusinessKeyProblem.TooLong)
            {
                problem = TooLongMessage;
                return null;
            }
            key = normalized;
        }

        if (instanceId is < 1)
        {
            problem = PositiveInstanceMessage;
            return null;
        }

        if (instanceId is not null && (family is not null || key is not null))
        {
            problem = "instanceId cannot be combined with workflowKey or businessKey.";
            return null;
        }

        if (key is not null && family is null)
        {
            problem = BusinessKeyNeedsFamilyMessage;
            return null;
        }

        problem = null;
        return new SignalSubscriptionFilter(family, key, instanceId);
    }

    private const string TooLongMessage = "businessKey can have at most 300 characters.";
    private const string PositiveInstanceMessage = "instanceId must be a positive whole number.";
    private const string BusinessKeyNeedsFamilyMessage =
        "businessKey needs a workflowKey: a business key identifies an instance only within one workflow family.";

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value;
}

/// <summary>
/// The optional filters of the subscription summary: a family, the instance with a
/// normalized business key in that family, or one instance.
/// </summary>
public sealed record SignalSubscriptionFilter(string? WorkflowKey, string? BusinessKey, long? InstanceId);

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
    public const string MaxOpenWorkflowBroadcastsKey = "Signals.MaxOpenWorkflowBroadcasts";
    public const int DefaultMaxOpenWorkflowBroadcasts = 10_000;
    public const int MaxOpenWorkflowBroadcastsCap = 100_000;

    /// <summary>The thrower kinds whose broadcasts count toward <see cref="MaxOpenBroadcastsKey"/>.</summary>
    public static IReadOnlyList<string> ApiThrowerKinds { get; } = [SignalThrowerKinds.User, SignalThrowerKinds.Client];

    /// <summary>The thrower kinds whose broadcasts count toward <see cref="MaxOpenWorkflowBroadcastsKey"/>.</summary>
    public static IReadOnlyList<string> WorkflowThrowerKinds { get; } = [SignalThrowerKinds.Instance];

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

public sealed record SignalSubscriptionCountRecord(string SignalName, string WorkflowKey, int Count, int StartCount);

/// <summary>
/// A broadcast to insert. A throw from a workflow sets <see cref="ThrownByKind"/> to
/// <c>instance</c>, names its instance and node, and carries the chain count its deliveries
/// continue; a throw through the API leaves those null and 0.
/// </summary>
public sealed record NewSignalBroadcastRecord(
    string SignalName,
    SignalBroadcastScope Scope,
    string? PayloadJson,
    string ThrownByKind,
    string ThrownBy,
    string ThrownByRolesJson,
    string? ThrownByClaimsJson,
    string? IdempotencyKey,
    byte[]? RequestHash,
    DateTimeOffset CreatedAt,
    long? ThrowerInstanceId = null,
    int? ThrowerNodeId = null,
    string? ThrowerNodeName = null,
    int AutomaticActivationCount = 0);

public sealed record SignalBroadcastRecord(
    long Id,
    string SignalName,
    string? WorkflowKey,
    string? BusinessKey,
    long? InstanceId,
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
    DateTimeOffset? CancelledAt,
    long? ThrowerInstanceId = null,
    int? ThrowerNodeId = null,
    string? ThrowerNodeName = null,
    int AutomaticActivationCount = 0,
    int StartDeliveryCount = 0)
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

/// <summary>
/// The recipients a throw froze for one workflow definition: <see cref="Count"/> in all, of
/// which <see cref="StartCount"/> are starts of the family.
/// </summary>
public sealed record SignalRecipientDefinitionRecord(
    long WorkflowDefinitionId,
    string WorkflowKey,
    int Count,
    int StartCount = 0);

/// <summary>How many waits and family starts one freeze inserted.</summary>
public sealed record SignalFreezeResult(int Waits, int Starts)
{
    public int Total => Waits + Starts;
}

/// <summary>
/// What a retry changed: the definitions whose deliveries are pending again, and how many
/// failed deliveries it changed in all, including starts it ended as skipped.
/// </summary>
public sealed record SignalRetryResult(IReadOnlyList<long> DefinitionIds, int Changed);

/// <summary>One family that starts on a signal, through its published default's start event.</summary>
public sealed record SignalStartSubscriptionRecord(
    long Id,
    string WorkflowKey,
    string SignalName,
    long WorkflowDefinitionId,
    int SignalNodeId,
    string SignalNodeName);

/// <summary>
/// One frozen recipient. A <c>wait</c> names its subscription, instance, token, and
/// activation. A <c>start</c> names its family and the version and event that listened when
/// the signal was thrown; <see cref="InstanceId"/> is the instance it created, once delivered.
/// </summary>
public sealed record SignalDeliveryRecord(
    long Id,
    long BroadcastId,
    long? SubscriptionId,
    long? InstanceId,
    long WorkflowDefinitionId,
    string WorkflowKey,
    long? TokenId,
    Guid? ActivationId,
    int SignalNodeId,
    int? AttachedToNodeId,
    bool CancelActivity,
    string Status,
    int AttemptCount,
    string? ResultCode,
    string? ResultDescription,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string Kind = SignalDeliveryKinds.Wait)
{
    public bool IsStart => string.Equals(Kind, SignalDeliveryKinds.Start, StringComparison.Ordinal);
}

/// <summary>
/// A page of broadcasts. <see cref="ThrownByKind"/> with <see cref="ThrownBy"/> limits a
/// managed client to its own throws; <see cref="ThrownByKinds"/> is the list's optional
/// filter by kind of thrower.
/// </summary>
public sealed record SignalBroadcastQuery(
    string? Name,
    string? WorkflowKey,
    string? Status,
    string? ThrownByKind,
    string? ThrownBy,
    int Page,
    int PageSize,
    IReadOnlyList<string>? ThrownByKinds = null);

public sealed record SignalDeliveryQuery(
    long BroadcastId,
    string? Status,
    long? InstanceId,
    int Page,
    int PageSize,
    string? Kind = null);

/// <summary>The job payload of a <c>signalBroadcastDeliver</c> job.</summary>
public sealed record SignalBroadcastJobPayload(long BroadcastId, long WorkflowDefinitionId);

/// <summary>The delivery backlog the Worker samples: pending deliveries and the oldest unfinished broadcast.</summary>
public sealed record SignalBacklogRecord(long PendingDeliveries, DateTimeOffset? OldestOpenBroadcastAt, DateTimeOffset ObservedAt);

/// <summary>How one delivery ended, as the executor reports it.</summary>
public sealed record SignalDeliveryOutcome(string Status, string? ResultCode, string? ResultDescription);
