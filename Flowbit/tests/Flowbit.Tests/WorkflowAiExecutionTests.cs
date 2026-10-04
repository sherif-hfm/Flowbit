using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Infrastructure.Ai;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Ai;
using Flowbit.Service.Authoring;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed partial class WorkflowAiExecutionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Finish = """{"kind":"finish","message":"Complete"}""";
    private const string Edit = """{"kind":"edit","batchId":"rename","baseRevision":0,"operations":[{"op":"set","target":"workflow","path":"/name","value":"Recovered"}]}""";
    private const string Key = "synthetic-execution-key";

    public static IEnumerable<object[]> MalformedCommands()
    {
        var cases = new (string Command, string Field)[]
        {
            ("""{"kind":"read"}""", "reads array"),
            ("""{"kind":"read","reads":[{"resource":"rule"}]}""", "kind"),
            ("""{"kind":"read","reads":[{"kind":"reference"}]}""", "resource"),
            ("""{"kind":"read","reads":[{"kind":"draft"}]}""", "target"),
            ("""{"kind":"read","reads":[{"kind":"draft","target":"node"}]}""", "id"),
            ("""{"kind":"read","reads":[{"kind":"draft","target":"workflow","count":"all"}]}""", "count"),
            ("""{"kind":"edit","batchId":"bad","operations":[]}""", "baseRevision"),
            ("""{"kind":"edit","batchId":"bad","baseRevision":"0","operations":[]}""", "baseRevision"),
            ("""{"kind":"edit","batchId":"bad","baseRevision":0}""", "operations array")
        };
        foreach (var variant in new[] { "current", "optimized" })
            foreach (var (command, field) in cases) yield return [variant, command, field];
    }

    [Theory]
    [MemberData(nameof(MalformedCommands))]
    public async Task MissingOrMistypedToolArgumentsReceiveActionableRepairWithoutChangingDraft(string variant, string malformed, string field)
    {
        using var handler = new Handler((index, body) =>
        {
            if (index == 1)
            {
                var context = Payload(body);
                Assert.Equal(0, context.GetProperty("revision").GetInt64());
                Assert.Equal("Original", context.GetProperty("currentWorkflow").GetProperty("name").GetString());
                var feedback = context.GetProperty("observations").ToString();
                Assert.Contains(field, feedback);
                Assert.DoesNotContain("given key", feedback, StringComparison.OrdinalIgnoreCase);
            }
            return Reply(index == 0 ? malformed : Finish, variant);
        });
        using var client = new HttpClient(handler);
        var options = Options(variant);
        var result = await Service(new OpenCodeGoProvider(client, options), options).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(0, result.Run!.AcceptedBatches);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("optimized")]
    public async Task AddingRejectionToExistingWorkflowFinishesWithoutChangingOriginalNodes(string variant)
    {
        const string add = """{"kind":"edit","batchId":"add-rejection","baseRevision":0,"operations":[{"op":"create","target":"node","id":4,"value":{"id":4,"name":"Rejected","type":"endEvent"}},{"op":"create","target":"flow","id":3,"value":{"id":3,"name":"Reject","sourceRef":2,"targetRef":4}}]}""";
        using var handler = new Handler((index, _) => Reply(index == 0 ? add : Finish, variant));
        using var client = new HttpClient(handler);
        var options = Options(variant);
        var result = await Service(new OpenCodeGoProvider(client, options), options).TurnAsync(Request() with { Message = "Add a Reject action to a new Rejected end event; preserve the existing workflow." }, Key, CancellationToken.None);
        Assert.True(result.Kind == "proposal", string.Join("; ", result.Validation.Errors));
        Assert.Equal(2, handler.Bodies.Count);
        var model = JsonSerializer.Deserialize<WorkflowModel>(result.Definition!.Value, Json)!;
        Assert.Equal(4, model.FlowNodes.Count);
        Assert.Equal(3, model.SequenceFlows.Count);
        Assert.All(Baseline().FlowNodes, node => Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(node), JsonSerializer.SerializeToNode(model.FlowNodes.Single(next => next.Id == node.Id)))));
        Assert.Contains(model.SequenceFlows, flow => flow.Id == 3 && flow.Name == "Reject" && flow.SourceRef == 2 && flow.TargetRef == 4);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("optimized")]
    public async Task FinishRepairsAnUnreachableRequiredStepBeforeReturningProposal(string variant)
    {
        const string add = """{"kind":"edit","batchId":"add-vote","baseRevision":0,"operations":[{"op":"create","target":"node","id":4,"value":{"id":4,"name":"Committee vote","type":"userTask"}},{"op":"create","target":"flow","id":3,"value":{"id":3,"name":"Approve","sourceRef":4,"targetRef":3}}]}""";
        const string repair = """{"kind":"edit","batchId":"wire-vote","baseRevision":1,"operations":[{"op":"set","target":"flow","id":2,"path":"/targetRef","value":4}]}""";
        using var handler = new Handler((index, body) =>
        {
            if (index == 2)
            {
                Assert.Contains("Unreachable draft nodes", Payload(body).GetProperty("observations").ToString());
                Assert.Contains("Committee vote", Payload(body).GetProperty("observations").ToString());
            }
            return Reply(index switch { 0 => add, 1 => Finish, 2 => repair, _ => Finish }, variant);
        });
        using var client = new HttpClient(handler);
        var options = Options(variant);
        var result = await Service(new OpenCodeGoProvider(client, options), options).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal(4, handler.Bodies.Count);
        var model = JsonSerializer.Deserialize<WorkflowModel>(result.Definition!.Value, Json)!;
        Assert.Equal(4, model.SequenceFlows.Single(flow => flow.Id == 2).TargetRef);
    }

    [Fact]
    public void CompletionReachabilityPreservesExistingIslandsButRejectsNewIslandsAndDisconnectedOldWork()
    {
        var original = Baseline();
        original.FlowNodes.Add(new() { Id = 10, Type = "userTask", Name = "Existing unused work" });
        var candidate = JsonSerializer.Deserialize<WorkflowModel>(JsonSerializer.Serialize(original, Json), Json)!;
        WorkflowAiAuthoringService.ValidateProposalReachability(candidate, original);
        candidate.SequenceFlows.Single(flow => flow.Id == 1).TargetRef = 3;
        Assert.Contains("#2", Assert.Throws<JsonException>(() => WorkflowAiAuthoringService.ValidateProposalReachability(candidate, original)).Message);
        candidate.SequenceFlows.Single(flow => flow.Id == 1).TargetRef = 2;
        candidate.FlowNodes.Add(new() { Id = 11, Type = "userTask", Name = "New island" });
        candidate.SequenceFlows.Add(new() { Id = 11, SourceRef = 10, TargetRef = 11 });
        candidate.SequenceFlows.Add(new() { Id = 12, SourceRef = 11, TargetRef = 10 });
        Assert.Contains("#11", Assert.Throws<JsonException>(() => WorkflowAiAuthoringService.ValidateProposalReachability(candidate, original)).Message);
        Assert.Throws<JsonException>(() => WorkflowAiAuthoringService.ValidateProposalReachability(candidate, null));
    }

    [Fact]
    public void CompletionReachabilityRecognizesMessageTimerStartsAndAttachedBoundaryRoutes()
    {
        var model = Baseline();
        model.InitialEventId = null;
        model.FlowNodes.Single(node => node.Id == 1).Type = "messageStartEvent";
        model.FlowNodes.AddRange([new() { Id = 4, Type = "timerStartEvent" }, new() { Id = 5, Type = "timerBoundaryEvent", AttachedToRef = 2 }, new() { Id = 6, Type = "endEvent" }]);
        model.SequenceFlows.AddRange([new() { Id = 3, SourceRef = 4, TargetRef = 2 }, new() { Id = 4, SourceRef = 5, TargetRef = 6 }]);
        WorkflowAiAuthoringService.ValidateProposalReachability(model, null);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("optimized")]
    public async Task SharedKernel_DiscardsTruncatedBatchAndDeduplicatesAcceptedReceipt(string variant)
    {
        var frames = new List<AiRunEventDto>();
        using var handler = new Handler((index, _) => Reply(index < 3 ? Edit : Finish, variant, index == 0));
        using var client = new HttpClient(handler);
        var options = Options(variant);
        var result = await Service(new OpenCodeGoProvider(client, options), options).RunAsync(Request(), Key,
            (frame, _) => { frames.Add(frame); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal("Recovered", result.Definition!.Value.GetProperty("name").GetString());
        Assert.Equal(4, handler.Bodies.Count); // A successful finish MUST NOT call the model again.
        Assert.Equal(1, result.Run!.AcceptedBatches);
        Assert.Equal(new long[] { 0, 0, 1 }, frames.Where(frame => frame.Type == "checkpoint").Select(frame => frame.Checkpoint!.Revision));
        Assert.All(handler.Bodies, body => Assert.DoesNotContain(Key, body));
        Assert.All(frames.Where(frame => frame.Checkpoint is not null), frame => Assert.Equal(2, frame.Checkpoint!.Version));

    }

    [Theory]
    [InlineData("current")]
    [InlineData("optimized")]
    public async Task FinishRunsLocalValidationAndReturnsTargetedRepairFeedback(string variant)
    {
        const string remove = """{"kind":"edit","batchId":"remove","baseRevision":0,"operations":[{"op":"delete","target":"flow","id":2}]}""";
        const string repair = """{"kind":"edit","batchId":"repair","baseRevision":1,"operations":[{"op":"create","target":"flow","id":2,"value":{"id":2,"sourceRef":2,"targetRef":3,"name":"Approve"}}]}""";
        using var handler = new Handler((index, body) =>
        {
            if (index == 2) Assert.Contains("validation", body, StringComparison.OrdinalIgnoreCase);
            return Reply(index switch { 0 => remove, 1 => Finish, 2 => repair, _ => Finish }, variant);
        });
        using var client = new HttpClient(handler);
        var options = Options(variant);
        var result = await Service(new OpenCodeGoProvider(client, options), options).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.True(result.Validation.IsValid);
        Assert.Equal(4, handler.Bodies.Count);
    }

    [Theory]
    [InlineData("variant")]
    [InlineData("reasoning")]
    [InlineData("profile")]
    public async Task VersionTwoCheckpointRejectsConfigurationChangesBeforeTransport(string setting)
    {
        using var handler = new Handler((_, _) => Reply(Edit, "optimized"));
        using var client = new HttpClient(handler);
        var options = Options("optimized"); options.MaxProviderCalls = 1;
        var service = Service(new OpenCodeGoProvider(client, options), options);
        var request = Request();
        var paused = await service.TurnAsync(request, Key, CancellationToken.None);
        Assert.Equal(2, paused.Checkpoint!.Version);
        if (setting == "variant") options.ExecutionVariant = "current";
        else if (setting == "reasoning") options.OpenCodeReasoningEfforts["glm-5.3-flash"] = "high";
        else options.InitialOutputTokens = 4096;
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => service.TurnAsync(request with { Checkpoint = paused.Checkpoint }, Key, CancellationToken.None));
        Assert.Equal("checkpoint_configuration_changed", error.Code);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task RemovedExecutionConfigurationFailsBeforeTransport()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No request should be sent."));
        using var client = new HttpClient(handler);
        var options = Options("agent-framework");
        var provider = new OpenCodeGoProvider(client, options);
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => Service(provider, options).TurnAsync(Request(), Key, CancellationToken.None));
        Assert.Equal("provider_configuration", error.Code);
        Assert.Equal(503, error.StatusCode);
        Assert.Contains("current or optimized", error.Message);
        Assert.False(provider.SupportsExecution("agent-framework"));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("optimized")]
    public async Task RemovedExecutionCheckpointFailsBeforeAnotherTransport(string variant)
    {
        using var handler = new Handler((_, _) => Reply(Edit, variant));
        using var client = new HttpClient(handler);
        var options = Options(variant); options.MaxProviderCalls = 1;
        var service = Service(new OpenCodeGoProvider(client, options), options);
        var paused = await service.TurnAsync(Request(), Key, CancellationToken.None);
        var oldCheckpoint = paused.Checkpoint! with { ExecutionVariant = "agent-framework" };
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => service.TurnAsync(Request() with { Checkpoint = oldCheckpoint }, Key, CancellationToken.None));
        Assert.Equal("checkpoint_configuration_changed", error.Code);
        Assert.Equal(409, error.StatusCode);
        Assert.Single(handler.Bodies);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("optimized")]
    public async Task CustomExecutionContinuesTheExactDraftWithFreshAccounting(string variant)
    {
        using var handler = new Handler((index, body) =>
        {
            if (index == 1)
            {
                Assert.Equal(1, Payload(body).GetProperty("revision").GetInt64());
                Assert.Equal("Recovered", Payload(body).GetProperty("currentWorkflow").GetProperty("name").GetString());
            }
            return Reply(index == 0 ? Edit : Finish, variant);
        });
        using var client = new HttpClient(handler);
        var options = Options(variant); options.MaxProviderCalls = 1;
        var service = Service(new OpenCodeGoProvider(client, options), options);
        var paused = await service.TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("paused", paused.Kind);
        AiCheckpointDto? restored = null;
        var result = await service.RunAsync(Request() with { Checkpoint = paused.Checkpoint }, Key,
            (frame, _) => { if (frame.Stage == "resuming") restored = frame.Checkpoint; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal("Recovered", result.Definition!.Value.GetProperty("name").GetString());
        Assert.Equal(paused.Checkpoint!.Draft.GetRawText(), restored!.Draft.GetRawText());
        Assert.Equal(paused.Checkpoint.Batches, restored.Batches);
        Assert.Equal(paused.Checkpoint.ContextReads, restored.ContextReads);
        Assert.Equal(variant, result.Run!.ExecutionVariant);
        Assert.Equal(1, result.Run.ProviderCalls);
        Assert.Equal(0, result.Run.AcceptedBatches);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task VersionOneCheckpointKeepsLegacyExecutionWhenServerDefaultChanges()
    {
        using var handler = new Handler((index, _) => Reply(index == 0 ? Edit : Finish, "current"));
        using var client = new HttpClient(handler);
        var options = Options("current"); options.MaxProviderCalls = 1;
        var service = Service(new OpenCodeGoProvider(client, options), options);
        var request = Request();
        var paused = await service.TurnAsync(request, Key, CancellationToken.None);
        options.ExecutionVariant = "optimized";
        var result = await service.TurnAsync(request with { Checkpoint = paused.Checkpoint! with
            { Version = 1, ExecutionVariant = null, ReasoningEffort = null, ModelProfileHash = null } }, Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal("current", result.Run!.ExecutionVariant);
        Assert.All(handler.Bodies, body => Assert.DoesNotContain("tool_choice", body));
    }

    [Theory]
    [InlineData(401, "provider_auth")]
    [InlineData(402, "provider_quota")]
    [InlineData(429, "provider_throttled")]
    [InlineData(503, "provider_unavailable")]
    public async Task ProviderDoesNotAddTransportRetries(int status, string code)
    {
        using var handler = new Handler((_, _) => new((HttpStatusCode)status) { Content = new StringContent("{}") });
        using var client = new HttpClient(handler);
        var provider = new OpenCodeGoProvider(client, Options("optimized"));
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => provider.CompleteAsync("glm-5.3-flash", "session",
            [new("system", "test")], Key, 8192, new("optimized", "low"), CancellationToken.None));
        Assert.Equal(code, error.Code);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task ProviderPropagatesCallerCancellationWithoutRetry()
    {
        using var handler = new BlockingHandler();
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var call = new OpenCodeGoProvider(client, Options("optimized")).CompleteAsync("glm-5.3-flash", "session",
            [new("system", "test")], Key, 8192, new("optimized", "low"), cancellation.Token);
        await handler.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("optimized")]
    public async Task FailedAttemptTimingIsAvailableBeforeBackoffAndFinalPause(string variant)
    {
        using var handler = new Handler((_, _) => new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
        using var client = new HttpClient(handler);
        var options = Options(variant); options.MaxTransportRetries = 0;
        var result = await Service(new OpenCodeGoProvider(client, options), options).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("paused", result.Kind);
        Assert.Single(handler.Bodies);
        Assert.True(result.Run!.LastCallSeconds > 0);
        Assert.True(result.Run.LastCallSeconds <= result.Run.ElapsedSeconds);
    }

    [Fact]
    public async Task OptimizedBatchGrowthRequiresTwoFastAcceptedBatches()
    {
        using var handler = new Handler((index, _) => Reply(index == 0 ? Edit : index < 4
            ? Edit.Replace("rename", "batch" + index).Replace("Recovered", "Name " + index).Replace("\"baseRevision\":0", "\"baseRevision\":" + (index - 1))
            : Finish, "optimized", index == 0));
        using var client = new HttpClient(handler);
        var options = Options("optimized");
        await Service(new OpenCodeGoProvider(client, options), options).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal(new[] { 20, 10, 10, 12, 12 }, handler.Bodies.Select(body => Payload(body).GetProperty("maxOperations").GetInt32()));
    }

    [Theory]
    [InlineData("optimized")]
    public async Task RepeatedTruncationHalvesEachBatchWithoutJumpingStraightToOne(string variant)
    {
        using var handler = new Handler((index, _) => Reply(index < 3 ? Edit : Finish, variant, index < 2));
        using var client = new HttpClient(handler);
        var options = Options(variant);
        var result = await Service(new OpenCodeGoProvider(client, options), options).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal(new[] { 20, 10, 5, 5 }, handler.Bodies.Select(body => Payload(body).GetProperty("maxOperations").GetInt32()));
        Assert.Equal(1, result.Run!.AcceptedBatches);
    }

    [Fact]
    public void SemanticContextPreservesBusinessFieldsBeyondOldCutoffAndDropsOnlyLayout()
    {
        var draft = JsonNode.Parse(JsonSerializer.Serialize(Baseline(), Json))!;
        draft["flowNodes"]![1]!["name"] = new string('r', 16000);
        draft["flowNodes"]![1]!["x"] = 12345;
        draft["flowNodes"]![1]!["multiInstance"] = JsonNode.Parse("{\"mode\":\"parallel\",\"completionEvaluation\":\"afterAll\"}");
        var context = new WorkflowAiContext(new Knowledge(), Request(), true);
        using var document = JsonDocument.Parse(context.Messages(JsonSerializer.SerializeToElement(draft), 0, "", new object[0], 20, 8192, text => text, false).Last().Content);
        var node = document.RootElement.GetProperty("currentWorkflow").GetProperty("flowNodes")[1];
        Assert.False(node.TryGetProperty("x", out _));
        Assert.Equal("manager", node.GetProperty("roles")[0].GetString());
        Assert.Equal("afterAll", node.GetProperty("multiInstance").GetProperty("completionEvaluation").GetString());
    }

    [Fact]
    public void ImmutableReferencesAreDeduplicatedAndSurviveDraftReadsAndEdits()
    {
        var context = new WorkflowAiContext(new Knowledge(), Request(), true);
        var draft = Request().CurrentWorkflow!.Value;
        var read = JsonSerializer.Deserialize<JsonElement>("""[{"kind":"reference","resource":"rule","count":100}]""");
        context.Observe(context.Read(read, draft, text => text));
        context.Observe(context.Read(read, draft, text => text));
        context.Observe(context.Read(JsonSerializer.Deserialize<JsonElement>("""[{"kind":"draft","target":"node","id":2}]"""), draft, text => text));
        context.DraftChanged();
        Assert.Single(context.RetainedReads());
        Assert.Equal(1, context.DuplicateReadCount);
        var message = context.Messages(draft, 1, "", new object[0], 20, 8192, text => text, false).Last().Content;
        Assert.Contains("CANONICAL_RULE", message);
        Assert.DoesNotContain("draft:node", message);
    }

    private static JsonElement Payload(string body)
    {
        using var document = JsonDocument.Parse(body);
        return JsonSerializer.Deserialize<JsonElement>(document.RootElement.GetProperty("messages").EnumerateArray().Last(item => item.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!);
    }
    private static WorkflowAiOptions Options(string variant) => new() { ExecutionVariant = variant, OpenCodeModels = ["glm-5.3-flash"], OpenCodeReasoningEfforts = new() { ["glm-5.3-flash"] = "low" } };
    private static AiTurnRequestDto Request() => new() { ModelId = "glm-5.3-flash", ConversationId = "b1781b3b-88d2-4708-9103-ec7daa7f9ca1", SnapshotId = "snapshot", Message = "Rename this workflow", CurrentWorkflow = JsonSerializer.SerializeToElement(Baseline(), Json) };
    private static WorkflowModel Baseline() => new()
    {
        Id = "existing", Name = "Original", InitialEventId = 1,
        FlowNodes = [new() { Id = 1, Name = "Start", Type = "startEvent" }, new() { Id = 2, Name = "Review", Type = "userTask", Roles = ["manager"] }, new() { Id = 3, Name = "End", Type = "endEvent" }],
        SequenceFlows = [new() { Id = 1, SourceRef = 1, TargetRef = 2 }, new() { Id = 2, SourceRef = 2, TargetRef = 3, Name = "Approve" }]
    };
    private static WorkflowAiAuthoringService Service(IAiWorkflowProvider provider, WorkflowAiOptions options) => new([provider], new Knowledge(),
        new WorkflowDefinitionValidator(new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance), new ServiceTaskOptions()),
        new WorkflowDefinitionReadinessChecker(), options, new WorkflowAiConcurrencyGate(options));
    private static HttpResponseMessage Reply(string command, string variant, bool length = false)
    {
        Assert.Contains(variant, new[] { "current", "optimized" });
        return Response(new { choices = new[] { new { finish_reason = length ? "length" : "stop", message = new { content = command } } }, usage = new { prompt_tokens = 100, completion_tokens = 100 } });
    }
    private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<int, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Contains(Assert.Single(request.Headers.GetValues("x-opencode-session")), new[] { "session", "b1781b3b-88d2-4708-9103-ec7daa7f9ca1" });
            Assert.Equal(Key, request.Headers.Authorization!.Parameter);
            var body = await request.Content!.ReadAsStringAsync(ct); Bodies.Add(body);
            using var payload = JsonDocument.Parse(body);
            Assert.False(payload.RootElement.TryGetProperty("tools", out _));
            Assert.False(payload.RootElement.TryGetProperty("tool_choice", out _));
            Assert.False(payload.RootElement.TryGetProperty("parallel_tool_calls", out _));
            Assert.All(payload.RootElement.GetProperty("messages").EnumerateArray(), message =>
            {
                Assert.NotEqual("tool", message.GetProperty("role").GetString());
                Assert.False(message.TryGetProperty("tool_calls", out _));
                Assert.False(message.TryGetProperty("reasoning_content", out _));
            });
            return respond(Bodies.Count - 1, body);
        }
    }
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; Arrived.SetResult(); await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); }
    }
    private sealed class Knowledge : IAuthoringKnowledge
    {
        public string ContractHash => "test-contract";
        public IReadOnlyDictionary<string, string> Resources { get; } = new Dictionary<string, string> { ["rule"] = "CANONICAL_RULE" };
        public string GetPromptContext(string? query = null) => "test";
        public byte[] GetPackageZip() => [];
    }
}
