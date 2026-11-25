using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// The signal catalog's storage (EVENT-04F). Every write takes the exclusive advisory lock of
/// its name first, then the entry row; definition saves, publications, reactivation, and API
/// throws take the shared lock of each name they use.
/// </summary>
public interface ISignalCatalogRepository
{
    /// <summary>A page of entries ordered by name; <paramref name="namePrefix"/> matches the start of the name.</summary>
    Task<PagedResult<SignalCatalogEntryRecord>> ListAsync(
        string? namePrefix,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<SignalCatalogEntryRecord?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>The entries of the names that have one, by exact name. One statement.</summary>
    Task<IReadOnlyDictionary<string, SignalCatalogEntryRecord>> GetManyAsync(
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates an entry under the exclusive name lock, refused with a conflict when the name is
    /// taken or a live version that uses the name would not fit it.
    /// </summary>
    Task<SignalCatalogEntryRecord> CreateAsync(
        string name,
        SignalCatalogEntryContent content,
        string? actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces an active entry's content, or returns null when the name has no entry. A stale
    /// timestamp, an archived entry, or a live version that would not fit is a conflict.
    /// </summary>
    Task<SignalCatalogEntryRecord?> UpdateAsync(
        string name,
        SignalCatalogEntryContent content,
        DateTimeOffset expectedUpdatedAt,
        string? actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Archives or reactivates an entry, or returns null when the name has no entry. Archiving is
    /// refused while anything uses the name; reactivating while a live version would not fit.
    /// Archiving an archived entry, or reactivating an active one, returns it unchanged.
    /// </summary>
    Task<SignalCatalogEntryRecord?> ChangeStatusAsync(
        string name,
        bool archive,
        DateTimeOffset expectedUpdatedAt,
        string? actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Takes the transaction-scoped advisory lock of each name, in ordinal order, inside the
    /// ambient transaction: shared for saves, publications, reactivation, and throws, exclusive
    /// for catalog writes.
    /// </summary>
    Task LockNamesAsync(IReadOnlyCollection<string> names, bool exclusive, CancellationToken cancellationToken);

    /// <summary>
    /// The problems of the live versions that use the candidate's name against it, without a
    /// lock, at most <paramref name="limit"/>, and the number of those versions.
    /// </summary>
    Task<(IReadOnlyList<SignalCatalogProblemDto> Problems, bool Truncated, int LiveVersionCount)> PreviewAsync(
        SignalCatalogEntryRecord candidate,
        int limit,
        CancellationToken cancellationToken);

    Task<SignalCatalogUsageCounts> GetUsageCountsAsync(string name, CancellationToken cancellationToken);

    Task<PagedResult<SignalCatalogVersionUseRecord>> ListVersionUsesAsync(
        string name,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Names that live versions use without an entry, at most <paramref name="limit"/>, by name.</summary>
    Task<(IReadOnlyList<UncatalogedSignalNameRecord> Items, bool Truncated)> ListUncatalogedNamesAsync(
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>Who reads or changes the catalog, for <c>canThrow</c> and audit.</summary>
/// <param name="IsClient">A managed API client rather than a JWT user.</param>
/// <param name="Id">The user's actor identity or the client's ID.</param>
/// <param name="Roles">A user's roles; empty for a client.</param>
/// <param name="HasThrowScope">Whether a client holds <c>signals.throw</c>.</param>
public sealed record SignalCatalogCaller(
    bool IsClient,
    string? Id,
    IReadOnlyList<string> Roles,
    bool HasThrowScope);

/// <summary>The signal catalog's routes (EVENT-04F).</summary>
public interface ISignalCatalogService
{
    Task<SignalCatalogListDto> ListAsync(
        SignalCatalogCaller caller,
        string? name,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<SignalCatalogEntryDto?> GetAsync(SignalCatalogCaller caller, string name, CancellationToken cancellationToken);

    Task<SignalCatalogEntryDto> CreateAsync(
        SignalCatalogCaller caller,
        CreateSignalCatalogEntryRequest request,
        CancellationToken cancellationToken);

    Task<SignalCatalogEntryDto?> UpdateAsync(
        SignalCatalogCaller caller,
        string name,
        UpdateSignalCatalogEntryRequest request,
        CancellationToken cancellationToken);

    Task<SignalCatalogEntryDto?> ArchiveAsync(
        SignalCatalogCaller caller,
        string name,
        SignalCatalogLifecycleRequest request,
        CancellationToken cancellationToken);

    Task<SignalCatalogEntryDto?> ReactivateAsync(
        SignalCatalogCaller caller,
        string name,
        SignalCatalogLifecycleRequest request,
        CancellationToken cancellationToken);

    Task<SignalCatalogUsageDto?> GetUsageAsync(string name, int page, int pageSize, CancellationToken cancellationToken);

    Task<SignalCatalogImpactDto> PreviewAsync(CreateSignalCatalogEntryRequest request, CancellationToken cancellationToken);

    Task<UncatalogedSignalNameListDto> ListUncatalogedNamesAsync(CancellationToken cancellationToken);
}
