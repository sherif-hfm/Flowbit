using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

public sealed class LocalIdentityService(ILocalIdentityRepository repository, ILocalPasswordHasher passwords,
    ILocalTokenIssuer issuer, LocalIdentityOptions options, IEngineSettingsService settings, TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();
    private static LocalIdentityException Invalid(string message) => new(400, "invalid_user_configuration", message);
    private static LocalIdentityException Unauthorized() => new(401, "invalid_credentials", "Sign-in or session credentials are invalid.");
    private static LocalIdentityException Conflict() => new(409, "revision_conflict", "This record changed. Reload and try again.");
    private static LocalIdentityException Missing() => new(404, "not_found", "The requested record does not exist.");
    private static string Text(string? value, string field, int max = 300)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value.EnumerateRunes().Count() > max || value.Any(char.IsControl)
            || (field == "Username" && value.Length > max))
            throw Invalid($"{field} must contain 1–{max} characters without control characters.");
        return value;
    }
    private static void Password(string? value)
    {
        if (string.IsNullOrEmpty(value))
            throw Invalid("Password is required.");
    }
    public async Task<string[]> ManagementRolesAsync(CancellationToken ct)
    {
        var value = (await settings.GetByKeyAsync("Users.RequiredRole", ct))?.Value;
        var roles = value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        return roles.Length == 0 ? ["admin"] : roles;
    }
    public Task<PagedResult<LocalUserDto>> ListAsync(string? search, bool? active, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        if ((long)(page - 1) * pageSize > int.MaxValue) throw Invalid("Requested user page is too large.");
        return repository.ListAsync(search, active, page, pageSize, ct);
    }
    public async Task<LocalUserDto> GetAsync(long id, CancellationToken ct)
        => ToDto(await repository.FindUserAsync(id, ct) ?? throw Missing());
    public Task<IReadOnlyList<UserAttributeDto>> AttributesAsync(CancellationToken ct) => repository.AttributesAsync(ct);

    public async Task BootstrapAsync(CancellationToken ct)
    {
        await using var transaction = await repository.BeginAsync(ct);
        if ((await repository.AllUsersAsync(ct)).Count != 0) return;
        if (string.IsNullOrWhiteSpace(options.BootstrapUsername) || string.IsNullOrEmpty(options.BootstrapPassword))
            throw new InvalidOperationException("An empty Local directory requires Authentication:Local:BootstrapUsername and BootstrapPassword.");
        Password(options.BootstrapPassword);
        var user = new LocalUserAccount { Username = Text(options.BootstrapUsername, "Username"), FirstName = "Flowbit",
            LastName = "Administrator", PasswordHash = passwords.Hash(options.BootstrapPassword),
            Roles = (await ManagementRolesAsync(ct)).Append("admin").Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            CreatedAt = Now, UpdatedAt = Now };
        await repository.SaveUserAsync(user, ct);
        await repository.CommitAsync(ct);
    }

    public async Task<LocalUserDto> CreateAsync(CreateLocalUserRequest request, CancellationToken ct)
    {
        await using var transaction = await repository.BeginAsync(ct);
        var username = Text(request.Username, "Username");
        if (await repository.FindUsernameAsync(username, ct) is not null)
            throw new LocalIdentityException(409, "duplicate_username", "Username already exists.");
        Password(request.Password);
        var user = new LocalUserAccount { Username = username, IsActive = request.IsActive, CreatedAt = Now, UpdatedAt = Now,
            PasswordHash = passwords.Hash(request.Password) };
        Apply(user, request.FirstName, request.LastName, request.Roles, request.Claims, request.Attributes,
            await repository.AttributesAsync(ct));
        await repository.SaveUserAsync(user, ct);
        await repository.CommitAsync(ct);
        return ToDto(user);
    }
    public async Task<LocalUserDto> UpdateAsync(long id, UpdateLocalUserRequest request, CancellationToken ct)
    {
        await using var transaction = await repository.BeginAsync(ct);
        var user = await RequiredAsync(id, request.ExpectedRevision, ct);
        Apply(user, request.FirstName, request.LastName, request.Roles, request.Claims, request.Attributes,
            await repository.AttributesAsync(ct));
        await PreserveAdministratorAsync(user, ct);
        await SaveAsync(user, ct);
        await repository.CommitAsync(ct);
        return ToDto(user);
    }
    public async Task<LocalUserDto> StatusAsync(long id, SetLocalUserStatusRequest request, CancellationToken ct)
    {
        await using var transaction = await repository.BeginAsync(ct);
        var user = await RequiredAsync(id, request.ExpectedRevision, ct);
        user.IsActive = request.IsActive;
        await PreserveAdministratorAsync(user, ct);
        if (!user.IsActive) await repository.RevokeAsync(id, null, ct);
        await SaveAsync(user, ct);
        await repository.CommitAsync(ct);
        return ToDto(user);
    }
    public async Task ResetPasswordAsync(long id, ResetLocalPasswordRequest request, CancellationToken ct)
    {
        await using var transaction = await repository.BeginAsync(ct);
        var user = await RequiredAsync(id, request.ExpectedRevision, ct);
        Password(request.NewPassword);
        user.PasswordHash = passwords.Hash(request.NewPassword);
        user.FailedAttempts = 0; user.LockedUntil = null;
        await repository.RevokeAsync(id, null, ct);
        await SaveAsync(user, ct);
        await repository.CommitAsync(ct);
    }
    public async Task ChangePasswordAsync(string username, Guid familyId, ChangeLocalPasswordRequest request, CancellationToken ct)
    {
        await using var transaction = await repository.BeginAsync(ct);
        var user = await repository.FindUsernameAsync(username, ct);
        if (user is null || !await repository.SessionActiveAsync(user.Id, familyId, Now, ct)
            || !passwords.Verify(user.PasswordHash, request.CurrentPassword ?? "")) throw Unauthorized();
        Password(request.NewPassword);
        user.PasswordHash = passwords.Hash(request.NewPassword);
        user.FailedAttempts = 0; user.LockedUntil = null;
        await repository.RevokeAsync(user.Id, null, ct);
        await SaveAsync(user, ct);
        await repository.CommitAsync(ct);
    }
    public async Task<LocalTokenPairDto> LoginAsync(LocalLoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 600 || string.IsNullOrEmpty(request.Password))
            throw Unauthorized();
        await using var transaction = await repository.BeginAsync(ct);
        var user = await repository.FindUsernameAsync(request.Username.Trim(), ct);
        var valid = passwords.Verify(user?.PasswordHash ?? "", request.Password);
        if (user is null || !user.IsActive || user.LockedUntil > Now) throw Unauthorized();
        if (!valid)
        {
            user.FailedAttempts++;
            if (user.FailedAttempts >= options.MaxFailedAttempts)
            { user.LockedUntil = Now + options.LockoutDuration; user.FailedAttempts = 0; }
            await repository.SaveUserAsync(user, ct);
            await repository.CommitAsync(ct);
            throw Unauthorized();
        }
        user.FailedAttempts = 0; user.LockedUntil = null;
        await repository.SaveUserAsync(user, ct);
        var result = await TokensAsync(user, Guid.NewGuid(), Now + options.SessionLifetime, ct);
        await repository.CommitAsync(ct);
        return result;
    }
    public async Task<LocalTokenPairDto> RefreshAsync(string token, CancellationToken ct)
    {
        await using var transaction = await repository.BeginAsync(ct);
        var session = await repository.FindRefreshAsync(Hash(token), ct);
        if (session is null || session.Revoked || session.ExpiresAt <= Now) throw Unauthorized();
        if (session.Used)
        {
            await repository.RevokeAsync(null, session.FamilyId, ct);
            await repository.CommitAsync(ct);
            throw Unauthorized();
        }
        var user = await repository.FindUserAsync(session.UserId, ct);
        if (user is null || !user.IsActive) throw Unauthorized();
        session.Used = true;
        await repository.SaveRefreshAsync(session, ct);
        var result = await TokensAsync(user, session.FamilyId, session.ExpiresAt, ct);
        await repository.CommitAsync(ct);
        return result;
    }
    public async Task LogoutAsync(string token, CancellationToken ct)
    {
        await using var transaction = await repository.BeginAsync(ct);
        var session = await repository.FindRefreshAsync(Hash(token), ct);
        if (session is not null) await repository.RevokeAsync(null, session.FamilyId, ct);
        await repository.CommitAsync(ct);
    }
    public async Task<ActorContextDto> SessionAsync(string username, Guid familyId, CancellationToken ct)
    {
        var user = await repository.FindUsernameAsync(username, ct);
        if (user is null || !await repository.SessionActiveAsync(user.Id, familyId, Now, ct)) throw Unauthorized();
        return new(user.Username, user.Roles);
    }
    private async Task<LocalTokenPairDto> TokensAsync(LocalUserAccount user, Guid family, DateTimeOffset deadline, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        await repository.SaveRefreshAsync(new() { Hash = Hash(token), FamilyId = family, UserId = user.Id, ExpiresAt = deadline }, ct);
        var expiry = Now + options.AccessTokenLifetime;
        if (expiry > deadline) expiry = deadline;
        var claims = Claims(user, await repository.AttributesAsync(ct));
        return new(issuer.Issue(user, claims, family, expiry), "Bearer", expiry, token, deadline, new(user.Username, user.Roles));
    }
    private static string Hash(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256) throw Unauthorized();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
    private async Task<LocalUserAccount> RequiredAsync(long id, long revision, CancellationToken ct)
    {
        var user = await repository.FindUserAsync(id, ct) ?? throw Missing();
        if (user.Revision != revision) throw Conflict();
        return user;
    }
    private Task SaveAsync(LocalUserAccount user, CancellationToken ct)
    { user.Revision++; user.UpdatedAt = Now; return repository.SaveUserAsync(user, ct); }
    private async Task PreserveAdministratorAsync(LocalUserAccount changed, CancellationToken ct)
    {
        var required = await ManagementRolesAsync(ct);
        bool Manager(LocalUserAccount u) => u.IsActive && u.Roles.Intersect(required, StringComparer.OrdinalIgnoreCase).Any();
        if (!Manager(changed) && !(await repository.AllUsersAsync(ct)).Any(u => u.Id != changed.Id && Manager(u)))
            throw Invalid("At least one active local user must retain a user-management role.");
    }
    private void Apply(LocalUserAccount user, string first, string last, string[]? roles, UserClaimDto[]? claims,
        Dictionary<string, JsonElement>? values, IReadOnlyList<UserAttributeDto> definitions)
    {
        user.FirstName = Text(first, "First name"); user.LastName = Text(last, "Last name");
        if ((roles?.Length ?? 0) > 100 || (claims?.Length ?? 0) > 32) throw Invalid("At most 100 roles and 32 claims are supported.");
        user.Roles = (roles ?? []).Select(r => Text(r, "Role")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        user.Claims = (claims ?? []).Select(c => new UserClaimDto(Text(c?.Type, "Claim name"), c?.Value ?? "")).ToArray();
        if (user.Claims.Any(c => c.Value.Length > 1000)) throw Invalid("Claim values cannot exceed 1,000 characters.");
        user.Attributes = values is null ? [] : new(values, StringComparer.Ordinal);
        if (user.Attributes.Keys.Any(key => !definitions.Any(d => d.Key == key))) throw Invalid("Unknown custom attribute.");
        foreach (var definition in definitions)
        {
            var supplied = user.Attributes.TryGetValue(definition.Key, out var value) && value.ValueKind != JsonValueKind.Null
                && !(value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));
            if (!supplied) { if (definition.Required) throw Invalid($"{definition.Label} is required."); continue; }
            var valid = definition.Type switch
            {
                "text" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 1000,
                "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "date" => value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                "dropdown" => value.ValueKind == JsonValueKind.String && definition.Choices.Contains(value.GetString(), StringComparer.Ordinal),
                _ => false
            };
            if (!valid) throw Invalid($"{definition.Label} has an invalid {definition.Type} value.");
        }
        _ = issuer.Issue(user, Claims(user, definitions), Guid.Empty, Now + options.AccessTokenLifetime);
    }
    private UserClaimDto[] Claims(LocalUserAccount user, IReadOnlyList<UserAttributeDto> definitions)
    {
        if (user.Claims.Any(c => issuer.ReservedClaims.Contains(c.Type))) throw Invalid("A custom claim uses a reserved token claim name.");
        var claims = user.Claims.ToList();
        foreach (var definition in definitions.Where(d => d.ClaimType is not null))
        {
            if (user.Claims.Any(c => string.Equals(c.Type, definition.ClaimType, StringComparison.OrdinalIgnoreCase)))
                throw Invalid($"Claim mapping for {definition.Label} conflicts with an explicit claim.");
            if (!user.Attributes.TryGetValue(definition.Key, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            claims.Add(new(definition.ClaimType!, text));
        }
        return claims.ToArray();
    }
    public async Task<UserAttributeDto> SaveAttributeAsync(long? id, SaveUserAttributeRequest request, CancellationToken ct)
    {
        await using var transaction = await repository.BeginAsync(ct);
        var definitions = await repository.AttributesAsync(ct);
        var existing = id is null ? null : definitions.FirstOrDefault(d => d.Id == id) ?? throw Missing();
        if (existing is not null && existing.Revision != request.ExpectedRevision) throw Conflict();
        var key = Text(request.Key, "Attribute key", 100);
        if (key.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-'))) throw Invalid("Attribute keys use letters, numbers, hyphens or underscores.");
        if (existing is not null && (key != existing.Key || request.Type != existing.Type)) throw Invalid("Attribute key and type cannot change.");
        if (definitions.Any(d => d.Id != id && string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)))
            throw new LocalIdentityException(409, "duplicate_attribute", "Attribute key already exists.");
        if (id is null && definitions.Count >= 32) throw Invalid("At most 32 custom attributes are supported.");
        if (request.Type is not ("text" or "number" or "boolean" or "date" or "dropdown")) throw Invalid("Unknown attribute type.");
        var choices = (request.Choices ?? []).Select(c => Text(c, "Choice")).Distinct(StringComparer.Ordinal).ToArray();
        if (choices.Length > 100 || (request.Type == "dropdown" && choices.Length == 0)) throw Invalid("Dropdowns require 1–100 choices.");
        var claim = string.IsNullOrWhiteSpace(request.ClaimType) ? null : Text(request.ClaimType, "Claim mapping");
        if (claim is not null && (issuer.ReservedClaims.Contains(claim) || definitions.Any(d => d.Id != id && string.Equals(d.ClaimType, claim, StringComparison.OrdinalIgnoreCase))))
            throw Invalid("Claim mapping is reserved or already used.");
        var result = new UserAttributeDto(id ?? 0, key, request.Type, Text(request.Label, "Label"), request.Required,
            request.Type == "dropdown" ? choices : [], claim, (existing?.Revision ?? 0) + 1);
        var next = definitions.Where(d => d.Id != id).Append(result).ToArray();
        foreach (var user in await repository.AllUsersAsync(ct))
        {
            _ = issuer.Issue(user, Claims(user, next), Guid.Empty, Now + options.AccessTokenLifetime);
            if (result.Type == "dropdown" && user.Attributes.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(value.GetString()) && !result.Choices.Contains(value.GetString(), StringComparer.Ordinal))
                throw Invalid("A dropdown choice is still used by an existing user.");
        }
        result = await repository.SaveAttributeAsync(result, ct);
        await repository.CommitAsync(ct);
        return result;
    }
    public static LocalUserDto ToDto(LocalUserAccount u) => new(u.Id, u.Username, u.FirstName, u.LastName,
        u.IsActive, u.Roles, u.Claims, u.Attributes, u.Revision, u.CreatedAt, u.UpdatedAt);
}
