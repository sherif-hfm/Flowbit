using System.Collections.Concurrent;
using System.Globalization;
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
public class ExampleAcceptanceFixture : IAsyncLifetime
{
    public BrowserStackFixture Stack { get; private set; } = null!;
    public ExampleRetryService Service { get; } = new();
    public const string MessageClientId = "example-message-client";
    public const string MessageClientSecret = "example-message-secret";

    /// <summary>Host configuration this collection adds to API and Worker; the example stack adds none.</summary>
    protected virtual IReadOnlyDictionary<string, string> HostSettings { get; } = new Dictionary<string, string>();

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
            HostSettings = HostSettings,
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

/// <summary>One call to a showcase route: <c>credit</c> records the customer, <c>reservation</c> the SKU.</summary>
public sealed record ExampleShowcaseCall(string Route, string OrderId, string Subject, int StatusCode);

/// <summary>Controlled external service; never fabricates an application API response.</summary>
public sealed class ExampleRetryService : IAsyncDisposable
{
    public const string Credential = "example-retry-test-credential";
    private readonly ConcurrentDictionary<long, int> counts = new();
    private readonly ConcurrentDictionary<long, byte> recovered = new();
    private readonly ConcurrentQueue<ExampleRetryRequest> requests = new();
    private readonly ConcurrentDictionary<string, int> stockAttempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExampleItemGroup> itemGroups = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> recoveredCustomers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> restockedOrders = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ExampleShowcaseCall> showcaseCalls = new();
    private WebApplication? app;

    /// <summary>The per-item routes of one scenario: <c>POST /items/{group}/{index}</c>.</summary>
    public ExampleItemGroup Items(string group) => itemGroups.GetOrAdd(group, name => new ExampleItemGroup(name));
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
        app.MapPost("/orders/{orderId}/submit", context => RespondAsync(context, (string)context.Request.RouteValues["orderId"]!, 0));
        app.MapPost("/stock/{scenario}", context =>
        {
            var scenario = (string)context.Request.RouteValues["scenario"]!;
            var attempt = stockAttempts.AddOrUpdate(scenario, 0, (_, previous) => previous + 1);
            return RespondAsync(context, scenario, attempt);
        });
        // Showcase 02: the customer id picks the credit answer, as ShowcaseCreditAnswer describes.
        app.MapPost("/customers/{customerId}/credit-checks", async context =>
        {
            if (context.Request.Headers.Authorization != $"Bearer {Credential}")
            {
                context.Response.StatusCode = 401;
                return;
            }
            var customerId = (string)context.Request.RouteValues["customerId"]!;
            using var payload = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            var orderId = payload.RootElement.GetProperty("orderId").GetString() ?? string.Empty;
            var (status, decision) = ShowcaseCreditAnswer(customerId);
            showcaseCalls.Enqueue(new("credit", orderId, customerId, status));
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(decision is null ? new { error = "credit_refused" } : (object)new { decision });
        });
        // Showcase 02: a SKU starting OUT- is out of stock until the order is restocked.
        app.MapPost("/orders/{orderId}/reservations", async context =>
        {
            if (context.Request.Headers.Authorization != $"Bearer {Credential}")
            {
                context.Response.StatusCode = 401;
                return;
            }
            var orderId = (string)context.Request.RouteValues["orderId"]!;
            using var payload = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            var sku = payload.RootElement.GetProperty("line").GetProperty("sku").GetString() ?? string.Empty;
            var available = !sku.StartsWith("OUT-", StringComparison.Ordinal) || restockedOrders.ContainsKey(orderId);
            var status = available ? 201 : 409;
            showcaseCalls.Enqueue(new("reservation", orderId, sku, status));
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(available
                ? new { reservationId = $"RES-{orderId}-{sku}" }
                : (object)new { error = "out_of_stock", sku });
        });
        app.MapPost("/items/{group}/{index:int}", context =>
        {
            if (context.Request.Headers.Authorization != $"Bearer {Credential}")
            {
                context.Response.StatusCode = 401;
                return Task.CompletedTask;
            }
            var group = (string)context.Request.RouteValues["group"]!;
            var index = int.Parse((string)context.Request.RouteValues["index"]!, CultureInfo.InvariantCulture);
            return Items(group).RespondAsync(context, index);
        });
        await app.StartAsync();
        BaseAddress = app.Urls.Single();
    }

