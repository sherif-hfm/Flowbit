namespace Flowbit.Shared.Models;

/// <summary>A resolved link: the throw a token enters and the catch where it continues.</summary>
public readonly record struct LinkEventEdge(int ThrowId, int CatchId);

/// <summary>
/// The one source of link-event pairing. A link throw and a link catch pair by their names,
/// trimmed as .NET <see cref="string.Trim()"/> does and then compared exactly, with no Unicode
/// normalization. Several throws may share one catch. Every analysis that follows a link
/// (definition reachability, gateway merge enabling, reactivation hazards, the shared-variable
/// lock-order proof, version-change topology, and the AI assistant's reachability check) reads
/// the pairing from here, and the editor's save validator mirrors <see cref="FindProblems"/>.
/// </summary>
public static class LinkEventRules
{
    /// <summary>The pairing key: the trimmed name, or null when the name is blank.</summary>
    public static string? NameKey(string? name)
    {
        var key = name?.Trim();
        return string.IsNullOrEmpty(key) ? null : key;
    }

    public static LinkEventIndex Index(WorkflowModel definition) => new(definition);

    /// <summary>Each resolved throw-to-catch edge, in the order the throws appear in the definition.</summary>
    public static IReadOnlyList<LinkEventEdge> ImplicitEdges(WorkflowModel definition) => Index(definition).Edges;

    /// <summary>
    /// Every topology and pairing problem of the definition's link events, in node order. For each
    /// link event: a blank name, then its sequence-flow topology, then its pairing. Settings a link
    /// event cannot carry are reported by the API's authored-input validation, not here.
    /// </summary>
    public static IReadOnlyList<string> FindProblems(WorkflowModel definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var nodes = (definition.FlowNodes ?? []).Where(node => node is not null).ToArray();
        var flows = (definition.SequenceFlows ?? []).Where(flow => flow is not null).ToArray();
        var throws = nodes.Where(node => BpmnFlowNodeTypes.IsLinkThrow(node.Type)).ToArray();
        var catches = nodes.Where(node => BpmnFlowNodeTypes.IsLinkCatch(node.Type)).ToArray();
        var problems = new List<string>();
        if (throws.Length == 0 && catches.Length == 0)
        {
            return problems;
        }

        var seenCatches = new List<FlowNodeModel>();
        foreach (var node in nodes)
        {
            var isThrow = BpmnFlowNodeTypes.IsLinkThrow(node.Type);
            if (!isThrow && !BpmnFlowNodeTypes.IsLinkCatch(node.Type))
            {
                continue;
            }

            var label = isThrow ? $"Link throw event #{node.Id}" : $"Link catch event #{node.Id}";
            var key = NameKey(node.Name);
            if (key is null)
            {
                problems.Add($"Flow node #{node.Id} name is required.");
            }

            var incoming = flows.Count(flow => flow.TargetRef == node.Id);
            var outgoing = flows.Where(flow => flow.SourceRef == node.Id).ToArray();
            if (isThrow)
            {
                if (incoming == 0)
                {
                    problems.Add($"{label} must have at least one incoming sequence flow.");
                }

                if (outgoing.Length > 0)
                {
                    problems.Add($"{label} cannot have outgoing sequence flows.");
                }

                if (key is null)
                {
                    continue;
                }

                var exact = catches.FirstOrDefault(candidate => string.Equals(
                    NameKey(candidate.Name), key, StringComparison.Ordinal));
                if (exact is not null)
                {
                    continue;
                }

                var caseOnly = catches.FirstOrDefault(candidate => string.Equals(
                    NameKey(candidate.Name), key, StringComparison.OrdinalIgnoreCase));
                problems.Add(caseOnly is null
                    ? $"{label} has no link catch event named '{key}'."
                    : $"{label} is named '{key}', but link catch event #{caseOnly.Id} is named '{NameKey(caseOnly.Name)}'; link names must match exactly, including letter case.");
                continue;
            }

            if (incoming > 0)
            {
                problems.Add($"{label} cannot have incoming sequence flows.");
            }

            if (outgoing.Length != 1)
            {
                problems.Add($"{label} must have exactly one outgoing sequence flow.");
            }
            else if (HasRoutingMetadata(outgoing[0]))
            {
                problems.Add($"{label} must have one unconditional outgoing sequence flow without user-action or multi-instance metadata.");
            }

            if (key is null)
            {
                continue;
            }

            var earlier = seenCatches.FirstOrDefault(candidate => string.Equals(
                NameKey(candidate.Name), key, StringComparison.OrdinalIgnoreCase));
            seenCatches.Add(node);
            if (earlier is not null)
            {
                problems.Add($"{label} has the same name as link catch event #{earlier.Id} ('{NameKey(earlier.Name)}'); link catch names must be unique, ignoring letter case.");
            }

            if (!throws.Any(candidate => string.Equals(NameKey(candidate.Name), key, StringComparison.Ordinal)))
            {
                problems.Add($"{label} has no link throw event named '{key}'.");
            }
        }

        return problems;
    }

