using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService
{
    internal IAutomaticMultiInstanceLifecycle CreateAutomaticMultiInstanceLifecycle(
        WorkflowInstanceRecord instance, WorkflowModel definition, ActorContext actor) =>
        new AutomaticBodyLifecycle(this, instance, definition, actor);

    private sealed class AutomaticBodyLifecycle(WorkflowEngineService engine,
        WorkflowInstanceRecord instance, WorkflowModel definition, ActorContext actor)
        : IAutomaticMultiInstanceLifecycle
    {
        private ActorContext Actor(AutomaticMultiInstanceRecord body) =>
            body.ActivityKind == BpmnFlowNodeTypes.CallActivity ? CallActivityActor() : actor;

        public Task InstallHostTimersAsync(AutomaticMultiInstanceRecord body, CancellationToken ct) =>
            engine.InstallAutomaticMultiInstanceTimersAsync(instance, body, definition, Actor(body), ct);

        public async Task DrainVariableNotificationsAsync(AutomaticMultiInstanceRecord body, CancellationToken ct) =>
            _ = await engine.DrainAutomaticMultiInstanceNotificationsAsync(instance, body, definition, Actor(body), ct);

        public Task CloseHostSubscriptionsAsync(AutomaticMultiInstanceRecord body, CancellationToken ct) =>
            engine.CloseAutomaticMultiInstanceBoundariesAsync(instance.Id, body.TokenId, ct);

        public Task AbandonItemCallsAsync(AutomaticMultiInstanceRecord body, CancellationToken ct) =>
            engine.AbandonAutomaticCallsForTokensAsync(instance.Id, [body.TokenId], ct);

        public Task ContinueAsync(AutomaticMultiInstanceRecord body, AutomaticMultiInstanceCompletion completion,
            CancellationToken ct) => engine.ContinueAutomaticMultiInstanceAsync(instance, body, definition, Actor(body), completion, ct);
    }

    private async Task InstallAutomaticMultiInstanceTimersAsync(WorkflowInstanceRecord instance,
        AutomaticMultiInstanceRecord body, WorkflowModel definition, ActorContext actor, CancellationToken ct)
    {
        var token = await runtime.GetExecutionTokenAsync(body.TokenId, false, ct)
            ?? throw new WorkflowConflictException("The automatic body's token no longer exists.");
        var values = await LoadInstanceVariablesAsync(instance.Id, ct);
        await ArmAutomaticMultiInstanceTimersAsync(instance, token, body, definition, actor, values, ct);
    }

    private async Task ContinueAutomaticMultiInstanceAsync(WorkflowInstanceRecord instance,
        AutomaticMultiInstanceRecord body, WorkflowModel definition, ActorContext actor,
        AutomaticMultiInstanceCompletion completion, CancellationToken cancellationToken)
    {
        var token = await runtime.GetExecutionTokenAsync(body.TokenId, false, cancellationToken);
        if (token is null || token.ActivationId != body.ActivationId || token.NodeId != body.NodeId
            || token.Status != ExecutionTokenRecordStatuses.Active || token.WaitState is not null
            || token.WaitingMultiInstanceExecutionId is not null)
            throw new WorkflowConflictException("The automatic completion no longer owns its continuation.");
        var node = GetFlowNode(definition, body.NodeId);
        var failureCode = completion.AggregateFailureCode ?? completion.ErrorRoute?.FailureCode;
        var boundary = completion.ErrorRoute is { BoundaryNodeId: int routeId }
            ? GetFlowNode(definition, routeId)
            : failureCode is not null ? ErrorBoundaryMatchRules.Select(definition, node.Id, null).Boundary : null;
        if (boundary is not null && (!BpmnFlowNodeTypes.IsErrorBoundary(boundary.Type)
                || boundary.AttachedToRef != node.Id))
            throw new WorkflowJobInvariantException("The saved automatic error route does not belong to its body.");
        if (failureCode is not null && boundary is null)
        {
            await runtime.UpdateExecutionTokenAsync(token.Id,
                ToSnapshot(node) with { FaultCode = failureCode, FaultDescription = failureCode },
                ExecutionTokenRecordStatuses.Faulted, token.GatewayBranchId, null,
                ExecutionTokenTerminationReasons.MultiInstanceFailed, null, ToNodeExecutionActor(actor),
                new NodeExecutionCompletionRecord(NodeExecutionRecordStatuses.Faulted,
                    NodeExecutionCompletionReasons.ErrorEnd, null, null, token.GatewayBranchId,
                    ToNodeExecutionActor(actor), failureCode, failureCode), cancellationToken);
            await FaultInstanceAsync(instance.Id, token.Id, actor, cancellationToken);
            return;
        }
        if (boundary is not null)
        {
            if (!string.IsNullOrWhiteSpace(boundary.ErrorVariable))
            {
                await WriteAutomaticMultiInstanceErrorAsync(instance, body, definition, boundary,
                    token.CurrentNodeExecutionId, actor, failureCode ?? string.Empty, cancellationToken);
                await ResumeForVariableChangesAsync(instance, actor, cancellationToken);
                if (!IsSameRunnableActivation(token,
                        await runtime.GetExecutionTokenAsync(token.Id, false, cancellationToken))) return;
            }
            await runtime.AddTokenHistoryAsync(instance.Id, token.Id, null, node.Id, boundary.Id,
                actor.User, new Dictionary<string, JsonElement>
                {
                    ["executionId"] = JsonSerializer.SerializeToElement(body.Id),
                    ["aggregatePublished"] = JsonSerializer.SerializeToElement(completion.AggregatePublished),
                    ["failureCode"] = JsonSerializer.SerializeToElement(failureCode)
                }, "error", cancellationToken, actor.ActingFor, actor.DelegationId, actorClaims: actor.AuditClaims);
            await runtime.UpdateExecutionTokenAsync(token.Id, ToSnapshot(boundary),
                ExecutionTokenRecordStatuses.Active, token.GatewayBranchId, null, null, null,
                ToNodeExecutionActor(actor), new NodeExecutionCompletionRecord(NodeExecutionRecordStatuses.Faulted,
                    NodeExecutionCompletionReasons.BoundaryCaught, null, null, token.GatewayBranchId,
                    ToNodeExecutionActor(actor), failureCode, failureCode), cancellationToken);
        }
        else
        {
            var flow = OutgoingFlows(instance.WorkflowDefinitionId, definition, node.Id).Single();
            if (node.AsyncAfter)
            {
                await EnsureAsyncAfterWaitAsync(instance, token, node, definition, actor, cancellationToken,
                    selectedFlowId: flow.Id, multiInstanceExecutionId: body.Id);
                return;
            }
            var values = await LoadVariablesAsync(instance.Id, cancellationToken, lockSharedValues: false);
            var flowInfo = await LoadSequenceFlowInfoAsync(instance.Id, definition, cancellationToken);
            // The coordinator records body settlement once. This row records its
            // outgoing traversal (deferred when async-after is used), preserving
            // the completion note expected by FlowInfo without another settlement.
            await AdvanceAutomaticTokenAsync(instance, token, token.GatewayBranchId, node, flow,
                InstanceHistoryNotes.AutomaticMultiInstanceCompleted, definition, actor, values,
                flowInfo, new Queue<long>(), cancellationToken, traversalMultiInstanceExecutionId: body.Id,
                historyPayload: AutomaticBodyTraversalPayload(body.Id));
        }
        if (await IsInstanceRunningAsync(instance.Id, cancellationToken))
        {
            var flowInfo = await LoadSequenceFlowInfoAsync(instance.Id, definition, cancellationToken);
            var resumed = await ResolvePassThroughAsync(instance, definition, actor, flowInfo,
                token.Id, cancellationToken, forceDurableActivities: true);
            await EnsureMultiInstanceInitializedAsync(resumed, definition, actor, cancellationToken);
            _ = await ApplyUserTaskOwnershipInheritanceAsync(resumed, definition, cancellationToken);
        }
    }

    internal async Task WriteAutomaticMultiInstanceErrorAsync(WorkflowInstanceRecord instance,
        AutomaticMultiInstanceRecord body, WorkflowModel definition, FlowNodeModel boundary,
        long? nodeExecutionId, ActorContext actor, string failureCode, CancellationToken cancellationToken)
    {
        if (workflowVariables is null || automaticMultiInstances is null)
            throw new WorkflowJobInvariantException("The automatic error route requires its variable store and body snapshot.");
        var target = definition.Variables.FirstOrDefault(variable =>
            string.Equals(variable.Name, boundary.ErrorVariable, StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkflowConflictException("The automatic error target is no longer declared.");
        var scope = sharedAccessPlanCache.GetOrAdd(body.WorkflowDefinitionId, definition)
            .SelectAutomaticPhase(body.NodeId, AutomaticMultiInstanceAccessPhases.Error, boundary.Id);
        var context = await LoadInstanceVariablesAsync(instance.Id, cancellationToken);
        var snapshot = await automaticMultiInstances.GetInputSnapshotAsync(body.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The automatic body input snapshot is missing.");
        var collection = snapshot.GetProperty("collection");
        context[GetFlowNode(definition, body.NodeId).MultiInstance!.CollectionVariable!] = collection.Clone();
        foreach (var alias in scope.AutomaticPhase!.FrozenReadAliases!)
            context[alias] = collection.Clone();

        // The saved winning route is fenced by the completion/body/token lease.
        // Its diagnostic is produced here under the selected error phase's live
        // dependency locks; the original collection remains the captured input.
        context = await workflowVariables.MergeEffectiveValuesAsync(definition, context, scope, cancellationToken);
        var value = JsonSerializer.SerializeToElement(failureCode);
        try
        {
            if (target.Scope != VariableScopes.Shared)
            {
                var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase) { [target.Name] = value };
                ValidateVariableValues([target], values, enforceRequired: false);
                ValidateResolvedVariableRules([target], values, context);
            }
            // The shared store validates the locked live catalog's value contract
            // (whose expression uses `value`), and retains normal audit/notifications.
            await WriteVariableAsync(definition, instance.WorkflowDefinitionId, instance.Id,
                target.Name, value, boundary.Id, nodeExecutionId, actor, cancellationToken);
        }
        catch (WorkflowDomainException)
        {
            // Routing cannot recursively catch its own rejected diagnostic. Keep
            // the saved completion retryable without exposing values or expressions.
            throw new WorkflowConflictException("The automatic error target rejected the saved failure code.");
        }
    }

    private static Dictionary<string, JsonElement> AutomaticBodyTraversalPayload(long executionId) => new()
    {
        ["executionId"] = JsonSerializer.SerializeToElement(executionId),
        ["purpose"] = JsonSerializer.SerializeToElement("traversal")
    };

    // Conditional entry can precede body creation; timer ownership cannot. A
    // pending normal completion remains interruptible, but a selected error
    // route and async-after have already ended the activity's observable scope.
    internal static bool IsAutomaticBoundaryHostEligible(
        FlowNodeModel node, ExecutionTokenRecord token,
        AutomaticMultiInstanceRecord? body, bool timer)
    {
        if (token.Status != ExecutionTokenRecordStatuses.Active || token.NodeId != node.Id
            || token.WaitState == ExecutionTokenWaitStates.AsyncAfter)
            return false;
        if (!AutomaticMultiInstanceRules.IsAutomaticBody(node))
            return !BpmnFlowNodeTypes.IsCallActivity(node.Type);
        if (body is null)
            return !timer && token.WaitingMultiInstanceExecutionId is null
                && token.WaitState is null or ExecutionTokenWaitStates.AsyncBefore or WorkflowJobKinds.MultiInstanceStart;
        return body.Status == MultiInstanceRecordStatuses.Active
            && body.Phase is "running" or "dispatch" or "completing"
            && body.CompletionIntent is null
            && body.InstanceId == token.InstanceId && body.TokenId == token.Id
            && body.ActivationId == token.ActivationId && body.NodeId == token.NodeId
            && body.ActivityKind == node.Type
            && token.WaitState is null or ExecutionTokenWaitStates.AsyncBefore
                or WorkflowJobKinds.MultiInstanceStart or ExecutionTokenWaitStates.AutomaticMultiInstance
            && (token.WaitingMultiInstanceExecutionId is null || token.WaitingMultiInstanceExecutionId == body.Id);
    }

    private async Task<AutomaticMultiInstanceRecord?> FindAutomaticBodyForHostAsync(
        ExecutionTokenRecord token, CancellationToken cancellationToken)
    {
        if (automaticMultiInstances is null) return null;
        var body = token.WaitingMultiInstanceExecutionId is long id
            ? await automaticMultiInstances.GetAsync(id, false, cancellationToken)
            : await automaticMultiInstances.FindOpenAsync(token.InstanceId, token.NodeId, cancellationToken);
        // Another token can be queued for the same node. Its condition owns only
        // that entry activation and must never capture the earlier token's body.
        return body?.TokenId == token.Id && body.ActivationId == token.ActivationId ? body : null;
    }

    private async Task<bool> IsBoundaryHostEligibleAsync(
        FlowNodeModel node, ExecutionTokenRecord token, bool timer, CancellationToken cancellationToken) =>
        IsAutomaticBoundaryHostEligible(node, token,
            AutomaticMultiInstanceRules.IsAutomaticBody(node)
                ? await FindAutomaticBodyForHostAsync(token, cancellationToken) : null, timer);

    /// <summary>Called inside the initialization transaction after parent/items
    /// exist and before aggregate reset notifications or admission. Never uses
    /// retry time as the duration base; activation/node uniqueness makes retries
    /// observe the same subscriptions.</summary>
    internal async Task ArmAutomaticMultiInstanceTimersAsync(
        WorkflowInstanceRecord instance, ExecutionTokenRecord token,
        AutomaticMultiInstanceRecord body, WorkflowModel definition, ActorContext actor,
        IReadOnlyDictionary<string, JsonElement> storedValues, CancellationToken cancellationToken)
    {
        var node = GetFlowNode(definition, body.NodeId);
        if (body.WorkflowDefinitionId != instance.WorkflowDefinitionId
            || !AutomaticMultiInstanceRules.IsAutomaticBody(node)
            || !IsAutomaticBoundaryHostEligible(node, token, body, timer: true))
            throw new WorkflowConflictException("The automatic body no longer owns its boundary activation.");
        await EnsureAttachedTimerBoundaryWaitsAsync(instance, token, node, definition,
            actor, storedValues, body.CreatedAt, cancellationToken);
    }

    /// <summary>Drains accepted instance writes before admission or continuation.
    /// Shared writes do not create notifications. The caller retains the ambient
    /// runtime transaction and reuses the returned fence decision.</summary>
    internal async Task<bool> DrainAutomaticMultiInstanceNotificationsAsync(
        WorkflowInstanceRecord instance, AutomaticMultiInstanceRecord body,
        WorkflowModel definition, ActorContext actor, CancellationToken cancellationToken)
    {
        var plan = conditionalEventPlans?.GetOrAdd(instance.WorkflowDefinitionId, definition)
            ?? ConditionalEventDependencyPlan.Empty;
        var overlay = await LoadInstanceVariablesAsync(instance.Id, cancellationToken);
        var flowInfo = await LoadSequenceFlowInfoAsync(instance.Id, definition, cancellationToken);
        var queue = new Queue<long>();
        var durable = new HashSet<long>();
        await DrainConditionalVariableChangesAsync(instance, definition, plan, actor,
            overlay, flowInfo, queue, durable, 10_000, cancellationToken);
        while (queue.TryDequeue(out var tokenId))
        {
            if (!await IsInstanceRunningAsync(instance.Id, cancellationToken)) break;
            var resumed = await ResolvePassThroughAsync(instance, definition, actor,
                flowInfo, tokenId, cancellationToken, forceDurableActivities: true);
            await EnsureMultiInstanceInitializedAsync(resumed, definition, actor, cancellationToken);
            _ = await ApplyUserTaskOwnershipInheritanceAsync(resumed, definition, cancellationToken);
        }
        var token = await runtime.GetExecutionTokenAsync(body.TokenId, false, cancellationToken);
        var current = automaticMultiInstances is null ? null
            : await automaticMultiInstances.GetAsync(body.Id, true, cancellationToken);
        return await IsInstanceRunningAsync(instance.Id, cancellationToken)
            && token is not null && current is not null
            && IsAutomaticBoundaryHostEligible(GetFlowNode(definition, body.NodeId), token, current, timer: true);
    }

    internal Task CloseAutomaticMultiInstanceBoundariesAsync(
        long instanceId, long tokenId, CancellationToken cancellationToken) =>
        CancelAttachedBoundaryWaitsAsync(instanceId, [tokenId], cancellationToken);

    private async Task CancelAutomaticBodyForBoundaryAsync(
        ExecutionTokenRecord token, string reason, CancellationToken cancellationToken)
    {
        if (automaticMultiInstances is null) return;
        var body = await FindAutomaticBodyForHostAsync(token, cancellationToken);
        if (body is null) return;
        await automaticMultiInstances.CancelUnfinishedItemsAsync(body.Id, body.ActivationId, reason, cancellationToken);
        if (!await automaticMultiInstances.SettleAsync(body.Id, body.ActivationId,
                MultiInstanceRecordStatuses.Cancelled, reason, cancellationToken))
            throw new WorkflowConflictException("The automatic body changed before boundary cancellation.");
    }
}
