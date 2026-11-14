using System.Text.Json;

namespace Flowbit.Service.Models;

public sealed record AutomaticMultiInstanceCreateRecord
{
    public required long InstanceId { get; init; }
    public required long TokenId { get; init; }
    public required long WorkflowDefinitionId { get; init; }
    public required Guid ActivationId { get; init; }
    public required int NodeId { get; init; }
    public required string NodeName { get; init; }
    public string? NodeExternalId { get; init; }
    public required string ActivityKind { get; init; }
    public required string Mode { get; init; }
    public required string ResultVariable { get; init; }
    public required string AggregateScope { get; init; }
    public string? AggregateSharedKey { get; init; }
    public required long AggregateValueVersion { get; init; }
    public required int MaxInstances { get; init; }
    public required int EffectiveConcurrency { get; init; }
    public required int TotalCount { get; init; }
    public required int ResultBytes { get; init; }
    public required JsonElement InputSnapshot { get; init; }
    public NodeExecutionActorRecord? ActorContext { get; init; }
    public DateTimeOffset InitializedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>
    /// The call adapter's target pin, resolved once at initialization. Required for a
    /// nonempty call body and refused on every other body.
    /// </summary>
    public long? PinnedChildDefinitionId { get; init; }
    public int? PinnedChildStartEventId { get; init; }
}

public sealed record AutomaticMultiInstanceRecord(
    long Id, long InstanceId, long TokenId, long WorkflowDefinitionId, Guid ActivationId,
    int NodeId, string ActivityKind, string Mode, string Status, string Phase,
    string ResultVariable, string AggregateScope, string? AggregateSharedKey,
    long AggregateValueVersion, long InputSnapshotId, int MaxInstances, int EffectiveConcurrency,
    int TotalCount, int CompletedCount, int FailedCount, int CancelledCount, int ResultBytes,
    string? AdmissionBlockedReason, long ControlGeneration, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt)
{
    public JsonElement? CompletionIntent { get; init; }
    public long? PinnedChildDefinitionId { get; init; }
    public int? PinnedChildStartEventId { get; init; }
    /// <summary><c>pinned</c> while the body is open, <c>released</c> once it settles; null without a pin.</summary>
    public string? PinState { get; init; }
}

public static class AutomaticMultiInstancePinStates
{
    public const string Pinned = "pinned";
    public const string Released = "released";
}

public sealed record AutomaticMultiInstanceItemRecord(
    long Id, long InstanceId, long MultiInstanceExecutionId, int ItemIndex,
    string Status, string Phase, JsonElement? StagedInput, JsonElement? Candidate,
    JsonElement? Result, JsonElement? SharedWriteCorrelations, string? ErrorCode,
    string? ErrorDescription, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt)
{
    // Audit history may be pruned independently of the durable item.
    public long? NodeExecutionId { get; init; }
}

public sealed record AutomaticMultiInstanceProgressRecord(
    AutomaticMultiInstanceRecord Execution, int ActiveCount, int PendingCount)
{
    public long? ControlJobId { get; init; }
    public long? ControlIncidentId { get; init; }
    public string? ControlIncidentType { get; init; }

    /// <summary>The version number of a call body's pinned target, for display.</summary>
    public int? PinnedChildDefinitionVersion { get; init; }
}

public sealed record AutomaticMultiInstanceItemPageRecord(
    IReadOnlyList<AutomaticMultiInstanceItemRecord> Items, long TotalCount);

public sealed record AutomaticMultiInstanceItemResultRecord(int ItemIndex, string Status, JsonElement? Result);
