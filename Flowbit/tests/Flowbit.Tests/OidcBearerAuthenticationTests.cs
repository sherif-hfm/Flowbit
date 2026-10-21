using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Flowbit.Tests;

/// <summary>Uses production registration, real JwtBearer, HTTP discovery/JWKS, and RSA signatures.</summary>
public sealed class OidcBearerAuthenticationTests
{
    [Theory]
    [InlineData("Production", "DevelopmentToken", "https://issuer.test", false)]
    [InlineData("Testing", "DevelopmentToken", "https://issuer.test", false)]
    [InlineData("Production", "OpenIdConnect", "http://issuer.test", true)]
    [InlineData("Development", "OpenIdConnect", "http://issuer.test", false)]
    [InlineData("Development", "OpenIdConnect", "http://issuer.test", true)]
    [InlineData("Development", "OpenIdConnect", "", false)]
    [InlineData("Development", "unknown", "https://issuer.test", false)]
    public void Invalid_trust_configuration_fails_before_serving(
        string environment, string mode, string authority, bool allowHttp)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        var options = new FlowbitAuthenticationOptions
        {
            Mode = mode,
            Oidc = new() { Authority = authority, AllowHttpMetadata = allowHttp }
        };
        Assert.Throws<InvalidOperationException>(() => options.Validate(builder.Environment));
    }

    [Fact]
    public async Task Discovery_access_token_preserves_actor_roles_and_selected_audit_claims()
    {
        await using var host = await BearerHost.CreateAsync();
        using var response = await host.SendAsync(host.Token(extraClaims:
        [
            new("roles", "Reviewer"), new("roles", "reviewer"),
            new("department", "Finance"), new("department", "HR")
        ]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actor = await response.Content.ReadFromJsonAsync<ActorResponse>();
        Assert.Equal("alice", actor!.User);
        Assert.Equal(["admin", "Reviewer"], actor.Roles);
        Assert.Equal(["Finance", "HR"], actor.AuditClaims!["department"]);
        Assert.True(host.DiscoveryRequests > 0);
        Assert.True(host.JwksRequests > 0);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("signature")]
    [InlineData("malformed")]
    [InlineData("missing_actor")]
    [InlineData("ambiguous_actor")]
    [InlineData("oversized_actor")]
    [InlineData("numeric_actor")]
    [InlineData("numeric_role")]
    [InlineData("nested_role")]
    [InlineData("blank_role")]
    public async Task Invalid_access_tokens_return_401_without_redirects(string defect)
    {
        await using var host = await BearerHost.CreateAsync();
        using var untrusted = RSA.Create(2048);
        var token = defect switch
        {
            "issuer" => host.Token(issuer: "https://other.test"),
            "audience" => host.Token(audience: "flowbit-ui"),
            "expired" => host.Token(expires: DateTime.UtcNow.AddMinutes(-5)),
            "future" => host.Token(notBefore: DateTime.UtcNow.AddMinutes(5)),
            "signature" => host.Token(key: new RsaSecurityKey(untrusted) { KeyId = "untrusted" }),
            "missing_actor" => host.Token(actor: null, includeSubject: false),
            "ambiguous_actor" => host.Token(extraClaims: [new("Username", "bob")]),
            "oversized_actor" => host.Token(actor: new string('a', 301)),
            "numeric_actor" => host.Token(actor: null, extraClaims: [new("Username", "42", ClaimValueTypes.Integer)]),
            "numeric_role" => host.Token(extraClaims: [new("roles", "42", ClaimValueTypes.Integer)]),
            "nested_role" => host.Token(extraClaims: [new("roles", "{\"admin\":true}", JsonClaimValueTypes.Json)]),
            "blank_role" => host.Token(extraClaims: [new("roles", " ")]),
            _ => "not.a.token"
        };
        using var response = await host.SendAsync(token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Id_token_cannot_be_used_as_an_api_access_token()
    {
        await using var host = await BearerHost.CreateAsync();
        using var response = await host.SendAsync(host.Token(audience: "flowbit-ui", scope: null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("openid profile")]
    [InlineData("Flowbit.Api")]
    public async Task Missing_delegated_scope_returns_403_on_default_and_named_bearer_policies(string? scope)
    {
        await using var host = await BearerHost.CreateAsync();
        foreach (var path in new[] { "/context", "/shared-admin" })
        {
            using var response = await host.SendAsync(host.Token(scope: scope), path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
    }

    [Fact]
    public async Task External_mode_never_accepts_the_configured_development_key()
    {
        await using var host = await BearerHost.CreateAsync();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiTestAuth.Key));
        using var response = await host.SendAsync(host.Token(key: key, algorithm: SecurityAlgorithms.HmacSha256));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Object_scope_cannot_grant_access_by_containing_the_scope_as_nested_text()
    {
        await using var host = await BearerHost.CreateAsync();
        using var response = await host.SendAsync(host.Token(scope: null, extraClaims:
            [new("scope", "{\"nested\":\" flowbit.api \"}", JsonClaimValueTypes.Json)]));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Key_rotation_refreshes_discovery_keys_and_accepts_the_new_signature()
    {
        await using var host = await BearerHost.CreateAsync();
        using (var first = await host.SendAsync(host.Token())) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        host.RotateKey();
        var token = host.Token();
        // The framework refreshes on an unknown signing key; no application key cache is used.
        using var firstRotated = await host.SendAsync(token);
        if (firstRotated.StatusCode != HttpStatusCode.OK)
        {
            var options = host.App.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Bearer");
            options.ConfigurationManager!.RequestRefresh();
            await options.ConfigurationManager.GetConfigurationAsync(CancellationToken.None);
        }
        using var response = await host.SendAsync(token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(host.JwksRequests >= 2);
    }

    [Fact]
    public async Task Provider_claim_names_are_configurable_and_engine_actor_override_remains_authoritative()
    {
        await using var host = await BearerHost.CreateAsync("preferred_username", "application_role", "scp", "employee_id");
        using var response = await host.SendAsync(host.Token(extraClaims: [new("employee_id", "employee-123")]));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actor = await response.Content.ReadFromJsonAsync<ActorResponse>();
        Assert.Equal("employee-123", actor!.User);
        Assert.Equal(["admin"], actor.Roles);
    }

    [Fact]
    public async Task Scope_gate_preserves_the_independent_managed_machine_identity()
    {
        var settings = new FlowbitAuthenticationOptions();
        var requirement = new FlowbitBearerScopeRequirement();
        var client = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(SharedVariableClientAuthenticationDefaults.ScopeClaim, "shared-variables.read")],
            SharedVariableClientAuthenticationDefaults.Scheme));
        var context = new AuthorizationHandlerContext([requirement], client, null);
        await new FlowbitBearerScopeHandler(settings).HandleAsync(context);
        Assert.True(context.HasSucceeded);
    }

    private sealed record ActorResponse(string? User, string[] Roles, Dictionary<string, string[]>? AuditClaims);

    [Theory]
    [MemberData(nameof(DefaultIdentityBearerTests.IdentityCases), MemberType = typeof(DefaultIdentityBearerTests))]
    public async Task Oidc_uses_the_default_identity_chain(string scenario, Claim[] claims, string? expected)
    {
        await using var host = await BearerHost.CreateAsync();
        using var response = await host.SendAsync(host.Token(actor: null, includeSubject: false, extraClaims: claims));
        Assert.True(response.StatusCode == (expected is null ? HttpStatusCode.Unauthorized : HttpStatusCode.OK), $"{scenario}: unexpected {response.StatusCode}");
        if (expected is not null) Assert.Equal(expected, (await response.Content.ReadFromJsonAsync<ActorResponse>())!.User);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("flowbit_user")]
    [InlineData(ClaimTypes.Name)]
    [InlineData("sub")]
    public async Task Oidc_name_override_is_strict_even_when_default_claims_are_available(string claimType)
    {
        await using var host = await BearerHost.CreateAsync(nameClaim: claimType);
        using var accepted = await host.SendAsync(host.Token(includeSubject: false, extraClaims: [new("Username", "different-user")]));
        Assert.Equal("alice", (await accepted.Content.ReadFromJsonAsync<ActorResponse>())!.User);
        using var rejected = await host.SendAsync(host.Token(actor: null, includeSubject: false, extraClaims: [new("Username", "alice")]));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task Database_override_has_precedence_over_the_oidc_name_override_and_does_not_fall_back()
    {
        await using var host = await BearerHost.CreateAsync(nameClaim: "flowbit_user", actorClaim: "employee_id");
        using var accepted = await host.SendAsync(host.Token(extraClaims: [new("Username", "different-user"), new("employee_id", "employee-user")]));
        Assert.Equal("employee-user", (await accepted.Content.ReadFromJsonAsync<ActorResponse>())!.User);
        using var rejected = await host.SendAsync(host.Token(extraClaims: [new("Username", "different-user")]));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    private sealed class BearerHost : IAsyncDisposable
    {
        private readonly List<RSA> keys = [];
        private RSA rsa = null!;
        private string keyId = "";
        private WebApplication issuerApp = null!;
        private string authority = "";
        private string nameClaim = "Username";
        private string roleClaim = "roles";
        private string scopeClaim = "scope";
        public WebApplication App { get; private set; } = null!;
        private HttpClient client = null!;
        public int DiscoveryRequests;
        public int JwksRequests;

        public static async Task<BearerHost> CreateAsync(
            string nameClaim = "Username", string roleClaim = "roles", string scopeClaim = "scope", string? actorClaim = null)
        {
            var host = new BearerHost { nameClaim = nameClaim, roleClaim = roleClaim, scopeClaim = scopeClaim };
            host.RotateKey();
            var issuerBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            TestWebHostDefaults.IsolateFromMachine(issuerBuilder);
            issuerBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            host.issuerApp = issuerBuilder.Build();
            host.issuerApp.MapGet("/.well-known/openid-configuration", () =>
            {
                Interlocked.Increment(ref host.DiscoveryRequests);
                return Results.Json(new { issuer = host.authority, jwks_uri = host.authority + "/jwks" });
            });
            host.issuerApp.MapGet("/jwks", () =>
            {
                Interlocked.Increment(ref host.JwksRequests);
                var key = host.rsa.ExportParameters(false);
                return Results.Json(new { keys = new[] { new
                {
                    kty = "RSA", use = "sig", kid = host.keyId, alg = "RS256",
                    n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!)
                } } });
            });
            await host.issuerApp.StartAsync();
            host.authority = host.issuerApp.Urls.Single();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            TestWebHostDefaults.IsolateFromMachine(builder);
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "OpenIdConnect",
                ["Authentication:Oidc:Authority"] = host.authority,
                ["Authentication:Oidc:Audience"] = "flowbit-api",
                ["Authentication:Oidc:AllowHttpMetadata"] = "true",
                ["Authentication:Oidc:NameClaimType"] = nameClaim,
                ["Authentication:Oidc:RoleClaimType"] = roleClaim,
                ["Authentication:Oidc:ScopeClaimType"] = scopeClaim,
                ["Authentication:Oidc:RequiredScope"] = "flowbit.api",
                ["Jwt:Key"] = ApiTestAuth.Key
            });
            builder.Services.AddFlowbitAuthentication(builder.Configuration, builder.Environment);
            var identity = new ActorIdentityConfiguration();
            identity.Initialize(actorClaim is null ? null : new EngineSettingRecord(
                1, "Authentication", "UserIdentityClaim", actorClaim, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            builder.Services.AddSingleton(identity);
            builder.Services.AddSingleton(new WorkflowAuditOptions { AllowedClaims = ["department"] });
            builder.Services.AddSingleton<IActorContextResolver, ActorContextResolver>();
            builder.Services.AddSingleton<IEngineSettingsService, EmptySettings>();
            builder.Services.AddSingleton<ISharedVariableClientService>(_ =>
                throw new InvalidOperationException("This bearer-only fixture does not invoke managed client authentication."));
            host.App = builder.Build();
            host.App.UseAuthentication();
            host.App.UseAuthorization();
            host.App.MapGet("/context", (HttpContext context, IActorContextResolver resolver) =>
            {
                var actor = resolver.Resolve(context.User);
                return Results.Json(new ActorResponse(actor.User, actor.Roles.ToArray(),
                    actor.AuditClaims?.ToDictionary(pair => pair.Key, pair => pair.Value)));
            }).RequireAuthorization();
            host.App.MapGet("/shared-admin", () => Results.Ok()).RequireAuthorization(SharedVariableAuthorizationPolicies.Administrator);
            await host.App.StartAsync();
            host.client = host.App.GetTestClient();
            return host;
        }

        public void RotateKey()
        {
            rsa = RSA.Create(2048);
            keys.Add(rsa);
            keyId = Guid.NewGuid().ToString("N");
        }

        public string Token(string? issuer = null, string audience = "flowbit-api", string? actor = "alice",
            string? scope = "openid flowbit.api", IEnumerable<Claim>? extraClaims = null,
            DateTime? expires = null, DateTime? notBefore = null, SecurityKey? key = null,
            string algorithm = SecurityAlgorithms.RsaSha256, bool includeSubject = true)
        {
            var claims = new List<Claim> { new(roleClaim, "admin") };
            if (includeSubject) claims.Add(new("sub", "opaque-subject"));
            if (actor is not null) claims.Add(new(nameClaim, actor));
            if (scope is not null) claims.Add(new(scopeClaim, scope));
            if (extraClaims is not null) claims.AddRange(extraClaims);
            var token = new JwtSecurityToken(issuer ?? authority, audience, claims,
                notBefore ?? DateTime.UtcNow.AddHours(-1), expires ?? DateTime.UtcNow.AddMinutes(15),
                new SigningCredentials(key ?? new RsaSecurityKey(rsa) { KeyId = keyId }, algorithm));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public Task<HttpResponseMessage> SendAsync(string token, string path = "/context")
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            client?.Dispose();
            if (App is not null) await App.DisposeAsync();
            if (issuerApp is not null) await issuerApp.DisposeAsync();
            foreach (var key in keys) key.Dispose();
        }
    }

    private sealed class EmptySettings : IEngineSettingsService
    {
        public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken) => Task.FromResult<EngineSettingRecord?>(null);
        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
