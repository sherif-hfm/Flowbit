using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Validates conditional-event expressions and produces their immutable variable
/// dependency plan from a workflow definition.
/// </summary>
public interface IConditionalEventDefinitionAnalyzer
{
    ConditionalEventDependencyPlan Analyze(WorkflowModel definition);
}

/// <summary>
/// Bounded process-local cache for immutable per-definition dependency plans.
/// </summary>
public interface IConditionalEventDependencyPlanCache
{
    ConditionalEventDependencyPlan GetOrAdd(
        long workflowDefinitionId,
        WorkflowModel definition);

    bool TryGet(
        long workflowDefinitionId,
        out ConditionalEventDependencyPlan plan);

    void Remove(long workflowDefinitionId);
}

/// <summary>
/// Bounded process-local cache for exact shared-variable access plans keyed by
/// immutable workflow-definition id.
/// </summary>
public interface ISharedVariableAccessPlanCache
{
    SharedVariableAccessPlan GetOrAdd(
        long workflowDefinitionId,
        WorkflowModel definition);

    bool TryGet(
        long workflowDefinitionId,
        out SharedVariableAccessPlan plan);

    void Remove(long workflowDefinitionId);
}
