namespace Flowbit.Infrastructure.Entities;

public sealed class SharedVariableWakeEntity
{
    public long Id { get; set; }
    public long SharedVariableId { get; set; }
    public long RevisionId { get; set; }
    public long Revision { get; set; }
    public string Status { get; set; } = "pending";
    public Guid? LeaseToken { get; set; }
    public long LeaseGeneration { get; set; }
    public string? LeasedBy { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public SharedVariableEntity SharedVariable { get; set; } = null!;
    public SharedVariableRevisionEntity RevisionRecord { get; set; } = null!;
    public List<SharedVariableWakeDeliveryEntity> Deliveries { get; set; } = [];
}
