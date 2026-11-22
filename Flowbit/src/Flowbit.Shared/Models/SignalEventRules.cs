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
    public static bool HasSignalWaits(WorkflowModel definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var flag = SignalFlags.GetValue(
            definition,
            model => (model.FlowNodes ?? []).Any(node => node is not null && BpmnFlowNodeTypes.IsSignalWait(node.Type))
                ? HasSignals
                : NoSignals);
        return ReferenceEquals(flag, HasSignals);
    }

    /// <summary>The noun every signal-event message starts with.</summary>
    public static string Label(FlowNodeModel node) =>
        BpmnFlowNodeTypes.IsSignalBoundary(node.Type)
            ? $"Signal boundary event #{node.Id}"
            : BpmnFlowNodeTypes.IsSignalThrow(node.Type)
                ? $"Signal throw event #{node.Id}"
                : BpmnFlowNodeTypes.IsSignalEnd(node.Type)
                    ? $"Signal end event #{node.Id}"
                    : $"Signal catch event #{node.Id}";

    public const int MaxPayloadEntries = 100;

    /// <summary>
    /// The payload member-name pattern as JavaScript and JSON Schema write it; the editor's
    /// <c>SIGNAL_PAYLOAD_NAME_PATTERN</c> must equal it, which a test checks. A name has no dot,
    /// because a receiver's mapping path uses dots for nesting.
    /// </summary>
    public const string PayloadNamePattern = "^[A-Za-z_][A-Za-z0-9_-]{0,99}$";

    public const string PayloadNameRuleDescription =
        "1 to 100 letters, digits, '_', or '-', starting with a letter or '_'";

    private static readonly Regex PayloadNameRegex = new(
        @"\A[A-Za-z_][A-Za-z0-9_-]{0,99}\z",
        RegexOptions.CultureInvariant);

    public static bool IsValidPayloadName(string? name) => name is not null && PayloadNameRegex.IsMatch(name);

    /// <summary>Whether any entry event of a definition configures a business key.</summary>
    public static bool HasBusinessKeys(WorkflowModel definition) =>
        (definition.FlowNodes ?? []).Any(node =>
            node is not null && BpmnFlowNodeTypes.IsEntry(node.Type) && node.BusinessKey is not null);

    /// <summary>
    /// The ordered problems of one signal throw or end event, after the shared type and
    /// topology rules: its name, its mappings, its scope, its payload, and a throw to this
    /// instance that nothing in the definition waits for. The editor's
    /// <c>validateSaveSignalThrows</c> reports the same messages in the same order, which
    /// <c>SignalThrowFixtures</c> checks.
    /// </summary>
    public static IReadOnlyList<string> ThrowProblems(WorkflowModel definition, FlowNodeModel node)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(node);
        var label = Label(node);
        var problems = new List<string>();
        var signal = node.Signal;
        var name = signal?.Name;
        if (ThrowEventNameProblem(node) is { } nameProblem)
        {
            problems.Add(nameProblem);
        }

        if (signal is null)
        {
            return problems;
        }

        if (signal.OutputMappings is { Count: > 0 })
        {
            problems.Add($"{label} cannot define output mappings; it sends signal.payload.");
        }

        var declarations = CallActivityRules.SourceDeclarations(definition);
        var ownKeyProblem = $"{label} uses this instance's business key, but this workflow's entry events do not configure businessKey.";
        problems.AddRange(ScopeProblems(definition, label, signal.Scope, declarations, ownKeyProblem));
        problems.AddRange(PayloadProblems(definition, label, signal.Payload, declarations, ownKeyProblem));

        if (string.Equals(signal.Scope?.Kind, SignalThrowScopeKinds.ThisInstance, StringComparison.Ordinal)
            && IsValidName(name)
            && !(definition.FlowNodes ?? []).Any(candidate =>
                candidate is not null
                && BpmnFlowNodeTypes.IsSignalWait(candidate.Type)
                && string.Equals(candidate.Signal?.Name, name, StringComparison.Ordinal)))
        {
            problems.Add(
                $"{label} throws '{name}' to this instance, but no signal catch or boundary event in this workflow waits for it.");
        }

        return problems;
    }

    /// <summary>The name problem of a signal throw or end event, or null when its name is valid.</summary>
    public static string? ThrowEventNameProblem(FlowNodeModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var name = node.Signal?.Name;
        return string.IsNullOrEmpty(name)
            ? $"{Label(node)} must define signal.name."
            : IsValidName(name)
                ? null
                : $"{Label(node)} signal.name must be {NameRuleDescription}.";
    }

    private static IEnumerable<string> ScopeProblems(
        WorkflowModel definition,
        string label,
        SignalThrowScopeModel? scope,
        IReadOnlyDictionary<string, IReadOnlyList<CallSourceDeclaration>> declarations,
        string ownKeyProblem)
    {
        var kind = scope?.Kind;
        if (scope is null || !SignalThrowScopeKinds.IsKnown(kind))
        {
            yield return $"{label} signal.scope.kind must be "
                + string.Join(", ", SignalThrowScopeKinds.All.Take(SignalThrowScopeKinds.All.Count - 1).Select(value => $"'{value}'"))
                + $", or '{SignalThrowScopeKinds.All[^1]}'.";
            yield break;
        }

        if (SignalThrowScopeKinds.RequiresWorkflowKey(kind))
        {
            if (string.IsNullOrWhiteSpace(scope.WorkflowKey))
            {
                yield return $"{label} signal.scope needs a workflowKey for kind '{kind}'.";
            }
            else if (WorkflowKeyProblem(scope.WorkflowKey) is { } keyProblem)
            {
                yield return $"{label} signal.scope.workflowKey {keyProblem}";
            }
        }
        else if (scope.WorkflowKey is not null)
        {
            yield return $"{label} signal.scope cannot define workflowKey for kind '{kind}'.";
        }

        if (SignalThrowScopeKinds.RequiresBusinessKeyVariable(kind))
        {
            var variable = scope.BusinessKeyVariable;
            if (string.IsNullOrWhiteSpace(variable))
            {
                yield return $"{label} signal.scope needs a businessKeyVariable for kind '{kind}'; use kind '{SignalThrowScopeKinds.OwnBusinessKey}' for this instance's own business key.";
            }
            else if (!declarations.TryGetValue(variable, out var declared))
            {
                yield return $"{label} signal.scope.businessKeyVariable '{variable}' is never declared or produced by this workflow.";
            }
            else if (!declared.Any(declaration =>
                         string.Equals(declaration.DataType, WorkflowVariableTypes.String, StringComparison.Ordinal)
                         && !declaration.IsArray))
            {
                yield return $"{label} signal.scope.businessKeyVariable '{variable}' is not declared as string.";
            }
            else if (declared.Any(declaration => declaration.IsShared))
            {
                yield return $"{label} signal.scope.businessKeyVariable '{variable}' is a shared-variable alias, which a signal scope cannot read.";
            }
        }
        else if (scope.BusinessKeyVariable is not null)
        {
            yield return $"{label} signal.scope cannot define businessKeyVariable for kind '{kind}'.";
        }

        if (string.Equals(kind, SignalThrowScopeKinds.OwnBusinessKey, StringComparison.Ordinal)
            && !HasBusinessKeys(definition))
        {
            yield return ownKeyProblem;
        }
    }

    private static IEnumerable<string> PayloadProblems(
        WorkflowModel definition,
        string label,
        IReadOnlyList<SignalPayloadEntryModel?>? payload,
        IReadOnlyDictionary<string, IReadOnlyList<CallSourceDeclaration>> declarations,
        string ownKeyProblem)
    {
        if (payload is null)
        {
            yield break;
        }

        if (payload.Count > MaxPayloadEntries)
        {
            yield return $"{label} defines more than {MaxPayloadEntries} payload entries.";
            yield break;
        }

        if (payload.Any(entry => entry is null))
        {
            yield return $"{label} payload cannot contain null entries.";
            yield break;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < payload.Count; index++)
        {
            var entry = payload[index]!;
            if (!IsValidPayloadName(entry.Name))
            {
                yield return $"{label} payload entry {index + 1} name must be {PayloadNameRuleDescription}.";
                continue;
            }

            var name = entry.Name!;
            if (!names.Add(name))
            {
                yield return $"{label} payload sends '{name}' more than once.";
                continue;
            }

            var hasVariable = !string.IsNullOrEmpty(entry.Variable);
            var hasSource = !string.IsNullOrEmpty(entry.Source);
            if (hasVariable == hasSource)
            {
                yield return $"{label} payload entry '{name}' must define either variable or source.";
                continue;
            }

            if (hasSource)
            {
                if (!SignalPayloadSources.All.Contains(entry.Source, StringComparer.Ordinal))
                {
                    yield return $"{label} payload entry '{name}' source must be '{SignalPayloadSources.InstanceId}' or '{SignalPayloadSources.BusinessKey}'.";
                }
                else if (string.Equals(entry.Source, SignalPayloadSources.BusinessKey, StringComparison.Ordinal)
                         && !HasBusinessKeys(definition))
                {
                    yield return ownKeyProblem;
                }

                continue;
            }

            var variable = entry.Variable!;
            if (!declarations.TryGetValue(variable, out var declared))
            {
                yield return $"{label} payload entry '{name}' reads '{variable}', which is never declared or produced by this workflow.";
            }
            else if (declared.Any(declaration => declaration.IsShared))
            {
                yield return $"{label} payload entry '{name}' reads shared-variable alias '{variable}', which a signal payload cannot read.";
            }
        }
    }

    /// <summary>The problem with a literal workflow key, after the property name, or null.</summary>
    private static string? WorkflowKeyProblem(string workflowKey)
    {
        if (!string.Equals(workflowKey, workflowKey.Trim(), StringComparison.Ordinal))
        {
            return "cannot have surrounding whitespace.";
        }

        if (workflowKey.EnumerateRunes().Count() > CallActivityConstraints.MaxWorkflowKeyLength)
        {
            return $"must contain at most {CallActivityConstraints.MaxWorkflowKeyLength} characters.";
        }

        return workflowKey.Contains("${", StringComparison.Ordinal)
            ? "is a literal key; ${...} is not replaced."
            : null;
    }

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
