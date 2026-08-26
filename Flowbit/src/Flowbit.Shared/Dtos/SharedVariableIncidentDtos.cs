namespace Flowbit.Shared.Dtos;

/// <summary>
/// Operator-facing durable shared-variable wake failure. It intentionally
/// contains no shared value or immutable revision payload.
/// </summary>
public sealed record SharedVariableIncidentDto(
    long Id,
    string WorkKind,
    long? WakeId,
    long? DeliveryId,
    long OriginalWakeId,
    long? OriginalDeliveryId,
    string SharedKey,
    long Revision,
    long? InstanceId,
    long? WorkflowDefinitionId,
    long? TokenId,
    Guid? ActivationId,
    int? NodeId,
    string Type,
    string Status,
    string Summary,
    string? Details,
    string? ResolutionReason,
    string? ResolvedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record ResolveSharedVariableIncidentRequest(string Reason);
