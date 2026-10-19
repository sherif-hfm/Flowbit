using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.AI;

namespace Flowbit.Service.Ai;

/// <summary>
/// Everything a later Send or Continue needs: the provider binding, the Microsoft Agent Framework session, the messages a stopped
/// call had not stored, the agent's saved files (with credential placeholders only) and its returned reads.
/// </summary>
internal sealed record WorkflowAiStatePayload
{
    public int FormatVersion { get; init; } = 1;
    public string AgentId { get; init; } = "";
    public string ProviderId { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string? ReasoningEffort { get; init; }
    public string PromptFingerprint { get; init; } = "";
    public string DocsFingerprint { get; init; } = "";
    public string ConversationId { get; init; } = "";
    public string RedactionNonce { get; init; } = "";
    public JsonElement Session { get; init; }
    public IReadOnlyList<ChatMessage> Pending { get; init; } = [];
    public int HistoryMessages { get; init; }
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);
    public IReadOnlyList<string> ReturnedReads { get; init; } = [];
    /// <summary>The diagram the agent last saw, ignoring layout and the workflow key: the last proposal, or the diagram sent with the last turn.</summary>
    public string? ExpectedEditorHash { get; init; }
    /// <summary>The workflow file's hash when the interrupted turn started; null when there was no file.</summary>
    public string? TurnStartWorkflowHash { get; init; }
    public IReadOnlyList<string> SentSources { get; init; } = [];
    public string? SharedVariablesHash { get; init; }
}

/// <summary>Encodes conversation state for the panel and checks it when it comes back. The returned state is untrusted input.</summary>
internal static class WorkflowAiConversationState
{
    public const string Format = "flowbit-agent-state/1";
    public const int MaxDataCharacters = 6 * 1024 * 1024;
    public const int MaxPayloadBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    public static AiConversationStateDto Encode(WorkflowAiStatePayload payload, int? workflowNodes, int? workflowFlows)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gzip, payload, Options);
        var data = Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length);
        if (data.Length > MaxDataCharacters)
            throw new WorkflowAiException("state_too_large", "The conversation has grown beyond the size the assistant can keep. Start a new conversation.", 413);
        return new()
        {
            Format = Format, Data = data, CanContinue = payload.Pending.Count > 0,
            Messages = payload.HistoryMessages + payload.Pending.Count, WorkflowNodes = workflowNodes, WorkflowFlows = workflowFlows
        };
    }

    public static WorkflowAiStatePayload Decode(AiConversationStateDto state, string apiKey)
    {
        if (state.Format != Format || string.IsNullOrEmpty(state.Data) || state.Data.Length > MaxDataCharacters)
            throw Invalid();
        string json;
        try
        {
            using var compressed = new MemoryStream(Convert.FromBase64String(state.Data));
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = gzip.Read(chunk)) > 0)
            {
                if (output.Length + read > MaxPayloadBytes) throw Invalid();
                output.Write(chunk, 0, read);
            }
            json = Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (Exception error) when (error is FormatException or InvalidDataException) { throw Invalid(); }
        if (json.Contains(apiKey, StringComparison.Ordinal) || json.Contains(JsonEncodedText.Encode(apiKey).ToString(), StringComparison.Ordinal))
            throw Invalid();
        WorkflowAiStatePayload payload;
        try { payload = JsonSerializer.Deserialize<WorkflowAiStatePayload>(json, Options) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); }
        if (payload.FormatVersion != 1 || payload.Session.ValueKind != JsonValueKind.Object || payload.Pending is null || payload.Files is null
            || payload.ReturnedReads is null || payload.SentSources is null || payload.ReturnedReads.Count > 10_000 || payload.SentSources.Count > 1_000
            || payload.ReturnedReads.Any(key => key is null || key.Length > 2_000)
            || payload.Files.Keys.Any(path => path.Length is 0 or > 300 || path.Contains('\\') || path.Split('/').Any(segment => segment is "" or "." or ".."))
            || payload.Files.Values.Sum(text => (long)Encoding.UTF8.GetByteCount(text ?? "")) > WorkflowAiWorkspace.MaxOutputBytes)
            throw Invalid();
        if (!payload.Pending.All(message => message.Role == ChatRole.User && message.Contents.All(content => content is TextContent)
                || message.Role == ChatRole.Tool && message.Contents.All(content => content is FunctionResultContent)))
            throw Invalid();
        return payload;
    }

    /// <summary>The restored history may hold only user, assistant and tool messages with text, reasoning and function contents.</summary>
    public static void CheckHistory(IReadOnlyList<ChatMessage> history, IReadOnlyList<ChatMessage> pending, int expectedCount)
    {
        if (history.Count != expectedCount
            || history.Any(message => !(message.Role == ChatRole.User || message.Role == ChatRole.Assistant || message.Role == ChatRole.Tool))
            || history.SelectMany(message => message.Contents).Any(content => content is not (TextContent or TextReasoningContent or FunctionCallContent or FunctionResultContent))
            || !HasCompleteToolExchanges(history.Concat(pending)))
            throw Invalid();
    }

    // Copied from Flowbit AI Lab v3 (Runs/SessionStore.cs): every tool call is followed by its result before other messages.
    public static bool HasCompleteToolExchanges(IEnumerable<ChatMessage> messages)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (pending.Count > 0 && message.Role != ChatRole.Tool) return false;
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call && !pending.Add(call.CallId)) return false;
                if (content is FunctionResultContent result && !pending.Remove(result.CallId)) return false;
            }
        }
        return pending.Count == 0;
    }

    private static WorkflowAiException Invalid() =>
        new("invalid_state", "The assistant's conversation state is invalid or too large. Start a new conversation.", 400);
}
