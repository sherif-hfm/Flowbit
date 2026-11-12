using System.Linq.Expressions;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Flowbit.Infrastructure.Repositories;

public sealed class CallActivityRepository(AppDbContext dbContext) : ICallActivityRepository
{
    private static readonly JsonSerializerOptions ContractOptions = new(JsonSerializerDefaults.Web);

    // An invocation without its captured contract and values, for reads that only describe
    // it. A snapshot can reach a mebibyte, so a page of them is not free to load and discard.
    private static readonly Expression<Func<CallActivityExecutionEntity, CallActivityExecutionEntity>> WithoutSnapshots =
        call => new CallActivityExecutionEntity
        {
            Id = call.Id,
            ParentInstanceId = call.ParentInstanceId,
            ParentTokenId = call.ParentTokenId,
            ActivationId = call.ActivationId,
            ParentNodeId = call.ParentNodeId,
            MultiInstanceExecutionId = call.MultiInstanceExecutionId,
            AutomaticMultiInstanceItemId = call.AutomaticMultiInstanceItemId,
            AutomaticMultiInstanceItem = call.AutomaticMultiInstanceItem == null ? null : new MultiInstanceActivityItemEntity
            {
                Id = call.AutomaticMultiInstanceItem.Id,
                ItemIndex = call.AutomaticMultiInstanceItem.ItemIndex
            },
            ParentNodeExecutionId = call.ParentNodeExecutionId,
            RootInstanceId = call.RootInstanceId,
            ChildDepth = call.ChildDepth,
            ParentDefinitionId = call.ParentDefinitionId,
            TargetWorkflowKey = call.TargetWorkflowKey,
            ChildDefinitionId = call.ChildDefinitionId,
            ChildStartEventId = call.ChildStartEventId,
            ChildInstanceId = call.ChildInstanceId,
            State = call.State,
            CapacityReserved = call.CapacityReserved,
            FailureCode = call.FailureCode,
            FailureDescription = call.FailureDescription,
            ChildFaultCode = call.ChildFaultCode,
            CausedBy = call.CausedBy,
            CreatedAt = call.CreatedAt,
            UpdatedAt = call.UpdatedAt,
            AdmittedAt = call.AdmittedAt,
            StartedAt = call.StartedAt,
            SettledAt = call.SettledAt
        };

    public async Task<CallInvocationRecord> AddInvocationAsync(
        CallInvocationCreateRecord create,
        CancellationToken cancellationToken)
    {
        if ((create.MultiInstanceExecutionId is null) != (create.AutomaticMultiInstanceItemId is null))
        {
            throw new ArgumentException(
                "An automatic call item names both its body and its item; an ordinary call names neither.",
                nameof(create));
        }
        var now = DateTimeOffset.UtcNow;
        var entity = new CallActivityExecutionEntity
        {
            MultiInstanceExecutionId = create.MultiInstanceExecutionId,
            AutomaticMultiInstanceItemId = create.AutomaticMultiInstanceItemId,
            ParentInstanceId = create.ParentInstanceId,
            ParentTokenId = create.ParentTokenId,
            ActivationId = create.ActivationId,
            ParentNodeId = create.ParentNodeId,
            ParentNodeExecutionId = create.ParentNodeExecutionId,
            RootInstanceId = create.RootInstanceId,
            ChildDepth = create.ChildDepth,
            ParentDefinitionId = create.ParentDefinitionId,
            TargetWorkflowKey = create.TargetWorkflowKey,
            State = CallInvocationStates.Queued,
            CapacityReserved = create.CapacityReserved,
            ContractJson = JsonSerializer.SerializeToDocument(create.Contract, ContractOptions),
            InputValuesJson = create.Inputs is null ? null : ToDocument(create.Inputs),
            FailureCode = create.FailureCode,
            FailureDescription = Bound(create.FailureDescription),
            CausedBy = create.CausedBy,
            CreatedAt = now,
            UpdatedAt = now,
            AdmittedAt = create.CapacityReserved ? now : null
        };

        dbContext.CallActivityExecutions.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        var record = ToRecord(entity);

        // Later writes go straight to the database, so a tracked copy would go stale.
        dbContext.Entry(entity).State = EntityState.Detached;
        return record;
    }

