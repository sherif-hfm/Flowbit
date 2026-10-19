using System.Text.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Ai;

/// <summary>Server settings of the authoring agent. The run limits are Flowbit AI Lab v3's (user decision, 2026-10-16).</summary>
public sealed class WorkflowAiOptions
{
    public const string SectionName = "WorkflowAi";

    /// <summary>Settings of the replaced command-loop engine. They have no effect now.</summary>
    public static readonly IReadOnlyList<string> RemovedSettings =
    [
        "ContextTokens", "ExecutionVariant", "InitialOutputTokens", "MaxConcurrentProviderCalls", "MaxContextCharacters", "MaxHistoryMessages",
        "MaxOperationsPerBatch", "MaxOutputBytes", "MaxParallelAnalysisCalls", "MaxRepairAttempts", "MaxRunOutputTokens", "MaxTransportRetries",
        "MaxTruncationRecoveries", "ModelProfiles", "RequestTimeoutSeconds", "RequirementsReviewEnabled", "RetryBaseDelayMilliseconds"
    ];

    public bool Enabled { get; set; } = true;
    public int MaxInputCharacters { get; set; } = 200_000;
    public int MaxWorkflowCharacters { get; set; } = 2_097_152;
    /// <summary>One deadline for a whole run, nudges included. Model calls have no separate timeout.</summary>
    public int RunTimeoutSeconds { get; set; } = 1800;
    /// <summary>Model requests per run; the next request is refused before it is sent.</summary>
    public int MaxProviderCalls { get; set; } = 40;
    public int MaxModelOutputTokens { get; set; } = 32_768;
    /// <summary>How often the host sends validation errors back before a turn ends without a proposal.</summary>
    public int MaxNudges { get; set; } = 2;
    public int MaxConcurrentRequests { get; set; } = 4;
    public string OpenCodeBaseUrl { get; set; } = "https://opencode.ai/zen/v1/";
    public List<string> OpenCodeModels { get; set; } = ["glm-5.3-flash"];
    public Dictionary<string, string> OpenCodeReasoningEfforts { get; set; } = [];

    /// <summary>Removed settings that would silently change behaviour if ignored, such as an engine selection. Set from configuration.</summary>
    public IReadOnlyList<string> UnsupportedSettings { get; set; } = [];
    /// <summary>Removed tuning settings present in configuration; they are ignored with a startup warning.</summary>
    public IReadOnlyList<string> IgnoredSettings { get; set; } = [];

    /// <summary>Reject invalid server settings before spending a provider call. Values are never caller supplied.</summary>
    public void Validate()
    {
        if (UnsupportedSettings.Count > 0)
            throw new WorkflowAiException("provider_configuration",
                $"WorkflowAi:{UnsupportedSettings[0]} is no longer supported: the AI assistant has one authoring engine. Remove the setting and restart.", 503);
        if (!InRange(MaxInputCharacters, 1, 1_000_000) || !InRange(MaxWorkflowCharacters, 1, 8_388_608)
            || !InRange(RunTimeoutSeconds, 1, AiAuthoringLimits.MaxRunTimeoutSeconds) || !InRange(MaxProviderCalls, 1, 100)
            || !InRange(MaxModelOutputTokens, 1, 262_144) || !InRange(MaxNudges, 0, 5) || !InRange(MaxConcurrentRequests, 1, 32)
            || OpenCodeModels is null || OpenCodeModels.Count is < 1 or > 100
            || OpenCodeModels.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 200 || id.Any(char.IsControl))
            || OpenCodeReasoningEfforts is null)
            throw new WorkflowAiException("provider_configuration", "The AI authoring limits are invalid. Ask your administrator to check the configuration.", 503);
        if (OpenCodeReasoningEfforts.Values.Any(value => value is not ("low" or "medium" or "high")))
            throw new WorkflowAiException("provider_configuration", "The configured model reasoning effort must be low, medium, or high.", 503);
    }

    private static bool InRange(int value, int minimum, int maximum) => value >= minimum && value <= maximum;
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
    Task<AiTurnResultDto> RunAsync(AiTurnRequestDto request, string apiKey,
        Func<AiRunEventDto, CancellationToken, Task>? emit, CancellationToken cancellationToken);
    Task<AiValidationResultDto> ValidateAsync(JsonElement definition, CancellationToken cancellationToken);
}
