using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Flowbit.Api.Auth;

/// <summary>
/// Applies the dynamic workflow-administrator role policy shared by workflow
/// definition management, running-instance management, and administrative actions.
/// </summary>
public static class WorkflowAdministratorAuthorization
{
    public static RouteHandlerBuilder RequireWorkflowAdministrator(
        this RouteHandlerBuilder endpoint)
    {
        endpoint.AddEndpointFilter(AuthorizeAsync);
        return endpoint;
    }

    public static RouteGroupBuilder RequireWorkflowAdministrator(
        this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(AuthorizeAsync);
        return group;
    }

    private static async ValueTask<object?> AuthorizeAsync(
        EndpointFilterInvocationContext invocationContext,
        EndpointFilterDelegate next)
    {
        var httpContext = invocationContext.HttpContext;
        if (httpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return await next(invocationContext);
        }

        var actorResolver = httpContext.RequestServices
            .GetRequiredService<IActorContextResolver>();
        var actor = actorResolver.Resolve(httpContext.User);
        var settings = httpContext.RequestServices.GetRequiredService<IEngineSettingsService>();
        var setting = await settings.GetByKeyAsync(
            WorkflowAdministratorPolicy.RequiredRoleSettingKey,
            httpContext.RequestAborted);
        if (!WorkflowAdministratorPolicy.IsAuthorized(actor.Roles, setting?.Value))
        {
            Log.Warning(
                "User '{User}' with roles [{Roles}] is forbidden from workflow administration. Required role(s): '{RequiredRole}'",
                actor.User ?? "anonymous",
                string.Join(", ", actor.Roles),
                string.Join(", ", WorkflowAdministratorPolicy.RequiredRoles(setting?.Value)));
            return Results.Forbid();
        }

        return await next(invocationContext);
    }
}
