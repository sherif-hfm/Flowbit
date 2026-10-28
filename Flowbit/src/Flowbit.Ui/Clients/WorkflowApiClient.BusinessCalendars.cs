using System.Globalization;
using System.Net.Http.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Clients;

/// <summary>Settings administrators' business calendars, which timer durations and task due dates can count in.</summary>
public sealed partial class WorkflowApiClient
{
    public async Task<IReadOnlyList<BusinessCalendarDto>> GetBusinessCalendarsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/business-calendars", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<BusinessCalendarDto>>(cancellationToken) ?? [];
    }

    public async Task<BusinessCalendarDto> CreateBusinessCalendarAsync(
        CreateBusinessCalendarRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/business-calendars", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BusinessCalendarDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty business calendar.");
    }

    public async Task<BusinessCalendarDto> UpdateBusinessCalendarAsync(
        long id,
        UpdateBusinessCalendarRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync(
            $"/api/business-calendars/{id.ToString(CultureInfo.InvariantCulture)}",
            request,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BusinessCalendarDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty business calendar.");
    }

    public async Task DeleteBusinessCalendarAsync(
        long id,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        var expected = Uri.EscapeDataString(expectedUpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        using var response = await httpClient.DeleteAsync(
            $"/api/business-calendars/{id.ToString(CultureInfo.InvariantCulture)}?expectedUpdatedAt={expected}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<BusinessCalendarPreviewDto> PreviewBusinessCalendarAsync(
        BusinessCalendarPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/business-calendars/preview", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<BusinessCalendarPreviewDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty preview.");
    }

    public async Task<IReadOnlyList<string>> GetBusinessCalendarTimeZonesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/business-calendars/time-zones", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<string>>(cancellationToken) ?? [];
    }
}
