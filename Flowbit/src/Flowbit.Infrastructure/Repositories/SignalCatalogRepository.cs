using System.Globalization;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

/// <summary>
/// The signal catalog (EVENT-04F). A write takes the exclusive advisory lock of its name, then
/// the entry row, and checks the live versions that use the name without locking them: every
/// definition save, publication, reactivation, and API throw of the name holds the shared lock,
/// so none can commit against the state a write is changing.
/// </summary>
public sealed class SignalCatalogRepository(AppDbContext dbContext) : ISignalCatalogRepository
{
    private const int LivePageSize = 50;
    private const int MaxWriteProblems = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PagedResult<SignalCatalogEntryRecord>> ListAsync(
        string? namePrefix,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.SignalCatalogEntries.AsNoTracking();
        if (!string.IsNullOrEmpty(namePrefix))
        {
            query = query.Where(entry => entry.Name.StartsWith(namePrefix));
        }

        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(entry => entry.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);
        var entities = await query
            .OrderBy(entry => entry.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<SignalCatalogEntryRecord>(entities.Select(Map).ToList(), page, pageSize, total);
    }

    public async Task<SignalCatalogEntryRecord?> GetAsync(string name, CancellationToken cancellationToken)
    {
        var entity = await dbContext.SignalCatalogEntries.AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Name == name, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyDictionary<string, SignalCatalogEntryRecord>> GetManyAsync(
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return new Dictionary<string, SignalCatalogEntryRecord>(StringComparer.Ordinal);
        }

        var distinct = names.Distinct(StringComparer.Ordinal).ToArray();
        var entities = await dbContext.SignalCatalogEntries.AsNoTracking()
            .Where(entry => distinct.Contains(entry.Name))
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToDictionary(entry => entry.Name, StringComparer.Ordinal);
    }

    public async Task<SignalCatalogEntryRecord> CreateAsync(
        string name,
        SignalCatalogEntryContent content,
        string? actor,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        await LockNamesAsync([name], exclusive: true, cancellationToken);
        if (await dbContext.SignalCatalogEntries.AnyAsync(entry => entry.Name == name, cancellationToken))
        {
            throw TakenConflict(name);
        }

        var now = UtcNow();
        var candidate = new SignalCatalogEntryRecord(
            0,
            name,
            content.Description,
            SignalCatalogStatuses.Active,
            content.Fields,
            content.ThrowRoles,
            content.AllowClients,
            content.AllowWorkflows,
            now,
            actor,
            now,
            actor,
            null);
        await EnsureLiveVersionsFitAsync(candidate, cancellationToken);

        var entity = new SignalCatalogEntryEntity
        {
            Name = name,
            Description = content.Description,
            Fields = FieldsJson(content.Fields),
            ThrowRoles = RolesJson(content.ThrowRoles),
            AllowClients = content.AllowClients,
            AllowWorkflows = content.AllowWorkflows,
            Status = SignalCatalogStatuses.Active,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
            UpdatedBy = actor
        };
        dbContext.SignalCatalogEntries.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            throw TakenConflict(name);
        }

        dbContext.Entry(entity).State = EntityState.Detached;
        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
        }

