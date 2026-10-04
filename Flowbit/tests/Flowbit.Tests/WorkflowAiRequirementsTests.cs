using System.Collections.Concurrent;
using System.Text.Json;
using Flowbit.Service.Ai;
using Flowbit.Service.Authoring;
using Flowbit.Shared.Dtos;
using Xunit;

namespace Flowbit.Tests;

public sealed partial class WorkflowAiExecutionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RequirementsReviewChecksExactCandidateAndReturnsOneOrderedTerminalResult(int workers)
    {
        var reviewers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var provider = new ReviewProvider(async (purpose, payload, _, ct) =>
        {
            if (purpose is "coverage" or "routing")
            {
                if (Interlocked.Increment(ref arrived) == 2) reviewers.TrySetResult();
                if (workers == 2) await reviewers.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                if (purpose == "coverage") await Task.Delay(15, ct); // Deliberately reverse completion order.
            }
            return ReviewReply(purpose, payload);
        });
        var events = new List<AiRunEventDto>();
        var result = await Service(provider, ReviewOptions(workers)).RunAsync(Request(), Key,
            (frame, _) => { events.Add(frame); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.True(result.RequirementsReview!.Passed);
        Assert.Equal(AuthoringPackageBuilder.Hash(result.Definition!.Value.GetRawText()), result.RequirementsReview.CandidateHash);
        Assert.Equal(new[] { "coverage", "routing" }, result.RequirementsReview.Checks.Select(check => check.Reviewer));
        Assert.Equal(4, result.Run!.ProviderCalls);
        Assert.Equal(workers, result.Run.PeakProviderCalls);
        Assert.Equal(0, result.Run.ActiveProviderCalls);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(number => (long)number), events.Select(frame => frame.Sequence));
        Assert.Single(events, frame => frame.Type == "result");
        Assert.Equal("result", events.Last().Type);
        Assert.All(events.Where(frame => frame.Checkpoint is not null), frame => Assert.Equal(3, frame.Checkpoint!.Version));
        Assert.All(provider.Messages.SelectMany(items => items), message => Assert.DoesNotContain(Key, message.Content));
    }

    [Theory]
    [InlineData("missing", "invalid")]
    [InlineData("uncertain", "clarification")]
    public async Task StructurallyValidButUnresolvedCandidateCannotBeApplied(string status, string kind)
    {
        var provider = new ReviewProvider((purpose, payload, _, _) => Task.FromResult(ReviewReply(purpose, payload, status)));
        var options = ReviewOptions(2); options.MaxRepairAttempts = 0;
        var result = await Service(provider, options).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal(kind, result.Kind);
        Assert.True(result.Validation.IsValid); // Business review is deliberately separate.
        Assert.False(result.RequirementsReview!.Passed);
        Assert.Null(result.Definition);
        Assert.NotNull(result.Checkpoint);
        Assert.Equal(4, result.Run!.ProviderCalls);
    }

    [Fact]
    public async Task MissingRequirementIsRepairedAndBothReviewsRunAgainOnNewDraft()
    {
        var builderCalls = 0;
        var provider = new ReviewProvider((purpose, payload, context, _) =>
        {
            if (purpose == "builder") return Task.FromResult(Interlocked.Increment(ref builderCalls) == 2 ? Edit : Finish);
            var repaired = context.GetProperty("currentWorkflow").GetProperty("name").GetString() == "Recovered";
            return Task.FromResult(ReviewReply(purpose, payload, repaired ? "covered" : "missing"));
        });
        var result = await Service(provider, ReviewOptions(2)).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal("Recovered", result.Definition!.Value.GetProperty("name").GetString());
        Assert.True(result.RequirementsReview!.Passed);
        Assert.Equal(1, result.Run!.AcceptedBatches);
        Assert.Equal(8, result.Run.ProviderCalls); // analysis, finish, two reviews, edit, finish, two fresh reviews
    }

    [Theory]
    [InlineData("quote")]
    [InlineData("entity")]
    [InlineData("checklist")]
    public async Task FabricatedOrMissingEvidenceNeverProducesProposal(string defect)
    {
        var provider = new ReviewProvider((purpose, payload, _, _) =>
        {
            var response = ReviewReply(purpose, payload);
            if (defect == "quote" && purpose == "analysis") response = response.Replace("Rename this workflow", "not present in the source");
            if (defect == "entity" && purpose is "coverage" or "routing") response = response.Replace("\"id\":2", "\"id\":9999");
            if (defect == "checklist" && purpose is "coverage" or "routing") response = """{"kind":"review","checks":[],"additionalRequirements":[]}""";
            return Task.FromResult(response);
        });
        var options = ReviewOptions(2); options.MaxRepairAttempts = 0;
        var result = await Service(provider, options).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("paused", result.Kind);
        Assert.Null(result.Definition);
        Assert.Null(result.RequirementsReview);
        Assert.Equal(0, result.Run!.ActiveProviderCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParallelCallsReserveSharedCallAndOutputBudgets(bool calls)
    {
        var provider = new ReviewProvider(async (purpose, payload, _, ct) =>
        {
            if (purpose is "coverage" or "routing") await Task.Delay(100, ct);
            return ReviewReply(purpose, payload);
        });
        var options = ReviewOptions(2);
        if (calls) options.MaxProviderCalls = 3;
        else options.MaxRunOutputTokens = options.InitialOutputTokens + 200; // two completed calls (100 each) plus one reservation
        var result = await Service(provider, options).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("paused", result.Kind);
        Assert.Null(result.Definition);
        Assert.Equal(3, result.Run!.ProviderCalls);
        Assert.Equal(0, result.Run.ActiveProviderCalls);
        Assert.True(result.Run.OutputTokens <= options.MaxRunOutputTokens);
    }

    [Fact]
    public async Task CancellingParallelReviewsStopsBothAndPreservesOnlyCommittedCheckpoint()
    {
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0; var cancelled = 0;
        using var cancellation = new CancellationTokenSource();
        var provider = new ReviewProvider(async (purpose, payload, _, ct) =>
        {
            if (purpose is "coverage" or "routing")
            {
                if (Interlocked.Increment(ref count) == 2) both.SetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            }
            return ReviewReply(purpose, payload);
        });
        var events = new List<AiRunEventDto>();
        var run = Service(provider, ReviewOptions(2)).RunAsync(Request(), Key,
            (frame, _) => { events.Add(frame); return Task.CompletedTask; }, cancellation.Token);
        await both.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(2, cancelled);
        Assert.DoesNotContain(events, frame => frame.Type == "result");
        var checkpoint = events.Last(frame => frame.Checkpoint is not null).Checkpoint!;
        Assert.Equal(0, checkpoint.Revision);
        // Continue always repeats analysis and review; a checkpoint never carries a passing verdict.
        var resumedProvider = new ReviewProvider((purpose, payload, _, _) => Task.FromResult(ReviewReply(purpose, payload)));
        var result = await Service(resumedProvider, ReviewOptions(2)).TurnAsync(Request() with { Checkpoint = checkpoint }, Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal(4, result.Run!.ProviderCalls);
    }

    [Fact]
    public async Task ChangingReviewPolicyRejectsContinuationBeforeTransport()
    {
        var options = ReviewOptions(1); options.MaxProviderCalls = 1;
        var provider = new ReviewProvider((purpose, payload, _, _) => Task.FromResult(ReviewReply(purpose, payload)));
        var paused = await Service(provider, options).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal(3, paused.Checkpoint!.Version);
        var noCalls = new ReviewProvider((_, _, _, _) => throw new InvalidOperationException("Must not call provider"));
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => Service(noCalls, ReviewOptions(2))
            .TurnAsync(Request() with { Checkpoint = paused.Checkpoint }, Key, CancellationToken.None));
        Assert.Equal("checkpoint_configuration_changed", error.Code);
        var disabled = Options("optimized");
        error = await Assert.ThrowsAsync<WorkflowAiException>(() => Service(noCalls, disabled)
            .TurnAsync(Request() with { Checkpoint = paused.Checkpoint }, Key, CancellationToken.None));
        Assert.Equal("checkpoint_configuration_changed", error.Code);
    }

    [Fact]
    public async Task SourceBatchesIncludeTailAndIndependentAnalysisOverlaps()
    {
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var sourceText = new string('a', 24000) + "required tail";
        var seen = new ConcurrentBag<string>();
        var provider = new ReviewProvider(async (purpose, payload, _, ct) =>
        {
            if (purpose == "analysis")
            {
                foreach (var source in payload.GetProperty("sourceBatch").EnumerateArray()) seen.Add(source.GetProperty("text").GetString()!);
                if (Interlocked.Increment(ref count) == 2) both.TrySetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            return ReviewReply(purpose, payload);
        });
        var result = await Service(provider, ReviewOptions(2)).TurnAsync(Request() with { Sources = [new("Test", 1, sourceText)] }, Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        Assert.Equal(2, count);
        Assert.Contains(seen, text => text == "required tail");
        Assert.Equal(2, result.RequirementsReview!.Requirements.Count);
    }

    [Fact]
    public async Task ProcessProviderCapAppliesAcrossIndependentRuns()
    {
        var options = ReviewOptions(2); options.MaxConcurrentProviderCalls = 2;
        using var gate = new WorkflowAiConcurrencyGate(options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var peak = 0; var started = 0;
        var sync = new object();
        var provider = new AttemptProvider(async ct =>
        {
            lock (sync) { peak = Math.Max(peak, ++active); started++; if (started == 2) occupied.TrySetResult(); }
            try { await release.Task.WaitAsync(ct); return new(Finish, "stop", 10, 20); }
            finally { lock (sync) active--; }
        });
        var states = Enumerable.Range(0, 3).Select(_ => new WorkflowAiSession()).ToArray();
        var dispatchers = states.Select(state => new WorkflowAiCallDispatcher(provider, options, gate, state, Request(), new("optimized", "low"), Key, (_, _) => Task.CompletedTask)).ToArray();
        try
        {
            var tasks = dispatchers.SelectMany(dispatcher => Enumerable.Range(0, 2).Select(_ =>
                dispatcher.AttemptAsync([new("user", "synthetic")], 100, "analysis", false, timeout.Token))).ToArray();
            await occupied.Task.WaitAsync(timeout.Token);
            Assert.Equal(2, started); Assert.Equal(0, gate.ProviderCalls.CurrentCount);
            release.SetResult();
            await Task.WhenAll(tasks);
            Assert.Equal(2, peak); Assert.Equal(6, started);
            Assert.All(states, state => { Assert.Equal(2, state.Calls); Assert.Equal(40, state.OutputTokens); Assert.Equal(0, state.ReservedOutputTokens); });
        }
        finally { release.TrySetResult(); foreach (var dispatcher in dispatchers) dispatcher.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownUsageAndFailedAttemptsConsumeReservation(bool fail)
    {
        var options = ReviewOptions(2);
        using var gate = new WorkflowAiConcurrencyGate(options);
        var state = new WorkflowAiSession();
        var provider = new AttemptProvider(_ => fail ? throw new WorkflowAiException("provider_unavailable", "Synthetic") { Retryable = true }
            : Task.FromResult(new AiCompletion(Finish, "stop")));
        using var dispatcher = new WorkflowAiCallDispatcher(provider, options, gate, state, Request(), new("optimized", "low"), Key, (_, _) => Task.CompletedTask);
        var attempt = dispatcher.AttemptAsync([new("user", "synthetic")], 100, "review-coverage", false, CancellationToken.None);
        if (fail) await Assert.ThrowsAsync<WorkflowAiException>(() => attempt); else await attempt;
        Assert.Equal(100, state.OutputTokens); Assert.True(state.Estimated);
        Assert.Equal(1, state.Calls); Assert.Equal(0, state.ActiveCalls); Assert.Equal(0, state.ReservedOutputTokens);
        Assert.Equal(options.MaxConcurrentProviderCalls, gate.ProviderCalls.CurrentCount);
    }

    [Fact]
    public async Task ExtractionAmbiguityCannotBeOverruledByCoveredReviews()
    {
        var provider = new ReviewProvider((purpose, payload, _, _) => Task.FromResult(
            ReviewReply(purpose, payload).Replace("\"needsClarification\":false", "\"needsClarification\":true")));
        var result = await Service(provider, ReviewOptions(2)).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("clarification", result.Kind); Assert.Null(result.Definition);
        Assert.Contains(result.RequirementsReview!.Checks, check => check.Reviewer == "analysis" && check.Status == "uncertain");
    }

    private sealed class AttemptProvider(Func<CancellationToken, Task<AiCompletion>> respond) : IAiWorkflowProvider
    {
        public AiProviderDto Descriptor => new("opencode-go", "Synthetic", "glm-5.3-flash", [new("glm-5.3-flash", "Synthetic")]);
        public Task<AiCompletion> CompleteAsync(string modelId, string conversationId, IReadOnlyList<AiChatMessageDto> messages,
            string apiKey, int maxOutputTokens, CancellationToken ct) => respond(ct);
    }

    private static WorkflowAiOptions ReviewOptions(int workers)
    {
        var options = Options("optimized");
        options.RequirementsReviewEnabled = true; options.MaxParallelAnalysisCalls = workers;
        options.RetryBaseDelayMilliseconds = 1;
        return options;
    }

    private static string ReviewReply(string purpose, JsonElement payload, string status = "covered")
    {
        if (purpose == "builder") return Finish;
        if (purpose == "analysis")
        {
            var source = payload.GetProperty("sourceBatch")[0];
            var text = source.GetProperty("text").GetString()!;
            return JsonSerializer.Serialize(new { kind = "analysis", requirements = new[] { new
            { text = "Preserve requested workflow behavior", source = source.GetProperty("key").GetString(), quote = text[..Math.Min(text.Length, 100)], needsClarification = false } } });
        }
        return JsonSerializer.Serialize(new { kind = "review", checks = payload.GetProperty("checklist").EnumerateArray().Select(item => new
        { requirementId = item.GetProperty("id").GetString(), status, explanation = "Verify the requested workflow behavior.", evidence = new[] { new { target = "node", id = 2 } } }), additionalRequirements = System.Array.Empty<object>() });
    }

    private sealed class ReviewProvider(Func<string, JsonElement, JsonElement, CancellationToken, Task<string>> respond) : IAiWorkflowProvider
    {
        public ConcurrentQueue<AiChatMessageDto[]> Messages { get; } = new();
        public AiProviderDto Descriptor => new("opencode-go", "Synthetic", "glm-5.3-flash", [new("glm-5.3-flash", "Synthetic")]);
        public async Task<AiCompletion> CompleteAsync(string modelId, string conversationId, IReadOnlyList<AiChatMessageDto> messages,
            string apiKey, int maxOutputTokens, CancellationToken ct)
        {
            Messages.Enqueue(messages.ToArray());
            using var last = JsonDocument.Parse(messages.Last().Content);
            using var context = JsonDocument.Parse(messages[1].Content);
            var purpose = last.RootElement.TryGetProperty("purpose", out var value) ? value.GetString()! : "builder";
            return new(await respond(purpose, last.RootElement, context.RootElement, ct), "stop", 100, 100);
        }
    }
}
