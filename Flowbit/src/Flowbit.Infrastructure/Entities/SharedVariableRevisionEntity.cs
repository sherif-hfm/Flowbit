using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class SharedVariableRevisionEntity
{
    public long Id { get; set; }
    public long SharedVariableId { get; set; }
    public long Revision { get; set; }
    public string Operation { get; set; } = string.Empty;
    public bool ValueChanged { get; set; }
    public JsonDocument? ValueJson { get; set; }
    public bool HasValue { get; set; }
    public string CallerKind { get; set; } = string.Empty;
    public string CallerId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public string? Reason { get; set; }
    public long? WorkflowDefinitionId { get; set; }
    public long? InstanceId { get; set; }
    public long? NodeExecutionId { get; set; }
    public int? SourceActionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public SharedVariableEntity SharedVariable { get; set; } = null!;
    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }
    public WorkflowInstanceEntity? Instance { get; set; }
    public NodeExecutionEntity? NodeExecution { get; set; }
    public SharedVariableCurrentValueEntity? CurrentValue { get; set; }
}
