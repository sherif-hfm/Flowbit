using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Components.Authorization;

namespace Flowbit.Ui.Auth;

public sealed class LocalAccessTokenProvider(IServiceProvider services, UiSessionStore sessions,
    IHttpClientFactory clients, TimeProvider clock) : IUiAccessTokenProvider, IUiTokenInfoProvider
{
    private string? sessionId;
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var principal = (await services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync()).User;
        return (await GetTokenInfoAsync(principal, ct)).AccessToken;
    }
    internal async Task<UiTokenInfo> GetTokenInfoAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        sessionId = UiSessionStore.SessionId(principal);
        var record = sessions.Find(sessionId) ?? throw new UiSessionExpiredException();
        await record.RefreshLock.WaitAsync(ct);
        try
        {
            if (sessions.Find(sessionId) is null) throw new UiSessionExpiredException();
            var token = sessions.ReadToken(record);
            using var client = clients.CreateClient(ApiActorContextClient.ClientName);
            if (token.Expiration <= clock.GetUtcNow().AddSeconds(60))
            {
                using var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new LocalRefreshRequest(token.RefreshToken?.ToString() ?? ""), ct);
                Check(refreshed);
                var pair = await refreshed.Content.ReadFromJsonAsync<LocalTokenPairDto>(ct) ?? throw new UiAuthenticationUnavailableException();
                sessions.UpdateToken(sessionId, ToToken(pair));
                token = sessions.ReadToken(record);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken.ToString());
            using var response = await client.SendAsync(request, ct);
            Check(response);
            if (sessions.NeedsActorRefresh(record))
            {
                var actor = await new ApiActorContextClient(clients).ResolveAsync(token.AccessToken.ToString(), ct);
                sessions.UpdateActor(record, actor);
            }
            if (sessions.Find(sessionId) is null || token.Expiration <= clock.GetUtcNow()) throw new UiSessionExpiredException();
            return new(record.Actor.User, record.Actor.Roles, token.AccessToken.ToString(), "Bearer", token.Expiration);
        }
        catch (UiSessionExpiredException) { sessions.Revoke(sessionId); throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (UiAuthenticationUnavailableException) { throw; }
        catch (Exception) { throw new UiAuthenticationUnavailableException(); }
        finally { record.RefreshLock.Release(); }
    }
    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UiSessionExpiredException();
        if (!response.IsSuccessStatusCode) throw new UiAuthenticationUnavailableException();
    }
    internal static UserToken ToToken(LocalTokenPairDto pair) => new()
    {
        AccessToken = AccessToken.Parse(pair.AccessToken), RefreshToken = RefreshToken.Parse(pair.RefreshToken),
        Expiration = pair.ExpiresAt, AccessTokenType = AccessTokenType.Parse("Bearer"), ClientId = ClientId.Parse("flowbit-local")
    };
    public void RejectSession() => sessions.Revoke(sessionId);
    Task<UiTokenInfo> IUiTokenInfoProvider.GetTokenInfoAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
        => GetTokenInfoAsync(principal, cancellationToken);
}
