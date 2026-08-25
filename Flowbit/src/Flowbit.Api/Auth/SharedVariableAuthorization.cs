using System.Security.Claims;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Flowbit.Api.Auth;

public static class SharedVariableAuthorizationPolicies
{
    public const string Read = "SharedVariables.Read";
    public const string Write = "SharedVariables.Write";
    public const string Administrator = "SharedVariables.Administrator";
    public const string RequiredRoleSettingKey = "SharedVariables.RequiredRole";
    public const string DefaultRequiredRole = "admin";

    public static void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(Read, policy =>
        {
            policy.AddAuthenticationSchemes(
                JwtBearerDefaults.AuthenticationScheme,
                SharedVariableClientAuthenticationDefaults.Scheme);
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new SharedVariableAccessRequirement(
                SharedVariableClientScopes.Read,
                AllowClient: true));
        });
        options.AddPolicy(Write, policy =>
        {
            policy.AddAuthenticationSchemes(
                JwtBearerDefaults.AuthenticationScheme,
                SharedVariableClientAuthenticationDefaults.Scheme);
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new SharedVariableAccessRequirement(
                SharedVariableClientScopes.Write,
                AllowClient: true));
        });
        options.AddPolicy(Administrator, policy =>
        {
            policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new SharedVariableAccessRequirement(
                RequiredClientScope: null,
                AllowClient: false));
        });
    }
}

public sealed record SharedVariableAccessRequirement(
    string? RequiredClientScope,
    bool AllowClient) : IAuthorizationRequirement;

/// <summary>
/// Applies one policy to two intentionally different caller types: JWT users are
/// authorized by the deployment's dynamic administrator role, while managed API
/// clients are authorized by their persisted read/write scopes.
/// </summary>
public sealed class SharedVariableAccessAuthorizationHandler(
    IEngineSettingsService settings,
    IActorContextResolver actorResolver)
    : AuthorizationHandler<SharedVariableAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SharedVariableAccessRequirement requirement)
    {
        var clientIdentity = context.User.Identities.FirstOrDefault(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType,
                SharedVariableClientAuthenticationDefaults.Scheme,
                StringComparison.Ordinal));
        if (clientIdentity is not null)
        {
            if (requirement.AllowClient
                && requirement.RequiredClientScope is not null
                && clientIdentity.FindAll(SharedVariableClientAuthenticationDefaults.ScopeClaim)
                    .Any(claim => string.Equals(
                        claim.Value,
                        requirement.RequiredClientScope,
                        StringComparison.OrdinalIgnoreCase)))
            {
                context.Succeed(requirement);
            }

            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var setting = await settings.GetByKeyAsync(
            SharedVariableAuthorizationPolicies.RequiredRoleSettingKey,
            (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None);
        var requiredRoles = ParseRequiredRoles(setting?.Value);
        var actorRoles = actorResolver.Resolve(context.User).Roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requiredRoles.Any(actorRoles.Contains))
        {
            context.Succeed(requirement);
        }
    }

    private static string[] ParseRequiredRoles(string? value)
    {
        var roles = (string.IsNullOrWhiteSpace(value)
                ? SharedVariableAuthorizationPolicies.DefaultRequiredRole
                : value)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return roles.Length == 0
            ? [SharedVariableAuthorizationPolicies.DefaultRequiredRole]
            : roles;
    }
}
