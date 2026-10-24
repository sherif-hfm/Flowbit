using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Ai;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Flowbit.Tests;

[Collection(InstanceDetailApiContractCollection.Name)]
public sealed class WorkflowAiEndpointTests
{
    [Fact]
    public async Task ConversationStateAndRunSummaryAppearInProductionOpenApi()
    {
        await using var factory = new AiFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync();
        var operation = document.Paths["/api/workflows/ai/turn"].Operations![HttpMethod.Post];
        Assert.Contains("200", operation.Responses!.Keys);
        var schemas = document.Components!.Schemas!;
        Assert.Contains("state", schemas["AiTurnRequestDto"].Properties!.Keys);
        Assert.Contains("continue", schemas["AiTurnRequestDto"].Properties!.Keys);
        Assert.Contains("modelId", schemas["AiTurnRequestDto"].Properties!.Keys);
        Assert.DoesNotContain("providerId", schemas["AiTurnRequestDto"].Properties!.Keys);
        Assert.DoesNotContain("history", schemas["AiTurnRequestDto"].Properties!.Keys);
        Assert.DoesNotContain("checkpoint", schemas["AiTurnRequestDto"].Properties!.Keys);
        Assert.Contains("/api/workflows/ai/models", document.Paths.Keys);
        Assert.DoesNotContain("/api/workflows/ai/providers", document.Paths.Keys);
        Assert.DoesNotContain(operation.Parameters ?? [], parameter => parameter.Name == "X-Flowbit-AI-Key");
        Assert.DoesNotContain("apiKey", schemas["AiModelSettingDto"].Properties!.Keys);
        Assert.Contains("openQuestions", schemas["AiTurnResultDto"].Properties!.Keys);
        Assert.DoesNotContain("requirementsReview", schemas["AiTurnResultDto"].Properties!.Keys);
        Assert.Contains("canContinue", schemas["AiConversationStateDto"].Properties!.Keys);
        Assert.Contains("cachedInputTokens", schemas["AiRunSummaryDto"].Properties!.Keys);
        Assert.Equal(0, factory.Service.Calls);
    }

