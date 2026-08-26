using System.Collections.Concurrent;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Bounded FIFO cache for immutable definition access plans. Lazy creation
/// makes concurrent first use analyze a definition only once per API replica.
/// </summary>
public sealed class SharedVariableAccessPlanCache(
    IConditionalEventDefinitionAnalyzer conditionalAnalyzer)
    : ISharedVariableAccessPlanCache
{
    public const int MaximumEntries = 512;

    private readonly ConcurrentDictionary<long, CacheEntry> entries = new();
    private readonly ConcurrentQueue<(long Id, CacheEntry Entry)> insertionOrder = new();

    public SharedVariableAccessPlan GetOrAdd(
        long workflowDefinitionId,
        WorkflowModel definition)
    {
        if (workflowDefinitionId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workflowDefinitionId),
                "Workflow definition id must be positive.");
        }
        ArgumentNullException.ThrowIfNull(definition);

        while (true)
        {
            if (entries.TryGetValue(workflowDefinitionId, out var existing))
            {
                return existing.Plan.Value;
            }

            var added = new CacheEntry(new Lazy<SharedVariableAccessPlan>(
                () => BuildRuntimeSafePlan(workflowDefinitionId, definition),
                LazyThreadSafetyMode.ExecutionAndPublication));
            if (!entries.TryAdd(workflowDefinitionId, added))
            {
                continue;
            }

            insertionOrder.Enqueue((workflowDefinitionId, added));
            Trim();
            try
            {
                return added.Plan.Value;
            }
            catch
            {
                ((ICollection<KeyValuePair<long, CacheEntry>>)entries).Remove(
                    new KeyValuePair<long, CacheEntry>(workflowDefinitionId, added));
                throw;
            }
        }
    }

    public bool TryGet(
        long workflowDefinitionId,
        out SharedVariableAccessPlan plan)
    {
        if (entries.TryGetValue(workflowDefinitionId, out var cached))
        {
            plan = cached.Plan.Value;
            return true;
        }

        plan = SharedVariableAccessPlan.Empty;
        return false;
    }

    public void Remove(long workflowDefinitionId) =>
        entries.TryRemove(workflowDefinitionId, out _);

    private SharedVariableAccessPlan BuildRuntimeSafePlan(
        long workflowDefinitionId,
        WorkflowModel definition)
    {
        var conditionalPlan = conditionalAnalyzer.Analyze(definition);
        var plan = SharedVariableAccessPlanner.Build(definition, conditionalPlan);
        try
        {
            SharedVariableTransactionLockOrderValidator.Validate(
                definition,
                plan,
                conditionalPlan);
        }
        catch (WorkflowDomainException ex)
        {
            throw new WorkflowConflictException(
                $"Workflow definition #{workflowDefinitionId} cannot execute safely: {ex.Message}");
        }
        return plan;
    }

    private void Trim()
    {
        while (entries.Count > MaximumEntries
            && insertionOrder.TryDequeue(out var oldest))
        {
            ((ICollection<KeyValuePair<long, CacheEntry>>)entries).Remove(
                new KeyValuePair<long, CacheEntry>(oldest.Id, oldest.Entry));
        }
    }

    private sealed record CacheEntry(Lazy<SharedVariableAccessPlan> Plan);
}
