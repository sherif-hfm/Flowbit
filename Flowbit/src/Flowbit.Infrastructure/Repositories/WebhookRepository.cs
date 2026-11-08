using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Npgsql;
using NpgsqlTypes;

namespace Flowbit.Infrastructure.Repositories;

/// <summary>
/// Management and query persistence for lifecycle webhooks. Mutations lock webhook rows in the order
/// subscription, dispatch state, signing key, delivery, attempt, never touch runtime rows, and commit their
/// audit entry in the same transaction. SQL is authoritative for filters, counts, ordering, and paging.
/// </summary>
public sealed class WebhookRepository(WebhookDataSource source, WebhookOptions options) : IWebhookRepository
{
    private const string SubscriptionColumns = """
        s."Id", s."Name", s."Description", s."State", s."PauseReason", s."MaxConcurrency", s."Revision",
        s."CreatedAt", s."CreatedBy", s."UpdatedAt", s."UpdatedBy", s."ArchivedAt", s."ArchivedBy", s."ArchiveSettledAt",
        r."Id", r."ConfigurationRevision", r."EndpointUrl", r."EventTypes", r."WorkflowKey", r."CreatedAt", r."CreatedBy",
        COALESCE(ds."ConsecutiveFailures", 0), ds."BackoffStartedAt", ds."BackoffUntil", ds."LastSuccessAt",
        ds."LastFailureAt", ds."LastOutcome", ds."LastHttpStatus", ds."LastOutcomeAt",
        counts.pending, counts.retry_scheduled, counts.delivering, counts.failed
        """;

    private const string SubscriptionJoins = """
        FROM flowbit.webhook_subscriptions s
        JOIN flowbit.webhook_subscription_revisions r ON r."Id" = s."CurrentRevisionId"
        LEFT JOIN flowbit.webhook_subscription_dispatch_state ds ON ds."SubscriptionId" = s."Id"
        LEFT JOIN LATERAL (
            SELECT count(*) FILTER (WHERE d."State" = 'pending') AS pending,
                   count(*) FILTER (WHERE d."State" = 'retryScheduled') AS retry_scheduled,
                   count(*) FILTER (WHERE d."State" = 'delivering') AS delivering,
                   count(*) FILTER (WHERE d."State" = 'failed') AS failed
            FROM flowbit.webhook_deliveries d
            WHERE d."SubscriptionId" = s."Id"
              AND d."State" IN ('pending', 'retryScheduled', 'delivering', 'failed')
        ) counts ON TRUE
        """;

    private const string DeliveryColumns = """
        d."Id", d."EventId", d."SubscriptionId", s."Name", d."RevisionId", r."ConfigurationRevision", r."EndpointUrl",
        d."EventType", d."IsTest", d."WorkflowKey", d."InstanceId", d."OccurredAt", d."State", d."DueAt",
        d."RetryGeneration", d."GenerationAttempts", d."LifetimeAttempts", d."LastOutcome", d."LastHttpStatus",
        d."FailureCode", d."LastAttemptAt", d."SettledAt", d."Revision", d."CreatedAt", d."UpdatedAt", s."State",
        EXISTS (SELECT 1 FROM flowbit.webhook_signing_keys k
                 WHERE k."SubscriptionId" = d."SubscriptionId" AND k."Status" = 'current'
                   AND k."ProtectedSecret" IS NOT NULL)
        """;

