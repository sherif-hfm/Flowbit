using System.Text.Json.Serialization;

namespace Flowbit.Shared.Dtos;

/// <summary>
/// The call that started a child instance. Relations grant no task, claim, cancellation,
/// or administrative permission.
/// </summary>
/// <param name="State">queued, running, resultReady, completed, failed, or cancelled.</param>
/// <param name="CancellationInProgress">
/// True while this instance runs under a cancelled call, at any level above it: it accepts no
/// new work, and a Worker will finish stopping it.
/// </param>
public sealed record ParentCallSummaryDto(
    long InvocationId,
    string State,
    long ParentInstanceId,
    int ParentNodeId,
    string? ParentNodeName,
    long RootInstanceId,
    int Depth,
    bool CancellationInProgress = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MultiInstanceExecutionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? AutomaticMultiInstanceItemId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ItemIndex = null);

public sealed record CallCountsDto(long Total, long Unsettled);

/// <summary>
/// What a token resting on a call is waiting for. It remains an execution token, not a user task.
/// </summary>
public sealed record ExecutionPositionCallDto(
    long InvocationId,
    string State,
    bool CapacityReserved,
    long? ChildInstanceId);

/// <summary>One visit to a call activity, with the version it pinned. It never exposes captured values.</summary>
/// <param name="State">queued, running, resultReady, completed, failed, or cancelled.</param>
/// <param name="Outcome">The result's call outcome when the child has finished: completed, faulted, or cancelled.</param>
/// <param name="OpenIncidentCount">Open incidents on the invocation's own jobs and on the child's durable work.</param>
public sealed record CallActivityExecutionDto(
    long InvocationId,
    int NodeId,
    string? NodeName,
    long TokenId,
    Guid ActivationId,
    string State,
    string? Outcome,
    bool CapacityReserved,
    long? ChildInstanceId,
    string? ChildStatus,
    long OpenIncidentCount,
    string TargetWorkflowKey,
    long? ChildDefinitionId,
    int? ChildDefinitionVersion,
    int? ChildStartEventId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AdmittedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? SettledAt,
    string? FailureCode,
    string? FailureDescription,
    string? ChildFaultCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? MultiInstanceExecutionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? AutomaticMultiInstanceItemId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ItemIndex = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? PinnedChildDefinitionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PinnedChildDefinitionVersion = null);

/// <summary>A published version that calls a family, and whether it passes the contract check.</summary>
public sealed record WorkflowCallerDto(
    long WorkflowId,
    string WorkflowKey,
    string Name,
    int Version,
    bool IsDefault,
    int NodeId,
    string? NodeName,
    bool Compatible,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> Reasons);

public static class CallersPreviewModes
{
    public const string Current = "current";
    public const string Candidate = "candidate";
    public const string Deletion = "deletion";
}

/// <summary>
/// An advisory snapshot of how a family's callers fare against a target version. It takes no
/// reservation and can go stale; the command rechecks its own rules under the family lock.
/// </summary>
/// <param name="Mode">current, candidate, or deletion.</param>
/// <param name="NoDefault">True when no published default exists, or would remain after the deletion.</param>
/// <param name="DeletionAllowed">In deletion mode, whether the deletion would be allowed; otherwise null.</param>
public sealed record CallersPreviewDto(
    string Mode,
    long? EffectiveTargetId,
    int? EffectiveTargetVersion,
    bool NoDefault,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? DeletionAllowed,
    IReadOnlyList<string> DeletionBlockers,
    PagedResult<WorkflowCallerDto> Callers);

/// <summary>What the editor's assistant is told about a call target: declarations, never values.</summary>
public sealed record CallTargetContractDto(
    string WorkflowKey,
    bool HasPublishedDefault,
    int? Version,
    bool CallOnly,
    IReadOnlyList<CallTargetVariableDto> Inputs,
    IReadOnlyList<CallTargetVariableDto> Results);

public sealed record CallTargetVariableDto(
    string Name,
    string DataType,
    bool IsArray,
    bool Required,
    bool MayBeNull);
