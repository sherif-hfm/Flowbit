using System.Text.Json;
using Flowbit.Service.Authoring;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Ai;

/// <summary>Request-local draft and accounting shared by current and optimized authoring execution.</summary>
internal sealed class WorkflowAiSession
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public long Sequence, Revision, OutputTokens, InputTokens;
    public int Calls, Retries, AcceptedBatches, ContextReads, DuplicateReads;
    public bool Estimated;
    public double LastCallSeconds;
    public double? FirstEditSeconds;
    public string Plan = "";
    public List<AiBatchReceiptDto> Batches { get; } = [];
    public AiCheckpointDto? Checkpoint;
    internal readonly object AccountingSync = new();
    internal long ReservedOutputTokens;
    internal int ActiveCalls, PeakCalls, RepeatedDraftReads;
    internal double TotalProviderSeconds;

    public JsonElement? ApplyBatch(JsonElement draft, JsonElement command, WorkflowAiRedaction redaction,
        string lockedId, int maxBytes, int maxOperations)
    {
        using var activity = WorkflowAiTelemetry.Start("draft.apply");
        activity?.SetTag("draft.revision", Revision);
        var batchId = command.TryGetProperty("batchId", out var batch) && batch.ValueKind == JsonValueKind.String ? batch.GetString() : null;
        if (string.IsNullOrWhiteSpace(batchId) || batchId.Length > 100) throw new JsonException("Use a nonempty batchId up to 100 characters.");
        var hash = AuthoringPackageBuilder.Hash(command.GetRawText());
        var receipt = Batches.FirstOrDefault(batch => batch.Id == batchId);
        if (receipt is not null)
        {
            if (receipt.Hash != hash) throw new JsonException("This batchId was already used for different operations.");
            activity?.SetTag("outcome", "idempotent");
            return null;
        }
        if (!command.TryGetProperty("baseRevision", out var revision) || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var number))
            throw new JsonException($"The edit command requires integer baseRevision; current revision is {Revision}.");
        if (number != Revision) throw new JsonException($"Stale draft revision; expected {Revision}.");
        if (!command.TryGetProperty("operations", out var operations)) throw new JsonException("The edit command requires an operations array of complete typed edits.");
        var next = WorkflowAiDraft.Apply(draft, operations, redaction, lockedId, maxBytes, maxOperations);
        if (next.GetRawText() == draft.GetRawText()) throw new JsonException("The batch made no changes; inspect remaining work or finish.");
        Revision++;
        activity?.SetTag("edit.count", operations.GetArrayLength());
        activity?.SetTag("outcome", "accepted");
        Batches.Add(new(batchId, hash));
        if (Batches.Count > 100) Batches.RemoveAt(0);
        return next;
    }

    public AiRunSummaryDto Summary(double seconds, AiExecutionSettings execution)
    {
        lock (AccountingSync) return new(Calls, OutputTokens, Estimated, seconds)
        {
            ExecutionVariant = execution.Variant, ReasoningEffort = execution.ReasoningEffort,
            InputTokens = InputTokens, FirstEditSeconds = FirstEditSeconds, LastCallSeconds = LastCallSeconds,
            ContextReads = ContextReads, DuplicateReads = DuplicateReads, Retries = Retries, AcceptedBatches = AcceptedBatches,
            ActiveProviderCalls = ActiveCalls, PeakProviderCalls = PeakCalls, TotalProviderSeconds = TotalProviderSeconds,
            RepeatedDraftReads = RepeatedDraftReads
        };
    }
}
