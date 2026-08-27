using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

public sealed class ConditionalBoundarySubscriptionRepository(AppDbContext dbContext)
    : IConditionalBoundarySubscriptionRepository
{
    public async Task<IReadOnlyList<ConditionalBoundarySubscriptionRecord>> CreateManyAsync(
        IReadOnlyList<ConditionalBoundarySubscriptionCreateRecord> creates,
        CancellationToken cancellationToken)
    {
        if (creates.Count == 0)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        var entities = creates.Select(create => new ConditionalBoundarySubscriptionEntity
        {
            InstanceId = create.InstanceId,
            WorkflowDefinitionId = create.WorkflowDefinitionId,
            WorkflowKey = create.WorkflowKey,
            HostTokenId = create.HostTokenId,
            HostActivationId = create.HostActivationId,
            BoundaryNodeId = create.BoundaryNodeId,
            BoundaryNodeName = create.BoundaryNodeName,
            AttachedToNodeId = create.AttachedToNodeId,
            OutgoingFlowId = create.OutgoingFlowId,
            Condition = create.Condition,
            DeliveryMode = create.DeliveryMode,
            CancelActivity = create.CancelActivity,
            IsConditionTrue = false,
            Occurrence = 0,
            Status = ConditionalBoundarySubscriptionStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now
        }).ToArray();
        dbContext.ConditionalBoundarySubscriptions.AddRange(entities);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return entities.OrderBy(entity => entity.Id).Select(Map).ToArray();
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
                  {
                      SqlState: PostgresErrorCodes.UniqueViolation
                  })
        {
            foreach (var entity in entities)
            {
                dbContext.Entry(entity).State = EntityState.Detached;
            }

            var first = creates[0];
            var existing = await dbContext.ConditionalBoundarySubscriptions.AsNoTracking()
                .Where(subscription =>
                    subscription.HostTokenId == first.HostTokenId
                    && subscription.HostActivationId == first.HostActivationId)
                .OrderBy(subscription => subscription.Id)
                .ToListAsync(cancellationToken);
            return existing.Select(Map).ToArray();
        }
    }

    public async Task<IReadOnlyList<ConditionalBoundarySubscriptionRecord>> ListForActivationAsync(
        long hostTokenId,
        Guid hostActivationId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.ConditionalBoundarySubscriptions.AsNoTracking()
            .Where(subscription =>
                subscription.HostTokenId == hostTokenId
                && subscription.HostActivationId == hostActivationId)
            .OrderBy(subscription => subscription.Id)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async Task<IReadOnlyList<ConditionalBoundarySubscriptionRecord>>
        ListActiveForUpdateByInstanceAndBoundaryNodeIdsAsync(
            long instanceId,
            IReadOnlyCollection<int> boundaryNodeIds,
            CancellationToken cancellationToken)
    {
        if (boundaryNodeIds.Count == 0)
        {
            return [];
        }

        var ids = boundaryNodeIds.Distinct().Order().ToArray();
        var entities = await dbContext.ConditionalBoundarySubscriptions
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM flowbit.conditional_boundary_subscriptions
                WHERE "InstanceId" = {instanceId}
                  AND "Status" = {ConditionalBoundarySubscriptionStatuses.Active}
                  AND "BoundaryNodeId" = ANY ({ids})
                ORDER BY "HostTokenId", "BoundaryNodeId", "Id"
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async Task<IReadOnlyList<ConditionalBoundarySubscriptionRecord>> ListActiveByInstanceAsync(
        long instanceId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        List<ConditionalBoundarySubscriptionEntity> entities;
        if (forUpdate)
        {
            entities = await dbContext.ConditionalBoundarySubscriptions
                .FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM flowbit.conditional_boundary_subscriptions AS subscription
                    WHERE subscription."InstanceId" = {instanceId}
                      AND (
                        subscription."Status" = {ConditionalBoundarySubscriptionStatuses.Active}
                        OR EXISTS (
                          SELECT 1
                          FROM flowbit.workflow_jobs AS job
                          WHERE job."ConditionalBoundarySubscriptionId" = subscription."Id"
                            AND job."Status" IN (
                              {WorkflowJobStatuses.Queued},
                              {WorkflowJobStatuses.Running},
                              {WorkflowJobStatuses.ResultReady},
                              {WorkflowJobStatuses.Retry},
                              {WorkflowJobStatuses.Incident})
                        )
                      )
                    ORDER BY subscription."Id"
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);
        }
        else
        {
            entities = await dbContext.ConditionalBoundarySubscriptions.AsNoTracking()
                .Where(subscription =>
                    subscription.InstanceId == instanceId
                    && (subscription.Status == ConditionalBoundarySubscriptionStatuses.Active
                        || subscription.Jobs.Any(job =>
                            job.Status == WorkflowJobStatuses.Queued
                            || job.Status == WorkflowJobStatuses.Running
                            || job.Status == WorkflowJobStatuses.ResultReady
                            || job.Status == WorkflowJobStatuses.Retry
                            || job.Status == WorkflowJobStatuses.Incident)))
                .OrderBy(subscription => subscription.Id)
                .ToListAsync(cancellationToken);
        }

        return entities.Select(Map).ToArray();
    }

    public async Task<bool> UpdateStateAsync(
        long subscriptionId,
        bool expectedConditionTrue,
        long expectedOccurrence,
        bool conditionTrue,
        long nextOccurrence,
        bool complete,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var affected = await dbContext.ConditionalBoundarySubscriptions
            .Where(subscription =>
                subscription.Id == subscriptionId
                && subscription.Status == ConditionalBoundarySubscriptionStatuses.Active
                && subscription.IsConditionTrue == expectedConditionTrue
                && subscription.Occurrence == expectedOccurrence)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(subscription => subscription.IsConditionTrue, conditionTrue)
                .SetProperty(subscription => subscription.Occurrence, nextOccurrence)
                .SetProperty(
                    subscription => subscription.Status,
                    complete
                        ? ConditionalBoundarySubscriptionStatuses.Completed
                        : ConditionalBoundarySubscriptionStatuses.Active)
                .SetProperty(
                    subscription => subscription.CompletedAt,
                    complete ? now : (DateTimeOffset?)null)
                .SetProperty(subscription => subscription.UpdatedAt, now),
                cancellationToken);
        return affected == 1;
    }

    public async Task<bool> StageStateUpdatesAsync(
        IReadOnlyList<ConditionalBoundarySubscriptionStateUpdateRecord> updates,
        CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return true;
        }
        if (updates.Select(update => update.SubscriptionId).Distinct().Count()
            != updates.Count)
        {
            throw new ArgumentException(
                "A conditional boundary subscription can be updated only once per batch.",
                nameof(updates));
        }

        var ids = updates.Select(update => update.SubscriptionId).Order().ToArray();
        var entities = dbContext.ConditionalBoundarySubscriptions.Local
            .Where(subscription => ids.Contains(subscription.Id))
            .ToDictionary(subscription => subscription.Id);
        var missingIds = ids.Where(id => !entities.ContainsKey(id)).ToArray();
        if (missingIds.Length > 0)
        {
            var loaded = await dbContext.ConditionalBoundarySubscriptions
                .FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM flowbit.conditional_boundary_subscriptions
                    WHERE "Id" = ANY ({missingIds})
                    ORDER BY "Id"
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);
            foreach (var entity in loaded)
            {
                entities[entity.Id] = entity;
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var update in updates.OrderBy(update => update.SubscriptionId))
        {
            if (!entities.TryGetValue(update.SubscriptionId, out var entity)
                || entity.Status != ConditionalBoundarySubscriptionStatuses.Active
                || entity.IsConditionTrue != update.ExpectedConditionTrue
                || entity.Occurrence != update.ExpectedOccurrence
                || update.NextOccurrence < 0)
            {
                return false;
            }
            entity.IsConditionTrue = update.ConditionTrue;
            entity.Occurrence = update.NextOccurrence;
            entity.Status = update.Complete
                ? ConditionalBoundarySubscriptionStatuses.Completed
                : ConditionalBoundarySubscriptionStatuses.Active;
            entity.CompletedAt = update.Complete ? now : null;
            entity.UpdatedAt = now;
        }
        return true;
    }

    public Task<int> CancelByInstanceAsync(
        long instanceId,
        CancellationToken cancellationToken) =>
        CancelAsync(
            dbContext.ConditionalBoundarySubscriptions.Where(subscription =>
                subscription.InstanceId == instanceId),
            cancellationToken);

    public Task<int> CancelByTokenIdsAsync(
        long instanceId,
        IReadOnlyCollection<long> hostTokenIds,
        CancellationToken cancellationToken)
    {
        if (hostTokenIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        return CancelAsync(
            dbContext.ConditionalBoundarySubscriptions.Where(subscription =>
                subscription.InstanceId == instanceId
                && hostTokenIds.Contains(subscription.HostTokenId)),
            cancellationToken);
    }

    public Task<int> CancelOtherForTokenAsync(
        long instanceId,
        long hostTokenId,
        long exceptSubscriptionId,
        CancellationToken cancellationToken) =>
        CancelAsync(
            dbContext.ConditionalBoundarySubscriptions.Where(subscription =>
                subscription.InstanceId == instanceId
                && subscription.HostTokenId == hostTokenId
                && subscription.Id != exceptSubscriptionId),
            cancellationToken);

    public async Task<int> RebindDefinitionAsync(
        long instanceId,
        long sourceWorkflowDefinitionId,
        long targetWorkflowDefinitionId,
        IReadOnlyDictionary<int, string> boundaryNodeNames,
        CancellationToken cancellationToken)
    {
        var affected = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in boundaryNodeNames.OrderBy(pair => pair.Key))
        {
            affected += await dbContext.ConditionalBoundarySubscriptions
                .Where(subscription =>
                    subscription.InstanceId == instanceId
                    && subscription.WorkflowDefinitionId == sourceWorkflowDefinitionId
                    && subscription.BoundaryNodeId == pair.Key
                    && subscription.Status == ConditionalBoundarySubscriptionStatuses.Active)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        subscription => subscription.WorkflowDefinitionId,
                        targetWorkflowDefinitionId)
                    .SetProperty(subscription => subscription.BoundaryNodeName, pair.Value)
                    .SetProperty(subscription => subscription.UpdatedAt, now),
                    cancellationToken);
        }
        return affected;
    }

    private static async Task<int> CancelAsync(
        IQueryable<ConditionalBoundarySubscriptionEntity> source,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await source
            .Where(subscription =>
                subscription.Status == ConditionalBoundarySubscriptionStatuses.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(
                    subscription => subscription.Status,
                    ConditionalBoundarySubscriptionStatuses.Cancelled)
                .SetProperty(subscription => subscription.CompletedAt, now)
                .SetProperty(subscription => subscription.UpdatedAt, now),
                cancellationToken);
    }

    private static ConditionalBoundarySubscriptionRecord Map(
        ConditionalBoundarySubscriptionEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.WorkflowDefinitionId,
            entity.WorkflowKey,
            entity.HostTokenId,
            entity.HostActivationId,
            entity.BoundaryNodeId,
            entity.BoundaryNodeName,
            entity.AttachedToNodeId,
            entity.OutgoingFlowId,
            entity.Condition,
            entity.DeliveryMode,
            entity.CancelActivity,
            entity.IsConditionTrue,
            entity.Occurrence,
            entity.Status,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CompletedAt);
}
