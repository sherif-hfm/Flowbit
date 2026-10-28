using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Abstractions;

public interface IBusinessCalendarRepository
{
    Task<IReadOnlyList<BusinessCalendarRecord>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Finds a calendar by name, ignoring case.</summary>
    Task<BusinessCalendarRecord?> GetByNameAsync(string name, CancellationToken cancellationToken);

    Task<BusinessCalendarRecord> CreateAsync(
        string name,
        string? description,
        BusinessCalendarContent content,
        CancellationToken cancellationToken);

    Task<BusinessCalendarRecord?> UpdateAsync(
        long id,
        string? description,
        BusinessCalendarContent content,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a calendar unless a published definition, a running instance, or an open job uses it, which is a conflict.
    /// </summary>
    Task<bool> DeleteAsync(long id, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Records the calendars a definition version names, locking them so a concurrent delete waits for this transaction.
    /// </summary>
    Task ReplaceDefinitionReferencesAsync(
        long workflowDefinitionId,
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken);
}

/// <summary>
/// Loads business calendars for computing due times. Each calendar is loaded once per request or Worker scope and kept for
/// that scope.
/// </summary>
public interface IBusinessCalendarResolver
{
    Task<BusinessCalendarLookup> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a work item's due time from its due value and creation time. A working duration that cannot be counted
    /// gives no due time and logs a warning; it never fails the transition.
    /// </summary>
    Task<DateTimeOffset?> ResolveDueAtAsync(
        UserTaskDueValue? value,
        DateTimeOffset createdAt,
        long instanceId,
        int nodeId,
        CancellationToken cancellationToken);
}

public interface IBusinessCalendarService
{
    Task<IReadOnlyList<BusinessCalendarDto>> ListAsync(CancellationToken cancellationToken);

    Task<BusinessCalendarDto> CreateAsync(
        CreateBusinessCalendarRequest request,
        CancellationToken cancellationToken);

    Task<BusinessCalendarDto?> UpdateAsync(
        long id,
        UpdateBusinessCalendarRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(long id, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken);

    BusinessCalendarPreviewDto Preview(BusinessCalendarPreviewRequest request);

    IReadOnlyList<string> ListTimeZones();
}
