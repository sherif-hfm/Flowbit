using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Flowbit.Infrastructure.Webhooks;

/// <summary>
/// Protects subscriber signing secrets with a dedicated <c>Flowbit.Webhooks</c> Data Protection key ring. The ring
/// lives in <c>webhook_data_protection_keys</c>, so the API and Worker share it without a shared volume, and every
/// key in it is wrapped with the configured certificate, so database backups hold only wrapped keys. There is no
/// fallback to machine-local or ephemeral keys: without a usable certificate the protector is unavailable.
/// </summary>
public sealed class DataProtectionWebhookSecretProtector : IWebhookSecretProtector, IDisposable
{
    public const string ApplicationName = "Flowbit.Webhooks";
    private const string Purpose = "Flowbit.Webhooks.SigningSecrets.v1";
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private readonly WebhookKeyProtectionOptions options;
    private readonly WebhookDataSource dataSource;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly TimeProvider timeProvider;
    private readonly Lock gate = new();
    private ServiceProvider? provider;
    private IDataProtector? protector;
    private string? unavailableReason;
    private DateTimeOffset retryAt = DateTimeOffset.MinValue;

    public DataProtectionWebhookSecretProtector(
        WebhookOptions webhookOptions,
        WebhookDataSource dataSource,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider)
    {
        options = webhookOptions.KeyProtection;
        this.dataSource = dataSource;
        this.loggerFactory = loggerFactory;
        this.timeProvider = timeProvider;
        logger = loggerFactory.CreateLogger<DataProtectionWebhookSecretProtector>();
    }

    public bool IsAvailable => EnsureProtector() is not null;

    public string? UnavailableReason
    {
        get
        {
            EnsureProtector();
            return unavailableReason;
        }
    }

    public byte[] Protect(byte[] secret) =>
        (EnsureProtector() ?? throw Unavailable()).Protect(secret);

    public byte[] Unprotect(byte[] protectedSecret)
    {
        var current = EnsureProtector() ?? throw Unavailable();
        try
        {
            return current.Unprotect(protectedSecret);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Another host may have added a ring key after this one loaded the ring; reload once.
            lock (gate)
            {
                if (ReferenceEquals(protector, current))
                {
                    provider?.Dispose();
                    provider = null;
                    protector = null;
                    retryAt = DateTimeOffset.MinValue;
                }
            }
            return (EnsureProtector() ?? throw Unavailable()).Unprotect(protectedSecret);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            provider?.Dispose();
            provider = null;
            protector = null;
        }
    }

    private WebhookKeyProtectionUnavailableException Unavailable() =>
        new(unavailableReason ?? "Webhook key protection is unavailable.");

    private IDataProtector? EnsureProtector()
    {
        if (Volatile.Read(ref protector) is { } ready)
        {
            return ready;
        }
        lock (gate)
        {
            if (protector is not null)
            {
                return protector;
            }
            if (timeProvider.GetUtcNow() < retryAt)
            {
                return null;
            }
            try
            {
                var certificate = LoadCertificate(options.CertificatePath, options.CertificatePassword, "Webhooks:KeyProtection:CertificatePath");
                var decryption = new List<X509Certificate2> { certificate };
                foreach (var previous in options.PreviousCertificates)
                {
                    decryption.Add(LoadCertificate(previous.Path, previous.Password, "Webhooks:KeyProtection:PreviousCertificates"));
                }
                var services = new ServiceCollection();
                services.AddSingleton(loggerFactory);
                services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
                services.AddDataProtection()
                    .SetApplicationName(ApplicationName)
                    .ProtectKeysWithCertificate(certificate)
                    .UnprotectKeysWithAnyCertificate([.. decryption]);
                services.Configure<KeyManagementOptions>(management =>
                    management.XmlRepository = new PostgresWebhookKeyRing(dataSource.DataSource));
                var built = services.BuildServiceProvider();
                // Every ring key must unwrap here. Otherwise Data Protection would create a fresh key, the probe
                // below would pass, and every existing secret would fail to decrypt on this host.
                var unreadable = UnreadableKeyCount(built.GetRequiredService<IKeyManager>());
                if (unreadable > 0)
                {
                    built.Dispose();
                    throw new WebhookKeyProtectionUnavailableException(
                        $"The webhook key-protection certificates cannot unwrap {unreadable} of the key ring's keys. Add the certificate that wrapped them to Webhooks:KeyProtection:PreviousCertificates; if that certificate is lost, follow the certificate-loss recovery in the deployment guide.");
                }
                var candidate = built.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
                // Prove the ring is usable now; the first use creates its first key.
                _ = candidate.Unprotect(candidate.Protect([0x66, 0x6c, 0x6f, 0x77]));
                provider = built;
                unavailableReason = null;
                Volatile.Write(ref protector, candidate);
                logger.LogInformation("Webhook key protection is ready with certificate {Thumbprint}.", certificate.Thumbprint);
                return candidate;
            }
            catch (Exception exception)
            {
                retryAt = timeProvider.GetUtcNow() + RetryAfterFailure;
                unavailableReason = exception is WebhookKeyProtectionUnavailableException
                    ? exception.Message
                    : "Webhook key protection could not use its certificate-wrapped key ring. Check the certificate, its password, and that it can unwrap the existing keys.";
                logger.LogError(
                    "Webhook key protection is unavailable ({Reason}). Secrets cannot be created, rotated, or used until it is fixed. Failure type: {FailureType}.",
                    unavailableReason,
                    exception.GetType().Name);
                return null;
            }
        }
    }

    /// <summary>Unrevoked ring keys whose descriptors the configured certificates cannot decrypt.</summary>
    private static int UnreadableKeyCount(IKeyManager keyManager)
    {
        var unreadable = 0;
        foreach (var key in keyManager.GetAllKeys())
        {
            if (key.IsRevoked)
            {
                continue;
            }
            try
            {
                _ = key.Descriptor;
            }
            catch (Exception)
            {
                unreadable++;
            }
        }
        return unreadable;
    }

    private static X509Certificate2 LoadCertificate(string? path, string? password, string setting)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new WebhookKeyProtectionUnavailableException(
                $"Webhook key protection is not configured: set {setting} to a PKCS#12 certificate with a private key.");
        }
        if (!File.Exists(path))
        {
            throw new WebhookKeyProtectionUnavailableException(
                $"The webhook key-protection certificate named by {setting} does not exist.");
        }
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            path,
            password,
            X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey)
        {
            throw new WebhookKeyProtectionUnavailableException(
                $"The webhook key-protection certificate named by {setting} has no private key.");
        }
        return certificate;
    }

    /// <summary>The key ring's XML elements in PostgreSQL. Each element is already wrapped with the certificate.</summary>
    private sealed class PostgresWebhookKeyRing(NpgsqlDataSource dataSource) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements()
        {
            using var connection = dataSource.OpenConnection();
            using var command = new NpgsqlCommand(
                "SELECT \"Xml\" FROM flowbit.webhook_data_protection_keys ORDER BY \"Id\"",
                connection);
            using var reader = command.ExecuteReader();
            var elements = new List<XElement>();
            while (reader.Read())
            {
                elements.Add(XElement.Parse(reader.GetString(0)));
            }
            return elements;
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            using var connection = dataSource.OpenConnection();
            using var command = new NpgsqlCommand(
                "INSERT INTO flowbit.webhook_data_protection_keys (\"FriendlyName\", \"Xml\") VALUES (@name, @xml)",
                connection);
            command.Parameters.AddWithValue("name", (object?)friendlyName ?? DBNull.Value);
            command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
            command.ExecuteNonQuery();
        }
    }
}
