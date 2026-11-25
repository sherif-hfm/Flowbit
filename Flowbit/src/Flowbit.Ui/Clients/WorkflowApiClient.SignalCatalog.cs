using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Clients;

/// <summary>
/// The signal catalog (EVENT-04F): entries with their payload contracts and throw permissions.
/// Settings administrators change it; signal users, workflow administrators, and settings
/// administrators read it.
/// </summary>
public sealed partial class WorkflowApiClient
{
    public async Task<SignalCatalogListDto> GetSignalCatalogEntriesAsync(
        string? name = null,
        string? status = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={Math.Clamp(pageSize, 1, 200).ToString(CultureInfo.InvariantCulture)}"
        };
        AddQueryValue(parameters, "name", name);
        AddQueryValue(parameters, "status", status);
        using var response = await httpClient.GetAsync(
            $"/api/signal-catalog/entries?{string.Join("&", parameters)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SignalCatalogListDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty signal catalog.");
    }

    /// <summary>The entry of a name, or null when the catalog has none.</summary>
    public async Task<SignalCatalogEntryDto?> GetSignalCatalogEntryAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(EntryPath(name), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SignalCatalogEntryDto>(cancellationToken);
    }

    public async Task<SignalCatalogEntryDto> CreateSignalCatalogEntryAsync(
        CreateSignalCatalogEntryRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/signal-catalog/entries", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadEntryAsync(response, cancellationToken);
    }

    public async Task<SignalCatalogEntryDto> UpdateSignalCatalogEntryAsync(
        string name,
        UpdateSignalCatalogEntryRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync(EntryPath(name), request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadEntryAsync(response, cancellationToken);
    }

    public async Task<SignalCatalogEntryDto> ArchiveSignalCatalogEntryAsync(
        string name,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            EntryPath(name) + "/archive",
            new SignalCatalogLifecycleRequest(expectedUpdatedAt),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadEntryAsync(response, cancellationToken);
    }

    public async Task<SignalCatalogEntryDto> ReactivateSignalCatalogEntryAsync(
        string name,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            EntryPath(name) + "/reactivate",
            new SignalCatalogLifecycleRequest(expectedUpdatedAt),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadEntryAsync(response, cancellationToken);
    }

    public async Task<SignalCatalogUsageDto> GetSignalCatalogUsageAsync(
        string name,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            EntryPath(name)
            + $"/usage?page={Math.Max(1, page).ToString(CultureInfo.InvariantCulture)}"
            + $"&pageSize={Math.Clamp(pageSize, 1, 200).ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SignalCatalogUsageDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty usage.");
    }

    /// <summary>The problems a candidate entry would have with the live versions that use its name. Writes nothing.</summary>
    public async Task<SignalCatalogImpactDto> PreviewSignalCatalogEntryAsync(
        CreateSignalCatalogEntryRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/signal-catalog/preview", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SignalCatalogImpactDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty preview.");
    }

    public async Task<UncatalogedSignalNameListDto> GetUncatalogedSignalNamesAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/signal-catalog/uncataloged-names", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<UncatalogedSignalNameListDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty list of names.");
    }

    private static string EntryPath(string name) =>
        "/api/signal-catalog/entries/" + Uri.EscapeDataString(name);

    private static async Task<SignalCatalogEntryDto> ReadEntryAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<SignalCatalogEntryDto>(cancellationToken)
        ?? throw new InvalidOperationException("The API returned an empty signal catalog entry.");
}
