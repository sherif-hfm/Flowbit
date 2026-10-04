using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Flowbit.Infrastructure.Ai;
using Flowbit.Service.Ai;
using Xunit;

namespace Flowbit.Tests;

public sealed partial class WorkflowAiExecutionTests
{
    [Theory]
    [InlineData("node", "flowNodes")]
    [InlineData("flow", "sequenceFlows")]
    [InlineData("lane", "lanes")]
    [InlineData("variable", "variables")]
    public void DraftCacheKeepsDifferentEntitiesAndOffsets(string target, string collection)
    {
        var draft = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["id"] = "draft", ["name"] = "Draft",
            [collection] = Enumerable.Range(1, 2).Select(id => new { id, name = "Entity " + id })
        });
        var context = new WorkflowAiContext(new Knowledge(), Request(), true);
        void Read(int id, int offset = 0) => context.Observe(context.Read(
            JsonSerializer.SerializeToElement(new[] { new { kind = "draft", target, id, offset, count = 8 } }), draft, text => text));
        Read(1); Read(2); Read(1); Read(1, 8);
        var observations = CacheObservations(context, draft);
        Assert.Equal(3, observations.Length);
        Assert.Equal(2, observations.Count(item => item.GetProperty("resource").GetString() == "draft:" + target + ":1"));
        Assert.Single(observations, item => item.GetProperty("resource").GetString() == "draft:" + target + ":2");
        Assert.Equal(1, context.RepeatedDraftReadCount);
        Assert.Equal(0, context.DuplicateReadCount);
        Assert.Empty(context.RetainedReads());
        context.DraftChanged();
        Assert.Empty(CacheObservations(context, draft));
        Read(1);
        Assert.Equal(1, context.RepeatedDraftReadCount); // Necessary read on a new revision is not repetition.
    }

    [Fact]
    public void DraftCacheBoundsAndTypesRemainDistinctAndSanitized()
    {
        var draft = JsonSerializer.SerializeToElement(new
        {
            id = "draft", name = "Draft",
            flowNodes = Enumerable.Range(1, 20).Select(id => new { id, name = "secret-canary" }),
            lanes = new[] { new { id = 20, name = "Lane" } }
        });
        var context = new WorkflowAiContext(new Knowledge(), Request(), true);
        for (var id = 1; id <= 20; id++)
            context.Observe(context.Read(JsonSerializer.SerializeToElement(new[] { new { kind = "draft", target = "node", id } }),
                draft, text => text.Replace("secret-canary", "[removed]")));
        context.Observe(context.Read(JsonSerializer.SerializeToElement(new[] { new { kind = "draft", target = "lane", id = 20 } }), draft, text => text));
        var observations = CacheObservations(context, draft);
        Assert.Equal(18, observations.Length);
        Assert.Contains(observations, item => item.GetProperty("resource").GetString() == "draft:node:20");
        Assert.Contains(observations, item => item.GetProperty("resource").GetString() == "draft:lane:20");
        Assert.DoesNotContain("secret-canary", JsonSerializer.Serialize(observations));
    }

    [Fact]
    public async Task AuthoringTraceContainsOnlyCorrelatedMetadataAcrossRetryReadEditAndFinish()
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
        using var handler = new Handler((index, _) => index == 0
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Reply(index switch { 1 => """{"kind":"read","reads":[{"kind":"reference","resource":"rule","count":100}]}""", 2 => Edit, _ => Finish }, "current"));
        using var http = new HttpClient(handler);
        var options = Options("current"); options.RetryBaseDelayMilliseconds = 1;
        var result = await Service(new OpenCodeGoProvider(http, options), options).TurnAsync(Request(), Key, CancellationToken.None);
        Assert.Equal("proposal", result.Kind);
        var spans = stopped.ToArray();
        Assert.Single(spans, item => item.OperationName == "authoring.run");
        Assert.Equal(4, spans.Count(item => item.OperationName == "provider.attempt"));
        Assert.Contains(spans, item => item.OperationName == "provider.retry_wait");
        Assert.Contains(spans, item => item.OperationName == "authoring.recovery" && item.GetTagItem("recovery.kind")?.ToString() == "transport");
        Assert.Contains(spans, item => item.OperationName == "context.read");
        Assert.Contains(spans, item => item.OperationName == "draft.apply");
        Assert.Contains(spans, item => item.OperationName == "draft.validate");
        Assert.All(spans, item => Assert.True(item.Duration >= TimeSpan.Zero));
        var metadata = JsonSerializer.Serialize(spans.Select(item => item.TagObjects.ToArray()));
        Assert.DoesNotContain(Key, metadata);
        Assert.DoesNotContain("CANONICAL_RULE", metadata);
        Assert.DoesNotContain("Recovered", metadata);
        Assert.DoesNotContain(Request().Message, metadata);
        Assert.Contains("proposal", metadata);
    }

    private static JsonElement[] CacheObservations(WorkflowAiContext context, JsonElement draft)
    {
        using var payload = JsonDocument.Parse(context.Messages(draft, 0, "", Array.Empty<object>(), 20, 8192, text => text, false).Last().Content);
        return payload.RootElement.GetProperty("observations").EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Array).SelectMany(item => item.EnumerateArray()).Select(item => item.Clone()).ToArray();
    }
}
