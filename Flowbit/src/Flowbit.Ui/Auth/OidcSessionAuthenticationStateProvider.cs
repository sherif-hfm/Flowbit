using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Flowbit.Ui.Auth;

public sealed class OidcSessionAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly UiSessionStore sessions;
    private readonly TokenState state;
    private readonly UiAuthenticationOptions options;
    private readonly LocalAccessTokenProvider? local;
    private string? currentSessionId;

    public OidcSessionAuthenticationStateProvider(ILoggerFactory loggerFactory, UiSessionStore sessions,
        TokenState state, UiAuthenticationOptions options, LocalAccessTokenProvider? local = null) : base(loggerFactory)
    {
        this.sessions = sessions;
        this.state = state;
        this.options = options;
        this.local = local;
        sessions.Changed += SessionChanged;
    }

    protected override TimeSpan RevalidationInterval => options.Session.RevalidationInterval;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var authentication = await base.GetAuthenticationStateAsync();
        currentSessionId = UiSessionStore.SessionId(authentication.User);
        if (ApplySession()) return authentication;
        return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
    }

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        if (options.IsLocal && local is not null)
        {
            try { await local.GetTokenInfoAsync(authenticationState.User, cancellationToken); }
            catch (UiSessionExpiredException) { return false; }
            catch (UiAuthenticationUnavailableException) { return true; }
        }
        return ApplySession();
    }

    private bool ApplySession()
    {
        var record = sessions.Find(currentSessionId);
        if (record is null)
        {
            state.InvalidateOidcSession();
            return false;
        }
        lock (record.Gate) state.ApplyOidcSession(record.Id, record.Actor);
        return true;
    }

    private void SessionChanged(string id)
    {
        if (id != currentSessionId) return;
        if (!ApplySession()) SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) sessions.Changed -= SessionChanged;
        base.Dispose(disposing);
    }
}
