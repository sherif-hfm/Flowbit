using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Xunit;

namespace Flowbit.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresApiCollection : ICollectionFixture<PostgresApiFixture>
{
    public const string Name = "postgres-api";
}

public sealed class PostgresApiFixture : IAsyncLifetime
{
    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("workflow_tests")
        .WithUsername("postgres")
        .WithPassword("workflow-tests")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public ApiDbCommandCounter CommandCounter { get; } = new();
    public ServiceInvocationProbe ServiceInvocations { get; } = new();
    public ErrorBoundaryLogProbe ErrorBoundaryLogs { get; } = new();
    public string ConnectionString => _postgres.GetConnectionString();
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>A generated certificate that wraps the test webhook key ring, shared by every host in the collection.</summary>
    public string WebhookCertificatePath { get; private set; } = string.Empty;

    public string WebhookCertificatePassword { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");
        SetEnvironment("ConnectionStrings__Flowbit", _postgres.GetConnectionString());
        SetEnvironment("Jwt__Issuer", ApiTestAuth.Issuer);
        SetEnvironment("Jwt__Audience", ApiTestAuth.Audience);
        SetEnvironment("Jwt__Key", ApiTestAuth.Key);
        SetEnvironment("WorkflowContext__Config__taskDistributionClientId", "config-distributor");
        SetEnvironment("WorkflowContext__Config__taskDistributionClientSecret", "config-distributor-secret");
        SetEnvironment("WorkflowContext__AllowedClaims__0", "department");
        WebhookCertificatePath = WebhookTestCertificates.Create(out var webhookCertificatePassword);
        WebhookCertificatePassword = webhookCertificatePassword;
        SetEnvironment("Webhooks__KeyProtection__CertificatePath", WebhookCertificatePath);
        SetEnvironment("Webhooks__KeyProtection__CertificatePassword", webhookCertificatePassword);

        // Program reads the process-latched actor identity setting during startup,
        // so the schema must exist before WebApplicationFactory starts the host.
        var migrationDataSourceBuilder = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString());
        migrationDataSourceBuilder.EnableDynamicJson();
        await using (var migrationDataSource = migrationDataSourceBuilder.Build())
        {
            var migrationOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(migrationDataSource, FlowbitDatabase.ConfigureProvider)
                .Options;
            await using var migrationDb = new AppDbContext(migrationOptions);
            await migrationDb.Database.MigrateAsync();
        }

