extern alias FlowbitWorker;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Http;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Infrastructure.Webhooks;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using LifecycleEventFanoutService = FlowbitWorker::Flowbit.Worker.LifecycleEventFanoutService;
using PostgresLifecycleWakeupSignal = FlowbitWorker::Flowbit.Worker.PostgresLifecycleWakeupSignal;
using WebhookDispatcher = FlowbitWorker::Flowbit.Worker.WebhookDispatcher;
using WebhookTelemetry = FlowbitWorker::Flowbit.Worker.WebhookTelemetry;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;

namespace Flowbit.Tests;

/// <summary>
/// The Worker's queue against real PostgreSQL and a fixture-owned loopback receiver. Loopback HTTP is the
/// Development fixture exception; nothing here reaches a production endpoint.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class WebhookDispatchTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private readonly List<Guid> pausedForIsolation = [];
    private WebhookDataSource source = null!;
    private WebhookDispatchRepository repository = null!;
    private DataProtectionWebhookSecretProtector protector = null!;
    private WebhookOptions options = null!;

    public async Task InitializeAsync()
    {
        options = new WebhookOptions
        {
            DeliveryEnabled = true,
            PollSeconds = 1,
            HttpTimeoutSeconds = 2,
            LeaseSeconds = 15,
            HeartbeatSeconds = 5,
            Network = new WebhookNetworkOptions { AllowLoopbackHttp = true },
            KeyProtection = new WebhookKeyProtectionOptions
            {
                CertificatePath = fixture.WebhookCertificatePath,
                CertificatePassword = fixture.WebhookCertificatePassword
            }
        };
        source = new WebhookDataSource(fixture.ConnectionString, 8);
        repository = new WebhookDispatchRepository(source);
        protector = new DataProtectionWebhookSecretProtector(options, source, NullLoggerFactory.Instance, TimeProvider.System);
        // Acquisition is deployment-wide: hold other subscriptions so only this test's work is due.
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using (var command = new NpgsqlCommand("""
            UPDATE flowbit.webhook_subscriptions SET "State" = 'paused', "PauseReason" = 'administrator'
            WHERE "State" = 'active' RETURNING "Id"
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                pausedForIsolation.Add(reader.GetGuid(0));
            }
        }
        // Tests ignore pause, so other tests' outstanding test deliveries would be acquired here too.
        await using var tests = new NpgsqlCommand("""
            UPDATE flowbit.webhook_deliveries d
               SET "State" = 'discarded', "SettledAt" = now(), "Revision" = d."Revision" + 1, "UpdatedAt" = now()
              FROM flowbit.webhook_subscriptions s
             WHERE s."Id" = d."SubscriptionId" AND s."State" <> 'archived'
               AND d."IsTest" AND d."State" IN ('pending', 'retryScheduled')
            """, connection);
        await tests.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await WebhookTestDatabase.RestoreCaptureAsync(fixture.DataSource);
        await using (var connection = await source.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("""
            UPDATE flowbit.webhook_subscriptions SET "State" = 'active', "PauseReason" = NULL WHERE "Id" = ANY(@ids)
            """, connection))
        {
            command.Parameters.AddWithValue("ids", pausedForIsolation.ToArray());
            await command.ExecuteNonQueryAsync();
        }
        protector.Dispose();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task HostedWorkerDeliversASignedLifecycleEventThatAnIndependentVerifierAccepts()
    {
        await using var receiver = await WebhookTestReceiver.StartAsync();
        var key = StandardWebhookSignature.GenerateKey();
        var subscription = await CreateSubscriptionAsync(receiver.Url("hook"), key, [LifecycleEventTypes.InstanceStarted]);
        await WebhookTestDatabase.SetCaptureAsync(fixture.DataSource, true);
        var instanceId = await InsertInstanceAsync(subscription.WorkflowKey);

        using var host = BuildWorkerHost();
        await host.StartAsync();
        try
        {
            var received = await receiver.WaitForAsync(1);
            var request = Assert.Single(received);
            var secret = StandardWebhookSignature.FormatSecret(key);
            Assert.True(WebhookSigningTests.IndependentVerifier.Verify(
                secret, request.Id, request.Timestamp, request.Body, request.Signature));
            Assert.Equal("application/json; charset=utf-8", request.ContentType);
            Assert.StartsWith("Flowbit-Webhooks/", request.UserAgent, StringComparison.Ordinal);
            var body = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<InstanceLifecycleEventDataDto>>(Encoding.UTF8.GetString(request.Body));
            Assert.Equal(request.Id, body.EventId.ToString("D"));
            Assert.Equal(instanceId, body.InstanceId);
            Assert.True(Math.Abs(long.Parse(request.Timestamp) - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) < 60);

            var delivery = await WaitForDeliveryAsync(subscription.Id, WebhookDeliveryStates.Succeeded);
            Assert.Equal(1, delivery.LifetimeAttempts);
            Assert.Equal(204, delivery.LastHttpStatus);
            // The sent bytes are exactly the stored event body.
            Assert.Equal(await StoredBodyAsync(delivery.EventId), request.Body);
            Assert.Equal(subscription.KeyId, Assert.Single(await AttemptKeysAsync(delivery.Id)));
        }
        finally
        {
            await host.StopAsync();
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task RetryableFailureSchedulesARetryWithStableBytesAndIdentity()
    {
        await using var receiver = await WebhookTestReceiver.StartAsync(_ => 503);
        var subscription = await CreateSubscriptionAsync(receiver.Url("busy"), StandardWebhookSignature.GenerateKey());
        var delivery = await QueueTestDeliveryAsync(subscription.Id);
        try
        {
            var first = await SendOnceAsync();
            Assert.Equal(WebhookOutcomeCodes.HttpRetryable, first.Outcome);
            var row = await DeliveryAsync(delivery);
            Assert.Equal(WebhookDeliveryStates.RetryScheduled, row.State);
            Assert.Equal(1, row.GenerationAttempts);
            Assert.NotNull(row.DeadlineAt);
            Assert.InRange(row.DueAt - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));

            await MakeDueAsync(delivery);
            await SendOnceAsync();
            var requests = receiver.Requests.ToArray();
            Assert.Equal(2, requests.Length);
            Assert.Equal(requests[0].Id, requests[1].Id);
            Assert.Equal(requests[0].Body, requests[1].Body);
            Assert.Equal(2, (await DeliveryAsync(delivery)).GenerationAttempts);
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task GoneReceiverFailsTheDeliveryAndPausesTheSubscriptionAutomatically()
    {
        await using var receiver = await WebhookTestReceiver.StartAsync(_ => 410);
        var subscription = await CreateSubscriptionAsync(receiver.Url("gone"), StandardWebhookSignature.GenerateKey(), active: true);
        var delivery = await QueueTestDeliveryAsync(subscription.Id);
        try
        {
            var result = await SendOnceAsync();
            Assert.Equal(WebhookOutcomeCodes.ReceiverGone, result.Outcome);
            var row = await DeliveryAsync(delivery);
            Assert.Equal(WebhookDeliveryStates.Failed, row.State);
            Assert.Equal(WebhookOutcomeCodes.ReceiverGone, row.FailureCode);
            Assert.Equal(("paused", "receiverGone"), await SubscriptionStateAsync(subscription.Id));
            Assert.Equal(1, await WebhookTestDatabase.CountEventsAsync(
                fixture.DataSource,
                "SELECT count(*) FROM flowbit.webhook_management_audit WHERE \"SubscriptionId\" = @id AND \"Operation\" = 'automaticPause' AND \"Actor\" = 'system'",
                ("id", subscription.Id)));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task ReceiverFailuresLeaveTheInstanceItsJobsAndItsIncidentsUntouched()
    {
        var statuses = new ConcurrentQueue<int>([503, 410]);
        await using var receiver = await WebhookTestReceiver.StartAsync(_ => statuses.TryDequeue(out var status) ? status : 410);
        var subscription = await CreateSubscriptionAsync(
            receiver.Url("failing"), StandardWebhookSignature.GenerateKey(), [LifecycleEventTypes.InstanceStarted]);
        await WebhookTestDatabase.SetCaptureAsync(fixture.DataSource, true);
        try
        {
            var instanceId = await InsertInstanceAsync(subscription.WorkflowKey);
            var before = await InstanceFootprintAsync(instanceId);
            await repository.FanoutAsync(1000, CancellationToken.None);

            Assert.Equal(WebhookOutcomeCodes.HttpRetryable, (await SendOnceAsync()).Outcome);
            await MakeAllDueAsync(subscription.Id);
            Assert.Equal(WebhookOutcomeCodes.ReceiverGone, (await SendOnceAsync()).Outcome);
            Assert.Equal(("paused", "receiverGone"), await SubscriptionStateAsync(subscription.Id));

            // Receiver failures stay in the webhook tables: no workflow job, incident, or instance change follows.
            Assert.Equal(before, await InstanceFootprintAsync(instanceId));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Theory]
    [InlineData(302, WebhookOutcomeCodes.RedirectRefused)]
    [InlineData(400, WebhookOutcomeCodes.ReceiverRejected)]
    public async Task RedirectsAndRejectionsFailWithoutFollowingOrRetrying(int status, string expected)
    {
        await using var receiver = await WebhookTestReceiver.StartAsync(context =>
        {
            context.Response.Headers.Location = "http://127.0.0.1:1/elsewhere";
            return status;
        });
        var subscription = await CreateSubscriptionAsync(receiver.Url("redirect"), StandardWebhookSignature.GenerateKey());
        var delivery = await QueueTestDeliveryAsync(subscription.Id);
        try
        {
            var result = await SendOnceAsync();
            Assert.Equal(expected, result.Outcome);
            Assert.Single(receiver.Requests);
            var row = await DeliveryAsync(delivery);
            Assert.Equal(WebhookDeliveryStates.Failed, row.State);
            Assert.Equal(expected, row.FailureCode);
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task ExpiredLeasesAreRecoveredAsUnknownAndStaleWorkersCannotFinalize()
    {
        await using var receiver = await WebhookTestReceiver.StartAsync();
        var subscription = await CreateSubscriptionAsync(receiver.Url("slow"), StandardWebhookSignature.GenerateKey());
        var delivery = await QueueTestDeliveryAsync(subscription.Id);
        try
        {
            var lease = Assert.Single(await repository.AcquireAsync(
                new WebhookAcquireRequest("stale-worker", 4, TimeSpan.FromSeconds(15), 100), CancellationToken.None));
            Assert.Equal(WebhookSendDecisionKind.Send,
                (await repository.PrepareSendAsync(lease, [subscription.KeyId], null, CancellationToken.None)).Kind);
            await ExpireLeaseAsync(delivery);
            Assert.Equal(1, await repository.RecoverExpiredLeasesAsync(100, CancellationToken.None));

            var row = await DeliveryAsync(delivery);
            Assert.Equal(WebhookDeliveryStates.RetryScheduled, row.State);
            Assert.Equal(WebhookOutcomeCodes.OutcomeUnknown, row.LastOutcome);
            Assert.Equal(WebhookOutcomeCodes.OutcomeUnknown, Assert.Single(await AttemptOutcomesAsync(delivery)));

            var stale = await repository.FinalizeAsync(lease, new WebhookSendOutcome(WebhookOutcomeCodes.Succeeded, 200, null, 5), CancellationToken.None);
            Assert.False(stale.Applied);
            Assert.Equal(WebhookDeliveryStates.RetryScheduled, (await DeliveryAsync(delivery)).State);
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task APrepareSendWhoseLeaseEndsWhileItWaitsForTheRowSendsNothing()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/row-wait", StandardWebhookSignature.GenerateKey(), active: true);
        var delivery = await InsertEventDeliveryAsync(subscription);
        try
        {
            var lease = Assert.Single(await AcquireOwnAsync(subscription.Id, 4));
            await ShortenLeaseAsync(delivery);
            var decision = await AfterARowLockAsync(
                "SELECT 1 FROM flowbit.webhook_deliveries WHERE \"Id\" = @id FOR UPDATE", delivery,
                () => repository.PrepareSendAsync(lease, [subscription.KeyId], null, CancellationToken.None));

            // Sending now could overlap another Worker that recovers the expired lease.
            Assert.Equal(WebhookSendDecisionKind.LeaseLost, decision.Kind);
            Assert.Equal(0, (await DeliveryAsync(delivery)).GenerationAttempts);
            Assert.Equal(0, await WebhookTestDatabase.CountEventsAsync(fixture.DataSource,
                "SELECT count(*) FROM flowbit.webhook_delivery_attempts WHERE \"DeliveryId\" = @id AND \"SendIntentAt\" IS NOT NULL",
                ("id", delivery)));
            Assert.True(await repository.RecoverExpiredLeasesAsync(100, CancellationToken.None) >= 1);
            Assert.Equal(WebhookDeliveryStates.Pending, (await DeliveryAsync(delivery)).State);
            Assert.Equal(WebhookOutcomeCodes.AbortedBeforeSend, Assert.Single(await AttemptOutcomesAsync(delivery)));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task AFinalizeWhoseLeaseEndsWhileItWaitsForTheDispatchStateRecordsNothing()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/state-wait", StandardWebhookSignature.GenerateKey(), active: true);
        var delivery = await InsertEventDeliveryAsync(subscription);
        try
        {
            var lease = Assert.Single(await AcquireOwnAsync(subscription.Id, 4));
            Assert.Equal(WebhookSendDecisionKind.Send,
                (await repository.PrepareSendAsync(lease, [subscription.KeyId], null, CancellationToken.None)).Kind);
            await ShortenLeaseAsync(delivery);
            // Another finalize of the same subscription holds its dispatch state.
            var result = await AfterARowLockAsync(
                "SELECT 1 FROM flowbit.webhook_subscription_dispatch_state WHERE \"SubscriptionId\" = @id FOR UPDATE", subscription.Id,
                () => repository.FinalizeAsync(lease, new WebhookSendOutcome(WebhookOutcomeCodes.Succeeded, 200, null, 5), CancellationToken.None));

            Assert.False(result.Applied);
            Assert.Equal(WebhookDeliveryStates.Delivering, (await DeliveryAsync(delivery)).State);
            // Recovery then treats the send as one whose outcome is unknown.
            Assert.True(await repository.RecoverExpiredLeasesAsync(100, CancellationToken.None) >= 1);
            Assert.Equal(WebhookDeliveryStates.RetryScheduled, (await DeliveryAsync(delivery)).State);
            Assert.Equal(WebhookOutcomeCodes.OutcomeUnknown, Assert.Single(await AttemptOutcomesAsync(delivery)));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task PauseBeforeSendIntentReleasesTheLeaseWithoutSpendingBudget()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/never", StandardWebhookSignature.GenerateKey(), active: true);
        var delivery = await InsertEventDeliveryAsync(subscription);
        try
        {
            var lease = Assert.Single(await repository.AcquireAsync(
                new WebhookAcquireRequest("pausing-worker", 4, TimeSpan.FromSeconds(15), 100), CancellationToken.None));
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "paused");
            var decision = await repository.PrepareSendAsync(lease, [subscription.KeyId], null, CancellationToken.None);
            Assert.Equal(WebhookSendDecisionKind.Aborted, decision.Kind);
            var row = await DeliveryAsync(delivery);
            Assert.Equal(WebhookDeliveryStates.Pending, row.State);
            Assert.Equal(0, row.GenerationAttempts);
            Assert.Null(row.DeadlineAt);
            Assert.Equal(WebhookOutcomeCodes.AbortedBeforeSend, Assert.Single(await AttemptOutcomesAsync(delivery)));
            // While paused, nothing but tests is acquired; the captured work is held.
            Assert.Empty(await repository.AcquireAsync(
                new WebhookAcquireRequest("pausing-worker", 4, TimeSpan.FromSeconds(15), 100), CancellationToken.None));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task ArchiveFencesActiveLeasesAndDiscardsOutstandingWork()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/never", StandardWebhookSignature.GenerateKey(), active: true);
        var leased = await InsertEventDeliveryAsync(subscription);
        var queued = await InsertEventDeliveryAsync(subscription);
        var lease = (await repository.AcquireAsync(
            new WebhookAcquireRequest("archive-worker", 1, TimeSpan.FromSeconds(15), 100), CancellationToken.None)).Single();
        await repository.PrepareSendAsync(lease, [subscription.KeyId], null, CancellationToken.None);

        await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        // The coordinator is deployment-wide and settles other tests' archives too, oldest first.
        var discarded = 0;
        for (var pass = 0; pass < 50 && await WebhookTestDatabase.CountEventsAsync(fixture.DataSource,
                 "SELECT count(*) FROM flowbit.webhook_subscriptions WHERE \"Id\" = @id AND \"ArchiveSettledAt\" IS NULL",
                 ("id", subscription.Id)) == 1; pass++)
        {
            discarded += await repository.SettleArchivedSubscriptionsAsync(100, TimeSpan.FromSeconds(75), CancellationToken.None);
        }
        Assert.True(discarded >= 2);

        Assert.Equal(WebhookDeliveryStates.Discarded, (await DeliveryAsync(lease.DeliveryId)).State);
        Assert.Equal(WebhookDeliveryStates.Discarded, (await DeliveryAsync(leased == lease.DeliveryId ? queued : leased)).State);
        Assert.Equal(WebhookOutcomeCodes.OutcomeUnknown, Assert.Single(await AttemptOutcomesAsync(lease.DeliveryId)));
        Assert.False((await repository.FinalizeAsync(lease, new WebhookSendOutcome(WebhookOutcomeCodes.Succeeded, 200, null, 1), CancellationToken.None)).Applied);
        Assert.Equal(1, await WebhookTestDatabase.CountEventsAsync(
            fixture.DataSource,
            "SELECT count(*) FROM flowbit.webhook_subscriptions WHERE \"Id\" = @id AND \"ArchiveSettledAt\" IS NOT NULL",
            ("id", subscription.Id)));
        Assert.Equal(1, await WebhookTestDatabase.CountEventsAsync(
            fixture.DataSource,
            "SELECT count(*) FROM flowbit.webhook_signing_keys WHERE \"SubscriptionId\" = @id AND \"Status\" = 'retired' AND \"RemoveSecretNotBefore\" > now()",
            ("id", subscription.Id)));
    }

    [Fact]
    public async Task AcquisitionHonorsPerSubscriptionConcurrencyAcrossWorkersAndRotatesFairly()
    {
        var busy = await CreateSubscriptionAsync("http://127.0.0.1:9/busy", StandardWebhookSignature.GenerateKey(), active: true, maxConcurrency: 2);
        var quiet = await CreateSubscriptionAsync("http://127.0.0.1:9/quiet", StandardWebhookSignature.GenerateKey(), active: true, maxConcurrency: 2);
        try
        {
            for (var index = 0; index < 5; index++)
            {
                await InsertEventDeliveryAsync(busy);
            }
            await InsertEventDeliveryAsync(quiet);

            var first = await repository.AcquireAsync(new WebhookAcquireRequest("worker-a", 1, TimeSpan.FromSeconds(15), 100), CancellationToken.None);
            var second = await repository.AcquireAsync(new WebhookAcquireRequest("worker-b", 1, TimeSpan.FromSeconds(15), 100), CancellationToken.None);
            Assert.NotEqual(Assert.Single(first).SubscriptionId, Assert.Single(second).SubscriptionId);

            var more = await repository.AcquireAsync(new WebhookAcquireRequest("worker-b", 10, TimeSpan.FromSeconds(15), 100), CancellationToken.None);
            Assert.Equal(1, more.Count(lease => lease.SubscriptionId == busy.Id));
            var none = await repository.AcquireAsync(new WebhookAcquireRequest("worker-c", 10, TimeSpan.FromSeconds(15), 100), CancellationToken.None);
            Assert.DoesNotContain(none, lease => lease.SubscriptionId == busy.Id);
            Assert.Equal(2, await WebhookTestDatabase.CountEventsAsync(
                fixture.DataSource,
                "SELECT count(*) FROM flowbit.webhook_deliveries WHERE \"SubscriptionId\" = @id AND \"State\" = 'delivering'",
                ("id", busy.Id)));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, busy.Id, "archived");
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, quiet.Id, "archived");
        }
    }

    [Fact]
    public async Task ReceiverBackoffHoldsTheQueueAndAnExhaustedGenerationFailsWithoutARequest()
    {
        await using var receiver = await WebhookTestReceiver.StartAsync(_ => 500);
        var subscription = await CreateSubscriptionAsync(receiver.Url("down"), StandardWebhookSignature.GenerateKey(), active: true, maxConcurrency: 16);
        try
        {
            for (var index = 0; index < 6; index++)
            {
                await InsertEventDeliveryAsync(subscription);
            }
            for (var round = 0; round < 5; round++)
            {
                await SendOnceAsync();
            }
            var backoff = await WebhookTestDatabase.CountEventsAsync(
                fixture.DataSource,
                "SELECT count(*) FROM flowbit.webhook_subscription_dispatch_state WHERE \"SubscriptionId\" = @id AND \"BackoffUntil\" > now() AND \"ConsecutiveFailures\" >= 5",
                ("id", subscription.Id));
            Assert.Equal(1, backoff);
            await MakeAllDueAsync(subscription.Id);
            Assert.Empty(await repository.AcquireAsync(new WebhookAcquireRequest("backoff", 16, TimeSpan.FromSeconds(15), 100), CancellationToken.None));

            // When backoff ends, exactly one probe is sent at a time.
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
                "UPDATE flowbit.webhook_subscription_dispatch_state SET \"BackoffUntil\" = now() - interval '1 second' WHERE \"SubscriptionId\" = @id",
                ("id", subscription.Id));
            Assert.Single(await repository.AcquireAsync(new WebhookAcquireRequest("probe", 16, TimeSpan.FromSeconds(15), 100), CancellationToken.None));

            var exhausted = await InsertEventDeliveryAsync(subscription);
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
                "UPDATE flowbit.webhook_deliveries SET \"GenerationAttempts\" = 12, \"LifetimeAttempts\" = 12, \"GenerationStartedAt\" = now(), \"DeadlineAt\" = now() + interval '1 hour' WHERE \"Id\" = @id",
                ("id", exhausted));
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
                "UPDATE flowbit.webhook_subscription_dispatch_state SET \"BackoffUntil\" = NULL, \"BackoffStartedAt\" = NULL, \"ConsecutiveFailures\" = 0 WHERE \"SubscriptionId\" = @id",
                ("id", subscription.Id));
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
                "UPDATE flowbit.webhook_deliveries SET \"State\" = 'discarded', \"SettledAt\" = now(), \"LeaseToken\" = NULL, \"LeaseOwner\" = NULL, \"LeaseExpiresAt\" = NULL, \"LeasedFromState\" = NULL WHERE \"SubscriptionId\" = @id AND \"Id\" <> @exhausted",
                ("id", subscription.Id), ("exhausted", exhausted));
            Assert.Empty(await repository.AcquireAsync(new WebhookAcquireRequest("exhausted", 16, TimeSpan.FromSeconds(15), 100), CancellationToken.None));
            var row = await DeliveryAsync(exhausted);
            Assert.Equal(WebhookDeliveryStates.Failed, row.State);
            Assert.Equal(WebhookOutcomeCodes.AttemptsExhausted, row.FailureCode);
            Assert.Empty(await AttemptOutcomesAsync(exhausted));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task FanoutIsIdempotentDiscardsArchivedTargetsAndSurvivesInstanceCancellation()
    {
        var active = await CreateSubscriptionAsync("http://127.0.0.1:9/a", StandardWebhookSignature.GenerateKey(), active: true);
        var archived = await CreateSubscriptionAsync("http://127.0.0.1:9/b", StandardWebhookSignature.GenerateKey(), active: true);
        try
        {
            Guid eventId;
            await using (var connection = await source.DataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("""
                SELECT flowbit.record_lifecycle_event('instance.started', now(), 'workflow_instances', 1, 'fanout',
                    NULL, 424242, NULL, '{"status":"running"}', '{}'::text[], ARRAY[@a, @b]::bigint[])
                """, connection))
            {
                command.Parameters.AddWithValue("a", active.RevisionId);
                command.Parameters.AddWithValue("b", archived.RevisionId);
                eventId = (Guid)(await command.ExecuteScalarAsync())!;
            }
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, archived.Id, "archived");
            var result = await repository.FanoutAsync(1000, CancellationToken.None);
            Assert.True(result.Events >= 1);
            Assert.Equal(
                [("archived", "discarded"), ("active", "pending")],
                await FanoutStatesAsync(eventId, active.Id, archived.Id));
            // Repeating fanout of the same event cannot create a second delivery.
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
                "UPDATE flowbit.lifecycle_events SET \"FanoutCompletedAt\" = NULL WHERE \"Id\" = @id", ("id", eventId));
            await repository.FanoutAsync(1000, CancellationToken.None);
            Assert.Equal(2, await WebhookTestDatabase.CountEventsAsync(
                fixture.DataSource,
                "SELECT count(*) FROM flowbit.webhook_deliveries WHERE \"EventId\" = @id",
                ("id", eventId)));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, active.Id, "archived");
        }
    }

    [Fact]
    public async Task TransportRefusesDeniedAddressesSlowReceiversAndOversizedHeaders()
    {
        await using var receiver = await WebhookTestReceiver.StartAsync(context =>
        {
            if (context.Request.Path.StartsWithSegments("/headers"))
            {
                context.Response.Headers["X-Large"] = new string('a', 32 * 1024);
            }
            return 200;
        });
        var denying = new WebhookHttpTransport(
            new WebhookOptions(),
            new WebhookDestinationPolicy(new WebhookNetworkOptions()),
            TimeProvider.System);
        var key = StandardWebhookSignature.GenerateKey();
        // A loopback name passes no URL check here; the connection-time check still denies it.
        var denied = await denying.SendAsync(Request(new Uri($"http://localhost:{receiver.Port}/hook"), key), CancellationToken.None);
        Assert.Equal(WebhookOutcomeCodes.DestinationDenied, denied.Outcome);
        Assert.Empty(receiver.Requests);

        var transport = new WebhookHttpTransport(options, new WebhookDestinationPolicy(options.Network), TimeProvider.System);
        var large = await transport.SendAsync(Request(new Uri(receiver.Url("headers")), key), CancellationToken.None);
        Assert.Equal(WebhookOutcomeCodes.ResponseHeadersTooLarge, large.Outcome);

        await using var slow = await WebhookTestReceiver.StartAsync(_ => 200, delay: TimeSpan.FromSeconds(5));
        var timedOut = await transport.SendAsync(Request(new Uri(slow.Url("slow")), key, TimeSpan.FromMilliseconds(500)), CancellationToken.None);
        Assert.Equal(WebhookOutcomeCodes.Timeout, timedOut.Outcome);
        Assert.Null(timedOut.HttpStatus);
        denying.Dispose();
        transport.Dispose();
    }

    [Fact]
    public async Task RetentionDeletesSettledWorkAndProtectsOutstandingWorkWithoutTouchingRuntimeHistory()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/r", StandardWebhookSignature.GenerateKey(), active: true);
        var settled = await InsertEventDeliveryAsync(subscription);
        var failed = await InsertEventDeliveryAsync(subscription);
        try
        {
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
                UPDATE flowbit.webhook_deliveries SET "State" = 'succeeded', "SettledAt" = now() - interval '10 days'
                WHERE "Id" = @id
                """, ("id", settled));
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
                UPDATE flowbit.webhook_deliveries SET "State" = 'failed', "FailureCode" = 'receiver_rejected'
                WHERE "Id" = @id
                """, ("id", failed));
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
                UPDATE flowbit.lifecycle_events SET "OccurredAt" = now() - interval '10 days'
                WHERE "Id" IN (SELECT "EventId" FROM flowbit.webhook_deliveries WHERE "Id" = ANY(@ids));
                UPDATE flowbit.webhook_deliveries SET "OccurredAt" = now() - interval '10 days' WHERE "Id" = ANY(@ids);
                """, ("ids", new[] { settled, failed }));
            var retention = new RetentionRepository(new RetentionDataSource(fixture.ConnectionString), TimeProvider.System);

            var deliveriesPreview = await retention.PreviewAsync(
                new PreviewRetentionRequest(RetentionCategories.WebhookDeliveries, 1), CancellationToken.None);
            var deliveriesTable = Assert.Single(deliveriesPreview.Tables, table => table.Table == "webhook_deliveries");
            Assert.True(deliveriesTable.EligibleCount >= 1);
            Assert.True(deliveriesTable.ProtectedCount >= 1);

            await SetRetentionAsync(RetentionCategories.WebhookDeliveries, 1);
            await SetRetentionAsync(RetentionCategories.LifecycleEvents, 1);
            var prunedBefore = await WebhookTestDatabase.CountEventsAsync(
                fixture.DataSource, "SELECT count(*) FROM flowbit.workflow_instances WHERE \"HistoryPrunedAt\" IS NOT NULL");
            // A run already in progress keeps the policies it started with, so finish it and run once more.
            for (var run = 0; run < 2; run++)
            {
                await retention.RequestRunAsync("tests", CancellationToken.None);
                for (var tick = 0; tick < 400; tick++)
                {
                    var result = await retention.ProcessBatchAsync(new RetentionExecutionOptions
                    {
                        WorkerId = "retention-test", BatchSize = 100, MaxRunnableJobs = 1_000_000, MaxQueueLagSeconds = 3600
                    }, CancellationToken.None);
                    if (!result.HasActiveRun) break;
                }
            }
            Assert.Equal(0, await WebhookTestDatabase.CountEventsAsync(
                fixture.DataSource, "SELECT count(*) FROM flowbit.webhook_deliveries WHERE \"Id\" = @id", ("id", settled)));
            Assert.Equal(1, await WebhookTestDatabase.CountEventsAsync(
                fixture.DataSource, "SELECT count(*) FROM flowbit.webhook_deliveries WHERE \"Id\" = @id", ("id", failed)));
            // The failed delivery's event stays; the settled delivery's event goes after its delivery.
            Assert.Equal(1, await WebhookTestDatabase.CountEventsAsync(fixture.DataSource, """
                SELECT count(*) FROM flowbit.lifecycle_events e
                WHERE e."Id" IN (SELECT "EventId" FROM flowbit.webhook_deliveries WHERE "Id" = @id)
                """, ("id", failed)));
            Assert.Equal(prunedBefore, await WebhookTestDatabase.CountEventsAsync(
                fixture.DataSource, "SELECT count(*) FROM flowbit.workflow_instances WHERE \"HistoryPrunedAt\" IS NOT NULL"));
        }
        finally
        {
            await SetRetentionAsync(RetentionCategories.WebhookDeliveries, null);
            await SetRetentionAsync(RetentionCategories.LifecycleEvents, null);
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task HeldWorkIsNotAcquiredAndTheDispatcherWaitsForTheBackoffToEnd()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/held", StandardWebhookSignature.GenerateKey(), active: true);
        try
        {
            for (var index = 0; index < 3; index++)
            {
                await InsertEventDeliveryAsync(subscription);
            }
            Assert.True(await repository.GetTimeUntilNextDueAsync(CancellationToken.None) <= TimeSpan.Zero);
            await SetBackoffAsync(subscription.Id, 5, "now()", "now() + interval '2 minutes'", 120);

            // Only the backoff end can need a dispatcher now, not the held deliveries that are already due.
            Assert.InRange(
                (await repository.GetTimeUntilNextDueAsync(CancellationToken.None))!.Value,
                TimeSpan.FromSeconds(100),
                TimeSpan.FromSeconds(121));
            Assert.Empty(await AcquireOwnAsync(subscription.Id, 16));

            // A test still goes out while the subscription backs off, and while it is paused.
            await QueueTestDeliveryAsync(subscription.Id);
            Assert.True(await repository.GetTimeUntilNextDueAsync(CancellationToken.None) <= TimeSpan.Zero);
            var test = Assert.Single(await AcquireOwnAsync(subscription.Id, 16));
            Assert.True(test.IsTest);
            Assert.True(await repository.ReleaseBeforeSendAsync(test, CancellationToken.None));
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "paused");
            Assert.True(Assert.Single(await AcquireOwnAsync(subscription.Id, 16)).IsTest);
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task WorkersAcquiringAtTheSameTimeNeverExceedASubscriptionsConcurrency()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/race", StandardWebhookSignature.GenerateKey(), active: true, maxConcurrency: 2);
        try
        {
            for (var index = 0; index < 6; index++)
            {
                await InsertEventDeliveryAsync(subscription);
            }
            for (var round = 0; round < 3; round++)
            {
                var acquisitions = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => repository.AcquireAsync(
                    new WebhookAcquireRequest($"racing-{round}-{index}", 4, TimeSpan.FromSeconds(15), 100), CancellationToken.None)));
                var leases = acquisitions.SelectMany(acquired => acquired).ToArray();
                var own = leases.Where(lease => lease.SubscriptionId == subscription.Id).ToArray();
                Assert.Equal(2, own.Length);
                Assert.Equal(2, own.Select(lease => lease.DeliveryId).Distinct().Count());
                foreach (var lease in leases)
                {
                    Assert.True(await repository.ReleaseBeforeSendAsync(lease, CancellationToken.None));
                }
            }
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task FanoutWaitsForAnArchiveInProgressAndRecordsItsDeliveryDiscarded()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/archiving", StandardWebhookSignature.GenerateKey(), active: true);
        try
        {
            Guid eventId;
            await using (var connection = await source.DataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("""
                SELECT flowbit.record_lifecycle_event('instance.started', now(), 'workflow_instances', 1, @key,
                    NULL, 434343, NULL, '{"status":"running"}', '{}'::text[], ARRAY[@revision]::bigint[])
                """, connection))
            {
                command.Parameters.AddWithValue("key", subscription.WorkflowKey);
                command.Parameters.AddWithValue("revision", subscription.RevisionId);
                eventId = (Guid)(await command.ExecuteScalarAsync())!;
            }
            // An administrator's archive holds the subscription row while fanout reads it.
            await using var archiving = await fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await archiving.BeginTransactionAsync();
            await using (var archive = new NpgsqlCommand("""
                SELECT 1 FROM flowbit.webhook_subscriptions WHERE "Id" = @id FOR UPDATE;
                UPDATE flowbit.webhook_subscriptions
                   SET "State" = 'archived', "PauseReason" = NULL, "ArchivedAt" = now(), "ArchivedBy" = 'tests',
                       "Revision" = "Revision" + 1
                 WHERE "Id" = @id;
                """, archiving, transaction))
            {
                archive.Parameters.AddWithValue("id", subscription.Id);
                await archive.ExecuteNonQueryAsync();
            }
            var fanout = repository.FanoutAsync(1000, CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.False(fanout.IsCompleted);
            await transaction.CommitAsync();
            await fanout;

            // The coordinator never revisits a settled archive, so a delivery left pending would wait forever.
            Assert.Equal(1, await WebhookTestDatabase.CountEventsAsync(fixture.DataSource, """
                SELECT count(*) FROM flowbit.webhook_deliveries
                WHERE "EventId" = @event AND "State" = 'discarded' AND "SettledAt" IS NOT NULL
                """, ("event", eventId)));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task PrepareSendSignsOnlyWithKeysThatStillSignAndReleasesTheLeaseAfterARotation()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/keys", StandardWebhookSignature.GenerateKey(), active: true);
        var previous = await AddSigningKeyAsync(subscription.Id, "previous", "now() + interval '1 hour'");
        try
        {
            var revoked = await InsertEventDeliveryAsync(subscription);
            var lease = Assert.Single(await AcquireOwnAsync(subscription.Id, 1));
            var loaded = await repository.LoadSigningKeysAsync(subscription.Id, CancellationToken.None);
            Assert.Equal([subscription.KeyId, previous], loaded.Select(key => key.KeyId));
            // An emergency revocation lands between loading the keys and recording the send intent.
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
                UPDATE flowbit.webhook_signing_keys
                   SET "Status" = 'revoked', "RevokedAt" = now(), "RevokedBy" = 'tests', "RetiredAt" = now(),
                       "RemoveSecretNotBefore" = now() + interval '1 minute'
                 WHERE "Id" = @id
                """, ("id", previous));
            var decision = await repository.PrepareSendAsync(lease, loaded.Select(key => key.KeyId).ToArray(), null, CancellationToken.None);
            Assert.Equal(WebhookSendDecisionKind.Send, decision.Kind);
            Assert.Equal([subscription.KeyId], decision.SigningKeyIds);
            Assert.Equal([subscription.KeyId], await AttemptKeysAsync(revoked));
            Assert.True((await repository.FinalizeAsync(lease, new WebhookSendOutcome(WebhookOutcomeCodes.Succeeded, 204, null, 3), CancellationToken.None)).Applied);

            // A rotation without overlap retires the current key before the send intent.
            var rotated = await InsertEventDeliveryAsync(subscription);
            var next = Assert.Single(await AcquireOwnAsync(subscription.Id, 1));
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
                UPDATE flowbit.webhook_signing_keys
                   SET "Status" = 'expired', "RetiredAt" = now(), "RemoveSecretNotBefore" = now() + interval '1 minute'
                 WHERE "Id" = @id
                """, ("id", subscription.KeyId));
            await AddSigningKeyAsync(subscription.Id, "current", null);
            var aborted = await repository.PrepareSendAsync(next, [subscription.KeyId], null, CancellationToken.None);
            Assert.Equal(WebhookSendDecisionKind.Aborted, aborted.Kind);
            var row = await DeliveryAsync(rotated);
            Assert.Equal(WebhookDeliveryStates.Pending, row.State);
            Assert.Equal(0, row.GenerationAttempts);
            Assert.Equal(WebhookOutcomeCodes.AbortedBeforeSend, Assert.Single(await AttemptOutcomesAsync(rotated)));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task OnlyAFailedProbeDoublesTheBackoffAndASuccessEndsIt()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/probe", StandardWebhookSignature.GenerateKey(), active: true);
        try
        {
            var delivery = await InsertEventDeliveryAsync(subscription);
            await SetBackoffAsync(subscription.Id, 5, "now() - interval '1 minute'", "now() - interval '1 second'", 30);

            // This request was already in flight when backoff began, so its failure does not extend it.
            var inFlight = Assert.Single(await AcquireOwnAsync(subscription.Id, 16));
            Assert.Equal(WebhookSendDecisionKind.Send,
                (await repository.PrepareSendAsync(inFlight, [subscription.KeyId], null, CancellationToken.None)).Kind);
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
                UPDATE flowbit.webhook_delivery_attempts SET "SendIntentAt" = now() - interval '2 minutes'
                WHERE "DeliveryId" = @id AND "CompletedAt" IS NULL
                """, ("id", delivery));
            await repository.FinalizeAsync(inFlight, new WebhookSendOutcome(WebhookOutcomeCodes.Timeout, null, null, 15000), CancellationToken.None);
            var held = await DispatchStateAsync(subscription.Id);
            Assert.Equal(6, held.Failures);
            Assert.Equal(30, held.Seconds);
            Assert.True(held.UntilSeconds < 5);

            // The probe sent after backoff began doubles it.
            await MakeDueAsync(delivery);
            var probe = Assert.Single(await AcquireOwnAsync(subscription.Id, 16));
            await repository.PrepareSendAsync(probe, [subscription.KeyId], null, CancellationToken.None);
            await repository.FinalizeAsync(probe, new WebhookSendOutcome(WebhookOutcomeCodes.HttpRetryable, 503, null, 20), CancellationToken.None);
            var doubled = await DispatchStateAsync(subscription.Id);
            Assert.Equal(60, doubled.Seconds);
            Assert.True(doubled.UntilSeconds > 40);

            // A success ends backoff and its failure count.
            await SetBackoffAsync(subscription.Id, 7, "now() - interval '2 minutes'", "now() - interval '1 second'", 60);
            await MakeDueAsync(delivery);
            var recovered = Assert.Single(await AcquireOwnAsync(subscription.Id, 16));
            await repository.PrepareSendAsync(recovered, [subscription.KeyId], null, CancellationToken.None);
            await repository.FinalizeAsync(recovered, new WebhookSendOutcome(WebhookOutcomeCodes.Succeeded, 200, null, 20), CancellationToken.None);
            var healthy = await DispatchStateAsync(subscription.Id);
            Assert.Equal(0, healthy.Failures);
            Assert.Null(healthy.StartedAt);
            Assert.Null(healthy.UntilSeconds);
            Assert.NotNull(healthy.LastSuccessAt);
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task SeventyTwoHoursOfFailingProbesPauseTheSubscriptionAsSystem()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/unavailable", StandardWebhookSignature.GenerateKey(), active: true);
        try
        {
            await InsertEventDeliveryAsync(subscription);
            await SetBackoffAsync(subscription.Id, 80, "now() - interval '73 hours'", "now() - interval '5 seconds'", 3600);
            var lease = Assert.Single(await AcquireOwnAsync(subscription.Id, 16));
            await repository.PrepareSendAsync(lease, [subscription.KeyId], null, CancellationToken.None);
            var result = await repository.FinalizeAsync(lease, new WebhookSendOutcome(WebhookOutcomeCodes.Timeout, null, null, 15000), CancellationToken.None);
            Assert.True(result.AutomaticallyPaused);
            Assert.Equal((WebhookSubscriptionStates.Paused, WebhookPauseReasons.ReceiverUnavailable), await SubscriptionStateAsync(subscription.Id));
            Assert.Equal(1, await WebhookTestDatabase.CountEventsAsync(fixture.DataSource, """
                SELECT count(*) FROM flowbit.webhook_management_audit
                WHERE "SubscriptionId" = @id AND "Operation" = 'automaticPause' AND "Actor" = 'system'
                """, ("id", subscription.Id)));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task SigningKeysSignDuringTheirOverlapAndTheirSecretsAreRemovedAfterTheGrace()
    {
        await using var receiver = await WebhookTestReceiver.StartAsync();
        var currentKey = StandardWebhookSignature.GenerateKey();
        var previousKey = StandardWebhookSignature.GenerateKey();
        var subscription = await CreateSubscriptionAsync(receiver.Url("overlap"), currentKey);
        var previous = await AddSigningKeyAsync(subscription.Id, "previous", "now() + interval '1 hour'", previousKey);
        try
        {
            // During the overlap both keys sign, current first.
            await QueueTestDeliveryAsync(subscription.Id);
            Assert.Equal(WebhookOutcomeCodes.Succeeded, (await SendOnceAsync()).Outcome);
            var request = Assert.Single(await receiver.WaitForAsync(1));
            Assert.Equal(2, request.Signature.Split(' ').Length);
            Assert.True(WebhookSigningTests.IndependentVerifier.Verify(
                StandardWebhookSignature.FormatSecret(currentKey), request.Id, request.Timestamp, request.Body, request.Signature));
            Assert.True(WebhookSigningTests.IndependentVerifier.Verify(
                StandardWebhookSignature.FormatSecret(previousKey), request.Id, request.Timestamp, request.Body, request.Signature));

            // Once the overlap ends, the sweep expires the previous key and keeps its secret through the grace.
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
                "UPDATE flowbit.webhook_signing_keys SET \"OverlapExpiresAt\" = now() - interval '1 second' WHERE \"Id\" = @id",
                ("id", previous));
            Assert.Equal([subscription.KeyId], (await repository.LoadSigningKeysAsync(subscription.Id, CancellationToken.None)).Select(key => key.KeyId));
            await repository.SweepSigningKeysAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.Equal(1, await WebhookTestDatabase.CountEventsAsync(fixture.DataSource, """
                SELECT count(*) FROM flowbit.webhook_signing_keys
                WHERE "Id" = @id AND "Status" = 'expired' AND "ProtectedSecret" IS NOT NULL AND "RemoveSecretNotBefore" > now()
                """, ("id", previous)));
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
                "UPDATE flowbit.webhook_signing_keys SET \"RemoveSecretNotBefore\" = now() - interval '1 second' WHERE \"Id\" = @id",
                ("id", previous));
            await repository.SweepSigningKeysAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.Equal(1, await WebhookTestDatabase.CountEventsAsync(fixture.DataSource, """
                SELECT count(*) FROM flowbit.webhook_signing_keys
                WHERE "Id" = @id AND "ProtectedSecret" IS NULL AND "SecretRemovedAt" IS NOT NULL
                """, ("id", previous)));
            // The current key keeps its secret.
            Assert.Single(await repository.LoadSigningKeysAsync(subscription.Id, CancellationToken.None));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task ReleasingBeforeTheSendIntentReturnsTheDeliveryWithoutSpendingBudget()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/release", StandardWebhookSignature.GenerateKey(), active: true);
        try
        {
            var delivery = await InsertEventDeliveryAsync(subscription);
            var lease = Assert.Single(await AcquireOwnAsync(subscription.Id, 1));
            Assert.True(await repository.ReleaseBeforeSendAsync(lease, CancellationToken.None));
            var row = await DeliveryAsync(delivery);
            Assert.Equal(WebhookDeliveryStates.Pending, row.State);
            Assert.Equal(0, row.GenerationAttempts);
            Assert.Null(row.DeadlineAt);
            Assert.Equal(WebhookOutcomeCodes.AbortedBeforeSend, Assert.Single(await AttemptOutcomesAsync(delivery)));
            // The released lease no longer belongs to anyone.
            Assert.False(await repository.ReleaseBeforeSendAsync(lease, CancellationToken.None));
            Assert.False(await repository.HeartbeatAsync(lease, TimeSpan.FromSeconds(15), CancellationToken.None));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task ARequestTheReceiverAcceptedIsSentAgainWithTheSameIdentityWhenItsWorkerDies()
    {
        await using var receiver = await WebhookTestReceiver.StartAsync();
        var key = StandardWebhookSignature.GenerateKey();
        var subscription = await CreateSubscriptionAsync(receiver.Url("crash"), key);
        var delivery = await QueueTestDeliveryAsync(subscription.Id);
        try
        {
            // The receiver accepts the request, but the Worker dies before it records the outcome.
            var lease = Assert.Single(await AcquireOwnAsync(subscription.Id, 1));
            Assert.Equal(WebhookSendDecisionKind.Send,
                (await repository.PrepareSendAsync(lease, [subscription.KeyId], null, CancellationToken.None)).Kind);
            using (var transport = new WebhookHttpTransport(options, new WebhookDestinationPolicy(options.Network), TimeProvider.System))
            {
                var accepted = await transport.SendAsync(
                    new WebhookSendRequest(new Uri(lease.EndpointUrl), lease.EventId, lease.Body, [key], options.HttpTimeout),
                    CancellationToken.None);
                Assert.Equal(WebhookOutcomeCodes.Succeeded, accepted.Outcome);
            }
            await ExpireLeaseAsync(delivery);
            Assert.True(await repository.RecoverExpiredLeasesAsync(100, CancellationToken.None) >= 1);
            Assert.Equal(WebhookOutcomeCodes.OutcomeUnknown, (await DeliveryAsync(delivery)).LastOutcome);

            // Recovery sends the same event again, so the receiver deduplicates by webhook-id.
            await MakeDueAsync(delivery);
            Assert.Equal(WebhookOutcomeCodes.Succeeded, (await SendOnceAsync()).Outcome);
            var received = await receiver.WaitForAsync(2);
            Assert.Equal(2, received.Length);
            Assert.Equal(received[0].Id, received[1].Id);
            Assert.Equal(received[0].Body, received[1].Body);
            var row = await DeliveryAsync(delivery);
            Assert.Equal(WebhookDeliveryStates.Succeeded, row.State);
            Assert.Equal(2, row.LifetimeAttempts);
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task LosingTheLeaseCancelsTheSendInFlightWithoutRecordingAnOutcome()
    {
        await using var receiver = await WebhookTestReceiver.StartAsync(_ => 204, delay: TimeSpan.FromSeconds(8));
        var subscription = await CreateSubscriptionAsync(receiver.Url("stolen"), StandardWebhookSignature.GenerateKey());
        var delivery = await QueueTestDeliveryAsync(subscription.Id);
        using var host = BuildWorkerHost(SlowReceiverOptions(heartbeatSeconds: 1));
        await host.StartAsync();
        try
        {
            await receiver.WaitForAsync(1);
            // Another Worker takes the delivery over while this one waits for the receiver.
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
                UPDATE flowbit.webhook_deliveries
                   SET "LeaseToken" = gen_random_uuid(), "LeaseGeneration" = "LeaseGeneration" + 1
                 WHERE "Id" = @id
                """, ("id", delivery));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
            while (receiver.Aborted == 0 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
            Assert.Equal(1, receiver.Aborted);
            // The new owner settles the attempt; this Worker wrote nothing.
            Assert.Equal(WebhookDeliveryStates.Delivering, (await DeliveryAsync(delivery)).State);
            Assert.Equal(["open"], await AttemptOutcomesAsync(delivery));
        }
        finally
        {
            await host.StopAsync();
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task AHardStopEndsAnUnfinishedSendWithAnUnknownOutcome()
    {
        await using var receiver = await WebhookTestReceiver.StartAsync(_ => 204, delay: TimeSpan.FromSeconds(8));
        var subscription = await CreateSubscriptionAsync(receiver.Url("shutdown"), StandardWebhookSignature.GenerateKey());
        var delivery = await QueueTestDeliveryAsync(subscription.Id);
        using var host = BuildWorkerHost(SlowReceiverOptions(heartbeatSeconds: 5), new WorkerOptions { ShutdownDrainSeconds = 1 });
        await host.StartAsync();
        try
        {
            await receiver.WaitForAsync(1);
            await host.StopAsync();
            var row = await DeliveryAsync(delivery);
            Assert.Equal(WebhookDeliveryStates.RetryScheduled, row.State);
            Assert.Equal(WebhookOutcomeCodes.OutcomeUnknown, row.LastOutcome);
            Assert.Equal(1, row.GenerationAttempts);
            Assert.Equal([WebhookOutcomeCodes.OutcomeUnknown], await AttemptOutcomesAsync(delivery));
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task NoWebhookTransactionStaysOpenWhileAReceiverIsSlow()
    {
        await using var receiver = await WebhookTestReceiver.StartAsync(_ => 204, delay: TimeSpan.FromSeconds(4));
        var subscription = await CreateSubscriptionAsync(receiver.Url("slow"), StandardWebhookSignature.GenerateKey());
        await QueueTestDeliveryAsync(subscription.Id);
        using var host = BuildWorkerHost(SlowReceiverOptions(heartbeatSeconds: 5));
        await host.StartAsync();
        try
        {
            await receiver.WaitForAsync(1);
            // A transaction held across the request would be older than a second in every sample.
            for (var sample = 0; sample < 5; sample++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400));
                await using var connection = await fixture.DataSource.OpenConnectionAsync();
                await using var command = new NpgsqlCommand("""
                    SELECT COALESCE(max(EXTRACT(EPOCH FROM now() - xact_start)), 0)::double precision
                    FROM pg_stat_activity
                    WHERE application_name = 'Flowbit.Webhooks' AND xact_start IS NOT NULL AND pid <> pg_backend_pid()
                    """, connection);
                Assert.True((double)(await command.ExecuteScalarAsync())! < 1.0);
            }
            await WaitForDeliveryAsync(subscription.Id, WebhookDeliveryStates.Succeeded);
        }
        finally
        {
            await host.StopAsync();
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task WorkerLogsNeverContainTheDestinationPathOrQuery()
    {
        await using var receiver = await WebhookTestReceiver.StartAsync(_ => 410);
        var subscription = await CreateSubscriptionAsync(
            receiver.Url("private-receiver-path?token=PrivateReceiverToken"), StandardWebhookSignature.GenerateKey(), active: true);
        var logs = new CapturingLoggerProvider();
        await WebhookTestDatabase.SetCaptureAsync(fixture.DataSource, true);
        await InsertInstanceAsync(subscription.WorkflowKey);
        using var host = BuildWorkerHost(logs: logs);
        await host.StartAsync();
        try
        {
            await WaitForDeliveryAsync(subscription.Id, WebhookDeliveryStates.Failed);
            Assert.Equal((WebhookSubscriptionStates.Paused, WebhookPauseReasons.ReceiverGone), await SubscriptionStateAsync(subscription.Id));
        }
        finally
        {
            await host.StopAsync();
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
        var entries = logs.Entries.ToArray();
        Assert.Contains(entries, entry => entry.Contains(subscription.Id.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Contains("PrivateReceiverToken", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Contains("private-receiver-path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UndecryptableKeysFailTheDeliveryWhileUnavailableProtectionReleasesIt()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/unprotect", StandardWebhookSignature.GenerateKey());
        var released = await QueueTestDeliveryAsync(subscription.Id);
        var unavailable = new FailingSecretProtector(protector, new WebhookKeyProtectionUnavailableException("The key ring could not be read."));
        try
        {
            // Protection that cannot read its key ring for now returns the delivery unsent and unspent.
            using (var host = BuildWorkerHost(secretProtector: unavailable))
            {
                await host.StartAsync();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (unavailable.Calls == 0 && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(100);
                }
                // Unavailable protection then stops acquisition instead of spinning on the released delivery.
                await Task.Delay(TimeSpan.FromSeconds(2));
                await host.StopAsync();
            }
            Assert.Equal(1, unavailable.Calls);
            var row = await DeliveryAsync(released);
            Assert.Equal(WebhookDeliveryStates.Pending, row.State);
            Assert.Equal(0, row.GenerationAttempts);
            Assert.Equal([WebhookOutcomeCodes.AbortedBeforeSend], await AttemptOutcomesAsync(released));

            // A key that even a freshly loaded ring cannot decrypt fails the delivery until a rotation fixes it.
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
                "UPDATE flowbit.webhook_signing_keys SET \"ProtectedSecret\" = @bytes WHERE \"Id\" = @id",
                ("bytes", RandomNumberGenerator.GetBytes(64)), ("id", subscription.KeyId));
            using (var host = BuildWorkerHost())
            {
                await host.StartAsync();
                try
                {
                    var failed = await WaitForDeliveryAsync(subscription.Id, WebhookDeliveryStates.Failed);
                    Assert.Equal(WebhookOutcomeCodes.SigningUnavailable, failed.FailureCode);
                    Assert.Equal(0, failed.GenerationAttempts);
                }
                finally
                {
                    await host.StopAsync();
                }
            }
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    [Fact]
    public async Task RetentionKeepsOutstandingAndUnfannedWorkAndDeletesSettledDeliveriesWithTheirAttempts()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/retain", StandardWebhookSignature.GenerateKey(), active: true);
        var held = await CreateSubscriptionAsync("http://127.0.0.1:9/retain-paused", StandardWebhookSignature.GenerateKey());
        var succeeded = await InsertEventDeliveryAsync(subscription);
        var discarded = await InsertEventDeliveryAsync(subscription);
        var pending = await InsertEventDeliveryAsync(subscription);
        var scheduled = await InsertEventDeliveryAsync(subscription);
        var failed = await InsertEventDeliveryAsync(subscription);
        var delivering = await InsertEventDeliveryAsync(subscription);
        var paused = await InsertEventDeliveryAsync(held);
        try
        {
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
                UPDATE flowbit.webhook_deliveries SET "State" = 'succeeded', "SettledAt" = now() - interval '10 days' WHERE "Id" = @succeeded;
                UPDATE flowbit.webhook_deliveries SET "State" = 'discarded', "SettledAt" = now() - interval '10 days' WHERE "Id" = @discarded;
                UPDATE flowbit.webhook_deliveries SET "State" = 'retryScheduled', "DueAt" = now() + interval '1 hour' WHERE "Id" = @scheduled;
                UPDATE flowbit.webhook_deliveries SET "State" = 'failed', "FailureCode" = 'receiver_rejected' WHERE "Id" = @failed;
                UPDATE flowbit.webhook_deliveries
                   SET "State" = 'delivering', "LeaseOwner" = 'retention-test', "LeaseToken" = gen_random_uuid(),
                       "LeaseGeneration" = 1, "LeaseExpiresAt" = now() + interval '1 hour', "LeasedFromState" = 'pending'
                 WHERE "Id" = @delivering;
                INSERT INTO flowbit.webhook_delivery_attempts
                    ("DeliveryId", "AttemptNumber", "RetryGeneration", "LeaseGeneration", "AcquiredAt", "SendIntentAt", "CompletedAt", "Outcome", "HttpStatus")
                VALUES (@succeeded, 1, 1, 1, now() - interval '11 days', now() - interval '11 days', now() - interval '11 days', 'httpRetryable', 503),
                       (@succeeded, 2, 1, 2, now() - interval '10 days', now() - interval '10 days', now() - interval '10 days', 'succeeded', 204);
                UPDATE flowbit.lifecycle_events SET "OccurredAt" = now() - interval '10 days', "FanoutCompletedAt" = now()
                 WHERE "Id" IN (SELECT "EventId" FROM flowbit.webhook_deliveries WHERE "SubscriptionId" IN (@subscription, @held));
                UPDATE flowbit.webhook_deliveries SET "OccurredAt" = now() - interval '10 days' WHERE "SubscriptionId" IN (@subscription, @held);
                """,
                ("succeeded", succeeded), ("discarded", discarded), ("scheduled", scheduled), ("failed", failed),
                ("delivering", delivering), ("subscription", subscription.Id), ("held", held.Id));
            var unfanned = await InsertOldEventAsync(subscription, fannedOut: false);
            var orphan = await InsertOldEventAsync(subscription, fannedOut: true);
            var succeededEvent = (await DeliveryAsync(succeeded)).EventId;
            var failedEvent = (await DeliveryAsync(failed)).EventId;

            await RunRetentionAsync();

            foreach (var gone in new[] { succeeded, discarded })
            {
                Assert.Equal(0, await WebhookTestDatabase.CountEventsAsync(fixture.DataSource,
                    "SELECT count(*) FROM flowbit.webhook_deliveries WHERE \"Id\" = @id", ("id", gone)));
            }
            Assert.Equal(0, await WebhookTestDatabase.CountEventsAsync(fixture.DataSource,
                "SELECT count(*) FROM flowbit.webhook_delivery_attempts WHERE \"DeliveryId\" = @id", ("id", succeeded)));
            Assert.Equal(5, await WebhookTestDatabase.CountEventsAsync(fixture.DataSource,
                "SELECT count(*) FROM flowbit.webhook_deliveries WHERE \"Id\" = ANY(@ids)",
                ("ids", new[] { pending, scheduled, failed, delivering, paused })));
            // An event goes once fanout completed and no delivery references it; the others stay.
            Assert.Equal(0, await CountEventAsync(succeededEvent));
            Assert.Equal(0, await CountEventAsync(orphan));
            Assert.Equal(1, await CountEventAsync(unfanned));
            Assert.Equal(1, await CountEventAsync(failedEvent));
        }
        finally
        {
            await SetRetentionAsync(RetentionCategories.WebhookDeliveries, null);
            await SetRetentionAsync(RetentionCategories.LifecycleEvents, null);
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
                UPDATE flowbit.webhook_deliveries
                   SET "State" = 'pending', "LeaseOwner" = NULL, "LeaseToken" = NULL, "LeaseExpiresAt" = NULL, "LeasedFromState" = NULL
                 WHERE "Id" = @id AND "State" = 'delivering'
                """, ("id", delivering));
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, held.Id, "archived");
        }
    }

    [Fact]
    public async Task RetentionSkipsARowAnotherTransactionHoldsAndDeletesItOnALaterRun()
    {
        var subscription = await CreateSubscriptionAsync("http://127.0.0.1:9/busy-row", StandardWebhookSignature.GenerateKey(), active: true);
        var settled = await InsertEventDeliveryAsync(subscription);
        try
        {
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
                UPDATE flowbit.webhook_deliveries
                   SET "State" = 'succeeded', "SettledAt" = now() - interval '10 days', "OccurredAt" = now() - interval '10 days'
                 WHERE "Id" = @id
                """, ("id", settled));
            await using (var busy = await fixture.DataSource.OpenConnectionAsync())
            await using (var transaction = await busy.BeginTransactionAsync())
            {
                await using (var hold = new NpgsqlCommand(
                    "SELECT 1 FROM flowbit.webhook_deliveries WHERE \"Id\" = @id FOR UPDATE", busy, transaction))
                {
                    hold.Parameters.AddWithValue("id", settled);
                    await hold.ExecuteScalarAsync();
                }
                // Cleanup skips the held row rather than waiting on it.
                await RunRetentionAsync(runs: 1);
                Assert.Equal(1, await WebhookTestDatabase.CountEventsAsync(fixture.DataSource,
                    "SELECT count(*) FROM flowbit.webhook_deliveries WHERE \"Id\" = @id", ("id", settled)));
                await transaction.CommitAsync();
            }
            await RunRetentionAsync(runs: 1);
            Assert.Equal(0, await WebhookTestDatabase.CountEventsAsync(fixture.DataSource,
                "SELECT count(*) FROM flowbit.webhook_deliveries WHERE \"Id\" = @id", ("id", settled)));
        }
        finally
        {
            await SetRetentionAsync(RetentionCategories.WebhookDeliveries, null);
            await SetRetentionAsync(RetentionCategories.LifecycleEvents, null);
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscription.Id, "archived");
        }
    }

    private IHost BuildWorkerHost(
        WebhookOptions? webhookOptions = null,
        WorkerOptions? workerOptions = null,
        IWebhookSecretProtector? secretProtector = null,
        ILoggerProvider? logs = null)
    {
        var effective = webhookOptions ?? options;
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
        }
        builder.Services.AddSingleton(effective);
        builder.Services.AddSingleton(workerOptions ?? new WorkerOptions());
        builder.Services.AddSingleton(source);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IWebhookSecretProtector>(secretProtector ?? protector);
        builder.Services.AddSingleton(new WebhookDestinationPolicy(effective.Network));
        builder.Services.AddSingleton<IWebhookTransport, WebhookHttpTransport>();
        builder.Services.AddScoped<IWebhookDispatchRepository, WebhookDispatchRepository>();
        builder.Services.AddSingleton<WebhookTelemetry>();
        builder.Services.AddSingleton<PostgresLifecycleWakeupSignal>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<PostgresLifecycleWakeupSignal>());
        builder.Services.AddHostedService<LifecycleEventFanoutService>();
        builder.Services.AddHostedService<WebhookDispatcher>();
        return builder.Build();
    }

    private async Task<WebhookSendOutcome> SendOnceAsync()
    {
        var lease = (await repository.AcquireAsync(
            new WebhookAcquireRequest("test-worker", 1, TimeSpan.FromSeconds(15), 100), CancellationToken.None)).Single();
        var keys = await repository.LoadSigningKeysAsync(lease.SubscriptionId, CancellationToken.None);
        var decision = await repository.PrepareSendAsync(lease, keys.Select(key => key.KeyId).ToArray(), null, CancellationToken.None);
        Assert.Equal(WebhookSendDecisionKind.Send, decision.Kind);
        using var transport = new WebhookHttpTransport(options, new WebhookDestinationPolicy(options.Network), TimeProvider.System);
        var outcome = await transport.SendAsync(
            new WebhookSendRequest(new Uri(lease.EndpointUrl), lease.EventId, lease.Body,
                keys.Select(key => protector.Unprotect(key.ProtectedSecret)).ToArray(), options.HttpTimeout),
            CancellationToken.None);
        Assert.True((await repository.FinalizeAsync(lease, outcome, CancellationToken.None)).Applied);
        return outcome;
    }

    private static WebhookSendRequest Request(Uri destination, byte[] key, TimeSpan? timeout = null) =>
        new(destination, Guid.CreateVersion7(), Encoding.UTF8.GetBytes("{\"test\":true}"), [key], timeout ?? TimeSpan.FromSeconds(2));

    private async Task<TestSubscription> CreateSubscriptionAsync(
        string url,
        byte[] key,
        IReadOnlyList<string>? types = null,
        bool active = false,
        int maxConcurrency = 4)
    {
        var workflowKey = "webhook-dispatch-" + Guid.NewGuid().ToString("N");
        var (id, revision) = await WebhookTestDatabase.CreateSubscriptionAsync(
            fixture.DataSource, types ?? LifecycleEventTypes.Subscribable, workflowKey,
            state: active || types is not null ? "active" : "paused", url: url, maxConcurrency: maxConcurrency);
        var keyId = Guid.CreateVersion7();
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO flowbit.webhook_signing_keys ("Id", "SubscriptionId", "ProtectedSecret", "Status", "CreatedAt", "CreatedBy")
            VALUES (@key, @id, @secret, 'current', now(), 'tests')
            """, connection);
        command.Parameters.AddWithValue("key", keyId);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("secret", protector.Protect(key));
        await command.ExecuteNonQueryAsync();
        return new TestSubscription(id, revision, keyId, workflowKey);
    }

    private async Task<long> InsertInstanceAsync(string workflowKey)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            WITH definition AS (
                INSERT INTO flowbit.workflow_definitions ("Name", "Version", "Definition", "IsPublished", "WorkflowKey")
                VALUES (@key, 1, jsonb_build_object('id', @key, 'flowNodes', '[]'::jsonb), FALSE, @key)
                RETURNING "Id")
            INSERT INTO flowbit.workflow_instances ("WorkflowDefinitionId", "WorkflowKey", "Status")
            SELECT "Id", @key, 'running' FROM definition
            RETURNING "Id"
            """, connection);
        command.Parameters.AddWithValue("key", workflowKey);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(string Status, DateTimeOffset UpdatedAt, long Jobs, long Incidents)> InstanceFootprintAsync(long instanceId)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT instance."Status", instance."UpdatedAt",
                   (SELECT count(*) FROM flowbit.workflow_jobs job WHERE job."InstanceId" = instance."Id"),
                   (SELECT count(*) FROM flowbit.workflow_incidents incident WHERE incident."InstanceId" = instance."Id")
            FROM flowbit.workflow_instances instance
            WHERE instance."Id" = @id
            """, connection);
        command.Parameters.AddWithValue("id", instanceId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private async Task<Guid> QueueTestDeliveryAsync(Guid subscriptionId)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT flowbit.record_webhook_test_event(@id)", connection);
        command.Parameters.AddWithValue("id", subscriptionId);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Guid> InsertEventDeliveryAsync(TestSubscription subscription)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            WITH event AS (
                SELECT flowbit.record_lifecycle_event('instance.started', now(), 'workflow_instances', 1, @key, NULL, 1, NULL,
                    '{"status":"running"}', '{}'::text[], ARRAY[@revision]::bigint[], now()) AS id)
            INSERT INTO flowbit.webhook_deliveries
                ("Id", "EventId", "SubscriptionId", "RevisionId", "IsTest", "EventType", "WorkflowKey", "InstanceId",
                 "OccurredAt", "State", "DueAt", "RetryGeneration", "GenerationAttempts", "LifetimeAttempts",
                 "LeaseGeneration", "Revision", "CreatedAt", "UpdatedAt")
            SELECT flowbit.uuid_v7(), event.id, @subscription, @revision, FALSE, 'instance.started', @key, 1,
                   now(), 'pending', now(), 1, 0, 0, 0, 1, now(), now()
            FROM event
            RETURNING "Id"
            """, connection);
        command.Parameters.AddWithValue("key", subscription.WorkflowKey);
        command.Parameters.AddWithValue("revision", subscription.RevisionId);
        command.Parameters.AddWithValue("subscription", subscription.Id);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private async Task<DeliveryRow> DeliveryAsync(Guid id)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT "State", "GenerationAttempts", "LifetimeAttempts", "DueAt", "DeadlineAt", "LastOutcome",
                   "LastHttpStatus", "FailureCode", "EventId", "Id"
            FROM flowbit.webhook_deliveries WHERE "Id" = @id
            """, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DeliveryRow(
            reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetGuid(8), reader.GetGuid(9));
    }

    private async Task<DeliveryRow> WaitForDeliveryAsync(Guid subscriptionId, string state)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var connection = await source.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT \"Id\" FROM flowbit.webhook_deliveries WHERE \"SubscriptionId\" = @id AND \"State\" = @state LIMIT 1", connection);
            command.Parameters.AddWithValue("id", subscriptionId);
            command.Parameters.AddWithValue("state", state);
            if (await command.ExecuteScalarAsync() is Guid id)
            {
                return await DeliveryAsync(id);
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"No {state} delivery appeared.");
    }

    private async Task<byte[]> StoredBodyAsync(Guid eventId)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT \"Body\" FROM flowbit.lifecycle_events WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("id", eventId);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private async Task<Guid[]> AttemptKeysAsync(Guid deliveryId)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"SigningKeyIds\" FROM flowbit.webhook_delivery_attempts WHERE \"DeliveryId\" = @id ORDER BY \"AttemptNumber\" DESC LIMIT 1", connection);
        command.Parameters.AddWithValue("id", deliveryId);
        return (Guid[])(await command.ExecuteScalarAsync())!;
    }

    private async Task<string[]> AttemptOutcomesAsync(Guid deliveryId)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"Outcome\" FROM flowbit.webhook_delivery_attempts WHERE \"DeliveryId\" = @id ORDER BY \"AttemptNumber\"", connection);
        command.Parameters.AddWithValue("id", deliveryId);
        var outcomes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            outcomes.Add(reader.IsDBNull(0) ? "open" : reader.GetString(0));
        }
        return outcomes.ToArray();
    }

    private async Task<(string, string?)> SubscriptionStateAsync(Guid id)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"State\", \"PauseReason\" FROM flowbit.webhook_subscriptions WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async Task<(string, string)[]> FanoutStatesAsync(Guid eventId, Guid active, Guid archived)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT CASE "SubscriptionId" WHEN @archived THEN 'archived' ELSE 'active' END, "State"
            FROM flowbit.webhook_deliveries WHERE "EventId" = @event
            ORDER BY CASE "SubscriptionId" WHEN @archived THEN 0 ELSE 1 END
            """, connection);
        command.Parameters.AddWithValue("event", eventId);
        command.Parameters.AddWithValue("archived", archived);
        var states = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            states.Add((reader.GetString(0), reader.GetString(1)));
        }
        return states.ToArray();
    }

    private Task MakeDueAsync(Guid deliveryId) =>
        WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
            "UPDATE flowbit.webhook_deliveries SET \"DueAt\" = now() WHERE \"Id\" = @id", ("id", deliveryId));

    private Task MakeAllDueAsync(Guid subscriptionId) =>
        WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
            "UPDATE flowbit.webhook_deliveries SET \"DueAt\" = now() WHERE \"SubscriptionId\" = @id AND \"State\" IN ('pending', 'retryScheduled')",
            ("id", subscriptionId));

    private Task ExpireLeaseAsync(Guid deliveryId) =>
        WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
            "UPDATE flowbit.webhook_deliveries SET \"LeaseExpiresAt\" = now() - interval '1 second' WHERE \"Id\" = @id",
            ("id", deliveryId));

    // Leaves the lease two seconds, which the lock wait in AfterARowLockAsync outlasts.
    private Task ShortenLeaseAsync(Guid deliveryId) =>
        WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
            "UPDATE flowbit.webhook_deliveries SET \"LeaseExpiresAt\" = clock_timestamp() + interval '2 seconds' WHERE \"Id\" = @id",
            ("id", deliveryId));

    // Starts the operation while another transaction holds a row and releases the row only after a shortened lease
    // has ended, so the operation begins with a valid lease and gets the row after the lease expired.
    private async Task<T> AfterARowLockAsync<T>(string lockSql, Guid id, Func<Task<T>> operation)
    {
        await using var busy = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await busy.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand(lockSql, busy, transaction))
        {
            hold.Parameters.AddWithValue("id", id);
            await hold.ExecuteScalarAsync();
        }
        var pending = operation();
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.False(pending.IsCompleted);
        await transaction.CommitAsync();
        return await pending;
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
        await command.ExecuteNonQueryAsync();
    }

    private async Task RunRetentionAsync(int runs = 2)
    {
        await SetRetentionAsync(RetentionCategories.WebhookDeliveries, 1);
        await SetRetentionAsync(RetentionCategories.LifecycleEvents, 1);
        await using var retentionSource = new RetentionDataSource(fixture.ConnectionString);
        var retention = new RetentionRepository(retentionSource, TimeProvider.System);
        // A run already in progress keeps the policies it started with, so the last run applies these.
        for (var run = 0; run < runs; run++)
        {
            await retention.RequestRunAsync("tests", CancellationToken.None);
            for (var tick = 0; tick < 400; tick++)
            {
                var result = await retention.ProcessBatchAsync(new RetentionExecutionOptions
                {
                    WorkerId = "retention-test", BatchSize = 100, MaxRunnableJobs = 1_000_000, MaxQueueLagSeconds = 3600
                }, CancellationToken.None);
                if (!result.HasActiveRun) break;
            }
        }
    }

    // Acquisition is deployment-wide; another subscription's lease goes back untouched.
    private async Task<IReadOnlyList<WebhookDeliveryLease>> AcquireOwnAsync(Guid subscriptionId, int maxCount)
    {
        var leases = await repository.AcquireAsync(
            new WebhookAcquireRequest("own-worker", maxCount, TimeSpan.FromSeconds(15), 100), CancellationToken.None);
        foreach (var foreign in leases.Where(lease => lease.SubscriptionId != subscriptionId))
        {
            await repository.ReleaseBeforeSendAsync(foreign, CancellationToken.None);
        }
        return leases.Where(lease => lease.SubscriptionId == subscriptionId).ToArray();
    }

    private Task SetBackoffAsync(Guid subscriptionId, int failures, string startedAt, string until, int seconds) =>
        WebhookTestDatabase.ExecuteAsync(fixture.DataSource, $"""
            UPDATE flowbit.webhook_subscription_dispatch_state
               SET "ConsecutiveFailures" = {failures}, "BackoffStartedAt" = {startedAt}, "BackoffUntil" = {until},
                   "BackoffSeconds" = {seconds}
             WHERE "SubscriptionId" = @id
            """, ("id", subscriptionId));

    // Times are read against the database clock, which may differ from this host's.
    private async Task<(int Failures, DateTimeOffset? StartedAt, double? UntilSeconds, int Seconds, DateTimeOffset? LastSuccessAt)> DispatchStateAsync(Guid subscriptionId)
    {
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT "ConsecutiveFailures", "BackoffStartedAt", EXTRACT(EPOCH FROM "BackoffUntil" - now())::double precision,
                   "BackoffSeconds", "LastSuccessAt"
            FROM flowbit.webhook_subscription_dispatch_state WHERE "SubscriptionId" = @id
            """, connection);
        command.Parameters.AddWithValue("id", subscriptionId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetDouble(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
    }

    private async Task<Guid> AddSigningKeyAsync(Guid subscriptionId, string status, string? overlapExpiresAt, byte[]? key = null)
    {
        var keyId = Guid.CreateVersion7();
        await using var connection = await source.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            INSERT INTO flowbit.webhook_signing_keys
                ("Id", "SubscriptionId", "ProtectedSecret", "Status", "OverlapExpiresAt", "CreatedAt", "CreatedBy")
            VALUES (@key, @id, @secret, @status, {overlapExpiresAt ?? "NULL"}, now(), 'tests')
            """, connection);
        command.Parameters.AddWithValue("key", keyId);
        command.Parameters.AddWithValue("id", subscriptionId);
        command.Parameters.AddWithValue("secret", protector.Protect(key ?? StandardWebhookSignature.GenerateKey()));
        command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync();
        return keyId;
    }

    private async Task<Guid> InsertOldEventAsync(TestSubscription subscription, bool fannedOut)
    {
        Guid eventId;
        await using (var connection = await source.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("""
            SELECT flowbit.record_lifecycle_event('instance.started', now(), 'workflow_instances', 1, @key,
                NULL, 1, NULL, '{"status":"running"}', '{}'::text[], ARRAY[@revision]::bigint[])
            """, connection))
        {
            command.Parameters.AddWithValue("key", subscription.WorkflowKey);
            command.Parameters.AddWithValue("revision", subscription.RevisionId);
            eventId = (Guid)(await command.ExecuteScalarAsync())!;
        }
        await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
            UPDATE flowbit.lifecycle_events
               SET "OccurredAt" = now() - interval '10 days', "FanoutCompletedAt" = CASE WHEN @fanned THEN now() END
             WHERE "Id" = @id
            """, ("fanned", fannedOut), ("id", eventId));
        return eventId;
    }

    private Task<long> CountEventAsync(Guid eventId) =>
        WebhookTestDatabase.CountEventsAsync(fixture.DataSource,
            "SELECT count(*) FROM flowbit.lifecycle_events WHERE \"Id\" = @id", ("id", eventId));

    private WebhookOptions SlowReceiverOptions(int heartbeatSeconds) => new()
    {
        DeliveryEnabled = true,
        PollSeconds = 1,
        HttpTimeoutSeconds = 10,
        LeaseSeconds = 15,
        HeartbeatSeconds = heartbeatSeconds,
        Network = options.Network,
        KeyProtection = options.KeyProtection
    };

    private sealed record TestSubscription(Guid Id, long RevisionId, Guid KeyId, string WorkflowKey);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }

    /// <summary>Key protection that cannot read its key ring, reporting itself unavailable after it fails, as the real one does.</summary>
    private sealed class FailingSecretProtector(IWebhookSecretProtector inner, Exception failure) : IWebhookSecretProtector
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public bool IsAvailable => Calls == 0 && inner.IsAvailable;

        public string? UnavailableReason => Calls == 0 ? inner.UnavailableReason : failure.Message;

        public byte[] Protect(byte[] secret) => inner.Protect(secret);

        public byte[] Unprotect(byte[] protectedSecret)
        {
            Interlocked.Increment(ref calls);
            throw failure;
        }
    }

    private sealed record DeliveryRow(
        string State,
        int GenerationAttempts,
        int LifetimeAttempts,
        DateTimeOffset DueAt,
        DateTimeOffset? DeadlineAt,
        string? LastOutcome,
        int? LastHttpStatus,
        string? FailureCode,
        Guid EventId,
        Guid Id);
}

