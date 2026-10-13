using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Components.Authorization;

namespace Flowbit.Ui.Auth;

public interface IUiAccessTokenProvider
{
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken);
    void RejectSession();
}

public sealed class DevelopmentAccessTokenProvider(TokenState state) : IUiAccessTokenProvider
{
    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult(state.Token);
    public void RejectSession() { }
}

public sealed class ApiActorContextClient(IHttpClientFactory clients)
{
    public const string ClientName = "Flowbit.Authentication";
    public async Task<ActorContextDto> ResolveAsync(string accessToken, CancellationToken ct)
    {
        using var client = clients.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/context");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UiSessionExpiredException();
        if (!response.IsSuccessStatusCode) throw new UiAuthenticationUnavailableException();
        var context = await response.Content.ReadFromJsonAsync<ActorContextDto>(ct);
        if (context is null || string.IsNullOrWhiteSpace(context.User)) throw new UiSessionExpiredException();
        return context;
    }
}

public sealed class OidcAccessTokenProvider(AuthenticationStateProvider authentication, UiSessionStore sessions,
    IUserTokenManager manager, ApiActorContextClient actorClient, TimeProvider clock,
    ILogger<OidcAccessTokenProvider> logger) : IUiAccessTokenProvider
{
    private string? sessionId;
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var principal = (await authentication.GetAuthenticationStateAsync()).User;
        sessionId = UiSessionStore.SessionId(principal);
        var record = sessions.Find(sessionId) ?? throw new UiSessionExpiredException();
        await record.RefreshLock.WaitAsync(cancellationToken);
        try
        {
            if (sessions.Find(sessionId) is null) throw new UiSessionExpiredException();
            var token = sessions.ReadToken(record);
            if (token.RefreshToken is not null && token.Expiration <= clock.GetUtcNow().AddSeconds(60))
            {
                var result = await manager.GetAccessTokenAsync(principal, new UserTokenRequestParameters
                {
                    ChallengeScheme = Scheme.Parse(UiAuthentication.OidcScheme),
                    SignInScheme = Scheme.Parse(UiAuthentication.CookieScheme)
                }, cancellationToken);
                if (!result.Succeeded)
                {
                    if (result.FailedResult.Error is "invalid_grant" or "session_expired") throw new UiSessionExpiredException();
                    throw new UiAuthenticationUnavailableException();
                }
                token = result.Token;
                UiAuthenticationDiagnostics.Record("refresh", "success");
            }
            if (sessions.Find(sessionId) is null || token.Expiration <= clock.GetUtcNow()
                || token.Expiration == DateTimeOffset.MaxValue) throw new UiSessionExpiredException();
            if (sessions.NeedsActorRefresh(record))
                sessions.UpdateActor(record, await actorClient.ResolveAsync(token.AccessToken.ToString(), cancellationToken));
            // Actor lookup performs network I/O. A token that was valid before
            // that await must still be valid when released to the API transport.
            if (sessions.Find(sessionId) is null || token.Expiration <= clock.GetUtcNow()
                || token.Expiration == DateTimeOffset.MaxValue) throw new UiSessionExpiredException();
            return token.AccessToken.ToString();
        }
        catch (UiSessionExpiredException)
        {
            sessions.Revoke(sessionId);
            logger.LogInformation("Authentication outcome {Outcome}", "session_expired");
            UiAuthenticationDiagnostics.Record("acquire", "session_expired");
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            logger.LogWarning("Authentication outcome {Outcome}", "provider_unavailable");
            UiAuthenticationDiagnostics.Record("acquire", "provider_unavailable");
            throw new UiAuthenticationUnavailableException();
        }
        finally { record.RefreshLock.Release(); }
    }
    public void RejectSession() => sessions.Revoke(sessionId);
}

public sealed class UiAuthenticationUnavailableException() : InvalidOperationException("Authentication is temporarily unavailable. Try again shortly.");
