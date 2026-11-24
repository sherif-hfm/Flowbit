using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

/// <summary>
/// Signal start events (EVENT-04C). A throw scoped to a family or to every family froze one
/// start delivery per listening family; this creates the instance for one of them, in its
/// own transaction, from the version that is the family's published default when it runs.
/// It locks no existing instance: the shared family lock, then the delivery row, then a
/// business-key claim, and then only new rows.
/// </summary>
public sealed partial class WorkflowEngineService
{
    /// <summary>
    /// Creates the instance of one frozen start, or ends the delivery without one. A start
    /// that the family's default no longer has is skipped here. Every other start without an
    /// instance, a payload that fails a mapping, a duplicate business key, or a refusal of
    /// the version it tried, is reported by <see cref="SignalStartOutcomeException"/> after
    /// its transaction rolls back, with that version and node; the processor records it in a
    /// transaction of its own.
    /// </summary>
    private async Task<SignalDeliveryOutcome> StartFromSignalAsync(
        SignalDeliveryRecord preview,
        CancellationToken cancellationToken)
    {
        var broadcasts = signalBroadcasts
            ?? throw new InvalidOperationException("Signal persistence is unavailable.");
        var broadcast = await broadcasts.GetAsync(preview.BroadcastId, false, cancellationToken)
            ?? throw new InvalidOperationException($"Signal broadcast #{preview.BroadcastId} does not exist.");

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        // Lock order: the family's shared start lock, then the delivery. An operation that
        // changes the family's default holds the exclusive lock, so the default read below
        // cannot change before this commits.
        await definitions.LockFamilyForStartAsync(preview.WorkflowKey, cancellationToken);
        var delivery = await broadcasts.GetDeliveryForUpdateAsync(preview.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Signal delivery #{preview.Id} does not exist.");
        if (delivery.Status != SignalDeliveryStatuses.Pending)
        {
            await transaction.CommitAsync(cancellationToken);
            return new SignalDeliveryOutcome(delivery.Status, delivery.ResultCode, null);
        }

        // EVENT-04E: after the family lock and the delivery row, which a default change can
        // hold for a while, and before the default is read: an expired start keeps the
        // version that listened and creates no instance.
        if (await broadcasts.IsExpiredAsync(delivery.BroadcastId, cancellationToken))
        {
            return await ExpireLockedDeliveryAsync(transaction, delivery.Id, cancellationToken);
        }

        // A frozen start belongs to the family: it starts the version that is the published
        // default now, if that version still starts on the name.
        var workflow = await definitions.GetDefaultByWorkflowKeyAsync(delivery.WorkflowKey, cancellationToken);
        var startEvent = workflow is null ? null : SignalStartEventFor(workflow.Definition, broadcast.SignalName);
        if (workflow is null || startEvent is null)
        {
            return await FinishDeliveryAsync(
                transaction,
                delivery.Id,
                SignalDeliveryStatuses.Skipped,
                SignalDeliveryResultCodes.SubscriptionEnded,
                $"Workflow '{delivery.WorkflowKey}' has no published default that starts on '{broadcast.SignalName}'.",
                cancellationToken);
        }

        // From here on the start tried this version and node: an outcome without an instance
        // records them on the delivery, as a started one does, and its description names them.
        WorkflowInstanceRecord instance;
        try
        {
            instance = await CreateSignalStartInstanceAsync(
                broadcast,
                delivery,
                workflow,
                startEvent,
                cancellationToken);
        }
        catch (SignalPayloadException exception)
        {
            throw new SignalStartOutcomeException(
                SignalDeliveryStatuses.Failed,
                SignalDeliveryResultCodes.PayloadInvalid,
                exception.Message,
                workflow.Id,
                startEvent.Id);
        }
        catch (BusinessKeyConflictException exception)
        {
            throw new SignalStartOutcomeException(
                SignalDeliveryStatuses.Skipped,
                SignalDeliveryResultCodes.BusinessKeyConflict,
                $"Instance #{exception.ExistingInstanceId} already has this start's business key.",
                workflow.Id,
                startEvent.Id);
        }
        catch (WorkflowConflictException exception)
        {
            // A wait's conflict means its instance moved on; a start has no earlier state,
            // so its conflict fails and can be retried.
            throw new SignalStartOutcomeException(
                SignalDeliveryStatuses.Failed,
                SignalDeliveryResultCodes.DeliveryRejected,
                exception.Message,
                workflow.Id,
                startEvent.Id);
        }
        catch (WorkflowDomainException exception)
        {
            // Engine text: routing runs script and service tasks as Worker jobs
            // (forceDurableActivities), so no task's own failure text reaches a start.
            throw new SignalStartOutcomeException(
                SignalDeliveryStatuses.Failed,
                SignalDeliveryResultCodes.DeliveryRejected,
                exception.Message,
                workflow.Id,
                startEvent.Id);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (!await broadcasts.CompleteStartDeliveryAsync(
                delivery.Id,
                instance.Id,
                workflow.Id,
                startEvent.Id,
                timeProvider.GetUtcNow(),
                cancellationToken))
        {
            throw new InvalidOperationException(
                $"Signal delivery #{delivery.Id} changed while its row was locked.");
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Signal broadcast {BroadcastId} delivery {DeliveryId} started instance {InstanceId}.",
            broadcast.Id, delivery.Id, instance.Id);
        return new SignalDeliveryOutcome(
            SignalDeliveryStatuses.Delivered,
            SignalDeliveryResultCodes.Started,
            null);
    }

    /// <summary>
    /// Steps 4 to 7 of a start: the entry checks, the mappings against the broadcast's payload,
    /// and the new instance with its history row and routing. A payload that fails a mapping
    /// throws <see cref="SignalPayloadException"/>; every other refusal is the engine's own.
    /// </summary>
    private async Task<WorkflowInstanceRecord> CreateSignalStartInstanceAsync(
        SignalBroadcastRecord broadcast,
        SignalDeliveryRecord delivery,
        WorkflowDefinitionRecord workflow,
        FlowNodeModel startEvent,
        CancellationToken cancellationToken)
    {
        var definition = workflow.Definition;
        EnsureEntryRuntimeContract(definition, startEvent);
        EnsureConditionalDefinitionSupported(workflow);
        await EnsureBusinessKeyFamilyStartableAsync(workflow, cancellationToken);
        await EnsureRequiredAssignmentFamilyStartableAsync(workflow, cancellationToken);

        JsonElement? payload = null;
        if (broadcast.PayloadJson is not null)
        {
            using var document = JsonDocument.Parse(broadcast.PayloadJson);
            payload = document.RootElement.Clone();
        }

        // The mappings resolve as a message start's do, in a context without an instance:
        // config, settings, and the sys.* values no caller influences. They read no shared
        // alias, which validation guarantees.
        await LoadSettingsAsync(cancellationToken);
        var actor = SignalActor();
        var resolutionContext = BuildAuthContext(
            new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            instance: null,
            definition,
            startEvent);
        BusinessKeyStartInput normalizedStart;
        Dictionary<string, JsonElement> values;
        try
        {
            (normalizedStart, values) = ResolveStartMappings(
                startEvent,
                startEvent.Signal?.OutputMappings ?? [],
                payload,
                resolutionContext,
                idempotency: null);
        }
        catch (WorkflowDomainException exception)
        {
            throw new SignalPayloadException(exception.Message);
        }

        var history = SignalStartHistoryPayload(broadcast, delivery, startEvent);
        return await CreateStartedInstanceAsync(
            workflow,
            startEvent,
            actor,
            idempotency: null,
            normalizedStart,
            values,
            new StartedInstanceOptions(
                ForceDurableActivities: true,
                AutomaticActivationCount: broadcast.AutomaticActivationCount,
                BeforeRouting: created => runtime.AddTokenHistoryAsync(
                    created.Id,
                    created.ActiveTokenId,
                    null,
                    startEvent.Id,
                    startEvent.Id,
                    actor.User,
                    history,
                    InstanceHistoryNotes.SignalReceived,
                    cancellationToken)),
            cancellationToken);
    }

    /// <summary>The definition's signal start on a name, the first in node order.</summary>
    private static FlowNodeModel? SignalStartEventFor(WorkflowModel definition, string signalName) =>
        definition.FlowNodes
            .Where(node => BpmnFlowNodeTypes.IsSignalStart(node.Type)
                && string.Equals(node.Signal?.Name, signalName, StringComparison.Ordinal))
            .OrderBy(node => node.Id)
            .FirstOrDefault();

    private static Dictionary<string, JsonElement> SignalStartHistoryPayload(
        SignalBroadcastRecord broadcast,
        SignalDeliveryRecord delivery,
        FlowNodeModel startEvent)
    {
        var payload = new Dictionary<string, JsonElement>
        {
            ["broadcastId"] = JsonSerializer.SerializeToElement(broadcast.Id),
            ["signalName"] = JsonSerializer.SerializeToElement(broadcast.SignalName),
            ["deliveryId"] = JsonSerializer.SerializeToElement(delivery.Id),
            ["eventNodeId"] = JsonSerializer.SerializeToElement(startEvent.Id),
            ["eventKind"] = JsonSerializer.SerializeToElement("start"),
            ["thrownBy"] = JsonSerializer.SerializeToElement(broadcast.ThrownBy),
            ["thrownByKind"] = JsonSerializer.SerializeToElement(broadcast.ThrownByKind)
        };
        if (broadcast.ThrowerInstanceId is long throwerInstanceId)
        {
            payload["thrownByInstanceId"] = JsonSerializer.SerializeToElement(throwerInstanceId);
            payload["thrownByNodeId"] = JsonSerializer.SerializeToElement(broadcast.ThrowerNodeId);
        }

        return payload;
    }
}

/// <summary>
/// A start delivery ended without an instance: an instance already had its business key, the
/// payload failed a mapping, or the version it tried refused the start. The start's
/// transaction has rolled back; the processor records the status with the version and node
/// the start tried, when it got that far.
/// </summary>
public sealed class SignalStartOutcomeException(
    string status,
    string resultCode,
    string message,
    long? workflowDefinitionId = null,
    int? signalNodeId = null)
    : InvalidOperationException(message)
{
    public string Status { get; } = status;

    public string ResultCode { get; } = resultCode;

    public long? WorkflowDefinitionId { get; } = workflowDefinitionId;

    public int? SignalNodeId { get; } = signalNodeId;
}
