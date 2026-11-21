using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Flowbit.Infrastructure.Repositories;

/// <summary>
/// Broadcasts, their frozen deliveries, and delivery job links. Nothing here locks a
/// runtime row: delivery rows name instances, tokens, and subscriptions by plain ids.
/// </summary>
public sealed class SignalBroadcastRepository(AppDbContext dbContext) : ISignalBroadcastRepository
{
    public async Task<SignalBroadcastRecord?> FindByIdempotencyKeyAsync(
        string thrownByKind,
        string thrownBy,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SignalBroadcasts.AsNoTracking()
            .SingleOrDefaultAsync(
                broadcast => broadcast.ThrownByKind == thrownByKind
                    && broadcast.ThrownBy == thrownBy
                    && broadcast.IdempotencyKey == idempotencyKey,
                cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public Task LockIdempotencyKeyAsync(
        string thrownByKind,
        string thrownBy,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        // A hash collision only serializes two unrelated throws briefly.
        var lockKey = $"{thrownByKind}:{thrownBy}:{idempotencyKey}";
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('signal-broadcast'), hashtext({lockKey}))",
            cancellationToken);
    }

    public Task<int> CountOpenAsync(CancellationToken cancellationToken) =>
        dbContext.SignalBroadcasts.AsNoTracking()
            .CountAsync(broadcast => broadcast.Status == SignalBroadcastStatuses.Delivering, cancellationToken);

