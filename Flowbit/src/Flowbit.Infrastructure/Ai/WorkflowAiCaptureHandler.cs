using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Flowbit.Service.Ai;

namespace Flowbit.Infrastructure.Ai;

/// <summary>
/// Request accounting for one agent run, ported from Flowbit AI Lab v3 (Runs/CaptureHandler.cs) without evidence files: refuses a
/// request beyond the cap before sending it, records usage, and stops the run on a provider error, an unreadable response or a
/// response truncated at the output limit. It never retries and never exposes a provider response body.
/// </summary>
internal sealed class WorkflowAiCaptureHandler(WorkflowAiRunMeter meter, string conversationId) : DelegatingHandler
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxErrorBytes = 65_536;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        meter.Reserve();
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Flowbit", "1.0"));
        request.Headers.Remove("x-opencode-session");
        request.Headers.TryAddWithoutValidation("x-opencode-session", conversationId);
        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            if ((int)response.StatusCode >= 400)
            {
                var error = await ReadBoundedAsync(response.Content, MaxErrorBytes, cancellationToken);
                throw Classify(response, error);
            }
            // Buffering lets the OpenAI SDK read the same content afterwards.
            byte[] body;
            try { body = await ReadBoundedAsync(response.Content, MaxResponseBytes, cancellationToken, truncate: false); }
            catch (InvalidDataException) { throw new WorkflowAiStopException("provider_invalid_response", "The provider response exceeded the authoring size limit."); }
            Inspect(body);
            var buffered = new ByteArrayContent(body);
            foreach (var header in response.Content.Headers) buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = buffered;
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private void Inspect(byte[] body)
    {
        string? finish = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            long? input = null, cached = null, output = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = Count(usage, "prompt_tokens");
                output = Count(usage, "completion_tokens");
                if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                    cached = Count(details, "cached_tokens");
            }
            meter.Record(input, cached, output);
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object && choices[0].TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                finish = reason.GetString();
        }
        catch (JsonException)
        {
            throw new WorkflowAiStopException("provider_invalid_response", "The provider returned a response that is not JSON.");
        }
        if (finish == "length")
            throw new WorkflowAiStopException("provider_truncated",
                "The model reached its output limit before finishing a response. Continue to resume from the last completed step.");
        if (finish is "content_filter" or "refusal")
            throw new WorkflowAiStopException("provider_refusal", "The selected model declined this request. Review the requirements before trying again.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellationToken, bool truncate = true)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                if (!truncate) throw new InvalidDataException();
                buffer.Write(chunk, 0, (int)(limit - buffer.Length));
                break;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // Flowbit's provider error classification (formerly OpenCodeGoProvider.ClassifyFailure). The response body is read only for
    // its error code; it never reaches the caller.
    private static WorkflowAiStopException Classify(HttpResponseMessage response, byte[] bytes)
    {
        var (code, type, message) = ReadError(bytes);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new("provider_auth", "OpenCode rejected the key or account access. Check your key and account, then Continue.");
        if (response.StatusCode == HttpStatusCode.PaymentRequired
            || IsOneOf(code, type, "insufficient_quota", "quota_exceeded", "billing_hard_limit_reached", "credit_balance_too_low", "insufficient_credits")
            || ContainsAny(message, "insufficient credits", "insufficient balance", "credit balance is too low", "quota exceeded", "billing limit"))
            return new("provider_quota", "OpenCode account quota or credit is exhausted. Check your provider account before continuing.");
        if (IsOneOf(code, type, "context_length_exceeded", "context_window_exceeded", "prompt_too_long", "input_too_long")
            || ContainsAny(message, "maximum context length", "context window", "context length exceeded", "too many tokens", "prompt is too long", "input is too long"))
            return new("provider_context", "The conversation exceeds the selected model's context window. Start a new conversation.");
        if (IsOneOf(code, type, "content_filter", "content_policy_violation", "safety_violation", "refusal"))
            return new("provider_refusal", "The selected model declined this request. Review the requirements before trying again.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return new("provider_throttled", "OpenCode is temporarily rate limited. Continue when the limit has passed.");
        if (response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
            return new("provider_unavailable", "OpenCode is temporarily unavailable. Continue to try again.");
        return new("provider_invalid_response", "OpenCode rejected this request. Check the selected model and provider configuration.");
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
    private static long? Count(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : null;
}
