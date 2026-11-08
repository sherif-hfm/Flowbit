namespace Flowbit.Shared.Dtos;

/// <summary>
/// The external body of one lifecycle event, as signed and sent to webhook receivers.
/// PostgreSQL capture triggers are the only producer of these bytes; receivers and tests
/// read them with these records. Every field listed for an event type is present, and
/// fields that belong only to another type are absent.
/// </summary>
public sealed record LifecycleEventEnvelopeDto<TData>(
    int SchemaVersion,
    Guid EventId,
    string Type,
    DateTimeOffset Timestamp,
    string? WorkflowKey,
    long? WorkflowDefinitionId,
    int? WorkflowVersion,
    long? InstanceId,
    TData Data);

/// <summary>Data of <c>instance.started</c>, <c>instance.completed</c>, <c>instance.faulted</c>, and <c>instance.cancelled</c>.</summary>
public sealed record InstanceLifecycleEventDataDto(string Status);

/// <summary>Data of <c>instance.reactivated</c>; <c>PreviousStatus</c> is <c>completed</c> or <c>cancelled</c>.</summary>
public sealed record InstanceReactivatedEventDataDto(string Status, string PreviousStatus);

/// <summary>
/// Data of <c>task.created</c> and <c>task.completed</c>. Multi-instance children carry their execution
/// and zero-based item index; normal tasks carry null for both.
/// </summary>
public sealed record TaskLifecycleEventDataDto(
    long UserTaskId,
    long ExecutionTokenId,
    int NodeId,
    string? NodeExternalId,
    long? NodeExecutionId,
    long? MultiInstanceExecutionId,
    int? ItemIndex,
    string TaskStatus);

/// <summary>Data of <c>task.assigned</c>; <c>AssignmentOperation</c> is <c>assigned</c> or <c>reassigned</c>.</summary>
public sealed record TaskAssignedEventDataDto(
    long UserTaskId,
    long ExecutionTokenId,
    int NodeId,
    string? NodeExternalId,
    long? NodeExecutionId,
    long? MultiInstanceExecutionId,
    int? ItemIndex,
    string TaskStatus,
    string AssignmentOperation);

/// <summary>Data of <c>task.cancelled</c>; <c>PreviousTaskStatus</c> is <c>active</c> or <c>pending</c>.</summary>
public sealed record TaskCancelledEventDataDto(
    long UserTaskId,
    long ExecutionTokenId,
    int NodeId,
    string? NodeExternalId,
    long? NodeExecutionId,
    long? MultiInstanceExecutionId,
    int? ItemIndex,
    string TaskStatus,
    string PreviousTaskStatus);

/// <summary>Data of <c>delegation.granted</c>; <c>AcceptanceState</c> is <c>pending</c> or <c>notRequired</c>.</summary>
public sealed record DelegationGrantedEventDataDto(long DelegationId, string AcceptanceState);

/// <summary>
/// Data of <c>incident.opened</c>. <c>JobId</c> is the incident's original job, which survives retention
/// detaching the live job link.
/// </summary>
public sealed record IncidentOpenedEventDataDto(
    long IncidentId,
    long JobId,
    long? ExecutionTokenId,
    int? NodeId,
    string? NodeExternalId,
    string Status);

/// <summary>Data of the <c>webhook.test</c> event that the test route queues for one subscription.</summary>
public sealed record WebhookTestEventDataDto(Guid SubscriptionId);
