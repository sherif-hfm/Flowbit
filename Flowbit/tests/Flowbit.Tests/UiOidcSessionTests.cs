extern alias FlowbitUi;

using System.Net;
using System.Security.Claims;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Ui = FlowbitUi::Flowbit.Ui.Auth;

namespace Flowbit.Tests;

public sealed class UiOidcSessionTests
{
    [Fact]
    public async Task Session_tickets_contain_no_tokens_and_revocation_cannot_be_undone_by_renewal()
    {
        using var store = NewStore();
        var user = User();
        var id = store.Create(user, Token(), new("alice", ["admin"]), TimeSpan.FromHours(1));
        var ticket = new AuthenticationTicket(user, new AuthenticationProperties(), "FlowbitCookie");
        Assert.Equal(id, await store.StoreAsync(ticket));
        var read = await store.RetrieveAsync(id);
        Assert.NotNull(read);
        Assert.Empty(read.Properties.GetTokens());
        Assert.False(read.Properties.IsPersistent);
        store.Revoke(id);
        await store.RenewAsync(id, ticket);
        Assert.Null(await store.RetrieveAsync(id));
        Assert.Throws<Ui.UiSessionExpiredException>(() => store.UpdateToken(id, Token("rotated")));
    }

    [Fact]
    public void Logins_for_the_same_subject_are_isolated_and_provider_cannot_choose_session_id()
    {
        using var store = NewStore();
        var alice = User();
        ((ClaimsIdentity)alice.Identity!).AddClaim(new(Ui.UiSessionStore.SessionClaim, "provider-chosen"));
        var first = store.Create(alice, Token("first"), new("alice", []), TimeSpan.FromHours(1));
        var second = store.Create(User(), Token("second"), new("alice", []), TimeSpan.FromHours(1));
        Assert.NotEqual("provider-chosen", first);
        Assert.NotEqual(first, second);
        Assert.Single(alice.FindAll(Ui.UiSessionStore.SessionClaim));
        store.Revoke(first);
        Assert.Equal("second", store.ReadToken(store.Find(second)!).AccessToken.ToString());
    }

    [Fact]
    public void Invalid_token_expiry_is_rejected_and_actor_switch_revokes_session()
    {
        using var store = NewStore();
        Assert.Throws<Ui.UiSessionExpiredException>(() => store.Create(User(), Token() with { Expiration = DateTimeOffset.MaxValue }, new("alice", []), TimeSpan.FromHours(1)));
        var id = store.Create(User(), Token(), new("alice", []), TimeSpan.FromHours(1));
        Assert.Throws<Ui.UiSessionExpiredException>(() => store.UpdateActor(store.Find(id)!, new("bob", [])));
        Assert.Null(store.Find(id));
    }

    [Fact]
    public void Refresh_preserves_original_id_token_and_existing_refresh_token_when_omitted()
    {
        using var store = NewStore();
        var id = store.Create(User(), Token() with { IdentityToken = IdentityToken.Parse("original"), RefreshToken = RefreshToken.Parse("refresh") }, new("alice", []), TimeSpan.FromHours(1));
        store.UpdateToken(id, Token("new") with { IdentityToken = IdentityToken.Parse("unvalidated") });
        var token = store.ReadToken(store.Find(id)!);
        Assert.Equal("original", token.IdentityToken?.ToString());
        Assert.Equal("refresh", token.RefreshToken?.ToString());
    }

    [Fact]
    public async Task Expiry_is_enforced_even_with_a_still_valid_cookie_and_restart_loses_sessions()
    {
        var clock = new TestClock();
        var protection = new EphemeralDataProtectionProvider();
        using var store = new Ui.UiSessionStore(protection, clock);
        var principal = User();
        var id = store.Create(principal, Token(), new("alice", []), TimeSpan.FromMinutes(5));
        await store.StoreAsync(new AuthenticationTicket(principal, "FlowbitCookie"));
        using var restarted = new Ui.UiSessionStore(protection, clock);
        Assert.Null(await restarted.RetrieveAsync(id));
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Null(await store.RetrieveAsync(id));
    }

