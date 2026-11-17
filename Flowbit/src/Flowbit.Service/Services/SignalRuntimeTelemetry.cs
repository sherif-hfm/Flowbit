using System.Diagnostics.Metrics;

namespace Flowbit.Service.Services;

/// <summary>
/// Metadata-only signal metrics: outcomes and result codes, never signal names,
/// payloads, instance ids, or thrower identities.
/// </summary>
internal static class SignalRuntimeTelemetry
{
    public const string MeterName = "Flowbit.Runtime.Signals";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Broadcasts = Meter.CreateCounter<long>(
        "flowbit.signals.broadcasts",
        description: "Signal broadcasts thrown, tagged with the thrower kind and whether recipients were frozen.");
    private static readonly Counter<long> Deliveries = Meter.CreateCounter<long>(
        "flowbit.signals.deliveries",
        description: "Signal deliveries that finished, tagged with their status and result code.");
    private static readonly Histogram<long> Recipients = Meter.CreateHistogram<long>(
        "flowbit.signals.recipients",
        description: "Recipients a broadcast froze when it was thrown.");

    public static void RecordBroadcast(string throwerKind, int recipients)
    {
        Broadcasts.Add(1,
            new KeyValuePair<string, object?>("thrower_kind", throwerKind),
            new KeyValuePair<string, object?>("has_recipients", recipients > 0));
        Recipients.Record(recipients);
    }

    public static void RecordDelivery(string status, string? resultCode) =>
        Deliveries.Add(1,
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("result_code", resultCode ?? "none"));
}
