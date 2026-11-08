extern alias FlowbitWorker;

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Flowbit.Infrastructure.Http;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Xunit;
using WebhookReadinessHealthCheck = FlowbitWorker::Flowbit.Worker.WebhookReadinessHealthCheck;
using WebhookTelemetry = FlowbitWorker::Flowbit.Worker.WebhookTelemetry;

namespace Flowbit.Tests;

/// <summary>Worker readiness, per-loop health, and the webhook transport's fixed HTTP settings.</summary>
public sealed class WebhookWorkerHealthTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T09:00:00Z");

    [Fact]
    public async Task ReadinessIsHealthyWhileDeliveryIsDisabledAndDegradedWhenKeyProtectionIsUnavailable()
    {
        using var telemetry = new WebhookTelemetry(new ManualClock(Now));
        Assert.Equal(HealthStatus.Healthy,
            (await CheckAsync(new WebhookOptions { DeliveryEnabled = false }, new StubProtector(false), telemetry)).Status);
        var unavailable = await CheckAsync(new WebhookOptions { DeliveryEnabled = true }, new StubProtector(false), telemetry);
        Assert.Equal(HealthStatus.Degraded, unavailable.Status);
        Assert.Equal(StubProtector.Reason, unavailable.Description);
        Assert.Equal(HealthStatus.Healthy,
            (await CheckAsync(new WebhookOptions { DeliveryEnabled = true }, new StubProtector(true), telemetry)).Status);
    }

    [Fact]
    public async Task ALoopThatKeepsFailingKeepsReadinessDegradedWhileAnotherLoopSucceeds()
    {
        var clock = new ManualClock(Now);
        using var telemetry = new WebhookTelemetry(clock);
        var options = new WebhookOptions { DeliveryEnabled = true };
        var protector = new StubProtector(true);

        telemetry.RecordLoopFailure(WebhookTelemetry.DispatchLoop);
        clock.Now = Now.AddSeconds(1);
        telemetry.RecordLoopSuccess(WebhookTelemetry.FanoutLoop);
        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(options, protector, telemetry)).Status);

        // Only a success of the failing loop itself clears it.
        clock.Now = Now.AddSeconds(2);
        telemetry.RecordLoopSuccess(WebhookTelemetry.DispatchLoop);
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(options, protector, telemetry)).Status);

        // A failure older than the window no longer degrades readiness.
        telemetry.RecordLoopFailure(WebhookTelemetry.SigningLoop);
        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(options, protector, telemetry)).Status);
        clock.Now = Now.AddMinutes(3);
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(options, protector, telemetry)).Status);
    }

    [Fact]
    public void TheTransportUsesNoAmbientProxyRedirectCookieOrCredential()
    {
        using var transport = new WebhookHttpTransport(
            new WebhookOptions(), new WebhookDestinationPolicy(new WebhookNetworkOptions()), TimeProvider.System);
        var handler = transport.Handler;
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.False(handler.PreAuthenticate);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
        Assert.Equal(TimeSpan.FromSeconds(120), handler.PooledConnectionLifetime);
        // Every new connection goes through the destination policy's address check.
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task AnUntrustedReceiverCertificateFailsAsATlsErrorBeforeAnyRequest()
    {
        using var certificate = SelfSignedServerCertificate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        var reached = 0;
        app.Run(context =>
        {
            Interlocked.Increment(ref reached);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;

        // The operator allowlists the loopback receiver; certificate validation still applies.
        using var transport = new WebhookHttpTransport(
            new WebhookOptions(),
            new WebhookDestinationPolicy(new WebhookNetworkOptions { AllowedCidrs = ["127.0.0.1/32"] }),
            TimeProvider.System);
        var outcome = await transport.SendAsync(
            new WebhookSendRequest(
                new Uri($"https://127.0.0.1:{port}/hook"),
                Guid.CreateVersion7(),
                Encoding.UTF8.GetBytes("{\"test\":true}"),
                [StandardWebhookSignature.GenerateKey()],
                TimeSpan.FromSeconds(5)),
            CancellationToken.None);
        Assert.Equal(WebhookOutcomeCodes.TlsError, outcome.Outcome);
        Assert.Null(outcome.HttpStatus);
        Assert.Equal(0, reached);
        await app.StopAsync();
    }

    private static Task<HealthCheckResult> CheckAsync(
        WebhookOptions options,
        IWebhookSecretProtector protector,
        WebhookTelemetry telemetry) =>
        new WebhookReadinessHealthCheck(options, protector, telemetry).CheckHealthAsync(new HealthCheckContext());

    private static X509Certificate2 SelfSignedServerCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows TLS needs a key that is not ephemeral, so the certificate round-trips through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubProtector(bool available) : IWebhookSecretProtector
    {
        public const string Reason = "The key ring cannot be unwrapped.";

        public bool IsAvailable => available;

        public string? UnavailableReason => available ? null : Reason;

        public byte[] Protect(byte[] secret) => secret;

        public byte[] Unprotect(byte[] protectedSecret) => protectedSecret;
    }
}
