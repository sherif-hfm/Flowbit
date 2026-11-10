namespace Flowbit.Shared.Dtos;

/// <summary>Retained node and traversal evidence for the instance's current exact definition.</summary>
public sealed record InstanceDiagramHistoryDto(
    long InstanceId,
    long WorkflowId,
    int WorkflowVersion,
    DateTimeOffset InstanceUpdatedAt,
    DateTimeOffset AsOf,
    string Scope,
    InstanceDiagramHistoryCoverageDto Coverage,
    IReadOnlyList<InstanceDiagramHistoryNodeDto> Nodes,
    IReadOnlyList<InstanceDiagramHistoryFlowDto> Flows);

public sealed record InstanceDiagramHistoryCoverageDto(
    string Status,
    IReadOnlyList<string> Reasons,
    DateTimeOffset? NodeActivityPrunedAt);

public sealed record InstanceDiagramHistoryNodeDto(int NodeId);

public sealed record InstanceDiagramHistoryFlowDto(int FlowId, long TraversalCount);
