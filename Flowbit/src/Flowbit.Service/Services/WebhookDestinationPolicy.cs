using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Flowbit.Service.Models;

namespace Flowbit.Service.Services;

public enum WebhookAddressClass
{
    Public,

    /// <summary>RFC 1918, carrier-grade NAT, and IPv6 unique-local addresses.</summary>
    Private,

    Loopback,

    /// <summary>Unspecified, link-local and metadata, multicast, documentation, and other non-routable addresses.</summary>
    Forbidden
}

/// <summary>
/// Server-side request forgery protection for webhook destinations: save-time URL rules and the connection-time
/// address check that the Worker applies to every resolved IPv4 or IPv6 address of a new connection.
/// </summary>
public sealed class WebhookDestinationPolicy
{
    private const int HttpsPort = 443;

    // AWS's IPv6 instance metadata endpoint sits inside the unique-local range.
    private static readonly IPAddress Ipv6Metadata = IPAddress.Parse("fd00:ec2::254");

    private readonly HashSet<(string Host, int Port)> allowedHosts = [];
    private readonly List<IPNetwork> allowedNetworks = [];
    private readonly bool allowLoopbackHttp;

    public WebhookDestinationPolicy(WebhookNetworkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        allowLoopbackHttp = options.AllowLoopbackHttp;
        foreach (var entry in options.AllowedHosts ?? [])
        {
            allowedHosts.Add(ParseHostEntry(entry));
        }
        foreach (var entry in options.AllowedCidrs ?? [])
        {
            if (!IPNetwork.TryParse(entry?.Trim(), out var network))
            {
                throw new InvalidOperationException(
                    $"{WebhookOptions.SectionName}:Network:AllowedCidrs contains an invalid CIDR range '{entry}'.");
            }
            allowedNetworks.Add(network);
        }
    }

    /// <summary>The scheme, host, and port of a destination: the only part safe to log, audit, or show in errors.</summary>
    public static string Origin(Uri uri) =>
        string.Create(CultureInfo.InvariantCulture, $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}");

    public static string? TryOrigin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Origin(uri) : null;

