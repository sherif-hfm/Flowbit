namespace Flowbit.Infrastructure.Entities;

public sealed class SharedVariableWakeDeliveryEntity
{
    public long Id { get; set; }
    public long WakeId { get; set; }
    public long InstanceId { get; set; }
    public long WorkflowDefinitionId { get; set; }
    public long TokenId { get; set; }
    public Guid ActivationId { get; set; }
    public int NodeId { get; set; }
    public string Status { get; set; } = "pending";
    public Guid? LeaseToken { get; set; }
    public long LeaseGeneration { get; set; }
    public string? LeasedBy { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public SharedVariableWakeEntity Wake { get; set; } = null!;
    public WorkflowInstanceEntity Instance { get; set; } = null!;
    public WorkflowDefinitionEntity WorkflowDefinition { get; set; } = null!;
    public ExecutionTokenEntity Token { get; set; } = null!;
}
