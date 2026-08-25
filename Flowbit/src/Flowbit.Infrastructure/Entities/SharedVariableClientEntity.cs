namespace Flowbit.Infrastructure.Entities;

public sealed class SharedVariableClientEntity
{
    public long Id { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = [];
    public string Status { get; set; } = "active";
    public long Revision { get; set; } = 1;
    public int ActiveSecretVersion { get; set; } = 1;
    public DateTimeOffset? ExpiresAt { get; set; }
    public string CreatedByKind { get; set; } = string.Empty;
    public string CreatedById { get; set; } = string.Empty;
    public string UpdatedByKind { get; set; } = string.Empty;
    public string UpdatedById { get; set; } = string.Empty;
    public string? RevocationReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }

    public List<SharedVariableClientSecretEntity> Secrets { get; set; } = [];
}
