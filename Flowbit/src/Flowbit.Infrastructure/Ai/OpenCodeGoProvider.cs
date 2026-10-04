using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Service.Ai;
using Flowbit.Shared.Dtos;

namespace Flowbit.Infrastructure.Ai;

/// <summary>One bounded OpenCode chat-completion attempt, using Zen by default. The authoring runner owns recovery and retry budgets.</summary>
public sealed class OpenCodeGoProvider(HttpClient httpClient, WorkflowAiOptions options) : IAiWorkflowProvider
{
    private const int MaxErrorBytes = 65_536;

    // Retain the original provider id so existing API callers and continuation requests remain compatible.
    private string ProviderName => Uri.TryCreate(options.OpenCodeBaseUrl, UriKind.Absolute, out var endpoint)
        && endpoint.AbsolutePath.TrimEnd('/').Equals("/zen/go/v1", StringComparison.OrdinalIgnoreCase)
            ? "OpenCode Go" : "OpenCode Zen";

    public AiProviderDto Descriptor => new("opencode-go", ProviderName, options.OpenCodeModels.FirstOrDefault() ?? "kimi-k2.7-code",
        options.OpenCodeModels.Distinct(StringComparer.Ordinal).Select(id => new AiModelDto(id, id)).ToArray());

    public bool SupportsExecution(string variant) => variant is "current" or "optimized";

    public Task<AiCompletion> CompleteAsync(string modelId, string conversationId,
        IReadOnlyList<AiChatMessageDto> messages, string apiKey, int maxOutputTokens, CancellationToken cancellationToken)
        => CompleteCoreAsync(modelId, conversationId, messages, apiKey, maxOutputTokens,
            new("current", options.OpenCodeReasoningEfforts.GetValueOrDefault(modelId)), cancellationToken);

    public Task<AiCompletion> CompleteAsync(string modelId, string conversationId,
        IReadOnlyList<AiChatMessageDto> messages, string apiKey, int maxOutputTokens, AiExecutionSettings execution, CancellationToken cancellationToken)
        => CompleteCoreAsync(modelId, conversationId, messages, apiKey, maxOutputTokens, execution, cancellationToken);

