using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Flowbit.Infrastructure.Ai;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Ai;
using Flowbit.Service.Authoring;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

// This tool runs synthetic authoring with the editor assistant's agent engine only. It never applies, saves, publishes or executes a workflow.
var arguments = args.Chunk(2).ToDictionary(pair => pair[0].TrimStart('-'), pair => pair.Length == 2 ? pair[1] : throw new ArgumentException("Use --name value pairs."));
string[] known = ["key-file", "package", "output", "fixture", "fixtures", "policy", "timeout-seconds", "resume-test", "check-only", "diagnostics"];
foreach (var name in arguments.Keys.Where(name => !known.Contains(name)))
    throw new ArgumentException($"Unknown argument --{name}. The agent engine has no variant, effort or review settings.");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var fixture = arguments.GetValueOrDefault("fixture", "complex");
var policy = arguments.GetValueOrDefault("policy", "strict-v1");
if (policy is not ("strict-v1" or "functional-v2")) throw new ArgumentException("Unknown acceptance policy.");
var labelWarnings = new List<object>();
var timeoutSeconds = int.Parse(arguments.GetValueOrDefault("timeout-seconds", "1800"), System.Globalization.CultureInfo.InvariantCulture);
if (timeoutSeconds is < 30 or > AiAuthoringLimits.MaxRunTimeoutSeconds) throw new ArgumentException("Timeout must be between 30 and 3600 seconds.");
var resumeTest = bool.Parse(arguments.GetValueOrDefault("resume-test", "false"));
var output = Path.GetFullPath(arguments["output"]);
Directory.CreateDirectory(output);
if (arguments.TryGetValue("check-only", out var checkFile))
{
    var candidate = JsonSerializer.Deserialize<AiTurnResultDto>(await File.ReadAllTextAsync(checkFile), json);
    var checkPassed = fixture is "simple" or "modify" && CheckSimple(candidate, fixture == "modify", policy, labelWarnings);
    await File.WriteAllTextAsync(Path.Combine(output, "acceptance.json"), JsonSerializer.Serialize(new { policy, passed = checkPassed, labelWarnings, offline = true }, json));
    return checkPassed ? 0 : 1;
}
if (fixture is not ("simple" or "modify" or "complex")) throw new ArgumentException("Fixture must be simple, modify or complex.");
var key = (await File.ReadAllTextAsync(arguments["key-file"])).Trim();
using var diagnostics = new EvaluationDiagnostics(arguments.GetValueOrDefault("diagnostics") == "true");
using var traces = new EvaluationTraces();
// The shipped configuration: Zen, GLM-5.3-Flash with high reasoning, and the lab's limits (40 calls, 32,768 output tokens, two follow-ups).
var options = new WorkflowAiOptions
{
    RunTimeoutSeconds = timeoutSeconds, OpenCodeModels = ["glm-5.3-flash"], OpenCodeReasoningEfforts = new() { ["glm-5.3-flash"] = "high" }
};
Action? onHttpAttempt = null;
using var transport = new ObservedHttpHandler(() => onHttpAttempt?.Invoke());
var clients = new OpenCodeZenChatClientFactory(options, () => transport);
using var gate = new WorkflowAiConcurrencyGate(options);
var knowledge = new AuthoringKnowledge(arguments["package"]);
var docs = new WorkflowAiDocs(knowledge);
var service = new WorkflowAiAuthoringService(clients, knowledge, docs,
    new WorkflowDefinitionValidator(new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance), new ServiceTaskOptions()),
    new WorkflowDefinitionReadinessChecker(durableProcessingOptions: new DurableProcessingOptions { PublicationEnabled = true }), options, gate);
