using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Auth;

/// <summary>
/// The current UI identity. Development mode holds its shared test token; OIDC mode
/// is circuit-scoped and exposes only a session identity and the API's actor context.
/// Access and refresh tokens remain in the server authentication session store.
/// </summary>
public sealed class TokenState
{
    private readonly Lock gate = new();
    private string? token;
    private string? sessionId;
    private string? identityKey;
    private string? currentUser;
    private IReadOnlyList<string> currentRoles = [];
    private bool identityResolved;
    private bool isDevelopment = true;
    private bool authReady = true;

    public event Action? Changed;

    public string? Token { get { lock (gate) return token; } }
    public string? IdentityKey { get { lock (gate) return identityKey; } }
    public string? CurrentUser { get { lock (gate) return currentUser; } }
    public IReadOnlyList<string> CurrentRoles { get { lock (gate) return currentRoles; } }
    public bool IdentityResolved { get { lock (gate) return identityResolved; } }
    public bool IsDevelopment { get { lock (gate) return isDevelopment; } }
    public bool AuthReady { get { lock (gate) return authReady; } }
    public bool HasToken
    {
        get { lock (gate) return isDevelopment ? !string.IsNullOrWhiteSpace(token) : sessionId is not null && identityResolved; }
    }

    public void ConfigureOidcMode()
    {
        lock (gate)
        {
            isDevelopment = false;
            authReady = false;
            token = sessionId = identityKey = currentUser = null;
            currentRoles = [];
            identityResolved = false;
        }
    }

    public void ApplyOidcSession(string sessionId, ActorContextDto context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(context);
        bool changed;
        lock (gate)
        {
            changed = isDevelopment || !authReady || !identityResolved
                || !string.Equals(this.sessionId, sessionId, StringComparison.Ordinal)
                || !string.Equals(currentUser, context.User, StringComparison.OrdinalIgnoreCase)
                || !SameRoles(currentRoles, context.Roles);
            isDevelopment = false;
            authReady = true;
            token = null;
            this.sessionId = sessionId;
            currentUser = context.User;
            currentRoles = context.Roles.ToArray();
            identityResolved = true;
            if (changed) identityKey = Guid.NewGuid().ToString("N");
        }
        if (changed) Changed?.Invoke();
    }

    public void InvalidateOidcSession()
    {
        bool changed;
        lock (gate)
        {
            changed = isDevelopment || !authReady || sessionId is not null || identityResolved;
            isDevelopment = false;
            authReady = true;
            token = sessionId = identityKey = currentUser = null;
            currentRoles = [];
            identityResolved = false;
        }
        if (changed) Changed?.Invoke();
    }

    public void CompleteOidcInitialization()
    {
        lock (gate)
        {
            if (authReady) return;
            authReady = true;
        }
        Changed?.Invoke();
    }

    public void Set(string? newToken)
    {
        lock (gate)
        {
            if (!isDevelopment) throw new InvalidOperationException("Development tokens are unavailable in OpenID Connect mode.");
            token = string.IsNullOrWhiteSpace(newToken) ? null : newToken.Trim();
            identityKey = token is null ? null : Guid.NewGuid().ToString("N");
            currentUser = null;
            currentRoles = [];
            identityResolved = false;
        }
        Changed?.Invoke();
    }

    public void Clear() => Set(null);

    public void ApplyResolvedContext(ActorContextDto context)
    {
        lock (gate)
        {
            if (!isDevelopment) throw new InvalidOperationException("OIDC actor context must be bound to its authenticated session.");
            currentUser = context.User;
            currentRoles = context.Roles.ToArray();
            identityResolved = true;
        }
        Changed?.Invoke();
    }

    public void ClearResolvedContext()
    {
        lock (gate)
        {
            currentUser = null;
            currentRoles = [];
            identityResolved = false;
        }
        Changed?.Invoke();
    }

    private static bool SameRoles(IReadOnlyList<string> first, IReadOnlyList<string> second) =>
        first.Count == second.Count && new HashSet<string>(first, StringComparer.OrdinalIgnoreCase).SetEquals(second);
}
