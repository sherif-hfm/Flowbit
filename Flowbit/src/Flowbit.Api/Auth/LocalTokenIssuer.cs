using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.IdentityModel.Tokens;

namespace Flowbit.Api.Auth;

public sealed class LocalTokenIssuer(FlowbitAuthenticationOptions authentication, ActorIdentityConfiguration identity,
    TimeProvider clock) : ILocalTokenIssuer
{
    public IReadOnlySet<string> ReservedClaims => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Username", "sub", "iss", "aud", "exp", "nbf", "iat", "jti", "sid", "given_name", "family_name",
        "name", "unique_name", "role", "roles", "flowbit_local_session", authentication.Local.RoleClaimType,
        identity.ClaimType ?? "Username" };
    public string Issue(LocalUserAccount user, IReadOnlyList<UserClaimDto> customClaims, Guid sessionId, DateTimeOffset expiresAt)
    {
        var local = authentication.Local;
        var claims = new List<Claim> { new("Username", user.Username), new("sub", user.Username),
            new("given_name", user.FirstName), new("family_name", user.LastName), new("sid", sessionId.ToString()),
            new("jti", Guid.NewGuid().ToString()), new("iat", clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64) };
        if (identity.ClaimType is { } type && !claims.Any(c => c.Type == type)) claims.Add(new(type, user.Username));
        claims.AddRange(user.Roles.Select(r => new Claim(local.RoleClaimType, r)));
        claims.AddRange(customClaims.Select(c => new Claim(c.Type, c.Value, ClaimValueTypes.String)));
        var jwt = new JwtSecurityToken(local.Issuer, local.Audience, claims, clock.GetUtcNow().UtcDateTime,
            expiresAt.UtcDateTime, new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(local.SigningKey)), SecurityAlgorithms.HmacSha256));
        var token = new JwtSecurityTokenHandler().WriteToken(jwt);
        if (Encoding.UTF8.GetByteCount(token) > 24000)
            throw new LocalIdentityException(400, "token_too_large", "User roles and claims exceed the 24 KB token limit.");
        return token;
    }
}
