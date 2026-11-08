using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;

namespace Flowbit.Infrastructure.Http;

/// <summary>
/// Sends signed webhook requests. Its dedicated handler resolves each new connection's host itself, rejects
/// addresses the operator's policy does not allow (including mapped and alternate IPv6 forms), and connects to a
/// validated address while keeping the hostname for TLS and SNI, so DNS rebinding cannot redirect a request.
/// Redirects, proxies, cookies, credentials, and response bodies are never used.
/// </summary>
public sealed class WebhookHttpTransport : IWebhookTransport, IDisposable
{
    public const string UserAgent = "Flowbit-Webhooks/1.0";

    private readonly HttpClient client;
    private readonly WebhookDestinationPolicy policy;
    private readonly TimeProvider timeProvider;

    public WebhookHttpTransport(WebhookOptions options, WebhookDestinationPolicy policy, TimeProvider timeProvider)
    {
        this.policy = policy;
        this.timeProvider = timeProvider;
        Handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            Proxy = null,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = options.MaxResponseHeaderKilobytes,
            PooledConnectionLifetime = TimeSpan.FromSeconds(options.ConnectionLifetimeSeconds),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(Math.Min(30, options.ConnectionLifetimeSeconds)),
            ConnectTimeout = options.HttpTimeout,
            ConnectCallback = ConnectAsync
        };
        client = new HttpClient(Handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The handler's settings, so tests can prove no ambient proxy, redirect, cookie, or credential applies.</summary>
    internal SocketsHttpHandler Handler { get; }

    public async Task<WebhookSendOutcome> SendAsync(WebhookSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        var now = timeProvider.GetUtcNow();
        var messageId = StandardWebhookSignature.MessageId(request.EventId);
        var timestamp = now.ToUnixTimeSeconds();
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Destination)
        {
            Content = new ByteArrayContent(request.Body)
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        message.Headers.UserAgent.ParseAdd(UserAgent);
        message.Headers.TryAddWithoutValidation(StandardWebhookSignature.IdHeader, messageId);
        message.Headers.TryAddWithoutValidation(
            StandardWebhookSignature.TimestampHeader,
            timestamp.ToString(CultureInfo.InvariantCulture));
        message.Headers.TryAddWithoutValidation(
            StandardWebhookSignature.SignatureHeader,
            StandardWebhookSignature.Header(request.SigningKeys, messageId, timestamp, request.Body));
        try
        {
            // Only the status line and headers are read; the body is never buffered or logged.
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            TimeSpan? retryAfter = null;
            if (status is 429 or 503 && response.Headers.TryGetValues("Retry-After", out var values))
            {
                retryAfter = WebhookRetryPolicy.ParseRetryAfter(values.FirstOrDefault(), timeProvider.GetUtcNow());
            }
            return new WebhookSendOutcome(WebhookRetryPolicy.ClassifyStatus(status), status, retryAfter, Elapsed(started));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebhookSendOutcome(WebhookOutcomeCodes.Timeout, null, null, Elapsed(started));
        }
        catch (HttpRequestException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebhookSendOutcome(Classify(exception), null, null, Elapsed(started));
        }
    }

    public void Dispose() => client.Dispose();

    internal static string Classify(HttpRequestException exception)
    {
        if (FindInner<WebhookDestinationDeniedException>(exception) is not null)
        {
            return WebhookOutcomeCodes.DestinationDenied;
        }
        if (exception.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            return WebhookOutcomeCodes.ResponseHeadersTooLarge;
        }
        if (FindInner<AuthenticationException>(exception) is not null)
        {
            return WebhookOutcomeCodes.TlsError;
        }
        return WebhookOutcomeCodes.NetworkError;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var destination = context.InitialRequestMessage.RequestUri
            ?? throw new WebhookDestinationDeniedException();
        var host = context.DnsEndPoint.Host;
        // Addresses are evaluated again on every new connection.
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);
        var allowed = addresses.Where(address => policy.IsAddressAllowed(address, destination)).ToArray();
        if (allowed.Length == 0)
        {
            throw new WebhookDestinationDeniedException();
        }
        SocketException? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                last = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last!;
    }

    private static T? FindInner<T>(Exception exception) where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }
        return null;
    }

    private static int Elapsed(long started) =>
        (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}

/// <summary>Every resolved address of a destination was rejected by the operator's network policy.</summary>
internal sealed class WebhookDestinationDeniedException()
    : IOException("The webhook destination's addresses are not allowed by the network policy.");
