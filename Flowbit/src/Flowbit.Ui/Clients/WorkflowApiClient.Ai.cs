using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Clients;

public sealed partial class WorkflowApiClient
{
    public async Task<IReadOnlyList<AiProviderDto>> GetAiProvidersAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/workflows/ai/providers", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<AiProviderDto>>(cancellationToken) ?? [];
    }

    public async Task<AiTurnResultDto> SendAiTurnAsync(AiTurnRequestDto request, string apiKey, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/workflows/ai/turn");
        message.Headers.Add("X-Flowbit-AI-Key", apiKey);
        message.Content = JsonContent.Create(request);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AiTurnResultDto>(cancellationToken)
            ?? throw new InvalidOperationException("The AI service returned an empty response.");
    }

    public async Task<AiDocumentExtractionDto> ExtractAiDocumentAsync(Stream stream, string fileName, bool forceOcr, string languages,
        CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", fileName);
        content.Add(new StringContent(forceOcr ? "true" : "false"), "forceOcr");
        content.Add(new StringContent(languages), "languages");
        using var response = await httpClient.PostAsync("/api/workflows/ai/extract", content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AiDocumentExtractionDto>(cancellationToken)
            ?? throw new InvalidOperationException("The document service returned an empty response.");
    }

    public async Task<AiValidationResultDto> ValidateAiWorkflowAsync(JsonElement definition, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/workflows/validate", definition, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AiValidationResultDto>(cancellationToken)
            ?? throw new InvalidOperationException("The validation service returned an empty response.");
    }

    public async Task<byte[]> DownloadAiSkillAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/workflows/ai/skill", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}