    public async Task<SignalBacklogRecord> GetBacklogAsync(CancellationToken cancellationToken)
    {
        // Only a delivering broadcast has pending deliveries: a broadcast leaves that status
        // under its row lock once its recount finds none pending, and a cancel or retry
        // changes deliveries under the same lock. So the count starts from the few open
        // broadcasts and reads each one's pending rows through its (BroadcastId, Status, Id)
        // index, never the whole delivery table, which keeps every finished delivery.
        var pending = await dbContext.Database.SqlQuery<long>(
                $"""
                SELECT COALESCE(sum(open."Pending"), 0)::bigint AS "Value"
                FROM flowbit.signal_broadcasts AS broadcast
                CROSS JOIN LATERAL (
                    SELECT count(*) AS "Pending"
                    FROM flowbit.signal_deliveries AS delivery
                    WHERE delivery."BroadcastId" = broadcast."Id"
                      AND delivery."Status" = 'pending'
                ) AS open
                WHERE broadcast."Status" = 'delivering'
                """)
            .SingleAsync(cancellationToken);
        var oldest = await dbContext.SignalBroadcasts.AsNoTracking()
            .Where(broadcast => broadcast.Status == SignalBroadcastStatuses.Delivering)
            .OrderBy(broadcast => broadcast.CreatedAt)
            .Select(broadcast => (DateTimeOffset?)broadcast.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return new SignalBacklogRecord(pending, oldest, DateTimeOffset.UtcNow);
    }

    public async Task<SignalBroadcastRecord> AddAsync(
        NewSignalBroadcastRecord broadcast,
        CancellationToken cancellationToken)
    {
        if (broadcast.Scope.Kind == SignalBroadcastScopeKind.Instance && broadcast.Scope.WorkflowKey is null)
        {
            throw new InvalidOperationException("An instance-scoped broadcast records its instance's family.");
        }

        var entity = new SignalBroadcastEntity
        {
            SignalName = broadcast.SignalName,
            WorkflowKey = broadcast.Scope.WorkflowKey,
            BusinessKey = broadcast.Scope.BusinessKey,
            InstanceId = broadcast.Scope.InstanceId,
            PayloadJson = broadcast.PayloadJson,
            Status = SignalBroadcastStatuses.Delivering,
            RecipientsJson = "[]",
            ThrownByKind = broadcast.ThrownByKind,
            ThrownBy = broadcast.ThrownBy,
            ThrownByRolesJson = broadcast.ThrownByRolesJson,
            ThrownByClaimsJson = broadcast.ThrownByClaimsJson,
            IdempotencyKey = broadcast.IdempotencyKey,
            RequestHash = broadcast.RequestHash,
            CreatedAt = broadcast.CreatedAt,
            UpdatedAt = broadcast.CreatedAt
        };
        dbContext.SignalBroadcasts.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.Entry(entity).State = EntityState.Detached;
        return Map(entity);
    }

    public Task<int> FreezeDeliveriesAsync(
        long broadcastId,
        string signalName,
        SignalBroadcastScope scope,
        int limit,
        CancellationToken cancellationToken)
    {
        var take = limit + 1;
        // One literal statement per scope, so each keeps a stable plan. Each is one
        // statement without a locking clause: the recipients are exactly the
        // subscriptions committed and active in its snapshot, and no transaction that
        // holds a recipient's instance or token delays it.
        return scope.Kind switch
        {
            SignalBroadcastScopeKind.AllWorkflows => dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO flowbit.signal_deliveries
                    ("BroadcastId", "SubscriptionId", "InstanceId", "WorkflowDefinitionId", "WorkflowKey",
                     "TokenId", "ActivationId", "SignalNodeId", "AttachedToNodeId", "CancelActivity",
                     "Status", "AttemptCount", "CreatedAt", "UpdatedAt")
                SELECT {broadcastId}, s."Id", s."InstanceId", s."WorkflowDefinitionId", s."WorkflowKey",
                       s."TokenId", s."ActivationId", s."SignalNodeId", s."AttachedToNodeId", s."CancelActivity",
                       'pending', 0, now(), now()
                FROM flowbit.signal_subscriptions s
                WHERE s."Status" = 'active'
                  AND s."SignalName" = {signalName}
                ORDER BY s."Id"
                LIMIT {take}
                """,
                cancellationToken),
            SignalBroadcastScopeKind.Family => dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO flowbit.signal_deliveries
                    ("BroadcastId", "SubscriptionId", "InstanceId", "WorkflowDefinitionId", "WorkflowKey",
                     "TokenId", "ActivationId", "SignalNodeId", "AttachedToNodeId", "CancelActivity",
                     "Status", "AttemptCount", "CreatedAt", "UpdatedAt")
                SELECT {broadcastId}, s."Id", s."InstanceId", s."WorkflowDefinitionId", s."WorkflowKey",
                       s."TokenId", s."ActivationId", s."SignalNodeId", s."AttachedToNodeId", s."CancelActivity",
                       'pending', 0, now(), now()
                FROM flowbit.signal_subscriptions s
                WHERE s."Status" = 'active'
                  AND s."SignalName" = {signalName}
                  AND s."WorkflowKey" = {scope.WorkflowKey}
                ORDER BY s."Id"
                LIMIT {take}
                """,
                cancellationToken),
            // Starts from the (WorkflowKey, BusinessKey, Status) index of the instances,
            // which returns the one running owner and any finished instances with the
            // key, and probes each one's active subscriptions. A subscription is active
            // only while its instance runs, so the instance's status needs no filter.
            SignalBroadcastScopeKind.BusinessKey => dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO flowbit.signal_deliveries
                    ("BroadcastId", "SubscriptionId", "InstanceId", "WorkflowDefinitionId", "WorkflowKey",
                     "TokenId", "ActivationId", "SignalNodeId", "AttachedToNodeId", "CancelActivity",
                     "Status", "AttemptCount", "CreatedAt", "UpdatedAt")
                SELECT {broadcastId}, s."Id", s."InstanceId", s."WorkflowDefinitionId", s."WorkflowKey",
                       s."TokenId", s."ActivationId", s."SignalNodeId", s."AttachedToNodeId", s."CancelActivity",
                       'pending', 0, now(), now()
                FROM flowbit.workflow_instances w
                JOIN flowbit.signal_subscriptions s ON s."InstanceId" = w."Id"
                WHERE w."WorkflowKey" = {scope.WorkflowKey}
                  AND w."BusinessKey" = {scope.BusinessKey}
                  AND s."Status" = 'active'
                  AND s."SignalName" = {signalName}
                ORDER BY s."Id"
                LIMIT {take}
                """,
                cancellationToken),
            SignalBroadcastScopeKind.Instance => dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO flowbit.signal_deliveries
                    ("BroadcastId", "SubscriptionId", "InstanceId", "WorkflowDefinitionId", "WorkflowKey",
                     "TokenId", "ActivationId", "SignalNodeId", "AttachedToNodeId", "CancelActivity",
                     "Status", "AttemptCount", "CreatedAt", "UpdatedAt")
                SELECT {broadcastId}, s."Id", s."InstanceId", s."WorkflowDefinitionId", s."WorkflowKey",
                       s."TokenId", s."ActivationId", s."SignalNodeId", s."AttachedToNodeId", s."CancelActivity",
                       'pending', 0, now(), now()
                FROM flowbit.signal_subscriptions s
                WHERE s."Status" = 'active'
                  AND s."SignalName" = {signalName}
                  AND s."InstanceId" = {scope.InstanceId!.Value}
                ORDER BY s."Id"
                LIMIT {take}
                """,
                cancellationToken),
            _ => throw new InvalidOperationException($"Unknown signal broadcast scope {scope.Kind}.")
        };
    }

    public Task<string?> FindInstanceWorkflowKeyAsync(long instanceId, CancellationToken cancellationToken) =>
        dbContext.WorkflowInstances.AsNoTracking()
            .Where(instance => instance.Id == instanceId)
            .Select(instance => instance.WorkflowKey)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SignalRecipientDefinitionRecord>> ListRecipientDefinitionsAsync(
        long broadcastId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.SignalDeliveries.AsNoTracking()
            .Where(delivery => delivery.BroadcastId == broadcastId)
            .GroupBy(delivery => new { delivery.WorkflowDefinitionId, delivery.WorkflowKey })
            .Select(group => new { group.Key.WorkflowDefinitionId, group.Key.WorkflowKey, Count = group.Count() })
            .OrderBy(row => row.WorkflowDefinitionId)
            .ToListAsync(cancellationToken);
        return rows.Select(row => new SignalRecipientDefinitionRecord(row.WorkflowDefinitionId, row.WorkflowKey, row.Count))
            .ToArray();
    }

    public async Task<IReadOnlyList<SignalRecipientDefinitionRecord>> ListPendingDefinitionsAsync(
        long broadcastId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.SignalDeliveries.AsNoTracking()
            .Where(delivery =>
                delivery.BroadcastId == broadcastId
                && delivery.Status == SignalDeliveryStatuses.Pending)
            .GroupBy(delivery => new { delivery.WorkflowDefinitionId, delivery.WorkflowKey })
            .Select(group => new { group.Key.WorkflowDefinitionId, group.Key.WorkflowKey, Count = group.Count() })
            .OrderBy(row => row.WorkflowDefinitionId)
            .ToListAsync(cancellationToken);
        return rows.Select(row => new SignalRecipientDefinitionRecord(row.WorkflowDefinitionId, row.WorkflowKey, row.Count))
            .ToArray();
    }

    public Task SetRecipientsAsync(
        long broadcastId,
        int total,
        string recipientsJson,
        bool recipientsTruncated,
        string status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? completedAt = status == SignalBroadcastStatuses.Delivering ? null : now;
        return dbContext.SignalBroadcasts
            .Where(broadcast => broadcast.Id == broadcastId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(broadcast => broadcast.TotalDeliveryCount, total)
                .SetProperty(broadcast => broadcast.RecipientsJson, recipientsJson)
                .SetProperty(broadcast => broadcast.RecipientsTruncated, recipientsTruncated)
                .SetProperty(broadcast => broadcast.Status, status)
                .SetProperty(broadcast => broadcast.CompletedAt, completedAt)
                .SetProperty(broadcast => broadcast.UpdatedAt, now),
                cancellationToken);
    }

    public async Task AddJobLinkAsync(
        long broadcastId,
        long workflowDefinitionId,
        long jobId,
        string reason,
        string requestedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var entity = new SignalBroadcastJobEntity
        {
            BroadcastId = broadcastId,
            WorkflowDefinitionId = workflowDefinitionId,
            JobId = jobId,
            OriginalJobId = jobId,
            Reason = reason,
            RequestedBy = requestedBy,
            CreatedAt = now
        };
        dbContext.SignalBroadcastJobs.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.Entry(entity).State = EntityState.Detached;
    }

    public async Task<SignalBroadcastRecord?> GetAsync(
        long broadcastId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var entity = forUpdate
            ? await dbContext.SignalBroadcasts
                .FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM flowbit.signal_broadcasts
                    WHERE "Id" = {broadcastId}
                    FOR UPDATE
                    """)
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.SignalBroadcasts.AsNoTracking()
                .SingleOrDefaultAsync(broadcast => broadcast.Id == broadcastId, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<PagedResult<SignalBroadcastRecord>> ListAsync(
        SignalBroadcastQuery query,
        CancellationToken cancellationToken)
    {
        var source = dbContext.SignalBroadcasts.AsNoTracking();
        if (!string.IsNullOrEmpty(query.Name))
        {
            source = source.Where(broadcast => broadcast.SignalName == query.Name);
        }
        if (!string.IsNullOrEmpty(query.WorkflowKey))
        {
            source = source.Where(broadcast => broadcast.WorkflowKey == query.WorkflowKey);
        }
        if (!string.IsNullOrEmpty(query.Status))
        {
            source = source.Where(broadcast => broadcast.Status == query.Status);
        }
        if (query.ThrownByKind is not null && query.ThrownBy is not null)
        {
            source = source.Where(broadcast =>
                broadcast.ThrownByKind == query.ThrownByKind
                && broadcast.ThrownBy == query.ThrownBy);
        }

        var total = await source.LongCountAsync(cancellationToken);
        var items = await source
            .OrderByDescending(broadcast => broadcast.CreatedAt)
            .ThenByDescending(broadcast => broadcast.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<SignalBroadcastRecord>(
            items.Select(Map).ToArray(),
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<PagedResult<SignalDeliveryRecord>> ListDeliveriesAsync(
        SignalDeliveryQuery query,
        CancellationToken cancellationToken)
    {
        var source = dbContext.SignalDeliveries.AsNoTracking()
            .Where(delivery => delivery.BroadcastId == query.BroadcastId);
        if (!string.IsNullOrEmpty(query.Status))
        {
            source = source.Where(delivery => delivery.Status == query.Status);
        }
        if (query.InstanceId is long instanceId)
        {
            source = source.Where(delivery => delivery.InstanceId == instanceId);
        }

        var total = await source.LongCountAsync(cancellationToken);
        var items = await source
            .OrderBy(delivery => delivery.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<SignalDeliveryRecord>(
            items.Select(MapDelivery).ToArray(),
            query.Page,
            query.PageSize,
            total);
    }

    public async Task<SignalBroadcastCountsRecord> CountDeliveriesAsync(
        long broadcastId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.SignalDeliveries.AsNoTracking()
            .Where(delivery => delivery.BroadcastId == broadcastId)
            .GroupBy(delivery => delivery.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        int Count(string status) => rows.FirstOrDefault(row => row.Status == status)?.Count ?? 0;
        return new SignalBroadcastCountsRecord(
            Count(SignalDeliveryStatuses.Pending),
            Count(SignalDeliveryStatuses.Delivered),
            Count(SignalDeliveryStatuses.Skipped),
            Count(SignalDeliveryStatuses.Failed),
            Count(SignalDeliveryStatuses.Cancelled));
    }

    public async Task<SignalBroadcastRecord?> RefreshCountsAsync(
        long broadcastId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The caller already holds the broadcast's row lock, so this statement's snapshot
        // includes the status any cancel or retry committed before it: a recount in one
        // autocommit statement would wait for that lock and then write a status computed
        // from its older snapshot.
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Signal broadcast counts are refreshed only under the broadcast's row lock.");
        }

        // Writes only when a counter or the status changes, so a poll that sees
        // nothing new leaves UpdatedAt, the retry fence, alone.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH counts AS (
                SELECT
                    count(*) FILTER (WHERE "Status" = 'pending')::int AS pending,
                    count(*) FILTER (WHERE "Status" = 'delivered')::int AS delivered,
                    count(*) FILTER (WHERE "Status" = 'skipped')::int AS skipped,
                    count(*) FILTER (WHERE "Status" = 'failed')::int AS failed,
                    count(*) FILTER (WHERE "Status" = 'cancelled')::int AS cancelled
                FROM flowbit.signal_deliveries
                WHERE "BroadcastId" = {broadcastId}
            ), next AS (
                SELECT
                    c.*,
                    CASE
                        WHEN b."Status" = 'delivering' AND c.pending = 0
                            THEN CASE WHEN c.failed > 0 THEN 'completedWithIssues' ELSE 'completed' END
                        WHEN b."Status" IN ('completed', 'completedWithIssues')
                            THEN CASE WHEN c.failed > 0 THEN 'completedWithIssues' ELSE 'completed' END
                        ELSE b."Status"
                    END AS status
                FROM flowbit.signal_broadcasts b, counts c
                WHERE b."Id" = {broadcastId}
            )
            UPDATE flowbit.signal_broadcasts b
            SET "DeliveredCount" = n.delivered,
                "SkippedCount" = n.skipped,
                "FailedCount" = n.failed,
                "CancelledCount" = n.cancelled,
                "Status" = n.status,
                "CompletedAt" = CASE
                    WHEN n.status = 'delivering' THEN NULL
                    ELSE COALESCE(b."CompletedAt", {now})
                END,
                "UpdatedAt" = {now}
            FROM next n
            WHERE b."Id" = {broadcastId}
              AND (b."DeliveredCount" IS DISTINCT FROM n.delivered
                   OR b."SkippedCount" IS DISTINCT FROM n.skipped
                   OR b."FailedCount" IS DISTINCT FROM n.failed
                   OR b."CancelledCount" IS DISTINCT FROM n.cancelled
                   OR b."Status" IS DISTINCT FROM n.status)
            """,
            cancellationToken);
        return await GetAsync(broadcastId, forUpdate: false, cancellationToken);
    }

    public async Task<IReadOnlyList<long>> ListPendingDeliveryIdsAsync(
        long broadcastId,
        long workflowDefinitionId,
        long afterId,
        int limit,
        CancellationToken cancellationToken) =>
        await dbContext.SignalDeliveries.AsNoTracking()
            .Where(delivery =>
                delivery.BroadcastId == broadcastId
                && delivery.WorkflowDefinitionId == workflowDefinitionId
                && delivery.Status == SignalDeliveryStatuses.Pending
                && delivery.Id > afterId)
            .OrderBy(delivery => delivery.Id)
            .Select(delivery => delivery.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<SignalDeliveryRecord?> GetDeliveryAsync(
        long deliveryId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SignalDeliveries.AsNoTracking()
            .SingleOrDefaultAsync(delivery => delivery.Id == deliveryId, cancellationToken);
        return entity is null ? null : MapDelivery(entity);
    }

    public async Task<SignalDeliveryRecord?> GetDeliveryForUpdateAsync(
        long deliveryId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SignalDeliveries
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM flowbit.signal_deliveries
                WHERE "Id" = {deliveryId}
                FOR UPDATE
                """)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        return entity is null ? null : MapDelivery(entity);
    }

    public async Task<bool> CompleteDeliveryAsync(
        long deliveryId,
        string status,
        string? resultCode,
        string? resultDescription,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var affected = await dbContext.SignalDeliveries
            .Where(delivery =>
                delivery.Id == deliveryId
                && delivery.Status == SignalDeliveryStatuses.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(delivery => delivery.Status, status)
                .SetProperty(delivery => delivery.ResultCode, resultCode)
                .SetProperty(delivery => delivery.ResultDescription, resultDescription)
                .SetProperty(delivery => delivery.AttemptCount, delivery => delivery.AttemptCount + 1)
                .SetProperty(delivery => delivery.CompletedAt, now)
                .SetProperty(delivery => delivery.UpdatedAt, now),
                cancellationToken);
        return affected == 1;
    }

    public Task<int> CancelPendingDeliveriesAsync(
        long broadcastId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        dbContext.SignalDeliveries
            .Where(delivery =>
                delivery.BroadcastId == broadcastId
                && delivery.Status == SignalDeliveryStatuses.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(delivery => delivery.Status, SignalDeliveryStatuses.Cancelled)
                .SetProperty(delivery => delivery.ResultCode, SignalDeliveryResultCodes.BroadcastCancelled)
                .SetProperty(delivery => delivery.CompletedAt, now)
                .SetProperty(delivery => delivery.UpdatedAt, now),
                cancellationToken);

    public async Task<IReadOnlyList<long>> RetryFailedDeliveriesAsync(
        long broadcastId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // A retried delivery moves to the version its instance runs now: a version change
        // since the throw may have left no instance on the earlier version, which can then
        // be deleted, and a delivery job needs an existing version. Instances are never
        // deleted, so the current version always exists.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE flowbit.signal_deliveries AS delivery
            SET "Status" = 'pending',
                "ResultCode" = NULL,
                "ResultDescription" = NULL,
                "CompletedAt" = NULL,
                "UpdatedAt" = {now},
                "WorkflowDefinitionId" = COALESCE(
                    (SELECT instance."WorkflowDefinitionId"
                     FROM flowbit.workflow_instances AS instance
                     WHERE instance."Id" = delivery."InstanceId"),
                    delivery."WorkflowDefinitionId")
            WHERE delivery."BroadcastId" = {broadcastId}
              AND delivery."Status" = 'failed'
            """,
            cancellationToken);

        // Only a delivering broadcast has pending deliveries, and the caller retries a
        // finished one under its row lock, so the pending rows are exactly those retried.
        return await dbContext.SignalDeliveries.AsNoTracking()
            .Where(delivery =>
                delivery.BroadcastId == broadcastId
                && delivery.Status == SignalDeliveryStatuses.Pending)
            .Select(delivery => delivery.WorkflowDefinitionId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
    }

    public Task MarkCancelledAsync(
        long broadcastId,
        string cancelledBy,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        dbContext.SignalBroadcasts
            .Where(broadcast => broadcast.Id == broadcastId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(broadcast => broadcast.Status, SignalBroadcastStatuses.Cancelled)
                .SetProperty(broadcast => broadcast.CancelledBy, cancelledBy)
                .SetProperty(broadcast => broadcast.CancellationReason, reason)
                .SetProperty(broadcast => broadcast.CancelledAt, now)
                .SetProperty(broadcast => broadcast.CompletedAt, now)
                .SetProperty(broadcast => broadcast.UpdatedAt, now),
                cancellationToken);

    public Task ReopenAsync(long broadcastId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.SignalBroadcasts
            .Where(broadcast => broadcast.Id == broadcastId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(broadcast => broadcast.Status, SignalBroadcastStatuses.Delivering)
                .SetProperty(broadcast => broadcast.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(broadcast => broadcast.UpdatedAt, now),
                cancellationToken);

    public Task<int> FailPendingDeliveriesAsync(
        long broadcastId,
        long workflowDefinitionId,
        string resultCode,
        string resultDescription,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        dbContext.SignalDeliveries
            .Where(delivery =>
                delivery.BroadcastId == broadcastId
                && delivery.WorkflowDefinitionId == workflowDefinitionId
                && delivery.Status == SignalDeliveryStatuses.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(delivery => delivery.Status, SignalDeliveryStatuses.Failed)
                .SetProperty(delivery => delivery.ResultCode, resultCode)
                .SetProperty(delivery => delivery.ResultDescription, resultDescription)
                .SetProperty(delivery => delivery.AttemptCount, delivery => delivery.AttemptCount + 1)
                .SetProperty(delivery => delivery.CompletedAt, now)
                .SetProperty(delivery => delivery.UpdatedAt, now),
                cancellationToken);

    private static SignalBroadcastRecord Map(SignalBroadcastEntity entity) =>
        new(
            entity.Id,
            entity.SignalName,
            entity.WorkflowKey,
            entity.BusinessKey,
            entity.InstanceId,
            entity.PayloadJson,
            entity.Status,
            entity.TotalDeliveryCount,
            entity.DeliveredCount,
            entity.SkippedCount,
            entity.FailedCount,
            entity.CancelledCount,
            entity.RecipientsJson,
            entity.RecipientsTruncated,
            entity.ThrownByKind,
            entity.ThrownBy,
            entity.ThrownByRolesJson,
            entity.ThrownByClaimsJson,
            entity.IdempotencyKey,
            entity.RequestHash,
            entity.CancelledBy,
            entity.CancellationReason,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CompletedAt,
            entity.CancelledAt);

    private static SignalDeliveryRecord MapDelivery(SignalDeliveryEntity entity) =>
        new(
            entity.Id,
            entity.BroadcastId,
            entity.SubscriptionId,
            entity.InstanceId,
            entity.WorkflowDefinitionId,
            entity.WorkflowKey,
            entity.TokenId,
            entity.ActivationId,
            entity.SignalNodeId,
            entity.AttachedToNodeId,
            entity.CancelActivity,
            entity.Status,
            entity.AttemptCount,
            entity.ResultCode,
            entity.ResultDescription,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CompletedAt);
}
