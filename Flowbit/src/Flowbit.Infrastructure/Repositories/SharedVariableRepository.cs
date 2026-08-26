using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
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

public sealed class SharedVariableRepository(AppDbContext dbContext) : ISharedVariableRepository
{
    private const string ProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const int DefaultWakeMaxAttempts = 25;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(IReadOnlyList<SharedVariableRecord> Items, long TotalCount)> ListAsync(
        string? search,
        string? status,
        int offset,
        int limit,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        var query = dbContext.SharedVariables.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(variable => variable.Status == SharedVariableStatuses.Active);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalizedStatus = status.Trim();
            query = query.Where(variable => variable.Status == normalizedStatus);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            query = query.Where(variable => EF.Functions.ILike(variable.Key, pattern)
                || (variable.Description != null && EF.Functions.ILike(variable.Description, pattern)));
        }

        var total = await query.LongCountAsync(cancellationToken);
        var entities = await query
            .Include(variable => variable.CurrentValue)
            .OrderBy(variable => variable.Key)
            .ThenBy(variable => variable.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return (entities.Select(MapVariable).ToArray(), total);
    }

    public async Task<SharedVariableRecord?> GetByKeyAsync(
        string key,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        var query = dbContext.SharedVariables.AsNoTracking()
            .Include(variable => variable.CurrentValue)
            .Where(variable => variable.Key == key.Trim());
        if (!includeArchived)
        {
            query = query.Where(variable => variable.Status == SharedVariableStatuses.Active);
        }
        var entity = await query.SingleOrDefaultAsync(cancellationToken);
        return entity is null ? null : MapVariable(entity);
    }

    public async Task<IReadOnlyDictionary<string, SharedVariableRecord>> GetManyByKeyAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeKeys(keys);
        if (normalized.Length == 0)
        {
            return new Dictionary<string, SharedVariableRecord>(StringComparer.OrdinalIgnoreCase);
        }

