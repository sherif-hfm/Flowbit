using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

public sealed partial class AdministrativeActionBatchService
{
    public async Task<PagedResult<InstanceAdministrativeActionPositionDto>?> ListInstanceActionsAsync(
        long instanceId,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await WorkflowAdministratorPolicy.RequireAsync(actor, engineSettings, cancellationToken);
        EnsurePositive(instanceId, "Instance id");
        var runtime = instanceRuntime
            ?? throw new InvalidOperationException("The instance administrative runtime is not configured.");
        var instance = await runtime.GetInstanceAsync(instanceId, cancellationToken);
        if (instance is null)
        {
            return null;
        }
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var result = await candidates.SearchAsync(new AdministrativeActionCandidateQuery
        {
            WorkflowDefinitionId = instance.WorkflowDefinitionId,
            InstanceId = instanceId,
            Page = Math.Max(1, page),
            PageSize = Math.Clamp(pageSize, 1, 200)
        }, cancellationToken);
        return new PagedResult<InstanceAdministrativeActionPositionDto>(
            result.Items.Select(position => new InstanceAdministrativeActionPositionDto(
                ToCandidateDto(position, workflow.Version),
                ResolveActions(workflow, position.NodeId)
                    .Where(action => action.ActionKind == AdministrativeActionKinds.DirectFlow)
                    .ToArray())).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    public async Task<AdministrativeActionResultDto?> ExecuteInstanceActionAsync(
        long instanceId,
        ExecuteInstanceAdministrativeActionRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await WorkflowAdministratorPolicy.RequireAsync(actor, engineSettings, cancellationToken);
        ArgumentNullException.ThrowIfNull(request);
        ValidateInstanceActionRequest(instanceId, request);
        var user = RequireActor(actor);
        var reason = NormalizeOptionalReason(request.Reason, "Reason");
        ValidateVariableNameUniqueness(request.Variables);
        var runtime = instanceRuntime
            ?? throw new InvalidOperationException("The instance administrative runtime is not configured.");
        var executor = administrativeExecutor
            ?? throw new InvalidOperationException("The instance administrative executor is not configured.");

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Acquire the established full runtime hierarchy before creating audit
        // rows. The engine shares this transaction and never commits it.
        var instance = await runtime.GetInstanceForUpdateAsync(
            instanceId, lockActiveUserTask: true, cancellationToken);
        if (instance is null)
        {
            return null;
        }
        long? positionInstanceId = request.PositionKind == AdministrativeActionPositionKinds.UserTask
            ? (await runtime.GetUserTaskAsync(request.PositionId, false, cancellationToken))?.InstanceId
            : (await runtime.GetMultiInstanceAsync(request.PositionId, false, cancellationToken))?.InstanceId;
        var expectedToken = await runtime.GetExecutionTokenAsync(
            request.ExpectedTokenId, false, cancellationToken);
        if (positionInstanceId != instanceId || expectedToken is null
            || expectedToken.InstanceId != instanceId)
        {
            return null;
        }
        if (instance.WorkflowDefinitionId != request.ExpectedWorkflowDefinitionId)
        {
            throw new WorkflowConflictException("The workflow definition changed after action selection.");
        }
        var page = await candidates.SearchAsync(new AdministrativeActionCandidateQuery
        {
            WorkflowDefinitionId = instance.WorkflowDefinitionId,
            SourceNodeId = request.SourceNodeId,
            InstanceId = instanceId,
            PositionKind = request.PositionKind,
            PositionId = request.PositionId,
            Page = 1,
            PageSize = 1
        }, cancellationToken);
        var position = page.Items.SingleOrDefault();
        if (position is null
            || position.TokenId != request.ExpectedTokenId
            || position.TokenActivationId != request.ExpectedTokenActivationId
            || position.PositionUpdatedAt != request.ExpectedPositionUpdatedAt
            || position.AffectedTaskCount != request.ExpectedAffectedTaskCount)
        {
            throw new WorkflowConflictException("The selected position changed. Refresh before selecting another action.");
        }
        if (position.AffectedTaskCount > await ResolveMaxAffectedTasksAsync(cancellationToken))
        {
            throw new WorkflowDomainException("The selected position exceeds the configured affected-task limit.");
        }
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var resolved = ResolveAction(workflow, request.SourceNodeId,
            AdministrativeActionKinds.DirectFlow, request.FlowId, null);
        ValidateMultiInstanceMode(resolved, request.MultiInstanceMode);
        ValidateCommonVariables(resolved.Summary, request.Variables);
        var roles = SnapshotRoles(actor.Roles);
        var variables = CloneVariables(request.Variables);
        var now = timeProvider.GetUtcNow();
        var selection = new AdministrativeActionBatchSelectionDto(
            AdministrativeActionBatchSelectionModes.Explicit,
            [new AdministrativeActionPositionReferenceDto(position.PositionKind, position.PositionId)],
            null,
            null);
        var batch = await batches.AddAsync(new NewAdministrativeActionBatchRecord(
            workflow.WorkflowKey,
            workflow.Id,
            resolved.Source.Id,
            AdministrativeActionKinds.DirectFlow,
            resolved.Flow.Id,
            null,
            NormalizeOptional(request.MultiInstanceMode),
            ToActionSnapshot(resolved),
            reason,
            variables,
            JsonSerializer.SerializeToElement(selection),
            user,
            roles,
            null,
            now), cancellationToken);
        var item = (await batches.AddItemsAsync(batch.Id,
            [ToNewItem(position, resolved, now)], cancellationToken)).Single();
        await batches.UpdateItemAsync(ToItemUpdate(item) with
        {
            Status = AdministrativeActionBatchItemStatuses.Queued,
            PreparedAt = now,
            StartedAt = now,
            UpdatedAt = now
        }, cancellationToken);
        batch = await batches.UpdateAsync(ToUpdate(batch) with
        {
            Status = AdministrativeActionBatchStatuses.Running,
            ConfirmedBy = user,
            ConfirmedByRoles = roles,
            TotalItemCount = 1,
            TotalAffectedTaskCount = position.AffectedTaskCount,
            QueuedItemCount = 1,
            PreparedAt = now,
            ConfirmedAt = now,
            StartedAt = now,
            UpdatedAt = now
        }, cancellationToken);
        var internalRequest = new AdministrativeActionRequest
        {
            BatchId = batch.Id,
            BatchItemId = item.Id,
            ExpectedWorkflowDefinitionId = workflow.Id,
            SourceNodeId = request.SourceNodeId,
            ActionKind = AdministrativeActionKinds.DirectFlow,
            FlowId = request.FlowId,
            MultiInstanceMode = NormalizeOptional(request.MultiInstanceMode),
            PositionKind = position.PositionKind,
            PositionId = position.PositionId,
            UserTaskId = position.UserTaskId,
            MultiInstanceExecutionId = position.MultiInstanceExecutionId,
            ExpectedTokenId = position.TokenId,
            ExpectedTokenActivationId = position.TokenActivationId,
            ExpectedPositionUpdatedAt = position.PositionUpdatedAt,
            Reason = reason,
            Variables = new Dictionary<string, JsonElement>(variables, StringComparer.OrdinalIgnoreCase)
        };
        AdministrativeActionResultDto result;
        try
        {
            result = await executor.ExecuteInTransactionAsync(internalRequest, actor, cancellationToken)
                ?? throw new WorkflowConflictException("The selected position is no longer available.");
        }
        catch (AdministrativeActionExecutionException exception)
        {
            throw new WorkflowDomainException(exception.Message);
        }
        var completedAt = timeProvider.GetUtcNow();
        await batches.UpdateAsync(ToUpdate(batch) with
        {
            Status = AdministrativeActionBatchStatuses.Completed,
            QueuedItemCount = 0,
            SucceededItemCount = 1,
            UpdatedAt = completedAt,
            CompletedAt = completedAt
        }, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static void ValidateInstanceActionRequest(
        long instanceId,
        ExecuteInstanceAdministrativeActionRequest request)
    {
        if (instanceId <= 0 || request.ExpectedWorkflowDefinitionId <= 0
            || request.SourceNodeId <= 0 || request.FlowId <= 0
            || request.PositionId <= 0 || request.ExpectedTokenId <= 0
            || request.ExpectedTokenActivationId == Guid.Empty
            || request.ExpectedPositionUpdatedAt == default
            || request.ExpectedAffectedTaskCount <= 0
            || !AdministrativeActionPositionKinds.IsKnown(request.PositionKind))
        {
            throw new WorkflowDomainException(
                "The instance, workflow, source, action, position, token activation, timestamp, and affected-task count are required.");
        }
    }
}
