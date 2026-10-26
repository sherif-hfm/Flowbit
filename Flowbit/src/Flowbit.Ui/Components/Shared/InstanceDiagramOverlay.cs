using System.Text.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Components.Shared;

/// <summary>Node states drawn by the instance diagram.</summary>
internal static class InstanceDiagramStates
{
    internal const string Active = "active";
    internal const string Ended = "ended";
    internal const string Faulted = "faulted";
    internal const string Cancelled = "cancelled";
}

/// <summary>A node the diagram marks, and the execution positions that put it there.</summary>
internal sealed record InstanceDiagramMarker(
    int NodeId,
    string State,
    string? Badge,
    string Label,
    IReadOnlyList<ExecutionPositionDto> Positions);

/// <summary>One entry of the viewer's <c>setOverlay</c> argument.</summary>
internal sealed record InstanceDiagramViewerNode(int Id, string State, string? Badge, string Label);

/// <summary>The viewer's <c>setOverlay</c> argument.</summary>
internal sealed record InstanceDiagramViewerOverlay(IReadOnlyList<InstanceDiagramViewerNode> Nodes);

/// <summary>
/// Derives the instance diagram's node markers from instance detail: where a
/// running instance is, or where a finished one ended. It never describes the
/// path taken. Pure: it reads only the supplied DTO.
/// </summary>
internal sealed class InstanceDiagramOverlay
{
    private static readonly JsonSerializerOptions SignatureOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<int, InstanceDiagramMarker> markersByNode;

    private InstanceDiagramOverlay(IReadOnlyList<InstanceDiagramMarker> markers)
    {
        Markers = markers;
        markersByNode = markers.ToDictionary(marker => marker.NodeId);
        ViewerOverlay = new InstanceDiagramViewerOverlay(markers
            .Select(marker => new InstanceDiagramViewerNode(
                marker.NodeId, marker.State, marker.Badge, marker.Label))
            .ToList());
        Signature = JsonSerializer.Serialize(ViewerOverlay, SignatureOptions);
    }

    internal static InstanceDiagramOverlay Empty { get; } = new([]);

    internal IReadOnlyList<InstanceDiagramMarker> Markers { get; }

    internal InstanceDiagramViewerOverlay ViewerOverlay { get; }

    /// <summary>Changes exactly when the viewer overlay changes.</summary>
    internal string Signature { get; }

    internal InstanceDiagramMarker? Find(int nodeId) => markersByNode.GetValueOrDefault(nodeId);

    internal static InstanceDiagramOverlay Build(InstanceDetailDto instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.ExecutionPositions.Count == 0) return BuildLegacy(instance);

