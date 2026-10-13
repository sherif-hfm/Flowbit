using Microsoft.AspNetCore.Authorization;

namespace Flowbit.Ui.Auth;

public static class UiHttpsBoundary
{
    public static IApplicationBuilder UseFlowbitHttpsBoundary(this IApplicationBuilder app,
        UiAuthenticationOptions options, IHostEnvironment environment)
    {
        if (options.IsDevelopmentToken || environment.IsDevelopment()) return app;
        return app.Use(async (context, next) =>
        {
            // Redirection may be unavailable behind a TLS-terminating proxy. Forwarded
            // headers must run first so only a trusted proxy can supply the HTTPS scheme.
            var path = context.Request.Path;
            var authenticationPath = path.StartsWithSegments("/auth")
                || path.StartsWithSegments("/_blazor")
                || path.Equals("/signin-oidc") || path.Equals("/signout-oidc")
                || path.Equals("/signout-callback-oidc");
            var protectedEndpoint = context.GetEndpoint()?.Metadata.GetMetadata<IAuthorizeData>() is not null;
            if (!context.Request.IsHttps && (authenticationPath || protectedEndpoint))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("HTTPS is required for authentication.");
                return;
            }
            await next(context);
        });
    }
}
