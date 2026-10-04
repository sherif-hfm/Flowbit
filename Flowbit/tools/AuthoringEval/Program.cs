using System.Diagnostics;
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

// This tool runs synthetic authoring only. It never applies, saves, publishes or executes a workflow.
var arguments = args.Chunk(2).ToDictionary(pair => pair[0].TrimStart('-'), pair => pair.Length == 2 ? pair[1] : throw new ArgumentException("Use --name value pairs."));
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var variant = arguments.GetValueOrDefault("variant", "current");
var effort = arguments.GetValueOrDefault("effort", "max");
var fixture = arguments.GetValueOrDefault("fixture", "complex");
var policy = arguments.GetValueOrDefault("policy", "strict-v1");
var review = bool.Parse(arguments.GetValueOrDefault("review", "false"));
var analysisWorkers = int.Parse(arguments.GetValueOrDefault("analysis-workers", "2"), System.Globalization.CultureInfo.InvariantCulture);
if (analysisWorkers is < 1 or > 2) throw new ArgumentException("Analysis workers must be 1 or 2.");
if (policy is not ("strict-v1" or "functional-v2")) throw new ArgumentException("Unknown acceptance policy.");
var labelWarnings = new List<object>();
var timeoutSeconds = int.Parse(arguments.GetValueOrDefault("timeout-seconds", "300"), System.Globalization.CultureInfo.InvariantCulture);
if (timeoutSeconds is < 30 or > AiAuthoringLimits.MaxRunTimeoutSeconds) throw new ArgumentException("Timeout must be between 30 and 3600 seconds.");
var output = Path.GetFullPath(arguments["output"]);
Directory.CreateDirectory(output);
if (arguments.TryGetValue("check-only", out var checkFile))
{
    var candidate = JsonSerializer.Deserialize<AiTurnResultDto>(await File.ReadAllTextAsync(checkFile), json);
    var checkPassed = fixture is "simple" or "modify" && CheckSimple(candidate, fixture == "modify", policy, labelWarnings);
    await File.WriteAllTextAsync(Path.Combine(output, "acceptance.json"), JsonSerializer.Serialize(new { policy, passed = checkPassed, labelWarnings, offline = true }, json));
    return checkPassed ? 0 : 1;
}
if (variant is not ("current" or "optimized")) throw new ArgumentException("Variant must be current or optimized.");
if (fixture is not ("simple" or "modify" or "complex")) throw new ArgumentException("Fixture must be simple, modify or complex.");
var key = (await File.ReadAllTextAsync(arguments["key-file"])).Trim();
using var diagnostics = new EvaluationDiagnostics(arguments.GetValueOrDefault("diagnostics") == "true");
using var traces = new EvaluationTraces();
var options = new WorkflowAiOptions
{
    ExecutionVariant = variant, RunTimeoutSeconds = timeoutSeconds, RequestTimeoutSeconds = Math.Min(180, timeoutSeconds),
    RequirementsReviewEnabled = review, MaxParallelAnalysisCalls = analysisWorkers,
    OpenCodeModels = ["glm-5.3-flash"], OpenCodeReasoningEfforts = new() { ["glm-5.3-flash"] = effort },
    ModelProfiles = new() { ["glm-5.3-flash"] = new() { InitialOutputTokens = 16384, MaxOutputTokens = 32768, ContextTokens = 65536 } }
};
Action? onHttpAttempt = null;
using var transport = new ObservedHttpHandler(() => onHttpAttempt?.Invoke());
using var http = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
var observed = new ObservedProvider(new OpenCodeGoProvider(http, options));
using var gate = new WorkflowAiConcurrencyGate(options);
var knowledge = new AuthoringKnowledge(arguments["package"]);
var service = new WorkflowAiAuthoringService([observed], knowledge,
    new WorkflowDefinitionValidator(new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance), new ServiceTaskOptions()),
    new WorkflowDefinitionReadinessChecker(durableProcessingOptions: new DurableProcessingOptions { PublicationEnabled = true }), options, gate);
var clock = Stopwatch.StartNew();
var inputPath = Path.Combine(AppContext.BaseDirectory, "fixtures", fixture + ".txt");
if (!File.Exists(inputPath)) inputPath = Path.Combine(arguments["fixtures"], fixture + ".txt");
var request = new AiTurnRequestDto
{
    ModelId = "glm-5.3-flash", ConversationId = Guid.NewGuid().ToString(), SnapshotId = "synthetic-evaluation",
    Message = await File.ReadAllTextAsync(inputPath),
    CurrentWorkflow = fixture == "modify" ? JsonSerializer.SerializeToElement(Baseline(), json) : null
};
using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
using var cancel = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
var events = new ConcurrentQueue<object>();
AiCheckpointDto? checkpoint = null;
AiTurnResultDto? result = null;
string? failure = null;
bool resumed = false;
int cancellationArmed = 0, cancellationIssued = 0;
bool resumeVerified = false;
using var recordingGate = new SemaphoreSlim(1);
AiRunSummaryDto? lastRun = null;
AiCheckpointDto? cancelledCheckpoint = null;
onHttpAttempt = () =>
{
    if (Volatile.Read(ref cancellationArmed) != 0 && Interlocked.CompareExchange(ref cancellationIssued, 1, 0) == 0)
    {
        // Cancel a real in-flight HTTP attempt, not the progress callback before transport starts.
        cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
    }
};
try
{
    try { result = await service.RunAsync(request, key, Observe, cancel.Token); }
    catch (OperationCanceledException) when (arguments.GetValueOrDefault("resume-test") == "true" && !overall.IsCancellationRequested && checkpoint is { Revision: > 0 })
    {
        resumed = true;
        cancelledCheckpoint = checkpoint;
        result = await service.RunAsync(request with { Checkpoint = checkpoint }, key, Observe, overall.Token);
    }
}
catch (WorkflowAiException error) { failure = error.Code; }
catch (OperationCanceledException) { failure = "evaluation_deadline"; }
if (result is not null) await Save("result.json", result);
var passed = result?.Kind == "proposal" && result.Validation.IsValid && result.Validation.CanSave && result.Validation.CanPublish
    && clock.Elapsed.TotalSeconds <= timeoutSeconds;
