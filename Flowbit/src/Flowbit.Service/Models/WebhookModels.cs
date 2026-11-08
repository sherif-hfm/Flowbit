using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Models;

/// <summary>Limits and fixed policy of outbound lifecycle webhooks (INT-01).</summary>
public static class WebhookConstraints
{
    public const int SchemaVersion = 1;
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 1000;
    public const int MaxEndpointUrlLength = 2048;
    public const int MaxWorkflowKeyLength = 300;
    public const int MaxActorLength = UserTaskConstraints.MaxActorNameLength;
    public const int DefaultMaxConcurrency = 4;
    public const int MinMaxConcurrency = 1;
    public const int MaxMaxConcurrency = 16;

    /// <summary>Non-archived subscriptions, enforced atomically when one is created.</summary>
    public const int MaxNonArchivedSubscriptions = 100;

    /// <summary>The bound on a stored event body. Catalog field limits keep every body far below it.</summary>
    public const int MaxBodyBytes = 16 * 1024;

    /// <summary>Send intents allowed in one retry generation.</summary>
    public const int MaxAttemptsPerGeneration = 12;

    /// <summary>How long one retry generation may keep sending after its first send intent.</summary>
    public static readonly TimeSpan GenerationWindow = TimeSpan.FromHours(72);

    public const int DefaultOverlapSeconds = 3600;
    public const int MaxOverlapSeconds = 86_400;
    public const int MaxBulkDeliveries = 1000;
    public const int SecretBytes = 32;
    public const string SecretPrefix = "whsec_";

    /// <summary>Consecutive retryable outcomes that start receiver backoff.</summary>
    public const int BackoffFailureThreshold = 5;

    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    /// <summary>Continuous backoff without a success after which a subscription pauses automatically.</summary>
    public static readonly TimeSpan AutomaticPauseAfter = TimeSpan.FromHours(72);

    /// <summary>The cap applied to a receiver's Retry-After hint.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// The delay before retry n (1-based) of a generation, before jitter. Twelve sends fit inside the 72-hour
    /// window: the delays total about 64 hours, and at most 70 with the ±10% jitter.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(4),
        TimeSpan.FromHours(8),
        TimeSpan.FromHours(12),
        TimeSpan.FromHours(16),
        TimeSpan.FromHours(20)
    ];
}

/// <summary>
/// One catalog entry: what a lifecycle event announces and its data fields in body order. The PostgreSQL
/// capture functions write fields in exactly this order.
/// </summary>
public sealed record LifecycleEventCatalogEntry(
    string Type,
    string Description,
    bool Subscribable,
    IReadOnlyList<LifecycleEventFieldDto> DataFields);

/// <summary>
/// The names, descriptions, and field lists of every lifecycle event, for the event-types route and the
/// golden-body tests. The capture triggers remain the only producer of event bodies.
/// </summary>
public static class LifecycleEventCatalog
{
    private static readonly LifecycleEventFieldDto[] TaskFields =
    [
        new("userTaskId", "integer", false, "The work item. A multi-instance child is its own work item."),
        new("executionTokenId", "integer", false, "The execution token the work item belongs to."),
        new("nodeId", "integer", false, "The version-scoped node id of the user task."),
        new("nodeExternalId", "string", true, "The node's version-stable external id; prefer it over nodeId."),
        new("nodeExecutionId", "integer", true, "The node visit; null for visits recorded before node executions existed."),
        new("multiInstanceExecutionId", "integer", true, "The multi-instance execution of a child; null for a normal task."),
        new("itemIndex", "integer", true, "The zero-based multi-instance item index; null for a normal task."),
        new("taskStatus", "string", false, "The work item's status after the transition.")
    ];

    public static IReadOnlyList<LifecycleEventFieldDto> EnvelopeFields { get; } =
    [
        new("schemaVersion", "integer", false, "The body schema version; 1 in this release."),
        new("eventId", "string", false, "The event's UUIDv7. Every attempt for every subscription sends it as webhook-id."),
        new("type", "string", false, "The case-sensitive event type."),
        new("timestamp", "string", false, "The ISO 8601 UTC transition time with microseconds, taken from the source row."),
        new("workflowKey", "string", true, "The workflow family; null only for webhook.test."),
        new("workflowDefinitionId", "integer", true, "The workflow version's definition id when the transition committed."),
        new("workflowVersion", "integer", true, "The workflow version number when the transition committed."),
        new("instanceId", "integer", true, "The workflow instance; null for delegations, global incidents, and tests."),
        new("data", "object", false, "The event type's fields, listed in this catalog.")
    ];

