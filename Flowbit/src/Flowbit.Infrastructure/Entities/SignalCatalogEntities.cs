using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

/// <summary>
/// A signal catalog entry (EVENT-04F): a signal name with its description, payload contract,
/// and throw permissions. Only definition saves and publications, reactivation, API throws,
/// cancels, and retries, and the catalog's own routes read it; no runtime path does.
/// </summary>
public sealed class SignalCatalogEntryEntity
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The payload fields, a JSON array of <c>SignalCatalogFieldDto</c>.</summary>
    public JsonElement Fields { get; set; }

    /// <summary>The roles that narrow who may throw the name, a JSON array of strings.</summary>
    public JsonElement ThrowRoles { get; set; }

    public bool AllowClients { get; set; } = true;

    public bool AllowWorkflows { get; set; } = true;

    public string Status { get; set; } = "active";

    public DateTimeOffset CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }
}

/// <summary>
/// One signal event of a workflow definition version and the name it uses, written when the
/// version is saved. Names without a catalog entry are recorded too, so an entry created later
/// finds its users without scanning definitions.
/// </summary>
public sealed class WorkflowDefinitionSignalNameEntity
{
    public long WorkflowDefinitionId { get; set; }

    public WorkflowDefinitionEntity? WorkflowDefinition { get; set; }

    public int NodeId { get; set; }

    public string SignalName { get; set; } = string.Empty;

    /// <summary>catch, boundary, start, throw, or end.</summary>
    public string Usage { get; set; } = string.Empty;
}
