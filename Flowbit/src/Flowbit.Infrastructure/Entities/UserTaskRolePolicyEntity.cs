using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

/// <summary>An immutable authorization snapshot shared by one task activation.</summary>
public sealed class UserTaskRolePolicyEntity
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public long WorkflowDefinitionId { get; set; }
    public int NodeId { get; set; }
    public List<string> Roles { get; set; } = [];
    public JsonDocument OutgoingFlowRolesJson { get; set; } = JsonDocument.Parse("{}");
    public DateTimeOffset CreatedAt { get; set; }
}
