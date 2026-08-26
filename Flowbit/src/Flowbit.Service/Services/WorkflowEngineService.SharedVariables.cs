using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService
{
    /// <summary>
    /// Re-evaluates one persisted cross-workflow conditional delivery against
    /// latest instance and shared state. The delivery's token/activation fence
    /// makes retries and replica races harmless.
    /// </summary>
    public async Task ProcessSharedVariableWakeDeliveryAsync(
        SharedVariableWakeDeliveryRecord delivery,
        CancellationToken cancellationToken)
    {
        var actor = new ActorContext(
            "shared-variable-worker",
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        await using var transaction = await unitOfWork.BeginTransactionAsync(
            cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(
            delivery.InstanceId,
            lockActiveUserTask: false,
            cancellationToken);
        if (instance is null
            || instance.Status != WorkflowInstanceStatuses.Running)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var token = await runtime.GetExecutionTokenAsync(
            delivery.TokenId,
            forUpdate: true,
            cancellationToken);
        if (token is null
            || token.Status != ExecutionTokenRecordStatuses.Active
            || token.ActivationId != delivery.ActivationId
            || token.NodeId != delivery.NodeId
            || token.WaitState is not null
            || token.WaitingJobId is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // A compatible version switch may commit after this delivery was
        // expanded. Evaluate against the instance's authoritative current
        // definition; the token fence and the alias/dependency checks below
        // prove that the delivery contract still applies.
        var workflow = await GetWorkflowAsync(
            instance.WorkflowDefinitionId,
            cancellationToken);
        var node = GetFlowNode(workflow.Definition, token.NodeId);
        if (!BpmnFlowNodeTypes.IsConditionalCatch(node.Type))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var aliases = workflow.Definition.Variables
            .Where(variable => string.Equals(
                    variable.Scope,
                    VariableScopes.Shared,
                    StringComparison.Ordinal)
                && string.Equals(
                    variable.SharedKey,
                    delivery.SharedKey,
                    StringComparison.OrdinalIgnoreCase))
            .Select(variable => variable.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (aliases.Length == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var plan = conditionalEventPlans?.GetOrAdd(
                workflow.Id,
                workflow.Definition)
            ?? ConditionalEventDependencyPlan.Empty;
        if (!plan.EventsByNodeId.TryGetValue(node.Id, out var eventPlan)
            || !eventPlan.Dependencies.Any(dependency => aliases.Contains(
                dependency,
                StringComparer.OrdinalIgnoreCase)))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var stored = await LoadVariablesAsync(
            instance.Id,
            cancellationToken,
            lockSharedValues: true,
            sharedAccessNodeId: token.NodeId);
        var flowInfo = await LoadSequenceFlowInfoAsync(
            instance.Id,
            workflow.Definition,
            cancellationToken);
        var routingQueue = new Queue<long>();
        var forceDurableTokenIds = new HashSet<long>();
        _ = await TriggerConditionalWaitsAsync(
            instance,
            workflow.Definition,
            plan,
            actor,
            stored,
            flowInfo,
            aliases,
            routingQueue,
            forceDurableTokenIds,
            maxTriggers: 10_000,
            onlyTokenId: delivery.TokenId,
            cancellationToken);

        while (routingQueue.Count > 0
               && await IsInstanceRunningAsync(instance.Id, cancellationToken))
        {
            var tokenId = routingQueue.Dequeue();
            var fresh = await runtime.GetInstanceAsync(instance.Id, cancellationToken);
            if (fresh is null)
            {
                break;
            }
            _ = await ResolvePassThroughAsync(
                fresh,
                workflow.Definition,
                actor,
                flowInfo,
                tokenId,
                cancellationToken,
                forceDurableActivities: true);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
