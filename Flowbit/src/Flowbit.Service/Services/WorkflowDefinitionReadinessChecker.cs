using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>Read-only save and publication readiness checks shared by definition commands and authoring previews.</summary>
public sealed class WorkflowDefinitionReadinessChecker(
    ISharedVariableRepository? sharedVariables = null,
    IConditionalEventDefinitionAnalyzer? conditionalEventAnalyzer = null,
    DurableProcessingOptions? durableProcessingOptions = null)
{
    private readonly IConditionalEventDefinitionAnalyzer conditionalAnalyzer =
        conditionalEventAnalyzer ?? new ConditionalEventDefinitionAnalyzer();
    private readonly DurableProcessingOptions durableProcessing =
        durableProcessingOptions ?? new DurableProcessingOptions();
    public void EnsureDurablePublicationAllowed(
        WorkflowModel definition,
        bool publish)
    {
        if (!publish
            || durableProcessing.PublicationEnabled
            || !definition.FlowNodes.Any(node =>
                node.AsyncBefore
                || node.AsyncAfter
                || BpmnFlowNodeTypes.IsTimerStart(node.Type)
                || BpmnFlowNodeTypes.IsTimerCatch(node.Type)
                || BpmnFlowNodeTypes.IsTimerBoundary(node.Type)
                || (BpmnFlowNodeTypes.IsConditionalEvent(node.Type)
                    && node.Conditional?.EffectiveDeliveryMode
                        == ConditionalEventDeliveryModes.DurableAsync)))
        {
            return;
        }

        throw new WorkflowDomainException(
            $"{DurableProcessingOptions.SectionName}:PublicationEnabled is false; "
            + "async, timer, and durable conditional definitions cannot be published until the durable worker is ready.");
    }

    public async Task ValidateSharedCatalogBindingsAsync(
        WorkflowModel definition,
        CancellationToken cancellationToken)
    {
        var bindings = (definition.Variables ?? [])
            .Where(variable => variable is not null
                && string.Equals(
                    variable.Scope,
                    VariableScopes.Shared,
                    StringComparison.Ordinal))
            .OrderBy(variable => variable.SharedKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (bindings.Length == 0)
        {
            return;
        }
        if (sharedVariables is null)
        {
            throw new WorkflowDomainException(
                "Shared-variable storage is not configured; shared workflow bindings cannot be saved.");
        }

        foreach (var binding in bindings)
        {
            var key = binding.SharedKey!;
            var catalog = await sharedVariables.GetByKeyAsync(
                key,
                includeArchived: true,
                cancellationToken);
            if (catalog is null)
            {
                throw new WorkflowDomainException(
                    $"Shared process variable '{binding.Name}' references unknown catalog key '{key}'.");
            }
            if (!string.Equals(catalog.Key, key, StringComparison.Ordinal))
            {
                throw new WorkflowDomainException(
                    $"Shared key '{key}' must use the catalog's canonical casing '{catalog.Key}'.");
            }
            if (!string.Equals(catalog.Status, SharedVariableStatuses.Active, StringComparison.Ordinal))
            {
                throw new WorkflowDomainException(
                    $"Shared process variable '{binding.Name}' references archived catalog key '{catalog.Key}'.");
            }
            if (!string.Equals(catalog.DataType, binding.DataType, StringComparison.Ordinal)
                || catalog.IsArray != binding.IsArray
                || catalog.Nullable != binding.Nullable
                || !string.Equals(
                    NormalizeContractRule(catalog.Validation),
                    NormalizeContractRule(binding.Validation),
                    StringComparison.Ordinal))
            {
                throw new WorkflowDomainException(
                    $"Shared process variable '{binding.Name}' contract does not match catalog key '{catalog.Key}'.");
            }
        }
    }

    public void ValidateSharedServiceTaskDurability(WorkflowModel definition)
    {
        if (!(definition.Variables ?? []).Any(variable => variable is not null
                && string.Equals(variable.Scope, VariableScopes.Shared, StringComparison.Ordinal)))
        {
            return;
        }

        var accessPlan = SharedVariableAccessPlanner.Build(
            definition,
            conditionalAnalyzer);
        foreach (var node in (definition.FlowNodes ?? []).Where(node =>
                     BpmnFlowNodeTypes.IsServiceTask(node.Type)
                     && string.Equals(
                         node.Service?.Type,
                         ServiceConnectorTypes.Rest,
                         StringComparison.Ordinal)
                     && !node.AsyncBefore))
        {
            if (accessPlan.ForNode(node.Id).TouchesSharedVariables)
            {
                throw new WorkflowDomainException(
                    $"REST service task #{node.Id} must set asyncBefore=true when its transition reads, writes, or locks shared variables.");
            }
        }
    }

    public void ValidateSharedTransactionLockOrder(WorkflowModel definition)
    {
        if (!(definition.Variables ?? []).Any(variable => variable is not null
                && string.Equals(variable.Scope, VariableScopes.Shared, StringComparison.Ordinal)))
        {
            return;
        }

        var conditionalPlan = conditionalAnalyzer.Analyze(definition);
        var accessPlan = SharedVariableAccessPlanner.Build(definition, conditionalPlan);
        SharedVariableTransactionLockOrderValidator.Validate(
            definition,
            accessPlan,
            conditionalPlan);
    }

    private static string? NormalizeContractRule(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
