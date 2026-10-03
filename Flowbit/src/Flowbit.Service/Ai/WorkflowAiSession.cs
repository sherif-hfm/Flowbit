using System.Text.Json;
using Flowbit.Service.Authoring;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Ai;

/// <summary>Request-local state shared by text and native-tool execution. No provider or SDK dependencies.</summary>
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

    public JsonElement? ApplyBatch(JsonElement draft, JsonElement command, WorkflowAiRedaction redaction,
        string lockedId, int maxBytes, int maxOperations)
    {
        var batchId = command.GetProperty("batchId").GetString();
        if (string.IsNullOrWhiteSpace(batchId) || batchId.Length > 100) throw new JsonException("Use a nonempty batchId up to 100 characters.");
        var hash = AuthoringPackageBuilder.Hash(command.GetRawText());
        var receipt = Batches.FirstOrDefault(batch => batch.Id == batchId);
        if (receipt is not null)
        {
            if (receipt.Hash != hash) throw new JsonException("This batchId was already used for different operations.");
            return null;
        }
        if (command.GetProperty("baseRevision").GetInt64() != Revision) throw new JsonException($"Stale draft revision; expected {Revision}.");
        var next = WorkflowAiDraft.Apply(draft, command.GetProperty("operations"), redaction, lockedId, maxBytes, maxOperations);
        if (next.GetRawText() == draft.GetRawText()) throw new JsonException("The batch made no changes; inspect remaining work or finish.");
        Revision++;
        Batches.Add(new(batchId, hash));
        if (Batches.Count > 100) Batches.RemoveAt(0);
        return next;
    }

    public AiRunSummaryDto Summary(double seconds, AiExecutionSettings execution)
    {
        return new(Calls, OutputTokens, Estimated, seconds)
        {
            ExecutionVariant = execution.Variant, ReasoningEffort = execution.ReasoningEffort,
            InputTokens = InputTokens, FirstEditSeconds = FirstEditSeconds, LastCallSeconds = LastCallSeconds,
            ContextReads = ContextReads, DuplicateReads = DuplicateReads, Retries = Retries, AcceptedBatches = AcceptedBatches
        };
    }
}
