using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Npgsql;
using NpgsqlTypes;
using static Flowbit.Infrastructure.Repositories.WebhookRepository;

namespace Flowbit.Infrastructure.Repositories;

/// <summary>
/// The Worker's webhook queue. Every method is one short transaction using the PostgreSQL clock, and none spans
/// DNS, signing, or HTTP. Rows locked together follow subscription, dispatch state, signing key, delivery,
/// attempt; acquisition normally locks only dispatch state, so delivery never blocks management edits. Fanout
/// locks its batch of events and then share-locks their subscriptions, so it waits only for a management
/// transaction already in progress on one of them.
/// </summary>
public sealed class WebhookDispatchRepository(WebhookDataSource source) : IWebhookDispatchRepository
{
    public async Task<WebhookFanoutResult> FanoutAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int events;
        int deliveries;
        // No highest-seen cursor: source transactions commit out of order. A crash commits
        // the deliveries and the completion marker together or neither.
        await using (var command = Command(connection, transaction, """
            WITH batch AS MATERIALIZED (
                SELECT e."Id", e."TargetRevisionIds", e."Type", e."WorkflowKey", e."InstanceId", e."OccurredAt"
                FROM flowbit.lifecycle_events e
                WHERE e."FanoutCompletedAt" IS NULL
                ORDER BY e."RecordedAt", e."Id"
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            ), targets AS MATERIALIZED (
                SELECT b."Id" AS event_id, b."Type", b."WorkflowKey", b."InstanceId", b."OccurredAt",
                       revision."Id" AS revision_id, revision."SubscriptionId" AS subscription_id
                FROM batch b
                CROSS JOIN LATERAL unnest(b."TargetRevisionIds") AS target(revision_id)
                JOIN flowbit.webhook_subscription_revisions revision ON revision."Id" = target.revision_id
            ), subscriptions AS MATERIALIZED (
                -- A share lock waits for an archive in progress and then reads its committed state, so a
                -- delivery is never left pending for a subscription the coordinator has already settled.
                SELECT s."Id", s."State"
                FROM flowbit.webhook_subscriptions s
                WHERE s."Id" IN (SELECT subscription_id FROM targets)
                ORDER BY s."Id"
                FOR SHARE
            ), inserted AS (
                INSERT INTO flowbit.webhook_deliveries
                    ("Id", "EventId", "SubscriptionId", "RevisionId", "IsTest", "EventType", "WorkflowKey",
                     "InstanceId", "OccurredAt", "State", "DueAt", "RetryGeneration", "GenerationAttempts",
                     "LifetimeAttempts", "LeaseGeneration", "SettledAt", "Revision", "CreatedAt", "UpdatedAt")
                SELECT flowbit.uuid_v7(), t.event_id, t.subscription_id, t.revision_id, FALSE, t."Type",
                       t."WorkflowKey", t."InstanceId", t."OccurredAt",
                       CASE WHEN s."State" = 'archived' THEN 'discarded' ELSE 'pending' END,
                       now(), 1, 0, 0, 0,
                       CASE WHEN s."State" = 'archived' THEN now() END,
                       1, now(), now()
                FROM targets t
                JOIN subscriptions s ON s."Id" = t.subscription_id
                ON CONFLICT ("EventId", "SubscriptionId") DO NOTHING
                RETURNING "State"
            ), completed AS (
                UPDATE flowbit.lifecycle_events e
                   SET "FanoutCompletedAt" = now()
                  FROM batch
                 WHERE e."Id" = batch."Id"
                RETURNING 1
            )
            SELECT (SELECT count(*) FROM completed)::integer,
                   (SELECT count(*) FROM inserted WHERE "State" = 'pending')::integer
            """))
        {
            Add(command, "limit", batchSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            events = reader.GetInt32(0);
            deliveries = reader.GetInt32(1);
        }
        if (deliveries > 0)
        {
            await using var notify = Command(connection, transaction, "SELECT pg_notify('flowbit_lifecycle_events', '')");
            await notify.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new WebhookFanoutResult(events, deliveries);
    }

    public async Task<int> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var expired = new List<(Guid Id, long LeaseGeneration, string FromState, int GenerationAttempts, DateTimeOffset? DeadlineAt, DateTimeOffset Now)>();
        await using (var select = Command(connection, transaction, """
            SELECT d."Id", d."LeaseGeneration", d."LeasedFromState", d."GenerationAttempts", d."DeadlineAt", now()
            FROM flowbit.webhook_deliveries d
            WHERE d."State" = 'delivering' AND d."LeaseExpiresAt" <= now()
            ORDER BY d."LeaseExpiresAt", d."Id"
            LIMIT @limit
            FOR UPDATE SKIP LOCKED
            """))
        {
            Add(select, "limit", batchSize);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                expired.Add((reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }
        foreach (var delivery in expired)
        {
            bool sent;
            await using (var close = Command(connection, transaction, """
                UPDATE flowbit.webhook_delivery_attempts
                   SET "CompletedAt" = now(),
                       "Outcome" = CASE WHEN "SendIntentAt" IS NULL THEN 'abortedBeforeSend' ELSE 'outcomeUnknown' END
                 WHERE "DeliveryId" = @id AND "LeaseGeneration" = @generation AND "CompletedAt" IS NULL
                RETURNING "SendIntentAt" IS NOT NULL
                """))
            {
                Add(close, "id", delivery.Id);
                Add(close, "generation", delivery.LeaseGeneration);
                sent = (await close.ExecuteScalarAsync(cancellationToken)) is true;
            }
            if (!sent)
            {
                await ReturnToQueueAsync(connection, transaction, delivery.Id, delivery.FromState, null, cancellationToken);
                continue;
            }
            // The previous outcome is unknown: the request may have reached the receiver.
            var decision = WebhookRetryPolicy.Decide(
                WebhookOutcomeCodes.OutcomeUnknown,
                delivery.GenerationAttempts,
                delivery.DeadlineAt ?? delivery.Now + WebhookConstraints.GenerationWindow,
                null,
                delivery.Now,
                WebhookRetryPolicy.NextJitter());
            await SettleLeaseAsync(connection, transaction, delivery.Id, decision, WebhookOutcomeCodes.OutcomeUnknown, null, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return expired.Count;
    }

    public async Task<int> SettleArchivedSubscriptionsAsync(
        int batchSize,
        TimeSpan secretRemovalGrace,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Guid>();
        await using (var connection = await source.DataSource.OpenConnectionAsync(cancellationToken))
        await using (var select = Command(connection, null, """
            SELECT "Id" FROM flowbit.webhook_subscriptions
            WHERE "State" = 'archived' AND "ArchiveSettledAt" IS NULL
            ORDER BY "ArchivedAt", "Id"
            LIMIT 10
            """))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(reader.GetGuid(0));
            }
        }
        var discarded = 0;
        foreach (var subscriptionId in candidates)
        {
            discarded += await SettleArchivedSubscriptionAsync(subscriptionId, batchSize, secretRemovalGrace, cancellationToken);
        }
        return discarded;
    }

    private async Task<int> SettleArchivedSubscriptionAsync(
        Guid subscriptionId,
        int batchSize,
        TimeSpan secretRemovalGrace,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockSubscription = Command(connection, transaction, """
            SELECT 1 FROM flowbit.webhook_subscriptions
            WHERE "Id" = @id AND "State" = 'archived' AND "ArchiveSettledAt" IS NULL
            FOR UPDATE SKIP LOCKED
            """))
        {
            Add(lockSubscription, "id", subscriptionId);
            if (await lockSubscription.ExecuteScalarAsync(cancellationToken) is null)
            {
                return 0;
            }
        }
        await using (var lockKeys = Command(connection, transaction, """
            SELECT "Id" FROM flowbit.webhook_signing_keys
            WHERE "SubscriptionId" = @id AND "Status" IN ('current', 'previous')
            ORDER BY "Id"
            FOR UPDATE
            """))
        {
            Add(lockKeys, "id", subscriptionId);
            await lockKeys.ExecuteNonQueryAsync(cancellationToken);
        }
        var outstanding = new List<(Guid Id, string State, long LeaseGeneration)>();
        await using (var select = Command(connection, transaction, """
            SELECT "Id", "State", "LeaseGeneration"
            FROM flowbit.webhook_deliveries
            WHERE "SubscriptionId" = @id AND "State" IN ('pending', 'retryScheduled', 'failed', 'delivering')
            ORDER BY "OccurredAt", "Id"
            LIMIT @limit
            FOR UPDATE SKIP LOCKED
            """))
        {
            Add(select, "id", subscriptionId);
            Add(select, "limit", batchSize);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                outstanding.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }
        var leased = outstanding.Where(item => item.State == WebhookDeliveryStates.Delivering).ToArray();
        if (leased.Length > 0)
        {
            // Fence the leases: late finalizers fail their token check. A request already sent may
            // still reach the receiver, so its outcome is unknown rather than proof of nothing sent.
            await using var close = Command(connection, transaction, """
                UPDATE flowbit.webhook_delivery_attempts a
                   SET "CompletedAt" = now(),
                       "Outcome" = CASE WHEN a."SendIntentAt" IS NULL THEN 'abortedBeforeSend' ELSE 'outcomeUnknown' END
                  FROM unnest(@ids, @generations) AS fenced(id, generation)
                 WHERE a."DeliveryId" = fenced.id AND a."LeaseGeneration" = fenced.generation
                   AND a."CompletedAt" IS NULL
                """);
            Add(close, "ids", leased.Select(item => item.Id).ToArray());
            Add(close, "generations", leased.Select(item => item.LeaseGeneration).ToArray());
            await close.ExecuteNonQueryAsync(cancellationToken);
        }
        if (outstanding.Count > 0)
        {
            await using var discard = Command(connection, transaction, """
                UPDATE flowbit.webhook_deliveries
                   SET "State" = 'discarded', "SettledAt" = now(), "LeaseOwner" = NULL, "LeaseToken" = NULL,
                       "LeaseExpiresAt" = NULL, "LeasedFromState" = NULL, "Revision" = "Revision" + 1,
                       "UpdatedAt" = now()
                 WHERE "Id" = ANY(@ids)
                """);
            Add(discard, "ids", outstanding.Select(item => item.Id).ToArray());
            await discard.ExecuteNonQueryAsync(cancellationToken);
        }
        bool remaining;
        await using (var check = Command(connection, transaction, """
            SELECT EXISTS (
                SELECT 1 FROM flowbit.webhook_deliveries
                WHERE "SubscriptionId" = @id AND "State" IN ('pending', 'retryScheduled', 'failed', 'delivering'))
            """))
        {
            Add(check, "id", subscriptionId);
            remaining = (bool)(await check.ExecuteScalarAsync(cancellationToken))!;
        }
        if (!remaining)
        {
            // Both keys retire now; their material stays until a fenced request can no longer be in flight.
            await using var finish = Command(connection, transaction, """
                UPDATE flowbit.webhook_signing_keys
                   SET "Status" = 'retired', "RetiredAt" = now(),
                       "RemoveSecretNotBefore" = now() + make_interval(secs => @grace)
                 WHERE "SubscriptionId" = @id AND "Status" IN ('current', 'previous');

                UPDATE flowbit.webhook_subscriptions SET "ArchiveSettledAt" = now() WHERE "Id" = @id;

                INSERT INTO flowbit.webhook_management_audit
                    ("Operation", "Actor", "SubscriptionId", "OccurredAt", "ChangesJson")
                VALUES ('archiveSettled', 'system', @id, now(), '{}'::jsonb);
                """);
            Add(finish, "id", subscriptionId);
            Add(finish, "grace", secretRemovalGrace.TotalSeconds);
            await finish.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return outstanding.Count;
    }

    public async Task<int> SweepSigningKeysAsync(TimeSpan secretRemovalGrace, CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            WITH expired AS (
                UPDATE flowbit.webhook_signing_keys
                   SET "Status" = 'expired', "RetiredAt" = now(),
                       "RemoveSecretNotBefore" = "OverlapExpiresAt" + make_interval(secs => @grace)
                 WHERE "Status" = 'previous' AND "OverlapExpiresAt" <= now()
                RETURNING 1
            ), removed AS (
                UPDATE flowbit.webhook_signing_keys
                   SET "ProtectedSecret" = NULL, "SecretRemovedAt" = now()
                 WHERE "ProtectedSecret" IS NOT NULL
                   AND "Status" NOT IN ('current', 'previous')
                   AND "RemoveSecretNotBefore" <= now()
                RETURNING 1
            )
            SELECT (SELECT count(*) FROM expired)::integer + (SELECT count(*) FROM removed)::integer
            """);
        Add(command, "grace", secretRemovalGrace.TotalSeconds);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<WebhookDeliveryLease>> AcquireAsync(
        WebhookAcquireRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MaxCount <= 0)
        {
            return [];
        }
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var subscriptions = new List<(Guid Id, string State, int MaxConcurrency, DateTimeOffset? BackoffUntil, DateTimeOffset Now)>();
        // Least-recently-acquired first, so one busy subscription cannot starve the others. A paused or
        // backing-off subscription qualifies only through a due test, found by its own index, so its held
        // backlog is never scanned.
        await using (var select = Command(connection, transaction, """
            SELECT ds."SubscriptionId", s."State", s."MaxConcurrency", ds."BackoffUntil", now()
            FROM flowbit.webhook_subscription_dispatch_state ds
            JOIN flowbit.webhook_subscriptions s ON s."Id" = ds."SubscriptionId"
            WHERE (s."State" = 'active'
                   AND (ds."BackoffUntil" IS NULL OR ds."BackoffUntil" <= now())
                   AND EXISTS (
                       SELECT 1 FROM flowbit.webhook_deliveries d
                       WHERE d."SubscriptionId" = ds."SubscriptionId"
                         AND d."State" IN ('pending', 'retryScheduled')
                         AND d."DueAt" <= now()))
               OR (s."State" IN ('active', 'paused')
                   AND EXISTS (
                       SELECT 1 FROM flowbit.webhook_deliveries test
                       WHERE test."SubscriptionId" = ds."SubscriptionId"
                         AND test."IsTest"
                         AND test."State" IN ('pending', 'retryScheduled')
                         AND test."DueAt" <= now()))
            ORDER BY ds."LastAcquiredAt" NULLS FIRST, ds."SubscriptionId"
            LIMIT @max_subscriptions
            FOR UPDATE OF ds SKIP LOCKED
            """))
        {
            Add(select, "max_subscriptions", request.MaxSubscriptions);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                subscriptions.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        var leases = new List<WebhookDeliveryLease>();
        foreach (var subscription in subscriptions)
        {
            var remaining = request.MaxCount - leases.Count;
            if (remaining <= 0)
            {
                break;
            }
            long valid;
            await using (var count = Command(connection, transaction, """
                SELECT count(*) FROM flowbit.webhook_deliveries
                WHERE "SubscriptionId" = @id AND "State" = 'delivering' AND "LeaseExpiresAt" > now()
                """))
            {
                Add(count, "id", subscription.Id);
                valid = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
            }
            // While backing off, nothing but tests is acquired; when backoff ends, one probe at a time.
            var backingOff = subscription.BackoffUntil is not null;
            var holding = backingOff && subscription.BackoffUntil > subscription.Now;
            var limit = backingOff ? 1 : subscription.MaxConcurrency;
            var capacity = (int)Math.Min(remaining, Math.Max(0, limit - valid));
            if (capacity <= 0)
            {
                continue;
            }
            var testsOnly = holding || subscription.State != WebhookSubscriptionStates.Active;
            var claimed = new List<(Guid Id, int GenerationAttempts, DateTimeOffset? DeadlineAt)>();
            // A literal test predicate lets PostgreSQL use the outstanding-test index instead of the held backlog.
            var testFilter = testsOnly ? "AND d.\"IsTest\"" : string.Empty;
            await using (var claim = Command(connection, transaction, $"""
                SELECT d."Id", d."GenerationAttempts", d."DeadlineAt"
                FROM flowbit.webhook_deliveries d
                WHERE d."SubscriptionId" = @id
                  AND d."State" IN ('pending', 'retryScheduled')
                  AND d."DueAt" <= now()
                  {testFilter}
                ORDER BY d."DueAt", d."Id"
                LIMIT @capacity
                FOR UPDATE SKIP LOCKED
                """))
            {
                Add(claim, "id", subscription.Id);
                Add(claim, "capacity", capacity);
                await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    claimed.Add((reader.GetGuid(0), reader.GetInt32(1),
                        reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
                }
            }
            if (claimed.Count == 0)
            {
                continue;
            }
            // An exhausted or expired generation fails without another request, even if it was scheduled in budget.
            var exhausted = claimed
                .Where(item => item.GenerationAttempts >= WebhookConstraints.MaxAttemptsPerGeneration
                    || (item.DeadlineAt is { } deadline && deadline <= subscription.Now))
                .ToArray();
            foreach (var item in exhausted)
            {
                await FailWithoutRequestAsync(
                    connection,
                    transaction,
                    item.Id,
                    item.GenerationAttempts >= WebhookConstraints.MaxAttemptsPerGeneration
                        ? WebhookOutcomeCodes.AttemptsExhausted
                        : WebhookOutcomeCodes.DeadlineExceeded,
                    cancellationToken);
            }
            var leaseIds = claimed.Except(exhausted).Select(item => item.Id).ToArray();
            if (leaseIds.Length == 0)
            {
                continue;
            }
            await using (var lease = Command(connection, transaction, """
                WITH leased AS (
                    UPDATE flowbit.webhook_deliveries d
                       SET "LeasedFromState" = d."State", "State" = 'delivering', "LeaseOwner" = @owner,
                           "LeaseToken" = gen_random_uuid(), "LeaseGeneration" = d."LeaseGeneration" + 1,
                           "LeaseExpiresAt" = now() + make_interval(secs => @lease_seconds),
                           "Revision" = d."Revision" + 1, "UpdatedAt" = now()
                     WHERE d."Id" = ANY(@ids)
                    RETURNING d."Id", d."EventId", d."SubscriptionId", d."RevisionId", d."IsTest", d."EventType",
                              d."LeaseToken", d."LeaseGeneration", d."RetryGeneration", d."LeaseExpiresAt"
                ), attempt AS (
                    INSERT INTO flowbit.webhook_delivery_attempts
                        ("DeliveryId", "AttemptNumber", "RetryGeneration", "LeaseGeneration", "AcquiredAt")
                    SELECT l."Id",
                           COALESCE((SELECT max(a."AttemptNumber")
                                       FROM flowbit.webhook_delivery_attempts a
                                      WHERE a."DeliveryId" = l."Id"), 0) + 1,
                           l."RetryGeneration", l."LeaseGeneration", now()
                    FROM leased l
                    RETURNING "Id", "DeliveryId", "AttemptNumber"
                )
                SELECT l."Id", l."EventId", l."SubscriptionId", l."RevisionId", l."IsTest", l."EventType",
                       revision."EndpointUrl", e."Body", l."LeaseToken", l."LeaseGeneration", attempt."Id",
                       attempt."AttemptNumber", l."RetryGeneration", l."LeaseExpiresAt"
                FROM leased l
                JOIN attempt ON attempt."DeliveryId" = l."Id"
                JOIN flowbit.webhook_subscription_revisions revision ON revision."Id" = l."RevisionId"
                JOIN flowbit.lifecycle_events e ON e."Id" = l."EventId"
                """))
            {
                Add(lease, "owner", request.WorkerId);
                Add(lease, "lease_seconds", request.LeaseDuration.TotalSeconds);
                Add(lease, "ids", leaseIds);
                await using var reader = await lease.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    leases.Add(new WebhookDeliveryLease(
                        reader.GetGuid(0),
                        reader.GetGuid(1),
                        reader.GetGuid(2),
                        reader.GetInt64(3),
                        reader.GetBoolean(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.GetFieldValue<byte[]>(7),
                        reader.GetGuid(8),
                        reader.GetInt64(9),
                        reader.GetInt64(10),
                        reader.GetInt32(11),
                        reader.GetInt32(12),
                        reader.GetFieldValue<DateTimeOffset>(13)));
                }
            }
            await using (var touch = Command(connection, transaction, """
                UPDATE flowbit.webhook_subscription_dispatch_state
                   SET "LastAcquiredAt" = clock_timestamp(), "UpdatedAt" = now()
                 WHERE "SubscriptionId" = @id
                """))
            {
                Add(touch, "id", subscription.Id);
                await touch.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        // Cancellation before this point rolls the leases back; once committed, the
        // caller owns them and must send or release each one.
        await transaction.CommitAsync(CancellationToken.None);
        return leases;
    }

    public async Task<IReadOnlyList<WebhookProtectedSigningKey>> LoadSigningKeysAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            SELECT "Id", "Status", "ProtectedSecret"
            FROM flowbit.webhook_signing_keys
            WHERE "SubscriptionId" = @id
              AND "ProtectedSecret" IS NOT NULL
              AND ("Status" = 'current' OR ("Status" = 'previous' AND "OverlapExpiresAt" > now()))
            ORDER BY CASE "Status" WHEN 'current' THEN 0 ELSE 1 END, "CreatedAt" DESC
            """);
        Add(command, "id", subscriptionId);
        var keys = new List<WebhookProtectedSigningKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(new WebhookProtectedSigningKey(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2)));
        }
        return keys;
    }

    public async Task<WebhookSendDecision> PrepareSendAsync(
        WebhookDeliveryLease lease,
        IReadOnlyList<Guid> signingKeyIds,
        string? permanentFailureCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockLeaseAsync(connection, transaction, lease, cancellationToken);
        if (current is null)
        {
            return new WebhookSendDecision(WebhookSendDecisionKind.LeaseLost);
        }
        var (subscriptionState, fromState, generationAttempts, deadlineAt, now) = current.Value;
        if (subscriptionState == WebhookSubscriptionStates.Archived)
        {
            await CloseAttemptAsync(connection, transaction, lease.AttemptId, WebhookOutcomeCodes.AbortedBeforeSend, null, null, cancellationToken);
            await DiscardLeasedAsync(connection, transaction, lease.DeliveryId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WebhookSendDecision(WebhookSendDecisionKind.Settled);
        }
        if (subscriptionState != WebhookSubscriptionStates.Active && !lease.IsTest)
        {
            // A pause before send intent consumes no attempt budget.
            await CloseAttemptAsync(connection, transaction, lease.AttemptId, WebhookOutcomeCodes.AbortedBeforeSend, null, null, cancellationToken);
            await ReturnToQueueAsync(connection, transaction, lease.DeliveryId, fromState, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WebhookSendDecision(WebhookSendDecisionKind.Aborted);
        }
        var failure = permanentFailureCode
            ?? (generationAttempts >= WebhookConstraints.MaxAttemptsPerGeneration
                ? WebhookOutcomeCodes.AttemptsExhausted
                : deadlineAt is { } deadline && deadline <= now
                    ? WebhookOutcomeCodes.DeadlineExceeded
                    : null);
        if (failure is not null)
        {
            await CloseAttemptAsync(connection, transaction, lease.AttemptId,
                permanentFailureCode ?? WebhookOutcomeCodes.AbortedBeforeSend, null, null, cancellationToken);
            await SettleLeaseAsync(connection, transaction, lease.DeliveryId,
                new WebhookRetryDecision(WebhookDeliveryStates.Failed, null, failure), failure, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WebhookSendDecision(WebhookSendDecisionKind.Settled);
        }
        // Keys revoked or expired since they were loaded stop signing now. A rotation since then releases the
        // lease instead, so the next acquisition signs with the new current key.
        var usable = new HashSet<Guid>();
        var currentStillCurrent = false;
        await using (var keys = Command(connection, transaction, """
            SELECT "Id", "Status" FROM flowbit.webhook_signing_keys
            WHERE "Id" = ANY(@keys) AND "ProtectedSecret" IS NOT NULL
              AND ("Status" = 'current' OR ("Status" = 'previous' AND "OverlapExpiresAt" > now()))
            """))
        {
            Add(keys, "keys", signingKeyIds.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid);
            await using var reader = await keys.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var keyId = reader.GetGuid(0);
                usable.Add(keyId);
                currentStillCurrent |= signingKeyIds.Count > 0 && keyId == signingKeyIds[0]
                    && reader.GetString(1) == WebhookSigningKeyStatuses.Current;
            }
        }
        if (!currentStillCurrent)
        {
            await CloseAttemptAsync(connection, transaction, lease.AttemptId, WebhookOutcomeCodes.AbortedBeforeSend, null, null, cancellationToken);
            await ReturnToQueueAsync(connection, transaction, lease.DeliveryId, fromState, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WebhookSendDecision(WebhookSendDecisionKind.Aborted);
        }
        var signingKeys = signingKeyIds.Where(usable.Contains).ToArray();
        // The send intent: from here the attempt counts toward the budget, whatever its outcome.
        await using (var intent = Command(connection, transaction, """
            UPDATE flowbit.webhook_delivery_attempts
               SET "SendIntentAt" = now(), "SigningKeyIds" = @keys
             WHERE "Id" = @attempt;

            UPDATE flowbit.webhook_deliveries
               SET "GenerationAttempts" = "GenerationAttempts" + 1,
                   "LifetimeAttempts" = "LifetimeAttempts" + 1,
                   "GenerationStartedAt" = COALESCE("GenerationStartedAt", now()),
                   "DeadlineAt" = COALESCE("DeadlineAt", now() + make_interval(secs => @window)),
                   "LastAttemptAt" = now(), "UpdatedAt" = now()
             WHERE "Id" = @id;
            """))
        {
            Add(intent, "attempt", lease.AttemptId);
            Add(intent, "keys", signingKeys, NpgsqlDbType.Array | NpgsqlDbType.Uuid);
            Add(intent, "id", lease.DeliveryId);
            Add(intent, "window", WebhookConstraints.GenerationWindow.TotalSeconds);
            await intent.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new WebhookSendDecision(WebhookSendDecisionKind.Send, signingKeys);
    }

    public async Task<bool> ReleaseBeforeSendAsync(WebhookDeliveryLease lease, CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockLeaseAsync(connection, transaction, lease, cancellationToken);
        if (current is null)
        {
            return false;
        }
        await CloseAttemptAsync(connection, transaction, lease.AttemptId, WebhookOutcomeCodes.AbortedBeforeSend, null, null, cancellationToken);
        await ReturnToQueueAsync(connection, transaction, lease.DeliveryId, current.Value.FromState, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> HeartbeatAsync(
        WebhookDeliveryLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            UPDATE flowbit.webhook_deliveries
               SET "LeaseExpiresAt" = now() + make_interval(secs => @lease_seconds)
             WHERE "Id" = @id AND "State" = 'delivering' AND "LeaseToken" = @token
               AND "LeaseGeneration" = @generation AND "LeaseExpiresAt" > now()
            """);
        Add(command, "lease_seconds", leaseDuration.TotalSeconds);
        Add(command, "id", lease.DeliveryId);
        Add(command, "token", lease.LeaseToken);
        Add(command, "generation", lease.LeaseGeneration);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<WebhookFinalizeResult> FinalizeAsync(
        WebhookDeliveryLease lease,
        WebhookSendOutcome outcome,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Lock order: subscription (only when this outcome can pause it), dispatch state, delivery, attempt.
        var mayPause = outcome.Outcome == WebhookOutcomeCodes.ReceiverGone
            || WebhookRetryPolicy.CountsTowardBackoff(outcome.Outcome);
        string? subscriptionState = null;
        if (mayPause)
        {
            await using var lockSubscription = Command(connection, transaction, """
                SELECT "State" FROM flowbit.webhook_subscriptions WHERE "Id" = @id FOR NO KEY UPDATE
                """);
            Add(lockSubscription, "id", lease.SubscriptionId);
            subscriptionState = (string?)await lockSubscription.ExecuteScalarAsync(cancellationToken);
        }
        WebhookReceiverState receiver;
        await using (var lockState = Command(connection, transaction, """
            SELECT "ConsecutiveFailures", "BackoffStartedAt", "BackoffUntil", "BackoffSeconds"
            FROM flowbit.webhook_subscription_dispatch_state
            WHERE "SubscriptionId" = @id
            FOR UPDATE
            """))
        {
            Add(lockState, "id", lease.SubscriptionId);
            await using var reader = await lockState.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The webhook subscription has no dispatch state.");
            }
            receiver = new WebhookReceiverState(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetInt32(3),
                false);
        }
        var current = await LockLeaseAsync(connection, transaction, lease, cancellationToken);
        if (current is null)
        {
            // A stale Worker cannot overwrite a newer outcome.
            return new WebhookFinalizeResult(false, null, false);
        }
        var now = current.Value.Now;
        var decision = WebhookRetryPolicy.Decide(
            outcome.Outcome,
            current.Value.GenerationAttempts,
            current.Value.DeadlineAt ?? now + WebhookConstraints.GenerationWindow,
            outcome.RetryAfter,
            now,
            WebhookRetryPolicy.NextJitter());
        var sentAt = await CloseAttemptAsync(connection, transaction, lease.AttemptId, outcome.Outcome, outcome.HttpStatus,
            outcome.DurationMilliseconds, cancellationToken) ?? now;
        await SettleLeaseAsync(connection, transaction, lease.DeliveryId, decision, outcome.Outcome, outcome.HttpStatus, cancellationToken);

        // Only a request sent after backoff started is a probe; one already in flight does not extend backoff.
        var next = WebhookRetryPolicy.NextReceiverState(receiver, outcome.Outcome, outcome.RetryAfter, now, sentAt);
        await using (var updateState = Command(connection, transaction, """
            UPDATE flowbit.webhook_subscription_dispatch_state
               SET "ConsecutiveFailures" = @failures, "BackoffStartedAt" = @backoff_started,
                   "BackoffUntil" = @backoff_until, "BackoffSeconds" = @backoff_seconds,
                   "LastSuccessAt" = CASE WHEN @succeeded THEN now() ELSE "LastSuccessAt" END,
                   "LastFailureAt" = CASE WHEN @succeeded THEN "LastFailureAt" ELSE now() END,
                   "LastOutcome" = @outcome, "LastHttpStatus" = @status, "LastOutcomeAt" = now(), "UpdatedAt" = now()
             WHERE "SubscriptionId" = @id
            """))
        {
            Add(updateState, "failures", next.ConsecutiveFailures);
            Add(updateState, "backoff_started", next.BackoffStartedAt, NpgsqlDbType.TimestampTz);
            Add(updateState, "backoff_until", next.BackoffUntil, NpgsqlDbType.TimestampTz);
            Add(updateState, "backoff_seconds", next.BackoffSeconds);
            Add(updateState, "succeeded", outcome.Outcome == WebhookOutcomeCodes.Succeeded);
            Add(updateState, "outcome", outcome.Outcome);
            Add(updateState, "status", outcome.HttpStatus, NpgsqlDbType.Integer);
            Add(updateState, "id", lease.SubscriptionId);
            await updateState.ExecuteNonQueryAsync(cancellationToken);
        }

        string? pauseReason = outcome.Outcome == WebhookOutcomeCodes.ReceiverGone
            ? WebhookPauseReasons.ReceiverGone
            : next.AutomaticallyPause ? WebhookPauseReasons.ReceiverUnavailable : null;
        var paused = false;
        if (pauseReason is not null && subscriptionState == WebhookSubscriptionStates.Active)
        {
            await using var pause = Command(connection, transaction, """
                UPDATE flowbit.webhook_subscriptions
                   SET "State" = 'paused', "PauseReason" = @reason, "Revision" = "Revision" + 1,
                       "UpdatedAt" = now(), "UpdatedBy" = 'system'
                 WHERE "Id" = @id AND "State" = 'active';

                INSERT INTO flowbit.webhook_management_audit
                    ("Operation", "Actor", "SubscriptionId", "DeliveryId", "OccurredAt", "ChangesJson")
                VALUES ('automaticPause', 'system', @id, @delivery, now(), @changes);
                """);
            Add(pause, "reason", pauseReason);
            Add(pause, "id", lease.SubscriptionId);
            Add(pause, "delivery", lease.DeliveryId);
            Add(pause, "changes", JsonSerializer.Serialize(new { pauseReason, outcome = outcome.Outcome, httpStatus = outcome.HttpStatus }), NpgsqlDbType.Jsonb);
            await pause.ExecuteNonQueryAsync(cancellationToken);
            paused = true;
        }
        await transaction.CommitAsync(cancellationToken);
        return new WebhookFinalizeResult(true, decision.State, paused);
    }

    public async Task<TimeSpan?> GetTimeUntilNextDueAsync(CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        // Work held by backoff or a pause counts only through its backoff end or a test, and each subscription's
        // earliest delivery comes from the due index, so a large held backlog costs nothing here. Expired leases
        // need no wake: the fanout loop returns them to the queue, where they count as due work.
        await using var command = Command(connection, null, """
            SELECT EXTRACT(EPOCH FROM LEAST(
                (SELECT min(next_due."DueAt")
                   FROM flowbit.webhook_subscriptions s
                   JOIN flowbit.webhook_subscription_dispatch_state ds ON ds."SubscriptionId" = s."Id"
                  CROSS JOIN LATERAL (
                        SELECT d."DueAt"
                          FROM flowbit.webhook_deliveries d
                         WHERE d."SubscriptionId" = s."Id" AND d."State" IN ('pending', 'retryScheduled')
                         ORDER BY d."DueAt"
                         LIMIT 1) next_due
                  WHERE s."State" = 'active' AND (ds."BackoffUntil" IS NULL OR ds."BackoffUntil" <= now())),
                (SELECT min(test."DueAt")
                   FROM flowbit.webhook_deliveries test
                   JOIN flowbit.webhook_subscriptions s ON s."Id" = test."SubscriptionId"
                  WHERE test."IsTest" AND test."State" IN ('pending', 'retryScheduled')
                    AND s."State" IN ('active', 'paused')),
                (SELECT min(ds."BackoffUntil")
                   FROM flowbit.webhook_subscription_dispatch_state ds
                   JOIN flowbit.webhook_subscriptions s ON s."Id" = ds."SubscriptionId"
                  WHERE s."State" = 'active' AND ds."BackoffUntil" > now())) - now())::double precision
            """);
        return await command.ExecuteScalarAsync(cancellationToken) is double seconds
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    public async Task<WebhookQueueSnapshot> GetQueueSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            SELECT COALESCE((SELECT "Enabled" FROM flowbit.lifecycle_event_capture_state WHERE "Id" = 1), FALSE),
                   (SELECT count(*) FROM flowbit.lifecycle_events WHERE "FanoutCompletedAt" IS NULL),
                   (SELECT min("RecordedAt") FROM flowbit.lifecycle_events WHERE "FanoutCompletedAt" IS NULL),
                   count(*) FILTER (WHERE d."State" = 'pending'),
                   count(*) FILTER (WHERE d."State" = 'retryScheduled'),
                   count(*) FILTER (WHERE d."State" = 'delivering'),
                   count(*) FILTER (WHERE d."State" = 'failed'),
                   min(d."DueAt") FILTER (WHERE d."State" IN ('pending', 'retryScheduled')),
                   (SELECT count(*) FROM flowbit.webhook_subscription_dispatch_state WHERE "BackoffUntil" IS NOT NULL)::integer,
                   (SELECT count(*) FROM flowbit.webhook_subscriptions WHERE "State" = 'paused')::integer
            FROM flowbit.webhook_deliveries d
            WHERE d."State" IN ('pending', 'retryScheduled', 'delivering', 'failed')
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new WebhookQueueSnapshot(
            reader.GetBoolean(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetInt32(8),
            reader.GetInt32(9));
    }

    // The fence: the lease token and generation still match and the lease is unexpired by the database clock.
    private static async Task<(string SubscriptionState, string FromState, int GenerationAttempts, DateTimeOffset? DeadlineAt, DateTimeOffset Now)?> LockLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WebhookDeliveryLease lease,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT s."State", d."LeasedFromState", d."GenerationAttempts", d."DeadlineAt", now()
            FROM flowbit.webhook_deliveries d
            JOIN flowbit.webhook_subscriptions s ON s."Id" = d."SubscriptionId"
            WHERE d."Id" = @id AND d."State" = 'delivering' AND d."LeaseToken" = @token
              AND d."LeaseGeneration" = @generation AND d."LeaseExpiresAt" > now()
            FOR UPDATE OF d
            """);
        Add(command, "id", lease.DeliveryId);
        Add(command, "token", lease.LeaseToken);
        Add(command, "generation", lease.LeaseGeneration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return (reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    /// <summary>Closes an open attempt and returns its send-intent time, which is null when nothing was sent.</summary>
    private static async Task<DateTimeOffset?> CloseAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long attemptId,
        string outcome,
        int? httpStatus,
        int? durationMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            UPDATE flowbit.webhook_delivery_attempts
               SET "CompletedAt" = now(), "Outcome" = @outcome, "HttpStatus" = @status,
                   "DurationMilliseconds" = @duration
             WHERE "Id" = @id AND "CompletedAt" IS NULL
            RETURNING "SendIntentAt"
            """);
        Add(command, "id", attemptId);
        Add(command, "outcome", outcome);
        Add(command, "status", httpStatus, NpgsqlDbType.Integer);
        Add(command, "duration", durationMilliseconds, NpgsqlDbType.Integer);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0)
            ? reader.GetFieldValue<DateTimeOffset>(0)
            : null;
    }

    private static async Task ReturnToQueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid deliveryId,
        string fromState,
        DateTimeOffset? dueAt,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            UPDATE flowbit.webhook_deliveries
               SET "State" = @state, "DueAt" = COALESCE(@due, "DueAt"), "LeaseOwner" = NULL, "LeaseToken" = NULL,
                   "LeaseExpiresAt" = NULL, "LeasedFromState" = NULL, "Revision" = "Revision" + 1, "UpdatedAt" = now()
             WHERE "Id" = @id
            """);
        Add(command, "state", fromState);
        Add(command, "due", dueAt, NpgsqlDbType.TimestampTz);
        Add(command, "id", deliveryId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DiscardLeasedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            UPDATE flowbit.webhook_deliveries
               SET "State" = 'discarded', "SettledAt" = now(), "LeaseOwner" = NULL, "LeaseToken" = NULL,
                   "LeaseExpiresAt" = NULL, "LeasedFromState" = NULL, "Revision" = "Revision" + 1, "UpdatedAt" = now()
             WHERE "Id" = @id
            """);
        Add(command, "id", deliveryId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SettleLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid deliveryId,
        WebhookRetryDecision decision,
        string outcome,
        int? httpStatus,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            UPDATE flowbit.webhook_deliveries
               SET "State" = @state,
                   "DueAt" = COALESCE(@due, "DueAt"),
                   "FailureCode" = @failure,
                   "LastOutcome" = @outcome,
                   "LastHttpStatus" = @status,
                   "SettledAt" = CASE WHEN @state = 'succeeded' THEN now() ELSE NULL END,
                   "LeaseOwner" = NULL, "LeaseToken" = NULL, "LeaseExpiresAt" = NULL, "LeasedFromState" = NULL,
                   "Revision" = "Revision" + 1, "UpdatedAt" = now()
             WHERE "Id" = @id
            """);
        Add(command, "state", decision.State);
        Add(command, "due", decision.DueAt, NpgsqlDbType.TimestampTz);
        Add(command, "failure", decision.FailureCode, NpgsqlDbType.Varchar);
        Add(command, "outcome", outcome);
        Add(command, "status", httpStatus, NpgsqlDbType.Integer);
        Add(command, "id", deliveryId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task FailWithoutRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid deliveryId,
        string failureCode,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            UPDATE flowbit.webhook_deliveries
               SET "State" = 'failed', "FailureCode" = @failure, "Revision" = "Revision" + 1, "UpdatedAt" = now()
             WHERE "Id" = @id
            """);
        Add(command, "failure", failureCode);
        Add(command, "id", deliveryId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
