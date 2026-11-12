using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface IAutomaticMultiInstanceQueryService
{
    Task<PagedResult<AutomaticMultiInstanceItemSummaryDto>?> ListItemsAsync(long executionId, int page,
        int pageSize, ActorContext actor, CancellationToken cancellationToken);
    Task<AutomaticMultiInstanceItemDetailDto?> GetItemAsync(long executionId, long itemId,
        ActorContext actor, CancellationToken cancellationToken);
}

/// <summary>SQL-authoritative read boundary. Detail visibility is applied before selecting payloads.</summary>
public interface IAutomaticMultiInstanceQueryRepository
{
    Task<PagedResult<AutomaticMultiInstanceItemSummaryDto>?> ListItemsAsync(long executionId,
        int page, int pageSize, NodeExecutionAuthorization authorization, CancellationToken cancellationToken);
    Task<AutomaticMultiInstanceItemDetailDto?> GetItemAsync(long executionId, long itemId,
        NodeExecutionAuthorization authorization, CancellationToken cancellationToken);
}
