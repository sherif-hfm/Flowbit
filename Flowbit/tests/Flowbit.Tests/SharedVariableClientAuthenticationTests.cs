using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

public sealed class SharedVariableClientAuthenticationTests
{
    [Fact]
    public async Task ExactHeaderPairAuthenticatesAndExposesOnlyClientMetadata()
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/shared-variables/check");
        request.Headers.Add(SharedVariableClientAuthenticationDefaults.ClientIdHeader, "finance-client");
        request.Headers.Add(SharedVariableClientAuthenticationDefaults.ClientSecretHeader, "correct-secret");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthenticatedClientResponse>();
        Assert.NotNull(body);
        Assert.Equal("finance-client", body.ClientId);
        Assert.Equal(
            [SharedVariableClientScopes.Read, SharedVariableClientScopes.Write],
            body.Scopes);
        Assert.DoesNotContain("correct-secret", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("finance-client", null)]
    [InlineData(null, "correct-secret")]
    [InlineData("FINANCE-CLIENT", "correct-secret")]
    [InlineData("finance-client", "wrong-secret")]
    public async Task MissingPartialOrInvalidCredentialsReturnOneUnauthorizedResult(
        string? clientId,
        string? clientSecret)
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/shared-variables/check");
        if (clientId is not null)
        {
            request.Headers.Add(SharedVariableClientAuthenticationDefaults.ClientIdHeader, clientId);
        }
        if (clientSecret is not null)
        {
            request.Headers.Add(SharedVariableClientAuthenticationDefaults.ClientSecretHeader, clientSecret);
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatedCredentialHeaderIsRejected(bool repeatClientId)
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/shared-variables/check");
        request.Headers.TryAddWithoutValidation(
            SharedVariableClientAuthenticationDefaults.ClientIdHeader,
            repeatClientId ? ["finance-client", "other-client"] : ["finance-client"]);
        request.Headers.TryAddWithoutValidation(
            SharedVariableClientAuthenticationDefaults.ClientSecretHeader,
            repeatClientId ? ["correct-secret"] : ["correct-secret", "other-secret"]);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/shared-variables/check")]
    [InlineData("/api/shared-variable-clients/check")]
    public async Task BearerAndClientHeadersAreRejectedBeforeAuthentication(string path)
    {
        await using var app = await CreateAppAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", "any-token");
        request.Headers.Add(SharedVariableClientAuthenticationDefaults.ClientIdHeader, "finance-client");
        request.Headers.Add(SharedVariableClientAuthenticationDefaults.ClientSecretHeader, "correct-secret");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "either a bearer token",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, app.Services.GetRequiredService<FakeClientService>().AuthenticationAttempts);
    }

    private static async Task<WebApplication> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<FakeClientService>();
        builder.Services.AddSingleton<ISharedVariableClientService>(provider =>
            provider.GetRequiredService<FakeClientService>());
        builder.Services
            .AddAuthentication(SharedVariableClientAuthenticationDefaults.Scheme)
            .AddScheme<SharedVariableClientAuthenticationOptions, SharedVariableClientAuthenticationHandler>(
                SharedVariableClientAuthenticationDefaults.Scheme,
                _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("test-client", policy =>
            {
                policy.AddAuthenticationSchemes(SharedVariableClientAuthenticationDefaults.Scheme);
                policy.RequireAuthenticatedUser();
            }));

        var app = builder.Build();
        app.UseMiddleware<SharedVariableCredentialSourceMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet(
                "/api/shared-variables/check",
                (ClaimsPrincipal principal) => Results.Ok(new AuthenticatedClientResponse(
                    principal.FindFirstValue(
                        SharedVariableClientAuthenticationDefaults.ClientIdClaim)!,
                    principal.FindAll(SharedVariableClientAuthenticationDefaults.ScopeClaim)
                        .Select(claim => claim.Value)
                        .ToArray())))
            .RequireAuthorization("test-client");
        app.MapGet("/api/shared-variable-clients/check", () => Results.Ok());
        await app.StartAsync();
        return app;
    }

    private sealed record AuthenticatedClientResponse(
        string ClientId,
        IReadOnlyList<string> Scopes);

    private sealed class FakeClientService : ISharedVariableClientService
    {
        public int AuthenticationAttempts { get; private set; }

        public Task<SharedVariableClientAuthentication?> AuthenticateAsync(
            string clientId,
            string clientSecret,
            CancellationToken cancellationToken)
        {
            AuthenticationAttempts++;
            SharedVariableClientAuthentication? result =
                clientId == "finance-client" && clientSecret == "correct-secret"
                    ? new(
                        17,
                        clientId,
                        "Finance integration",
                        [SharedVariableClientScopes.Read, SharedVariableClientScopes.Write],
                        3)
                    : null;
            return Task.FromResult(result);
        }

        public Task<PagedResult<SharedVariableClientDto>> ListAsync(
            int page,
            int pageSize,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SharedVariableClientDto?> GetAsync(
            long id,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CreateSharedVariableClientResult> CreateAsync(
            CreateSharedVariableClientRequest request,
            SharedVariableCaller caller,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SharedVariableClientDto?> UpdateAsync(
            long id,
            UpdateSharedVariableClientRequest request,
            SharedVariableCaller caller,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RotateSharedVariableClientSecretResult?> RotateSecretAsync(
            long id,
            RotateSharedVariableClientSecretRequest request,
            SharedVariableCaller caller,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SharedVariableClientDto?> RevokeAsync(
            long id,
            RevokeSharedVariableClientRequest request,
            SharedVariableCaller caller,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
