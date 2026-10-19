using System.ClientModel;
using System.ClientModel.Primitives;
using Flowbit.Service.Ai;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Flowbit.Infrastructure.Ai;

/// <summary>
/// The OpenCode chat-completions client of one agent run (Zen by default), as in Flowbit AI Lab v3: the OpenAI SDK with no retries,
/// a network timeout as long as the run, no redirects, and GLM reasoning replayed on later tool requests.
/// </summary>
public sealed class OpenCodeZenChatClientFactory(WorkflowAiOptions options, Func<HttpMessageHandler> primaryHandler) : IWorkflowAiChatClientFactory
{
    public const string HttpClientName = "Flowbit.OpenCodeZen";

    // Retain the original provider id so existing API callers and panels remain compatible.
    public AiProviderDto Descriptor => new("opencode-go", ProviderName, options.OpenCodeModels.FirstOrDefault() ?? "glm-5.3-flash",
        options.OpenCodeModels.Distinct(StringComparer.Ordinal).Select(id => new AiModelDto(id, id)).ToArray());

    public string Endpoint => new Uri(BaseUri, "chat/completions").AbsoluteUri;

    public IChatClient Create(string modelId, string apiKey, string conversationId, WorkflowAiRunMeter meter, TimeSpan networkTimeout)
    {
        // The pooled primary handler belongs to the handler factory, so neither the client nor the capture handler disposes it.
        var capture = new WorkflowAiCaptureHandler(meter, conversationId) { InnerHandler = primaryHandler() };
        var http = new HttpClient(capture, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions
        {
            Endpoint = BaseUri,
            NetworkTimeout = networkTimeout,
            RetryPolicy = new ClientRetryPolicy(0),
            Transport = new HttpClientPipelineTransport(http)
        });
        return new ProviderErrorChatClient(new CompatibleReasoningReplayChatClient(client.GetChatClient(modelId).AsIChatClient()));
    }

    private Uri BaseUri => string.IsNullOrWhiteSpace(options.OpenCodeBaseUrl)
        || !Uri.TryCreate(options.OpenCodeBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
        || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            ? throw new WorkflowAiException("provider_configuration", "The configured provider endpoint must use HTTPS or local loopback HTTP.", 503)
            : uri;

    private string ProviderName => Uri.TryCreate(options.OpenCodeBaseUrl, UriKind.Absolute, out var endpoint)
        && endpoint.AbsolutePath.TrimEnd('/').Equals("/zen/go/v1", StringComparison.OrdinalIgnoreCase)
            ? "OpenCode Go" : "OpenCode Zen";

    // Transport failures the capture handler cannot see, such as a refused connection, stop the run like a provider error.
    private sealed class ProviderErrorChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            try { return await base.GetResponseAsync(messages, options, cancellationToken); }
            catch (ClientResultException) when (!cancellationToken.IsCancellationRequested)
            { throw new WorkflowAiStopException("provider_unavailable", "The provider request failed. Continue to try again."); }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            { throw new WorkflowAiStopException("provider_unavailable", "The provider could not be reached. Continue to try again."); }
        }
    }
}
