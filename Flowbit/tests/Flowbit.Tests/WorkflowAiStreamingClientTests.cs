extern alias FlowbitUi;
using System.Net;
using System.Text;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using WorkflowApiException = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiException;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowAiStreamingClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task StreamsCheckpointsAndResultWithRequestLocalCredentialsAndFrozenInputs()
    {
        var checkpoint = new AiCheckpointDto { Draft = JsonSerializer.SerializeToElement(new { id = "workflow" }), Revision = 2 };
        var result = Result("paused") with { Checkpoint = checkpoint };
        using var handler = new Handler(async request =>
        {
            Assert.Equal("/api/workflows/ai/turn/stream", request.RequestUri!.AbsolutePath);
            Assert.Equal("synthetic-session-key", Assert.Single(request.Headers.GetValues("X-Flowbit-AI-Key")));
            var body = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("synthetic-session-key", body);
            Assert.Contains("Original requirements", body);
            Assert.Contains("\"revision\":2", body);
            return StreamResponse(Lines(
                new() { RunId = "run", Sequence = 1, Type = "progress", Stage = "planning" },
                new() { RunId = "run", Sequence = 2, Type = "checkpoint", Checkpoint = checkpoint },
                new() { RunId = "run", Sequence = 3, Type = "paused", Result = result }));
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://flowbit.test") };
        var received = new List<AiRunEventDto>();
        var response = await new WorkflowApiClient(http).SendAiTurnStreamingAsync(
            new() { Message = "Original requirements", Checkpoint = checkpoint }, "synthetic-session-key",
            frame => { received.Add(frame); return Task.CompletedTask; });
        Assert.Equal(new[] { "progress", "checkpoint", "paused" }, received.Select(frame => frame.Type));
        Assert.Equal("paused", response.Kind);
        Assert.Equal(2, response.Checkpoint!.Revision);
        Assert.False(http.DefaultRequestHeaders.Contains("X-Flowbit-AI-Key"));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("run")]
    [InlineData("version")]
    [InlineData("missing-result")]
    [InlineData("missing-checkpoint")]
    [InlineData("unknown")]
    public async Task RejectsInvalidEventBeforeCallingConsumer(string failure)
    {
        var invalid = new AiRunEventDto { RunId = "run", Sequence = 2, Type = "progress" };
        invalid = failure switch
        {
            "duplicate" => invalid with { Sequence = 1 },
            "run" => invalid with { RunId = "different" },
            "version" => invalid with { Version = 2 },
            "missing-result" => invalid with { Type = "result" },
            "missing-checkpoint" => invalid with { Type = "checkpoint" },
            _ => invalid with { Type = "unknown" }
        };
        using var http = Client(Lines(new() { RunId = "run", Sequence = 1 }, invalid));
        var received = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowApiClient(http).SendAiTurnStreamingAsync(new(), "key",
            _ => { received++; return Task.CompletedTask; }));
        Assert.Equal(1, received);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EofWithoutTerminalRejectsPartialCheckpoint(bool partialCheckpoint)
    {
        var content = Lines(new AiRunEventDto { RunId = "run", Sequence = 1, Type = "progress" });
        if (partialCheckpoint) content += "{\"type\":\"checkpoint\",\"checkpoint\":{\"draft\":";
        using var http = Client(content);
        var received = new List<AiRunEventDto>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowApiClient(http).SendAiTurnStreamingAsync(new(), "key",
            frame => { received.Add(frame); return Task.CompletedTask; }));
        Assert.Contains("before a final result", error.Message);
        Assert.Single(received);
    }

    [Fact]
    public async Task ErrorFrameKeepsEarlierCheckpointAvailable()
    {
        using var http = Client(Lines(
            new() { RunId = "run", Sequence = 1, Type = "checkpoint", Checkpoint = new() { Draft = JsonSerializer.SerializeToElement(new { id = "workflow" }) } },
            new() { RunId = "run", Sequence = 2, Type = "error", Code = "provider_quota", Message = "Provider quota exhausted." }));
        AiCheckpointDto? checkpoint = null;
        var error = await Assert.ThrowsAsync<WorkflowApiException>(() => new WorkflowApiClient(http).SendAiTurnStreamingAsync(new(), "key",
            frame => { checkpoint = frame.Checkpoint ?? checkpoint; return Task.CompletedTask; }));
        Assert.NotNull(checkpoint);
        Assert.Contains("quota exhausted", error.Message);
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforeConsumer()
    {
        using var http = Client(new string(' ', 4 * 1024 * 1024 + 1));
        var calls = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowApiClient(http).SendAiTurnStreamingAsync(new(), "key",
            _ => { calls++; return Task.CompletedTask; }));
        Assert.Contains("size limit", error.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TooManyFramesAreBounded()
    {
        using var http = Client(Lines(Enumerable.Range(1, 257).Select(i => new AiRunEventDto { RunId = "run", Sequence = i }).ToArray()));
        var count = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowApiClient(http).SendAiTurnStreamingAsync(new(), "key",
            _ => { count++; return Task.CompletedTask; }));
        Assert.Contains("event limit", error.Message);
        Assert.Equal(256, count);
    }

    [Fact]
    public async Task CancellationAfterProgressStopsQueuedEvents()
    {
        using var http = Client(Lines(new() { RunId = "run", Sequence = 1 }, new() { RunId = "run", Sequence = 2, Type = "result", Result = Result() }));
        using var cts = new CancellationTokenSource();
        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkflowApiClient(http).SendAiTurnStreamingAsync(new(), "key",
            _ => { count++; cts.Cancel(); return Task.CompletedTask; }, cts.Token));
        Assert.Equal(1, count);
    }

    private static AiTurnResultDto Result(string kind = "clarification") => new(kind, "Working", [], null, [], [], [], new(false, [], []), "snapshot", "contract");
    private static string Lines(params AiRunEventDto[] events) => string.Join('\n', events.Select(frame => JsonSerializer.Serialize(frame, JsonOptions))) + "\n";
    private static HttpResponseMessage StreamResponse(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/x-ndjson") };
    private static HttpClient Client(string content) => new(new Handler(_ => Task.FromResult(StreamResponse(content)))) { BaseAddress = new("https://flowbit.test") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
