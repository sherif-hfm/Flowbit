using System.Text.Json;

namespace Flowbit.Shared.Dtos;

public sealed record AiModelDto(string Id, string Name);
public sealed record AiProviderDto(string Id, string Name, string DefaultModelId, IReadOnlyList<AiModelDto> Models);
public sealed record AiSourcePageDto(string SourceName, int PageNumber, string Text);

/// <summary>Transient authoring input. Provider credentials must only travel in the dedicated request header.</summary>
public sealed record AiTurnRequestDto
{
    public string ProviderId { get; init; } = "opencode-go";
    public string ModelId { get; init; } = "glm-5.3-flash";
    public string ConversationId { get; init; } = string.Empty;
    /// <summary>The user's message, sent to the model exactly as typed. Empty only with <see cref="Continue"/>.</summary>
    public string Message { get; init; } = string.Empty;
    /// <summary>Resume a stopped run from its last checkpoint, without a new message.</summary>
    public bool Continue { get; init; }
    public JsonElement? CurrentWorkflow { get; init; }
    public string SnapshotId { get; init; } = string.Empty;
    public IReadOnlyList<AiSourcePageDto> Sources { get; init; } = [];
    /// <summary>Explicitly selected catalog keys. The API additionally requires shared-catalog read permission.</summary>
    public IReadOnlyList<string> SharedVariableKeys { get; init; } = [];
    /// <summary>The conversation state from the previous result or checkpoint; null starts a new conversation.</summary>
    public AiConversationStateDto? State { get; init; }
}

/// <summary>
/// Opaque, untrusted state of an authoring conversation, kept only in the open assistant panel and validated when it returns.
/// It holds no credentials: workflow files and the conversation carry credential placeholders only.
/// </summary>
public sealed record AiConversationStateDto
{
    public string Format { get; init; } = "";
    public string Data { get; init; } = "";
    /// <summary>True when a run stopped mid-turn, so Continue can resume it without a new message.</summary>
    public bool CanContinue { get; init; }
    public int Messages { get; init; }
    public int? WorkflowNodes { get; init; }
    public int? WorkflowFlows { get; init; }
}

public sealed record AiValidationResultDto(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool CanSave { get; init; }
    public bool CanPublish { get; init; }
    public IReadOnlyList<string> SaveBlockers { get; init; } = [];
    public IReadOnlyList<string> PublicationBlockers { get; init; } = [];
}

/// <summary>
/// The outcome of one turn. Kind is proposal (a validated, laid-out definition to review and apply), answer (the model replied
/// without changing the workflow), invalid (no valid workflow after the host's nudges) or paused (the run stopped; Continue resumes it).
/// </summary>
public sealed record AiTurnResultDto(
    string Kind,
    string Message,
    JsonElement? Definition,
    AiValidationResultDto Validation,
    string SnapshotId,
    string ContractHash)
{
    /// <summary>Why a run stopped or ended without a proposal, such as run_budget or invalid_workflow.</summary>
    public string? Code { get; init; }
    /// <summary>The open questions and assumptions the model wrote to open-questions.md.</summary>
    public string? OpenQuestions { get; init; }
    public AiConversationStateDto? State { get; init; }
    public AiRunSummaryDto? Run { get; init; }
}

public sealed record AiRunSummaryDto(int ProviderCalls, long OutputTokens, bool UsageEstimated, double ElapsedSeconds)
{
    public int MaxProviderCalls { get; init; }
    public long InputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public int Nudges { get; init; }
    public int? WorkflowNodes { get; init; }
    public int? WorkflowFlows { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? PromptFingerprint { get; init; }
    public string? DocsFingerprint { get; init; }
}

/// <summary>Versioned NDJSON frame. Checkpoint states are private resume points, never applicable proposals.</summary>
public sealed record AiRunEventDto
{
    public int Version { get; init; } = 2;
    public string RunId { get; init; } = "";
    public long Sequence { get; init; }
    /// <summary>progress, checkpoint (carries State), result or paused (carry Result), or error.</summary>
    public string Type { get; init; } = "progress";
    public string? Stage { get; init; }
    public string? Message { get; init; }
    public string? Code { get; init; }
    public AiTurnResultDto? Result { get; init; }
    public AiRunSummaryDto? Run { get; init; }
    public AiConversationStateDto? State { get; init; }
}
