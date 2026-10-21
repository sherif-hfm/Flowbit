using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

public static class LocalIdentityEndpoints
{
    public static IEndpointRouteBuilder MapLocalIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Local Authentication").AddEndpointFilter<LocalIdentityFilter>();
        auth.MapPost("/token", async (LocalLoginRequest body, [FromServices] LocalIdentityService service, CancellationToken ct)
            => Results.Ok(await service.LoginAsync(body, ct))).Produces<LocalTokenPairDto>().Produces(400).Produces(401).Produces(404).Produces(429).AllowAnonymous().RequireRateLimiting("local-login");
        auth.MapPost("/refresh", async (LocalRefreshRequest body, [FromServices] LocalIdentityService service, CancellationToken ct)
            => Results.Ok(await service.RefreshAsync(body.RefreshToken, ct))).Produces<LocalTokenPairDto>().Produces(401).Produces(404).Produces(429).AllowAnonymous().RequireRateLimiting("local-login");
        auth.MapPost("/logout", async (LocalRefreshRequest body, [FromServices] LocalIdentityService service, CancellationToken ct)
            => { await service.LogoutAsync(body.RefreshToken, ct); return Results.NoContent(); }).Produces(204).Produces(401).Produces(404).Produces(429).AllowAnonymous().RequireRateLimiting("local-login");
        auth.MapGet("/session", async (ClaimsPrincipal principal, [FromServices] IActorContextResolver actors,
            [FromServices] LocalIdentityService service, CancellationToken ct)
            => Results.Ok(await service.SessionAsync(actors.Resolve(principal).User!, SessionId(principal), ct))).Produces<ActorContextDto>().Produces(401).Produces(404).RequireAuthorization();
        auth.MapPost("/change-password", async (ChangeLocalPasswordRequest body, ClaimsPrincipal principal,
            [FromServices] IActorContextResolver actors, [FromServices] LocalIdentityService service, CancellationToken ct)
            => { await service.ChangePasswordAsync(actors.Resolve(principal).User!, SessionId(principal), body, ct); return Results.NoContent(); }).Produces(204).Produces(400).Produces(401).Produces(404).RequireAuthorization();
        var users = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization()
            .AddEndpointFilter<LocalIdentityFilter>().AddEndpointFilter<LocalUserManagementFilter>();
        users.MapGet("", async ([FromServices] LocalIdentityService service, string? search, bool? active, int? page, int? pageSize, CancellationToken ct)
            => Results.Ok(await service.ListAsync(search, active, page ?? 1, pageSize ?? 50, ct))).Produces<PagedResult<LocalUserDto>>().Produces(404);
        users.MapGet("/{id:long}", async (long id, [FromServices] LocalIdentityService service, CancellationToken ct) => Results.Ok(await service.GetAsync(id, ct))).Produces<LocalUserDto>().Produces(404);
        users.MapPost("", async (CreateLocalUserRequest body, [FromServices] LocalIdentityService service, CancellationToken ct)
            => { var user = await service.CreateAsync(body, ct); return Results.Created($"/api/users/{user.Id}", user); }).Produces<LocalUserDto>(201).Produces(400).Produces(404).Produces(409);
        users.MapPut("/{id:long}", async (long id, UpdateLocalUserRequest body, [FromServices] LocalIdentityService service, CancellationToken ct)
            => Results.Ok(await service.UpdateAsync(id, body, ct))).Produces<LocalUserDto>().Produces(400).Produces(404).Produces(409);
        users.MapPost("/{id:long}/status", async (long id, SetLocalUserStatusRequest body, [FromServices] LocalIdentityService service, CancellationToken ct)
            => Results.Ok(await service.StatusAsync(id, body, ct))).Produces<LocalUserDto>().Produces(400).Produces(404).Produces(409);
        users.MapPost("/{id:long}/password", async (long id, ResetLocalPasswordRequest body, [FromServices] LocalIdentityService service, CancellationToken ct)
            => { await service.ResetPasswordAsync(id, body, ct); return Results.NoContent(); }).Produces(204).Produces(400).Produces(404).Produces(409);
        var attributes = app.MapGroup("/api/user-attributes").WithTags("User Attributes").RequireAuthorization()
            .AddEndpointFilter<LocalIdentityFilter>().AddEndpointFilter<LocalUserManagementFilter>();
        attributes.MapGet("", async ([FromServices] LocalIdentityService service, CancellationToken ct) => Results.Ok(await service.AttributesAsync(ct))).Produces<IReadOnlyList<UserAttributeDto>>().Produces(404);
        attributes.MapPost("", async (SaveUserAttributeRequest body, [FromServices] LocalIdentityService service, CancellationToken ct)
            => { var attribute = await service.SaveAttributeAsync(null, body, ct); return Results.Created($"/api/user-attributes/{attribute.Id}", attribute); }).Produces<UserAttributeDto>(201).Produces(400).Produces(404).Produces(409);
        attributes.MapPut("/{id:long}", async (long id, SaveUserAttributeRequest body, [FromServices] LocalIdentityService service, CancellationToken ct)
            => Results.Ok(await service.SaveAttributeAsync(id, body, ct))).Produces<UserAttributeDto>().Produces(400).Produces(404).Produces(409);
        return app;
    }
    private static Guid SessionId(ClaimsPrincipal principal) => Guid.TryParse(principal.FindFirstValue("sid"), out var id)
        ? id : throw new LocalIdentityException(401, "invalid_session", "A local login session is required.");
}

public sealed class LocalIdentityFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.RequestServices.GetService<FlowbitAuthenticationOptions>()?.Mode != FlowbitAuthenticationOptions.LocalMode) return Results.NotFound();
        if (!http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment() && !http.Request.IsHttps)
            return Results.Json(new { code = "https_required", error = "HTTPS is required for local account operations." }, statusCode: 400);
        http.Response.Headers.CacheControl = "no-store, private";
        try { return await next(context); }
        catch (LocalIdentityException e)
        { if (e.Status == 401) http.Response.Headers.WWWAuthenticate = "Bearer"; return Results.Json(new { code = e.Code, error = e.Message }, statusCode: e.Status); }
    }
}
public sealed class LocalUserManagementFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var service = context.HttpContext.RequestServices.GetRequiredService<LocalIdentityService>();
        var actor = context.HttpContext.RequestServices.GetRequiredService<IActorContextResolver>().Resolve(context.HttpContext.User);
        return actor.Roles.Intersect(await service.ManagementRolesAsync(context.HttpContext.RequestAborted), StringComparer.OrdinalIgnoreCase).Any()
            ? await next(context) : Results.Forbid();
    }
}
