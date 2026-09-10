using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface IInstanceAdministrativeActionService
{
    Task<PagedResult<InstanceAdministrativeActionPositionDto>?> ListInstanceActionsAsync(
        long instanceId,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<AdministrativeActionResultDto?> ExecuteInstanceActionAsync(
        long instanceId,
        ExecuteInstanceAdministrativeActionRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// Trusted service entry point. The caller owns the shared unit-of-work
/// transaction, holds runtime locks, and has inserted the matching audit item.
/// This interface is not exposed through ordinary task HTTP contracts.
/// </summary>
public interface IAdministrativeActionExecutor
{
    Task<AdministrativeActionResultDto?> ExecuteInTransactionAsync(
        AdministrativeActionRequest request,
        ActorContext actor,
        CancellationToken cancellationToken);
}
