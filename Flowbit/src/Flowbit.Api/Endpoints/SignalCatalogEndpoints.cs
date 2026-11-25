using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

/// <summary>
/// The signal catalog (EVENT-04F). Settings administrators change it; users with a role from
/// Settings.RequiredRole, Signals.RequiredRole, or Workflow.RequiredRole, and managed API clients
/// with a signal scope, read it. The collection routes sit beside <c>entries</c>, so no signal
/// name can shadow them.
/// </summary>
public static class SignalCatalogEndpoints
{
    private const long MaxBodyBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapSignalCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var readers = app.MapGroup("/api/signal-catalog")
            .WithTags("Signal catalog")
            .RequireAuthorization(SignalCatalogAuthorizationPolicies.Read);

        readers.MapGet("/entries", List)
            .Produces<SignalCatalogListDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        readers.MapGet("/entries/{name}", Get)
            .Produces<SignalCatalogEntryDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        var administrators = app.MapGroup("/api/signal-catalog")
            .WithTags("Signal catalog")
            .RequireAuthorization()
            .AddEndpointFilter<SettingsAuthorizationFilter>();

        administrators.MapPost("/entries", Create)
            .Accepts<CreateSignalCatalogEntryRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .Produces<SignalCatalogEntryDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);
        administrators.MapPut("/entries/{name}", Update)
            .Accepts<UpdateSignalCatalogEntryRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .Produces<SignalCatalogEntryDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        administrators.MapPost("/entries/{name}/archive", Archive)
            .Accepts<SignalCatalogLifecycleRequest>("application/json")
            .Produces<SignalCatalogEntryDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        administrators.MapPost("/entries/{name}/reactivate", Reactivate)
            .Accepts<SignalCatalogLifecycleRequest>("application/json")
            .Produces<SignalCatalogEntryDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        administrators.MapGet("/entries/{name}/usage", Usage)
            .Produces<SignalCatalogUsageDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        administrators.MapPost("/preview", Preview)
            .Accepts<CreateSignalCatalogEntryRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .Produces<SignalCatalogImpactDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        administrators.MapGet("/uncataloged-names", Uncataloged)
            .Produces<UncatalogedSignalNameListDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> List(
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] ISignalCatalogService service,
        [FromQuery] string? name,
        [FromQuery] string? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(
            SignalCatalogCallers.Resolve(principal, actorResolver),
            name,
            status,
            page ?? 1,
            pageSize ?? 50,
            cancellationToken));

    private static async Task<IResult> Get(
        string name,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] ISignalCatalogService service,
        CancellationToken cancellationToken)
    {
        var entry = await service.GetAsync(
            SignalCatalogCallers.Resolve(principal, actorResolver),
            name,
            cancellationToken);
        return entry is null ? Results.NotFound() : Results.Ok(entry);
    }

    private static async Task<IResult> Create(
        CreateSignalCatalogEntryRequest request,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] ISignalCatalogService service,
        CancellationToken cancellationToken)
    {
        var entry = await service.CreateAsync(
            SignalCatalogCallers.Resolve(principal, actorResolver),
            request,
            cancellationToken);
        return Results.Created($"/api/signal-catalog/entries/{Uri.EscapeDataString(entry.Name)}", entry);
    }

    private static async Task<IResult> Update(
        string name,
        UpdateSignalCatalogEntryRequest request,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] ISignalCatalogService service,
        CancellationToken cancellationToken)
    {
        var entry = await service.UpdateAsync(
            SignalCatalogCallers.Resolve(principal, actorResolver),
            name,
            request,
            cancellationToken);
        return entry is null ? Results.NotFound() : Results.Ok(entry);
    }

    private static async Task<IResult> Archive(
        string name,
        SignalCatalogLifecycleRequest request,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] ISignalCatalogService service,
        CancellationToken cancellationToken)
    {
        var entry = await service.ArchiveAsync(
            SignalCatalogCallers.Resolve(principal, actorResolver),
            name,
            request,
            cancellationToken);
        return entry is null ? Results.NotFound() : Results.Ok(entry);
    }

    private static async Task<IResult> Reactivate(
        string name,
        SignalCatalogLifecycleRequest request,
        ClaimsPrincipal principal,
        [FromServices] IActorContextResolver actorResolver,
        [FromServices] ISignalCatalogService service,
        CancellationToken cancellationToken)
    {
        var entry = await service.ReactivateAsync(
            SignalCatalogCallers.Resolve(principal, actorResolver),
            name,
            request,
            cancellationToken);
        return entry is null ? Results.NotFound() : Results.Ok(entry);
    }

    private static async Task<IResult> Usage(
        string name,
        [FromServices] ISignalCatalogService service,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var usage = await service.GetUsageAsync(name, page ?? 1, pageSize ?? 50, cancellationToken);
        return usage is null ? Results.NotFound() : Results.Ok(usage);
    }

    private static async Task<IResult> Preview(
        CreateSignalCatalogEntryRequest request,
        [FromServices] ISignalCatalogService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.PreviewAsync(request, cancellationToken));

    private static async Task<IResult> Uncataloged(
        [FromServices] ISignalCatalogService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListUncatalogedNamesAsync(cancellationToken));
}
