using System.Text.Json.Serialization;

namespace Flowbit.Shared.Dtos;

/// <summary>
/// A signal catalog entry (EVENT-04F): a signal name with its description, its payload
/// contract, and who may throw it. Saved definitions and API throws of the name are checked
/// against it; running instances never read it.
/// </summary>
/// <param name="Name">The signal name, under the signal name rule. Fixed at creation.</param>
/// <param name="Description">What the signal means, or null.</param>
/// <param name="Status"><c>active</c> or <c>archived</c>. An archived name cannot be thrown or used by a saved definition.</param>
/// <param name="Fields">The payload contract, in order. A throw may send only these members; an entry without fields accepts only an empty payload.</param>
/// <param name="ThrowRoles">Roles that narrow who may throw, cancel, or retry a broadcast of the name, on top of Signals.RequiredRole. Empty narrows nothing.</param>
/// <param name="AllowClients">Whether managed API clients with <c>signals.throw</c> may throw the name.</param>
/// <param name="AllowWorkflows">Whether published workflows may throw the name.</param>
/// <param name="CanThrow">Whether the caller could throw the name now.</param>
/// <param name="CreatedAt">When the entry was created.</param>
/// <param name="CreatedBy">Who created the entry; null for a managed API client.</param>
/// <param name="UpdatedAt">When the entry last changed; send it back as <c>expectedUpdatedAt</c>.</param>
/// <param name="UpdatedBy">Who last changed the entry; null for a managed API client.</param>
/// <param name="ArchivedAt">When the entry was archived, or null while it is active.</param>
public sealed record SignalCatalogEntryDto(
    string Name,
    string? Description,
    string Status,
    IReadOnlyList<SignalCatalogFieldDto> Fields,
    IReadOnlyList<string> ThrowRoles,
    bool AllowClients,
    bool AllowWorkflows,
    bool CanThrow,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy,
    DateTimeOffset? ArchivedAt);

/// <summary>One payload field of a signal catalog entry.</summary>
/// <param name="Name">The payload member name: 1 to 100 letters, digits, '_', or '-', starting with a letter or '_'; unique ignoring case.</param>
/// <param name="DataType">string, number, boolean, date, datetime, or json. A json field accepts any value and leaves its inside unchecked.</param>
/// <param name="IsArray">Whether the value is an array of <paramref name="DataType"/>.</param>
/// <param name="Required">Whether every throw must send the field. No field may be JSON null: a throw leaves out an optional field it has no value for.</param>
/// <param name="Description">What the field holds, or null; at most 300 characters.</param>
[JsonNumberHandling(JsonNumberHandling.Strict)]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SignalCatalogFieldDto(
    string Name,
    string DataType,
    bool IsArray = false,
    bool Required = false,
    string? Description = null);

/// <summary>A page of catalog entries, ordered by name, with the catalog mode in effect.</summary>
/// <param name="Items">The entries of the page.</param>
/// <param name="Page">The 1-based page number.</param>
/// <param name="PageSize">The page size, from 1 to 200.</param>
/// <param name="TotalCount">The number of entries that match.</param>
/// <param name="Mode">The value of Signals.CatalogMode in effect: <c>optional</c> or <c>required</c>.</param>
/// <param name="SignalRoles">The roles of Signals.RequiredRole, which a user needs as well as an entry's throw roles; empty for a managed API client.</param>
public sealed record SignalCatalogListDto(
    IReadOnlyList<SignalCatalogEntryDto> Items,
    int Page,
    int PageSize,
    long TotalCount,
    string Mode,
    IReadOnlyList<string> SignalRoles);

/// <summary>An entry as the hosted editor's name suggestions list it, without its fields.</summary>
/// <param name="Name">The signal name.</param>
/// <param name="Description">The first 120 characters of the description, or null.</param>
/// <param name="AllowWorkflows">Whether workflows may throw the name.</param>
/// <param name="FieldCount">The number of payload fields.</param>
public sealed record SignalCatalogEntrySummaryDto(
    string Name,
    string? Description,
    bool AllowWorkflows,
    int FieldCount);

/// <summary>Creates a catalog entry. Every member but the name can change later.</summary>
/// <param name="Name">The signal name, under the signal name rule.</param>
/// <param name="Description">What the signal means; at most 1,000 characters.</param>
/// <param name="Fields">At most 100 payload fields.</param>
/// <param name="ThrowRoles">At most 100 roles that narrow who may throw the name.</param>
/// <param name="AllowClients">Whether managed API clients may throw the name; true when omitted.</param>
/// <param name="AllowWorkflows">Whether workflows may throw the name; true when omitted.</param>
[JsonNumberHandling(JsonNumberHandling.Strict)]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSignalCatalogEntryRequest(
    string? Name,
    string? Description = null,
    IReadOnlyList<SignalCatalogFieldDto?>? Fields = null,
    IReadOnlyList<string?>? ThrowRoles = null,
    bool AllowClients = true,
    bool AllowWorkflows = true);