    [Theory]
    [InlineData("/api/workflows/ai/turn", "\"history\":[{\"role\":\"user\",\"content\":\"Earlier\"}]", HttpStatusCode.BadRequest, "history_unsupported")]
    [InlineData("/api/workflows/ai/turn/stream", "\"history\":[{\"role\":\"user\",\"content\":\"Earlier\"}]", HttpStatusCode.BadRequest, "history_unsupported")]
    [InlineData("/api/workflows/ai/turn", "\"checkpoint\":{\"version\":2}", HttpStatusCode.Conflict, "checkpoint_configuration_changed")]
    [InlineData("/api/workflows/ai/turn/stream", "\"checkpoint\":{\"version\":2}", HttpStatusCode.Conflict, "checkpoint_configuration_changed")]
    public async Task FieldsOfTheReplacedEngineAreRefusedBeforeGeneration(string path, string field, HttpStatusCode expected, string code)
    {
        await using var factory = new AiFactory();
        using var client = factory.CreateClient();
        using var request = ApiTestAuth.Authorize(new(HttpMethod.Post, path)
        { Content = new StringContent("{\"message\":\"Build a review\",\"snapshotId\":\"revision-1\"," + field + "}", Encoding.UTF8, "application/json") });
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Service.Calls);
    }

    [Theory]
    [InlineData("/api/workflows/ai/models")]
    [InlineData("/api/workflows/ai/skill")]
    public async Task AuthoringRequiresAuthenticationAndTheConfiguredAuthorRole(string path)
    {
        await using var factory = new AiFactory();
        using var client = factory.CreateClient();
        using var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var request = ApiTestAuth.Authorize(new(HttpMethod.Get, path), "reader", "reviewer");
        using var denied = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, factory.Service.Calls);
    }

    [Fact]
    public async Task ReadOnlyValidationDoesNotRequireAnAiKey()
    {
        await using var factory = new AiFactory();
        using var client = factory.CreateClient();
        using var request = ApiTestAuth.Authorize(new(HttpMethod.Post, "/api/workflows/validate")
        { Content = JsonContent.Create(new { id = "validation-only" }) });
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AiValidationResultDto>();
        Assert.True(result!.IsValid);
        Assert.True(result.CanSave);
        Assert.False(result.CanPublish);
        Assert.Equal("validation-only", factory.Service.LastDefinition!.Value.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("/api/workflows/ai/turn")]
    [InlineData("/api/workflows/ai/turn/stream")]
    public async Task TurnForwardsTheSelectedModelWithoutACallerKey(string path)
    {
        await using var factory = new AiFactory();
        using var client = factory.CreateClient();
        using var request = ApiTestAuth.Authorize(new(HttpMethod.Post, path)
        { Content = JsonContent.Create(new AiTurnRequestDto { ModelId = "configured-model", Message = "Build a review", SnapshotId = "revision-1" }) });
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Equal("configured-model", factory.Service.LastModelId);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("/api/workflows/ai/turn")]
    [InlineData("/api/workflows/ai/turn/stream")]
    public async Task TurnRefusesAProviderKeyHeaderBeforeGeneration(string path)
    {
        await using var factory = new AiFactory();
        using var client = factory.CreateClient();
        using var request = ApiTestAuth.Authorize(new(HttpMethod.Post, path)
        { Content = JsonContent.Create(new AiTurnRequestDto { ModelId = "configured-model", Message = "Build a review", SnapshotId = "revision-1" }) });
        request.Headers.Add("X-Flowbit-AI-Key", "synthetic-caller-key");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("api_key_not_accepted", body);
        Assert.DoesNotContain("synthetic-caller-key", body);
        Assert.Equal(0, factory.Service.Calls);
    }

    [Theory]
    [InlineData("/api/workflows/ai/turn", "text/plain", "{}", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("/api/workflows/ai/turn", "application/json", "{invalid", HttpStatusCode.BadRequest)]
    [InlineData("/api/workflows/ai/turn/stream", "text/plain", "{}", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("/api/workflows/ai/turn/stream", "application/json", "{invalid", HttpStatusCode.BadRequest)]
    public async Task InvalidRequestNeverReachesGeneration(string path, string contentType, string body, HttpStatusCode expected)
    {
        await using var factory = new AiFactory();
        using var client = factory.CreateClient();
        using var request = ApiTestAuth.Authorize(new(HttpMethod.Post, path)
        { Content = new StringContent(body, Encoding.UTF8, contentType) });
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, factory.Service.Calls);
    }

    [Fact]
    public async Task SkillDownloadsAsZipWithoutProviderCall()
    {
        await using var factory = new AiFactory();
        using var client = factory.CreateClient();
        using var request = ApiTestAuth.Authorize(new(HttpMethod.Get, "/api/workflows/ai/skill"));
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal("flowbit-authoring.zip", (disposition.FileNameStar ?? disposition.FileName)?.Trim('"'));
        using var data = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new System.IO.Compression.ZipArchive(data);
        Assert.NotNull(zip.GetEntry("flowbit-authoring/SKILL.md"));
        Assert.NotNull(zip.GetEntry("flowbit-authoring/references/workflow.schema.json"));
        Assert.NotNull(zip.GetEntry("flowbit-authoring/manifest.json"));
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith("flowbit-authoring/references/docs/plans/", StringComparison.Ordinal));
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.StartsWith("flowbit-authoring/references/docs/enhancements/", StringComparison.Ordinal));
        Assert.Equal(0, factory.Service.Calls);
    }

    [Fact]
    public async Task PdfUploadUsesRealExtractorWithoutAnAiKey()
    {
        await using var factory = new AiFactory();
        using var client = factory.CreateClient();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(AiDocumentExtractionTests.Pdf("Approve this purchase request.")), "file", "requirements.pdf");
        form.Add(new StringContent("eng+ara"), "languages");
        using var request = ApiTestAuth.Authorize(new(HttpMethod.Post, "/api/workflows/ai/extract") { Content = form });
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<AiDocumentExtractionDto>();
        Assert.Contains("purchase", Assert.Single(document!.Pages).Text);
        Assert.Equal(0, factory.Service.Calls);
    }

    [Theory]
    [InlineData(false, "/api/workflows/ai/turn", HttpStatusCode.OK)]
    [InlineData(true, "/api/workflows/ai/turn", HttpStatusCode.Forbidden)]
    [InlineData(false, "/api/workflows/ai/turn/stream", HttpStatusCode.OK)]
    [InlineData(true, "/api/workflows/ai/turn/stream", HttpStatusCode.Forbidden)]
    public async Task CatalogContextRequiresItsOwnReadPermission(bool selectCatalog, string path, HttpStatusCode expected)
    {
        await using var factory = new AiFactory("catalog-admin");
        using var client = factory.CreateClient();
        using var request = ApiTestAuth.Authorize(new(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new AiTurnRequestDto
            {
                Message = "Create a review", SnapshotId = "revision-1",
                SharedVariableKeys = selectCatalog ? ["finance.limit"] : []
            })
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(selectCatalog ? 0 : 1, factory.Service.Calls);
    }

    [Fact]
    public async Task StreamingProgressIsFlushedBeforeRunFinishes()
    {
        await using var factory = new AiFactory();
        factory.Service.FinishGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = factory.CreateClient();
        using var request = StreamRequest();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("\"type\":\"progress\"", first);
        factory.Service.FinishGate.SetResult();
        var remaining = await reader.ReadToEndAsync();
        Assert.Contains("\"type\":\"result\"", remaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingFailureUsesHttpBeforeProgressAndTerminalEventAfterProgress(bool afterProgress)
    {
        await using var factory = new AiFactory();
        factory.Service.FailBeforeProgress = !afterProgress;
        factory.Service.FailAfterProgress = afterProgress;
        using var client = factory.CreateClient();
        using var request = StreamRequest();
        using var response = await client.SendAsync(request);
        Assert.Equal(afterProgress ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (afterProgress)
        {
            var frames = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<AiRunEventDto>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
            Assert.Equal(new[] { "progress", "error" }, frames.Select(frame => frame.Type));
            Assert.Equal(new long[] { 1, 2 }, frames.Select(frame => frame.Sequence));
            Assert.All(frames, frame => Assert.Equal("test-run", frame.RunId));
            Assert.Equal("provider_rate_limited", frames[1].Code);
        }
        else Assert.Contains("provider_rate_limited", body);
    }

    private static HttpRequestMessage StreamRequest()
    {
        return ApiTestAuth.Authorize(new(HttpMethod.Post, "/api/workflows/ai/turn/stream")
        { Content = JsonContent.Create(new AiTurnRequestDto { Message = "Build a review", SnapshotId = "revision-1" }) });
    }

    private sealed class AiFactory(string? catalogRole = null) : WebApplicationFactory<Program>
    {
        public FakeAuthoring Service { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            ApiTestAuthentication.Configure(builder);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Flowbit"] = "Host=127.0.0.1;Port=1;Database=unused;Username=test;Password=test;Timeout=1" }));
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.ValidIssuer = ApiTestAuth.Issuer;
                    options.TokenValidationParameters.ValidAudience = ApiTestAuth.Audience;
                    options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiTestAuth.Key));
                });
                services.RemoveAll<IEngineSettingsService>();
                services.AddSingleton<IEngineSettingsService>(new EmptySettings(catalogRole));
                services.RemoveAll<IWorkflowAiAuthoringService>();
                services.AddSingleton<IWorkflowAiAuthoringService>(Service);
                services.RemoveAll<IWorkflowDefinitionService>();
                services.AddScoped<IWorkflowDefinitionService>(_ => throw new InvalidOperationException("Authoring must not resolve a writer."));
            });
        }
    }

    private sealed class EmptySettings(string? catalogRole) : IEngineSettingsService
    {
        public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken ct) => Task.FromResult<EngineSettingRecord?>(
            key == "SharedVariables.RequiredRole" && catalogRole is not null
                ? new(1, null, key, catalogRole, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) : null);
        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken ct) => throw new NotSupportedException();
        public Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeAuthoring : IWorkflowAiAuthoringService
    {
        public int Calls { get; private set; }
        public string? LastModelId { get; private set; }
        public JsonElement? LastDefinition { get; private set; }
        public bool FailBeforeProgress { get; set; }
        public bool FailAfterProgress { get; set; }
        public TaskCompletionSource? FinishGate { get; set; }
        public Task<IReadOnlyList<AiModelDto>> GetModelsAsync(CancellationToken ct)
        { Calls++; return Task.FromResult<IReadOnlyList<AiModelDto>>([]); }
        public Task<AiValidationResultDto> ValidateAsync(JsonElement definition, CancellationToken ct)
        { Calls++; LastDefinition = definition.Clone(); return Task.FromResult(new AiValidationResultDto(true, [], []) { CanSave = true }); }
        public Task<AiTurnResultDto> TurnAsync(AiTurnRequestDto request, CancellationToken ct)
        {
            Calls++; LastModelId = request.ModelId;
            return Task.FromResult(new AiTurnResultDto("answer", "Who reviews?", null, new(false, [], []), request.SnapshotId, "test-contract"));
        }
        public async Task<AiTurnResultDto> RunAsync(AiTurnRequestDto request,
            Func<AiRunEventDto, CancellationToken, Task>? emit, CancellationToken ct)
        {
            if (FailBeforeProgress) throw new WorkflowAiException("provider_rate_limited", "Try again shortly.", 429);
            if (emit is not null) await emit(new() { RunId = "test-run", Sequence = 1, Type = "progress", Stage = "planning" }, ct);
            if (FailAfterProgress) throw new WorkflowAiException("provider_rate_limited", "Try again shortly.", 429);
            if (FinishGate is not null) await FinishGate.Task.WaitAsync(ct);
            var result = await TurnAsync(request, ct);
            if (emit is not null) await emit(new() { RunId = "test-run", Sequence = 2, Type = "result", Result = result }, ct);
            return result;
        }
    }
}
