using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Flowbit.Infrastructure.Ai;
using Flowbit.Service.Ai;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;

namespace Flowbit.Tests;

/// <summary>Microsoft Agent Framework behaviour the authoring agent depends on, checked with mocked HTTP only.</summary>
public sealed class WorkflowAiFrameworkTests
{
    [Fact]
    public async Task ReasoningIsReplayedAcrossToolTurnsThroughTheCompactClient()
    {
        // Ported from Flowbit AI Lab v3: GLM reasoning_content must be replayed on later tool-loop requests.
        const string firstReasoning = "opaque-test-state-one\nwith quoted \"value\"";
        const string secondReasoning = "opaque-test-state-two";
        var handler = new ScriptedHandler(Completions.ReasoningTools(firstReasoning, "call-a", "call-b"),
            Completions.ReasoningTools(secondReasoning, "call-c"), Completions.Final("finished"));
        var agent = CreateAgent(handler, out _);

        var final = await agent.RunAsync("source text", await agent.CreateSessionAsync());

        Assert.Equal("finished", final.Text);
        Assert.Equal(3, handler.Bodies.Count);
        AssertReasoning(handler.Bodies[1], [firstReasoning]);
        AssertReasoning(handler.Bodies[2], [firstReasoning, secondReasoning]);
        using var body = JsonDocument.Parse(handler.Bodies[2]);
        var tools = body.RootElement.GetProperty("messages").EnumerateArray().Where(m => m.GetProperty("role").GetString() == "tool").ToArray();
        Assert.Equal(new[] { "call-a", "call-b", "call-c" }, tools.Select(m => m.GetProperty("tool_call_id").GetString()));
        // Structured tool results go on the wire without indentation.
        Assert.All(tools, m => Assert.Equal("{\"value\":\"tool-result\",\"items\":[1,2]}", m.GetProperty("content").GetString()));
    }

    [Fact]
    public async Task AStartOfCallSnapshotResumesWithThePendingToolResults()
    {
        // The third request is refused, as the request cap refuses request N+1.
        var stopped = new ScriptedHandler(Completions.ReasoningTools("first", "call-1"), Completions.ReasoningTools("second", "call-2"));
        var agent = CreateAgent(stopped, out var snapshots);
        var firstSession = await agent.CreateSessionAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => agent.RunAsync("Build the workflow.", firstSession));
        Assert.Equal(3, stopped.Bodies.Count);
        Assert.Equal(3, snapshots.Count);

