using System.Text.RegularExpressions;

namespace Flowbit.Shared.Models;

/// <summary>
/// One place a workflow declares or produces a variable it stores on its instance.
/// </summary>
/// <param name="Name">The declared spelling.</param>
/// <param name="DataType">The declared data type.</param>
/// <param name="IsArray">Whether the value is an array.</param>
/// <param name="MayBeNull">Whether the stored value can be a whole JSON null.</param>
/// <param name="IsShared">Whether the name is a shared-variable alias, which call mappings cannot read.</param>
/// <param name="IsRequiredInput">Whether a start or flow input requires a value.</param>
public sealed record CallSourceDeclaration(
    string Name,
    string DataType,
    bool IsArray,
    bool MayBeNull,
    bool IsShared = false,
    bool IsRequiredInput = false);

/// <summary>One reason a call activity cannot use its target.</summary>
public sealed record CallContractProblem(string Code, string Message);

/// <summary>
/// The stable call failure codes persisted on invocations, results, and incidents, and
/// written to an attached boundary's error variable.
/// </summary>
public static class CallFailureCodes
{
    public const string TargetUnavailable = "call_target_unavailable";
    public const string TargetIncompatible = "call_target_incompatible";
    public const string StartFailed = "call_start_failed";
    public const string InputInvalid = "call_input_invalid";
    public const string BusinessKeyConflict = "call_business_key_conflict";
    public const string DepthExceeded = "call_depth_exceeded";
    public const string CapacityExceeded = "call_capacity_exceeded";
    public const string ChildFaulted = "call_child_faulted";
    public const string ChildCancelled = "call_child_cancelled";
    public const string OutputInvalid = "call_output_invalid";
}

/// <summary>
/// Contract rules for call activities: the checks that need only the parent, and those
/// that compare a call with its target. The editor's save validator mirrors the local rules.
/// </summary>
public static class CallActivityRules
{
    private static readonly string[] ReservedPrefixes = ["sys.", "config.", "setting.", "mi.", "gateway."];

    private static readonly string[] SupportedDataTypes =
    [
        WorkflowVariableTypes.String,
        WorkflowVariableTypes.Number,
        WorkflowVariableTypes.Boolean,
        WorkflowVariableTypes.Date,
        WorkflowVariableTypes.DateTime,
        WorkflowVariableTypes.Json
    ];

    public static bool IsReservedName(string name) =>
        ReservedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether any start or call activity in a definition can only run with a Worker: a
    /// call node, or a call-only start that would otherwise look fully synchronous.
    /// </summary>
    public static bool UsesCallExecution(WorkflowModel definition) =>
        (definition.FlowNodes ?? []).Any(node =>
            BpmnFlowNodeTypes.IsCallActivity(node.Type) || node.CallOnly);

