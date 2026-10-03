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
}
