using System.Net.Http.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Clients;

/// <summary>Signal broadcasts: throw, follow deliveries, cancel, and retry.</summary>
public sealed partial class WorkflowApiClient
{
    public async Task<SignalBroadcastDetailDto> ThrowSignalAsync(
        ThrowSignalRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/signal-broadcasts", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty signal broadcast.");
    }

    public async Task<PagedResult<SignalBroadcastSummaryDto>> GetSignalBroadcastsAsync(
        string? name = null,
        string? workflowKey = null,
        string? status = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"page={Math.Max(1, page)}",
            $"pageSize={Math.Clamp(pageSize, 1, 200)}"
        };
        AddQueryValue(parameters, "name", name);
        AddQueryValue(parameters, "workflowKey", workflowKey);
        AddQueryValue(parameters, "status", status);
        using var response = await httpClient.GetAsync(
            $"/api/signal-broadcasts?{string.Join("&", parameters)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<SignalBroadcastSummaryDto>>(cancellationToken)
            ?? new PagedResult<SignalBroadcastSummaryDto>([], page, pageSize, 0);
    }

    public async Task<SignalBroadcastDetailDto?> GetSignalBroadcastAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"/api/signal-broadcasts/{id}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(cancellationToken);
    }

    public async Task<PagedResult<SignalDeliveryDto>> GetSignalDeliveriesAsync(
        long broadcastId,
        string? status = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"page={Math.Max(1, page)}",
            $"pageSize={Math.Clamp(pageSize, 1, 200)}"
        };
        AddQueryValue(parameters, "status", status);
        using var response = await httpClient.GetAsync(
            $"/api/signal-broadcasts/{broadcastId}/deliveries?{string.Join("&", parameters)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<SignalDeliveryDto>>(cancellationToken)
            ?? new PagedResult<SignalDeliveryDto>([], page, pageSize, 0);
    }

    public async Task<SignalBroadcastDetailDto> CancelSignalBroadcastAsync(
        long id,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/signal-broadcasts/{id}/cancel",
            new CancelSignalBroadcastRequest(reason),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty signal broadcast.");
    }

    public async Task<SignalBroadcastDetailDto> RetrySignalDeliveriesAsync(
        long id,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/signal-broadcasts/{id}/retry-failed",
            new RetrySignalDeliveriesRequest(expectedUpdatedAt),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty signal broadcast.");
    }

    public async Task<SignalSubscriptionSummaryDto> GetSignalSubscriptionSummaryAsync(
        string? name = null,
        string? workflowKey = null,
        string? businessKey = null,
        long? instanceId = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>();
        AddQueryValue(parameters, "name", name);
        AddQueryValue(parameters, "workflowKey", workflowKey);
        AddQueryValue(parameters, "businessKey", businessKey);
        if (instanceId is long id)
        {
            parameters.Add(FormattableString.Invariant($"instanceId={id}"));
        }
        var query = parameters.Count == 0 ? string.Empty : "?" + string.Join("&", parameters);
        using var response = await httpClient.GetAsync($"/api/signal-subscriptions/summary{query}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SignalSubscriptionSummaryDto>(cancellationToken)
            ?? new SignalSubscriptionSummaryDto([], false);
    }
}
