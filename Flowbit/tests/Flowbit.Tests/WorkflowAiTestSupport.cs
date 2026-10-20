using System.Net;
using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Ai;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Ai;
using Flowbit.Service.Authoring;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flowbit.Tests;

/// <summary>The repository root and its authoring package, built in memory exactly as the API exports it.</summary>
internal static class AuthoringRepository
{
    public static string Root { get; } = Find();
    public static IReadOnlyDictionary<string, string> Package { get; } = AuthoringPackageBuilder.CreateResources(Root);

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "authoring", "flowbit-authoring", "SKILL.md"))
                && File.Exists(Path.Combine(directory.FullName, "Flowbit", "src", "Flowbit.Shared", "Flowbit.Shared.csproj"))) return directory.FullName;
        throw new InvalidOperationException("The repository root was not found.");
    }
}

/// <summary>Authoring knowledge over a fixed set of resources.</summary>
internal sealed class ResourceKnowledge(IReadOnlyDictionary<string, string> resources) : IAuthoringKnowledge
{
    public string ContractHash => "test-contract";
    public IReadOnlyDictionary<string, string> Resources => resources;
    public byte[] GetPackageZip() => throw new NotSupportedException();

    /// <summary>The six guides as one-line stubs plus one example, for tests that do not need the real docs.</summary>
    public static ResourceKnowledge Small { get; } = new(WorkflowAiDocs.Guides
        .ToDictionary(guide => "references/docs/" + guide, guide => "# " + guide + "\nLine two.\n")
        .Append(new("examples/README.md", "# Examples\n"))
        .Append(new("examples/basics/02-two-step-approval.json", ExampleWorkflowData.Read("examples/basics/02-two-step-approval.json")))
        .Append(new("references/docs/api-guide.md", "# Not for the agent\n"))
        .ToDictionary(item => item.Key, item => item.Value));
}

/// <summary>Builds the authoring conversation over mocked HTTP, with Flowbit's real validator.</summary>
internal static class AgentHarness
{
    public const string Key = "sk-agent-test-provider-key-0123456789";
    public const string Model = "glm-5.3-flash";

    /// <summary>The configured model of the tests, as the AI models page stores it.</summary>
    public static AiModelConnection Connection { get; } =
        new("test-model", "GLM-5.3-Flash", "https://opencode.example/zen/v1/", Model, "high", Key);

    public static WorkflowAiOptions Options(Action<WorkflowAiOptions>? configure = null)
    {
        var options = new WorkflowAiOptions { MaxProviderCalls = 40, RunTimeoutSeconds = 1800, MaxModelOutputTokens = 32_768, MaxNudges = 2 };
        configure?.Invoke(options);
        return options;
    }

    public static IWorkflowDefinitionValidator Validator() =>
        new WorkflowDefinitionValidator(new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance), new ServiceTaskOptions());

    public static WorkflowAiConversation Conversation(HttpMessageHandler handler, WorkflowAiOptions? options = null, IAuthoringKnowledge? knowledge = null)
    {
        options ??= Options();
        var validator = Validator();
        return new(new OpenAiCompatibleChatClientFactory(() => handler), new WorkflowAiDocs(knowledge ?? ResourceKnowledge.Small), validator, options,
            "test-contract", (model, _) =>
            {
                try
                {
                    validator.ValidateAuthored(model);
                    WorkflowModelMigrator.Normalize(model);
                    validator.ValidateNormalized(model);
                    return Task.FromResult(new AiValidationResultDto(true, [], []) { CanSave = true, CanPublish = true });
                }
                catch (WorkflowDomainException error) { return Task.FromResult(new AiValidationResultDto(false, [error.Message], [])); }
            });
    }

    public static AiTurnRequestDto Request(string message, JsonElement? current = null, AiConversationStateDto? state = null, bool resume = false,
        string? conversationId = null) => new()
    {
        ModelId = Connection.Id, ConversationId = conversationId ?? Conversations.Default, Message = message,
        CurrentWorkflow = current, SnapshotId = "snapshot-1", State = state, Continue = resume
    };

    public static class Conversations
    {
        public static readonly string Default = Guid.NewGuid().ToString();
    }
}