    private const string DeliveryJoins = """
        FROM flowbit.webhook_deliveries d
        JOIN flowbit.webhook_subscriptions s ON s."Id" = d."SubscriptionId"
        JOIN flowbit.webhook_subscription_revisions r ON r."Id" = d."RevisionId"
        """;

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    public async Task<LifecycleEventCaptureDto> GetCaptureStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            SELECT "Enabled", "ChangedAt", "ChangedBy"
            FROM flowbit.lifecycle_event_capture_state
            WHERE "Id" = 1
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The lifecycle event capture switch row is missing; apply the database migrations.");
        }
        return new LifecycleEventCaptureDto(
            reader.GetBoolean(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task<bool> WorkflowKeyExistsAsync(string workflowKey, CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, """
            SELECT EXISTS (SELECT 1 FROM flowbit.workflow_definitions WHERE "WorkflowKey" = @key)
            """);
        Add(command, "key", workflowKey);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<(IReadOnlyList<WebhookSubscriptionDto> Items, long TotalCount)> ListSubscriptionsAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        long total;
        await using (var count = Command(connection, null, "SELECT count(*) FROM flowbit.webhook_subscriptions"))
        {
            total = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        }
        var rows = new List<SubscriptionRow>();
        await using (var command = Command(connection, null, $"""
            SELECT {SubscriptionColumns}
            {SubscriptionJoins}
            ORDER BY s."CreatedAt" DESC, s."Id" DESC
            OFFSET @offset LIMIT @limit
            """))
        {
            Add(command, "offset", offset);
            Add(command, "limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(ReadSubscription(reader));
            }
        }
        var keys = await ReadKeysAsync(connection, null, rows.Select(row => row.Id).ToArray(), activeOnly: true, cancellationToken);
        return (rows.Select(row => row.ToDto(keys)).ToArray(), total);
    }

    public async Task<WebhookSubscriptionDetailDto?> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        var subscription = await ReadSubscriptionAsync(connection, null, id, activeKeysOnly: false, cancellationToken);
        if (subscription is null)
        {
            return null;
        }
        var revisions = new List<WebhookDestinationRevisionDto>();
        await using (var command = Command(connection, null, """
            SELECT r."Id", r."ConfigurationRevision", r."EndpointUrl", r."EventTypes", r."WorkflowKey", r."CreatedAt",
                   r."CreatedBy",
                   (SELECT count(*) FROM flowbit.webhook_deliveries d
                     WHERE d."RevisionId" = r."Id"
                       AND d."State" IN ('pending', 'retryScheduled', 'delivering', 'failed'))
            FROM flowbit.webhook_subscription_revisions r
            WHERE r."SubscriptionId" = @id
            ORDER BY r."ConfigurationRevision" DESC
            """))
        {
            Add(command, "id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var destination = ReadDestination(reader, 0);
                revisions.Add(new WebhookDestinationRevisionDto(
                    destination,
                    destination.RevisionId == subscription.Destination.RevisionId,
                    reader.GetInt64(7)));
            }
        }
        return new WebhookSubscriptionDetailDto(subscription, revisions);
    }

    public async Task<WebhookSubscriptionDto> CreateSubscriptionAsync(
        WebhookSubscriptionCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // The cap is enforced atomically: concurrent creations serialize here.
        await using (var guard = Command(connection, transaction, """
            SELECT pg_advisory_xact_lock(hashtextextended('flowbit.webhook_subscriptions.create', 0))
            """))
        {
            await guard.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var count = Command(connection, transaction, """
            SELECT count(*) FROM flowbit.webhook_subscriptions WHERE "State" <> 'archived'
            """))
        {
            var existing = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
            if (existing >= WebhookConstraints.MaxNonArchivedSubscriptions)
            {
                throw new WorkflowConflictException(
                    $"At most {WebhookConstraints.MaxNonArchivedSubscriptions} non-archived webhook subscriptions are allowed. Archive one first.");
            }
        }
        await using (var insert = Command(connection, transaction, """
            INSERT INTO flowbit.webhook_subscriptions
                ("Id", "Name", "Description", "CurrentRevisionId", "State", "PauseReason", "MaxConcurrency",
                 "Revision", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy")
            VALUES (@id, @name, @description, NULL, 'paused', 'administrator', @concurrency,
                    1, now(), @actor, now(), @actor);

            WITH revision AS (
                INSERT INTO flowbit.webhook_subscription_revisions
                    ("SubscriptionId", "ConfigurationRevision", "EndpointUrl", "EventTypes", "WorkflowKey",
                     "CreatedAt", "CreatedBy")
                VALUES (@id, 1, @url, @types, @workflow_key, now(), @actor)
                RETURNING "Id"
            )
            UPDATE flowbit.webhook_subscriptions
               SET "CurrentRevisionId" = (SELECT "Id" FROM revision)
             WHERE "Id" = @id;

            INSERT INTO flowbit.webhook_subscription_dispatch_state
                ("SubscriptionId", "ConsecutiveFailures", "BackoffSeconds", "UpdatedAt")
            VALUES (@id, 0, 0, now());

            INSERT INTO flowbit.webhook_signing_keys
                ("Id", "SubscriptionId", "ProtectedSecret", "Status", "CreatedAt", "CreatedBy")
            VALUES (@key_id, @id, @secret, 'current', now(), @actor);
            """))
        {
            Add(insert, "id", command.Id);
            Add(insert, "name", command.Name);
            Add(insert, "description", command.Description, NpgsqlDbType.Varchar);
            Add(insert, "concurrency", command.MaxConcurrency);
            Add(insert, "actor", command.Actor);
            Add(insert, "url", command.EndpointUrl);
            Add(insert, "types", command.EventTypes.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
            Add(insert, "workflow_key", command.WorkflowKey, NpgsqlDbType.Varchar);
            Add(insert, "key_id", command.KeyId);
            Add(insert, "secret", command.ProtectedSecret, NpgsqlDbType.Bytea);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, "subscriptionCreated", command.Actor, command.Id, null, new
        {
            name = command.Name,
            destinationOrigin = command.DestinationOrigin,
            eventTypes = command.EventTypes,
            workflowKey = command.WorkflowKey,
            maxConcurrency = command.MaxConcurrency,
            signingKeyId = command.KeyId,
            state = WebhookSubscriptionStates.Paused
        }, cancellationToken);
        var created = await ReadSubscriptionAsync(connection, transaction, command.Id, activeKeysOnly: true, cancellationToken)
            ?? throw new InvalidOperationException("The created webhook subscription could not be read.");
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<WebhookSubscriptionDto?> UpdateSubscriptionAsync(
        WebhookSubscriptionUpdateCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockSubscriptionAsync(connection, transaction, command.Id, cancellationToken);
        if (current is null)
        {
            return null;
        }
        RequireRevision(current, command.ExpectedRevision);
        RequireNotArchived(current);
        var destinationChanged = !string.Equals(current.EndpointUrl, command.EndpointUrl, StringComparison.Ordinal)
            || !current.EventTypes.SequenceEqual(command.EventTypes, StringComparer.Ordinal)
            || !string.Equals(current.WorkflowKey, command.WorkflowKey, StringComparison.Ordinal);
        if (destinationChanged)
        {
            // An immutable revision for future captures; existing targets keep their destination.
            await using var revise = Command(connection, transaction, """
                WITH revision AS (
                    INSERT INTO flowbit.webhook_subscription_revisions
                        ("SubscriptionId", "ConfigurationRevision", "EndpointUrl", "EventTypes", "WorkflowKey",
                         "CreatedAt", "CreatedBy")
                    SELECT @id, COALESCE(max("ConfigurationRevision"), 0) + 1, @url, @types, @workflow_key, now(), @actor
                    FROM flowbit.webhook_subscription_revisions
                    WHERE "SubscriptionId" = @id
                    RETURNING "Id"
                )
                UPDATE flowbit.webhook_subscriptions
                   SET "CurrentRevisionId" = (SELECT "Id" FROM revision)
                 WHERE "Id" = @id
                """);
            Add(revise, "id", command.Id);
            Add(revise, "url", command.EndpointUrl);
            Add(revise, "types", command.EventTypes.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
            Add(revise, "workflow_key", command.WorkflowKey, NpgsqlDbType.Varchar);
            Add(revise, "actor", command.Actor);
            await revise.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var update = Command(connection, transaction, """
            UPDATE flowbit.webhook_subscriptions
               SET "Name" = @name, "Description" = @description, "MaxConcurrency" = @concurrency,
                   "Revision" = "Revision" + 1, "UpdatedAt" = now(), "UpdatedBy" = @actor
             WHERE "Id" = @id
            """))
        {
            Add(update, "id", command.Id);
            Add(update, "name", command.Name);
            Add(update, "description", command.Description, NpgsqlDbType.Varchar);
            Add(update, "concurrency", command.MaxConcurrency);
            Add(update, "actor", command.Actor);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, "subscriptionUpdated", command.Actor, command.Id, null, new
        {
            name = command.Name,
            maxConcurrency = command.MaxConcurrency,
            destinationChanged,
            previousDestinationOrigin = WebhookDestinationPolicy.TryOrigin(current.EndpointUrl),
            destinationOrigin = command.DestinationOrigin,
            eventTypes = command.EventTypes,
            workflowKey = command.WorkflowKey
        }, cancellationToken);
        var updated = await ReadSubscriptionAsync(connection, transaction, command.Id, activeKeysOnly: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<WebhookSubscriptionDto?> PauseAsync(Guid id, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockSubscriptionAsync(connection, transaction, id, cancellationToken);
        if (current is null)
        {
            return null;
        }
        RequireNotArchived(current);
        if (current.State == WebhookSubscriptionStates.Active)
        {
            await using var update = Command(connection, transaction, """
                UPDATE flowbit.webhook_subscriptions
                   SET "State" = 'paused', "PauseReason" = 'administrator', "Revision" = "Revision" + 1,
                       "UpdatedAt" = now(), "UpdatedBy" = @actor
                 WHERE "Id" = @id
                """);
            Add(update, "id", id);
            Add(update, "actor", actor);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await AuditAsync(connection, transaction, "subscriptionPaused", actor, id, null,
                new { pauseReason = WebhookPauseReasons.Administrator }, cancellationToken);
        }
        var result = await ReadSubscriptionAsync(connection, transaction, id, activeKeysOnly: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WebhookSubscriptionDto?> ResumeAsync(
        Guid id,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockSubscriptionAsync(connection, transaction, id, cancellationToken);
        if (current is null)
        {
            return null;
        }
        RequireRevision(current, expectedRevision);
        RequireNotArchived(current);
        if (current.State == WebhookSubscriptionStates.Paused)
        {
            await RequireCurrentKeyAsync(connection, transaction, id, cancellationToken);
            await using var update = Command(connection, transaction, """
                UPDATE flowbit.webhook_subscriptions
                   SET "State" = 'active', "PauseReason" = NULL, "Revision" = "Revision" + 1,
                       "UpdatedAt" = now(), "UpdatedBy" = @actor
                 WHERE "Id" = @id;

                UPDATE flowbit.webhook_subscription_dispatch_state
                   SET "ConsecutiveFailures" = 0, "BackoffStartedAt" = NULL, "BackoffUntil" = NULL,
                       "BackoffSeconds" = 0, "UpdatedAt" = now()
                 WHERE "SubscriptionId" = @id;
                """);
            Add(update, "id", id);
            Add(update, "actor", actor);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await AuditAsync(connection, transaction, "subscriptionResumed", actor, id, null,
                new { previousPauseReason = current.PauseReason }, cancellationToken);
        }
        var result = await ReadSubscriptionAsync(connection, transaction, id, activeKeysOnly: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WebhookTestResultDto?> QueueTestAsync(Guid id, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockSubscriptionAsync(connection, transaction, id, cancellationToken);
        if (current is null)
        {
            return null;
        }
        RequireNotArchived(current);
        await RequireCurrentKeyAsync(connection, transaction, id, cancellationToken);
        await using (var outstanding = Command(connection, transaction, """
            SELECT EXISTS (
                SELECT 1 FROM flowbit.webhook_deliveries
                WHERE "SubscriptionId" = @id AND "IsTest"
                  AND "State" IN ('pending', 'retryScheduled', 'delivering'))
            """))
        {
            Add(outstanding, "id", id);
            if ((bool)(await outstanding.ExecuteScalarAsync(cancellationToken))!)
            {
                throw new WorkflowConflictException(
                    "A test event for this subscription is still outstanding. Wait for it to settle or fail.");
            }
        }
        Guid deliveryId;
        Guid eventId;
        await using (var record = Command(connection, transaction, "SELECT flowbit.record_webhook_test_event(@id)"))
        {
            Add(record, "id", id);
            deliveryId = (Guid)(await record.ExecuteScalarAsync(cancellationToken))!;
        }
        await using (var read = Command(connection, transaction, """
            SELECT "EventId" FROM flowbit.webhook_deliveries WHERE "Id" = @delivery
            """))
        {
            Add(read, "delivery", deliveryId);
            eventId = (Guid)(await read.ExecuteScalarAsync(cancellationToken))!;
        }
        await AuditAsync(connection, transaction, "testQueued", actor, id, deliveryId,
            new { eventId }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new WebhookTestResultDto(deliveryId, eventId, WebhookDeliveryStates.Pending);
    }

    public async Task<WebhookSubscriptionDto?> RotateKeyAsync(
        WebhookKeyRotationCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockSubscriptionAsync(connection, transaction, command.SubscriptionId, cancellationToken);
        if (current is null)
        {
            return null;
        }
        RequireRevision(current, command.ExpectedRevision);
        RequireNotArchived(current);
        var keys = await LockSigningKeysAsync(connection, transaction, command.SubscriptionId, cancellationToken);
        var previous = keys.SingleOrDefault(key => key.Status == WebhookSigningKeyStatuses.Previous);
        if (previous is not null)
        {
            if (previous.OverlapExpiresAt > previous.Now)
            {
                throw new WorkflowConflictException(
                    "The previous signing key is still in its overlap window. Revoke it before rotating again.");
            }
            await ExpireKeyAsync(connection, transaction, previous.Id, WebhookSigningKeyStatuses.Expired, null, cancellationToken);
        }
        var oldCurrent = keys.SingleOrDefault(key => key.Status == WebhookSigningKeyStatuses.Current)
            ?? throw new InvalidOperationException("The webhook subscription has no current signing key.");
        if (command.OverlapSeconds > 0)
        {
            await using var demote = Command(connection, transaction, """
                UPDATE flowbit.webhook_signing_keys
                   SET "Status" = 'previous', "OverlapExpiresAt" = now() + make_interval(secs => @overlap)
                 WHERE "Id" = @key
                """);
            Add(demote, "key", oldCurrent.Id);
            Add(demote, "overlap", (double)command.OverlapSeconds);
            await demote.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await ExpireKeyAsync(connection, transaction, oldCurrent.Id, WebhookSigningKeyStatuses.Expired, null, cancellationToken);
        }
        await using (var insert = Command(connection, transaction, """
            INSERT INTO flowbit.webhook_signing_keys
                ("Id", "SubscriptionId", "ProtectedSecret", "Status", "CreatedAt", "CreatedBy")
            VALUES (@key_id, @id, @secret, 'current', now(), @actor);

            UPDATE flowbit.webhook_subscriptions
               SET "Revision" = "Revision" + 1, "UpdatedAt" = now(), "UpdatedBy" = @actor
             WHERE "Id" = @id;
            """))
        {
            Add(insert, "key_id", command.KeyId);
            Add(insert, "id", command.SubscriptionId);
            Add(insert, "secret", command.ProtectedSecret, NpgsqlDbType.Bytea);
            Add(insert, "actor", command.Actor);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, "secretRotated", command.Actor, command.SubscriptionId, null, new
        {
            signingKeyId = command.KeyId,
            previousSigningKeyId = oldCurrent.Id,
            overlapSeconds = command.OverlapSeconds
        }, cancellationToken);
        var result = await ReadSubscriptionAsync(connection, transaction, command.SubscriptionId, activeKeysOnly: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WebhookSubscriptionDto?> RevokePreviousKeyAsync(
        Guid id,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockSubscriptionAsync(connection, transaction, id, cancellationToken);
        if (current is null)
        {
            return null;
        }
        RequireRevision(current, expectedRevision);
        RequireNotArchived(current);
        var keys = await LockSigningKeysAsync(connection, transaction, id, cancellationToken);
        var previous = keys.SingleOrDefault(key => key.Status == WebhookSigningKeyStatuses.Previous)
            ?? throw new WorkflowConflictException("The subscription has no previous signing key to revoke.");
        await ExpireKeyAsync(connection, transaction, previous.Id, WebhookSigningKeyStatuses.Revoked, actor, cancellationToken);
        await using (var update = Command(connection, transaction, """
            UPDATE flowbit.webhook_subscriptions
               SET "Revision" = "Revision" + 1, "UpdatedAt" = now(), "UpdatedBy" = @actor
             WHERE "Id" = @id
            """))
        {
            Add(update, "id", id);
            Add(update, "actor", actor);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, "previousSecretRevoked", actor, id, null,
            new { signingKeyId = previous.Id }, cancellationToken);
        var result = await ReadSubscriptionAsync(connection, transaction, id, activeKeysOnly: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WebhookSubscriptionDto?> ArchiveAsync(
        Guid id,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockSubscriptionAsync(connection, transaction, id, cancellationToken);
        if (current is null)
        {
            return null;
        }
        if (current.State != WebhookSubscriptionStates.Archived)
        {
            RequireRevision(current, expectedRevision);
            // The gate commits first; the Worker's coordinator then settles outstanding work in bounded batches.
            await using var update = Command(connection, transaction, """
                UPDATE flowbit.webhook_subscriptions
                   SET "State" = 'archived', "PauseReason" = NULL, "ArchivedAt" = now(), "ArchivedBy" = @actor,
                       "Revision" = "Revision" + 1, "UpdatedAt" = now(), "UpdatedBy" = @actor
                 WHERE "Id" = @id
                """);
            Add(update, "id", id);
            Add(update, "actor", actor);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await AuditAsync(connection, transaction, "subscriptionArchived", actor, id, null,
                new { previousState = current.State }, cancellationToken);
            await using var notify = Command(connection, transaction, "SELECT pg_notify('flowbit_lifecycle_events', '')");
            await notify.ExecuteNonQueryAsync(cancellationToken);
        }
        var result = await ReadSubscriptionAsync(connection, transaction, id, activeKeysOnly: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WebhookBulkDeliveryResultDto?> BulkRetryAsync(
        Guid subscriptionId,
        WebhookDeliveryFilter filter,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var subscription = await ShareSubscriptionAsync(connection, transaction, subscriptionId, cancellationToken);
        if (subscription is null)
        {
            return null;
        }
        RejectRecoveryWhenArchived(subscription.Value.State, "retried");
        if (subscription.Value.State != WebhookSubscriptionStates.Active)
        {
            throw new WorkflowConflictException("Bulk retry requires an active subscription. Resume it first.");
        }
        await RequireCurrentKeyAsync(connection, transaction, subscriptionId, cancellationToken);
        var predicate = new StringBuilder();
        await using var select = Command(connection, transaction, string.Empty);
        AppendFilter(select, predicate, filter with { States = [WebhookDeliveryStates.Failed] });
        select.CommandText = $"""
            SELECT d."Id"
            FROM flowbit.webhook_deliveries d
            WHERE {predicate}
            ORDER BY d."OccurredAt", d."Id"
            LIMIT @bulk_limit
            FOR UPDATE OF d SKIP LOCKED
            """;
        Add(select, "bulk_limit", WebhookConstraints.MaxBulkDeliveries);
        var ids = await ReadGuidsAsync(select, cancellationToken);
        var changed = 0;
        if (ids.Length > 0)
        {
            await using var update = Command(connection, transaction, """
                UPDATE flowbit.webhook_deliveries
                   SET "State" = 'pending', "DueAt" = now(), "RetryGeneration" = "RetryGeneration" + 1,
                       "GenerationStartedAt" = NULL, "DeadlineAt" = NULL, "GenerationAttempts" = 0,
                       "FailureCode" = NULL, "Revision" = "Revision" + 1, "UpdatedAt" = now()
                 WHERE "Id" = ANY(@ids) AND "State" = 'failed'
                """);
            Add(update, "ids", ids);
            changed = await update.ExecuteNonQueryAsync(cancellationToken);
        }
        var hasMore = await AnyMatchingAsync(connection, transaction, filter with { States = [WebhookDeliveryStates.Failed] }, cancellationToken);
        var result = new WebhookBulkDeliveryResultDto(ids.Length, changed, ids.Length - changed, hasMore);
        await AuditAsync(connection, transaction, "deliveriesBulkRetried", actor, subscriptionId, null, new
        {
            filter = AuditFilter(filter),
            matched = result.Matched,
            changed = result.Changed,
            hasMore
        }, cancellationToken);
        if (changed > 0)
        {
            await using var notify = Command(connection, transaction, "SELECT pg_notify('flowbit_lifecycle_events', '')");
            await notify.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WebhookBulkDeliveryResultDto?> BulkDiscardAsync(
        Guid subscriptionId,
        WebhookDeliveryFilter filter,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var subscription = await ShareSubscriptionAsync(connection, transaction, subscriptionId, cancellationToken);
        if (subscription is null)
        {
            return null;
        }
        RejectRecoveryWhenArchived(subscription.Value.State, "discarded by hand");
        var discardable = filter with
        {
            States = [WebhookDeliveryStates.Pending, WebhookDeliveryStates.RetryScheduled, WebhookDeliveryStates.Failed]
        };
        var predicate = new StringBuilder();
        await using var select = Command(connection, transaction, string.Empty);
        AppendFilter(select, predicate, discardable);
        select.CommandText = $"""
            SELECT d."Id"
            FROM flowbit.webhook_deliveries d
            WHERE {predicate}
            ORDER BY d."OccurredAt", d."Id"
            LIMIT @bulk_limit
            FOR UPDATE OF d SKIP LOCKED
            """;
        Add(select, "bulk_limit", WebhookConstraints.MaxBulkDeliveries);
        var ids = await ReadGuidsAsync(select, cancellationToken);
        var changed = 0;
        if (ids.Length > 0)
        {
            await using var update = Command(connection, transaction, """
                UPDATE flowbit.webhook_deliveries
                   SET "State" = 'discarded', "SettledAt" = now(), "Revision" = "Revision" + 1, "UpdatedAt" = now()
                 WHERE "Id" = ANY(@ids) AND "State" IN ('pending', 'retryScheduled', 'failed')
                """);
            Add(update, "ids", ids);
            changed = await update.ExecuteNonQueryAsync(cancellationToken);
        }
        // Active leases are skipped, not fenced; only archive fences them.
        long leased;
        var leasedPredicate = new StringBuilder();
        await using (var countLeased = Command(connection, transaction, string.Empty))
        {
            AppendFilter(countLeased, leasedPredicate, filter with { States = [WebhookDeliveryStates.Delivering] });
            countLeased.CommandText = $"""
                SELECT count(*) FROM (
                    SELECT 1 FROM flowbit.webhook_deliveries d WHERE {leasedPredicate}
                    LIMIT {WebhookConstraints.MaxBulkDeliveries}) leased
                """;
            leased = (long)(await countLeased.ExecuteScalarAsync(cancellationToken))!;
        }
        var hasMore = await AnyMatchingAsync(connection, transaction, discardable, cancellationToken);
        var result = new WebhookBulkDeliveryResultDto(
            ids.Length + (int)leased,
            changed,
            ids.Length - changed + (int)leased,
            hasMore);
        await AuditAsync(connection, transaction, "deliveriesBulkDiscarded", actor, subscriptionId, null, new
        {
            filter = AuditFilter(filter),
            matched = result.Matched,
            changed = result.Changed,
            skipped = result.Skipped,
            hasMore
        }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<(IReadOnlyList<WebhookDeliveryDto> Items, long TotalCount)> ListDeliveriesAsync(
        WebhookDeliveryFilter filter,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        long total;
        var countPredicate = new StringBuilder();
        await using (var count = Command(connection, null, string.Empty))
        {
            AppendFilter(count, countPredicate, filter);
            count.CommandText = $"SELECT count(*) FROM flowbit.webhook_deliveries d WHERE {countPredicate}";
            total = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        }
        var items = new List<WebhookDeliveryDto>();
        var predicate = new StringBuilder();
        await using (var command = Command(connection, null, string.Empty))
        {
            AppendFilter(command, predicate, filter);
            command.CommandText = $"""
                SELECT {DeliveryColumns}
                {DeliveryJoins}
                WHERE {predicate}
                ORDER BY d."OccurredAt" DESC, d."Id" DESC
                OFFSET @offset LIMIT @limit
                """;
            Add(command, "offset", offset);
            Add(command, "limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadDelivery(reader));
            }
        }
        return (items, total);
    }

    public async Task<WebhookDeliveryDetailDto?> GetDeliveryAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, null, $"""
            SELECT {DeliveryColumns}, e."Body", d."GenerationStartedAt", d."DeadlineAt"
            {DeliveryJoins}
            JOIN flowbit.lifecycle_events e ON e."Id" = d."EventId"
            WHERE d."Id" = @id
            """);
        Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var delivery = ReadDelivery(reader);
        return new WebhookDeliveryDetailDto(
            delivery,
            reader.GetString(6),
            Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(27)),
            reader.GetString(25),
            NullableTimestamp(reader, 28),
            NullableTimestamp(reader, 29),
            WebhookConstraints.MaxAttemptsPerGeneration,
            delivery.CanRetry,
            delivery.CanDiscard);
    }

    public async Task<(IReadOnlyList<WebhookDeliveryAttemptDto> Items, long TotalCount)?> ListAttemptsAsync(
        Guid deliveryId,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        long total;
        await using (var count = Command(connection, null, """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM flowbit.webhook_deliveries WHERE "Id" = @id)
                        THEN (SELECT count(*) FROM flowbit.webhook_delivery_attempts WHERE "DeliveryId" = @id)
                        ELSE -1 END
            """))
        {
            Add(count, "id", deliveryId);
            total = (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        }
        if (total < 0)
        {
            return null;
        }
        var items = new List<WebhookDeliveryAttemptDto>();
        await using var command = Command(connection, null, """
            SELECT "Id", "AttemptNumber", "RetryGeneration", "LeaseGeneration", "SigningKeyIds", "AcquiredAt",
                   "SendIntentAt", "CompletedAt", "Outcome", "HttpStatus", "DurationMilliseconds"
            FROM flowbit.webhook_delivery_attempts
            WHERE "DeliveryId" = @id
            ORDER BY "AttemptNumber"
            OFFSET @offset LIMIT @limit
            """);
        Add(command, "id", deliveryId);
        Add(command, "offset", offset);
        Add(command, "limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new WebhookDeliveryAttemptDto(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetFieldValue<Guid[]>(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                NullableTimestamp(reader, 6),
                NullableTimestamp(reader, 7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10)));
        }
        return (items, total);
    }

    public async Task<WebhookDeliveryActionResultDto?> RetryDeliveryAsync(
        Guid id,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var subscriptionId = await DeliverySubscriptionAsync(connection, transaction, id, cancellationToken);
        if (subscriptionId is null)
        {
            return null;
        }
        var subscription = await ShareSubscriptionAsync(connection, transaction, subscriptionId.Value, cancellationToken);
        var delivery = await LockDeliveryAsync(connection, transaction, id, cancellationToken);
        if (subscription is null || delivery is null)
        {
            return null;
        }
        RejectRecoveryWhenArchived(subscription.Value.State, "retried");
        RequireDeliveryRevision(delivery.Value, expectedRevision);
        if (delivery.Value.State != WebhookDeliveryStates.Failed)
        {
            throw new WorkflowConflictException(
                $"Only a failed delivery can be retried; this delivery is {delivery.Value.State}.");
        }
        var state = subscription.Value.State;
        if (state != WebhookSubscriptionStates.Active
            && !(delivery.Value.IsTest && state == WebhookSubscriptionStates.Paused))
        {
            throw new WorkflowConflictException("Retry requires an active subscription. Resume it first.");
        }
        await RequireCurrentKeyAsync(connection, transaction, subscriptionId.Value, cancellationToken);
        WebhookDeliveryActionResultDto result;
        await using (var update = Command(connection, transaction, """
            UPDATE flowbit.webhook_deliveries
               SET "State" = 'pending', "DueAt" = now(), "RetryGeneration" = "RetryGeneration" + 1,
                   "GenerationStartedAt" = NULL, "DeadlineAt" = NULL, "GenerationAttempts" = 0,
                   "FailureCode" = NULL, "Revision" = "Revision" + 1, "UpdatedAt" = now()
             WHERE "Id" = @id
            RETURNING "State", "Revision", "RetryGeneration"
            """))
        {
            Add(update, "id", id);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            result = new WebhookDeliveryActionResultDto(id, reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2));
        }
        await AuditAsync(connection, transaction, "deliveryRetried", actor, subscriptionId, id,
            new { retryGeneration = result.RetryGeneration }, cancellationToken);
        await using (var notify = Command(connection, transaction, "SELECT pg_notify('flowbit_lifecycle_events', '')"))
        {
            await notify.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<WebhookDeliveryActionResultDto?> DiscardDeliveryAsync(
        Guid id,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var subscriptionId = await DeliverySubscriptionAsync(connection, transaction, id, cancellationToken);
        if (subscriptionId is null)
        {
            return null;
        }
        var subscription = await ShareSubscriptionAsync(connection, transaction, subscriptionId.Value, cancellationToken);
        var delivery = await LockDeliveryAsync(connection, transaction, id, cancellationToken);
        if (subscription is null || delivery is null)
        {
            return null;
        }
        RejectRecoveryWhenArchived(subscription.Value.State, "discarded by hand");
        RequireDeliveryRevision(delivery.Value, expectedRevision);
        if (delivery.Value.State == WebhookDeliveryStates.Delivering)
        {
            throw new WorkflowConflictException(
                "A Worker is delivering this event now. Wait for the attempt to finish, then discard it.");
        }
        if (delivery.Value.State is not (WebhookDeliveryStates.Pending
            or WebhookDeliveryStates.RetryScheduled
            or WebhookDeliveryStates.Failed))
        {
            throw new WorkflowConflictException($"This delivery is already {delivery.Value.State}.");
        }
        WebhookDeliveryActionResultDto result;
        await using (var update = Command(connection, transaction, """
            UPDATE flowbit.webhook_deliveries
               SET "State" = 'discarded', "SettledAt" = now(), "Revision" = "Revision" + 1, "UpdatedAt" = now()
             WHERE "Id" = @id
            RETURNING "State", "Revision", "RetryGeneration"
            """))
        {
            Add(update, "id", id);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            result = new WebhookDeliveryActionResultDto(id, reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2));
        }
        await AuditAsync(connection, transaction, "deliveryDiscarded", actor, subscriptionId, id,
            new { previousState = delivery.Value.State }, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<WebhookSubscriptionDto?> ReadSubscriptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid id,
        bool activeKeysOnly,
        CancellationToken cancellationToken)
    {
        SubscriptionRow? row = null;
        await using (var command = Command(connection, transaction, $"""
            SELECT {SubscriptionColumns}
            {SubscriptionJoins}
            WHERE s."Id" = @id
            """))
        {
            Add(command, "id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                row = ReadSubscription(reader);
            }
        }
        if (row is null)
        {
            return null;
        }
        var keys = await ReadKeysAsync(connection, transaction, [id], activeKeysOnly, cancellationToken);
        return row.ToDto(keys);
    }

    private static async Task<Dictionary<Guid, List<WebhookSigningKeyDto>>> ReadKeysAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid[] subscriptionIds,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        var keys = new Dictionary<Guid, List<WebhookSigningKeyDto>>();
        if (subscriptionIds.Length == 0)
        {
            return keys;
        }
        await using var command = Command(connection, transaction, $"""
            SELECT "SubscriptionId", "Id", "Status", "CreatedAt", "CreatedBy", "OverlapExpiresAt", "RevokedAt",
                   "RevokedBy", "RetiredAt"
            FROM flowbit.webhook_signing_keys
            WHERE "SubscriptionId" = ANY(@ids)
              {(activeOnly ? "AND \"Status\" IN ('current', 'previous')" : string.Empty)}
            ORDER BY "SubscriptionId", "CreatedAt" DESC, "Id" DESC
            """);
        Add(command, "ids", subscriptionIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var subscriptionId = reader.GetGuid(0);
            if (!keys.TryGetValue(subscriptionId, out var list))
            {
                keys[subscriptionId] = list = [];
            }
            list.Add(new WebhookSigningKeyDto(
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetString(4),
                NullableTimestamp(reader, 5),
                NullableTimestamp(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                NullableTimestamp(reader, 8)));
        }
        return keys;
    }

    private static async Task<LockedSubscription?> LockSubscriptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT s."State", s."PauseReason", s."Revision", r."EndpointUrl", r."EventTypes", r."WorkflowKey"
            FROM flowbit.webhook_subscriptions s
            JOIN flowbit.webhook_subscription_revisions r ON r."Id" = s."CurrentRevisionId"
            WHERE s."Id" = @id
            FOR UPDATE OF s
            """);
        Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new LockedSubscription(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetFieldValue<string[]>(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    // Recovery reads the subscription gate under a share lock, so a concurrent pause or archive either
    // commits first or waits, without blocking other recoveries.
    private static async Task<(string State, long Revision)?> ShareSubscriptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT "State", "Revision" FROM flowbit.webhook_subscriptions WHERE "Id" = @id FOR SHARE
            """);
        Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    private static async Task<Guid?> DeliverySubscriptionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT "SubscriptionId" FROM flowbit.webhook_deliveries WHERE "Id" = @id
            """);
        Add(command, "id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid subscriptionId ? subscriptionId : null;
    }

    private static async Task<(string State, long Revision, bool IsTest)?> LockDeliveryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT "State", "Revision", "IsTest" FROM flowbit.webhook_deliveries WHERE "Id" = @id FOR UPDATE
            """);
        Add(command, "id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetInt64(1), reader.GetBoolean(2))
            : null;
    }

    private static async Task<List<LockedKey>> LockSigningKeysAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT "Id", "Status", "OverlapExpiresAt", now()
            FROM flowbit.webhook_signing_keys
            WHERE "SubscriptionId" = @id AND "Status" IN ('current', 'previous')
            ORDER BY "Id"
            FOR UPDATE
            """);
        Add(command, "id", subscriptionId);
        var keys = new List<LockedKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(new LockedKey(
                reader.GetGuid(0),
                reader.GetString(1),
                NullableTimestamp(reader, 2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return keys;
    }

    private async Task ExpireKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid keyId,
        string status,
        string? revokedBy,
        CancellationToken cancellationToken)
    {
        // Retired material stays until a request already signed with it can no longer be in flight.
        await using var command = Command(connection, transaction, """
            UPDATE flowbit.webhook_signing_keys
               SET "Status" = @status, "RetiredAt" = now(),
                   "RevokedAt" = CASE WHEN @status = 'revoked' THEN now() ELSE "RevokedAt" END,
                   "RevokedBy" = CASE WHEN @status = 'revoked' THEN @revoked_by ELSE "RevokedBy" END,
                   "RemoveSecretNotBefore" = now() + make_interval(secs => @grace)
             WHERE "Id" = @id
            """);
        Add(command, "id", keyId);
        Add(command, "status", status);
        Add(command, "revoked_by", revokedBy, NpgsqlDbType.Varchar);
        Add(command, "grace", options.SecretRemovalGrace.TotalSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RequireCurrentKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT EXISTS (
                SELECT 1 FROM flowbit.webhook_signing_keys
                WHERE "SubscriptionId" = @id AND "Status" = 'current' AND "ProtectedSecret" IS NOT NULL)
            """);
        Add(command, "id", subscriptionId);
        if (!(bool)(await command.ExecuteScalarAsync(cancellationToken))!)
        {
            throw new WorkflowConflictException(
                "The subscription has no usable signing key. Rotate its secret before sending.");
        }
    }

    private static async Task<bool> AnyMatchingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WebhookDeliveryFilter filter,
        CancellationToken cancellationToken)
    {
        var predicate = new StringBuilder();
        await using var command = Command(connection, transaction, string.Empty);
        AppendFilter(command, predicate, filter);
        command.CommandText = $"SELECT EXISTS (SELECT 1 FROM flowbit.webhook_deliveries d WHERE {predicate})";
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<Guid[]> ReadGuidsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids.ToArray();
    }

    private static void AppendFilter(NpgsqlCommand command, StringBuilder predicate, WebhookDeliveryFilter filter)
    {
        predicate.Append("TRUE");
        if (filter.SubscriptionId is { } subscriptionId)
        {
            predicate.Append(" AND d.\"SubscriptionId\" = @f_subscription");
            Add(command, "f_subscription", subscriptionId);
        }
        if (filter.EventId is { } eventId)
        {
            predicate.Append(" AND d.\"EventId\" = @f_event");
            Add(command, "f_event", eventId);
        }
        if (filter.EventType is { } eventType)
        {
            predicate.Append(" AND d.\"EventType\" = @f_type");
            Add(command, "f_type", eventType);
        }
        if (filter.WorkflowKey is { } workflowKey)
        {
            predicate.Append(" AND d.\"WorkflowKey\" = @f_workflow_key");
            Add(command, "f_workflow_key", workflowKey);
        }
        if (filter.InstanceId is { } instanceId)
        {
            predicate.Append(" AND d.\"InstanceId\" = @f_instance");
            Add(command, "f_instance", instanceId);
        }
        if (filter.RevisionId is { } revisionId)
        {
            predicate.Append(" AND d.\"RevisionId\" = @f_revision");
            Add(command, "f_revision", revisionId);
        }
        if (filter.States is { Count: > 0 } states)
        {
            predicate.Append(" AND d.\"State\" = ANY(@f_states)");
            Add(command, "f_states", states.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
        }
        if (filter.FailureCode is { } failureCode)
        {
            predicate.Append(" AND d.\"FailureCode\" = @f_failure");
            Add(command, "f_failure", failureCode);
        }
        if (filter.From is { } from)
        {
            predicate.Append(" AND d.\"OccurredAt\" >= @f_from");
            Add(command, "f_from", from);
        }
        if (filter.To is { } to)
        {
            predicate.Append(" AND d.\"OccurredAt\" < @f_to");
            Add(command, "f_to", to);
        }
    }

    private static object AuditFilter(WebhookDeliveryFilter filter) => new
    {
        filter.EventType,
        filter.WorkflowKey,
        filter.InstanceId,
        filter.RevisionId,
        filter.FailureCode,
        filter.From,
        filter.To
    };

    private static async Task AuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string operation,
        string actor,
        Guid? subscriptionId,
        Guid? deliveryId,
        object changes,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO flowbit.webhook_management_audit
                ("Operation", "Actor", "SubscriptionId", "DeliveryId", "OccurredAt", "ChangesJson")
            VALUES (@operation, @actor, @subscription, @delivery, now(), @changes)
            """);
        Add(command, "operation", operation);
        Add(command, "actor", actor);
        Add(command, "subscription", subscriptionId, NpgsqlDbType.Uuid);
        Add(command, "delivery", deliveryId, NpgsqlDbType.Uuid);
        Add(command, "changes", JsonSerializer.Serialize(changes, AuditJson), NpgsqlDbType.Jsonb);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void RequireRevision(LockedSubscription subscription, long expectedRevision)
    {
        if (subscription.Revision != expectedRevision)
        {
            throw new WorkflowConflictException(
                $"The subscription changed (current revision {subscription.Revision}). Refresh and try again.");
        }
    }

    private static void RequireDeliveryRevision((string State, long Revision, bool IsTest) delivery, long expectedRevision)
    {
        if (delivery.Revision != expectedRevision)
        {
            throw new WorkflowConflictException(
                $"The delivery changed (current revision {delivery.Revision}, now {delivery.State}). Refresh and try again.");
        }
    }

    private static void RequireNotArchived(LockedSubscription subscription)
    {
        if (subscription.State == WebhookSubscriptionStates.Archived)
        {
            throw new WorkflowConflictException("The subscription is archived; only reads remain available.");
        }
    }

    // Archive leaves its outstanding work to the Worker's archival coordinator, the only writer that remains.
    private static void RejectRecoveryWhenArchived(string subscriptionState, string action)
    {
        if (subscriptionState == WebhookSubscriptionStates.Archived)
        {
            throw new WorkflowConflictException(
                $"The subscription is archived: the Worker discards its outstanding deliveries automatically, so they cannot be {action}.");
        }
    }

    private static SubscriptionRow ReadSubscription(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt32(5),
        reader.GetInt64(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.GetString(8),
        reader.GetFieldValue<DateTimeOffset>(9),
        reader.GetString(10),
        NullableTimestamp(reader, 11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        NullableTimestamp(reader, 13),
        ReadDestination(reader, 14),
        new WebhookReceiverHealthDto(
            reader.GetInt32(21),
            NullableTimestamp(reader, 22),
            NullableTimestamp(reader, 23),
            NullableTimestamp(reader, 24),
            NullableTimestamp(reader, 25),
            reader.IsDBNull(26) ? null : reader.GetString(26),
            reader.IsDBNull(27) ? null : reader.GetInt32(27),
            NullableTimestamp(reader, 28)),
        new WebhookQueueCountsDto(
            reader.GetInt64(29),
            reader.GetInt64(30),
            reader.GetInt64(31),
            reader.GetInt64(32)));

    private static WebhookDestinationDto ReadDestination(NpgsqlDataReader reader, int offset)
    {
        var url = reader.GetString(offset + 2);
        return new WebhookDestinationDto(
            reader.GetInt64(offset),
            reader.GetInt32(offset + 1),
            url,
            WebhookDestinationPolicy.TryOrigin(url) ?? string.Empty,
            reader.GetFieldValue<string[]>(offset + 3),
            reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
            reader.GetFieldValue<DateTimeOffset>(offset + 5),
            reader.GetString(offset + 6));
    }

    private static WebhookDeliveryDto ReadDelivery(NpgsqlDataReader reader)
    {
        var state = reader.GetString(12);
        var isTest = reader.GetBoolean(8);
        var subscriptionState = reader.GetString(25);
        // These mirror the single retry and discard rules; an archived subscription allows neither.
        var canRetry = state == WebhookDeliveryStates.Failed
            && reader.GetBoolean(26)
            && (subscriptionState == WebhookSubscriptionStates.Active
                || (isTest && subscriptionState == WebhookSubscriptionStates.Paused));
        var canDiscard = subscriptionState != WebhookSubscriptionStates.Archived
            && (state is WebhookDeliveryStates.Pending or WebhookDeliveryStates.RetryScheduled or WebhookDeliveryStates.Failed);
        return new WebhookDeliveryDto(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt32(5),
            WebhookDestinationPolicy.TryOrigin(reader.GetString(6)) ?? string.Empty,
            reader.GetString(7),
            reader.GetBoolean(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.GetFieldValue<DateTimeOffset>(11),
            state,
            state is WebhookDeliveryStates.Pending or WebhookDeliveryStates.RetryScheduled
                ? reader.GetFieldValue<DateTimeOffset>(13)
                : null,
            reader.GetInt32(14),
            reader.GetInt32(15),
            reader.GetInt32(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetInt32(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            NullableTimestamp(reader, 20),
            NullableTimestamp(reader, 21),
            reader.GetInt64(22),
            reader.GetFieldValue<DateTimeOffset>(23),
            reader.GetFieldValue<DateTimeOffset>(24),
            canRetry,
            canDiscard);
    }

    private static DateTimeOffset? NullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    internal static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql) =>
        new(sql, connection, transaction);

    internal static void Add(NpgsqlCommand command, string name, object? value, NpgsqlDbType? type = null)
    {
        var parameter = type is null
            ? new NpgsqlParameter(name, value ?? DBNull.Value)
            : new NpgsqlParameter(name, type.Value) { Value = value ?? DBNull.Value };
        command.Parameters.Add(parameter);
    }

    private sealed record LockedSubscription(
        string State,
        string? PauseReason,
        long Revision,
        string EndpointUrl,
        string[] EventTypes,
        string? WorkflowKey)
    {
        // The URL can carry a receiver token.
        public override string ToString() => nameof(LockedSubscription);
    }

    private sealed record LockedKey(Guid Id, string Status, DateTimeOffset? OverlapExpiresAt, DateTimeOffset Now);

    private sealed record SubscriptionRow(
        Guid Id,
        string Name,
        string? Description,
        string State,
        string? PauseReason,
        int MaxConcurrency,
        long Revision,
        DateTimeOffset CreatedAt,
        string CreatedBy,
        DateTimeOffset UpdatedAt,
        string UpdatedBy,
        DateTimeOffset? ArchivedAt,
        string? ArchivedBy,
        DateTimeOffset? ArchiveSettledAt,
        WebhookDestinationDto Destination,
        WebhookReceiverHealthDto Health,
        WebhookQueueCountsDto Queue)
    {
        public WebhookSubscriptionDto ToDto(IReadOnlyDictionary<Guid, List<WebhookSigningKeyDto>> keys) => new(
            Id,
            Name,
            Description,
            State,
            PauseReason,
            MaxConcurrency,
            Revision,
            Destination,
            keys.TryGetValue(Id, out var list) ? list : [],
            Health,
            Queue,
            CreatedAt,
            CreatedBy,
            UpdatedAt,
            UpdatedBy,
            ArchivedAt,
            ArchivedBy,
            ArchiveSettledAt);
    }
}
