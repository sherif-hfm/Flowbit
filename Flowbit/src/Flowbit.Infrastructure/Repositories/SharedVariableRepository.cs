using System.Security.Cryptography;
using System.Text;
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
        var normalized = keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key, StringComparer.Ordinal)
            .ToArray();
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

    private async Task<IReadOnlyDictionary<string, SharedVariableCurrentValueRecord>> LoadCurrentCoreAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var normalized = keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key, StringComparer.Ordinal)
            .ToArray();
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
            EnqueueWake(variable, revision, now);
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

        if (current is null)
        {
            current = new SharedVariableCurrentValueEntity { SharedVariableId = variable.Id };
            variable.CurrentValue = current;
            dbContext.SharedVariableCurrentValues.Add(current);
        }
        current.SourceRevisionId = revision.Id;
        current.Revision = revisionNumber;
        current.ValueJson = command.DeleteValue ? null : ToDocument(command.Value!.Value);
        current.IsDeleted = command.DeleteValue;
        current.SetAt = now;

        variable.CurrentRevision = revisionNumber;
        variable.UpdatedByKind = command.Caller.Kind;
        variable.UpdatedById = command.Caller.Id;
        variable.UpdatedAt = now;
        if (command.Description is not null)
        {
            variable.Description = TrimToNull(command.Description);
        }
        if (valueChanged)
        {
            EnqueueWake(variable, revision, now);
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
                && wake.Status != SharedVariableWakeStatuses.Cancelled)
            .LongCountAsync(cancellationToken);

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
            reasons.Add($"{pendingWakes} shared-variable wake(s) are incomplete");
        return new SharedVariableLifecycleBlockersRecord(
            publishedDefinitions,
            runningInstances,
            openJobs,
            activeConditionalWaits,
            pendingWakes,
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
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key, StringComparer.Ordinal)
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
        List<SharedVariableWakeEntity> wakes;
        if (IsNpgsql())
        {
            wakes = await dbContext.SharedVariableWakes.FromSqlInterpolated($$"""
                SELECT *
                FROM flowbit.shared_variable_wakes
                WHERE (("Status" = 'pending' AND "AvailableAt" <= {{request.Now}})
                       OR ("Status" = 'leased' AND "LeaseExpiresAt" <= {{request.Now}}))
                ORDER BY "Revision", "Id"
                LIMIT {{request.MaxCount}}
                FOR UPDATE SKIP LOCKED
                """).Include(wake => wake.SharedVariable).ToListAsync(cancellationToken);
        }
        else
        {
            wakes = await dbContext.SharedVariableWakes
                .Include(wake => wake.SharedVariable)
                .Where(wake => (wake.Status == SharedVariableWakeStatuses.Pending && wake.AvailableAt <= request.Now)
                    || (wake.Status == SharedVariableWakeStatuses.Leased && wake.LeaseExpiresAt <= request.Now))
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
            wake.LeaseExpiresAt = request.Now + request.LeaseDuration;
            wake.AttemptCount++;
            wake.UpdatedAt = request.Now;
            leased.Add(MapWake(wake));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return leased;
    }

    public async Task ExpandWakeAsync(
        SharedVariableWakeFence fence,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var wake = await LockWakeAsync(fence, cancellationToken);
        const int expansionPageSize = 500;
        long afterTokenId = 0;
        while (true)
        {
            // Keyset paging keeps fan-out memory bounded even when a key is
            // consumed by many workflow families. The unique wake/token fence
            // and the NOT EXISTS predicate make lease retries idempotent.
            var candidates = await (
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
                          && token.Id > afterTokenId
                          && !dbContext.SharedVariableWakeDeliveries.Any(delivery =>
                              delivery.WakeId == wake.Id
                              && delivery.TokenId == token.Id
                              && delivery.ActivationId == token.ActivationId)
                    orderby token.Id
                    select new
                    {
                        token.InstanceId,
                        instance.WorkflowDefinitionId,
                        TokenId = token.Id,
                        token.ActivationId,
                        token.NodeId
                    })
                .Distinct()
                .Take(expansionPageSize)
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0)
            {
                break;
            }

            var now = UtcNow();
            var deliveries = candidates.Select(candidate =>
                new SharedVariableWakeDeliveryEntity
                {
                    WakeId = wake.Id,
                    InstanceId = candidate.InstanceId,
                    WorkflowDefinitionId = candidate.WorkflowDefinitionId,
                    TokenId = candidate.TokenId,
                    ActivationId = candidate.ActivationId,
                    NodeId = candidate.NodeId,
                    Status = SharedVariableWakeStatuses.Pending,
                    AvailableAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                }).ToArray();
            dbContext.SharedVariableWakeDeliveries.AddRange(deliveries);
            await dbContext.SaveChangesAsync(cancellationToken);
            foreach (var delivery in deliveries)
            {
                dbContext.Entry(delivery).State = EntityState.Detached;
            }
            afterTokenId = candidates[^1].TokenId;
        }
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
    }

    public Task CompleteWakeExpansionAsync(
        SharedVariableWakeFence fence,
        string? error,
        CancellationToken cancellationToken) =>
        CompleteWakeAsync(fence, error, cancellationToken);

    public async Task<IReadOnlyList<SharedVariableWakeDeliveryRecord>> LeaseWakeDeliveriesAsync(
        SharedVariableWakeLeaseRequest request,
        CancellationToken cancellationToken)
    {
        ValidateLeaseRequest(request);
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        List<SharedVariableWakeDeliveryEntity> deliveries;
        if (IsNpgsql())
        {
            deliveries = await dbContext.SharedVariableWakeDeliveries.FromSqlInterpolated($$"""
                SELECT delivery.*
                FROM flowbit.shared_variable_wake_deliveries AS delivery
                JOIN flowbit.shared_variable_wakes AS wake ON wake."Id" = delivery."WakeId"
                WHERE ((delivery."Status" = 'pending' AND delivery."AvailableAt" <= {{request.Now}})
                       OR (delivery."Status" = 'leased' AND delivery."LeaseExpiresAt" <= {{request.Now}}))
                  AND NOT EXISTS (
                      SELECT 1
                      FROM flowbit.shared_variable_wake_deliveries AS earlier
                      JOIN flowbit.shared_variable_wakes AS earlier_wake
                        ON earlier_wake."Id" = earlier."WakeId"
                      WHERE earlier."TokenId" = delivery."TokenId"
                        AND earlier."ActivationId" = delivery."ActivationId"
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
                .Where(delivery => (delivery.Status == SharedVariableWakeStatuses.Pending && delivery.AvailableAt <= request.Now)
                    || (delivery.Status == SharedVariableWakeStatuses.Leased && delivery.LeaseExpiresAt <= request.Now))
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
            delivery.LeaseExpiresAt = request.Now + request.LeaseDuration;
            delivery.AttemptCount++;
            delivery.UpdatedAt = request.Now;
            leased.Add(MapDelivery(delivery));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return leased;
    }

    public async Task CompleteWakeDeliveryAsync(
        SharedVariableWakeFence fence,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var delivery = await LockDeliveryAsync(fence, cancellationToken);
        var now = UtcNow();
        var exhausted = error is not null && delivery.AttemptCount >= 25;
        delivery.Status = error is null
            ? SharedVariableWakeStatuses.Completed
            : exhausted
                ? SharedVariableWakeStatuses.Failed
                : SharedVariableWakeStatuses.Pending;
        delivery.LastError = BoundError(error);
        delivery.LeaseToken = null;
        delivery.LeasedBy = null;
        delivery.LeaseExpiresAt = null;
        delivery.UpdatedAt = now;
        delivery.AvailableAt = error is null || exhausted
            ? now
            : RetryAt(now, delivery.AttemptCount);
        delivery.CompletedAt = error is null ? now : null;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
    }

    private async Task CompleteWakeAsync(
        SharedVariableWakeFence fence,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var wake = await LockWakeAsync(fence, cancellationToken);
        var now = UtcNow();
        var exhausted = error is not null && wake.AttemptCount >= 25;
        wake.Status = error is null
            ? SharedVariableWakeStatuses.Completed
            : exhausted
                ? SharedVariableWakeStatuses.Failed
                : SharedVariableWakeStatuses.Pending;
        wake.LastError = BoundError(error);
        wake.LeaseToken = null;
        wake.LeasedBy = null;
        wake.LeaseExpiresAt = null;
        wake.UpdatedAt = now;
        wake.AvailableAt = error is null || exhausted
            ? now
            : RetryAt(now, wake.AttemptCount);
        wake.CompletedAt = error is null ? now : null;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
    }

    private async Task<SharedVariableWakeEntity> LockWakeAsync(
        SharedVariableWakeFence fence,
        CancellationToken cancellationToken)
    {
        var wake = IsNpgsql()
            ? await dbContext.SharedVariableWakes.FromSqlInterpolated(
                    $"""SELECT * FROM flowbit.shared_variable_wakes WHERE "Id" = {fence.Id} FOR UPDATE""")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.SharedVariableWakes.SingleOrDefaultAsync(item => item.Id == fence.Id, cancellationToken);
        if (wake is null
            || wake.Status != SharedVariableWakeStatuses.Leased
            || wake.LeaseToken != fence.LeaseToken
            || wake.LeaseGeneration != fence.LeaseGeneration)
        {
            throw new WorkflowConflictException("The shared-variable wake lease is stale.");
        }
        return wake;
    }

    private async Task<SharedVariableWakeDeliveryEntity> LockDeliveryAsync(
        SharedVariableWakeFence fence,
        CancellationToken cancellationToken)
    {
        var delivery = IsNpgsql()
            ? await dbContext.SharedVariableWakeDeliveries.FromSqlInterpolated(
                    $"""SELECT * FROM flowbit.shared_variable_wake_deliveries WHERE "Id" = {fence.Id} FOR UPDATE""")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.SharedVariableWakeDeliveries.SingleOrDefaultAsync(item => item.Id == fence.Id, cancellationToken);
        if (delivery is null
            || delivery.Status != SharedVariableWakeStatuses.Leased
            || delivery.LeaseToken != fence.LeaseToken
            || delivery.LeaseGeneration != fence.LeaseGeneration)
        {
            throw new WorkflowConflictException("The shared-variable wake-delivery lease is stale.");
        }
        return delivery;
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
        var state = IsNpgsql()
            ? await dbContext.SharedVariableRevisionStates.FromSqlRaw(
                    "SELECT * FROM flowbit.shared_variable_revision_state WHERE \"Id\" = 1 FOR UPDATE")
                .SingleAsync(cancellationToken)
            : await dbContext.SharedVariableRevisionStates.SingleAsync(item => item.Id == 1, cancellationToken);
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
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

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
        if (request.LeaseDuration <= TimeSpan.Zero || request.LeaseDuration > TimeSpan.FromMinutes(30))
            throw new WorkflowDomainException("Shared-variable wake lease duration must be between zero and 30 minutes.");
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
            entity.ArchivedAt);
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
        entity.AttemptCount);

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
        entity.AttemptCount);
}
