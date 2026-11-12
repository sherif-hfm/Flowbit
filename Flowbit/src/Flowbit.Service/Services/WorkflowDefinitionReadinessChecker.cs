using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>Read-only save and publication readiness checks shared by definition commands and authoring previews.</summary>
public sealed class WorkflowDefinitionReadinessChecker(
    ISharedVariableRepository? sharedVariables = null,
    IConditionalEventDefinitionAnalyzer? conditionalEventAnalyzer = null,
    DurableProcessingOptions? durableProcessingOptions = null,
    IBusinessCalendarRepository? businessCalendars = null,
    IWorkflowDefinitionRepository? definitions = null,
    AutomaticMultiInstanceOptions? automaticMultiInstanceOptions = null)
{
    private readonly IConditionalEventDefinitionAnalyzer conditionalAnalyzer =
        conditionalEventAnalyzer ?? new ConditionalEventDefinitionAnalyzer();
    private readonly DurableProcessingOptions durableProcessing =
        durableProcessingOptions ?? new DurableProcessingOptions();

    /// <summary>
    /// Whether a definition can run only with a Worker. A call node qualifies even when
    /// nothing else in it is durable, and so does a call-only start, whose start
    /// restriction legacy replicas would not enforce.
    /// </summary>
    public static bool RequiresDurableProcessing(WorkflowModel definition) =>
        CallActivityRules.UsesCallExecution(definition)
        || definition.FlowNodes.Any(node =>
            node.AsyncBefore
            || node.AsyncAfter
            || BpmnFlowNodeTypes.IsTimerStart(node.Type)
            || BpmnFlowNodeTypes.IsTimerCatch(node.Type)
            || BpmnFlowNodeTypes.IsTimerBoundary(node.Type)
            || (BpmnFlowNodeTypes.IsConditionalEvent(node.Type)
                && node.Conditional?.EffectiveDeliveryMode
                    == ConditionalEventDeliveryModes.DurableAsync));

    public void EnsureDurablePublicationAllowed(
        WorkflowModel definition,
        bool publish)
    {
        // Until the integration step, only a Development host with the release switch
        // open may publish an automatic body, and only of a kind whose adapter is
        // installed. A body of any other kind would otherwise run as one activity.
        if (publish && definition.FlowNodes.Any(AutomaticMultiInstanceRules.IsAutomaticBody))
        {
            if (automaticMultiInstanceOptions?.Enabled != true)
            {
                throw new WorkflowDomainException(
                    "Automatic multi-instance publication is unavailable until the service, script, and call adapters pass the integration release gates.");
            }

            var unavailable = definition.FlowNodes.FirstOrDefault(node =>
                AutomaticMultiInstanceRules.IsAutomaticBody(node)
                && !AutomaticMultiInstanceRules.IsAdapterInstalled(node.Type));
            if (unavailable is not null)
            {
                throw new WorkflowDomainException(AutomaticMultiInstanceRules.AdapterUnavailableMessage(unavailable));
            }
        }

        if (!publish
            || durableProcessing.PublicationEnabled
            || !RequiresDurableProcessing(definition))
        {
            return;
        }

        throw new WorkflowDomainException(
            $"{DurableProcessingOptions.SectionName}:PublicationEnabled is false; "
            + "async, timer, durable conditional, call-activity, and call-only definitions cannot be published until the durable worker is ready.");
    }

    /// <summary>
    /// Requires every call in a published definition to pass the contract check against
    /// its target family's current published default. Drafts do not call this, so a
    /// parent and child can be drafted in either order.
    /// </summary>
    public async Task ValidateCallTargetsAsync(
        WorkflowModel definition,
        CancellationToken cancellationToken)
    {
        var problems = await FindCallTargetProblemsAsync(definition, cancellationToken);
        if (problems.Count > 0)
        {
            throw new WorkflowDomainException(string.Join(" ", problems.Select(problem => problem.Message)));
        }
    }

    /// <summary>
    /// What a call can pass to and read from each family's published default: declarations,
    /// never values. A family without one is reported so, rather than failing the request.
    /// </summary>
    public async Task<IReadOnlyList<CallTargetContractDto>> GetCallTargetContractsAsync(
        IReadOnlyList<string> workflowKeys,
        CancellationToken cancellationToken)
    {
        if (workflowKeys.Count == 0 || definitions is null)
        {
            return [];
        }

        var contracts = new List<CallTargetContractDto>(workflowKeys.Count);
        foreach (var key in workflowKeys)
        {
            var target = await definitions.GetDefaultByWorkflowKeyAsync(key, cancellationToken);
            if (target is null)
            {
                contracts.Add(new CallTargetContractDto(key, false, null, false, [], []));
                continue;
            }

            var start = target.Definition.FlowNodes.FirstOrDefault(node => node.Id == target.Definition.InitialEventId);
            var inputs = (start?.Variables ?? [])
                .Select(variable => new CallTargetVariableDto(
                    variable.Name,
                    variable.DataType,
                    variable.IsArray,
                    variable.Required,
                    !variable.Required && !variable.IsArray && variable.DataType == WorkflowVariableTypes.Json))
                .ToList();
            var results = CallActivityRules.SourceDeclarations(target.Definition)
                .SelectMany(pair => pair.Value)
                .Where(source => !source.IsShared)
                .DistinctBy(source => (source.Name.ToLowerInvariant(), source.DataType, source.IsArray))
                .Select(source => new CallTargetVariableDto(source.Name, source.DataType, source.IsArray, false, source.MayBeNull))
                .ToList();
            contracts.Add(new CallTargetContractDto(key, true, target.Version, start?.CallOnly == true, inputs, results));
        }

        return contracts;
    }

    /// <summary>
    /// Every immediate call target problem of a definition. Targets are not expanded
    /// recursively; actual nesting is bounded at runtime.
    /// </summary>
    public async Task<IReadOnlyList<CallContractProblem>> FindCallTargetProblemsAsync(
        WorkflowModel definition,
        CancellationToken cancellationToken)
    {
        var calls = (definition.FlowNodes ?? [])
            .Where(node => BpmnFlowNodeTypes.IsCallActivity(node.Type))
            .ToArray();
        if (calls.Length == 0)
        {
            return [];
        }

        if (definitions is null)
        {
            return
            [
                new CallContractProblem(
                    CallFailureCodes.TargetUnavailable,
                    "Workflow definition storage is not configured; call targets cannot be checked.")
            ];
        }

        var problems = new List<CallContractProblem>();
        var targets = new Dictionary<string, WorkflowDefinitionRecord?>(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            var key = call.CallActivity?.WorkflowKey;
            WorkflowDefinitionRecord? target = null;
            if (!string.IsNullOrWhiteSpace(key) && !targets.TryGetValue(key, out target))
            {
                target = await definitions.GetDefaultByWorkflowKeyAsync(key, cancellationToken);
                targets[key] = target;
            }

            problems.AddRange(FindCallTargetProblems(definition, call, target?.Definition));
        }

        return problems;
    }

    /// <summary>
    /// The contract check that publication, caller previews, and actual child creation
    /// share. Besides the mappings, it repeats the target's own shared and durable
    /// readiness, because a target can predate those rules or a changed gate.
    /// </summary>
    public IReadOnlyList<CallContractProblem> FindCallTargetProblems(
        WorkflowModel definition,
        FlowNodeModel call,
        WorkflowModel? target)
    {
        var problems = CallActivityRules.FindTargetProblems(definition, call, target).ToList();
        if (target is null)
        {
            return problems;
        }

        try
        {
            ValidateSharedServiceTaskDurability(target);
            ValidateSharedTransactionLockOrder(target);
        }
        catch (WorkflowDomainException error)
        {
            problems.Add(new CallContractProblem(
                CallFailureCodes.TargetIncompatible,
                $"Call activity #{call.Id} target '{call.CallActivity?.WorkflowKey}' is not ready: {error.Message}"));
        }

        return problems;
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

    /// <summary>
    /// Every calendar a timer or user task names must exist and be written with its exact casing, as a shared key is.
    /// </summary>
    public async Task ValidateBusinessCalendarReferencesAsync(
        WorkflowModel definition,
        CancellationToken cancellationToken)
    {
        var references = BusinessCalendarRules.References(definition);
        if (references.Count == 0)
        {
            return;
        }
        if (businessCalendars is null)
        {
            throw new WorkflowDomainException(
                "Business-calendar storage is not configured; definitions that name a calendar cannot be saved.");
        }

        foreach (var group in references.GroupBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var calendar = await businessCalendars.GetByNameAsync(first.Name, cancellationToken);
            if (calendar is null)
            {
                throw new WorkflowDomainException(
                    $"Flow node #{first.NodeId} names business calendar '{first.Name}', which does not exist. "
                    + "Create it on the Business calendars page or correct the name.");
            }

            var miswritten = group.FirstOrDefault(reference =>
                !string.Equals(reference.Name, calendar.Name, StringComparison.Ordinal));
            if (miswritten.Name is not null)
            {
                throw new WorkflowDomainException(
                    $"Flow node #{miswritten.NodeId} names business calendar '{miswritten.Name}', which must be written '{calendar.Name}'.");
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
