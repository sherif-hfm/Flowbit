using System.Diagnostics.Metrics;

namespace Flowbit.Service.Services;

/// <summary>Metadata-only call metrics: stable notes and codes, never ids, names, or values.</summary>
internal static class CallActivityRuntimeTelemetry
{
    public const string MeterName = "Flowbit.Runtime.CallActivities";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Events = Meter.CreateCounter<long>(
        "flowbit.call.events",
        description: "Call invocation lifecycle events, tagged with the history note and any failure code.");
    private static readonly Counter<long> FenceStops = Meter.CreateCounter<long>(
        "flowbit.call.fence_stops",
        description: "Durable jobs stopped because a call above their instance was cancelled.");
    private static readonly Histogram<double> StartQueueAge = Meter.CreateHistogram<double>(
        "flowbit.call.start_queue_age",
        "s",
        "How long a call-start job waited before its child started.");
    private static readonly Histogram<double> CancelAge = Meter.CreateHistogram<double>(
        "flowbit.call.cancel_age",
        "s",
        "How long a call-cancel job waited before it stopped a running child.");

    public static void RecordEvent(string note, string? failureCode) =>
        Events.Add(1,
            new KeyValuePair<string, object?>("event", note),
            new KeyValuePair<string, object?>("failure_code", failureCode ?? "none"));

    public static void RecordFenceStop() => FenceStops.Add(1);

    public static void RecordStartQueueAge(TimeSpan age) => StartQueueAge.Record(age.TotalSeconds);

    public static void RecordCancelAge(TimeSpan age) => CancelAge.Record(age.TotalSeconds);
}
