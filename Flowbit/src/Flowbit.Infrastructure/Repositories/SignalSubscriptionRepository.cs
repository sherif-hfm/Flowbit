using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Microsoft.EntityFrameworkCore;

namespace Flowbit.Infrastructure.Repositories;

/// <summary>
/// Signal subscriptions. Every write runs under the owning instance's lock, so a
/// subscription is never created twice for one activation and node.
/// </summary>
public sealed class SignalSubscriptionRepository(AppDbContext dbContext) : ISignalSubscriptionRepository
{
    public async Task<IReadOnlyList<SignalSubscriptionRecord>> CreateManyAsync(
        IReadOnlyList<SignalSubscriptionCreateRecord> creates,
        CancellationToken cancellationToken)
    {
        if (creates.Count == 0)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var entities = creates.Select(create => new SignalSubscriptionEntity
        {
            InstanceId = create.InstanceId,
            WorkflowDefinitionId = create.WorkflowDefinitionId,
            WorkflowKey = create.WorkflowKey,
            TokenId = create.TokenId,
            ActivationId = create.ActivationId,
            SignalNodeId = create.SignalNodeId,
            SignalNodeName = create.SignalNodeName,
            AttachedToNodeId = create.AttachedToNodeId,
            OutgoingFlowId = create.OutgoingFlowId,
            SignalName = create.SignalName,
            CancelActivity = create.CancelActivity,
            Occurrence = 0,
            Status = SignalSubscriptionStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now
        }).ToArray();
        dbContext.SignalSubscriptions.AddRange(entities);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entities.OrderBy(entity => entity.Id).Select(Map).ToArray();
    }

