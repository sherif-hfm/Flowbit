namespace Flowbit.Infrastructure.Entities;

public sealed class ConditionalBoundarySubscriptionEntity
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public long WorkflowDefinitionId { get; set; }
    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }
    public string WorkflowKey { get; set; } = string.Empty;
    public long HostTokenId { get; set; }
    public ExecutionTokenEntity? HostToken { get; set; }
    public Guid HostActivationId { get; set; }
    public int BoundaryNodeId { get; set; }
    public string BoundaryNodeName { get; set; } = string.Empty;
    public int AttachedToNodeId { get; set; }
    public int OutgoingFlowId { get; set; }
    public string Condition { get; set; } = string.Empty;
    public string DeliveryMode { get; set; } = string.Empty;
    public bool CancelActivity { get; set; }
    public bool IsConditionTrue { get; set; }
    public long Occurrence { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<WorkflowJobEntity> Jobs { get; set; } = [];
}
