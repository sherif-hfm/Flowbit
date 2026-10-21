using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Flowbit.Api.Auth;

public interface IActorContextResolver
{
    ActorContext Resolve(ClaimsPrincipal principal);
}

/// <summary>
/// Converts a validated JWT principal into the single actor identity used throughout
/// workflow authorization, assignment, claiming, runtime context, and audit fields.
/// </summary>
public sealed class ActorContextResolver(
    ActorIdentityConfiguration configuration,
    WorkflowAuditOptions? auditOptions = null,
    FlowbitAuthenticationOptions? authentication = null)
    : IActorContextResolver
{
    public ActorContext Resolve(ClaimsPrincipal principal)
    {
        var configuredClaimType = configuration.ClaimType;
        if (configuredClaimType is null && authentication?.Mode == FlowbitAuthenticationOptions.OpenIdConnectMode
            && !string.Equals(authentication.Oidc.NameClaimType, ActorIdentityConfiguration.DefaultClaimType, StringComparison.OrdinalIgnoreCase))
        {
            configuredClaimType = authentication.Oidc.NameClaimType;
        }
        var user = configuredClaimType is null
            ? ResolveDefaultIdentity(principal)
            : ResolveConfiguredIdentity(principal, configuredClaimType);

        var roles = principal.Identities.SelectMany(identity => identity.FindAll(identity.RoleClaimType))
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Preserve the first value for each raw claim type. WorkflowContext.AllowedClaims
        // independently controls which entries may be exposed as sys.claim.*.
        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in principal.Claims)
        {
            claims.TryAdd(claim.Type, claim.Value);
        }

        return new ActorContext(user, roles, claims)
        {
            AuditClaims = CaptureAuditClaims(principal)
        };
    }

    private IReadOnlyDictionary<string, string[]>? CaptureAuditClaims(ClaimsPrincipal principal)
    {
        var selectedNames = (auditOptions?.AllowedClaims ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedNames.Length == 0)
        {
            return null;
        }

        // The bearer handler marks its validated identity explicitly. Other
        // authenticated schemes, such as API client credentials, have no JWT.
        var jwtIdentities = principal.Identities
            .Where(identity => identity.IsAuthenticated
                && string.Equals(identity.AuthenticationType,
                    JwtBearerDefaults.AuthenticationScheme, StringComparison.Ordinal))
            .ToArray();
        if (jwtIdentities.Length == 0)
        {
            return null;
        }

        var claims = jwtIdentities.SelectMany(identity => identity.Claims).ToArray();
        var snapshot = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in selectedNames)
        {
            var candidates = ClaimTypeCandidates(name);
            var values = claims.Where(claim => candidates.Contains(claim.Type))
                .Select(claim => claim.Value)
                .ToArray();
            if (values.Length > 0)
            {
                snapshot.Add(name, values);
            }
        }
        return snapshot;
    }

    private static string? ResolveDefaultIdentity(ClaimsPrincipal principal)
    {
        // These are explicit fallback groups, not Identity.Name or arbitrary JWT
        // aliases: a provider's bare "name" is often a mutable display name.
        string[][] groups = [[ActorIdentityConfiguration.DefaultClaimType], [ClaimTypes.Name], ["sub", ClaimTypes.NameIdentifier]];
        foreach (var group in groups)
        {
            var matchingClaims = principal.Claims.Where(claim => group.Contains(claim.Type, StringComparer.OrdinalIgnoreCase)).ToList();
            if (matchingClaims.Count != 0) return ValidateIdentity(matchingClaims, group[0]);
        }
        return null;
    }

    private static string ResolveConfiguredIdentity(ClaimsPrincipal principal, string configuredClaimType)
    {
        var candidateTypes = ClaimTypeCandidates(configuredClaimType);
        var matchingClaims = principal.Claims
            .Where(claim => candidateTypes.Contains(claim.Type))
            .ToList();

        return ValidateIdentity(matchingClaims, configuredClaimType);
    }

    private static string ValidateIdentity(List<Claim> matchingClaims, string configuredClaimType)
    {
        if (matchingClaims.Count == 0 || matchingClaims.Any(claim =>
                claim.ValueType != ClaimValueTypes.String || string.IsNullOrWhiteSpace(claim.Value)))
        {
            throw InvalidIdentityClaim(configuredClaimType);
        }

        var values = matchingClaims
            .Select(claim => claim.Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (values.Count != 1 || values[0].Length > UserTaskConstraints.MaxActorNameLength)
        {
            throw InvalidIdentityClaim(configuredClaimType);
        }

        return values[0];
    }

    private static HashSet<string> ClaimTypeCandidates(string configuredClaimType)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            configuredClaimType
        };

        // JwtBearer uses the framework's inbound claim map by default. Accept both the
        // JWT short name (for example "sub") and its mapped .NET URI so administrators
        // do not have to depend on that implementation detail.
        if (JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.TryGetValue(
                configuredClaimType, out var mappedType))
        {
            candidates.Add(mappedType);
        }

        foreach (var pair in JwtSecurityTokenHandler.DefaultInboundClaimTypeMap)
        {
            if (string.Equals(pair.Value, configuredClaimType, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(pair.Key);
            }
        }

        return candidates;
    }

    private static WorkflowUnauthorizedException InvalidIdentityClaim(string claimType) =>
        new($"The authenticated token does not contain one valid '{claimType}' actor identity claim.");
}