    [Fact]
    public async Task Parallel_circuits_refresh_once_and_recheck_api_actor_after_rotation()
    {
        using var store = NewStore();
        var principal = User();
        var id = store.Create(principal, Token() with { Expiration = DateTimeOffset.UtcNow.AddSeconds(20), RefreshToken = RefreshToken.Parse("r1") }, new("alice", ["admin"]), TimeSpan.FromHours(1));
        var manager = new RefreshManager(store);
        var calls = 0;
        var actorClient = new Ui.ApiActorContextClient(new FixedHttpFactory(new CallbackHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"user\":\"alice\",\"roles\":[\"admin\"]}", System.Text.Encoding.UTF8, "application/json") };
        })));
        Ui.OidcAccessTokenProvider Provider() => new(new Authentication(principal), store, manager, actorClient, TimeProvider.System, NullLogger<Ui.OidcAccessTokenProvider>.Instance);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Provider().GetAccessTokenAsync(CancellationToken.None)));
        Assert.All(results, value => Assert.Equal("rotated", value));
        Assert.Equal(1, manager.Calls);
        Assert.Equal(1, calls);
        Assert.Equal("r2", store.ReadToken(store.Find(id)!).RefreshToken?.ToString());
    }

    [Fact]
    public async Task Logout_during_refresh_cannot_resurrect_or_send_credentials()
    {
        using var store = NewStore();
        var principal = User();
        var id = store.Create(principal, Token() with { Expiration = DateTimeOffset.UtcNow.AddSeconds(20), RefreshToken = RefreshToken.Parse("r1") }, new("alice", []), TimeSpan.FromHours(1));
        var manager = new RefreshManager(store, () => store.Revoke(id));
        var provider = new Ui.OidcAccessTokenProvider(new Authentication(principal), store, manager,
            new Ui.ApiActorContextClient(new FixedHttpFactory(new CallbackHandler(_ => throw new Exception("Must not send")))), TimeProvider.System, NullLogger<Ui.OidcAccessTokenProvider>.Instance);
        await Assert.ThrowsAsync<Ui.UiSessionExpiredException>(() => provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Null(store.Find(id));
    }

    [Fact]
    public async Task Unauthorized_mutation_is_sent_once_and_session_is_rejected()
    {
        var tokens = new AccessProvider();
        var requests = 0;
        using var client = new HttpClient(new Ui.AuthTokenHandler(tokens)
        {
            InnerHandler = new CallbackHandler(request =>
            {
                requests++;
                Assert.Equal("access", request.Headers.Authorization?.Parameter);
                return new(HttpStatusCode.Unauthorized);
            })
        });
        using var response = await client.PostAsync("https://api.example/action", new StringContent("action"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, requests);
        Assert.True(tokens.Rejected);
    }

    [Theory]
    [InlineData("https://outside.example/", "/")]
    [InlineData("//outside.example/", "/")]
    [InlineData("/\\outside.example/", "/")]
    [InlineData("/workflows/12/edit?x=1", "/workflows/12/edit?x=1")]
    public void Login_return_paths_are_local(string input, string expected) => Assert.Equal(expected, Ui.UiAuthentication.LocalReturnUrl(input));

    [Fact]
    public void Development_auth_and_insecure_oidc_are_rejected_in_deployed_environments()
    {
        var options = new Ui.UiAuthenticationOptions { Mode = "DevelopmentToken" };
        Assert.Throws<InvalidOperationException>(() => options.Validate(new Environment("Production")));
        options.Validate(new Environment("Development"));
        options = new() { Oidc = new() { Authority = "http://localhost:8080/realm", ClientId = "ui", ClientSecret = "sample", AllowHttpMetadata = true } };
        options.Validate(new Environment("Development"));
        Assert.Throws<InvalidOperationException>(() => options.Validate(new Environment("Production")));
    }

    private static Ui.UiSessionStore NewStore() => new(new EphemeralDataProtectionProvider(), TimeProvider.System);
    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim("sub", "subject")], "oidc"));
    private static UserToken Token(string access = "access") => new()
    {
        AccessToken = AccessToken.Parse(access), ClientId = ClientId.Parse("ui"), AccessTokenType = AccessTokenType.Parse("Bearer"), Expiration = DateTimeOffset.UtcNow.AddMinutes(10)
    };
    private sealed class TestClock : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Authentication(ClaimsPrincipal user) : AuthenticationStateProvider { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user)); }
    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(callback(request)); }
    private sealed class FixedHttpFactory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new("https://api.example") }; }
    private sealed class RefreshManager(Ui.UiSessionStore store, Action? beforeStore = null) : IUserTokenManager
    {
        public int Calls;
        public async Task<TokenResult<UserToken>> GetAccessTokenAsync(ClaimsPrincipal user, UserTokenRequestParameters? parameters = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(20, ct);
            beforeStore?.Invoke();
            var token = Token("rotated") with { RefreshToken = RefreshToken.Parse("r2") };
            store.UpdateToken(Ui.UiSessionStore.SessionId(user), token);
            return token;
        }
        public Task RevokeRefreshTokenAsync(ClaimsPrincipal user, UserTokenRequestParameters? parameters = null, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class AccessProvider : Ui.IUiAccessTokenProvider
    {
        public bool Rejected;
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>("access");
        public void RejectSession() => Rejected = true;
    }
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
