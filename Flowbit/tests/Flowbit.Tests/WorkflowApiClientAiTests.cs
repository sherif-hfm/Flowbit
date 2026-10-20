extern alias FlowbitUi;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowApiClientAiTests
{
    [Fact]
    public async Task TurnsSendTheSelectedModelAndNoProviderKey()
    {
        var requests = new List<(string Path, bool KeyHeader, string Body)>();
        using var handler = new Handler(async (request, cancellationToken) =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Contains("X-Flowbit-AI-Key"),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new(HttpStatusCode.OK)
            {
                Content = request.RequestUri.AbsolutePath.EndsWith("/turn")
                    ? JsonContent.Create(new AiTurnResultDto("answer", "Who approves?", null, new(false, [], []), "snapshot", "contract"))
                    : JsonContent.Create(new AiValidationResultDto(true, [], []) { CanSave = true, CanPublish = true })
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);
        var request = new AiTurnRequestDto { ModelId = "configured-model", ConversationId = Guid.NewGuid().ToString(), Message = "Create approvals", SnapshotId = "snapshot" };
        var response = await client.SendAiTurnAsync(request);
        Assert.Equal("answer", response.Kind);
        var definition = JsonSerializer.SerializeToElement(new { id = "workflow", name = "Flow" });
        Assert.True((await client.ValidateAiWorkflowAsync(definition)).CanSave);
        Assert.Equal("/api/workflows/ai/turn", requests[0].Path);
        Assert.Equal("configured-model", JsonDocument.Parse(requests[0].Body).RootElement.GetProperty("modelId").GetString());
        Assert.False(JsonDocument.Parse(requests[0].Body).RootElement.TryGetProperty("providerId", out _));
        Assert.Equal("/api/workflows/validate", requests[1].Path);
        Assert.Equal("workflow", JsonDocument.Parse(requests[1].Body).RootElement.GetProperty("id").GetString());
        Assert.All(requests, recorded => Assert.False(recorded.KeyHeader));
    }

    [Fact]
    public async Task ModelsAreListedByIdAndNameAndErrorCodesReachTheCaller()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/workflows/ai/models"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new AiModelDto("first", "First model") }) }
            : new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"code\":\"unsupported_model\",\"error\":\"The selected AI model is not configured.\"}", Encoding.UTF8, "application/json")
            }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        Assert.Equal([new AiModelDto("first", "First model")], await client.GetAiModelsAsync());
        var error = await Assert.ThrowsAsync<FlowbitUi::Flowbit.Ui.Clients.WorkflowApiException>(() =>
            client.SendAiTurnAsync(new AiTurnRequestDto { ModelId = "removed" }));
        Assert.Equal((HttpStatusCode.BadRequest, "unsupported_model", "The selected AI model is not configured."), (error.StatusCode, error.Code, error.Message));
    }

    [Fact]
    public async Task DocumentExtractionUsesMultipartAndRetainsPageWarningsAndLanguage()
    {
        using var handler = new Handler(async (request, cancellationToken) =>
        {
            Assert.Equal("/api/workflows/ai/extract", request.RequestUri!.AbsolutePath);
            Assert.False(request.Headers.Contains("X-Flowbit-AI-Key"));
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var fields = multipart.ToDictionary(content => content.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("application/pdf", fields["file"].Headers.ContentType!.MediaType);
            Assert.Equal("%PDF-test", await fields["file"].ReadAsStringAsync(cancellationToken));
            Assert.Equal("true", await fields["forceOcr"].ReadAsStringAsync(cancellationToken));
            Assert.Equal("eng+ara", await fields["languages"].ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AiDocumentExtractionDto("requirements.pdf",
                    [new(2, "مراجعة الطلب", true, ["Review OCR accuracy."])], ["One scanned page."]))
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-test"));
        var result = await new WorkflowApiClient(http).ExtractAiDocumentAsync(input, "requirements.pdf", true, "eng+ara");
        var page = Assert.Single(result.Pages);
        Assert.Equal(2, page.Page);
        Assert.True(page.UsedOcr);
        Assert.Equal("مراجعة الطلب", page.Text);
        Assert.Single(page.Warnings);
        Assert.Single(result.Warnings);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
