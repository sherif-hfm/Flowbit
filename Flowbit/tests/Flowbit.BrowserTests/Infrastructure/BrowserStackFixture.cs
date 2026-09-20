using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Testcontainers.PostgreSql;
using Xunit;

namespace Flowbit.BrowserTests.Infrastructure;

/// <summary>
/// Owns the disposable browser smoke stack: one PostgreSQL Testcontainer, the
/// published API and UI processes on ephemeral loopback ports, the test-only
/// editor host, and Playwright's Chromium. One fixture instance backs the whole
/// serialized collection; individual scenarios get isolated browser contexts.
/// </summary>
public sealed class BrowserStackFixture : IAsyncLifetime
{
    /// <summary>Set to 1 to launch Chromium headed for local diagnosis.</summary>
    public const string HeadedEnv = "FLOWBIT_BROWSER_HEADED";

    private static readonly TimeSpan DatabaseStartupTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ApplicationReadyTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollingObservationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan TeardownPerProcessTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] AdminRoles = ["admin"];

    private PostgreSqlContainer? postgres;
    private HostedProcess? api;
    private HostedProcess? ui;
    private EditorStaticHost? editorHost;
    private IPlaywright? playwright;
    private IBrowser? browser;
    private bool initialized;
    private string? invalidReason;

    internal void Invalidate(string reason) => invalidReason = reason;
    private readonly List<string> scenarioNames = [];
    private readonly object manifestGate = new();

    public string RepositoryRoot { get; private set; } = string.Empty;
    public string RunDirectory { get; private set; } = string.Empty;
    public string ApiBaseAddress { get; private set; } = string.Empty;
    public string UiBaseAddress { get; private set; } = string.Empty;
    public string EditorBaseAddress { get; private set; } = string.Empty;
    public string JwtIssuer { get; } = "browser-smoke-issuer";
    public string JwtAudience { get; } = "browser-smoke-api";
    public string JwtKey { get; private set; } = new('k', 64);
    public string BrowserVersion { get; private set; } = string.Empty;
    public string SetupActor { get; private set; } = "setup";
    public string[] SetupRoles { get; } = ["admin"];
    public bool Headless { get; private set; } = true;

    public WorkflowFixtureClient SetupClient { get; private set; } = null!;

    /// <summary>Current captured API process output (for host-log assertions).</summary>
    public IReadOnlyList<string> ApiOutputLines => api?.OutputLines ?? [];

    /// <summary>Current captured UI process output (for host-log assertions).</summary>
    public IReadOnlyList<string> UiOutputLines => ui?.OutputLines ?? [];

    /// <summary>Seconds budget for one full scenario (read by tests).</summary>
    public TimeSpan ScenarioBudget => ScenarioTimeout;

    /// <summary>Default Playwright timeout for ordinary assertions.</summary>
    public TimeSpan AssertionBudget => AssertionTimeout;

    /// <summary>Budget for observing a five-second polling cycle.</summary>
    public TimeSpan PollingObservationBudget => PollingObservationTimeout;

    public async Task InitializeAsync()
    {
        var startTimestamp = Stopwatch.StartNew();
        try
        {
            RepositoryRoot = DiscoverRepositoryRoot();
            Headless = !string.Equals(
                Environment.GetEnvironmentVariable(HeadedEnv),
                "1",
                StringComparison.OrdinalIgnoreCase);
            var runName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
            RunDirectory = Path.Combine(RepositoryRoot, "artifacts", "browser", "runs", runName);
            Directory.CreateDirectory(RunDirectory);
            JwtKey = $"bk-{Guid.NewGuid():N}-{Guid.NewGuid():N}";
            if (JwtKey.Length < 64)
            {
                JwtKey = JwtKey + new string('k', 64 - JwtKey.Length);
            }

            await WriteSetupLogAsync(
                $"Browser smoke stack initializing. Repository root: {RepositoryRoot}. Headless: {Headless}.");

            await StartDatabaseAsync();
            await StartApiAsync();
            await StartUiAsync();
            await StartEditorHostAsync();
            await StartBrowserAsync();

            // Verify the real interactive token action and use its generated
            // token for authenticated HTTP fixture setup (admin/setup identity).
            var setupToken = await EstablishSetupIdentityAsync();
            SetupClient = new WorkflowFixtureClient(
                ApiBaseAddress, setupToken, SetupActor, AdminRoles, "setup-client", RunDirectory);
            var workflows = await SetupClient.ListWorkflowsAsync();
            await WriteSetupLogAsync(
                $"Setup identity '{SetupActor}' verified: {workflows.Count} workflow(s) readable through /api/workflows/. " +
                $"Stack ready in {startTimestamp.Elapsed.TotalSeconds:0.0}s.");

            initialized = true;
            await WriteManifestAsync();
        }
        catch (Exception failure)
        {
            await WriteSetupLogAsync($"SETUP FAILED: {failure}");
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Creates an authenticated HTTP client bound to an explicit token. Changing
    /// the UI identity never changes an already configured fixture client.
    /// </summary>
    public WorkflowFixtureClient CreateClient(
        string actor, string[] roles, string token, string purpose) =>
        new(ApiBaseAddress, token, actor, roles, purpose, RunDirectory);

    /// <summary>
    /// Mints a setup JWT through the real /token screen in a throwaway browser
    /// context and returns the raw token for HTTP fixture clients.
    /// </summary>
    public async Task<string> EstablishSetupIdentityAsync()
    {
        await using var scenario = await CreateScenario("fixture-setup-identity");
        var identity = new IdentityScreen(scenario.Page, UiBaseAddress);
        try
        {
            return await identity.GenerateAndApplyIdentityAsync(SetupActor, AdminRoles);
        }
        catch (Exception failure)
        {
            await scenario.CaptureFailureArtifactsAsync("establish-setup-identity", failure);
            throw;
        }
    }

    /// <summary>
    /// Creates a fresh browser context and page with failure artifacts and a
    /// declared viewport. Tracing starts immediately; successful scenarios
    /// discard traces while failures keep them under the scenario directory.
    /// </summary>
    public async Task<BrowserScenario> CreateScenario(
        string name,
        int viewportWidth = 1440,
        int viewportHeight = 900,
        bool blockNativeFilePicker = false,
        TimeSpan? scenarioTimeout = null)
    {
        if (invalidReason is not null) throw new InvalidOperationException(invalidReason);
        if (browser is null)
        {
            throw new InvalidOperationException(
                "The browser stack is not ready; the fixture initialization failed.");
        }

        var contextOptions = new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = viewportWidth, Height = viewportHeight },
            AcceptDownloads = true,
            BaseURL = UiBaseAddress,
        };
        lock (manifestGate)
        {
            scenarioNames.Add(name);
        }
        _ = WriteManifestAsync();
        var context = browser.NewContextAsync(contextOptions).GetAwaiter().GetResult();
        if (blockNativeFilePicker)
        {
            // Test-only capability override: force the editor's download
            // fallback by making the native picker unavailable before page load.
            // This is not a replacement implementation of save().
            await context.AddInitScriptAsync(
                """
                try { Object.defineProperty(window, 'showSaveFilePicker', { value: undefined, configurable: true }); } catch {}
                try { delete window.showSaveFilePicker; } catch {}
                """);
        }
        var scenario = new BrowserScenario(
            this,
            context,
            name,
            Path.Combine(RunDirectory, SanitizeFileName(name)),
            NavigationTimeout,
            AssertionTimeout,
            scenarioTimeout ?? ScenarioTimeout,
            TeardownPerProcessTimeout);
        await scenario.PrepareAsync();
        return scenario;
    }

    public async Task DisposeAsync()
    {
        var failures = new List<Exception>();

        async Task RunStepAsync(string step, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException($"Fixture teardown step '{step}' failed.", ex));
            }
        }

        if (browser is not null)
        {
            await RunStepAsync("close browser", async () =>
            {
                await browser.CloseAsync();
                await browser.DisposeAsync();
            });
            browser = null;
        }
        if (playwright is not null)
        {
            await RunStepAsync("dispose playwright", () =>
            {
                playwright.Dispose();
                return Task.CompletedTask;
            });
            playwright = null;
        }
        if (editorHost is not null)
        {
            await RunStepAsync("stop editor host", () => editorHost.DisposeAsync().AsTask());
            editorHost = null;
        }
        if (ui is not null)
        {
            await RunStepAsync("stop ui", () => ui.DisposeAsync().AsTask());
            ui = null;
        }
        if (api is not null)
        {
            await RunStepAsync("stop api", () => api.DisposeAsync().AsTask());
            api = null;
        }
        if (SetupClient is not null)
        {
            await RunStepAsync("dispose setup http client", () => SetupClient.DisposeAsync().AsTask());
            SetupClient = null!;
        }
        if (postgres is not null)
        {
            await RunStepAsync("dispose database container", () => postgres.DisposeAsync().AsTask());
            postgres = null;
        }

        if (failures.Count > 0)
        {
            await WriteSetupLogAsync("Teardown failures: " + string.Join("; ", failures.Select(f => f.Message)));
        }
        else if (initialized)
        {
            await WriteSetupLogAsync("Browser smoke stack disposed cleanly.");
        }

        await WriteManifestAsync();
    }

    private async Task StartDatabaseAsync()
    {
        // The PostgreSql module applies its own bounded readiness wait; the
        // container (and its data) is disposable and never reused across runs.
        var builder = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("flowbit_browser")
            .WithUsername("flowbit")
            .WithPassword($"pw-{Guid.NewGuid():N}")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted(
                    ["pg_isready", "--dbname=flowbit_browser", "--username=flowbit"],
                    wait => wait.WithTimeout(DatabaseStartupTimeout)));
        postgres = builder.Build();
        using var databaseStart = new CancellationTokenSource(DatabaseStartupTimeout);
        await postgres.StartAsync(databaseStart.Token);
        await WriteSetupLogAsync(
            $"PostgreSQL container started ({postgres.Id[..12]}); disposable credentials and database for this run.");
    }

    private async Task StartApiAsync()
    {
        api = HostedProcess.Start(
            "api",
            Path.Combine(RepositoryRoot, "artifacts", "browser", "hosts", "api", "Flowbit.Api.dll"),
            Path.Combine(RepositoryRoot, "artifacts", "browser", "hosts", "api"),
            RunDirectory,
            new Dictionary<string, string?>
            {
                ["ConnectionStrings__Flowbit"] = postgres!.GetConnectionString(),
                ["Jwt__Issuer"] = JwtIssuer,
                ["Jwt__Audience"] = JwtAudience,
                ["Jwt__Key"] = JwtKey,
                ["WorkflowDurableProcessing__PublicationEnabled"] = "false",
                ["WorkflowContext__AllowedClaims__0"] = "depId",
                ["Serilog__MinimumLevel__Override__Microsoft.Hosting.Lifetime"] = "Information",
            },
            CreateProbeClient(),
            readyTimeout: ApplicationReadyTimeout);
        ApiBaseAddress = await api.WaitForReadyAsync("/openapi/v1.json", CancellationToken.None);
        await WriteSetupLogAsync($"API ready at {ApiBaseAddress}.");
    }

    private async Task StartUiAsync()
    {
        ui = HostedProcess.Start(
            "ui",
            Path.Combine(RepositoryRoot, "artifacts", "browser", "hosts", "ui", "Flowbit.Ui.dll"),
            Path.Combine(RepositoryRoot, "artifacts", "browser", "hosts", "ui"),
            RunDirectory,
            new Dictionary<string, string?>
            {
                ["Jwt__Issuer"] = JwtIssuer,
                ["Jwt__Audience"] = JwtAudience,
                ["Jwt__Key"] = JwtKey,
                ["WorkflowApi__BaseUrl"] = api!.BaseAddress,
                ["Serilog__MinimumLevel__Override__Microsoft.Hosting.Lifetime"] = "Information",
            },
            CreateProbeClient(),
            readyTimeout: ApplicationReadyTimeout);
        UiBaseAddress = await ui.WaitForReadyAsync("/token", CancellationToken.None);
        await ui.WaitForReadyAsync("/_framework/blazor.web.js", CancellationToken.None);
        await WriteSetupLogAsync($"UI ready at {UiBaseAddress} (prerender and blazor.web.js probed; identity is applied interactively in scenarios).");
    }

    private async Task StartEditorHostAsync()
    {
        editorHost = new EditorStaticHost();
        await editorHost.StartAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "flowbit-editor.html"),
            CancellationToken.None);
        EditorBaseAddress = editorHost.BaseAddress;
        await WriteSetupLogAsync($"Editor host ready at {EditorBaseAddress}.");
    }

    private async Task StartBrowserAsync()
    {
        playwright = await Playwright.CreateAsync();
        var launch = new BrowserTypeLaunchOptions { Headless = Headless };
        try
        {
            browser = await playwright.Chromium.LaunchAsync(launch);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Chromium could not be launched. Install it with:\n" +
                "  pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium" +
                " (add --with-deps on Linux)\n" +
                $"Original failure: {ex.Message}", ex);
        }
        BrowserVersion = browser.Version;
        await WriteSetupLogAsync(
            $"Chromium {BrowserVersion} launched ({browser.BrowserType.ExecutablePath}). Headless: {Headless}.");
    }

    private static HttpClient CreateProbeClient() => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    private async Task WriteManifestAsync()
    {
        if (RunDirectory.Length == 0)
        {
            return;
        }

        string[] scenarios;
        lock (manifestGate)
        {
            scenarios = [.. scenarioNames];
        }

        var payload = new
        {
            browser = string.IsNullOrEmpty(BrowserVersion) ? null : $"Chromium {BrowserVersion}",
            headless = Headless,
            apiBaseAddress = string.IsNullOrEmpty(ApiBaseAddress) ? null : ApiBaseAddress,
            uiBaseAddress = string.IsNullOrEmpty(UiBaseAddress) ? null : UiBaseAddress,
            editorBaseAddress = string.IsNullOrEmpty(EditorBaseAddress) ? null : EditorBaseAddress,
            initialized,
            scenarios,
        };
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(RunDirectory, "manifest.json"),
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal async Task WriteSetupLogAsync(string message)
    {
        if (RunDirectory.Length == 0)
        {
            return;
        }
        try
        {
            await File.AppendAllTextAsync(
                Path.Combine(RunDirectory, "setup.log"),
                $"{DateTime.UtcNow:O} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never break teardown.
        }
    }

    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string DiscoverRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // The root carries the editor, this document, and the Docker stack.
            if (File.Exists(Path.Combine(directory.FullName, "flowbit-editor.html")) &&
                File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) &&
                File.Exists(Path.Combine(directory.FullName, "compose.yaml")))
            {
                return directory.FullName;
            }
            var parent = directory.Parent;
            if (parent is null || parent.FullName == directory.FullName)
            {
                break;
            }
            directory = parent;
        }
        throw new InvalidOperationException(
            "Could not locate the repository root (a directory containing flowbit-editor.html, AGENTS.md, and compose.yaml) " +
            $"above the test output directory {AppContext.BaseDirectory}.");
    }
}
