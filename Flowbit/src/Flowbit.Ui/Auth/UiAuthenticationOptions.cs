namespace Flowbit.Ui.Auth;

public sealed class UiAuthenticationOptions
{
    public string Mode { get; set; } = "OpenIdConnect";
    public bool IsDevelopmentToken => Mode == "DevelopmentToken";
    public UiOidcOptions Oidc { get; set; } = new();
    public UiSessionOptions Session { get; set; } = new();
    public string? DataProtectionKeysPath { get; set; }
    public string? DataProtectionCertificatePath { get; set; }
    public string? DataProtectionCertificatePassword { get; set; }

    public void Validate(IHostEnvironment environment)
    {
        if (Mode is not ("OpenIdConnect" or "DevelopmentToken"))
            throw new InvalidOperationException("Authentication:Mode must be OpenIdConnect or DevelopmentToken.");
        if (IsDevelopmentToken)
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("DevelopmentToken authentication requires the Development environment.");
            return;
        }
        if (Oidc is null || Session is null)
            throw new InvalidOperationException("Authentication:Oidc and Authentication:Session must be configured objects.");
        if (!Uri.TryCreate(Oidc.Authority, UriKind.Absolute, out var authority)
            || authority.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(authority.UserInfo)
            || !string.IsNullOrEmpty(authority.Query) || !string.IsNullOrEmpty(authority.Fragment))
            throw new InvalidOperationException("Authentication:Oidc:Authority must be an absolute authority URL.");
        if (Oidc.AllowHttpMetadata && (!environment.IsDevelopment() || !authority.IsLoopback)
            || authority.Scheme != "https" && !Oidc.AllowHttpMetadata)
            throw new InvalidOperationException("HTTP OIDC metadata is permitted only for an explicitly enabled Development loopback authority.");
        if (string.IsNullOrWhiteSpace(Oidc.ClientId) || string.IsNullOrWhiteSpace(Oidc.ClientSecret))
            throw new InvalidOperationException("Authentication:Oidc requires ClientId and ClientSecret.");
        if (Oidc.Scopes is null || !Oidc.Scopes.Contains("openid", StringComparer.Ordinal)
            || Oidc.Scopes.Any(scope => string.IsNullOrWhiteSpace(scope) || scope.Any(char.IsWhiteSpace)))
            throw new InvalidOperationException("Authentication:Oidc:Scopes must include openid; each scope must be one nonblank value without whitespace.");
        if (Session.AbsoluteLifetime < TimeSpan.FromMinutes(1) || Session.AbsoluteLifetime > TimeSpan.FromDays(1)
            || Session.RevalidationInterval < TimeSpan.FromSeconds(1) || Session.RevalidationInterval > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("Authentication session lifetime must be 1 minute–24 hours; revalidation interval must be 1 second–5 minutes.");
        if (!environment.IsDevelopment() && (string.IsNullOrWhiteSpace(DataProtectionKeysPath)
            || !OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(DataProtectionCertificatePath)))
            throw new InvalidOperationException("Deployed OIDC requires Authentication:DataProtectionKeysPath and, outside Windows, DataProtectionCertificatePath.");
    }
}

public sealed class UiOidcOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public bool UsePkce { get; set; } = true;
    public bool AllowHttpMetadata { get; set; }
    public string[] Scopes { get; set; } = ["openid", "profile", "offline_access", "flowbit.api"];
}

public sealed class UiSessionOptions
{
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(8);
    public TimeSpan RevalidationInterval { get; set; } = TimeSpan.FromMinutes(1);
}
