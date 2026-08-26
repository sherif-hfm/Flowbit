namespace Flowbit.Infrastructure.Entities;

public sealed class SharedVariableWakeIncidentEntity
{
    public long Id { get; set; }
    public string WorkKind { get; set; } = string.Empty;
    public long? WakeId { get; set; }
    public long? DeliveryId { get; set; }
    public long OriginalWakeId { get; set; }
    public long? OriginalDeliveryId { get; set; }
    public long SharedVariableId { get; set; }
    public string SharedKey { get; set; } = string.Empty;
    public long Revision { get; set; }
    public long? InstanceId { get; set; }
    public long? WorkflowDefinitionId { get; set; }
    public long? TokenId { get; set; }
    public Guid? ActivationId { get; set; }
    public int? NodeId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = "open";
    public string Summary { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? ResolutionReason { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }

    public SharedVariableWakeEntity? Wake { get; set; }
    public SharedVariableWakeDeliveryEntity? Delivery { get; set; }
    public SharedVariableEntity SharedVariable { get; set; } = null!;
}
