using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Scoped projection service behind instance detail and execution-position
/// responses. It owns detail assembly, the execution projection, grouped
/// multi-instance progress, and version-change audit loading as read-only
/// database reads in the caller's scope; it does not evaluate actor
/// capabilities, authorize actions, or write any rows.
/// </summary>
public interface IWorkflowInstanceProjectionService
{
    Task<InstanceDetailDto?> GetDetailAsync(long instanceId, CancellationToken cancellationToken);

    Task<InstanceExecutionProjection> BuildExecutionAsync(
        WorkflowInstanceRecord instance,
        bool includeHistory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Pages an instance's direct outgoing calls, newest first. Null when the instance does
    /// not exist.
    /// </summary>
    Task<PagedResult<CallActivityExecutionDto>?> ListCallsAsync(
        long instanceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<MultiInstanceProgressDto?> GetMultiInstanceProgressAsync(
        long executionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<long, MultiInstanceProgressDto>> GetMultiInstanceProgressAsync(
        IReadOnlyCollection<long> executionIds,
        CancellationToken cancellationToken);
}
