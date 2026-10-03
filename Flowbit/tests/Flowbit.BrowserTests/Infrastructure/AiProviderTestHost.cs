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

/// <summary>A local upstream provider, never an API or application-state substitute.</summary>
public sealed class AiProviderTestHost : IAsyncDisposable
{
    private WebApplication? app;
    private readonly ConcurrentQueue<Reply> replies = new();
    public string BaseAddress { get; private set; } = "";
    public ConcurrentQueue<Request> Requests { get; } = new();

    public sealed record Request(string KeyHash, string Conversation, string Model, string Requirement, string WorkflowKey, string[] SourceTexts, string[] CatalogKeys, bool IncludesCatalogValue,
        long Revision, int MaxOperations, int MaxTokens);
    public sealed class Reply(string kind, string workflowName, bool delayed)
    {
        internal string Kind { get; } = kind;
        internal string WorkflowName { get; } = workflowName;
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Delayed { get; } = delayed;
        internal Func<JsonNode, string>? Content { get; init; }
        internal string FinishReason { get; init; } = "stop";
        internal int StatusCode { get; init; } = StatusCodes.Status200OK;
        public void Complete() => Release.TrySetResult();
    }

    public Reply Enqueue(string workflowName, bool clarification = false, bool delayed = false)
    {
        var reply = new Reply(clarification ? "clarification" : "proposal", workflowName, delayed);
        replies.Enqueue(reply);
        return reply;
    }

    public Reply EnqueueTruncation()
    {
        var reply = new Reply("edit", "", false)
        {
            FinishReason = "length",
            Content = _ => "{\"kind\":\"edit\",\"operations\":[{\"op\":\"set\",\"target\":\"workflow\",\"path\":\"/name\",\"value\":\"Truncated name must never apply"
        };
        replies.Enqueue(reply);
        return reply;
    }

    public Reply EnqueueNameEdit(string name)
    {
        var batchId = Guid.NewGuid().ToString("N");
        var reply = new Reply("edit", name, false)
        {
            Content = input => JsonSerializer.Serialize(new
            {
                kind = "edit", baseRevision = input["revision"]!.GetValue<long>(), batchId,
                plan = "Rename the workflow, then validate the completed draft.",
                operations = new[] { new { op = "set", target = "workflow", path = "/name", value = name } }
            })
        };
        replies.Enqueue(reply);
        return reply;
    }

    public Reply EnqueueFinish(bool delayed = false)
    {
        var reply = new Reply("finish", "", delayed)
        {
            Content = _ => JsonSerializer.Serialize(new
            {
                kind = "finish", message = "The workflow is ready for review.",
                assumptions = Array.Empty<string>(), dependencies = Array.Empty<string>(),
                changeSummary = new[] { "Updated the workflow name." }, sourceReferences = Array.Empty<object>()
            })
        };
        replies.Enqueue(reply);
        return reply;
    }

    public Reply EnqueueFailure(HttpStatusCode status)
    {
        var reply = new Reply("failure", "", false) { StatusCode = (int)status };
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
            if (!replies.TryDequeue(out var reply))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var envelope = body.RootElement;
            var prompt = envelope.GetProperty("messages").EnumerateArray()
                .Last(item => item.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;
            var input = JsonNode.Parse(prompt)!;
            var current = input["currentWorkflow"];
            var workflowKey = current?["id"]?.GetValue<string>() ?? "missing-workflow-key";
            Requests.Enqueue(new(HashKey(context.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal)),
                context.Request.Headers["x-opencode-session"].ToString(), envelope.GetProperty("model").GetString()!,
                input["request"]!.GetValue<string>(), workflowKey,
                input["sourcePages"]!.AsArray().Select(page => page!["text"]!.GetValue<string>()).ToArray(),
                input["selectedSharedVariables"]!.AsArray().Select(item => item!["key"]!.GetValue<string>()).ToArray(),
                input["selectedSharedVariables"]!.AsArray().Any(item => item!.AsObject().ContainsKey("value")),
                input["revision"]?.GetValue<long>() ?? 0, input["maxOperations"]?.GetValue<int>() ?? 0,
                envelope.TryGetProperty("max_tokens", out var maxTokens) ? maxTokens.GetInt32() : 0));
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
            if (reply.Content is not null)
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    choices = new[] { new { finish_reason = reply.FinishReason, message = new { role = "assistant", content = reply.Content(input) } } }
                }, context.RequestAborted);
                return;
            }
            var definition = current?["flowNodes"] is JsonArray { Count: > 0 }
                ? current.DeepClone()
                : JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "editor-basic.json")));
            definition!["id"] = workflowKey;
            definition["name"] = reply.WorkflowName;
            // Provider suggestions deliberately move existing objects. Flowbit must keep authored positions.
            foreach (var node in definition["flowNodes"]!.AsArray()) { node!["x"] = 0; node["y"] = 0; }
            var response = JsonSerializer.Serialize(new
            {
                kind = reply.Kind,
                message = reply.Kind == "clarification" ? "Who should review the request?" : "The workflow is ready for review.",
                questions = reply.Kind == "clarification" ? new[] { "Which role reviews the request?" } : [],
                definition = reply.Kind == "clarification" ? null : definition,
                assumptions = new[] { "One review is required." },
                dependencies = Array.Empty<string>(),
                changeSummary = new[] { $"Name the workflow {reply.WorkflowName}." },
                sourceReferences = Array.Empty<string>()
            });
            await context.Response.WriteAsJsonAsync(new
            {
                choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = response } } }
            }, context.RequestAborted);
        });
        await app.StartAsync();
        BaseAddress = app.Urls.Single() + "/";
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
