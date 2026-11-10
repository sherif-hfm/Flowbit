using System.Net.Http.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Clients;

public sealed partial class WorkflowApiClient
{
    public async Task<InstanceDiagramHistoryDto> GetInstanceDiagramHistoryAsync(
        long id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"/api/instances/{id}/diagram-history", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<InstanceDiagramHistoryDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty diagram history response.");
    }
}
