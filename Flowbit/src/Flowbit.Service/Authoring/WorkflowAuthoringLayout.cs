using Flowbit.Shared.Models;

namespace Flowbit.Service.Authoring;

/// <summary>Deterministic layout of canonical models. Attached boundaries retain renderer-owned positions.</summary>
public static class WorkflowAuthoringLayout
{
    private const int ColumnWidth = 300;
    private const int RowHeight = 200;

    /// <summary>
    /// A new diagram (no original, or one without flow nodes) gets the compact lane layout. An edit keeps the positions of
    /// existing nodes in their lane and places additions in free columns to the right.
    /// </summary>
    public static void Apply(WorkflowModel model, WorkflowModel? original = null)
    {
        if (original is null || original.FlowNodes.Count == 0)
        {
            ApplyCompact(model);
            return;
        }
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

    // Compact lane layout, from Flowbit AI Lab v3 (2026-10-14). Spacing uses the editor's shape sizes; x/y is the top-left corner.
    private const int ArrowGap = 100;     // between the shapes of neighbouring columns, so arrows and their labels show
    private const int CaptionGap = 30;    // between the captions of neighbouring columns
    private const int RowGap = 30;        // between a row's lowest point and the next row's highest
    private const int LaneGap = 30;
    private const int TitleBand = 55;     // the lane's id and name are drawn in its top 50 px
    private const int SidePadding = 40;
    private const int BottomPadding = 25;
    private const int EmptyLaneHeight = 120;
    private const int Left = 40, Top = 30;
    private const int RowHalf = 44;       // half a task's height: the nodes of a row share one centre line

    /// <summary>
    /// Columns follow the flow with loops broken, so a rework loop keeps its column. Columns and rows are only as far apart as
    /// their shapes need. A node keeps the row of a predecessor in its lane when that place is free, so chains stay straight and
    /// parallel branches stack in one column. Every lane spans the diagram width and is only as tall as its rows.
    /// </summary>
    private static void ApplyCompact(WorkflowModel model)
    {
        // A boundary event's flows count as its host's.
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
        var predecessors = ForwardPredecessors(nodes, edges);
        var column = Columns(nodes, predecessors);

        // Column centres: neighbouring shapes keep the arrow gap; neighbouring captions only need to clear each other.
        var columns = nodes.Length == 0 ? 0 : column.Values.Max() + 1;
        var shapeHalf = new int[columns];
        var visualHalf = new int[columns];
        foreach (var node in nodes)
        {
            shapeHalf[column[node.Id]] = Math.Max(shapeHalf[column[node.Id]], Size(node.Type).W / 2);
            visualHalf[column[node.Id]] = Math.Max(visualHalf[column[node.Id]], VisualWidth(node.Type) / 2);
        }
        var centreX = new int[columns];
        for (var c = 0; c < columns; c++)
            centreX[c] = c == 0 ? Left + SidePadding + visualHalf[0]
                : centreX[c - 1] + Math.Max(shapeHalf[c - 1] + shapeHalf[c] + ArrowGap, visualHalf[c - 1] + visualHalf[c] + CaptionGap);
        var laneWidth = columns == 0 ? 400 : centreX[columns - 1] + visualHalf[columns - 1] + SidePadding - Left;

        // Lanes top to bottom in their declared order, then the nodes without a lane.
        var position = new Dictionary<int, (int X, int Y)>();
        var laneIds = model.Lanes.Select(lane => lane.Id).ToHashSet();
        var groups = model.Lanes.Select(lane => (Key: (int?)lane.Id, Lane: (LaneModel?)lane)).ToList();
        if (nodes.Any(node => node.LaneId is not { } id || !laneIds.Contains(id))) groups.Add((null, null));
        int? GroupOf(FlowNodeModel node) => node.LaneId is { } id && laneIds.Contains(id) ? id : null;
        var top = Top;
        foreach (var (key, lane) in groups)
        {
            var members = nodes.Where(node => GroupOf(node) == key).OrderBy(node => column[node.Id]).ThenBy(node => node.Id).ToArray();
            // A node takes the topmost row of its predecessors in this lane (row 0 without one), or the next free row below it in its column.
            var row = new Dictionary<int, int>();
            var taken = new HashSet<(int Column, int Row)>();
            foreach (var member in members)
            {
                var inLane = predecessors[member.Id].Where(row.ContainsKey).Select(p => row[p]).ToArray();
                var r = inLane.Length == 0 ? 0 : inLane.Min();
                while (taken.Contains((column[member.Id], r))) r++;
                taken.Add((column[member.Id], r));
                row[member.Id] = r;
            }
            var rows = members.Length == 0 ? 0 : row.Values.Max() + 1;
            var centreY = new int[rows];
            var below = 0;
            for (var r = 0; r < rows; r++)
            {
                centreY[r] = r == 0 ? top + TitleBand + RowHalf : centreY[r - 1] + below + RowGap + RowHalf;
                below = members.Where(node => row[node.Id] == r).Max(node => Below(node.Type));
            }
            foreach (var member in members)
            {
                var (w, h) = Size(member.Type);
                position[member.Id] = (centreX[column[member.Id]] - w / 2, centreY[row[member.Id]] - h / 2);
            }
            var height = rows == 0 ? EmptyLaneHeight : centreY[rows - 1] + below + BottomPadding - top;
            if (lane is not null)
            {
                lane.X = Left; lane.Y = top; lane.W = laneWidth; lane.H = height;
            }
            top += height + LaneGap;
        }

        foreach (var node in nodes)
            (node.X, node.Y) = position[node.Id];
        foreach (var boundary in model.FlowNodes.Where(node => BpmnFlowNodeTypes.IsBoundary(node.Type)))
        {
            if (boundary.AttachedToRef is { } id && position.TryGetValue(id, out var at))
            {
                boundary.LaneId = allById[id].LaneId;
                (boundary.X, boundary.Y) = at;
            }
        }
    }

    // Shape sizes in the editor (flowbit-editor.js: NODE_W x NODE_H, EVENT_D, GATEWAY_D).
    private static (int W, int H) Size(string type) =>
        type.EndsWith("Gateway", StringComparison.Ordinal) ? (76, 76) : type.EndsWith("Event", StringComparison.Ordinal) ? (64, 64) : (190, 88);
    // Events and gateways carry their caption under the shape, about 150 px wide (edgeNodeBounds in flowbit-editor.js).
    private static bool Captioned(string type) => type.EndsWith("Gateway", StringComparison.Ordinal) || type.EndsWith("Event", StringComparison.Ordinal);
    private static int VisualWidth(string type) => Captioned(type) ? 150 : Size(type).W;
    // How far below its row's centre line a node reaches, caption included (74 px under a startEvent, 62 under other captions).
    private static int Below(string type) => Captioned(type) ? Size(type).H / 2 + (type == BpmnFlowNodeTypes.StartEvent ? 74 : 62) : Size(type).H / 2;

    /// <summary>
    /// Each node's predecessors along forward edges. Loops are broken by a depth-first search from the entry events, then from
    /// nodes nothing leads to, then from the rest, all in ID order: an edge to a node still on the search path goes back.
    /// </summary>
    private static Dictionary<int, List<int>> ForwardPredecessors(FlowNodeModel[] nodes, Dictionary<int, SortedSet<int>> edges)
    {
        var incoming = nodes.ToDictionary(node => node.Id, _ => 0);
        foreach (var targets in edges.Values) foreach (var target in targets) incoming[target]++;
        var roots = nodes.Where(node => BpmnFlowNodeTypes.IsEntry(node.Type))
            .Concat(nodes.Where(node => incoming[node.Id] == 0)).Concat(nodes).Select(node => node.Id);
        var finished = new Dictionary<int, bool>();   // false while on the search path, true once left
        var back = new HashSet<(int Source, int Target)>();
        foreach (var start in roots)
        {
            if (finished.ContainsKey(start)) continue;
            var stack = new Stack<(int Id, IEnumerator<int> Targets)>();
            stack.Push((start, edges[start].GetEnumerator()));
            finished[start] = false;
            while (stack.TryPeek(out var current))
            {
                if (!current.Targets.MoveNext()) { finished[current.Id] = true; stack.Pop(); continue; }
                var target = current.Targets.Current;
                if (!finished.TryGetValue(target, out var done)) { finished[target] = false; stack.Push((target, edges[target].GetEnumerator())); }
                else if (!done) back.Add((current.Id, target));
            }
        }
        var predecessors = nodes.ToDictionary(node => node.Id, _ => new List<int>());
        foreach (var (source, targets) in edges)
            foreach (var target in targets)
                if (!back.Contains((source, target))) predecessors[target].Add(source);
        return predecessors;
    }

    // Longest path over the forward edges: a node's column is one more than its furthest predecessor's.
    private static Dictionary<int, int> Columns(FlowNodeModel[] nodes, Dictionary<int, List<int>> predecessors)
    {
        var column = new Dictionary<int, int>();
        foreach (var node in nodes)
        {
            // Iterative, so a long chain cannot overflow the call stack.
            var stack = new Stack<int>();
            stack.Push(node.Id);
            while (stack.TryPeek(out var current))
            {
                if (column.ContainsKey(current)) { stack.Pop(); continue; }
                var open = predecessors[current].Where(p => !column.ContainsKey(p)).ToArray();
                if (open.Length > 0) { foreach (var p in open) stack.Push(p); continue; }
                column[current] = predecessors[current].Count == 0 ? 0 : predecessors[current].Max(p => column[p]) + 1;
                stack.Pop();
            }
        }
        return column;
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
