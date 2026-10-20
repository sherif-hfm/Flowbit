extern alias FlowbitUi;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowApiClientAiModelsTests
{
    [Fact]
    public async Task ModelRequestsUseTheAdminRoutesAndSendAKeyOnlyWhenOneIsGiven()
    {
        var model = new AiModelSettingDto("id/1", "Flash", "https://opencode.example/zen/v1/", "glm-5.3-flash", null, 2,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var requests = new List<(HttpMethod Method, string PathAndQuery, string? Body)>();
        using var handler = new Handler(async (request, cancellationToken) =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.Method == HttpMethod.Get ? JsonContent.Create(new[] { model }) : JsonContent.Create(model)
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        Assert.Equal([model], await client.GetAiModelSettingsAsync());
        Assert.Equal(model, await client.CreateAiModelAsync(
            new CreateAiModelRequest("Flash", "https://opencode.example/zen/v1/", "glm-5.3-flash", "high", "sk-client-test-key-0123456789")));
        Assert.Equal(model, await client.UpdateAiModelAsync("id/1",
            new UpdateAiModelRequest("Flash", "https://opencode.example/zen/v1/", "glm-5.3-flash", null, null, 2)));
        await client.DeleteAiModelAsync("id/1", 3);

        Assert.Equal((HttpMethod.Get, "/api/ai-models"), (requests[0].Method, requests[0].PathAndQuery));
        Assert.Equal((HttpMethod.Post, "/api/ai-models"), (requests[1].Method, requests[1].PathAndQuery));
        Assert.Equal("sk-client-test-key-0123456789", JsonDocument.Parse(requests[1].Body!).RootElement.GetProperty("apiKey").GetString());
        Assert.Equal((HttpMethod.Put, "/api/ai-models/id%2F1"), (requests[2].Method, requests[2].PathAndQuery));
        using (var update = JsonDocument.Parse(requests[2].Body!))
        {
            Assert.Equal(JsonValueKind.Null, update.RootElement.GetProperty("apiKey").ValueKind);
            Assert.Equal(2, update.RootElement.GetProperty("expectedRevision").GetInt64());
        }
        Assert.Equal((HttpMethod.Delete, "/api/ai-models/id%2F1?expectedRevision=3"), (requests[3].Method, requests[3].PathAndQuery));
        Assert.False(http.DefaultRequestHeaders.Contains("X-Flowbit-AI-Key"));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