    /// <summary>
    /// Every name a workflow declares or produces on its instance, keyed by name ignoring
    /// case, in declaration order. A name can be declared more than once, with different
    /// types, so a mapping's actual value is always checked at runtime too.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<CallSourceDeclaration>> SourceDeclarations(
        WorkflowModel definition)
    {
        var declarations = new Dictionary<string, List<CallSourceDeclaration>>(StringComparer.OrdinalIgnoreCase);
        void Add(CallSourceDeclaration declaration)
        {
            if (string.IsNullOrWhiteSpace(declaration.Name))
            {
                return;
            }

            if (!declarations.TryGetValue(declaration.Name, out var list))
            {
                declarations[declaration.Name] = list = [];
            }

            list.Add(declaration);
        }

        void AddInputs(IEnumerable<VariableModel>? variables)
        {
            foreach (var variable in (variables ?? []).OfType<VariableModel>())
            {
                Add(new CallSourceDeclaration(
                    variable.Name,
                    variable.DataType,
                    variable.IsArray,
                    MayBeNull: !variable.Required
                        && !variable.IsArray
                        && variable.DataType == WorkflowVariableTypes.Json,
                    IsRequiredInput: variable.Required));
            }
        }

        foreach (var variable in (definition.Variables ?? []).OfType<VariableModel>())
        {
            Add(new CallSourceDeclaration(
                variable.Name,
                variable.DataType,
                variable.IsArray,
                variable.Nullable,
                IsShared: string.Equals(variable.Scope, VariableScopes.Shared, StringComparison.Ordinal)));
        }

        foreach (var node in (definition.FlowNodes ?? []).OfType<FlowNodeModel>())
        {
            AddInputs(node.Variables);
            if (node.Idempotency is { } idempotency)
            {
                Add(new CallSourceDeclaration(idempotency.Variable, WorkflowVariableTypes.String, false, false));
            }

            foreach (var mapping in (node.Message?.OutputMappings ?? []).OfType<MessageOutputMappingModel>())
            {
                var dataType = mapping.DataType ?? WorkflowVariableTypes.Json;
                var isArray = mapping.IsArray ?? false;
                Add(new CallSourceDeclaration(
                    mapping.Variable,
                    dataType,
                    isArray,
                    MayBeNull: !mapping.Required && !isArray && dataType == WorkflowVariableTypes.Json));
            }

            foreach (var mapping in (node.Service?.OutputMappings ?? []).OfType<ServiceOutputMappingModel>())
            {
                if (AutomaticMultiInstanceRules.IsLocalOutput(node, mapping.Variable)) continue;
                var dataType = mapping.DataType ?? WorkflowVariableTypes.Json;
                var isArray = mapping.IsArray ?? false;
                Add(new CallSourceDeclaration(
                    mapping.Variable,
                    dataType,
                    isArray,
                    MayBeNull: !mapping.Required && !isArray && dataType == WorkflowVariableTypes.Json));
            }

            if (!string.IsNullOrWhiteSpace(node.Service?.StatusVariable)
                && !AutomaticMultiInstanceRules.IsLocalOutput(node, node.Service.StatusVariable))
            {
                Add(new CallSourceDeclaration(node.Service!.StatusVariable!, WorkflowVariableTypes.Number, false, false));
            }

            if (!string.IsNullOrWhiteSpace(node.ErrorVariable))
            {
                Add(new CallSourceDeclaration(node.ErrorVariable!, WorkflowVariableTypes.String, false, false));
            }
        }

        foreach (var flow in (definition.SequenceFlows ?? []).OfType<SequenceFlowModel>())
        {
            AddInputs(flow.Variables);
        }

        return declarations.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<CallSourceDeclaration>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first problem a call node has on its own: its configuration and the parent's
    /// declarations, without looking at the target. Returns null when it is valid.
    /// </summary>
    public static string? FindLocalProblem(WorkflowModel definition, FlowNodeModel node)
    {
        var owner = $"Call activity #{node.Id}";
        if (node.CallActivity is not { } call)
        {
            return $"{owner} must define callActivity.";
        }

        if (WorkflowKeyProblem(call.WorkflowKey) is { } keyProblem)
        {
            return $"{owner} {keyProblem}";
        }

        if (string.Equals(call.WorkflowKey, definition.Id, StringComparison.Ordinal))
        {
            return $"{owner} cannot call its own workflow family '{call.WorkflowKey}'.";
        }

        if (call.VersionBinding is not null
            && !string.Equals(call.VersionBinding, CallActivityVersionBindings.PublishedDefault, StringComparison.Ordinal))
        {
            return $"{owner} has unsupported callActivity.versionBinding '{call.VersionBinding}'; omit it to call the published default.";
        }

        return MappingListProblem(owner, "inputMappings", call.InputMappings)
            ?? MappingListProblem(owner, "outputMappings", call.OutputMappings)
            ?? InputSourceProblem(owner, definition, node, call)
            ?? OutputTargetProblem(owner, definition, node, call);
    }

    /// <summary>
    /// Every reason a call cannot use the target's default plain start and results, each
    /// with a stable code. A null target is reported as unavailable.
    /// </summary>
    public static IReadOnlyList<CallContractProblem> FindTargetProblems(
        WorkflowModel parent,
        FlowNodeModel node,
        WorkflowModel? target)
    {
        var owner = $"Call activity #{node.Id}";
        var call = node.CallActivity;
        if (call is null)
        {
            return [new CallContractProblem(CallFailureCodes.TargetIncompatible, $"{owner} must define callActivity.")];
        }

        if (target is null)
        {
            return
            [
                new CallContractProblem(
                    CallFailureCodes.TargetUnavailable,
                    $"{owner} calls workflow family '{call.WorkflowKey}', which has no published default version.")
            ];
        }

        var problems = new List<CallContractProblem>();
        void Incompatible(string message) =>
            problems.Add(new CallContractProblem(CallFailureCodes.TargetIncompatible, $"{owner} target '{call.WorkflowKey}' {message}"));

        var start = (target.FlowNodes ?? []).FirstOrDefault(candidate => candidate.Id == target.InitialEventId);
        if (start is null || !BpmnFlowNodeTypes.IsStart(start.Type))
        {
            Incompatible("has no default plain start event.");
            return problems;
        }

        if (start.Roles is { Count: > 0 })
        {
            Incompatible($"start event #{start.Id} restricts its roles; a call starts as the system actor with no roles.");
        }

        if (start.Idempotency is not null)
        {
            Incompatible($"start event #{start.Id} requires a transport idempotency header, which a call cannot send.");
        }

        var inputs = start.Variables ?? [];
        var inputMappings = (call.InputMappings ?? []).OfType<CallVariableMappingModel>().ToList();
        var parentSources = SourceDeclarations(parent);
        foreach (var mapping in inputMappings)
        {
            var input = inputs.FirstOrDefault(variable =>
                string.Equals(variable.Name, mapping.Variable, StringComparison.OrdinalIgnoreCase));
            if (input is null)
            {
                Incompatible($"start event #{start.Id} does not declare input '{mapping.Variable}'.");
                continue;
            }

            if (!string.Equals(input.DataType, mapping.DataType, StringComparison.Ordinal)
                || input.IsArray != mapping.IsArray)
            {
                Incompatible(
                    $"input '{input.Name}' is {Describe(input.DataType, input.IsArray)}, but the mapping declares {Describe(mapping.DataType, mapping.IsArray)}.");
            }

            var inputAcceptsNull = !input.Required && !input.IsArray && input.DataType == WorkflowVariableTypes.Json;
            if (!mapping.Required
                && !inputAcceptsNull
                && parentSources.TryGetValue(mapping.SourceVariable, out var declared)
                && declared.Any(source => source.MayBeNull && Matches(source, mapping)))
            {
                Incompatible(
                    $"input '{input.Name}' does not accept null, but source '{mapping.SourceVariable}' can be null; mark the mapping required or use an optional scalar json input.");
            }
        }

        foreach (var input in inputs.Where(variable => variable.Required))
        {
            var mapping = inputMappings.FirstOrDefault(candidate =>
                string.Equals(candidate.Variable, input.Name, StringComparison.OrdinalIgnoreCase));
            if (mapping is null || !mapping.Required)
            {
                Incompatible($"required input '{input.Name}' needs a mapping with required set to true.");
            }
        }

        var childSources = SourceDeclarations(target);
        foreach (var mapping in (call.OutputMappings ?? []).OfType<CallVariableMappingModel>())
        {
            if (!childSources.TryGetValue(mapping.SourceVariable, out var declared))
            {
                Incompatible($"never declares or produces result '{mapping.SourceVariable}'.");
                continue;
            }

            if (declared.Any(source => source.IsShared))
            {
                Incompatible($"result '{mapping.SourceVariable}' is a shared-variable alias, which a call mapping cannot read.");
                continue;
            }

            var matching = declared.Where(source => Matches(source, mapping)).ToList();
            if (matching.Count == 0)
            {
                Incompatible(
                    $"result '{mapping.SourceVariable}' is not declared as {Describe(mapping.DataType, mapping.IsArray)}.");
                continue;
            }

            var parentTarget = (parent.Variables ?? []).FirstOrDefault(variable =>
                string.Equals(variable.Name, mapping.Variable, StringComparison.OrdinalIgnoreCase));
            parentTarget ??= AutomaticMultiInstanceRules.IsAutomaticBody(node)
                ? (node.MultiInstance!.OutputVariables ?? []).Where(output => output is not null)
                    .Where(output => string.Equals(output.Name, mapping.Variable, StringComparison.OrdinalIgnoreCase))
                    .Select(AutomaticMultiInstanceRules.ToVariable).FirstOrDefault()
                : null;
            if (!mapping.Required
                && parentTarget is { Nullable: false }
                && matching.Any(source => source.MayBeNull))
            {
                Incompatible(
                    $"result '{mapping.SourceVariable}' can be null, but parent variable '{parentTarget.Name}' is not nullable; mark the mapping required or make the variable nullable.");
            }
        }

        return problems;
    }

    public static string? WorkflowKeyProblem(string? workflowKey)
    {
        if (string.IsNullOrWhiteSpace(workflowKey))
        {
            return "must name a workflow key in callActivity.workflowKey.";
        }

        if (!string.Equals(workflowKey, workflowKey.Trim(), StringComparison.Ordinal))
        {
            return "callActivity.workflowKey cannot have surrounding whitespace.";
        }

        if (workflowKey.EnumerateRunes().Count() > CallActivityConstraints.MaxWorkflowKeyLength)
        {
            return $"callActivity.workflowKey must contain at most {CallActivityConstraints.MaxWorkflowKeyLength} characters.";
        }

        return Regex.IsMatch(workflowKey, @"\$\{")
            ? "callActivity.workflowKey is a literal key; ${...} is not replaced."
            : null;
    }

    private static string? MappingListProblem(
        string owner,
        string property,
        IReadOnlyList<CallVariableMappingModel>? mappings)
    {
        if (mappings is null)
        {
            return $"{owner} callActivity.{property} must be an array.";
        }

        if (mappings.Count > CallActivityConstraints.MaxMappingsPerDirection)
        {
            return $"{owner} callActivity.{property} has {mappings.Count} entries; at most {CallActivityConstraints.MaxMappingsPerDirection} are allowed.";
        }

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < mappings.Count; index++)
        {
            var path = $"callActivity.{property}[{index}]";
            if (mappings[index] is not { } mapping)
            {
                return $"{owner} {path} cannot be null.";
            }

            if (NameProblem(mapping.SourceVariable) is { } sourceProblem)
            {
                return $"{owner} {path}.sourceVariable {sourceProblem}";
            }

            if (NameProblem(mapping.Variable) is { } targetProblem)
            {
                return $"{owner} {path}.variable {targetProblem}";
            }

            if (!SupportedDataTypes.Contains(mapping.DataType, StringComparer.Ordinal))
            {
                return $"{owner} {path}.dataType must be one of {string.Join(", ", SupportedDataTypes)}.";
            }

            if (!destinations.Add(mapping.Variable))
            {
                return $"{owner} callActivity.{property} writes '{mapping.Variable}' more than once.";
            }
        }

        return null;
    }

    private static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "must name a variable.";
        }

        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal))
        {
            return "cannot have surrounding whitespace.";
        }

        if (name.EnumerateRunes().Count() > CallActivityConstraints.MaxVariableNameLength)
        {
            return $"must contain at most {CallActivityConstraints.MaxVariableNameLength} characters.";
        }

        return IsReservedName(name) ? $"'{name}' uses a reserved context prefix." : null;
    }

    private static string? InputSourceProblem(string owner, WorkflowModel definition, FlowNodeModel node, CallActivityModel call)
    {
        var sources = SourceDeclarations(definition);
        foreach (var mapping in call.InputMappings)
        {
            if (!sources.TryGetValue(mapping.SourceVariable, out var declared))
            {
                return $"{owner} input source '{mapping.SourceVariable}' is never declared or produced by this workflow.";
            }

            if (declared.Any(source => source.IsShared) && !AutomaticMultiInstanceRules.IsAutomaticBody(node))
            {
                return $"{owner} input source '{mapping.SourceVariable}' is a shared-variable alias, which a call mapping cannot read.";
            }

            if (!declared.Any(source => Matches(source, mapping)))
            {
                return $"{owner} input source '{mapping.SourceVariable}' is not declared as {Describe(mapping.DataType, mapping.IsArray)}.";
            }
        }

        return null;
    }

    private static string? OutputTargetProblem(
        string owner,
        WorkflowModel definition,
        FlowNodeModel node,
        CallActivityModel call)
    {
        foreach (var mapping in call.OutputMappings)
        {
            var target = (definition.Variables ?? []).FirstOrDefault(variable =>
                string.Equals(variable.Name, mapping.Variable, StringComparison.OrdinalIgnoreCase));
            target ??= AutomaticMultiInstanceRules.IsAutomaticBody(node)
                ? (node.MultiInstance!.OutputVariables ?? []).Where(output => output is not null)
                    .Where(output => string.Equals(output.Name, mapping.Variable, StringComparison.OrdinalIgnoreCase))
                    .Select(AutomaticMultiInstanceRules.ToVariable).FirstOrDefault()
                : null;
            if (target is null)
            {
                return $"{owner} output target '{mapping.Variable}' must be a declared process variable.";
            }

            if (string.Equals(target.Scope, VariableScopes.Shared, StringComparison.Ordinal)
                && (!AutomaticMultiInstanceRules.IsAutomaticBody(node) || target.Access != SharedVariableAccessModes.ReadWrite))
            {
                return $"{owner} output target '{target.Name}' is a shared-variable alias; a call writes only instance process variables.";
            }

            if (!string.Equals(target.DataType, mapping.DataType, StringComparison.Ordinal)
                || target.IsArray != mapping.IsArray)
            {
                return $"{owner} output target '{target.Name}' is {Describe(target.DataType, target.IsArray)}, but the mapping declares {Describe(mapping.DataType, mapping.IsArray)}.";
            }

            var overlap = (definition.FlowNodes ?? []).FirstOrDefault(candidate =>
                BpmnFlowNodeTypes.IsErrorBoundary(candidate.Type)
                && candidate.AttachedToRef == node.Id
                && string.Equals(candidate.ErrorVariable, mapping.Variable, StringComparison.OrdinalIgnoreCase));
            if (overlap is not null)
            {
                return $"{owner} output target '{target.Name}' is also the errorVariable of error boundary event #{overlap.Id}.";
            }
        }

        return null;
    }

    private static bool Matches(CallSourceDeclaration source, CallVariableMappingModel mapping) =>
        string.Equals(source.DataType, mapping.DataType, StringComparison.Ordinal)
        && source.IsArray == mapping.IsArray;

    private static string Describe(string dataType, bool isArray) =>
        dataType + (isArray ? "[]" : string.Empty);
}