internal sealed record ReceivedWebhook(
    string Id,
    string Timestamp,
    string Signature,
    string? ContentType,
    string? UserAgent,
    byte[] Body);

/// <summary>A fixture-owned loopback receiver that records signed requests and answers with a scripted status.</summary>
internal sealed class WebhookTestReceiver : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int expected = int.MaxValue;
    private int aborted;

    private WebhookTestReceiver(WebApplication app) => this.app = app;

    public ConcurrentQueue<ReceivedWebhook> Requests { get; } = new();

    /// <summary>Delayed requests the sender abandoned before this receiver answered.</summary>
    public int Aborted => Volatile.Read(ref aborted);

    public int Port => new Uri(app.Urls.First()).Port;

    public string Url(string path) => $"http://127.0.0.1:{Port}/{path}";

    public static async Task<WebhookTestReceiver> StartAsync(Func<HttpContext, int>? respond = null, TimeSpan? delay = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var receiver = new WebhookTestReceiver(app);
        app.Run(async context =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            receiver.Requests.Enqueue(new ReceivedWebhook(
                context.Request.Headers["webhook-id"].ToString(),
                context.Request.Headers["webhook-timestamp"].ToString(),
                context.Request.Headers["webhook-signature"].ToString(),
                context.Request.ContentType,
                context.Request.Headers.UserAgent.ToString(),
                buffer.ToArray()));
            if (receiver.Requests.Count >= Volatile.Read(ref receiver.expected))
            {
                receiver.arrived.TrySetResult();
            }
            if (delay is { } wait)
            {
                try
                {
                    await Task.Delay(wait, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref receiver.aborted);
                    return;
                }
            }
            context.Response.StatusCode = respond?.Invoke(context) ?? StatusCodes.Status204NoContent;
        });
        await app.StartAsync();
        return receiver;
    }

    public async Task<ReceivedWebhook[]> WaitForAsync(int count)
    {
        Volatile.Write(ref expected, count);
        if (Requests.Count < count)
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        return Requests.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