    public int StockCalls(string scenario) =>
        stockAttempts.TryGetValue(scenario, out var attempt) ? attempt + 1 : 0;

    /// <summary>Calls to the showcase credit and reservation routes, in arrival order.</summary>
    public IReadOnlyList<ExampleShowcaseCall> ShowcaseCalls => showcaseCalls.ToArray();

    /// <summary>Makes a <c>down-</c> customer's credit checks answer as approved from now on.</summary>
    public void RecoverCreditService(string customerId) => recoveredCustomers.TryAdd(customerId, 0);

    /// <summary>Makes every <c>OUT-</c> SKU of this order available from now on.</summary>
    public void Restock(string orderId) => restockedOrders.TryAdd(orderId, 0);

    // good- approves, review- asks for a manual review, odd- answers a decision the workflow
    // does not accept, refused- answers 403, and down- answers 503 until RecoverCreditService
    // releases it.
    private (int Status, string? Decision) ShowcaseCreditAnswer(string customerId) => customerId.Split('-')[0] switch
    {
        "good" => (200, "approved"),
        "review" => (200, "review"),
        "odd" => (200, "declined"),
        "refused" => (403, null),
        "down" when recoveredCustomers.ContainsKey(customerId) => (200, "approved"),
        "down" => (503, null),
        _ => (404, null),
    };

    // The scenario's first segment picks the behavior and the rest keeps it unique:
    // status-409-x answers 409, sequence-503-409-x answers each status in turn,
    // lost-503-x drops the connection mid-body, hang-x never answers in time,
    // late-409-x answers after four seconds, and stall-409-x leaves its first call
    // open until the caller goes away, then answers the next one.
    private static async Task RespondAsync(HttpContext context, string scenario, int attempt)
    {
        if (context.Request.Headers.Authorization != $"Bearer {Credential}")
        {
            context.Response.StatusCode = 401;
            return;
        }
        var parts = scenario.Split('-');
        switch (parts[0])
        {
            case "status":
                context.Response.StatusCode = int.Parse(parts[1], CultureInfo.InvariantCulture);
                break;
            case "sequence":
                var statuses = parts.Skip(1).Where(part => int.TryParse(part, out _)).ToArray();
                context.Response.StatusCode = int.Parse(statuses[Math.Min(attempt, statuses.Length - 1)], CultureInfo.InvariantCulture);
                break;
            case "lost":
                context.Response.StatusCode = int.Parse(parts[1], CultureInfo.InvariantCulture);
                context.Response.ContentLength = 1000;
                await context.Response.WriteAsync("partial");
                await context.Response.Body.FlushAsync();
                // A reset can overtake data the client has not read, which would lose the
                // status too, so give it time to read the headers first.
                await Task.Delay(TimeSpan.FromMilliseconds(500), context.RequestAborted);
                context.Abort();
                break;
            case "hang":
                await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                break;
            case "late":
                await Task.Delay(TimeSpan.FromSeconds(4), context.RequestAborted);
                context.Response.StatusCode = int.Parse(parts[1], CultureInfo.InvariantCulture);
                break;
            case "stall":
                if (attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromMinutes(3), context.RequestAborted);
                }
                context.Response.StatusCode = int.Parse(parts[1], CultureInfo.InvariantCulture);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (app is null) return;
        foreach (var group in itemGroups.Values) group.ReleaseAll();
        await app.StopAsync();
        await app.DisposeAsync();
        app = null;
    }
}