    public static IReadOnlyList<LifecycleEventCatalogEntry> Entries { get; } =
    [
        new(LifecycleEventTypes.InstanceStarted,
            "A new workflow instance was inserted: an ordinary, message, timer, or child-call start.",
            true,
            [new("status", "string", false, "Always running.")]),
        new(LifecycleEventTypes.InstanceCompleted,
            "The instance status changed to completed, including a terminate end.",
            true,
            [new("status", "string", false, "Always completed.")]),
        new(LifecycleEventTypes.InstanceFaulted,
            "The instance status changed to faulted through an error end or an uncaught child failure.",
            true,
            [new("status", "string", false, "Always faulted.")]),
        new(LifecycleEventTypes.InstanceCancelled,
            "The instance status changed to cancelled by a user, an administrator, or call propagation.",
            true,
            [new("status", "string", false, "Always cancelled.")]),
        new(LifecycleEventTypes.InstanceReactivated,
            "A completed or cancelled instance returned to running through terminal reactivation.",
            true,
            [
                new("status", "string", false, "Always running."),
                new("previousStatus", "string", false, "completed or cancelled.")
            ]),
        new(LifecycleEventTypes.TaskCreated,
            "A work item became active: inserted active, activated from pending, or completed directly from pending.",
            true,
            TaskFields),
        new(LifecycleEventTypes.TaskAssigned,
            "A non-null assignee was first set or changed to a different actor. Unassignment and claims are excluded.",
            true,
            [
                .. TaskFields,
                new("assignmentOperation", "string", false, "assigned or reassigned.")
            ]),
        new(LifecycleEventTypes.TaskCompleted,
            "A work item's status changed to completed.",
            true,
            TaskFields),
        new(LifecycleEventTypes.TaskCancelled,
            "A work item changed to cancelled from active, or from pending when it had an assignee.",
            true,
            [
                .. TaskFields,
                new("previousTaskStatus", "string", false, "active or pending.")
            ]),
        new(LifecycleEventTypes.DelegationGranted,
            "A standing delegation grant was persisted for a workflow family.",
            true,
            [
                new("delegationId", "integer", false, "The grant."),
                new("acceptanceState", "string", false, "pending or notRequired.")
            ]),
        new(LifecycleEventTypes.IncidentOpened,
            "A new incident row was inserted for a durable job, including timer-start incidents without an instance.",
            true,
            [
                new("incidentId", "integer", false, "The incident."),
                new("jobId", "integer", false, "The incident's original job, retained after retention detaches the live job."),
                new("executionTokenId", "integer", true, "The source job's execution token; null for a job without one."),
                new("nodeId", "integer", true, "The node the job served; null when no authored node applies, as for version-change and variable-update batch jobs. An administrative batch job reports the user task its batch acts on."),
                new("nodeExternalId", "string", true, "The node's external id from the workflow definition; null when nodeId is null, when the node has none, or when it is longer than the 300 characters a running node can hold."),
                new("status", "string", false, "Always open.")
            ]),
        new(LifecycleEventTypes.WebhookTest,
            "A signed test event queued for one subscription by the test route, delivered even while paused.",
            false,
            [new("subscriptionId", "string", false, "The subscription that requested the test.")])
    ];

    public static LifecycleEventCatalogEntry? Find(string type) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Type, type, StringComparison.Ordinal));

    public static bool IsSubscribable(string type) =>
        LifecycleEventTypes.Subscribable.Contains(type, StringComparer.Ordinal);
}

public sealed record WebhookDeliveryQuery(
    Guid? SubscriptionId,
    Guid? EventId,
    string? EventType,
    string? WorkflowKey,
    long? InstanceId,
    long? RevisionId,
    string? State,
    string? FailureCode,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize);

