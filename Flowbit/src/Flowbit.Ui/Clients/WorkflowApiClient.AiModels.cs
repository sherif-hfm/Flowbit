using System.Globalization;
using System.Net.Http.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Clients;

/// <summary>Settings administrators' AI models. Keys travel only in create and update requests and are never returned.</summary>
public sealed partial class WorkflowApiClient
{
    public async Task<IReadOnlyList<AiModelSettingDto>> GetAiModelSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/ai-models", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<AiModelSettingDto>>(cancellationToken) ?? [];
    }

    public async Task<AiModelSettingDto> CreateAiModelAsync(CreateAiModelRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/ai-models", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AiModelSettingDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty AI model.");
    }

    public async Task<AiModelSettingDto> UpdateAiModelAsync(string id, UpdateAiModelRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync($"/api/ai-models/{Uri.EscapeDataString(id)}", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AiModelSettingDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty AI model.");
    }

    public async Task DeleteAiModelAsync(string id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync(
            $"/api/ai-models/{Uri.EscapeDataString(id)}?expectedRevision={expectedRevision.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }
}
