using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

/// <summary>
/// Signal events: subscriptions that live exactly as long as their wait, and the
/// delivery of one frozen broadcast recipient inside its own transaction.
/// </summary>
public sealed partial class WorkflowEngineService : ISignalDeliveryExecutor
{
    // A delivered signal continues the instance as this fixed actor, as a finished
    // call continues its parent as "callActivity". The thrower is recorded on the
    // broadcast and in the history payload, never as the actor.
    private const string SignalActorName = "signal";

    private static ActorContext SignalActor() =>
        new(SignalActorName, [], new Dictionary<string, string>());

    /// <summary>
    /// <c>Workflow.Gateway.MaxActiveTokens</c>, the most active tokens one instance may
    /// hold: 1,000 when the setting is missing or not a positive whole number.
    /// </summary>
    private async Task<int> ReadMaxActiveTokensAsync(CancellationToken cancellationToken)
    {
        var configured = await engineSettings.GetByKeyAsync(
            "Workflow.Gateway.MaxActiveTokens", cancellationToken);
        return configured is not null
               && int.TryParse(configured.Value, out var parsed)
               && parsed > 0
            ? parsed
            : 1000;
    }

    /// <summary>
    /// Creates the signal subscriptions a token owns at its current node: a signal
    /// catch's own wait, and one per signal boundary attached to an eligible host. Runs
    /// once the conditional boundaries attached to the node have had their chance to
    /// interrupt it on entry, so a subscription never belongs to a token that has
    /// already left. Idempotent per activation.
    /// </summary>
    private async Task EnsureSignalSubscriptionsAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel node,
        WorkflowModel definition,
        CancellationToken cancellationToken)
    {
        if (signalSubscriptions is null || !SignalEventRules.HasSignalEvents(definition))
        {
            return;
        }

        var waits = new List<FlowNodeModel>();
        if (BpmnFlowNodeTypes.IsSignalCatch(node.Type))
        {
            waits.Add(node);
        }

        var boundaries = definition.FlowNodes
            .Where(candidate => BpmnFlowNodeTypes.IsSignalBoundary(candidate.Type)
                                && candidate.AttachedToRef == node.Id)
            .OrderBy(candidate => candidate.Id)
            .ToList();
        if (boundaries.Count > 0
            && await IsBoundaryHostEligibleAsync(node, token, timer: false, cancellationToken))
        {
            waits.AddRange(boundaries);
        }

        if (waits.Count == 0)
        {
            return;
        }

        var existing = (await signalSubscriptions.ListForActivationAsync(
                token.Id,
                token.ActivationId,
                cancellationToken))
            .Select(subscription => subscription.SignalNodeId)
            .ToHashSet();
        var creates = new List<SignalSubscriptionCreateRecord>();
        foreach (var wait in waits.Where(wait => !existing.Contains(wait.Id)))
        {
            var boundary = BpmnFlowNodeTypes.IsSignalBoundary(wait.Type);
            var signal = wait.Signal
                ?? throw new WorkflowDomainException($"{SignalEventRules.Label(wait)} has no signal configuration.");
            var flow = OutgoingFlows(instance.WorkflowDefinitionId, definition, wait.Id).SingleOrDefault()
                ?? throw new WorkflowDomainException(
                    $"{SignalEventRules.Label(wait)} must have exactly one outgoing sequence flow.");
            creates.Add(new SignalSubscriptionCreateRecord
            {
                InstanceId = instance.Id,
                WorkflowDefinitionId = instance.WorkflowDefinitionId,
                WorkflowKey = instance.WorkflowKey,
                TokenId = token.Id,
                ActivationId = token.ActivationId,
                SignalNodeId = wait.Id,
                SignalNodeName = wait.Name,
                AttachedToNodeId = boundary ? node.Id : null,
                OutgoingFlowId = flow.Id,
                SignalName = signal.Name,
                CancelActivity = !boundary || wait.CancelActivity != false
            });
        }

        if (creates.Count > 0)
        {
            await signalSubscriptions.CreateManyAsync(creates, cancellationToken);
        }
    }

    /// <summary>
    /// Cancels the active signal subscriptions of the given tokens. A definition without
    /// signal events never has any, so the statement is skipped when the caller passes
    /// the instance's definition; without it the statement always runs.
    /// </summary>
    private async Task CancelSignalSubscriptionsAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        WorkflowModel? definition,
        CancellationToken cancellationToken)
    {
        if (signalSubscriptions is null
            || tokenIds.Count == 0
            || (definition is not null && !SignalEventRules.HasSignalEvents(definition)))
        {
            return;
        }

        await signalSubscriptions.CancelByTokenIdsAsync(instanceId, tokenIds, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SignalDeliveryOutcome> DeliverSignalAsync(
        long deliveryId,
        CancellationToken cancellationToken)
    {
        if (signalSubscriptions is null || signalBroadcasts is null)
        {
            throw new InvalidOperationException("Signal persistence is unavailable.");
        }

        var preview = await signalBroadcasts.GetDeliveryAsync(deliveryId, cancellationToken)
            ?? throw new InvalidOperationException($"Signal delivery #{deliveryId} does not exist.");
        if (preview.Status != SignalDeliveryStatuses.Pending)
        {
            return new SignalDeliveryOutcome(preview.Status, preview.ResultCode, null);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Lock order: the instance hierarchy, then the subscription, then the delivery.
        var instance = await runtime.GetInstanceForUpdateAsync(preview.InstanceId, true, cancellationToken);
        var subscription = await signalSubscriptions.GetForUpdateAsync(preview.SubscriptionId, cancellationToken);
        var delivery = await signalBroadcasts.GetDeliveryForUpdateAsync(deliveryId, cancellationToken)
            ?? throw new InvalidOperationException($"Signal delivery #{deliveryId} does not exist.");
        if (delivery.Status != SignalDeliveryStatuses.Pending)
        {
            await transaction.CommitAsync(cancellationToken);
            return new SignalDeliveryOutcome(delivery.Status, delivery.ResultCode, null);
        }

        if (instance is null
            || subscription is null
            || subscription.Status != SignalSubscriptionStatuses.Active
            || !string.Equals(instance.Status, WorkflowInstanceStatuses.Running, StringComparison.Ordinal))
        {
            return await FinishDeliveryAsync(
                transaction,
                deliveryId,
                SignalDeliveryStatuses.Skipped,
                SignalDeliveryResultCodes.SubscriptionEnded,
                null,
                cancellationToken);
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var token = await runtime.GetExecutionTokenAsync(subscription.TokenId, true, cancellationToken);
        var node = GetFlowNode(workflow.Definition, subscription.SignalNodeId);
        var isBoundary = subscription.AttachedToNodeId is not null;
        var expectedNodeId = subscription.AttachedToNodeId ?? subscription.SignalNodeId;
        if (token is null
            || token.InstanceId != instance.Id
            || token.Status != ExecutionTokenRecordStatuses.Active
            || token.ActivationId != subscription.ActivationId
            || token.NodeId != expectedNodeId
            || (isBoundary
                ? !BpmnFlowNodeTypes.IsSignalBoundary(node.Type) || node.AttachedToRef != expectedNodeId
                : !BpmnFlowNodeTypes.IsSignalCatch(node.Type)))
        {
            // Every path that ends a wait cancels its subscriptions, so an active one
            // whose token has moved is an invariant failure, not a business outcome.
            throw new SignalDeliveryInvariantException(
                $"Signal subscription #{subscription.Id} is active, but its token is not waiting at node #{expectedNodeId}.");
        }

        if (isBoundary
            && !await IsBoundaryHostEligibleAsync(
                GetFlowNode(workflow.Definition, token.NodeId), token, timer: false, cancellationToken))
        {
            // The host has finished and waits for its async-after continuation, which
            // ends the boundary's wait.
            throw new WorkflowConflictException(
                $"The host of signal boundary event #{node.Id} is no longer waiting.");
        }

        var broadcast = await signalBroadcasts.GetAsync(delivery.BroadcastId, false, cancellationToken)
            ?? throw new InvalidOperationException($"Signal broadcast #{delivery.BroadcastId} does not exist.");
        JsonElement? payload = null;
        if (broadcast.PayloadJson is not null)
        {
            using var document = JsonDocument.Parse(broadcast.PayloadJson);
            payload = document.RootElement.Clone();
        }

        logger.LogInformation(
            "Delivering signal broadcast {BroadcastId} to signal event #{NodeId} on instance {InstanceId}.",
            broadcast.Id, node.Id, instance.Id);

        await LoadSettingsAsync(cancellationToken);
        var actor = SignalActor();
        var processVariables = workflow.Definition.Variables;
        var stored = await LoadVariablesAsync(instance.Id, workflow.Definition, cancellationToken, sharedAccess: null);
        var tokenInstance = instance with { ActiveTokenId = token.Id, CurrentStepId = token.NodeId };
        var context = WithContext(stored, actor, tokenInstance, workflow.Definition, node);
        var mappings = (node.Signal?.OutputMappings ?? [])
            .Select(mapping => ToTypedOutputRuntime(mapping, processVariables))
            .ToList();
        Dictionary<string, JsonElement> values;
        try
        {
            values = ResolveTypedOutputs(
                node.Id,
                isBoundary ? "Signal boundary event" : "Signal catch event",
                mappings,
                processVariables,
                payload,
                context);
        }
        catch (WorkflowDomainException exception)
        {
            throw new SignalPayloadException(exception.Message);
        }

        var history = SignalHistoryPayload(broadcast, subscription);
        if (!isBoundary)
        {
            return await ApplySignalAsync(
                transaction, deliveryId, instance, token, workflow, node, actor, stored, values, history,
                completeSubscriptionId: subscription.Id, SignalDeliveryResultCodes.Caught, cancellationToken);
        }

        if (subscription.CancelActivity)
        {
            // Consume the subscription first, so the host's cleanup cannot cancel it.
            if (!await signalSubscriptions.CompleteAsync(subscription.Id, cancellationToken))
            {
                throw new WorkflowConflictException(
                    $"Signal subscription #{subscription.Id} ended while the signal was being delivered.");
            }

            await InterruptBoundaryHostAsync(
                instance,
                token,
                node,
                workflow.Definition,
                actor,
                NodeExecutionCompletionReasons.SignalReceived,
                "interruptingSignalBoundary",
                keepConditionalSubscriptionId: null,
                cancellationToken);
            var moved = await runtime.GetExecutionTokenAsync(token.Id, false, cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The interrupted host token disappeared while the signal was being delivered.");
            return await ApplySignalAsync(
                transaction, deliveryId, instance, moved, workflow, node, actor, stored, values, history,
                completeSubscriptionId: null, SignalDeliveryResultCodes.Interrupted, cancellationToken);
        }

        var maxActiveTokens = await ReadMaxActiveTokensAsync(cancellationToken);
        var activeCount = (await runtime.ListExecutionTokensAsync(
            instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken)).Count;
        if (activeCount + 1 > maxActiveTokens)
        {
            throw new SignalTokenLimitException(
                $"Signal boundary event #{node.Id} would exceed the active token limit ({maxActiveTokens}).");
        }

        // The subscription stays active for the next signal; only its count moves.
        if (!await signalSubscriptions.RecordOccurrenceAsync(subscription.Id, cancellationToken))
        {
            throw new WorkflowConflictException(
                $"Signal subscription #{subscription.Id} ended while the signal was being delivered.");
        }

        var sibling = await runtime.AddExecutionTokenAsync(
            instance.Id,
            ToSnapshot(node),
            token.GatewayBranchId,
            null,
            ToNodeExecutionActor(actor),
            cancellationToken,
            automaticActivationCount:
                WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger(),
            automaticActivationStateIds: token.AutomaticActivationStateIds);
        return await ApplySignalAsync(
            transaction, deliveryId, instance, sibling, workflow, node, actor, stored, values, history,
            completeSubscriptionId: null, SignalDeliveryResultCodes.Branched, cancellationToken);
    }

    /// <summary>
    /// Writes a delivery's mapped values on the visit of the token at the signal event,
    /// lets conditional events observe them as one batch, and continues along the event's
    /// flow when the token is still the same runnable activation; otherwise a conditional
    /// boundary took it first and the delivery is <c>superseded</c>. A catch completes its
    /// subscription only then, so a superseded catch is cancelled with its host's waits.
    /// </summary>
    private async Task<SignalDeliveryOutcome> ApplySignalAsync(
        IWorkflowTransaction transaction,
        long deliveryId,
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        WorkflowDefinitionRecord workflow,
        FlowNodeModel node,
        ActorContext actor,
        Dictionary<string, JsonElement> stored,
        Dictionary<string, JsonElement> values,
        Dictionary<string, JsonElement> history,
        long? completeSubscriptionId,
        string resultCode,
        CancellationToken cancellationToken)
    {
        var processVariables = workflow.Definition.Variables;
        await WriteVariablesAsync(
            new WorkflowModel { Variables = processVariables.ToList() },
            workflow.Id,
            instance.Id,
            values,
            node.Id,
            token.CurrentNodeExecutionId,
            actor,
            cancellationToken);
        foreach (var pair in values)
        {
            stored[pair.Key] = pair.Value;
        }

        // The mapped values are one observable batch while the token is still at the
        // event, so an interrupting conditional boundary may take it first.
        await ResumeForVariableChangesAsync(instance, actor, cancellationToken);
        var current = await runtime.GetExecutionTokenAsync(token.Id, false, cancellationToken);
        if (!IsSameRunnableActivation(token, current))
        {
            return await FinishDeliveryAsync(
                transaction,
                deliveryId,
                SignalDeliveryStatuses.Delivered,
                SignalDeliveryResultCodes.Superseded,
                null,
                cancellationToken);
        }

        if (completeSubscriptionId is long subscriptionId
            && !await signalSubscriptions!.CompleteAsync(subscriptionId, cancellationToken))
        {
            throw new WorkflowConflictException(
                $"Signal subscription #{subscriptionId} ended while the signal was being delivered.");
        }

        await AdvanceFromSignalEventAsync(
            instance,
            current!,
            workflow.Id,
            workflow.Definition,
            node,
            actor,
            stored,
            history,
            cancellationToken);

        return await FinishDeliveryAsync(
            transaction,
            deliveryId,
            SignalDeliveryStatuses.Delivered,
            resultCode,
            null,
            cancellationToken);
    }

    private async Task AdvanceFromSignalEventAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        long workflowDefinitionId,
        WorkflowModel definition,
        FlowNodeModel node,
        ActorContext actor,
        Dictionary<string, JsonElement> stored,
        Dictionary<string, JsonElement> historyPayload,
        CancellationToken cancellationToken)
    {
        var boundary = BpmnFlowNodeTypes.IsSignalBoundary(node.Type);
        var outgoing = OutgoingFlows(workflowDefinitionId, definition, node.Id).Take(2).ToList();
        if (outgoing.Count != 1)
        {
            throw new WorkflowDomainException(
                $"{SignalEventRules.Label(node)} must have exactly one outgoing sequence flow.");
        }

        var flow = outgoing[0];
        var nextNode = GetFlowNode(definition, flow.TargetRef);
        var targetTokenStatus = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
            ? ExecutionTokenRecordStatuses.Faulted
            : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                ? ExecutionTokenRecordStatuses.Completed
                : ExecutionTokenRecordStatuses.Active;
        var terminationReason = BpmnFlowNodeTypes.IsTerminateEnd(nextNode.Type)
            ? ExecutionTokenTerminationReasons.TerminateEnd
            : BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
                ? ExecutionTokenTerminationReasons.ErrorEnd
                : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                    ? ExecutionTokenTerminationReasons.NormalEnd
                    : null;

        var flowInfo = await LoadSequenceFlowInfoAsync(instance.Id, definition, cancellationToken);
        await RecordSequenceFlowOccurrenceAsync(
            flowInfo,
            instance.Id,
            token.Id,
            null,
            null,
            null,
            flow,
            boundary ? "signalBoundary" : "signalCatch",
            isAction: false,
            isTraversal: true,
            actor: actor,
            values: null,
            cancellationToken: cancellationToken);
        await runtime.AddTokenHistoryAsync(
            instance.Id,
            token.Id,
            null,
            node.Id,
            nextNode.Id,
            actor.User,
            historyPayload,
            InstanceHistoryNotes.SignalReceived,
            cancellationToken);

        var tokenInstance = instance with
        {
            ActiveTokenId = token.Id,
            CurrentStepId = nextNode.Id,
            ClaimedBy = null,
            FaultCode = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type) ? nextNode.ErrorCode : null,
            FaultDescription = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
                ? nextNode.ErrorDescription ?? nextNode.Name
                : null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var nextContext = WithContext(stored, actor, tokenInstance, definition, nextNode);
        if (!boundary)
        {
            // A token on a boundary has no waits of its own: an interrupted host's
            // were cancelled with its work, and a sibling never had any.
            await CancelAttachedBoundaryWaitsAsync(instance.Id, [token.Id], cancellationToken, definition);
        }
        await runtime.UpdateExecutionTokenAsync(
            token.Id,
            ToSnapshot(nextNode, nextContext, instance.Id, definition),
            targetTokenStatus,
            token.GatewayBranchId,
            flow.Id,
            terminationReason,
            null,
            ToNodeExecutionActor(actor),
            new NodeExecutionCompletionRecord(
                NodeExecutionRecordStatuses.Completed,
                NodeExecutionCompletionReasons.SignalReceived,
                null,
                flow.Id,
                token.GatewayBranchId,
                ToNodeExecutionActor(actor)),
            cancellationToken,
            automaticActivationCount:
                WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (BpmnFlowNodeTypes.IsTerminateEnd(nextNode.Type))
        {
            await TerminateInstanceAsync(instance.Id, token.Id, actor, cancellationToken, definition);
        }
        else if (BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type))
        {
            await FaultInstanceAsync(instance.Id, token.Id, actor, cancellationToken, definition);
        }
        else if (BpmnFlowNodeTypes.IsEnd(nextNode.Type))
        {
            var remaining = await runtime.ListExecutionTokensAsync(
                instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken);
            if (remaining.Count == 0)
            {
                await SetInstanceTerminalStatusAsync(
                    instance.Id, WorkflowInstanceStatuses.Completed, cancellationToken);
            }
            await CloseInactiveGatewayScopesAsync(instance.Id, "allEnded", cancellationToken);
        }
        else
        {
            var resolved = await ResolvePassThroughAsync(
                tokenInstance,
                definition,
                actor,
                flowInfo,
                token.Id,
                cancellationToken,
                forceDurableActivities: true);
            await EnsureMultiInstanceInitializedAsync(resolved, definition, actor, cancellationToken);
            _ = await ApplyUserTaskOwnershipInheritanceAsync(resolved, definition, cancellationToken);
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static Dictionary<string, JsonElement> SignalHistoryPayload(
        SignalBroadcastRecord broadcast,
        SignalSubscriptionRecord subscription)
    {
        var payload = new Dictionary<string, JsonElement>
        {
            ["broadcastId"] = JsonSerializer.SerializeToElement(broadcast.Id),
            ["signalName"] = JsonSerializer.SerializeToElement(broadcast.SignalName),
            ["subscriptionId"] = JsonSerializer.SerializeToElement(subscription.Id),
            ["eventNodeId"] = JsonSerializer.SerializeToElement(subscription.SignalNodeId),
            ["eventKind"] = JsonSerializer.SerializeToElement(subscription.IsBoundary ? "boundary" : "catch"),
            ["hostTokenId"] = JsonSerializer.SerializeToElement(subscription.TokenId),
            ["cancelActivity"] = JsonSerializer.SerializeToElement(subscription.CancelActivity),
            ["thrownBy"] = JsonSerializer.SerializeToElement(broadcast.ThrownBy),
            ["thrownByKind"] = JsonSerializer.SerializeToElement(broadcast.ThrownByKind)
        };
        return payload;
    }

    private async Task<SignalDeliveryOutcome> FinishDeliveryAsync(
        IWorkflowTransaction transaction,
        long deliveryId,
        string status,
        string resultCode,
        string? description,
        CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (!await signalBroadcasts!.CompleteDeliveryAsync(
                deliveryId,
                status,
                resultCode,
                description,
                timeProvider.GetUtcNow(),
                cancellationToken))
        {
            throw new WorkflowConflictException($"Signal delivery #{deliveryId} was finished by another attempt.");
        }
        await transaction.CommitAsync(cancellationToken);
        return new SignalDeliveryOutcome(status, resultCode, description);
    }
}

/// <summary>
/// A broadcast's payload failed a signal event's typed mappings: the delivery fails
/// with <c>signal_payload_invalid</c>, nothing is written, and the wait remains.
/// </summary>
public sealed class SignalPayloadException(string message) : InvalidOperationException(message);
