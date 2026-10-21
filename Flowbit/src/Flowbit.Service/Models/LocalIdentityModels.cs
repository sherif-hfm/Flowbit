using System.Text.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Models;

public class LocalIdentityOptions
{
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);
    public int MaxFailedAttempts { get; set; } = 5;
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(5);
    public string? BootstrapUsername { get; set; }
    public string? BootstrapPassword { get; set; }
    public void Validate()
    {
        if (AccessTokenLifetime < TimeSpan.FromMinutes(1) || AccessTokenLifetime > TimeSpan.FromHours(1)
            || SessionLifetime < AccessTokenLifetime || SessionLifetime > TimeSpan.FromDays(1)
            || MaxFailedAttempts is < 1 or > 100
            || LockoutDuration < TimeSpan.FromSeconds(1) || LockoutDuration > TimeSpan.FromDays(1))
            throw new InvalidOperationException("Invalid Authentication:Local lockout or lifetime limits.");
    }
}

public sealed class LocalUserAccount
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string PasswordHash { get; set; } = "";
    public string[] Roles { get; set; } = [];
    public UserClaimDto[] Claims { get; set; } = [];
    public Dictionary<string, JsonElement> Attributes { get; set; } = [];
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

public sealed class LocalRefreshSession
{
    public string Hash { get; set; } = "";
    public Guid FamilyId { get; set; }
    public long UserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Used { get; set; }
    public bool Revoked { get; set; }
}

public sealed class LocalIdentityException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