var clock = Stopwatch.StartNew();
var inputPath = Path.Combine(AppContext.BaseDirectory, "fixtures", fixture + ".txt");
if (!File.Exists(inputPath)) inputPath = Path.Combine(arguments["fixtures"], fixture + ".txt");
var request = new AiTurnRequestDto
{
    ModelId = "glm-5.3-flash", ConversationId = Guid.NewGuid().ToString("N"), SnapshotId = "synthetic-evaluation",
    Message = await File.ReadAllTextAsync(inputPath),
    CurrentWorkflow = fixture == "modify" ? JsonSerializer.SerializeToElement(Baseline(), json) : null
};
using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
using var cancel = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
var events = new ConcurrentQueue<object>();
AiConversationStateDto? state = null;
AiTurnResultDto? result = null;
AiRunSummaryDto? lastRun = null;
string? failure = null;
var resumed = false;
int cancellationArmed = 0, cancellationIssued = 0;
using var recordingGate = new SemaphoreSlim(1);
onHttpAttempt = () =>
{
    if (Volatile.Read(ref cancellationArmed) != 0 && Interlocked.CompareExchange(ref cancellationIssued, 1, 0) == 0)
    {
        // Cancel a real in-flight model call, as the panel's Cancel does, not the host work before it.
        transport.MarkNextAsCancelTarget();
        cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
    }
};
try
{
    try { result = await service.RunAsync(request, key, Observe, cancel.Token); }
    catch (OperationCanceledException) when (resumeTest && !overall.IsCancellationRequested && state is { CanContinue: true })
    {
        // Continue, as the panel does: the same request with the last checkpoint and no new message.
        resumed = true;
        transport.MarkResumeStart();
        result = await service.RunAsync(request with { Message = "", Continue = true, State = state }, key, Observe, overall.Token);
    }
}
catch (WorkflowAiException error) { failure = error.Code; }
catch (OperationCanceledException) { failure = "evaluation_deadline"; }
// Continue must resend exactly the request whose model call was cancelled.
var resumeVerified = resumed && transport.CancelledRequest is { } stopped && transport.FirstResumedRequest == stopped;
if (result is not null) await Save("result.json", result with { State = null });
var passed = result?.Kind == "proposal" && result.Validation.IsValid && result.Validation.CanSave && result.Validation.CanPublish
    && clock.Elapsed.TotalSeconds <= timeoutSeconds;
if (fixture is "simple" or "modify") passed &= CheckSimple(result, fixture == "modify", policy, labelWarnings);
if (resumeTest) passed &= resumed && resumeVerified && transport.CancelledAttempts > 0;
await Save("evidence.json", new
{
    engine = "agent", fixture, policy, passed, failure, kind = result?.Kind, code = result?.Code, labelWarnings, resumeTest, resumed, resumeVerified,
    transport.Attempts, transport.CancelledAttempts, seconds = clock.Elapsed.TotalSeconds,
    model = request.ModelId, endpoint = options.OpenCodeBaseUrl, knowledge.ContractHash,
    promptFingerprint = WorkflowAiAgent.PromptFingerprint, docsFingerprint = docs.Fingerprint, docsFiles = docs.Files.Count,
    limits = new { options.RunTimeoutSeconds, options.MaxProviderCalls, options.MaxModelOutputTokens, options.MaxNudges, reasoningEffort = "high" },
    run = lastRun, events, calls = transport.Calls, diagnostics = diagnostics.Snapshot()
});
await Save("trace.json", traces.Snapshot());
return passed ? 0 : 1;

