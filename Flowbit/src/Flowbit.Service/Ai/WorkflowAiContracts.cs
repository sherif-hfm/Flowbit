using System.Text.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Ai;

public sealed class WorkflowAiOptions
{
    public const string SectionName = "WorkflowAi";
    public bool Enabled { get; set; } = true;
    // New engines are opt-in until the live evaluation gates pass.
    public string ExecutionVariant { get; set; } = "current";
    public int MaxInputCharacters { get; set; } = 200_000;
    public int MaxContextCharacters { get; set; } = 750_000;
    public int MaxWorkflowCharacters { get; set; } = 2_097_152;
    public int MaxOutputBytes { get; set; } = 2_097_152;
    public int MaxHistoryMessages { get; set; } = 30;
    public int MaxRepairAttempts { get; set; } = 2;
    public int RunTimeoutSeconds { get; set; } = 1800;
    public int RequestTimeoutSeconds { get; set; } = 180;
    public int MaxProviderCalls { get; set; } = 50;
    public int MaxRunOutputTokens { get; set; } = 262_144;
    public int MaxTransportRetries { get; set; } = 2;
    public int MaxTruncationRecoveries { get; set; } = 3;
    public int MaxOperationsPerBatch { get; set; } = 20;
    public int InitialOutputTokens { get; set; } = 8_192;
    public int MaxModelOutputTokens { get; set; } = 32_768;
    public int ContextTokens { get; set; } = 65_536;
    public int RetryBaseDelayMilliseconds { get; set; } = 1_000;
    public int MaxConcurrentRequests { get; set; } = 4;
    public string OpenCodeBaseUrl { get; set; } = "https://opencode.ai/zen/v1/";
    public List<string> OpenCodeModels { get; set; } = ["kimi-k2.7-code", "glm-5.3", "glm-5.3-flash"];
    public Dictionary<string, string> OpenCodeReasoningEfforts { get; set; } = [];
    public Dictionary<string, AiModelProfile> ModelProfiles { get; set; } = [];

    /// <summary>Reject invalid server settings before spending a provider call. Values are never caller supplied.</summary>
    public void Validate()
    {
        if (ExecutionVariant is not ("current" or "optimized" or "agent-framework")
            || !InRange(MaxInputCharacters, 1, 1_000_000) || !InRange(MaxContextCharacters, 1, 4_000_000)
            || !InRange(MaxWorkflowCharacters, 1, 8_388_608) || !InRange(MaxOutputBytes, 1, 8_388_608)
            || !InRange(MaxHistoryMessages, 0, 100) || !InRange(MaxRepairAttempts, 0, 2)
            || !InRange(RunTimeoutSeconds, 1, AiAuthoringLimits.MaxRunTimeoutSeconds) || !InRange(RequestTimeoutSeconds, 1, 600)
            || !InRange(MaxProviderCalls, 1, 100) || !InRange(MaxRunOutputTokens, 1, 1_048_576)
            || !InRange(MaxTransportRetries, 0, 5) || !InRange(MaxTruncationRecoveries, 0, 10)
            || !InRange(MaxOperationsPerBatch, 1, 100) || !InRange(MaxConcurrentRequests, 1, 32)
            || !InRange(RetryBaseDelayMilliseconds, 1, 30_000)
            || OpenCodeModels is null || OpenCodeModels.Count is < 1 or > 100
            || OpenCodeModels.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl))
            || OpenCodeReasoningEfforts is null
            || ModelProfiles is null || ModelProfiles.Count > 100)
            throw ConfigurationError();
        if (OpenCodeReasoningEfforts.Values.Any(value => value is not ("low" or "high" or "max")))
            throw new WorkflowAiException("provider_configuration", "The configured model reasoning effort must be low, high, or max.", 503);
        ValidateProfile(new() { ContextTokens = ContextTokens, InitialOutputTokens = InitialOutputTokens, MaxOutputTokens = MaxModelOutputTokens });
        foreach (var (id, profile) in ModelProfiles)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl) || profile is null)
                throw ConfigurationError();
            ValidateProfile(profile);
        }
    }

    public AiModelProfile GetModelProfile(string modelId)
    {
        Validate();
        return ModelProfiles.TryGetValue(modelId, out var configured)
            ? new() { ContextTokens = configured.ContextTokens, InitialOutputTokens = configured.InitialOutputTokens, MaxOutputTokens = configured.MaxOutputTokens }
            : new() { ContextTokens = ContextTokens, InitialOutputTokens = InitialOutputTokens, MaxOutputTokens = MaxModelOutputTokens };
    }

    private static void ValidateProfile(AiModelProfile profile)
    {
        if (!InRange(profile.ContextTokens, 1_024, 2_097_152)
            || !InRange(profile.MaxOutputTokens, 1, 262_144) || profile.MaxOutputTokens >= profile.ContextTokens
            || !InRange(profile.InitialOutputTokens, 1, profile.MaxOutputTokens))
            throw ConfigurationError();
    }

    private static bool InRange(int value, int minimum, int maximum) => value >= minimum && value <= maximum;
    private static WorkflowAiException ConfigurationError() =>
        new("provider_configuration", "The AI authoring limits or model profiles are invalid. Ask your administrator to check the configuration.", 503);
}

