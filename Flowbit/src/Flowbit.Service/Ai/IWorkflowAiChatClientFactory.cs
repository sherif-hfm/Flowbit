using Flowbit.Shared.Dtos;
using Microsoft.Extensions.AI;

namespace Flowbit.Service.Ai;

/// <summary>Creates the model-facing chat client of one agent run. Implementations never retain credentials or request contents.</summary>
public interface IWorkflowAiChatClientFactory
{
    AiProviderDto Descriptor { get; }

    /// <summary>The chat-completions endpoint, recorded in the conversation state so a resumed run uses the same provider.</summary>
    string Endpoint { get; }

    /// <summary>
    /// A client for the user's key that never retries or follows redirects, refuses requests beyond <paramref name="meter"/>,
    /// records usage, and stops the run with a <see cref="WorkflowAiStopException"/> on a provider error or a truncated response.
    /// </summary>
    IChatClient Create(string modelId, string apiKey, string conversationId, WorkflowAiRunMeter meter, TimeSpan networkTimeout);
}