if (fixture is "simple" or "modify") passed &= CheckSimple(result, fixture == "modify", policy, labelWarnings);
if (arguments.GetValueOrDefault("resume-test") == "true") passed &= resumed && resumeVerified && transport.CancelledAttempts > 0;
await Save("evidence.json", new { variant, effort, fixture, policy, review, analysisWorkers, passed, failure, labelWarnings, resumed, resumeVerified, transport.Attempts, transport.CancelledAttempts, seconds = clock.Elapsed.TotalSeconds,
    model = request.ModelId, endpoint = options.OpenCodeBaseUrl, knowledge.ContractHash, limits = new { options.RunTimeoutSeconds, options.RequestTimeoutSeconds, options.MaxProviderCalls, options.MaxRunOutputTokens },
    firstEditSeconds = lastRun?.FirstEditSeconds, run = lastRun, events, observed.Calls, diagnostics = diagnostics.Snapshot() });
await Save("trace.json", traces.Snapshot());
return passed ? 0 : 1;

async Task Observe(AiRunEventDto frame, CancellationToken token)
{
    await recordingGate.WaitAsync(token);
    try
    {
        if (frame.Checkpoint is { } saved) checkpoint = saved;
        if (frame.Run is { } run) lastRun = run;
        if (resumed && frame.Stage == "resuming" && cancelledCheckpoint is { } previous && frame.Checkpoint is { } restored)
            resumeVerified = restored.Revision == previous.Revision && restored.Draft.GetRawText() == previous.Draft.GetRawText()
                && restored.InputHash == previous.InputHash && restored.ContractHash == previous.ContractHash
                && restored.ExecutionVariant == previous.ExecutionVariant && restored.ReasoningEffort == previous.ReasoningEffort
                && restored.Batches.SequenceEqual(previous.Batches) && restored.ContextReads.SequenceEqual(previous.ContextReads);
        var item = new { frame.Type, frame.Stage, frame.Code, frame.Sequence, frame.Run, revision = frame.Checkpoint?.Revision, kind = frame.Result?.Kind };
        events.Enqueue(item);
        await File.AppendAllTextAsync(Path.Combine(output, "progress.ndjson"), JsonSerializer.Serialize(item) + Environment.NewLine, token);
        Console.WriteLine(JsonSerializer.Serialize(item));
        if (!resumed && arguments.GetValueOrDefault("resume-test") == "true" && checkpoint is { Revision: > 0 } && frame.Stage == "generating") Volatile.Write(ref cancellationArmed, 1);
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
sealed class ObservedProvider(IAiWorkflowProvider inner) : IAiWorkflowProvider
{
    public AiProviderDto Descriptor => inner.Descriptor;
    public bool SupportsExecution(string variant) => inner.SupportsExecution(variant);
    public ConcurrentQueue<object> Calls { get; } = new();
    public Task<AiCompletion> CompleteAsync(string model, string conversation, IReadOnlyList<AiChatMessageDto> messages, string key, int tokens, CancellationToken ct) =>
        CompleteAsync(model, conversation, messages, key, tokens, new("current", "max"), ct);
    public Task<AiCompletion> CompleteAsync(string model, string conversation, IReadOnlyList<AiChatMessageDto> messages, string key, int tokens, AiExecutionSettings execution, CancellationToken ct)
        => ObserveAsync(() => inner.CompleteAsync(model, conversation, messages, key, tokens, execution, ct), tokens);

    private async Task<AiCompletion> ObserveAsync(Func<Task<AiCompletion>> complete, int tokens)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var response = await complete();
            // Only allowlisted shape metadata: never command values, prompts or reasoning.
            object? command = null;
            if (response.FinishReason == "stop")
            {
                try
                {
                    using var parsed = JsonDocument.Parse(response.Content);
                    var root = parsed.RootElement;
                    var kind = root.TryGetProperty("kind", out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (kind is "read" or "edit" or "finish" or "clarification" or "validate" or "proposal" or "analysis" or "review")
                        command = new { kind, operations = Count("operations"), reads = Count("reads"), sourceReferences = Count("sourceReferences") };
                    int? Count(string name) => root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.GetArrayLength() : null;
                }
                catch (JsonException) { }
            }
            Calls.Enqueue(new { seconds = clock.Elapsed.TotalSeconds, response.FinishReason, response.InputTokens, response.OutputTokens, allowance = tokens, command });
            return response;
        }
        catch (WorkflowAiException error) { Calls.Enqueue(new { seconds = clock.Elapsed.TotalSeconds, error.Code, allowance = tokens }); throw; }
        catch (OperationCanceledException) { Calls.Enqueue(new { seconds = clock.Elapsed.TotalSeconds, Code = "cancelled", allowance = tokens }); throw; }
    }
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
        if (args.Exception is not (KeyNotFoundException or JsonException or InvalidOperationException or WorkflowDomainException or WorkflowAiException)) return;
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

sealed class ObservedHttpHandler(Action onAttempt) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    private int attempts, cancelledAttempts;
    public int Attempts => Volatile.Read(ref attempts);
    public int CancelledAttempts => Volatile.Read(ref cancelledAttempts);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref attempts); onAttempt();
        try { return await base.SendAsync(request, cancellationToken); }
        catch (OperationCanceledException) { Interlocked.Increment(ref cancelledAttempts); throw; }
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
