using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

public static class SharedVariableEndpoints
{
    private const long MaxRequestBodyBytes = 1024 * 1024;
    private const long MaxLifecycleRequestBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapSharedVariableEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shared-variables")
            .WithTags("Shared Variables");

        group.MapGet(string.Empty, List)
            .RequireAuthorization(SharedVariableAuthorizationPolicies.Read)
            .WithSummary("List deployment-wide shared variables")
            .WithDescription("Returns catalog metadata only. Read the current value from the explicit value route.")
            .Produces<PagedResult<SharedVariableMetadataDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost(string.Empty, Create)
            .RequireAuthorization(SharedVariableAuthorizationPolicies.Administrator)
            .Accepts<CreateSharedVariableRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .WithSummary("Create a deployment-wide shared variable")
            .Produces<SharedVariableMetadataDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        group.MapGet("/{key}", Get)
            .RequireAuthorization(SharedVariableAuthorizationPolicies.Read)
            .WithSummary("Get a shared variable by its deployment-wide key")
            .WithDescription("Returns catalog metadata only. Read the current value from /value.")
            .Produces<SharedVariableMetadataDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPatch("/{key}", UpdateDescription)
            .RequireAuthorization(SharedVariableAuthorizationPolicies.Administrator)
            .Accepts<UpdateSharedVariableDescriptionRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxLifecycleRequestBodyBytes))
            .WithSummary("Update shared-variable description metadata")
            .Produces<SharedVariableMetadataDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        group.MapGet("/{key}/value", GetValue)
            .RequireAuthorization(SharedVariableAuthorizationPolicies.Read)
            .WithSummary("Get the current shared-variable value")
            .Produces<SharedVariableValueDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{key}/value", UpdateValue)
            .RequireAuthorization(SharedVariableAuthorizationPolicies.Write)
            .Accepts<UpdateSharedVariableValueRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .WithSummary("Update a shared-variable value using optimistic concurrency")
            .WithDescription("This route changes only the current value; contract and description metadata are unchanged.")
            .Produces<SharedVariableValueDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        group.MapPost("/{key}/archive", Archive)
            .RequireAuthorization(SharedVariableAuthorizationPolicies.Administrator)
            .Accepts<ArchiveSharedVariableRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxLifecycleRequestBodyBytes))
            .WithSummary("Archive an unused shared variable")
            .Produces<SharedVariableMetadataDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        group.MapPost("/{key}/reactivate", Reactivate)
            .RequireAuthorization(SharedVariableAuthorizationPolicies.Administrator)
            .Accepts<ReactivateSharedVariableRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxLifecycleRequestBodyBytes))
            .WithSummary("Reactivate an archived shared variable")
            .Produces<SharedVariableMetadataDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        group.MapGet("/{key}/history", ListHistory)
            .RequireAuthorization(SharedVariableAuthorizationPolicies.Administrator)
            .WithSummary("List immutable revisions for a shared variable")
            .Produces<IReadOnlyList<SharedVariableRevisionDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{key}/lifecycle-blockers", GetLifecycleBlockers)
            .RequireAuthorization(SharedVariableAuthorizationPolicies.Administrator)
            .WithSummary("Inspect blockers that prevent shared-variable archival")
            .Produces<SharedVariableLifecycleBlockersDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> List(
        string? search,
        string? status,
        int? page,
        int? pageSize,
        bool? includeArchived,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableService service,
        CancellationToken cancellationToken)
    {
        var request = new SharedVariableListRequest(
            search,
            status,
            Math.Max(1, page ?? 1),
            Math.Clamp(pageSize ?? 50, 1, 200),
            includeArchived ?? false);
        var result = await service.ListAsync(
            request,
            callerResolver.Resolve(principal),
            cancellationToken);
        return Results.Ok(new PagedResult<SharedVariableMetadataDto>(
            result.Items.Select(MapMetadata).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount)
        {
            NextCursor = result.NextCursor
        });
    }

    private static async Task<IResult> Create(
        CreateSharedVariableRequest request,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableService service,
        CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(
            request,
            callerResolver.Resolve(principal),
            cancellationToken);
        return Results.Created(
            $"/api/shared-variables/{Uri.EscapeDataString(created.Key)}",
            MapMetadata(created));
    }

    private static async Task<IResult> Get(
        string key,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(
            key,
            callerResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(MapMetadata(result));
    }

    private static async Task<IResult> GetValue(
        string key,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(
            key,
            callerResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(MapValue(result));
    }

    private static async Task<IResult> UpdateValue(
        string key,
        UpdateSharedVariableValueRequest request,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableService service,
        CancellationToken cancellationToken)
    {
        var result = await service.UpdateAsync(
            key,
            new UpdateSharedVariableRequest(
                request.Value,
                request.ExpectedRevision,
                request.RequestId,
                request.Reason),
            callerResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(MapValue(result));
    }

    private static async Task<IResult> UpdateDescription(
        string key,
        UpdateSharedVariableDescriptionRequest request,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableService service,
        CancellationToken cancellationToken)
    {
        var result = await service.UpdateDescriptionAsync(
            key,
            request,
            callerResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(MapMetadata(result));
    }

    private static async Task<IResult> Archive(
        string key,
        ArchiveSharedVariableRequest request,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ArchiveAsync(
            key,
            request,
            callerResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(MapMetadata(result));
    }

    private static async Task<IResult> Reactivate(
        string key,
        ReactivateSharedVariableRequest request,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ReactivateAsync(
            key,
            request,
            callerResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(MapMetadata(result));
    }

    private static async Task<IResult> ListHistory(
        string key,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableService service,
        CancellationToken cancellationToken)
    {
        var history = await service.ListHistoryAsync(
            key,
            Math.Max(1, page ?? 1),
            Math.Clamp(pageSize ?? 50, 1, 200),
            callerResolver.Resolve(principal),
            cancellationToken);
        return Results.Ok(history);
    }

    private static async Task<IResult> GetLifecycleBlockers(
        string key,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableService service,
        CancellationToken cancellationToken)
    {
        var blockers = await service.GetLifecycleBlockersAsync(
            key,
            callerResolver.Resolve(principal),
            cancellationToken);
        return blockers is null ? Results.NotFound() : Results.Ok(blockers);
    }

    private static SharedVariableMetadataDto MapMetadata(SharedVariableDto variable) => new(
        variable.Id,
        variable.Key,
        variable.DataType,
        variable.IsArray,
        variable.Nullable,
        variable.Validation,
        variable.Description,
        variable.HasValue,
        variable.Status,
        variable.Revision,
        variable.CreatedAt,
        variable.UpdatedAt,
        variable.ArchivedAt,
        variable.ValueRevision);

    private static SharedVariableValueDto MapValue(SharedVariableDto variable) => new(
        variable.Key,
        variable.HasValue,
        variable.Value,
        variable.Revision,
        variable.UpdatedAt,
        variable.ValueRevision);
}
