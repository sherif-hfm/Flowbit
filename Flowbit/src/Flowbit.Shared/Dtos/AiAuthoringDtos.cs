using System.Text.Json;

namespace Flowbit.Shared.Dtos;

public sealed record AiModelDto(string Id, string Name);
public sealed record AiProviderDto(string Id, string Name, string DefaultModelId, IReadOnlyList<AiModelDto> Models);
public sealed record AiChatMessageDto(string Role, string Content);
public sealed record AiSourcePageDto(string SourceName, int PageNumber, string Text);

/// <summary>Transient authoring input. Provider credentials must only travel in the dedicated request header.</summary>
public sealed record AiTurnRequestDto
{
    public string ProviderId { get; init; } = "opencode-go";
    public string ModelId { get; init; } = "kimi-k2.7-code";
    public string ConversationId { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<AiChatMessageDto> History { get; init; } = [];
    public JsonElement? CurrentWorkflow { get; init; }
    public string SnapshotId { get; init; } = string.Empty;
    public IReadOnlyList<AiSourcePageDto> Sources { get; init; } = [];
    /// <summary>Explicitly selected catalog keys. The API additionally requires shared-catalog read permission.</summary>
    public IReadOnlyList<string> SharedVariableKeys { get; init; } = [];
    public AiCheckpointDto? Checkpoint { get; init; }
}

public sealed record AiValidationResultDto(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool CanSave { get; init; }
    public bool CanPublish { get; init; }
    public IReadOnlyList<string> SaveBlockers { get; init; } = [];
    public IReadOnlyList<string> PublicationBlockers { get; init; } = [];
}

public sealed record AiSourceReferenceDto(string SourceName, int PageNumber, string Requirement);

public sealed record AiTurnResultDto(
    string Kind,
    string Message,
    IReadOnlyList<string> Questions,
    JsonElement? Definition,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> ChangeSummary,
    AiValidationResultDto Validation,
    string SnapshotId,
    string ContractHash)
{
    public IReadOnlyList<AiSourceReferenceDto> SourceReferences { get; init; } = [];
    public AiCheckpointDto? Checkpoint { get; init; }
    public AiRunSummaryDto? Run { get; init; }
}

/// <summary>Untrusted, bounded continuation state, retained only in the open assistant circuit.</summary>
public sealed record AiCheckpointDto
{
    public int Version { get; init; } = 1;
    public string? ExecutionVariant { get; init; }
    public string? ReasoningEffort { get; init; }
    public string? ModelProfileHash { get; init; }
    public string InputHash { get; init; } = "";
    public string ContractHash { get; init; } = "";
    public JsonElement Draft { get; init; }
    public long Revision { get; init; }
    public string Plan { get; init; } = "";
    public IReadOnlyList<AiBatchReceiptDto> Batches { get; init; } = [];
    public int? OutputAllowance { get; init; }
    public int? MaxOperations { get; init; }
    /// <summary>Bounded read positions; excerpts are rebuilt from original inputs and verified references on resume.</summary>
    public IReadOnlyList<AiContextReadDto> ContextReads { get; init; } = [];
}

public sealed record AiContextReadDto(string Kind, string Resource, int Offset, int Count);
public sealed record AiBatchReceiptDto(string Id, string Hash);
public sealed record AiRunSummaryDto(int ProviderCalls, long OutputTokens, bool UsageEstimated, double ElapsedSeconds)
{
    public string? ExecutionVariant { get; init; }
    public string? ReasoningEffort { get; init; }
    public double? FirstEditSeconds { get; init; }
    public double LastCallSeconds { get; init; }
    public int ContextReads { get; init; }
    public int DuplicateReads { get; init; }
    public int Retries { get; init; }
    public int AcceptedBatches { get; init; }
    public long InputTokens { get; init; }
}

/// <summary>Versioned NDJSON frame. Draft checkpoints are private state, never applicable proposals.</summary>
public sealed record AiRunEventDto
{
    public int Version { get; init; } = 1;
    public string RunId { get; init; } = "";
    public long Sequence { get; init; }
    public string Type { get; init; } = "progress";
    public string? Stage { get; init; }
    public string? Message { get; init; }
    public string? Code { get; init; }
    public AiCheckpointDto? Checkpoint { get; init; }
    public AiTurnResultDto? Result { get; init; }
    public AiRunSummaryDto? Run { get; init; }
}
