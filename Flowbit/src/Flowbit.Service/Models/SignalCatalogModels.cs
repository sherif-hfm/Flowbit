using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Models;

/// <summary>A signal catalog entry as stored (EVENT-04F).</summary>
public sealed record SignalCatalogEntryRecord(
    long Id,
    string Name,
    string? Description,
    string Status,
    IReadOnlyList<SignalCatalogFieldDto> Fields,
    IReadOnlyList<string> ThrowRoles,
    bool AllowClients,
    bool AllowWorkflows,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy,
    DateTimeOffset? ArchivedAt)
{
    public bool IsActive => string.Equals(Status, SignalCatalogStatuses.Active, StringComparison.Ordinal);
}

/// <summary>The content of an entry to create or write, already normalized and checked.</summary>
public sealed record SignalCatalogEntryContent(
    string? Description,
    IReadOnlyList<SignalCatalogFieldDto> Fields,
    IReadOnlyList<string> ThrowRoles,
    bool AllowClients,
    bool AllowWorkflows);

/// <summary>
/// When a definition is checked against the catalog. Save rules apply at every stage;
/// publication rules at publication and reactivation, and in every catalog write; the
/// <c>required</c>-mode rule at publication only.
/// </summary>
public enum SignalCatalogStage
{
    Draft,
    Publication,
    Reactivation
}

/// <summary>
/// One problem of a definition against the catalog. <see cref="IsSaveProblem"/> is true for
/// the problems that are wrong at any stage, which also refuse a draft.
/// </summary>
public sealed record SignalCatalogDefinitionProblem(
    int NodeId,
    string SignalName,
    bool IsSaveProblem,
    string Message);

/// <summary>A live workflow version that uses a signal name, with the definition to check.</summary>
public sealed record SignalCatalogLiveVersion(
    long WorkflowDefinitionId,
    string WorkflowKey,
    string WorkflowName,
    int Version,
    WorkflowModel Definition);

/// <summary>The five counts that block archiving an entry.</summary>
public sealed record SignalCatalogUsageCounts(
    long PublishedVersionCount,
    long RunningVersionCount,
    long ActiveWaitCount,
    long StartingFamilyCount,
    long DeliveringBroadcastCount)
{
    public bool IsUnused =>
        PublishedVersionCount == 0
        && RunningVersionCount == 0
        && ActiveWaitCount == 0
        && StartingFamilyCount == 0
        && DeliveringBroadcastCount == 0;
}

/// <summary>A workflow version that uses a signal name, for the usage read.</summary>
public sealed record SignalCatalogVersionUseRecord(
    long WorkflowDefinitionId,
    string WorkflowKey,
    string WorkflowName,
    int Version,
    bool IsPublished,
    IReadOnlyList<int> NodeIds);

/// <summary>A name that live versions use without an entry.</summary>
public sealed record UncatalogedSignalNameRecord(string Name, int VersionCount);
