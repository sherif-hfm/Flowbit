using System.Security.Claims;
using System.Text.Encodings.Web;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace Flowbit.Ui.Auth;

public static class LocalUiAuthentication
{
    public static void MapLocalUiAuthentication(this WebApplication app)
    {
        app.MapGet("/auth/login", (HttpContext http, IAntiforgery antiforgery, string? returnUrl)
            => LoginPage(http, antiforgery, returnUrl)).AllowAnonymous();
        app.MapPost("/auth/login", async (HttpContext http, IAntiforgery antiforgery,
            IHttpClientFactory clients, UiSessionStore sessions, UiAuthenticationOptions options) =>
        {
            try { await antiforgery.ValidateRequestAsync(http); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var target = UiAuthentication.LocalReturnUrl(form["returnUrl"]);
            try
            {
                using var client = clients.CreateClient(ApiActorContextClient.ClientName);
                using var response = await client.PostAsJsonAsync("/api/auth/token", new LocalLoginRequest(form["username"].ToString(), form["password"].ToString()), http.RequestAborted);
                if (!response.IsSuccessStatusCode)
                    return LoginPage(http, antiforgery, target, response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? "Too many attempts. Try again shortly." : "Sign-in could not be completed. Check your credentials or try again shortly.");
                var pair = await response.Content.ReadFromJsonAsync<LocalTokenPairDto>(http.RequestAborted) ?? throw new UiAuthenticationUnavailableException();
                var actor = await new ApiActorContextClient(clients).ResolveAsync(pair.AccessToken, http.RequestAborted);
                var claims = new List<Claim> { new("Username", actor.User!) };
                claims.AddRange(actor.Roles.Select(r => new Claim("roles", r)));
                var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, UiAuthentication.CookieScheme, "Username", "roles"));
                sessions.Revoke(UiSessionStore.SessionId(http.User));
                var remaining = pair.RefreshExpiresAt - DateTimeOffset.UtcNow;
                var id = sessions.Create(principal, LocalAccessTokenProvider.ToToken(pair), actor,
                    remaining < options.Session.AbsoluteLifetime ? remaining : options.Session.AbsoluteLifetime);
                try
                {
                    await http.SignInAsync(UiAuthentication.CookieScheme, principal,
                        new AuthenticationProperties { IsPersistent = false, AllowRefresh = false, ExpiresUtc = sessions.Find(id)!.ExpiresAt });
                }
                catch { sessions.Revoke(id); throw; }
                return Results.LocalRedirect(target);
            }
            catch (Exception) when (!http.RequestAborted.IsCancellationRequested)
            { return LoginPage(http, antiforgery, target, "Sign-in is temporarily unavailable. Try again shortly."); }
        }).AllowAnonymous();
        app.MapPost("/auth/logout", async (HttpContext http, IAntiforgery antiforgery, UiSessionStore sessions, IHttpClientFactory clients) =>
        {
            try { await antiforgery.ValidateRequestAsync(http); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var id = UiSessionStore.SessionId(http.User);
            var record = sessions.Find(id);
            if (record is not null)
            {
                await record.RefreshLock.WaitAsync(http.RequestAborted);
                try
                {
                    using var client = clients.CreateClient(ApiActorContextClient.ClientName);
                    using var response = await client.PostAsJsonAsync("/api/auth/logout", new LocalRefreshRequest(sessions.ReadToken(record).RefreshToken?.ToString() ?? ""), http.RequestAborted);
                }
                catch (Exception) { /* Always end this browser's session, including during API outages. */ }
                finally { sessions.Revoke(id); record.RefreshLock.Release(); }
            }
            await http.SignOutAsync(UiAuthentication.CookieScheme);
            return Results.Redirect("/auth/signed-out");
        }).AllowAnonymous();
        app.MapGet("/auth/signed-out", () => Results.Content(Page("Signed out", "<p>You are signed out of Flowbit.</p><a class=\"btn btn-primary\" href=\"/auth/login\">Sign in</a>"), "text/html")).AllowAnonymous();
    }
    private static IResult LoginPage(HttpContext http, IAntiforgery antiforgery, string? returnUrl, string? error = null)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Headers.CacheControl = "no-store, private";
        string E(string? value) => HtmlEncoder.Default.Encode(value ?? "");
        var form = $"""
            <p>Sign in with your Flowbit username and password.</p>
            {(error is null ? "" : $"<div class=\"alert alert-danger\" role=\"alert\">{E(error)}</div>")}
            <form method="post" action="/auth/login">
            <input type="hidden" name="{E(tokens.FormFieldName)}" value="{E(tokens.RequestToken)}">
            <input type="hidden" name="returnUrl" value="{E(UiAuthentication.LocalReturnUrl(returnUrl))}">
            <label class="form-label" for="username">Username</label><input class="form-control mb-3" id="username" name="username" autocomplete="username" maxlength="300" required autofocus>
            <label class="form-label" for="password">Password</label><input class="form-control mb-4" id="password" name="password" type="password" autocomplete="current-password" maxlength="128" required>
            <button class="btn btn-primary" type="submit">Sign in</button></form>
            """;
        return Results.Content(Page("Sign in", form), "text/html");
    }
    private static string Page(string title, string body) => $"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{title} · Flowbit</title><link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css"><link rel="stylesheet" href="/app.css"><link rel="icon" href="/branding/flowbit-mark.svg"></head><body><main class="container" style="max-width:30rem;margin:4rem auto;padding:1rem"><section class="panel p-4"><img src="/branding/flowbit-mark.svg" alt="Flowbit" width="40" height="40" class="mb-4"><h1>{title}</h1>{body}</section></main></body></html>
        """;
}
