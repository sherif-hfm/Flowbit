using System.Text.Json;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Models;

/// <summary>
/// The stored lifecycle of a call invocation. <c>resultReady</c> is not one of them: it is
/// a projection of a running invocation that has a result row, because a child's
/// transaction never writes the invocation.
/// </summary>
public static class CallInvocationStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";

    /// <summary>The call's failure was delivered, caught by a boundary or faulting the parent.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// The owning parent activation abandoned the call. This differs from a child that
    /// was cancelled on its own, which is a failure the parent handles.
    /// </summary>
    public const string Cancelled = "cancelled";

    /// <summary>The read-only projection of a running invocation with a result row.</summary>
    public const string ResultReady = "resultReady";

    public static bool IsSettled(string state) =>
        state is Completed or Failed or Cancelled;
}

/// <summary>What a finished child means to its call, which can differ from the child's status.</summary>
public static class CallResultOutcomes
{
    public const string Completed = "completed";
    public const string Faulted = "faulted";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Values copied at one moment. A missing source stays distinct from an explicit JSON null.
/// </summary>
public sealed record CallValueSnapshot(
    IReadOnlyDictionary<string, JsonElement> Values,
    IReadOnlyList<string> Missing);

public sealed record CallInvocationCreateRecord(
    long ParentInstanceId,
    long ParentTokenId,
    Guid ActivationId,
    int ParentNodeId,
    long? ParentNodeExecutionId,
    long RootInstanceId,
    int ChildDepth,
    long ParentDefinitionId,
    string TargetWorkflowKey,
    CallActivityModel Contract,
    CallValueSnapshot? Inputs,
    bool CapacityReserved,
    string? CausedBy,
    string? FailureCode = null,
    string? FailureDescription = null);

public sealed record CallInvocationRecord(
    long Id,
    long ParentInstanceId,
    long ParentTokenId,
    Guid ActivationId,
    int ParentNodeId,
    long? ParentNodeExecutionId,
    long RootInstanceId,
    int ChildDepth,
    long ParentDefinitionId,
    string TargetWorkflowKey,
    long? ChildDefinitionId,
    int? ChildStartEventId,
    long? ChildInstanceId,
    string State,
    bool CapacityReserved,
    CallActivityModel? Contract,
    CallValueSnapshot? Inputs,
    string? FailureCode,
    string? FailureDescription,
    string? ChildFaultCode,
    string? CausedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? AdmittedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? SettledAt);

public sealed record CallResultRecord(
    long ChildInstanceId,
    long InvocationId,
    string Outcome,
    CallValueSnapshot? Outputs,
    string? FailureCode,
    string? FailureDescription,
    string? ChildFaultCode,
    DateTimeOffset CapturedAt);

/// <summary>The call identity of an instance, read without locking it.</summary>
public sealed record InstanceCallIdentityRecord(long? RootInstanceId, int Depth);

/// <summary>Links a child to its call while it is being created.</summary>
public sealed record CallChildLinkRecord(long InvocationId, long RootInstanceId, int Depth);

/// <summary>A page of one parent's call visits with their read-side context.</summary>
public sealed record CallInvocationListRow(
    CallInvocationRecord Invocation,
    string? ChildStatus,
    int? ChildDefinitionVersion,
    bool HasResult,
    string? ResultOutcome,
    long OpenIncidentCount);

public sealed record CallCountsRecord(long Total, long Unsettled);

/// <summary>A published definition version that calls a family.</summary>
public sealed record WorkflowCallerRecord(
    long WorkflowDefinitionId,
    string WorkflowKey,
    string Name,
    int Version,
    bool IsDefault,
    int NodeId);

/// <summary>
/// What deleting a definition would do. The deletion command and its preview share this
/// assessment, so a preview never approximates the successor.
/// </summary>
public sealed record WorkflowDefinitionDeletionAssessment(
    bool Exists,
    string? WorkflowKey,
    bool IsDefault,
    bool Allowed,
    IReadOnlyList<string> Blockers,
    long? SuccessorId,
    int? SuccessorVersion,
    bool NoDefault);
