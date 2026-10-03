using Flowbit.Shared.Models;

namespace Flowbit.Service.Authoring;

/// <summary>Deterministic layout of canonical models. Attached boundaries retain renderer-owned positions.</summary>
public static class WorkflowAuthoringLayout
{
    private const int ColumnWidth = 300;
    private const int RowHeight = 200;

    public static void Apply(WorkflowModel model, WorkflowModel? original = null)
    {
        var nodes = model.FlowNodes.Where(node => !BpmnFlowNodeTypes.IsBoundary(node.Type)).OrderBy(node => node.Id).ToArray();
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();
        var allById = model.FlowNodes.ToDictionary(node => node.Id);
        int Host(int id) => allById.TryGetValue(id, out var node) && BpmnFlowNodeTypes.IsBoundary(node.Type)
            ? node.AttachedToRef ?? id : id;
        var edges = nodes.ToDictionary(node => node.Id, _ => new SortedSet<int>());
        foreach (var flow in model.SequenceFlows)
        {
            var source = Host(flow.SourceRef);
            var target = Host(flow.TargetRef);
            if (source != target && nodeIds.Contains(source) && nodeIds.Contains(target)) edges[source].Add(target);
        }
        var ranks = RankStronglyConnectedComponents(edges);
        var originalNodes = (original?.FlowNodes ?? []).ToDictionary(node => node.Id);
        var originalLanes = (original?.Lanes ?? []).ToDictionary(lane => lane.Id);
        var preserve = original is not null;
        var newColumnStart = preserve && originalNodes.Count > 0
            ? originalNodes.Values.Max(node => node.X) + ColumnWidth : 100;
        var nextLaneY = preserve && originalLanes.Count > 0
            ? originalLanes.Values.Max(lane => lane.Y + lane.H) + 40 : 20;
        var laneIds = model.Lanes.Select(lane => (long)lane.Id).ToHashSet();
        var groups = model.Lanes.Select(lane => (Key: (long)lane.Id, Lane: (LaneModel?)lane)).ToList();
        if (nodes.Any(node => node.LaneId is null || !laneIds.Contains(node.LaneId.Value))) groups.Add((long.MinValue, null));
        foreach (var (key, lane) in groups)
        {
            var members = nodes.Where(node => (node.LaneId is { } id && laneIds.Contains(id) ? id : long.MinValue) == key).ToArray();
            var savedLane = lane is not null && originalLanes.TryGetValue(lane.Id, out var previousLane) ? previousLane : null;
            var top = savedLane?.Y ?? nextLaneY;
            if (lane is not null)
            {
                lane.X = savedLane?.X ?? 40;
                lane.Y = top;
                lane.W = savedLane?.W ?? 400;
                lane.H = savedLane?.H ?? RowHeight;
            }
            var occupiedRows = new Dictionary<int, int>();
            var nextAddedX = newColumnStart;
            var positioned = new List<FlowNodeModel>();
            foreach (var node in members.OrderBy(node => ranks[node.Id]).ThenBy(node => node.Id))
            {
                if (preserve && originalNodes.TryGetValue(node.Id, out var previous) && previous.LaneId == node.LaneId)
                {
                    node.X = previous.X;
                    node.Y = previous.Y;
                    continue;
                }
                var column = ranks[node.Id];
                var row = occupiedRows.GetValueOrDefault(column);
                occupiedRows[column] = row + 1;
                // Existing lane rows cannot grow into another preserved lane. Additions use
                // free columns to the right; new diagrams/lanes can use independent branch rows.
                node.X = savedLane is null ? newColumnStart + column * ColumnWidth
                    : Math.Max(nextAddedX, newColumnStart + column * ColumnWidth);
                node.Y = top + 65 + (savedLane is null ? row * RowHeight : 0);
                nextAddedX = node.X + ColumnWidth;
                positioned.Add(node);
            }
            if (lane is not null && positioned.Count > 0)
            {
                // Include labels and renderer-anchored boundary circles in the lane envelope.
                var left = Math.Min(lane.X, positioned.Min(node => node.X) - 45);
                var laneTop = Math.Min(lane.Y, positioned.Min(node => node.Y) - 55);
                lane.W = Math.Max(lane.X + lane.W, positioned.Max(node => node.X) + 250) - left;
                lane.H = Math.Max(lane.Y + lane.H, positioned.Max(node => node.Y) + 150) - laneTop;
                lane.X = left;
                lane.Y = laneTop;
            }
            nextLaneY = Math.Max(nextLaneY, lane is null ? top + Math.Max(1, members.Length) * RowHeight : lane.Y + lane.H + 40);
        }
        foreach (var boundary in model.FlowNodes.Where(node => BpmnFlowNodeTypes.IsBoundary(node.Type)))
        {
            if (boundary.AttachedToRef is { } id && allById.TryGetValue(id, out var host))
            {
                boundary.LaneId = host.LaneId;
                boundary.X = host.X;
                boundary.Y = host.Y;
            }
        }
    }

    private static Dictionary<int, int> RankStronglyConnectedComponents(Dictionary<int, SortedSet<int>> edges)
    {
        // Iterative Kosaraju avoids a recursion limit on large imported graphs.
        var visited = new HashSet<int>();
        var finish = new List<int>();
        foreach (var start in edges.Keys.Order())
        {
            var stack = new Stack<(int Id, bool Exit)>();
            stack.Push((start, false));
            while (stack.TryPop(out var item))
            {
                if (item.Exit) { finish.Add(item.Id); continue; }
                if (!visited.Add(item.Id)) continue;
                stack.Push((item.Id, true));
                foreach (var target in edges[item.Id].Reverse()) if (!visited.Contains(target)) stack.Push((target, false));
            }
        }
        var reverse = edges.Keys.ToDictionary(id => id, _ => new List<int>());
        foreach (var (source, targets) in edges) foreach (var target in targets) reverse[target].Add(source);
        var componentById = new Dictionary<int, int>();
        var componentCount = 0;
        foreach (var start in finish.AsEnumerable().Reverse())
        {
            if (componentById.ContainsKey(start)) continue;
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.TryPop(out var id))
            {
                if (!componentById.TryAdd(id, componentCount)) continue;
                foreach (var previous in reverse[id]) stack.Push(previous);
            }
            componentCount++;
        }
        var outgoing = Enumerable.Range(0, componentCount).ToDictionary(id => id, _ => new HashSet<int>());
        var indegree = new int[componentCount];
        foreach (var (source, targets) in edges)
        foreach (var target in targets)
        {
            var from = componentById[source]; var to = componentById[target];
            if (from != to && outgoing[from].Add(to)) indegree[to]++;
        }
        var ready = new SortedSet<int>(Enumerable.Range(0, componentCount).Where(id => indegree[id] == 0));
        var members = componentById.GroupBy(item => item.Value)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Key).Order().ToArray());
        var rank = new int[componentCount];
        while (ready.Count > 0)
        {
            var current = ready.Min;
            ready.Remove(current);
            foreach (var next in outgoing[current].Order())
            {
                rank[next] = Math.Max(rank[next], rank[current] + members[current].Length);
                if (--indegree[next] == 0) ready.Add(next);
            }
        }
        return members.SelectMany(group => group.Value.Select((id, index) => (Id: id, Rank: rank[group.Key] + index)))
            .ToDictionary(item => item.Id, item => item.Rank);
    }
}
