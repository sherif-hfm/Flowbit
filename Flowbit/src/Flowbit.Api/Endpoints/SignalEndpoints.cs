using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

/// <summary>
/// Signal broadcasts: throw, read, cancel, and retry, for JWT users with a role from
/// <c>Signals.RequiredRole</c> and managed API clients with the signal scopes. A client
/// sees and changes only the broadcasts it threw.
/// </summary>
public static class SignalEndpoints
{
    private const long MaxThrowBodyBytes = 1024 * 1024;
    private const long MaxCommandBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapSignalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/signal-broadcasts")
            .WithTags("Signals");

        group.MapPost(string.Empty, Throw)
            .RequireAuthorization(SignalAuthorizationPolicies.Throw)
            .Accepts<ThrowSignalRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxThrowBodyBytes))
            .WithSummary("Throw a signal")
            .WithDescription("Freezes the signal subscriptions that are active now and, for a workflowKey or allWorkflows scope, one start of each family whose published default starts on the name, as the recipients, and queues their delivery. State one scope: a workflowKey, a workflowKey with a businessKey, allWorkflows set to true, or an instanceId alone. An optional expiresAfter, a fixed ISO-8601 duration from PT1S to P365D, makes the broadcast expire that long after the database's time at the throw: deliveries that have not run by then are skipped with broadcast_expired and their waits stay open. A repeated idempotencyKey returns the first broadcast with 200, with its first expiresAt.")
            .Produces<SignalBroadcastDetailDto>(StatusCodes.Status202Accepted)
            .Produces<SignalBroadcastDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        group.MapGet(string.Empty, List)
            .RequireAuthorization(SignalAuthorizationPolicies.Read)
            .WithSummary("List signal broadcasts")
            .WithDescription("Newest first, without payloads. Repeat thrownByKind (user, client, or instance) to keep only those throwers. A managed API client sees only the broadcasts it threw.")
            .Produces<PagedResult<SignalBroadcastSummaryDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:long}", Get)
            .RequireAuthorization(SignalAuthorizationPolicies.Read)
            .WithSummary("Get a signal broadcast with its counts and payload")
            .Produces<SignalBroadcastDetailDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:long}/deliveries", ListDeliveries)
            .RequireAuthorization(SignalAuthorizationPolicies.Read)
            .WithSummary("List a broadcast's deliveries")
            .WithDescription("In ascending id; pageSize is clamped to 1-200. Filter by kind (wait or start), status, or instanceId; a start's instanceId is the instance it created. A managed API client receives result codes without descriptions.")
            .Produces<PagedResult<SignalDeliveryDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:long}/cancel", Cancel)
            .RequireAuthorization(SignalAuthorizationPolicies.Throw)
            .Accepts<CancelSignalBroadcastRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxCommandBodyBytes))
            .WithSummary("Cancel a broadcast's pending deliveries")
            .WithDescription("A delivery already in its transaction finishes normally. Repeating the request changes nothing; a finished broadcast returns 409.")
            .Produces<SignalBroadcastDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:long}/retry-failed", RetryFailed)
            .RequireAuthorization(SignalAuthorizationPolicies.Throw)
            .Accepts<RetrySignalDeliveriesRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxCommandBodyBytes))
            .WithSummary("Retry a broadcast's failed deliveries")
            .WithDescription("Allowed only for a broadcast that finished with issues, with its current updatedAt, and before its expiresAt: once the expiry has passed the retry returns 409. A retried broadcast keeps its expiresAt. A wait that has ended meanwhile is skipped; a start runs the family's published default, or is skipped when that no longer starts on the name.")
            .Produces<SignalBroadcastDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        app.MapGet("/api/signal-subscriptions/summary", Summary)
            .WithTags("Signals")
            .RequireAuthorization(SignalAuthorizationPolicies.Summary)
            .WithSummary("Count active signal subscriptions and signal starts by signal name and workflow family")
            .WithDescription("count is the waits and startCount the families that start on the name. Optionally for one family, the instance with one businessKey in a family, or one instanceId; the last two count no start. At most 200 rows, with truncated set when more exist. Users only.")
            .Produces<SignalSubscriptionSummaryDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> Throw(
        ThrowSignalRequest request,
        ClaimsPrincipal principal,
        [FromServices] ISignalCallerResolver callerResolver,
        [FromServices] ISignalBroadcastService service,
        CancellationToken cancellationToken)
    {
        var (broadcast, replayed) = await service.ThrowAsync(
            request,
            callerResolver.Resolve(principal),
            cancellationToken);
        return replayed
            ? Results.Ok(broadcast)
            : Results.Accepted($"/api/signal-broadcasts/{broadcast.Id}", broadcast);
    }

    private static async Task<IResult> List(
        ClaimsPrincipal principal,
        [FromServices] ISignalCallerResolver callerResolver,
        [FromServices] ISignalBroadcastService service,
        [FromQuery] string? name,
        [FromQuery] string? workflowKey,
        [FromQuery] string? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string[]? thrownByKind,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(
            callerResolver.Resolve(principal),
            name,
            workflowKey,
            status,
            page ?? 1,
            pageSize ?? 50,
            cancellationToken,
            thrownByKind));

    private static async Task<IResult> Get(
        long id,
        ClaimsPrincipal principal,
        [FromServices] ISignalCallerResolver callerResolver,
        [FromServices] ISignalBroadcastService service,
        CancellationToken cancellationToken)
    {
        var broadcast = await service.GetAsync(callerResolver.Resolve(principal), id, cancellationToken);
        return broadcast is null ? Results.NotFound() : Results.Ok(broadcast);
    }

    private static async Task<IResult> ListDeliveries(
        long id,
        ClaimsPrincipal principal,
        [FromServices] ISignalCallerResolver callerResolver,
        [FromServices] ISignalBroadcastService service,
        [FromQuery] string? status,
        [FromQuery] long? instanceId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] string? kind,
        CancellationToken cancellationToken)
    {
        var deliveries = await service.ListDeliveriesAsync(
            callerResolver.Resolve(principal),
            id,
            status,
            instanceId,
            page ?? 1,
            pageSize ?? 50,
            cancellationToken,
            kind);
        return deliveries is null ? Results.NotFound() : Results.Ok(deliveries);
    }

    private static async Task<IResult> Cancel(
        long id,
        CancelSignalBroadcastRequest? request,
        ClaimsPrincipal principal,
        [FromServices] ISignalCallerResolver callerResolver,
        [FromServices] ISignalBroadcastService service,
        CancellationToken cancellationToken)
    {
        var broadcast = await service.CancelAsync(
            callerResolver.Resolve(principal),
            id,
            request ?? new CancelSignalBroadcastRequest(),
            cancellationToken);
        return broadcast is null ? Results.NotFound() : Results.Ok(broadcast);
    }

    private static async Task<IResult> RetryFailed(
        long id,
        RetrySignalDeliveriesRequest request,
        ClaimsPrincipal principal,
        [FromServices] ISignalCallerResolver callerResolver,
        [FromServices] ISignalBroadcastService service,
        CancellationToken cancellationToken)
    {
        var broadcast = await service.RetryFailedAsync(
            callerResolver.Resolve(principal),
            id,
            request,
            cancellationToken);
        return broadcast is null ? Results.NotFound() : Results.Ok(broadcast);
    }

    private static async Task<IResult> Summary(
        [FromServices] ISignalBroadcastService service,
        [FromQuery] string? name,
        [FromQuery] string? workflowKey,
        [FromQuery] string? businessKey,
        [FromQuery] long? instanceId,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.SummarizeSubscriptionsAsync(
            name, workflowKey, businessKey, instanceId, cancellationToken));
}
