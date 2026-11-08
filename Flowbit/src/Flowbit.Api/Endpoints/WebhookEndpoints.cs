using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

/// <summary>
/// Settings-administrator management of outbound lifecycle webhooks: subscriptions, signing secrets, test events,
/// and recovery of deliveries. Every route, including reads and rotation, requires the settings role; a workflow-key
/// filter on a subscription controls matching, not authority.
/// </summary>
public static class WebhookEndpoints
{
    private const long MaxRequestBodyBytes = 64 * 1024;

    private static readonly string[] PagingParameters = ["page", "pageSize"];

    private static readonly string[] DeliveryListParameters =
    [
        "subscriptionId", "eventId", "eventType", "workflowKey", "instanceId", "revisionId", "state", "failureCode",
        "from", "to", "page", "pageSize"
    ];

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/webhook-event-types", GetEventTypes)
            .WithTags("Webhooks")
            .RequireAuthorization()
            .AddEndpointFilter<SettingsAuthorizationFilter>()
            .Produces<WebhookEventCatalogDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        var subscriptions = app.MapGroup("/api/webhook-subscriptions")
            .WithTags("Webhooks")
            .RequireAuthorization()
            .AddEndpointFilter<SettingsAuthorizationFilter>();

        subscriptions.MapGet(string.Empty, ListSubscriptions)
            .Produces<PagedResult<WebhookSubscriptionDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        subscriptions.MapPost(string.Empty, CreateSubscription)
            .Accepts<CreateWebhookSubscriptionRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .Produces<WebhookSecretResultDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        subscriptions.MapGet("/{id:guid}", GetSubscription)
            .Produces<WebhookSubscriptionDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        subscriptions.MapPut("/{id:guid}", UpdateSubscription)
            .Accepts<UpdateWebhookSubscriptionRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .Produces<WebhookSubscriptionDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        subscriptions.MapPost("/{id:guid}/pause", PauseSubscription)
            .Produces<WebhookSubscriptionDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        subscriptions.MapPost("/{id:guid}/resume", ResumeSubscription)
            .Accepts<WebhookRevisionRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .Produces<WebhookSubscriptionDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        subscriptions.MapPost("/{id:guid}/test", SendTest)
            .Produces<WebhookTestResultDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        subscriptions.MapPost("/{id:guid}/rotate-secret", RotateSecret)
            .Accepts<RotateWebhookSecretRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .Produces<WebhookSecretResultDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        subscriptions.MapPost("/{id:guid}/revoke-previous-secret", RevokePreviousSecret)
            .Accepts<WebhookRevisionRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .Produces<WebhookSubscriptionDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        subscriptions.MapPost("/{id:guid}/archive", ArchiveSubscription)
            .Accepts<WebhookRevisionRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .Produces<WebhookSubscriptionDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        subscriptions.MapPost("/{id:guid}/deliveries/retry", BulkRetry)
            .Accepts<WebhookBulkDeliveryRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .Produces<WebhookBulkDeliveryResultDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        subscriptions.MapPost("/{id:guid}/deliveries/discard", BulkDiscard)
            .Accepts<WebhookBulkDeliveryRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .Produces<WebhookBulkDeliveryResultDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        var deliveries = app.MapGroup("/api/webhook-deliveries")
            .WithTags("Webhooks")
            .RequireAuthorization()
            .AddEndpointFilter<SettingsAuthorizationFilter>();

