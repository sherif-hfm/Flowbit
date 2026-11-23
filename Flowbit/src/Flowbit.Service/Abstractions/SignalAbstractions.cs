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

    /// <summary>
    /// Counts of active subscriptions and of start subscriptions by signal name and workflow
    /// family, in name and family order, cut at the limit after they are merged; optionally
    /// for one family, the instance with one business key in it, or one instance, which two
    /// filters count no start. Takes no lock.
    /// </summary>
    Task<IReadOnlyList<SignalSubscriptionCountRecord>> SummarizeActiveAsync(
        string? signalName,
        SignalSubscriptionFilter filter,
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

    /// <summary>
    /// The broadcasts of the given thrower kinds still delivering, counted without a lock up
    /// to <paramref name="stopAt"/>: a budget needs to know only whether it is used up.
    /// </summary>
    Task<int> CountOpenAsync(IReadOnlyList<string> thrownByKinds, int stopAt, CancellationToken cancellationToken);

    /// <summary>Pending deliveries and the creation time of the oldest broadcast still delivering.</summary>
    Task<SignalBacklogRecord> GetBacklogAsync(CancellationToken cancellationToken);

    Task<SignalBroadcastRecord> AddAsync(
        NewSignalBroadcastRecord broadcast,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a broadcast inserted in the current transaction, with its frozen deliveries, so a
    /// throw refused after the freeze leaves nothing a caller that catches the refusal could commit.
    /// </summary>
    Task DeleteAsync(long broadcastId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a delivery for every active subscription of the name within the scope, in
    /// one statement that takes no runtime lock, and for a family or all-workflows scope one
    /// start delivery per listening family, in a second statement that share-locks the start
    /// subscription rows it reads. Reads at most <paramref name="limit"/> plus one in all, and
    /// returns how many waits and starts it inserted.
    /// </summary>
    Task<SignalFreezeResult> FreezeDeliveriesAsync(
        long broadcastId,
        string signalName,
        SignalBroadcastScope scope,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// The workflow key of an instance, read without a lock; null when no instance has the ID.
    /// </summary>
    Task<string?> FindInstanceWorkflowKeyAsync(long instanceId, CancellationToken cancellationToken);

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
        int startCount,
        string recipientsJson,
        bool recipientsTruncated,
        string status,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Links a broadcast to its delivery jobs, one per workflow definition, in one save.</summary>
    Task AddJobLinksAsync(
        long broadcastId,
        IReadOnlyList<(long WorkflowDefinitionId, long JobId)> links,
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

    /// <summary>
    /// Finishes a pending start delivery as delivered with <c>started</c>, naming the instance
    /// it created and the version and event that started it; false when it was no longer pending.
    /// </summary>
    Task<bool> CompleteStartDeliveryAsync(
        long deliveryId,
        long instanceId,
        long workflowDefinitionId,
        int signalNodeId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finishes a pending start delivery without an instance, naming the version and event
    /// that the start tried; false when it was no longer pending.
    /// </summary>
    Task<bool> CompleteStartWithoutInstanceAsync(
        long deliveryId,
        string status,
        string resultCode,
        string? resultDescription,
        long workflowDefinitionId,
        int signalNodeId,
        DateTimeOffset now,
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

    /// <summary>
    /// Returns failed deliveries to pending, and the definitions the pending ones belong to.
    /// A failed wait moves to the version its instance runs now. A failed start moves to the
    /// version and event of its family's start subscription, which it share-locks, or ends as
    /// skipped when its family no longer starts on the name.
    /// </summary>
    Task<SignalRetryResult> RetryFailedDeliveriesAsync(
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

    /// <summary>
    /// A page of broadcasts, newest first. <paramref name="thrownByKinds"/> keeps only the
    /// broadcasts of those thrower kinds (user, client, instance); empty keeps every kind.
    /// </summary>
    Task<PagedResult<SignalBroadcastSummaryDto>> ListAsync(
        SignalCaller caller,
        string? name,
        string? workflowKey,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? thrownByKinds = null);

    Task<SignalBroadcastDetailDto?> GetAsync(
        SignalCaller caller,
        long broadcastId,
        CancellationToken cancellationToken);

    /// <summary>
    /// A page of a broadcast's deliveries, optionally of one status, instance, or kind
    /// (<c>wait</c> or <c>start</c>); null when the caller cannot see the broadcast.
    /// </summary>
    Task<PagedResult<SignalDeliveryDto>?> ListDeliveriesAsync(
        SignalCaller caller,
        long broadcastId,
        string? status,
        long? instanceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        string? kind = null);

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
        string? businessKey,
        long? instanceId,
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
