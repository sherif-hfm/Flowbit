using System.Text.Json;
using System.Diagnostics;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Npgsql;
using NpgsqlTypes;

namespace Flowbit.Infrastructure.Repositories;

/// <summary>
/// Database-owned maintenance. Each tick owns the singleton coordinator before
/// taking instance locks and (for shared revisions) catalog locks. Runtime never
/// takes the coordinator lock. Cursors, deletions and truncation markers commit
/// together, so another replica can safely resume an expired lease.
/// </summary>
public sealed class RetentionRepository(RetentionDataSource source, TimeProvider timeProvider)
    : IRetentionRepository
{
    private const int PreviewLimit = 10_000;
    private const int MaximumCategoryBatches = 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ExecutionOrder = [RetentionCategories.WorkflowHistory,
        RetentionCategories.VariableHistory, RetentionCategories.SharedVariableHistory,
        RetentionCategories.NodeActivity, RetentionCategories.AdministrativeAudits,
        RetentionCategories.CompletedJobs, RetentionCategories.ResolvedIncidents];

    public async Task<RetentionStatusDto> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        var policies = await ReadPoliciesAsync(connection, null, cancellationToken);
        var state = await ReadStateAsync(connection, null, false, cancellationToken);
        return new(policies, state?.Current?.ToDto(), state?.Last?.ToDto(),
            state?.NextScheduledAt, state?.WorkerLastSeenAt);
    }

    public async Task<RetentionPolicyDto> UpdatePolicyAsync(string category,
        UpdateRetentionPolicyRequest request, string actor, CancellationToken cancellationToken)
    {
        Validate(category, request.RetentionDays);
        if (request.ExpectedRevision < 1) throw new WorkflowDomainException("Expected policy revision must be positive.");
        actor = RequireActor(actor);
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            UPDATE flowbit.retention_policies
            SET "RetentionDays"=@days, "Revision"="Revision"+1, "UpdatedAt"=@now,
                "UpdatedBy"=@actor, "IsInitialized"=TRUE
            WHERE "Category"=@category AND "Revision"=@revision
            RETURNING "Category", "RetentionDays", "Revision", "UpdatedAt", "UpdatedBy", "IsInitialized"
            """);
        Add(command, "days", request.RetentionDays, NpgsqlDbType.Integer);
        Add(command, "category", category); Add(command, "revision", request.ExpectedRevision);
        Add(command, "now", timeProvider.GetUtcNow()); Add(command, "actor", actor);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new WorkflowConflictException("The retention policy changed. Refresh and try again.");
        return ReadPolicy(reader);
    }

    public async Task InitializeAsync(int completedJobDays, int resolvedIncidentDays, CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var pending = new List<string>();
        await using (var select = Command(connection, transaction, """
            SELECT "Category" FROM flowbit.retention_policies
            WHERE NOT "IsInitialized" AND "Category" IN ('completedJobs','resolvedIncidents')
            ORDER BY "Category" FOR UPDATE
            """))
        {
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) pending.Add(reader.GetString(0));
        }
        if (pending.Contains(RetentionCategories.CompletedJobs)) Validate(RetentionCategories.CompletedJobs, completedJobDays);
        if (pending.Contains(RetentionCategories.ResolvedIncidents)) Validate(RetentionCategories.ResolvedIncidents, resolvedIncidentDays);
        await using var command = Command(connection, transaction, """
            UPDATE flowbit.retention_policies
            SET "RetentionDays"=CASE "Category" WHEN 'completedJobs' THEN @jobs ELSE @incidents END,
                "IsInitialized"=TRUE, "UpdatedAt"=@now, "Revision"="Revision"+1
            WHERE NOT "IsInitialized" AND "Category" IN ('completedJobs','resolvedIncidents')
            """);
        Add(command, "jobs", completedJobDays); Add(command, "incidents", resolvedIncidentDays);
        Add(command, "now", timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<RetentionPreviewDto> PreviewAsync(PreviewRetentionRequest request, CancellationToken cancellationToken)
    {
        Validate(request.Category, request.RetentionDays);
        var now = timeProvider.GetUtcNow();
        if (request.RetentionDays is null) return new(request.Category, null, [], false, now);
        var cutoff = now.AddDays(-request.RetentionDays.Value);
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ConfigureTimeoutsAsync(connection, transaction, 5, 500, cancellationToken);
        var counts = new List<RetentionTablePreviewDto>();
        var limited = false;
        foreach (var spec in Tables(request.Category))
        {
            var from = spec.Kind is TableKind.Instance or TableKind.Shared
                ? $"flowbit.{spec.Name} t LEFT JOIN flowbit.workflow_instances i ON i.\"Id\"=t.\"InstanceId\""
                : $"flowbit.{spec.Name} t";
            var age = AgePredicate(spec);
            await using var command = Command(connection, transaction, $"""
                SELECT "Protected" FROM (
                    SELECT ({spec.Protection}) AS "Protected" FROM {from}
                    WHERE {age} ORDER BY t."Id" LIMIT {PreviewLimit + 1}
                ) sample
                """);
            Add(command, "cutoff", cutoff);
            long eligible = 0, pinned = 0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (eligible + pinned == PreviewLimit) { limited = true; break; }
                if (reader.GetBoolean(0)) pinned++; else eligible++;
            }
            counts.Add(new(spec.Name, eligible, pinned));
        }
        if (request.Category == RetentionCategories.CompletedJobs)
        {
            var jobs = Tables(request.Category)[0];
            await using var attempts = Command(connection, transaction, $"""
                WITH sampled_jobs AS (
                    SELECT t."Id",({jobs.Protection}) AS protected FROM flowbit.workflow_jobs t
                    WHERE {AgePredicate(jobs)} ORDER BY t."Id" LIMIT {PreviewLimit}
                )
                SELECT j.protected FROM flowbit.workflow_job_attempts a
                JOIN sampled_jobs j ON j."Id"=a."JobId"
                ORDER BY a."Id" LIMIT {PreviewLimit + 1}
                """);
            Add(attempts, "cutoff", cutoff);
            long eligible = 0, pinned = 0;
            await using var reader = await attempts.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (eligible + pinned == PreviewLimit) { limited = true; break; }
                if (reader.GetBoolean(0)) pinned++; else eligible++;
            }
            counts.Insert(1, new("workflow_job_attempts", eligible, pinned));
        }
        await transaction.CommitAsync(cancellationToken);
        return new(request.Category, cutoff, counts, limited, now);
    }

    public async Task<RetentionRunDto> RequestRunAsync(string actor, CancellationToken cancellationToken)
    {
        actor = RequireActor(actor);
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var state = await ReadStateAsync(connection, transaction, true, cancellationToken)
            ?? throw new InvalidOperationException("Retention coordinator has not been migrated.");
        if (state.Current is null)
        {
            var now = timeProvider.GetUtcNow();
            state.Current = CreateRun(await ReadPoliciesAsync(connection, transaction, cancellationToken), now, actor, state.Last);
            state.NextScheduledAt = now.AddHours(1);
            await SaveStateAsync(connection, transaction, state, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return state.Current.ToDto();
    }

    public async Task<RetentionTickResult> ProcessBatchAsync(RetentionExecutionOptions options, CancellationToken cancellationToken)
    {
        if (options.BatchSize is < 1 or > 1000 || options.MaxRunnableJobs < 1
            || options.MaxQueueLagSeconds < 1 || options.StatementTimeoutSeconds is < 1 or > 30
            || options.LockTimeoutMilliseconds is < 1 or > 5000
            || string.IsNullOrWhiteSpace(options.WorkerId) || options.WorkerId.Length > 300)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid retention execution limits.");
        var fence = new AttemptFence();
        try
        {
            return await ProcessCoreAsync(options, fence, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState is "57014" or "55P03" or "40P01")
        {
            // The transaction (including cursor) has rolled back. A future tick retries it.
            await RecordOutcomeAsync(fence, failed: false, cancellationToken);
            return new(true, false, true, Message: "Maintenance paused because the database is busy.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordOutcomeAsync(fence, failed: true, cancellationToken);
            throw;
        }
    }

    private async Task<RetentionTickResult> ProcessCoreAsync(RetentionExecutionOptions options, AttemptFence fence, CancellationToken ct)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(ct);
        await using var acquisition = await connection.BeginTransactionAsync(ct);
        var transaction = acquisition;
        await ConfigureTimeoutsAsync(connection, transaction, options.StatementTimeoutSeconds,
            options.LockTimeoutMilliseconds, ct);
        var state = await ReadStateAsync(connection, transaction, true, ct, skipLocked: true);
        if (state is null) return new(true, false, true, Message: "Another maintenance worker is active.");
        var now = timeProvider.GetUtcNow();
        state.WorkerLastSeenAt = now;
        if (state.LeaseOwner != options.WorkerId && state.LeaseExpiresAt > now)
        {
            await SaveStateAsync(connection, transaction, state, ct);
            await transaction.CommitAsync(ct);
            return new(state.Current is not null, false, false);
        }
        if (state.Current is null && state.NextScheduledAt <= now)
        {
            state.Current = CreateRun(await ReadPoliciesAsync(connection, transaction, ct), now, null, state.Last);
            state.NextScheduledAt = now.AddHours(1);
        }
        if (state.Current is null)
        {
            await SaveStateAsync(connection, transaction, state, ct);
            await transaction.CommitAsync(ct);
            return new(false, false, false);
        }
        if (state.LeaseOwner != options.WorkerId || state.LeaseExpiresAt <= now) state.LeaseGeneration++;
        state.LeaseOwner = options.WorkerId;
        state.LeaseExpiresAt = now.AddSeconds(30);
        var run = state.Current;
        run.StartedAt ??= now;
        await SaveStateAsync(connection, transaction, state, ct);
        await transaction.CommitAsync(ct);
        fence.RunId = run.Id; fence.WorkerId = options.WorkerId; fence.Generation = state.LeaseGeneration;
        await using var workTransaction = await connection.BeginTransactionAsync(ct);
        transaction = workTransaction;
        await ConfigureTimeoutsAsync(connection, transaction, options.StatementTimeoutSeconds,
            options.LockTimeoutMilliseconds, ct);
        state = await ReadStateAsync(connection, transaction, true, ct, skipLocked: true);
        if (state?.Current?.Id != fence.RunId || state.LeaseOwner != fence.WorkerId
            || state.LeaseGeneration != fence.Generation)
            return new(true, false, true, Message: "Another maintenance worker is active.");
        run = state.Current;
        var category = run.Categories.FirstOrDefault(item => item.Status is "pending" or "running" or "paused");
        if (category is null)
        {
            Finish(state, now, run.Categories.Any(item => item.Status == "failed") ? "failed"
                : run.Categories.Any(item => item.Status == "budgetReached") ? "budgetReached" : "succeeded");
            await SaveStateAsync(connection, transaction, state, ct);
            await transaction.CommitAsync(ct);
            return new(false, true, false);
        }
        fence.Category = category.Category;
        await using var policyLock = Command(connection, transaction,
            "SELECT \"Revision\" FROM flowbit.retention_policies WHERE \"Category\"=@category FOR SHARE");
        Add(policyLock, "category", category.Category);
        if (Convert.ToInt64(await policyLock.ExecuteScalarAsync(ct)) != category.PolicyRevision)
        {
            category.Status = "policyChanged";
            category.Message = "Stopped because the saved policy changed. The next run uses the new policy.";
            await SaveStateAsync(connection, transaction, state, ct);
            await transaction.CommitAsync(ct);
            return new(true, true, false);
        }
        if (category.BatchCount >= MaximumCategoryBatches || category.ElapsedMilliseconds >= 30_000)
        {
            category.Status = "budgetReached";
            category.Message = "The per-run maintenance budget was reached. The next run continues from this scan position.";
            await SaveStateAsync(connection, transaction, state, ct);
            await transaction.CommitAsync(ct);
            return new(true, true, false);
        }
        if (await IsRuntimeBusyAsync(connection, transaction, now, options, ct))
        {
            run.Status = "paused";
            run.Message = "Paused while workflow jobs are waiting for capacity.";
            category.Status = "paused";
            await SaveStateAsync(connection, transaction, state, ct);
            await transaction.CommitAsync(ct);
            return new(true, false, true, Message: run.Message);
        }
        run.Status = "running"; run.Message = null; category.Status = "running";
        var specs = Tables(category.Category);
        var elapsed = Stopwatch.StartNew();
        if (category.TableUpperIds.Count == 0)
        {
            // Capture a finite table frontier before the first deletion. In
            // particular, age-independent orphan snapshot cleanup must not
            // chase new snapshots forever across budget continuations.
            foreach (var spec in specs)
            {
                await using var upper = Command(connection, transaction,
                    $"SELECT COALESCE((SELECT \"Id\" FROM flowbit.{spec.Name} ORDER BY \"Id\" DESC LIMIT 1),0)");
                category.TableUpperIds[spec.Name] = Convert.ToInt64(await upper.ExecuteScalarAsync(ct));
            }
        }
        var result = await ProcessTableAsync(connection, transaction, specs[category.TableIndex],
            category, options.BatchSize, now, ct);
        category.DeletedRows += result.Deleted;
        category.ProtectedRows += result.Protected;
        run.BatchCount++;
        category.BatchCount++;
        category.ElapsedMilliseconds += elapsed.ElapsedMilliseconds;
        if (result.Done)
        {
            category.TableIndex++;
            category.OwnerCursor = 0; category.RowCursor = 0;
            if (category.TableIndex == specs.Length) category.Status = "succeeded";
        }
        await SaveStateAsync(connection, transaction, state, ct);
        await transaction.CommitAsync(ct);
        return new(true, true, false, result.Deleted)
        {
            Category = category.Category,
            DeletedByTable = result.DeletedByTable ?? new Dictionary<string, long>()
        };
    }

    private async Task<BatchResult> ProcessTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        TableSpec spec, CategoryDocument category, int batchSize, DateTimeOffset now, CancellationToken ct)
    {
        if (spec.Kind is TableKind.Instance or TableKind.Shared)
        {
            var ownerId = await NextInstanceAsync(connection, transaction, spec, category, ct);
            if (ownerId is not null)
            {
                if (category.OwnerCursor != ownerId) category.RowCursor = 0;
                category.OwnerCursor = ownerId.Value;
                return await DeleteSampleAsync(connection, transaction, spec, category, batchSize, ownerId, now, ct);
            }
            if (spec.Kind == TableKind.Instance) return new(0, 0, true);
            // Shared writes without an owning instance use their own creation time.
            // A negative owner cursor denotes the separate global-revision pass.
            if (category.OwnerCursor >= 0) { category.OwnerCursor = -1; category.RowCursor = 0; }
        }
        return await DeleteSampleAsync(connection, transaction, spec, category, batchSize, null, now, ct);
    }

    private static async Task<long?> NextInstanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        TableSpec spec, CategoryDocument category, CancellationToken ct)
    {
        if (category.OwnerCursor < 0) return null;
        await using var command = Command(connection, transaction, $"""
            SELECT i."Id" FROM flowbit.workflow_instances i
            WHERE i."Id">=@owner AND i."Status" IN ('completed','cancelled','faulted')
              AND i."FinishedAt" < @cutoff
              AND EXISTS (SELECT 1 FROM flowbit.{spec.Name} t WHERE t."InstanceId"=i."Id"
                  AND t."Id"<=@upper AND (i."Id">@owner OR t."Id">@row))
            ORDER BY i."Id" LIMIT 1 FOR UPDATE OF i SKIP LOCKED
            """);
        Add(command, "owner", category.OwnerCursor); Add(command, "row", category.RowCursor);
        Add(command, "cutoff", category.Cutoff!.Value);
        Add(command, "upper", category.TableUpperIds[spec.Name]);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static async Task<BatchResult> DeleteSampleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        TableSpec spec, CategoryDocument category, int batchSize, long? instanceId, DateTimeOffset now, CancellationToken ct)
    {
        var predicate = instanceId is not null ? "t.\"InstanceId\"=@instance AND t.\"Id\">@row"
            : spec.Kind == TableKind.Shared ? "t.\"InstanceId\" IS NULL AND t.\"CreatedAt\"<@cutoff AND t.\"Id\">@row"
            : $"{AgePredicate(spec)} AND t.\"Id\">@row";
        var rows = new List<(long Id, bool Protected, long? SharedVariableId)>();
        await using (var command = Command(connection, transaction, $"""
            SELECT t."Id", ({spec.Protection}) AS "Protected",
                {(spec.Kind == TableKind.Shared ? "t.\"SharedVariableId\"" : "NULL::bigint")}
            FROM flowbit.{spec.Name} t WHERE ({predicate}) AND t."Id"<=@upper ORDER BY t."Id" LIMIT @limit
            {(spec.Kind is TableKind.Jobs or TableKind.Incidents or TableKind.Snapshots ? "FOR UPDATE OF t SKIP LOCKED" : "")}
            """))
        {
            Add(command, "row", category.RowCursor); Add(command, "cutoff", category.Cutoff!.Value);
            Add(command, "limit", batchSize); Add(command, "instance", instanceId, NpgsqlDbType.Bigint);
            Add(command, "upper", category.TableUpperIds[spec.Name]);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add((reader.GetInt64(0), reader.GetBoolean(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }
        if (rows.Count == 0) return new(0, 0, instanceId is null);
        var ids = rows.Where(row => !row.Protected).Select(row => row.Id).ToArray();
        long[] sharedIds = [];
        if (spec.Kind == TableKind.Shared && ids.Length > 0)
        {
            var candidates = rows.Where(row => !row.Protected).Select(row => row.SharedVariableId!.Value).Distinct().Order().ToArray();
            var keys = new List<(long Id, string Key)>();
            await using (var catalog = Command(connection, transaction,
                "SELECT \"Id\",\"Key\" FROM flowbit.shared_variables WHERE \"Id\"=ANY(@ids)"))
            {
                Add(catalog, "ids", candidates);
                await using var catalogReader = await catalog.ExecuteReaderAsync(ct);
                while (await catalogReader.ReadAsync(ct)) keys.Add((catalogReader.GetInt64(0), catalogReader.GetString(1)));
            }
            // Runtime orders Unicode keys with .NET Ordinal. PostgreSQL C
            // collation differs for supplementary Unicode characters, and the
            // catalog's citext comparison would also fold case. Preserve the
            // exact bounded application order when acquiring catalog locks.
            candidates = keys.OrderBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.Id)
                .Select(item => item.Id).ToArray();
            await using var locks = Command(connection, transaction, """
                SELECT "Id" FROM flowbit.shared_variables WHERE "Id"=ANY(@ids)
                ORDER BY array_position(@ids,"Id") FOR UPDATE SKIP LOCKED
                """);
            Add(locks, "ids", candidates);
            var acquired = new List<long>();
            await using var reader = await locks.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) acquired.Add(reader.GetInt64(0));
            sharedIds = acquired.ToArray();
            ids = rows.Where(row => !row.Protected && sharedIds.Contains(row.SharedVariableId!.Value)).Select(row => row.Id).ToArray();
        }
        long deleted = 0;
        var counts = new Dictionary<string, long>();
        if (ids.Length > 0)
        {
            long attempts = 0;
            if (spec.Name == "workflow_jobs")
            {
                await using var count = Command(connection, transaction,
                    "SELECT count(*) FROM flowbit.workflow_job_attempts WHERE \"JobId\"=ANY(@ids)");
                Add(count, "ids", ids); attempts = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            }
            var deleteAge = spec.Kind is TableKind.Jobs or TableKind.Incidents or TableKind.Snapshots
                ? $" AND ({AgePredicate(spec)})" : "";
            var removed = new List<long>();
            await using (var delete = Command(connection, transaction, $"""
                DELETE FROM flowbit.{spec.Name} t WHERE t."Id"=ANY(@ids) AND NOT ({spec.Protection})
                {deleteAge} RETURNING t."Id"
                """))
            {
                Add(delete, "ids", ids); Add(delete, "cutoff", category.Cutoff!.Value);
                await using var reader = await delete.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) removed.Add(reader.GetInt64(0));
            }
            deleted = removed.Count;
            counts[spec.Name] = deleted;
            if (spec.Name == "workflow_jobs" && deleted > 0)
            {
                counts["workflow_job_attempts"] = attempts;
                deleted += attempts;
            }
            if (deleted > 0 && instanceId is not null)
            {
                await using var mark = Command(connection, transaction, """
                    UPDATE flowbit.workflow_instances SET "HistoryPrunedAt"=COALESCE("HistoryPrunedAt",@now),
                        "UpdatedAt"=@now WHERE "Id"=@id
                    """);
                Add(mark, "now", now); Add(mark, "id", instanceId.Value);
                await mark.ExecuteNonQueryAsync(ct);
            }
            if (deleted > 0 && sharedIds.Length > 0)
            {
                sharedIds = rows.Where(row => removed.Contains(row.Id)).Select(row => row.SharedVariableId!.Value).Distinct().ToArray();
                await using var mark = Command(connection, transaction, """
                    UPDATE flowbit.shared_variables SET "HistoryPrunedAt"=COALESCE("HistoryPrunedAt",@now)
                    WHERE "Id"=ANY(@ids)
                    """);
                Add(mark, "now", now); Add(mark, "ids", sharedIds);
                await mark.ExecuteNonQueryAsync(ct);
            }
        }
        category.RowCursor = rows[^1].Id;
        return new(deleted, rows.LongCount(row => row.Protected), false, counts);
    }

    private static async Task<bool> IsRuntimeBusyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        DateTimeOffset now, RetentionExecutionOptions options, CancellationToken ct)
    {
        // Match acquisition's indexed frontiers. A live result-ready lease is
        // in flight, while expired running/result-ready leases await recovery.
        // Each frontier is bounded: reaching the limit already requires pause;
        // below it, every runnable row participates in the lag calculation.
        var frontiers = new[] { "control", "activity" }.SelectMany(queue => new[]
        {
            $"""
            (SELECT "DueAt" AS ready_at FROM flowbit.workflow_jobs
             WHERE "QueueClass"='{queue}' AND "Status" IN ('queued','retry') AND "DueAt"<=@now
             ORDER BY "Priority","DueAt","Id" LIMIT @maximum)
            """,
            $"""
            (SELECT "LeaseExpiresAt" AS ready_at FROM flowbit.workflow_jobs
             WHERE "QueueClass"='{queue}' AND "Status"='running' AND "LeaseExpiresAt"<=@now
             ORDER BY "LeaseExpiresAt","Id" LIMIT @maximum)
            """,
            $"""
            (SELECT "LeaseExpiresAt" AS ready_at FROM flowbit.workflow_jobs
             WHERE "QueueClass"='{queue}' AND "Status"='resultReady' AND "LeaseExpiresAt"<=@now
             ORDER BY "LeaseExpiresAt","Id" LIMIT @maximum)
            """
        });
        await using var command = Command(connection, transaction,
            "SELECT count(*) >= @maximum OR COALESCE(min(ready_at)<@late,FALSE) FROM ("
            + string.Join(" UNION ALL ", frontiers) + ") runnable");
        Add(command, "maximum", options.MaxRunnableJobs); Add(command, "now", now);
        Add(command, "late", now.AddSeconds(-options.MaxQueueLagSeconds));
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task RecordOutcomeAsync(AttemptFence fence, bool failed, CancellationToken ct)
    {
        if (fence.RunId is null) return;
        await using var connection = await source.DataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var state = await ReadStateAsync(connection, transaction, true, ct, skipLocked: true);
        if (state?.Current?.Id != fence.RunId || state.LeaseOwner != fence.WorkerId
            || state.LeaseGeneration != fence.Generation) return;
        var message = failed ? "This category stopped after a database failure. See the worker log for details."
            : "Maintenance paused because the database is busy.";
        var category = state.Current.Categories.SingleOrDefault(item => item.Category == fence.Category);
        if (category is not null) { category.Status = failed ? "failed" : "paused"; category.Message = message; }
        state.Current.Status = failed ? "running" : "paused";
        state.Current.Message = message;
        await SaveStateAsync(connection, transaction, state, ct);
        await transaction.CommitAsync(ct);
    }

    private static RunDocument CreateRun(IReadOnlyList<RetentionPolicyDto> policies, DateTimeOffset now, string? actor,
        RunDocument? previous) => new()
    {
        Id = Guid.NewGuid(), Status = "queued", RequestedAt = now, RequestedBy = actor,
        Categories = ExecutionOrder.Select(key =>
        {
            var policy = policies.Single(item => item.Category == key);
            var bookmark = previous?.Categories.SingleOrDefault(item => item.Category == key
                && item.PolicyRevision == policy.Revision && item.Status == "budgetReached");
            return new CategoryDocument { Category = key, PolicyRevision = policy.Revision,
                // A budget continuation is one scan spread across hourly runs.
                // Keep its original eligibility horizon so newly aging owners
                // cannot indefinitely extend an early table ahead of later ones.
                Cutoff = bookmark?.Cutoff
                    ?? (policy.RetentionDays is int days && policy.IsInitialized ? now.AddDays(-days) : null),
                Status = policy.RetentionDays is null || !policy.IsInitialized ? "keepForever" : "pending",
                TableIndex = bookmark?.TableIndex ?? 0, OwnerCursor = bookmark?.OwnerCursor ?? 0,
                RowCursor = bookmark?.RowCursor ?? 0,
                TableUpperIds = bookmark?.TableUpperIds.ToDictionary(item => item.Key, item => item.Value) ?? [] };
        }).ToList()
    };

    private static void Finish(Coordinator state, DateTimeOffset now, string status)
    {
        state.Current!.Status = status; state.Current.CompletedAt = now;
        state.Last = state.Current; state.Current = null;
        state.LeaseOwner = null; state.LeaseExpiresAt = null;
        state.NextScheduledAt = now.AddHours(1);
    }

    private static async Task<List<RetentionPolicyDto>> ReadPoliciesAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT "Category", "RetentionDays", "Revision", "UpdatedAt", "UpdatedBy", "IsInitialized"
            FROM flowbit.retention_policies ORDER BY "Category"
            """);
        var result = new List<RetentionPolicyDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadPolicy(reader));
        return result;
    }

    private static RetentionPolicyDto ReadPolicy(NpgsqlDataReader reader) => new(reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.GetInt64(2), reader.GetFieldValue<DateTimeOffset>(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetBoolean(5));

    private static async Task<Coordinator?> ReadStateAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        bool forUpdate, CancellationToken ct, bool skipLocked = false)
    {
        await using var command = Command(connection, transaction, """
            SELECT "CurrentRunJson"::text,"LastRunJson"::text,"NextScheduledAt","WorkerLastSeenAt",
                "LeaseOwner","LeaseGeneration","LeaseExpiresAt"
            FROM flowbit.retention_coordinator WHERE "Id"=1
            """ + (forUpdate ? " FOR UPDATE" + (skipLocked ? " SKIP LOCKED" : "") : ""));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new Coordinator
        {
            Current = reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<RunDocument>(reader.GetString(0), JsonOptions),
            Last = reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<RunDocument>(reader.GetString(1), JsonOptions),
            NextScheduledAt = reader.GetFieldValue<DateTimeOffset>(2),
            WorkerLastSeenAt = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            LeaseOwner = reader.IsDBNull(4) ? null : reader.GetString(4), LeaseGeneration = reader.GetInt64(5),
            LeaseExpiresAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)
        };
    }

    private static async Task SaveStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Coordinator state, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            UPDATE flowbit.retention_coordinator SET "CurrentRunJson"=@current,"LastRunJson"=@last,
                "NextScheduledAt"=@next,"WorkerLastSeenAt"=@seen,"LeaseOwner"=@owner,
                "LeaseGeneration"=@generation,"LeaseExpiresAt"=@expires WHERE "Id"=1
            """);
        Add(command, "current", state.Current is null ? null : JsonSerializer.Serialize(state.Current, JsonOptions), NpgsqlDbType.Jsonb);
        Add(command, "last", state.Last is null ? null : JsonSerializer.Serialize(state.Last, JsonOptions), NpgsqlDbType.Jsonb);
        Add(command, "next", state.NextScheduledAt); Add(command, "seen", state.WorkerLastSeenAt, NpgsqlDbType.TimestampTz);
        Add(command, "owner", state.LeaseOwner, NpgsqlDbType.Text); Add(command, "generation", state.LeaseGeneration);
        Add(command, "expires", state.LeaseExpiresAt, NpgsqlDbType.TimestampTz);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ConfigureTimeoutsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        int seconds, int lockMilliseconds, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT set_config('statement_timeout',@statement,true), set_config('lock_timeout',@lock,true)");
        Add(command, "statement", $"{seconds * 1000}ms"); Add(command, "lock", $"{lockMilliseconds}ms");
        await command.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql) => new(sql, connection, transaction);
    private static void Add(NpgsqlCommand command, string name, object? value, NpgsqlDbType? type = null)
    {
        var parameter = new NpgsqlParameter { ParameterName = name, Value = value ?? DBNull.Value };
        if (type is not null) parameter.NpgsqlDbType = type.Value;
        command.Parameters.Add(parameter);
    }

    private static void Validate(string category, int? days)
    {
        if (!RetentionCategories.All.Contains(category, StringComparer.Ordinal))
            throw new WorkflowDomainException("Unknown retention category.");
        if (days is < 1 or > 36500) throw new WorkflowDomainException("Retention must be between 1 and 36500 days, or Keep forever.");
    }

    private static string RequireActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().EnumerateRunes().Count() > 300)
            throw new WorkflowDomainException("A valid actor identity is required.");
        return actor.Trim();
    }

    private enum TableKind { Instance, Shared, Jobs, Incidents, Snapshots }
    private sealed record TableSpec(string Name, TableKind Kind, string Protection);
    private sealed record BatchResult(long Deleted, long Protected, bool Done,
        IReadOnlyDictionary<string, long>? DeletedByTable = null);
    private static string AgePredicate(TableSpec spec) => spec.Kind switch
    {
        TableKind.Instance => "i.\"Status\" IN ('completed','cancelled','faulted') AND i.\"FinishedAt\"<@cutoff",
        TableKind.Shared => "((t.\"InstanceId\" IS NOT NULL AND i.\"Status\" IN ('completed','cancelled','faulted') AND i.\"FinishedAt\"<@cutoff) OR (t.\"InstanceId\" IS NULL AND t.\"CreatedAt\"<@cutoff))",
        TableKind.Jobs => "t.\"Status\" IN ('completed','cancelled','skipped') AND t.\"CompletedAt\"<@cutoff",
        TableKind.Incidents => "t.\"Status\"='resolved' AND t.\"ResolvedAt\"<@cutoff",
        _ => "TRUE"
    };

    private static TableSpec[] Tables(string category) => category switch
    {
        RetentionCategories.WorkflowHistory => [
            new("instance_history", TableKind.Instance, "EXISTS (SELECT 1 FROM flowbit.message_delivery_receipts r WHERE r.\"WaitHistoryId\"=t.\"Id\")"),
            new("sequence_flow_occurrences", TableKind.Instance, "FALSE")],
        RetentionCategories.VariableHistory => [new("instance_variables", TableKind.Instance,
            "EXISTS (SELECT 1 FROM flowbit.instance_variable_current_values v WHERE v.\"InstanceId\"=t.\"InstanceId\" AND v.\"SourceVariableId\"=t.\"Id\")")],
        RetentionCategories.NodeActivity => [new("node_executions", TableKind.Instance,
            "t.\"Status\" NOT IN ('completed','cancelled','faulted','merged') OR EXISTS (SELECT 1 FROM flowbit.execution_tokens e WHERE e.\"CurrentNodeExecutionId\"=t.\"Id\") OR EXISTS (SELECT 1 FROM flowbit.instance_variables v WHERE v.\"NodeExecutionId\"=t.\"Id\") OR EXISTS (SELECT 1 FROM flowbit.shared_variable_revisions r WHERE r.\"NodeExecutionId\"=t.\"Id\")")],
        RetentionCategories.AdministrativeAudits => [new("instance_variable_updates", TableKind.Instance,
            "t.\"IdempotencyKey\" IS NOT NULL OR t.\"BatchId\" IS NOT NULL OR EXISTS (SELECT 1 FROM flowbit.instance_variables v WHERE v.\"InstanceVariableUpdateAuditId\"=t.\"Id\")"),
            new("workflow_instance_version_changes", TableKind.Instance, "t.\"BatchId\" IS NOT NULL")],
        RetentionCategories.SharedVariableHistory => [new("shared_variable_revisions", TableKind.Shared,
            "EXISTS (SELECT 1 FROM flowbit.shared_variables s WHERE s.\"Id\"=t.\"SharedVariableId\" AND (s.\"CurrentRevision\"=t.\"Revision\" OR s.\"ValueRevision\"=t.\"Revision\")) OR EXISTS (SELECT 1 FROM flowbit.shared_variable_current_values v WHERE v.\"SourceRevisionId\"=t.\"Id\") OR EXISTS (SELECT 1 FROM flowbit.shared_variable_requests r WHERE r.\"SharedVariableId\"=t.\"SharedVariableId\" AND r.\"ResultRevision\"=t.\"Revision\")")],
        RetentionCategories.CompletedJobs => [new("workflow_jobs", TableKind.Jobs,
            "t.\"WorkerId\" IS NOT NULL OR t.\"LeaseToken\" IS NOT NULL OR t.\"LeaseExpiresAt\" IS NOT NULL OR EXISTS (SELECT 1 FROM flowbit.workflow_incidents i WHERE i.\"JobId\"=t.\"Id\" AND i.\"Status\"='open') OR EXISTS (SELECT 1 FROM flowbit.execution_tokens e WHERE e.\"WaitingJobId\"=t.\"Id\")"),
            new("workflow_job_snapshots", TableKind.Snapshots, "EXISTS (SELECT 1 FROM flowbit.workflow_jobs j WHERE j.\"SnapshotId\"=t.\"Id\")")],
        RetentionCategories.ResolvedIncidents => [new("workflow_incidents", TableKind.Incidents, "FALSE")],
        _ => throw new WorkflowDomainException("Unknown retention category.")
    };

    private sealed class Coordinator
    {
        public RunDocument? Current { get; set; }
        public RunDocument? Last { get; set; }
        public DateTimeOffset NextScheduledAt { get; set; }
        public DateTimeOffset? WorkerLastSeenAt { get; set; }
        public string? LeaseOwner { get; set; }
        public long LeaseGeneration { get; set; }
        public DateTimeOffset? LeaseExpiresAt { get; set; }
    }

    private sealed class AttemptFence
    {
        public Guid? RunId { get; set; }
        public string? WorkerId { get; set; }
        public long Generation { get; set; }
        public string? Category { get; set; }
    }

    public sealed class RunDocument
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "queued";
        public DateTimeOffset RequestedAt { get; set; }
        public string? RequestedBy { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? Message { get; set; }
        public int BatchCount { get; set; }
        public List<CategoryDocument> Categories { get; set; } = [];
        public RetentionRunDto ToDto() => new(Id, Status, RequestedAt, RequestedBy, StartedAt, CompletedAt,
            Message, Categories.Select(item => new RetentionCategoryProgressDto(item.Category, item.Cutoff,
                item.PolicyRevision, item.Status, item.DeletedRows, item.ProtectedRows, item.Message)).ToArray());
    }

    public sealed class CategoryDocument
    {
        public string Category { get; set; } = string.Empty;
        public DateTimeOffset? Cutoff { get; set; }
        public long PolicyRevision { get; set; }
        public string Status { get; set; } = "pending";
        public long DeletedRows { get; set; }
        public long ProtectedRows { get; set; }
        public string? Message { get; set; }
        public int TableIndex { get; set; }
        public long OwnerCursor { get; set; }
        public long RowCursor { get; set; }
        public Dictionary<string, long> TableUpperIds { get; set; } = [];
        public int BatchCount { get; set; }
        public long ElapsedMilliseconds { get; set; }
    }
}
