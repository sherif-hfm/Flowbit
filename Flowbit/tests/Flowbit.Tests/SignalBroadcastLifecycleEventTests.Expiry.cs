using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// EVENT-04E: the fourth broadcast type, signalBroadcast.expired. Every transaction that derives the status raises it
/// once: a settle by a delivery job, a settle by the Worker's sweep, and the reconciliation of a lost lease. Its body
/// is the shared one plus expiredCount; a subscription that lists only the three earlier types hears nothing.
/// </summary>
public sealed partial class SignalBroadcastLifecycleEventTests
{
    private static readonly string[] EarlierBroadcastTypes =
    [
        LifecycleEventTypes.SignalBroadcastCompleted,
        LifecycleEventTypes.SignalBroadcastCompletedWithIssues,
        LifecycleEventTypes.SignalBroadcastCancelled
    ];

    [Fact]
    public async Task Expired_BySettleOfAJobRaisesOneEventWithItsCountAndNoneForTheEarlierTypes()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflowId = await CreateAsync(model);
        await StartAsync(workflowId);
        await StartAsync(workflowId);
        var (earlierId, earlierRevision) = await WebhookTestDatabase.CreateSubscriptionAsync(
            fixture.DataSource, EarlierBroadcastTypes, model.Id);
        try
        {
            await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
            {
                var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3), ExpiresAfter: "PT1H"));
                await MoveExpiryIntoThePastAsync(broadcast.Id);
                await ProcessDeliveryJobsAsync(broadcast.Id);

                var row = await BroadcastRowAsync(broadcast.Id);
                var captured = Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));
                var body = AssertEvent(captured, LifecycleEventTypes.SignalBroadcastExpired);
                AssertMatchesRow(body, row);
                Assert.Equal((2, 2, 2, (int?)2), (body.Data.TotalDeliveryCount, body.Data.SkippedCount, row.ExpiredCount, body.Data.ExpiredCount));
                Assert.Equal(row.UpdatedAt, captured.OccurredAt);

                // The golden body: the twelve shared fields in catalog order, then expiredCount.
                using var document = JsonDocument.Parse(captured.Body);
                var fields = document.RootElement.GetProperty("data").EnumerateObject().Select(property => property.Name).ToArray();
                Assert.Equal(13, fields.Length);
                Assert.Equal("expiredCount", fields[^1]);
                Assert.Equal(
                    LifecycleEventCatalog.Find(LifecycleEventTypes.SignalBroadcastCompleted)!.DataFields.Select(field => field.Name),
                    fields[..^1]);

                Assert.Empty(await BroadcastEventsAsync(earlierRevision, broadcast.Id));
            });
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, earlierId, "archived");
        }
    }

    [Fact]
    public async Task Expired_BySweepRaisesOneEvent()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3), ExpiresAfter: "PT1H"));
            foreach (var job in await DeliveryJobsAsync(broadcast.Id))
            {
                await HoldJobAsync(job);
            }
            await MoveExpiryIntoThePastAsync(broadcast.Id);

            var settled = new List<long>();
            await using (var clock = fixture.Factory.Services.CreateAsyncScope())
            {
                var databaseNow = await clock.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>()
                    .GetDatabaseTimeAsync(CancellationToken.None);
                for (var pass = 0; pass < 500; pass++)
                {
                    await using var scope = fixture.Factory.Services.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<ISignalBroadcastExpirer>()
                        .ExpireNextDueAsync(databaseNow, CancellationToken.None);
                    if (result is null) break;
                    settled.Add(result.BroadcastId);
                }
            }
            Assert.Contains(broadcast.Id, settled);

            var body = AssertEvent(Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id)),
                LifecycleEventTypes.SignalBroadcastExpired);
            AssertMatchesRow(body, await BroadcastRowAsync(broadcast.Id));
            Assert.Equal(1, body.Data.ExpiredCount);

            // A job that runs afterwards finds nothing pending and raises nothing.
            await ProcessDeliveryJobsAsync(broadcast.Id);
            Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));
        });
    }

    [Fact]
    public async Task Expired_ByTheReconciliationOfALostLeaseRaisesOneEvent()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3), ExpiresAfter: "PT1H"));
            var job = Assert.Single(await DeliveryJobsAsync(broadcast.Id));
            await ExhaustLeaseAsync(job);
            await MoveExpiryIntoThePastAsync(broadcast.Id);
            await OpenExhaustedLeaseIncidentsAsync(job);

            var body = AssertEvent(Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id)),
                LifecycleEventTypes.SignalBroadcastExpired);
            AssertMatchesRow(body, await BroadcastRowAsync(broadcast.Id));
            Assert.Equal((1, 0, (int?)1), (body.Data.SkippedCount, body.Data.FailedCount, body.Data.ExpiredCount));
        });
    }

    [Fact]
    public async Task EarlierTypes_KeepTheirBodiesWithoutExpiredCount()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            // An expiry that has not passed changes nothing in a completed broadcast's body.
            var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3), ExpiresAfter: "PT1H"));
            await ProcessDeliveryJobsAsync(broadcast.Id);
            var captured = Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));
            var body = AssertEvent(captured, LifecycleEventTypes.SignalBroadcastCompleted);
            Assert.Null(body.Data.ExpiredCount);
            using var document = JsonDocument.Parse(captured.Body);
            Assert.False(document.RootElement.GetProperty("data").TryGetProperty("expiredCount", out _));
            Assert.Equal(12, document.RootElement.GetProperty("data").EnumerateObject().Count());
        });
    }

    /// <summary>Moves a broadcast's expiry a second into the past by the database's clock.</summary>
    private Task MoveExpiryIntoThePastAsync(long broadcastId) =>
        WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
            UPDATE flowbit.signal_broadcasts
            SET "ExpiresAt" = clock_timestamp() - interval '1 second'
            WHERE "Id" = @id
            """, ("id", broadcastId));
}
