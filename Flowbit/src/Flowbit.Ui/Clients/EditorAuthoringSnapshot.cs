using System.Text.Json;

namespace Flowbit.Ui.Clients;

public sealed record EditorAuthoringSnapshot(string SnapshotId, JsonElement Definition);

public sealed record EditorAiProposal(JsonElement Definition, string SnapshotId, string OperationId);
