using System.Text.Json;

namespace Flowbit.Service.Models;

public sealed record WorkflowVariableWrite(
    string Alias,
    JsonElement Value,
    int? SourceActionId = null,
    long? NodeExecutionId = null,
    string? SetBy = null,
    string? ActingFor = null,
    long? DelegationId = null,
    long? InstanceVariableUpdateAuditId = null,
    string? Reason = null);

public sealed record WorkflowVariableWriteResult(
    string Alias,
    string Scope,
    string? SharedKey,
    long? SharedRevision,
    bool ValueChanged);

