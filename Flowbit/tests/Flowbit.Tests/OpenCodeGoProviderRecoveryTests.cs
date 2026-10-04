using System.Net;
using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Ai;
using Flowbit.Service.Ai;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Flowbit.Tests;

public sealed class OpenCodeGoProviderRecoveryTests
{
    [Fact]
    public async Task CompletionRetainsFinishReasonUsageAndRequestIdAndUsesRequestedTokenBudget()
    {
        string? sent = null;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            sent = await request.Content!.ReadAsStringAsync(ct);
            var response = Response(HttpStatusCode.OK,
                """{"choices":[{"finish_reason":"length","message":{"content":"{\"unfinished\":"}}],"usage":{"prompt_tokens":4321,"completion_tokens":8192}}""");
            response.Headers.Add("x-request-id", "request-123");
            return response;
        }));
        var completion = await new OpenCodeGoProvider(client, new()).CompleteAsync("kimi-k2.7-code", "session-one", [], "key-one", 8192, CancellationToken.None);

        Assert.Equal("{\"unfinished\":", completion.Content);
        Assert.Equal("length", completion.FinishReason);
        Assert.Equal(4321, completion.InputTokens);
        Assert.Equal(8192, completion.OutputTokens);
        Assert.Equal("request-123", completion.RequestId);
        using var body = JsonDocument.Parse(sent!);
        Assert.Equal(8192, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData("""{"choices":[{"finish_reason":"length","message":{"content":null}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"length","message":{"reasoning_content":"private"}}]}""")]
    public async Task LengthWithoutVisibleContentRemainsRecoverable(string body)
    {
        var result = await CompleteAsync(Response(HttpStatusCode.OK, body));
        Assert.Equal("length", result.FinishReason);
        Assert.Empty(result.Content);
    }

    [Theory]
    [InlineData(401, "{}", "provider_auth", false)]
    [InlineData(403, "{}", "provider_auth", false)]
    [InlineData(402, "{}", "provider_quota", false)]
    [InlineData(429, "{}", "provider_throttled", true)]
    [InlineData(429, """{"error":{"code":"insufficient_quota","message":"secret"}}""", "provider_quota", false)]
    [InlineData(429, """{"error":{"message":"Insufficient credits: secret"}}""", "provider_quota", false)]
    [InlineData(400, """{"error":{"code":"context_length_exceeded","message":"secret"}}""", "provider_context", false)]
    [InlineData(400, """{"error":{"message":"Maximum context length exceeded: secret"}}""", "provider_context", false)]
    [InlineData(422, """{"error":{"type":"input_too_long","message":"secret"}}""", "provider_context", false)]
    [InlineData(400, """{"error":{"code":"content_policy_violation","message":"secret"}}""", "provider_refusal", false)]
    [InlineData(400, """{"error":{"message":"secret"}}""", "provider_invalid_response", false)]
    [InlineData(408, "{}", "provider_unavailable", true)]
    [InlineData(500, "{}", "provider_unavailable", true)]
    [InlineData(503, "{}", "provider_unavailable", true)]
    public async Task DistinguishesTerminalFailuresFromTransientFailuresWithoutEchoingBody(int status, string body, string code, bool retryable)
    {
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => CompleteAsync(Response((HttpStatusCode)status, body)));
        Assert.Equal(code, error.Code);
        Assert.Equal(retryable, error.Retryable);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData("2700", 2700)]
    [InlineData("999999", AiAuthoringLimits.MaxRunTimeoutSeconds)]
    [InlineData("0", 0)]
    [InlineData("invalid", null)]
    public async Task ReadsAndBoundsRetryAfterSeconds(string header, int? expectedSeconds)
    {
        var response = Response(HttpStatusCode.TooManyRequests, "{}");
        response.Headers.TryAddWithoutValidation("Retry-After", header);
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => CompleteAsync(response));
        Assert.Equal(expectedSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, error.RetryAfter);
    }

    [Fact]
    public async Task ReadsRetryAfterHttpDate()
    {
        var response = Response(HttpStatusCode.ServiceUnavailable, "{}");
        response.Headers.TryAddWithoutValidation("Retry-After", DateTimeOffset.UtcNow.AddSeconds(30).ToString("R"));
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => CompleteAsync(response));
        Assert.NotNull(error.RetryAfter);
        Assert.InRange(error.RetryAfter.Value.TotalSeconds, 20, 31);
    }

    [Theory]
    [InlineData("""{"choices":[{"finish_reason":"content_filter","message":{"content":"secret"}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"refusal":"secret","content":null}}]}""")]
    public async Task RefusalsAreTerminalAndDoNotExposeProviderExplanation(string body)
    {
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => CompleteAsync(Response(HttpStatusCode.OK, body)));
        Assert.Equal("provider_refusal", error.Code);
        Assert.False(error.Retryable);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData("""{"choices":[{"finish_reason":"tool_calls","message":{"content":"secret"}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":"secret","tool_calls":[{}]}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":42,"message":{"content":"secret"}}]}""")]
    public async Task UnsupportedOrMalformedCompletionsAreTerminal(string body)
    {
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => CompleteAsync(Response(HttpStatusCode.OK, body)));
        Assert.Equal("provider_invalid_response", error.Code);
        Assert.False(error.Retryable);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData("{\"secret\":")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":""}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":null}}]}""")]
    public async Task EmptyOrUnreadableSuccessfulResponsesAllowBoundedRunnerRetry(string body)
    {
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => CompleteAsync(Response(HttpStatusCode.OK, body)));
        Assert.Equal("provider_invalid_response", error.Code);
        Assert.True(error.Retryable);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task InvalidOptionalUsageAndUnsafeRequestIdAreNotExposed()
    {
        var response = Response(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"{}"}}],"usage":{"prompt_tokens":-1,"completion_tokens":"secret"}}""");
        response.Headers.TryAddWithoutValidation("x-request-id", "prefix key-one");
        var result = await CompleteAsync(response);
        Assert.Null(result.InputTokens);
        Assert.Null(result.OutputTokens);
        Assert.Null(result.RequestId);
        Assert.Equal("stop", result.FinishReason);
    }

    [Fact]
    public async Task ProviderDoesNotRetryTransportErrorsItself()
    {
        var attempts = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            attempts++;
            throw new HttpRequestException("secret");
        }));
        var provider = new OpenCodeGoProvider(client, new());
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => provider.CompleteAsync("kimi-k2.7-code", "session-one", [], "key-one", 8192, CancellationToken.None));
        Assert.Equal(1, attempts);
        Assert.Equal("provider_unavailable", error.Code);
        Assert.True(error.Retryable);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task ProviderTimeoutIsRetryableButCallerCancellationIsPreserved()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new TaskCanceledException("secret")));
        var provider = new OpenCodeGoProvider(client, new());
        var timeout = await Assert.ThrowsAsync<WorkflowAiException>(() => provider.CompleteAsync("kimi-k2.7-code", "session-one", [], "key-one", 8192, CancellationToken.None));
        Assert.True(timeout.Retryable);
        Assert.Equal("provider_unavailable", timeout.Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync("kimi-k2.7-code", "session-one", [], "key-one", 8192, cancelled.Token));
    }

    [Fact]
    public async Task ErrorBodyIsReadOnlyUpToBoundEvenWithoutContentLength()
    {
        using var stream = new CountingStream(Encoding.UTF8.GetBytes(new string('x', 100_000)));
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StreamContent(stream) };
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => CompleteAsync(response));
        Assert.Equal("provider_throttled", error.Code);
        Assert.InRange(stream.BytesRead, 1, 65_537);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutputByteLimitIsTerminalWithOrWithoutContentLength(bool contentLength)
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 2048));
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = contentLength ? new ByteArrayContent(bytes) : new StreamContent(new CountingStream(bytes))
        };
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => CompleteAsync(response, new() { MaxOutputBytes = 1024 }));
        Assert.Equal("provider_output_too_large", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task SelectedProfileBoundsOutputBeforeAnyTransport()
    {
        var attempts = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            attempts++;
            return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
        }));
        var options = new WorkflowAiOptions
        {
            ModelProfiles = new() { ["kimi-k2.7-code"] = new() { ContextTokens = 16_384, InitialOutputTokens = 2048, MaxOutputTokens = 4096 } }
        };
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() =>
            new OpenCodeGoProvider(client, options).CompleteAsync("kimi-k2.7-code", "session-one", [], "key-one", 8192, CancellationToken.None));
        Assert.Equal("provider_configuration", error.Code);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public void OptionsRejectInvalidLimitsAndReturnIndependentProfileCopies()
    {
        Assert.Throws<WorkflowAiException>(() => new WorkflowAiOptions { MaxTransportRetries = -1 }.Validate());
        Assert.Throws<WorkflowAiException>(() => new WorkflowAiOptions { RunTimeoutSeconds = 0 }.Validate());
        Assert.Throws<WorkflowAiException>(() => new WorkflowAiOptions { InitialOutputTokens = 40_000 }.Validate());
        Assert.Throws<WorkflowAiException>(() => new WorkflowAiOptions { ContextTokens = 32_768 }.Validate());
        Assert.Throws<WorkflowAiException>(() => new WorkflowAiOptions { ModelProfiles = new() { ["model"] = null! } }.Validate());
        var options = new WorkflowAiOptions { InitialOutputTokens = 4096 };
        var profile = options.GetModelProfile("unmapped-model");
        Assert.Equal(4096, profile.InitialOutputTokens);
        profile.InitialOutputTokens = 1;
        Assert.Equal(4096, options.GetModelProfile("unmapped-model").InitialOutputTokens);
    }

    [Fact]
    public void ConfigurationBindingDefaultModelListRemainsUsableAndDiscoveryDeduplicates()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkflowAi:OpenCodeModels:0"] = "kimi-k2.7-code",
            ["WorkflowAi:OpenCodeModels:1"] = "glm-5.3",
            ["WorkflowAi:OpenCodeModels:2"] = "glm-5.3-flash",
            ["WorkflowAi:RequestTimeoutSeconds"] = "90",
            ["WorkflowAi:RunTimeoutSeconds"] = "300"
        }).Build();
        var options = WorkflowAiServiceCollectionExtensions.ReadWorkflowAiOptions(configuration);
        options.Validate();
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Discovery must not call the provider.")));
        var descriptor = new OpenCodeGoProvider(client, options).Descriptor;
        Assert.Equal("kimi-k2.7-code", descriptor.DefaultModelId);
        Assert.Equal(new[] { "kimi-k2.7-code", "glm-5.3", "glm-5.3-flash" }, descriptor.Models.Select(model => model.Id));
    }

    [Fact]
    public void ConfiguredModelListReplacesFallbacksAndSetsTheDiscoveredDefault()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkflowAi:OpenCodeModels:0"] = "glm-5.3-flash"
        }).Build();
        var options = WorkflowAiServiceCollectionExtensions.ReadWorkflowAiOptions(configuration);
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Discovery must not call the provider.")));
        var descriptor = new OpenCodeGoProvider(client, options).Descriptor;
        Assert.Equal("glm-5.3-flash", descriptor.DefaultModelId);
        Assert.Equal("glm-5.3-flash", Assert.Single(descriptor.Models).Id);
        var unconfigured = WorkflowAiServiceCollectionExtensions.ReadWorkflowAiOptions(new ConfigurationBuilder().Build());
        Assert.Equal(new WorkflowAiOptions().OpenCodeModels, unconfigured.OpenCodeModels);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("optimized")]
    public async Task ShippedFlashSettingsReachTransportWithTheTestedOutputAllowanceAndReasoning(string variant)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Flowbit", "src", "Flowbit.Api", "appsettings.json")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "Flowbit", "src", "Flowbit.Api", "appsettings.json");
        var configurationBuilder = new ConfigurationBuilder().AddJsonFile(path);
        if (variant == "optimized") configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        { ["WorkflowAi:ExecutionVariant"] = variant });
        var configuration = configurationBuilder.Build();
        var options = WorkflowAiServiceCollectionExtensions.ReadWorkflowAiOptions(configuration);
        options.Validate();
        var profile = options.GetModelProfile("glm-5.3-flash");
        string? sent = null;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("https://opencode.ai/zen/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            sent = await request.Content!.ReadAsStringAsync(ct);
            return Response(HttpStatusCode.OK, """{"choices":[{"finish_reason":"stop","message":{"content":"{}"}}]}""");
        }));
        await new OpenCodeGoProvider(client, options).CompleteAsync("glm-5.3-flash", "session-one", [new("system", "Transport contract test")], "key-one", profile.InitialOutputTokens,
            new(options.ExecutionVariant, options.OpenCodeReasoningEfforts.GetValueOrDefault("glm-5.3-flash")), CancellationToken.None);
        using var body = JsonDocument.Parse(sent!);
        Assert.Equal(180, options.RequestTimeoutSeconds);
        Assert.Equal(1800, options.RunTimeoutSeconds);
        Assert.Equal(50, options.MaxProviderCalls);
        Assert.Equal(262_144, options.MaxRunOutputTokens);
        Assert.Equal(16_384, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("max", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(variant, options.ExecutionVariant);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
        Assert.False(body.RootElement.TryGetProperty("tool_choice", out _));
        Assert.False(body.RootElement.TryGetProperty("parallel_tool_calls", out _));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<AiCompletion> CompleteAsync(HttpResponseMessage response, WorkflowAiOptions? options = null)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(response)));
        return await new OpenCodeGoProvider(client, options ?? new()).CompleteAsync("kimi-k2.7-code", "session-one", [], "key-one", 8192, CancellationToken.None);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            return count;
        }
    }
}