/// <summary>
/// Replaces everything about an entry but its name. The fields, the throw roles, and both
/// switches are required, so a request that leaves one out cannot widen who may throw the name
/// or empty its contract by accident.
/// </summary>
/// <param name="Description">What the signal means; at most 1,000 characters. Omitted or null removes it.</param>
/// <param name="Fields">At most 100 payload fields; required, <c>[]</c> for none.</param>
/// <param name="ThrowRoles">At most 100 roles that narrow who may throw the name; required, <c>[]</c> for none.</param>
/// <param name="AllowClients">Whether managed API clients may throw the name; required.</param>
/// <param name="AllowWorkflows">Whether workflows may throw the name; required.</param>
/// <param name="ExpectedUpdatedAt">The entry's updatedAt as last read.</param>
[JsonNumberHandling(JsonNumberHandling.Strict)]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateSignalCatalogEntryRequest(
    DateTimeOffset ExpectedUpdatedAt,
    string? Description,
    [property: JsonRequired] IReadOnlyList<SignalCatalogFieldDto?>? Fields,
    [property: JsonRequired] IReadOnlyList<string?>? ThrowRoles,
    [property: JsonRequired] bool AllowClients,
    [property: JsonRequired] bool AllowWorkflows);

/// <summary>Archives or reactivates an entry.</summary>
/// <param name="ExpectedUpdatedAt">The entry's updatedAt as last read.</param>
[JsonNumberHandling(JsonNumberHandling.Strict)]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SignalCatalogLifecycleRequest(DateTimeOffset ExpectedUpdatedAt);

/// <summary>What a candidate entry would mean for the live workflow versions that use its name.</summary>
/// <param name="Name">The signal name.</param>
/// <param name="LiveVersionCount">The published versions and versions with a running instance that use the name.</param>
/// <param name="Problems">The problems of those versions against the candidate, in version and node order; at most 200.</param>
/// <param name="Truncated">Whether more problems exist than are listed.</param>
public sealed record SignalCatalogImpactDto(
    string Name,
    int LiveVersionCount,
    IReadOnlyList<SignalCatalogProblemDto> Problems,
    bool Truncated);

/// <summary>One problem of a workflow version against a catalog entry.</summary>
/// <param name="WorkflowDefinitionId">The version's ID.</param>
/// <param name="WorkflowKey">The version's workflow key.</param>
/// <param name="WorkflowName">The version's name.</param>
/// <param name="Version">The version number.</param>
/// <param name="NodeId">The signal event the problem is about.</param>
/// <param name="Message">The problem, naming the node, the field, and the types, never a value.</param>
public sealed record SignalCatalogProblemDto(
    long WorkflowDefinitionId,
    string WorkflowKey,
    string WorkflowName,
    int Version,
    int NodeId,
    string Message);

/// <summary>What uses a signal name: the five things that block archiving its entry, and the versions that use it.</summary>
/// <param name="Name">The signal name.</param>
/// <param name="PublishedVersionCount">Published workflow versions that use the name.</param>
/// <param name="RunningVersionCount">Workflow versions with a running instance that use the name.</param>
/// <param name="ActiveWaitCount">Active signal waits for the name.</param>
/// <param name="StartingFamilyCount">Workflow families whose published default starts on the name.</param>
/// <param name="DeliveringBroadcastCount">Broadcasts of the name still delivering.</param>
/// <param name="Versions">A page of the versions that use the name, newest first.</param>
public sealed record SignalCatalogUsageDto(
    string Name,
    long PublishedVersionCount,
    long RunningVersionCount,
    long ActiveWaitCount,
    long StartingFamilyCount,
    long DeliveringBroadcastCount,
    PagedResult<SignalCatalogVersionUseDto> Versions);

/// <summary>A workflow version that uses a signal name.</summary>
/// <param name="WorkflowDefinitionId">The version's ID.</param>
/// <param name="WorkflowKey">The version's workflow key.</param>
/// <param name="WorkflowName">The version's name.</param>
/// <param name="Version">The version number.</param>
/// <param name="IsPublished">Whether the version is published.</param>
/// <param name="NodeIds">The signal events of the version that use the name.</param>
public sealed record SignalCatalogVersionUseDto(
    long WorkflowDefinitionId,
    string WorkflowKey,
    string WorkflowName,
    int Version,
    bool IsPublished,
    IReadOnlyList<int> NodeIds);

/// <summary>A name that live workflow versions use without a catalog entry.</summary>
/// <param name="Name">The signal name.</param>
/// <param name="VersionCount">The live versions that use it.</param>
public sealed record UncatalogedSignalNameDto(string Name, int VersionCount);

/// <summary>The names in use without an entry, at most 200, with the catalog mode in effect.</summary>
/// <param name="Items">The names, ordered by name.</param>
/// <param name="Truncated">Whether more names exist than are listed.</param>
/// <param name="Mode">The value of Signals.CatalogMode in effect.</param>
public sealed record UncatalogedSignalNameListDto(
    IReadOnlyList<UncatalogedSignalNameDto> Items,
    bool Truncated,
    string Mode);
