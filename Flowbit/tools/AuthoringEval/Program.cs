using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
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
if (policy is not ("strict-v1" or "functional-v2")) throw new ArgumentException("Unknown acceptance policy.");
var labelWarnings = new List<object>();
var output = Path.GetFullPath(arguments["output"]);
Directory.CreateDirectory(output);
if (arguments.TryGetValue("check-only", out var checkFile))
{
    var candidate = JsonSerializer.Deserialize<AiTurnResultDto>(await File.ReadAllTextAsync(checkFile), json);
    var checkPassed = fixture is "simple" or "modify" && CheckSimple(candidate, fixture == "modify", policy, labelWarnings);
    await File.WriteAllTextAsync(Path.Combine(output, "acceptance.json"), JsonSerializer.Serialize(new { policy, passed = checkPassed, labelWarnings, offline = true }, json));
    return checkPassed ? 0 : 1;
}
var key = (await File.ReadAllTextAsync(arguments["key-file"])).Trim();
var options = new WorkflowAiOptions
{
    ExecutionVariant = variant, RunTimeoutSeconds = 300, RequestTimeoutSeconds = 180,
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
if (fixture == "probe")
{
    try
    {
        var completion = await observed.CompleteAsync("glm-5.3-flash", Guid.NewGuid().ToString(),
            [new("system", "You are testing tool compatibility, not generating a workflow."),
             new("user", "Call request_clarification with questions [\"Which department approves?\"] and message \"Capability probe\". Do not read or edit anything.")],
            key, 16384, new(variant, effort), CancellationToken.None);
        var probePassed = completion.FinishReason == "stop" && completion.Content.Contains("clarification", StringComparison.Ordinal)
            && completion.InputTokens.HasValue && completion.OutputTokens.HasValue;
        await Save("probe.json", new { passed = probePassed, completion.FinishReason, completion.InputTokens, completion.OutputTokens, seconds = clock.Elapsed.TotalSeconds, observed.Calls });
        return probePassed ? 0 : 1;
    }
    catch (WorkflowAiException error) { await Save("probe.json", new { passed = false, error.Code, error.StatusCode, observed.Calls }); return 1; }
}
var inputPath = Path.Combine(AppContext.BaseDirectory, "fixtures", fixture + ".txt");
if (!File.Exists(inputPath)) inputPath = Path.Combine(arguments["fixtures"], fixture + ".txt");
var request = new AiTurnRequestDto
{
    ModelId = "glm-5.3-flash", ConversationId = Guid.NewGuid().ToString(), SnapshotId = "synthetic-evaluation",
    Message = await File.ReadAllTextAsync(inputPath),
    CurrentWorkflow = fixture == "modify" ? JsonSerializer.SerializeToElement(Baseline(), json) : null
};
using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(300));
using var cancel = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
var events = new List<object>();
AiCheckpointDto? checkpoint = null;
AiTurnResultDto? result = null;
string? failure = null;
bool resumed = false;
bool cancellationArmed = false, cancellationIssued = false, resumeVerified = false;
AiRunSummaryDto? lastRun = null;
AiCheckpointDto? cancelledCheckpoint = null;
onHttpAttempt = () =>
{
    if (cancellationArmed && !cancellationIssued)
    {
        cancellationIssued = true;
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
    && clock.Elapsed.TotalSeconds <= 300;
if (fixture is "simple" or "modify") passed &= CheckSimple(result, fixture == "modify", policy, labelWarnings);
if (arguments.GetValueOrDefault("resume-test") == "true") passed &= resumed && resumeVerified && transport.CancelledAttempts > 0;
await Save("evidence.json", new { variant, effort, fixture, policy, passed, failure, labelWarnings, resumed, resumeVerified, transport.Attempts, transport.CancelledAttempts, seconds = clock.Elapsed.TotalSeconds,
    model = request.ModelId, endpoint = options.OpenCodeBaseUrl, knowledge.ContractHash, limits = new { options.RunTimeoutSeconds, options.RequestTimeoutSeconds, options.MaxProviderCalls, options.MaxRunOutputTokens },
    firstEditSeconds = lastRun?.FirstEditSeconds, events, observed.Calls });
return passed ? 0 : 1;

async Task Observe(AiRunEventDto frame, CancellationToken token)
{
    if (frame.Checkpoint is { } saved) checkpoint = saved;
    if (frame.Run is { } run) lastRun = run;
    if (resumed && frame.Stage == "resuming" && cancelledCheckpoint is { } previous && frame.Checkpoint is { } restored)
        resumeVerified = restored.Revision == previous.Revision && restored.Draft.GetRawText() == previous.Draft.GetRawText()
            && restored.InputHash == previous.InputHash && restored.ContractHash == previous.ContractHash
            && restored.ExecutionVariant == previous.ExecutionVariant && restored.ReasoningEffort == previous.ReasoningEffort
            && restored.Batches.SequenceEqual(previous.Batches) && restored.ContextReads.SequenceEqual(previous.ContextReads);
    var item = new { frame.Type, frame.Stage, frame.Code, frame.Sequence, frame.Run, revision = frame.Checkpoint?.Revision, kind = frame.Result?.Kind };
    events.Add(item);
    Console.WriteLine(JsonSerializer.Serialize(item));
    if (!resumed && arguments.GetValueOrDefault("resume-test") == "true" && checkpoint is { Revision: > 0 } && frame.Stage == "generating") cancellationArmed = true;
    await Task.CompletedTask;
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
    public List<object> Calls { get; } = [];
    public Task<AiCompletion> CompleteAsync(string model, string conversation, IReadOnlyList<AiChatMessageDto> messages, string key, int tokens, CancellationToken ct) =>
        CompleteAsync(model, conversation, messages, key, tokens, new("current", "max"), ct);
    public async Task<AiCompletion> CompleteAsync(string model, string conversation, IReadOnlyList<AiChatMessageDto> messages, string key, int tokens, AiExecutionSettings execution, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var response = await inner.CompleteAsync(model, conversation, messages, key, tokens, execution, ct);
            Calls.Add(new { seconds = clock.Elapsed.TotalSeconds, response.FinishReason, response.InputTokens, response.OutputTokens, allowance = tokens });
            return response;
        }
        catch (WorkflowAiException error) { Calls.Add(new { seconds = clock.Elapsed.TotalSeconds, error.Code, allowance = tokens }); throw; }
        catch (OperationCanceledException) { Calls.Add(new { seconds = clock.Elapsed.TotalSeconds, Code = "cancelled", allowance = tokens }); throw; }
    }
}

sealed class ObservedHttpHandler(Action onAttempt) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    public int Attempts { get; private set; }
    public int CancelledAttempts { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Attempts++; onAttempt();
        try { return await base.SendAsync(request, cancellationToken); }
        catch (OperationCanceledException) { CancelledAttempts++; throw; }
    }
}
