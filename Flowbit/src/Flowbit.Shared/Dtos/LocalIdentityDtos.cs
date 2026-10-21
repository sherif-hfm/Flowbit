using System.Text.Json;

namespace Flowbit.Shared.Dtos;

public sealed record UserClaimDto(string Type, string Value);
public sealed record LocalUserDto(long Id, string Username, string FirstName, string LastName,
    bool IsActive, string[] Roles, UserClaimDto[] Claims, Dictionary<string, JsonElement> Attributes,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record CreateLocalUserRequest(string Username, string FirstName, string LastName,
    string Password, bool IsActive, string[] Roles, UserClaimDto[] Claims, Dictionary<string, JsonElement> Attributes)
{
    public override string ToString() => nameof(CreateLocalUserRequest);
}
public sealed record UpdateLocalUserRequest(string FirstName, string LastName, string[] Roles,
    UserClaimDto[] Claims, Dictionary<string, JsonElement> Attributes, long ExpectedRevision);
public sealed record SetLocalUserStatusRequest(bool IsActive, long ExpectedRevision);
public sealed record ResetLocalPasswordRequest(string NewPassword, long ExpectedRevision)
{
    public override string ToString() => nameof(ResetLocalPasswordRequest);
}
public sealed record UserAttributeDto(long Id, string Key, string Type, string Label, bool Required,
    string[] Choices, string? ClaimType, long Revision);
public sealed record SaveUserAttributeRequest(string Key, string Type, string Label, bool Required,
    string[] Choices, string? ClaimType, long? ExpectedRevision = null);
public sealed record LocalLoginRequest(string Username, string Password)
{
    public override string ToString() => nameof(LocalLoginRequest);
}
public sealed record LocalRefreshRequest(string RefreshToken)
{
    public override string ToString() => nameof(LocalRefreshRequest);
}
public sealed record ChangeLocalPasswordRequest(string CurrentPassword, string NewPassword)
{
    public override string ToString() => nameof(ChangeLocalPasswordRequest);
}
public sealed record LocalTokenPairDto(string AccessToken, string TokenType, DateTimeOffset ExpiresAt,
    string RefreshToken, DateTimeOffset RefreshExpiresAt, ActorContextDto Actor)
{
    public override string ToString() => nameof(LocalTokenPairDto);
}
