using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Playwright;
using Testcontainers.PostgreSql;

namespace Flowbit.BrowserTests.Infrastructure;

/// <summary>Independent OIDC stack; the ordinary workflow smoke fixture stays Worker-free and development-token based.</summary>
public sealed class OidcBrowserStack : IAsyncDisposable
{
    private PostgreSqlContainer? postgres;
    private HostedProcess? api;
    private HostedProcess? ui;
    private IPlaywright? playwright;
    private IBrowser? browser;
    private X509Certificate2? certificate;
    public OidcProviderTestHost Provider { get; } = new();
    public AiProviderTestHost AiProvider { get; } = new();
    public OidcUiProxy? Proxy { get; private set; }
    public string RunDirectory { get; private set; } = "";
    public string ApiOrigin { get; private set; } = "";
    public string UiOrigin { get; private set; } = "";
    public string UiBackendOrigin { get; private set; } = "";
    public string BrowserVersion => browser?.Version ?? "";

    public async Task StartAsync(bool usePkce, bool enableAi = false, bool useTrustedProxy = false)
    {
        var root = DiscoverRoot();
        RunDirectory = Path.Combine(root, "artifacts", "browser", "oidc", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
            + "-" + (usePkce ? "pkce" : "code") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(RunDirectory);
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Provider.RequirePkce = usePkce;
        await Provider.StartAsync(startup.Token);
        if (enableAi) await AiProvider.StartAsync();
        postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("flowbit_oidc")
            .WithUsername("flowbit").WithPassword(Guid.NewGuid().ToString("N")).Build();
        await postgres.StartAsync(startup.Token);

        var apiDirectory = Path.Combine(root, "artifacts", "browser", "hosts", "api");
        var apiEnvironment = new Dictionary<string, string?>
            {
                ["Authentication__Mode"] = "OpenIdConnect",
                ["Authentication__Oidc__Authority"] = Provider.Authority,
                ["Authentication__Oidc__Audience"] = OidcProviderTestHost.Audience,
                ["Authentication__Oidc__NameClaimType"] = "flowbit_user",
                ["Authentication__Oidc__RoleClaimType"] = "roles",
                ["Authentication__Oidc__ScopeClaimType"] = "scope",
                ["Authentication__Oidc__RequiredScope"] = "flowbit.api",
                ["Authentication__Oidc__AllowHttpMetadata"] = "true",
                ["ConnectionStrings__Flowbit"] = postgres.GetConnectionString(),
                ["WorkflowDurableProcessing__PublicationEnabled"] = "false",
                ["WorkflowAi__Enabled"] = enableAi.ToString(),
                ["Serilog__MinimumLevel__Override__Microsoft.Hosting.Lifetime"] = "Information"
            };
        // A test that enables AI configures its model through the AI models API after signing in.
        api = HostedProcess.Start("oidc-api", Path.Combine(apiDirectory, "Flowbit.Api.dll"), apiDirectory, RunDirectory,
            apiEnvironment, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }));
        ApiOrigin = await api.WaitForReadyAsync("/openapi/v1.json", startup.Token);

        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=Flowbit OIDC browser fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        certificateRequest.CertificateExtensions.Add(san.Build());
        using var generatedCertificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Give the in-process TLS proxy its own persisted Windows key handle;
        // the RSA used to create the certificate leaves scope after startup.
        certificate = X509CertificateLoader.LoadPkcs12(generatedCertificate.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);
        var password = Guid.NewGuid().ToString("N");
        var certificatePath = Path.Combine(RunDirectory, "fixture-ui.pfx");
        await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pfx, password), startup.Token);
        var uiDirectory = Path.Combine(root, "artifacts", "browser", "hosts", "ui");
        ui = HostedProcess.Start("oidc-ui", Path.Combine(uiDirectory, "Flowbit.Ui.dll"), uiDirectory, RunDirectory,
            new Dictionary<string, string?>
            {
                ["Authentication__Mode"] = "OpenIdConnect",
                ["Authentication__Oidc__Authority"] = Provider.Authority,
                ["Authentication__Oidc__ClientId"] = OidcProviderTestHost.ClientId,
                ["Authentication__Oidc__ClientSecret"] = Provider.ClientSecret,
                ["Authentication__Oidc__UsePkce"] = usePkce.ToString(),
                ["Authentication__Oidc__AllowHttpMetadata"] = "true",
                ["Authentication__Session__RevalidationInterval"] = "00:00:01",
                ["Authentication__DataProtectionKeysPath"] = Path.Combine(RunDirectory, "keys"),
                ["WorkflowApi__BaseUrl"] = ApiOrigin,
                ["AllowedHosts"] = "127.0.0.1",
                ["ReverseProxy__KnownProxies__0"] = "127.0.0.1",
                ["Kestrel__Certificates__Default__Path"] = certificatePath,
                ["Kestrel__Certificates__Default__Password"] = password,
                ["Serilog__MinimumLevel__Override__Microsoft.Hosting.Lifetime"] = "Information"
            }, new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, serverCertificate, _, _) =>
                    serverCertificate?.Thumbprint == certificate.Thumbprint
            }), useHttps: !useTrustedProxy);
        UiBackendOrigin = await ui.WaitForReadyAsync("/health/live", startup.Token);
        if (useTrustedProxy)
        {
            Proxy = new OidcUiProxy();
            await Proxy.StartAsync(UiBackendOrigin, certificate, Path.Combine(RunDirectory, "proxy-outcomes.json"));
        }
        UiOrigin = Proxy?.Origin ?? UiBackendOrigin;
        Provider.UiOrigin = UiOrigin;
        if (!useTrustedProxy) await ui.WaitForReadyAsync("/_framework/blazor.web.js", startup.Token);
        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("FLOWBIT_BROWSER_HEADED") != "1"
        });
        await File.WriteAllTextAsync(Path.Combine(RunDirectory, "manifest.json"), JsonSerializer.Serialize(new
        {
            ui = UiOrigin, uiBackend = UiBackendOrigin, useTrustedProxy, api = ApiOrigin, authority = Provider.Authority,
            browser = browser.Version, usePkce, enableAi, apiMode = "OpenIdConnect", uiMode = "OpenIdConnect",
            database = "disposable PostgreSQL", session = "server store, browser cookie only"
        }, new JsonSerializerOptions { WriteIndented = true }), startup.Token);
    }

    public async Task<IBrowserContext> CreateContextAsync(int width = 1440, int height = 900)
    {
        var context = await browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = UiOrigin, IgnoreHTTPSErrors = true, AcceptDownloads = true,
            ViewportSize = new ViewportSize { Width = width, Height = height }
        });
        context.SetDefaultTimeout(15_000);
        context.SetDefaultNavigationTimeout(30_000);
        // Exercise the documented JSON download fallback, as the ordinary
        // smoke suite does. This changes browser capability, never app state.
        await context.AddInitScriptAsync("Object.defineProperty(window, 'showSaveFilePicker', { value: undefined, configurable: true });");
        return context;
    }

    private static string DiscoverRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "compose.yaml"))
                && Directory.Exists(Path.Combine(directory.FullName, "Flowbit"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate the Flowbit repository root.");
    }

    public async ValueTask DisposeAsync()
    {
        try { if (browser is not null) await browser.DisposeAsync(); }
        finally
        {
            playwright?.Dispose();
            try { if (Proxy is not null) await Proxy.DisposeAsync(); }
            finally
            {
                try { if (ui is not null) await ui.DisposeAsync(); }
                finally
                {
                    try { if (api is not null) await api.DisposeAsync(); }
                    finally
                    {
                        try { if (postgres is not null) await postgres.DisposeAsync(); }
                        finally
                        {
                            try { await AiProvider.DisposeAsync(); }
                            finally { await Provider.DisposeAsync(); certificate?.Dispose(); }
                        }
                    }
                }
            }
        }
    }
}
