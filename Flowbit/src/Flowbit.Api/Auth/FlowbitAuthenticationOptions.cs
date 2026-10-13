namespace Flowbit.Api.Auth;

/// <summary>The API's single, explicit trust boundary for user access tokens.</summary>
public sealed class FlowbitAuthenticationOptions
{
    public const string SectionName = "Authentication";
    public const string OpenIdConnectMode = "OpenIdConnect";
    public const string DevelopmentTokenMode = "DevelopmentToken";

    public string Mode { get; set; } = OpenIdConnectMode;
    public FlowbitOidcBearerOptions Oidc { get; set; } = new();

    public void Validate(IHostEnvironment environment)
    {
        if (Mode == DevelopmentTokenMode)
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Authentication:Mode DevelopmentToken is permitted only in Development.");
            return;
        }
        if (Mode != OpenIdConnectMode)
            throw new InvalidOperationException("Authentication:Mode must be OpenIdConnect or DevelopmentToken.");
        if (Oidc is null)
            throw new InvalidOperationException("Authentication:Oidc must be a configured object.");
        if (!Uri.TryCreate(Oidc.Authority, UriKind.Absolute, out var authority)
            || !string.IsNullOrEmpty(authority.UserInfo)
            || !string.IsNullOrEmpty(authority.Query)
            || !string.IsNullOrEmpty(authority.Fragment)
            || (authority.Scheme != Uri.UriSchemeHttps && authority.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("Authentication:Oidc:Authority must be an absolute HTTP(S) issuer URL without credentials, query, or fragment.");
        if (Oidc.AllowHttpMetadata && (!environment.IsDevelopment() || !authority.IsLoopback))
            throw new InvalidOperationException("Authentication:Oidc:AllowHttpMetadata is permitted only for a Development loopback authority.");
        if (authority.Scheme != Uri.UriSchemeHttps && !Oidc.AllowHttpMetadata)
            throw new InvalidOperationException("Authentication:Oidc:Authority requires HTTPS unless Development explicitly allows HTTP metadata.");
        foreach (var (name, value) in new[]
        {
            (nameof(Oidc.Audience), Oidc.Audience),
            (nameof(Oidc.NameClaimType), Oidc.NameClaimType),
            (nameof(Oidc.RoleClaimType), Oidc.RoleClaimType),
            (nameof(Oidc.ScopeClaimType), Oidc.ScopeClaimType),
            (nameof(Oidc.RequiredScope), Oidc.RequiredScope)
        })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
                throw new InvalidOperationException($"Authentication:Oidc:{name} must contain one nonblank value without whitespace.");
        }
    }
}

public sealed class FlowbitOidcBearerOptions
{
    public string Authority { get; set; } = "";
    public string Audience { get; set; } = "flowbit-api";
    public string NameClaimType { get; set; } = "flowbit_user";
    public string RoleClaimType { get; set; } = "roles";
    public string ScopeClaimType { get; set; } = "scope";
    public string RequiredScope { get; set; } = "flowbit.api";
    public bool AllowHttpMetadata { get; set; }
}