    /// <summary>
    /// A catch's continuation is fixed routing: it carries no condition, default or priority, and
    /// none of a user action's or a multi-instance outcome's settings.
    /// </summary>
    public static bool HasRoutingMetadata(SequenceFlowModel flow) =>
        !flow.IsSelectable
        || flow.IsDefault
        || !string.IsNullOrWhiteSpace(flow.Condition)
        || flow.ConditionPriority is not null
        || flow.Roles is { Count: > 0 }
        || flow.Variables is { Count: > 0 }
        || flow.CanActWithoutClaim
        || flow.CanActWithoutClaimRoles is { Count: > 0 }
        || !string.IsNullOrWhiteSpace(flow.CompletionCondition)
        || flow.CompletionPriority is not null
        || flow.CancelRemainingInstances;
}

/// <summary>
/// The pairing of one definition's link events. A throw resolves to the first catch, in node order,
/// whose trimmed name equals its own exactly; a throw with a blank name or no such catch is unresolved.
/// </summary>
public sealed class LinkEventIndex
{
    private static readonly IReadOnlyList<int> NoThrows = [];
    private readonly Dictionary<int, int> catchByThrow = new();
    private readonly Dictionary<int, List<int>> throwsByCatch = new();
    private readonly List<LinkEventEdge> edges = [];

    public LinkEventIndex(WorkflowModel definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var nodes = (definition.FlowNodes ?? []).Where(node => node is not null).ToArray();
        var catchByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes.Where(node => BpmnFlowNodeTypes.IsLinkCatch(node.Type)))
        {
            if (LinkEventRules.NameKey(node.Name) is { } key)
            {
                catchByName.TryAdd(key, node.Id);
            }
        }

        foreach (var node in nodes.Where(node => BpmnFlowNodeTypes.IsLinkThrow(node.Type)))
        {
            if (LinkEventRules.NameKey(node.Name) is not { } key
                || !catchByName.TryGetValue(key, out var catchId)
                || !catchByThrow.TryAdd(node.Id, catchId))
            {
                continue;
            }

            if (!throwsByCatch.TryGetValue(catchId, out var throws))
            {
                throwsByCatch[catchId] = throws = [];
            }

            throws.Add(node.Id);
            edges.Add(new LinkEventEdge(node.Id, catchId));
        }
    }

    public IReadOnlyList<LinkEventEdge> Edges => edges;

    public bool IsEmpty => edges.Count == 0;

    /// <summary>The catch a throw continues at, or null when the throw is unresolved.</summary>
    public int? CatchOf(int throwId) => catchByThrow.TryGetValue(throwId, out var catchId) ? catchId : null;

    /// <summary>The throws that continue at a catch, in node order.</summary>
    public IReadOnlyList<int> ThrowsOf(int catchId) =>
        throwsByCatch.TryGetValue(catchId, out var throws) ? throws : NoThrows;
}
