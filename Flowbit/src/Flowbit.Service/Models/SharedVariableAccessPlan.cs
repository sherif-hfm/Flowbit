using System.Collections.Frozen;

namespace Flowbit.Service.Models;

public static class AutomaticMultiInstanceAccessPhases
{
    public const string Initialize = "initialize";
    public const string StageItem = "stageItem";
    public const string PublishItem = "publishItem";
    public const string Complete = "complete";
    public const string Error = "error";
}

public readonly record struct AutomaticMultiInstanceAccessKey(int NodeId, string Phase, int? BoundaryNodeId = null);

public sealed record SharedVariableAutomaticPhaseAccessPlan(
    IReadOnlySet<string> ProducerAliases,
    IReadOnlySet<string> ReadAliases,
    IReadOnlySet<string>? FrozenReadAliases = null,
    string? CollectionSourceAlias = null)
{
    public IReadOnlySet<string> ProducerAliases { get; } = ProducerAliases.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> ReadAliases { get; } = ReadAliases.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string>? FrozenReadAliases { get; } =
        (FrozenReadAliases ?? new HashSet<string>()).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    // A phase reads mutable shared dependencies under the same exact ordered
    // locks as its targets; later phases reacquire their own live dependencies.
    public IReadOnlySet<string> LockAliases { get; } = ProducerAliases.Concat(ReadAliases).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

public sealed record SharedVariableNodeAccessPlan(
    int NodeId,
    IReadOnlySet<string> ProducerAliases,
    IReadOnlySet<string> ReadAliases)
{
    public IReadOnlySet<string> ProducerAliases { get; } =
        ProducerAliases.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> ReadAliases { get; } =
        ReadAliases.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> LockAliases { get; } =
        ProducerAliases.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool TouchesSharedVariables =>
        LockAliases.Count > 0 || ReadAliases.Count > 0;
}

public sealed record SharedVariableFlowAccessPlan(
    int FlowId,
    IReadOnlySet<string> ProducerAliases)
{
    public IReadOnlySet<string> ProducerAliases { get; } =
        ProducerAliases.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Immutable selection from one immutable-definition access plan. Engine
/// callers identify nodes/flows; only the plan owns alias expansion, preventing
/// arbitrary raw alias sets from widening or narrowing repository locks.
/// </summary>
public sealed class SharedVariableAccessScope
{
    internal SharedVariableAccessScope(
        SharedVariableAccessPlan plan,
        IEnumerable<int> nodeIds,
        IEnumerable<int> flowIds,
        IEnumerable<string> lockAliases,
        SharedVariableAutomaticPhaseAccessPlan? automaticPhase = null)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(flowIds);
        ArgumentNullException.ThrowIfNull(lockAliases);
        NodeIds = nodeIds.ToFrozenSet();
        FlowIds = flowIds.ToFrozenSet();
        LockAliases = lockAliases.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        AutomaticPhase = automaticPhase;
    }

    public SharedVariableAccessPlan Plan { get; }

    public IReadOnlySet<int> NodeIds { get; }

    public IReadOnlySet<int> FlowIds { get; }

    public IReadOnlySet<string> LockAliases { get; }

    public SharedVariableAutomaticPhaseAccessPlan? AutomaticPhase { get; }
}

/// <summary>
/// Immutable, node/flow-indexed shared-variable access plan. Callers select
/// only the currently executing node or flow; unrelated producers elsewhere in
/// the same definition never widen the lock set.
/// </summary>
public sealed class SharedVariableAccessPlan(
    IReadOnlyDictionary<int, SharedVariableNodeAccessPlan> nodes,
    IReadOnlyDictionary<int, SharedVariableFlowAccessPlan> flows,
    IReadOnlyDictionary<AutomaticMultiInstanceAccessKey, SharedVariableAutomaticPhaseAccessPlan>? automaticPhases = null)
{
    private static readonly IReadOnlySet<string> EmptyAliasSet =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static SharedVariableAccessPlan Empty { get; } = new(
        FrozenDictionary<int, SharedVariableNodeAccessPlan>.Empty,
        FrozenDictionary<int, SharedVariableFlowAccessPlan>.Empty);

    public IReadOnlyDictionary<int, SharedVariableNodeAccessPlan> Nodes { get; } =
        nodes.ToFrozenDictionary();

    public IReadOnlyDictionary<int, SharedVariableFlowAccessPlan> Flows { get; } =
        flows.ToFrozenDictionary();

    public IReadOnlyDictionary<AutomaticMultiInstanceAccessKey, SharedVariableAutomaticPhaseAccessPlan> AutomaticPhases { get; } =
        (automaticPhases ?? new Dictionary<AutomaticMultiInstanceAccessKey, SharedVariableAutomaticPhaseAccessPlan>()).ToFrozenDictionary();

    public SharedVariableAutomaticPhaseAccessPlan ForAutomaticPhase(int nodeId, string phase, int? boundaryNodeId = null) =>
        AutomaticPhases.GetValueOrDefault(new(nodeId, phase, boundaryNodeId))
        ?? new SharedVariableAutomaticPhaseAccessPlan(EmptyAliasSet, EmptyAliasSet);

    public SharedVariableAccessScope SelectAutomaticPhase(int nodeId, string phase, int? boundaryNodeId = null)
    {
        var selected = ForAutomaticPhase(nodeId, phase, boundaryNodeId);
        return new(this, [nodeId], [], selected.LockAliases, selected);
    }

    public SharedVariableNodeAccessPlan ForNode(int nodeId) =>
        Nodes.TryGetValue(nodeId, out var plan)
            ? plan
            : new SharedVariableNodeAccessPlan(
                nodeId,
                EmptyAliasSet,
                EmptyAliasSet);

    public SharedVariableFlowAccessPlan ForFlow(int flowId) =>
        Flows.TryGetValue(flowId, out var plan)
            ? plan
            : new SharedVariableFlowAccessPlan(flowId, EmptyAliasSet);

    public SharedVariableAccessScope SelectNode(int nodeId) =>
        new(this, [nodeId], [], ForNode(nodeId).LockAliases);

    public SharedVariableAccessScope SelectNodeAndFlow(int nodeId, int flowId) =>
        new(
            this,
            [nodeId],
            [flowId],
            ForNode(nodeId).LockAliases.Concat(ForFlow(flowId).ProducerAliases));

    public SharedVariableAccessScope SelectNodes(IEnumerable<int> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var selected = nodeIds.ToFrozenSet();
        var aliases = selected.SelectMany(nodeId => ForNode(nodeId).LockAliases);
        return new SharedVariableAccessScope(this, selected, [], aliases);
    }
}
