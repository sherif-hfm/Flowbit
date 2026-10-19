using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Flowbit.Service.Ai;

// Copied from Flowbit AI Lab v3. The OpenAI adapter writes non-string tool results with AIJsonUtilities.DefaultOptions,
// which indent with Environment.NewLine. Send the same JSON compactly. Only the outgoing copy changes:
// history and the session keep the framework values.
public sealed class CompactToolResultsChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private static readonly JsonTypeInfo ResultInfo = CreateResultInfo();

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages.Select(CompactMessage).ToArray(), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages.Select(CompactMessage).ToArray(), options, cancellationToken);

    private static string Serialize(object? value) => JsonSerializer.Serialize(value, ResultInfo);

    private static JsonTypeInfo CreateResultInfo()
    {
        // Same encoder and resolver as the adapter, so only insignificant whitespace differs.
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
        options.MakeReadOnly();
        return options.GetTypeInfo(typeof(object));
    }

    private static ChatMessage CompactMessage(ChatMessage message)
    {
        if (!message.Contents.Any(IsStructuredResult)) return message;
        var copy = message.Clone();
        copy.Contents = message.Contents.Select(content => content is FunctionResultContent result && IsStructuredResult(result)
            ? new FunctionResultContent(result.CallId, Serialize(result.Result))
                { Exception = result.Exception, AdditionalProperties = result.AdditionalProperties }
            : content).ToList();
        return copy;
    }

    private static bool IsStructuredResult(AIContent content) => content is FunctionResultContent { Result: not (null or string) };
}
