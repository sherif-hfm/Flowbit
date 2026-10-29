using System.Collections.Immutable;
using System.Text;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using NCalc;
using NCalc.Exceptions;

namespace Flowbit.Service.Services;

/// <summary>
/// Extracts conditional-event dependencies from NCalc's parsed AST. Only
/// statically named, persisted instance-variable producers are observable;
/// deployment-wide shared-variable bindings are deliberately not wake sources.
/// </summary>
public sealed class ConditionalEventDefinitionAnalyzer
    : IConditionalEventDefinitionAnalyzer
{
    private const ExpressionOptions Options =
        ExpressionOptions.CaseInsensitiveStringComparer
        | ExpressionOptions.AllowNullParameter;

    private static readonly string[] NonObservablePrefixes =
        ["sys.", "config.", "setting.", "mi.", "gateway."];

    public ConditionalEventDependencyPlan Analyze(WorkflowModel definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var conditionalNodes = (definition.FlowNodes ?? [])
            .Where(node => node is not null
                && BpmnFlowNodeTypes.IsConditionalEvent(node.Type))
            .OrderBy(node => node.Id)
            .ToList();
        if (conditionalNodes.Count == 0)
        {
            return ConditionalEventDependencyPlan.Empty;
        }

        var sharedDependency = FindSharedVariableDependencies(
                definition,
                conditionalNodes)
            .FirstOrDefault();
        if (sharedDependency is not null)
        {
            var sharedNode = conditionalNodes.Single(node => node.Id == sharedDependency.NodeId);
            throw new WorkflowDomainException(
                $"{Describe(sharedNode)} cannot reference "
                + $"shared variable '{sharedDependency.VariableName}'. Conditional events "
                + "may reference only persisted instance variables. Use a message event or "
                + "copy the value into an instance variable.");
        }

        var canonicalVariables = BuildCanonicalVariableMap(definition);
        var entries = ImmutableDictionary.CreateBuilder<int, ConditionalEventPlanEntry>();
        var inverse = new Dictionary<string, SortedSet<int>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var node in conditionalNodes)
        {
            var description = Describe(node);
            var conditional = node.Conditional
                ?? throw new WorkflowDomainException(
                    $"{description} must have a conditional configuration.");
            var condition = ConditionalDefinitionRules.NormalizeCondition(
                conditional.Condition);
            if (condition is null)
            {
                throw new WorkflowDomainException(
                    $"{description} must define a condition.");
            }
            if (condition.EnumerateRunes()
                    .Take(ConditionalDefinitionRules.MaxConditionLength + 1)
                    .Count() > ConditionalDefinitionRules.MaxConditionLength)
            {
                throw new WorkflowDomainException(
                    $"{description} condition must contain at most "
                    + $"{ConditionalDefinitionRules.MaxConditionLength} Unicode scalar values.");
            }

            var deliveryMode = ConditionalEventDeliveryModes.GetEffective(
                conditional.DeliveryMode);
            if (deliveryMode is not (ConditionalEventDeliveryModes.Atomic
                or ConditionalEventDeliveryModes.DurableAsync))
            {
                throw new WorkflowDomainException(
                    $"{description} has unsupported deliveryMode "
                    + $"'{conditional.DeliveryMode}'.");
            }

            var parsed = new Expression(condition, Options);
            try
            {
                if (parsed.HasErrors())
                {
                    throw new WorkflowDomainException(
                        $"{description} has an invalid condition: "
                        + $"'{conditional.Condition}'.");
                }

                // Ignoring a built-in's casing keeps definitions saved earlier
                // running; saving rejects it (RejectUnresolvableNames).
                var unknownFunction = parsed.GetFunctionNames()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(function => !SequenceFlowConditionEvaluator.IsGeneralFunction(
                        function,
                        ignoreBuiltInCasing: true));
                if (unknownFunction is not null)
                {
                    throw new WorkflowDomainException(
                        $"{description} condition uses unsupported "
                        + $"or non-observable function '{unknownFunction}'.");
                }

                var dependencies = ResolveDependencies(
                    description,
                    parsed.GetParameterNames(),
                    canonicalVariables);
                if (dependencies.Length == 0)
                {
                    throw new WorkflowDomainException(
                        $"{description} condition must reference at "
                        + "least one declared stored instance variable.");
                }
                if (dependencies.Length > ConditionalDefinitionRules.MaxDependencies)
                {
                    throw new WorkflowDomainException(
                        $"{description} condition may reference at most "
                        + $"{ConditionalDefinitionRules.MaxDependencies} stored variables.");
                }
                var entry = new ConditionalEventPlanEntry(
                    node.Id,
                    condition,
                    deliveryMode,
                    dependencies,
                    BpmnFlowNodeTypes.IsConditionalBoundary(node.Type),
                    node.AttachedToRef,
                    node.CancelActivity ?? true);
                if (!entries.TryAdd(node.Id, entry))
                {
                    throw new WorkflowDomainException(
                        $"Flow node id #{node.Id} is duplicated.");
                }

                foreach (var dependency in dependencies)
                {
                    if (!inverse.TryGetValue(dependency, out var nodeIds))
                    {
                        nodeIds = [];
                        inverse.Add(dependency, nodeIds);
                    }
                    nodeIds.Add(node.Id);
                }
            }
            catch (WorkflowDomainException)
            {
                throw;
            }
            catch (NCalcException)
            {
                throw new WorkflowDomainException(
                    $"{description} has an invalid condition: "
                    + $"'{conditional.Condition}'.");
            }
        }

        var immutableInverse = inverse.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);
        return new ConditionalEventDependencyPlan(
            entries.ToImmutable(),
            immutableInverse);
    }

    internal static ImmutableArray<ConditionalSharedVariableDependency>
        FindSharedVariableDependencies(WorkflowModel definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var conditionalNodes = (definition.FlowNodes ?? [])
            .Where(node => node is not null
                && BpmnFlowNodeTypes.IsConditionalEvent(node.Type))
            .OrderBy(node => node.Id)
            .ToArray();
        return FindSharedVariableDependencies(definition, conditionalNodes);
    }

    private static ImmutableArray<ConditionalSharedVariableDependency>
        FindSharedVariableDependencies(
            WorkflowModel definition,
            IReadOnlyCollection<FlowNodeModel> conditionalNodes)
    {
        var sharedAliases = (definition.Variables ?? [])
            .Where(variable => variable is not null
                && !string.IsNullOrWhiteSpace(variable.Name)
                && string.Equals(
                    variable.Scope,
                    VariableScopes.Shared,
                    StringComparison.OrdinalIgnoreCase))
            .GroupBy(variable => variable.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(variable => variable.Name, StringComparer.Ordinal).First().Name.Trim(),
                StringComparer.OrdinalIgnoreCase);
        if (sharedAliases.Count == 0 || conditionalNodes.Count == 0)
        {
            return [];
        }

        var dependencies = ImmutableArray.CreateBuilder<ConditionalSharedVariableDependency>();
        foreach (var node in conditionalNodes)
        {
            var condition = ConditionalDefinitionRules.NormalizeCondition(
                node.Conditional?.Condition);
            if (condition is null)
            {
                continue;
            }

            try
            {
                var parsed = new Expression(condition, Options);
                if (parsed.HasErrors())
                {
                    continue;
                }

                foreach (var parameter in parsed.GetParameterNames()
                             .Select(name => name.Trim())
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (sharedAliases.TryGetValue(parameter, out var canonical))
                    {
                        dependencies.Add(new ConditionalSharedVariableDependency(
                            node.Id,
                            canonical));
                    }
                }
            }
            catch (NCalcException)
            {
                // The main analysis path reports malformed expressions with its
                // normal definition error. This preflight is scoped only to the
                // shared-variable policy.
            }
        }

        return dependencies
            .Distinct()
            .OrderBy(dependency => dependency.NodeId)
            .ThenBy(dependency => dependency.VariableName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static ImmutableArray<string> ResolveDependencies(
        string eventDescription,
        IEnumerable<string> parameters,
        IReadOnlyDictionary<string, string> canonicalVariables)
    {
        var dependencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawParameter in parameters.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parameter = rawParameter.Trim();
            if (parameter.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prefix = NonObservablePrefixes.FirstOrDefault(candidate =>
                parameter.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (prefix is not null)
            {
                throw new WorkflowDomainException(
                    $"{eventDescription} condition references "
                    + $"non-observable context parameter '{parameter}'.");
            }

            if (!canonicalVariables.TryGetValue(parameter, out var canonical))
            {
                throw new WorkflowDomainException(
                    $"{eventDescription} condition references undeclared "
                    + $"stored variable '{parameter}'.");
            }
            dependencies.Add(canonical);
        }

        return dependencies.ToImmutableArray();
    }

    /// <summary>
    /// Rejects names that evaluation never resolves, which would leave the
    /// condition false: a built-in function in another casing, such as If for
    /// if, and a bracketed name with spaces inside its brackets, such as
    /// [ approved ], which dependency analysis trims but NCalc reads with the
    /// spaces. Only saving applies these rules: <see cref="Analyze"/> still
    /// accepts definitions saved earlier, so their instances run as before.
    /// </summary>
    internal static void RejectUnresolvableNames(WorkflowModel definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        foreach (var node in (definition.FlowNodes ?? [])
                     .Where(node => node is not null
                         && BpmnFlowNodeTypes.IsConditionalEvent(node.Type))
                     .OrderBy(node => node.Id))
        {
            var condition = ConditionalDefinitionRules.NormalizeCondition(
                node.Conditional?.Condition);
            if (condition is null)
            {
                continue;
            }

            string? misspelled;
            string? padded;
            try
            {
                var parsed = new Expression(condition, Options);
                if (parsed.HasErrors())
                {
                    continue;
                }

                misspelled = parsed.GetFunctionNames().FirstOrDefault(function =>
                    !SequenceFlowConditionEvaluator.IsGeneralFunction(function));
                padded = FindPaddedParameter(parsed.GetParameterNames());
            }
            catch (NCalcException)
            {
                // Dependency analysis reports an invalid condition.
                continue;
            }

            if (misspelled is not null)
            {
                throw new WorkflowDomainException(
                    $"{Describe(node)} condition uses unsupported "
                    + $"or non-observable function '{misspelled}'"
                    + $"{SequenceFlowConditionEvaluator.FunctionSpellingHint(misspelled)}.");
            }

            if (padded is not null)
            {
                throw new WorkflowDomainException(
                    $"{Describe(node)} condition {PaddedBracketProblem(padded)}");
            }
        }
    }

    /// <summary>
    /// The first NCalc parameter name with spaces around it, which only a
    /// bracketed name such as [ approved ] produces, or null. Timer
    /// expressions share this rule.
    /// </summary>
    internal static string? FindPaddedParameter(IEnumerable<string> parameters) =>
        parameters.FirstOrDefault(name =>
            name.Trim() is { Length: > 0 } trimmed && trimmed.Length != name.Length);

    /// <summary>
    /// Explains a name that <see cref="FindPaddedParameter"/> found, after the
    /// subject that names the expression.
    /// </summary>
    internal static string PaddedBracketProblem(string padded) =>
        $"references '[{padded}]' with spaces inside the brackets; NCalc keeps them "
        + $"in the name, so it never matches a stored variable. Write '[{padded.Trim()}]'.";

    private static string Describe(FlowNodeModel node) =>
        BpmnFlowNodeTypes.IsConditionalBoundary(node.Type)
            ? $"Conditional boundary event #{node.Id}"
            : $"Conditional catch event #{node.Id}";

    private static IReadOnlyDictionary<string, string> BuildCanonicalVariableMap(
        WorkflowModel definition)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in DeclaredStoredVariableNames(definition)
                     .GroupBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var spellings = group
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (spellings.Length > 1)
            {
                throw new WorkflowDomainException(
                    $"Stored variable declarations for '{group.Key}' use ambiguous casing "
                    + $"({string.Join(", ", spellings.Select(value => $"'{value}'"))}); "
                    + "stored variable names are case-insensitive.");
            }
            result.Add(group.Key, spellings[0]);
        }

        return result;
    }

    /// <summary>
    /// The trimmed names of every statically declared, persisted instance
    /// variable producer, in declaration order and with repeats: process, node,
    /// and sequence-flow variables plus service/message output targets, status
    /// and error targets, and entry idempotency targets. Timer expressions share
    /// this vocabulary.
    /// </summary>
    internal static IReadOnlyList<string> DeclaredStoredVariableNames(WorkflowModel definition)
    {
        var names = new List<string>();

        AddVariables(definition.Variables);
        foreach (var node in definition.FlowNodes ?? [])
        {
            if (node is null) continue;
            AddVariables(node.Variables);
            AddTarget(node.Service?.StatusVariable);
            foreach (var mapping in node.Service?.OutputMappings ?? [])
            {
                if (mapping is not null)
                {
                    AddTarget(mapping.Variable);
                }
            }
            foreach (var mapping in node.Message?.OutputMappings ?? [])
            {
                if (mapping is not null)
                {
                    AddTarget(mapping.Variable);
                }
            }
            AddTarget(node.ErrorVariable);
            AddTarget(node.Idempotency?.Variable);
        }
        foreach (var flow in definition.SequenceFlows ?? [])
        {
            if (flow is not null)
            {
                AddVariables(flow.Variables);
            }
        }

        return names;

        void AddVariables(IEnumerable<VariableModel>? variables)
        {
            foreach (var variable in variables ?? [])
            {
                if (variable is not null)
                {
                    AddTarget(variable.Name);
                }
            }
        }

        void AddTarget(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                names.Add(value.Trim());
            }
        }
    }
}

internal sealed record ConditionalSharedVariableDependency(
    int NodeId,
    string VariableName);