        var running = ExecutionPositionText.Is(instance.Status, "running");
        var lastRunStart = running ? 0 : LastRunStartTokenId(instance);
        var markers = instance.ExecutionPositions
            .Where(position => position.TokenId >= lastRunStart)
            .Select(position => new MarkedPosition(position, running ? ActiveState(position) : FinalState(position)))
            .Where(item => item.State is not null)
            .GroupBy(item => item.Position.NodeId)
            .OrderBy(group => group.Key)
            .Select(group => CreateMarker(
                instance,
                group.Key,
                group.OrderBy(item => item.Position.TokenId).ToList()))
            .ToList();
        return new InstanceDiagramOverlay(markers);
    }

    internal static MultiInstanceProgressDto? FindProgress(InstanceDetailDto instance, long executionId) =>
        instance.MultiInstances.FirstOrDefault(progress => progress.ExecutionId == executionId)
        ?? (instance.MultiInstance?.ExecutionId == executionId ? instance.MultiInstance : null);

    private static string? ActiveState(ExecutionPositionDto position) =>
        ExecutionPositionText.Is(position.TokenStatus, "active") ? InstanceDiagramStates.Active : null;

    // Reactivation keeps the earlier runs' finished tokens and starts the next run
    // with a new root token, so the last run's tokens have that token's id or a
    // later one. The instance keeps that id after retention removes its history;
    // the history entry covers a response without it.
    private static long LastRunStartTokenId(InstanceDetailDto instance) =>
        instance.LastReactivationTokenId
        ?? instance.History
            .Where(entry => entry.Note == "instanceReactivated")
            .Max(entry => entry.TokenId)
        ?? 0;

    // Final positions only: tokens that ended at an end event, faulted, or were
    // cancelled with the instance. Earlier scope cancellations are history.
    // Tokens finished before termination reasons were recorded have none; their
    // instances had a single token, which finished where the instance did.
    private static string? FinalState(ExecutionPositionDto position)
    {
        if (ExecutionPositionText.Is(position.TokenStatus, "faulted")) return InstanceDiagramStates.Faulted;
        if (ExecutionPositionText.Is(position.TokenStatus, "completed")
            && (position.TerminationReason is null
                || ExecutionPositionText.Is(position.TerminationReason, "normalEnd")
                || ExecutionPositionText.Is(position.TerminationReason, "terminateEnd")))
        {
            return InstanceDiagramStates.Ended;
        }

        if (ExecutionPositionText.Is(position.TokenStatus, "cancelled")
            && (position.TerminationReason is null
                || ExecutionPositionText.Is(position.TerminationReason, "instanceCancelled")
                || ExecutionPositionText.Is(position.TerminationReason, "terminateEnd")
                || ExecutionPositionText.Is(position.TerminationReason, "errorEnd")))
        {
            return InstanceDiagramStates.Cancelled;
        }

        return null;
    }

    private static int StatePrecedence(string state) => state switch
    {
        InstanceDiagramStates.Faulted => 3,
        InstanceDiagramStates.Cancelled => 2,
        InstanceDiagramStates.Ended => 1,
        _ => 0
    };

    private static InstanceDiagramMarker CreateMarker(
        InstanceDetailDto instance,
        int nodeId,
        IReadOnlyList<MarkedPosition> items)
    {
        var state = items.Select(item => item.State!).MaxBy(StatePrecedence)!;
        var matching = items.Where(item => item.State == state).Select(item => item.Position).ToList();
        var positions = items.Select(item => item.Position).ToList();
        var count = matching.Count;
        if (state == InstanceDiagramStates.Active && CombinedProgress(instance, matching) is { } progress)
        {
            var summary = $"{progress.Completed} of {progress.Total} completed, {progress.Active} active";
            if (progress.Pending > 0) summary += $", {progress.Pending} pending";
            // One execution's badge is its progress; several are counted like other positions.
            return count == 1
                ? new InstanceDiagramMarker(nodeId, state, $"{progress.Completed}/{progress.Total}",
                    $"Multi-instance work: {summary}", positions)
                : new InstanceDiagramMarker(nodeId, state, $"×{count}",
                    $"Multi-instance work in {count} executions: {summary}", positions);
        }

        // Positions at one node can differ, such as user work beside a background job.
        var description = string.Join("; ", matching.Select(position => Describe(instance, state, position)).Distinct());
        return count > 1
            ? new InstanceDiagramMarker(nodeId, state, $"×{count}", $"{description} ({count} positions)", positions)
            : new InstanceDiagramMarker(nodeId, state, null, description, positions);
    }

    private static string Describe(InstanceDetailDto instance, string state, ExecutionPositionDto position) => state switch
    {
        InstanceDiagramStates.Active => ExecutionPositionText.DescribeWait(position),
        InstanceDiagramStates.Faulted => instance.Fault?.Code is { } code && !string.IsNullOrWhiteSpace(code)
            ? $"Faulted here ({code})"
            : "Faulted here",
        _ => ExecutionPositionText.DescribePosition(position)
    };

    // The summed progress of the positions' multi-instance executions, when every
    // position waits for multi-instance work rather than a background job.
    private static MultiInstanceTotals? CombinedProgress(
        InstanceDetailDto instance,
        IReadOnlyList<ExecutionPositionDto> positions)
    {
        var executions = new List<MultiInstanceProgressDto>();
        foreach (var position in positions)
        {
            if (ExecutionPositionText.IsBackgroundWait(position)
                || position.MultiInstanceExecutionId is not long executionId
                || FindProgress(instance, executionId) is not { } progress)
            {
                return null;
            }
            executions.Add(progress);
        }

        return new MultiInstanceTotals(
            executions.Sum(progress => progress.Completed),
            executions.Sum(progress => progress.Total),
            executions.Sum(progress => progress.Active),
            executions.Sum(progress => progress.Pending));
    }

    private sealed record MultiInstanceTotals(int Completed, int Total, int Active, int Pending);

    private sealed record MarkedPosition(ExecutionPositionDto Position, string? State);

    // Instances created before execution tokens expose only their current node.
    private static InstanceDiagramOverlay BuildLegacy(InstanceDetailDto instance)
    {
        var status = instance.Status?.Trim().ToLowerInvariant();
        (string State, string Label)? marker = status switch
        {
            "running" => (InstanceDiagramStates.Active, "Current position"),
            "completed" => (InstanceDiagramStates.Ended, "Ended here"),
            "faulted" => (InstanceDiagramStates.Faulted, "Faulted here"),
            "cancelled" => (InstanceDiagramStates.Cancelled, "Cancelled here"),
            _ => null
        };
        return marker is { } value
            ? new InstanceDiagramOverlay([new InstanceDiagramMarker(instance.CurrentNodeId, value.State, null, value.Label, [])])
            : Empty;
    }
}
