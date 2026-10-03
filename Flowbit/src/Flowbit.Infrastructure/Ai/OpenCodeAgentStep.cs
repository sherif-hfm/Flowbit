using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Service.Ai;
using Flowbit.Shared.Dtos;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Flowbit.Infrastructure.Ai;

/// <summary>
/// One native-tool step through Agent Framework. Flowbit dispatches the returned tool through its shared
/// kernel; automatic SDK function loops/history/retries are deliberately disabled. This makes finishing,
/// cancellation, compaction and every network attempt subject to the same budgets as text execution.
/// </summary>
internal static class OpenCodeAgentStep
{
    internal static string Kind(string name) => name switch
    {
        "read_authoring_context" => "read", "apply_draft_batch" => "edit",
        "finish_proposal" => "finish", "request_clarification" => "clarification",
        _ => throw new WorkflowAiException("provider_tools_unsupported", "The model requested an unsupported authoring tool.", 502)
    };
    private static string Name(string kind) => kind switch
    {
        "read" => "read_authoring_context", "edit" => "apply_draft_batch",
        "finish" => "finish_proposal", "clarification" => "request_clarification",
        _ => throw new InvalidOperationException("Unknown authoring tool.")
    };

    internal static async Task<AiCompletion> CompleteAsync(OpenCodeGoProvider provider, string modelId, string conversationId,
        IReadOnlyList<AiChatMessageDto> messages, string apiKey, int tokens, AiExecutionSettings execution, CancellationToken cancellationToken)
    {
        using var client = new ZenChatClient(provider, modelId, conversationId, apiKey, tokens, execution);
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "Flowbit authoring", UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions { ModelId = modelId, MaxOutputTokens = tokens, Tools = ToolDeclarations() }
        });
        var input = messages.Select(message => new ChatMessage(new ChatRole(message.Role), message.Content)).ToList();
        input.Insert(1, new ChatMessage(ChatRole.System,
            "Use exactly ONE native function tool per response, with command fields as its arguments and without kind. " +
            "Do not return text JSON commands. read_authoring_context reads; apply_draft_batch edits; finish_proposal " +
            "performs final validation locally (errors return on the next step); request_clarification asks questions. " +
            "No standalone validate tool exists: finish as soon as the complete checklist is implemented."));
        var response = await agent.RunAsync(input, cancellationToken: cancellationToken);
        var wire = client.Completion ?? throw new InvalidOperationException("Agent made no provider call.");
        if (wire.FinishReason == "length") return wire;
        var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
        if (calls.Length != 1) throw new WorkflowAiException("provider_tools_unsupported", "The agent did not return one complete authoring tool.", 502);
        // Preserve the original JSON for duplicate checks, exact receipt hashing and credential checks in the kernel.
        return wire with { FinishReason = "stop" };
    }

    private static IList<AITool> ToolDeclarations()
    {
        var common = "\"message\":{\"type\":\"string\"},\"plan\":{\"type\":\"string\"}";
        var final = "\"assumptions\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
            "\"dependencies\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
            "\"changeSummary\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
            "\"sourceReferences\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"sourceName\":{\"type\":\"string\"},\"pageNumber\":{\"type\":\"integer\"},\"requirement\":{\"type\":\"string\"}}}}";
        return
        [
            Tool("read", "Read bounded packaged references, original inputs or the private draft.", common + ",\"reads\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":6,\"items\":{\"type\":\"object\"}}", "reads"),
            Tool("edit", "Atomically apply complete typed operations at the current revision; never saves or publishes.", common + ",\"baseRevision\":{\"type\":\"integer\"},\"batchId\":{\"type\":\"string\"},\"operations\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}}", "baseRevision", "batchId", "operations"),
            Tool("finish", "Validate the complete draft and return a proposal; return diagnostics if invalid.", common + "," + final),
            Tool("clarification", "Ask for missing essential business decisions.", common + "," + final + ",\"questions\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", "questions")
        ];
    }

    private static AIFunctionDeclaration Tool(string kind, string description, string properties, params string[] required) =>
        AIFunctionFactory.CreateDeclaration(Name(kind), description, JsonSerializer.Deserialize<JsonElement>(
            "{\"type\":\"object\",\"properties\":{" + properties + "},\"additionalProperties\":false,\"required\":" + JsonSerializer.Serialize(required) + "}"));

    private sealed class ZenChatClient(OpenCodeGoProvider provider, string modelId, string conversationId, string apiKey,
        int tokens, AiExecutionSettings execution) : IChatClient
    {
        public AiCompletion? Completion { get; private set; }
        private int calls;
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) != 1) throw new InvalidOperationException("SDK attempted an unbudgeted provider call.");
            var tools = (options?.Tools ?? []).OfType<AIFunctionDeclaration>().Select(tool => (object)new
            { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.JsonSchema } }).ToArray();
            if (tools.Length != 4) throw new InvalidOperationException("Authoring tools are missing.");
            Completion = await provider.CompleteCoreAsync(modelId, conversationId,
                messages.Select(message => new AiChatMessageDto(message.Role.Value, message.Text)).ToArray(), apiKey, tokens, execution, tools, cancellationToken);
            IList<AIContent> contents = [];
            if (Completion.FinishReason != "length")
            {
                var command = JsonNode.Parse(Completion.Content)!.AsObject();
                var name = Name(command["kind"]!.GetValue<string>());
                command.Remove("kind");
                contents.Add(new FunctionCallContent("flowbit-step", name, command.ToDictionary(pair => pair.Key, pair => (object?)JsonSerializer.SerializeToElement(pair.Value))));
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
            {
                FinishReason = Completion.FinishReason == "length" ? ChatFinishReason.Length : ChatFinishReason.ToolCalls,
                Usage = new UsageDetails { InputTokenCount = Completion.InputTokens, OutputTokenCount = Completion.OutputTokens }, ModelId = modelId
            };
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Native tool fragments are never dispatched. Flowbit streams only trusted checkpoint/progress envelopes.
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
