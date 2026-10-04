using System.Text.Json;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Ai;

public sealed partial class WorkflowAiAuthoringService
{
    // Authoring completion guard only: do not change the runtime's support for saved partial diagrams.
    internal static void ValidateProposalReachability(WorkflowModel model, WorkflowModel? original)
    {
        var reached = Reachable(model);
        var existingIslands = original is null ? [] : original.FlowNodes.Select(node => node.Id).Except(Reachable(original)).ToHashSet();
        var disconnected = model.FlowNodes.Where(node => !reached.Contains(node.Id) && !existingIslands.Contains(node.Id)).ToArray();
        if (disconnected.Length > 0)
            throw new JsonException("Unreachable draft nodes: " + string.Join(", ", disconnected.Take(12).Select(node => $"#{node.Id} ({node.Name})"))
                + ". Connect the required work from a start event using the intended incoming routes. Trace actual sourceRef/targetRef IDs; do not remove required steps to silence this check.");
    }

    private static HashSet<int> Reachable(WorkflowModel model)
    {
        var edges = model.SequenceFlows.Select(flow => (Source: flow.SourceRef, Target: flow.TargetRef))
            .Concat(model.FlowNodes.Where(node => BpmnFlowNodeTypes.IsBoundary(node.Type) && node.AttachedToRef.HasValue)
                .Select(node => (Source: node.AttachedToRef!.Value, Target: node.Id)))
            .ToLookup(edge => edge.Source, edge => edge.Target);
        var reached = new HashSet<int>();
        var queue = new Queue<int>(model.FlowNodes.Where(node => BpmnFlowNodeTypes.IsEntry(node.Type) || node.Id == model.InitialEventId).Select(node => node.Id));
        while (queue.TryDequeue(out var id))
        {
            if (!reached.Add(id)) continue;
            foreach (var target in edges[id]) if (!reached.Contains(target)) queue.Enqueue(target);
        }
        return reached;
    }
}
