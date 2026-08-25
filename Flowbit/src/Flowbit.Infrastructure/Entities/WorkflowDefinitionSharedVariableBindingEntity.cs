namespace Flowbit.Infrastructure.Entities;

public sealed class WorkflowDefinitionSharedVariableBindingEntity
{
    public long Id { get; set; }
    public long WorkflowDefinitionId { get; set; }
    public long SharedVariableId { get; set; }
    public string Alias { get; set; } = string.Empty;
    public string SharedKey { get; set; } = string.Empty;
    public string Access { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public WorkflowDefinitionEntity WorkflowDefinition { get; set; } = null!;
    public SharedVariableEntity SharedVariable { get; set; } = null!;
}
