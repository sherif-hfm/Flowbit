using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

/// <summary>Settings-administrator management of webhook subscriptions and recovery of their deliveries.</summary>
public interface IWebhookManagementService
{
    Task<WebhookEventCatalogDto> GetEventCatalogAsync(CancellationToken cancellationToken);

    Task<PagedResult<WebhookSubscriptionDto>> ListSubscriptionsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<WebhookSubscriptionDetailDto?> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken);

    Task<WebhookSecretResultDto> CreateSubscriptionAsync(
        CreateWebhookSubscriptionRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookSubscriptionDto?> UpdateSubscriptionAsync(
        Guid id,
        UpdateWebhookSubscriptionRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookSubscriptionDto?> PauseAsync(Guid id, string actor, CancellationToken cancellationToken);

    Task<WebhookSubscriptionDto?> ResumeAsync(
        Guid id,
        WebhookRevisionRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookTestResultDto?> SendTestAsync(Guid id, string actor, CancellationToken cancellationToken);

    Task<WebhookSecretResultDto?> RotateSecretAsync(
        Guid id,
        RotateWebhookSecretRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookSubscriptionDto?> RevokePreviousSecretAsync(
        Guid id,
        WebhookRevisionRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookSubscriptionDto?> ArchiveAsync(
        Guid id,
        WebhookRevisionRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookBulkDeliveryResultDto?> BulkRetryAsync(
        Guid subscriptionId,
        WebhookBulkDeliveryRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookBulkDeliveryResultDto?> BulkDiscardAsync(
        Guid subscriptionId,
        WebhookBulkDeliveryRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task<PagedResult<WebhookDeliveryDto>> ListDeliveriesAsync(
        WebhookDeliveryQuery query,
        CancellationToken cancellationToken);

    Task<WebhookDeliveryDetailDto?> GetDeliveryAsync(Guid id, CancellationToken cancellationToken);

    Task<PagedResult<WebhookDeliveryAttemptDto>?> ListAttemptsAsync(
        Guid deliveryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<WebhookDeliveryActionResultDto?> RetryDeliveryAsync(
        Guid id,
        WebhookRevisionRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookDeliveryActionResultDto?> DiscardDeliveryAsync(
        Guid id,
        WebhookRevisionRequest request,
        string actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// Protects subscriber signing secrets with the dedicated, certificate-wrapped <c>Flowbit.Webhooks</c>
/// Data Protection key ring shared by the API and Worker. It never falls back to machine-local or ephemeral keys.
/// </summary>
public interface IWebhookSecretProtector
{
    bool IsAvailable { get; }

    /// <summary>A safe, actionable reason when <see cref="IsAvailable"/> is false.</summary>
    string? UnavailableReason { get; }

    byte[] Protect(byte[] secret);

    byte[] Unprotect(byte[] protectedSecret);
}

/// <summary>Management and query persistence. Every mutation commits its audit entry in the same transaction.</summary>
public interface IWebhookRepository
{
    Task<LifecycleEventCaptureDto> GetCaptureStateAsync(CancellationToken cancellationToken);

    Task<bool> WorkflowKeyExistsAsync(string workflowKey, CancellationToken cancellationToken);

    Task<(IReadOnlyList<WebhookSubscriptionDto> Items, long TotalCount)> ListSubscriptionsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<WebhookSubscriptionDetailDto?> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Creates a paused subscription, its first revision, dispatch state, and current key; enforces the subscription cap.</summary>
    Task<WebhookSubscriptionDto> CreateSubscriptionAsync(
        WebhookSubscriptionCreateCommand command,
        CancellationToken cancellationToken);

    Task<WebhookSubscriptionDto?> UpdateSubscriptionAsync(
        WebhookSubscriptionUpdateCommand command,
        CancellationToken cancellationToken);

    Task<WebhookSubscriptionDto?> PauseAsync(Guid id, string actor, CancellationToken cancellationToken);

    Task<WebhookSubscriptionDto?> ResumeAsync(
        Guid id,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookTestResultDto?> QueueTestAsync(Guid id, string actor, CancellationToken cancellationToken);

    Task<WebhookSubscriptionDto?> RotateKeyAsync(
        WebhookKeyRotationCommand command,
        CancellationToken cancellationToken);

    Task<WebhookSubscriptionDto?> RevokePreviousKeyAsync(
        Guid id,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookSubscriptionDto?> ArchiveAsync(
        Guid id,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookBulkDeliveryResultDto?> BulkRetryAsync(
        Guid subscriptionId,
        WebhookDeliveryFilter filter,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookBulkDeliveryResultDto?> BulkDiscardAsync(
        Guid subscriptionId,
        WebhookDeliveryFilter filter,
        string actor,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<WebhookDeliveryDto> Items, long TotalCount)> ListDeliveriesAsync(
        WebhookDeliveryFilter filter,
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<WebhookDeliveryDetailDto?> GetDeliveryAsync(Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<WebhookDeliveryAttemptDto> Items, long TotalCount)?> ListAttemptsAsync(
        Guid deliveryId,
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<WebhookDeliveryActionResultDto?> RetryDeliveryAsync(
        Guid id,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken);

    Task<WebhookDeliveryActionResultDto?> DiscardDeliveryAsync(
        Guid id,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// The Worker's queue: fanout, leases, send intents, fenced finalization, recovery, and archival settlement.
/// Every operation is a short transaction; no connection is held during remote I/O.
/// </summary>
public interface IWebhookDispatchRepository
{
    /// <summary>Creates deliveries for pending events and marks their fanout complete in the same transaction.</summary>
    Task<WebhookFanoutResult> FanoutAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>Returns expired leases to the queue, closing open attempts as unknown or aborted.</summary>
    Task<int> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>Discards outstanding work of archived subscriptions in bounded batches, then retires their keys.</summary>
    Task<int> SettleArchivedSubscriptionsAsync(
        int batchSize,
        TimeSpan secretRemovalGrace,
        CancellationToken cancellationToken);

    /// <summary>Expires finished overlaps and removes retired secret material whose protection window ended.</summary>
    Task<int> SweepSigningKeysAsync(TimeSpan secretRemovalGrace, CancellationToken cancellationToken);

    Task<IReadOnlyList<WebhookDeliveryLease>> AcquireAsync(
        WebhookAcquireRequest request,
        CancellationToken cancellationToken);

    /// <summary>The protected secrets that sign this subscription's next send: the current key and an unexpired previous key.</summary>
    Task<IReadOnlyList<WebhookProtectedSigningKey>> LoadSigningKeysAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken);

    Task<WebhookSendDecision> PrepareSendAsync(
        WebhookDeliveryLease lease,
        IReadOnlyList<Guid> signingKeyIds,
        string? permanentFailureCode,
        CancellationToken cancellationToken);

    /// <summary>Releases a lease before any send intent, consuming no attempt budget.</summary>
    Task<bool> ReleaseBeforeSendAsync(WebhookDeliveryLease lease, CancellationToken cancellationToken);

    Task<bool> HeartbeatAsync(
        WebhookDeliveryLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<WebhookFinalizeResult> FinalizeAsync(
        WebhookDeliveryLease lease,
        WebhookSendOutcome outcome,
        CancellationToken cancellationToken);

    /// <summary>
    /// The time until a delivery of an eligible subscription, a test, or a backoff end needs a dispatcher, by the
    /// database clock: negative when work is already due, null when nothing is queued.
    /// </summary>
    Task<TimeSpan?> GetTimeUntilNextDueAsync(CancellationToken cancellationToken);

    Task<WebhookQueueSnapshot> GetQueueSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>Sends one signed request to a captured destination under the operator's network policy.</summary>
public interface IWebhookTransport
{
    Task<WebhookSendOutcome> SendAsync(WebhookSendRequest request, CancellationToken cancellationToken);
}