        // Between calls the stored history ends with the assistant's tool calls; their results are the pending input.
        var (sessionJson, pendingJson) = snapshots[^1];
        var pending = JsonSerializer.Deserialize<List<ChatMessage>>(pendingJson, AIJsonUtilities.DefaultOptions)!;
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(Assert.Single(pending).Contents));
        Assert.Equal("call-2", result.CallId);

        var resumed = new ScriptedHandler(Completions.Final("done"));
        var restoredAgent = CreateAgent(resumed, out _);
        var session = await restoredAgent.DeserializeSessionAsync(sessionJson);
        var final = await restoredAgent.RunAsync(pending, session);

        Assert.Equal("done", final.Text);
        Assert.Equal(Completions.Conversation(stopped.Bodies[2]), Completions.Conversation(resumed.Bodies[0]));
        Assert.True(session.TryGetInMemoryChatHistory(out var history));
        Assert.Equal(new[] { "user", "assistant", "tool", "assistant", "tool", "assistant" }, history!.Select(m => m.Role.Value));
    }

    [Fact]
    public async Task AFailureOnTheFirstCallResumesWithThePendingUserText()
    {
        var stopped = new ScriptedHandler();
        var agent = CreateAgent(stopped, out var snapshots);
        var firstSession = await agent.CreateSessionAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => agent.RunAsync("Build the workflow.", firstSession));

        // A failed call stores nothing, so the user's message exists only as the pending input.
        var (sessionJson, pendingJson) = Assert.Single(snapshots);
        var pending = JsonSerializer.Deserialize<List<ChatMessage>>(pendingJson, AIJsonUtilities.DefaultOptions)!;
        Assert.Equal("Build the workflow.", Assert.Single(pending).Text);

        var resumed = new ScriptedHandler(Completions.Final("done"));
        var restoredAgent = CreateAgent(resumed, out _);
        var session = await restoredAgent.DeserializeSessionAsync(sessionJson);
        await restoredAgent.RunAsync(pending, session);

        Assert.Equal(Completions.Conversation(stopped.Bodies[0]), Completions.Conversation(resumed.Bodies[0]));
        Assert.Equal(new[] { "system | text:Mock transport test.", "user | text:Build the workflow." }, Completions.Conversation(resumed.Bodies[0]));
    }

    [Fact]
    public async Task ARunWithoutPendingInputWouldResendTheUnansweredToolCall()
    {
        // Why Continue must send the pending input: without it, the request ends with tool calls that have no results.
        var stopped = new ScriptedHandler(Completions.ReasoningTools("first", "call-1"));
        var agent = CreateAgent(stopped, out var snapshots);
        var firstSession = await agent.CreateSessionAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => agent.RunAsync("Build the workflow.", firstSession));

        var resumed = new ScriptedHandler(Completions.Final("done"));
        var restoredAgent = CreateAgent(resumed, out _);
        var session = await restoredAgent.DeserializeSessionAsync(snapshots[^1].Session);
        await restoredAgent.RunAsync(session);

        var conversation = Completions.Conversation(Assert.Single(resumed.Bodies));
        Assert.StartsWith("assistant", conversation[^1]);
        Assert.Contains("call:call-1:test_tool", conversation[^1]);
    }

    private static ChatClientAgent CreateAgent(ScriptedHandler handler, out List<(JsonElement Session, string Pending)> snapshots)
    {
        var client = new OpenAIClient(new ApiKeyCredential("mock-provider-key"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://example.invalid/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            RetryPolicy = new ClientRetryPolicy(0)
        });
        SnapshotClient? snapshot = null;
        var chat = new CompactToolResultsChatClient(new CompatibleReasoningReplayChatClient(client.GetChatClient("glm-5.3-flash").AsIChatClient()))
            .AsBuilder()
            .UseFunctionInvocation(configure: functions =>
            {
                functions.AllowConcurrentInvocation = false;
                functions.IncludeDetailedErrors = true;
                functions.MaximumIterationsPerRequest = 40;
            })
            .Use(inner => snapshot = new SnapshotClient(inner))
            .UsePerServiceCallChatHistoryPersistence()
            .Build();
        snapshots = snapshot!.Snapshots;
        return new ChatClientAgent(chat, new ChatClientAgentOptions
        {
            Id = "framework-test",
            UseProvidedChatClientAsIs = true,
            RequirePerServiceCallChatHistoryPersistence = true,
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            ChatOptions = new ChatOptions
            {
                Instructions = "Mock transport test.",
                Tools = [AIFunctionFactory.Create(() => new { value = "tool-result", items = new[] { 1, 2 } }, "test_tool")],
                ToolMode = ChatToolMode.Auto
            }
        });
    }

    private static void AssertReasoning(string body, string[] expected)
    {
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().Where(m => m.TryGetProperty("reasoning_content", out _)).ToArray();
        Assert.Equal(expected, messages.Select(m => m.GetProperty("reasoning_content").GetString()));
        Assert.All(messages, m => Assert.Equal("assistant", m.GetProperty("role").GetString()));
    }

    /// <summary>Records the stored session and the not-yet-stored input at the start of every model call.</summary>
    private sealed class SnapshotClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public List<(JsonElement Session, string Pending)> Snapshots { get; } = [];

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var pending = messages.ToList();
            var run = AIAgent.CurrentRunContext ?? throw new InvalidOperationException("Snapshots need a running agent.");
            var session = await run.Agent.SerializeSessionAsync(run.Session!, cancellationToken: cancellationToken);
            Snapshots.Add((session, JsonSerializer.Serialize(pending, AIJsonUtilities.DefaultOptions)));
            return await base.GetResponseAsync(pending, options, cancellationToken);
        }
    }
}
