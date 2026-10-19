using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Flowbit.Service.Ai;

/// <summary>Metadata-only instrumentation. Never attach prompts, source, workflow, response, path or exception text.</summary>
internal static class WorkflowAiTelemetry
{
    public static readonly ActivitySource Activities = new("Flowbit.Ai.Authoring");
    public static readonly Meter Meter = new("Flowbit.Ai.Authoring");
    public static readonly Counter<long> Calls = Meter.CreateCounter<long>("flowbit.ai.provider.calls");
    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("flowbit.ai.tool.calls");
    public static readonly Counter<long> Nudges = Meter.CreateCounter<long>("flowbit.ai.nudges");
    public static readonly Histogram<double> RunSeconds = Meter.CreateHistogram<double>("flowbit.ai.run.seconds");
    public static readonly Histogram<double> CallSeconds = Meter.CreateHistogram<double>("flowbit.ai.provider.seconds");

    public static Activity? Start(string operation) => Activities.StartActivity(operation, ActivityKind.Internal);
}
