using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

public static class SharedVariableIncidentEndpoints
{
    private const int MaxPageSize = 200;
    private const int MaxResolutionReasonLength = 1000;
    private const long MaxResolveRequestBodyBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapSharedVariableIncidentEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shared-variable-incidents")
            .WithTags("Shared Variable Incidents")
            .RequireAuthorization(SharedVariableAuthorizationPolicies.Administrator);
        group.AddEndpointFilter(static async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });

        group.MapGet(string.Empty, Search)
            .WithSummary("Search durable shared-variable wake incidents")
            .Produces<PagedResult<SharedVariableIncidentDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{incidentId:long}", Get)
            .WithSummary("Get one shared-variable wake incident")
            .Produces<SharedVariableIncidentDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{incidentId:long}/retry", Retry)
            .WithSummary("Grant one additional attempt and queue incident work for retry")
            .Produces<SharedVariableIncidentDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{incidentId:long}/resolve", Resolve)
            .Accepts<ResolveSharedVariableIncidentRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxResolveRequestBodyBytes))
            .WithSummary("Resolve an incident and cancel its durable work")
            .Produces<SharedVariableIncidentDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> Search(
        [AsParameters] SharedVariableIncidentHttpQuery query,
        [FromServices] ISharedVariableRepository repository,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page ?? 1);
        var pageSize = Math.Clamp(query.PageSize ?? 50, 1, MaxPageSize);
        var longOffset = ((long)page - 1) * pageSize;
        if (longOffset > int.MaxValue)
        {
            return Results.BadRequest(new { error = "Requested incident page is out of range." });
        }
        var result = await repository.SearchWakeIncidentsAsync(
            new SharedVariableWakeIncidentQuery(
                Normalize(query.Status),
                Normalize(query.WorkKind),
                Normalize(query.SharedKey),
                (int)longOffset,
                pageSize),
            cancellationToken);
        return Results.Ok(new PagedResult<SharedVariableIncidentDto>(
            result.Items.Select(incident => Map(incident, includeDetails: false)).ToArray(),
            page,
            pageSize,
            result.TotalCount));
    }

    private static async Task<IResult> Get(
        long incidentId,
        [FromServices] ISharedVariableRepository repository,
        CancellationToken cancellationToken)
    {
        var incident = await repository.GetWakeIncidentAsync(
            incidentId,
            cancellationToken);
        return incident is null
            ? Results.NotFound()
            : Results.Ok(Map(incident, includeDetails: true));
    }

    private static async Task<IResult> Retry(
        long incidentId,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableRepository repository,
        CancellationToken cancellationToken)
    {
        var caller = callerResolver.Resolve(principal);
        var incident = await repository.RetryWakeIncidentAsync(
            incidentId,
            caller.Id,
            cancellationToken);
        return incident is null
            ? Results.NotFound()
            : Results.Ok(Map(incident, includeDetails: false));
    }

    private static async Task<IResult> Resolve(
        long incidentId,
        ResolveSharedVariableIncidentRequest request,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableRepository repository,
        CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Results.BadRequest(new { error = "A nonblank resolution reason is required." });
        }
        if (reason.Length > MaxResolutionReasonLength)
        {
            return Results.BadRequest(new
            {
                error = $"Resolution reason cannot exceed {MaxResolutionReasonLength} characters."
            });
        }

        var caller = callerResolver.Resolve(principal);
        var incident = await repository.ResolveWakeIncidentAsync(
            incidentId,
            caller.Id,
            reason,
            cancellationToken);
        return incident is null
            ? Results.NotFound()
            : Results.Ok(Map(incident, includeDetails: false));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SharedVariableIncidentDto Map(
        SharedVariableWakeIncidentRecord incident,
        bool includeDetails) => new(
            incident.Id,
            incident.WorkKind,
            incident.WakeId,
            incident.DeliveryId,
            incident.OriginalWakeId,
            incident.OriginalDeliveryId,
            incident.SharedKey,
            incident.Revision,
            incident.InstanceId,
            incident.WorkflowDefinitionId,
            incident.TokenId,
            incident.ActivationId,
            incident.NodeId,
            incident.Type,
            incident.Status,
            incident.Summary,
            includeDetails ? incident.Details : null,
            incident.ResolutionReason,
            incident.ResolvedBy,
            incident.CreatedAt,
            incident.UpdatedAt,
            incident.ResolvedAt);

    public sealed class SharedVariableIncidentHttpQuery
    {
        [FromQuery(Name = "status")] public string? Status { get; init; }
        [FromQuery(Name = "workKind")] public string? WorkKind { get; init; }
        [FromQuery(Name = "sharedKey")] public string? SharedKey { get; init; }
        [FromQuery(Name = "page")] public int? Page { get; init; }
        [FromQuery(Name = "pageSize")] public int? PageSize { get; init; }
    }
}