/// <summary>A validated delivery filter; <c>From</c> is inclusive and <c>To</c> exclusive on the event time.</summary>
public sealed record WebhookDeliveryFilter(
    Guid? SubscriptionId,
    Guid? EventId,
    string? EventType,
    string? WorkflowKey,
    long? InstanceId,
    long? RevisionId,
    IReadOnlyList<string>? States,
    string? FailureCode,
    DateTimeOffset? From,
    DateTimeOffset? To);

public sealed record WebhookSubscriptionCreateCommand(
    Guid Id,
    string Name,
    string? Description,
    string EndpointUrl,
    string DestinationOrigin,
    IReadOnlyList<string> EventTypes,
    string? WorkflowKey,
    int MaxConcurrency,
    Guid KeyId,
    byte[] ProtectedSecret,
    string Actor)
{
    // The URL's path and query can carry a receiver token, so the string form omits every member.
    public override string ToString() => nameof(WebhookSubscriptionCreateCommand);
}

public sealed record WebhookSubscriptionUpdateCommand(
    Guid Id,
    string Name,
    string? Description,
    string EndpointUrl,
    string DestinationOrigin,
    IReadOnlyList<string> EventTypes,
    string? WorkflowKey,
    int MaxConcurrency,
    long ExpectedRevision,
    string Actor)
{
    public override string ToString() => nameof(WebhookSubscriptionUpdateCommand);
}

public sealed record WebhookKeyRotationCommand(
    Guid SubscriptionId,
    long ExpectedRevision,
    Guid KeyId,
    byte[] ProtectedSecret,
    int OverlapSeconds,
    string Actor);

public sealed record WebhookFanoutResult(int Events, int Deliveries);

public sealed record WebhookAcquireRequest(
    string WorkerId,
    int MaxCount,
    TimeSpan LeaseDuration,
    int MaxSubscriptions);

/// <summary>An acquired delivery: its captured destination, immutable body, and the lease fence.</summary>
public sealed record WebhookDeliveryLease(
    Guid DeliveryId,
    Guid EventId,
    Guid SubscriptionId,
    long RevisionId,
    bool IsTest,
    string EventType,
    string EndpointUrl,
    byte[] Body,
    Guid LeaseToken,
    long LeaseGeneration,
    long AttemptId,
    int AttemptNumber,
    int RetryGeneration,
    DateTimeOffset LeaseExpiresAt)
{
    public override string ToString() => nameof(WebhookDeliveryLease);
}

public sealed record WebhookProtectedSigningKey(Guid KeyId, string Status, byte[] ProtectedSecret);

public enum WebhookSendDecisionKind
{
    /// <summary>The send intent is recorded; the request must now be sent.</summary>
    Send,

    /// <summary>A pause or shutdown came first: the lease is released and no budget is used.</summary>
    Aborted,

    /// <summary>The delivery failed or was discarded without a request.</summary>
    Settled,

    /// <summary>The lease no longer belongs to this Worker.</summary>
    LeaseLost
}

/// <summary>What prepare-send decided; a send carries the loaded keys that may still sign, current first.</summary>
public sealed record WebhookSendDecision(WebhookSendDecisionKind Kind, IReadOnlyList<Guid>? SigningKeyIds = null);

public sealed record WebhookSendRequest(
    Uri Destination,
    Guid EventId,
    byte[] Body,
    IReadOnlyList<byte[]> SigningKeys,
    TimeSpan Timeout)
{
    public override string ToString() => nameof(WebhookSendRequest);
}

/// <summary>A safe attempt result: an outcome code, the HTTP status when headers arrived, and a receiver Retry-After hint.</summary>
public sealed record WebhookSendOutcome(
    string Outcome,
    int? HttpStatus,
    TimeSpan? RetryAfter,
    int DurationMilliseconds);

public sealed record WebhookFinalizeResult(bool Applied, string? State, bool AutomaticallyPaused);

public sealed record WebhookQueueSnapshot(
    bool CaptureEnabled,
    long PendingFanout,
    DateTimeOffset? OldestPendingFanoutAt,
    long Pending,
    long RetryScheduled,
    long Delivering,
    long Failed,
    DateTimeOffset? OldestDueAt,
    int BackingOffSubscriptions,
    int PausedSubscriptions);
