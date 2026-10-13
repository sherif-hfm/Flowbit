using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Flowbit.Ui.Clients;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Flowbit.Ui.Auth;

public static class UiAuthentication
{
    public const string CookieScheme = "FlowbitCookie";
    public const string OidcScheme = "FlowbitOidc";

    public static UiAuthenticationOptions AddFlowbitUiAuthentication(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Authentication").Get<UiAuthenticationOptions>() ?? new();
        options.Validate(builder.Environment);
        var allowedHosts = (builder.Configuration["AllowedHosts"] ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!options.IsDevelopmentToken && !builder.Environment.IsDevelopment()
            && (allowedHosts.Length == 0 || allowedHosts.Contains("*", StringComparer.Ordinal)))
            throw new InvalidOperationException("Deployed OIDC requires explicit AllowedHosts for its public UI hostname.");
        var services = builder.Services;
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddAuthorization();
        services.Configure<HostFilteringOptions>(filter => filter.AllowedHosts = allowedHosts);
        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            // Host filtering can run before forwarded headers. Apply the same host
            // boundary to the forwarded value used for OIDC callback URLs.
            if (!allowedHosts.Contains("*", StringComparer.Ordinal))
                foreach (var host in allowedHosts) forwarded.AllowedHosts.Add(host);
            foreach (var proxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
                forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
        });
        if (options.IsDevelopmentToken)
        {
            services.AddSingleton<TokenState>();
            services.AddSingleton<DevTokenFactory>();
            services.AddScoped<IUiAccessTokenProvider, DevelopmentAccessTokenProvider>();
            return options;
        }

