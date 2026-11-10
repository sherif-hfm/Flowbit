using Flowbit.Shared.Models;

namespace Flowbit.Service.Models;

/// <summary>Already grouped evidence, never a page of individual visits.</summary>
public sealed record InstanceDiagramTraversalEvidence(int NodeId, int FlowId, long Count);

public sealed record InstanceDiagramHistorySnapshot
{
    public required long InstanceId { get; init; }
    public required long WorkflowId { get; init; }
    public required int WorkflowVersion { get; init; }
    public required DateTimeOffset InstanceUpdatedAt { get; init; }
    public required DateTimeOffset AsOf { get; init; }
    public required WorkflowModel Definition { get; init; }
    public required bool NodeActivityFromCreation { get; init; }
    public DateTimeOffset? NodeActivityPrunedAt { get; init; }
    public DateTimeOffset? HistoryPrunedAt { get; init; }
    public bool HasCutoverSeeded { get; init; }
    public bool HasConflictingEntries { get; init; }
    public bool HasUnmappedMultiInstanceNodes { get; init; }
    public IReadOnlyList<int> ReachedNodeIds { get; init; } = [];
    public IReadOnlyList<InstanceDiagramTraversalEvidence> Exits { get; init; } = [];
    public IReadOnlyList<InstanceDiagramTraversalEvidence> Entries { get; init; } = [];
    public IReadOnlyList<InstanceDiagramTraversalEvidence> MultiInstanceEntries { get; init; } = [];
}