/// <summary>Returns scripted chat-completion bodies in order and records every request body (ported from Flowbit AI Lab v3).</summary>
internal sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
{
    public List<string> Bodies { get; } = [];
    public List<HttpRequestMessage> Requests { get; } = [];
    public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
    public TimeSpan Delay { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (Bodies.Count > responses.Length) throw new InvalidOperationException("An unexpected network request was attempted.");
        return new(Status) { Content = new StringContent(responses[Bodies.Count - 1], Encoding.UTF8, "application/json") };
    }
}

/// <summary>Chat-completion response bodies in the OpenAI-compatible format, as OpenCode Zen returns them.</summary>
internal static class Completions
{
    private static object Usage => new { prompt_tokens = 1000, completion_tokens = 100, total_tokens = 1100, prompt_tokens_details = new { cached_tokens = 400 } };

    public static string Final(string content, string finish = "stop") => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-final", @object = "chat.completion", created = 1_791_350_000, model = "glm-5.3-flash",
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = finish } },
        usage = Usage
    });

    public static string Tools(params (string Name, object Arguments)[] calls) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-tools", @object = "chat.completion", created = 1_791_350_000, model = "glm-5.3-flash",
        choices = new[] { new { index = 0, message = new { role = "assistant", content = "",
            tool_calls = calls.Select((c, i) => new { id = $"call-{Guid.NewGuid():N}-{i}", type = "function",
                function = new { name = c.Name, arguments = JsonSerializer.Serialize(c.Arguments) } }).ToArray() },
            finish_reason = "tool_calls" } },
        usage = Usage
    });

    public static string ReasoningTools(string reasoning, params string[] callIds) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-reasoning", @object = "chat.completion", created = 1_791_350_000, model = "glm-5.3-flash",
        choices = new[] { new { index = 0, message = new { role = "assistant", content = "", reasoning_content = reasoning,
            tool_calls = callIds.Select(id => new { id, type = "function", function = new { name = "test_tool", arguments = "{}" } }).ToArray() },
            finish_reason = "tool_calls" } }
    });

    public static string Save(string path, string content, bool append = false) => Tools(("save_append_file", new { path, content, append }));

    public static string Edit(string path, string oldText, string newText, bool replaceAll = false) =>
        Tools(("edit_file", new { path, oldText, newText, replaceAll }));

    /// <summary>The paths in the get_files_list result that the request body sends back as its latest tool message.</summary>
    public static string[] ListedPaths(string body)
    {
        var tool = JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "tool");
        using var result = JsonDocument.Parse(tool.GetProperty("content").GetString()!);
        return result.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!).ToArray();
    }

    /// <summary>The request's messages reduced to what the provider acts on: roles, text, tool calls and results, reasoning.
    /// Restored sessions differ only in serialization details such as a participant name or argument indentation.</summary>
    public static string[] Conversation(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("messages").EnumerateArray().Select(message =>
        {
            var parts = new List<string> { message.GetProperty("role").GetString()! };
            if (message.TryGetProperty("content", out var content))
                parts.Add(content.ValueKind == JsonValueKind.String ? "text:" + content.GetString()
                    : content.ValueKind == JsonValueKind.Array ? "text:" + string.Concat(content.EnumerateArray().Select(p => p.GetProperty("text").GetString()))
                    : "text:");
            if (message.TryGetProperty("tool_call_id", out var callId)) parts.Add("result-of:" + callId.GetString());
            if (message.TryGetProperty("tool_calls", out var calls))
                foreach (var call in calls.EnumerateArray())
                {
                    var function = call.GetProperty("function");
                    using var arguments = JsonDocument.Parse(function.GetProperty("arguments").GetString()!);
                    parts.Add($"call:{call.GetProperty("id").GetString()}:{function.GetProperty("name").GetString()}:{JsonSerializer.Serialize(arguments.RootElement)}");
                }
            if (message.TryGetProperty("reasoning_content", out var reasoning)) parts.Add("reasoning:" + reasoning.GetString());
            return string.Join(" | ", parts);
        }).ToArray();
    }
}