        Factory = new WorkflowApiFactory(
            _postgres.GetConnectionString(),
            CommandCounter,
            ServiceInvocations,
            ErrorBoundaryLogs);
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await using var scope = Factory.Services.CreateAsyncScope();
        DataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        // Opt-in producer sweep: every test in the collection then commits through the
        // lifecycle capture triggers with a catch-all subscription active.
        if (string.Equals(
                Environment.GetEnvironmentVariable("FLOWBIT_TEST_LIFECYCLE_CAPTURE"),
                "all",
                StringComparison.OrdinalIgnoreCase))
        {
            await WebhookTestDatabase.EnableCatchAllCaptureAsync(DataSource);
        }
    }

    public async Task DisposeAsync()
    {
        await WriteLifecycleCaptureReportAsync();
        Client.Dispose();
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
        WebhookTestCertificates.Delete(WebhookCertificatePath);
        foreach (var pair in _originalEnvironment)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    /// <summary>
    /// After a producer sweep, appends how many events of each type the run recorded to the file named by
    /// FLOWBIT_TEST_LIFECYCLE_CAPTURE_REPORT. Tests that delete events make these lower bounds.
    /// </summary>
    private async Task WriteLifecycleCaptureReportAsync()
    {
        var path = Environment.GetEnvironmentVariable("FLOWBIT_TEST_LIFECYCLE_CAPTURE_REPORT");
        if (string.IsNullOrWhiteSpace(path) || DataSource is null)
        {
            return;
        }

        var counts = await WebhookTestDatabase.CaptureReportAsync(DataSource);
        await File.AppendAllLinesAsync(path, counts.Select(item =>
            string.Create(CultureInfo.InvariantCulture, $"{item.Type}\t{item.Count}")));
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(DataSource, FlowbitDatabase.ConfigureProvider)
            .Options;
        return new AppDbContext(options);
    }

    private void SetEnvironment(string key, string value)
    {
        _originalEnvironment.TryAdd(key, Environment.GetEnvironmentVariable(key));
        Environment.SetEnvironmentVariable(key, value);
    }

    private sealed class WorkflowApiFactory(
        string connectionString,
        ApiDbCommandCounter commandCounter,
        ServiceInvocationProbe serviceInvocations,
        ErrorBoundaryLogProbe errorBoundaryLogs) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            ApiTestAuthentication.Configure(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Flowbit"] = connectionString,
                    ["Jwt:Issuer"] = ApiTestAuth.Issuer,
                    ["Jwt:Audience"] = ApiTestAuth.Audience,
                    ["Jwt:Key"] = ApiTestAuth.Key,
                    ["Serilog:WriteTo:0:Name"] = "Console"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(commandCounter);
                services.AddDbContext<AppDbContext>((_, options) => options.AddInterceptors(commandCounter));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { });
                services.RemoveAll<IServiceTaskInvoker>();
                services.AddSingleton(serviceInvocations);
                services.AddScoped<IServiceTaskInvoker, DeterministicServiceTaskInvoker>();
                // Serilog writes through a static logger that other test hosts replace,
                // so a test reads these lines from the engine's logger itself.
                services.AddSingleton<ILogger<WorkflowEngineService>>(provider => new ProbingEngineLogger(
                    provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(WorkflowEngineService).FullName!),
                    errorBoundaryLogs));
            });
        }
    }

    private sealed class ProbingEngineLogger(ILogger inner, ErrorBoundaryLogProbe probe) : ILogger<WorkflowEngineService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
            probe.Record(logLevel, formatter(state, exception), state as IEnumerable<KeyValuePair<string, object?>>);
        }
    }

    private sealed class DeterministicServiceTaskInvoker(
        AppDbContext dbContext,
        ServiceInvocationProbe probe) : IServiceTaskInvoker
    {
        public async Task<ServiceTaskResult> InvokeAsync(
            ServiceTaskRequest request,
            CancellationToken cancellationToken)
        {
            probe.Record(
                dbContext.Database.CurrentTransaction is not null,
                dbContext.Database.GetDbConnection().State);
            await probe.WaitIfBlockedAsync(request.Url, cancellationToken);
            if (ControlledFailure(request.Url, probe) is { } controlled)
            {
                return controlled;
            }
            var body = request.Url switch
            {
                var url when url.EndsWith("/typed-output-success", StringComparison.Ordinal) =>
                    """{"result":{"decision":"approved","score":12},"tags":["safe","priority"],"businessDate":"2026-07-15","approved":true,"receivedAt":"2026-07-15T10:30:00+03:00","metadata":{"source":"service"},"ratings":[1.5,2.5]}""",
                var url when url.EndsWith("/typed-output-invalid", StringComparison.Ordinal) =>
                    """{"result":{"decision":"approved","score":"12"},"tags":["safe"],"businessDate":"2026-07-15","approved":true,"receivedAt":"2026-07-15T10:30:00Z","metadata":{},"ratings":[1]}""",
                var url when url.EndsWith("/typed-output-blocked", StringComparison.Ordinal) =>
                    """{"result":{"decision":"blocked","score":12},"tags":["safe"],"businessDate":"2026-07-15","approved":true,"receivedAt":"2026-07-15T10:30:00Z","metadata":{},"ratings":[1]}""",
                var url when url.EndsWith("/nullable-output", StringComparison.Ordinal) =>
                    """{"value":null}""",
                var url when url.EndsWith("/send-reminder", StringComparison.Ordinal) =>
                    """{"sent":true}""",
                var url when url.EndsWith("/parallel-stale", StringComparison.Ordinal) =>
                    """{"ok":true}""",
                _ => null
            };
            return body is null
                ? new ServiceTaskResult(true, 404, null, "No deterministic test response configured.")
                : new ServiceTaskResult(true, 200, body, null);
        }

        // /status/409 completes with that status, /lost-body/503 keeps the status
        // of a body read that failed, /no-response has none, and
        // /status-sequence/{unique}/503,409 uses the next status on each attempt.
        private static ServiceTaskResult? ControlledFailure(string url, ServiceInvocationProbe probe)
        {
            var path = new Uri(url).AbsolutePath.Trim('/').Split('/');
            return path switch
            {
                ["status", var status] => Completed(int.Parse(status, CultureInfo.InvariantCulture)),
                ["lost-body", var status] => new ServiceTaskResult(
                    false,
                    int.Parse(status, CultureInfo.InvariantCulture),
                    null,
                    "Transport request failed."),
                ["no-response"] => new ServiceTaskResult(false, 0, null, "Request timed out after 5s."),
                ["status-sequence", _, var statuses] => Completed(int.Parse(
                    statuses.Split(',')[Math.Min(probe.NextAttempt(url), statuses.Split(',').Length - 1)],
                    CultureInfo.InvariantCulture)),
                _ => null
            };
        }

        private static ServiceTaskResult Completed(int status) =>
            new(true, status, status is >= 200 and < 300 ? "{}" : null, null);
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "FlowbitTests";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user),
                new(ClaimTypes.NameIdentifier, user)
            };
            var roles = Request.Headers["X-Test-Roles"].FirstOrDefault()
                ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? [];
            var suppressAdmin = string.Equals(
                Request.Headers["X-Test-Suppress-Admin"].FirstOrDefault(),
                "true",
                StringComparison.OrdinalIgnoreCase);
            var effectiveRoles = suppressAdmin ? roles : roles.Append("admin");
            foreach (var role in effectiveRoles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
                claims.Add(new Claim("role", role));
            }
            const string testClaimPrefix = "X-Test-Claim-";
            foreach (var header in Request.Headers.Where(header =>
                         header.Key.StartsWith(testClaimPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                var claimType = header.Key[testClaimPrefix.Length..];
                var claimValue = header.Value.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(claimType)
                    && !string.IsNullOrWhiteSpace(claimValue))
                {
                    claims.Add(new Claim(claimType, claimValue));
                }
            }
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, SchemeName)));
        }
    }
}

