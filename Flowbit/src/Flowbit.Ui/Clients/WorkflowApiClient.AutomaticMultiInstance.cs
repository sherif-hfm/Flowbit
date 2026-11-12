using System.Net.Http.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Clients;

public sealed partial class WorkflowApiClient
{
    public async Task<PagedResult<AutomaticMultiInstanceItemSummaryDto>> GetAutomaticMultiInstanceItemsAsync(
        long executionId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/multi-instance-executions/{executionId}/items?page={page}&pageSize={pageSize}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<AutomaticMultiInstanceItemSummaryDto>>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty automatic item page.");
    }

    public async Task<AutomaticMultiInstanceItemDetailDto> GetAutomaticMultiInstanceItemAsync(
        long executionId, long itemId, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/multi-instance-executions/{executionId}/items/{itemId}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AutomaticMultiInstanceItemDetailDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty automatic item detail.");
    }
}
