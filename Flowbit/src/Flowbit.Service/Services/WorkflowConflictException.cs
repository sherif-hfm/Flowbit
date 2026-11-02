namespace Flowbit.Service.Services;

public class WorkflowConflictException(string message) : Exception(message);

/// <summary>
/// A call above this instance was cancelled, so its descendants stop accepting new work
/// while their callCancel jobs catch up. Commands return 409, and a job completes as stale
/// instead of opening an incident.
/// </summary>
public sealed class WorkflowCallFenceException(long instanceId, long cancelledInvocationId)
    : WorkflowConflictException(
        $"Workflow instance #{instanceId} is being cancelled because a call above it was cancelled.")
{
    public long InstanceId { get; } = instanceId;

    public long CancelledInvocationId { get; } = cancelledInvocationId;
}

/// <summary>
/// An instance's persisted call ancestry is missing, cyclic, or inconsistent. The fence
/// cannot decide, so it fails closed: nothing transitions, and a job opens an operational
/// incident rather than completing as stale.
/// </summary>
public sealed class WorkflowCallChainException(long instanceId, string problem)
    : InvalidOperationException($"Workflow instance #{instanceId} has an inconsistent call chain: {problem}")
{
    public long InstanceId { get; } = instanceId;
}

public sealed class BusinessKeyConflictException(long existingInstanceId)
    : WorkflowConflictException("A workflow instance already owns this business key.")
{
    public long ExistingInstanceId { get; } = existingInstanceId;
}

public sealed class IdempotencyKeyConflictException(long existingInstanceId)
    : WorkflowConflictException("A workflow instance already owns this idempotency key.")
{
    public long ExistingInstanceId { get; } = existingInstanceId;
}

public sealed class MessageDeliveryConflictException(
    string code,
    long instanceId,
    int sourceNodeId,
    string message)
    : WorkflowConflictException(message)
{
    public string Code { get; } = code;

    public long InstanceId { get; } = instanceId;

    public int SourceNodeId { get; } = sourceNodeId;
}
