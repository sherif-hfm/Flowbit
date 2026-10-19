using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Flowbit.BrowserTests.Infrastructure;

/// <summary>
/// A local upstream OpenAI-compatible chat-completions provider, never an API or application-state substitute. Each request takes the
/// next scripted reply: native tool calls (with GLM-style reasoning_content), a final text, a truncated response or an HTTP failure.
/// The agent's tools run inside Flowbit; this host only decides what the model would call.
/// </summary>
public sealed class AiProviderTestHost : IAsyncDisposable
{
    public const string Reasoning = "Synthetic reasoning before the tool call.";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private WebApplication? app;
    private readonly ConcurrentQueue<Reply> replies = new();
    private int unexpected;
    public string BaseAddress { get; private set; } = "";
    public ConcurrentQueue<Request> Requests { get; } = new();
    /// <summary>Requests that arrived with no scripted reply; they are answered with 503.</summary>
    public int UnexpectedRequests => Volatile.Read(ref unexpected);

    /// <summary>
    /// What one provider request carried. Conversation reduces the messages to roles, text, tool calls, tool results and reasoning,
    /// so a resumed request can be compared with the interrupted one. Body is the raw request for "never sent" assertions.
    /// </summary>
    public sealed record Request(string KeyHash, string Session, string UserAgent, string Model, string? ReasoningEffort, int MaxTokens,
        string[] ToolNames, string Instructions, string FirstUserMessage, string LastUserMessage, string LastRole, string[] Conversation, string Body);

    public sealed class Reply(bool delayed)
    {
        internal bool Delayed { get; } = delayed;
        internal int StatusCode { get; init; } = StatusCodes.Status200OK;
        internal string FinishReason { get; init; } = "stop";
        internal string Content { get; init; } = "";
        internal string? ReasoningContent { get; init; }
        internal (string Name, object Arguments)[] Calls { get; init; } = [];
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete() => Release.TrySetResult();
    }

    /// <summary>The model calls these tools in one response.</summary>
    public Reply EnqueueTools((string Name, object Arguments)[] calls, bool delayed = false) =>
        Add(new Reply(delayed) { Calls = calls, FinishReason = "tool_calls", ReasoningContent = Reasoning });

