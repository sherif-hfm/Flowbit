using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// Stands in for the external services that the REST examples call and for webhook receivers. Every
/// route answers after a fixed delay and records what arrived, so a run can check that each call
/// happened as often as it should: once for a job that succeeds, twice for one that fails once, and
/// once per webhook delivery with a valid signature.
/// </summary>
sealed class LoadStub : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly int _delayMilliseconds;
    private readonly ConcurrentDictionary<long, RetryJob> _jobs = new();
    private readonly ConcurrentDictionary<int, byte[]> _secrets = new();
    private readonly ConcurrentDictionary<string, int> _deliveries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _eventTypes = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<double> _lags = [];
    private long _orders;
    private long _hooks;
    private long _duplicates;
    private long _badSignatures;
    private long _unknownReceivers;
    private long _firstHookTicks;
    private long _lastHookTicks;

    private LoadStub(WebApplication app, int delayMilliseconds, int port)
    {
        _app = app;
        _delayMilliseconds = delayMilliseconds;
        BaseAddress = $"http://127.0.0.1:{port}";
    }

    public string BaseAddress { get; }
    public long HookCount => Interlocked.Read(ref _hooks);
    public long CallCount => Interlocked.Read(ref _orders) + _jobs.Values.Sum(j => (long)j.Calls) + HookCount;

    public double SecondsSinceLastHook =>
        Interlocked.Read(ref _lastHookTicks) is var last and > 0 ? (DateTime.UtcNow.Ticks - last) / (double)TimeSpan.TicksPerSecond : double.MaxValue;

    /// <summary>Calls that did not happen as they should: repeated or missing job calls and bad webhook deliveries.</summary>
    public long ProblemCount =>
        _jobs.Values.Count(j => j.Calls != j.ExpectedCalls) + Interlocked.Read(ref _badSignatures) + Interlocked.Read(ref _unknownReceivers);

    public static async Task<LoadStub> StartAsync(int port, int delayMilliseconds)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        var stub = new LoadStub(app, delayMilliseconds, port);
        app.MapPost("/orders/{customerId}", (RequestDelegate)stub.OrderAsync);
        app.MapPost("/retry", (RequestDelegate)stub.RetryAsync);
        app.MapPost("/hooks/{index:int}", (RequestDelegate)stub.HookAsync);
        await app.StartAsync();
        return stub;
    }

    public void RegisterSecret(int index, string secret) =>
        _secrets[index] = Convert.FromBase64String(secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret[6..] : secret);

    /// <summary>Starts the delivery window at the run's start rather than at a test delivery before it.</summary>
    public void ResetHookClock()
    {
        Interlocked.Exchange(ref _firstHookTicks, 0);
        Interlocked.Exchange(ref _lastHookTicks, 0);
    }

    public IEnumerable<string> Report()
    {
        if (Interlocked.Read(ref _orders) is var orders and > 0)
            yield return $"Stub: {orders:N0} synchronous order calls answered.";

        if (!_jobs.IsEmpty)
        {
            foreach (var group in _jobs.Values.GroupBy(j => j.Scenario).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var wrong = group.Count(j => j.Calls != j.ExpectedCalls);
                yield return $"Stub: {group.Count():N0} '{group.Key}' jobs called {group.Sum(j => j.Calls):N0} times; " +
                    (wrong == 0 ? $"each exactly {group.First().ExpectedCalls} times" : $"{wrong:N0} jobs were called a different number of times");
            }
        }

        if (!_secrets.IsEmpty || HookCount > 0)
        {
            var first = Interlocked.Read(ref _firstHookTicks);
            var last = Interlocked.Read(ref _lastHookTicks);
            var window = first > 0 && last > first ? TimeSpan.FromTicks(last - first) : TimeSpan.Zero;
            var lags = _lags.OrderBy(x => x).ToArray();
            yield return $"Stub: {HookCount:N0} webhook deliveries to {_secrets.Count} receivers" +
                (window > TimeSpan.Zero ? $" over {window.TotalSeconds:F0} s ({HookCount / window.TotalSeconds:F1}/s)" : "") +
                $"; repeated {Interlocked.Read(ref _duplicates):N0}, bad signatures {Interlocked.Read(ref _badSignatures):N0}, unknown receiver {Interlocked.Read(ref _unknownReceivers):N0}";
            if (lags.Length > 0)
                yield return $"Stub: event time to arrival p50 {Pick(lags, .5) / 1000:F2} s, p95 {Pick(lags, .95) / 1000:F2} s, max {lags[^1] / 1000:F2} s " +
                    "(the event's time is the database's clock; see the clock line above)";
            if (!_eventTypes.IsEmpty)
                yield return "Stub: deliveries by type: " + string.Join(", ",
                    _eventTypes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:N0}"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // Example 01's mappings require each of these fields with its declared type.
    private async Task OrderAsync(HttpContext context)
    {
        await Task.Delay(_delayMilliseconds);
        var n = Interlocked.Increment(ref _orders);
        var customerId = context.Request.RouteValues["customerId"] as string ?? "unknown";
        var now = DateTime.UtcNow;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            data = new
            {
                order = new { id = $"ORD-{customerId}-{n}" },
                accepted = true,
                total = 10 + n % 90,
                labels = new[] { "load-test" },
                shipDate = now.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                receivedAt = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            },
            meta = new { traceId = $"trace-{n}", warnings = Array.Empty<string>() }
        }));
    }

    // Durable-jobs example 02 sends its job id, which stays the same on every attempt of one job.
    private async Task RetryAsync(HttpContext context)
    {
        await Task.Delay(_delayMilliseconds);
        if (!long.TryParse(context.Request.Headers["X-Flowbit-Job-Id"], out var jobId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var payload = await JsonDocument.ParseAsync(context.Request.Body);
        var scenario = payload.RootElement.TryGetProperty("scenario", out var value) ? value.GetString() ?? "" : "";
        var job = _jobs.GetOrAdd(jobId, _ => new RetryJob(scenario));
        var call = Interlocked.Increment(ref job.Calls);
        var ok = scenario != "fail-once" || call > 1;
        context.Response.StatusCode = ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(ok ? """{"ok":true}""" : """{"ok":false}""");
    }

    private async Task HookAsync(HttpContext context)
    {
        var arrived = DateTimeOffset.UtcNow;
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        var body = buffer.ToArray();
        var index = int.Parse((string)context.Request.RouteValues["index"]!, CultureInfo.InvariantCulture);
        var id = context.Request.Headers["webhook-id"].ToString();
        if (!_secrets.TryGetValue(index, out var key))
            Interlocked.Increment(ref _unknownReceivers);
        else if (!Verify(key, id, context.Request.Headers["webhook-timestamp"].ToString(), body, context.Request.Headers["webhook-signature"].ToString()))
            Interlocked.Increment(ref _badSignatures);
        if (_deliveries.AddOrUpdate($"{index}:{id}", 1, (_, count) => count + 1) > 1)
            Interlocked.Increment(ref _duplicates);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("timestamp", out var time) && time.TryGetDateTimeOffset(out var occurred))
                _lags.Add((arrived - occurred).TotalMilliseconds);
            if (document.RootElement.TryGetProperty("type", out var type) && type.GetString() is { } name)
                _eventTypes.AddOrUpdate(name, 1, (_, count) => count + 1);
        }
        catch (JsonException)
        {
        }

        Interlocked.Increment(ref _hooks);
        Interlocked.CompareExchange(ref _firstHookTicks, arrived.UtcTicks, 0);
        Interlocked.Exchange(ref _lastHookTicks, arrived.UtcTicks);
        await Task.Delay(_delayMilliseconds);
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    // Standard Webhooks v1: HMAC-SHA256 over "id.timestamp." and the exact body; the header may carry several entries.
    private static bool Verify(byte[] key, string id, string timestamp, byte[] body, string header)
    {
        var prefix = Encoding.UTF8.GetBytes($"{id}.{timestamp}.");
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload, prefix.Length);
        var expected = Encoding.ASCII.GetBytes("v1," + Convert.ToBase64String(HMACSHA256.HashData(key, payload)));
        return header.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(entry), expected));
    }

    private static double Pick(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    private sealed class RetryJob(string scenario)
    {
        public int Calls;
        public string Scenario { get; } = scenario;
        public int ExpectedCalls { get; } = scenario == "fail-once" ? 2 : 1;
    }
}
