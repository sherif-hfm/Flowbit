using System.Text.Json;

namespace Flowbit.Shared.Models;

/// <summary>Common automatic-body authoring contracts, mirrored by the editor save validator.</summary>
public static class AutomaticMultiInstanceRules
{
    public const int MaxConcurrency = 1000;
    public const int MaxOutputVariables = 100;
    public const int MaxNameLength = 300;
    public const int MaxSnapshotBytes = 1024 * 1024;
    public const int MaxResultBytes = 1024 * 1024;

    public static bool IsAutomaticNode(FlowNodeModel node) =>
        BpmnFlowNodeTypes.IsServiceTask(node.Type)
        || BpmnFlowNodeTypes.IsScriptTask(node.Type)
        || BpmnFlowNodeTypes.IsCallActivity(node.Type);

    public static bool IsAutomaticBody(FlowNodeModel node) => IsAutomaticNode(node) && node.MultiInstance is not null;

    /// <summary>
    /// The activity kinds whose adapter is installed. Publication, runtime entry, and
    /// item processing read nothing else, so a kind stays refused until its adapter's
    /// entry, item, failure, and cancellation paths are complete. The integration change
    /// removes this list together with the Development-only release switch.
    /// </summary>
    public static IReadOnlySet<string> InstalledAdapterKinds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { BpmnFlowNodeTypes.ScriptTask };

    /// <summary>Whether an automatic body of this node type has an installed activity adapter.</summary>
    public static bool IsAdapterInstalled(string? nodeType) =>
        nodeType is not null && InstalledAdapterKinds.Contains(nodeType);

    public static string AdapterUnavailableMessage(FlowNodeModel node) =>
        $"Automatic multi-instance {node.Type} #{node.Id} cannot be published or run until its activity adapter is installed.";

