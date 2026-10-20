using System.ClientModel;
using System.ClientModel.Primitives;
using Flowbit.Service.Ai;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Flowbit.Infrastructure.Ai;

/// <summary>
/// The chat-completions client of one agent run for an administrator-configured OpenAI-compatible endpoint, as in Flowbit AI Lab
/// v3: the OpenAI SDK with no retries, a network timeout as long as the run, no redirects, and compatible reasoning replayed on
/// later tool requests.
/// </summary>
public sealed class OpenAiCompatibleChatClientFactory(Func<HttpMessageHandler> primaryHandler) : IWorkflowAiChatClientFactory
{
    public const string HttpClientName = "Flowbit.AiProvider";

    public IChatClient Create(AiModelConnection model, string conversationId, WorkflowAiRunMeter meter, TimeSpan networkTimeout)
    {
        // The stored list is checked when it is read; check the endpoint again before the key is sent anywhere.
        if (!AiModelSettingRules.TryNormalizeEndpoint(model.Endpoint, out var endpoint, out _))
            throw new WorkflowAiException("provider_configuration",
                "The selected model's endpoint must use HTTPS or local loopback HTTP. Ask a settings administrator to review the AI models page.", 503);
        // The pooled primary handler belongs to the handler factory, so neither the client nor the capture handler disposes it.
        var capture = new WorkflowAiCaptureHandler(meter, conversationId) { InnerHandler = primaryHandler() };
        var http = new HttpClient(capture, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new OpenAIClient(new ApiKeyCredential(model.ApiKey), new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
            NetworkTimeout = networkTimeout,
            RetryPolicy = new ClientRetryPolicy(0),
            Transport = new HttpClientPipelineTransport(http)
        });
        return new ProviderErrorChatClient(new CompatibleReasoningReplayChatClient(client.GetChatClient(model.Model).AsIChatClient()));
    }

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
