namespace Flowbit.Api.Auth;

/// <summary>
/// Rejects ambiguous caller attribution before either authentication scheme runs.
/// This applies to both shared-variable surfaces. Data routes accept either
/// credential source, while API-client administration remains JWT-only.
/// </summary>
public sealed class SharedVariableCredentialSourceMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsSharedVariableRoute(context.Request.Path)
            && context.Request.Headers.ContainsKey("Authorization")
            && (context.Request.Headers.ContainsKey(
                    SharedVariableClientAuthenticationDefaults.ClientIdHeader)
                || context.Request.Headers.ContainsKey(
                    SharedVariableClientAuthenticationDefaults.ClientSecretHeader)))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Use either a bearer token or X-Client-Id/X-Client-Secret, not both."
            });
            return;
        }

        await next(context);
    }

    private static bool IsSharedVariableRoute(PathString path) =>
        path.StartsWithSegments(
            "/api/shared-variables",
            StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(
            "/api/shared-variable-clients",
            StringComparison.OrdinalIgnoreCase);
}
