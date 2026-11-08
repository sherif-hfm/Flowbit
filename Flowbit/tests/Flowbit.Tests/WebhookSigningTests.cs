using System.Security.Cryptography;
using System.Text;
using Flowbit.Service.Services;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Standard Webhooks v1 signatures. The golden vector is the specification's published example, so it is an
/// independent check; <see cref="IndependentVerifier"/> re-implements the receiver side from the specification
/// text rather than calling Flowbit's signer.
/// </summary>
public sealed class WebhookSigningTests
{
    // From the Standard Webhooks specification and reference test suites.
    private const string SpecSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    private const string SpecMessageId = "msg_p5jXN8AQM9LWM0D4loKWxJek";
    private const long SpecTimestamp = 1614265330;
    private const string SpecPayload = "{\"test\": 2432232314}";
    private const string SpecSignature = "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=";

    [Fact]
    public void SignatureMatchesThePublishedStandardWebhooksVector()
    {
        var key = StandardWebhookSignature.ParseSecret(SpecSecret);
        var signature = StandardWebhookSignature.Sign(key, SpecMessageId, SpecTimestamp, Encoding.UTF8.GetBytes(SpecPayload));
        Assert.Equal(SpecSignature, signature);
        Assert.True(IndependentVerifier.Verify(SpecSecret, SpecMessageId, SpecTimestamp.ToString(), Encoding.UTF8.GetBytes(SpecPayload), SpecSignature));
    }

    [Fact]
    public void FlowbitGoldenVectorIsStable()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var secret = StandardWebhookSignature.FormatSecret(key);
        Assert.Equal("whsec_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", secret);
        var body = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"eventId\":\"019a5e2d-6d40-7000-8000-000000000001\",\"type\":\"webhook.test\",\"timestamp\":\"2026-11-07T09:00:00.000000Z\",\"workflowKey\":null,\"workflowDefinitionId\":null,\"workflowVersion\":null,\"instanceId\":null,\"data\":{\"subscriptionId\":\"019a5e2d-6d40-7000-8000-0000000000aa\"}}");
        var header = StandardWebhookSignature.Header([key], "019a5e2d-6d40-7000-8000-000000000001", 1794042000, body);
        // Fixed golden value; computed independently with .NET HMACSHA256 outside the signer.
        Assert.Equal("v1,wcywvqJxEx7/57XYECrKs95IPg3NrnsPEvKEMScKEl0=", header);
        Assert.Equal(ExpectedHmac(key, "019a5e2d-6d40-7000-8000-000000000001", 1794042000, body), header);
        Assert.True(IndependentVerifier.Verify(secret, "019a5e2d-6d40-7000-8000-000000000001", "1794042000", body, header));
    }

    [Fact]
    public void RotationOverlapSendsBothSignaturesSeparatedByASpace()
    {
        var current = RandomNumberGenerator.GetBytes(32);
        var previous = RandomNumberGenerator.GetBytes(32);
        var body = Encoding.UTF8.GetBytes("{\"a\":1}");
        var header = StandardWebhookSignature.Header([current, previous], "evt", 1700000000, body);
        var entries = header.Split(' ');
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry => Assert.StartsWith("v1,", entry, StringComparison.Ordinal));
        Assert.True(IndependentVerifier.Verify(StandardWebhookSignature.FormatSecret(previous), "evt", "1700000000", body, header));
        Assert.True(IndependentVerifier.Verify(StandardWebhookSignature.FormatSecret(current), "evt", "1700000000", body, header));
        Assert.False(IndependentVerifier.Verify(StandardWebhookSignature.FormatSecret(RandomNumberGenerator.GetBytes(32)), "evt", "1700000000", body, header));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("id")]
    [InlineData("timestamp")]
    [InlineData("utf8")]
    public void TamperingInvalidatesTheSignature(string tamper)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var body = Encoding.UTF8.GetBytes("{\"name\":\"Zoë\"}");
        var header = StandardWebhookSignature.Header([key], "evt-1", 1700000000, body);
        var secret = StandardWebhookSignature.FormatSecret(key);
        var (id, timestamp, payload) = tamper switch
        {
            "body" => ("evt-1", "1700000000", Encoding.UTF8.GetBytes("{\"name\":\"Zoe\"}")),
            "id" => ("evt-2", "1700000000", body),
            "timestamp" => ("evt-1", "1700000001", body),
            // A receiver that reserializes or re-encodes instead of verifying raw bytes fails.
            _ => ("evt-1", "1700000000", Encoding.Latin1.GetBytes("{\"name\":\"Zoë\"}"))
        };
        Assert.False(IndependentVerifier.Verify(secret, id, timestamp, payload, header));
    }

    [Fact]
    public void SecretsRoundTripAndKeysAre256Bits()
    {
        var key = StandardWebhookSignature.GenerateKey();
        Assert.Equal(32, key.Length);
        Assert.Equal(key, StandardWebhookSignature.ParseSecret(StandardWebhookSignature.FormatSecret(key)));
        Assert.Equal("019a5e2d-6d40-7000-8000-000000000001",
            StandardWebhookSignature.MessageId(Guid.Parse("019A5E2D-6D40-7000-8000-000000000001")));
    }

    private static string ExpectedHmac(byte[] key, string id, long timestamp, byte[] body)
    {
        using var hmac = new HMACSHA256(key);
        var content = Encoding.UTF8.GetBytes($"{id}.{timestamp}.").Concat(body).ToArray();
        return "v1," + Convert.ToBase64String(hmac.ComputeHash(content));
    }

    /// <summary>A receiver-side verifier written from the specification: constant-time comparison of any v1 entry.</summary>
    internal static class IndependentVerifier
    {
        public static bool Verify(string secret, string id, string timestamp, byte[] body, string signatureHeader)
        {
            var key = Convert.FromBase64String(secret.Split('_', 2)[1]);
            var signed = new List<byte>(Encoding.UTF8.GetBytes(id + "." + timestamp + "."));
            signed.AddRange(body);
            var expected = new HMACSHA256(key).ComputeHash(signed.ToArray());
            foreach (var entry in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split(',', 2);
                if (parts.Length == 2 && parts[0] == "v1"
                    && CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(parts[1]), expected))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
