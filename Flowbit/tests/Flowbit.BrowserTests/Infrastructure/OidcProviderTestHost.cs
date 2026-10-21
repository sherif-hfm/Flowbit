using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Flowbit.BrowserTests.Infrastructure;

/// <summary>
/// A fixture-owned identity provider. Tests use its real browser login and code
/// exchange; the Flowbit application is never given injected session/database state.
/// One-use codes, S256 verification and rotating refresh tokens enforce the protocol.
/// </summary>
public sealed class OidcProviderTestHost : IAsyncDisposable
{
    private readonly RSA rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, AuthorizationGrant> codes = new();
    private readonly ConcurrentDictionary<string, RefreshGrant> refreshTokens = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> expirations = new();
    private readonly ConcurrentDictionary<string, int> refreshCounts = new();
    private readonly ConcurrentDictionary<string, string> lastAccessTokens = new();
    private readonly ConcurrentQueue<bool> codeExchanges = new();
    private WebApplication? app;
    private int nextFailure;
    public const string ClientId = "flowbit-browser-ui";
    public const string Audience = "flowbit-browser-api";
    public string ClientSecret { get; } = Guid.NewGuid().ToString("N");
    public string Authority { get; private set; } = "";
    public string? UiOrigin { get; set; }
    public bool RequirePkce { get; set; }
    public IReadOnlyList<bool> CodeExchanges => codeExchanges.ToArray();
    public int RefreshCount(string user) => refreshCounts.GetValueOrDefault(user);
    public DateTimeOffset AccessTokenExpiresAt(string user) => expirations[user];
    public string LastAccessToken(string user) => lastAccessTokens[user];
    public AuthorizationResponse? LastAuthorizationResponse { get; private set; }
    public void FailNextAuthorization(AuthorizationFailure failure) => Interlocked.Exchange(ref nextFailure, (int)failure);
    public void RevokeRefreshTokens(string user)
    {
        foreach (var token in refreshTokens.Where(pair => pair.Value.User == user)) refreshTokens.TryRemove(token.Key, out _);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        app = builder.Build();
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = Authority,
            authorization_endpoint = Authority + "/authorize",
            token_endpoint = Authority + "/token",
            jwks_uri = Authority + "/jwks",
            end_session_endpoint = Authority + "/logout",
            response_types_supported = new[] { "code" },
            response_modes_supported = new[] { "form_post", "query" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post", "client_secret_basic" },
            code_challenge_methods_supported = new[] { "S256" },
            scopes_supported = new[] { "openid", "profile", "offline_access", "flowbit.api" }
        }));
        app.MapGet("/jwks", () =>
        {
            var parameters = rsa.ExportParameters(false);
            return Results.Json(new { keys = new[] { new
            {
                kty = "RSA", use = "sig", kid = "browser-oidc-key", alg = "RS256",
                n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!)
            } } });
        });
        app.MapGet("/authorize", (HttpContext context) =>
        {
            if (context.Request.Query["client_id"] != ClientId
                || context.Request.Query["redirect_uri"] != UiOrigin + "/signin-oidc"
                || context.Request.Query["response_type"] != "code") return Results.BadRequest();
            var fields = string.Join("", context.Request.Query.Select(pair => Hidden(pair.Key, pair.Value.ToString())));
            return Results.Content($"""
                <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Fixture identity provider</title></head>
                <body><main><h1>Sign in to the test identity provider</h1><form method="post" action="/authorize">
                {fields}<label for="user">User</label><select id="user" name="user"><option value="alice">Alice</option><option value="bob">Bob</option></select>
                <button type="submit">Continue</button></form></main></body></html>
                """, "text/html");
        });
        app.MapPost("/authorize", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var redirect = form["redirect_uri"].ToString();
            var challenge = form["code_challenge"].ToString();
            var method = form["code_challenge_method"].ToString();
            var user = form["user"].ToString();
            if (form["client_id"] != ClientId || redirect != UiOrigin + "/signin-oidc"
                || user is not ("alice" or "bob")
                || string.IsNullOrWhiteSpace(form["state"]) || string.IsNullOrWhiteSpace(form["nonce"])
                || RequirePkce && (challenge.Length == 0 || method != "S256")
                || challenge.Length > 0 && method != "S256") return Results.BadRequest();
            var code = Guid.NewGuid().ToString("N");
            var failure = (AuthorizationFailure)Interlocked.Exchange(ref nextFailure, 0);
            codes[code] = new AuthorizationGrant(user, form["nonce"].ToString(), redirect, challenge,
                DateTimeOffset.UtcNow.AddMinutes(1), failure);
            var state = failure == AuthorizationFailure.InvalidState ? "tampered-provider-state" : form["state"].ToString();
            LastAuthorizationResponse = new AuthorizationResponse(code, state);
            if (form["response_mode"] == "form_post")
                return Results.Content($"""
                    <!doctype html><html><head><title>Completing sign-in</title></head><body>
                    <form id="callback" method="post" action="{WebUtility.HtmlEncode(redirect)}">
                    {Hidden("code", code)}{Hidden("state", state)}{Hidden("iss", Authority)}
                    <button type="submit">Return to Flowbit</button></form><script>document.getElementById('callback').submit();</script></body></html>
                    """, "text/html");
            return Results.Redirect(redirect + "?code=" + Uri.EscapeDataString(code)
                + "&state=" + Uri.EscapeDataString(state));
        });
        app.MapPost("/token", (Func<HttpContext, Task<IResult>>)ExchangeAsync);
        app.MapGet("/logout", (HttpContext context) =>
        {
            var redirect = context.Request.Query["post_logout_redirect_uri"].ToString();
            if (redirect != UiOrigin + "/signout-callback-oidc") return Results.BadRequest();
            return Results.Redirect(redirect + "?state=" + Uri.EscapeDataString(context.Request.Query["state"].ToString()));
        });
        app.MapGet("/favicon.ico", () => Results.NoContent());
        await app.StartAsync(cancellationToken);
        Authority = app.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
    }

    private async Task<IResult> ExchangeAsync(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var clientId = form["client_id"].ToString();
        var secret = form["client_secret"].ToString();
        if (context.Request.Headers.Authorization.ToString().StartsWith("Basic ", StringComparison.Ordinal))
        {
            var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(context.Request.Headers.Authorization.ToString()[6..])).Split(':', 2);
            clientId = Uri.UnescapeDataString(credentials[0]);
            secret = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : "";
        }
        if (clientId != ClientId || secret != ClientSecret)
            return Results.Json(new { error = "invalid_client" }, statusCode: 401);

        if (form["grant_type"] == "authorization_code")
        {
            if (!codes.TryRemove(form["code"].ToString(), out var grant) || grant.ExpiresAt <= DateTimeOffset.UtcNow
                || form["redirect_uri"] != grant.RedirectUri) return InvalidGrant();
            var verifier = form["code_verifier"].ToString();
            if (grant.Challenge.Length > 0 && (verifier.Length is < 43 or > 128
                || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != grant.Challenge))
                return InvalidGrant();
            if (grant.Challenge.Length == 0 && verifier.Length != 0) return InvalidGrant();
            codeExchanges.Enqueue(grant.Challenge.Length > 0);
            return IssueTokens(grant.User, grant.Nonce, grant.Failure);
        }
        if (form["grant_type"] == "refresh_token")
        {
            if (!refreshTokens.TryRemove(form["refresh_token"].ToString(), out var grant)) return InvalidGrant();
            refreshCounts.AddOrUpdate(grant.User, 1, (_, count) => count + 1);
            return IssueTokens(grant.User, grant.Nonce);
        }
        return Results.Json(new { error = "unsupported_grant_type" }, statusCode: 400);
    }

    private IResult IssueTokens(string user, string nonce, AuthorizationFailure failure = AuthorizationFailure.None)
    {
        var now = DateTimeOffset.UtcNow;
        // The server refreshes sixty seconds before expiry. A 65-second real
        // token lets the browser exercise refresh after five seconds, with no clock injection.
        var expires = now.AddSeconds(65);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "browser-oidc-key" };
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
        var access = new JwtSecurityToken(Authority, Audience,
            [new("sub", user), new("Username", user), new("roles", "admin"),
             new("scope", "openid profile offline_access flowbit.api"), new("jti", Guid.NewGuid().ToString("N"))],
            now.AddSeconds(-1).UtcDateTime, expires.UtcDateTime, credentials);
        var identity = new JwtSecurityToken(failure == AuthorizationFailure.WrongIssuer ? Authority + "/wrong-issuer" : Authority, ClientId,
            [new("sub", user), new("name", user + " display name"), new("nonce", failure == AuthorizationFailure.InvalidNonce ? "tampered-provider-nonce" : nonce),
             new("iat", now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)],
            now.AddSeconds(-1).UtcDateTime, now.AddMinutes(5).UtcDateTime, credentials);
        var handler = new JwtSecurityTokenHandler();
        var accessText = handler.WriteToken(access);
        var refresh = Guid.NewGuid().ToString("N");
        refreshTokens[refresh] = new RefreshGrant(user, nonce);
        expirations[user] = expires;
        lastAccessTokens[user] = accessText;
        return Results.Json(new
        {
            access_token = accessText, id_token = handler.WriteToken(identity), refresh_token = refresh,
            token_type = "Bearer", expires_in = 65, scope = "openid profile offline_access flowbit.api"
        });
    }

    private static IResult InvalidGrant() => Results.Json(new { error = "invalid_grant" }, statusCode: 400);
    private static string Hidden(string name, string value) => $"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(name)}\" value=\"{WebUtility.HtmlEncode(value)}\">";
    private sealed record AuthorizationGrant(string User, string Nonce, string RedirectUri, string Challenge, DateTimeOffset ExpiresAt, AuthorizationFailure Failure);
    private sealed record RefreshGrant(string User, string Nonce);
    public sealed record AuthorizationResponse(string Code, string State);
    public enum AuthorizationFailure { None, InvalidState, InvalidNonce, WrongIssuer }

    public async ValueTask DisposeAsync()
    {
        if (app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
        }
        rsa.Dispose();
    }
}
