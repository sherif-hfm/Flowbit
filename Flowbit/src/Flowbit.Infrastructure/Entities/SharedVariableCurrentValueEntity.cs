using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class SharedVariableCurrentValueEntity
{
    public long SharedVariableId { get; set; }
    public long SourceRevisionId { get; set; }
    public long Revision { get; set; }
    public JsonDocument? ValueJson { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset SetAt { get; set; } = DateTimeOffset.UtcNow;

    public SharedVariableEntity SharedVariable { get; set; } = null!;
    public SharedVariableRevisionEntity SourceRevision { get; set; } = null!;
}
