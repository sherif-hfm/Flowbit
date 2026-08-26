using System.Text.Json;

namespace Flowbit.Service.Models;

public static class SharedVariableOperations
{
    public const string Create = "create";
    public const string Set = "set";
    public const string DeleteValue = "deleteValue";
    public const string UpdateDescription = "updateDescription";
    public const string Archive = "archive";
    public const string Reactivate = "reactivate";
}

public static class SharedVariableSources
{
    public const string Api = "api";
    public const string Workflow = "workflow";
    public const string Definition = "definition";
    public const string System = "system";
}

public sealed record SharedVariableRecord(
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
    long ValueRevision = 0);

public sealed record SharedVariableValueStamp(
    string Key,
    bool HasValue,
    long ValueRevision,
    string Status);

public sealed record SharedVariableCurrentValueRecord(
    long SharedVariableId,
    string Key,
    string DataType,
    bool IsArray,
    bool Nullable,
    JsonElement Value,
    long Revision,
    DateTimeOffset SetAt);

public sealed record SharedVariableRevisionRecord(
    long Id,
    long SharedVariableId,
    long Revision,
    string Operation,
    bool ValueChanged,
    bool HasValue,
    JsonElement? Value,
    string CallerKind,
    string CallerId,
    string Source,
    string? RequestId,
    string? Reason,
    long? WorkflowDefinitionId,
    long? InstanceId,
    long? NodeExecutionId,
    int? SourceActionId,
    DateTimeOffset CreatedAt);

public sealed record SharedVariableLifecycleBlockersRecord(
    long PublishedDefinitionCount,
    long RunningInstanceCount,
    long OpenJobCount,
    IReadOnlyList<string> Reasons);

public sealed record SharedVariableCallerRecord(
    string Kind,
    string Id,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Scopes);

public sealed record SharedVariableCreateCommand(
    string Key,
    string DataType,
    bool IsArray,
    bool Nullable,
    string? Validation,
    string? Description,
    bool HasValue,
    JsonElement? Value,
    SharedVariableCallerRecord Caller,
    string Source,
    string? RequestId,
    string? RequestFingerprint,
    string? Reason);

public sealed record SharedVariableWriteCommand(
    string Key,
    JsonElement? Value,
    bool DeleteValue,
    long? ExpectedRevision,
    SharedVariableCallerRecord Caller,
    string Source,
    string? RequestId = null,
    string? RequestFingerprint = null,
    string? Reason = null,
    string? Description = null,
    long? WorkflowDefinitionId = null,
    long? InstanceId = null,
    long? NodeExecutionId = null,
    int? SourceActionId = null,
    bool DescriptionOnly = false,
    long? ExpectedValueRevision = null);

public sealed record SharedVariableLifecycleCommand(
    string Key,
    long ExpectedRevision,
    SharedVariableCallerRecord Caller,
    string Source,
    string Operation,
    string? RequestId,
    string? RequestFingerprint,
    string? Reason);

public sealed record SharedVariableMutationResult(
    SharedVariableRecord Variable,
    SharedVariableRevisionRecord Revision,
    bool IsIdempotentReplay);

public sealed record SharedVariableDefinitionBindingProjection(
    string SharedKey,
    string Access,
    string? Alias = null);

public sealed record SharedVariableClientRecord(
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

public sealed record SharedVariableClientSecretRecord(
    long Id,
    long ClientId,
    int Version,
    string Algorithm,
    int Iterations,
    byte[] Salt,
    byte[] Digest,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ValidUntil,
    DateTimeOffset? RevokedAt);

public sealed record SharedVariableClientCreateCommand(
    string ClientId,
    string DisplayName,
    IReadOnlyCollection<string> Scopes,
    DateTimeOffset? ExpiresAt,
    string CreatedByKind,
    string CreatedById,
    string Algorithm,
    int Iterations,
    byte[] Salt,
    byte[] Digest);

public sealed record SharedVariableClientUpdateCommand(
    long ClientId,
    long ExpectedRevision,
    string DisplayName,
    IReadOnlyCollection<string> Scopes,
    DateTimeOffset? ExpiresAt,
    string UpdatedByKind,
    string UpdatedById);

public sealed record SharedVariableClientRotateCommand(
    long ClientId,
    long ExpectedRevision,
    DateTimeOffset PreviousSecretValidUntil,
    string RotatedByKind,
    string RotatedById,
    string Algorithm,
    int Iterations,
    byte[] Salt,
    byte[] Digest);

public sealed record SharedVariableClientRevokeCommand(
    long ClientId,
    long ExpectedRevision,
    string RevokedByKind,
    string RevokedById,
    string? Reason);
