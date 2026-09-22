using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Flowbit.BrowserTests.Infrastructure;

/// <summary>
/// Test-owned transport seam for Blazor's server-side API calls. Responses come from
/// the real API; gates delay delivery without manufacturing or changing payloads.
/// Only method/path/status/timing are recorded, never authorization or bodies.
/// </summary>
public sealed class RecordingApiProxy : IAsyncDisposable
{
    public sealed record RecordedRequest(long Id, string Method, string PathAndQuery,
        DateTimeOffset StartedAt, int? StatusCode = null, DateTimeOffset? CompletedAt = null);

    public sealed class ResponseGate : IDisposable
    {
        private readonly TaskCompletionSource<RecordedRequest> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ResponseGate(Func<RecordedRequest, bool> predicate, TimeSpan timeout)
        { Predicate = predicate; Timeout = timeout; }
        internal Func<RecordedRequest, bool> Predicate { get; }
        internal TimeSpan Timeout { get; }
        internal bool Claimed { get; set; }
        internal bool Disposed { get; private set; }
        public Task<RecordedRequest> Entered => entered.Task;
        internal async Task HoldAsync(RecordedRequest request, CancellationToken cancellationToken)
        {
            entered.TrySetResult(request);
            await released.Task.WaitAsync(Timeout, cancellationToken);
        }
        public void Release() => released.TrySetResult();
        public void Dispose() { Disposed = true; Release(); }
    }

    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host" };
    private readonly object sync = new();
    private readonly List<RecordedRequest> requests = [];
    private readonly List<ResponseGate> gates = [];
    private readonly HttpClient upstream = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None })
    { Timeout = TimeSpan.FromSeconds(90) };
    private WebApplication? app;
    private string? artifactPath;
    private long nextId;
    public string BaseAddress { get; private set; } = string.Empty;
    public IReadOnlyList<RecordedRequest> Requests { get { lock (sync) return requests.ToArray(); } }

    public ResponseGate GateNextResponse(Func<RecordedRequest, bool> predicate, TimeSpan? timeout = null)
    {
        var gate = new ResponseGate(predicate, timeout ?? TimeSpan.FromSeconds(20));
        lock (sync) gates.Add(gate);
        return gate;
    }

    public void ReleaseAllGates()
    {
        lock (sync) { foreach (var gate in gates) gate.Dispose(); gates.Clear(); }
    }

    public async Task StartAsync(string apiBaseAddress, string? outputPath = null)
    {
        var address = new Uri(apiBaseAddress);
        if (!address.IsLoopback || address.Scheme != "http")
            throw new ArgumentException("Acceptance proxy upstream must be the fixture's loopback HTTP API.", nameof(apiBaseAddress));
        artifactPath = outputPath;
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        app = builder.Build();
        app.Run(context => ForwardAsync(context, address));
        await app.StartAsync();
        BaseAddress = app.Urls.Single();
    }

    private async Task ForwardAsync(HttpContext context, Uri apiAddress)
    {
        var request = new RecordedRequest(Interlocked.Increment(ref nextId), context.Request.Method,
            context.Request.Path + context.Request.QueryString, DateTimeOffset.UtcNow);
        ResponseGate? responseGate;
        lock (sync)
        {
            requests.Add(request);
            responseGate = gates.FirstOrDefault(gate => !gate.Disposed && !gate.Claimed && gate.Predicate(request));
            if (responseGate is not null) responseGate.Claimed = true;
        }
        try
        {
            // Build against the fixed host; a request path can never select another server.
            var destination = new UriBuilder(apiAddress) { Path = context.Request.Path, Query = context.Request.QueryString.Value };
            using var outgoing = new HttpRequestMessage(new HttpMethod(request.Method), destination.Uri);
            if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
                outgoing.Content = new StreamContent(context.Request.Body);
            var connectionHeaders = context.Request.Headers.Connection.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var header in context.Request.Headers)
            {
                if (HopHeaders.Contains(header.Key) || connectionHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) continue;
                if (!outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    outgoing.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            using var response = await upstream.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            var bytes = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
            request = request with { StatusCode = (int)response.StatusCode };
            Update(request);
            if (responseGate is not null) await responseGate.HoldAsync(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!HopHeaders.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
        finally
        {
            Update(request with { CompletedAt = DateTimeOffset.UtcNow });
        }
    }

    private void Update(RecordedRequest request)
    {
        lock (sync)
        {
            var index = requests.FindIndex(row => row.Id == request.Id);
            if (index >= 0) requests[index] = request;
        }
    }

    public async ValueTask DisposeAsync()
    {
        ReleaseAllGates();
        try
        {
            if (app is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await app.StopAsync(timeout.Token); }
                finally { await app.DisposeAsync(); app = null; }
            }
        }
        finally
        {
            upstream.Dispose();
            if (artifactPath is not null)
                await File.WriteAllTextAsync(artifactPath, JsonSerializer.Serialize(Requests, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
