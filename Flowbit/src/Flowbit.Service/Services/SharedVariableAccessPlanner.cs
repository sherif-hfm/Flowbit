using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using NCalc;
using NCalc.Helpers;
using System.Text.Json;

namespace Flowbit.Service.Services;

/// <summary>
/// Builds exact-name, current-node/current-flow shared-variable access plans.
/// Producer aliases come from authored write targets rather than access
/// declarations. Conditional events are instance-variable-only and therefore
/// never widen a shared-variable lock scope.
/// </summary>
public static class SharedVariableAccessPlanner
{
    public static SharedVariableAccessPlan Build(
        WorkflowModel definition,
        IConditionalEventDefinitionAnalyzer conditionalAnalyzer)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(conditionalAnalyzer);

        var hasSharedBindings = (definition.Variables ?? []).Any(variable =>
            variable is not null
            && string.Equals(variable.Scope, VariableScopes.Shared, StringComparison.Ordinal));
        var conditionalPlan = hasSharedBindings
            ? conditionalAnalyzer.Analyze(definition)
            : ConditionalEventDependencyPlan.Empty;
        return Build(definition, conditionalPlan);
    }

    public static SharedVariableAccessPlan Build(
        WorkflowModel definition,
        ConditionalEventDependencyPlan conditionalPlan)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(conditionalPlan);

        var bindings = (definition.Variables ?? [])
            .Where(variable => variable is not null
                && string.Equals(variable.Scope, VariableScopes.Shared, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(variable.Name)
                && !string.IsNullOrWhiteSpace(variable.SharedKey))
            .ToDictionary(variable => variable.Name, StringComparer.OrdinalIgnoreCase);
        var variables = (definition.Variables ?? [])
            .Where(variable => variable is not null
                && !string.IsNullOrWhiteSpace(variable.Name))
            .ToDictionary(variable => variable.Name, StringComparer.OrdinalIgnoreCase);
        var nodes = new Dictionary<int, MutableNodePlan>();
        var flows = new Dictionary<int, SharedVariableFlowAccessPlan>();
        var automaticPhases = new Dictionary<AutomaticMultiInstanceAccessKey, SharedVariableAutomaticPhaseAccessPlan>();

        MutableNodePlan Node(int nodeId)
        {
            if (!nodes.TryGetValue(nodeId, out var plan))
            {
                plan = new MutableNodePlan();
                nodes[nodeId] = plan;
            }
            return plan;
        }

        bool IsWritableShared(string? alias, out string canonicalAlias)
        {
            canonicalAlias = string.Empty;
            if (string.IsNullOrWhiteSpace(alias)
                || !bindings.TryGetValue(alias, out var binding)
                || !string.Equals(
                    binding.Access,
                    SharedVariableAccessModes.ReadWrite,
                    StringComparison.Ordinal))
            {
                return false;
            }
            canonicalAlias = binding.Name;
            return true;
        }

        void AddExpressionReads(MutableNodePlan plan, string? expressionText)
        {
            if (string.IsNullOrWhiteSpace(expressionText))
            {
                return;
            }
            var expression = new Expression(
                expressionText,
                ExpressionOptions.CaseInsensitiveStringComparer
                | ExpressionOptions.AllowNullParameter);
            if (expression.HasErrors())
            {
                return;
            }
            foreach (var parameter in expression.GetParameterNames())
            {
                if (bindings.TryGetValue(parameter, out var binding))
                {
                    plan.Reads.Add(binding.Name);
                }
            }
        }

        void AddDefaultReads(MutableNodePlan plan, JsonElement? defaultValue)
        {
            if (defaultValue is not { } raw)
            {
                return;
            }

            var templates = raw.ValueKind switch
            {
                JsonValueKind.String => [raw.GetString()],
                JsonValueKind.Array => raw.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .ToArray(),
                _ => []
            };
            foreach (var placeholder in ServiceTaskTemplating
                         .GetPlaceholderNames(templates)
                         .Where(bindings.ContainsKey))
            {
                plan.Reads.Add(bindings[placeholder].Name);
            }
        }

        foreach (var node in definition.FlowNodes ?? [])
        {
            var nodePlan = Node(node.Id);
            // User-task roles and all selectable action roles are captured together
            // on entry. Reading a policy later never accesses these variables.
            if (BpmnFlowNodeTypes.IsUserTask(node.Type))
            {
                var roleAliases = (definition.SequenceFlows ?? [])
                    .Where(flow => flow.SourceRef == node.Id && flow.IsSelectable && !flow.IsDefault)
                    .Select(flow => flow.RolesVariable)
                    .Prepend(node.RolesVariable);
                foreach (var roleAlias in roleAliases)
                {
                    if (roleAlias is not null && bindings.TryGetValue(roleAlias.Trim(), out var binding))
                        nodePlan.Reads.Add(binding.Name);
                }
            }
            foreach (var variable in node.Variables ?? [])
            {
                if (IsWritableShared(variable?.Name, out var alias)) nodePlan.Producers.Add(alias);
            }
            foreach (var mapping in node.Service?.OutputMappings ?? [])
            {
                if (IsWritableShared(mapping?.Variable, out var alias)) nodePlan.Producers.Add(alias);
                AddExpressionReads(nodePlan, mapping?.Validation);
                AddDefaultReads(nodePlan, mapping?.DefaultValue);
                if (mapping is not null
                    && variables.TryGetValue(mapping.Variable, out var target))
                {
                    AddExpressionReads(nodePlan, target.Validation);
                }
            }
            if (IsWritableShared(node.Service?.StatusVariable, out var statusAlias))
            {
                nodePlan.Producers.Add(statusAlias);
            }
            if (!string.IsNullOrWhiteSpace(node.Service?.StatusVariable)
                && variables.TryGetValue(node.Service.StatusVariable, out var statusTarget))
            {
                AddExpressionReads(nodePlan, statusTarget.Validation);
            }
            foreach (var mapping in node.Message?.OutputMappings ?? [])
            {
                if (IsWritableShared(mapping?.Variable, out var alias)) nodePlan.Producers.Add(alias);
            }
            // Ordinary calls write only instance variables; automatic items may
            // explicitly publish a declared shared target.
            foreach (var mapping in node.CallActivity?.OutputMappings ?? [])
            {
                if (AutomaticMultiInstanceRules.IsAutomaticBody(node)
                    && IsWritableShared(mapping?.Variable, out var callOutputAlias))
                    nodePlan.Producers.Add(callOutputAlias);
                if (!string.IsNullOrWhiteSpace(mapping?.Variable)
                    && variables.TryGetValue(mapping.Variable, out var target))
                {
                    AddExpressionReads(nodePlan, target.Validation);
                }
            }
            foreach (var assignment in node.Assignments ?? [])
            {
                if (IsWritableShared(assignment?.Variable, out var alias)) nodePlan.Producers.Add(alias);
            }
            if (IsWritableShared(node.MultiInstance?.ResultVariable, out var resultAlias))
            {
                nodePlan.Producers.Add(resultAlias);
            }
            if (AutomaticMultiInstanceRules.IsAutomaticBody(node))
            {
                if (node.MultiInstance!.CollectionVariable is { } sourceAlias && bindings.TryGetValue(sourceAlias, out var sourceBinding))
                    nodePlan.Reads.Add(sourceBinding.Name);
                foreach (var output in node.MultiInstance.OutputVariables ?? [])
                {
                    if (output is null) continue;
                    AddExpressionReads(nodePlan, output.Validation);
                    AddDefaultReads(nodePlan, output.DefaultValue);
                }
                if (variables.TryGetValue(node.MultiInstance.ResultVariable, out var aggregateTarget))
                    AddExpressionReads(nodePlan, aggregateTarget.Validation);
                foreach (var mapping in node.CallActivity?.InputMappings ?? [])
                    if (mapping is not null && bindings.TryGetValue(mapping.SourceVariable, out var inputBinding))
                        nodePlan.Reads.Add(inputBinding.Name);
            }

            if (node.Service is not null)
            {
                var templates = node.Service.Headers
                    .Select(header => header.Value)
                    .Prepend(node.Service.Body)
                    .Prepend(node.Service.Url)
                    .ToArray();
                foreach (var placeholder in ServiceTaskTemplating.GetPlaceholderNames(templates)
                             .Where(bindings.ContainsKey))
                {
                    nodePlan.Reads.Add(bindings[placeholder].Name);
                }
            }

            // JavaScript permits computed setVariable(name, value) targets. The
            // fallback is deliberately local to this JavaScript node.
            if (BpmnFlowNodeTypes.IsScriptTask(node.Type)
                && string.Equals(node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal))
            {
                foreach (var binding in bindings.Values.Where(binding =>
                             string.Equals(
                                 binding.Access,
                                 SharedVariableAccessModes.ReadWrite,
                                 StringComparison.Ordinal)))
                {
                    nodePlan.Producers.Add(binding.Name);
                }
            }
        }

        // A boundary error variable is produced while its attached host is
        // executing, so it belongs to the host node's lock plan.
        foreach (var boundary in (definition.FlowNodes ?? []).Where(node =>
                     BpmnFlowNodeTypes.IsErrorBoundary(node.Type)
                     && node.AttachedToRef is not null))
        {
            var hostPlan = Node(boundary.AttachedToRef!.Value);
            if (IsWritableShared(boundary.ErrorVariable, out var alias))
            {
                hostPlan.Producers.Add(alias);
            }
            if (!string.IsNullOrWhiteSpace(boundary.ErrorVariable)
                && variables.TryGetValue(boundary.ErrorVariable, out var target))
            {
                AddExpressionReads(hostPlan, target.Validation);
            }
        }

        foreach (var flow in definition.SequenceFlows ?? [])
        {
            var producers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in flow.Variables ?? [])
            {
                if (IsWritableShared(variable?.Name, out var alias)) producers.Add(alias);
            }
            flows[flow.Id] = new SharedVariableFlowAccessPlan(flow.Id, producers);
        }

        foreach (var node in (definition.FlowNodes ?? []).Where(AutomaticMultiInstanceRules.IsAutomaticBody))
        {
            var configuration = node.MultiInstance!;
            var initialize = new MutableNodePlan();
            var stage = new MutableNodePlan();
            var publish = new MutableNodePlan();
            var complete = new MutableNodePlan();
            var boundaries = (definition.FlowNodes ?? []).Where(boundary =>
                boundary.AttachedToRef == node.Id && BpmnFlowNodeTypes.IsErrorBoundary(boundary.Type)).ToArray();
            var reserved = boundaries.Select(boundary => boundary.ErrorVariable)
                .Append(configuration.CollectionVariable).Append(configuration.ResultVariable)
                .Where(alias => alias is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reservedKeys = bindings.Values.Where(binding => reserved.Contains(binding.Name))
                .Select(binding => binding.SharedKey!).ToHashSet(StringComparer.Ordinal);

            void Target(MutableNodePlan phase, string? alias)
            {
                if (IsWritableShared(alias, out var canonical)) phase.Producers.Add(canonical);
                if (alias is not null && variables.TryGetValue(alias, out var variable))
                {
                    var dependencies = new MutableNodePlan();
                    AddExpressionReads(dependencies, variable.Validation);
                    phase.Reads.UnionWith(dependencies.Reads.Where(read => !string.Equals(read, canonical, StringComparison.OrdinalIgnoreCase)));
                }
            }
            void ItemValidation(MutableNodePlan phase, string? expression)
            {
                var dependencies = new MutableNodePlan();
                AddExpressionReads(dependencies, expression);
                // The candidate overlay supplies its produced values. Their
                // catalog rows are still locked/fenced, but an unset old value
                // must not prevent validating a first successful write.
                phase.Reads.UnionWith(dependencies.Reads.Where(alias => !phase.Producers.Contains(alias)));
            }
            var optionalStageReads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Save(string phase, MutableNodePlan value, int? boundaryId = null)
            {
                var frozenReads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (phase != AutomaticMultiInstanceAccessPhases.Initialize
                    && bindings.TryGetValue(configuration.CollectionVariable ?? string.Empty, out var sourceBinding))
                    foreach (var alias in value.Reads.Where(alias => bindings.TryGetValue(alias, out var binding)
                                 && binding.SharedKey == sourceBinding.SharedKey).ToArray())
                    {
                        value.Reads.Remove(alias);
                        frozenReads.Add(alias);
                    }
                automaticPhases[new(node.Id, phase, boundaryId)] = new(value.Producers, value.Reads, frozenReads,
                    phase == AutomaticMultiInstanceAccessPhases.Initialize ? configuration.CollectionVariable : null,
                    phase == AutomaticMultiInstanceAccessPhases.StageItem
                        ? optionalStageReads.Where(value.Reads.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase)
                        : null);
            }

            Target(initialize, configuration.ResultVariable);
            Target(complete, configuration.ResultVariable);
            foreach (var timer in (definition.FlowNodes ?? []).Where(boundary => boundary.AttachedToRef == node.Id
                         && BpmnFlowNodeTypes.IsTimerBoundary(boundary.Type)).Select(boundary => boundary.Timer).OfType<TimerDefinitionModel>())
            {
                AddExpressionReads(initialize, TimerDefinitionRules.ClassifyScheduleValue(
                    TimerDefinitionRules.TimeDateProperty, timer.TimeDate, allowExpressions: true).Expression);
                AddExpressionReads(initialize, TimerDefinitionRules.ClassifyScheduleValue(
                    TimerDefinitionRules.TimeDurationProperty, timer.TimeDuration, allowExpressions: true).Expression);
            }
            if (bindings.TryGetValue(configuration.CollectionVariable ?? string.Empty, out var source))
                initialize.Reads.Add(source.Name);
            foreach (var target in AutomaticMultiInstanceRules.ItemOutputTargets(node))
            {
                Target(stage, target);
                Target(publish, target);
            }
            foreach (var local in configuration.OutputVariables ?? [])
            {
                if (local is null) continue;
                AddDefaultReads(stage, local.DefaultValue);
                ItemValidation(stage, local.Validation);
                ItemValidation(publish, local.Validation);
            }
            foreach (var mapping in node.Service?.OutputMappings ?? [])
            {
                AddDefaultReads(stage, mapping.DefaultValue);
                ItemValidation(stage, mapping.Validation);
                ItemValidation(publish, mapping.Validation);
            }
            foreach (var assignment in node.Assignments ?? []) AddExpressionReads(stage, assignment.Expression);
            foreach (var mapping in node.CallActivity?.InputMappings ?? [])
                if (bindings.TryGetValue(mapping.SourceVariable, out var binding)) stage.Reads.Add(binding.Name);
            if (node.Service is { } service)
                foreach (var alias in ServiceTaskTemplating.GetPlaceholderNames(service.Headers.Select(header => header.Value)
                    .Prepend(service.Body).Prepend(service.Url).ToArray()).Where(bindings.ContainsKey))
                    stage.Reads.Add(bindings[alias].Name);

            if (BpmnFlowNodeTypes.IsScriptTask(node.Type) && node.ScriptFormat == ScriptFormats.JavaScript)
            {
                // Computed getters/setters cannot be statically narrowed. This
                // conservative scope belongs only to this dynamic script phase.
                // A binding read only because of it may still be unset: the item
                // sees it as missing rather than failing the whole stage. Targets
                // go first so a binding another target's validation names stays
                // a required dependency.
                foreach (var binding in bindings.Values.Where(binding =>
                             !reserved.Contains(binding.Name) && !reservedKeys.Contains(binding.SharedKey!)
                             && binding.Access == SharedVariableAccessModes.ReadWrite))
                {
                    Target(stage, binding.Name);
                    Target(publish, binding.Name);
                }
                foreach (var binding in bindings.Values)
                {
                    if (stage.Reads.Add(binding.Name)) optionalStageReads.Add(binding.Name);
                }
            }

            publish.Reads.ExceptWith(publish.Producers);

            // A collection is copied at initialization and is never reread by
            // later phases, even when a template/validation names its alias.
            Save(AutomaticMultiInstanceAccessPhases.Initialize, initialize);
            Save(AutomaticMultiInstanceAccessPhases.StageItem, stage);
            Save(AutomaticMultiInstanceAccessPhases.PublishItem, publish);
            Save(AutomaticMultiInstanceAccessPhases.Complete, complete);
            foreach (var boundary in boundaries)
            {
                var error = new MutableNodePlan();
                Target(error, boundary.ErrorVariable);
                Save(AutomaticMultiInstanceAccessPhases.Error, error, boundary.Id);
            }
        }

        return new SharedVariableAccessPlan(
            nodes.ToDictionary(
                pair => pair.Key,
                pair => new SharedVariableNodeAccessPlan(
                    pair.Key,
                    pair.Value.Producers,
                    pair.Value.Reads)),
            flows,
            automaticPhases);
    }

    private sealed class MutableNodePlan
    {
        public HashSet<string> Producers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Reads { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
