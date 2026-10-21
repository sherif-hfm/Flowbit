namespace Flowbit.Infrastructure.Entities;

public sealed class LocalUserEntity
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public bool IsActive { get; set; }
    public string PasswordHash { get; set; } = "";
    public string RolesJson { get; set; } = "[]";
    public string ClaimsJson { get; set; } = "[]";
    public string AttributesJson { get; set; } = "{}";
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}
public sealed class UserAttributeEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Required { get; set; }
    public string ChoicesJson { get; set; } = "[]";
    public string? ClaimType { get; set; }
    public long Revision { get; set; }
}
public sealed class LocalRefreshSessionEntity
{
    public string Hash { get; set; } = "";
    public Guid FamilyId { get; set; }
    public long UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Used { get; set; }
    public bool Revoked { get; set; }
}
