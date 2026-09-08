using System.Text.Json;

namespace Flowbit.Shared.Dtos;

public static class SharedVariableStatuses
{
    public const string Active = "active";
    public const string Archived = "archived";
}

public static class SharedVariableClientStatuses
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}

public static class SharedVariableClientScopes
{
    public const string Read = "shared-variables.read";
    public const string Write = "shared-variables.write";

    public static IReadOnlySet<string> Allowed { get; } =
        new HashSet<string>([Read, Write], StringComparer.Ordinal);
}

public static class SharedVariableCallerKinds
{
    public const string User = "user";
    public const string Client = "client";
    public const string Workflow = "workflow";
    public const string System = "system";
}

public sealed record SharedVariableCaller(
    string Kind,
    string Id,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Scopes);

public sealed record SharedVariableDto(
    long Id,
    string Key,
    string DataType,
    bool IsArray,
    bool Nullable,
    string? Validation,
    string? Description,
    bool HasValue,
    JsonElement? Value,
    string Status,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long ValueRevision = 0,
    DateTimeOffset? HistoryPrunedAt = null);

public sealed record SharedVariableListRequest(
    string? Search = null,
    string? Status = null,
    int Page = 1,
    int PageSize = 50,
    bool IncludeArchived = false);

public sealed record CreateSharedVariableRequest(
    string Key,
    string DataType,
    bool IsArray,
    bool Nullable,
    bool HasValue,
    JsonElement? Value,
    string? Validation = null,
    string? Description = null,
    string? RequestId = null,
    string? Reason = null);

public sealed record UpdateSharedVariableRequest(
    JsonElement Value,
    long ExpectedRevision,
    string? RequestId = null,
    string? Reason = null,
    string? Description = null);

public sealed record ArchiveSharedVariableRequest(
    long ExpectedRevision,
    string? RequestId = null,
    string? Reason = null);

public sealed record ReactivateSharedVariableRequest(
    long ExpectedRevision,
    string? RequestId = null,
    string? Reason = null);

public sealed record SharedVariableRevisionDto(
    long Revision,
    string Operation,
    bool ValueChanged,
    bool HasValue,
    JsonElement? Value,
    string CallerKind,
    string CallerId,
    string Source,
    string? Reason,
    long? WorkflowDefinitionId,
    long? InstanceId,
    long? NodeExecutionId,
    int? SourceActionId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Value-free correlation between a workflow-local alias and the durable
/// shared-variable revision produced by one workflow action.
/// </summary>
public sealed record SharedVariableWriteCorrelationDto(
    string Alias,
    string Key,
    long Revision,
    bool ValueChanged);

public sealed record SharedVariableLifecycleBlockersDto(
    long PublishedDefinitionCount,
    long RunningInstanceCount,
    long OpenJobCount,
    IReadOnlyList<string> Reasons)
{
    public bool CanArchive => Reasons.Count == 0;
}

/// <summary>
/// Value-free catalog metadata for one workflow-local shared alias. Instance
/// APIs expose this contract without copying deployment-wide values into an
/// instance response or history.
/// </summary>
public sealed record SharedVariableBindingMetadataDto(
    string Alias,
    string Key,
    string Access,
    string DataType,
    bool IsArray,
    bool Nullable,
    string? Validation,
    string Status,
    long Revision,
    bool HasValue,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    long ValueRevision = 0);

public sealed record SharedVariableClientDto(
    long Id,
    string ClientId,
    string DisplayName,
    IReadOnlyList<string> Scopes,
    string Status,
    long Revision,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RevokedAt);

public sealed record CreateSharedVariableClientRequest(
    string ClientId,
    string DisplayName,
    IReadOnlyCollection<string> Scopes,
    DateTimeOffset? ExpiresAt = null);

public sealed record CreateSharedVariableClientResult(
    SharedVariableClientDto Client,
    string ClientSecret);

public sealed record RotateSharedVariableClientSecretRequest(
    long ExpectedRevision,
    int GracePeriodHours = 24);

public sealed record RotateSharedVariableClientSecretResult(
    SharedVariableClientDto Client,
    string ClientSecret);

public sealed record RevokeSharedVariableClientRequest(
    long ExpectedRevision,
    string? Reason = null);

public sealed record SharedVariableClientAuthentication(
    long Id,
    string ClientId,
    string DisplayName,
    IReadOnlyList<string> Scopes,
    long SecretVersion);