/// <summary>One call to an item route, recorded when it arrives and completed when it is answered.</summary>
public sealed class ExampleItemCall
{
    public required string Group { get; init; }
    public required int Index { get; init; }
    /// <summary>The call's position among this index's calls, from 1.</summary>
    public required int Occurrence { get; init; }
    public string? IdempotencyKey { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset ArrivedAt { get; init; }
    public int? StatusCode { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }
    /// <summary>The caller went away before this call was answered.</summary>
    public bool Abandoned { get; set; }
}

/// <summary>
/// Controlled per-item behavior for one scenario's automatic multi-instance body. By default
/// each call answers 200 with <c>{"id":"receipt-{index}","index":index,"amount":index*10}</c>.
/// A held index waits until released, a status sequence answers each call of an index in
/// turn (repeating the last), and an oversized index answers a body above 1 MiB. Every call
/// records its arrival, idempotency key and answer; nothing about the caller is changed.
/// </summary>
public sealed class ExampleItemGroup(string name)
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource> gates = new();
    private readonly ConcurrentDictionary<int, int[]> sequences = new();
    private readonly ConcurrentDictionary<int, byte> oversized = new();
    private readonly ConcurrentDictionary<int, int> occurrences = new();
    private readonly ConcurrentQueue<ExampleItemCall> calls = new();

    public string Name { get; } = name;
    public IReadOnlyList<ExampleItemCall> Calls => calls.ToArray();
    public int CallCount(int index) => calls.Count(call => call.Index == index);

    /// <summary>Holds every call of these indexes until <see cref="Release"/>.</summary>
    public ExampleItemGroup Hold(params int[] indexes)
    {
        foreach (var index in indexes) gates.TryAdd(index, new(TaskCreationOptions.RunContinuationsAsynchronously));
        return this;
    }

    public void Release(int index)
    {
        if (gates.TryGetValue(index, out var gate)) gate.TrySetResult();
    }

    public void ReleaseAll()
    {
        foreach (var gate in gates.Values) gate.TrySetResult();
    }

    /// <summary>Answers this index's calls with these statuses in turn; the last one repeats.</summary>
    public ExampleItemGroup Answer(int index, params int[] statuses)
    {
        sequences[index] = statuses;
        return this;
    }

    public ExampleItemGroup Oversize(int index)
    {
        oversized.TryAdd(index, 0);
        return this;
    }

    /// <summary>Waits until this index has received at least <paramref name="count"/> calls.</summary>
    public async Task<ExampleItemCall> ArrivedAsync(int index, int count = 1, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(90));
        while (true)
        {
            var arrived = calls.Where(call => call.Index == index).ToArray();
            if (arrived.Length >= count) return arrived[count - 1];
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Item {index} of '{Name}' received {arrived.Length} of {count} calls.");
            await Task.Delay(100, ScenarioCancellation.Token);
        }
    }

    internal async Task RespondAsync(HttpContext context, int index)
    {
        using var reader = new StreamReader(context.Request.Body);
        var call = new ExampleItemCall
        {
            Group = Name,
            Index = index,
            Occurrence = occurrences.AddOrUpdate(index, 1, (_, previous) => previous + 1),
            IdempotencyKey = context.Request.Headers["Idempotency-Key"].FirstOrDefault(),
            Body = await reader.ReadToEndAsync(context.RequestAborted),
            ArrivedAt = DateTimeOffset.UtcNow,
        };
        calls.Enqueue(call);
        if (gates.TryGetValue(index, out var gate))
        {
            try { await gate.Task.WaitAsync(context.RequestAborted); }
            catch (OperationCanceledException)
            {
                call.Abandoned = true;
                return;
            }
        }
        var status = sequences.TryGetValue(index, out var statuses)
            ? statuses[Math.Min(call.Occurrence - 1, statuses.Length - 1)]
            : 200;
        context.Response.StatusCode = status;
        call.StatusCode = status;
        if (oversized.ContainsKey(index))
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync($"{{\"id\":\"receipt-{index}\",\"padding\":\"{new string('x', 1_100_000)}\"}}");
        }
        else
        {
            await context.Response.WriteAsJsonAsync(new { id = $"receipt-{index}", index, amount = index * 10 });
        }
        call.AnsweredAt = DateTimeOffset.UtcNow;
    }
}
