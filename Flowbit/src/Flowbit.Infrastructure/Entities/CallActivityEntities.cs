using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

/// <summary>
/// One durable visit to a call activity. It is written only while holding the parent
/// instance's lock, and it outlives history retention as the parent/child relationship.
/// </summary>
public sealed class CallActivityExecutionEntity
{
    public long Id { get; set; }
    public long ParentInstanceId { get; set; }
    public WorkflowInstanceEntity? ParentInstance { get; set; }
    public long ParentTokenId { get; set; }
    public ExecutionTokenEntity? ParentToken { get; set; }
    public Guid ActivationId { get; set; }
    public int ParentNodeId { get; set; }

    /// <summary>The visit's audit row, which history retention can remove.</summary>
    public long? ParentNodeExecutionId { get; set; }
    public NodeExecutionEntity? ParentNodeExecution { get; set; }

    // A plain column rather than a foreign key, so creating a nested child never
    // waits on the root's row lock.
    public long RootInstanceId { get; set; }
    public int ChildDepth { get; set; }
    public long ParentDefinitionId { get; set; }
    public WorkflowDefinitionEntity? ParentDefinition { get; set; }
    public string TargetWorkflowKey { get; set; } = string.Empty;
    public long? ChildDefinitionId { get; set; }
    public WorkflowDefinitionEntity? ChildDefinition { get; set; }
    public int? ChildStartEventId { get; set; }
    public long? ChildInstanceId { get; set; }
    public WorkflowInstanceEntity? ChildInstance { get; set; }
    public string State { get; set; } = "queued";
    public bool CapacityReserved { get; set; }
    public JsonDocument? ContractJson { get; set; }
    public JsonDocument? InputValuesJson { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureDescription { get; set; }
    public string? ChildFaultCode { get; set; }

    /// <summary>The actor whose transition entered the call. The child runs as the system actor.</summary>
    public string? CausedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? AdmittedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public DateTimeOffset? SnapshotsPrunedAt { get; set; }
}

/// <summary>
/// What a child ended with while its invocation was unsettled. It is written once while
/// holding the child instance's lock and never changes, apart from retention clearing
/// its captured values after the parent has finished.
/// </summary>
public sealed class CallActivityResultEntity
{
    public long ChildInstanceId { get; set; }
    public WorkflowInstanceEntity? ChildInstance { get; set; }
    public long InvocationId { get; set; }
    public CallActivityExecutionEntity? Invocation { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public JsonDocument? OutputValuesJson { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureDescription { get; set; }
    public string? ChildFaultCode { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

/// <summary>
/// One call node of a definition version and the family it targets, so administrators
/// can see which published workflows call a family.
/// </summary>
public sealed class WorkflowDefinitionCallTargetEntity
{
    public long WorkflowDefinitionId { get; set; }
    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }
    public int NodeId { get; set; }
    public string TargetWorkflowKey { get; set; } = string.Empty;
}
