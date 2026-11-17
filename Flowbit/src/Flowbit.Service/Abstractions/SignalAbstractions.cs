using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Signal subscriptions, written only under the owning instance's lock in the
/// transaction that starts or ends the wait.
/// </summary>
public interface ISignalSubscriptionRepository
{
    Task<IReadOnlyList<SignalSubscriptionRecord>> CreateManyAsync(
        IReadOnlyList<SignalSubscriptionCreateRecord> creates,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SignalSubscriptionRecord>> ListForActivationAsync(
        long tokenId,
        Guid activationId,
        CancellationToken cancellationToken);

    /// <summary>Active subscriptions of an instance, ordered by id.</summary>
    Task<IReadOnlyList<SignalSubscriptionRecord>> ListActiveByInstanceAsync(
        long instanceId,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<SignalSubscriptionRecord?> GetForUpdateAsync(
        long subscriptionId,
        CancellationToken cancellationToken);

    /// <summary>Completes an active subscription; false when it was not active.</summary>
    Task<bool> CompleteAsync(long subscriptionId, CancellationToken cancellationToken);

    /// <summary>
    /// Counts one delivery on a non-interrupting boundary's subscription, which stays
    /// active; false when it was not active.
    /// </summary>
    Task<bool> RecordOccurrenceAsync(long subscriptionId, CancellationToken cancellationToken);

    Task<int> CancelByTokenIdsAsync(
        long instanceId,
        IReadOnlyCollection<long> tokenIds,
        CancellationToken cancellationToken);

    Task<int> CancelByInstanceAsync(long instanceId, CancellationToken cancellationToken);

    /// <summary>Counts of active subscriptions by signal name and workflow family.</summary>
    Task<IReadOnlyList<SignalSubscriptionCountRecord>> SummarizeActiveAsync(
        string? signalName,
        string? workflowKey,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>Broadcasts, their frozen deliveries, and their delivery jobs.</summary>
public interface ISignalBroadcastRepository
{
    Task<SignalBroadcastRecord?> FindByIdempotencyKeyAsync(
        string thrownByKind,
        string thrownBy,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Serializes throws that use one idempotency key, until the transaction ends.</summary>
    Task LockIdempotencyKeyAsync(
        string thrownByKind,
        string thrownBy,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<int> CountOpenAsync(CancellationToken cancellationToken);

    /// <summary>Pending deliveries and the creation time of the oldest broadcast still delivering.</summary>
    Task<SignalBacklogRecord> GetBacklogAsync(CancellationToken cancellationToken);

    Task<SignalBroadcastRecord> AddAsync(
        NewSignalBroadcastRecord broadcast,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a delivery for every active subscription of the name, and of the family
    /// when one is given, in one statement, reading at most <paramref name="limit"/>
    /// plus one. Returns the number inserted.
    /// </summary>
    Task<int> FreezeDeliveriesAsync(
        long broadcastId,
        string signalName,
        string? workflowKey,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>The frozen deliveries grouped by workflow definition.</summary>
    Task<IReadOnlyList<SignalRecipientDefinitionRecord>> ListRecipientDefinitionsAsync(
        long broadcastId,
        CancellationToken cancellationToken);

    /// <summary>Definitions with pending deliveries of the broadcast.</summary>
    Task<IReadOnlyList<SignalRecipientDefinitionRecord>> ListPendingDefinitionsAsync(
        long broadcastId,
        CancellationToken cancellationToken);

    Task SetRecipientsAsync(
        long broadcastId,
        int total,
        string recipientsJson,
        bool recipientsTruncated,
        string status,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task AddJobLinkAsync(
        long broadcastId,
        long workflowDefinitionId,
        long jobId,
        string reason,
        string requestedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<SignalBroadcastRecord?> GetAsync(
        long broadcastId,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<PagedResult<SignalBroadcastRecord>> ListAsync(
        SignalBroadcastQuery query,
        CancellationToken cancellationToken);

    Task<PagedResult<SignalDeliveryRecord>> ListDeliveriesAsync(
        SignalDeliveryQuery query,
        CancellationToken cancellationToken);

    Task<SignalBroadcastCountsRecord> CountDeliveriesAsync(
        long broadcastId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes the counters and, when nothing is pending, the final status. A
    /// cancelled broadcast keeps its status. Returns the updated row. The caller holds
    /// the broadcast's row lock (<see cref="GetAsync"/> with <c>forUpdate</c>) in its
    /// transaction: the counts and the status are read after that lock, so a recount
    /// never overwrites a status committed by a cancel or retry it waited for.
    /// </summary>
    Task<SignalBroadcastRecord?> RefreshCountsAsync(
        long broadcastId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<long>> ListPendingDeliveryIdsAsync(
        long broadcastId,
        long workflowDefinitionId,
        long afterId,
        int limit,
        CancellationToken cancellationToken);

    Task<SignalDeliveryRecord?> GetDeliveryAsync(
        long deliveryId,
        CancellationToken cancellationToken);

    Task<SignalDeliveryRecord?> GetDeliveryForUpdateAsync(
        long deliveryId,
        CancellationToken cancellationToken);

    /// <summary>Finishes a pending delivery; false when it was no longer pending.</summary>
    Task<bool> CompleteDeliveryAsync(
        long deliveryId,
        string status,
        string? resultCode,
        string? resultDescription,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<int> CancelPendingDeliveriesAsync(
        long broadcastId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Returns failed deliveries to pending, and the definitions they belong to.</summary>
    Task<IReadOnlyList<long>> RetryFailedDeliveriesAsync(
        long broadcastId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task MarkCancelledAsync(
        long broadcastId,
        string cancelledBy,
        string? reason,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task ReopenAsync(long broadcastId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Fails the pending deliveries of one definition, for a job whose retries are exhausted.</summary>
    Task<int> FailPendingDeliveriesAsync(
        long broadcastId,
        long workflowDefinitionId,
        string resultCode,
        string resultDescription,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>
/// Applies one delivery inside its own transaction: the engine locks the receiving
/// instance, checks the subscription's fence, maps the payload, and moves the token.
/// </summary>
public interface ISignalDeliveryExecutor
{
    Task<SignalDeliveryOutcome> DeliverSignalAsync(
        long deliveryId,
        CancellationToken cancellationToken);
}

/// <summary>Who throws, reads, cancels, or retries a broadcast.</summary>
public sealed record SignalCaller(
    string Kind,
    string Id,
    IReadOnlyList<string> Roles,
    IReadOnlyDictionary<string, string[]>? AuditClaims)
{
    public bool IsClient => string.Equals(Kind, SignalThrowerKinds.Client, StringComparison.Ordinal);
}

/// <summary>The API commands and queries of signal broadcasts.</summary>
public interface ISignalBroadcastService
{
    /// <summary>Throws a signal. Replayed is true when an idempotency key returned an earlier broadcast.</summary>
    Task<(SignalBroadcastDetailDto Broadcast, bool Replayed)> ThrowAsync(
        ThrowSignalRequest request,
        SignalCaller caller,
        CancellationToken cancellationToken);

    Task<PagedResult<SignalBroadcastSummaryDto>> ListAsync(
        SignalCaller caller,
        string? name,
        string? workflowKey,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<SignalBroadcastDetailDto?> GetAsync(
        SignalCaller caller,
        long broadcastId,
        CancellationToken cancellationToken);

    Task<PagedResult<SignalDeliveryDto>?> ListDeliveriesAsync(
        SignalCaller caller,
        long broadcastId,
        string? status,
        long? instanceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<SignalBroadcastDetailDto?> CancelAsync(
        SignalCaller caller,
        long broadcastId,
        CancelSignalBroadcastRequest request,
        CancellationToken cancellationToken);

    Task<SignalBroadcastDetailDto?> RetryFailedAsync(
        SignalCaller caller,
        long broadcastId,
        RetrySignalDeliveriesRequest request,
        CancellationToken cancellationToken);

    Task<SignalSubscriptionSummaryDto> SummarizeSubscriptionsAsync(
        string? name,
        string? workflowKey,
        CancellationToken cancellationToken);

    /// <summary>Active signal waits of one instance, for instance detail.</summary>
    Task<IReadOnlyList<SignalSubscriptionDto>> ListInstanceSubscriptionsAsync(
        long instanceId,
        CancellationToken cancellationToken);
}

public interface ISignalBroadcastJobProcessor
{
    Task ProcessAsync(WorkflowJobLeaseRecord lease, CancellationToken cancellationToken);
}
