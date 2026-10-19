using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Flowbit.Infrastructure.Ai;

// Copied from Flowbit AI Lab v3. The installed OpenAI adapter reads compatible reasoning_content into framework
// TextReasoningContent but drops it on requests. Patch only that transport field;
// the framework still owns messages, tool invocation, history and serialization.
public sealed class CompatibleReasoningReplayChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages.Select(ReplayMessage).ToArray(), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages.Select(ReplayMessage).ToArray(), options, cancellationToken);

    private static ChatMessage ReplayMessage(ChatMessage message)
    {
        if (message.Role != ChatRole.Assistant) return message;
        var reasoning = message.Contents.OfType<TextReasoningContent>().ToArray();
        if (reasoning.Length == 0) return message;
        AssistantChatMessage raw;
        if (message.RawRepresentation is AssistantChatMessage original)
            raw = ModelReaderWriter.Read<AssistantChatMessage>(ModelReaderWriter.Write(original))!;
        else if (message.RawRepresentation is ChatCompletion completion) raw = new AssistantChatMessage(completion);
        else
        {
            if (message.Contents.Any(c => c is not (TextContent or TextReasoningContent or FunctionCallContent or ErrorContent)))
                throw new NotSupportedException("Compatible reasoning replay requires supported text/function message content.");
            var text = message.Contents.OfType<TextContent>().Select(t => ChatMessageContentPart.CreateTextPart(t.Text)).ToArray();
            var calls = message.Contents.OfType<FunctionCallContent>().Select(c => ChatToolCall.CreateFunctionToolCall(c.CallId, c.Name,
                BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(c.Arguments, AIJsonUtilities.DefaultOptions)))).ToArray();
            raw = text.Length > 0 ? new AssistantChatMessage(text) : calls.Length > 0 ? new AssistantChatMessage(calls) : new AssistantChatMessage("");
            if (text.Length > 0) foreach (var call in calls) raw.ToolCalls.Add(call);
            raw.Refusal = message.Contents.OfType<ErrorContent>().LastOrDefault(c => c.ErrorCode == "Refusal")?.Message;
            if (message.AuthorName is not null)
            {
                var name = new string(message.AuthorName.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(64).ToArray());
                if (name.Length > 0) raw.ParticipantName = name;
            }
        }
#pragma warning disable SCME0001 // Installed SDK JSON extension point for compatible providers.
        raw.Patch.Set("$.reasoning_content"u8, string.Concat(reasoning.Select(r => r.Text)));
#pragma warning restore SCME0001
        var replay = message.Clone(); replay.RawRepresentation = raw;
        return replay;
    }
}
