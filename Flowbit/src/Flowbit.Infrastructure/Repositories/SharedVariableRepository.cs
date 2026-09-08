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
        await dbContext.SaveChangesAsync(cancellationToken);

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
        var reasons = new List<string>();
        if (publishedDefinitions > 0)
            reasons.Add($"{publishedDefinitions} published workflow definition(s) bind the key");
        if (runningInstances > 0)
            reasons.Add($"{runningInstances} running workflow instance(s) bind the key");
        if (openJobs > 0)
            reasons.Add($"{openJobs} open workflow job(s) bind the key");
        return new SharedVariableLifecycleBlockersRecord(
            publishedDefinitions,
            runningInstances,
            openJobs,
            reasons);
    }

    public async Task ReplaceDefinitionBindingsAsync(
        long workflowDefinitionId,
        IReadOnlyCollection<SharedVariableDefinitionBindingProjection> bindings,
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
        await dbContext.SaveChangesAsync(cancellationToken);
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

    private async Task<IDbContextTransaction?> BeginOwnedTransactionAsync(
        CancellationToken cancellationToken) =>
        dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

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
            entity.ValueRevision,
            entity.HistoryPrunedAt);
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

}
