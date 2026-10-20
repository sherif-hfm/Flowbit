using Microsoft.Extensions.AI;

namespace Flowbit.Service.Ai;

/// <summary>Creates the model-facing chat client of one agent run. Implementations never retain credentials or request contents.</summary>
public interface IWorkflowAiChatClientFactory
{
    /// <summary>
    /// A client for the configured <paramref name="model"/>, its endpoint and its key that never retries or follows redirects,
    /// refuses requests beyond <paramref name="meter"/>, records usage, and stops the run with a <see cref="WorkflowAiStopException"/>
    /// on a provider error or a truncated response.
    /// </summary>
    IChatClient Create(AiModelConnection model, string conversationId, WorkflowAiRunMeter meter, TimeSpan networkTimeout);
}