    /// <summary>The model saves the browser fixture workflow under a new name, with every node at 0,0 so Flowbit's layout must place it.</summary>
    public Reply EnqueueCreate(string name, string? openQuestions = null, bool delayed = false)
    {
        var definition = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "editor-basic.json")))!;
        definition["name"] = name;
        foreach (var node in definition["flowNodes"]!.AsArray()) { node!["x"] = 0; node["y"] = 0; }
        (string, object)[] calls = openQuestions is null
            ? [("save_append_file", new { path = "workflow.json", content = definition.ToJsonString(Indented), append = false })]
            : [("save_append_file", new { path = "workflow.json", content = definition.ToJsonString(Indented), append = false }),
               ("save_append_file", new { path = "open-questions.md", content = openQuestions, append = false })];
        return EnqueueTools(calls, delayed);
    }

    /// <summary>The model renames the saved workflow with edit_file, which works on the indented workflow.json the host saved.</summary>
    public Reply EnqueueRename(string from, string to, bool delayed = false) =>
        EnqueueTools([("edit_file", new { path = "workflow.json", oldText = $"\"name\": \"{from}\"", newText = $"\"name\": \"{to}\"", replaceAll = false })], delayed);

    /// <summary>The model ends its turn with this text.</summary>
    public Reply EnqueueText(string text, bool delayed = false) => Add(new Reply(delayed) { Content = text });

    /// <summary>The response reaches max_completion_tokens before it finishes.</summary>
    public Reply EnqueueTruncation() => Add(new Reply(false) { FinishReason = "length", Content = "{\"path\":\"workflow.json\",\"content\":\"{\\\"name\\\":\\\"Truncated" });

    public Reply EnqueueFailure(HttpStatusCode status, bool delayed = false) => Add(new Reply(delayed) { StatusCode = (int)status });

    private Reply Add(Reply reply)
    {
        replies.Enqueue(reply);
        return reply;
    }

    public static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        app = builder.Build();
        app.MapPost("/chat/completions", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            var request = Read(body, context.Request.Headers);
            Requests.Enqueue(request);
            if (!replies.TryDequeue(out var reply))
            {
                Interlocked.Increment(ref unexpected);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            reply.Arrived.TrySetResult();
            if (reply.Delayed)
            {
                try { await reply.Release.Task.WaitAsync(context.RequestAborted); }
                catch (OperationCanceledException) { reply.Cancelled.TrySetResult(); return; }
            }
            if (reply.StatusCode != StatusCodes.Status200OK)
            {
                context.Response.StatusCode = reply.StatusCode;
                await context.Response.WriteAsJsonAsync(new { error = new { type = "test_provider_failure", message = "Synthetic provider failure." } }, context.RequestAborted);
                return;
            }
            var message = new JsonObject { ["role"] = "assistant", ["content"] = reply.Content };
            if (reply.ReasoningContent is not null) message["reasoning_content"] = reply.ReasoningContent;
            if (reply.Calls.Length > 0)
                message["tool_calls"] = new JsonArray(reply.Calls.Select(call => (JsonNode)new JsonObject
                {
                    ["id"] = "call_" + Guid.NewGuid().ToString("N"), ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = JsonSerializer.Serialize(call.Arguments) }
                }).ToArray());
            var completion = new JsonObject
            {
                ["id"] = "chatcmpl-" + Guid.NewGuid().ToString("N"), ["object"] = "chat.completion", ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = request.Model,
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = reply.FinishReason }),
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = 2000, ["completion_tokens"] = reply.FinishReason == "length" ? request.MaxTokens : 100,
                    ["total_tokens"] = 2000 + (reply.FinishReason == "length" ? request.MaxTokens : 100),
                    ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 1500 }
                }
            };
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(completion.ToJsonString(), context.RequestAborted);
        });
        await app.StartAsync();
        BaseAddress = app.Urls.Single() + "/";
    }

    private static Request Read(string body, IHeaderDictionary headers)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        var users = messages.Where(item => item.GetProperty("role").GetString() == "user").Select(Text).ToArray();
        return new(
            HashKey(headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal)),
            headers["x-opencode-session"].ToString(),
            headers.UserAgent.ToString(),
            root.GetProperty("model").GetString()!,
            root.TryGetProperty("reasoning_effort", out var reasoning) ? reasoning.GetString() : null,
            root.TryGetProperty("max_completion_tokens", out var maxTokens) ? maxTokens.GetInt32() : 0,
            root.TryGetProperty("tools", out var tools) ? tools.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!).ToArray() : [],
            messages.FirstOrDefault(item => item.GetProperty("role").GetString() is "system" or "developer") is { ValueKind: JsonValueKind.Object } system ? Text(system) : "",
            users.FirstOrDefault() ?? "",
            users.LastOrDefault() ?? "",
            messages.Length == 0 ? "" : messages[^1].GetProperty("role").GetString()!,
            messages.Select(Describe).ToArray(),
            body);
    }

    private static string Text(JsonElement message) => !message.TryGetProperty("content", out var content) ? ""
        : content.ValueKind == JsonValueKind.String ? content.GetString()!
        : content.ValueKind == JsonValueKind.Array ? string.Concat(content.EnumerateArray().Select(part => part.TryGetProperty("text", out var text) ? text.GetString() : ""))
        : "";

    private static string Describe(JsonElement message)
    {
        var parts = new List<string> { message.GetProperty("role").GetString()!, "text:" + Text(message) };
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
    }

    public async ValueTask DisposeAsync()
    {
        while (replies.TryDequeue(out var reply)) reply.Complete();
        if (app is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StopAsync(timeout.Token);
        await app.DisposeAsync();
        app = null;
    }
}