async Task Observe(AiRunEventDto frame, CancellationToken token)
{
    await recordingGate.WaitAsync(token);
    try
    {
        if (frame.State is { } saved) state = saved;
        if (frame.Run is { } run) lastRun = run;
        // Only allowlisted metadata: never messages, tool summaries, prompts or workflow content.
        var item = new { frame.Type, frame.Stage, frame.Code, frame.Sequence, frame.Run, canContinue = frame.State?.CanContinue, kind = frame.Result?.Kind };
        events.Enqueue(item);
        await File.AppendAllTextAsync(Path.Combine(output, "progress.ndjson"), JsonSerializer.Serialize(item) + Environment.NewLine, token);
        Console.WriteLine(JsonSerializer.Serialize(item));
        // A checkpoint starts a model call; arm the cancellation once at least one call has completed.
        if (resumeTest && !resumed && frame.Type == "checkpoint" && frame.Run is { ProviderCalls: >= 1 }) Volatile.Write(ref cancellationArmed, 1);
    }
    finally { recordingGate.Release(); }
}
async Task Save(string name, object value)
{
    var text = JsonSerializer.Serialize(value, json);
    if (text.Contains(key, StringComparison.Ordinal)) throw new InvalidOperationException("Secret detected; refusing to persist evidence.");
    await File.WriteAllTextAsync(Path.Combine(output, name), text);
}
static WorkflowModel Baseline() => new()
{
    Id = "evaluation-existing", Name = "Existing approval", InitialEventId = 1,
    FlowNodes = [new() { Id = 1, Type = "startEvent", Name = "Start" }, new() { Id = 2, Type = "userTask", Name = "Review", Roles = ["manager"] }, new() { Id = 3, Type = "endEvent", Name = "Approved" }],
    SequenceFlows = [new() { Id = 1, SourceRef = 1, TargetRef = 2 }, new() { Id = 2, SourceRef = 2, TargetRef = 3, Name = "Approve" }]
};
static bool CheckSimple(AiTurnResultDto? result, bool modification, string policy, List<object> warnings)
{
    if (result?.Definition is not { } definition) return false;
    var model = JsonSerializer.Deserialize<WorkflowModel>(definition, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    var review = model.FlowNodes.FirstOrDefault(node => node.Name == "Review");
    var approved = model.FlowNodes.FirstOrDefault(node => node.Name == "Approved");
    var rejected = model.FlowNodes.FirstOrDefault(node => node.Name == "Rejected");
    bool Label(string? actual, string expected, bool preserve = false)
    {
        if (actual == expected) return true;
        if (!preserve && policy == "functional-v2" && actual == (expected == "Approve" ? "Approved" : "Rejected"))
        { warnings.Add(new { severity = "warning", expected, actual }); return true; }
        return false;
    }
    bool Preserved()
    {
        var baseline = Baseline();
        var before = JsonSerializer.SerializeToNode(baseline)!.AsObject();
        var after = JsonSerializer.SerializeToNode(model)!.AsObject();
        foreach (var name in new[] { "flowNodes", "sequenceFlows" }) { before.Remove(name); after.Remove(name); }
        return JsonNode.DeepEquals(before, after)
            && baseline.FlowNodes.All(node => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(node), JsonSerializer.SerializeToNode(model.FlowNodes.FirstOrDefault(next => next.Id == node.Id))))
            && baseline.SequenceFlows.All(flow => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(flow), JsonSerializer.SerializeToNode(model.SequenceFlows.FirstOrDefault(next => next.Id == flow.Id))));
    }
    return result.Kind == "proposal" && result.Validation.IsValid && result.Validation.CanSave && result.Validation.CanPublish
        && review is { Type: "userTask" } && review.Roles.SequenceEqual(["manager"]) && string.IsNullOrEmpty(review.RolesVariable)
        && approved?.Type == "endEvent" && rejected?.Type == "endEvent"
        && model.FlowNodes.Count == 4 && model.SequenceFlows.Count == 3
        && model.Variables.Count == 0 && model.FlowNodes.Any(node => node.Id == model.InitialEventId && node.Name == "Start" && node.Type == "startEvent")
        && (modification || model.Name == "Simple approval")
        && model.SequenceFlows.Any(flow => flow.SourceRef == review.Id && flow.TargetRef == approved.Id && Label(flow.Name, "Approve", modification))
        && model.SequenceFlows.Any(flow => flow.SourceRef == review.Id && flow.TargetRef == rejected.Id && Label(flow.Name, "Reject"))
        && model.SequenceFlows.All(flow => flow.IsSelectable && !flow.IsDefault && string.IsNullOrEmpty(flow.Condition) && string.IsNullOrEmpty(flow.RolesVariable) && flow.Roles.All(role => role == "manager"))
        && (!modification || model.Id == "evaluation-existing" && model.Name == "Existing approval" && review.Id == 2 && approved.Id == 3 && model.InitialEventId == 1
            && model.SequenceFlows.Any(flow => flow.Id == 1 && flow.SourceRef == 1 && flow.TargetRef == 2)
            && model.SequenceFlows.Any(flow => flow.Id == 2 && flow.SourceRef == 2 && flow.TargetRef == 3 && flow.Name == "Approve") && Preserved());
}

