using System.Text.RegularExpressions;

namespace Flowbit.Shared.Models;

/// <summary>
/// The one source of signal-name rules. A signal name is literal text: 1 to 100
/// lowercase ASCII letters, digits, '.', '_', or '-', starting with a letter or digit,
/// compared exactly. Lowercase-only names mean a broadcast cannot miss its waits
/// because of letter case. Definition validation and the throw endpoint both use
/// these rules, and the editor's save validator mirrors <see cref="NodeProblems"/> and
/// <see cref="BoundaryNameProblems"/>.
/// </summary>
public static class SignalEventRules
{
    public const int MaxNameLength = 100;
    public const int MaxOutputMappings = 100;

    /// <summary>
    /// The name pattern as JavaScript and JSON Schema write it; the editor's
    /// <c>SIGNAL_NAME_PATTERN</c> must equal it, which a test checks.
    /// </summary>
    public const string NamePattern = "^[a-z0-9][a-z0-9._-]{0,99}$";

    public const string NameRuleDescription =
        "1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit";

    // \A and \z, because .NET's $ also matches before a final line feed.
    private static readonly Regex NameRegex = new(
        @"\A[a-z0-9][a-z0-9._-]{0,99}\z",
        RegexOptions.CultureInvariant);

    public static bool IsValidName(string? name) => name is not null && NameRegex.IsMatch(name);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorkflowModel, object> SignalFlags = new();
    private static readonly object HasSignals = new();
    private static readonly object NoSignals = new();

    /// <summary>
    /// Whether a definition contains a signal event, computed once per definition object.
    /// A definition without one never has signal subscriptions, so the engine skips
    /// every signal statement for it.
    /// </summary>
    public static bool HasSignalEvents(WorkflowModel definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var flag = SignalFlags.GetValue(
            definition,
            model => (model.FlowNodes ?? []).Any(node => node is not null && BpmnFlowNodeTypes.IsSignalEvent(node.Type))
                ? HasSignals
                : NoSignals);
        return ReferenceEquals(flag, HasSignals);
    }

    /// <summary>The noun every signal-event message starts with.</summary>
    public static string Label(FlowNodeModel node) =>
        BpmnFlowNodeTypes.IsSignalBoundary(node.Type)
            ? $"Signal boundary event #{node.Id}"
            : $"Signal catch event #{node.Id}";

    /// <summary>
    /// The problem with a signal name given to the throw endpoint, or null when it is valid.
    /// </summary>
    public static string? ThrowNameProblem(string? name) =>
        string.IsNullOrEmpty(name)
            ? "Signal name is required."
            : IsValidName(name)
                ? null
                : $"Signal name must be {NameRuleDescription}.";

    /// <summary>
    /// The boundary name rules, in node order: a later boundary repeating an earlier
    /// one's name on the same host, and a boundary repeating its host catch's name.
    /// Only valid names are compared, so an invalid name is reported once.
    /// </summary>
    public static IReadOnlyList<string> BoundaryNameProblems(IReadOnlyList<FlowNodeModel> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var problems = new List<string>();
        var byId = new Dictionary<int, FlowNodeModel>();
        foreach (var node in nodes)
        {
            byId.TryAdd(node.Id, node);
        }

        var firstOnHost = new Dictionary<(int Host, string Name), int>();
        foreach (var node in nodes.OrderBy(node => node.Id))
        {
            if (!BpmnFlowNodeTypes.IsSignalBoundary(node.Type)
                || node.AttachedToRef is not int hostId
                || !IsValidName(node.Signal?.Name))
            {
                continue;
            }

            var name = node.Signal!.Name!;
            if (firstOnHost.TryGetValue((hostId, name), out var earlier))
            {
                problems.Add(
                    $"Signal boundary event #{node.Id} uses signal '{name}', which signal boundary event #{earlier} on host #{hostId} already uses.");
            }
            else
            {
                firstOnHost[(hostId, name)] = node.Id;
            }

            if (byId.TryGetValue(hostId, out var host)
                && BpmnFlowNodeTypes.IsSignalCatch(host.Type)
                && string.Equals(host.Signal?.Name, name, StringComparison.Ordinal))
            {
                problems.Add(
                    $"Signal boundary event #{node.Id} uses signal '{name}', the signal its host signal catch event #{hostId} waits for.");
            }
        }

        return problems;
    }

    /// <summary>The name and mapping-count problems of one signal event.</summary>
    public static IReadOnlyList<string> NodeProblems(FlowNodeModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var label = Label(node);
        var problems = new List<string>();
        var name = node.Signal?.Name;
        if (string.IsNullOrEmpty(name))
        {
            problems.Add($"{label} must define signal.name.");
        }
        else if (!IsValidName(name))
        {
            problems.Add($"{label} signal.name must be {NameRuleDescription}.");
        }

        if ((node.Signal?.OutputMappings?.Count ?? 0) > MaxOutputMappings)
        {
            problems.Add($"{label} defines more than {MaxOutputMappings} output mappings.");
        }

        return problems;
    }
}