public sealed class AiModelProfile
{
    public int ContextTokens { get; set; } = 65_536;
    public int InitialOutputTokens { get; set; } = 8_192;
    public int MaxOutputTokens { get; set; } = 32_768;
}

public sealed record AiCompletion(string Content, string FinishReason, int? InputTokens = null, int? OutputTokens = null, string? RequestId = null);

/// <summary>Server-selected settings frozen for one run; never supplied directly by an HTTP caller.</summary>
public sealed record AiExecutionSettings(string Variant, string? ReasoningEffort);

public sealed class WorkflowAiConcurrencyGate(WorkflowAiOptions options) : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(Math.Clamp(options.MaxConcurrentRequests, 1, 32));
    public void Dispose() => Semaphore.Dispose();
}

public sealed class WorkflowAiException(string code, string message, int statusCode = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public bool Retryable { get; init; }
    public TimeSpan? RetryAfter { get; init; }
}

public interface IWorkflowAiAuthoringService
{
    Task<IReadOnlyList<AiProviderDto>> GetProvidersAsync(CancellationToken cancellationToken);
    Task<AiTurnResultDto> TurnAsync(AiTurnRequestDto request, string apiKey, CancellationToken cancellationToken);
    async Task<AiTurnResultDto> RunAsync(AiTurnRequestDto request, string apiKey,
        Func<AiRunEventDto, CancellationToken, Task>? emit, CancellationToken cancellationToken)
    {
        var result = await TurnAsync(request, apiKey, cancellationToken);
        if (emit is not null) await emit(new AiRunEventDto { RunId = Guid.NewGuid().ToString("N"), Sequence = 1, Type = "result", Result = result }, cancellationToken);
        return result;
    }
    Task<AiValidationResultDto> ValidateAsync(JsonElement definition, CancellationToken cancellationToken);
}

/// <summary>Provider-neutral text completion boundary. Implementations never retain credentials or request contents.</summary>
public interface IAiWorkflowProvider
{
    AiProviderDto Descriptor { get; }
    bool SupportsExecution(string variant) => variant is "current" or "optimized";
    Task<AiCompletion> CompleteAsync(string modelId, string conversationId, IReadOnlyList<AiChatMessageDto> messages,
        string apiKey, int maxOutputTokens, CancellationToken cancellationToken);
    Task<AiCompletion> CompleteAsync(string modelId, string conversationId, IReadOnlyList<AiChatMessageDto> messages,
        string apiKey, int maxOutputTokens, AiExecutionSettings execution, CancellationToken cancellationToken)
        => CompleteAsync(modelId, conversationId, messages, apiKey, maxOutputTokens, cancellationToken);
}