public sealed record ErrorBoundaryLogEntry(
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> Fields);

/// <summary>The engine's error-boundary selection lines, with their structured fields.</summary>
public sealed class ErrorBoundaryLogProbe
{
    private readonly ConcurrentQueue<ErrorBoundaryLogEntry> entries = new();

    public IReadOnlyList<ErrorBoundaryLogEntry> ForInstance(long instanceId) =>
        entries.Where(entry => Equals(entry.Fields.GetValueOrDefault("InstanceId"), instanceId)).ToArray();

    internal void Record(LogLevel level, string message, IEnumerable<KeyValuePair<string, object?>>? fields)
    {
        if (fields is not null && message.Contains("error boundary", StringComparison.Ordinal))
        {
            entries.Enqueue(new ErrorBoundaryLogEntry(level, message, fields.ToDictionary()));
        }
    }
}

public sealed record ServiceInvocationObservation(
    bool HasTransaction,
    ConnectionState ConnectionState);

public sealed class ServiceInvocationProbe
{
    private readonly ConcurrentQueue<ServiceInvocationObservation> observations = new();
    private readonly ConcurrentDictionary<string, int> attempts = new(StringComparer.Ordinal);
    private readonly object blockLock = new();
    private ServiceInvocationBlock? pendingBlock;

    public IReadOnlyList<ServiceInvocationObservation> Snapshot() =>
        observations.ToArray();

    /// <summary>Zero-based count of earlier invocations of this URL.</summary>
    public int NextAttempt(string url) =>
        attempts.AddOrUpdate(url, 0, (_, attempt) => attempt + 1);

    public int AttemptCount(string url) =>
        attempts.TryGetValue(url, out var attempt) ? attempt + 1 : 0;

    public void Record(bool hasTransaction, ConnectionState connectionState) =>
        observations.Enqueue(new ServiceInvocationObservation(
            hasTransaction,
            connectionState));

    public ServiceInvocationBlock BlockNext(string urlSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlSuffix);
        lock (blockLock)
        {
            if (pendingBlock is not null)
            {
                throw new InvalidOperationException(
                    "A deterministic service invocation is already blocked.");
            }
            pendingBlock = new ServiceInvocationBlock(urlSuffix);
            return pendingBlock;
        }
    }

    internal async Task WaitIfBlockedAsync(
        string url,
        CancellationToken cancellationToken)
    {
        ServiceInvocationBlock? block;
        lock (blockLock)
        {
            block = pendingBlock is not null
                    && url.EndsWith(
                        pendingBlock.UrlSuffix,
                        StringComparison.Ordinal)
                ? pendingBlock
                : null;
        }
        if (block is null)
        {
            return;
        }

        block.SignalEntered();
        try
        {
            await block.WaitForReleaseAsync(cancellationToken);
        }
        finally
        {
            lock (blockLock)
            {
                if (ReferenceEquals(pendingBlock, block))
                {
                    pendingBlock = null;
                }
            }
        }
    }

    public void Reset()
    {
        lock (blockLock)
        {
            pendingBlock?.Release();
            pendingBlock = null;
        }
        while (observations.TryDequeue(out _))
        {
        }
    }
}

public sealed class ServiceInvocationBlock
{
    private readonly TaskCompletionSource entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal ServiceInvocationBlock(string urlSuffix)
    {
        UrlSuffix = urlSuffix;
    }

    internal string UrlSuffix { get; }

    public Task WaitUntilEnteredAsync(CancellationToken cancellationToken) =>
        entered.Task.WaitAsync(cancellationToken);

    public void Release() => released.TrySetResult();

    internal void SignalEntered() => entered.TrySetResult();

    internal Task WaitForReleaseAsync(CancellationToken cancellationToken) =>
        released.Task.WaitAsync(cancellationToken);
}

public sealed class ApiDbCommandCounter : DbCommandInterceptor
{
    private int _readerCommands;
    private int _captureReaderCommandTexts;
    private readonly ConcurrentQueue<string> _readerCommandTexts = new();

    public int ReaderCommands => Volatile.Read(ref _readerCommands);
    public IReadOnlyList<string> ReaderCommandTexts => _readerCommandTexts.ToArray();

    public void Reset(bool captureReaderCommandTexts = false)
    {
        Interlocked.Exchange(ref _readerCommands, 0);
        _readerCommandTexts.Clear();
        Volatile.Write(
            ref _captureReaderCommandTexts,
            captureReaderCommandTexts ? 1 : 0);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _readerCommands);
        if (Volatile.Read(ref _captureReaderCommandTexts) != 0)
        {
            _readerCommandTexts.Enqueue(command.CommandText);
        }
        return ValueTask.FromResult(result);
    }
}
