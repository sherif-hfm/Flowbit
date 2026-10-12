using System.Collections.Concurrent;
using System.Net;
using Flowbit.BrowserTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExampleAcceptanceCollection : ICollectionFixture<ExampleAcceptanceFixture>
{
    public const string Name = "canonical-example-worker-stack";
}

/// <summary>The service is started first so API and Worker share the same real endpoint.</summary>
public sealed class ExampleAcceptanceFixture : IAsyncLifetime
{
    public BrowserStackFixture Stack { get; private set; } = null!;
    public ExampleRetryService Service { get; } = new();
    public const string MessageClientId = "example-message-client";
    public const string MessageClientSecret = "example-message-secret";

    public async Task InitializeAsync()
    {
        await Service.StartAsync();
        Stack = BrowserStackFixture.CreateForAcceptance(new()
        {
            ApiHostDirectory = Environment.GetEnvironmentVariable("FLOWBIT_ACCEPTANCE_API_HOST"),
            UiHostDirectory = Environment.GetEnvironmentVariable("FLOWBIT_ACCEPTANCE_UI_HOST"),
            WorkerHostDirectory = Environment.GetEnvironmentVariable("FLOWBIT_ACCEPTANCE_WORKER_HOST"),
            ArtifactRoot = Environment.GetEnvironmentVariable("FLOWBIT_ACCEPTANCE_ARTIFACT_ROOT"),
            StartEditorHost = true,
            WorkflowContextConfig = new Dictionary<string, string>
            {
                ["exampleApiBaseUrl"] = Service.BaseAddress,
                ["exampleApiToken"] = ExampleRetryService.Credential,
                ["exampleMessageClientSecret"] = MessageClientSecret,
            },
        });
        try { await Stack.InitializeAsync(); }
        catch { await Service.DisposeAsync(); throw; }
    }

    public async Task DisposeAsync()
    {
        try { if (Stack is not null) await Stack.DisposeAsync(); }
        finally { await Service.DisposeAsync(); }
    }
}

public sealed record ExampleRetryRequest(long JobId, int Attempt, string Scenario, int StatusCode);

/// <summary>Controlled external service; never fabricates an application API response.</summary>
public sealed class ExampleRetryService : IAsyncDisposable
{
    public const string Credential = "example-retry-test-credential";
    private readonly ConcurrentDictionary<long, int> counts = new();
    private readonly ConcurrentDictionary<long, byte> recovered = new();
    private readonly ConcurrentQueue<ExampleRetryRequest> requests = new();
    private WebApplication? app;
    public string BaseAddress { get; private set; } = string.Empty;
    public IReadOnlyList<ExampleRetryRequest> Requests => requests.ToArray();
    public void Recover(long jobId) => recovered.TryAdd(jobId, 0);

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        app = builder.Build();
        app.MapPost("/retry", async context =>
        {
            if (context.Request.Headers["X-Example-Credential"] != Credential)
            {
                context.Response.StatusCode = 401;
                return;
            }
            if (!long.TryParse(context.Request.Headers["X-Flowbit-Job-Id"], out var jobId)
                || !int.TryParse(context.Request.Headers["X-Flowbit-Job-Attempt"], out var attempt))
            {
                context.Response.StatusCode = 400;
                return;
            }
            using var payload = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            var mode = payload.RootElement.GetProperty("scenario").GetString() ?? string.Empty;
            var count = counts.AddOrUpdate(jobId, 1, (_, previous) => previous + 1);
            var success = mode == "fail-once" ? count > 1 : mode == "recover" && recovered.ContainsKey(jobId);
            context.Response.StatusCode = success ? 200 : 503;
            requests.Enqueue(new(jobId, attempt, mode, context.Response.StatusCode));
            await context.Response.WriteAsJsonAsync(new { ok = success });
        });
        await app.StartAsync();
        BaseAddress = app.Urls.Single();
    }

    public async ValueTask DisposeAsync()
    {
        if (app is null) return;
        await app.StopAsync();
        await app.DisposeAsync();
        app = null;
    }
}