    public async Task<CallInvocationRecord?> GetInvocationAsync(
        long invocationId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.CallActivityExecutions.AsNoTracking()
            .SingleOrDefaultAsync(call => call.Id == invocationId, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<CallInvocationRecord?> GetForActivationAsync(
        long parentTokenId,
        Guid activationId,
        CancellationToken cancellationToken)
    {
        // An ordinary call owns one invocation per activation. Automatic items share
        // their body's activation, so they are looked up by item, never here.
        var entity = await dbContext.CallActivityExecutions.AsNoTracking()
            .SingleOrDefaultAsync(
                call => call.ParentTokenId == parentTokenId && call.ActivationId == activationId
                        && call.AutomaticMultiInstanceItemId == null,
                cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<CallInvocationRecord?> GetByChildAsync(
        long childInstanceId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.CallActivityExecutions.AsNoTracking()
            .SingleOrDefaultAsync(call => call.ChildInstanceId == childInstanceId, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<CallInvocationRecord?> GetSummaryByChildAsync(
        long childInstanceId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.CallActivityExecutions.AsNoTracking()
            .Where(call => call.ChildInstanceId == childInstanceId)
            .Select(WithoutSnapshots)
            .SingleOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<CallInvocationRecord>> ListUnsettledForTokensAsync(
        long parentInstanceId,
        IReadOnlyCollection<long> tokenIds,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0)
        {
            return [];
        }

        var tracked = dbContext.ExecutionTokens.Local
            .Where(token => tokenIds.Contains(token.Id))
            .ToArray();
        if (tracked.Length == tokenIds.Count
            && tracked.All(token => !BpmnFlowNodeTypes.IsCallActivity(token.NodeType)))
        {
            return [];
        }

        var entities = await dbContext.CallActivityExecutions.AsNoTracking()
            .Where(call =>
                call.ParentInstanceId == parentInstanceId
                && call.AutomaticMultiInstanceItemId == null
                && tokenIds.Contains(call.ParentTokenId)
                && (call.State == CallInvocationStates.Queued || call.State == CallInvocationStates.Running))
            .OrderBy(call => call.Id)
            .Select(WithoutSnapshots)
            .ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<CallInvocationRecord>> ListUnsettledAutomaticForTokensAsync(
        long parentInstanceId, IReadOnlyCollection<long> tokenIds, CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0) return [];
        var tracked = dbContext.ExecutionTokens.Local.Where(token => tokenIds.Contains(token.Id)).ToArray();
        if (tracked.Length == tokenIds.Count && tracked.All(token => !BpmnFlowNodeTypes.IsCallActivity(token.NodeType)))
            return [];
        var entities = await dbContext.CallActivityExecutions.AsNoTracking()
            .Where(call => call.ParentInstanceId == parentInstanceId
                && call.AutomaticMultiInstanceItemId != null && tokenIds.Contains(call.ParentTokenId)
                && (call.State == CallInvocationStates.Queued || call.State == CallInvocationStates.Running))
            .OrderBy(call => call.Id).Select(WithoutSnapshots).ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToArray();
    }

    public Task<bool> HasUnsettledOutgoingAsync(
        long parentInstanceId,
        CancellationToken cancellationToken) =>
        dbContext.CallActivityExecutions.AsNoTracking()
            .AnyAsync(
                call => call.ParentInstanceId == parentInstanceId
                    && (call.State == CallInvocationStates.Queued || call.State == CallInvocationStates.Running),
                cancellationToken);

    public Task<bool> HasUnsettledUnderRootAsync(
        long rootInstanceId,
        CancellationToken cancellationToken) =>
        dbContext.CallActivityExecutions.AsNoTracking()
            .AnyAsync(
                call => call.RootInstanceId == rootInstanceId
                    && (call.State == CallInvocationStates.Queued || call.State == CallInvocationStates.Running),
                cancellationToken);

    public Task<bool> HasRunningDescendantsAsync(
        long rootInstanceId,
        CancellationToken cancellationToken) =>
        dbContext.WorkflowInstances.AsNoTracking()
            .AnyAsync(
                instance => instance.CallRootInstanceId == rootInstanceId
                    && instance.Status == WorkflowInstanceStatuses.Running,
                cancellationToken);

    public Task<int> CountReservedUnsettledAsync(
        long rootInstanceId,
        CancellationToken cancellationToken) =>
        dbContext.CallActivityExecutions.AsNoTracking()
            .CountAsync(
                call => call.RootInstanceId == rootInstanceId
                    && call.CapacityReserved
                    && (call.State == CallInvocationStates.Queued || call.State == CallInvocationStates.Running),
                cancellationToken);

    public async Task<bool> SetInputsAsync(
        long invocationId,
        CallValueSnapshot inputs,
        CancellationToken cancellationToken)
    {
        var document = ToDocument(inputs);
        var now = DateTimeOffset.UtcNow;
        return await dbContext.CallActivityExecutions
            .Where(call => call.Id == invocationId && call.State == CallInvocationStates.Queued)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(call => call.InputValuesJson, document)
                    .SetProperty(call => call.UpdatedAt, now),
                cancellationToken) == 1;
    }

    public async Task<bool> ReserveAsync(
        long invocationId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await dbContext.CallActivityExecutions
            .Where(call =>
                call.Id == invocationId
                && call.State == CallInvocationStates.Queued
                && !call.CapacityReserved)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(call => call.CapacityReserved, true)
                    .SetProperty(call => call.AdmittedAt, now)
                    .SetProperty(call => call.UpdatedAt, now),
                cancellationToken) == 1;
    }

    public async Task<bool> RecordBlockedAsync(
        long invocationId,
        string failureCode,
        string failureDescription,
        CancellationToken cancellationToken)
    {
        var description = Bound(failureDescription);
        var now = DateTimeOffset.UtcNow;
        return await dbContext.CallActivityExecutions
            .Where(call =>
                call.Id == invocationId
                && (call.State == CallInvocationStates.Queued || call.State == CallInvocationStates.Running))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(call => call.FailureCode, failureCode)
                    .SetProperty(call => call.FailureDescription, description)
                    .SetProperty(call => call.UpdatedAt, now),
                cancellationToken) == 1;
    }

    public async Task<bool> LinkChildAsync(
        long invocationId,
        long childInstanceId,
        long childDefinitionId,
        int childStartEventId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await dbContext.CallActivityExecutions
            .Where(call =>
                call.Id == invocationId
                && call.State == CallInvocationStates.Queued
                && call.CapacityReserved
                && call.ChildInstanceId == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(call => call.ChildInstanceId, childInstanceId)
                    .SetProperty(call => call.ChildDefinitionId, childDefinitionId)
                    .SetProperty(call => call.ChildStartEventId, childStartEventId)
                    .SetProperty(call => call.State, CallInvocationStates.Running)
                    .SetProperty(call => call.FailureCode, (string?)null)
                    .SetProperty(call => call.FailureDescription, (string?)null)
                    .SetProperty(call => call.StartedAt, now)
                    .SetProperty(call => call.UpdatedAt, now),
                cancellationToken) == 1;
    }

    public async Task<bool> SettleAsync(
        long invocationId,
        string state,
        string? failureCode,
        string? failureDescription,
        string? childFaultCode,
        CancellationToken cancellationToken)
    {
        var description = Bound(failureDescription);
        var now = DateTimeOffset.UtcNow;
        return await dbContext.CallActivityExecutions
            .Where(call =>
                call.Id == invocationId
                && (call.State == CallInvocationStates.Queued || call.State == CallInvocationStates.Running))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(call => call.State, state)
                    .SetProperty(call => call.CapacityReserved, false)
                    .SetProperty(call => call.FailureCode, failureCode)
                    .SetProperty(call => call.FailureDescription, description)
                    .SetProperty(call => call.ChildFaultCode, childFaultCode)
                    .SetProperty(call => call.SettledAt, now)
                    .SetProperty(call => call.UpdatedAt, now),
                cancellationToken) == 1;
    }

    public async Task AddResultAsync(
        CallResultRecord result,
        CancellationToken cancellationToken)
    {
        var entity = new CallActivityResultEntity
        {
            ChildInstanceId = result.ChildInstanceId,
            InvocationId = result.InvocationId,
            Outcome = result.Outcome,
            OutputValuesJson = result.Outputs is null ? null : ToDocument(result.Outputs),
            FailureCode = result.FailureCode,
            FailureDescription = Bound(result.FailureDescription),
            ChildFaultCode = result.ChildFaultCode,
            CapturedAt = result.CapturedAt
        };
        dbContext.CallActivityResults.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.Entry(entity).State = EntityState.Detached;
    }

    public async Task<CallResultRecord?> GetResultAsync(
        long childInstanceId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.CallActivityResults.AsNoTracking()
            .SingleOrDefaultAsync(result => result.ChildInstanceId == childInstanceId, cancellationToken);
        return entity is null
            ? null
            : new CallResultRecord(
                entity.ChildInstanceId,
                entity.InvocationId,
                entity.Outcome,
                FromDocument(entity.OutputValuesJson),
                entity.FailureCode,
                entity.FailureDescription,
                entity.ChildFaultCode,
                entity.CapturedAt);
    }

    public async Task<PagedResult<CallInvocationListRow>> ListOutgoingAsync(
        long parentInstanceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.CallActivityExecutions.AsNoTracking()
            .Where(call => call.ParentInstanceId == parentInstanceId);
        var totalCount = await query.CountAsync(cancellationToken);
        var entities = await query
            .OrderByDescending(call => call.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(WithoutSnapshots)
            .ToListAsync(cancellationToken);
        if (entities.Count == 0)
        {
            return new PagedResult<CallInvocationListRow>([], page, pageSize, totalCount);
        }

        var invocationIds = entities.Select(call => call.Id).ToArray();
        var childIds = entities
            .Where(call => call.ChildInstanceId is not null)
            .Select(call => call.ChildInstanceId!.Value)
            .ToArray();
        var definitionIds = entities
            .Where(call => call.ChildDefinitionId is not null)
            .Select(call => call.ChildDefinitionId!.Value)
            .Distinct()
            .ToArray();
        var children = await dbContext.WorkflowInstances.AsNoTracking()
            .Where(instance => childIds.Contains(instance.Id))
            .Select(instance => new { instance.Id, instance.Status })
            .ToDictionaryAsync(instance => instance.Id, instance => instance.Status, cancellationToken);
        var versions = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(definition => definitionIds.Contains(definition.Id))
            .Select(definition => new { definition.Id, definition.Version })
            .ToDictionaryAsync(definition => definition.Id, definition => definition.Version, cancellationToken);
        var results = await dbContext.CallActivityResults.AsNoTracking()
            .Where(result => invocationIds.Contains(result.InvocationId))
            .Select(result => new { result.InvocationId, result.Outcome })
            .ToDictionaryAsync(result => result.InvocationId, result => result.Outcome, cancellationToken);

        // Incidents on the invocation's own jobs, because callResume belongs to the child
        // and callCancel to the parent, plus those on the child's other durable work,
        // which includes the jobs of the child's own calls.
        var ownJobIncidents = await dbContext.WorkflowIncidents.AsNoTracking()
            .Where(incident =>
                incident.Status == WorkflowIncidentStatuses.Open
                && incident.Job != null
                && incident.Job.CallInvocationId != null
                && invocationIds.Contains(incident.Job.CallInvocationId.Value))
            .GroupBy(incident => incident.Job!.CallInvocationId!.Value)
            .Select(group => new { InvocationId = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(group => group.InvocationId, group => group.Count, cancellationToken);
        var childIncidents = await dbContext.WorkflowIncidents.AsNoTracking()
            .Where(incident =>
                incident.Status == WorkflowIncidentStatuses.Open
                && incident.InstanceId != null
                && childIds.Contains(incident.InstanceId.Value)
                && (incident.Job == null
                    || incident.Job.CallInvocationId == null
                    || !invocationIds.Contains(incident.Job.CallInvocationId.Value)))
            .GroupBy(incident => incident.InstanceId!.Value)
            .Select(group => new { InstanceId = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(group => group.InstanceId, group => group.Count, cancellationToken);

        var rows = entities.Select(entity => new CallInvocationListRow(
                ToRecord(entity),
                entity.ChildInstanceId is long childId ? children.GetValueOrDefault(childId) : null,
                entity.ChildDefinitionId is long definitionId && versions.TryGetValue(definitionId, out var version)
                    ? version
                    : null,
                results.ContainsKey(entity.Id),
                results.GetValueOrDefault(entity.Id),
                ownJobIncidents.GetValueOrDefault(entity.Id)
                    + (entity.ChildInstanceId is long incidentChildId
                        ? childIncidents.GetValueOrDefault(incidentChildId)
                        : 0)))
            .ToList();
        return new PagedResult<CallInvocationListRow>(rows, page, pageSize, totalCount);
    }

    public async Task<CallCountsRecord> GetOutgoingCountsAsync(
        long parentInstanceId,
        CancellationToken cancellationToken)
    {
        var counts = await dbContext.CallActivityExecutions.AsNoTracking()
            .Where(call => call.ParentInstanceId == parentInstanceId)
            .GroupBy(call => 1)
            .Select(group => new
            {
                Total = group.LongCount(),
                Unsettled = group.LongCount(call =>
                    call.State == CallInvocationStates.Queued || call.State == CallInvocationStates.Running)
            })
            .SingleOrDefaultAsync(cancellationToken);
        return counts is null
            ? new CallCountsRecord(0, 0)
            : new CallCountsRecord(counts.Total, counts.Unsettled);
    }

    public async Task<IReadOnlyDictionary<long, CallInvocationRecord>> GetUnsettledByTokensAsync(
        long parentInstanceId,
        IReadOnlyCollection<long> tokenIds,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0)
        {
            return new Dictionary<long, CallInvocationRecord>();
        }

        var entities = await dbContext.CallActivityExecutions.AsNoTracking()
            .Where(call =>
                call.ParentInstanceId == parentInstanceId
                && tokenIds.Contains(call.ParentTokenId)
                && (call.State == CallInvocationStates.Queued || call.State == CallInvocationStates.Running))
            .Select(WithoutSnapshots)
            .ToListAsync(cancellationToken);
        return entities
            .GroupBy(call => call.ParentTokenId)
            .ToDictionary(
                group => group.Key,
                group => ToRecord(group.OrderByDescending(call => call.Id).First()));
    }

    public async Task<IReadOnlySet<long>> ListChildrenWithResultAsync(
        IReadOnlyCollection<long> childInstanceIds,
        CancellationToken cancellationToken)
    {
        if (childInstanceIds.Count == 0)
        {
            return new HashSet<long>();
        }

        var ids = await dbContext.CallActivityResults.AsNoTracking()
            .Where(result => childInstanceIds.Contains(result.ChildInstanceId))
            .Select(result => result.ChildInstanceId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public async Task<PagedResult<WorkflowCallerRecord>> ListPublishedCallersAsync(
        string targetWorkflowKey,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query =
            from target in dbContext.WorkflowDefinitionCallTargets.AsNoTracking()
            join definition in dbContext.WorkflowDefinitions.AsNoTracking()
                on target.WorkflowDefinitionId equals definition.Id
            where target.TargetWorkflowKey == targetWorkflowKey && definition.IsPublished
            select new { target.NodeId, definition.Id, definition.WorkflowKey, definition.Name, definition.Version, definition.IsDefault };
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(row => row.WorkflowKey)
            .ThenByDescending(row => row.Version)
            .ThenBy(row => row.NodeId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<WorkflowCallerRecord>(
            rows.Select(row => new WorkflowCallerRecord(
                    row.Id,
                    row.WorkflowKey,
                    row.Name,
                    row.Version,
                    row.IsDefault,
                    row.NodeId))
                .ToList(),
            page,
            pageSize,
            totalCount);
    }

    private static CallInvocationRecord ToRecord(CallActivityExecutionEntity entity) =>
        new(
            entity.Id,
            entity.ParentInstanceId,
            entity.ParentTokenId,
            entity.ActivationId,
            entity.ParentNodeId,
            entity.ParentNodeExecutionId,
            entity.RootInstanceId,
            entity.ChildDepth,
            entity.ParentDefinitionId,
            entity.TargetWorkflowKey,
            entity.ChildDefinitionId,
            entity.ChildStartEventId,
            entity.ChildInstanceId,
            entity.State,
            entity.CapacityReserved,
            entity.ContractJson?.Deserialize<CallActivityModel>(ContractOptions),
            FromDocument(entity.InputValuesJson),
            entity.FailureCode,
            entity.FailureDescription,
            entity.ChildFaultCode,
            entity.CausedBy,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.AdmittedAt,
            entity.StartedAt,
            entity.SettledAt,
            entity.MultiInstanceExecutionId,
            entity.AutomaticMultiInstanceItemId,
            entity.AutomaticMultiInstanceItem?.ItemIndex);

    private static JsonDocument ToDocument(CallValueSnapshot snapshot) =>
        JsonSerializer.SerializeToDocument(new StoredSnapshot(
            snapshot.Values.ToDictionary(pair => pair.Key, pair => pair.Value),
            snapshot.Missing.ToArray()));

    private static CallValueSnapshot? FromDocument(JsonDocument? document)
    {
        var stored = document?.Deserialize<StoredSnapshot>();
        return stored is null
            ? null
            : new CallValueSnapshot(
                stored.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase),
                stored.Missing);
    }

    private static string? Bound(string? description) =>
        description is null
            ? null
            : UnicodeText.Limit(description, CallActivityConstraints.MaxFailureDescriptionLength);

    private sealed record StoredSnapshot(
        Dictionary<string, JsonElement> Values,
        string[] Missing);
}
