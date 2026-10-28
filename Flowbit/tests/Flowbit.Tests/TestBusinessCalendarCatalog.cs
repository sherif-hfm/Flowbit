using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;

namespace Flowbit.Tests;

/// <summary>An in-memory calendar catalog for definition readiness checks, found by name ignoring case.</summary>
public sealed class TestBusinessCalendarCatalog(params string[] names) : IBusinessCalendarRepository
{
    private readonly Dictionary<string, BusinessCalendarRecord> calendars = names
        .Select((name, index) => new BusinessCalendarRecord(
            index + 1,
            name,
            null,
            BusinessCalendarContentRules.Parse(
                "Etc/UTC",
                new BusinessCalendarWeekDto(Monday: [new WorkingPeriodDto("09:00", "17:00")]),
                null,
                checkTimeZone: false),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch))
        .ToDictionary(calendar => calendar.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The calendars that the example catalog and validation tests name.</summary>
    public static TestBusinessCalendarCatalog Examples() => new("examples.weekdays", "uk-office");

    public Task<BusinessCalendarRecord?> GetByNameAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(calendars.GetValueOrDefault(name.Trim()));

    public Task<IReadOnlyList<BusinessCalendarRecord>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BusinessCalendarRecord>>(calendars.Values.ToList());

    public Task<BusinessCalendarRecord> CreateAsync(
        string name,
        string? description,
        BusinessCalendarContent content,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<BusinessCalendarRecord?> UpdateAsync(
        long id,
        string? description,
        BusinessCalendarContent content,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> DeleteAsync(long id, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task ReplaceDefinitionReferencesAsync(
        long workflowDefinitionId,
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
