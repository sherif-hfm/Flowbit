using System.Text.Json;

namespace Flowbit.Shared.Dtos;

/// <summary>
/// Catalog and lifecycle metadata for a deployment-wide shared variable. The
/// current value is intentionally available only from the explicit value route.
/// </summary>
public sealed record SharedVariableMetadataDto(
    long Id,
    string Key,
    string DataType,
    bool IsArray,
    bool Nullable,
    string? Validation,
    string? Description,
    bool HasValue,
    string Status,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

/// <summary>
/// Current-value projection. HasValue distinguishes an explicitly stored JSON
/// null from a variable for which no value has been set.
/// </summary>
public sealed record SharedVariableValueDto(
    string Key,
    bool HasValue,
    JsonElement? Value,
    long Revision,
    DateTimeOffset UpdatedAt);

public sealed record UpdateSharedVariableValueRequest(
    JsonElement Value,
    long ExpectedRevision,
    string? RequestId = null,
    string? Reason = null);

public sealed record UpdateSharedVariableDescriptionRequest(
    string? Description,
    long ExpectedRevision,
    string? RequestId = null,
    string? Reason = null);

public sealed record UpdateSharedVariableClientRequest(
    string DisplayName,
    IReadOnlyCollection<string> Scopes,
    DateTimeOffset? ExpiresAt,
    long ExpectedRevision);
