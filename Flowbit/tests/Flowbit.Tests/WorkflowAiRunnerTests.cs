using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Ai;
using Flowbit.Service.Authoring;
using Flowbit.Service.Services;
using Flowbit.Shared.Authoring;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowAiRunnerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private const string Key = "fake-runner-provider-key";
    private const string Finish = """{"kind":"finish","message":"Review the completed workflow."}""";

    [Fact]
    public async Task Truncation_DiscardsEvenACompleteEditEnvelopeAndRetriesSmallerFromSameRevision()
    {
        var discarded = Edit(0, "discarded", Set("node", 2, "/name", "Must not appear"));
        var accepted = Edit(0, "accepted", Set("workflow", null, "/name", "Recovered"));
        var provider = new ScriptedProvider(new(discarded, "length", OutputTokens: 128), Complete(accepted), Complete(Finish));
        var events = new List<AiRunEventDto>();
        var result = await Service(provider).RunAsync(Request(), Key, Capture(events), CancellationToken.None);

        Assert.Equal("proposal", result.Kind);
        Assert.Equal("Recovered", result.Definition!.Value.GetProperty("name").GetString());
        Assert.Equal("Review", result.Definition.Value.GetProperty("flowNodes")[1].GetProperty("name").GetString());
        Assert.Equal(new long[] { 0, 0, 1 }, events.Where(frame => frame.Type == "checkpoint").Select(frame => frame.Checkpoint!.Revision));
        Assert.Equal(0, Payload(provider.Calls[1]).GetProperty("revision").GetInt64());
        Assert.Equal(10, Payload(provider.Calls[1]).GetProperty("maxOperations").GetInt32());
        Assert.Contains(events, frame => frame.Stage == "recovering");
        Assert.DoesNotContain("Must not appear", Payload(provider.Calls[1]).GetProperty("currentWorkflow").GetRawText());
    }

    [Fact]
    public async Task Context_ReportsRemainingRunBudgetAfterEachCompleteProviderResponse()
    {
        var provider = new ScriptedProvider(Complete(Edit(0, "rename", Set("workflow", null, "/name", "Renamed"))), Complete(Finish));
        var options = new WorkflowAiOptions();
        await Service(provider, options).TurnAsync(Request(), Key, CancellationToken.None);
        var first = Payload(provider.Calls[0]).GetProperty("runBudget");
        var second = Payload(provider.Calls[1]).GetProperty("runBudget");
        Assert.Equal(options.MaxProviderCalls, first.GetProperty("providerCallsRemaining").GetInt32());
        Assert.Equal(options.MaxProviderCalls - 1, second.GetProperty("providerCallsRemaining").GetInt32());
        Assert.Equal(options.MaxRunOutputTokens - 1, second.GetProperty("outputTokensRemaining").GetInt64());
        Assert.InRange(second.GetProperty("secondsRemaining").GetDouble(), 0, options.RunTimeoutSeconds);
    }

    [Fact]
    public async Task ReadPlan_IsCheckpointedWithoutAdvancingDraftRevisionAndSurvivesDeadline()
    {
        const string read = """{"kind":"read","plan":"Requirements inspected; build the missing review branch next.","reads":[{"kind":"source","resource":"requirements","count":100}]}""";
        var provider = new ScriptedProvider(async (call, _, token) =>
        {
            if (call == 0) return Complete(read);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        var events = new List<AiRunEventDto>();
        var result = await Service(provider, new WorkflowAiOptions { RunTimeoutSeconds = 1 }).RunAsync(Request(), Key, Capture(events), CancellationToken.None);
        Assert.Equal("paused", result.Kind);
        Assert.Equal(0, result.Checkpoint!.Revision);
        Assert.Equal("Requirements inspected; build the missing review branch next.", result.Checkpoint.Plan);
        Assert.Contains(events, frame => frame.Type == "checkpoint" && frame.Stage == "reading" && frame.Checkpoint!.Plan == result.Checkpoint.Plan);
    }

    [Theory]
    [InlineData(20, 128, 256, 10, 128)]
    [InlineData(1, 128, 256, 1, 256)]
    public async Task Resume_PreservesBoundedOutputRecoveryHints(int operations, int initial, int maximum, int expectedOperations, int expectedAllowance)
    {
        var options = new WorkflowAiOptions { MaxProviderCalls = 1, MaxOperationsPerBatch = operations, InitialOutputTokens = initial, MaxModelOutputTokens = maximum };
        var request = Request();
        var first = new ScriptedProvider(new AiCompletion("", "length", OutputTokens: initial));
        var paused = await Service(first, options).TurnAsync(request, Key, CancellationToken.None);
        Assert.Equal("paused", paused.Kind);
        Assert.Equal(expectedOperations, paused.Checkpoint!.MaxOperations);
        Assert.Equal(expectedAllowance, paused.Checkpoint.OutputAllowance);
        var second = new ScriptedProvider(Complete(Finish));
        var result = await Service(second, options).TurnAsync(request with { Checkpoint = paused.Checkpoint }, Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal(expectedAllowance, second.Calls.Single().OutputAllowance);
        Assert.Equal(expectedOperations, Payload(second.Calls.Single()).GetProperty("maxOperations").GetInt32());
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(262145, 20)]
    [InlineData(8192, 0)]
    [InlineData(8192, 101)]
    public async Task Resume_RejectsMalformedRecoveryHintsBeforeCallingTheProvider(int allowance, int operations)
    {
        var request = Request();
        var options = new WorkflowAiOptions { MaxProviderCalls = 1 };
        var paused = await Service(new ScriptedProvider(Complete(Edit(0, "rename", Set("workflow", null, "/name", "Draft")))), options).TurnAsync(request, Key, CancellationToken.None);
        var provider = new ScriptedProvider(Complete(Finish));
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => Service(provider, options).TurnAsync(request with
            { Checkpoint = paused.Checkpoint! with { OutputAllowance = allowance, MaxOperations = operations } }, Key, CancellationToken.None));
        Assert.Equal("invalid_checkpoint", error.Code);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task ProviderCallBudget_PausesWithCheckpointAndResumesWithFreshBudgetAgainstSameBaseline()
    {
        var options = new WorkflowAiOptions { MaxProviderCalls = 1 };
        var request = Request();
        var first = new ScriptedProvider(Complete(Edit(0, "rename", Set("workflow", null, "/name", "First completed step"))));
        var paused = await Service(first, options).TurnAsync(request, Key, CancellationToken.None);
        Assert.Equal("paused", paused.Kind);
        Assert.Null(paused.Definition);
        Assert.Equal(1, paused.Checkpoint!.Revision);
        Assert.Equal(1, paused.Run!.ProviderCalls);

        var second = new ScriptedProvider(Complete(Finish));
        var result = await Service(second, options).TurnAsync(request with { Checkpoint = paused.Checkpoint }, Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal("First completed step", result.Definition!.Value.GetProperty("name").GetString());
        Assert.Equal(1, Payload(second.Calls.Single()).GetProperty("revision").GetInt64());
        Assert.Equal(1, result.Run!.ProviderCalls);
        Assert.Equal("AI test", request.CurrentWorkflow!.Value.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("requirements")]
    [InlineData("snapshot")]
    [InlineData("baseline")]
    [InlineData("sources")]
    public async Task Resume_RejectsChangedInputsBeforeSpendingAnotherProviderCall(string changed)
    {
        var request = Request();
        var options = new WorkflowAiOptions { MaxProviderCalls = 1 };
        var paused = await Service(new ScriptedProvider(Complete(Edit(0, "rename", Set("workflow", null, "/name", "Draft")))), options)
            .TurnAsync(request, Key, CancellationToken.None);
        var resumed = request with { Checkpoint = paused.Checkpoint };
        resumed = changed switch
        {
            "requirements" => resumed with { Message = "Different requirements" },
            "snapshot" => resumed with { SnapshotId = "another-snapshot" },
            "baseline" => resumed with { CurrentWorkflow = JsonSerializer.SerializeToElement(Model("Changed baseline"), JsonOptions) },
            _ => resumed with { Sources = [new("new.pdf", 1, "Changed source")] }
        };
        var provider = new ScriptedProvider(Complete(Finish));
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => Service(provider, options).TurnAsync(resumed, Key, CancellationToken.None));
        Assert.Equal("stale_checkpoint", error.Code);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task Resume_NewWorkflowRetainsItsGeneratedIdentity()
    {
        var request = Request() with { CurrentWorkflow = null };
        var options = new WorkflowAiOptions { MaxProviderCalls = 1 };
        var paused = await Service(new ScriptedProvider(Complete(Edit(0, "name", Set("workflow", null, "/name", "New draft")))), options)
            .TurnAsync(request, Key, CancellationToken.None);
        Assert.Equal("paused", paused.Kind);
        var generatedId = paused.Checkpoint!.Draft.GetProperty("id").GetString();
        var provider = new ScriptedProvider(Complete("""{"kind":"clarification","message":"Need one detail","questions":["Who reviews?"]}"""));
        var resumed = await Service(provider, options).TurnAsync(request with { Checkpoint = paused.Checkpoint }, Key, CancellationToken.None);
        Assert.Equal("clarification", resumed.Kind);
        Assert.Equal(generatedId, resumed.Checkpoint!.Draft.GetProperty("id").GetString());
        Assert.Equal(generatedId, Payload(provider.Calls.Single()).GetProperty("currentWorkflow").GetProperty("id").GetString());
    }

    [Fact]
    public async Task MissingUsage_ReservesRequestedOutputAndPausesAtTheRunBudget()
    {
        var options = new WorkflowAiOptions { InitialOutputTokens = 128, MaxRunOutputTokens = 128 };
        var provider = new ScriptedProvider(new AiCompletion(Edit(0, "name", Set("workflow", null, "/name", "Budgeted")), "stop"));
        var events = new List<AiRunEventDto>();
        var result = await Service(provider, options).RunAsync(Request(), Key, Capture(events), CancellationToken.None);
        Assert.Equal("paused", result.Kind);
        Assert.Equal(1, result.Checkpoint!.Revision);
        Assert.True(result.Run!.UsageEstimated);
        Assert.Equal(128, result.Run.OutputTokens);
        Assert.Single(provider.Calls);
        Assert.Contains(events, frame => frame.Type == "paused" && frame.Code == "run_budget");
    }

    [Fact]
    public async Task AuthenticationError_IsTerminalAndDoesNotRetry()
    {
        var provider = new ScriptedProvider((_, _, _) => throw new WorkflowAiException("provider_auth", "Invalid provider credentials.", 401));
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => Service(provider).TurnAsync(Request(), Key, CancellationToken.None));
        Assert.Equal("provider_auth", error.Code);
        Assert.Equal(401, error.StatusCode);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task ExhaustedTransportRetries_PauseWithCompleteEstimatedUsage()
    {
        var provider = new ScriptedProvider((_, _, _) => throw new WorkflowAiException("provider_unavailable", "Busy.", 503) { Retryable = true });
        var options = new WorkflowAiOptions { RetryBaseDelayMilliseconds = 1 };
        var events = new List<AiRunEventDto>();
        var result = await Service(provider, options).RunAsync(Request(), Key, Capture(events), CancellationToken.None);
        Assert.Equal("paused", result.Kind);
        Assert.Equal(3, provider.Calls.Count);
        Assert.Equal(3 * options.InitialOutputTokens, result.Run!.OutputTokens);
        Assert.True(result.Run.UsageEstimated);
        Assert.NotNull(result.Checkpoint);
        Assert.Equal("provider_unavailable", events.Last().Code);
    }

    [Fact]
    public async Task SlowCheckpointEmission_RespectsRunDeadlineAndRetainsCheckpoint()
    {
        var provider = new ScriptedProvider(Complete(Finish));
        var events = new List<AiRunEventDto>();
        async Task Observe(AiRunEventDto frame, CancellationToken token)
        {
            events.Add(frame);
            if (frame.Type == "checkpoint") await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        var result = await Service(provider, new WorkflowAiOptions { RunTimeoutSeconds = 1 })
            .RunAsync(Request(), Key, Observe, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("paused", result.Kind);
        Assert.NotNull(result.Checkpoint);
        Assert.Empty(provider.Calls);
        Assert.Equal("authoring_timeout", events.Last().Code);
    }

    [Fact]
    public async Task FailedEdit_DoesNotReplaceTheCommittedPlan()
    {
        const string accepted = """{"kind":"edit","baseRevision":0,"batchId":"accepted","plan":"Finish review routing","operations":[{"op":"set","target":"workflow","path":"/name","value":"Renamed"}]}""";
        const string rejected = """{"kind":"edit","baseRevision":0,"batchId":"rejected","plan":"All work completed","operations":[{"op":"set","target":"workflow","path":"/name","value":"Wrong"}]}""";
        var provider = new ScriptedProvider(Complete(accepted), Complete(rejected));
        var result = await Service(provider, new WorkflowAiOptions { MaxProviderCalls = 2 }).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("paused", result.Kind);
        Assert.Equal("Finish review routing", result.Checkpoint!.Plan);
        Assert.Equal("Renamed", result.Checkpoint.Draft.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ProviderTimeout_RetriesWithSmallerWorkAndRetainsTheRecoveryCheckpoint()
    {
        var provider = new ScriptedProvider((call, _, _) => call == 0
            ? throw new WorkflowAiException("provider_unavailable", "Timed out.", 504) { Retryable = true }
            : Task.FromResult(Complete(Finish)));
        var events = new List<AiRunEventDto>();
        var result = await Service(provider, new WorkflowAiOptions { RetryBaseDelayMilliseconds = 1 }).RunAsync(Request(), Key, Capture(events), CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal(10, Payload(provider.Calls[1]).GetProperty("maxOperations").GetInt32());
        Assert.Contains(events, frame => frame.Type == "checkpoint" && frame.Stage == "recovering" && frame.Checkpoint!.MaxOperations == 10);
    }

    [Fact]
    public async Task SuccessfulEditNearOutputLimit_ReducesTheNextBatchInsteadOfGrowingIt()
    {
        var provider = new ScriptedProvider(new AiCompletion(Edit(0, "rename", Set("workflow", null, "/name", "Renamed")), "stop", OutputTokens: 7500), Complete(Finish));
        var result = await Service(provider).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal(10, Payload(provider.Calls[1]).GetProperty("maxOperations").GetInt32());
    }

    [Fact]
    public async Task RetryableProviderFailure_RetriesAndAccountsForFailedAttempt()
    {
        var provider = new ScriptedProvider((call, _, _) => call == 0
            ? throw new WorkflowAiException("provider_unavailable", "Temporarily unavailable.", 503) { Retryable = true }
            : Task.FromResult(Complete(Finish)));
        var options = new WorkflowAiOptions { RetryBaseDelayMilliseconds = 1 };
        var events = new List<AiRunEventDto>();
        var result = await Service(provider, options).RunAsync(Request(), Key, Capture(events), CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal(options.InitialOutputTokens + 1, result.Run!.OutputTokens);
        Assert.True(result.Run.UsageEstimated);
        Assert.Contains(events, frame => frame.Stage == "waiting");
    }

    [Fact]
    public async Task CancellationDuringBackoff_StopsImmediatelyWithoutAnotherProviderCall()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new ScriptedProvider((_, _, _) => throw new WorkflowAiException("provider_unavailable", "Busy.", 503)
            { Retryable = true, RetryAfter = TimeSpan.FromSeconds(30) });
        var events = new List<AiRunEventDto>();
        Task Observe(AiRunEventDto frame, CancellationToken _)
        {
            events.Add(frame);
            if (frame.Stage == "waiting") cancellation.Cancel();
            return Task.CompletedTask;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(provider).RunAsync(Request(), Key, Observe, cancellation.Token));
        Assert.Single(provider.Calls);
        Assert.Contains(events, frame => frame.Type == "checkpoint");
        Assert.DoesNotContain(events, frame => frame.Type == "paused");
    }

    [Fact]
    public async Task RetryAfterBeyondRemainingDeadline_PausesWithoutSleepingOrRetrying()
    {
        var provider = new ScriptedProvider((_, _, _) => throw new WorkflowAiException("provider_rate_limit", "Wait.", 429)
            { Retryable = true, RetryAfter = TimeSpan.FromMinutes(10) });
        var events = new List<AiRunEventDto>();
        var result = await Service(provider).RunAsync(Request(), Key, Capture(events), CancellationToken.None);
        Assert.Equal("paused", result.Kind);
        Assert.Single(provider.Calls);
        Assert.Contains(events, frame => frame.Code == "provider_wait");
        Assert.NotNull(result.Checkpoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextOverflow_GetsOnlyOneCompactionRecovery(bool failTwice)
    {
        var provider = new ScriptedProvider((call, _, _) => call == 0 || failTwice
            ? throw new WorkflowAiException("provider_context", "Context exceeded.", 400)
            : Task.FromResult(Complete(Finish)));
        var events = new List<AiRunEventDto>();
        if (failTwice)
        {
            var error = await Assert.ThrowsAsync<WorkflowAiException>(() => Service(provider).RunAsync(Request(), Key, Capture(events), CancellationToken.None));
            Assert.Equal("provider_context", error.Code);
        }
        else Assert.Equal("proposal", (await Service(provider).RunAsync(Request(), Key, Capture(events), CancellationToken.None)).Kind);
        Assert.Equal(2, provider.Calls.Count);
        Assert.Single(events, frame => frame.Stage == "compacting");
        Assert.True(Payload(provider.Calls[1]).GetProperty("currentWorkflow").TryGetProperty("note", out _));
    }

    [Fact]
    public async Task RunDeadline_RetainsTheLastCompletedAndEmittedCheckpoint()
    {
        var provider = new ScriptedProvider(async (call, _, cancellationToken) =>
        {
            if (call == 0) return Complete(Edit(0, "name", Set("workflow", null, "/name", "Completed before timeout")));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The deadline should cancel the pending call.");
        });
        var events = new List<AiRunEventDto>();
        var result = await Service(provider, new WorkflowAiOptions { RunTimeoutSeconds = 1 })
            .RunAsync(Request(), Key, Capture(events), CancellationToken.None);
        Assert.Equal("paused", result.Kind);
        Assert.Null(result.Definition);
        Assert.Equal(1, result.Checkpoint!.Revision);
        Assert.Equal("Completed before timeout", result.Checkpoint.Draft.GetProperty("name").GetString());
        Assert.Equal(events.Last(frame => frame.Type == "checkpoint").Checkpoint!.Draft.GetRawText(), result.Checkpoint.Draft.GetRawText());
        Assert.Contains(events, frame => frame.Code == "authoring_timeout");
        Assert.Equal(2, provider.Calls.Count);
    }

    [Fact]
    public async Task ReadCommands_ReturnBoundedSourceAndExactPackagedReferenceExcerpts()
    {
        const string sourceMarker = "SOURCE_TAIL_REQUIREMENT";
        const string referenceMarker = "REFERENCE_TASK_RULE";
        var reads = JsonSerializer.Serialize(new
        {
            kind = "read", reads = new object[]
            {
                new { kind = "source", resource = "source/0", offset = 4000, count = 100 },
                new { kind = "reference", resource = "references/test.md", offset = 0, count = 100 },
                new { kind = "reference", resource = "schema:LaneModel", offset = 0, count = 400 }
            }
        });
        var provider = new ScriptedProvider(Complete(reads), Complete(Finish));
        var knowledge = new TestKnowledge(new Dictionary<string, string>
        {
            ["references/test.md"] = referenceMarker,
            ["references/workflow.schema.json"] = WorkflowAuthoringJson.CreateSchema()
        });
        var request = Request() with { Sources = [new("requirements.pdf", 1, new string('a', 4000) + sourceMarker)] };
        var result = await Service(provider, knowledge: knowledge).TurnAsync(request, Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.DoesNotContain(sourceMarker, string.Join("\n", provider.Calls[0].Messages.Select(message => message.Content)));
        var observation = Payload(provider.Calls[1]).GetProperty("observations").GetRawText();
        Assert.Contains(sourceMarker, observation);
        Assert.Contains(referenceMarker, observation);
        Assert.Contains("nextOffset", observation);
        Assert.Contains("schema:LaneModel", observation);
    }

    [Fact]
    public void Context_LongSourcePagesRetainExactMetadataInIndexAndReadResults()
    {
        var request = Request() with { Sources = [new("long-requirements.pdf", 17, new string('a', 5000))] };
        var context = new WorkflowAiContext(new TestKnowledge(), request);
        var payload = ContextPayload(context, request, compact: true);
        var index = payload.GetProperty("sourceIndex").EnumerateArray().Single(item => item.GetProperty("resource").GetString() == "source/0");
        Assert.Equal("long-requirements.pdf", index.GetProperty("sourceName").GetString());
        Assert.Equal(17, index.GetProperty("pageNumber").GetInt32());
        using var reads = JsonDocument.Parse("""[{"kind":"source","resource":"source/0","offset":4000,"count":100}]""");
        using var result = JsonDocument.Parse(context.Read(reads.RootElement, request.CurrentWorkflow!.Value, text => text));
        Assert.Equal("long-requirements.pdf", result.RootElement[0].GetProperty("sourceName").GetString());
        Assert.Equal(17, result.RootElement[0].GetProperty("pageNumber").GetInt32());
        Assert.Equal(4100, result.RootElement[0].GetProperty("nextOffset").GetInt32());
    }

    [Fact]
    public void Context_LargeHistoryIsBoundedInlineAndEveryOriginalMessageRemainsReadable()
    {
        const string marker = "ORIGINAL_HISTORY_TAIL";
        var request = Request() with { History = [new("user", new string('a', 100_000) + marker), new("assistant", "Latest summary")] };
        var context = new WorkflowAiContext(new TestKnowledge(), request);
        var payload = ContextPayload(context, request, compact: true);
        Assert.True(payload.GetRawText().Length < 10_000);
        Assert.Equal(2, payload.GetProperty("historyIndex").GetArrayLength());
        Assert.Single(payload.GetProperty("history").EnumerateArray());
        using var reads = JsonDocument.Parse("""[{"kind":"source","resource":"history/0","offset":100000,"count":100}]""");
        using var result = JsonDocument.Parse(context.Read(reads.RootElement, request.CurrentWorkflow!.Value, text => text));
        Assert.Equal(marker, result.RootElement[0].GetProperty("text").GetString());
        Assert.Equal("user", result.RootElement[0].GetProperty("role").GetString());
    }

    [Fact]
    public void Context_RepairNoticesDoNotEvictTheLatestStructuredReadResult()
    {
        var request = Request() with { Sources = [new("requirements.pdf", 2, "KEEP_THIS_REQUIREMENT")] };
        var context = new WorkflowAiContext(new TestKnowledge(), request);
        using var reads = JsonDocument.Parse("""[{"kind":"source","resource":"source/0","count":100}]""");
        context.Observe(context.Read(reads.RootElement, request.CurrentWorkflow!.Value, text => text));
        context.Observe("First repair diagnostic.");
        context.Observe("Second repair diagnostic.");
        context.Observe("Third repair diagnostic.");
        var observations = ContextPayload(context, request, compact: true).GetProperty("observations");
        Assert.Equal(JsonValueKind.Array, observations[0].ValueKind);
        Assert.Equal("KEEP_THIS_REQUIREMENT", observations[0][0].GetProperty("text").GetString());
        Assert.Equal("Third repair diagnostic.", observations[1].GetString());
    }

    [Fact]
    public void Context_ReducingReadBudgetPreservesNewestBatchBeforeOlderResults()
    {
        var request = Request();
        var context = new WorkflowAiContext(new TestKnowledge(), request);
        context.Observe(JsonSerializer.Serialize(new[] { new { resource = "older", offset = 0, text = new string('a', 800) } }));
        context.Observe(JsonSerializer.Serialize(new[] { new { resource = "newer", offset = 0, text = new string('b', 800) } }));
        context.ObservationCharacters = 900;

        var observations = ContextPayload(context, request, compact: false).GetProperty("observations");
        Assert.Equal("older", observations[0][0].GetProperty("resource").GetString());
        Assert.Equal(new string('a', 100), observations[0][0].GetProperty("text").GetString());
        Assert.True(observations[0][0].GetProperty("contextExcerpt").GetBoolean());
        Assert.Equal("newer", observations[1][0].GetProperty("resource").GetString());
        Assert.Equal(new string('b', 800), observations[1][0].GetProperty("text").GetString());
        Assert.False(observations[1][0].TryGetProperty("contextExcerpt", out _));
    }

    [Fact]
    public void Context_ShortCredentialsDoNotRewritePublicResourceNamesOrProtocolEnums()
    {
        var request = Request() with { Message = "A node label included an old credential: node" };
        var knowledge = new TestKnowledge(new Dictionary<string, string> { ["references/docs/node-reference.md"] = "Node configuration" });
        var context = new WorkflowAiContext(knowledge, request);
        string Scrub(string text) => text.Replace("node", "[credential removed]", StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(context.Messages(request.CurrentWorkflow!.Value, 0, "", Array.Empty<object>(), 20, 8192, Scrub, true).Last().Content);
        Assert.Equal("references/docs/node-reference.md", payload.RootElement.GetProperty("referenceIndex")[0].GetString());
        Assert.DoesNotContain("node", payload.RootElement.GetProperty("request").GetString());
        using var reads = JsonDocument.Parse("""[{"kind":"reference","resource":"references/docs/node-reference.md","count":100}]""");
        using var result = JsonDocument.Parse(context.Read(reads.RootElement, request.CurrentWorkflow.Value, Scrub));
        Assert.Equal("references/docs/node-reference.md", result.RootElement[0].GetProperty("resource").GetString());
    }

    [Fact]
    public void ReferenceQuery_FindsLiteralCaseInsensitiveMatchesAndExposesHeadingOffsets()
    {
        var request = Request();
        var reference = new string('a', 5000) + "\n## MultiInstance\nConfigure aggregate outcomes.";
        var context = new WorkflowAiContext(new TestKnowledge(new Dictionary<string, string> { ["references/test.md"] = reference }), request);
        using var reads = JsonDocument.Parse("""[{"kind":"reference","resource":"references/test.md","query":"multiinstance","count":1000}]""");
        using var result = JsonDocument.Parse(context.Read(reads.RootElement, request.CurrentWorkflow!.Value, text => text));
        Assert.True(result.RootElement[0].GetProperty("found").GetBoolean());
        Assert.True(result.RootElement[0].GetProperty("offset").GetInt32() > 4000);
        Assert.Contains("Configure aggregate outcomes", result.RootElement[0].GetProperty("text").GetString());
        var heading = ContextPayload(context, request, compact: false).GetProperty("referenceIndex")[0].GetProperty("headings")[0];
        Assert.Equal("## MultiInstance", heading.GetProperty("label").GetString());
        Assert.Equal(5001, heading.GetProperty("offset").GetInt32());
        using var noMatch = JsonDocument.Parse("""[{"kind":"reference","resource":"references/test.md","query":"not.*a.regex","count":1000}]""");
        using var missed = JsonDocument.Parse(context.Read(noMatch.RootElement, request.CurrentWorkflow.Value, text => text));
        Assert.False(missed.RootElement[0].GetProperty("found").GetBoolean());
        Assert.Equal("", missed.RootElement[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ReferenceQuery_PrefersDetailedHeadingsOverContentsAndExposesSharedReadBudget()
    {
        var request = Request();
        var reference = "Contents: [scriptTask](#scripttask)\n" + new string('a', 5000) + "\n### scriptTask\nSCRIPT_CONFIGURATION_RULE";
        var context = new WorkflowAiContext(new TestKnowledge(new Dictionary<string, string> { ["references/test.md"] = reference }), request);
        using var reads = JsonDocument.Parse("""[{"kind":"reference","resource":"references/test.md","query":"scriptTask","count":1000}]""");
        using var result = JsonDocument.Parse(context.Read(reads.RootElement, request.CurrentWorkflow!.Value, text => text));
        Assert.True(result.RootElement[0].GetProperty("offset").GetInt32() > 4000);
        Assert.Contains("SCRIPT_CONFIGURATION_RULE", result.RootElement[0].GetProperty("text").GetString());
        Assert.Equal(18_000, ContextPayload(context, request, compact: false).GetProperty("maxReadCharacters").GetInt32());
    }

    [Fact]
    public void Context_RetainsTwoFocusedReadBatchesWithinTheWorkingBudget()
    {
        var request = Request();
        var knowledge = new TestKnowledge(new Dictionary<string, string>
            { ["references/first.md"] = "FIRST_RULE" + new string('a', 13900), ["references/second.md"] = "SECOND_RULE" + new string('b', 13900) });
        var context = new WorkflowAiContext(knowledge, request);
        foreach (var resource in new[] { "references/first.md", "references/second.md" })
        {
            using var reads = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new { kind = "reference", resource, count = 12000 } }));
            context.Observe(context.Read(reads.RootElement, request.CurrentWorkflow!.Value, text => text));
        }
        var observations = ContextPayload(context, request, compact: false).GetProperty("observations").GetRawText();
        Assert.Contains("FIRST_RULE", observations);
        Assert.Contains("SECOND_RULE", observations);
    }

    [Fact]
    public void Context_AdvertisesExactSchemaResourcesForTypedReads()
    {
        var request = Request();
        var context = new WorkflowAiContext(new TestKnowledge(new Dictionary<string, string>
            { ["references/workflow.schema.json"] = WorkflowAuthoringJson.CreateSchema() }), request);
        var index = ContextPayload(context, request, compact: true).GetProperty("schemaIndex");
        Assert.Contains(index.EnumerateArray(), item => item.GetString() == "schema:VariableModel");
        Assert.DoesNotContain(index.EnumerateArray(), item => item.GetString() == "schema:WorkflowVariableModel");
        using var reads = JsonDocument.Parse("""[{"kind":"reference","resource":"schema:VariableModel","count":6000}]""");
        using var result = JsonDocument.Parse(context.Read(reads.RootElement, request.CurrentWorkflow!.Value, text => text));
        Assert.Contains("dataType", result.RootElement[0].GetProperty("text").GetString());
    }

    [Fact]
    public void ContextTokenEstimate_UsesRoundedUtf8HalfBytesAndPerMessageOverhead()
    {
        Assert.Equal(34, WorkflowAiContext.EstimateTokens([new("user", "abc")]));
        Assert.Equal((Encoding.UTF8.GetByteCount("مرحبا") + 1) / 2 + 32, WorkflowAiContext.EstimateTokens([new("user", "مرحبا")]));
    }

    [Fact]
    public async Task MultipleBatches_AssembleAValidGraphLargerThanEveryIndividualResponse()
    {
        var responses = new List<AiCompletion>
        {
            Complete(Edit(0, "start", Create("node", 1, new { id = 1, name = "Start", type = "startEvent" }), Set("workflow", null, "/initialEventId", 1)))
        };
        for (var id = 2; id <= 9; id++)
        {
            responses.Add(Complete(Edit(id - 1, "step-" + id,
                Create("node", id, new { id, name = id == 9 ? "End" : "Step " + id, type = id == 9 ? "endEvent" : "userTask" }),
                Create("flow", id - 1, new { id = id - 1, sourceRef = id - 1, targetRef = id, name = "Continue" }))));
        }
        responses.Add(Complete(Finish));
        var provider = new ScriptedProvider(responses.ToArray());
        var events = new List<AiRunEventDto>();
        var service = Service(provider);
        var result = await service.RunAsync(Request() with { CurrentWorkflow = null }, Key, Capture(events), CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.True(result.Validation.IsValid);
        Assert.Equal(9, result.Definition!.Value.GetProperty("flowNodes").GetArrayLength());
        Assert.Equal(8, result.Definition.Value.GetProperty("sequenceFlows").GetArrayLength());
        Assert.True(Encoding.UTF8.GetByteCount(result.Definition.Value.GetRawText()) > responses.Max(response => Encoding.UTF8.GetByteCount(response.Content)));
        Assert.Equal(9, events.Last(frame => frame.Type == "checkpoint").Checkpoint!.Revision);
        Assert.True((await service.ValidateAsync(result.Definition.Value, CancellationToken.None)).IsValid);
    }

    [Fact]
    public async Task Resume_ScrubsBaselineSecretsEvenAfterTheDraftDeletedTheirOriginalFields()
    {
        const string secret = "deleted-baseline-credential";
        var model = Model();
        model.TaskDistribution = new() { ClientId = "dispatcher", ClientSecret = secret };
        var request = Request() with
        {
            CurrentWorkflow = JsonSerializer.SerializeToElement(model, JsonOptions),
            Message = "Remove distribution. Earlier note: " + secret,
            History = [new("user", "Previous credential was " + secret)],
            Sources = [new("requirements.pdf", 1, "Historical text: " + secret)]
        };
        var options = new WorkflowAiOptions { MaxProviderCalls = 1 };
        var first = new ScriptedProvider(Complete(Edit(0, "remove-secret", new { op = "remove", target = "workflow", path = "/taskDistribution" })));
        var paused = await Service(first, options).TurnAsync(request, Key, CancellationToken.None);
        Assert.Equal("paused", paused.Kind);
        Assert.False(paused.Checkpoint!.Draft.TryGetProperty("taskDistribution", out _));
        var second = new ScriptedProvider(Complete(Finish));
        var result = await Service(second, options).TurnAsync(request with { Checkpoint = paused.Checkpoint }, Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.False(result.Definition!.Value.TryGetProperty("taskDistribution", out _));
        foreach (var call in first.Calls.Concat(second.Calls))
            Assert.All(call.Messages, message => Assert.DoesNotContain(secret, message.Content));
    }

    [Fact]
    public async Task DuplicateBatch_IsAcknowledgedWithoutReapplyingOrAdvancingRevision()
    {
        var batch = Edit(0, "stable-batch", Set("workflow", null, "/name", "Once"));
        var provider = new ScriptedProvider(Complete(batch), Complete(batch), Complete(Finish));
        var events = new List<AiRunEventDto>();
        var result = await Service(provider).RunAsync(Request(), Key, Capture(events), CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal(new long[] { 0, 1 }, events.Where(frame => frame.Type == "checkpoint").Select(frame => frame.Checkpoint!.Revision));
        Assert.Equal(1, Payload(provider.Calls[2]).GetProperty("revision").GetInt64());
        Assert.Contains("already applied", provider.Calls[2].Messages.Last().Content);
    }

    [Fact]
    public async Task NestedDuplicateOperationMembers_DoNotCommitAnEdit()
    {
        const string invalid = """
            {"kind":"edit","baseRevision":0,"batchId":"duplicate-json","operations":[
             {"op":"set","target":"workflow","path":"/name","value":"first","value":"second"}]}
            """;
        var provider = new ScriptedProvider(Complete(invalid), Complete(Finish));
        var events = new List<AiRunEventDto>();
        var result = await Service(provider).RunAsync(Request(), Key, Capture(events), CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal("AI test", result.Definition!.Value.GetProperty("name").GetString());
        Assert.Single(events, frame => frame.Type == "checkpoint");
        Assert.Contains(events, frame => frame.Stage == "repairing");
    }

    [Theory]
    [InlineData(false, "property")]
    [InlineData(true, "property")]
    [InlineData(true, "path")]
    [InlineData(true, "batchId")]
    public async Task ProviderKeyInDecodedIdentifiers_RejectsBatchWithoutRenamingOrCommitting(bool unicodeEscaped, string location)
    {
        var value = new Dictionary<string, object?>
        {
            ["id"] = 11, ["name"] = "payload", ["dataType"] = "json", ["isArray"] = false, ["required"] = false,
            ["defaultValue"] = new Dictionary<string, object?> { ["prefix-" + Key + "-suffix"] = "business data" }
        };
        var command = location switch
        {
            "property" => Edit(0, "bad-property", new { op = "create", target = "variable", owner = "workflow", id = 11, value }),
            "path" => Edit(0, "bad-path", Set("workflow", null, "/" + Key, "business data")),
            _ => Edit(0, "batch-" + Key, Set("workflow", null, "/name", "Must not commit"))
        };
        if (unicodeEscaped)
            command = command.Replace(Key, string.Concat(Key.Select(character => "\\u" + ((int)character).ToString("x4"))), StringComparison.Ordinal);
        var provider = new ScriptedProvider(Complete("Here is the next step:\n" + command), Complete(Finish));
        var events = new List<AiRunEventDto>();
        var result = await Service(provider).RunAsync(Request(), Key, Capture(events), CancellationToken.None);

        Assert.Equal("proposal", result.Kind);
        Assert.Equal("AI test", result.Definition!.Value.GetProperty("name").GetString());
        Assert.Empty(result.Definition.Value.GetProperty("variables").EnumerateArray());
        Assert.Single(events, frame => frame.Type == "checkpoint");
        Assert.Contains(events, frame => frame.Stage == "repairing");
        Assert.Contains("Provider credentials cannot appear", Payload(provider.Calls[1]).GetProperty("observations").GetRawText());
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(result, JsonOptions));
        Assert.DoesNotContain("[provider key removed]", result.Definition.Value.GetRawText());
        Assert.All(provider.Calls.SelectMany(call => call.Messages), message => Assert.DoesNotContain(Key, message.Content));
    }

    private static AiTurnRequestDto Request() => new()
    {
        ConversationId = Guid.NewGuid().ToString(), Message = "Update the review workflow", SnapshotId = "runner-snapshot",
        CurrentWorkflow = JsonSerializer.SerializeToElement(Model(), JsonOptions)
    };

    private static WorkflowModel Model(string name = "AI test") => new()
    {
        Id = "runner-test", Name = name, InitialEventId = 1,
        FlowNodes = [new() { Id = 1, Name = "Start", Type = "startEvent" }, new() { Id = 2, Name = "Review", Type = "userTask" }, new() { Id = 3, Name = "End", Type = "endEvent" }],
        SequenceFlows = [new() { Id = 1, SourceRef = 1, TargetRef = 2, Name = "Begin" }, new() { Id = 2, SourceRef = 2, TargetRef = 3, Name = "Finish" }]
    };

    private static WorkflowAiAuthoringService Service(ScriptedProvider provider, WorkflowAiOptions? configured = null, TestKnowledge? knowledge = null)
    {
        var options = configured ?? new WorkflowAiOptions();
        return new([provider], knowledge ?? new(),
            new WorkflowDefinitionValidator(new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance), new ServiceTaskOptions()),
            new WorkflowDefinitionReadinessChecker(), options, new WorkflowAiConcurrencyGate(options));
    }

    private static Func<AiRunEventDto, CancellationToken, Task> Capture(List<AiRunEventDto> events) => (frame, _) =>
    {
        events.Add(frame);
        return Task.CompletedTask;
    };

    private static string Edit(long revision, string batch, params object[] operations) =>
        JsonSerializer.Serialize(new { kind = "edit", baseRevision = revision, batchId = batch, operations }, JsonOptions);

    private static object Set(string target, int? id, string path, object? value) => new { op = "set", target, id, path, value };
    private static object Create(string target, int id, object value) => new { op = "create", target, id, value };
    private static AiCompletion Complete(string content) => new(content, "stop", InputTokens: 100, OutputTokens: 1);

    private static JsonElement Payload(ProviderCall call)
    {
        using var document = JsonDocument.Parse(call.Messages.Last().Content);
        return document.RootElement.Clone();
    }

    private static JsonElement ContextPayload(WorkflowAiContext context, AiTurnRequestDto request, bool compact)
    {
        using var document = JsonDocument.Parse(context.Messages(request.CurrentWorkflow!.Value, 0, "", Array.Empty<object>(), 20, 8192, text => text, compact).Last().Content);
        return document.RootElement.Clone();
    }

    private sealed record ProviderCall(AiChatMessageDto[] Messages, int OutputAllowance);

    private sealed class ScriptedProvider : IAiWorkflowProvider
    {
        private readonly Func<int, ProviderCall, CancellationToken, Task<AiCompletion>> respond;
        public List<ProviderCall> Calls { get; } = [];
        public AiProviderDto Descriptor => new("opencode-go", "Fake provider", "kimi-k2.7-code", [new("kimi-k2.7-code", "Fake model")]);

        public ScriptedProvider(params AiCompletion[] responses) : this((index, _, _) =>
            index < responses.Length ? Task.FromResult(responses[index]) : throw new InvalidOperationException("The runner made an unexpected provider call.")) { }

        public ScriptedProvider(Func<int, ProviderCall, CancellationToken, Task<AiCompletion>> respond) => this.respond = respond;

        public Task<AiCompletion> CompleteAsync(string modelId, string conversationId, IReadOnlyList<AiChatMessageDto> messages,
            string apiKey, int maxOutputTokens, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = new ProviderCall(messages.ToArray(), maxOutputTokens);
            Calls.Add(call);
            return respond(Calls.Count - 1, call, cancellationToken);
        }
    }

    private sealed class TestKnowledge(IReadOnlyDictionary<string, string>? resources = null) : IAuthoringKnowledge
    {
        public string ContractHash => "runner-test-contract";
        public IReadOnlyDictionary<string, string> Resources { get; } = resources ?? new Dictionary<string, string>();
        public string GetPromptContext(string? query = null) => "Flowbit canonical authoring contract";
        public byte[] GetPackageZip() => [];
    }
}
