using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

/// <summary>One broadcast for <see cref="SignalBroadcastWriter"/> to write.</summary>
/// <param name="Broadcast">The row to insert, with its thrower and chain count.</param>
/// <param name="MaxRecipients">The most waits the throw may reach.</param>
/// <param name="RequestedBy">The text recorded on each delivery job link.</param>
public sealed record SignalBroadcastWrite(
    NewSignalBroadcastRecord Broadcast,
    int MaxRecipients,
    string RequestedBy);

/// <summary>
/// The outcome of a write. <see cref="RecipientCount"/> counts waits and family starts, of
/// which <see cref="StartCount"/> are starts. When <see cref="RecipientLimitExceeded"/> is
/// true nothing was kept: the broadcast and its frozen deliveries were deleted in the same
/// transaction.
/// </summary>
public sealed record SignalBroadcastWriteResult(
    long BroadcastId,
    int RecipientCount,
    int FamilyCount,
    bool RecipientLimitExceeded,
    int StartCount = 0);

/// <summary>
/// Writes one broadcast inside the caller's transaction: it inserts the broadcast, freezes
/// its recipients in one statement without a runtime lock, stores the counts and the
/// status, and enqueues one delivery job per represented workflow definition. The API's
/// throw and a workflow's throw or end event both use it; only the thrower and the
/// transaction differ. Nothing here locks an existing row, so a throw never waits for
/// another instance.
/// </summary>
public sealed class SignalBroadcastWriter(
    ISignalBroadcastRepository broadcasts,
    IWorkflowDefinitionRepository definitions,
    IWorkflowJobRepository jobs)
{
    public const string DeliverPhase = "deliver";

    private static readonly TimeSpan[] DeliveryRetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5)
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SignalBroadcastWriteResult> WriteAsync(
        SignalBroadcastWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var broadcast = await broadcasts.AddAsync(write.Broadcast, cancellationToken);
        var frozen = await broadcasts.FreezeDeliveriesAsync(
            broadcast.Id,
            write.Broadcast.SignalName,
            write.Broadcast.Scope,
            write.MaxRecipients,
            cancellationToken);
        var total = frozen.Total;
        if (total > write.MaxRecipients)
        {
            // The limit is known only after the freeze. Deleting here means a caller that
            // catches the refusal and commits keeps no part of the broadcast.
            await broadcasts.DeleteAsync(broadcast.Id, cancellationToken);
            return new SignalBroadcastWriteResult(broadcast.Id, total, 0, RecipientLimitExceeded: true);
        }

        var recipients = await broadcasts.ListRecipientDefinitionsAsync(broadcast.Id, cancellationToken);
        var families = recipients
            .GroupBy(recipient => recipient.WorkflowKey, StringComparer.Ordinal)
            .Select(group => new SignalRecipientCountDto(
                group.Key,
                group.Sum(recipient => recipient.Count),
                group.Sum(recipient => recipient.StartCount)))
            .OrderByDescending(family => family.Count)
            .ThenBy(family => family.WorkflowKey, StringComparer.Ordinal)
            .ToList();
        await broadcasts.SetRecipientsAsync(
            broadcast.Id,
            total,
            frozen.Starts,
            JsonSerializer.Serialize(families.Take(SignalConstraints.MaxRecipientFamilies), JsonOptions),
            families.Count > SignalConstraints.MaxRecipientFamilies,
            total == 0 ? SignalBroadcastStatuses.Completed : SignalBroadcastStatuses.Delivering,
            write.Broadcast.CreatedAt,
            cancellationToken);
        await EnqueueDeliveryJobsAsync(
            broadcast.Id,
            recipients.Select(recipient => recipient.WorkflowDefinitionId).Distinct().ToArray(),
            SignalBroadcastJobReasons.Throw,
            write.RequestedBy,
            write.Broadcast.CreatedAt,
            cancellationToken);
        return new SignalBroadcastWriteResult(
            broadcast.Id,
            total,
            families.Count,
            RecipientLimitExceeded: false,
            StartCount: frozen.Starts);
    }

    /// <summary>Enqueues one delivery job per workflow definition, for a throw or a retry.</summary>
    public async Task EnqueueDeliveryJobsAsync(
        long broadcastId,
        IReadOnlyCollection<long> definitionIds,
        string reason,
        string requestedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (definitionIds.Count == 0)
        {
            return;
        }

        var records = await definitions.GetManyAsync(definitionIds, cancellationToken);
        // One insert of all jobs and one of their links: a throw to every workflow can reach
        // a definition per listening family, and a save per job made it grow with their square.
        var ordered = definitionIds.Order().ToArray();
        var creates = ordered.Select(definitionId =>
        {
            var definition = records.GetValueOrDefault(definitionId)
                ?? throw new InvalidOperationException($"Workflow definition #{definitionId} does not exist.");
            // The job names the definition's initial start event only to anchor its row;
            // it reports no node in incident lifecycle events.
            var anchor = definition.Definition.FlowNodes.FirstOrDefault(node =>
                             node.Id == definition.Definition.InitialEventId)
                         ?? definition.Definition.FlowNodes.First();
            return new WorkflowJobCreateRecord
            {
                WorkflowDefinitionId = definition.Id,
                WorkflowKey = definition.WorkflowKey,
                ActivationId = Guid.NewGuid(),
                NodeId = anchor.Id,
                NodeName = anchor.Name,
                NodeType = anchor.Type,
                Kind = WorkflowJobKinds.SignalBroadcastDeliver,
                QueueClass = WorkflowJobClasses.Activity,
                Phase = DeliverPhase,
                DueAt = now,
                MaxAttempts = DeliveryRetryDelays.Length + 1,
                RetryDelays = DeliveryRetryDelays,
                FailureHandling = WorkflowJobFailureHandling.RetryFirst,
                Payload = JsonSerializer.SerializeToElement(
                    new SignalBroadcastJobPayload(broadcastId, definition.Id))
            };
        }).ToList();
        var created = await jobs.EnqueueManyAsync(creates, cancellationToken);
        await broadcasts.AddJobLinksAsync(
            broadcastId,
            created.Select(job => (job.WorkflowDefinitionId, job.Id)).ToList(),
            reason,
            requestedBy,
            now,
            cancellationToken);
    }
}