    private async Task<AiCompletion> CompleteCoreAsync(string modelId, string conversationId,
        IReadOnlyList<AiChatMessageDto> messages, string apiKey, int maxOutputTokens, AiExecutionSettings execution,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = options.GetModelProfile(modelId);
        if (!SupportsExecution(execution.Variant))
            throw new WorkflowAiException("execution_unavailable", "Select current or optimized AI authoring execution.", 503);
        if (!options.OpenCodeModels.Contains(modelId, StringComparer.Ordinal))
            throw new WorkflowAiException("unsupported_model", "Select an enabled OpenCode chat model.");
        if (maxOutputTokens < 1 || maxOutputTokens > profile.MaxOutputTokens)
            throw new WorkflowAiException("provider_configuration", "The requested AI output budget exceeds the configured model limits.", 503);
        var reasoningEffort = execution.ReasoningEffort;
        var hasReasoningEffort = reasoningEffort is not null;
        if (hasReasoningEffort && reasoningEffort is not ("low" or "high" or "max"))
            throw new WorkflowAiException("provider_configuration", "The configured model reasoning effort must be low, high, or max.", 503);
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192 || apiKey.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > 256 || conversationId.Any(char.IsControl))
            throw new WorkflowAiException("invalid_request", "A valid provider key and conversation identifier are required.");
        if (string.IsNullOrWhiteSpace(options.OpenCodeBaseUrl)
            || !Uri.TryCreate(options.OpenCodeBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || !(baseUri.Scheme == Uri.UriSchemeHttps || (baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback))
            || !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new WorkflowAiException("provider_configuration", "The configured provider endpoint must use HTTPS or local loopback HTTP.", 503);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("Flowbit/1.0");
        request.Headers.Add("x-opencode-session", conversationId);
        var payload = new Dictionary<string, object>
        {
            ["model"] = modelId,
            ["messages"] = messages.Select(message => new { role = message.Role, content = message.Content }),
            ["max_tokens"] = maxOutputTokens,
            ["stream"] = false
        };
        if (hasReasoningEffort) payload["reasoning_effort"] = reasoningEffort!;
        request.Content = JsonContent.Create(payload);

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.PaymentRequired)
                    throw ClassifyFailure(response, []);
                // Error bodies are inspected only for known classifications, never included in diagnostics or logs.
                var errorBytes = await ReadBoundedAsync(response.Content, MaxErrorBytes, rejectOversize: false, timeout.Token);
                throw ClassifyFailure(response, errorBytes);
            }
            var bytes = await ReadBoundedAsync(response.Content, options.MaxOutputBytes, rejectOversize: true, timeout.Token);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw InvalidResponse(retryable: true);
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object) throw InvalidResponse();
            var finishReason = "stop";
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)
            {
                if (finish.ValueKind != JsonValueKind.String) throw InvalidResponse();
                finishReason = finish.GetString()!;
            }
            if (finishReason is "content_filter" or "refusal") throw Refusal();
            if (finishReason is not ("stop" or "length")) throw InvalidResponse();
            if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                throw InvalidResponse();
            if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && (refusal.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(refusal.GetString())))
                throw Refusal();
            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind != JsonValueKind.Null
                && (toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() > 0))
                throw InvalidResponse();
            var hasContent = message.TryGetProperty("content", out var content);
            if ((!hasContent && finishReason != "length") || (hasContent && content.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                throw InvalidResponse();
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : "";
            // Reasoning models may exhaust their output allowance before producing visible content.
            if (finishReason != "length" && string.IsNullOrWhiteSpace(text)) throw InvalidResponse(retryable: true);
            int? inputTokens = null, outputTokens = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = ReadTokens(usage, "prompt_tokens");
                outputTokens = ReadTokens(usage, "completion_tokens");
            }
            return new(text, finishReason, inputTokens, outputTokens, ReadRequestId(response, apiKey));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkflowAiException("provider_unavailable", "The AI provider request timed out. Your editor has not changed.", 504) { Retryable = true };
        }
        catch (HttpRequestException)
        {
            throw new WorkflowAiException("provider_unavailable", "Unable to connect to OpenCode. Your editor has not changed.", 502) { Retryable = true };
        }
        catch (IOException)
        {
            throw new WorkflowAiException("provider_unavailable", "The AI provider connection was interrupted. Your editor has not changed.", 502) { Retryable = true };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw InvalidResponse(retryable: true);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, bool rejectOversize, CancellationToken cancellationToken)
    {
        if (rejectOversize && content.Headers.ContentLength > limit) throw OutputTooLarge();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(8192, limit + 1)];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - (int)output.Length + 1)), cancellationToken);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit)
            {
                if (rejectOversize) throw OutputTooLarge();
                // A truncated provider error is not safe to parse. Status classification remains available.
                return [];
            }
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private static WorkflowAiException ClassifyFailure(HttpResponseMessage response, byte[] bytes)
    {
        var (code, type, message) = ReadError(bytes);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new("provider_auth", "OpenCode rejected the key or account access. Check your key and account.", 400);
        if (response.StatusCode == HttpStatusCode.PaymentRequired
            || IsOneOf(code, type, "insufficient_quota", "quota_exceeded", "billing_hard_limit_reached", "credit_balance_too_low", "insufficient_credits")
            || ContainsAny(message, "insufficient credits", "insufficient balance", "credit balance is too low", "quota exceeded", "billing limit"))
            return new("provider_quota", "OpenCode account quota or credit is exhausted. Check your provider account before continuing.", 429);
        if (IsOneOf(code, type, "context_length_exceeded", "context_window_exceeded", "prompt_too_long", "input_too_long")
            || ContainsAny(message, "maximum context length", "context window", "context length exceeded", "too many tokens", "prompt is too long", "input is too long"))
            return new("provider_context", "The request exceeds the selected model's context window.", 413);
        if (IsOneOf(code, type, "content_filter", "content_policy_violation", "safety_violation", "refusal")) return Refusal();
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return new("provider_throttled", "OpenCode is temporarily rate limited.", 429) { Retryable = true, RetryAfter = ReadRetryAfter(response) };
        if (response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
            return new("provider_unavailable", "OpenCode is temporarily unavailable.", 502) { Retryable = true, RetryAfter = ReadRetryAfter(response) };
        return new("provider_invalid_response", "OpenCode rejected this request. Check the selected model and provider configuration.", 502);
    }

    private static (string Code, string Type, string Message) ReadError(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("", "", "");
            var error = root.TryGetProperty("error", out var inner) ? inner : root;
            if (error.ValueKind != JsonValueKind.Object) return ("", "", "");
            return (StringValue(error, "code"), StringValue(error, "type"), StringValue(error, "message"));
        }
        catch (JsonException) { return ("", "", ""); }
    }

    private static string StringValue(JsonElement value, string property) =>
        value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
    private static bool IsOneOf(string code, string type, params string[] values) =>
        values.Any(value => string.Equals(value, code, StringComparison.OrdinalIgnoreCase) || string.Equals(value, type, StringComparison.OrdinalIgnoreCase));
    private static bool ContainsAny(string message, params string[] values) => values.Any(value => message.Contains(value, StringComparison.OrdinalIgnoreCase));
    private static int? ReadTokens(JsonElement usage, string property) =>
        usage.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var tokens) && tokens >= 0 ? tokens : null;

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values)) return null;
        var text = values.FirstOrDefault();
        if (text is null || text.Length > 128 || !RetryConditionHeaderValue.TryParse(text, out var parsed)) return null;
        var delay = parsed.Delta ?? (parsed.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
        // Keep hints at least as large as any permitted run, so oversized waits pause instead of retrying early.
        return delay is { } value ? TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 0, AiAuthoringLimits.MaxRunTimeoutSeconds)) : null;
    }

    private static string? ReadRequestId(HttpResponseMessage response, string apiKey)
    {
        foreach (var header in new[] { "x-request-id", "request-id" })
        {
            if (!response.Headers.TryGetValues(header, out var values)) continue;
            var id = values.FirstOrDefault();
            if (id is { Length: > 0 and <= 128 } && !id.Contains(apiKey, StringComparison.Ordinal)
                && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')) return id;
        }
        return null;
    }

    private static WorkflowAiException InvalidResponse(bool retryable = false) =>
        new("provider_invalid_response", "The provider returned an unreadable authoring response.", 502) { Retryable = retryable };
    private static WorkflowAiException OutputTooLarge() => new("provider_output_too_large", "The provider response exceeded the authoring size limit.", 502);
    private static WorkflowAiException Refusal() => new("provider_refusal", "The selected model declined this request. Review the requirements before trying again.", 400);
}