        var protection = services.AddDataProtection().SetApplicationName("Flowbit.Ui");
        if (!string.IsNullOrWhiteSpace(options.DataProtectionKeysPath))
            protection.PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionKeysPath));
        if (!string.IsNullOrWhiteSpace(options.DataProtectionCertificatePath))
            protection.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(
                options.DataProtectionCertificatePath, options.DataProtectionCertificatePassword));
        else if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();

        services.AddSingleton<UiSessionStore>();
        services.AddSingleton<IUiSessionStore>(sp => sp.GetRequiredService<UiSessionStore>());
        services.AddScoped(_ => { var state = new TokenState(); state.ConfigureOidcMode(); return state; });
        services.AddScoped<AuthenticationStateProvider, OidcSessionAuthenticationStateProvider>();
        services.AddCascadingAuthenticationState();
        services.AddScoped<IUiAccessTokenProvider, OidcAccessTokenProvider>();
        services.AddScoped<ApiActorContextClient>();

        services.AddAuthentication(authentication =>
        {
            authentication.DefaultScheme = CookieScheme;
            authentication.DefaultChallengeScheme = OidcScheme;
        }).AddCookie(CookieScheme, cookie =>
        {
            cookie.Cookie.Name = "__Host-Flowbit.Session";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.Path = "/";
            cookie.SlidingExpiration = false;
            cookie.ExpireTimeSpan = options.Session.AbsoluteLifetime;
            cookie.Events.OnValidatePrincipal = context =>
            {
                var store = context.HttpContext.RequestServices.GetRequiredService<UiSessionStore>();
                if (context.Principal is null || store.Find(UiSessionStore.SessionId(context.Principal)) is null)
                    context.RejectPrincipal();
                return Task.CompletedTask;
            };
        }).AddOpenIdConnect(OidcScheme, oidc =>
        {
            oidc.SignInScheme = CookieScheme;
            oidc.Authority = options.Oidc.Authority;
            oidc.ClientId = options.Oidc.ClientId;
            oidc.ClientSecret = options.Oidc.ClientSecret;
            oidc.ResponseType = OpenIdConnectResponseType.Code;
            oidc.UsePkce = options.Oidc.UsePkce;
            oidc.RequireHttpsMetadata = !options.Oidc.AllowHttpMetadata;
            oidc.MapInboundClaims = false;
            oidc.SaveTokens = false;
            oidc.GetClaimsFromUserInfoEndpoint = false;
            oidc.UseTokenLifetime = false;
            oidc.BackchannelTimeout = TimeSpan.FromSeconds(20);
            oidc.Scope.Clear();
            foreach (var scope in options.Oidc.Scopes.Distinct(StringComparer.Ordinal)) oidc.Scope.Add(scope);
            oidc.TokenValidationParameters.NameClaimType = "name";
            oidc.TokenValidationParameters.RoleClaimType = "roles";
            oidc.Events.OnTokenValidated = async context =>
            {
                try
                {
                    var response = context.TokenEndpointResponse;
                    if (response is null || context.Principal is null
                        || !double.TryParse(response.ExpiresIn, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                        || !double.IsFinite(seconds) || seconds <= 0 || seconds > 86400 * 30
                        || string.IsNullOrWhiteSpace(response.AccessToken)
                        || !string.Equals(response.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
                        throw new UiSessionExpiredException();
                    var expiration = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>()
                        .GetUtcNow().AddSeconds(seconds);
                    var actor = await context.HttpContext.RequestServices.GetRequiredService<ApiActorContextClient>()
                        .ResolveAsync(response.AccessToken, context.HttpContext.RequestAborted);
                    var token = new UserToken
                    {
                        AccessToken = AccessToken.Parse(response.AccessToken),
                        AccessTokenType = AccessTokenType.Parse("Bearer"), ClientId = ClientId.Parse(options.Oidc.ClientId),
                        RefreshToken = string.IsNullOrEmpty(response.RefreshToken) ? null : RefreshToken.Parse(response.RefreshToken),
                        IdentityToken = IdentityToken.Parse(response.IdToken),
                        Scope = string.IsNullOrEmpty(response.Scope) ? null : Scope.Parse(response.Scope),
                        Expiration = expiration
                    };
                    var store = context.HttpContext.RequestServices.GetRequiredService<UiSessionStore>();
                    var id = store.Create(context.Principal, token, actor, options.Session.AbsoluteLifetime);
                    context.Properties ??= new AuthenticationProperties();
                    context.Properties.Items[UiSessionStore.SessionClaim] = id;
                    context.Properties.ExpiresUtc = store.Find(id)!.ExpiresAt;
                    context.Properties.IsPersistent = false;
                    context.Properties.AllowRefresh = false;
                    UiAuthenticationDiagnostics.Record("login", "success");
                }
                catch (Exception)
                {
                    UiAuthenticationDiagnostics.Record("login", "failed");
                    context.Fail("Flowbit sign-in could not be completed.");
                }
            };
            oidc.Events.OnTicketReceived = async context =>
            {
                // Signing in as another user invalidates existing circuits in this browser.
                var store = context.HttpContext.RequestServices.GetRequiredService<UiSessionStore>();
                // Remote callbacks run before the default cookie authentication handler.
                // Explicit authentication also finds the prior session on that path.
                var previous = await context.HttpContext.AuthenticateAsync(CookieScheme);
                var oldId = previous.Principal is null ? null : UiSessionStore.SessionId(previous.Principal);
                if (oldId != UiSessionStore.SessionId(context.Principal!)) store.Revoke(oldId);
            };
            oidc.Events.OnRemoteFailure = context =>
            {
                if (context.Properties?.Items.TryGetValue(UiSessionStore.SessionClaim, out var id) == true)
                    context.HttpContext.RequestServices.GetRequiredService<UiSessionStore>().Revoke(id);
                context.HandleResponse();
                context.Response.Redirect("/auth/error");
                return Task.CompletedTask;
            };
            oidc.Events.OnRedirectToIdentityProviderForSignOut = context =>
            {
                if (context.Properties.Items.TryGetValue("logout_hint", out var hint)) context.ProtocolMessage.IdTokenHint = hint;
                return Task.CompletedTask;
            };
        });
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme).Configure<UiSessionStore>((cookie, store) => cookie.SessionStore = store);
        services.AddOpenIdConnectAccessTokenManagement(token =>
        {
            token.ChallengeScheme = Scheme.Parse(OidcScheme);
            token.RefreshBeforeExpiration = TimeSpan.FromSeconds(60);
        }).AddBlazorServerAccessTokenManagement<FlowbitUserTokenStore>();
        // Authentication failures are translated to fixed Flowbit outcomes, never provider response text.
        builder.Logging.AddFilter("Duende.AccessTokenManagement", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect", LogLevel.None);
        return options;
    }

    public static void MapFlowbitAuthentication(this WebApplication app, UiAuthenticationOptions options)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        if (options.IsDevelopmentToken) return;
        app.MapGet("/auth/login", (string? returnUrl) => Results.Challenge(
            new AuthenticationProperties { RedirectUri = LocalReturnUrl(returnUrl) }, [OidcScheme])).AllowAnonymous();
        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery,
            UiSessionStore store, IOptionsMonitor<OpenIdConnectOptions> oidcOptions) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var record = store.Find(UiSessionStore.SessionId(context.User));
            string? hint = null;
            if (record is not null)
            {
                try { hint = store.ReadToken(record).IdentityToken?.ToString(); } catch (UiSessionExpiredException) { }
                store.Revoke(record.Id);
            }
            await context.SignOutAsync(CookieScheme);
            var properties = new AuthenticationProperties { RedirectUri = "/auth/signed-out" };
            if (hint is not null) properties.Items["logout_hint"] = hint;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var provider = await oidcOptions.Get(OidcScheme).ConfigurationManager!.GetConfigurationAsync(timeout.Token);
                if (!string.IsNullOrEmpty(provider.EndSessionEndpoint)) return Results.SignOut(properties, [OidcScheme]);
            }
            catch (Exception) { /* Local logout already completed. */ }
            return Results.Redirect("/auth/signed-out?local=true");
        }).AllowAnonymous();
        app.MapGet("/auth/signed-out", (HttpContext context) => Results.Content(
            AuthPage("Signed out", context.Request.Query["local"] == "true"
                ? "You are signed out of Flowbit. Your identity-provider session may still be active."
                : "You are signed out of Flowbit."), "text/html")).AllowAnonymous();
        app.MapGet("/auth/error", () => Results.Content(AuthPage("Sign-in unavailable",
            "Flowbit could not complete sign-in. Check your access or try again shortly."), "text/html")).AllowAnonymous();
    }

    internal static string LocalReturnUrl(string? value) => !string.IsNullOrEmpty(value)
        && value[0] == '/' && (value.Length == 1 || value[1] is not ('/' or '\\'))
        && !value.Contains('\\') && !value.Any(char.IsControl) ? value : "/";

    private static string AuthPage(string title, string message) => $"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{title} · Flowbit</title><link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css"><link rel="stylesheet" href="/app.css"><link rel="icon" href="/branding/flowbit-mark.svg" type="image/svg+xml"></head>
        <body><main class="container" style="max-width:40rem;margin:4rem auto;padding:1rem"><section class="panel p-4" aria-labelledby="auth-heading"><img src="/branding/flowbit-mark.svg" width="40" height="40" alt="Flowbit" class="mb-4"><h1 id="auth-heading">{title}</h1><p>{message}</p><a href="/auth/login" class="btn btn-primary">Sign in</a></section></main></body></html>
        """;
}