sealed class EvaluationDiagnostics : IDisposable
{
    private readonly bool enabled;
    private readonly List<object> failures = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    public EvaluationDiagnostics(bool enabled)
    {
        this.enabled = enabled;
        if (enabled) AppDomain.CurrentDomain.FirstChanceException += Observe;
    }
    private void Observe(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
    {
        if (args.Exception is not (KeyNotFoundException or JsonException or InvalidOperationException or WorkflowDomainException or WorkflowAiException or WorkflowAiStopException)) return;
        var frames = new StackTrace(args.Exception, true).GetFrames()
            .Where(frame => frame.GetMethod()?.DeclaringType?.Namespace?.StartsWith("Flowbit.", StringComparison.Ordinal) == true)
            .Take(8).Select(frame => new { type = frame.GetMethod()!.DeclaringType!.FullName, method = frame.GetMethod()!.Name, line = frame.GetFileLineNumber() }).ToArray();
        if (frames.Length == 0) return;
        lock (failures)
            if (failures.Count < 100) failures.Add(new { seconds = clock.Elapsed.TotalSeconds, exception = args.Exception.GetType().Name, frames });
    }
    public object[] Snapshot() { lock (failures) return failures.ToArray(); }
    public void Dispose() { if (enabled) AppDomain.CurrentDomain.FirstChanceException -= Observe; }
}

/// <summary>
/// The transport under Flowbit's capture handler. It counts and cancels real HTTP attempts and records allowlisted metadata only: status,
/// finish reason, token usage and the names of the tools called. Request conversations are kept only as hashes, to prove that Continue
/// resends the cancelled request.
/// </summary>
sealed class ObservedHttpHandler(Action onAttempt) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    private static readonly string[] ToolNames = ["get_files_list", "get_file", "save_append_file", "edit_file", "validate_workflow", "validate_item"];
    private int attempts, cancelledAttempts, cancelTarget, resumeStart;
    public int Attempts => Volatile.Read(ref attempts);
    public int CancelledAttempts => Volatile.Read(ref cancelledAttempts);
    public ConcurrentQueue<object> Calls { get; } = new();
    public string? CancelledRequest { get; private set; }
    public string? FirstResumedRequest { get; private set; }
    public void MarkNextAsCancelTarget() => Volatile.Write(ref cancelTarget, 1);
    public void MarkResumeStart() => Volatile.Write(ref resumeStart, 1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var number = Interlocked.Increment(ref attempts);
        var conversation = request.Content is null ? null : Conversation(await request.Content.ReadAsByteArrayAsync(cancellationToken));
        if (Interlocked.CompareExchange(ref resumeStart, 0, 1) == 1) FirstResumedRequest = conversation;
        onAttempt();
        var target = Interlocked.CompareExchange(ref cancelTarget, 0, 1) == 1;
        var clock = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            object? completion = null;
            if (response.IsSuccessStatusCode)
            {
                await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, cancellationToken);
                completion = Completion(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            }
            Calls.Enqueue(new { number, seconds = clock.Elapsed.TotalSeconds, status = (int)response.StatusCode, completion });
            return response;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref cancelledAttempts);
            if (target) CancelledRequest = conversation;
            Calls.Enqueue(new { number, seconds = clock.Elapsed.TotalSeconds, status = 0, cancelled = true });
            throw;
        }
    }

    private static object? Completion(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var choice = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 ? choices[0] : default;
            string? finish = choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
            var tools = choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("message", out var message) && message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array
                ? calls.EnumerateArray().Select(call => call.TryGetProperty("function", out var function) && function.TryGetProperty("name", out var name)
                    && ToolNames.Contains(name.GetString()) ? name.GetString()! : "other").ToArray()
                : [];
            var usage = root.TryGetProperty("usage", out var counts) && counts.ValueKind == JsonValueKind.Object ? counts : default;
            return new
            {
                finishReason = finish, tools,
                promptTokens = Count(usage, "prompt_tokens"), completionTokens = Count(usage, "completion_tokens"),
                cachedTokens = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens_details", out var details) ? Count(details, "cached_tokens") : null
            };
        }
        catch (JsonException) { return new { finishReason = "not-json" }; }
        static long? Count(JsonElement owner, string name) =>
            owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) ? count : null;
    }

    /// <summary>A hash of the request's messages reduced to roles, text, tool calls, tool results and reasoning, so a restored session
    /// that differs only in serialization details compares equal.</summary>
    private static string? Conversation(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var text = new StringBuilder();
            foreach (var message in document.RootElement.GetProperty("messages").EnumerateArray())
            {
                text.Append(message.GetProperty("role").GetString()).Append('\u001f');
                if (message.TryGetProperty("content", out var content))
                    text.Append(content.ValueKind == JsonValueKind.String ? content.GetString()
                        : content.ValueKind == JsonValueKind.Array ? string.Concat(content.EnumerateArray().Select(part => part.TryGetProperty("text", out var item) ? item.GetString() : "")) : "");
                if (message.TryGetProperty("tool_call_id", out var callId)) text.Append('\u001f').Append(callId.GetString());
                if (message.TryGetProperty("tool_calls", out var calls))
                    foreach (var call in calls.EnumerateArray())
                    {
                        var function = call.GetProperty("function");
                        using var arguments = JsonDocument.Parse(function.GetProperty("arguments").GetString()!);
                        text.Append('\u001f').Append(call.GetProperty("id").GetString()).Append(':').Append(function.GetProperty("name").GetString())
                            .Append(':').Append(JsonSerializer.Serialize(arguments.RootElement));
                    }
                if (message.TryGetProperty("reasoning_content", out var reasoning)) text.Append('\u001f').Append(reasoning.GetString());
                text.Append('\u001e');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
}

sealed class EvaluationTraces : IDisposable
{
    private readonly ConcurrentQueue<object> spans = new();
    private readonly ActivityListener listener;
    public EvaluationTraces()
    {
        listener = new()
        {
            ShouldListenTo = source => source.Name == "Flowbit.Ai.Authoring",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (spans.Count < 10_000) spans.Enqueue(new
                {
                    operation = activity.OperationName, traceId = activity.TraceId.ToString(),
                    spanId = activity.SpanId.ToString(), parentSpanId = activity.ParentSpanId.ToString(),
                    startedUtc = activity.StartTimeUtc, seconds = activity.Duration.TotalSeconds,
                    tags = activity.TagObjects.ToDictionary(pair => pair.Key, pair => pair.Value)
                });
            }
        };
        ActivitySource.AddActivityListener(listener);
    }
    public object[] Snapshot() => spans.ToArray();
    public void Dispose() => listener.Dispose();
}
