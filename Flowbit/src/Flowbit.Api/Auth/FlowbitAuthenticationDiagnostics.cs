using System.Diagnostics.Metrics;

namespace Flowbit.Api.Auth;

internal static class FlowbitAuthenticationDiagnostics
{
    private static readonly Meter Meter = new("Flowbit.Api.Authentication", "1.0");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("flowbit.api.authentication.outcomes");

    // Fixed codes only. Never attach tokens, exception text, claims, or identities.
    public static void ValidToken() => Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "validated"));
    public static void InvalidToken() => Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "invalid_token"));
}
