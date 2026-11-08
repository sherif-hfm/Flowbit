using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Flowbit.Service.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Standard Webhooks v1 symmetric signatures: HMAC-SHA256 over the UTF-8 bytes of
/// <c>webhook-id</c>, a full stop, <c>webhook-timestamp</c>, another full stop, and the exact body.
/// </summary>
public static class StandardWebhookSignature
{
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(WebhookConstraints.SecretBytes);

    /// <summary>The one-time secret shown to administrators: <c>whsec_</c> followed by the Base64 key.</summary>
    public static string FormatSecret(ReadOnlySpan<byte> key) =>
        WebhookConstraints.SecretPrefix + Convert.ToBase64String(key);

    public static byte[] ParseSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var encoded = secret.StartsWith(WebhookConstraints.SecretPrefix, StringComparison.Ordinal)
            ? secret[WebhookConstraints.SecretPrefix.Length..]
            : secret;
        return Convert.FromBase64String(encoded);
    }

    /// <summary>The canonical lowercase <c>webhook-id</c> of an event.</summary>
    public static string MessageId(Guid eventId) => eventId.ToString("D", CultureInfo.InvariantCulture);

    public static string Sign(ReadOnlySpan<byte> key, string messageId, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{messageId}.{timestamp}."));
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefix.Length));
        var mac = HMACSHA256.HashData(key, payload);
        return "v1," + Convert.ToBase64String(mac);
    }

    /// <summary>
    /// The <c>webhook-signature</c> value: one space-separated <c>v1</c> entry per key, current key first. During
    /// rotation overlap the previous key adds a second entry.
    /// </summary>
    public static string Header(
        IReadOnlyList<byte[]> keys,
        string messageId,
        long timestamp,
        ReadOnlySpan<byte> body)
    {
        if (keys.Count == 0)
        {
            throw new ArgumentException("At least one signing key is required.", nameof(keys));
        }
        var entries = new string[keys.Count];
        for (var index = 0; index < keys.Count; index++)
        {
            entries[index] = Sign(keys[index], messageId, timestamp, body);
        }
        return string.Join(' ', entries);
    }
}
