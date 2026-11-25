using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Who may throw, cancel, or retry a broadcast of a cataloged name (EVENT-04F). An entry's
/// throw roles narrow <c>Signals.RequiredRole</c> for users, and <c>allowClients</c> decides
/// whether managed API clients with <c>signals.throw</c> may throw the name at all.
/// </summary>
public static class SignalCatalogAccess
{
    /// <summary>The <c>code</c> of a 403 that a catalog rule, not an authorization policy, returns.</summary>
    public const string ForbiddenCode = "signal_catalog_forbidden";

    public const string Throwing = "Throwing";
    public const string Cancelling = "Cancelling";
    public const string Retrying = "Retrying";

    /// <summary>
    /// Why the caller may not act on a broadcast of the entry's name, or null when it may.
    /// <paramref name="verb"/> is <see cref="Throwing"/>, <see cref="Cancelling"/>, or <see cref="Retrying"/>.
    /// </summary>
    public static string? Refusal(SignalCatalogEntryRecord entry, SignalCaller caller, string verb)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.IsClient)
        {
            return entry.AllowClients
                ? null
                : $"Signal '{entry.Name}' cannot be thrown by an API client; its catalog entry does not allow clients.";
        }

        return HoldsThrowRole(entry, caller.Roles)
            ? null
            : $"{verb} signal '{entry.Name}' needs one of the roles its catalog entry lists: {string.Join(", ", entry.ThrowRoles)}.";
    }

    /// <summary>
    /// Whether a reader of the catalog could throw the name now: an active entry, and for a user
    /// a role from <paramref name="signalRoles"/> plus, when the entry lists throw roles, one of
    /// them; for a client the <c>signals.throw</c> scope and <c>allowClients</c>.
    /// </summary>
    public static bool CanThrow(
        SignalCatalogEntryRecord entry,
        SignalCatalogCaller caller,
        IReadOnlyList<string> signalRoles)
    {
        if (!entry.IsActive)
        {
            return false;
        }

        if (caller.IsClient)
        {
            return caller.HasThrowScope && entry.AllowClients;
        }

        var roles = Normalize(caller.Roles);
        return signalRoles.Any(role => roles.Contains(role.Trim())) && HoldsThrowRole(entry, caller.Roles);
    }

    private static bool HoldsThrowRole(SignalCatalogEntryRecord entry, IReadOnlyList<string> callerRoles)
    {
        if (entry.ThrowRoles.Count == 0)
        {
            return true;
        }

        var roles = Normalize(callerRoles);
        return entry.ThrowRoles.Any(roles.Contains);
    }

    private static HashSet<string> Normalize(IEnumerable<string> roles) =>
        roles.Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
