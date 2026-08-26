using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Proves that every newly-acquired shared catalog row in one workflow
/// transaction is acquired in canonical ordinal-key order. The analysis mirrors
/// the engine's FIFO pass-through queue and deliberately over-approximates merge
/// enabling and interrupt cancellation; conservative rejection is safer than a
/// database deadlock after an external activity has run.
/// </summary>
public static class SharedVariableTransactionLockOrderValidator
{
    private const int MaximumAnalysisStates = 100_000;
    private const int MaximumQueuedTokens = 512;
    private const int MaximumSubsetWidth = 12;

    public static void Validate(
        WorkflowModel definition,
        SharedVariableAccessPlan accessPlan,
        ConditionalEventDependencyPlan conditionalPlan)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(accessPlan);
        ArgumentNullException.ThrowIfNull(conditionalPlan);

        var variables = definition.Variables ?? [];
        var flowNodes = definition.FlowNodes ?? [];
        var sequenceFlows = definition.SequenceFlows ?? [];
        var sharedByAlias = variables
            .Where(variable => variable is not null
                && string.Equals(variable.Scope, VariableScopes.Shared, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(variable.Name)
                && !string.IsNullOrWhiteSpace(variable.SharedKey))
            .ToDictionary(variable => variable.Name, StringComparer.OrdinalIgnoreCase);
        if (sharedByAlias.Count < 2)
        {
            return;
        }

        string[] KeysForAliases(IEnumerable<string> aliases) => aliases
            .Where(sharedByAlias.ContainsKey)
            .Select(alias => sharedByAlias[alias].SharedKey!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var nodeKeys = flowNodes.ToDictionary(
            node => node.Id,
            node => KeysForAliases(accessPlan.ForNode(node.Id).LockAliases));
        var flowKeys = sequenceFlows.ToDictionary(
            flow => flow.Id,
            flow => KeysForAliases(accessPlan.ForFlow(flow.Id).ProducerAliases));
        var nodes = flowNodes.ToDictionary(node => node.Id);
        var outgoing = nodes.Keys.ToDictionary(
            nodeId => nodeId,
            nodeId => sequenceFlows
                .Where(flow => flow.SourceRef == nodeId)
                .OrderBy(flow => flow.Id)
                .ToArray());
        var variableByAlias = variables
            .Where(variable => variable is not null && !string.IsNullOrWhiteSpace(variable.Name))
            .ToDictionary(variable => variable.Name, StringComparer.OrdinalIgnoreCase);
        var boundariesByHost = flowNodes
            .Where(node => BpmnFlowNodeTypes.IsErrorBoundary(node.Type)
                && node.AttachedToRef is not null)
            .GroupBy(node => node.AttachedToRef!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(node => node.Id).ToArray());

        PostWritePlan BuildPostWritePlan(IEnumerable<string?> authoredTargets)
        {
            var affectedNodeIds = new SortedSet<int>();
            foreach (var alias in authoredTargets
                         .Where(alias => !string.IsNullOrWhiteSpace(alias))
                         .Select(alias => alias!.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // WorkflowVariableStore deliberately fans shared writes out via
                // the durable catalog outbox. Only instance writes participate
                // in the same-instance mutation tracker and inline drain.
                if (variableByAlias.TryGetValue(alias, out var target)
                    && string.Equals(target.Scope, VariableScopes.Shared, StringComparison.Ordinal))
                {
                    continue;
                }
                if (conditionalPlan.NodeIdsByVariable.TryGetValue(alias, out var conditionalNodeIds))
                {
                    affectedNodeIds.UnionWith(conditionalNodeIds);
                }
            }

            var keys = affectedNodeIds
                .SelectMany(nodeId => accessPlan.ForNode(nodeId).ConditionalDependencyAliases)
                .Where(sharedByAlias.ContainsKey)
                .Select(alias => sharedByAlias[alias].SharedKey!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var atomicTargets = affectedNodeIds
                .Where(nodeId => conditionalPlan.EventsByNodeId.TryGetValue(nodeId, out var entry)
                    && entry.DeliveryMode == ConditionalEventDeliveryModes.Atomic)
                .SelectMany(nodeId => outgoing.GetValueOrDefault(nodeId, []))
                .Select(flow => flow.TargetRef)
                .Distinct()
                .Order()
                .ToArray();
            return new PostWritePlan(keys, atomicTargets);
        }

        IEnumerable<string?> NodeWriteTargets(FlowNodeModel node)
        {
            if (BpmnFlowNodeTypes.IsEntry(node.Type))
            {
                // Process initialization writes only authored defaults and the
                // nullable-null materialization. Treating every declaration as
                // written would invent locks that can mask a real later
                // descending acquisition in the same simulated transaction.
                foreach (var variable in variables.Where(variable =>
                             !string.Equals(variable.Scope, VariableScopes.Shared, StringComparison.Ordinal)
                             && (variable.DefaultValue is not null || variable.Nullable)))
                {
                    yield return variable.Name;
                }
                foreach (var variable in node.Variables ?? []) yield return variable?.Name;
                foreach (var mapping in node.Message?.OutputMappings ?? []) yield return mapping?.Variable;
            }
            if (BpmnFlowNodeTypes.IsServiceTask(node.Type))
            {
                foreach (var mapping in node.Service?.OutputMappings ?? []) yield return mapping?.Variable;
                yield return node.Service?.StatusVariable;
            }
            if (BpmnFlowNodeTypes.IsScriptTask(node.Type))
            {
                if (string.Equals(node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal))
                {
                    foreach (var variable in variables) yield return variable.Name;
                }
                else
                {
                    foreach (var assignment in node.Assignments ?? []) yield return assignment?.Variable;
                }
            }
        }

        IEnumerable<string?> ActionWriteTargets(FlowNodeModel node, SequenceFlowModel flow)
        {
            foreach (var variable in flow.Variables ?? []) yield return variable?.Name;
            if (BpmnFlowNodeTypes.IsUserTask(node.Type))
            {
                // Retain node-declared user input compatibility in addition to
                // canonical sequence-flow action values. Older normalized
                // definitions and administrative paths can still submit them
                // in the same externally-triggered transaction.
                foreach (var variable in node.Variables ?? []) yield return variable?.Name;
            }
            if (BpmnFlowNodeTypes.IsMessageCatch(node.Type))
            {
                foreach (var mapping in node.Message?.OutputMappings ?? []) yield return mapping?.Variable;
            }
            if (BpmnFlowNodeTypes.IsUserTask(node.Type))
            {
                yield return node.MultiInstance?.ResultVariable;
            }
        }

        var nodePostWrites = flowNodes.ToDictionary(
            node => node.Id,
            node => BuildPostWritePlan(NodeWriteTargets(node)));
        var boundaryPostWrites = flowNodes
            .Where(node => BpmnFlowNodeTypes.IsErrorBoundary(node.Type))
            .ToDictionary(
                boundary => boundary.Id,
                boundary => BuildPostWritePlan([boundary.ErrorVariable]));
        var actionPostWrites = sequenceFlows.ToDictionary(
            flow => flow.Id,
            flow => BuildPostWritePlan(ActionWriteTargets(nodes[flow.SourceRef], flow)));

        var scenarios = new List<SimulationState>();
        foreach (var entry in flowNodes.Where(node => BpmnFlowNodeTypes.IsEntry(node.Type)))
        {
            scenarios.Add(SimulationState.Start(
                $"entry node #{entry.Id}",
                new Cursor(entry.Id)));
        }
        foreach (var node in flowNodes.Where(RequiresPreNodeDurableBoundary))
        {
            scenarios.Add(SimulationState.Start(
                $"durable pre-node finalization for node #{node.Id}",
                new Cursor(node.Id, BypassAsyncBefore: true)));
        }
        foreach (var node in flowNodes.Where(node => node.AsyncAfter))
        {
            scenarios.Add(SimulationState.Start(
                $"asyncAfter finalization for node #{node.Id}",
                new Cursor(
                    node.Id,
                    BypassAsyncBefore: true,
                    BypassAsyncAfter: true,
                    SkipPostWrites: true)));
        }

        foreach (var node in flowNodes.Where(IsExternalRestingOrBoundary))
        {
            foreach (var flow in outgoing[node.Id])
            {
                var label = $"external continuation from node #{node.Id} through sequence flow #{flow.Id}";
                var baseState = SimulationState.Empty(label);
                Acquire(
                    baseState,
                    nodeKeys[node.Id].Concat(flowKeys[flow.Id]),
                    $"node #{node.Id} / sequence flow #{flow.Id} continuation");
                var post = actionPostWrites[flow.Id];
                Acquire(
                    baseState,
                    post.LockKeys,
                    $"conditional dependency reload after sequence flow #{flow.Id} writes");

                var mainTargetInActionTransaction = !node.AsyncAfter;
                foreach (var triggered in Subsets(post.AtomicTargets, includeEmpty: true))
                {
                    var state = baseState.Clone();
                    if (mainTargetInActionTransaction)
                    {
                        // The action moves its own token before entering
                        // ResolvePassThroughAsync. Its target is therefore at
                        // the head of the new queue; the initial mutation drain
                        // appends released conditional siblings behind it.
                        state.Queue.Add(new Cursor(flow.TargetRef));
                    }
                    state.Queue.AddRange(triggered.Select(target => new Cursor(target)));
                    scenarios.Add(state);
                }

                if (node.AsyncAfter)
                {
                    var finalize = SimulationState.Empty(
                        $"asyncAfter continuation from node #{node.Id} through sequence flow #{flow.Id}");
                    Acquire(
                        finalize,
                        nodeKeys[node.Id].Concat(flowKeys[flow.Id]),
                        $"node #{node.Id} / sequence flow #{flow.Id} asyncAfter continuation");
                    finalize.Queue.Add(new Cursor(flow.TargetRef));
                    scenarios.Add(finalize);
                }
            }
        }

        // Administrative instance-variable updates may change any subset of
        // local dependencies in one request. The coordinator reloads the union
        // of affected shared dependencies first, then advances true atomic
        // catches in node-id order, retaining those locks across every resumed
        // continuation. Singles plus every pair prove both dependency-to-route
        // and route-to-later-route ordering without exponential subset growth.
        var conditionalEvents = conditionalPlan.EventsByNodeId.Values
            .OrderBy(item => item.NodeId)
            .ToArray();
        for (var first = 0; first < conditionalEvents.Length; first++)
        {
            AddAdministrativeConditionalScenario([conditionalEvents[first]]);
            for (var second = first + 1; second < conditionalEvents.Length; second++)
            {
                AddAdministrativeConditionalScenario(
                    [conditionalEvents[first], conditionalEvents[second]]);
            }
        }

        void AddAdministrativeConditionalScenario(
            IReadOnlyList<ConditionalEventPlanEntry> affected)
        {
            var description = "administrative conditional wave for node(s) "
                + string.Join(", ", affected.Select(item => $"#{item.NodeId}"));
            var state = SimulationState.Empty(description);
            Acquire(
                state,
                affected
                    .SelectMany(item => accessPlan.ForNode(item.NodeId)
                        .ConditionalDependencyAliases)
                    .Where(sharedByAlias.ContainsKey)
                    .Select(alias => sharedByAlias[alias].SharedKey!),
                "shared conditional dependency reload");
            foreach (var item in affected.Where(item =>
                         item.DeliveryMode == ConditionalEventDeliveryModes.Atomic))
            {
                state.Queue.Add(new Cursor(outgoing[item.NodeId].Single().TargetRef));
            }
            scenarios.Add(state);
        }

        var analyzedStates = 0;
        foreach (var scenario in scenarios)
        {
            AnalyzeScenario(
                scenario,
                nodes,
                outgoing,
                nodeKeys,
                nodePostWrites,
                boundaryPostWrites,
                boundariesByHost,
                ref analyzedStates);
        }
    }

    private static void AnalyzeScenario(
        SimulationState initial,
        IReadOnlyDictionary<int, FlowNodeModel> nodes,
        IReadOnlyDictionary<int, SequenceFlowModel[]> outgoing,
        IReadOnlyDictionary<int, string[]> nodeKeys,
        IReadOnlyDictionary<int, PostWritePlan> nodePostWrites,
        IReadOnlyDictionary<int, PostWritePlan> boundaryPostWrites,
        IReadOnlyDictionary<int, FlowNodeModel[]> boundariesByHost,
        ref int analyzedStates)
    {
        var pending = new Stack<SimulationState>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(initial);
        while (pending.Count > 0)
        {
            var state = pending.Pop();
            if (!visited.Add(state.Signature()))
            {
                continue;
            }
            analyzedStates++;
            if (analyzedStates > MaximumAnalysisStates)
            {
                throw new WorkflowDomainException(
                    "Shared-variable lock-order analysis exceeded its bounded state limit. "
                    + "Add asyncBefore or a resting/durable boundary to split complex synchronous routing.");
            }
            if (state.Queue.Count == 0)
            {
                continue;
            }
            if (state.Queue.Count > MaximumQueuedTokens)
            {
                throw new WorkflowDomainException(
                    "Shared-variable lock-order analysis exceeded its queued-token limit. "
                    + "Add asyncBefore or a resting/durable boundary inside the fork/loop.");
            }

            var cursor = state.Queue[0];
            state.Queue.RemoveAt(0);
            var node = nodes[cursor.NodeId];
            if (RequiresPreNodeDurableBoundary(node) && !cursor.BypassAsyncBefore)
            {
                pending.Push(state);
                continue;
            }

            Acquire(state, nodeKeys[node.Id], $"node #{node.Id} ('{node.Name}')");
            var post = cursor.SkipPostWrites
                ? PostWritePlan.Empty
                : nodePostWrites[node.Id];
            Acquire(
                state,
                post.LockKeys,
                $"conditional dependency reload after node #{node.Id} writes");

            if (BpmnFlowNodeTypes.IsTerminateEnd(node.Type))
            {
                state.Queue.Clear();
                pending.Push(state);
                continue;
            }

            var routes = RouteChoices(
                node,
                cursor,
                outgoing[node.Id]);
            foreach (var triggered in Subsets(post.AtomicTargets, includeEmpty: true))
            {
                foreach (var route in routes)
                {
                    var next = state.Clone();
                    next.Queue.AddRange(triggered.Select(target => new Cursor(target)));
                    next.Queue.AddRange(route.Select(target => new Cursor(target)));
                    pending.Push(next);
                }
            }

            // A service/script failure drains its normal output/status batch
            // before it writes and drains the attached boundary error variable.
            // Keep those acquisitions as distinct phases: sorting their union
            // would incorrectly prove z-then-a safe as the synthetic order a,z.
            if (BpmnFlowNodeTypes.IsServiceTask(node.Type)
                || BpmnFlowNodeTypes.IsScriptTask(node.Type))
            {
                foreach (var boundary in boundariesByHost.GetValueOrDefault(node.Id, []))
                {
                    var failure = state.Clone();
                    var boundaryPost = boundaryPostWrites[boundary.Id];
                    Acquire(
                        failure,
                        boundaryPost.LockKeys,
                        $"conditional dependency reload after error boundary #{boundary.Id} writes");
                    foreach (var primaryTriggered in Subsets(
                                 post.AtomicTargets,
                                 includeEmpty: true))
                    {
                        foreach (var boundaryTriggered in Subsets(
                                     boundaryPost.AtomicTargets,
                                     includeEmpty: true))
                        {
                            var next = failure.Clone();
                            next.Queue.AddRange(primaryTriggered.Select(target => new Cursor(target)));
                            next.Queue.AddRange(boundaryTriggered.Select(target => new Cursor(target)));
                            next.Queue.Add(new Cursor(boundary.Id));
                            pending.Push(next);
                        }
                    }
                }
            }
        }
    }

    private static IReadOnlyList<int[]> RouteChoices(
        FlowNodeModel node,
        Cursor cursor,
        IReadOnlyList<SequenceFlowModel> outgoing)
    {
        if (BpmnFlowNodeTypes.IsEnd(node.Type))
        {
            return [[]];
        }
        if (BpmnFlowNodeTypes.IsConditionalCatch(node.Type))
        {
            if (node.Conditional?.EffectiveDeliveryMode == ConditionalEventDeliveryModes.Atomic)
            {
                return [[], [outgoing.Single().TargetRef]];
            }
            return [[]];
        }
        if (IsExternalResting(node) || node.AsyncAfter && !cursor.BypassAsyncAfter)
        {
            return [[]];
        }

        var choices = new List<int[]>();
        if (BpmnFlowNodeTypes.IsGateway(node.Type))
        {
            if (outgoing.Count <= 1)
            {
                // Merge enabling depends on other queued tokens. Both choices
                // over-approximate "wait" and "this arrival releases".
                choices.Add([]);
                if (outgoing.Count == 1) choices.Add([outgoing[0].TargetRef]);
                return choices;
            }
            if (BpmnFlowNodeTypes.IsParallelGateway(node.Type))
            {
                return [outgoing.OrderBy(flow => flow.Id).Select(flow => flow.TargetRef).ToArray()];
            }
            if (BpmnFlowNodeTypes.IsInclusiveGateway(node.Type)
                || BpmnFlowNodeTypes.IsComplexGateway(node.Type))
            {
                return Subsets(
                        outgoing.OrderBy(flow => flow.Id).Select(flow => flow.TargetRef).ToArray(),
                        includeEmpty: false)
                    .ToArray();
            }
            return outgoing.Select(flow => new[] { flow.TargetRef }).ToArray();
        }

        choices.AddRange(outgoing.Select(flow => new[] { flow.TargetRef }));
        return choices.Count == 0 ? [[]] : choices;
    }

    private static bool IsExternalRestingOrBoundary(FlowNodeModel node) =>
        IsExternalResting(node) || BpmnFlowNodeTypes.IsTimerBoundary(node.Type);

    private static bool RequiresPreNodeDurableBoundary(FlowNodeModel node) =>
        node.AsyncBefore
        || node.AsyncAfter
           && (BpmnFlowNodeTypes.IsServiceTask(node.Type)
               || BpmnFlowNodeTypes.IsScriptTask(node.Type));

    private static bool IsExternalResting(FlowNodeModel node) =>
        BpmnFlowNodeTypes.IsUserTask(node.Type)
        || BpmnFlowNodeTypes.IsMessageCatch(node.Type)
        || BpmnFlowNodeTypes.IsTimerCatch(node.Type)
        || BpmnFlowNodeTypes.IsConditionalCatch(node.Type);

    private static IEnumerable<int[]> Subsets(
        IReadOnlyList<int> values,
        bool includeEmpty)
    {
        if (values.Count > MaximumSubsetWidth)
        {
            throw new WorkflowDomainException(
                $"Shared-variable lock-order analysis cannot safely enumerate {values.Count} "
                + "same-transaction branch/conditional combinations. Add durable boundaries to reduce the fan-out.");
        }
        var start = includeEmpty ? 0 : 1;
        var count = 1 << values.Count;
        for (var mask = start; mask < count; mask++)
        {
            var subset = new List<int>(values.Count);
            for (var index = 0; index < values.Count; index++)
            {
                if ((mask & (1 << index)) != 0) subset.Add(values[index]);
            }
            yield return subset.ToArray();
        }
    }

    private static void Acquire(
        SimulationState state,
        IEnumerable<string> keys,
        string location)
    {
        foreach (var key in keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (state.HeldKeys.Contains(key))
            {
                continue;
            }
            if (state.MaximumKey is not null
                && StringComparer.Ordinal.Compare(key, state.MaximumKey) < 0)
            {
                throw new WorkflowDomainException(
                    "Shared-variable lock order is unsafe in one synchronous transaction "
                    + $"starting at {state.EntryDescription}: {location} may newly acquire "
                    + $"catalog key '{key}' after key '{state.MaximumKey}' was acquired at "
                    + $"{state.MaximumKeyLocation}. Newly acquired shared keys must be monotonic "
                    + "in ordinal order. Add asyncBefore=true before the later acquisition, or "
                    + "insert a true resting/durable boundary.");
            }
            state.HeldKeys.Add(key);
            state.MaximumKey = key;
            state.MaximumKeyLocation = location;
        }
    }

    private sealed record Cursor(
        int NodeId,
        bool BypassAsyncBefore = false,
        bool BypassAsyncAfter = false,
        bool SkipPostWrites = false);

    private sealed record PostWritePlan(
        IReadOnlyList<string> LockKeys,
        IReadOnlyList<int> AtomicTargets)
    {
        public static PostWritePlan Empty { get; } = new([], []);
    }

    private sealed class SimulationState
    {
        private SimulationState(string entryDescription)
        {
            EntryDescription = entryDescription;
        }

        public string EntryDescription { get; }
        public List<Cursor> Queue { get; } = [];
        public HashSet<string> HeldKeys { get; } = new(StringComparer.Ordinal);
        public string? MaximumKey { get; set; }
        public string? MaximumKeyLocation { get; set; }

        public static SimulationState Empty(string entryDescription) => new(entryDescription);

        public static SimulationState Start(string entryDescription, Cursor cursor)
        {
            var state = new SimulationState(entryDescription);
            state.Queue.Add(cursor);
            return state;
        }

        public SimulationState Clone()
        {
            var clone = new SimulationState(EntryDescription)
            {
                MaximumKey = MaximumKey,
                MaximumKeyLocation = MaximumKeyLocation
            };
            clone.Queue.AddRange(Queue);
            clone.HeldKeys.UnionWith(HeldKeys);
            return clone;
        }

        public string Signature() =>
            string.Join(',', Queue.Select(cursor =>
                $"{cursor.NodeId}:{cursor.BypassAsyncBefore}:{cursor.BypassAsyncAfter}:{cursor.SkipPostWrites}"))
            + "|"
            + string.Join("\u001f", HeldKeys.Order(StringComparer.Ordinal));
    }
}
