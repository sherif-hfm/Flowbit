using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

/// <summary>Reads identity, definition, coverage, and grouped evidence in one database snapshot.</summary>
public interface IInstanceDiagramHistoryQueryRepository
{
    Task<InstanceDiagramHistorySnapshot?> GetAsync(long instanceId, CancellationToken cancellationToken);
}

/// <summary>Uses the instance-detail read boundary; execution-detail authorization is independent.</summary>
public interface IInstanceDiagramHistoryQueryService
{
    Task<InstanceDiagramHistoryDto?> GetAsync(long instanceId, CancellationToken cancellationToken);
}
