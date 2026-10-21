using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace Flowbit.Ui.Auth;

public static class UiTokenInfoEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void MapCurrentUserTokenInfo(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/_info", async (HttpContext context, IUiTokenInfoProvider tokens) =>
        {
            context.Response.Headers.CacheControl = "no-store, private";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            try
            {
                var info = await tokens.GetTokenInfoAsync(context.User, context.RequestAborted);
                return Results.Json(info, JsonOptions);
            }
            catch (UiSessionExpiredException)
            {
                await context.SignOutAsync(UiAuthentication.CookieScheme);
                return Results.Json(new { error = "session_expired", loginUrl = "/auth/login?returnUrl=/_info" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            catch (UiAuthenticationUnavailableException)
            {
                return Results.Json(new { error = "authentication_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization();
    }
}
