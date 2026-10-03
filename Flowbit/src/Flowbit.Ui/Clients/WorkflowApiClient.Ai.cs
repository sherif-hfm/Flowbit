using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Clients;

public sealed partial class WorkflowApiClient
{
    public const string AiClientName = "Flowbit.AiAuthoring";
    public static TimeSpan AiRequestTimeout => TimeSpan.FromSeconds(AiAuthoringLimits.MaxRunTimeoutSeconds + 60);
    public async Task<IReadOnlyList<AiProviderDto>> GetAiProvidersAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/workflows/ai/providers", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<AiProviderDto>>(cancellationToken) ?? [];
    }

    public async Task<AiTurnResultDto> SendAiTurnAsync(AiTurnRequestDto request, string apiKey, CancellationToken cancellationToken = default)
    {
        using var authoringClient = clientFactory?.CreateClient(AiClientName);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/workflows/ai/turn");
        message.Headers.Add("X-Flowbit-AI-Key", apiKey);
        message.Content = JsonContent.Create(request);
        using var response = await (authoringClient ?? httpClient).SendAsync(message, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<AiTurnResultDto>(cancellationToken)
            ?? throw new InvalidOperationException("The AI service returned an empty response.");
    }

    public async Task<AiTurnResultDto> SendAiTurnStreamingAsync(AiTurnRequestDto request, string apiKey,
        Func<AiRunEventDto, Task> onEvent, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AiRequestTimeout);
        var ct = timeout.Token;
        using var authoringClient = clientFactory?.CreateClient(AiClientName);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/workflows/ai/turn/stream");
        message.Headers.Add("X-Flowbit-AI-Key", apiKey);
        message.Content = JsonContent.Create(request);
        using var response = await (authoringClient ?? httpClient).SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        if (response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
            throw new InvalidOperationException("The AI service returned an unsupported stream format.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var frameBytes = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 64 };
        string? runId = null;
        long sequence = 0;
        var frameCount = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                Append(start, i - start);
                if (++frameCount > 256) throw new InvalidOperationException("The AI stream exceeded its event limit.");
                AiRunEventDto frame;
                try
                {
                    frame = JsonSerializer.Deserialize<AiRunEventDto>(frameBytes.GetBuffer().AsSpan(0, checked((int)frameBytes.Length)), jsonOptions)
                        ?? throw new JsonException();
                }
                catch (JsonException) { throw new InvalidOperationException("The AI stream contains an invalid event."); }
                frameBytes.SetLength(0);
                start = i + 1;
                runId ??= frame.RunId;
                if (frame.Version != 1 || string.IsNullOrWhiteSpace(frame.RunId) || frame.RunId.Length > 128 ||
                    frame.RunId != runId || frame.Sequence <= sequence ||
                    frame.Type is not ("progress" or "checkpoint" or "result" or "paused" or "error") ||
                    frame.Type == "checkpoint" && frame.Checkpoint is null ||
                    frame.Type is "result" or "paused" && frame.Result is null)
                    throw new InvalidOperationException("The AI stream contains an invalid event sequence.");
                sequence = frame.Sequence;
                ct.ThrowIfCancellationRequested();
                await onEvent(frame);
                ct.ThrowIfCancellationRequested();
                if (frame.Type == "error")
                    throw new WorkflowApiException(System.Net.HttpStatusCode.BadGateway, frame.Message ?? "AI authoring was interrupted.");
                if (frame.Type is "result" or "paused") return frame.Result!;
            }
            Append(start, count - start);
        }
        throw new InvalidOperationException("The AI connection ended before a final result. Continue from the last received checkpoint.");

        void Append(int offset, int length)
        {
            if (frameBytes.Length + length > 4 * 1024 * 1024)
                throw new InvalidOperationException("The AI stream event exceeds its size limit.");
            frameBytes.Write(buffer, offset, length);
        }
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
