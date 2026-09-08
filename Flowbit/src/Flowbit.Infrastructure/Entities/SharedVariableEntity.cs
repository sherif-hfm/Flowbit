namespace Flowbit.Infrastructure.Entities;

public sealed class SharedVariableEntity
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsArray { get; set; }
    public bool Nullable { get; set; }
    public string? Validation { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = "active";
    public long CurrentRevision { get; set; }
    public long ValueRevision { get; set; }
    public string CreatedByKind { get; set; } = string.Empty;
    public string CreatedById { get; set; } = string.Empty;
    public string UpdatedByKind { get; set; } = string.Empty;
    public string UpdatedById { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset? HistoryPrunedAt { get; set; }

    public SharedVariableCurrentValueEntity? CurrentValue { get; set; }
    public List<SharedVariableRevisionEntity> Revisions { get; set; } = [];
    public List<WorkflowDefinitionSharedVariableBindingEntity> DefinitionBindings { get; set; } = [];
}
