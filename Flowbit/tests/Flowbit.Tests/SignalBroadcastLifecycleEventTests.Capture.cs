using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The capture rules themselves, through raw SQL: which status changes raise an event, the gates, the body bound,
/// that capture takes no lock, retention, and the migration's down and up.
/// </summary>
public sealed partial class SignalBroadcastLifecycleEventTests
{
    private const string CaptureMigration = "20261123120000_AddSignalBroadcastLifecycleEvents";
    private const string PreviousMigration = "20261122161924_ValidateSignalStartConstraints";
    private static readonly DateTimeOffset LongExpiredLease = new(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RawSql_RaisesAnEventOnlyForTheStatusThatCommits()
    {
        var family = "signal-events-raw-" + Guid.NewGuid().ToString("N");
        var ids = new List<long>();
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, family, BroadcastTypes, async revision =>
        {
            try
            {
                await using var connection = await fixture.DataSource.OpenConnectionAsync();

                // A row inserted as finished raises its event.
                var inserted = await InsertBroadcastAsync(connection, null, family, SignalBroadcastStatuses.Completed);
                ids.Add(inserted);
                var body = AssertEvent(Assert.Single(await BroadcastEventsAsync(revision, inserted)),
                    LifecycleEventTypes.SignalBroadcastCompleted);
                AssertMatchesRow(body, await BroadcastRowAsync(inserted));

                // A row inserted as delivering, then counters alone, then the same status named again: nothing.
                var open = await InsertBroadcastAsync(connection, null, family, SignalBroadcastStatuses.Delivering);
                ids.Add(open);
                await ExecuteAsync(connection, null,
                    "UPDATE flowbit.signal_broadcasts SET \"DeliveredCount\" = 1, \"SkippedCount\" = 2, \"UpdatedAt\" = now() WHERE \"Id\" = @id", open);
                await ExecuteAsync(connection, null,
                    "UPDATE flowbit.signal_broadcasts SET \"Status\" = \"Status\", \"SkippedCount\" = 2 WHERE \"Id\" = @id", open);
                Assert.Empty(await BroadcastEventsAsync(revision, open));

                // Back to delivering raises nothing; finishing again does.
                await ExecuteAsync(connection, null,
                    "UPDATE flowbit.signal_broadcasts SET \"Status\" = 'delivering', \"CompletedAt\" = NULL WHERE \"Id\" = @id", inserted);
                Assert.Single(await BroadcastEventsAsync(revision, inserted));

                // Two changes in one transaction: only the status that commits is announced.
                await using (var transaction = await connection.BeginTransactionAsync())
                {
                    await ExecuteAsync(connection, transaction,
                        "UPDATE flowbit.signal_broadcasts SET \"Status\" = 'completedWithIssues', \"CompletedAt\" = now() WHERE \"Id\" = @id", open);
                    await ExecuteAsync(connection, transaction,
                        "UPDATE flowbit.signal_broadcasts SET \"Status\" = 'cancelled', \"CancelledAt\" = now() WHERE \"Id\" = @id", open);
                    await transaction.CommitAsync();
                }
                AssertEvent(Assert.Single(await BroadcastEventsAsync(revision, open)), LifecycleEventTypes.SignalBroadcastCancelled);

                // A finish that rolls back, and a row finished and deleted in one transaction, raise nothing.
                var rolledBack = await InsertBroadcastAsync(connection, null, family, SignalBroadcastStatuses.Delivering);
                ids.Add(rolledBack);
                await using (var transaction = await connection.BeginTransactionAsync())
                {
                    await ExecuteAsync(connection, transaction,
                        "UPDATE flowbit.signal_broadcasts SET \"Status\" = 'completed', \"CompletedAt\" = now() WHERE \"Id\" = @id", rolledBack);
                    await transaction.RollbackAsync();
                }
                long deleted;
                await using (var transaction = await connection.BeginTransactionAsync())
                {
                    deleted = await InsertBroadcastAsync(connection, transaction, family, SignalBroadcastStatuses.Delivering);
                    await ExecuteAsync(connection, transaction,
                        "UPDATE flowbit.signal_broadcasts SET \"Status\" = 'completed', \"CompletedAt\" = now() WHERE \"Id\" = @id", deleted);
                    await ExecuteAsync(connection, transaction, "DELETE FROM flowbit.signal_broadcasts WHERE \"Id\" = @id", deleted);
                    await transaction.CommitAsync();
                }
                Assert.Empty(await BroadcastEventsAsync(revision, rolledBack));
                Assert.Empty(await BroadcastEventsAsync(revision, deleted));
                Assert.Equal(2, (await BroadcastEventsAsync(revision)).Length);
            }
            finally
            {
                await DeleteBroadcastsAsync(ids);
            }
        });
    }