        return Map(entity);
    }

    public async Task<SignalCatalogEntryRecord?> UpdateAsync(
        string name,
        SignalCatalogEntryContent content,
        DateTimeOffset expectedUpdatedAt,
        string? actor,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        await LockNamesAsync([name], exclusive: true, cancellationToken);
        var entity = await LockRowAsync(name, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        if (entity.UpdatedAt != expectedUpdatedAt)
        {
            throw StaleConflict(name);
        }

        if (entity.Status != SignalCatalogStatuses.Active)
        {
            throw new WorkflowConflictException(
                $"Signal catalog entry '{name}' is archived; reactivate it before changing it.");
        }

        var updatedAt = NextTimestamp(entity.UpdatedAt);
        var candidate = Map(entity) with
        {
            Description = content.Description,
            Fields = content.Fields,
            ThrowRoles = content.ThrowRoles,
            AllowClients = content.AllowClients,
            AllowWorkflows = content.AllowWorkflows,
            UpdatedAt = updatedAt,
            UpdatedBy = actor
        };
        await EnsureLiveVersionsFitAsync(candidate, cancellationToken);

        var fields = FieldsJson(content.Fields);
        var roles = RolesJson(content.ThrowRoles);
        await dbContext.SignalCatalogEntries
            .Where(entry => entry.Id == entity.Id)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(entry => entry.Description, content.Description)
                    .SetProperty(entry => entry.Fields, fields)
                    .SetProperty(entry => entry.ThrowRoles, roles)
                    .SetProperty(entry => entry.AllowClients, content.AllowClients)
                    .SetProperty(entry => entry.AllowWorkflows, content.AllowWorkflows)
                    .SetProperty(entry => entry.UpdatedAt, updatedAt)
                    .SetProperty(entry => entry.UpdatedBy, actor),
                cancellationToken);
        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
        }

        return candidate;
    }

    public async Task<SignalCatalogEntryRecord?> ChangeStatusAsync(
        string name,
        bool archive,
        DateTimeOffset expectedUpdatedAt,
        string? actor,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        await LockNamesAsync([name], exclusive: true, cancellationToken);
        var entity = await LockRowAsync(name, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var target = archive ? SignalCatalogStatuses.Archived : SignalCatalogStatuses.Active;
        if (entity.Status == target)
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            return Map(entity);
        }

        if (entity.UpdatedAt != expectedUpdatedAt)
        {
            throw StaleConflict(name);
        }

        var updatedAt = NextTimestamp(entity.UpdatedAt);
        DateTimeOffset? archivedAt = archive ? updatedAt : null;
        var changed = Map(entity) with
        {
            Status = target,
            UpdatedAt = updatedAt,
            UpdatedBy = actor,
            ArchivedAt = archivedAt
        };
        if (archive)
        {
            var usage = await GetUsageCountsAsync(name, cancellationToken);
            if (!usage.IsUnused)
            {
                throw new WorkflowConflictException(
                    $"Signal catalog entry '{name}' cannot be archived: {DescribeBlockers(usage)}.");
            }
        }
        else
        {
            await EnsureLiveVersionsFitAsync(changed, cancellationToken);
        }

        await dbContext.SignalCatalogEntries
            .Where(entry => entry.Id == entity.Id)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(entry => entry.Status, target)
                    .SetProperty(entry => entry.ArchivedAt, archivedAt)
                    .SetProperty(entry => entry.UpdatedAt, updatedAt)
                    .SetProperty(entry => entry.UpdatedBy, actor),
                cancellationToken);
        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
        }

        return changed;
    }

    public async Task LockNamesAsync(
        IReadOnlyCollection<string> names,
        bool exclusive,
        CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return;
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Signal name locks are transaction-scoped and need an open transaction.");
        }

        // Ordinal order everywhere, so two transactions that share names never wait for each other in a cycle.
        foreach (var name in names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (exclusive)
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtext('signal-name'), hashtext({name}))",
                    cancellationToken);
            }
            else
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock_shared(hashtext('signal-name'), hashtext({name}))",
                    cancellationToken);
            }
        }
    }

    public async Task<(IReadOnlyList<SignalCatalogProblemDto> Problems, bool Truncated, int LiveVersionCount)> PreviewAsync(
        SignalCatalogEntryRecord candidate,
        int limit,
        CancellationToken cancellationToken)
    {
        var liveCount = await LiveUsers(candidate.Name).CountAsync(cancellationToken);
        var problems = await FindLiveProblemsAsync(candidate, limit + 1, cancellationToken);
        return (problems.Take(limit).ToList(), problems.Count > limit, liveCount);
    }

    public async Task<SignalCatalogUsageCounts> GetUsageCountsAsync(string name, CancellationToken cancellationToken)
    {
        var published = await dbContext.WorkflowDefinitions
            .Where(definition => definition.IsPublished
                && definition.SignalNames.Any(reference => reference.SignalName == name))
            .LongCountAsync(cancellationToken);
        var running = await dbContext.WorkflowDefinitions
            .Where(definition => definition.SignalNames.Any(reference => reference.SignalName == name)
                && dbContext.WorkflowInstances.Any(instance =>
                    instance.WorkflowDefinitionId == definition.Id
                    && instance.Status == WorkflowInstanceStatuses.Running))
            .LongCountAsync(cancellationToken);
        var waits = await dbContext.SignalSubscriptions
            .Where(subscription => subscription.SignalName == name
                && subscription.Status == SignalSubscriptionStatuses.Active)
            .LongCountAsync(cancellationToken);
        var families = await dbContext.SignalStartSubscriptions
            .Where(subscription => subscription.SignalName == name)
            .LongCountAsync(cancellationToken);
        var delivering = await dbContext.SignalBroadcasts
            .Where(broadcast => broadcast.SignalName == name
                && broadcast.Status == SignalBroadcastStatuses.Delivering)
            .LongCountAsync(cancellationToken);
        return new SignalCatalogUsageCounts(published, running, waits, families, delivering);
    }

    public async Task<PagedResult<SignalCatalogVersionUseRecord>> ListVersionUsesAsync(
        string name,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(definition => definition.SignalNames.Any(reference => reference.SignalName == name));
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(definition => definition.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(definition => new
            {
                definition.Id,
                definition.WorkflowKey,
                definition.Name,
                definition.Version,
                definition.IsPublished,
                NodeIds = definition.SignalNames
                    .Where(reference => reference.SignalName == name)
                    .OrderBy(reference => reference.NodeId)
                    .Select(reference => reference.NodeId)
                    .ToList()
            })
            .ToListAsync(cancellationToken);
        return new PagedResult<SignalCatalogVersionUseRecord>(
            rows.Select(row => new SignalCatalogVersionUseRecord(
                    row.Id, row.WorkflowKey, row.Name, row.Version, row.IsPublished, row.NodeIds))
                .ToList(),
            page,
            pageSize,
            total);
    }

    public async Task<(IReadOnlyList<UncatalogedSignalNameRecord> Items, bool Truncated)> ListUncatalogedNamesAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.WorkflowDefinitionSignalNames.AsNoTracking()
            .Where(reference =>
                (reference.WorkflowDefinition!.IsPublished
                    || dbContext.WorkflowInstances.Any(instance =>
                        instance.WorkflowDefinitionId == reference.WorkflowDefinitionId
                        && instance.Status == WorkflowInstanceStatuses.Running))
                && !dbContext.SignalCatalogEntries.Any(entry => entry.Name == reference.SignalName))
            .Select(reference => new { reference.SignalName, reference.WorkflowDefinitionId })
            .Distinct()
            .GroupBy(reference => reference.SignalName)
            .Select(group => new { Name = group.Key, Count = group.Count() })
            .OrderBy(row => row.Name)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        return (
            rows.Take(limit).Select(row => new UncatalogedSignalNameRecord(row.Name, row.Count)).ToList(),
            rows.Count > limit);
    }

    /// <summary>The published versions and versions with a running instance that use a name.</summary>
    private IQueryable<WorkflowDefinitionEntity> LiveUsers(string name) =>
        dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(definition => definition.SignalNames.Any(reference => reference.SignalName == name)
                && (definition.IsPublished
                    || dbContext.WorkflowInstances.Any(instance =>
                        instance.WorkflowDefinitionId == definition.Id
                        && instance.Status == WorkflowInstanceStatuses.Running)));

    /// <summary>
    /// The problems of the live versions that use the candidate's name, read in pages and in
    /// version order, stopping once <paramref name="limit"/> problems are found. Only the nodes
    /// that use the name are checked, so another name's problem cannot count.
    /// </summary>
    private async Task<List<SignalCatalogProblemDto>> FindLiveProblemsAsync(
        SignalCatalogEntryRecord candidate,
        int limit,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, SignalCatalogEntryRecord>(StringComparer.Ordinal)
        {
            [candidate.Name] = candidate
        };
        var problems = new List<SignalCatalogProblemDto>();
        var lastId = 0L;
        while (problems.Count < limit)
        {
            var page = await LiveUsers(candidate.Name)
                .Where(definition => definition.Id > lastId)
                .OrderBy(definition => definition.Id)
                .Take(LivePageSize)
                .ToListAsync(cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var version in page)
            {
                lastId = version.Id;
                WorkflowModelMigrator.Normalize(version.Definition);
                foreach (var problem in SignalCatalogContract.FindDefinitionProblems(
                             version.Definition,
                             entries,
                             SignalCatalogModes.Optional,
                             SignalCatalogStage.Publication,
                             candidate.Name))
                {
                    problems.Add(new SignalCatalogProblemDto(
                        version.Id,
                        version.WorkflowKey,
                        version.Name,
                        version.Version,
                        problem.NodeId,
                        problem.Message));
                    if (problems.Count >= limit)
                    {
                        return problems;
                    }
                }
            }

            if (page.Count < LivePageSize)
            {
                break;
            }
        }

        return problems;
    }

    private async Task EnsureLiveVersionsFitAsync(
        SignalCatalogEntryRecord candidate,
        CancellationToken cancellationToken)
    {
        // One past the listed problems, to say whether there may be more.
        var problems = await FindLiveProblemsAsync(candidate, MaxWriteProblems + 1, cancellationToken);
        if (problems.Count == 0)
        {
            return;
        }

        var listed = string.Join(" ", problems.Take(MaxWriteProblems).Select(problem =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"{problem.WorkflowKey} v{problem.Version} (#{problem.WorkflowDefinitionId}): {problem.Message}")));
        var more = problems.Count > MaxWriteProblems
            ? " There are more problems; review the entry to see them all."
            : string.Empty;
        throw new WorkflowConflictException(
            $"Signal catalog entry '{candidate.Name}' does not fit live workflow versions: {listed}{more}");
    }

    private Task<SignalCatalogEntryEntity?> LockRowAsync(string name, CancellationToken cancellationToken) =>
        dbContext.SignalCatalogEntries
            .FromSqlInterpolated(
                $"""SELECT * FROM "flowbit"."signal_catalog_entries" WHERE "Name" = {name} FOR UPDATE""")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

    private static string DescribeBlockers(SignalCatalogUsageCounts usage)
    {
        var reasons = new List<string>();
        if (usage.PublishedVersionCount > 0)
        {
            reasons.Add(Count(usage.PublishedVersionCount, "published workflow version uses", "published workflow versions use") + " it");
        }

        if (usage.RunningVersionCount > 0)
        {
            reasons.Add(Count(usage.RunningVersionCount, "workflow version with running instances uses", "workflow versions with running instances use") + " it");
        }

        if (usage.ActiveWaitCount > 0)
        {
            reasons.Add(Count(usage.ActiveWaitCount, "signal wait is", "signal waits are") + " open for it");
        }

        if (usage.StartingFamilyCount > 0)
        {
            reasons.Add(Count(usage.StartingFamilyCount, "workflow family starts", "workflow families start") + " on it");
        }

        if (usage.DeliveringBroadcastCount > 0)
        {
            reasons.Add(Count(usage.DeliveringBroadcastCount, "broadcast of it is", "broadcasts of it are") + " still delivering");
        }

        return string.Join("; ", reasons);
    }

    private static string Count(long count, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");

    private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(CancellationToken cancellationToken) =>
        dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

    internal static SignalCatalogEntryRecord Map(SignalCatalogEntryEntity entity) =>
        new(
            entity.Id,
            entity.Name,
            entity.Description,
            entity.Status,
            entity.Fields.ValueKind == JsonValueKind.Array
                ? entity.Fields.Deserialize<List<SignalCatalogFieldDto>>(JsonOptions) ?? []
                : [],
            entity.ThrowRoles.ValueKind == JsonValueKind.Array
                ? entity.ThrowRoles.Deserialize<List<string>>(JsonOptions) ?? []
                : [],
            entity.AllowClients,
            entity.AllowWorkflows,
            entity.CreatedAt,
            entity.CreatedBy,
            entity.UpdatedAt,
            entity.UpdatedBy,
            entity.ArchivedAt);

    private static JsonElement FieldsJson(IReadOnlyList<SignalCatalogFieldDto> fields) =>
        JsonSerializer.SerializeToElement(fields, JsonOptions);

    private static JsonElement RolesJson(IReadOnlyList<string> roles) =>
        JsonSerializer.SerializeToElement(roles, JsonOptions);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static WorkflowConflictException TakenConflict(string name) =>
        new($"Signal catalog entry '{name}' already exists.");

    private static WorkflowConflictException StaleConflict(string name) =>
        new($"Signal catalog entry '{name}' was changed by another request; reload and try again.");

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