    public async Task<IReadOnlyList<SignalSubscriptionRecord>> ListForActivationAsync(
        long tokenId,
        Guid activationId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.SignalSubscriptions.AsNoTracking()
            .Where(subscription =>
                subscription.TokenId == tokenId
                && subscription.ActivationId == activationId)
            .OrderBy(subscription => subscription.Id)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async Task<IReadOnlyList<SignalSubscriptionRecord>> ListActiveByInstanceAsync(
        long instanceId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        List<SignalSubscriptionEntity> entities;
        if (forUpdate)
        {
            entities = await dbContext.SignalSubscriptions
                .FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM flowbit.signal_subscriptions
                    WHERE "InstanceId" = {instanceId}
                      AND "Status" = {SignalSubscriptionStatuses.Active}
                    ORDER BY "Id"
                    FOR UPDATE
                    """)
                .AsNoTracking()
                .ToListAsync(cancellationToken);
        }
        else
        {
            entities = await dbContext.SignalSubscriptions.AsNoTracking()
                .Where(subscription =>
                    subscription.InstanceId == instanceId
                    && subscription.Status == SignalSubscriptionStatuses.Active)
                .OrderBy(subscription => subscription.Id)
                .ToListAsync(cancellationToken);
        }
        return entities.Select(Map).ToArray();
    }

    public async Task<SignalSubscriptionRecord?> GetForUpdateAsync(
        long subscriptionId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SignalSubscriptions
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM flowbit.signal_subscriptions
                WHERE "Id" = {subscriptionId}
                FOR UPDATE
                """)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<bool> CompleteAsync(long subscriptionId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = await dbContext.SignalSubscriptions
            .Where(subscription =>
                subscription.Id == subscriptionId
                && subscription.Status == SignalSubscriptionStatuses.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(subscription => subscription.Status, SignalSubscriptionStatuses.Completed)
                .SetProperty(subscription => subscription.Occurrence, subscription => subscription.Occurrence + 1)
                .SetProperty(subscription => subscription.CompletedAt, now)
                .SetProperty(subscription => subscription.UpdatedAt, now),
                cancellationToken);
        return affected == 1;
    }

    public async Task<bool> RecordOccurrenceAsync(long subscriptionId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = await dbContext.SignalSubscriptions
            .Where(subscription =>
                subscription.Id == subscriptionId
                && subscription.Status == SignalSubscriptionStatuses.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(subscription => subscription.Occurrence, subscription => subscription.Occurrence + 1)
                .SetProperty(subscription => subscription.UpdatedAt, now),
                cancellationToken);
        return affected == 1;
    }

    public Task<int> CancelByTokenIdsAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        var ids = tokenIds.Distinct().ToArray();
        return CancelAsync(
            dbContext.SignalSubscriptions.Where(subscription =>
                subscription.InstanceId == instanceId
                && ids.Contains(subscription.TokenId)),
            cancellationToken);
    }

    public Task<int> CancelByInstanceAsync(long instanceId, CancellationToken cancellationToken) =>
        CancelAsync(
            dbContext.SignalSubscriptions.Where(subscription => subscription.InstanceId == instanceId),
            cancellationToken);

    public async Task<IReadOnlyList<SignalSubscriptionCountRecord>> SummarizeActiveAsync(
        string? signalName,
        SignalSubscriptionFilter filter,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = dbContext.SignalSubscriptions.AsNoTracking()
            .Where(subscription => subscription.Status == SignalSubscriptionStatuses.Active);
        if (!string.IsNullOrEmpty(signalName))
        {
            query = query.Where(subscription => subscription.SignalName == signalName);
        }
        if (filter.WorkflowKey is { } workflowKey)
        {
            query = query.Where(subscription => subscription.WorkflowKey == workflowKey);
        }
        if (filter.BusinessKey is { } businessKey)
        {
            // The key is on the instance; the family is on both rows. A plain read, as
            // the throw's freeze statement is.
            var familyKey = filter.WorkflowKey;
            query = query.Where(subscription => dbContext.WorkflowInstances.Any(instance =>
                instance.Id == subscription.InstanceId
                && instance.WorkflowKey == familyKey
                && instance.BusinessKey == businessKey));
        }
        if (filter.InstanceId is long instanceId)
        {
            query = query.Where(subscription => subscription.InstanceId == instanceId);
        }

        var rows = query.Select(subscription => new SummaryRow
        {
            SignalName = subscription.SignalName,
            WorkflowKey = subscription.WorkflowKey,
            Waits = 1,
            Starts = 0
        });

        // A business-key or instance filter names existing instances, which no start is. The
        // starts join the waits before grouping, ordering, and the limit, so one statement cuts
        // the merged list and no family past the cut of one list is counted from the other.
        if (filter.BusinessKey is null && filter.InstanceId is null)
        {
            var starts = dbContext.SignalStartSubscriptions.AsNoTracking();
            if (!string.IsNullOrEmpty(signalName))
            {
                starts = starts.Where(start => start.SignalName == signalName);
            }
            if (filter.WorkflowKey is { } startKey)
            {
                starts = starts.Where(start => start.WorkflowKey == startKey);
            }

            rows = rows.Concat(starts.Select(start => new SummaryRow
            {
                SignalName = start.SignalName,
                WorkflowKey = start.WorkflowKey,
                Waits = 0,
                Starts = 1
            }));
        }

        var counted = await rows
            .GroupBy(row => new { row.SignalName, row.WorkflowKey })
            .Select(group => new
            {
                group.Key.SignalName,
                group.Key.WorkflowKey,
                Count = group.Sum(row => row.Waits),
                StartCount = group.Sum(row => row.Starts)
            })
            .OrderBy(row => row.SignalName)
            .ThenBy(row => row.WorkflowKey)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return counted
            .Select(row => new SignalSubscriptionCountRecord(row.SignalName, row.WorkflowKey, row.Count, row.StartCount))
            .ToArray();
    }

    /// <summary>One wait or one start of the subscription summary, before grouping.</summary>
    private sealed class SummaryRow
    {
        public required string SignalName { get; init; }

        public required string WorkflowKey { get; init; }

        public int Waits { get; init; }

        public int Starts { get; init; }
    }

    private static async Task<int> CancelAsync(
        IQueryable<SignalSubscriptionEntity> source,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await source
            .Where(subscription => subscription.Status == SignalSubscriptionStatuses.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(subscription => subscription.Status, SignalSubscriptionStatuses.Cancelled)
                .SetProperty(subscription => subscription.CompletedAt, now)
                .SetProperty(subscription => subscription.UpdatedAt, now),
                cancellationToken);
    }

    internal static SignalSubscriptionRecord Map(SignalSubscriptionEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.WorkflowDefinitionId,
            entity.WorkflowKey,
            entity.TokenId,
            entity.ActivationId,
            entity.SignalNodeId,
            entity.SignalNodeName,
            entity.AttachedToNodeId,
            entity.OutgoingFlowId,
            entity.SignalName,
            entity.CancelActivity,
            entity.Occurrence,
            entity.Status,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CompletedAt);
}
