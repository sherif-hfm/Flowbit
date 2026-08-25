namespace Flowbit.Infrastructure.Entities;

public sealed class SharedVariableClientSecretEntity
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public int Version { get; set; }
    public string Algorithm { get; set; } = string.Empty;
    public int Iterations { get; set; }
    public byte[] Salt { get; set; } = [];
    public byte[] Digest { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ValidUntil { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public SharedVariableClientEntity Client { get; set; } = null!;
}
