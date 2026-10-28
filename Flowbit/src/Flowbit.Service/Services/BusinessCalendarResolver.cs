using Microsoft.Extensions.Logging;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Loads each named calendar once per scope, so every due time computed in a request or Worker scope uses the same
/// calendar. Saved due times never change when a calendar is edited later.
/// </summary>
public sealed class BusinessCalendarResolver(
    IBusinessCalendarRepository repository,
    ILogger<BusinessCalendarResolver> logger) : IBusinessCalendarResolver
{
    private readonly Dictionary<string, BusinessCalendarLookup> lookups = new(StringComparer.OrdinalIgnoreCase);

    public async Task<BusinessCalendarLookup> GetAsync(string name, CancellationToken cancellationToken)
    {
        var key = name.Trim();
        if (lookups.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var record = await repository.GetByNameAsync(key, cancellationToken);
        BusinessCalendarLookup lookup;
        if (record is null)
        {
            lookup = new BusinessCalendarLookup(null, $"business calendar '{key}' does not exist");
        }
        else if (!BusinessCalendarTimeZones.TryFind(record.Content.TimeZone, out var zone, out _))
        {
            lookup = new BusinessCalendarLookup(
                null,
                $"business calendar '{record.Name}' uses time zone '{record.Content.TimeZone}', which this server does not know");
        }
        else
        {
            lookup = new BusinessCalendarLookup(new BusinessCalendar(record.Name, record.Content, zone), null);
        }

        lookups[key] = lookup;
        return lookup;
    }

    public async Task<DateTimeOffset?> ResolveDueAtAsync(
        UserTaskDueValue? value,
        DateTimeOffset createdAt,
        long instanceId,
        int nodeId,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Working is not WorkingDuration working)
        {
            return value.ResolveAt(createdAt);
        }

        var lookup = await GetAsync(value.Calendar!, cancellationToken);
        var problem = lookup.Problem;
        if (lookup.Calendar is { } calendar && calendar.TryAdd(createdAt, working, out var dueAt, out problem))
        {
            return dueAt;
        }

        logger.LogWarning(
            "User task #{NodeId} dueDate could not be counted in business calendar '{Calendar}' for instance {InstanceId} ({Problem}); creating the work item without it.",
            nodeId,
            value.Calendar,
            instanceId,
            problem);
        return null;
    }
}
