using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

public static class InstanceAdministrativeActionEndpoints
{
    public static IEndpointRouteBuilder MapInstanceAdministrativeActionEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/instances/{id:long}/administrative-actions")
            .WithTags("Administrative Actions")
            .RequireAuthorization()
            .RequireWorkflowAdministrator();
        group.MapGet(string.Empty, ListActions)
            .WithSummary("List active instance positions and direct administrative actions")
            .WithDescription("Returns ordinary user tasks and multi-instance parents independently of personal inbox eligibility.")
            .Produces<PagedResult<InstanceAdministrativeActionPositionDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost(string.Empty, ExecuteAction)
            .WithSummary("Immediately execute one administrative action and atomically record its audit")
            .WithDescription("Requires the current workflow administrator role and exact displayed position concurrency values. Multi-instance parents require an explicit mode. Ordinary task API permissions remain unchanged.")
            .Accepts<ExecuteInstanceAdministrativeActionRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(1024 * 1024))
            .Produces<AdministrativeActionResultDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);
        return app;
    }

    private static async Task<IResult> ListActions(
        long id,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceAdministrativeActionService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListInstanceActionsAsync(
            id, page ?? 1, pageSize ?? 50, actorResolver.Resolve(principal), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ExecuteAction(
        long id,
        ExecuteInstanceAdministrativeActionRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceAdministrativeActionService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ExecuteInstanceActionAsync(
            id, request, actorResolver.Resolve(principal), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
}
