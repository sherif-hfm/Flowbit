using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

public sealed class BusinessCalendarRepository(AppDbContext dbContext) : IBusinessCalendarRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<BusinessCalendarRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var entities = await dbContext.BusinessCalendars
            .AsNoTracking()
            .OrderBy(calendar => calendar.Name)
            .ThenBy(calendar => calendar.Id)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToList();
    }

    public async Task<BusinessCalendarRecord?> GetByNameAsync(string name, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();
        var entity = await dbContext.BusinessCalendars
            .AsNoTracking()
            .SingleOrDefaultAsync(calendar => calendar.Name == trimmed, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<BusinessCalendarRecord> CreateAsync(
        string name,
        string? description,
        BusinessCalendarContent content,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var now = UtcNow();
        var entity = new BusinessCalendarEntity
        {
            Name = name,
            Description = description,
            TimeZone = content.TimeZone,
            WeeklyHours = WeeklyHoursJson(content),
            Holidays = HolidaysJson(content),
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.BusinessCalendars.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            throw new WorkflowConflictException($"A business calendar named '{name}' already exists.");
        }

        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
        }

        return Map(entity);
    }

    public async Task<BusinessCalendarRecord?> UpdateAsync(
        long id,
        string? description,
        BusinessCalendarContent content,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);

        // A non-key lock lets definition saves that hold FOR KEY SHARE on the calendar continue.
        var entity = await dbContext.BusinessCalendars
            .FromSqlInterpolated(
                $"""SELECT * FROM "flowbit"."business_calendars" WHERE "Id" = {id} FOR NO KEY UPDATE""")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return null;
        }

        if (entity.UpdatedAt != expectedUpdatedAt)
        {
            throw StaleConflict(entity.Name);
        }

        var updatedAt = NextTimestamp(entity.UpdatedAt);
        var weeklyHours = WeeklyHoursJson(content);
        var holidays = HolidaysJson(content);
        var updated = await dbContext.BusinessCalendars
            .Where(calendar => calendar.Id == id && calendar.UpdatedAt == entity.UpdatedAt)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(calendar => calendar.Description, description)
                    .SetProperty(calendar => calendar.TimeZone, content.TimeZone)
                    .SetProperty(calendar => calendar.WeeklyHours, weeklyHours)
                    .SetProperty(calendar => calendar.Holidays, holidays)
                    .SetProperty(calendar => calendar.UpdatedAt, updatedAt),
                cancellationToken);
        if (updated == 0)
        {
            throw StaleConflict(entity.Name);
        }

        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
        }

        return new BusinessCalendarRecord(entity.Id, entity.Name, description, content, entity.CreatedAt, updatedAt);
    }

    public async Task<bool> DeleteAsync(
        long id,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);

        // Definition saves and publications lock the calendar FOR KEY SHARE, so this waits for them and then sees
        // what they committed.
        var entity = await dbContext.BusinessCalendars
            .FromSqlInterpolated(
                $"""SELECT * FROM "flowbit"."business_calendars" WHERE "Id" = {id} FOR UPDATE""")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return false;
        }

        if (entity.UpdatedAt != expectedUpdatedAt)
        {
            throw StaleConflict(entity.Name);
        }

        var blockers = await GetBlockersAsync(id, cancellationToken);
        if (blockers.Count > 0)
        {
            throw new WorkflowConflictException(
                $"Business calendar '{entity.Name}' cannot be deleted: {string.Join("; ", blockers)}.");
        }

        await dbContext.BusinessCalendars
            .Where(calendar => calendar.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
        }

        return true;
    }

    public async Task ReplaceDefinitionReferencesAsync(
        long workflowDefinitionId,
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken)
    {
        var distinct = names
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var calendarIds = new List<long>(distinct.Length);
        if (distinct.Length > 0)
        {
            // Locked in name order, so a concurrent delete of one of these calendars waits for this transaction.
            var locked = await dbContext.BusinessCalendars
                .FromSqlInterpolated(
                    $"""SELECT * FROM "flowbit"."business_calendars" WHERE "Name" = ANY({distinct}::citext[]) ORDER BY "Name" FOR KEY SHARE""")
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            foreach (var name in distinct)
            {
                var calendar = locked.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new WorkflowDomainException($"Business calendar '{name}' does not exist.");
                calendarIds.Add(calendar.Id);
            }
        }

        await dbContext.WorkflowDefinitionBusinessCalendars
            .Where(reference => reference.WorkflowDefinitionId == workflowDefinitionId)
            .ExecuteDeleteAsync(cancellationToken);
        if (calendarIds.Count == 0)
        {
            return;
        }

        var now = UtcNow();
        dbContext.WorkflowDefinitionBusinessCalendars.AddRange(calendarIds.Distinct().Select(calendarId =>
            new WorkflowDefinitionBusinessCalendarEntity
            {
                WorkflowDefinitionId = workflowDefinitionId,
                BusinessCalendarId = calendarId,
                CreatedAt = now
            }));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // The same lifecycle blockers as archiving a shared variable: work that could still compute a due time with it.
    private async Task<IReadOnlyList<string>> GetBlockersAsync(long calendarId, CancellationToken cancellationToken)
    {
        var publishedDefinitions = await dbContext.WorkflowDefinitionBusinessCalendars
            .Where(reference => reference.BusinessCalendarId == calendarId
                && reference.WorkflowDefinition.IsPublished)
            .LongCountAsync(cancellationToken);
        var runningInstances = await dbContext.WorkflowInstances
            .Where(instance => instance.Status == "running"
                && dbContext.WorkflowDefinitionBusinessCalendars.Any(reference =>
                    reference.BusinessCalendarId == calendarId
                    && reference.WorkflowDefinitionId == instance.WorkflowDefinitionId))
            .LongCountAsync(cancellationToken);
        var openJobs = await dbContext.WorkflowJobs
            .Where(job => job.Status != WorkflowJobStatuses.Completed
                && job.Status != WorkflowJobStatuses.Cancelled
                && job.Status != WorkflowJobStatuses.Skipped
                && dbContext.WorkflowDefinitionBusinessCalendars.Any(reference =>
                    reference.BusinessCalendarId == calendarId
                    && reference.WorkflowDefinitionId == job.WorkflowDefinitionId))
            .LongCountAsync(cancellationToken);
        var reasons = new List<string>();
        if (publishedDefinitions > 0)
        {
            reasons.Add($"{publishedDefinitions} published workflow definition(s) use it");
        }
        if (runningInstances > 0)
        {
            reasons.Add($"{runningInstances} running workflow instance(s) use it");
        }
        if (openJobs > 0)
        {
            reasons.Add($"{openJobs} open workflow job(s) use it");
        }

        return reasons;
    }

    private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(CancellationToken cancellationToken) =>
        dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

    private static JsonElement WeeklyHoursJson(BusinessCalendarContent content) =>
        JsonSerializer.SerializeToElement(BusinessCalendarContentRules.ToWeekDto(content), JsonOptions);

    private static JsonElement HolidaysJson(BusinessCalendarContent content) =>
        JsonSerializer.SerializeToElement(BusinessCalendarContentRules.ToHolidayDtos(content), JsonOptions);

    private static BusinessCalendarRecord Map(BusinessCalendarEntity entity) =>
        new(
            entity.Id,
            entity.Name,
            entity.Description,
            BusinessCalendarContentRules.Parse(
                entity.TimeZone,
                entity.WeeklyHours.Deserialize<BusinessCalendarWeekDto>(JsonOptions),
                entity.Holidays.Deserialize<List<BusinessCalendarHolidayDto>>(JsonOptions),
                checkTimeZone: false),
            entity.CreatedAt,
            entity.UpdatedAt);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static WorkflowConflictException StaleConflict(string name) =>
        new($"Business calendar '{name}' was changed by another request; reload and try again.");

    // PostgreSQL keeps microseconds, so timestamps are truncated to round-trip exactly.
    private static DateTimeOffset UtcNow()
    {
        var value = DateTimeOffset.UtcNow;
        return new DateTimeOffset(value.Ticks - value.Ticks % 10, TimeSpan.Zero);
    }

    private static DateTimeOffset NextTimestamp(DateTimeOffset previous)
    {
        var now = UtcNow();
        var previousUtc = previous.ToUniversalTime();
        return now > previousUtc ? now : previousUtc.AddTicks(10);
    }
}
