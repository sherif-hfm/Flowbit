using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace Flowbit.Ui.Auth;

/// <summary>One login, shared by that browser's circuits; never keyed by username or provider subject.</summary>
public sealed class UiSessionRecord
{
    internal readonly object Gate = new();
    public SemaphoreSlim RefreshLock { get; } = new(1, 1);
    internal byte[]? ProtectedTicket;
    internal string ProtectedTokens = "";
    internal bool Revoked;
    internal long TokenVersion = 1;
    internal long ContextVersion = 1;
    public required string Id { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public ActorContextDto Actor { get; internal set; } = new(null, []);
}

public interface IUiSessionStore
{
    UiSessionRecord? Find(string? id);
    void Revoke(string? id);
    event Action<string>? Changed;
}

public sealed class UiSessionStore : IUiSessionStore, ITicketStore, IDisposable
{
    public const string SessionClaim = "flowbit_local_session";
    private readonly ConcurrentDictionary<string, UiSessionRecord> sessions = new(StringComparer.Ordinal);
    private readonly IDataProtector protector;
    private readonly TimeProvider clock;
    private readonly ITimer cleanup;
    public event Action<string>? Changed;

    public UiSessionStore(IDataProtectionProvider protection, TimeProvider clock)
    {
        protector = protection.CreateProtector("Flowbit.Ui.OidcSession.v1");
        this.clock = clock;
        cleanup = clock.CreateTimer(_ => PurgeExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public static string? SessionId(ClaimsPrincipal principal) => principal.FindFirstValue(SessionClaim);

    public string Create(ClaimsPrincipal principal, UserToken token, ActorContextDto actor, TimeSpan lifetime)
    {
        ValidateToken(token);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        // Never allow a provider claim to choose our local session identifier.
        foreach (var identity in principal.Identities)
            foreach (var claim in identity.FindAll(SessionClaim).ToArray()) identity.RemoveClaim(claim);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(SessionClaim, id));
        sessions[id] = new UiSessionRecord
        {
            Id = id, ExpiresAt = clock.GetUtcNow() + lifetime, Actor = actor,
            ProtectedTokens = Protect(token)
        };
        return id;
    }

    public UiSessionRecord? Find(string? id)
    {
        if (id is null || !sessions.TryGetValue(id, out var record)) return null;
        lock (record.Gate)
            if (!record.Revoked && record.ExpiresAt > clock.GetUtcNow()) return record;
        Revoke(id);
        return null;
    }

    public void Revoke(string? id)
    {
        if (id is null || !sessions.TryRemove(id, out var record)) return;
        lock (record.Gate)
        {
            record.Revoked = true;
            record.ProtectedTokens = "";
            record.ProtectedTicket = null;
        }
        Changed?.Invoke(id);
        UiAuthenticationDiagnostics.Record("session", "revoked");
    }

    public UserToken ReadToken(UiSessionRecord record)
    {
        lock (record.Gate)
        {
            EnsureActive(record);
            return JsonSerializer.Deserialize<StoredTokens>(protector.Unprotect(record.ProtectedTokens))!.ToToken();
        }
    }

    public void UpdateToken(string? id, UserToken token)
    {
        ValidateToken(token);
        var record = Find(id) ?? throw new UiSessionExpiredException();
        lock (record.Gate)
        {
            EnsureActive(record);
            var previous = ReadToken(record);
            // The original validated ID token is the logout hint, not a refreshed unvalidated identity.
            record.ProtectedTokens = Protect(token with
            {
                IdentityToken = previous.IdentityToken,
                RefreshToken = token.RefreshToken ?? previous.RefreshToken
            });
            record.TokenVersion++;
        }
    }

    public void UpdateActor(UiSessionRecord record, ActorContextDto actor)
    {
        bool identityChanged;
        lock (record.Gate)
        {
            EnsureActive(record);
            identityChanged = !string.Equals(record.Actor.User, actor.User, StringComparison.OrdinalIgnoreCase);
            if (!identityChanged)
            {
                record.Actor = actor;
                record.ContextVersion = record.TokenVersion;
            }
        }
        if (identityChanged)
        {
            Revoke(record.Id);
            throw new UiSessionExpiredException();
        }
        Changed?.Invoke(record.Id);
    }

    public bool NeedsActorRefresh(UiSessionRecord record)
    {
        lock (record.Gate) { EnsureActive(record); return record.TokenVersion != record.ContextVersion; }
    }

    private void EnsureActive(UiSessionRecord record)
    {
        if (record.Revoked || record.ExpiresAt <= clock.GetUtcNow()) throw new UiSessionExpiredException();
    }

    private void ValidateToken(UserToken token)
    {
        if (token.Expiration == DateTimeOffset.MaxValue || token.Expiration <= clock.GetUtcNow()
            || string.IsNullOrWhiteSpace(token.AccessToken.ToString())
            || !string.Equals(token.AccessTokenType?.ToString(), "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new UiSessionExpiredException();
    }

    private string Protect(UserToken token) => protector.Protect(JsonSerializer.Serialize(new StoredTokens(
        token.AccessToken.ToString(), token.RefreshToken?.ToString(), token.IdentityToken?.ToString(),
        token.ClientId.ToString(), token.Scope?.ToString(), token.Expiration)));

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var record = Find(SessionId(ticket.Principal)) ?? throw new UiSessionExpiredException();
        lock (record.Gate)
        {
            EnsureActive(record);
            ticket.Properties.ExpiresUtc = record.ExpiresAt;
            ticket.Properties.IsPersistent = false;
            record.ProtectedTicket = protector.Protect(TicketSerializer.Default.Serialize(ticket));
        }
        return Task.FromResult(record.Id);
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        if (key != SessionId(ticket.Principal) || Find(key) is null) return;
        try { await StoreAsync(ticket); } catch (UiSessionExpiredException) { }
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var record = Find(key);
        if (record is null) return Task.FromResult<AuthenticationTicket?>(null);
        lock (record.Gate)
        {
            if (record.Revoked || record.ProtectedTicket is null) return Task.FromResult<AuthenticationTicket?>(null);
            try { return Task.FromResult(TicketSerializer.Default.Deserialize(protector.Unprotect(record.ProtectedTicket))); }
            catch (CryptographicException) { return Task.FromResult<AuthenticationTicket?>(null); }
        }
    }

    public Task RemoveAsync(string key) { Revoke(key); return Task.CompletedTask; }
    private void PurgeExpired() { foreach (var id in sessions.Keys) _ = Find(id); }
    public void Dispose() { cleanup.Dispose(); foreach (var id in sessions.Keys) Revoke(id); }

    private sealed record StoredTokens(string Access, string? Refresh, string? Identity, string Client, string? Scope, DateTimeOffset Expiry)
    {
        public UserToken ToToken() => new()
        {
            AccessToken = AccessToken.Parse(Access), RefreshToken = Refresh is null ? null : RefreshToken.Parse(Refresh),
            IdentityToken = Identity is null ? null : IdentityToken.Parse(Identity), ClientId = ClientId.Parse(Client),
            Scope = Scope is null ? null : Duende.AccessTokenManagement.Scope.Parse(Scope),
            Expiration = Expiry, AccessTokenType = AccessTokenType.Parse("Bearer")
        };
    }
}

public sealed class UiSessionExpiredException() : InvalidOperationException("Your session has ended. Sign in again to continue.");

public sealed class FlowbitUserTokenStore(UiSessionStore sessions) : IUserTokenStore
{
    public Task StoreTokenAsync(ClaimsPrincipal user, UserToken token, UserTokenRequestParameters? parameters = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        sessions.UpdateToken(UiSessionStore.SessionId(user), token);
        return Task.CompletedTask;
    }

    public Task<TokenResult<TokenForParameters>> GetTokenAsync(ClaimsPrincipal user, UserTokenRequestParameters? parameters = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var record = sessions.Find(UiSessionStore.SessionId(user));
        if (record is null) return Task.FromResult<TokenResult<TokenForParameters>>(TokenResult.Failure("session_expired"));
        var token = sessions.ReadToken(record);
        return Task.FromResult<TokenResult<TokenForParameters>>(new TokenForParameters(token,
            token.RefreshToken is { } refresh ? new UserRefreshToken(refresh, null) : null));
    }

    public Task ClearTokenAsync(ClaimsPrincipal user, UserTokenRequestParameters? parameters = null, CancellationToken ct = default)
    {
        sessions.Revoke(UiSessionStore.SessionId(user));
        return Task.CompletedTask;
    }
}
