namespace Flowbit.Service.Services;

/// <summary>
/// Operator configuration of lifecycle webhooks, bound from the <c>Webhooks</c> section by the API and the Worker.
/// Capture is switched in the database; <see cref="DeliveryEnabled"/> only controls whether this Worker sends.
/// </summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>Webhook connections beyond one per concurrent send.</summary>
    public const int ReservedConnections = 4;

    public bool DeliveryEnabled { get; set; }
    public int MaxConcurrentSends { get; set; } = 16;
    public int FanoutBatchSize { get; set; } = 100;
    public int PollSeconds { get; set; } = 5;
    public int HttpTimeoutSeconds { get; set; } = 15;
    public int LeaseSeconds { get; set; } = 60;
    public int HeartbeatSeconds { get; set; } = 15;
    public int MaxResponseHeaderKilobytes { get; set; } = 16;
    public int ConnectionLifetimeSeconds { get; set; } = 120;

    /// <summary>
    /// The webhook queue's own PostgreSQL pool, separate from workflow-job processing. A sending Worker needs one
    /// connection per concurrent send for its short prepare, heartbeat, and finalize transactions, plus
    /// <see cref="ReservedConnections"/> for the notification listener, fanout, acquisition, and key-ring reads.
    /// </summary>
    public int ConnectionPoolSize { get; set; } = 20;

    /// <summary>The pool size at which no send waits for a connection: <see cref="MaxConcurrentSends"/> plus the reserve.</summary>
    public int RecommendedConnectionPoolSize => MaxConcurrentSends + ReservedConnections;

    public WebhookNetworkOptions Network { get; set; } = new();
    public WebhookKeyProtectionOptions KeyProtection { get; set; } = new();

    public TimeSpan PollInterval => TimeSpan.FromSeconds(PollSeconds);
    public TimeSpan HttpTimeout => TimeSpan.FromSeconds(HttpTimeoutSeconds);
    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseSeconds);
    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(HeartbeatSeconds);

    /// <summary>
    /// How long retired secret material stays after its keys stop signing: a request fenced by archival or
    /// revocation may still be in flight until its lease expires and its HTTP timeout elapses.
    /// </summary>
    public TimeSpan SecretRemovalGrace => LeaseDuration + HttpTimeout;

    public void Validate(bool isDevelopment)
    {
        if (MaxConcurrentSends is < 1 or > 256)
            throw new InvalidOperationException($"{SectionName}:MaxConcurrentSends must be between 1 and 256.");
        if (FanoutBatchSize is < 1 or > 1000)
            throw new InvalidOperationException($"{SectionName}:FanoutBatchSize must be between 1 and 1000.");
        if (PollSeconds is < 1 or > 300)
            throw new InvalidOperationException($"{SectionName}:PollSeconds must be between 1 and 300.");
        if (HttpTimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException($"{SectionName}:HttpTimeoutSeconds must be between 1 and 60.");
        if (LeaseSeconds is < 15 or > 600)
            throw new InvalidOperationException($"{SectionName}:LeaseSeconds must be between 15 and 600.");
        if (HeartbeatSeconds < 1 || HeartbeatSeconds * 2 >= LeaseSeconds)
            throw new InvalidOperationException($"{SectionName}:HeartbeatSeconds must be less than half LeaseSeconds.");
        if (HttpTimeoutSeconds >= LeaseSeconds)
            throw new InvalidOperationException($"{SectionName}:HttpTimeoutSeconds must be less than LeaseSeconds.");
        if (MaxResponseHeaderKilobytes is < 1 or > 256)
            throw new InvalidOperationException($"{SectionName}:MaxResponseHeaderKilobytes must be between 1 and 256.");
        if (ConnectionLifetimeSeconds is < 10 or > 3600)
            throw new InvalidOperationException($"{SectionName}:ConnectionLifetimeSeconds must be between 10 and 3600.");
        if (ConnectionPoolSize is < 4 or > 100)
            throw new InvalidOperationException($"{SectionName}:ConnectionPoolSize must be between 4 and 100: the notification listener, fanout, and acquisition each hold one connection.");
        if (Network.AllowLoopbackHttp && !isDevelopment)
            throw new InvalidOperationException(
                $"{SectionName}:Network:AllowLoopbackHttp is a Development fixture exception and is rejected outside Development.");
        _ = new WebhookDestinationPolicy(Network);
    }
}

/// <summary>
/// The operator-only destination policy. Subscription administrators cannot change it. Allowlisted hosts may use a
/// port other than 443 and resolve to private addresses; allowlisted CIDR ranges admit any address inside them.
/// </summary>
public sealed class WebhookNetworkOptions
{
    /// <summary><c>host</c> (port 443) or <c>host:port</c> entries for internal HTTPS receivers.</summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>CIDR ranges, such as <c>10.20.0.0/16</c>, whose addresses receivers may use.</summary>
    public List<string> AllowedCidrs { get; set; } = [];

    /// <summary>Lets Development fixtures use their own loopback HTTP receiver. Rejected outside Development.</summary>
    public bool AllowLoopbackHttp { get; set; }
}

/// <summary>
/// The certificate that wraps the <c>Flowbit.Webhooks</c> Data Protection key ring. Without it, webhook secrets
/// cannot be created, rotated, or used, and no fallback key store is ever used.
/// </summary>
public sealed class WebhookKeyProtectionOptions
{
    /// <summary>A PKCS#12 (.pfx) file with the private key, readable only by the API and Worker.</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    /// <summary>Earlier certificates, kept so key-ring entries wrapped before a certificate change still unwrap.</summary>
    public List<WebhookCertificateOptions> PreviousCertificates { get; set; } = [];
}

public sealed class WebhookCertificateOptions
{
    public string? Path { get; set; }
    public string? Password { get; set; }
}
