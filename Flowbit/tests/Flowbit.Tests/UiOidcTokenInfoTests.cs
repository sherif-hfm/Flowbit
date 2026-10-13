extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;
using Ui = FlowbitUi::Flowbit.Ui.Auth;

namespace Flowbit.Tests;

public sealed class UiOidcTokenInfoTests
{
    [Fact]
    public async Task Anonymous_request_starts_provider_sign_in_and_development_mode_has_no_endpoint()
    {
        await using var host = await Host.StartAsync();
        using var response = await host.Client.GetAsync("/_info");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("identity.flowbit.test", response.Headers.Location!.Host);
        Assert.DoesNotContain("accessToken", await response.Content.ReadAsStringAsync());

        await using var development = await Host.StartAsync(developmentToken: true);
        using var missing = await development.Client.GetAsync("/_info");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Real_session_cookies_select_only_their_own_access_token_and_responses_are_not_cacheable()
    {
        await using var host = await Host.StartAsync();
        var alice = await host.LoginAsync("alice");
        var bob = await host.LoginAsync("bob");
        foreach (var (user, cookie) in new[] { ("alice", alice), ("bob", bob), ("alice", alice) })
        {
            // Query parameters cannot select another user's session.
            using var response = await host.InfoAsync(cookie, "?user=other&sessionId=other");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.True(response.Headers.CacheControl.Private);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
            var info = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(["accessToken", "expiresAt", "roles", "tokenType", "user"],
                info.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal(user, info.GetProperty("user").GetString());
            Assert.Equal("access-" + user, info.GetProperty("accessToken").GetString());
            Assert.Equal("Bearer", info.GetProperty("tokenType").GetString());
            Assert.Equal("Reviewer", info.GetProperty("roles")[0].GetString());
            Assert.True(info.GetProperty("expiresAt").GetDateTimeOffset() > host.Clock.GetUtcNow());
            Assert.DoesNotContain("private-refresh", info.ToString());
            Assert.DoesNotContain("private-id-token", info.ToString());
        }
        Assert.Equal(0, host.Manager.Calls);
    }

    [Fact]
    public async Task Near_expiry_refreshes_once_and_returns_the_refreshed_api_identity()
    {
        await using var host = await Host.StartAsync();
        var cookie = await host.LoginAsync("alice", seconds: 20, refresh: true);
        using var response = await host.InfoAsync(cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rotated-access", info.GetProperty("accessToken").GetString());
        Assert.Equal("Manager", info.GetProperty("roles")[0].GetString());
        Assert.Equal(host.Clock.GetUtcNow().AddMinutes(10), info.GetProperty("expiresAt").GetDateTimeOffset());
        using var second = await host.InfoAsync(cookie);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, host.Manager.Calls);
        Assert.Equal(1, host.Api.Calls);
    }

    [Theory]
    [InlineData("invalid_grant", 401, "session_expired")]
    [InlineData("temporarily_unavailable", 503, "authentication_unavailable")]
    public async Task Refresh_failures_return_fixed_errors_without_tokens(string error, int status, string code)
    {
        await using var host = await Host.StartAsync();
        var cookie = await host.LoginAsync("alice", seconds: 20, refresh: true);
        host.Manager.Error = error;
        using var response = await host.InfoAsync(cookie);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var info = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, info.GetProperty("error").GetString());
        Assert.False(info.TryGetProperty("accessToken", out _));
        Assert.DoesNotContain("private-", info.ToString());
        if (status == 401)
        {
            Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-Flowbit.Session=;", StringComparison.Ordinal));
            using var retry = await host.InfoAsync(cookie);
            Assert.Equal(HttpStatusCode.Redirect, retry.StatusCode);
        }
        else
        {
            host.Manager.Error = null;
            using var retry = await host.InfoAsync(cookie);
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        }
    }

    [Fact]
    public async Task Expired_access_without_refresh_does_not_return_a_stale_token()
    {
        await using var host = await Host.StartAsync();
        var cookie = await host.LoginAsync("alice", seconds: 20);
        host.Clock.Now += TimeSpan.FromSeconds(21);
        using var response = await host.InfoAsync(cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("access-alice", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Manager.Calls);
    }

    private sealed class Host(WebApplication app, Clock clock, RefreshManager manager, ActorApi api) : IAsyncDisposable
    {
        public readonly HttpClient Client = app.GetTestClient();
        public Clock Clock => clock;
        public RefreshManager Manager => manager;
        public ActorApi Api => api;

        public static async Task<Host> StartAsync(bool developmentToken = false)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            TestWebHostDefaults.IsolateFromMachine(builder);
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = developmentToken ? "DevelopmentToken" : "OpenIdConnect",
                ["Authentication:Oidc:Authority"] = "https://identity.flowbit.test",
                ["Authentication:Oidc:AllowHttpMetadata"] = "false",
                ["Authentication:Oidc:ClientId"] = "ui",
                ["Authentication:Oidc:ClientSecret"] = "synthetic-test-secret"
            });
            var options = Ui.UiAuthentication.AddFlowbitUiAuthentication(builder);
            builder.Services.AddAntiforgery();
            var clock = new Clock();
            var manager = new RefreshManager(clock);
            var api = new ActorApi();
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddSingleton<IUserTokenManager>(manager);
            builder.Services.AddSingleton<IHttpClientFactory>(api);
            builder.Services.Configure<OpenIdConnectOptions>(Ui.UiAuthentication.OidcScheme, oidc =>
                oidc.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://identity.flowbit.test",
                    AuthorizationEndpoint = "https://identity.flowbit.test/authorize",
                    TokenEndpoint = "https://identity.flowbit.test/token"
                });
            var app = builder.Build();
            if (!developmentToken)
            {
                manager.Store = app.Services.GetRequiredService<Ui.UiSessionStore>();
                app.UseAuthentication();
                app.UseAuthorization();
                // Only this in-memory test host mints synthetic authenticated sessions.
                app.MapGet("/test-login/{actor}", async (HttpContext context, string actor, int seconds, bool refresh, Ui.UiSessionStore store) =>
                {
                    var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", actor)], "oidc"));
                    store.Create(principal, new UserToken
                    {
                        AccessToken = AccessToken.Parse("access-" + actor), AccessTokenType = AccessTokenType.Parse("Bearer"),
                        ClientId = ClientId.Parse("ui"), Expiration = clock.GetUtcNow().AddSeconds(seconds),
                        IdentityToken = IdentityToken.Parse("private-id-token"),
                        RefreshToken = refresh ? RefreshToken.Parse("private-refresh") : null
                    }, new(actor, ["Reviewer"]), TimeSpan.FromHours(1));
                    await context.SignInAsync(Ui.UiAuthentication.CookieScheme, principal);
                }).AllowAnonymous();
            }
            Ui.UiAuthentication.MapFlowbitAuthentication(app, options);
            await app.StartAsync();
            var host = new Host(app, clock, manager, api);
            host.Client.BaseAddress = new Uri("https://ui.flowbit.test");
            return host;
        }

        public async Task<string> LoginAsync(string actor, int seconds = 600, bool refresh = false)
        {
            using var response = await Client.GetAsync($"/test-login/{actor}?seconds={seconds}&refresh={refresh}");
            response.EnsureSuccessStatusCode();
            return response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-Flowbit.Session=", StringComparison.Ordinal)).Split(';')[0];
        }

        public async Task<HttpResponseMessage> InfoAsync(string cookie, string query = "")
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/_info" + query);
            request.Headers.Add("Cookie", cookie);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync() { Client.Dispose(); await app.DisposeAsync(); }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RefreshManager(Clock clock) : IUserTokenManager
    {
        public Ui.UiSessionStore Store = null!;
        public string? Error;
        public int Calls;
        public Task<TokenResult<UserToken>> GetAccessTokenAsync(ClaimsPrincipal user, UserTokenRequestParameters? parameters = null, CancellationToken ct = default)
        {
            Calls++;
            if (Error is not null) return Task.FromResult<TokenResult<UserToken>>(TokenResult.Failure(Error));
            var token = new UserToken
            {
                AccessToken = AccessToken.Parse("rotated-access"), AccessTokenType = AccessTokenType.Parse("Bearer"),
                ClientId = ClientId.Parse("ui"), Expiration = clock.GetUtcNow().AddMinutes(10)
            };
            Store.UpdateToken(Ui.UiSessionStore.SessionId(user), token);
            return Task.FromResult<TokenResult<UserToken>>(token);
        }
        public Task RevokeRefreshTokenAsync(ClaimsPrincipal user, UserTokenRequestParameters? parameters = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ActorApi : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new("https://api.flowbit.test") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("rotated-access", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { user = "alice", roles = new[] { "Manager" } })
            });
        }
    }
}
