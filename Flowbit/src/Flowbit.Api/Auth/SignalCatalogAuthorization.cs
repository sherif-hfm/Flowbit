using System.Security.Claims;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Flowbit.Api.Auth;

public static class SignalCatalogAuthorizationPolicies
{
    /// <summary>
    /// Reading the signal catalog: a JWT user with a role from Settings.RequiredRole,
    /// Signals.RequiredRole, or Workflow.RequiredRole, or a managed API client with
    /// <c>signals.read</c> or <c>signals.throw</c>. Names, contracts, and roles are not secrets.
    /// </summary>
    public const string Read = "SignalCatalog.Read";

    public static void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(Read, policy =>
        {
            policy.AddAuthenticationSchemes(
                JwtBearerDefaults.AuthenticationScheme,
                SharedVariableClientAuthenticationDefaults.Scheme);
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new FlowbitBearerScopeRequirement());
            policy.AddRequirements(new SignalCatalogReadRequirement());
        });
    }
}

/// <summary>The requirement of <see cref="SignalCatalogAuthorizationPolicies.Read"/>.</summary>
public sealed record SignalCatalogReadRequirement : IAuthorizationRequirement;

public sealed class SignalCatalogReadAuthorizationHandler(
    IEngineSettingsService settings,
    IActorContextResolver actorResolver)
    : AuthorizationHandler<SignalCatalogReadRequirement>
{
    private const string SettingsRequiredRoleKey = "Settings.RequiredRole";

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SignalCatalogReadRequirement requirement)
    {
        if (SignalCatalogCallers.ClientIdentity(context.User) is { } client)
        {
            var scopes = SignalCatalogCallers.Scopes(client);
            if (scopes.Contains(SharedVariableClientScopes.SignalsRead)
                || scopes.Contains(SharedVariableClientScopes.SignalsThrow))
            {
                context.Succeed(requirement);
            }

            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var cancellationToken = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        var actorRoles = actorResolver.Resolve(context.User).Roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[]
                 {
                     SettingsRequiredRoleKey,
                     SignalSettings.RequiredRoleKey,
                     WorkflowAdministratorPolicy.RequiredRoleSettingKey
                 })
        {
            var setting = await settings.GetByKeyAsync(key, cancellationToken);
            if (SignalSettings.ParseRoles(setting?.Value).Any(actorRoles.Contains))
            {
                context.Succeed(requirement);
                return;
            }
        }
    }
}

/// <summary>Turns an authorized principal into a reader of the signal catalog.</summary>
public static class SignalCatalogCallers
{
    public static SignalCatalogCaller Resolve(ClaimsPrincipal principal, IActorContextResolver actorResolver)
    {
        if (ClientIdentity(principal) is { } client)
        {
            return new SignalCatalogCaller(
                true,
                client.FindFirst(SharedVariableClientAuthenticationDefaults.ClientIdClaim)?.Value,
                [],
                Scopes(client).Contains(SharedVariableClientScopes.SignalsThrow));
        }

        var actor = actorResolver.Resolve(principal);
        return new SignalCatalogCaller(
            false,
            string.IsNullOrWhiteSpace(actor.User) ? null : actor.User.Trim(),
            actor.Roles.ToArray(),
            false);
    }

    internal static ClaimsIdentity? ClientIdentity(ClaimsPrincipal principal) =>
        principal.Identities.FirstOrDefault(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType,
                SharedVariableClientAuthenticationDefaults.Scheme,
                StringComparison.Ordinal));

    internal static HashSet<string> Scopes(ClaimsIdentity client) =>
        client.FindAll(SharedVariableClientAuthenticationDefaults.ScopeClaim)
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
