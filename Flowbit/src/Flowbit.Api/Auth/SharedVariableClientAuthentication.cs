using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Flowbit.Service.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Flowbit.Api.Auth;

public static class SharedVariableClientAuthenticationDefaults
{
    public const string Scheme = "SharedVariableClient";
    public const string ClientIdHeader = "X-Client-Id";
    public const string ClientSecretHeader = "X-Client-Secret";
    public const string ClientIdClaim = "flowbit:shared_variable_client_id";
    public const string ClientRecordIdClaim = "flowbit:shared_variable_client_record_id";
    public const string ClientDisplayNameClaim = "flowbit:shared_variable_client_display_name";
    public const string ClientSecretVersionClaim = "flowbit:shared_variable_client_secret_version";
    public const string ScopeClaim = "flowbit:shared_variable_client_scope";
}

/// <summary>
/// Transport bounds for the deployment-wide shared-variable client credentials.
/// The authenticated secret is never copied into claims, logs, or response DTOs.
/// </summary>
public sealed class SharedVariableClientAuthenticationOptions : AuthenticationSchemeOptions
{
    public int MaxClientIdRunes { get; set; } = 300;

    public int MaxClientSecretBytes { get; set; } = 512;
}

/// <summary>
/// Authenticates a managed shared-variable API client from exactly one client-id
/// header and exactly one client-secret header.
/// </summary>
public sealed class SharedVariableClientAuthenticationHandler(
    IOptionsMonitor<SharedVariableClientAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ISharedVariableClientService clients)
    : AuthenticationHandler<SharedVariableClientAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var hasClientId = Request.Headers.TryGetValue(
            SharedVariableClientAuthenticationDefaults.ClientIdHeader,
            out var clientIdValues);
        var hasClientSecret = Request.Headers.TryGetValue(
            SharedVariableClientAuthenticationDefaults.ClientSecretHeader,
            out var clientSecretValues);

        if (!hasClientId && !hasClientSecret)
        {
            return AuthenticateResult.NoResult();
        }

        if (Request.Headers.ContainsKey("Authorization"))
        {
            return AuthenticateResult.Fail(
                "Bearer and shared-variable client credentials cannot be combined.");
        }

        if (!hasClientId
            || !hasClientSecret
            || clientIdValues.Count != 1
            || clientSecretValues.Count != 1)
        {
            return InvalidCredentials();
        }

        var clientId = clientIdValues[0];
        var clientSecret = clientSecretValues[0];
        if (string.IsNullOrEmpty(clientId)
            || string.IsNullOrEmpty(clientSecret)
            || clientId.EnumerateRunes().Take(Options.MaxClientIdRunes + 1).Count()
                > Options.MaxClientIdRunes
            || Encoding.UTF8.GetByteCount(clientSecret) > Options.MaxClientSecretBytes)
        {
            return InvalidCredentials();
        }

        var authentication = await clients.AuthenticateAsync(
            clientId,
            clientSecret,
            Context.RequestAborted);
        if (authentication is null)
        {
            Logger.LogWarning("Shared-variable client authentication failed.");
            return InvalidCredentials();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, authentication.ClientId),
            new(ClaimTypes.Name, authentication.ClientId),
            new(
                SharedVariableClientAuthenticationDefaults.ClientIdClaim,
                authentication.ClientId),
            new(
                SharedVariableClientAuthenticationDefaults.ClientRecordIdClaim,
                authentication.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(
                SharedVariableClientAuthenticationDefaults.ClientDisplayNameClaim,
                authentication.DisplayName),
            new(
                SharedVariableClientAuthenticationDefaults.ClientSecretVersionClaim,
                authentication.SecretVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        claims.AddRange(authentication.Scopes.Select(scope =>
            new Claim(SharedVariableClientAuthenticationDefaults.ScopeClaim, scope)));

        var identity = new ClaimsIdentity(
            claims,
            SharedVariableClientAuthenticationDefaults.Scheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(
            principal,
            SharedVariableClientAuthenticationDefaults.Scheme);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    private static AuthenticateResult InvalidCredentials() =>
        AuthenticateResult.Fail("Invalid client credentials.");
}
