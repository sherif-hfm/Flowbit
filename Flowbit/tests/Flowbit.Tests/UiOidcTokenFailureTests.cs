extern alias FlowbitUi;

using System.Net;
using System.Security.Claims;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Xunit;
using Ui = FlowbitUi::Flowbit.Ui.Auth;

namespace Flowbit.Tests;

public sealed class UiOidcTokenFailureTests
{
    [Theory]
    [InlineData(20, false)]
    [InlineData(60, true)]
    public async Task Login_actor_lookup_does_not_extend_the_provider_token_lifetime(int lifetimeSeconds, bool succeeds)
    {
        var clock = new TestClock();
        var expectedExpiry = clock.Now.AddSeconds(lifetimeSeconds);
        using var api = new ActorApi { BeforeResponse = () => clock.Now += TimeSpan.FromSeconds(30) };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        TestWebHostDefaults.IsolateFromMachine(builder);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "OpenIdConnect",
            ["Authentication:Oidc:Authority"] = "https://issuer.test",
            ["Authentication:Oidc:ClientId"] = "ui",
            ["Authentication:Oidc:ClientSecret"] = "test"
        });
        Ui.UiAuthentication.AddFlowbitUiAuthentication(builder);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<IHttpClientFactory>(api);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(Ui.UiAuthentication.OidcScheme);
        var context = new TokenValidatedContext(http,
            new AuthenticationScheme(Ui.UiAuthentication.OidcScheme, null, typeof(OpenIdConnectHandler)),
            options, SessionFixture.User(), new AuthenticationProperties())
        {
            TokenEndpointResponse = new OpenIdConnectMessage
            {
                AccessToken = "rotated-access", IdToken = "validated-identity-token", TokenType = "Bearer",
                ExpiresIn = lifetimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };

        await options.Events.OnTokenValidated(context);

        var store = scope.ServiceProvider.GetRequiredService<Ui.UiSessionStore>();
        var record = store.Find(Ui.UiSessionStore.SessionId(context.Principal!));
        Assert.Equal(1, api.Calls);
        if (succeeds)
        {
            Assert.Null(context.Result?.Failure);
            Assert.NotNull(record);
            Assert.Equal(expectedExpiry, store.ReadToken(record).Expiration);
        }
        else
        {
            Assert.NotNull(context.Result?.Failure);
            Assert.Null(record);
        }
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("session_expired")]
    public async Task Rejected_refresh_revokes_only_that_login_and_does_not_call_the_api(string error)
    {
        using var fixture = new SessionFixture();
        var other = fixture.Store.Create(SessionFixture.User(), SessionFixture.Token(fixture.Clock),
            new("alice", []), TimeSpan.FromHours(1));
        fixture.Manager.Callback = (_, _) => Task.FromResult<TokenResult<UserToken>>(TokenResult.Failure(error));

        await Assert.ThrowsAsync<Ui.UiSessionExpiredException>(() => fixture.Provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.Null(fixture.Store.Find(fixture.Id));
        Assert.NotNull(fixture.Store.Find(other));
        Assert.Equal(0, fixture.Api.Calls);
        Assert.Equal(1, fixture.Manager.Calls);
    }

    [Fact]
    public async Task Temporary_provider_error_preserves_session_and_releases_refresh_lock_for_retry()
    {
        using var fixture = new SessionFixture();
        fixture.Manager.Callback = (_, _) => Task.FromResult<TokenResult<UserToken>>(TokenResult.Failure("temporarily_unavailable"));

        var failure = await Assert.ThrowsAsync<Ui.UiAuthenticationUnavailableException>(() =>
            fixture.Provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.Null(failure.InnerException);
        Assert.NotNull(fixture.Store.Find(fixture.Id));
        Assert.Equal(0, fixture.Api.Calls);
        fixture.Manager.Callback = fixture.SuccessfulRefresh;
        Assert.Equal("rotated-access", await fixture.Provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(2, fixture.Manager.Calls);
        Assert.Equal(1, fixture.Api.Calls);
    }

    [Fact]
    public async Task Provider_transport_diagnostics_are_not_returned_to_the_ui()
    {
        using var fixture = new SessionFixture();
        fixture.Manager.Callback = (_, _) => throw new HttpRequestException("provider-secret raw-refresh-token");

        var failure = await Assert.ThrowsAsync<Ui.UiAuthenticationUnavailableException>(() =>
            fixture.Provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.DoesNotContain("provider-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("raw-refresh-token", failure.ToString(), StringComparison.Ordinal);
        Assert.NotNull(fixture.Store.Find(fixture.Id));
        Assert.Equal(0, fixture.Api.Calls);
    }

    [Fact]
    public async Task No_refresh_token_allows_unexpired_access_but_expiry_ends_the_session()
    {
        using var fixture = new SessionFixture(hasRefreshToken: false);
        Assert.Equal("original-access", await fixture.Provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Manager.Calls);
        fixture.Clock.Now += TimeSpan.FromMinutes(2);

        await Assert.ThrowsAsync<Ui.UiSessionExpiredException>(() => fixture.Provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.Null(fixture.Store.Find(fixture.Id));
        Assert.Equal(0, fixture.Manager.Calls);
        Assert.Equal(0, fixture.Api.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Api_rejection_of_refreshed_identity_revokes_the_session(HttpStatusCode status)
    {
        using var fixture = new SessionFixture();
        fixture.Manager.Callback = fixture.SuccessfulRefresh;
        fixture.Api.Status = status;

        await Assert.ThrowsAsync<Ui.UiSessionExpiredException>(() => fixture.Provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.Null(fixture.Store.Find(fixture.Id));
        Assert.Equal(1, fixture.Manager.Calls);
        Assert.Equal(1, fixture.Api.Calls);
    }

    [Fact]
    public async Task Api_outage_after_refresh_preserves_token_but_requires_identity_recheck_before_returning_it()
    {
        using var fixture = new SessionFixture();
        fixture.Manager.Callback = fixture.SuccessfulRefresh;
        fixture.Api.Status = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<Ui.UiAuthenticationUnavailableException>(() =>
            fixture.Provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.NotNull(fixture.Store.Find(fixture.Id));
        Assert.True(fixture.Store.NeedsActorRefresh(fixture.Store.Find(fixture.Id)!));

        fixture.Api.Status = HttpStatusCode.OK;
        Assert.Equal("rotated-access", await fixture.Provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(1, fixture.Manager.Calls);
        Assert.Equal(2, fixture.Api.Calls);
        Assert.False(fixture.Store.NeedsActorRefresh(fixture.Store.Find(fixture.Id)!));
    }

    [Fact]
    public async Task Token_expiring_during_actor_lookup_is_not_returned_to_the_api_transport()
    {
        using var fixture = new SessionFixture();
        fixture.Manager.Callback = (user, _) =>
        {
            var token = SessionFixture.Token(fixture.Clock) with
            {
                AccessToken = AccessToken.Parse("rotated-access"),
                Expiration = fixture.Clock.Now.AddSeconds(1)
            };
            fixture.Store.UpdateToken(Ui.UiSessionStore.SessionId(user), token);
            return Task.FromResult<TokenResult<UserToken>>(token);
        };
        fixture.Api.BeforeResponse = () => fixture.Clock.Now += TimeSpan.FromSeconds(1);

        await Assert.ThrowsAsync<Ui.UiSessionExpiredException>(() =>
            fixture.Provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.Null(fixture.Store.Find(fixture.Id));
        Assert.Equal(1, fixture.Manager.Calls);
        Assert.Equal(1, fixture.Api.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_revoke_session_or_keep_the_refresh_lock()
    {
        using var fixture = new SessionFixture();
        using var cancelled = new CancellationTokenSource();
        fixture.Manager.Callback = (_, cancellationToken) =>
        {
            cancelled.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider.GetAccessTokenAsync(cancelled.Token));

        Assert.NotNull(fixture.Store.Find(fixture.Id));
        fixture.Manager.Callback = fixture.SuccessfulRefresh;
        Assert.Equal("rotated-access", await fixture.Provider.GetAccessTokenAsync(CancellationToken.None));
    }

    private sealed class SessionFixture : IDisposable
    {
        public readonly TestClock Clock = new();
        public readonly Ui.UiSessionStore Store;
        public readonly RefreshManager Manager = new();
        public readonly ActorApi Api = new();
        public readonly Ui.OidcAccessTokenProvider Provider;
        public readonly string Id;

        public SessionFixture(bool hasRefreshToken = true)
        {
            Store = new(new EphemeralDataProtectionProvider(), Clock);
            var user = User();
            Id = Store.Create(user, Token(Clock) with
            {
                Expiration = Clock.Now.AddSeconds(20),
                RefreshToken = hasRefreshToken ? RefreshToken.Parse("original-refresh") : null
            }, new("alice", ["admin"]), TimeSpan.FromHours(1));
            Provider = new(new Authentication(user), Store, Manager, new Ui.ApiActorContextClient(Api),
                Clock, NullLogger<Ui.OidcAccessTokenProvider>.Instance);
        }

        public Task<TokenResult<UserToken>> SuccessfulRefresh(ClaimsPrincipal user, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = Token(Clock) with
            {
                AccessToken = AccessToken.Parse("rotated-access"),
                RefreshToken = RefreshToken.Parse("rotated-refresh")
            };
            Store.UpdateToken(Ui.UiSessionStore.SessionId(user), token);
            return Task.FromResult<TokenResult<UserToken>>(token);
        }

        public static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim("sub", "subject")], "oidc"));
        public static UserToken Token(TestClock clock) => new()
        {
            AccessToken = AccessToken.Parse("original-access"),
            AccessTokenType = AccessTokenType.Parse("Bearer"),
            ClientId = ClientId.Parse("flowbit-ui"), Expiration = clock.Now.AddMinutes(10)
        };
        public void Dispose() { Store.Dispose(); Api.Dispose(); }
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Authentication(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class RefreshManager : IUserTokenManager
    {
        public int Calls;
        public Func<ClaimsPrincipal, CancellationToken, Task<TokenResult<UserToken>>> Callback = (_, _) =>
            throw new InvalidOperationException("A refresh was not expected.");
        public Task<TokenResult<UserToken>> GetAccessTokenAsync(ClaimsPrincipal user, UserTokenRequestParameters? parameters = null, CancellationToken ct = default)
        {
            Calls++;
            return Callback(user, ct);
        }
        public Task RevokeRefreshTokenAsync(ClaimsPrincipal user, UserTokenRequestParameters? parameters = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ActorApi : HttpMessageHandler, IHttpClientFactory
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public int Calls;
        public Action? BeforeResponse;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.flowbit.test") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("rotated-access", request.Headers.Authorization?.Parameter);
            BeforeResponse?.Invoke();
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent("{\"user\":\"alice\",\"roles\":[\"admin\"]}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
