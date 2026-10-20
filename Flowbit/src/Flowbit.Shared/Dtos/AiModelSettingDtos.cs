namespace Flowbit.Shared.Dtos;

/// <summary>
/// An AI model the editor assistant can use, as managed on the AI models page. The provider API key is write-only and is never
/// returned. The first model in the list is the assistant's default.
/// </summary>
public sealed record AiModelSettingDto(
    string Id,
    string Name,
    string Endpoint,
    string Model,
    string? ReasoningEffort,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateAiModelRequest(
    string Name,
    string Endpoint,
    string Model,
    string? ReasoningEffort,
    string ApiKey);

/// <summary>Replaces a model's settings. A null or blank <see cref="ApiKey"/> keeps the stored key.</summary>
public sealed record UpdateAiModelRequest(
    string Name,
    string Endpoint,
    string Model,
    string? ReasoningEffort,
    string? ApiKey,
    long ExpectedRevision);
