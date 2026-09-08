using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;

namespace Flowbit.Api.Endpoints;

public static class RetentionEndpoints
{
    public static IEndpointRouteBuilder MapRetentionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/retention")
            .WithTags("Retention")
            .RequireAuthorization()
            .AddEndpointFilter<SettingsAuthorizationFilter>();

        group.MapGet(string.Empty, GetStatus)
            .WithSummary("Get retention policies and cleanup progress")
            .Produces<RetentionStatusDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/policies/{category}", UpdatePolicy)
            .Accepts<UpdateRetentionPolicyRequest>("application/json")
            .WithSummary("Update a retention period with an optimistic policy revision")
            .Produces<RetentionPolicyDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/preview", Preview)
            .Accepts<PreviewRetentionRequest>("application/json")
            .WithSummary("Preview eligible and protected rows without saving or deleting")
            .Produces<RetentionPreviewDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/runs", RequestRun)
            .WithSummary("Queue or join cleanup using the saved retention policies")
            .WithDescription("Manual requests use the same bounded batches and workflow-load protection as scheduled cleanup.")
            .Produces<RetentionRunDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> GetStatus(
        IRetentionRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok(await repository.GetAsync(cancellationToken));

    private static async Task<IResult> UpdatePolicy(
        string category,
        UpdateRetentionPolicyRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IRetentionRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok(await repository.UpdatePolicyAsync(
            category, request, ResolveActor(principal, actorResolver), cancellationToken));

    private static async Task<IResult> Preview(
        PreviewRetentionRequest request,
        IRetentionRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok(await repository.PreviewAsync(request, cancellationToken));

    private static async Task<IResult> RequestRun(
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IRetentionRepository repository,
        CancellationToken cancellationToken) =>
        Results.Accepted("/api/retention", await repository.RequestRunAsync(
            ResolveActor(principal, actorResolver), cancellationToken));

    private static string ResolveActor(
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver)
    {
        var actor = actorResolver.Resolve(principal).User;
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new WorkflowForbiddenException("A user identity is required to manage retention.");
        }
        return actor;
    }
}