        var query = dbContext.SharedVariables.AsNoTracking()
            .Include(variable => variable.CurrentValue)
            .Where(variable => normalized.Contains(variable.Key));
        if (!includeArchived)
        {
            query = query.Where(variable => variable.Status == SharedVariableStatuses.Active);
        }
        var variables = await query.ToListAsync(cancellationToken);
        return variables.ToDictionary(
            variable => variable.Key,
            MapVariable,
            StringComparer.OrdinalIgnoreCase);
    }

    public Task<IReadOnlyDictionary<string, SharedVariableCurrentValueRecord>> LoadCurrentAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        CancellationToken cancellationToken) =>
        LoadCurrentCoreAsync(keys, includeArchived, forUpdate: false, cancellationToken);

    public Task<IReadOnlyDictionary<string, SharedVariableCurrentValueRecord>> LockCurrentAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        CancellationToken cancellationToken) =>
        LoadCurrentCoreAsync(keys, includeArchived, forUpdate: true, cancellationToken);

    public Task<IReadOnlyDictionary<string, SharedVariableValueStamp>> LoadValueStampsAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        CancellationToken cancellationToken) =>
        LoadValueStampsCoreAsync(keys, includeArchived, forUpdate: false, cancellationToken);

    public Task<IReadOnlyDictionary<string, SharedVariableValueStamp>> LockValueStampsAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        CancellationToken cancellationToken) =>
        LoadValueStampsCoreAsync(keys, includeArchived, forUpdate: true, cancellationToken);

    public async Task PrelockDefinitionKeysForLegacyAllocatorAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!IsNpgsql()) return;
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Legacy shared-variable allocator prelocking requires an ambient database transaction.");
        }

        var normalized = NormalizeKeys(keys);
        if (normalized.Length == 0) return;

        var allocatorMode = await dbContext.SharedVariableRevisionStates.AsNoTracking()
            .Where(state => state.Id == 1)
            .Select(state => state.AllocatorMode)
            .SingleAsync(cancellationToken);
        if (!string.Equals(allocatorMode, "legacy", StringComparison.Ordinal)) return;

        // Legacy writers take a catalog row before updating the singleton
        // revision allocator. Locking the definition's complete key set in
        // exact ordinal order at transaction entry gives new multi-key writers
        // the same prefix and prevents key/singleton lock inversions.
        foreach (var key in normalized)
        {
            _ = await dbContext.SharedVariables.FromSqlInterpolated(
                    $"""SELECT * FROM flowbit.shared_variables WHERE "Key" = {key} FOR UPDATE""")
                .SingleOrDefaultAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyDictionary<string, SharedVariableValueStamp>> LoadValueStampsCoreAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeKeys(keys);
        if (normalized.Length == 0)
        {
            return new Dictionary<string, SharedVariableValueStamp>(StringComparer.OrdinalIgnoreCase);
        }

        if (forUpdate)
        {
            _ = await LoadCurrentCoreAsync(
                normalized,
                includeArchived,
                forUpdate: true,
                cancellationToken);
        }

        var query = dbContext.SharedVariables.AsNoTracking()
            .Where(variable => normalized.Contains(variable.Key));
        if (!includeArchived)
        {
            query = query.Where(variable => variable.Status == SharedVariableStatuses.Active);
        }

        var rows = await query
            .Select(variable => new
            {
                variable.Key,
                variable.Status,
                variable.ValueRevision,
                HasValue = variable.CurrentValue != null
                           && !variable.CurrentValue.IsDeleted
                           && variable.CurrentValue.ValueJson != null
            })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(
            row => row.Key,
            row => new SharedVariableValueStamp(
                row.Key,
                row.HasValue,
                row.ValueRevision,
                row.Status),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, SharedVariableCurrentValueRecord>> LoadCurrentCoreAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeKeys(keys);
        if (normalized.Length == 0)
        {
            return new Dictionary<string, SharedVariableCurrentValueRecord>(
                StringComparer.OrdinalIgnoreCase);
        }

        List<SharedVariableEntity> variables;
        if (forUpdate && IsNpgsql())
        {
            variables = [];
            foreach (var key in normalized)
            {
                var entity = await dbContext.SharedVariables
                    .FromSqlInterpolated(
                        $"""SELECT * FROM flowbit.shared_variables WHERE "Key" = {key} FOR UPDATE""")
                    .SingleOrDefaultAsync(cancellationToken);
                if (entity is not null && (includeArchived || entity.Status == SharedVariableStatuses.Active))
                {
                    variables.Add(entity);
                }
            }

            var ids = variables.Select(variable => variable.Id).ToArray();
            if (ids.Length > 0)
            {
                await dbContext.SharedVariableCurrentValues
                    .Where(value => ids.Contains(value.SharedVariableId))
                    .LoadAsync(cancellationToken);
            }
        }
        else
        {
            var query = dbContext.SharedVariables
                .AsNoTracking()
                .Include(variable => variable.CurrentValue)
                .Where(variable => normalized.Contains(variable.Key));
            if (!includeArchived)
            {
                query = query.Where(variable => variable.Status == SharedVariableStatuses.Active);
            }
            variables = await query.ToListAsync(cancellationToken);
        }

        var result = new Dictionary<string, SharedVariableCurrentValueRecord>(
            variables.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (var variable in variables)
        {
            if (variable.CurrentValue is not { IsDeleted: false, ValueJson: not null } current)
            {
                continue;
            }
            result[variable.Key] = new SharedVariableCurrentValueRecord(
                variable.Id,
                variable.Key,
                variable.DataType,
                variable.IsArray,
                variable.Nullable,
                current.ValueJson.RootElement.Clone(),
                current.Revision,
                current.SetAt);
        }
        return result;
    }

    public async Task<SharedVariableMutationResult> CreateAsync(
        SharedVariableCreateCommand command,
        CancellationToken cancellationToken)
    {
        var key = command.Key.Trim();
        SharedVariableValueValidator.ValidateContract(
            key,
            command.DataType,
            command.IsArray,
            command.Nullable,
            command.Validation);
        if (command.HasValue)
        {
            if (command.Value is not JsonElement value)
            {
                throw new WorkflowDomainException($"Shared variable '{key}' initial value is required.");
            }
            SharedVariableValueValidator.Validate(
                key,
                command.DataType,
                command.IsArray,
                command.Nullable,
                command.Validation,
                value);
        }

        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        await LockIdempotencyRequestAsync(command.Caller, command.RequestId, cancellationToken);
        var replay = await TryReplayAsync(
            command.Caller,
            command.RequestId,
            command.RequestFingerprint,
            SharedVariableOperations.Create,
            key,
            cancellationToken);
        if (replay is not null)
        {
            if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
            return replay;
        }

        if (await dbContext.SharedVariables.AnyAsync(variable => variable.Key == key, cancellationToken))
        {
            throw new WorkflowConflictException(
                $"Shared variable key '{key}' already exists; keys are case-insensitive.");
        }

        var now = UtcNow();
        var variable = new SharedVariableEntity
        {
            Key = key,
            DataType = command.DataType,
            IsArray = command.IsArray,
            Nullable = command.Nullable,
            Validation = TrimToNull(command.Validation),
            Description = TrimToNull(command.Description),
            Status = SharedVariableStatuses.Active,
            CreatedByKind = command.Caller.Kind,
            CreatedById = command.Caller.Id,
            UpdatedByKind = command.Caller.Kind,
            UpdatedById = command.Caller.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.SharedVariables.Add(variable);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new WorkflowConflictException(
                $"Shared variable key '{key}' already exists; keys are case-insensitive.");
        }

        var revisionNumber = await AllocateRevisionAsync(cancellationToken);
        var revision = new SharedVariableRevisionEntity
        {
            SharedVariableId = variable.Id,
            Revision = revisionNumber,
            Operation = SharedVariableOperations.Create,
            ValueChanged = command.HasValue,
            HasValue = command.HasValue,
            ValueJson = command.HasValue ? ToDocument(command.Value!.Value) : null,
            CallerKind = command.Caller.Kind,
            CallerId = command.Caller.Id,
            Source = command.Source,
            RequestId = TrimToNull(command.RequestId),
            Reason = TrimToNull(command.Reason),
            CreatedAt = now
        };
        dbContext.SharedVariableRevisions.Add(revision);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (command.HasValue)
        {
            variable.CurrentValue = new SharedVariableCurrentValueEntity
            {
                SharedVariableId = variable.Id,
                SourceRevisionId = revision.Id,
                Revision = revisionNumber,
                ValueJson = ToDocument(command.Value!.Value),
                IsDeleted = false,
                SetAt = now
            };
            dbContext.SharedVariableCurrentValues.Add(variable.CurrentValue);
            variable.ValueRevision = revisionNumber;
        }
        variable.CurrentRevision = revisionNumber;
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = new SharedVariableMutationResult(
            MapVariable(variable),
            MapRevision(revision),
            false);
        await RecordRequestAsync(
            command.Caller,
            command.RequestId,
            command.RequestFingerprint,
            SharedVariableOperations.Create,
            key,
            variable.Id,
            revisionNumber,
            result,
            now,
            cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<SharedVariableMutationResult?> WriteAsync(
        SharedVariableWriteCommand command,
        CancellationToken cancellationToken)
    {
        var key = command.Key.Trim();
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        await LockIdempotencyRequestAsync(command.Caller, command.RequestId, cancellationToken);
        var operation = command.DescriptionOnly
            ? SharedVariableOperations.UpdateDescription
            : command.DeleteValue
                ? SharedVariableOperations.DeleteValue
                : SharedVariableOperations.Set;
        var replay = await TryReplayAsync(
            command.Caller,
            command.RequestId,
            command.RequestFingerprint,
            operation,
            key,
            cancellationToken);
        if (replay is not null)
        {
            if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
            return replay;
        }

        var variable = await LockVariableAsync(key, cancellationToken);
        if (variable is null || variable.Status != SharedVariableStatuses.Active)
        {
            return null;
        }
        await dbContext.Entry(variable).Reference(item => item.CurrentValue).LoadAsync(cancellationToken);
        EnsureExpectedRevision(variable, command.ExpectedRevision);
        EnsureExpectedValueRevision(variable, command.ExpectedValueRevision);

        if (!command.DeleteValue)
        {
            if (command.Value is not JsonElement value)
            {
                throw new WorkflowDomainException($"Shared variable '{variable.Key}' value is required.");
            }
            SharedVariableValueValidator.Validate(MapVariable(variable), value);
        }

        var current = variable.CurrentValue;
        var valueChanged = command.DeleteValue
            ? current is { IsDeleted: false }
            : current is null
              || current.IsDeleted
              || current.ValueJson is null
              || !JsonElement.DeepEquals(
                  current.ValueJson.RootElement,
                  command.Value!.Value);
        var now = UtcNow();
        var revisionNumber = await AllocateRevisionAsync(cancellationToken);
        var revision = new SharedVariableRevisionEntity
        {
            SharedVariableId = variable.Id,
            Revision = revisionNumber,
            Operation = operation,
            ValueChanged = valueChanged,
            HasValue = !command.DeleteValue,
            ValueJson = command.DeleteValue ? null : ToDocument(command.Value!.Value),
            CallerKind = command.Caller.Kind,
            CallerId = command.Caller.Id,
            Source = command.Source,
            RequestId = TrimToNull(command.RequestId),
            Reason = TrimToNull(command.Reason),
            WorkflowDefinitionId = command.WorkflowDefinitionId,
            InstanceId = command.InstanceId,
            NodeExecutionId = command.NodeExecutionId,
            SourceActionId = command.SourceActionId,
            CreatedAt = now
        };
        dbContext.SharedVariableRevisions.Add(revision);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (valueChanged && current is null)
        {
            current = new SharedVariableCurrentValueEntity { SharedVariableId = variable.Id };
            variable.CurrentValue = current;
            dbContext.SharedVariableCurrentValues.Add(current);
        }
        if (valueChanged)
        {
            current!.SourceRevisionId = revision.Id;
            current.Revision = revisionNumber;
            current.ValueJson = command.DeleteValue ? null : ToDocument(command.Value!.Value);
            current.IsDeleted = command.DeleteValue;
            current.SetAt = now;
            variable.ValueRevision = revisionNumber;
        }

        variable.CurrentRevision = revisionNumber;
        variable.UpdatedByKind = command.Caller.Kind;
        variable.UpdatedById = command.Caller.Id;
        variable.UpdatedAt = now;
        if (command.Description is not null)
        {
            variable.Description = TrimToNull(command.Description);
        }
        var wakeEnqueued = valueChanged && await dbContext.WorkflowDefinitionSharedVariableDependencies
            .AnyAsync(dependency => dependency.SharedVariableId == variable.Id, cancellationToken);
        if (wakeEnqueued)
        {
            var wakeNow = await DatabaseNowAsync(cancellationToken);
            EnqueueWake(variable, revision, wakeNow);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        if (wakeEnqueued) await NotifyWakeupAsync(cancellationToken);

        var result = new SharedVariableMutationResult(
            MapVariable(variable),
            MapRevision(revision),
            false);
        await RecordRequestAsync(
            command.Caller,
            command.RequestId,
            command.RequestFingerprint,
            operation,
            variable.Key,
            variable.Id,
            revisionNumber,
            result,
            now,
            cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<SharedVariableMutationResult?> ChangeLifecycleAsync(
        SharedVariableLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Operation is not (SharedVariableOperations.Archive or SharedVariableOperations.Reactivate))
        {
            throw new WorkflowDomainException(
                $"Unsupported shared-variable lifecycle operation '{command.Operation}'.");
        }

        var key = command.Key.Trim();
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        await LockIdempotencyRequestAsync(command.Caller, command.RequestId, cancellationToken);
        var replay = await TryReplayAsync(
            command.Caller,
            command.RequestId,
            command.RequestFingerprint,
            command.Operation,
            key,
            cancellationToken);
        if (replay is not null)
        {
            if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
            return replay;
        }

        var variable = await LockVariableAsync(key, cancellationToken);
        if (variable is null)
        {
            return null;
        }
        await dbContext.Entry(variable).Reference(item => item.CurrentValue).LoadAsync(cancellationToken);
        EnsureExpectedRevision(variable, command.ExpectedRevision);

        var archive = command.Operation == SharedVariableOperations.Archive;
        if (archive && variable.Status != SharedVariableStatuses.Archived)
        {
            var blockers = await GetBlockersAsync(variable.Id, cancellationToken);
            if (blockers.Reasons.Count > 0)
            {
                throw new WorkflowConflictException(
                    $"Shared variable '{variable.Key}' cannot be archived: {string.Join("; ", blockers.Reasons)}");
            }
        }

        var now = UtcNow();
        var revisionNumber = await AllocateRevisionAsync(cancellationToken);
        var revision = new SharedVariableRevisionEntity
        {
            SharedVariableId = variable.Id,
            Revision = revisionNumber,
            Operation = command.Operation,
            ValueChanged = false,
            HasValue = false,
            CallerKind = command.Caller.Kind,
            CallerId = command.Caller.Id,
            Source = command.Source,
            RequestId = TrimToNull(command.RequestId),
            Reason = TrimToNull(command.Reason),
            CreatedAt = now
        };
        dbContext.SharedVariableRevisions.Add(revision);
        variable.Status = archive ? SharedVariableStatuses.Archived : SharedVariableStatuses.Active;
        variable.ArchivedAt = archive ? now : null;
        variable.CurrentRevision = revisionNumber;
        variable.UpdatedByKind = command.Caller.Kind;
        variable.UpdatedById = command.Caller.Id;
        variable.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = new SharedVariableMutationResult(
            MapVariable(variable),
            MapRevision(revision),
            false);
        await RecordRequestAsync(
            command.Caller,
            command.RequestId,
            command.RequestFingerprint,
            command.Operation,
            variable.Key,
            variable.Id,
            revisionNumber,
            result,
            now,
            cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<SharedVariableRevisionRecord>> ListRevisionsAsync(
        string key,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.SharedVariableRevisions
            .AsNoTracking()
            .Where(revision => revision.SharedVariable.Key == key.Trim())
            .OrderByDescending(revision => revision.Revision)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return entities.Select(MapRevision).ToArray();
    }

    public async Task<SharedVariableLifecycleBlockersRecord?> GetLifecycleBlockersAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var variable = await dbContext.SharedVariables
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == key.Trim(), cancellationToken);
        return variable is null ? null : await GetBlockersAsync(variable.Id, cancellationToken);
    }

    private async Task<SharedVariableLifecycleBlockersRecord> GetBlockersAsync(
        long sharedVariableId,
        CancellationToken cancellationToken)
    {
        var publishedDefinitions = await dbContext.WorkflowDefinitionSharedVariableBindings
            .Where(binding => binding.SharedVariableId == sharedVariableId
                && binding.WorkflowDefinition.IsPublished)
            .Select(binding => binding.WorkflowDefinitionId)
            .Distinct()
            .LongCountAsync(cancellationToken);
        var runningInstances = await dbContext.WorkflowInstances
            .Where(instance => instance.Status == "running"
                && dbContext.WorkflowDefinitionSharedVariableBindings.Any(binding =>
                    binding.SharedVariableId == sharedVariableId
                    && binding.WorkflowDefinitionId == instance.WorkflowDefinitionId))
            .LongCountAsync(cancellationToken);
        var openJobs = await dbContext.WorkflowJobs
            .Where(job => job.Status != WorkflowJobStatuses.Completed
                && job.Status != WorkflowJobStatuses.Cancelled
                && job.Status != WorkflowJobStatuses.Skipped
                && dbContext.WorkflowDefinitionSharedVariableBindings.Any(binding =>
                    binding.SharedVariableId == sharedVariableId
                    && binding.WorkflowDefinitionId == job.WorkflowDefinitionId))
            .LongCountAsync(cancellationToken);
        var activeConditionalWaits = await (
            from token in dbContext.ExecutionTokens
            join instance in dbContext.WorkflowInstances
                on token.InstanceId equals instance.Id
            join dependency in dbContext.WorkflowDefinitionSharedVariableDependencies
                on new { instance.WorkflowDefinitionId, token.NodeId }
                equals new { dependency.WorkflowDefinitionId, dependency.NodeId }
            where dependency.SharedVariableId == sharedVariableId
                && dependency.Kind == SharedVariableDependencyKinds.ConditionalCatch
                && token.Status == ExecutionTokenStatuses.Active
                && instance.Status == "running"
            select token.Id)
            .Distinct()
            .LongCountAsync(cancellationToken);
        var pendingWakes = await dbContext.SharedVariableWakes
            .Where(wake => wake.SharedVariableId == sharedVariableId
                && wake.Status != SharedVariableWakeStatuses.Completed
                && wake.Status != SharedVariableWakeStatuses.Cancelled
                && wake.Status != SharedVariableWakeStatuses.Incident)
            .LongCountAsync(cancellationToken);
        var pendingDeliveries = await dbContext.SharedVariableWakeDeliveries
            .Where(delivery => delivery.Wake.SharedVariableId == sharedVariableId
                && delivery.Status != SharedVariableWakeStatuses.Completed
                && delivery.Status != SharedVariableWakeStatuses.Cancelled
                && delivery.Status != SharedVariableWakeStatuses.Incident)
            .LongCountAsync(cancellationToken);
        var openIncidents = await dbContext.SharedVariableWakeIncidents
            .Where(incident => incident.SharedVariableId == sharedVariableId
                && incident.Status == SharedVariableWakeIncidentStatuses.Open)
            .LongCountAsync(cancellationToken);
        var pendingOutboxWork = checked(pendingWakes + pendingDeliveries + openIncidents);

        var reasons = new List<string>();
        if (publishedDefinitions > 0)
            reasons.Add($"{publishedDefinitions} published workflow definition(s) bind the key");
        if (runningInstances > 0)
            reasons.Add($"{runningInstances} running workflow instance(s) bind the key");
        if (openJobs > 0)
            reasons.Add($"{openJobs} open workflow job(s) bind the key");
        if (activeConditionalWaits > 0)
            reasons.Add($"{activeConditionalWaits} active conditional wait(s) depend on the key");
        if (pendingWakes > 0)
            reasons.Add($"{pendingWakes} shared-variable wake expansion(s) are incomplete");
        if (pendingDeliveries > 0)
            reasons.Add($"{pendingDeliveries} shared-variable wake delivery/deliveries are incomplete");
        if (openIncidents > 0)
            reasons.Add($"{openIncidents} shared-variable wake incident(s) are open");
        return new SharedVariableLifecycleBlockersRecord(
            publishedDefinitions,
            runningInstances,
            openJobs,
            activeConditionalWaits,
            pendingOutboxWork,
            reasons);
    }

    public async Task ReplaceDefinitionProjectionAsync(
        long workflowDefinitionId,
        IReadOnlyCollection<SharedVariableDefinitionBindingProjection> bindings,
        IReadOnlyCollection<SharedVariableConditionalDependencyProjection> dependencies,
        CancellationToken cancellationToken)
    {
        var duplicateKey = bindings
            .GroupBy(binding => binding.SharedKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateKey is not null)
        {
            throw new WorkflowDomainException(
                $"Shared variable key '{duplicateKey.Key}' is bound more than once in the workflow definition.");
        }
        var duplicateAlias = bindings
            .Where(binding => !string.IsNullOrWhiteSpace(binding.Alias))
            .GroupBy(binding => binding.Alias!.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateAlias is not null)
        {
            throw new WorkflowDomainException(
                $"Shared variable alias '{duplicateAlias.Key}' is declared more than once in the workflow definition.");
        }
        foreach (var binding in bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.Alias) || binding.Alias.Trim().Length > 300)
            {
                throw new WorkflowDomainException(
                    $"Shared variable binding '{binding.SharedKey}' must have a local alias of at most 300 characters.");
            }
            if (binding.Access is not (SharedVariableAccessModes.Read or SharedVariableAccessModes.ReadWrite))
            {
                throw new WorkflowDomainException(
                    $"Shared variable binding '{binding.SharedKey}' has unsupported access '{binding.Access}'.");
            }
        }

        var keys = bindings.Select(binding => binding.SharedKey.Trim())
            .Concat(dependencies.Select(dependency => dependency.SharedKey.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var catalog = new Dictionary<string, SharedVariableEntity>(
            keys.Length,
            StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            // Serialize definition binding/publishing decisions with archive.
            // The catalog row is authoritative and is locked in the same stable
            // key order used by runtime transitions.
            var variable = await LockVariableAsync(key, cancellationToken);
            if (variable is null)
            {
                throw new WorkflowDomainException(
                    $"Shared variable key '{key}' does not exist in the deployment catalog.");
            }
            if (variable.Status != SharedVariableStatuses.Active)
            {
                throw new WorkflowDomainException(
                    $"Shared variable key '{variable.Key}' is archived.");
            }
            catalog[variable.Key] = variable;
        }
        foreach (var dependency in dependencies)
        {
            if (!bindings.Any(binding => string.Equals(
                    binding.SharedKey.Trim(),
                    dependency.SharedKey.Trim(),
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new WorkflowDomainException(
                    $"Conditional dependency '{dependency.SharedKey}' has no workflow shared-variable binding.");
            }
        }

        await dbContext.WorkflowDefinitionSharedVariableDependencies
            .Where(item => item.WorkflowDefinitionId == workflowDefinitionId)
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.WorkflowDefinitionSharedVariableBindings
            .Where(item => item.WorkflowDefinitionId == workflowDefinitionId)
            .ExecuteDeleteAsync(cancellationToken);

        var now = UtcNow();
        dbContext.WorkflowDefinitionSharedVariableBindings.AddRange(bindings.Select(binding =>
        {
            var variable = catalog[binding.SharedKey.Trim()];
            return new WorkflowDefinitionSharedVariableBindingEntity
            {
                WorkflowDefinitionId = workflowDefinitionId,
                SharedVariableId = variable.Id,
                Alias = binding.Alias!.Trim(),
                SharedKey = variable.Key,
                Access = binding.Access,
                CreatedAt = now
            };
        }));
        dbContext.WorkflowDefinitionSharedVariableDependencies.AddRange(dependencies.Select(dependency =>
        {
            var variable = catalog[dependency.SharedKey.Trim()];
            return new WorkflowDefinitionSharedVariableDependencyEntity
            {
                WorkflowDefinitionId = workflowDefinitionId,
                SharedVariableId = variable.Id,
                SharedKey = variable.Key,
                NodeId = dependency.NodeId,
                NodeExternalId = TrimToNull(dependency.NodeExternalId),
                Kind = dependency.Kind,
                CreatedAt = now
            };
        }));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SharedVariableWakeRecord>> LeaseWakeExpansionsAsync(
        SharedVariableWakeLeaseRequest request,
        CancellationToken cancellationToken)
    {
        ValidateLeaseRequest(request);
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var observedAt = await DatabaseNowAsync(cancellationToken);
        await EscalateExpiredExhaustedWakeExpansionsAsync(
            observedAt,
            request.MaxCount,
            cancellationToken);
        List<SharedVariableWakeEntity> wakes;
        if (IsNpgsql())
        {
            wakes = await dbContext.SharedVariableWakes.FromSqlInterpolated($$"""
                SELECT *
                FROM flowbit.shared_variable_wakes
                WHERE (("Status" = 'pending' AND "AvailableAt" <= {{observedAt}})
                       OR ("Status" = 'leased' AND "LeaseExpiresAt" <= {{observedAt}}))
                  AND "AttemptCount" < "MaxAttempts"
                ORDER BY "Revision", "Id"
                LIMIT {{request.MaxCount}}
                FOR UPDATE SKIP LOCKED
                """).Include(wake => wake.SharedVariable).ToListAsync(cancellationToken);
        }
        else
        {
            wakes = await dbContext.SharedVariableWakes
                .Include(wake => wake.SharedVariable)
                .Where(wake => (wake.Status == SharedVariableWakeStatuses.Pending && wake.AvailableAt <= observedAt)
                    || (wake.Status == SharedVariableWakeStatuses.Leased && wake.LeaseExpiresAt <= observedAt))
                .Where(wake => wake.AttemptCount < wake.MaxAttempts)
                .OrderBy(wake => wake.Revision)
                .ThenBy(wake => wake.Id)
                .Take(request.MaxCount)
                .ToListAsync(cancellationToken);
        }

        var leased = new List<SharedVariableWakeRecord>(wakes.Count);
        foreach (var wake in wakes)
        {
            wake.Status = SharedVariableWakeStatuses.Leased;
            wake.LeaseToken = Guid.NewGuid();
            wake.LeaseGeneration++;
            wake.LeasedBy = request.WorkerId;
            wake.LeaseExpiresAt = observedAt + request.LeaseDuration;
            wake.HeartbeatAt = observedAt;
            wake.AttemptCount++;
            wake.UpdatedAt = observedAt;
            leased.Add(MapWake(wake));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return leased;
    }

    public async Task<bool> HeartbeatWakeAsync(
        SharedVariableWakeFence fence,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateFence(fence);
        if (leaseDuration < TimeSpan.FromSeconds(15) || leaseDuration > TimeSpan.FromMinutes(30))
        {
            throw new WorkflowDomainException(
                "Shared-variable wake lease duration must be between 15 seconds and 30 minutes.");
        }

        if (IsNpgsql())
        {
            return fence.WorkKind == SharedVariableWakeWorkKinds.Expansion
                ? await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE flowbit.shared_variable_wakes
                    SET "LeaseExpiresAt" = clock_timestamp() + {leaseDuration},
                        "HeartbeatAt" = clock_timestamp(),
                        "UpdatedAt" = clock_timestamp()
                    WHERE "Id" = {fence.Id}
                      AND "Status" = 'leased'
                      AND "LeasedBy" = {fence.WorkerId}
                      AND "LeaseToken" = {fence.LeaseToken}
                      AND "LeaseGeneration" = {fence.LeaseGeneration}
                      AND "LeaseExpiresAt" > clock_timestamp()
                    """, cancellationToken) == 1
                : await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE flowbit.shared_variable_wake_deliveries
                    SET "LeaseExpiresAt" = clock_timestamp() + {leaseDuration},
                        "HeartbeatAt" = clock_timestamp(),
                        "UpdatedAt" = clock_timestamp()
                    WHERE "Id" = {fence.Id}
                      AND "Status" = 'leased'
                      AND "LeasedBy" = {fence.WorkerId}
                      AND "LeaseToken" = {fence.LeaseToken}
                      AND "LeaseGeneration" = {fence.LeaseGeneration}
                      AND "LeaseExpiresAt" > clock_timestamp()
                    """, cancellationToken) == 1;
        }

        var now = UtcNow();
        if (fence.WorkKind == SharedVariableWakeWorkKinds.Expansion)
        {
            var wake = await dbContext.SharedVariableWakes.SingleOrDefaultAsync(
                item => item.Id == fence.Id,
                cancellationToken);
            if (!FenceMatches(wake, fence, now)) return false;
            wake!.LeaseExpiresAt = now + leaseDuration;
            wake.HeartbeatAt = now;
            wake.UpdatedAt = now;
        }
        else
        {
            var delivery = await dbContext.SharedVariableWakeDeliveries.SingleOrDefaultAsync(
                item => item.Id == fence.Id,
                cancellationToken);
            if (!FenceMatches(delivery, fence, now)) return false;
            delivery!.LeaseExpiresAt = now + leaseDuration;
            delivery.HeartbeatAt = now;
            delivery.UpdatedAt = now;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> IsWakeLeaseAliveAsync(
        SharedVariableWakeFence fence,
        CancellationToken cancellationToken)
    {
        ValidateFence(fence);
        if (IsNpgsql())
        {
            return fence.WorkKind == SharedVariableWakeWorkKinds.Expansion
                ? await dbContext.Database.SqlQuery<bool>($"""
                    SELECT EXISTS (
                        SELECT 1
                        FROM flowbit.shared_variable_wakes
                        WHERE "Id" = {fence.Id}
                          AND "Status" = 'leased'
                          AND "LeasedBy" = {fence.WorkerId}
                          AND "LeaseToken" = {fence.LeaseToken}
                          AND "LeaseGeneration" = {fence.LeaseGeneration}
                          AND "LeaseExpiresAt" > clock_timestamp()) AS "Value"
                    """).SingleAsync(cancellationToken)
                : await dbContext.Database.SqlQuery<bool>($"""
                    SELECT EXISTS (
                        SELECT 1
                        FROM flowbit.shared_variable_wake_deliveries
                        WHERE "Id" = {fence.Id}
                          AND "Status" = 'leased'
                          AND "LeasedBy" = {fence.WorkerId}
                          AND "LeaseToken" = {fence.LeaseToken}
                          AND "LeaseGeneration" = {fence.LeaseGeneration}
                          AND "LeaseExpiresAt" > clock_timestamp()) AS "Value"
                    """).SingleAsync(cancellationToken);
        }

        var now = UtcNow();
        return fence.WorkKind == SharedVariableWakeWorkKinds.Expansion
            ? FenceMatches(await dbContext.SharedVariableWakes.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == fence.Id, cancellationToken), fence, now)
            : FenceMatches(await dbContext.SharedVariableWakeDeliveries.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == fence.Id, cancellationToken), fence, now);
    }

    public async Task<SharedVariableWakeExpansionPageResult> ExpandWakePageAsync(
        SharedVariableWakeFence fence,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ValidateFence(fence, SharedVariableWakeWorkKinds.Expansion);
        if (pageSize is < 1 or > 500)
            throw new WorkflowDomainException("Shared-variable wake expansion page size must be between 1 and 500.");

        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var locked = await TryLockWakeAsync(fence, cancellationToken);
        if (locked is null)
        {
            if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
            return new SharedVariableWakeExpansionPageResult(
                IsComplete: false,
                CreatedCount: 0,
                CursorTokenId: 0,
                Disposition: SharedVariableWakeExpansionPageDispositions.LeaseLost);
        }
        var (wake, now) = locked.Value;

        var candidateQuery =
                from token in dbContext.ExecutionTokens.AsNoTracking()
                join instance in dbContext.WorkflowInstances.AsNoTracking()
                    on token.InstanceId equals instance.Id
                join dependency in dbContext.WorkflowDefinitionSharedVariableDependencies.AsNoTracking()
                    on new { instance.WorkflowDefinitionId, token.NodeId }
                    equals new { dependency.WorkflowDefinitionId, dependency.NodeId }
                where dependency.SharedVariableId == wake.SharedVariableId
                      && dependency.Kind == SharedVariableDependencyKinds.ConditionalCatch
                      && token.Status == "active"
                      && instance.Status == "running"
                      && token.WaitState == null
                      && token.WaitingJobId == null
                      && token.Id > wake.ExpansionCursorTokenId
                      && !dbContext.SharedVariableWakeDeliveries.Any(delivery =>
                          delivery.WakeId == wake.Id
                          && delivery.TokenId == token.Id
                          && delivery.ActivationId == token.ActivationId)
                select new
                {
                    token.InstanceId,
                    instance.WorkflowDefinitionId,
                    TokenId = token.Id,
                    token.ActivationId,
                    token.NodeId
                };
        var candidates = await candidateQuery
            .Distinct()
            .OrderBy(candidate => candidate.TokenId)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var deliveries = candidates.Select(candidate => new SharedVariableWakeDeliveryEntity
        {
            WakeId = wake.Id,
            InstanceId = candidate.InstanceId,
            WorkflowDefinitionId = candidate.WorkflowDefinitionId,
            TokenId = candidate.TokenId,
            ActivationId = candidate.ActivationId,
            NodeId = candidate.NodeId,
            Status = SharedVariableWakeStatuses.Pending,
            MaxAttempts = DefaultWakeMaxAttempts,
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now
        }).ToArray();
        dbContext.SharedVariableWakeDeliveries.AddRange(deliveries);
        if (candidates.Count > 0)
        {
            wake.ExpansionCursorTokenId = candidates[^1].TokenId;
        }
        wake.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return new SharedVariableWakeExpansionPageResult(
            candidates.Count < pageSize,
            candidates.Count,
            wake.ExpansionCursorTokenId);
    }

    public Task<SharedVariableWakeFinalizationResult> CompleteWakeExpansionAsync(
        SharedVariableWakeFence fence,
        SharedVariableWakeFailure? failure,
        CancellationToken cancellationToken)
    {
        ValidateFence(fence, SharedVariableWakeWorkKinds.Expansion);
        return CompleteWakeAsync(fence, failure, cancellationToken);
    }

    public async Task<IReadOnlyList<SharedVariableWakeDeliveryRecord>> LeaseWakeDeliveriesAsync(
        SharedVariableWakeLeaseRequest request,
        CancellationToken cancellationToken)
    {
        ValidateLeaseRequest(request);
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var observedAt = await DatabaseNowAsync(cancellationToken);
        await EscalateExpiredExhaustedWakeDeliveriesAsync(
            observedAt,
            request.MaxCount,
            cancellationToken);
        List<SharedVariableWakeDeliveryEntity> deliveries;
        if (IsNpgsql())
        {
            deliveries = await dbContext.SharedVariableWakeDeliveries.FromSqlInterpolated($$"""
                SELECT delivery.*
                FROM flowbit.shared_variable_wake_deliveries AS delivery
                JOIN flowbit.shared_variable_wakes AS wake ON wake."Id" = delivery."WakeId"
                WHERE ((delivery."Status" = 'pending' AND delivery."AvailableAt" <= {{observedAt}})
                       OR (delivery."Status" = 'leased' AND delivery."LeaseExpiresAt" <= {{observedAt}}))
                  AND delivery."AttemptCount" < delivery."MaxAttempts"
                  AND NOT EXISTS (
                      SELECT 1
                      FROM flowbit.shared_variable_wake_deliveries AS earlier
                      JOIN flowbit.shared_variable_wakes AS earlier_wake
                        ON earlier_wake."Id" = earlier."WakeId"
                      WHERE earlier."TokenId" = delivery."TokenId"
                        AND earlier."ActivationId" = delivery."ActivationId"
                        AND earlier_wake."SharedVariableId" = wake."SharedVariableId"
                        AND earlier."Status" IN ('pending', 'leased')
                        AND earlier_wake."Revision" < wake."Revision")
                ORDER BY wake."Revision", delivery."InstanceId", delivery."TokenId", delivery."Id"
                LIMIT {{request.MaxCount}}
                FOR UPDATE OF delivery SKIP LOCKED
                """).ToListAsync(cancellationToken);
        }
        else
        {
            deliveries = await dbContext.SharedVariableWakeDeliveries
                .Include(delivery => delivery.Wake)
                .Where(delivery => (delivery.Status == SharedVariableWakeStatuses.Pending && delivery.AvailableAt <= observedAt)
                    || (delivery.Status == SharedVariableWakeStatuses.Leased && delivery.LeaseExpiresAt <= observedAt))
                .Where(delivery => delivery.AttemptCount < delivery.MaxAttempts)
                .OrderBy(delivery => delivery.Wake.Revision)
                .ThenBy(delivery => delivery.InstanceId)
                .ThenBy(delivery => delivery.TokenId)
                .Take(request.MaxCount)
                .ToListAsync(cancellationToken);
        }

        var wakeIds = deliveries.Select(delivery => delivery.WakeId).Distinct().ToArray();
        if (wakeIds.Length > 0)
        {
            await dbContext.SharedVariableWakes
                .Where(wake => wakeIds.Contains(wake.Id))
                .Include(wake => wake.SharedVariable)
                .LoadAsync(cancellationToken);
        }
        var leased = new List<SharedVariableWakeDeliveryRecord>(deliveries.Count);
        foreach (var delivery in deliveries)
        {
            delivery.Status = SharedVariableWakeStatuses.Leased;
            delivery.LeaseToken = Guid.NewGuid();
            delivery.LeaseGeneration++;
            delivery.LeasedBy = request.WorkerId;
            delivery.LeaseExpiresAt = observedAt + request.LeaseDuration;
            delivery.HeartbeatAt = observedAt;
            delivery.AttemptCount++;
            delivery.UpdatedAt = observedAt;
            leased.Add(MapDelivery(delivery));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return leased;
    }

    public Task<SharedVariableWakeFinalizationResult> CompleteWakeDeliveryAsync(
        SharedVariableWakeFence fence,
        SharedVariableWakeFailure? failure,
        CancellationToken cancellationToken)
    {
        ValidateFence(fence, SharedVariableWakeWorkKinds.Delivery);
        return CompleteDeliveryAsync(fence, failure, cancellationToken);
    }

    private async Task EscalateExpiredExhaustedWakeExpansionsAsync(
        DateTimeOffset observedAt,
        int limit,
        CancellationToken cancellationToken)
    {
        List<SharedVariableWakeEntity> exhausted;
        if (IsNpgsql())
        {
            exhausted = await dbContext.SharedVariableWakes.FromSqlInterpolated($$"""
                SELECT *
                FROM flowbit.shared_variable_wakes
                WHERE "Status" = 'failed'
                   OR ((("Status" = 'pending' AND "AvailableAt" <= {{observedAt}})
                        OR ("Status" = 'leased' AND "LeaseExpiresAt" <= {{observedAt}}))
                       AND "AttemptCount" >= "MaxAttempts")
                ORDER BY "Revision", "Id"
                LIMIT {{limit}}
                FOR UPDATE SKIP LOCKED
                """).ToListAsync(cancellationToken);
            var variableIds = exhausted
                .Select(wake => wake.SharedVariableId)
                .Distinct()
                .ToArray();
            if (variableIds.Length > 0)
            {
                await dbContext.SharedVariables
                    .Where(variable => variableIds.Contains(variable.Id))
                    .LoadAsync(cancellationToken);
            }
        }
        else
        {
            exhausted = await dbContext.SharedVariableWakes
                .Include(wake => wake.SharedVariable)
                .Where(wake => wake.Status == SharedVariableWakeStatuses.Failed
                               || (((wake.Status == SharedVariableWakeStatuses.Pending
                                && wake.AvailableAt <= observedAt)
                               || (wake.Status == SharedVariableWakeStatuses.Leased
                                   && wake.LeaseExpiresAt <= observedAt))
                                  && wake.AttemptCount >= wake.MaxAttempts))
                .OrderBy(wake => wake.Revision)
                .ThenBy(wake => wake.Id)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }

        foreach (var wake in exhausted)
        {
            var failure = wake.Status == SharedVariableWakeStatuses.Failed
                ? new SharedVariableWakeFailure(
                    "legacyFailure",
                    wake.LastError ?? "Legacy wake expansion exhausted during rolling replacement.")
                : new SharedVariableWakeFailure(
                    "leaseExpiredAfterMaxAttempts",
                    $"Wake expansion lease expired after attempt {wake.AttemptCount} of {wake.MaxAttempts}.");
            dbContext.SharedVariableWakeIncidents.Add(NewIncident(
                SharedVariableWakeWorkKinds.Expansion,
                wake,
                null,
                wake.SharedVariable,
                failure,
                observedAt));
            wake.Status = SharedVariableWakeStatuses.Incident;
            wake.LastError = BoundError(failure.Description);
            wake.CompletedAt = null;
            wake.UpdatedAt = observedAt;
            ClearLease(wake);
        }

        if (exhausted.Count > 0)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EscalateExpiredExhaustedWakeDeliveriesAsync(
        DateTimeOffset observedAt,
        int limit,
        CancellationToken cancellationToken)
    {
        List<SharedVariableWakeDeliveryEntity> exhausted;
        if (IsNpgsql())
        {
            exhausted = await dbContext.SharedVariableWakeDeliveries.FromSqlInterpolated($$"""
                SELECT *
                FROM flowbit.shared_variable_wake_deliveries
                WHERE "Status" = 'failed'
                   OR ((("Status" = 'pending' AND "AvailableAt" <= {{observedAt}})
                        OR ("Status" = 'leased' AND "LeaseExpiresAt" <= {{observedAt}}))
                       AND "AttemptCount" >= "MaxAttempts")
                ORDER BY "Id"
                LIMIT {{limit}}
                FOR UPDATE SKIP LOCKED
                """).ToListAsync(cancellationToken);
            var wakeIds = exhausted.Select(delivery => delivery.WakeId).Distinct().ToArray();
            if (wakeIds.Length > 0)
            {
                await dbContext.SharedVariableWakes
                    .Include(wake => wake.SharedVariable)
                    .Where(wake => wakeIds.Contains(wake.Id))
                    .LoadAsync(cancellationToken);
            }
        }
        else
        {
            exhausted = await dbContext.SharedVariableWakeDeliveries
                .Include(delivery => delivery.Wake)
                .ThenInclude(wake => wake.SharedVariable)
                .Where(delivery => delivery.Status == SharedVariableWakeStatuses.Failed
                                   || (((delivery.Status == SharedVariableWakeStatuses.Pending
                                    && delivery.AvailableAt <= observedAt)
                                   || (delivery.Status == SharedVariableWakeStatuses.Leased
                                       && delivery.LeaseExpiresAt <= observedAt))
                                      && delivery.AttemptCount >= delivery.MaxAttempts))
                .OrderBy(delivery => delivery.Id)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }

        foreach (var delivery in exhausted)
        {
            var failure = delivery.Status == SharedVariableWakeStatuses.Failed
                ? new SharedVariableWakeFailure(
                    "legacyFailure",
                    delivery.LastError ?? "Legacy wake delivery exhausted during rolling replacement.")
                : new SharedVariableWakeFailure(
                    "leaseExpiredAfterMaxAttempts",
                    $"Wake delivery lease expired after attempt {delivery.AttemptCount} of {delivery.MaxAttempts}.");
            dbContext.SharedVariableWakeIncidents.Add(NewIncident(
                SharedVariableWakeWorkKinds.Delivery,
                delivery.Wake,
                delivery,
                delivery.Wake.SharedVariable,
                failure,
                observedAt));
            delivery.Status = SharedVariableWakeStatuses.Incident;
            delivery.LastError = BoundError(failure.Description);
            delivery.CompletedAt = null;
            delivery.UpdatedAt = observedAt;
            ClearLease(delivery);
        }

        if (exhausted.Count > 0)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<SharedVariableWakeFinalizationResult> CompleteWakeAsync(
        SharedVariableWakeFence fence,
        SharedVariableWakeFailure? failure,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var locked = await TryLockWakeAsync(fence, cancellationToken);
        if (locked is null)
        {
            if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
            return new SharedVariableWakeFinalizationResult(
                SharedVariableWakeFinalizationDispositions.LeaseLost);
        }
        var (wake, now) = locked.Value;

        var result = await ApplyWakeFinalizationAsync(wake, failure, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (result.Disposition == SharedVariableWakeFinalizationDispositions.RetryScheduled)
            await NotifyWakeupAsync(cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<SharedVariableWakeFinalizationResult> CompleteDeliveryAsync(
        SharedVariableWakeFence fence,
        CancellationToken cancellationToken)
    {
        return await CompleteDeliveryAsync(fence, null, cancellationToken);
    }

    private async Task<SharedVariableWakeFinalizationResult> CompleteDeliveryAsync(
        SharedVariableWakeFence fence,
        SharedVariableWakeFailure? failure,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var locked = await TryLockDeliveryAsync(fence, cancellationToken);
        if (locked is null)
        {
            if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
            return new SharedVariableWakeFinalizationResult(
                SharedVariableWakeFinalizationDispositions.LeaseLost);
        }
        var (delivery, now) = locked.Value;

        var result = await ApplyDeliveryFinalizationAsync(delivery, failure, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (result.Disposition == SharedVariableWakeFinalizationDispositions.RetryScheduled)
            await NotifyWakeupAsync(cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<SharedVariableWakeFinalizationResult> ApplyWakeFinalizationAsync(
        SharedVariableWakeEntity wake,
        SharedVariableWakeFailure? failure,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (failure is null)
        {
            CompleteWork(wake, now);
            return new SharedVariableWakeFinalizationResult(
                SharedVariableWakeFinalizationDispositions.Completed);
        }

        wake.LastError = BoundError(failure.Description);
        ClearLease(wake);
        wake.UpdatedAt = now;
        wake.CompletedAt = null;
        if (wake.AttemptCount < wake.MaxAttempts)
        {
            wake.Status = SharedVariableWakeStatuses.Pending;
            wake.AvailableAt = RetryAt(now, wake.AttemptCount);
            return new SharedVariableWakeFinalizationResult(
                SharedVariableWakeFinalizationDispositions.RetryScheduled,
                AvailableAt: wake.AvailableAt);
        }

        wake.Status = SharedVariableWakeStatuses.Incident;
        var variable = await dbContext.SharedVariables.AsNoTracking()
            .SingleAsync(item => item.Id == wake.SharedVariableId, cancellationToken);
        var incident = NewIncident(
            SharedVariableWakeWorkKinds.Expansion,
            wake,
            null,
            variable,
            failure,
            now);
        dbContext.SharedVariableWakeIncidents.Add(incident);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SharedVariableWakeFinalizationResult(
            SharedVariableWakeFinalizationDispositions.IncidentOpened,
            incident.Id);
    }

    private async Task<SharedVariableWakeFinalizationResult> ApplyDeliveryFinalizationAsync(
        SharedVariableWakeDeliveryEntity delivery,
        SharedVariableWakeFailure? failure,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (failure is null)
        {
            CompleteWork(delivery, now);
            return new SharedVariableWakeFinalizationResult(
                SharedVariableWakeFinalizationDispositions.Completed);
        }

        delivery.LastError = BoundError(failure.Description);
        ClearLease(delivery);
        delivery.UpdatedAt = now;
        delivery.CompletedAt = null;
        if (delivery.AttemptCount < delivery.MaxAttempts)
        {
            delivery.Status = SharedVariableWakeStatuses.Pending;
            delivery.AvailableAt = RetryAt(now, delivery.AttemptCount);
            return new SharedVariableWakeFinalizationResult(
                SharedVariableWakeFinalizationDispositions.RetryScheduled,
                AvailableAt: delivery.AvailableAt);
        }

        delivery.Status = SharedVariableWakeStatuses.Incident;
        await dbContext.Entry(delivery).Reference(item => item.Wake).LoadAsync(cancellationToken);
        await dbContext.Entry(delivery.Wake).Reference(item => item.SharedVariable).LoadAsync(cancellationToken);
        var incident = NewIncident(
            SharedVariableWakeWorkKinds.Delivery,
            delivery.Wake,
            delivery,
            delivery.Wake.SharedVariable,
            failure,
            now);
        dbContext.SharedVariableWakeIncidents.Add(incident);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SharedVariableWakeFinalizationResult(
            SharedVariableWakeFinalizationDispositions.IncidentOpened,
            incident.Id);
    }

    private async Task<(SharedVariableWakeEntity Wake, DateTimeOffset ObservedAt)?> TryLockWakeAsync(
        SharedVariableWakeFence fence,
        CancellationToken cancellationToken)
    {
        var wake = IsNpgsql()
            ? await dbContext.SharedVariableWakes.FromSqlInterpolated(
                    $"""SELECT * FROM flowbit.shared_variable_wakes WHERE "Id" = {fence.Id} FOR UPDATE""")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.SharedVariableWakes.SingleOrDefaultAsync(item => item.Id == fence.Id, cancellationToken);
        // The database clock must be sampled after SELECT ... FOR UPDATE has
        // returned. A pre-lock sample lets a caller whose statement waited
        // beyond LeaseExpiresAt mutate work with an already-expired fence.
        var observedAt = await DatabaseNowAsync(cancellationToken);
        return FenceMatches(wake, fence, observedAt) ? (wake!, observedAt) : null;
    }

    private async Task<(SharedVariableWakeDeliveryEntity Delivery, DateTimeOffset ObservedAt)?> TryLockDeliveryAsync(
        SharedVariableWakeFence fence,
        CancellationToken cancellationToken)
    {
        var delivery = IsNpgsql()
            ? await dbContext.SharedVariableWakeDeliveries.FromSqlInterpolated(
                    $"""SELECT * FROM flowbit.shared_variable_wake_deliveries WHERE "Id" = {fence.Id} FOR UPDATE""")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.SharedVariableWakeDeliveries.SingleOrDefaultAsync(item => item.Id == fence.Id, cancellationToken);
        var observedAt = await DatabaseNowAsync(cancellationToken);
        return FenceMatches(delivery, fence, observedAt) ? (delivery!, observedAt) : null;
    }

    public async Task<(IReadOnlyList<SharedVariableWakeIncidentRecord> Items, long TotalCount)>
        SearchWakeIncidentsAsync(
            SharedVariableWakeIncidentQuery query,
            CancellationToken cancellationToken)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 200)
            throw new WorkflowDomainException("Shared-variable incident paging is invalid.");
        if (query.Status is not null
            && query.Status is not (SharedVariableWakeIncidentStatuses.Open
                or SharedVariableWakeIncidentStatuses.Resolved))
            throw new WorkflowDomainException("Unknown shared-variable incident status.");
        if (query.WorkKind is not null
            && query.WorkKind is not (SharedVariableWakeWorkKinds.Expansion
                or SharedVariableWakeWorkKinds.Delivery))
            throw new WorkflowDomainException("Unknown shared-variable incident work kind.");

        var source = dbContext.SharedVariableWakeIncidents.AsNoTracking();
        if (query.Status is not null)
            source = source.Where(incident => incident.Status == query.Status);
        if (query.WorkKind is not null)
            source = source.Where(incident => incident.WorkKind == query.WorkKind);
        if (!string.IsNullOrWhiteSpace(query.SharedKey))
        {
            var key = query.SharedKey.Trim();
            source = source.Where(incident => incident.SharedKey == key);
        }

        var total = await source.LongCountAsync(cancellationToken);
        var incidents = await source
            .OrderByDescending(incident => incident.UpdatedAt)
            .ThenByDescending(incident => incident.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToListAsync(cancellationToken);
        return (incidents
            .Select(incident => MapIncident(incident) with { Details = null })
            .ToArray(), total);
    }

    public async Task<SharedVariableWakeIncidentRecord?> GetWakeIncidentAsync(
        long incidentId,
        CancellationToken cancellationToken)
    {
        var incident = await dbContext.SharedVariableWakeIncidents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == incidentId, cancellationToken);
        return incident is null ? null : MapIncident(incident);
    }

    public Task<SharedVariableWakeIncidentRecord?> RetryWakeIncidentAsync(
        long incidentId,
        string resolvedBy,
        CancellationToken cancellationToken) =>
        MutateWakeIncidentAsync(
            incidentId,
            resolvedBy,
            reason: null,
            retry: true,
            cancellationToken);

    public Task<SharedVariableWakeIncidentRecord?> ResolveWakeIncidentAsync(
        long incidentId,
        string resolvedBy,
        string reason,
        CancellationToken cancellationToken) =>
        MutateWakeIncidentAsync(
            incidentId,
            resolvedBy,
            reason,
            retry: false,
            cancellationToken);

    private async Task<SharedVariableWakeIncidentRecord?> MutateWakeIncidentAsync(
        long incidentId,
        string resolvedBy,
        string? reason,
        bool retry,
        CancellationToken cancellationToken)
    {
        if (incidentId <= 0) throw new WorkflowDomainException("A valid incident id is required.");
        if (!retry && string.IsNullOrWhiteSpace(reason))
            throw new WorkflowDomainException("A nonblank incident resolution reason is required.");
        var actor = BoundText(resolvedBy, 300) ?? "system";
        var lookup = await dbContext.SharedVariableWakeIncidents.AsNoTracking()
            .Where(incident => incident.Id == incidentId)
            .Select(incident => new
            {
                incident.Id,
                incident.WorkKind,
                incident.WakeId,
                incident.DeliveryId,
                incident.InstanceId,
                incident.TokenId,
                incident.ActivationId
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (lookup is null) return null;
        if (lookup.WakeId is null && lookup.DeliveryId is null)
            throw new WorkflowConflictException("The retained incident no longer has retryable work.");

        await using var transaction = await BeginOwnedTransactionAsync(cancellationToken);
        if (retry && lookup.WorkKind == SharedVariableWakeWorkKinds.Delivery)
        {
            await ValidateRetryOwnershipAsync(
                lookup.InstanceId,
                lookup.TokenId,
                lookup.ActivationId,
                cancellationToken);
        }

        var incident = IsNpgsql()
            ? await dbContext.SharedVariableWakeIncidents.FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM flowbit.shared_variable_wake_incidents
                    WHERE "Id" = {incidentId}
                    FOR UPDATE
                    """)
                .SingleAsync(cancellationToken)
            : await dbContext.SharedVariableWakeIncidents.SingleAsync(
                item => item.Id == incidentId,
                cancellationToken);
        if (incident.Status != SharedVariableWakeIncidentStatuses.Open)
            throw new WorkflowConflictException("The shared-variable incident is already resolved.");

        var now = await DatabaseNowAsync(cancellationToken);
        if (incident.WorkKind == SharedVariableWakeWorkKinds.Expansion)
        {
            var wake = await LockWakeByIdAsync(
                incident.WakeId ?? throw new WorkflowConflictException("The incident wake was detached."),
                cancellationToken);
            if (wake.Status != SharedVariableWakeStatuses.Incident)
                throw new WorkflowConflictException("The incident no longer owns an expansion awaiting recovery.");
            if (retry)
            {
                wake.Status = SharedVariableWakeStatuses.Pending;
                wake.MaxAttempts = Math.Max(wake.MaxAttempts, checked(wake.AttemptCount + 1));
                wake.AvailableAt = now;
                wake.LastError = null;
                wake.CompletedAt = null;
            }
            else
            {
                wake.Status = SharedVariableWakeStatuses.Cancelled;
                wake.LastError = BoundError(reason) ?? wake.LastError;
                wake.CompletedAt = now;
            }
            ClearLease(wake);
            wake.UpdatedAt = now;
        }
        else
        {
            var delivery = await LockDeliveryByIdAsync(
                incident.DeliveryId ?? throw new WorkflowConflictException("The incident delivery was detached."),
                cancellationToken);
            if (delivery.Status != SharedVariableWakeStatuses.Incident)
                throw new WorkflowConflictException("The incident no longer owns a delivery awaiting recovery.");
            if (retry)
            {
                delivery.Status = SharedVariableWakeStatuses.Pending;
                delivery.MaxAttempts = Math.Max(delivery.MaxAttempts, checked(delivery.AttemptCount + 1));
                delivery.AvailableAt = now;
                delivery.LastError = null;
                delivery.CompletedAt = null;
            }
            else
            {
                delivery.Status = SharedVariableWakeStatuses.Cancelled;
                delivery.LastError = BoundError(reason) ?? delivery.LastError;
                delivery.CompletedAt = now;
            }
            ClearLease(delivery);
            delivery.UpdatedAt = now;
        }

        incident.Status = SharedVariableWakeIncidentStatuses.Resolved;
        incident.ResolvedBy = actor;
        incident.ResolvedAt = now;
        incident.UpdatedAt = now;
        incident.ResolutionReason = retry
            ? "retryRequested"
            : BoundText(reason, 1000);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (retry) await NotifyWakeupAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return MapIncident(incident);
    }

    private async Task ValidateRetryOwnershipAsync(
        long? instanceId,
        long? tokenId,
        Guid? activationId,
        CancellationToken cancellationToken)
    {
        if (instanceId is not long owningInstanceId
            || tokenId is not long owningTokenId
            || activationId is not Guid owningActivationId)
            throw new WorkflowConflictException("The delivery incident has incomplete workflow ownership.");

        var instance = IsNpgsql()
            ? await dbContext.WorkflowInstances.FromSqlInterpolated(
                    $"""SELECT * FROM flowbit.workflow_instances WHERE "Id" = {owningInstanceId} FOR UPDATE""")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.WorkflowInstances.SingleOrDefaultAsync(
                item => item.Id == owningInstanceId,
                cancellationToken);
        if (instance is null || instance.Status != WorkflowInstanceStatuses.Running)
            throw new WorkflowConflictException("The delivery incident's workflow instance is no longer running.");

        var token = IsNpgsql()
            ? await dbContext.ExecutionTokens.FromSqlInterpolated(
                    $"""SELECT * FROM flowbit.execution_tokens WHERE "Id" = {owningTokenId} FOR UPDATE""")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.ExecutionTokens.SingleOrDefaultAsync(
                item => item.Id == owningTokenId,
                cancellationToken);
        if (token is null
            || token.InstanceId != owningInstanceId
            || token.Status != ExecutionTokenStatuses.Active
            || token.ActivationId != owningActivationId)
            throw new WorkflowConflictException("The delivery incident's workflow activation is no longer current.");
    }

    private async Task<SharedVariableWakeEntity> LockWakeByIdAsync(
        long wakeId,
        CancellationToken cancellationToken) => IsNpgsql()
        ? await dbContext.SharedVariableWakes.FromSqlInterpolated(
                $"""SELECT * FROM flowbit.shared_variable_wakes WHERE "Id" = {wakeId} FOR UPDATE""")
            .SingleAsync(cancellationToken)
        : await dbContext.SharedVariableWakes.SingleAsync(item => item.Id == wakeId, cancellationToken);

    private async Task<SharedVariableWakeDeliveryEntity> LockDeliveryByIdAsync(
        long deliveryId,
        CancellationToken cancellationToken) => IsNpgsql()
        ? await dbContext.SharedVariableWakeDeliveries.FromSqlInterpolated(
                $"""SELECT * FROM flowbit.shared_variable_wake_deliveries WHERE "Id" = {deliveryId} FOR UPDATE""")
            .SingleAsync(cancellationToken)
        : await dbContext.SharedVariableWakeDeliveries.SingleAsync(
            item => item.Id == deliveryId,
            cancellationToken);

    public async Task<SharedVariableWakeCleanupResult> CleanupWakeOutboxAsync(
        DateTimeOffset completedBefore,
        DateTimeOffset resolvedIncidentsBefore,
        int batchSize,
        CancellationToken cancellationToken)
    {
        batchSize = Math.Clamp(batchSize, 1, 1000);
        var elapsed = Stopwatch.StartNew();
        var result = new SharedVariableWakeCleanupResult(0, 0, 0);
        for (var batch = 0; batch < 20 && elapsed.Elapsed < TimeSpan.FromSeconds(30); batch++)
        {
            var current = await CleanupWakeOutboxBatchAsync(
                completedBefore,
                resolvedIncidentsBefore,
                batchSize,
                cancellationToken);
            result = new SharedVariableWakeCleanupResult(
                result.WakesDeleted + current.WakesDeleted,
                result.DeliveriesDeleted + current.DeliveriesDeleted,
                result.IncidentsDeleted + current.IncidentsDeleted);
            if (current.WakesDeleted + current.DeliveriesDeleted + current.IncidentsDeleted == 0)
                break;
        }
        return result;
    }

    private async Task<SharedVariableWakeCleanupResult> CleanupWakeOutboxBatchAsync(
        DateTimeOffset completedBefore,
        DateTimeOffset resolvedIncidentsBefore,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginOwnedTransactionAsync(cancellationToken);
        var incidentIds = await dbContext.SharedVariableWakeIncidents
            .Where(incident => incident.Status == SharedVariableWakeIncidentStatuses.Resolved
                               && incident.ResolvedAt < resolvedIncidentsBefore)
            .OrderBy(incident => incident.ResolvedAt)
            .ThenBy(incident => incident.Id)
            .Select(incident => incident.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);
        var incidentsDeleted = incidentIds.Length == 0
            ? 0
            : await dbContext.SharedVariableWakeIncidents
                .Where(incident => incidentIds.Contains(incident.Id))
                .ExecuteDeleteAsync(cancellationToken);

        var deliveryIds = await dbContext.SharedVariableWakeDeliveries
            .Where(delivery => (delivery.Status == SharedVariableWakeStatuses.Completed
                                || delivery.Status == SharedVariableWakeStatuses.Cancelled)
                               && delivery.CompletedAt < completedBefore
                               && delivery.LeaseToken == null
                               && delivery.LeaseExpiresAt == null
                               && !dbContext.SharedVariableWakeIncidents.Any(incident =>
                                   incident.DeliveryId == delivery.Id
                                   && incident.Status == SharedVariableWakeIncidentStatuses.Open))
            .OrderBy(delivery => delivery.CompletedAt)
            .ThenBy(delivery => delivery.Id)
            .Select(delivery => delivery.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);
        var deliveriesDeleted = deliveryIds.Length == 0
            ? 0
            : await dbContext.SharedVariableWakeDeliveries
                .Where(delivery => deliveryIds.Contains(delivery.Id))
                .ExecuteDeleteAsync(cancellationToken);

        var wakeIds = await dbContext.SharedVariableWakes
            .Where(wake => (wake.Status == SharedVariableWakeStatuses.Completed
                            || wake.Status == SharedVariableWakeStatuses.Cancelled)
                           && wake.CompletedAt < completedBefore
                           && wake.LeaseToken == null
                           && wake.LeaseExpiresAt == null
                           && !dbContext.SharedVariableWakeDeliveries.Any(delivery => delivery.WakeId == wake.Id)
                           && !dbContext.SharedVariableWakeIncidents.Any(incident =>
                               incident.WakeId == wake.Id
                               && incident.Status == SharedVariableWakeIncidentStatuses.Open))
            .OrderBy(wake => wake.CompletedAt)
            .ThenBy(wake => wake.Id)
            .Select(wake => wake.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);
        var wakesDeleted = wakeIds.Length == 0
            ? 0
            : await dbContext.SharedVariableWakes
                .Where(wake => wakeIds.Contains(wake.Id))
                .ExecuteDeleteAsync(cancellationToken);

        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new SharedVariableWakeCleanupResult(wakesDeleted, deliveriesDeleted, incidentsDeleted);
    }

    private static SharedVariableWakeIncidentEntity NewIncident(
        string workKind,
        SharedVariableWakeEntity wake,
        SharedVariableWakeDeliveryEntity? delivery,
        SharedVariableEntity variable,
        SharedVariableWakeFailure failure,
        DateTimeOffset now) => new()
    {
        WorkKind = workKind,
        WakeId = workKind == SharedVariableWakeWorkKinds.Expansion ? wake.Id : null,
        DeliveryId = delivery?.Id,
        OriginalWakeId = wake.Id,
        OriginalDeliveryId = delivery?.Id,
        SharedVariableId = variable.Id,
        SharedKey = variable.Key,
        Revision = wake.Revision,
        InstanceId = delivery?.InstanceId,
        WorkflowDefinitionId = delivery?.WorkflowDefinitionId,
        TokenId = delivery?.TokenId,
        ActivationId = delivery?.ActivationId,
        NodeId = delivery?.NodeId,
        Type = BoundText(failure.Code, 100) ?? "shared_variable_wake_failure",
        Status = SharedVariableWakeIncidentStatuses.Open,
        Summary = BoundText(
            workKind == SharedVariableWakeWorkKinds.Expansion
                ? "Shared-variable wake expansion exhausted its retry budget."
                : "Shared-variable wake delivery exhausted its retry budget.",
            500)!,
        Details = BoundText(failure.Description, 4000),
        CreatedAt = now,
        UpdatedAt = now
    };

    private static bool FenceMatches(
        SharedVariableWakeEntity? wake,
        SharedVariableWakeFence fence,
        DateTimeOffset now) => wake is not null
            && wake.Status == SharedVariableWakeStatuses.Leased
            && string.Equals(wake.LeasedBy, fence.WorkerId, StringComparison.Ordinal)
            && wake.LeaseToken == fence.LeaseToken
            && wake.LeaseGeneration == fence.LeaseGeneration
            && wake.LeaseExpiresAt > now;

    private static bool FenceMatches(
        SharedVariableWakeDeliveryEntity? delivery,
        SharedVariableWakeFence fence,
        DateTimeOffset now) => delivery is not null
            && delivery.Status == SharedVariableWakeStatuses.Leased
            && string.Equals(delivery.LeasedBy, fence.WorkerId, StringComparison.Ordinal)
            && delivery.LeaseToken == fence.LeaseToken
            && delivery.LeaseGeneration == fence.LeaseGeneration
            && delivery.LeaseExpiresAt > now;

    private static void CompleteWork(SharedVariableWakeEntity wake, DateTimeOffset now)
    {
        wake.Status = SharedVariableWakeStatuses.Completed;
        wake.LastError = null;
        ClearLease(wake);
        wake.AvailableAt = now;
        wake.UpdatedAt = now;
        wake.CompletedAt = now;
    }

    private static void CompleteWork(SharedVariableWakeDeliveryEntity delivery, DateTimeOffset now)
    {
        delivery.Status = SharedVariableWakeStatuses.Completed;
        delivery.LastError = null;
        ClearLease(delivery);
        delivery.AvailableAt = now;
        delivery.UpdatedAt = now;
        delivery.CompletedAt = now;
    }

    private static void ClearLease(SharedVariableWakeEntity wake)
    {
        wake.LeaseToken = null;
        wake.LeasedBy = null;
        wake.LeaseExpiresAt = null;
        wake.HeartbeatAt = null;
    }

    private static void ClearLease(SharedVariableWakeDeliveryEntity delivery)
    {
        delivery.LeaseToken = null;
        delivery.LeasedBy = null;
        delivery.LeaseExpiresAt = null;
        delivery.HeartbeatAt = null;
    }

    private async Task<SharedVariableEntity?> LockVariableAsync(
        string key,
        CancellationToken cancellationToken) => IsNpgsql()
        ? await dbContext.SharedVariables.FromSqlInterpolated(
                $"""SELECT * FROM flowbit.shared_variables WHERE "Key" = {key} FOR UPDATE""")
            .SingleOrDefaultAsync(cancellationToken)
        : await dbContext.SharedVariables.SingleOrDefaultAsync(variable => variable.Key == key, cancellationToken);

    private async Task<long> AllocateRevisionAsync(CancellationToken cancellationToken)
    {
        if (IsNpgsql())
        {
            return await dbContext.Database
                .SqlQueryRaw<long>(
                    "SELECT flowbit.next_shared_variable_revision() AS \"Value\"")
                .SingleAsync(cancellationToken);
        }

        var state = await dbContext.SharedVariableRevisionStates.SingleAsync(
            item => item.Id == 1,
            cancellationToken);
        state.LastRevision++;
        state.UpdatedAt = UtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        return state.LastRevision;
    }

    private async Task LockIdempotencyRequestAsync(
        SharedVariableCallerRecord caller,
        string? requestId,
        CancellationToken cancellationToken)
    {
        if (!IsNpgsql() || string.IsNullOrWhiteSpace(requestId)) return;
        var identity = $"shared-variable-request\n{caller.Kind}\n{caller.Id}\n{requestId.Trim()}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))",
            cancellationToken);
    }

    private async Task<SharedVariableMutationResult?> TryReplayAsync(
        SharedVariableCallerRecord caller,
        string? requestId,
        string? fingerprint,
        string operation,
        string key,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return null;
        var normalizedRequestId = requestId.Trim();
        var request = await dbContext.SharedVariableRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CallerKind == caller.Kind
                && item.CallerId == caller.Id
                && item.RequestId == normalizedRequestId, cancellationToken);
        if (request is null) return null;

        var hash = Fingerprint(fingerprint);
        if (!string.Equals(request.Operation, operation, StringComparison.Ordinal)
            || !string.Equals(request.SharedKey, key, StringComparison.OrdinalIgnoreCase)
            || !CryptographicOperations.FixedTimeEquals(request.RequestHash, hash))
        {
            throw new WorkflowConflictException(
                $"Request id '{normalizedRequestId}' was already used for a different shared-variable operation.");
        }
        var result = request.ResponseJson.RootElement.Deserialize<SharedVariableMutationResult>(JsonOptions)
            ?? throw new InvalidOperationException("Stored shared-variable idempotency response is invalid.");
        return result with { IsIdempotentReplay = true };
    }

    private async Task RecordRequestAsync(
        SharedVariableCallerRecord caller,
        string? requestId,
        string? fingerprint,
        string operation,
        string key,
        long sharedVariableId,
        long resultRevision,
        SharedVariableMutationResult result,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return;
        dbContext.SharedVariableRequests.Add(new SharedVariableRequestEntity
        {
            CallerKind = caller.Kind,
            CallerId = caller.Id,
            RequestId = requestId.Trim(),
            Operation = operation,
            SharedKey = key,
            RequestHash = Fingerprint(fingerprint),
            SharedVariableId = sharedVariableId,
            ResultRevision = resultRevision,
            ResponseJson = JsonSerializer.SerializeToDocument(result, JsonOptions),
            CreatedAt = createdAt
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static byte[] Fingerprint(string? fingerprint) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint ?? string.Empty));

    private static void EnsureExpectedRevision(SharedVariableEntity variable, long? expectedRevision)
    {
        if (expectedRevision is long expected && variable.CurrentRevision != expected)
        {
            throw new WorkflowConflictException(
                $"Shared variable '{variable.Key}' is at revision {variable.CurrentRevision}, not expected revision {expected}.");
        }
    }

    private static void EnsureExpectedValueRevision(
        SharedVariableEntity variable,
        long? expectedValueRevision)
    {
        if (expectedValueRevision is long expected && variable.ValueRevision != expected)
        {
            throw new WorkflowConflictException(
                $"Shared variable '{variable.Key}' value changed while async work was running.");
        }
    }

    private void EnqueueWake(
        SharedVariableEntity variable,
        SharedVariableRevisionEntity revision,
        DateTimeOffset now) =>
        dbContext.SharedVariableWakes.Add(new SharedVariableWakeEntity
        {
            SharedVariableId = variable.Id,
            RevisionId = revision.Id,
            Revision = revision.Revision,
            Status = SharedVariableWakeStatuses.Pending,
            MaxAttempts = DefaultWakeMaxAttempts,
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

    private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(
        CancellationToken cancellationToken) =>
        dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

    private async Task<DateTimeOffset> DatabaseNowAsync(CancellationToken cancellationToken) =>
        IsNpgsql()
            ? await dbContext.Database
                .SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"")
                .SingleAsync(cancellationToken)
            : UtcNow();

    private Task NotifyWakeupAsync(CancellationToken cancellationToken) => IsNpgsql()
        ? dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_notify('flowbit_jobs', 'shared-variable')",
            cancellationToken)
        : Task.CompletedTask;

    private bool IsNpgsql() =>
        string.Equals(dbContext.Database.ProviderName, ProviderName, StringComparison.Ordinal);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static DateTimeOffset UtcNow()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }

    private static JsonDocument ToDocument(JsonElement value) => JsonDocument.Parse(value.GetRawText());

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] NormalizeKeys(IReadOnlyCollection<string> keys) => keys
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .Select(key => key.Trim())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string? BoundText(string? value, int maxRunes)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var runes = value.Trim().EnumerateRunes().Take(maxRunes).ToArray();
        return string.Concat(runes.Select(rune => rune.ToString()));
    }

    private static string? BoundError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var runes = error.Trim().EnumerateRunes().Take(1000).ToArray();
        return string.Concat(runes.Select(rune => rune.ToString()));
    }

    private static DateTimeOffset RetryAt(DateTimeOffset now, int attemptCount)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 8);
        var delaySeconds = Math.Min(300, 1 << exponent);
        return now.AddSeconds(delaySeconds);
    }

    private static void ValidateLeaseRequest(SharedVariableWakeLeaseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkerId)
            || request.WorkerId.EnumerateRunes().Count() > 300)
            throw new WorkflowDomainException("Shared-variable wake worker id is required and must be at most 300 characters.");
        if (request.MaxCount is < 1 or > 1000)
            throw new WorkflowDomainException("Shared-variable wake lease count must be between 1 and 1000.");
        if (request.LeaseDuration < TimeSpan.FromSeconds(15)
            || request.LeaseDuration > TimeSpan.FromMinutes(30))
            throw new WorkflowDomainException("Shared-variable wake lease duration must be between 15 seconds and 30 minutes.");
    }

    private static void ValidateFence(
        SharedVariableWakeFence fence,
        string? expectedWorkKind = null)
    {
        if (fence.Id <= 0
            || string.IsNullOrWhiteSpace(fence.WorkerId)
            || fence.WorkerId.EnumerateRunes().Count() > 300
            || fence.LeaseToken == Guid.Empty
            || fence.LeaseGeneration <= 0
            || fence.WorkKind is not (SharedVariableWakeWorkKinds.Expansion
                or SharedVariableWakeWorkKinds.Delivery))
            throw new WorkflowDomainException("The shared-variable wake fence is invalid.");
        if (expectedWorkKind is not null
            && !string.Equals(fence.WorkKind, expectedWorkKind, StringComparison.Ordinal))
            throw new WorkflowDomainException("The shared-variable wake fence has the wrong work kind.");
    }

    private static SharedVariableRecord MapVariable(SharedVariableEntity entity)
    {
        var current = entity.CurrentValue;
        var hasValue = current is { IsDeleted: false, ValueJson: not null };
        return new SharedVariableRecord(
            entity.Id,
            entity.Key,
            entity.DataType,
            entity.IsArray,
            entity.Nullable,
            entity.Validation,
            entity.Description,
            hasValue,
            hasValue ? current!.ValueJson!.RootElement.Clone() : null,
            entity.Status,
            entity.CurrentRevision,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.ArchivedAt,
            entity.ValueRevision);
    }

    private static SharedVariableRevisionRecord MapRevision(SharedVariableRevisionEntity entity) => new(
        entity.Id,
        entity.SharedVariableId,
        entity.Revision,
        entity.Operation,
        entity.ValueChanged,
        entity.HasValue,
        entity.HasValue ? entity.ValueJson!.RootElement.Clone() : null,
        entity.CallerKind,
        entity.CallerId,
        entity.Source,
        entity.RequestId,
        entity.Reason,
        entity.WorkflowDefinitionId,
        entity.InstanceId,
        entity.NodeExecutionId,
        entity.SourceActionId,
        entity.CreatedAt);

    private static SharedVariableWakeRecord MapWake(SharedVariableWakeEntity entity) => new(
        entity.Id,
        entity.SharedVariableId,
        entity.SharedVariable?.Key ?? string.Empty,
        entity.RevisionId,
        entity.Revision,
        entity.Status,
        entity.LeaseToken!.Value,
        entity.LeaseGeneration,
        entity.LeaseExpiresAt!.Value,
        entity.AttemptCount,
        entity.MaxAttempts,
        entity.ExpansionCursorTokenId);

    private static SharedVariableWakeDeliveryRecord MapDelivery(SharedVariableWakeDeliveryEntity entity) => new(
        entity.Id,
        entity.WakeId,
        entity.Wake.SharedVariableId,
        entity.Wake.SharedVariable?.Key ?? string.Empty,
        entity.Wake.Revision,
        entity.InstanceId,
        entity.WorkflowDefinitionId,
        entity.TokenId,
        entity.ActivationId,
        entity.NodeId,
        entity.Status,
        entity.LeaseToken!.Value,
        entity.LeaseGeneration,
        entity.LeaseExpiresAt!.Value,
        entity.AttemptCount,
        entity.MaxAttempts);

    private static SharedVariableWakeIncidentRecord MapIncident(
        SharedVariableWakeIncidentEntity entity) => new(
        entity.Id,
        entity.WorkKind,
        entity.WakeId,
        entity.DeliveryId,
        entity.OriginalWakeId,
        entity.OriginalDeliveryId,
        entity.SharedVariableId,
        entity.SharedKey,
        entity.Revision,
        entity.InstanceId,
        entity.WorkflowDefinitionId,
        entity.TokenId,
        entity.ActivationId,
        entity.NodeId,
        entity.Type,
        entity.Status,
        entity.Summary,
        entity.Details,
        entity.ResolutionReason,
        entity.ResolvedBy,
        entity.CreatedAt,
        entity.UpdatedAt,
        entity.ResolvedAt);
}