        deliveries.MapGet(string.Empty, ListDeliveries)
            .Produces<PagedResult<WebhookDeliveryDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        deliveries.MapGet("/{id:guid}", GetDelivery)
            .Produces<WebhookDeliveryDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        deliveries.MapGet("/{id:guid}/attempts", ListAttempts)
            .Produces<PagedResult<WebhookDeliveryAttemptDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        deliveries.MapPost("/{id:guid}/retry", RetryDelivery)
            .Accepts<WebhookRevisionRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .Produces<WebhookDeliveryActionResultDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);
        deliveries.MapPost("/{id:guid}/discard", DiscardDelivery)
            .Accepts<WebhookRevisionRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .Produces<WebhookDeliveryActionResultDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> GetEventTypes(
        HttpContext context,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        RejectUnsupportedQuery(context);
        return Results.Ok(await service.GetEventCatalogAsync(cancellationToken));
    }

    private static async Task<IResult> ListSubscriptions(
        int? page,
        int? pageSize,
        HttpContext context,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        RejectUnsupportedQuery(context, PagingParameters);
        return Results.Ok(await service.ListSubscriptionsAsync(page ?? 1, pageSize ?? 50, cancellationToken));
    }

    private static async Task<IResult> CreateSubscription(
        CreateWebhookSubscriptionRequest request,
        ClaimsPrincipal principal,
        HttpContext context,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateSubscriptionAsync(request, Actor(principal, actorResolver), cancellationToken);
        MarkSecretResponseAsNonCacheable(context.Response);
        return Results.Created($"/api/webhook-subscriptions/{result.Subscription.Id}", result);
    }

    private static async Task<IResult> GetSubscription(
        Guid id,
        HttpContext context,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        RejectUnsupportedQuery(context);
        var result = await service.GetSubscriptionAsync(id, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> UpdateSubscription(
        Guid id,
        UpdateWebhookSubscriptionRequest request,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.UpdateSubscriptionAsync(id, request, Actor(principal, actorResolver), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    // Pausing is always safe, so it takes no expected revision and repeating it changes nothing.
    private static async Task<IResult> PauseSubscription(
        Guid id,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.PauseAsync(id, Actor(principal, actorResolver), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ResumeSubscription(
        Guid id,
        WebhookRevisionRequest request,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ResumeAsync(id, request, Actor(principal, actorResolver), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> SendTest(
        Guid id,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.SendTestAsync(id, Actor(principal, actorResolver), cancellationToken);
        return result is null
            ? Results.NotFound()
            : Results.Accepted($"/api/webhook-deliveries/{result.DeliveryId}", result);
    }

    private static async Task<IResult> RotateSecret(
        Guid id,
        RotateWebhookSecretRequest request,
        ClaimsPrincipal principal,
        HttpContext context,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.RotateSecretAsync(id, request, Actor(principal, actorResolver), cancellationToken);
        if (result is null)
        {
            return Results.NotFound();
        }
        MarkSecretResponseAsNonCacheable(context.Response);
        return Results.Ok(result);
    }

    private static async Task<IResult> RevokePreviousSecret(
        Guid id,
        WebhookRevisionRequest request,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.RevokePreviousSecretAsync(id, request, Actor(principal, actorResolver), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ArchiveSubscription(
        Guid id,
        WebhookRevisionRequest request,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ArchiveAsync(id, request, Actor(principal, actorResolver), cancellationToken);
        return result is null
            ? Results.NotFound()
            : Results.Accepted($"/api/webhook-subscriptions/{id}", result);
    }

    private static async Task<IResult> BulkRetry(
        Guid id,
        WebhookBulkDeliveryRequest request,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.BulkRetryAsync(id, request, Actor(principal, actorResolver), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> BulkDiscard(
        Guid id,
        WebhookBulkDeliveryRequest request,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.BulkDiscardAsync(id, request, Actor(principal, actorResolver), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ListDeliveries(
        Guid? subscriptionId,
        Guid? eventId,
        string? eventType,
        string? workflowKey,
        long? instanceId,
        long? revisionId,
        string? state,
        string? failureCode,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? page,
        int? pageSize,
        HttpContext context,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        RejectUnsupportedQuery(context, DeliveryListParameters);
        var result = await service.ListDeliveriesAsync(
            new WebhookDeliveryQuery(
                subscriptionId,
                eventId,
                eventType,
                workflowKey,
                instanceId,
                revisionId,
                state,
                failureCode,
                from,
                to,
                page ?? 1,
                pageSize ?? 50),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetDelivery(
        Guid id,
        HttpContext context,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        RejectUnsupportedQuery(context);
        var result = await service.GetDeliveryAsync(id, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ListAttempts(
        Guid id,
        int? page,
        int? pageSize,
        HttpContext context,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        RejectUnsupportedQuery(context, PagingParameters);
        var result = await service.ListAttemptsAsync(id, page ?? 1, pageSize ?? 50, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> RetryDelivery(
        Guid id,
        WebhookRevisionRequest request,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.RetryDeliveryAsync(id, request, Actor(principal, actorResolver), cancellationToken);
        return result is null
            ? Results.NotFound()
            : Results.Accepted($"/api/webhook-deliveries/{id}", result);
    }

    private static async Task<IResult> DiscardDelivery(
        Guid id,
        WebhookRevisionRequest request,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] IWebhookManagementService service,
        CancellationToken cancellationToken)
    {
        var result = await service.DiscardDeliveryAsync(id, request, Actor(principal, actorResolver), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static string Actor(ClaimsPrincipal principal, IActorContextResolver resolver)
    {
        var actor = resolver.Resolve(principal).User;
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new WorkflowUnauthorizedException("An authenticated actor is required.");
        }
        return actor;
    }

    // A GET route accepts only the query parameters it documents, each once, matched like binding without regard
    // to case. Anything else is rejected rather than ignored, so a misspelled filter cannot silently widen a result.
    // Ordering is fixed by each list's contract, so a sort parameter is rejected too.
    private static void RejectUnsupportedQuery(HttpContext context, params string[] supported)
    {
        foreach (var (key, values) in context.Request.Query)
        {
            if (!supported.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                if (supported.Length > 0 && string.Equals(key, "sort", StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorkflowDomainException(
                        "Sorting is fixed: subscriptions by creation time and deliveries by occurrence time, newest first; attempts by number.");
                }
                throw new WorkflowDomainException(supported.Length == 0
                    ? $"Query parameter '{key}' is not supported; this route takes no query parameters."
                    : $"Query parameter '{key}' is not supported. This route accepts: {string.Join(", ", supported)}.");
            }
            if (values.Count > 1)
            {
                throw new WorkflowDomainException($"Query parameter '{key}' may appear only once.");
            }
        }
    }

    private static void MarkSecretResponseAsNonCacheable(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }
}