    [Fact]
    public async Task Bound_TheWorstTextsCountsAndIdsStayUnderThreeKiB()
    {
        // Every character of both texts escapes to six bytes (\u0001), the most a column character can cost.
        var family = new string((char)0x01, 300);
        var signalName = new string((char)0x1F, 100);
        await DeleteBroadcastsAsync([long.MaxValue]);
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, null, BroadcastTypes, async revision =>
        {
            try
            {
                await using (var connection = await fixture.DataSource.OpenConnectionAsync())
                await using (var command = new NpgsqlCommand("""
                    INSERT INTO flowbit.signal_broadcasts
                        ("Id", "SignalName", "WorkflowKey", "InstanceId", "Status", "TotalDeliveryCount", "StartDeliveryCount",
                         "DeliveredCount", "SkippedCount", "FailedCount", "CancelledCount", "RecipientsJson", "RecipientsTruncated",
                         "ThrownByKind", "ThrownBy", "ThrownByRolesJson", "ThrowerInstanceId", "ThrowerNodeId", "CompletedAt")
                    VALUES (@id, @name, @key, @id, 'completedWithIssues', 100000, 100000, 25000, 25000, 25000, 25000, '[]', false,
                            'instance', '1', '[]', @id, 2147483647, now())
                    """, connection))
                {
                    command.Parameters.AddWithValue("id", long.MaxValue);
                    command.Parameters.AddWithValue("name", signalName);
                    command.Parameters.AddWithValue("key", family);
                    await command.ExecuteNonQueryAsync();
                }

                var captured = Assert.Single(await BroadcastEventsAsync(revision, long.MaxValue));
                var body = AssertEvent(captured, LifecycleEventTypes.SignalBroadcastCompletedWithIssues);
                Assert.Equal((signalName, family, "instance", (long?)long.MaxValue, (long?)long.MaxValue),
                    (body.Data.SignalName, body.WorkflowKey, body.Data.Scope, body.InstanceId, body.Data.ThrownByInstanceId));
                var bytes = Encoding.UTF8.GetByteCount(captured.Body);
                Assert.InRange(bytes, 2_400, 3 * 1024 - 1);
            }
            finally
            {
                await DeleteBroadcastsAsync([long.MaxValue]);
            }
        });
    }

    [Fact]
    public async Task Gates_StoreNothingAndASubscriptionEditAffectsOnlyLaterFinishes()
    {
        var family = "signal-events-gates-" + Guid.NewGuid().ToString("N");
        var (activeId, active) = await WebhookTestDatabase.CreateSubscriptionAsync(fixture.DataSource, BroadcastTypes, family);
        var (pausedId, paused) = await WebhookTestDatabase.CreateSubscriptionAsync(
            fixture.DataSource, BroadcastTypes, family, state: "paused");
        var (archivedId, archived) = await WebhookTestDatabase.CreateSubscriptionAsync(fixture.DataSource, BroadcastTypes, family);
        await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, archivedId, "archived");
        // A subscription saved before the broadcast types existed lists the eleven earlier types.
        var earlierTypes = LifecycleEventTypes.Subscribable.Except(LifecycleEventTypes.SignalBroadcastTypes).ToArray();
        Assert.Equal(11, earlierTypes.Length);
        var (earlierId, earlier) = await WebhookTestDatabase.CreateSubscriptionAsync(fixture.DataSource, earlierTypes, family);
        var ids = new List<long>();
        try
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await WebhookTestDatabase.SetCaptureAsync(fixture.DataSource, false);
            ids.Add(await InsertBroadcastAsync(connection, null, family, SignalBroadcastStatuses.Completed));
            Assert.Empty(await BroadcastEventsAsync(active));

            await WebhookTestDatabase.SetCaptureAsync(fixture.DataSource, true);
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await ExecuteAsync(connection, transaction, "SET LOCAL flowbit.lifecycle_events = 'off'", null);
                ids.Add(await InsertBroadcastAsync(connection, transaction, family, SignalBroadcastStatuses.Completed));
                await transaction.CommitAsync();
            }
            Assert.Empty(await BroadcastEventsAsync(active));

            var captured = await InsertBroadcastAsync(connection, null, family, SignalBroadcastStatuses.Completed);
            ids.Add(captured);
            Assert.Equal(captured, Assert.Single(await BroadcastEventsAsync(active)).SourceId);
            Assert.Empty(await BroadcastEventsAsync(paused));
            Assert.Empty(await BroadcastEventsAsync(archived));
            // An existing subscription receives no new type until an administrator adds it.
            Assert.Empty(await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, earlier));

            // The edited subscription lists only cancellations: a later completion reaches neither revision.
            long edited;
            await using (var command = new NpgsqlCommand("""
                WITH next AS (
                    INSERT INTO flowbit.webhook_subscription_revisions
                        ("SubscriptionId", "ConfigurationRevision", "EndpointUrl", "EventTypes", "WorkflowKey", "CreatedAt", "CreatedBy")
                    SELECT "SubscriptionId", 2, "EndpointUrl", ARRAY['signalBroadcast.cancelled'], "WorkflowKey", now(), 'tests'
                    FROM flowbit.webhook_subscription_revisions WHERE "Id" = @revision
                    RETURNING "Id", "SubscriptionId")
                UPDATE flowbit.webhook_subscriptions s SET "CurrentRevisionId" = next."Id"
                FROM next WHERE s."Id" = next."SubscriptionId"
                RETURNING next."Id"
                """, connection))
            {
                command.Parameters.AddWithValue("revision", active);
                edited = (long)(await command.ExecuteScalarAsync())!;
            }
            ids.Add(await InsertBroadcastAsync(connection, null, family, SignalBroadcastStatuses.Completed));
            var cancelled = await InsertBroadcastAsync(connection, null, family, SignalBroadcastStatuses.Cancelled);
            ids.Add(cancelled);
            Assert.Equal(captured, Assert.Single(await BroadcastEventsAsync(active)).SourceId);
            Assert.Equal(cancelled, Assert.Single(await BroadcastEventsAsync(edited)).SourceId);
            Assert.Empty(await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, earlier));
        }
        finally
        {
            foreach (var id in new[] { activeId, pausedId, earlierId })
            {
                await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, id, "archived");
            }
            await WebhookTestDatabase.RestoreCaptureAsync(fixture.DataSource);
            await DeleteBroadcastsAsync(ids);
        }
    }

    [Fact]
    public async Task Finish_DoesNotWaitForALockedSubscription()
    {
        var model = SignalRuntimeTests.CatchModel(UniqueSignal());
        await CreateAsync(model);
        var (subscriptionId, revision) = await WebhookTestDatabase.CreateSubscriptionAsync(fixture.DataSource, BroadcastTypes, model.Id);
        try
        {
            await WebhookTestDatabase.SetCaptureAsync(fixture.DataSource, true);
            await using var locker = await fixture.DataSource.OpenConnectionAsync();
            await using var locked = await locker.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand("""
                SELECT 1 FROM flowbit.webhook_subscriptions WHERE "Id" = @subscription FOR UPDATE;
                SELECT 1 FROM flowbit.webhook_subscription_revisions WHERE "Id" = @revision FOR UPDATE;
                """, locker, locked))
            {
                command.Parameters.AddWithValue("subscription", subscriptionId);
                command.Parameters.AddWithValue("revision", revision);
                await command.ExecuteNonQueryAsync();
            }

            var throwing = ThrowAsync(new ThrowSignalRequest(UniqueSignal(), model.Id));
            var first = await Task.WhenAny(throwing, Task.Delay(TimeSpan.FromSeconds(30)));
            await locked.RollbackAsync();
            Assert.Same(throwing, first);
            var broadcast = await throwing;
            AssertEvent(Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id)), LifecycleEventTypes.SignalBroadcastCompleted);
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscriptionId, "archived");
            await WebhookTestDatabase.RestoreCaptureAsync(fixture.DataSource);
        }
    }

    [Fact]
    public async Task Retention_DeletesAFinishedBroadcastAndKeepsItsEvent()
    {
        var model = SignalRuntimeTests.CatchModel(UniqueSignal());
        await CreateAsync(model);
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), model.Id));
            var before = Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
                UPDATE flowbit.signal_broadcasts
                SET "CreatedAt" = now() - interval '10 days', "CompletedAt" = now() - interval '10 days'
                WHERE "Id" = @id
                """, ("id", broadcast.Id));

            await SetRetentionAsync(RetentionCategories.SignalBroadcasts, 1);
            try
            {
                await using var retentionSource = new RetentionDataSource(fixture.ConnectionString);
                var retention = new RetentionRepository(retentionSource, TimeProvider.System);
                // A run already in progress keeps the policies it started with, so finish it and run once more.
                for (var run = 0; run < 2; run++)
                {
                    await retention.RequestRunAsync("tests", CancellationToken.None);
                    for (var tick = 0; tick < 400; tick++)
                    {
                        var result = await retention.ProcessBatchAsync(new RetentionExecutionOptions
                        {
                            WorkerId = "signal-events-retention", BatchSize = 100, MaxRunnableJobs = 1_000_000, MaxQueueLagSeconds = 3600
                        }, CancellationToken.None);
                        if (!result.HasActiveRun) break;
                    }
                }
            }
            finally
            {
                await SetRetentionAsync(RetentionCategories.SignalBroadcasts, null);
            }

            await using (var db = fixture.CreateDbContext())
            {
                Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.Id == broadcast.Id));
            }
            var after = Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));
            Assert.Equal((before.Id, before.Body, before.OccurredAt), (after.Id, after.Body, after.OccurredAt));
        });
    }

    [Fact]
    public async Task Migration_DownStopsTheCaptureAndUpRestoresIt()
    {
        var databaseName = "signal_events_migration_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
        await ExecuteOnAsync(adminBuilder.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        try
        {
            await MigrateToAsync(connectionString, CaptureMigration);
            await using (var dataSource = NpgsqlDataSource.Create(connectionString))
            {
                await WebhookTestDatabase.CreateSubscriptionAsync(dataSource, BroadcastTypes, null);
                await WebhookTestDatabase.SetCaptureAsync(dataSource, true);
                async Task<long> FinishAndCountAsync()
                {
                    await using var connection = await dataSource.OpenConnectionAsync();
                    await InsertBroadcastAsync(connection, null, "orders", SignalBroadcastStatuses.Completed);
                    return await WebhookTestDatabase.CountEventsAsync(dataSource,
                        "SELECT count(*) FROM flowbit.lifecycle_events WHERE \"SourceTable\" = 'signal_broadcasts'");
                }
                const string triggers = """
                    SELECT count(*) FROM pg_catalog.pg_trigger
                    WHERE tgrelid = 'flowbit.signal_broadcasts'::regclass AND tgname LIKE 'TR_signal_broadcasts_lifecycle_%'
                    """;

                Assert.Equal(1, await FinishAndCountAsync());
                Assert.Equal(2, await WebhookTestDatabase.CountEventsAsync(dataSource, triggers));

                await MigrateToAsync(connectionString, PreviousMigration);
                Assert.Equal(0, await WebhookTestDatabase.CountEventsAsync(dataSource, triggers));
                Assert.Equal(0, await WebhookTestDatabase.CountEventsAsync(dataSource,
                    "SELECT count(*) FROM pg_catalog.pg_proc WHERE proname = 'capture_signal_broadcast_lifecycle'"));
                // The stored event stays, and a finish after the downgrade stores nothing.
                Assert.Equal(1, await FinishAndCountAsync());

                await MigrateToAsync(connectionString, CaptureMigration);
                Assert.Equal(2, await FinishAndCountAsync());
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnAsync(adminBuilder.ConnectionString, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
        }
    }

    // HTTP helpers

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "signal-events",
        string roles = "admin")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles.Split(','));
        return await fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsClientAsync(
        HttpMethod method, string path, string clientId, string secret, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        request.Headers.Add("X-Client-Id", clientId);
        request.Headers.Add("X-Client-Secret", secret);
        return await fixture.Client.SendAsync(request);
    }

    private async Task<long> CreateAsync(WorkflowModel model)
    {
        using var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return (await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!.Id;
    }

    private async Task<long> NewVersionAsync(long id, WorkflowModel model)
    {
        using var update = await SendAsync(HttpMethod.Put, $"/api/workflows/{id}", new UpdateWorkflowRequest(model, true));
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
        return (await update.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!.Id;
    }

    private async Task SetDefaultAsync(long id)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/workflows/{id}/set-default");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId, Dictionary<string, JsonElement>? variables = null)
    {
        using var start = await SendAsync(HttpMethod.Post, "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, variables));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        return (await start.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private async Task<SignalBroadcastDetailDto> ThrowAsync(
        ThrowSignalRequest request, string user = "signal-events", string roles = "admin")
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request, user, roles);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
    }

    private async Task<SignalBroadcastDetailDto> GetBroadcastAsync(long id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts/{id}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
    }

    private async Task TakeAsync(long instanceId, int nodeId, int flowId)
    {
        using var response = await TakeResponseAsync(instanceId, nodeId, flowId);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> TakeResponseAsync(long instanceId, int nodeId, int flowId)
    {
        long taskId;
        await using (var db = fixture.CreateDbContext())
        {
            taskId = await db.UserTasks.Where(task => task.InstanceId == instanceId && task.NodeId == nodeId
                && task.Status == UserTaskStatuses.Active).Select(task => task.Id).SingleAsync();
        }
        return await SendAsync(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/{flowId}", new TakeFlowRequest(null));
    }

    // Database helpers

    private async Task<SignalBroadcastEntity> BroadcastRowAsync(long id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SignalBroadcasts.AsNoTracking().SingleAsync(item => item.Id == id);
    }

    private async Task<SignalBroadcastEntity> ThrownByAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SignalBroadcasts.AsNoTracking().SingleAsync(item => item.ThrowerInstanceId == instanceId);
    }

    private async Task<List<long>> DeliveryJobsAsync(long broadcastId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SignalBroadcastJobs.AsNoTracking()
            .Where(link => link.BroadcastId == broadcastId && link.JobId != null)
            .OrderBy(link => link.Id)
            .Select(link => link.JobId!.Value)
            .ToListAsync();
    }

    /// <summary>Keeps a queued job from being leased until a later ProcessJobsAsync makes it due.</summary>
    private async Task HoldJobAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        await db.WorkflowJobs.Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddYears(10)));
    }

    private async Task ProcessDeliveryJobsAsync(long broadcastId) =>
        await ProcessJobsAsync(await DeliveryJobsAsync(broadcastId));

    /// <summary>
    /// Makes the given queued jobs due first and processes leases until none of them is queued. Another class's
    /// leftover job that is leased first is processed too, as the Worker would.
    /// </summary>
    private async Task ProcessJobsAsync(IReadOnlyCollection<long> jobIds)
    {
        for (var round = 0; round < 20; round++)
        {
            await using (var db = fixture.CreateDbContext())
            {
                var open = await db.WorkflowJobs
                    .Where(job => jobIds.Contains(job.Id)
                        && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry))
                    .ToListAsync();
                if (open.Count == 0)
                {
                    return;
                }
                foreach (var job in open)
                {
                    job.Priority = 1_000_000;
                    job.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                }
                await db.SaveChangesAsync();
            }

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                new WorkflowJobLeaseRequest($"signal-events-test-{Guid.NewGuid():N}", 1, 1, 1, TimeSpan.FromMinutes(2)),
                CancellationToken.None);
            foreach (var lease in leases)
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
                    .ProcessAsync(lease, CancellationToken.None);
            }
        }

        Assert.Fail($"Jobs {string.Join(", ", jobIds)} did not finish.");
    }

    /// <summary>Leaves a queued job running on its final attempt with a lease that expired long ago.</summary>
    private async Task ExhaustLeaseAsync(long jobId) =>
        await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
            UPDATE flowbit.workflow_jobs
            SET "Status" = 'running', "AttemptCount" = "MaxAttempts", "WorkerId" = 'signal-events-tests',
                "LeaseToken" = gen_random_uuid(), "LeaseGeneration" = "LeaseGeneration" + 1,
                "LeaseExpiresAt" = @expired, "HeartbeatAt" = @expired, "StartedAt" = now(), "UpdatedAt" = now()
            WHERE "Id" = @id
            """, ("expired", LongExpiredLease), ("id", jobId));

    /// <summary>
    /// Leases through the real repository, which first reconciles running jobs whose final attempt lost its lease,
    /// in the transaction that opens their lease_exhausted incidents. As LifecycleEventCaptureTests does, it leases
    /// no activity job; a due control job left by another suite may be leased. Each call reconciles the job whose
    /// lease expired longest ago, so it repeats until the given job waits in its incident.
    /// </summary>
    private async Task OpenExhaustedLeaseIncidentsAsync(long jobId)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                    new WorkflowJobLeaseRequest($"signal-events-lease:{Guid.NewGuid():N}", 1, 0, 1, TimeSpan.FromMinutes(1)),
                    CancellationToken.None);
            }
            await using var db = fixture.CreateDbContext();
            if (await db.WorkflowJobs.Where(job => job.Id == jobId).Select(job => job.Status).SingleAsync()
                == WorkflowJobStatuses.Incident)
            {
                return;
            }
        }

        Assert.Fail($"Job #{jobId} did not reach its lease_exhausted incident.");
    }

    private async Task<string?> ScalarTextAsync(string sql, long id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (await command.ExecuteScalarAsync()) as string;
    }

    private static async Task<long> InsertBroadcastAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string workflowKey, string status)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO flowbit.signal_broadcasts
                ("SignalName", "WorkflowKey", "Status", "TotalDeliveryCount", "DeliveredCount", "SkippedCount",
                 "FailedCount", "CancelledCount", "RecipientsJson", "RecipientsTruncated", "ThrownByKind", "ThrownBy",
                 "ThrownByRolesJson", "CompletedAt", "CancelledAt")
            VALUES ('raw.signal', @key, @status, 3, 2, 1, 0, 0, '[]', false, 'user', 'raw-tests', '[]',
                    CASE WHEN @status = 'delivering' THEN NULL ELSE now() END,
                    CASE WHEN @status = 'cancelled' THEN now() END)
            RETURNING "Id"
            """, connection, transaction);
        command.Parameters.AddWithValue("key", workflowKey);
        command.Parameters.AddWithValue("status", status);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, long? id)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (id is not null)
        {
            command.Parameters.AddWithValue("id", id.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteBroadcastsAsync(IReadOnlyCollection<long> ids)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("DELETE FROM flowbit.signal_broadcasts WHERE \"Id\" = ANY(@ids)", connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Sets an engine setting, or removes it for null, and returns its previous value.</summary>
    private async Task<string?> SetEngineSettingAsync(string settingNamespace, string key, string? value)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleOrDefaultAsync(item =>
            item.Namespace == settingNamespace && item.Key == key);
        var previous = setting?.Value;
        if (value is null)
        {
            if (setting is not null) db.EngineSettings.Remove(setting);
        }
        else if (setting is null)
        {
            db.EngineSettings.Add(new EngineSettingEntity { Namespace = settingNamespace, Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }
        await db.SaveChangesAsync();
        return previous;
    }

    private async Task SetRetentionAsync(string category, int? days)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE flowbit.retention_policies SET "RetentionDays" = @days, "Revision" = "Revision" + 1, "IsInitialized" = TRUE
            WHERE "Category" = @category
            """, connection);
        command.Parameters.Add(new NpgsqlParameter("days", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)days ?? DBNull.Value });
        command.Parameters.AddWithValue("category", category);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task MigrateToAsync(string connectionString, string targetMigration)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task ExecuteOnAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
