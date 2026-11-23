using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// EVENT-04D: a committed change of a signal broadcast's status to completed, completedWithIssues, or cancelled
/// raises one lifecycle event, captured by deferred triggers on signal_broadcasts with no producer code. These
/// cases cover every writer of the status: a delivery job's last recount, a throw that reaches nothing, a
/// cancel, a retry, and the raw-SQL reconciliation of a lost lease.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed partial class SignalBroadcastLifecycleEventTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] BroadcastTypes = [.. LifecycleEventTypes.SignalBroadcastTypes];

    public Task InitializeAsync() => Task.CompletedTask;

    // Some cases leave a broadcast delivering on purpose; later classes must not lease its jobs.
    public Task DisposeAsync() => SignalDeliveryJobLeftovers.RetireAsync(fixture);

    /// <summary>
    /// A broadcast status added later, such as an expiry, fails here until it gets an event type in
    /// capture_signal_broadcast_lifecycle() and LifecycleEventCatalog, or is deliberately left out of this list.
    /// </summary>
    [Fact]
    public void EveryFinishedBroadcastStatusHasItsEventType()
    {
        Assert.Equal(
            SignalBroadcastStatuses.All.Where(status => status != SignalBroadcastStatuses.Delivering)
                .Select(status => "signalBroadcast." + status),
            LifecycleEventTypes.SignalBroadcastTypes);
        var constants = typeof(SignalBroadcastStatuses)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!);
        Assert.Equal(SignalBroadcastStatuses.All.Order(StringComparer.Ordinal), constants.Order(StringComparer.Ordinal));
        Assert.All(LifecycleEventTypes.SignalBroadcastTypes, type =>
        {
            Assert.Contains(type, LifecycleEventTypes.Subscribable);
            Assert.True(LifecycleEventCatalog.Find(type) is { Subscribable: true });
        });
    }

    [Fact]
    public async Task FinishByDelivery_RaisesOneEventWhenTheLastOfTwoJobsRecounts()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var v1 = await CreateAsync(model);
        var first = await StartAsync(v1);
        var second = await StartAsync(await NewVersionAsync(v1, WithKey(SignalRuntimeTests.CatchModel(signal), model.Id)));
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3)));
            Assert.Equal(2, broadcast.TotalDeliveryCount);
            var jobs = await DeliveryJobsAsync(broadcast.Id);
            Assert.Equal(2, jobs.Count);

            // One job delivers its definition's wait and recounts: one delivery is still pending.
            await HoldJobAsync(jobs[1]);
            await ProcessJobsAsync([jobs[0]]);
            Assert.Equal(SignalBroadcastStatuses.Delivering, (await BroadcastRowAsync(broadcast.Id)).Status);
            Assert.Empty(await BroadcastEventsAsync(revision, broadcast.Id));

            await ProcessJobsAsync([jobs[1]]);
            var row = await BroadcastRowAsync(broadcast.Id);
            var captured = Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));
            var body = AssertEvent(captured, LifecycleEventTypes.SignalBroadcastCompleted);
            AssertMatchesRow(body, row);
            Assert.Equal((2, 2, 0, "workflow", "user"),
                (body.Data.TotalDeliveryCount, body.Data.DeliveredCount, body.Data.StartCount, body.Data.Scope, body.Data.ThrownByKind));
            Assert.Equal((model.Id, (long?)null), (body.WorkflowKey, body.InstanceId));
            // The timestamp is the UpdatedAt of the row version that changed the status.
            Assert.Equal(row.UpdatedAt, captured.OccurredAt);
            Assert.Equal(row.CompletedAt, captured.OccurredAt);

            // A job that runs again with nothing pending recounts without a change and raises nothing.
            await ProcessDeliveryJobsAsync(broadcast.Id);
            Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));
        });
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task FailedDelivery_RaisesCompletedWithIssuesOnlyForItsType()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        var (completedOnlyId, completedOnlyRevision) = await WebhookTestDatabase.CreateSubscriptionAsync(
            fixture.DataSource, [LifecycleEventTypes.SignalBroadcastCompleted], model.Id);
        try
        {
            await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
            {
                var broadcast = await ThrowAsync(new ThrowSignalRequest(
                    signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = "seven" })));
                await ProcessDeliveryJobsAsync(broadcast.Id);

                var body = AssertEvent(
                    Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id)),
                    LifecycleEventTypes.SignalBroadcastCompletedWithIssues);
                AssertMatchesRow(body, await BroadcastRowAsync(broadcast.Id));
                Assert.Equal((1, 1, 0), (body.Data.TotalDeliveryCount, body.Data.FailedCount, body.Data.DeliveredCount));
                Assert.Empty(await BroadcastEventsAsync(completedOnlyRevision, broadcast.Id));
            });
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, completedOnlyId, "archived");
        }
    }

    [Fact]
    public async Task ThrowThatReachesNothing_StoresItsEventWithTheThrow()
    {
        var model = SignalRuntimeTests.CatchModel(UniqueSignal());
        await CreateAsync(model);
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), model.Id));
            Assert.Equal(SignalBroadcastStatuses.Completed, broadcast.Status);

            // Present when the request returns: it committed with the broadcast.
            var captured = Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));
            var body = AssertEvent(captured, LifecycleEventTypes.SignalBroadcastCompleted);
            Assert.Equal((0, 0, 0, 0, 0, 0), (body.Data.TotalDeliveryCount, body.Data.StartCount, body.Data.DeliveredCount,
                body.Data.SkippedCount, body.Data.FailedCount, body.Data.CancelledCount));
            Assert.Equal(broadcast.Name, body.Data.SignalName);
            Assert.Equal((model.Id, "workflow"), (body.WorkflowKey, body.Data.Scope));
        });
    }

    [Fact]
    public async Task Cancel_ReportsTheCountsItCommitsAndRaisesNothingAfterwards()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflowId = await CreateAsync(model);
        await StartAsync(workflowId);
        await StartAsync(workflowId);
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(5)));
            Assert.Equal(2, broadcast.TotalDeliveryCount);

            using (var cancel = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/cancel",
                       new CancelSignalBroadcastRequest("Wrong version")))
            {
                Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            }
            var body = AssertEvent(
                Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id)),
                LifecycleEventTypes.SignalBroadcastCancelled);
            // The cancel writes the status before it recounts; the event has the counts it commits.
            Assert.Equal((2, 2, 0), (body.Data.TotalDeliveryCount, body.Data.CancelledCount, body.Data.DeliveredCount));
            AssertMatchesRow(body, await BroadcastRowAsync(broadcast.Id));

            // A repeated cancel and the jobs' later recount change no status.
            using (var again = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/cancel", new { }))
            {
                Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            }
            await ProcessDeliveryJobsAsync(broadcast.Id);
            Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));

            // A finished broadcast cannot be cancelled and raises nothing more.
            var finished = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), model.Id));
            using (var refused = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{finished.Id}/cancel", new { }))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            }
            Assert.Equal(LifecycleEventTypes.SignalBroadcastCompleted,
                Assert.Single(await BroadcastEventsAsync(revision, finished.Id)).Type);
        });
    }

    [Fact]
    public async Task RetryThatReopens_RaisesNothingUntilTheBroadcastFinishesAgain()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(
                signal, model.Id, Payload: JsonSerializer.SerializeToElement(new { version = "bad" })));
            await ProcessDeliveryJobsAsync(broadcast.Id);
            var first = Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));
            Assert.Equal(LifecycleEventTypes.SignalBroadcastCompletedWithIssues, first.Type);

            var failed = await GetBroadcastAsync(broadcast.Id);
            using (var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
                       new RetrySignalDeliveriesRequest(failed.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            }
            Assert.Equal(SignalBroadcastStatuses.Delivering, (await BroadcastRowAsync(broadcast.Id)).Status);
            Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));

            await ProcessDeliveryJobsAsync(broadcast.Id);
            var events = await BroadcastEventsAsync(revision, broadcast.Id);
            Assert.Equal(2, events.Length);
            Assert.Equal((first.Id, first.Body, first.OccurredAt), (events[0].Id, events[0].Body, events[0].OccurredAt));
            Assert.NotEqual(first.Id, events[1].Id);
            var body = AssertEvent(events[1], LifecycleEventTypes.SignalBroadcastCompletedWithIssues);
            AssertMatchesRow(body, await BroadcastRowAsync(broadcast.Id));
        });
    }

    [Fact]
    public async Task RetryOfAStartThatNoLongerListens_RaisesCompletedAtTheRetrysTime()
    {
        var signal = UniqueSignal();
        var model = StartModel(signal);
        var v1 = await CreateAsync(model);
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            // The start's required mapping fails, so the start delivery fails.
            var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id,
                Payload: JsonSerializer.SerializeToElement(new { other = "R-1" })));
            Assert.Equal((1, 1), (broadcast.TotalDeliveryCount, broadcast.StartCount));
            await ProcessDeliveryJobsAsync(broadcast.Id);
            var first = Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));
            var firstBody = AssertEvent(first, LifecycleEventTypes.SignalBroadcastCompletedWithIssues);
            Assert.Equal((1, 1), (firstBody.Data.StartCount, firstBody.Data.FailedCount));

            await SetDefaultAsync(await NewVersionAsync(v1, StartModel(UniqueSignal(), model.Id)));
            var failed = await GetBroadcastAsync(broadcast.Id);
            using (var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
                       new RetrySignalDeliveriesRequest(failed.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            }

            var row = await BroadcastRowAsync(broadcast.Id);
            var events = await BroadcastEventsAsync(revision, broadcast.Id);
            Assert.Equal(2, events.Length);
            var body = AssertEvent(events[1], LifecycleEventTypes.SignalBroadcastCompleted);
            Assert.Equal((1, 1, 0), (body.Data.StartCount, body.Data.SkippedCount, body.Data.FailedCount));
            AssertMatchesRow(body, row);
            // CompletedAt keeps the first completion; the event's time is the retry's.
            Assert.Equal(row.UpdatedAt, events[1].OccurredAt);
            Assert.Equal(first.OccurredAt, row.CompletedAt);
            Assert.True(events[1].OccurredAt > first.OccurredAt);
        });
    }

    [Fact]
    public async Task LostLease_RaisesTheBroadcastEventBesideTheIncidentInOneTransaction()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id,
            [.. BroadcastTypes, LifecycleEventTypes.IncidentOpened], async revision =>
            {
                var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1)));
                var job = Assert.Single(await DeliveryJobsAsync(broadcast.Id));

                await ExhaustLeaseAsync(job);
                await OpenExhaustedLeaseIncidentsAsync(job);

                var row = await BroadcastRowAsync(broadcast.Id);
                Assert.Equal(SignalBroadcastStatuses.CompletedWithIssues, row.Status);
                var captured = Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));
                var body = AssertEvent(captured, LifecycleEventTypes.SignalBroadcastCompletedWithIssues);
                Assert.Equal((1, 1), (body.Data.TotalDeliveryCount, body.Data.FailedCount));
                AssertMatchesRow(body, row);
                var incident = Assert.Single(
                    await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision),
                    item => item.Type == LifecycleEventTypes.IncidentOpened
                        && item.Data.GetProperty("jobId").GetInt64() == job);
                Assert.Equal(model.Id, incident.WorkflowKey);
                // Both events were inserted by one transaction: the reconciliation's.
                Assert.Equal(1, await WebhookTestDatabase.CountEventsAsync(fixture.DataSource,
                    "SELECT count(DISTINCT xmin::text) FROM flowbit.lifecycle_events WHERE \"Id\" = ANY(@ids)",
                    ("ids", new[] { captured.Id, incident.Id })));
            });
    }

    [Fact]
    public async Task LostLeaseWithAnotherDefinitionPending_RaisesTheEventWhenTheOtherJobFinishes()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var v1 = await CreateAsync(model);
        await StartAsync(v1);
        await StartAsync(await NewVersionAsync(v1, WithKey(SignalRuntimeTests.CatchModel(signal), model.Id)));
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1)));
            var jobs = await DeliveryJobsAsync(broadcast.Id);
            Assert.Equal(2, jobs.Count);

            await HoldJobAsync(jobs[1]);
            await ExhaustLeaseAsync(jobs[0]);
            await OpenExhaustedLeaseIncidentsAsync(jobs[0]);
            var reconciled = await BroadcastRowAsync(broadcast.Id);
            Assert.Equal((SignalBroadcastStatuses.Delivering, 1), (reconciled.Status, reconciled.FailedCount));
            Assert.Empty(await BroadcastEventsAsync(revision, broadcast.Id));

            await ProcessJobsAsync([jobs[1]]);
            var body = AssertEvent(
                Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id)),
                LifecycleEventTypes.SignalBroadcastCompletedWithIssues);
            Assert.Equal((2, 1, 1), (body.Data.TotalDeliveryCount, body.Data.DeliveredCount, body.Data.FailedCount));
            AssertMatchesRow(body, await BroadcastRowAsync(broadcast.Id));
        });
    }

    [Fact]
    public async Task BroadcastOpenBehindAnIncident_RaisesNothingUntilTheRetriedJobFinishesIt()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(2)));
            var job = Assert.Single(await DeliveryJobsAsync(broadcast.Id));

            // A payload that names no broadcast stops the job with an incident and delivers nothing.
            var original = await ScalarTextAsync("SELECT \"PayloadJson\"::text FROM flowbit.workflow_jobs WHERE \"Id\" = @id", job);
            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
                "UPDATE flowbit.workflow_jobs SET \"PayloadJson\" = '{}'::jsonb WHERE \"Id\" = @id", ("id", job));
            await ProcessJobsAsync([job]);
            long incidentId;
            await using (var db = fixture.CreateDbContext())
            {
                Assert.Equal(WorkflowJobStatuses.Incident, await db.WorkflowJobs.Where(item => item.Id == job)
                    .Select(item => item.Status).SingleAsync());
                incidentId = await db.WorkflowIncidents.Where(item => item.JobId == job && item.Status == "open")
                    .Select(item => item.Id).SingleAsync();
            }
            Assert.Equal(SignalBroadcastStatuses.Delivering, (await BroadcastRowAsync(broadcast.Id)).Status);
            Assert.Empty(await BroadcastEventsAsync(revision, broadcast.Id));

            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
                "UPDATE flowbit.workflow_jobs SET \"PayloadJson\" = @payload::jsonb WHERE \"Id\" = @id",
                ("payload", original!), ("id", job));
            using (var retry = await SendAsync(HttpMethod.Post, $"/api/incidents/{incidentId}/retry"))
            {
                Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
            }
            await ProcessJobsAsync([job]);

            var body = AssertEvent(
                Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id)),
                LifecycleEventTypes.SignalBroadcastCompleted);
            Assert.Equal((1, 1), (body.Data.TotalDeliveryCount, body.Data.DeliveredCount));
        });
    }

    [Fact]
    public async Task IdempotentReplayAndRefusedThrow_StoreNoEvent()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflowId = await CreateAsync(model);
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            var key = "replay-" + Guid.NewGuid().ToString("N");
            var request = new ThrowSignalRequest(UniqueSignal(), model.Id, IdempotencyKey: key);
            var thrown = await ThrowAsync(request);
            Assert.Single(await BroadcastEventsAsync(revision, thrown.Id));
            using (var replay = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request))
            {
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                Assert.Equal(thrown.Id, (await replay.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!.Id);
            }
            Assert.Single(await BroadcastEventsAsync(revision, thrown.Id));

            // At the recipient limit the broadcast is inserted and deleted in one transaction.
            await StartAsync(workflowId);
            await StartAsync(workflowId);
            var previous = await SetEngineSettingAsync("Signals", "MaxSubscriptionsPerBroadcast", "1");
            try
            {
                using var refused = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", new ThrowSignalRequest(signal, model.Id));
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            }
            finally
            {
                await SetEngineSettingAsync("Signals", "MaxSubscriptionsPerBroadcast", previous);
            }
            await using var db = fixture.CreateDbContext();
            Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.SignalName == signal));
            Assert.DoesNotContain(await BroadcastEventsAsync(revision), item => item.Body.Contains(signal, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task WorkflowThrows_NameTheThrowingInstanceAndCommitWithTheStep()
    {
        var signal = UniqueSignal();
        var receiver = KeyedCatchModel(signal);
        var receiverId = await CreateAsync(receiver);
        var marker = Guid.NewGuid().ToString("N")[..8];
        var waiting = await StartAsync(receiverId, OrderNumber("ORD-1-" + marker));
        var scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = receiver.Id };
        var intermediate = ThrowerModel(signal, scope, endEvent: false);
        var end = ThrowerModel(UniqueSignal(), scope, endEvent: true);
        var intermediateId = await CreateAsync(intermediate);
        var endId = await CreateAsync(end);
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, receiver.Id, BroadcastTypes, async revision =>
        {
            // An intermediate throw that reaches the waiting instance: the event follows its delivery.
            var throwing = await StartAsync(intermediateId);
            await TakeAsync(throwing.Id, 2, 201);
            var thrown = await ThrownByAsync(throwing.Id);
            Assert.Empty(await BroadcastEventsAsync(revision, thrown.Id));
            await ProcessDeliveryJobsAsync(thrown.Id);
            var body = AssertEvent(
                Assert.Single(await BroadcastEventsAsync(revision, thrown.Id)),
                LifecycleEventTypes.SignalBroadcastCompleted);
            Assert.Equal(("instance", (long?)throwing.Id, 1), (body.Data.ThrownByKind, body.Data.ThrownByInstanceId, body.Data.DeliveredCount));
            // The envelope names the scope, never the thrower.
            Assert.Equal((receiver.Id, (long?)null), (body.WorkflowKey, body.InstanceId));
            Assert.NotEqual(waiting.Id, throwing.Id);

            // A signal end event that reaches nothing stores its event with the step that ended the instance.
            var ending = await StartAsync(endId);
            await TakeAsync(ending.Id, 2, 201);
            var ended = await ThrownByAsync(ending.Id);
            var endBody = AssertEvent(
                Assert.Single(await BroadcastEventsAsync(revision, ended.Id)),
                LifecycleEventTypes.SignalBroadcastCompleted);
            Assert.Equal(("instance", (long?)ending.Id, 0), (endBody.Data.ThrownByKind, endBody.Data.ThrownByInstanceId, endBody.Data.TotalDeliveryCount));
            await using (var check = fixture.CreateDbContext())
            {
                Assert.Equal(WorkflowInstanceStatuses.Completed, await check.WorkflowInstances
                    .Where(item => item.Id == ending.Id).Select(item => item.Status).SingleAsync());
            }

            // A refused throw fails its step and stores nothing: two waits are over a limit of one.
            await StartAsync(receiverId, OrderNumber("ORD-2-" + marker));
            await StartAsync(receiverId, OrderNumber("ORD-3-" + marker));
            var refusedThrower = await StartAsync(intermediateId);
            var previous = await SetEngineSettingAsync("Signals", "MaxSubscriptionsPerBroadcast", "1");
            try
            {
                using var refused = await TakeResponseAsync(refusedThrower.Id, 2, 201);
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            }
            finally
            {
                await SetEngineSettingAsync("Signals", "MaxSubscriptionsPerBroadcast", previous);
            }
            await using var db = fixture.CreateDbContext();
            Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.ThrowerInstanceId == refusedThrower.Id));
            Assert.DoesNotContain(await BroadcastEventsAsync(revision),
                item => item.Data.GetProperty("thrownByInstanceId").ValueKind == JsonValueKind.Number
                    && item.Data.GetProperty("thrownByInstanceId").GetInt64() == refusedThrower.Id);
        });
    }

    [Fact]
    public async Task ClientAndUserThrowers_AreNamedByKindOnly()
    {
        var model = SignalRuntimeTests.CatchModel(UniqueSignal());
        await CreateAsync(model);
        var clientId = $"signal-events-{Guid.NewGuid():N}"[..40];
        string secret;
        using (var create = await SendAsync(HttpMethod.Post, "/api/shared-variable-clients",
                   new CreateSharedVariableClientRequest(clientId, "Signal event test client",
                       [SharedVariableClientScopes.SignalsThrow, SharedVariableClientScopes.SignalsRead])))
        {
            Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
            secret = (await create.Content.ReadFromJsonAsync<CreateSharedVariableClientResult>(JsonOptions))!.ClientSecret;
        }
        var userName = "user-marker-" + Guid.NewGuid().ToString("N")[..12];
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, BroadcastTypes, async revision =>
        {
            using var clientThrow = await SendAsClientAsync(HttpMethod.Post, "/api/signal-broadcasts", clientId, secret,
                new ThrowSignalRequest(UniqueSignal(), model.Id));
            Assert.Equal(HttpStatusCode.Accepted, clientThrow.StatusCode);
            var byClient = (await clientThrow.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
            var userThrown = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), model.Id), userName);

            var clientEvent = Assert.Single(await BroadcastEventsAsync(revision, byClient.Id));
            Assert.Equal("client", AssertEvent(clientEvent, LifecycleEventTypes.SignalBroadcastCompleted).Data.ThrownByKind);
            Assert.DoesNotContain(clientId, clientEvent.Body, StringComparison.Ordinal);
            var userEvent = Assert.Single(await BroadcastEventsAsync(revision, userThrown.Id));
            var userBody = AssertEvent(userEvent, LifecycleEventTypes.SignalBroadcastCompleted);
            Assert.Equal(("user", (long?)null), (userBody.Data.ThrownByKind, userBody.Data.ThrownByInstanceId));
            Assert.DoesNotContain(userName, userEvent.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Scopes_NameTheirKindAndTheEnvelopeNamesTheScopesFamilyAndInstance()
    {
        var family = KeyedCatchModel(UniqueSignal());
        var familyId = await CreateAsync(family);
        var instance = await StartAsync(familyId, OrderNumber("ORD-" + Guid.NewGuid().ToString("N")[..8]));
        var startSignal = UniqueSignal();
        var starter = StartModel(startSignal);
        await CreateAsync(starter);
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, null, BroadcastTypes, async revision =>
        {
            var byFamily = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), family.Id));
            var byKey = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), family.Id, BusinessKey: "ORD-NONE"));
            var byInstance = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), InstanceId: instance.Id));
            var everywhere = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), AllWorkflows: true));

            foreach (var (broadcast, scope, key, instanceId) in new[]
                     {
                         (byFamily, "workflow", (string?)family.Id, (long?)null),
                         (byKey, "businessKey", family.Id, null),
                         (byInstance, "instance", family.Id, instance.Id),
                         (everywhere, "allWorkflows", null, null)
                     })
            {
                var captured = Assert.Single(await BroadcastEventsAsync(revision, broadcast.Id));
                var body = AssertEvent(captured, LifecycleEventTypes.SignalBroadcastCompleted);
                Assert.Equal((scope, key, instanceId), (body.Data.Scope, body.WorkflowKey, body.InstanceId));
                Assert.Equal((key, instanceId), (captured.WorkflowKey, captured.InstanceId));
            }
            // The business key itself is never sent.
            Assert.DoesNotContain("ORD-NONE", Assert.Single(await BroadcastEventsAsync(revision, byKey.Id)).Body, StringComparison.Ordinal);

            var started = await ThrowAsync(new ThrowSignalRequest(startSignal, starter.Id,
                Payload: JsonSerializer.SerializeToElement(new { recallNumber = "R-9" })));
            await ProcessDeliveryJobsAsync(started.Id);
            var startBody = AssertEvent(
                Assert.Single(await BroadcastEventsAsync(revision, started.Id)),
                LifecycleEventTypes.SignalBroadcastCompleted);
            Assert.Equal((1, 1, 1), (startBody.Data.TotalDeliveryCount, startBody.Data.StartCount, startBody.Data.DeliveredCount));
        });
    }

    [Fact]
    public async Task FamilySubscriptions_MatchTheFamilyTheScopeNames()
    {
        var waitSignal = UniqueSignal();
        var familyA = KeyedCatchModel(waitSignal);
        var familyAId = await CreateAsync(familyA);
        var instance = await StartAsync(familyAId, OrderNumber("ORD-" + Guid.NewGuid().ToString("N")[..8]));
        var familyB = SignalRuntimeTests.CatchModel(UniqueSignal());
        await CreateAsync(familyB);
        var (subscriptionA, revisionA) = await WebhookTestDatabase.CreateSubscriptionAsync(fixture.DataSource, BroadcastTypes, familyA.Id);
        var (subscriptionAll, revisionAll) = await WebhookTestDatabase.CreateSubscriptionAsync(fixture.DataSource, BroadcastTypes, null);
        try
        {
            await WebhookTestDatabase.SetCaptureAsync(fixture.DataSource, true);
            var toFamilyA = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), familyA.Id));
            var toKeyInA = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), familyA.Id, BusinessKey: "ORD-OTHER"));
            var toInstanceOfA = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), InstanceId: instance.Id));
            var toFamilyB = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), familyB.Id));
            // An instance of A receives this broadcast, and still only the subscription without a family hears of it.
            var toAll = await ThrowAsync(new ThrowSignalRequest(waitSignal, AllWorkflows: true));
            Assert.Equal(1, toAll.TotalDeliveryCount);
            await ProcessDeliveryJobsAsync(toAll.Id);
            Assert.Equal(1, (await BroadcastRowAsync(toAll.Id)).DeliveredCount);

            foreach (var (broadcast, matchesA) in new[]
                     {
                         (toFamilyA, true), (toKeyInA, true), (toInstanceOfA, true), (toFamilyB, false), (toAll, false)
                     })
            {
                Assert.Single(await BroadcastEventsAsync(revisionAll, broadcast.Id));
                Assert.Equal(matchesA ? 1 : 0, (await BroadcastEventsAsync(revisionA, broadcast.Id)).Length);
            }
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscriptionA, "archived");
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscriptionAll, "archived");
            await WebhookTestDatabase.RestoreCaptureAsync(fixture.DataSource);
        }
    }

    [Fact]
    public async Task Bodies_NeverCarryThePayloadBusinessKeyThrowerReasonOrDescription()
    {
        var signal = UniqueSignal();
        var family = KeyedCatchModel(signal, requiredNumber: true);
        var familyId = await CreateAsync(family);
        var marker = Guid.NewGuid().ToString("N")[..10];
        var businessKey = "BK-MARKER-" + marker;
        await StartAsync(familyId, OrderNumber(businessKey));
        await StartAsync(familyId, OrderNumber("ORD-" + marker));
        var thrower = "thrower-marker-" + marker;
        var role = "role-marker-" + marker;
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, family.Id, BroadcastTypes, async revision =>
        {
            var failing = await ThrowAsync(new ThrowSignalRequest(signal, family.Id,
                Payload: JsonSerializer.SerializeToElement(new { version = "PAYLOAD-MARKER-" + marker }),
                BusinessKey: businessKey), thrower, $"admin,{role}");
            await ProcessDeliveryJobsAsync(failing.Id);
            string description;
            await using (var db = fixture.CreateDbContext())
            {
                description = await db.SignalDeliveries.Where(item => item.BroadcastId == failing.Id)
                    .Select(item => item.ResultDescription!).SingleAsync();
            }
            Assert.False(string.IsNullOrEmpty(description));

            var cancelled = await ThrowAsync(new ThrowSignalRequest(signal, family.Id, Payload: Version(1)), thrower, $"admin,{role}");
            using (var cancel = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{cancelled.Id}/cancel",
                       new CancelSignalBroadcastRequest("REASON-MARKER-" + marker), thrower, $"admin,{role}"))
            {
                Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            }

            var events = await BroadcastEventsAsync(revision);
            Assert.Equal(LifecycleEventTypes.SignalBroadcastCompletedWithIssues,
                Assert.Single(events, item => item.SourceId == failing.Id).Type);
            Assert.Equal(LifecycleEventTypes.SignalBroadcastCancelled,
                Assert.Single(events, item => item.SourceId == cancelled.Id).Type);
            foreach (var captured in events.Where(item => item.SourceId == failing.Id || item.SourceId == cancelled.Id))
            {
                foreach (var text in new[] { marker, businessKey, thrower, role, description, "PAYLOAD-MARKER", "REASON-MARKER" })
                {
                    Assert.DoesNotContain(text, captured.Body, StringComparison.Ordinal);
                }
            }
        });
    }

    // Models

    private static string UniqueSignal() => $"test.events-{Guid.NewGuid():N}";

    private static JsonElement Version(int value) => JsonSerializer.SerializeToElement(new { version = value });

    private static Dictionary<string, JsonElement> OrderNumber(string value) => new()
    {
        ["orderNumber"] = JsonSerializer.SerializeToElement(value)
    };

    private static WorkflowModel WithKey(WorkflowModel model, string key)
    {
        model.Id = key;
        return model;
    }

    /// <summary>
    /// A family with active business keys: start #1 with input orderNumber leads to signal catch #2, task #3, and
    /// end #4. With <paramref name="requiredNumber"/> the catch maps the required number version.
    /// </summary>
    private static WorkflowModel KeyedCatchModel(string signal, bool requiredNumber = false) => new()
    {
        Id = $"signal-events-keyed-{Guid.NewGuid():N}",
        Name = "Signal events keyed",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent,
                Variables = [new VariableModel { Id = 11, Name = "orderNumber", DataType = WorkflowVariableTypes.String, Required = true }],
                BusinessKey = new BusinessKeyModel { Variable = "orderNumber", Uniqueness = BusinessKeyUniqueness.Active }
            },
            new FlowNodeModel
            {
                Id = 2, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
                Signal = new SignalEventModel
                {
                    Name = signal,
                    OutputMappings = requiredNumber
                        ? new List<MessageOutputMappingModel>
                        {
                            new()
                            {
                                Variable = "version", Path = "version", DataType = WorkflowVariableTypes.Number,
                                IsArray = false, Required = true
                            }
                        }
                        : new List<MessageOutputMappingModel>()
                }
            },
            new FlowNodeModel { Id = 3, Name = "React", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4, Name = "Close" }
        ]
    };

    /// <summary>Signal start #1 with the required string mapping recallNumber, task #2, and end #3.</summary>
    private static WorkflowModel StartModel(string signal, string? key = null) => new()
    {
        Id = key ?? $"signal-events-start-{Guid.NewGuid():N}",
        Name = "Signal events start",
        InitialEventId = null,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1, Name = "Started", Type = BpmnFlowNodeTypes.SignalStartEvent,
                Signal = new SignalEventModel
                {
                    Name = signal,
                    OutputMappings =
                    [
                        new MessageOutputMappingModel
                        {
                            Variable = "recallNumber", Path = "recallNumber", DataType = WorkflowVariableTypes.String,
                            IsArray = false, Required = true
                        }
                    ]
                }
            },
            new FlowNodeModel { Id = 2, Name = "Write the notice", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Done" }
        ]
    };

    /// <summary>
    /// Start #1, task #2 whose action 201 leads to the throw: intermediate throw #3 then end #4, or signal end #3.
    /// </summary>
    private static WorkflowModel ThrowerModel(string signal, SignalThrowScopeModel scope, bool endEvent)
    {
        var model = new WorkflowModel
        {
            Id = $"signal-events-thrower-{Guid.NewGuid():N}",
            Name = "Signal events thrower",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Decide", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Announce",
                    Type = endEvent ? BpmnFlowNodeTypes.SignalEndEvent : BpmnFlowNodeTypes.IntermediateSignalThrowEvent,
                    Signal = new SignalEventModel { Name = signal, Scope = scope, Payload = [] }
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Announce" }
            ]
        };
        if (!endEvent)
        {
            model.FlowNodes.Add(new FlowNodeModel { Id = 4, Name = "Announced", Type = BpmnFlowNodeTypes.EndEvent });
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 });
        }
        return model;
    }

    // Event assertions

    private async Task<CapturedLifecycleEvent[]> BroadcastEventsAsync(long revision, long? broadcastId = null) =>
    [
        .. (await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision))
            .Where(item => item.SourceTable == "signal_broadcasts" && (broadcastId is null || item.SourceId == broadcastId))
    ];

    /// <summary>
    /// The golden-body checks of one broadcast event: catalog order and nothing else, the typed body, the envelope
    /// rules, counters that add up to the total, and the 3 KiB bound.
    /// </summary>
    private static LifecycleEventEnvelopeDto<SignalBroadcastEventDataDto> AssertEvent(CapturedLifecycleEvent captured, string type)
    {
        Assert.Equal(type, captured.Type);
        LifecycleEventBodies.AssertMatchesCatalog(captured);
        var body = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<SignalBroadcastEventDataDto>>(captured.Body);
        Assert.Equal(type, "signalBroadcast." + body.Data.Status);
        Assert.Equal((null, null), (body.WorkflowDefinitionId, body.WorkflowVersion));
        Assert.Equal((captured.SourceId, captured.WorkflowKey, captured.InstanceId),
            (body.Data.BroadcastId, body.WorkflowKey, body.InstanceId));
        Assert.Empty(captured.RecipientUserIds);
        Assert.Equal(body.Data.TotalDeliveryCount,
            body.Data.DeliveredCount + body.Data.SkippedCount + body.Data.FailedCount + body.Data.CancelledCount);
        Assert.InRange(body.Data.StartCount, 0, body.Data.TotalDeliveryCount);
        Assert.Contains(body.Data.Scope, new[] { "workflow", "businessKey", "instance", "allWorkflows" });
        Assert.Equal(body.Data.ThrownByKind == "instance", body.Data.ThrownByInstanceId is not null);
        Assert.True(Encoding.UTF8.GetByteCount(captured.Body) < 3 * 1024);
        return body;
    }

    private static void AssertMatchesRow(LifecycleEventEnvelopeDto<SignalBroadcastEventDataDto> body, SignalBroadcastEntity row)
    {
        Assert.Equal(
            (row.Id, row.SignalName, row.ThrownByKind, row.ThrowerInstanceId, row.Status, row.TotalDeliveryCount,
             row.StartDeliveryCount, row.DeliveredCount, row.SkippedCount, row.FailedCount, row.CancelledCount),
            (body.Data.BroadcastId, body.Data.SignalName, body.Data.ThrownByKind, body.Data.ThrownByInstanceId,
             body.Data.Status, body.Data.TotalDeliveryCount, body.Data.StartCount, body.Data.DeliveredCount,
             body.Data.SkippedCount, body.Data.FailedCount, body.Data.CancelledCount));
        Assert.Equal((row.WorkflowKey, row.InstanceId), (body.WorkflowKey, body.InstanceId));
    }
}
