namespace Flowbit.Infrastructure.Entities;

/// <summary>One durable coordinator and two bounded run documents, never an append-only run log.</summary>
public sealed class RetentionCoordinatorEntity
{
    public int Id { get; set; } = 1;
    public string? CurrentRunJson { get; set; }
    public string? LastRunJson { get; set; }
    public DateTimeOffset NextScheduledAt { get; set; }
    public DateTimeOffset? WorkerLastSeenAt { get; set; }
    public string? LeaseOwner { get; set; }
    public long LeaseGeneration { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
