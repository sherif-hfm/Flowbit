using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Authoring;
using Flowbit.Service.Services;
using Flowbit.Shared.Authoring;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Flowbit.Service.Ai;

/// <summary>
/// One turn of the authoring agent, ported from Flowbit AI Lab v3 (Steps/GenerateStep.cs). The host sends the user's exact message;
/// the agent reads the docs, writes workflow.json and open-questions.md itself and may check its work. When it ends its turn, the
/// host checks workflow.json with Flowbit's validation and sends the errors back at most MaxNudges times. A valid workflow is laid
/// out and returned as a proposal for review; nothing is saved, published or executed.
/// </summary>
public sealed class WorkflowAiConversation(
    IWorkflowAiChatClientFactory clients,
    WorkflowAiDocs docs,
    IWorkflowDefinitionValidator validator,
    WorkflowAiOptions options,
    string contractHash,
    Func<WorkflowModel, CancellationToken, Task<AiValidationResultDto>> validateProposal)
{
    /// <summary>The line put before the user's message when the editor's diagram replaced workflow.json (user decision, 2026-10-16).</summary>
    public const string SyncNote = "workflow.json now contains the current diagram from the editor.";
    public const int MaxErrorsPerNudge = 40;
    private const int MaxProgressEvents = 250;
    private const int MaxMessageCharacters = 20_000;
    private const int MaxOpenQuestionsCharacters = 100_000;
    private static readonly JsonSerializerOptions DefinitionOptions = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 128 };
    private static readonly JsonSerializerOptions FileOptions = new()
        { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 128 };

    /// <summary>How often a waiting model call reports progress, so the stream stays alive through long reasoning.</summary>
    public TimeSpan WaitInterval { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<AiTurnResultDto> RunAsync(AiTurnRequestDto request, string apiKey, string? sharedVariables,
        Func<AiRunEventDto, CancellationToken, Task>? emit, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var runId = Guid.NewGuid().ToString("N");
        long sequence = 0;
        var progressEvents = 0;
        var meter = new WorkflowAiRunMeter(options.MaxProviderCalls);
        var nudges = 0;
        var activity = new List<string>();
        using var runActivity = WorkflowAiTelemetry.Start("authoring.run");
        runActivity?.SetTag("outcome", "failed");

        if (request.CurrentWorkflow is { } supplied && supplied.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new WorkflowAiException("invalid_current_workflow", "The current workflow must be a canonical JSON object.");
        var editorJson = request.CurrentWorkflow is { ValueKind: JsonValueKind.Object } current ? current : (JsonElement?)null;
        WorkflowModel? baseline = null;
        if (editorJson is { } editorElement)
        {
            try { baseline = WorkflowAuthoringJson.Parse(editorElement.GetRawText()); }
            catch (JsonException) { throw new WorkflowAiException("invalid_current_workflow", "The editor workflow does not match Flowbit's canonical contract. Fix it before asking the assistant."); }
        }

        var reasoning = options.OpenCodeReasoningEfforts.GetValueOrDefault(request.ModelId);
        var saved = request.State is { } returned ? WorkflowAiConversationState.Decode(returned, apiKey) : null;
        if (saved is not null && saved.ConversationId != request.ConversationId)
            throw new WorkflowAiException("invalid_state", "The conversation state belongs to another conversation. Start a new conversation.");
        if (saved is not null && (saved.AgentId != WorkflowAiAgent.AgentId || saved.ProviderId != clients.Descriptor.Id || saved.ModelId != request.ModelId
            || saved.Endpoint != clients.Endpoint || saved.ReasoningEffort != reasoning))
            throw new WorkflowAiException("checkpoint_configuration_changed",
                "The model or provider settings changed since this conversation started. Start a new conversation.", 409);
        if (request.Continue && (saved is null || saved.Pending.Count == 0))
            throw new WorkflowAiException("nothing_to_continue", "There is no stopped run to continue. Send a message instead.", 409);

        var redaction = new WorkflowAiRedaction(saved?.RedactionNonce);
        var redactedEditor = editorJson is { } raw ? redaction.Redact(raw) : (JsonElement?)null;
        var editorHash = redactedEditor is { } redactedDiagram ? SemanticHash(redactedDiagram.GetRawText()) : null;
        var check = new WorkflowAiWorkflowCheck(validator, redaction, apiKey, baseline);
        var workspace = new WorkflowAiWorkspace(docs, saved?.Files, saved?.ReturnedReads, check, tool =>
        {
            activity.Add(tool.Summary);
            WorkflowAiTelemetry.ToolCalls.Add(1, new KeyValuePair<string, object?>("tool", tool.Tool), new KeyValuePair<string, object?>("success", tool.Success));
        });
        var sentSources = new List<string>(saved?.SentSources ?? []);
        var sharedVariablesHash = saved?.SharedVariablesHash;

        IList<ChatMessage> input;
        string? turnStartHash;
        if (request.Continue)
        {
            if (editorHash != saved!.ExpectedEditorHash)
                throw new WorkflowAiException("stale_checkpoint", "The diagram changed after this run stopped. Send a message to continue with the current diagram.", 409);
            input = saved.Pending.ToList();
            turnStartHash = saved.TurnStartWorkflowHash;
        }
        else
        {
            var text = new StringBuilder();
            if (saved is null)
            {
                // An existing diagram is the agent's current workflow; for a new diagram the agent creates the file itself.
                if (redactedEditor is { } diagram && baseline is { FlowNodes.Count: > 0 }) workspace.SetOutput(WorkflowAiAgent.WorkflowFile, Pretty(diagram));
            }
            else if (redactedEditor is { } diagram && editorHash != saved.ExpectedEditorHash
                && (workspace.Outputs.GetValueOrDefault(WorkflowAiAgent.WorkflowFile) is not { } file || SemanticHash(file) != editorHash))
            {
                // The user changed the diagram since the agent last saw it.
                workspace.SetOutput(WorkflowAiAgent.WorkflowFile, Pretty(diagram));
                text.Append(SyncNote).Append("\n\n");
            }
            // The message as typed, except credential values from the workflow, which never reach the provider.
            text.Append(redaction.Sanitize(request.Message));
            foreach (var page in request.Sources)
            {
                var key = AuthoringPackageBuilder.Hash(page.SourceName + "\n" + page.PageNumber + "\n" + page.Text);
                if (sentSources.Contains(key)) continue;
                sentSources.Add(key);
                text.Append($"\n\n--- {page.SourceName}, page {page.PageNumber} ---\n").Append(redaction.Sanitize(page.Text));
            }
            if (sharedVariables is not null && AuthoringPackageBuilder.Hash(sharedVariables) != sharedVariablesHash)
            {
                sharedVariablesHash = AuthoringPackageBuilder.Hash(sharedVariables);
                text.Append("\n\n--- Selected shared variables ---\n").Append(sharedVariables);
            }
            input = [.. saved?.Pending ?? [], new ChatMessage(ChatRole.User, text.ToString())];
            turnStartHash = workspace.Outputs.TryGetValue(WorkflowAiAgent.WorkflowFile, out var start) ? AuthoringPackageBuilder.Hash(start) : null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.RunTimeoutSeconds));
        AgentSession? session = null;
        AiConversationStateDto? latest = null;
        var agent = WorkflowAiAgent.Create(clients.Create(request.ModelId, apiKey, request.ConversationId, meter, TimeSpan.FromSeconds(options.RunTimeoutSeconds)),
            workspace.CreateTools(), request.ModelId, reasoning, options.MaxModelOutputTokens, options.MaxProviderCalls,
            inner => new WorkflowAiCheckpointingChatClient(inner, CallStartingAsync, WaitingAsync, WaitInterval));
        try
        {
            session = saved is null ? await agent.CreateSessionAsync(deadline.Token)
                : await agent.DeserializeSessionAsync(saved.Session, cancellationToken: deadline.Token);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            throw new WorkflowAiException("invalid_state", "The assistant's conversation state is invalid or too large. Start a new conversation.");
        }
        if (saved is not null) WorkflowAiConversationState.CheckHistory(History(session), saved.Pending, saved.HistoryMessages);
        // Until the first model call takes its own checkpoint, a stop resumes from here.
        latest = Encode(await agent.SerializeSessionAsync(session, cancellationToken: deadline.Token), input.ToArray(), editorHash, turnStartHash);

        string kind, message;
        string? code = null, finalText = null;
        WorkflowAiCheckResult? gate = null;
        await EmitAsync("progress", "starting", request.Continue ? "Continuing the stopped run." : "Sending your message to the model.");
        try
        {
            while (true)
            {
                var response = await agent.RunAsync(input, session, cancellationToken: deadline.Token);
                finalText = response.Text;
                var file = workspace.Outputs.GetValueOrDefault(WorkflowAiAgent.WorkflowFile);
                if (file is not null && turnStartHash is not null && AuthoringPackageBuilder.Hash(file) == turnStartHash)
                {
                    kind = "answer";
                    break;
                }
                string nudge;
                if (file is null)
                    nudge = $"No output/{WorkflowAiAgent.WorkflowFile} has been saved yet.\nSave the complete canonical Flowbit workflow JSON with save_append_file as {WorkflowAiAgent.WorkflowFile}.";
                else
                {
                    await EmitAsync("progress", "validating", "Checking the saved workflow with Flowbit's validation.");
                    gate = check.Check(file);
                    if (gate.Valid)
                    {
                        kind = "proposal";
                        break;
                    }
                    nudge = Nudge(gate.Errors);
                }
                if (nudges >= options.MaxNudges || meter.RemainingRequests == 0)
                {
                    kind = "invalid";
                    code = file is null ? "not_saved" : "invalid_workflow";
                    break;
                }
                nudges++;
                WorkflowAiTelemetry.Nudges.Add(1);
                await EmitAsync("progress", "nudge", file is null
                    ? $"The model has not saved workflow.json; asking it to save the workflow ({nudges} of {options.MaxNudges})."
                    : $"The saved workflow has {Count(gate!.Errors.Count, "error")}; sending them to the model ({nudges} of {options.MaxNudges}).");
                input = [new ChatMessage(ChatRole.User, nudge)];
            }
            message = Scrub(finalText, redaction, apiKey, MaxMessageCharacters) ?? "";
        }
        catch (WorkflowAiStopException stop)
        {
            (kind, code, message) = ("paused", stop.Code, stop.Message);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            (kind, code, message) = ("paused", "authoring_timeout",
                $"The run reached its {options.RunTimeoutSeconds}-second limit. Continue to resume from the last completed step.");
        }
        catch (Exception error) when (error is not (OperationCanceledException or WorkflowAiException))
        {
            runActivity?.SetTag("error.type", error.GetType().Name);
            (kind, code, message) = ("paused", "authoring_interrupted", "AI authoring was interrupted. Continue to resume from the last completed step.");
        }

        JsonElement? definition = null;
        var validation = new AiValidationResultDto(false, [], []);
        var workflow = workspace.Outputs.GetValueOrDefault(WorkflowAiAgent.WorkflowFile);
        var expectedEditorHash = editorHash;
        if (kind == "proposal")
        {
            var model = WorkflowAuthoringJson.Parse(gate!.RestoredJson!);
            if (baseline is not null) model.Id = baseline.Id;
            validation = Scrub(await validateProposal(model, cancellationToken), redaction, apiKey);
            if (validation.IsValid)
            {
                WorkflowAuthoringLayout.Apply(model, baseline is { FlowNodes.Count: > 0 } ? baseline : null);
                validation = Scrub(await validateProposal(model, cancellationToken), redaction, apiKey);
            }
            var serialized = JsonSerializer.SerializeToElement(model, DefinitionOptions);
            if (!validation.IsValid) (kind, code) = ("invalid", "invalid_workflow");
            else if (Encoding.UTF8.GetByteCount(serialized.GetRawText()) > options.MaxWorkflowCharacters)
                (kind, code, validation) = ("invalid", "workflow_too_large", new(false, ["The workflow exceeds the authoring size limit."], []));
            else
            {
                definition = serialized;
                // If the user applies this proposal, the editor will show it; a later turn then needs no sync.
                expectedEditorHash = SemanticHash(workflow!);
                if (message.Length == 0) message = "The workflow passed Flowbit's checks and is ready for review.";
            }
        }
        if (kind == "invalid" && code is "invalid_workflow" && gate is { Valid: false })
            validation = new(false, gate.Errors.Select(error => error.Describe()).ToArray(), []);
        if (kind == "answer" && message.Length == 0) message = "The model answered without changing the workflow.";
        if (kind == "invalid" && message.Length == 0)
            message = code == "not_saved" ? "The model did not save a workflow." : "The saved workflow still fails Flowbit validation.";

        // A finished turn stores its whole conversation; a stopped run keeps the checkpoint of its last model call.
        var state = kind == "paused" ? latest
            : Encode(await agent.SerializeSessionAsync(session, cancellationToken: CancellationToken.None), [], expectedEditorHash, null);
        var result = new AiTurnResultDto(kind, message, definition, validation, request.SnapshotId, contractHash)
        {
            Code = code,
            OpenQuestions = Scrub(workspace.Outputs.GetValueOrDefault(WorkflowAiAgent.OpenQuestionsFile), redaction, apiKey, MaxOpenQuestionsCharacters),
            State = state,
            Run = Summary()
        };
        runActivity?.SetTag("outcome", code ?? kind);
        runActivity?.SetTag("requests", meter.Requests);
        runActivity?.SetTag("nudges", nudges);
        WorkflowAiTelemetry.RunSeconds.Record(clock.Elapsed.TotalSeconds);
        if (emit is not null)
            await emit(new AiRunEventDto { RunId = runId, Sequence = ++sequence, Type = kind == "paused" ? "paused" : "result", Stage = kind,
                Code = code, Message = message, Result = result, Run = result.Run, State = state }, cancellationToken);
        return result;

        async Task CallStartingAsync(WorkflowAiCallStart start, CancellationToken token)
        {
            latest = Encode(start.Session, start.Pending, editorHash, turnStartHash);
            var summary = activity.Count == 0 ? "" : " " + string.Join("; ", activity) + ".";
            activity.Clear();
            await EmitAsync("checkpoint", "model", $"Model call {meter.Requests + 1} of {meter.MaxRequests}.{summary}", latest);
        }

        Task WaitingAsync(TimeSpan elapsed, CancellationToken token) =>
            EmitAsync("progress", "model", $"Waiting for the model: {(int)elapsed.TotalSeconds} s on this call.");

        async Task EmitAsync(string type, string stage, string text, AiConversationStateDto? state = null)
        {
            if (emit is null || progressEvents >= MaxProgressEvents) return;
            progressEvents++;
            await emit(new AiRunEventDto { RunId = runId, Sequence = ++sequence, Type = type, Stage = stage, Message = text, State = state, Run = Summary() },
                cancellationToken);
        }

        AiConversationStateDto Encode(JsonElement sessionJson, IReadOnlyList<ChatMessage> pending, string? expected, string? turnStart)
        {
            var (nodes, flows) = Counts(workspace.Outputs.GetValueOrDefault(WorkflowAiAgent.WorkflowFile));
            return WorkflowAiConversationState.Encode(new WorkflowAiStatePayload
            {
                AgentId = WorkflowAiAgent.AgentId, ProviderId = clients.Descriptor.Id, ModelId = request.ModelId, Endpoint = clients.Endpoint,
                ReasoningEffort = reasoning, PromptFingerprint = WorkflowAiAgent.PromptFingerprint, DocsFingerprint = docs.Fingerprint,
                ConversationId = request.ConversationId, RedactionNonce = redaction.Nonce, Session = sessionJson, Pending = pending,
                HistoryMessages = session is null ? 0 : History(session).Count,
                Files = new(workspace.Outputs, StringComparer.Ordinal), ReturnedReads = workspace.ReturnedReads,
                ExpectedEditorHash = expected, TurnStartWorkflowHash = turnStart, SentSources = sentSources, SharedVariablesHash = sharedVariablesHash
            }, nodes, flows);
        }

        AiRunSummaryDto Summary()
        {
            var (nodes, flows) = Counts(workspace.Outputs.GetValueOrDefault(WorkflowAiAgent.WorkflowFile));
            return new(meter.Requests, meter.OutputTokens, meter.UsageMissing, Math.Round(clock.Elapsed.TotalSeconds, 2))
            {
                InputTokens = meter.InputTokens, CachedInputTokens = meter.CachedInputTokens, MaxProviderCalls = meter.MaxRequests,
                Nudges = nudges, WorkflowNodes = nodes, WorkflowFlows = flows,
                ReasoningEffort = reasoning, PromptFingerprint = WorkflowAiAgent.PromptFingerprint, DocsFingerprint = docs.Fingerprint
            };
        }
    }

    /// <summary>The host's follow-up for a saved workflow that fails validation, verbatim from Flowbit AI Lab v3.</summary>
    internal static string Nudge(IReadOnlyList<WorkflowAiError> errors)
    {
        var lines = errors.Take(MaxErrorsPerNudge).Select(error => "- " + error.Describe());
        var more = errors.Count > MaxErrorsPerNudge ? $"\n- ... and {errors.Count - MaxErrorsPerNudge} more errors." : "";
        return $"The saved output/{WorkflowAiAgent.WorkflowFile} failed Flowbit validation with {errors.Count} error(s).\n"
            + string.Join("\n", lines) + more
            + $"\nFix these problems and save the complete corrected workflow again with save_append_file as {WorkflowAiAgent.WorkflowFile}.";
    }

    /// <summary>
    /// A hash of what a diagram means, ignoring layout and the workflow key: the editor and the agent's file compare equal when only
    /// positions, the key or canonical formatting differ. Null when the text is not a canonical workflow.
    /// </summary>
    internal static string? SemanticHash(string text)
    {
        try
        {
            var model = WorkflowAuthoringJson.Parse(text);
            WorkflowModelMigrator.Normalize(model);
            model.Id = "";
            foreach (var node in model.FlowNodes)
            {
                (node.X, node.Y) = (0, 0);
                if (BpmnFlowNodeTypes.IsBoundary(node.Type)) node.LaneId = null;
            }
            foreach (var lane in model.Lanes) (lane.X, lane.Y, lane.W, lane.H) = (0, 0, 0, 0);
            return AuthoringPackageBuilder.Hash(JsonSerializer.Serialize(model, DefinitionOptions));
        }
        catch (Exception error) when (error is JsonException or WorkflowDomainException or FormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Pretty(JsonElement workflow) => JsonSerializer.Serialize(workflow, FileOptions) + "\n";

    private static IReadOnlyList<ChatMessage> History(AgentSession session) =>
        session.TryGetInMemoryChatHistory(out var history) && history is not null ? history : [];

    private static (int? Nodes, int? Flows) Counts(string? workflow)
    {
        if (workflow is null) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(workflow);
            static int? Length(JsonElement root, string name) =>
                root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : null;
            return (Length(document.RootElement, "flowNodes"), Length(document.RootElement, "sequenceFlows"));
        }
        catch (JsonException) { return (null, null); }
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string? Scrub(string? text, WorkflowAiRedaction redaction, string apiKey, int limit)
    {
        if (text is null) return null;
        var scrubbed = redaction.Sanitize(text)
            .Replace(apiKey, "[provider key removed]", StringComparison.Ordinal)
            .Replace(JsonEncodedText.Encode(apiKey).ToString(), "[provider key removed]", StringComparison.Ordinal)
            .Trim();
        return scrubbed.Length <= limit ? scrubbed : scrubbed[..limit] + "…";
    }

    private static AiValidationResultDto Scrub(AiValidationResultDto result, WorkflowAiRedaction redaction, string apiKey)
    {
        string Clean(string text) => Scrub(text, redaction, apiKey, 1500)!;
        return result with
        {
            Errors = result.Errors.Select(Clean).ToArray(),
            Warnings = result.Warnings.Select(Clean).ToArray(),
            SaveBlockers = result.SaveBlockers.Select(Clean).ToArray(),
            PublicationBlockers = result.PublicationBlockers.Select(Clean).ToArray()
        };
    }
}
