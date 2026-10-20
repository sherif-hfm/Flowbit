using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The Flowbit.Ai.Authoring spans and metrics carry metadata only: never prompts, workflow text, paths, keys or credentials.</summary>
public sealed class WorkflowAiObservabilityTests
{
    [Fact]
    public async Task AuthoringTraceContainsOnlyMetadataAcrossToolCallsANudgeAndTheProposal()
    {
        using var parent = new Activity("test-observability").Start();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Flowbit.Ai.Authoring",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { if (activity.TraceId == parent.TraceId) stopped.Enqueue(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        var measurements = new ConcurrentQueue<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var meters = new MeterListener();
        meters.InstrumentPublished = (instrument, listener) => { if (instrument.Meter.Name == "Flowbit.Ai.Authoring") listener.EnableMeasurementEvents(instrument); };
        meters.SetMeasurementEventCallback<long>((instrument, _, tags, _) => measurements.Enqueue((instrument.Name, tags.ToArray())));
        meters.SetMeasurementEventCallback<double>((instrument, _, tags, _) => measurements.Enqueue((instrument.Name, tags.ToArray())));
        meters.Start();

        var example = ExampleWorkflowData.Read("examples/basics/02-two-step-approval.json");
        var broken = example.Replace("\"name\": \"approval1\"", "\"name\": \"SECRET-CANARY\", \"colour\": 1", StringComparison.Ordinal);
        var handler = new ScriptedHandler(Completions.Tools(("get_file", new { path = "node-reference.md" })), Completions.Save("workflow.json", broken),
            Completions.Final("Saved."), Completions.Save("workflow.json", example), Completions.Final("Fixed."));
        var result = await AgentHarness.Conversation(handler).RunAsync(AgentHarness.Request("Confidential requirement text."), AgentHarness.Connection, null, null, CancellationToken.None);

        Assert.Equal("proposal", result.Kind);
        var spans = stopped.ToArray();
        Assert.Single(spans, item => item.OperationName == "authoring.run");
        Assert.Equal(5, spans.Count(item => item.OperationName == "provider.attempt"));
        Assert.Contains(spans, item => item.OperationName == "workflow.check" && item.GetTagItem("errors") is > 0);
        Assert.Contains(spans, item => item.OperationName == "workflow.check" && item.GetTagItem("valid") is true);
        var run = spans.Single(item => item.OperationName == "authoring.run");
        Assert.Equal(("proposal", 5, 1), (run.GetTagItem("outcome"), run.GetTagItem("requests"), run.GetTagItem("nudges")));
        meters.RecordObservableInstruments();
        Assert.Contains(measurements, item => item.Name == "flowbit.ai.tool.calls" && item.Tags.Any(tag => tag.Key == "tool" && (string?)tag.Value == "save_append_file"));
        Assert.Contains(measurements, item => item.Name == "flowbit.ai.nudges");

        var metadata = JsonSerializer.Serialize(spans.Select(item => item.TagObjects.ToArray())) + JsonSerializer.Serialize(measurements.Select(item => item.Tags));
        foreach (var secret in new[] { AgentHarness.Key, AgentHarness.Connection.Endpoint, AgentHarness.Connection.Name, "Confidential requirement text", "SECRET-CANARY", "node-reference.md", "workflow.json", "Saved.", "Fixed." })
            Assert.DoesNotContain(secret, metadata);
    }
}
