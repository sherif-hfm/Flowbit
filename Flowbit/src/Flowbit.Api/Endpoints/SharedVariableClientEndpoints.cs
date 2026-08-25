using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

public static class SharedVariableClientEndpoints
{
    private const long MaxRequestBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapSharedVariableClientEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shared-variable-clients")
            .WithTags("Shared Variable Clients")
            .RequireAuthorization(SharedVariableAuthorizationPolicies.Administrator);

        group.MapGet(string.Empty, List)
            .WithSummary("List managed shared-variable API clients")
            .Produces<PagedResult<SharedVariableClientDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:long}", Get)
            .WithSummary("Get managed shared-variable API-client metadata")
            .Produces<SharedVariableClientDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{id:long}", Update)
            .Accepts<UpdateSharedVariableClientRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .WithSummary("Update API-client metadata and scopes using optimistic concurrency")
            .WithDescription(
                "Replaces displayName, scopes, and expiresAt. clientId is case-sensitive, " +
                "immutable, and deliberately absent from this request.")
            .Produces<SharedVariableClientDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        group.MapPost(string.Empty, Create)
            .Accepts<CreateSharedVariableClientRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .WithSummary("Create a managed API client and return its secret once")
            .WithDescription(
                "clientId is administrator-chosen, case-sensitive, and immutable. " +
                "The generated 256-bit clientSecret is returned only by this response. " +
                "Flowbit stores only a versioned proof and cannot display the secret later.")
            .Produces<CreateSharedVariableClientResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        group.MapPost("/{id:long}/rotate", Rotate)
            .Accepts<RotateSharedVariableClientSecretRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .WithSummary("Rotate an API-client secret with a zero-to-seven-day grace period")
            .WithDescription(
                "gracePeriodHours defaults to 24 when omitted and must be between 0 and 168. " +
                "The generated replacement secret is returned only once.")
            .Produces<RotateSharedVariableClientSecretResult>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        group.MapPost("/{id:long}/revoke", Revoke)
            .Accepts<RevokeSharedVariableClientRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .WithSummary("Immediately revoke an API client and every active secret")
            .Produces<SharedVariableClientDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        return app;
    }

    private static async Task<IResult> List(
        int? page,
        int? pageSize,
        [FromServices] ISharedVariableClientService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListAsync(
            Math.Max(1, page ?? 1),
            Math.Clamp(pageSize ?? 50, 1, 200),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> Get(
        long id,
        [FromServices] ISharedVariableClientService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(id, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> Create(
        CreateSharedVariableClientRequest request,
        ClaimsPrincipal principal,
        HttpContext context,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableClientService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(
            request,
            callerResolver.Resolve(principal),
            cancellationToken);
        MarkSecretResponseAsNonCacheable(context.Response);
        return Results.Created(
            $"/api/shared-variable-clients/{result.Client.Id}",
            result);
    }

    private static async Task<IResult> Update(
        long id,
        UpdateSharedVariableClientRequest request,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableClientService service,
        CancellationToken cancellationToken)
    {
        var result = await service.UpdateAsync(
            id,
            request,
            callerResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> Rotate(
        long id,
        RotateSharedVariableClientSecretRequest request,
        ClaimsPrincipal principal,
        HttpContext context,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableClientService service,
        CancellationToken cancellationToken)
    {
        var result = await service.RotateSecretAsync(
            id,
            request,
            callerResolver.Resolve(principal),
            cancellationToken);
        if (result is not null)
        {
            MarkSecretResponseAsNonCacheable(context.Response);
        }
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> Revoke(
        long id,
        RevokeSharedVariableClientRequest request,
        ClaimsPrincipal principal,
        [FromServices] ISharedVariableCallerResolver callerResolver,
        [FromServices] ISharedVariableClientService service,
        CancellationToken cancellationToken)
    {
        var result = await service.RevokeAsync(
            id,
            request,
            callerResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static void MarkSecretResponseAsNonCacheable(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }
}
