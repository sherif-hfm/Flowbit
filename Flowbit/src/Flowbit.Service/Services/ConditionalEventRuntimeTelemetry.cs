using System.Diagnostics.Metrics;

namespace Flowbit.Service.Services;

internal static class ConditionalEventRuntimeTelemetry
{
    public const string MeterName = "Flowbit.Runtime.ConditionalEvents";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Evaluations = Meter.CreateCounter<long>(
        "flowbit.conditional.evaluations",
        description: "Conditional-event expressions evaluated.");
    private static readonly Counter<long> Triggers = Meter.CreateCounter<long>(
        "flowbit.conditional.triggers",
        description: "Conditional token activations triggered or latched.");
    private static readonly Counter<long> Rearms = Meter.CreateCounter<long>(
        "flowbit.conditional.boundary.rearms",
        description: "Conditional boundary subscriptions rearmed after becoming false.");
    private static readonly Counter<long> Suppressed = Meter.CreateCounter<long>(
        "flowbit.conditional.boundary.suppressed",
        description: "Simultaneous interrupting conditional boundary matches suppressed by deterministic selection.");
    private static readonly Counter<long> Stale = Meter.CreateCounter<long>(
        "flowbit.conditional.stale",
        description: "Stale conditional jobs or subscription transitions rejected by a durable fence.");
    private static readonly Histogram<long> Candidates = Meter.CreateHistogram<long>(
        "flowbit.conditional.candidates",
        description: "Conditional nodes selected by one variable-write batch.");
    private static readonly Histogram<double> EvaluationDuration = Meter.CreateHistogram<double>(
        "flowbit.conditional.evaluation.duration",
        "ms",
        "Time spent selecting, evaluating, and latching one conditional wave.");

    public static void RecordEvaluation(
        bool matched,
        string source,
        string eventKind = "catch") =>
        Evaluations.Add(1,
            new KeyValuePair<string, object?>("outcome", matched ? "true" : "false"),
            new KeyValuePair<string, object?>("source", source),
            new KeyValuePair<string, object?>("event_kind", eventKind));

    public static void RecordTrigger(
        string deliveryMode,
        string source,
        string eventKind = "catch",
        bool? interrupting = null) =>
        Triggers.Add(1,
            new KeyValuePair<string, object?>("delivery_mode", deliveryMode),
            new KeyValuePair<string, object?>("source", source),
            new KeyValuePair<string, object?>("event_kind", eventKind),
            new KeyValuePair<string, object?>(
                "interrupting",
                interrupting is null ? "n/a" : interrupting.Value ? "true" : "false"));

    public static void RecordRearm() => Rearms.Add(1);

    public static void RecordSuppressed(int count) => Suppressed.Add(count);

    public static void RecordStale(string source) =>
        Stale.Add(1, new KeyValuePair<string, object?>("source", source));

    public static void RecordWave(int candidateCount, TimeSpan duration)
    {
        Candidates.Record(candidateCount);
        EvaluationDuration.Record(duration.TotalMilliseconds);
    }
}
