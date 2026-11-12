using System.Text.Json;

namespace Flowbit.Shared.Dtos;

/// <summary>Automatic body progress. Human work/voting always stays in MultiInstanceProgressDto.</summary>
public sealed record AutomaticMultiInstanceProgressDto(
    long ExecutionId, long TokenId, int NodeId, string ActivityKind, string Mode, string Status,
    string Phase, int TotalCount, int CompletedCount, int ActiveCount, int PendingCount,
    int FailedCount, int CancelledCount, int EffectiveConcurrency, bool AdmissionBlocked,
    string? AdmissionBlockedReason, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt)
{
    public long? ControlJobId { get; init; }
    public long? ControlIncidentId { get; init; }
    public string? ControlIncidentType { get; init; }
}

/// <summary>Payload-free item identity and current operational correlations.</summary>
public record AutomaticMultiInstanceItemSummaryDto(
    long Id, long InstanceId, long MultiInstanceExecutionId, int ItemIndex, string ActivityKind,
    string Status, string Phase, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, long? JobId, long? IncidentId,
    long? NodeExecutionId, bool CanReadPayload = false);

/// <summary>
/// An authorized copied element and committed local result. Never exposes staging,
/// publication candidates, credentials, or unrelated shared-variable reads.
/// </summary>
public sealed record AutomaticMultiInstanceItemDetailDto(
    AutomaticMultiInstanceItemSummaryDto Summary, JsonElement? Item, JsonElement? Variables,
    string? ErrorCode, string? ErrorDescription);
