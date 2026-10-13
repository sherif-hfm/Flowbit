using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Flowbit.BrowserTests.Infrastructure;

/// <summary>Real loopback TLS termination and WebSocket forwarding; never changes application payloads or authentication state.</summary>
public sealed class OidcUiProxy : IAsyncDisposable
{
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host" };
    private readonly HttpClient client = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<int, (WebSocket Browser, ClientWebSocket Backend)> sockets = new();
    private readonly ConcurrentQueue<object> requests = new();
    private WebApplication? app;
    private string? artifactPath;
    private int websocketConnections;
    public string Origin { get; private set; } = "";
    public int WebSocketConnections => Volatile.Read(ref websocketConnections);

    public async Task StartAsync(string backendOrigin, X509Certificate2 certificate, string outputPath)
    {
        var backend = new Uri(backendOrigin);
        if (!backend.IsLoopback || backend.Scheme != "http")
            throw new ArgumentException("OIDC proxy must forward to its fixture-owned loopback HTTP UI.", nameof(backendOrigin));
        artifactPath = outputPath;
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listener => listener.UseHttps(certificate)));
        app = builder.Build();
        app.UseWebSockets();
        app.Run(context => ForwardAsync(context, backend));
        await app.StartAsync();
        Origin = app.Urls.Single();
    }

    // Deliberately drop only real transport connections. The browser and UI
    // perform their normal Blazor reconnect handshake against retained circuits.
    public void DropWebSocketConnections()
    {
        foreach (var socket in sockets.Values)
        {
            socket.Browser.Abort();
            socket.Backend.Abort();
        }
    }

    private async Task ForwardAsync(HttpContext context, Uri backend)
    {
        var destination = new UriBuilder(backend) { Path = context.Request.Path, Query = context.Request.QueryString.Value };
        requests.Enqueue(new { method = context.Request.Method, path = context.Request.Path.Value,
            externalScheme = context.Request.Scheme, externalHost = context.Request.Host.Value, backendScheme = backend.Scheme,
            websocket = context.WebSockets.IsWebSocketRequest });
        if (context.WebSockets.IsWebSocketRequest)
        {
            destination.Scheme = "ws";
            await ForwardWebSocketAsync(context, destination.Uri);
            return;
        }

        using var outgoing = new HttpRequestMessage(new HttpMethod(context.Request.Method), destination.Uri);
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            outgoing.Content = new StreamContent(context.Request.Body);
        var connectionHeaders = context.Request.Headers.Connection.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var header in context.Request.Headers)
        {
            if (HopHeaders.Contains(header.Key) || header.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                || connectionHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) continue;
            if (!outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                outgoing.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        outgoing.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
        outgoing.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);
        outgoing.Headers.TryAddWithoutValidation("X-Forwarded-For", context.Connection.RemoteIpAddress?.ToString());
        try
        {
            using var response = await client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!HopHeaders.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private async Task ForwardWebSocketAsync(HttpContext context, Uri destination)
    {
        using var upstream = new ClientWebSocket();
        upstream.Options.SetRequestHeader("Cookie", context.Request.Headers.Cookie.ToString());
        upstream.Options.SetRequestHeader("Origin", context.Request.Headers.Origin.ToString());
        upstream.Options.SetRequestHeader("X-Forwarded-Proto", context.Request.Scheme);
        upstream.Options.SetRequestHeader("X-Forwarded-Host", context.Request.Host.Value);
        upstream.Options.SetRequestHeader("X-Forwarded-For", context.Connection.RemoteIpAddress?.ToString());
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols) upstream.Options.AddSubProtocol(protocol);
        await upstream.ConnectAsync(destination, context.RequestAborted);
        using var downstream = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        var id = Interlocked.Increment(ref websocketConnections);
        sockets[id] = (downstream, upstream);
        try
        {
            var toBackend = PumpAsync(downstream, upstream, context.RequestAborted);
            var toBrowser = PumpAsync(upstream, downstream, context.RequestAborted);
            await Task.WhenAny(toBackend, toBrowser);
            downstream.Abort();
            upstream.Abort();
            await Task.WhenAll(toBackend, toBrowser);
        }
        finally { sockets.TryRemove(id, out _); }
    }

    private static async Task PumpAsync(WebSocket source, WebSocket target, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await source.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var status = source.CloseStatus ?? WebSocketCloseStatus.NormalClosure;
                    await source.CloseOutputAsync(status, source.CloseStatusDescription, cancellationToken);
                    if (target.State == WebSocketState.Open)
                        await target.CloseOutputAsync(status, source.CloseStatusDescription, cancellationToken);
                    return;
                }
                await target.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        DropWebSocketConnections();
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
            client.Dispose();
            if (artifactPath is not null)
                await File.WriteAllTextAsync(artifactPath, JsonSerializer.Serialize(new
                { websocketConnections = WebSocketConnections, requests = requests.ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
