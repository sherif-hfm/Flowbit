using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

/// <summary>
/// Throws signals and manages broadcasts. A throw takes no runtime lock: it freezes its
/// recipients in one statement and leaves delivery to the Worker. A managed API client
/// sees and changes only the broadcasts it threw.
/// </summary>
public sealed class SignalBroadcastService(
    ISignalBroadcastRepository broadcasts,
    ISignalSubscriptionRepository subscriptions,
    IWorkflowDefinitionRepository definitions,
    IWorkflowJobRepository jobs,
    IEngineSettingsRepository engineSettings,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<SignalBroadcastService> logger)
    : ISignalBroadcastService
{
    public const string DeliverPhase = "deliver";

    private static readonly TimeSpan[] DeliveryRetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5)
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(SignalBroadcastDetailDto Broadcast, bool Replayed)> ThrowAsync(
        ThrowSignalRequest request,
        SignalCaller caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        var name = request.Name;
        if (SignalEventRules.ThrowNameProblem(name) is { } nameProblem)
        {
            throw new WorkflowDomainException(nameProblem);
        }

        // A blank key is refused rather than read as "no key", so a request that names a
        // family by mistake cannot reach every workflow.
        var workflowKey = request.WorkflowKey;
        if (workflowKey is not null && string.IsNullOrWhiteSpace(workflowKey))
        {
            throw new WorkflowDomainException(
                "workflowKey cannot be blank; omit it and set allWorkflows to true to reach every workflow.");
        }

        if ((workflowKey is null) == !request.AllWorkflows)
        {
            throw new WorkflowDomainException(
                "A signal must name one workflowKey or set allWorkflows to true, but not both.");
        }

        var payloadJson = NormalizePayload(request.Payload);
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        var requestHash = HashRequest(name!, workflowKey, payloadJson);
        if (idempotencyKey is not null
            && await broadcasts.FindByIdempotencyKeyAsync(caller.Kind, caller.Id, idempotencyKey, cancellationToken)
                is { } earlier)
        {
            return (ReplayOrConflict(earlier, requestHash, caller), true);
        }

        // After the replay check: a retried request still gets its broadcast when the
        // family's last version has been deleted since.
        if (workflowKey is not null
            && (await definitions.ListVersionsByKeyAsync(workflowKey, cancellationToken)).Count == 0)
        {
            throw new WorkflowDomainException($"No workflow definition has the key '{workflowKey}'.");
        }

        var maxRecipients = await ReadLimitAsync(
            SignalSettings.MaxSubscriptionsPerBroadcastKey,
            SignalSettings.DefaultMaxSubscriptionsPerBroadcast,
            SignalSettings.MaxSubscriptionsPerBroadcastCap,
            cancellationToken);
        var maxOpen = await ReadLimitAsync(
            SignalSettings.MaxOpenBroadcastsKey,
            SignalSettings.DefaultMaxOpenBroadcasts,
            SignalSettings.MaxOpenBroadcastsCap,
            cancellationToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (idempotencyKey is not null)
        {
            await broadcasts.LockIdempotencyKeyAsync(caller.Kind, caller.Id, idempotencyKey, cancellationToken);
            if (await broadcasts.FindByIdempotencyKeyAsync(caller.Kind, caller.Id, idempotencyKey, cancellationToken)
                is { } winner)
            {
                await transaction.CommitAsync(cancellationToken);
                return (ReplayOrConflict(winner, requestHash, caller), true);
            }
        }

        // A soft limit: counted without a lock, so no throw queues behind another
        // throw's recipient insert, and concurrent throws may pass it by a few.
        if (await broadcasts.CountOpenAsync(cancellationToken) >= maxOpen)
        {
            throw new WorkflowConflictException(
                $"{maxOpen} signal broadcasts are still delivering; wait for one to finish or cancel one.");
        }

        var broadcast = await broadcasts.AddAsync(
            new NewSignalBroadcastRecord(
                name!,
                workflowKey,
                payloadJson,
                caller.Kind,
                caller.Id,
                JsonSerializer.Serialize(caller.Roles, JsonOptions),
                caller.AuditClaims is null ? null : JsonSerializer.Serialize(caller.AuditClaims, JsonOptions),
                idempotencyKey,
                requestHash,
                now),
            cancellationToken);
        var frozen = await broadcasts.FreezeDeliveriesAsync(
            broadcast.Id,
            name!,
            workflowKey,
            maxRecipients,
            cancellationToken);
        if (frozen > maxRecipients)
        {
            throw new WorkflowDomainException(
                $"Signal '{name}' would reach more than {maxRecipients} waiting subscriptions; "
                + "limit it to one workflow family or raise Signals.MaxSubscriptionsPerBroadcast.");
        }

        var recipients = await broadcasts.ListRecipientDefinitionsAsync(broadcast.Id, cancellationToken);
        var families = recipients
            .GroupBy(recipient => recipient.WorkflowKey, StringComparer.Ordinal)
            .Select(group => new SignalRecipientCountDto(group.Key, group.Sum(recipient => recipient.Count)))
            .OrderByDescending(family => family.Count)
            .ThenBy(family => family.WorkflowKey, StringComparer.Ordinal)
            .ToList();
        await broadcasts.SetRecipientsAsync(
            broadcast.Id,
            frozen,
            JsonSerializer.Serialize(families.Take(SignalConstraints.MaxRecipientFamilies), JsonOptions),
            families.Count > SignalConstraints.MaxRecipientFamilies,
            frozen == 0 ? SignalBroadcastStatuses.Completed : SignalBroadcastStatuses.Delivering,
            now,
            cancellationToken);
        await EnqueueDeliveryJobsAsync(
            broadcast.Id,
            recipients.Select(recipient => recipient.WorkflowDefinitionId).Distinct().ToArray(),
            SignalBroadcastJobReasons.Throw,
            caller.Id,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        SignalRuntimeTelemetry.RecordBroadcast(caller.Kind, frozen);
        logger.LogInformation(
            "Signal broadcast {BroadcastId} froze {RecipientCount} recipients in {FamilyCount} workflow families.",
            broadcast.Id, frozen, families.Count);
        var stored = await broadcasts.GetAsync(broadcast.Id, false, cancellationToken)
            ?? throw new InvalidOperationException($"Signal broadcast #{broadcast.Id} disappeared after it was thrown.");
        return (ToDetail(stored, caller), false);
    }

    public async Task<PagedResult<SignalBroadcastSummaryDto>> ListAsync(
        SignalCaller caller,
        string? name,
        string? workflowKey,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(status) && !SignalBroadcastStatuses.All.Contains(status))
        {
            throw new WorkflowDomainException($"Unknown signal broadcast status '{status}'.");
        }

        var (safePage, safeSize) = ClampPage(page, pageSize);
        var result = await broadcasts.ListAsync(
            new SignalBroadcastQuery(
                string.IsNullOrWhiteSpace(name) ? null : name,
                string.IsNullOrWhiteSpace(workflowKey) ? null : workflowKey,
                string.IsNullOrWhiteSpace(status) ? null : status,
                caller.IsClient ? caller.Kind : null,
                caller.IsClient ? caller.Id : null,
                safePage,
                safeSize),
            cancellationToken);
        return new PagedResult<SignalBroadcastSummaryDto>(
            result.Items.Select(ToSummary).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    public async Task<SignalBroadcastDetailDto?> GetAsync(
        SignalCaller caller,
        long broadcastId,
        CancellationToken cancellationToken)
    {
        var broadcast = await broadcasts.GetAsync(broadcastId, false, cancellationToken);
        return broadcast is null || !IsVisible(broadcast, caller) ? null : ToDetail(broadcast, caller);
    }

    public async Task<PagedResult<SignalDeliveryDto>?> ListDeliveriesAsync(
        SignalCaller caller,
        long broadcastId,
        string? status,
        long? instanceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(status) && !SignalDeliveryStatuses.All.Contains(status))
        {
            throw new WorkflowDomainException($"Unknown signal delivery status '{status}'.");
        }

        var broadcast = await broadcasts.GetAsync(broadcastId, false, cancellationToken);
        if (broadcast is null || !IsVisible(broadcast, caller))
        {
            return null;
        }

        var (safePage, safeSize) = ClampPage(page, pageSize);
        var result = await broadcasts.ListDeliveriesAsync(
            new SignalDeliveryQuery(
                broadcastId,
                string.IsNullOrWhiteSpace(status) ? null : status,
                instanceId,
                safePage,
                safeSize),
            cancellationToken);
        return new PagedResult<SignalDeliveryDto>(
            result.Items.Select(delivery => ToDelivery(delivery, caller)).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    public async Task<SignalBroadcastDetailDto?> CancelAsync(
        SignalCaller caller,
        long broadcastId,
        CancelSignalBroadcastRequest request,
        CancellationToken cancellationToken)
    {
        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? null : request!.Reason!.Trim();
        if (reason is not null && reason.Length > SignalConstraints.MaxReasonLength)
        {
            throw new WorkflowDomainException(
                $"A cancellation reason can have at most {SignalConstraints.MaxReasonLength} characters.");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var broadcast = await broadcasts.GetAsync(broadcastId, true, cancellationToken);
        if (broadcast is null || !IsVisible(broadcast, caller))
        {
            return null;
        }

        if (broadcast.Status == SignalBroadcastStatuses.Cancelled)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToDetail(broadcast, caller);
        }

        if (broadcast.Status != SignalBroadcastStatuses.Delivering)
        {
            throw new WorkflowConflictException(
                $"Signal broadcast #{broadcastId} has already finished and cannot be cancelled.");
        }

        // A delivery inside its own transaction holds its row, so this waits for it and
        // then leaves it as it finished.
        await broadcasts.CancelPendingDeliveriesAsync(broadcastId, now, cancellationToken);
        await broadcasts.MarkCancelledAsync(broadcastId, caller.Id, reason, now, cancellationToken);
        var updated = await broadcasts.RefreshCountsAsync(broadcastId, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Signal broadcast {BroadcastId} was cancelled.", broadcastId);
        return ToDetail(updated!, caller);
    }

    public async Task<SignalBroadcastDetailDto?> RetryFailedAsync(
        SignalCaller caller,
        long broadcastId,
        RetrySignalDeliveriesRequest request,
        CancellationToken cancellationToken)
    {
        if (request?.ExpectedUpdatedAt is not DateTimeOffset expectedUpdatedAt)
        {
            throw new WorkflowDomainException(
                "A retry needs expectedUpdatedAt, the broadcast's updatedAt as last read.");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var broadcast = await broadcasts.GetAsync(broadcastId, true, cancellationToken);
        if (broadcast is null || !IsVisible(broadcast, caller))
        {
            return null;
        }

        if (broadcast.Status != SignalBroadcastStatuses.CompletedWithIssues)
        {
            throw new WorkflowConflictException(
                $"Only a broadcast that finished with failed deliveries can be retried; #{broadcastId} is {broadcast.Status}.");
        }

        if (broadcast.UpdatedAt != expectedUpdatedAt)
        {
            throw new WorkflowConflictException(
                $"Signal broadcast #{broadcastId} changed since it was read. Refresh and try again.");
        }

        var definitionIds = await broadcasts.RetryFailedDeliveriesAsync(broadcastId, now, cancellationToken);
        if (definitionIds.Count == 0)
        {
            throw new WorkflowConflictException($"Signal broadcast #{broadcastId} has no failed deliveries.");
        }

        await broadcasts.ReopenAsync(broadcastId, now, cancellationToken);
        await EnqueueDeliveryJobsAsync(
            broadcastId,
            definitionIds,
            SignalBroadcastJobReasons.Retry,
            caller.Id,
            now,
            cancellationToken);
        var updated = await broadcasts.RefreshCountsAsync(broadcastId, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Signal broadcast {BroadcastId} queued its failed deliveries again in {JobCount} jobs.",
            broadcastId, definitionIds.Count);
        return ToDetail(updated!, caller);
    }

    public async Task<SignalSubscriptionSummaryDto> SummarizeSubscriptionsAsync(
        string? name,
        string? workflowKey,
        CancellationToken cancellationToken)
    {
        var rows = await subscriptions.SummarizeActiveAsync(
            string.IsNullOrWhiteSpace(name) ? null : name,
            string.IsNullOrWhiteSpace(workflowKey) ? null : workflowKey,
            SignalConstraints.MaxSummaryRows + 1,
            cancellationToken);
        return new SignalSubscriptionSummaryDto(
            rows.Take(SignalConstraints.MaxSummaryRows)
                .Select(row => new SignalSubscriptionCountDto(row.SignalName, row.WorkflowKey, row.Count))
                .ToArray(),
            rows.Count > SignalConstraints.MaxSummaryRows);
    }

    public async Task<IReadOnlyList<SignalSubscriptionDto>> ListInstanceSubscriptionsAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var active = await subscriptions.ListActiveByInstanceAsync(instanceId, false, cancellationToken);
        return active
            .Take(SignalConstraints.MaxInstanceSubscriptions)
            .Select(subscription => new SignalSubscriptionDto(
                subscription.Id,
                subscription.SignalName,
                subscription.SignalNodeId,
                subscription.SignalNodeName,
                subscription.AttachedToNodeId,
                subscription.CancelActivity,
                subscription.TokenId,
                subscription.CreatedAt))
            .ToArray();
    }

    private async Task EnqueueDeliveryJobsAsync(
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
        foreach (var definitionId in definitionIds.Order())
        {
            var definition = records.GetValueOrDefault(definitionId)
                ?? throw new InvalidOperationException($"Workflow definition #{definitionId} does not exist.");
            // The job names the definition's initial start event only to anchor its row;
            // it reports no node in incident lifecycle events.
            var anchor = definition.Definition.FlowNodes.FirstOrDefault(node =>
                             node.Id == definition.Definition.InitialEventId)
                         ?? definition.Definition.FlowNodes.First();
            var job = await jobs.EnqueueAsync(
                new WorkflowJobCreateRecord
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
                },
                cancellationToken);
            await broadcasts.AddJobLinkAsync(
                broadcastId,
                definition.Id,
                job.Id,
                reason,
                requestedBy,
                now,
                cancellationToken);
        }
    }

    private async Task<int> ReadLimitAsync(string key, int fallback, int cap, CancellationToken cancellationToken)
    {
        var setting = await engineSettings.GetByKeyAsync(key, cancellationToken);
        return SignalSettings.ParseLimit(setting?.Value, fallback, cap);
    }

    private static string? NormalizePayload(JsonElement? payload)
    {
        if (payload is not { } value
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new WorkflowDomainException("A signal payload must be a JSON object.");
        }

        if (JsonbText.ByteCount(value) > SignalConstraints.MaxPayloadBytes)
        {
            throw new WorkflowDomainException(
                $"A signal payload can have at most {SignalConstraints.MaxPayloadBytes} bytes.");
        }

        return value.GetRawText();
    }

    private static string? NormalizeIdempotencyKey(string? value)
    {
        var key = value?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (key.Length > SignalConstraints.MaxIdempotencyKeyLength)
        {
            throw new WorkflowDomainException(
                $"IdempotencyKey can have at most {SignalConstraints.MaxIdempotencyKeyLength} characters.");
        }

        return key;
    }

    private static byte[] HashRequest(string name, string? workflowKey, string? payloadJson)
    {
        // The payload is compared as parsed JSON, so whitespace and member order in the
        // replayed request do not count as a different request.
        var canonicalPayload = payloadJson is null
            ? "null"
            : CanonicalJson(JsonDocument.Parse(payloadJson).RootElement);
        var text = string.Join('\n', name, workflowKey ?? "*", canonicalPayload);
        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }

    private static string CanonicalJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(CanonicalJson)) + "]",
        _ => value.GetRawText()
    };

    private static SignalBroadcastDetailDto ReplayOrConflict(
        SignalBroadcastRecord earlier,
        byte[] requestHash,
        SignalCaller caller)
    {
        if (earlier.RequestHash is null
            || !CryptographicOperations.FixedTimeEquals(earlier.RequestHash, requestHash))
        {
            throw new WorkflowConflictException(
                "IdempotencyKey was already used for a different signal broadcast.");
        }

        return ToDetail(earlier, caller);
    }

    private static bool IsVisible(SignalBroadcastRecord broadcast, SignalCaller caller) =>
        !caller.IsClient
        || (string.Equals(broadcast.ThrownByKind, caller.Kind, StringComparison.Ordinal)
            && string.Equals(broadcast.ThrownBy, caller.Id, StringComparison.Ordinal));

    // The endpoints pass 50 when pageSize is omitted; a given value is clamped to 1-200.
    private static (int Page, int PageSize) ClampPage(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));

    private static SignalBroadcastSummaryDto ToSummary(SignalBroadcastRecord broadcast) =>
        new(
            broadcast.Id,
            broadcast.SignalName,
            broadcast.WorkflowKey,
            broadcast.WorkflowKey is null,
            broadcast.Status,
            broadcast.TotalDeliveryCount,
            broadcast.PendingCount,
            broadcast.DeliveredCount,
            broadcast.SkippedCount,
            broadcast.FailedCount,
            broadcast.CancelledCount,
            broadcast.ThrownBy,
            broadcast.ThrownByKind,
            broadcast.CreatedAt,
            broadcast.UpdatedAt,
            broadcast.CompletedAt);

    private static SignalBroadcastDetailDto ToDetail(SignalBroadcastRecord broadcast, SignalCaller caller)
    {
        JsonElement? payload = null;
        if (broadcast.PayloadJson is not null)
        {
            using var document = JsonDocument.Parse(broadcast.PayloadJson);
            payload = document.RootElement.Clone();
        }

        var roles = JsonSerializer.Deserialize<string[]>(broadcast.ThrownByRolesJson, JsonOptions) ?? [];
        var recipients = JsonSerializer.Deserialize<SignalRecipientCountDto[]>(broadcast.RecipientsJson, JsonOptions)
            ?? [];
        // A managed client sees its own cancellation, but not which user cancelled its
        // broadcast or why: like a result description, that is the deployment's to read.
        var hideCancellation = caller.IsClient
            && !string.Equals(broadcast.CancelledBy, caller.Id, StringComparison.Ordinal);
        return new SignalBroadcastDetailDto(
            broadcast.Id,
            broadcast.SignalName,
            broadcast.WorkflowKey,
            broadcast.WorkflowKey is null,
            broadcast.Status,
            broadcast.TotalDeliveryCount,
            broadcast.PendingCount,
            broadcast.DeliveredCount,
            broadcast.SkippedCount,
            broadcast.FailedCount,
            broadcast.CancelledCount,
            broadcast.ThrownBy,
            broadcast.ThrownByKind,
            roles,
            payload,
            recipients,
            broadcast.RecipientsTruncated,
            hideCancellation ? null : broadcast.CancelledBy,
            hideCancellation ? null : broadcast.CancellationReason,
            broadcast.CreatedAt,
            broadcast.UpdatedAt,
            broadcast.CompletedAt,
            broadcast.CancelledAt);
    }

    private static SignalDeliveryDto ToDelivery(SignalDeliveryRecord delivery, SignalCaller caller) =>
        new(
            delivery.Id,
            delivery.BroadcastId,
            delivery.InstanceId,
            delivery.WorkflowKey,
            delivery.WorkflowDefinitionId,
            delivery.SignalNodeId,
            delivery.AttachedToNodeId,
            delivery.CancelActivity,
            delivery.Status,
            delivery.ResultCode,
            caller.IsClient ? null : delivery.ResultDescription,
            delivery.AttemptCount,
            delivery.CreatedAt,
            delivery.UpdatedAt,
            delivery.CompletedAt);
}
