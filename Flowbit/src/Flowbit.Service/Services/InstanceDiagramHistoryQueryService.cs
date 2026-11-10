using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Applies graph attribution to PostgreSQL's grouped evidence. History and current
/// positions deliberately have different run scopes and are never combined here.
/// </summary>
public sealed class InstanceDiagramHistoryQueryService(IInstanceDiagramHistoryQueryRepository repository)
    : IInstanceDiagramHistoryQueryService
{
    public async Task<InstanceDiagramHistoryDto?> GetAsync(long instanceId, CancellationToken cancellationToken)
    {
        var snapshot = await repository.GetAsync(instanceId, cancellationToken);
        if (snapshot is null) return null;
        cancellationToken.ThrowIfCancellationRequested();

        var nodes = snapshot.Definition.FlowNodes.ToDictionary(node => node.Id);
        var flows = snapshot.Definition.SequenceFlows.ToDictionary(flow => flow.Id);
        var reached = new HashSet<int>();
        var counts = new Dictionary<int, long>();
        var unmapped = snapshot.HasUnmappedMultiInstanceNodes;
        foreach (var nodeId in snapshot.ReachedNodeIds)
        {
            if (nodes.ContainsKey(nodeId)) reached.Add(nodeId);
            else unmapped = true;
        }

        void Count(InstanceDiagramTraversalEvidence evidence, bool exit, bool multiInstance)
        {
            if (evidence.Count <= 0
                || !flows.TryGetValue(evidence.FlowId, out var flow)
                || !nodes.TryGetValue(flow.SourceRef, out var source)
                || !nodes.TryGetValue(flow.TargetRef, out var target)
                || evidence.NodeId != (exit ? flow.SourceRef : flow.TargetRef))
            {
                unmapped = true;
                return;
            }
            // An MI parent has no visit. A child attributed to a non-MI node,
            // or an ordinary entry attributed to an MI node, is not evidence
            // that can be counted with either supported representation.
            if (!exit && IsMultiInstance(target) != multiInstance)
            {
                unmapped = true;
                return;
            }
            var targetSide = source.AsyncAfter || IsMultiInstance(source);
            if (exit == targetSide) return;
            counts[flow.Id] = checked(counts.GetValueOrDefault(flow.Id) + evidence.Count);
            reached.Add(flow.TargetRef);
        }

        foreach (var evidence in snapshot.Exits) Count(evidence, exit: true, multiInstance: false);
        foreach (var evidence in snapshot.Entries) Count(evidence, exit: false, multiInstance: false);
        foreach (var evidence in snapshot.MultiInstanceEntries) Count(evidence, exit: false, multiInstance: true);

        var reasons = new List<string>();
        if (snapshot.HasCutoverSeeded) reasons.Add("cutover");
        if (snapshot.NodeActivityPrunedAt is not null) reasons.Add("nodeActivityPruned");
        if (unmapped) reasons.Add("unmappedEvidence");
        if (snapshot.HasConflictingEntries) reasons.Add("conflictingEvidence");
        var partial = reasons.Count > 0;
        if (!snapshot.NodeActivityFromCreation && !snapshot.HasCutoverSeeded)
            reasons.Add("legacyCoverageUnknown");
        if (!snapshot.NodeActivityFromCreation
            && snapshot.HistoryPrunedAt is not null
            && snapshot.NodeActivityPrunedAt is null)
            reasons.Add("retentionCategoryUnknown");
        // Before fan-out an MI parent has no visit. Local properties cannot
        // prove safety: another branch can terminate/interupt the activation.
        if (flows.Values.Any(flow => nodes.TryGetValue(flow.SourceRef, out var source)
                                     && nodes.TryGetValue(flow.TargetRef, out var target)
                                     && (source.AsyncAfter || IsMultiInstance(source))
                                     && IsMultiInstance(target)))
            reasons.Add("multiInstanceEntryUnrecorded");

        return new InstanceDiagramHistoryDto(
            snapshot.InstanceId, snapshot.WorkflowId, snapshot.WorkflowVersion,
            snapshot.InstanceUpdatedAt, snapshot.AsOf, "currentVersionAllRuns",
            new InstanceDiagramHistoryCoverageDto(
                partial ? "partial" : reasons.Count > 0 ? "unknown" : "complete",
                reasons, snapshot.NodeActivityPrunedAt),
            reached.Order().Select(id => new InstanceDiagramHistoryNodeDto(id)).ToArray(),
            counts.OrderBy(pair => pair.Key)
                .Select(pair => new InstanceDiagramHistoryFlowDto(pair.Key, pair.Value)).ToArray());
    }

    private static bool IsMultiInstance(FlowNodeModel node) =>
        BpmnFlowNodeTypes.IsUserTask(node.Type) && node.MultiInstance is not null;
}
