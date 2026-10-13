using System.Diagnostics.Metrics;

namespace Flowbit.Ui.Auth;

internal static class UiAuthenticationDiagnostics
{
    private static readonly Meter Meter = new("Flowbit.Ui.Authentication", "1.0");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("flowbit.ui.authentication.outcomes");

    // Callers supply fixed codes only, never provider descriptions or identity/session values.
    public static void Record(string operation, string outcome) => Outcomes.Add(1,
        new KeyValuePair<string, object?>("operation", operation),
        new KeyValuePair<string, object?>("outcome", outcome));
}