    /// <summary>Validates a destination when a subscription is saved; addresses are checked again at connection time.</summary>
    public bool TryValidateUrl(string? value, out Uri? uri, out string? error)
    {
        uri = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "The endpoint URL is required.";
            return false;
        }
        if (value.Length > WebhookConstraints.MaxEndpointUrlLength)
        {
            error = $"The endpoint URL must not exceed {WebhookConstraints.MaxEndpointUrlLength} characters.";
            return false;
        }
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            error = "The endpoint URL must be an absolute HTTPS URL.";
            return false;
        }
        if (parsed.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || string.IsNullOrEmpty(parsed.IdnHost))
        {
            error = "The endpoint URL must name a DNS host or an IP address.";
            return false;
        }
        var loopbackHttp = allowLoopbackHttp && IsLoopbackHost(parsed);
        if (parsed.Scheme != Uri.UriSchemeHttps && !(loopbackHttp && parsed.Scheme == Uri.UriSchemeHttp))
        {
            error = "The endpoint URL must use HTTPS.";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "The endpoint URL must not contain user information.";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.Fragment) || value.Contains('#', StringComparison.Ordinal))
        {
            error = "The endpoint URL must not contain a fragment.";
            return false;
        }
        var host = parsed.IdnHost.ToLowerInvariant();
        if (parsed.Port != HttpsPort && !loopbackHttp && !allowedHosts.Contains((host, parsed.Port)))
        {
            error = "The endpoint URL must use port 443 unless the operator's allowlist names its host and port.";
            return false;
        }
        if (IPAddress.TryParse(parsed.IdnHost.Trim('[', ']'), out var literal))
        {
            if (!IsAddressAllowed(literal, parsed))
            {
                error = "The endpoint URL's address is not a public address and is not allowlisted by the operator.";
                return false;
            }
        }
        else if (!loopbackHttp && (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)))
        {
            error = "The endpoint URL must not name a loopback host.";
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>Whether a new connection to <paramref name="destination"/> may use <paramref name="address"/>.</summary>
    public bool IsAddressAllowed(IPAddress address, Uri destination)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(destination);
        var normalized = Normalize(address);
        if (allowedNetworks.Any(network => network.Contains(normalized) || network.Contains(address)))
        {
            return true;
        }
        return Classify(address) switch
        {
            WebhookAddressClass.Public => true,
            WebhookAddressClass.Private =>
                allowedHosts.Contains((destination.IdnHost.ToLowerInvariant(), destination.Port)),
            WebhookAddressClass.Loopback => allowLoopbackHttp && IsLoopbackHost(destination),
            _ => false
        };
    }

    public static WebhookAddressClass Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = Normalize(address);
        if (normalized.AddressFamily == AddressFamily.InterNetwork)
        {
            return ClassifyIpv4(normalized.GetAddressBytes());
        }
        if (normalized.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return WebhookAddressClass.Forbidden;
        }

        var bytes = normalized.GetAddressBytes();
        if (normalized.Equals(IPAddress.IPv6Any) || normalized.Equals(IPAddress.IPv6None))
            return WebhookAddressClass.Forbidden;
        if (normalized.Equals(IPAddress.IPv6Loopback))
            return WebhookAddressClass.Loopback;
        if (normalized.Equals(Ipv6Metadata))
            return WebhookAddressClass.Forbidden;
        if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            return WebhookAddressClass.Forbidden; // fe80::/10 link-local
        if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0)
            return WebhookAddressClass.Forbidden; // fec0::/10 deprecated site-local
        if ((bytes[0] & 0xfe) == 0xfc)
            return WebhookAddressClass.Private; // fc00::/7 unique-local
        if (bytes[0] == 0xff)
            return WebhookAddressClass.Forbidden; // multicast
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            return WebhookAddressClass.Forbidden; // 2001:db8::/32 documentation
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            return WebhookAddressClass.Forbidden; // 2001::/32 Teredo
        if (bytes[0] == 0x01 && bytes[1] == 0x00 && bytes.Skip(2).Take(6).All(value => value == 0))
            return WebhookAddressClass.Forbidden; // 100::/64 discard
        return (bytes[0] & 0xe0) == 0x20
            ? WebhookAddressClass.Public // 2000::/3 global unicast
            : WebhookAddressClass.Forbidden;
    }

    /// <summary>
    /// Maps IPv4-mapped, IPv4-compatible, NAT64 (64:ff9b::/96), and 6to4 (2002::/16) forms to the IPv4 address they
    /// carry, so an alternate spelling of a private address is classified as that address.
    /// </summary>
    public static IPAddress Normalize(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }
        var bytes = address.GetAddressBytes();
        if (bytes.Take(12).All(value => value == 0)
            && !address.Equals(IPAddress.IPv6Any)
            && !address.Equals(IPAddress.IPv6Loopback))
        {
            return new IPAddress(bytes[12..16]); // ::a.b.c.d IPv4-compatible
        }
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
            && bytes.Skip(4).Take(8).All(value => value == 0))
        {
            return new IPAddress(bytes[12..16]); // 64:ff9b::/96 NAT64
        }
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            return new IPAddress(bytes[2..6]); // 2002::/16 6to4
        }
        return address;
    }

    private static WebhookAddressClass ClassifyIpv4(byte[] bytes)
    {
        var (a, b) = (bytes[0], bytes[1]);
        if (a == 0) return WebhookAddressClass.Forbidden; // 0.0.0.0/8
        if (a == 127) return WebhookAddressClass.Loopback;
        if (a == 10) return WebhookAddressClass.Private;
        if (a == 172 && b is >= 16 and <= 31) return WebhookAddressClass.Private;
        if (a == 192 && b == 168) return WebhookAddressClass.Private;
        if (a == 100 && b is >= 64 and <= 127) return WebhookAddressClass.Private; // carrier-grade NAT
        if (a == 169 && b == 254) return WebhookAddressClass.Forbidden; // link-local and metadata
        if (a == 192 && b == 0 && bytes[2] is 0 or 2) return WebhookAddressClass.Forbidden;
        if (a == 198 && b is 18 or 19) return WebhookAddressClass.Forbidden; // benchmarking
        if (a == 198 && b == 51 && bytes[2] == 100) return WebhookAddressClass.Forbidden; // TEST-NET-2
        if (a == 203 && b == 0 && bytes[2] == 113) return WebhookAddressClass.Forbidden; // TEST-NET-3
        if (a >= 224) return WebhookAddressClass.Forbidden; // multicast, reserved, broadcast
        return WebhookAddressClass.Public;
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        var host = uri.IdnHost.Trim('[', ']');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(Normalize(address)));
    }

    private static (string Host, int Port) ParseHostEntry(string? entry)
    {
        var value = entry?.Trim() ?? string.Empty;
        if (Uri.TryCreate($"https://{value}", UriKind.Absolute, out var parsed)
            && parsed.AbsolutePath == "/"
            && string.IsNullOrEmpty(parsed.UserInfo)
            && string.IsNullOrEmpty(parsed.Query)
            && parsed.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return (parsed.IdnHost.ToLowerInvariant(), parsed.Port);
        }
        throw new InvalidOperationException(
            $"{WebhookOptions.SectionName}:Network:AllowedHosts contains an invalid host or host:port entry '{entry}'.");
    }
}
