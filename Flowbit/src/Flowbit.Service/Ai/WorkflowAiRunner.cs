using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Service.Authoring;
using Flowbit.Shared.Authoring;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Ai;

public sealed partial class WorkflowAiAuthoringService
{
    private sealed record ModelStep(AiCompletion? Completion, int MaxOutputTokens, AiTurnResultDto? Result = null);

    private static readonly Histogram<double> RunDuration = WorkflowAiTelemetry.RunSeconds;

    public Task<AiTurnResultDto> TurnAsync(AiTurnRequestDto request, string apiKey, CancellationToken cancellationToken)
        => RunAsync(request, apiKey, null, cancellationToken);

    public async Task<AiTurnResultDto> RunAsync(AiTurnRequestDto request, string apiKey,
        Func<AiRunEventDto, CancellationToken, Task>? emit, CancellationToken cancellationToken)
    {
        if (!options.Enabled) throw new WorkflowAiException("ai_disabled", "AI authoring is disabled.", 503);
        options.Validate();
        ValidateRequest(request, apiKey);
        if (request.CurrentWorkflow is { } supplied && supplied.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new WorkflowAiException("invalid_current_workflow", "The current workflow must be a canonical JSON object.");
        var matches = providers.Where(item => item.Descriptor.Id == request.ProviderId).ToArray();
        if (matches.Length > 1) throw new WorkflowAiException("provider_configuration", "The provider is registered more than once.", 503);
        var provider = matches.FirstOrDefault() ?? throw new WorkflowAiException("unsupported_provider", "Select an enabled AI provider.");
        if (!provider.Descriptor.Models.Any(model => model.Id == request.ModelId)) throw new WorkflowAiException("unsupported_model", "Select an enabled model.");
        var profile = options.GetModelProfile(request.ModelId);
        var execution = new AiExecutionSettings(request.Checkpoint?.Version == 1 ? "current" : options.ExecutionVariant,
            options.OpenCodeReasoningEfforts.GetValueOrDefault(request.ModelId));
        var profileHash = AuthoringPackageBuilder.Hash(JsonSerializer.Serialize(new { profile, options.OpenCodeBaseUrl }, JsonOptions));
        var reviewPolicyHash = options.RequirementsReviewEnabled ? AuthoringPackageBuilder.Hash(JsonSerializer.Serialize(new
        { version = 1, options.RequirementsReviewEnabled, options.MaxParallelAnalysisCalls, options.MaxConcurrentProviderCalls }, JsonOptions)) : null;
        if (!provider.SupportsExecution(execution.Variant))
            throw new WorkflowAiException("execution_unavailable", "The selected provider does not support this authoring engine.", 503);
        if (request.Checkpoint is { } configured && (options.RequirementsReviewEnabled != (configured.Version == 3)
            || configured.Version == 3 && configured.ReviewPolicyHash != reviewPolicyHash))
            throw new WorkflowAiException("checkpoint_configuration_changed", "Requirements review settings changed. Start a new request.", 409);
        if (request.Checkpoint is { Version: 2 or 3 } bound && (bound.ExecutionVariant != execution.Variant
            || bound.ReasoningEffort != execution.ReasoningEffort || bound.ModelProfileHash != profileHash))
            throw new WorkflowAiException("checkpoint_configuration_changed", "The authoring engine or model settings changed. Start a new request.", 409);
        var optimized = execution.Variant != "current";
        if (!await concurrency.Semaphore.WaitAsync(0, cancellationToken)) throw new WorkflowAiException("authoring_busy", "AI authoring is busy. Try again when another request finishes.", 429);
        var clock = Stopwatch.StartNew();
        using var runActivity = WorkflowAiTelemetry.Start("authoring.run");
        runActivity?.SetTag("variant", execution.Variant);
        runActivity?.SetTag("outcome", "failed");
        var state = new WorkflowAiSession();
        using var eventGate = new SemaphoreSlim(1, 1);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.RunTimeoutSeconds));
        try
        {
            var catalog = await LoadSelectedCatalogAsync(request.SharedVariableKeys, deadline.Token);
            var inputHash = AuthoringPackageBuilder.Hash(JsonSerializer.Serialize(new { request = request with { Checkpoint = null }, catalog }, JsonOptions));
            var redaction = new WorkflowAiRedaction();
            WorkflowModel? original = null;
            if (request.CurrentWorkflow is { ValueKind: JsonValueKind.Object } current)
            {
                try
                {
                    original = WorkflowAuthoringJson.Parse(current.GetRawText());
                    WorkflowAiDraft.ValidateCandidate(current, original.Id, options.MaxWorkflowCharacters);
                }
                catch (JsonException) { throw new WorkflowAiException("invalid_current_workflow", "Validate the current workflow before using it as authoring context."); }
                redaction.Redact(current); // Seed baseline secrets even when a resumed draft removed them.
            }
            var draft = request.CurrentWorkflow is { ValueKind: JsonValueKind.Object } baseline ? baseline
                : JsonSerializer.SerializeToElement(new WorkflowModel { Id = "workflow-" + Guid.NewGuid().ToString("N")[..12], Name = "New workflow" }, JsonOptions);
            if (request.Checkpoint is { } checkpoint)
            {
                ValidateCheckpoint(checkpoint, inputHash, original?.Id ?? "");
                draft = checkpoint.Draft;
                state.Revision = checkpoint.Revision;
                state.Plan = checkpoint.Plan;
                state.Batches.AddRange(checkpoint.Batches);
            }
            draft = redaction.Redact(draft);
            if (Encoding.UTF8.GetByteCount(redaction.Restore(draft).GetRawText()) > options.MaxWorkflowCharacters)
                throw new WorkflowAiException("workflow_too_large", "The canonical workflow exceeds the authoring checkpoint size limit.", 413);
            string Sanitize(string text) => redaction.Sanitize(RemoveApiKey(text, apiKey));
            state.Plan = Sanitize(state.Plan);
            var context = new WorkflowAiContext(knowledge, request, optimized)
            { DraftCharacters = Math.Min(options.MaxContextCharacters, profile.ContextTokens * 2) };
            if (request.Checkpoint is { } resumed)
            {
                try { context.RestoreReads(resumed.ContextReads, draft, Sanitize); }
                catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
                { throw new WorkflowAiException("invalid_checkpoint", "The continuation read positions are malformed."); }
            }
            state.ContextReads = context.ReadCount;
            state.DuplicateReads = context.DuplicateReadCount;
            var maxOperations = Math.Min(options.MaxOperationsPerBatch, request.Checkpoint?.MaxOperations ?? options.MaxOperationsPerBatch);
            var outputAllowance = Math.Clamp(request.Checkpoint?.OutputAllowance ?? profile.InitialOutputTokens, profile.InitialOutputTokens, profile.MaxOutputTokens);
            var validationFailures = 0;
            var truncations = 0;
            var escalated = outputAllowance > profile.InitialOutputTokens;
            var contextRecovery = false;
            var lastFailure = "";
            var sameFailures = 0;
            var compact = optimized;
            var fastBatches = 0;
            AiValidationResultDto validation = Invalid("The draft is not yet complete.");
            AiRequirementsReviewDto? requirementsReview = null;
            var requirementRepairs = 0;
            using var dispatcher = new WorkflowAiCallDispatcher(provider, options, concurrency, state, request, execution, apiKey,
                (stage, message) => Event("progress", stage, message));
            var requirements = options.RequirementsReviewEnabled
                ? new WorkflowAiRequirements(knowledge, request, options, dispatcher, Sanitize) : null;

            AiCheckpointDto MakeCheckpoint() => new()
            {
                Version = options.RequirementsReviewEnabled ? 3 : 2, ReviewPolicyHash = reviewPolicyHash,
                ExecutionVariant = execution.Variant, ReasoningEffort = execution.ReasoningEffort, ModelProfileHash = profileHash,
                InputHash = inputHash, ContractHash = knowledge.ContractHash,
                Draft = redaction.Restore(draft), Revision = state.Revision,
                Plan = Sanitize(state.Plan), Batches = state.Batches.TakeLast(100).ToArray(),
                OutputAllowance = outputAllowance, MaxOperations = maxOperations, ContextReads = context.RetainedReads()
            };
            state.Checkpoint = MakeCheckpoint();
            await Event("checkpoint", request.Checkpoint is null ? "preparing" : "resuming",
                request.Checkpoint is null ? "Preparing workflow context." : $"Resuming the saved draft after {state.Revision} completed draft steps.", state.Checkpoint);
            if (requirements is not null)
            {
                await requirements.AnalyzeAsync(draft, deadline.Token);
                context.RequirementChecklist = requirements.Checklist;
            }
            while (true)
            {
                var step = await NextModelStepAsync(deadline.Token);
                if (step.Result is { } stopped) { runActivity?.SetTag("outcome", stopped.Kind); return stopped; }
                var completed = await ExecuteCommandAsync(step, deadline.Token);
                if (completed is not null) { runActivity?.SetTag("outcome", completed.Kind); return completed; }
            }

            async Task<ModelStep> PauseStep(string code, string message)
                => new(null, 0, await FinishPaused(code, message));

            async Task<ModelStep> NextModelStepAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                // Recover failed or truncated attempts before dispatching a complete command.
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (clock.Elapsed.TotalSeconds >= options.RunTimeoutSeconds - Math.Min(5, options.RunTimeoutSeconds / 10d))
                        return await PauseStep("authoring_timeout", "This run has too little time for another model call. Continue from the last completed draft step.");
                    var available = options.MaxRunOutputTokens - state.OutputTokens;
                    if (state.Calls >= options.MaxProviderCalls || available < Math.Min(1024, profile.InitialOutputTokens))
                        return await PauseStep("run_budget", "This run reached its model usage budget. Continue from the last completed draft step.");
                    var tokens = (int)Math.Min(outputAllowance, available);
                    object Budget() => new { secondsRemaining = Math.Max(0, options.RunTimeoutSeconds - clock.Elapsed.TotalSeconds),
                        providerCallsRemaining = options.MaxProviderCalls - state.Calls, outputTokensRemaining = options.MaxRunOutputTokens - state.OutputTokens };
                    var messages = context.Messages(draft, state.Revision, state.Plan, catalog, maxOperations, tokens, Sanitize, compact, Budget());
                    if (messages.Sum(message => (long)message.Content.Length) > options.MaxContextCharacters)
                    {
                        compact = true;
                        messages = context.Messages(draft, state.Revision, state.Plan, catalog, maxOperations, tokens, Sanitize, true, Budget());
                        if (messages.Sum(message => (long)message.Content.Length) > options.MaxContextCharacters)
                        {
                            context.UseDraftIndex = true;
                            messages = context.Messages(draft, state.Revision, state.Plan, catalog, maxOperations, tokens, Sanitize, true, Budget());
                        }
                        if (messages.Sum(message => (long)message.Content.Length) > options.MaxContextCharacters)
                            throw new WorkflowAiException("context_too_large", "Authoring context exceeds the configured character limit. Reduce the supplied input or increase the configured limit; original requirements were retained.", 413);
                    }
                    var safeContext = (long)(profile.ContextTokens * .9) - tokens;
                    if (optimized && WorkflowAiContext.EstimateTokens(messages) > safeContext)
                    {
                        // Allocate the remainder after actual instructions, sources and retained reads, not a fixed draft cutoff.
                        var excessBytes = (WorkflowAiContext.EstimateTokens(messages) - safeContext) * 2;
                        context.DraftCharacters = (int)Math.Max(2000, Encoding.UTF8.GetByteCount(draft.GetRawText()) - excessBytes);
                        messages = context.Messages(draft, state.Revision, state.Plan, catalog, maxOperations, tokens, Sanitize, compact, Budget());
                    }
                    if (WorkflowAiContext.EstimateTokens(messages) > safeContext)
                    {
                        compact = true;
                        messages = context.Messages(draft, state.Revision, state.Plan, catalog, maxOperations, tokens, Sanitize, true, Budget());
                        if (WorkflowAiContext.EstimateTokens(messages) > safeContext)
                        {
                            context.UseDraftIndex = true;
                            context.ObservationCharacters = Math.Max(2000, context.ObservationCharacters / 2);
                            messages = context.Messages(draft, state.Revision, state.Plan, catalog, maxOperations, tokens, Sanitize, true, Budget());
                        }
                        if (WorkflowAiContext.EstimateTokens(messages) > safeContext)
                            return await PauseStep("context_too_large", "Required authoring context exceeds this model's configured context budget. Reduce the input or adjust the model profile.");
                    }
                    AiCompletion completion;
                    for (var retry = 0; ; retry++)
                    {
                        // Recovery guidance also consumes context. Recheck after every retry rebuild.
                        if (messages.Sum(message => (long)message.Content.Length) > options.MaxContextCharacters
                            || WorkflowAiContext.EstimateTokens(messages) > safeContext)
                            return await PauseStep("context_too_large", "Required authoring context exceeds this model's configured context budget. Reduce the input or adjust the model profile.");
                        if (state.Calls >= options.MaxProviderCalls || options.MaxRunOutputTokens - state.OutputTokens < tokens)
                            return await PauseStep("run_budget", "This run reached its model usage budget. Continue from the last completed draft step.");
                        try
                        {
                            completion = await dispatcher.AttemptAsync(messages, tokens, "builder", retry > 0, deadline.Token);
                            break;
                        }
                        catch (WorkflowAiException error) when (error.Code == "provider_context" && !contextRecovery)
                        {
                            fastBatches = 0;
                            contextRecovery = compact = true;
                            context.UseDraftIndex = true;
                            context.ObservationCharacters = 2000;
                            messages = context.Messages(draft, state.Revision, state.Plan, catalog, maxOperations, tokens, Sanitize, true, Budget());
                            WorkflowAiTelemetry.Recovery("context", "builder");
                            await Event("progress", "compacting", "Rebuilding a smaller context; original requirements remain available.");
                        }
                        catch (WorkflowAiException error) when (error.Retryable)
                        {
                            if (retry >= options.MaxTransportRetries)
                                return error.Code == "provider_invalid_response"
                                    ? await PauseStep("provider_invalid_response", "The provider repeatedly returned an unreadable response. Continue from the last completed draft step.")
                                    : error.StatusCode == 504
                                    ? await PauseStep("provider_timeout", $"The model repeatedly exceeded the {options.RequestTimeoutSeconds}-second request timeout. Continue from the last completed draft step, or send a smaller request.")
                                    : await PauseStep("provider_unavailable", "The provider is still unavailable after bounded retries. Continue later from the last completed draft step.");
                            if (error.StatusCode == 504)
                            {
                                fastBatches = 0;
                                maxOperations = Math.Max(1, maxOperations / 2);
                                context.Observe($"The previous model call timed out. No output from it was applied. Focus only on the next safe batch of at most {maxOperations} operations; reuse prior planning decisions instead of rebuilding the full plan.");
                                state.Checkpoint = MakeCheckpoint();
                                messages = context.Messages(draft, state.Revision, state.Plan, catalog, maxOperations, tokens, Sanitize, compact, Budget());
                                await Event("checkpoint", "recovering", "The model call timed out. Retrying a smaller complete step.", state.Checkpoint);
                            }
                            var backoff = TimeSpan.FromMilliseconds(options.RetryBaseDelayMilliseconds * Math.Pow(2, retry) + Random.Shared.Next(0, 251));
                            var delay = error.RetryAfter > backoff ? error.RetryAfter.Value : backoff;
                            if (delay.TotalSeconds >= options.RunTimeoutSeconds - clock.Elapsed.TotalSeconds)
                                return await PauseStep("provider_wait", "The provider asked to wait beyond this run's remaining time. Continue later.");
                            WorkflowAiTelemetry.Recovery("transport", "builder");
                            await Event("progress", "waiting", $"Provider temporarily unavailable. Retrying in {Math.Ceiling(delay.TotalSeconds)} seconds.");
                            using (WorkflowAiTelemetry.Start("provider.retry_wait")) await Task.Delay(delay, deadline.Token);
                        }
                    }
                    deadline.Token.ThrowIfCancellationRequested();
                    if (Encoding.UTF8.GetByteCount(completion.Content) > options.MaxOutputBytes)
                        throw new WorkflowAiException("provider_output_too_large", "The provider response exceeded the authoring size limit.", 502);
                    if (completion.FinishReason == "length")
                    {
                        fastBatches = 0;
                        WorkflowAiTelemetry.Recovery("truncation", "builder");
                        if (++truncations > options.MaxTruncationRecoveries)
                            return await PauseStep("provider_truncated", "The model repeatedly exceeded its output limit. The last completed draft is retained; narrow the requested change or continue.");
                        if (maxOperations > 1) maxOperations = optimized || truncations == 1 ? Math.Max(1, maxOperations / 2) : 1;
                        else if (!escalated && outputAllowance < profile.MaxOutputTokens)
                        { outputAllowance = profile.MaxOutputTokens; escalated = true; }
                        else return await PauseStep("provider_value_too_large", "A single change exceeds this model's output allowance. Split the value or simplify that change.");
                        state.Checkpoint = MakeCheckpoint();
                        context.Observe($"The last response hit the OUTPUT limit. Nothing from it was applied. Base revision remains {state.Revision}. Return at most {maxOperations} operations, editing smaller properties. Never repeat the whole workflow.");
                        await Event("checkpoint", "recovering", "Response reached the output limit. Retrying a smaller complete step.", state.Checkpoint);
                        continue;
                    }
                    if (completion.FinishReason is "content_filter" or "refusal") throw new WorkflowAiException("provider_refusal", "The selected model declined this request.", 502);
                    return new(completion, tokens);
                }
            }

            async Task<AiTurnResultDto?> ExecuteCommandAsync(ModelStep step, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var completion = step.Completion ?? throw new InvalidOperationException("A complete model response is required.");
                var tokens = step.MaxOutputTokens;
                void ObserveCommand(string text) => context.Observe(Sanitize(text));
                try
                {
                    using var commandDoc = ParseProviderCommand(completion.Content, apiKey);
                    var commandNode = JsonNode.Parse(commandDoc.RootElement.GetRawText())!;
                    SanitizeCommand(commandNode.AsObject(), Sanitize);
                    var command = JsonSerializer.SerializeToElement(commandNode, JsonOptions);
                    var kind = RequiredString(command, "kind");
                    var nextPlan = command.TryGetProperty("plan", out _) ? RequiredString(command, "plan") : state.Plan;
                    if (nextPlan.Length > 10_000) throw new JsonException("Keep the remaining plan within 10000 characters.");
                    if (kind == "clarification")
                    {
                        var envelope = CommandEnvelope(command, request, apiKey, "clarification");
                        if (envelope.Questions.Count == 0 || envelope.Definition is not null) throw new JsonException("A clarification needs questions and no definition.");
                        state.Plan = nextPlan;
                        var clarification = Result(envelope, null, new(false, [], []), request) with { Checkpoint = MakeCheckpoint(), Run = Summary() };
                        await Event("result", "clarification", "Additional business details are needed.", result: clarification);
                        return clarification;
                    }
                    if (kind == "read")
                    {
                        if (!command.TryGetProperty("reads", out var reads)) throw new JsonException("The read command requires a reads array with 1 to 6 read objects.");
                        ObserveCommand(context.Read(reads, draft, Sanitize));
                        state.ContextReads = context.ReadCount;
                        state.DuplicateReads = context.DuplicateReadCount;
                        state.RepeatedDraftReads = context.RepeatedDraftReadCount;
                        var checkpointChanged = state.Plan != nextPlan || !state.Checkpoint!.ContextReads.SequenceEqual(context.RetainedReads());
                        state.Plan = nextPlan;
                        state.Checkpoint = MakeCheckpoint();
                        await Event(checkpointChanged ? "checkpoint" : "progress", "reading", "Reading relevant workflow references and requirements.", checkpointChanged ? state.Checkpoint : null);
                    }
                    else if (kind == "edit")
                    {
                        var batchId = RequiredString(command, "batchId");
                        var next = state.ApplyBatch(draft, command, redaction, original?.Id ?? draft.GetProperty("id").GetString()!, options.MaxWorkflowCharacters, maxOperations);
                        if (next is null)
                        {
                            ObserveCommand($"Batch {batchId} was already applied. Current revision {state.Revision}; submit new work or finish.");
                        }
                        else
                        {
                            draft = next.Value;
                            requirementsReview = null;
                            state.Plan = nextPlan;
                            state.AcceptedBatches++;
                            state.FirstEditSeconds ??= clock.Elapsed.TotalSeconds;
                            context.DraftChanged();
                            ObserveCommand($"Batch {batchId} accepted atomically. Current revision {state.Revision}. Continue remaining planned changes or finish.");
                            validationFailures = truncations = sameFailures = 0;
                            if (optimized)
                            {
                                fastBatches = completion.OutputTokens is { } used && used < tokens / 2
                                    && state.LastCallSeconds < options.RequestTimeoutSeconds / 2d ? fastBatches + 1 : 0;
                                if (fastBatches >= 2) { maxOperations = Math.Min(options.MaxOperationsPerBatch, maxOperations + 2); fastBatches = 0; }
                            }
                            else maxOperations = completion.OutputTokens is { } used && used >= tokens * .8
                                ? Math.Max(1, maxOperations / 2) : Math.Min(options.MaxOperationsPerBatch, maxOperations * 2);
                            state.Checkpoint = MakeCheckpoint();
                            await Event("checkpoint", "building", "Completed a workflow draft step.", state.Checkpoint);
                        }
                    }
                    else if (kind is "validate" or "finish" or "proposal")
                    {
                        var raw = draft;
                        if (kind == "proposal" && !command.TryGetProperty("definition", out raw)) throw new JsonException("The proposal command requires a definition object. Use finish to validate the existing draft without repeating it.");
                        if (raw.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(raw.GetRawText()) > options.MaxWorkflowCharacters) throw new JsonException("A bounded workflow object is required.");
                        var restored = redaction.Restore(raw);
                        var model = WorkflowAuthoringJson.Parse(restored.GetRawText());
                        if (original is not null && model.Id != original.Id) throw new JsonException("Edits must preserve the existing workflow id.");
                        await Event("progress", "validating", "Checking workflow structure, rules, and readiness.");
                        validation = ScrubDiagnostics(await ValidateModelAsync(model, deadline.Token), redaction, apiKey);
                        if (validation.IsValid && kind is "finish" or "proposal")
                        {
                            ValidateProposalReachability(model, original);
                            WorkflowAuthoringLayout.Apply(model, original);
                            validation = ScrubDiagnostics(await ValidateModelAsync(model, deadline.Token), redaction, apiKey);
                            if (!validation.IsValid) throw new JsonException("The final layout did not pass workflow validation.");
                            var definition = JsonSerializer.SerializeToElement(model, JsonOptions);
                            if (requirements is not null)
                            {
                                requirementsReview = await requirements.ReviewAsync(redaction.Redact(definition),
                                    AuthoringPackageBuilder.Hash(definition.GetRawText()), deadline.Token);
                                if (!requirementsReview.Passed)
                                {
                                    var unresolved = requirementsReview.Checks.Where(check => check.Status != "covered").ToArray();
                                    if (unresolved.Any(check => check.Status == "uncertain"))
                                    {
                                        var clarification = new AiTurnResultDto("clarification", "Clarify the unresolved business requirements before applying a proposal.",
                                            unresolved.Where(check => check.Status == "uncertain").Select(check => check.Explanation).Distinct().Take(10).ToArray(),
                                            null, [], [], [], validation, request.SnapshotId, knowledge.ContractHash)
                                        { RequirementsReview = requirementsReview, Checkpoint = MakeCheckpoint(), Run = Summary() };
                                        await Event("result", "clarification", clarification.Message, result: clarification);
                                        return clarification;
                                    }
                                    if (requirementRepairs++ >= options.MaxRepairAttempts)
                                    {
                                        var invalid = new AiTurnResultDto("invalid", "The workflow still has unresolved requirements after bounded repair.", [], null,
                                            [], [], [], validation, request.SnapshotId, knowledge.ContractHash)
                                        { RequirementsReview = requirementsReview, Checkpoint = MakeCheckpoint(), Run = Summary() };
                                        await Event("result", "invalid", invalid.Message, result: invalid);
                                        return invalid;
                                    }
                                    context.RequirementChecklist = requirements.Checklist;
                                    WorkflowAiTelemetry.Recovery("requirements", "builder");
                                    ObserveCommand(JsonSerializer.Serialize(new { requirementsReview.Checks, instruction = "Repair the missing requirements, preserving unrelated work, then finish again." }, JsonOptions));
                                    await Event("progress", "repairing", "Repairing requirements found by independent review.");
                                    return null;
                                }
                            }
                            var envelope = CommandEnvelope(command, request, apiKey, "proposal");
                            var result = Result(envelope, definition, validation, request) with { Run = Summary(), RequirementsReview = requirementsReview };
                            await Event("result", "complete", "The validated proposal is ready for review.", result: result);
                            return result;
                        }
                        ObserveCommand(Sanitize(JsonSerializer.Serialize(validation, JsonOptions)));
                        if (!validation.IsValid) throw new JsonException(string.Join("\n", validation.Errors.Take(12)));
                        state.Plan = nextPlan;
                        state.Checkpoint = MakeCheckpoint();
                        validationFailures = 0;
                    }
                    else throw new JsonException("Unknown command kind. Use read, edit, validate, finish, or clarification.");
                }
                catch (Exception error) when (IsDefinitionError(error) || error is KeyNotFoundException or InvalidOperationException)
                {
                    var diagnostic = Sanitize(SafeDiagnostic(error));
                    validation = Invalid(diagnostic);
                    var failure = AuthoringPackageBuilder.Hash(state.Revision + ":" + diagnostic);
                    sameFailures = failure == lastFailure ? sameFailures + 1 : 1;
                    lastFailure = failure;
                    if (++validationFailures > options.MaxRepairAttempts || sameFailures >= 3)
                    {
                        var invalid = new AiTurnResultDto("invalid", "The model could not complete a valid workflow. Review the diagnostics or refine the requirements.",
                            [], null, [], [], [], validation, request.SnapshotId, knowledge.ContractHash)
                        { Checkpoint = MakeCheckpoint(), Run = Summary() };
                        await Event("result", "invalid", "The draft needs correction.", result: invalid);
                        return invalid;
                    }
                    ObserveCommand("The command failed Flowbit validation; the private draft is unchanged. Correct this error using the actual schema and smaller edits: " + diagnostic);
                    WorkflowAiTelemetry.Recovery("validation", "builder");
                    await Event("progress", "repairing", "Repairing a workflow validation error.");
                }
                return null;
            }

            AiRunSummaryDto Summary() => state.Summary(clock.Elapsed.TotalSeconds, execution);
            async Task Event(string type, string stage, string message, AiCheckpointDto? checkpoint = null, AiTurnResultDto? result = null)
            {
                if (emit is null) return;
                await eventGate.WaitAsync(deadline.Token);
                try { await emit(new AiRunEventDto { RunId = state.Id, Sequence = ++state.Sequence, Type = type,
                    Stage = stage, Message = message, Checkpoint = checkpoint, Result = result, Run = Summary() }, deadline.Token); }
                finally { eventGate.Release(); }
            }
            async Task<AiTurnResultDto> FinishPaused(string code, string message)
            {
                var result = new AiTurnResultDto("paused", message, [], null, [], [], [], validation, request.SnapshotId, knowledge.ContractHash)
                    { Checkpoint = MakeCheckpoint(), Run = Summary() };
                if (emit is not null) await emit(new AiRunEventDto { RunId = state.Id, Sequence = ++state.Sequence, Type = "paused", Stage = "paused",
                    Code = code, Message = message, Result = result, Run = result.Run }, cancellationToken);
                return result;
            }
        }
        catch (WorkflowAiException error) when (error.Code == "run_budget" || options.RequirementsReviewEnabled
            && error.Code is "context_too_large" or "requirements_unverified" or "provider_timeout" or "provider_wait")
        {
            runActivity?.SetTag("outcome", "paused");
            var result = new AiTurnResultDto("paused", error.Message, [], null, [], [], [], Invalid("Generation or requirements review is incomplete."), request.SnapshotId, knowledge.ContractHash)
                { Checkpoint = state.Checkpoint, Run = state.Summary(clock.Elapsed.TotalSeconds, execution) };
            if (emit is not null) await emit(new AiRunEventDto { RunId = state.Id, Sequence = ++state.Sequence, Type = "paused", Code = error.Code,
                Message = result.Message, Result = result, Run = result.Run }, cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            runActivity?.SetTag("outcome", "paused");
            var result = new AiTurnResultDto("paused", "The authoring run reached its time limit. Continue from the last completed draft step.", [], null, [], [], [],
                Invalid("Generation is not complete."), request.SnapshotId, knowledge.ContractHash)
            { Checkpoint = state.Checkpoint, Run = state.Summary(clock.Elapsed.TotalSeconds, execution) };
            if (emit is not null) await emit(new AiRunEventDto { RunId = state.Id, Sequence = ++state.Sequence, Type = "paused", Code = "authoring_timeout", Message = result.Message, Result = result, Run = result.Run }, cancellationToken);
            return result;
        }
        catch (OperationCanceledException) { runActivity?.SetTag("outcome", "cancelled"); throw; }
        finally
        {
            concurrency.Semaphore.Release();
            RunDuration.Record(clock.Elapsed.TotalSeconds);
        }
    }

    private void ValidateCheckpoint(AiCheckpointDto checkpoint, string inputHash, string lockedId)
    {
        if (checkpoint.Version is not (1 or 2 or 3) || checkpoint.InputHash != inputHash || checkpoint.ContractHash != knowledge.ContractHash)
            throw new WorkflowAiException("stale_checkpoint", "The original inputs, catalog, or authoring contract changed. Start a new request.", 409);
        if (checkpoint.Revision < 0 || checkpoint.Revision > 1_000_000 || checkpoint.Plan is null || checkpoint.Plan.Length > 10_000
            || checkpoint.Batches is null || checkpoint.Batches.Count > 100 || checkpoint.Batches.Any(batch => batch is null || string.IsNullOrWhiteSpace(batch.Id) || batch.Id.Length > 100 || batch.Hash is not { Length: 64 })
            || checkpoint.Batches.Select(batch => batch.Id).Distinct(StringComparer.Ordinal).Count() != checkpoint.Batches.Count)
            throw new WorkflowAiException("invalid_checkpoint", "The continuation checkpoint is malformed.");
        if (checkpoint.OutputAllowance is < 1 or > 262_144 || checkpoint.MaxOperations is < 1 or > 100)
            throw new WorkflowAiException("invalid_checkpoint", "The continuation recovery limits are malformed.");
        if (checkpoint.ContextReads is null || checkpoint.ContextReads.Count > 18
            || checkpoint.ContextReads.Any(read => read is null || read.Kind is not ("reference" or "source")
                || read.Resource is not { Length: > 0 and <= 300 } || read.Offset < 0 || read.Count is < 1 or > 12_000)
            || checkpoint.ContextReads.Sum(read => (long)read.Count) > 32_000)
            throw new WorkflowAiException("invalid_checkpoint", "The continuation read positions exceed their limits.");
        try { WorkflowAiDraft.ValidateCandidate(checkpoint.Draft, string.IsNullOrEmpty(lockedId) ? checkpoint.Draft.GetProperty("id").GetString()! : lockedId, options.MaxWorkflowCharacters); }
        catch (Exception error) when (IsDefinitionError(error) || error is InvalidOperationException or KeyNotFoundException)
        { throw new WorkflowAiException("invalid_checkpoint", "The continuation draft is malformed or exceeds its limits."); }
    }

    private static JsonDocument ParseCommand(string response)
    {
        var text = response.Trim();
        if (text.StartsWith("```json", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal)) text = text[7..^3];
        // Some chat models surround one complete command with prose. Never join fragments or accept two objects.
        var firstObject = text.IndexOf('{');
        var lastObject = text.LastIndexOf('}');
        if (firstObject >= 0 && lastObject >= firstObject) text = text[firstObject..(lastObject + 1)];
        var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        try
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Return a JSON command object.");
            CheckMembers(doc.RootElement);
            var kind = RequiredString(doc.RootElement, "kind");
            var allowed = new HashSet<string>(["kind", "message", "plan"], StringComparer.Ordinal);
            allowed.UnionWith(kind switch
            {
                "read" => ["reads"],
                "edit" => ["baseRevision", "batchId", "operations"],
                "validate" => Array.Empty<string>(),
                "finish" => ["assumptions", "dependencies", "changeSummary", "sourceReferences"],
                "proposal" or "clarification" => ["questions", "definition", "assumptions", "dependencies", "changeSummary", "sourceReferences"],
                _ => throw new JsonException("Unknown command kind. Use read, edit, validate, finish, or clarification.")
            });
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject()) if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw new JsonException("Unknown or duplicate command property: " + property.Name);
            return doc;
        }
        catch { doc.Dispose(); throw; }

        static void CheckMembers(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON command member: " + property.Name);
                    CheckMembers(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) CheckMembers(child);
        }
    }

    private static void SanitizeCommand(JsonObject command, Func<string, string> sanitize)
    {
        foreach (var name in new[] { "message", "plan", "questions", "assumptions", "dependencies", "changeSummary", "sourceReferences", "definition" })
        {
            if (command[name] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) command[name] = sanitize(text);
            else if (command[name] is { } node) SanitizeValues(node, sanitize);
        }
        if (command["operations"] is JsonArray operations)
            foreach (var operation in operations.OfType<JsonObject>())
                if (operation["value"] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) operation["value"] = sanitize(text);
                else if (operation["value"] is { } value) SanitizeValues(value, sanitize);
    }

    private static Envelope CommandEnvelope(JsonElement command, AiTurnRequestDto request, string apiKey, string kind)
    {
        var node = new JsonObject { ["kind"] = kind, ["message"] = command.TryGetProperty("message", out var message) ? JsonNode.Parse(message.GetRawText()) : "Review the workflow proposal." };
        foreach (var name in new[] { "questions", "assumptions", "dependencies", "changeSummary", "sourceReferences", "definition" })
            if (command.TryGetProperty(name, out var value)) node[name] = JsonNode.Parse(value.GetRawText());
        return ParseEnvelope(node.ToJsonString(), request.Sources, apiKey);
    }

}
