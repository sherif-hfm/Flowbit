using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Coordinates mixed instance/shared writes in the caller's ambient unit of
/// work. The engine locks an instance before using this service; shared keys are
/// then processed in stable key order to preserve the global lock ordering.
/// </summary>
public sealed class WorkflowVariableStore(
    IWorkflowRuntimeRepository runtime,
    ISharedVariableRepository sharedVariables) : IWorkflowVariableStore
{
    public async Task<IReadOnlyDictionary<string, long>> LoadSharedRevisionsAsync(
        WorkflowModel definition,
        IReadOnlyCollection<string>? aliases,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        var allBindings = SharedBindings(definition);
        var bindings = aliases is null
            ? allBindings.Values.ToArray()
            : aliases
                .Where(allBindings.ContainsKey)
                .Select(alias => allBindings[alias])
                .DistinctBy(binding => binding.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var keys = bindings.Select(binding => binding.SharedKey!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (lockForUpdate)
        {
            await sharedVariables.PrelockDefinitionKeysForLegacyAllocatorAsync(
                SharedCatalogKeys(allBindings.Values),
                cancellationToken);
            _ = await sharedVariables.LockCurrentAsync(
                keys,
                includeArchived: true,
                cancellationToken);
        }

        var catalog = await sharedVariables.GetManyByKeyAsync(
            keys,
            includeArchived: true,
            cancellationToken);
        var revisions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            if (catalog.TryGetValue(binding.SharedKey!, out var variable))
            {
                revisions[binding.Name] = variable.Revision;
            }
        }
        return revisions;
    }

    public async Task<IReadOnlyDictionary<string, long>> LoadSharedValueVersionsAsync(
        WorkflowModel definition,
        IReadOnlyCollection<string> aliases,
        bool lockForUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        var bindings = SharedBindings(definition);
        var requested = aliases
            .Where(bindings.ContainsKey)
            .Select(alias => bindings[alias])
            .DistinctBy(binding => binding.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var keys = requested
            .Select(binding => binding.SharedKey!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (lockForUpdate && keys.Length > 0)
        {
            await sharedVariables.PrelockDefinitionKeysForLegacyAllocatorAsync(
                SharedCatalogKeys(bindings.Values),
                cancellationToken);
        }
        var stamps = lockForUpdate
            ? await sharedVariables.LockValueStampsAsync(
                keys,
                includeArchived: true,
                cancellationToken)
            : await sharedVariables.LoadValueStampsAsync(
                keys,
                includeArchived: true,
                cancellationToken);
        var versions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in requested)
        {
            if (stamps.TryGetValue(binding.SharedKey!, out var stamp))
            {
                versions[binding.Name] = stamp.ValueRevision;
            }
        }
        return versions;
    }

    public async Task<IReadOnlyList<SharedVariableBindingMetadataDto>> DescribeBindingsAsync(
        WorkflowModel definition,
        CancellationToken cancellationToken)
    {
        var bindings = SharedBindings(definition).Values
            .OrderBy(binding => binding.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var catalog = await sharedVariables.GetManyByKeyAsync(
            bindings.Select(binding => binding.SharedKey!).ToArray(),
            includeArchived: true,
            cancellationToken);
        var result = new List<SharedVariableBindingMetadataDto>(bindings.Length);
        foreach (var binding in bindings)
        {
            if (!catalog.TryGetValue(binding.SharedKey!, out var variable))
            {
                continue;
            }
            result.Add(new SharedVariableBindingMetadataDto(
                binding.Name,
                variable.Key,
                binding.Access!,
                variable.DataType,
                variable.IsArray,
                variable.Nullable,
                variable.Validation,
                variable.Status,
                variable.Revision,
                variable.HasValue,
                variable.CreatedAt,
                variable.UpdatedAt,
                variable.ArchivedAt,
                variable.ValueRevision));
        }
        return result;
    }

    public async Task<Dictionary<string, JsonElement>> MergeEffectiveValuesAsync(
        WorkflowModel definition,
        IReadOnlyDictionary<string, JsonElement> instanceValues,
        SharedVariableAccessScope? sharedAccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var bindings = SharedBindings(definition);
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in instanceValues)
        {
            // An old instance history row must never shadow a name that the
            // immutable current definition explicitly binds to the shared store.
            if (!bindings.ContainsKey(pair.Key))
            {
                result[pair.Key] = pair.Value.Clone();
            }
        }

        if (bindings.Count == 0)
        {
            return result;
        }

        var allKeys = bindings.Values
            .Select(binding => binding.SharedKey!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        IEnumerable<string> lockedAliases = (IEnumerable<string>?)sharedAccess?.LockAliases
            ?? Array.Empty<string>();
        var lockedKeys = lockedAliases
            .Where(bindings.ContainsKey)
            .Select(alias => bindings[alias].SharedKey!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        IReadOnlyDictionary<string, SharedVariableCurrentValueRecord> current;
        if (lockedKeys.Length > 0)
        {
            // During the legacy singleton-revision-allocator rollout, every
            // workflow transaction that will lock a shared row first takes the
            // immutable definition's complete key set in ordinal order. The
            // repository makes this a no-op after sequence cutover, restoring
            // the permanent exact node/flow locking plan automatically.
            await sharedVariables.PrelockDefinitionKeysForLegacyAllocatorAsync(
                allKeys,
                cancellationToken);
            var locked = await sharedVariables.LockCurrentAsync(
                lockedKeys,
                includeArchived: false,
                cancellationToken);
            var unlockedKeys = allKeys
                .Except(lockedKeys, StringComparer.Ordinal)
                .ToArray();
            var unlocked = await sharedVariables.LoadCurrentAsync(
                unlockedKeys,
                includeArchived: false,
                cancellationToken);
            var combined = locked.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in unlocked)
            {
                combined[pair.Key] = pair.Value;
            }
            current = combined;
        }
        else
        {
            current = await sharedVariables.LoadCurrentAsync(
                allKeys,
                includeArchived: false,
                cancellationToken);
        }

        foreach (var binding in bindings.Values)
        {
            if (current.TryGetValue(binding.SharedKey!, out var value))
            {
                result[binding.Name] = value.Value.Clone();
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<WorkflowVariableWriteResult>> WriteAsync(
        WorkflowModel definition,
        long workflowDefinitionId,
        long instanceId,
        IReadOnlyCollection<WorkflowVariableWrite> writes,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(actor);
        if (writes.Count == 0)
        {
            return [];
        }

        var bindings = SharedBindings(definition);
        var instanceWrites = new List<(int Index, WorkflowVariableWrite Write)>();
        var sharedWrites = new List<(int Index, WorkflowVariableWrite Write, VariableModel Binding)>();
        var index = 0;
        foreach (var write in writes)
        {
            if (string.IsNullOrWhiteSpace(write.Alias))
            {
                throw new WorkflowDomainException("A workflow variable write requires an alias.");
            }

            if (bindings.TryGetValue(write.Alias, out var binding))
            {
                if (binding.Access != Flowbit.Shared.Models.SharedVariableAccessModes.ReadWrite)
                {
                    throw new WorkflowDomainException(
                        $"Shared variable alias '{binding.Name}' is read-only in this workflow definition.");
                }
                EnsureValueAllowed(binding, write.Value);
                sharedWrites.Add((index, write, binding));
            }
            else
            {
                instanceWrites.Add((index, write));
            }
            index++;
        }

        var results = new WorkflowVariableWriteResult[writes.Count];
        foreach (var (writeIndex, write) in instanceWrites)
        {
            await runtime.AddVariableAsync(
                instanceId,
                write.Alias,
                write.SourceActionId,
                write.SetBy ?? actor.User,
                write.Value,
                cancellationToken,
                write.NodeExecutionId,
                write.ActingFor ?? actor.ActingFor,
                write.DelegationId ?? actor.DelegationId,
                write.InstanceVariableUpdateAuditId);
            results[writeIndex] = new WorkflowVariableWriteResult(
                write.Alias,
                VariableScopes.Instance,
                null,
                null,
                ValueChanged: true);
        }

        // Capture pre-write values once so no-op writes can avoid local
        // conditional reevaluation. The repository performs the authoritative
        // comparison and suppresses its durable wake expansion as well.
        if (sharedWrites.Count > 0)
        {
            await sharedVariables.PrelockDefinitionKeysForLegacyAllocatorAsync(
                SharedCatalogKeys(bindings.Values),
                cancellationToken);
            _ = await sharedVariables.LockCurrentAsync(
                sharedWrites.Select(item => item.Binding.SharedKey!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                includeArchived: false,
                cancellationToken);
        }

        foreach (var (writeIndex, write, binding) in sharedWrites
                     .OrderBy(item => item.Binding.SharedKey, StringComparer.Ordinal)
                     .ThenBy(item => item.Index))
        {
            var mutation = await sharedVariables.WriteAsync(
                new SharedVariableWriteCommand(
                    binding.SharedKey!,
                    write.Value.Clone(),
                    DeleteValue: false,
                    ExpectedRevision: null,
                    new SharedVariableCallerRecord(
                        SharedVariableCallerKinds.Workflow,
                        actor.User ?? "system",
                        actor.Roles,
                        []),
                    SharedVariableSources.Workflow,
                    Reason: write.Reason,
                    WorkflowDefinitionId: workflowDefinitionId,
                    InstanceId: instanceId,
                    NodeExecutionId: write.NodeExecutionId,
                    SourceActionId: write.SourceActionId),
                cancellationToken)
                ?? throw new WorkflowConflictException(
                    $"Shared variable key '{binding.SharedKey}' does not exist or is archived.");

            // Shared changes are fanned out only through the durable expansion
            // outbox. Treating the writer instance as a local mutation would
            // edge-latch truth synchronously and violate the catalog's
            // cross-workflow latest-state/coalescing semantics.
            var changed = mutation.Revision.ValueChanged;
            results[writeIndex] = new WorkflowVariableWriteResult(
                binding.Name,
                VariableScopes.Shared,
                binding.SharedKey,
                mutation.Variable.Revision,
                changed);
        }

        return results;
    }

    private static Dictionary<string, VariableModel> SharedBindings(WorkflowModel definition) =>
        (definition.Variables ?? [])
            .Where(variable => variable is not null
                && !string.IsNullOrWhiteSpace(variable.Name)
                && string.Equals(variable.Scope, VariableScopes.Shared, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(variable.SharedKey))
            .ToDictionary(variable => variable.Name, StringComparer.OrdinalIgnoreCase);

    private static string[] SharedCatalogKeys(IEnumerable<VariableModel> bindings) =>
        bindings
            .Select(binding => binding.SharedKey!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void EnsureValueAllowed(VariableModel binding, JsonElement value)
    {
        SharedVariableValueValidator.Validate(
            binding.SharedKey ?? binding.Name ?? "shared variable",
            binding.DataType,
            binding.IsArray,
            binding.Nullable,
            binding.Validation,
            value);
    }
}
