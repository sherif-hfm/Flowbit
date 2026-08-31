extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowApiClientReactivationTests
{
    [Fact]
    public void SharedContracts_ExposeTargetsIssuesAndOptimisticConcurrency()
    {
        Assert.Equal(
            new[]
            {
                "InstanceId",
                "WorkflowId",
                "Status",
                "CanReactivate",
                "Targets",
                "Blockers",
                "Warnings",
                "ExpectedUpdatedAt"
            },
            typeof(InstanceReactivationPreviewDto).GetProperties().Select(property => property.Name));

        Assert.Equal(
            new[] { "TargetNodeId", "ExpectedWorkflowId", "ExpectedUpdatedAt", "Reason" },
            typeof(ReactivateInstanceRequest).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task Preview_GetsReactivationResourceAndDeserializesTargets()
    {
        var now = DateTimeOffset.Parse("2026-08-30T12:30:00Z");
        var expected = new InstanceReactivationPreviewDto(
            41,
            11,
            "completed",
            true,
            [new(7, "Manager review", "manager-review", now.AddMinutes(-5))],
            [],
            [new("side_effects_retained", "Prior side effects and variable values are retained.")],
            now);
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(expected)
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var actual = await client.PreviewInstanceReactivationAsync(41);

        Assert.NotNull(actual);
        Assert.True(actual.CanReactivate);
        Assert.Equal(7, Assert.Single(actual.Targets).NodeId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/instances/41/reactivation", request.Path);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task Reactivate_PostsTargetConcurrencyFenceAndReason()
    {
        var expectedUpdatedAt = DateTimeOffset.Parse("2026-08-30T13:45:12.345Z");
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        await client.ReactivateInstanceAsync(
            72,
            new ReactivateInstanceRequest(
                9,
                17,
                expectedUpdatedAt,
                "Customer requested another review"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/instances/72/reactivation", request.Path);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal(9, root.GetProperty("targetNodeId").GetInt32());
        Assert.Equal(17, root.GetProperty("expectedWorkflowId").GetInt64());
        Assert.Equal(expectedUpdatedAt, root.GetProperty("expectedUpdatedAt").GetDateTimeOffset());
        Assert.Equal("Customer requested another review", root.GetProperty("reason").GetString());
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.PathAndQuery ?? string.Empty,
                body));
            return responseFactory(request);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Body);
}
