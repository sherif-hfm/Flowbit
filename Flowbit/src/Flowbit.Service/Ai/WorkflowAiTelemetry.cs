using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Flowbit.Service.Ai;

/// <summary>Metadata-only instrumentation. Never attach source, draft, response, or exception text.</summary>
internal static class WorkflowAiTelemetry
{
    public static readonly ActivitySource Activities = new("Flowbit.Ai.Authoring");
    public static readonly Meter Meter = new("Flowbit.Ai.Authoring");
    public static readonly Counter<long> Calls = Meter.CreateCounter<long>("flowbit.ai.provider.calls");
    public static readonly Counter<long> Recoveries = Meter.CreateCounter<long>("flowbit.ai.recoveries");
    public static readonly Counter<long> DraftRepeats = Meter.CreateCounter<long>("flowbit.ai.reads.draft_repeated");
    public static readonly Counter<long> RestoredReads = Meter.CreateCounter<long>("flowbit.ai.reads.restored");
    public static readonly Histogram<double> RunSeconds = Meter.CreateHistogram<double>("flowbit.ai.run.seconds");
    public static readonly Histogram<double> CallSeconds = Meter.CreateHistogram<double>("flowbit.ai.provider.seconds");

    public static Activity? Start(string operation) => Activities.StartActivity(operation, ActivityKind.Internal);

    public static void Recovery(string kind, string purpose)
    {
        Recoveries.Add(1, new KeyValuePair<string, object?>("kind", kind));
        using var activity = Start("authoring.recovery");
        activity?.SetTag("recovery.kind", kind);
        activity?.SetTag("call.purpose", purpose);
    }
}
