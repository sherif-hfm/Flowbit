using System.Text.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Ai;

public sealed class WorkflowAiOptions
{
    public const string SectionName = "WorkflowAi";
    public bool Enabled { get; set; } = true;
    public int MaxInputCharacters { get; set; } = 200_000;
    public int MaxContextCharacters { get; set; } = 750_000;
    public int MaxWorkflowCharacters { get; set; } = 2_097_152;
    public int MaxOutputBytes { get; set; } = 2_097_152;
    public int MaxHistoryMessages { get; set; } = 30;
    public int MaxRepairAttempts { get; set; } = 2;
    public int RequestTimeoutSeconds { get; set; } = 180;
    public int MaxConcurrentRequests { get; set; } = 4;
    public string OpenCodeBaseUrl { get; set; } = "https://opencode.ai/zen/go/v1/";
    public List<string> OpenCodeModels { get; set; } = ["kimi-k2.7-code", "glm-5.3", "glm-5.3-flash"];
    public Dictionary<string, string> OpenCodeReasoningEfforts { get; set; } = [];
}

public sealed class WorkflowAiConcurrencyGate(WorkflowAiOptions options) : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(Math.Clamp(options.MaxConcurrentRequests, 1, 32));
    public void Dispose() => Semaphore.Dispose();
}

public sealed class WorkflowAiException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public interface IWorkflowAiAuthoringService
{
    Task<IReadOnlyList<AiProviderDto>> GetProvidersAsync(CancellationToken cancellationToken);
    Task<AiTurnResultDto> TurnAsync(AiTurnRequestDto request, string apiKey, CancellationToken cancellationToken);
    Task<AiValidationResultDto> ValidateAsync(JsonElement definition, CancellationToken cancellationToken);
}

/// <summary>Provider-neutral text completion boundary. Implementations never retain credentials or request contents.</summary>
public interface IAiWorkflowProvider
{
    AiProviderDto Descriptor { get; }
    Task<string> CompleteAsync(string modelId, string conversationId, IReadOnlyList<AiChatMessageDto> messages,
        string apiKey, CancellationToken cancellationToken);
}

