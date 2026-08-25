namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowDefinitionSharedVariableDependencyEntity
{
    public long Id { get; set; }
    public long WorkflowDefinitionId { get; set; }
    public long SharedVariableId { get; set; }
    public string SharedKey { get; set; } = string.Empty;
    public int NodeId { get; set; }
    public string? NodeExternalId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public WorkflowDefinitionEntity WorkflowDefinition { get; set; } = null!;
    public SharedVariableEntity SharedVariable { get; set; } = null!;
}
