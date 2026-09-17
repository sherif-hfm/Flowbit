using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Focused query service behind the instance list and advanced instance search
/// routes. It owns input parsing, dynamic instance-list authorization, the
/// repository query call, page enrichment, and summary DTO construction.
/// </summary>
public interface IWorkflowInstanceQueryService
{
    Task<PagedResult<InstanceSummaryDto>> ListInstancesAsync(
        ActorContext actor,
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<string>? variables,
        IReadOnlyList<string>? sort,
        string? cursor,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceSummaryDto>> SearchInstancesAsync(
        ActorContext actor,
        InstanceSearchRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow repository port for the instance list query. The scoped runtime
/// repository implements this port and <see cref="IWorkflowRuntimeRepository"/>
/// as the same instance, so query callers and the engine share one repository
/// scope, DbContext, and per-scope bookkeeping.
/// </summary>
public interface IWorkflowInstanceQueryRepository
{
    Task<PagedResult<InstanceListItem>> ListInstancesAsync(
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        VariableFilterExpression? variableFilter,
        IReadOnlyList<InstanceSortCriterion> sort,
        InstanceListAuthorization authorization,
        string? cursor,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
