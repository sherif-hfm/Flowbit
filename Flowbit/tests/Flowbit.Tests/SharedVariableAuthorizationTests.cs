using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Flowbit.Tests;

public sealed class SharedVariableAuthorizationTests
{
    [Fact]
    public async Task ClientScopeMustMatchTheRequestedOperation()
    {
        var handler = CreateHandler();
        var principal = ClientPrincipal(SharedVariableClientScopes.Read);

        var read = Context(
            principal,
            new SharedVariableAccessRequirement(SharedVariableClientScopes.Read, true));
        await handler.HandleAsync(read);
        Assert.True(read.HasSucceeded);

        var write = Context(
            principal,
            new SharedVariableAccessRequirement(SharedVariableClientScopes.Write, true));
        await handler.HandleAsync(write);
        Assert.False(write.HasSucceeded);
    }

    [Fact]
    public async Task ClientIdentityCannotSatisfyTheJwtAdministratorPolicy()
    {
        var handler = CreateHandler();
        var context = Context(
            ClientPrincipal(SharedVariableClientScopes.Read, SharedVariableClientScopes.Write),
            new SharedVariableAccessRequirement(null, false));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task JwtRoleUsesTheDynamicCaseInsensitiveRequiredRole()
    {
        var handler = CreateHandler("VariableManager, Operations");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "sherif"),
                new Claim(ClaimTypes.Role, "variablemanager")
            ],
            "Bearer"));
        var context = Context(
            principal,
            new SharedVariableAccessRequirement(SharedVariableClientScopes.Write, true));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task MissingRequiredRoleSettingFallsBackToAdmin()
    {
        var handler = CreateHandler();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "admin-user"),
                new Claim(ClaimTypes.Role, "ADMIN")
            ],
            "Bearer"));
        var context = Context(
            principal,
            new SharedVariableAccessRequirement(null, false));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    private static SharedVariableAccessAuthorizationHandler CreateHandler(
        string? requiredRoles = null)
    {
        var identityConfiguration = new ActorIdentityConfiguration();
        identityConfiguration.Initialize(null);
        return new SharedVariableAccessAuthorizationHandler(
            new FakeSettings(requiredRoles),
            new ActorContextResolver(identityConfiguration));
    }

    private static AuthorizationHandlerContext Context(
        ClaimsPrincipal principal,
        SharedVariableAccessRequirement requirement) =>
        new([requirement], principal, new DefaultHttpContext());

    private static ClaimsPrincipal ClientPrincipal(params string[] scopes)
    {
        var claims = new List<Claim>
        {
            new(SharedVariableClientAuthenticationDefaults.ClientIdClaim, "finance-client")
        };
        claims.AddRange(scopes.Select(scope =>
            new Claim(SharedVariableClientAuthenticationDefaults.ScopeClaim, scope)));
        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            SharedVariableClientAuthenticationDefaults.Scheme));
    }

    private sealed class FakeSettings(string? requiredRoles) : IEngineSettingsService
    {
        public Task<EngineSettingRecord?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken)
        {
            Assert.Equal(SharedVariableAuthorizationPolicies.RequiredRoleSettingKey, key);
            EngineSettingRecord? result = requiredRoles is null
                ? null
                : new(
                    1,
                    "SharedVariables",
                    "RequiredRole",
                    requiredRoles,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(
            string pattern,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EngineSettingRecord> SetAsync(
            string key,
            string value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            string key,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
