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
    public async Task AiKeyIsRequestLocalAndNeverIncludedInJsonOrSubsequentValidation()
    {
        var requests = new List<(string Path, string? Key, string Body)>();
        using var handler = new Handler(async (request, cancellationToken) =>
        {
            requests.Add((request.RequestUri!.AbsolutePath,
                request.Headers.TryGetValues("X-Flowbit-AI-Key", out var values) ? values.Single() : null,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new(HttpStatusCode.OK)
            {
                Content = request.RequestUri.AbsolutePath.EndsWith("/turn")
                    ? JsonContent.Create(new AiTurnResultDto("clarification", "Who approves?", ["Which role?"], null,
                        [], [], [], new(false, [], []), "snapshot", "contract"))
                    : JsonContent.Create(new AiValidationResultDto(true, [], []) { CanSave = true, CanPublish = true })
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);
        var request = new AiTurnRequestDto { ConversationId = Guid.NewGuid().ToString(), Message = "Create approvals", SnapshotId = "snapshot" };
        var response = await client.SendAiTurnAsync(request, "synthetic-first-key");
        Assert.Equal("clarification", response.Kind);
        await client.SendAiTurnAsync(request, "synthetic-second-key");
        var definition = JsonSerializer.SerializeToElement(new { id = "workflow", name = "Flow" });
        Assert.True((await client.ValidateAiWorkflowAsync(definition)).CanSave);
        Assert.Equal("synthetic-first-key", requests[0].Key);
        Assert.Equal("synthetic-second-key", requests[1].Key);
        Assert.Null(requests[2].Key);
        Assert.Equal("/api/workflows/validate", requests[2].Path);
        Assert.Equal("workflow", JsonDocument.Parse(requests[2].Body).RootElement.GetProperty("id").GetString());
        Assert.All(requests, recorded => Assert.DoesNotContain("synthetic-", recorded.Body));
        Assert.False(http.DefaultRequestHeaders.Contains("X-Flowbit-AI-Key"));
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
