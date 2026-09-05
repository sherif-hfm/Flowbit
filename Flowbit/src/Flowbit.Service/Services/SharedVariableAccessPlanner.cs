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
            foreach (var assignment in node.Assignments ?? [])
            {
                if (IsWritableShared(assignment?.Variable, out var alias)) nodePlan.Producers.Add(alias);
            }
            if (IsWritableShared(node.MultiInstance?.ResultVariable, out var resultAlias))
            {
                nodePlan.Producers.Add(resultAlias);
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

        return new SharedVariableAccessPlan(
            nodes.ToDictionary(
                pair => pair.Key,
                pair => new SharedVariableNodeAccessPlan(
                    pair.Key,
                    pair.Value.Producers,
                    pair.Value.Reads)),
            flows);
    }

    private sealed class MutableNodePlan
    {
        public HashSet<string> Producers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Reads { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
