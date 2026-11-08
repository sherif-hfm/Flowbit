using System.Security.Cryptography;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Webhooks;
using Flowbit.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The certificate-wrapped <c>Flowbit.Webhooks</c> key ring in real PostgreSQL. The ring is shared by every test,
/// so keys created here under other certificates are removed again.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class WebhookKeyProtectionTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private readonly List<string> certificates = [];
    private WebhookDataSource source = null!;
    private long ringHighWater;

    public async Task InitializeAsync()
    {
        source = new WebhookDataSource(fixture.ConnectionString, 4);
        // The fixture's certificate owns the shared ring; make sure it holds a key before any test runs.
        using (var owner = Protector(fixture.WebhookCertificatePath, fixture.WebhookCertificatePassword))
        {
            Assert.True(owner.IsAvailable);
        }
        ringHighWater = await RingHighWaterAsync();
    }

    public async Task DisposeAsync()
    {
        await WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
            "DELETE FROM flowbit.webhook_data_protection_keys WHERE \"Id\" > @id", ("id", ringHighWater));
        foreach (var path in certificates)
        {
            WebhookTestCertificates.Delete(path);
        }
        await source.DisposeAsync();
    }

    [Fact]
    public void WithoutACertificateKeyProtectionIsUnavailable()
    {
        using var protector = Protector(null, null);
        Assert.False(protector.IsAvailable);
        Assert.Contains("Webhooks:KeyProtection:CertificatePath", protector.UnavailableReason, StringComparison.Ordinal);
        Assert.Throws<WebhookKeyProtectionUnavailableException>(() => protector.Protect([1, 2, 3]));
    }

    [Fact]
    public async Task AHostWhoseCertificateCannotUnwrapTheRingIsUnavailableAndAddsNoKey()
    {
        var other = NewCertificate(out var password);
        using var protector = Protector(other, password);
        // Data Protection would otherwise add a key it can read and pass its own round trip.
        Assert.False(protector.IsAvailable);
        Assert.Contains("PreviousCertificates", protector.UnavailableReason, StringComparison.Ordinal);
        Assert.Throws<WebhookKeyProtectionUnavailableException>(() => protector.Unprotect([1, 2, 3]));
        Assert.Equal(ringHighWater, await RingHighWaterAsync());
    }

    [Fact]
    public void APreviousCertificateStillUnwrapsTheRingAfterACertificateChange()
    {
        using var original = Protector(fixture.WebhookCertificatePath, fixture.WebhookCertificatePassword);
        var secret = RandomNumberGenerator.GetBytes(32);
        var protectedSecret = original.Protect(secret);

        var replacement = NewCertificate(out var password);
        using var changed = Protector(replacement, password, (fixture.WebhookCertificatePath, fixture.WebhookCertificatePassword));
        Assert.True(changed.IsAvailable);
        Assert.Equal(secret, changed.Unprotect(protectedSecret));
    }

    [Fact]
    public void SeparateHostsShareTheRingThroughTheDatabase()
    {
        // The API protects a new secret; a Worker process with its own provider signs with it.
        using var api = Protector(fixture.WebhookCertificatePath, fixture.WebhookCertificatePassword);
        using var worker = Protector(fixture.WebhookCertificatePath, fixture.WebhookCertificatePassword);
        var secret = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(secret, worker.Unprotect(api.Protect(secret)));
    }

    private DataProtectionWebhookSecretProtector Protector(
        string? path,
        string? password,
        params (string Path, string Password)[] previous) =>
        new(new WebhookOptions
        {
            KeyProtection = new WebhookKeyProtectionOptions
            {
                CertificatePath = path,
                CertificatePassword = password,
                PreviousCertificates =
                [
                    .. previous.Select(item => new WebhookCertificateOptions { Path = item.Path, Password = item.Password })
                ]
            }
        }, source, NullLoggerFactory.Instance, TimeProvider.System);

    private string NewCertificate(out string password)
    {
        var path = WebhookTestCertificates.Create(out password);
        certificates.Add(path);
        return path;
    }

    private Task<long> RingHighWaterAsync() =>
        WebhookTestDatabase.CountEventsAsync(fixture.DataSource,
            "SELECT COALESCE(max(\"Id\"), 0) FROM flowbit.webhook_data_protection_keys");
}