    public static bool IsLocalOutput(FlowNodeModel node, string? name) =>
        IsAutomaticBody(node) && (node.MultiInstance!.OutputVariables ?? []).Any(output =>
            output is not null && string.Equals(output.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string DisabledMessage(int nodeId) => $"Flow node #{nodeId} automatic multiInstance is not enabled.";

    /// <summary>Checks raw metadata before normalization, without changing any authored value.</summary>
    public static string? FindAuthoredProblem(FlowNodeModel node, bool enabled)
    {
        if (node.MultiInstance is not { } multi) return null;
        if (BpmnFlowNodeTypes.IsUserTask(node.Type))
            return multi.HasMaxConcurrency || multi.HasOutputVariables
                ? $"User task #{node.Id} cannot define automatic multi-instance maxConcurrency or outputVariables."
                : null;
        if (!IsAutomaticNode(node)) return $"Flow node #{node.Id} cannot define multiInstance.";
        if (!enabled) return DisabledMessage(node.Id);
        var owner = $"Automatic multi-instance activity #{node.Id}";
        if (multi.HasMaxConcurrency && multi.MaxConcurrency is null)
            return $"{owner} maxConcurrency must be an integer from 1 to 1000.";
        if (!string.Equals(multi.Source, MultiInstanceSources.Collection, StringComparison.OrdinalIgnoreCase))
            return $"{owner} source must be 'collection'.";
        if (multi.CardinalityExpression is not null)
            return $"{owner} cannot define cardinalityExpression.";
        if (multi.OnePerActor) return $"{owner} cannot set onePerActor=true.";
        if (!string.Equals(multi.CompletionEvaluation, MultiInstanceCompletionEvaluations.AfterAll, StringComparison.OrdinalIgnoreCase))
            return $"{owner} requires completionEvaluation='afterAll'.";
        return null;
    }

    /// <summary>Definition-local contracts; catalog lifecycle/value checks remain readiness/runtime responsibilities.</summary>
    public static string? FindLocalProblem(WorkflowModel definition, FlowNodeModel node)
    {
        if (!IsAutomaticBody(node)) return null;
        if (FindAuthoredProblem(node, enabled: true) is { } authored) return authored;
        var multi = node.MultiInstance!;
        var owner = $"Automatic multi-instance activity #{node.Id}";
        if (multi.Mode is not (MultiInstanceModes.Parallel or MultiInstanceModes.Sequential))
            return $"{owner} mode must be 'parallel' or 'sequential'.";
        if (multi.MaxConcurrency is < 1 or > MaxConcurrency)
            return $"{owner} maxConcurrency must be an integer from 1 to 1000.";
        if (multi.Mode == MultiInstanceModes.Sequential && multi.MaxConcurrency is not (null or 1))
            return $"{owner} sequential mode accepts only maxConcurrency=1 or omission.";
        if (BpmnFlowNodeTypes.IsCallActivity(node.Type))
        {
            if (node.AsyncBefore || node.AsyncAfter || node.Job is not null)
                return $"Call activity #{node.Id} cannot set asyncBefore, asyncAfter, or job; a call is always durable.";
        }
        else if (!node.AsyncBefore) return $"{owner} requires asyncBefore=true.";

        var variables = (definition.Variables ?? []).OfType<VariableModel>().ToList();
        var source = Find(variables, multi.CollectionVariable);
        if (source is null || !source.IsArray || !SupportedType(source.DataType)
            || (IsShared(source) && source.Access is not (SharedVariableAccessModes.Read or SharedVariableAccessModes.ReadWrite)))
            return $"{owner} collectionVariable must reference a declared readable typed-array variable.";
        var result = Find(variables, multi.ResultVariable);
        if (result is null || result.DataType != WorkflowVariableTypes.Json || result.IsArray
            || (IsShared(result) ? result.Access != SharedVariableAccessModes.ReadWrite
                : result.DefaultValue is null || result.DefaultValue.Value.ValueKind != JsonValueKind.Array))
            return $"{owner} resultVariable must reference a writable scalar json variable accepting an array; an instance target requires an array defaultValue.";
        if (SameTarget(source, result)) return $"{owner} collectionVariable and resultVariable must be different.";
        if (multi.OutputVariables is null) return $"{owner} requires an outputVariables array (which may be empty).";
        if (multi.OutputVariables.Count > MaxOutputVariables) return $"{owner} permits at most 100 outputVariables.";

        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processNames = CallActivityRules.SourceDeclarations(definition).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var output in multi.OutputVariables)
        {
            if (output is null) return $"{owner} outputVariables cannot contain null entries.";
            if (string.IsNullOrWhiteSpace(output.Name) || output.Name.EnumerateRunes().Count() > MaxNameLength)
                return $"{owner} local output names must contain 1 to 300 Unicode scalar values.";
            if (output.Name != output.Name.Trim() || CallActivityRules.IsReservedName(output.Name))
                return $"{owner} local output '{output.Name}' must be trimmed and cannot use a reserved context prefix.";
            if (!localNames.Add(output.Name)) return $"{owner} local output '{output.Name}' is duplicated.";
            if (processNames.Contains(output.Name)) return $"{owner} local output '{output.Name}' collides with a process variable or producer.";
            if (!SupportedType(output.DataType)) return $"{owner} local output '{output.Name}' has unsupported dataType '{output.DataType}'.";
        }

        var reservedTargets = new[] { source, result }.Concat((definition.FlowNodes ?? [])
            .Where(boundary => boundary.AttachedToRef == node.Id && BpmnFlowNodeTypes.IsErrorBoundary(boundary.Type))
            .Select(boundary => Find(variables, boundary.ErrorVariable)).OfType<VariableModel>()).ToList();
        foreach (var targetName in ItemOutputTargets(node))
        {
            var target = Find(variables, targetName);
            if (target is not null && reservedTargets.Any(reserved => SameTarget(reserved, target)))
                return $"{owner} item target '{targetName}' overlaps its collection, aggregate, or boundary error target.";
            if (!localNames.Contains(targetName) && (target is null || !IsShared(target) || target.Access != SharedVariableAccessModes.ReadWrite))
                return $"{owner} item target '{targetName}' must be a declared local output or readWrite shared alias.";
        }
        foreach (var boundary in (definition.FlowNodes ?? []).Where(candidate => candidate.AttachedToRef == node.Id && BpmnFlowNodeTypes.IsErrorBoundary(candidate.Type)))
        {
            var error = Find(variables, boundary.ErrorVariable);
            if (error is not null && (SameTarget(error, source) || SameTarget(error, result)))
                return $"{owner} boundary error target must differ from its collection and aggregate targets.";
        }
        foreach (var other in (definition.FlowNodes ?? []).Where(candidate => candidate.Id != node.Id && candidate.MultiInstance is not null))
        {
            var otherResult = Find(variables, other.MultiInstance!.ResultVariable);
            if (otherResult is not null && SameTarget(result, otherResult))
                return $"{owner} resultVariable must not be another multi-instance node's aggregate target.";
        }
        var outgoing = (definition.SequenceFlows ?? []).Where(flow => flow.SourceRef == node.Id).ToList();
        if (outgoing.Count != 1 || outgoing.Any(flow => !string.IsNullOrWhiteSpace(flow.Condition)
            || flow.IsDefault || !flow.IsSelectable || flow.CompletionPriority is not null
            || !string.IsNullOrWhiteSpace(flow.CompletionCondition) || flow.CancelRemainingInstances))
            return $"{owner} requires exactly one ordinary unconditional outgoing flow without outcome settings.";
        return null;
    }

    public static IEnumerable<string> ItemOutputTargets(FlowNodeModel node) =>
        (node.Service?.OutputMappings ?? []).OfType<ServiceOutputMappingModel>().Select(mapping => mapping.Variable)
        .Concat(string.IsNullOrWhiteSpace(node.Service?.StatusVariable) ? [] : new[] { node.Service!.StatusVariable! })
        .Concat((node.Assignments ?? []).OfType<AssignmentModel>().Select(assignment => assignment.Variable))
        .Concat((node.CallActivity?.OutputMappings ?? []).OfType<CallVariableMappingModel>().Select(mapping => mapping.Variable));

    public static VariableModel ToVariable(MultiInstanceOutputVariableModel output) => new()
    {
        Name = output.Name, DataType = output.DataType, IsArray = output.IsArray,
        Nullable = output.Nullable, DefaultValue = output.DefaultValue, Validation = output.Validation
    };

    private static VariableModel? Find(IEnumerable<VariableModel> variables, string? name) =>
        variables.FirstOrDefault(variable => string.Equals(variable.Name, name, StringComparison.OrdinalIgnoreCase));
    private static bool IsShared(VariableModel variable) => variable.Scope == VariableScopes.Shared;
    private static bool SameTarget(VariableModel left, VariableModel right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase)
        || (IsShared(left) && IsShared(right) && string.Equals(left.SharedKey, right.SharedKey, StringComparison.OrdinalIgnoreCase));
    private static bool SupportedType(string? type) => type is WorkflowVariableTypes.String or WorkflowVariableTypes.Number
        or WorkflowVariableTypes.Boolean or WorkflowVariableTypes.Date or WorkflowVariableTypes.DateTime or WorkflowVariableTypes.Json;
}
