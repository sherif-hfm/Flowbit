using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowbit.Service.Ai;
using Flowbit.Shared.Dtos;

namespace Flowbit.Infrastructure.Ai;

/// <summary>OpenCode Go's documented chat-completions protocol. Other protocols belong in separate adapters.</summary>
public sealed class OpenCodeGoProvider(HttpClient httpClient, WorkflowAiOptions options) : IAiWorkflowProvider
{
    public AiProviderDto Descriptor => new("opencode-go", "OpenCode Go", options.OpenCodeModels.FirstOrDefault() ?? "kimi-k2.7-code",
        options.OpenCodeModels.Select(id => new AiModelDto(id, id)).ToArray());

    public async Task<string> CompleteAsync(string modelId, string conversationId,
        IReadOnlyList<AiChatMessageDto> messages, string apiKey, CancellationToken cancellationToken)
    {
        if (!options.OpenCodeModels.Contains(modelId, StringComparer.Ordinal))
            throw new WorkflowAiException("unsupported_model", "Select an enabled OpenCode Go chat model.");
        var hasReasoningEffort = options.OpenCodeReasoningEfforts.TryGetValue(modelId, out var reasoningEffort);
        if (hasReasoningEffort && reasoningEffort is not ("low" or "high" or "max"))
            throw new WorkflowAiException("provider_configuration", "The configured model reasoning effort must be low, high, or max.", 503);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 10, 600)));
        if (!Uri.TryCreate(options.OpenCodeBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || !(baseUri.Scheme == Uri.UriSchemeHttps || (baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback))
            || !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new WorkflowAiException("provider_configuration", "The configured provider endpoint must use HTTPS or local loopback HTTP.", 503);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("Flowbit/1.0");
        request.Headers.Add("x-opencode-session", conversationId);
        var payload = new Dictionary<string, object>
        {
            ["model"] = modelId,
            ["messages"] = messages.Select(message => new { role = message.Role, content = message.Content }),
            ["max_tokens"] = 32_768,
            ["stream"] = false
        };
        if (hasReasoningEffort) payload["reasoning_effort"] = reasoningEffort!;
        request.Content = JsonContent.Create(payload);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new WorkflowAiException("provider_auth", "OpenCode Go rejected the key or account access. Check your key and subscription.", 400),
                    HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired => new WorkflowAiException("provider_limit", "OpenCode Go usage or rate limit reached. Check your provider account before retrying.", 429),
                    _ => new WorkflowAiException("provider_unavailable", "OpenCode Go could not complete this request. Check the selected model and try again later.", 502)
                };
            var limit = Math.Clamp(options.MaxOutputBytes, 1024, 8_388_608);
            if (response.Content.Headers.ContentLength > limit)
                throw new WorkflowAiException("provider_output_too_large", "The provider response exceeded the authoring size limit.", 502);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + count > limit)
                    throw new WorkflowAiException("provider_output_too_large", "The provider response exceeded the authoring size limit.", 502);
                await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            }
            using var document = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 128 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new WorkflowAiException("provider_invalid_response", "The provider returned no authoring response.", 502);
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object)
                throw new WorkflowAiException("provider_invalid_response", "The provider returned an unreadable response.", 502);
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() == "length")
                throw new WorkflowAiException("provider_truncated", "The generated response was truncated. Ask for a smaller workflow or choose another model.", 502);
            if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                throw new WorkflowAiException("provider_invalid_response", "The provider returned an unreadable response.", 502);
            var text = content.GetString();
            if (string.IsNullOrWhiteSpace(text))
                throw new WorkflowAiException("provider_invalid_response", "The provider returned no authoring response.", 502);
            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkflowAiException("provider_timeout", "The AI request timed out. Your editor has not changed.", 504);
        }
        catch (HttpRequestException)
        {
            throw new WorkflowAiException("provider_unavailable", "Unable to connect to OpenCode Go. Your editor has not changed.", 502);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new WorkflowAiException("provider_invalid_response", "The provider returned an unreadable response.", 502);
        }
    }
}

