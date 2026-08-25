using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class SharedVariableRequestEntity
{
    public long Id { get; set; }
    public string CallerKind { get; set; } = string.Empty;
    public string CallerId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string SharedKey { get; set; } = string.Empty;
    public byte[] RequestHash { get; set; } = [];
    public long SharedVariableId { get; set; }
    public long ResultRevision { get; set; }
    public JsonDocument ResponseJson { get; set; } = JsonDocument.Parse("{}");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public SharedVariableEntity SharedVariable { get; set; } = null!;
}
