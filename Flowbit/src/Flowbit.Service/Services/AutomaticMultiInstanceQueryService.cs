using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

public sealed class AutomaticMultiInstanceQueryService(
    IAutomaticMultiInstanceQueryRepository repository,
    IEngineSettingsRepository engineSettings) : IAutomaticMultiInstanceQueryService
{
    public async Task<PagedResult<AutomaticMultiInstanceItemSummaryDto>?> ListItemsAsync(long executionId,
        int page, int pageSize, ActorContext actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateId(executionId);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        // Identical to instance detail: the authenticated actor may read progress.
        // Payload access below independently applies the node-activity scope.
        return await repository.ListItemsAsync(executionId, page, pageSize,
            await ResolveAuthorizationAsync(actor, cancellationToken), cancellationToken);
    }

    public async Task<AutomaticMultiInstanceItemDetailDto?> GetItemAsync(long executionId, long itemId,
        ActorContext actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateId(executionId);
        ValidateId(itemId);
        return await repository.GetItemAsync(executionId, itemId,
            await ResolveAuthorizationAsync(actor, cancellationToken), cancellationToken);
    }

    private async Task<NodeExecutionAuthorization> ResolveAuthorizationAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        var setting = await engineSettings.GetByKeyAsync(NodeExecutionQueryService.RequiredRoleSettingKey, cancellationToken);
        var permittedRoles = NodeExecutionQueryService.ParseConfiguredGlobalRoles(setting?.Value);
        var callerRoles = actor.Roles.Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        return new NodeExecutionAuthorization(
            permittedRoles.Any(role => callerRoles.Contains(role.ToLowerInvariant(), StringComparer.Ordinal)), callerRoles);
    }

    private static void ValidateId(long id)
    {
        if (id < 1) throw new WorkflowDomainException("Automatic multi-instance execution and item ids must be positive.");
    }
}
