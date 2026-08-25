namespace Flowbit.Infrastructure.Entities;

public sealed class SharedVariableRevisionStateEntity
{
    public short Id { get; set; } = 1;
    public long LastRevision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
