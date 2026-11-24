extern alias FlowbitWorker;

using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using SignalBroadcastExpiryService = FlowbitWorker::Flowbit.Worker.SignalBroadcastExpiryService;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;

namespace Flowbit.Tests;

/// <summary>
/// EVENT-04E: a broadcast thrown with expiresAfter expires at a time the database computes, and from then on no
/// delivery acts. Each delivery checks the expiry after its locks; one statement under the broadcast's row lock
/// settles the rest, from a delivery job, from the Worker's sweep, or in the reconciliation of a lost lease; the
/// broadcast finishes as expired. The database clock cannot be faked, so most cases move a broadcast's ExpiresAt
/// into the past with raw SQL, as the lease helpers backdate due times; two use a real expiry of one second.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class SignalExpiryTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset LongExpiredLease = new(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => Task.CompletedTask;

    // Some cases leave a broadcast delivering on purpose; later classes must not lease its jobs.
    public Task DisposeAsync() => SignalDeliveryJobLeftovers.RetireAsync(fixture);

    // Throw

    [Fact]
    public async Task Throw_StoresTheDatabaseTimePlusTheLength()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));

        var before = await DatabaseTimeAsync();
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT10M"));
        var after = await DatabaseTimeAsync();
        Assert.NotNull(broadcast.ExpiresAt);
        Assert.InRange(broadcast.ExpiresAt!.Value, before.AddMinutes(10), after.AddMinutes(10));
        Assert.Equal(0, broadcast.ExpiredCount);
        Assert.Equal(broadcast.ExpiresAt, (await GetBroadcastAsync(broadcast.Id)).ExpiresAt);
        Assert.Equal(broadcast.ExpiresAt, (await BroadcastRowAsync(broadcast.Id)).ExpiresAt);

        // The list carries the members too.
        using (var list = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts?name={signal}"))
        {
            var page = (await list.Content.ReadFromJsonAsync<PagedResult<SignalBroadcastSummaryDto>>(JsonOptions))!;
            Assert.Equal(broadcast.ExpiresAt, Assert.Single(page.Items).ExpiresAt);
        }

        // Without expiresAfter nothing expires, as before.
        var never = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1)));
        Assert.Null(never.ExpiresAt);
        Assert.Null((await BroadcastRowAsync(never.Id)).ExpiresAt);
        await CancelAsync(broadcast.Id);
        await CancelAsync(never.Id);
    }

    [Theory]
    [InlineData("", "expiresAfter cannot be blank; omit it for a signal that does not expire.")]
    [InlineData("   ", "expiresAfter cannot be blank; omit it for a signal that does not expire.")]
    [InlineData("P1M", "expiresAfter must be a fixed ISO-8601 duration such as PT10M or P1D.")]
    [InlineData("10m", "expiresAfter must be a fixed ISO-8601 duration such as PT10M or P1D.")]
    [InlineData("PT0.5S", "expiresAfter must be between 1 second and 365 days.")]
    [InlineData("P366D", "expiresAfter must be between 1 second and 365 days.")]
    [InlineData("P9999999999999999D", "expiresAfter must be between 1 second and 365 days.")]
    public async Task Throw_RefusesAnInvalidExpiryWithItsMessage(string expiresAfter, string message)
    {
        var signal = UniqueSignal();
        using var response = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
            new ThrowSignalRequest(signal, AllWorkflows: true, ExpiresAfter: expiresAfter));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.SignalName == signal));
    }

    [Fact]
    public async Task Throw_RefusesANumberAsABindingError()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/signal-broadcasts")
        {
            Content = new StringContent(
                $$"""{"name":"{{UniqueSignal()}}","allWorkflows":true,"expiresAfter":600}""",
                Encoding.UTF8,
                "application/json")
        };
        ApiTestAuth.Authorize(request, "signal-expiry", ["admin"]);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Contains("expiresAfter", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throw_WithoutRecipientsCompletesAndStaysCompletedAfterItsExpiry()
    {
        var broadcast = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), AllWorkflows: true, ExpiresAfter: "PT1S"));
        Assert.Equal((SignalBroadcastStatuses.Completed, 0), (broadcast.Status, broadcast.TotalDeliveryCount));
        Assert.NotNull(broadcast.ExpiresAt);
        await MoveExpiryIntoThePastAsync(broadcast.Id);
        await SweepUntilNoneAsync();
        Assert.Equal(SignalBroadcastStatuses.Completed, (await GetBroadcastAsync(broadcast.Id)).Status);
    }

    [Fact]
    public async Task Throw_FromAWorkflowStoresNoExpiry()
    {
        var signal = UniqueSignal();
        var thrower = new WorkflowModel
        {
            Id = $"signal-expiry-thrower-{Guid.NewGuid():N}",
            Name = "Signal expiry thrower",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Announce",
                    Type = BpmnFlowNodeTypes.SignalEndEvent,
                    Signal = new SignalEventModel
                    {
                        Name = signal,
                        Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows },
                        Payload = []
                    }
                }
            ],
            SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
        };
        var instance = await StartAsync(await CreateAsync(thrower));
        await using var db = fixture.CreateDbContext();
        var row = await db.SignalBroadcasts.AsNoTracking().SingleAsync(item => item.ThrowerInstanceId == instance.Id);
        Assert.Null(row.ExpiresAt);
    }

    [Fact]
    public async Task ManagedClient_SetsAnExpiryAndReadsItBack()
    {
        var clientId = $"signal-expiry-client-{Guid.NewGuid():N}"[..40];
        var secret = await CreateClientAsync(clientId);
        using var thrown = await SendAsClientAsync(HttpMethod.Post, "/api/signal-broadcasts", clientId, secret,
            new ThrowSignalRequest(UniqueSignal(), AllWorkflows: true, ExpiresAfter: "P1D"));
        Assert.Equal(HttpStatusCode.Accepted, thrown.StatusCode);
        var broadcast = (await thrown.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
        Assert.NotNull(broadcast.ExpiresAt);
        using var read = await SendAsClientAsync(HttpMethod.Get, $"/api/signal-broadcasts/{broadcast.Id}", clientId, secret);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(broadcast.ExpiresAt,
            (await read.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!.ExpiresAt);
    }

    // Idempotency

    [Fact]
    public async Task Idempotency_ReplaysTheSameLengthAndRefusesAnotherOrANewExpiry()
    {
        var signal = UniqueSignal();
        var key = $"expiry-{Guid.NewGuid():N}";
        var first = await ThrowAsync(new ThrowSignalRequest(signal, AllWorkflows: true, IdempotencyKey: key, ExpiresAfter: "PT1M"));

        // PT60S is the same length as PT1M.
        using (var replay = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                   new ThrowSignalRequest(signal, AllWorkflows: true, IdempotencyKey: key, ExpiresAfter: "PT60S")))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var replayed = (await replay.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
            Assert.Equal((first.Id, first.ExpiresAt), (replayed.Id, replayed.ExpiresAt));
        }

        foreach (var changed in new string?[] { "PT2M", null })
        {
            using var conflict = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                new ThrowSignalRequest(signal, AllWorkflows: true, IdempotencyKey: key, ExpiresAfter: changed));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        // An expiry where the first request had none is another request too.
        var plainKey = $"expiry-plain-{Guid.NewGuid():N}";
        await ThrowAsync(new ThrowSignalRequest(signal, AllWorkflows: true, IdempotencyKey: plainKey));
        using (var conflict = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                   new ThrowSignalRequest(signal, AllWorkflows: true, IdempotencyKey: plainKey, ExpiresAfter: "PT1M")))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        // A replay after the expiry returns the first expiresAt; it never extends it.
        await MoveExpiryIntoThePastAsync(first.Id);
        var moved = (await BroadcastRowAsync(first.Id)).ExpiresAt;
        using (var late = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                   new ThrowSignalRequest(signal, AllWorkflows: true, IdempotencyKey: key, ExpiresAfter: "PT1M")))
        {
            Assert.Equal(HttpStatusCode.OK, late.StatusCode);
            Assert.Equal(moved, (await late.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!.ExpiresAt);
        }
    }

    [Fact]
    public void Idempotency_HashAddsALineOnlyForAnExpiry()
    {
        var scope = SignalBroadcastScope.AllWorkflows;
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes("policy.changed\n*\nnull")),
            SignalBroadcastService.HashRequest("policy.changed", scope, null, null));
        Assert.Equal(
            SHA256.HashData(Encoding.UTF8.GetBytes($"policy.changed\n*\nnull\nexpiresAfter:{TimeSpan.FromMinutes(1).Ticks}")),
            SignalBroadcastService.HashRequest("policy.changed", scope, null, TimeSpan.FromMinutes(1)));
        Assert.Equal(
            SignalBroadcastService.HashRequest("a.b", SignalBroadcastScope.Family("x"), null, TimeSpan.FromSeconds(60)),
            SignalBroadcastService.HashRequest("a.b", SignalBroadcastScope.Family("x"), null, TimeSpan.FromMinutes(1)));
    }

    // Settle

    [Fact]
    public async Task SettleByTheJob_LocksNoInstanceAndWritesNothingToThem()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflowId = await CreateAsync(model);
        var instances = new[] { await StartAsync(workflowId), await StartAsync(workflowId), await StartAsync(workflowId) };
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(4), ExpiresAfter: "PT1H"));
        Assert.Equal(3, broadcast.TotalDeliveryCount);
        await MoveExpiryIntoThePastAsync(broadcast.Id);
        var historyBefore = await HistoryCountAsync(instances.Select(item => item.Id).ToArray());

        // Another transaction holds every recipient's instance, as transitions do.
        await using var holder = fixture.CreateDbContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        foreach (var instance in instances)
        {
            await holder.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM flowbit.workflow_instances WHERE \"Id\" = {instance.Id} FOR UPDATE");
        }

        var processing = ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.Same(processing, await Task.WhenAny(processing, Task.Delay(TimeSpan.FromSeconds(20))));
        await processing;
        await held.RollbackAsync();

        Assert.All((await DeliveriesAsync(broadcast.Id)).Items, delivery =>
        {
            Assert.Equal((SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.BroadcastExpired, 0),
                (delivery.Status, delivery.ResultCode, delivery.AttemptCount));
            Assert.Null(delivery.ResultDescription);
            Assert.NotNull(delivery.CompletedAt);
        });
        foreach (var instance in instances)
        {
            Assert.Equal(SignalSubscriptionStatuses.Active, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);
            Assert.False(await HasVariableAsync(instance.Id, "policyVersion"));
        }
        Assert.Equal(historyBefore, await HistoryCountAsync(instances.Select(item => item.Id).ToArray()));
        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Expired, 3, 3, 0, 0), (finished.Status, finished.ExpiredCount,
            finished.SkippedCount, finished.PendingCount, finished.DeliveredCount));
        Assert.NotNull(finished.CompletedAt);
        // The deliveries filter shows the expired delivery of one instance.
        using (var filtered = await SendAsync(HttpMethod.Get,
                   $"/api/signal-broadcasts/{broadcast.Id}/deliveries?instanceId={instances[1].Id}"))
        {
            var page = (await filtered.Content.ReadFromJsonAsync<PagedResult<SignalDeliveryDto>>(JsonOptions))!;
            Assert.Equal(SignalDeliveryResultCodes.BroadcastExpired, Assert.Single(page.Items).ResultCode);
        }
        using (var list = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts?name={signal}&status=expired"))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var page = (await list.Content.ReadFromJsonAsync<PagedResult<SignalBroadcastSummaryDto>>(JsonOptions))!;
            Assert.Equal((broadcast.Id, 3), (Assert.Single(page.Items).Id, page.Items[0].ExpiredCount));
        }
    }

    [Fact]
    public async Task SettleByTheJob_AfterAnUnexpectedFailureCompletesTheJobWithoutARetry()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflowId = await CreateAsync(model);
        var instances = new[] { await StartAsync(workflowId), await StartAsync(workflowId) };
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(5), ExpiresAfter: "PT1H"));
        var jobId = Assert.Single(await DeliveryJobsAsync(broadcast.Id));
        var deliveryIds = (await DeliveriesAsync(broadcast.Id)).Items.Select(item => item.Id).Order().ToArray();
        Assert.Equal(2, deliveryIds.Length);

        // The first delivery fails unexpectedly while the expiry passes; the second one's own check
        // then finds it passed, and the job settles the broadcast, which ends the first one too.
        var lease = await LeaseAsync(jobId);
        Assert.True(lease.AttemptNumber < lease.Job.MaxAttempts);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var processor = new SignalBroadcastJobProcessor(
                new FailingDeliveryScopeFactory(
                    fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                    deliveryIds[0],
                    () => MoveExpiryIntoThePastAsync(broadcast.Id)),
                scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>(),
                TimeProvider.System,
                NullLogger<SignalBroadcastJobProcessor>.Instance);
            await processor.ProcessAsync(lease, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(WorkflowJobStatuses.Completed,
                await db.WorkflowJobs.Where(job => job.Id == jobId).Select(job => job.Status).SingleAsync());
            var attempt = Assert.Single(await db.WorkflowJobAttempts.AsNoTracking()
                .Where(item => item.JobId == jobId).ToListAsync());
            Assert.Equal((WorkflowJobAttemptStatuses.Completed, null), (attempt.Status, attempt.FailureCode));
        }
        Assert.All((await DeliveriesAsync(broadcast.Id)).Items, delivery =>
            Assert.Equal((SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.BroadcastExpired, 0),
                (delivery.Status, delivery.ResultCode, delivery.AttemptCount)));
        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Expired, 2, 0), (finished.Status, finished.ExpiredCount, finished.PendingCount));
        foreach (var instance in instances)
        {
            Assert.Equal(SignalSubscriptionStatuses.Active, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);
        }
    }

    [Fact]
    public async Task CheckAfterTheLocks_ADeliveryThatWaitedForItsInstanceDoesNotActAfterTheExpiry()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var instance = await StartAsync(await CreateAsync(model));
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(4), ExpiresAfter: "PT1H"));
        var historyBefore = await HistoryCountAsync([instance.Id]);

        await using var holder = fixture.CreateDbContext();
        var held = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM flowbit.workflow_instances WHERE \"Id\" = {instance.Id} FOR UPDATE");
        var processing = ProcessDeliveryJobsAsync(broadcast.Id);
        await WaitForLockWaitAsync(processing, "workflow_instances");

        // The expiry passes while the delivery waits.
        await MoveExpiryIntoThePastAsync(broadcast.Id);
        await held.RollbackAsync();
        await held.DisposeAsync();
        await processing;

        var delivery = Assert.Single((await DeliveriesAsync(broadcast.Id)).Items);
        Assert.Equal((SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.BroadcastExpired, 0),
            (delivery.Status, delivery.ResultCode, delivery.AttemptCount));
        Assert.Equal(SignalSubscriptionStatuses.Active, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);
        Assert.False(await HasVariableAsync(instance.Id, "policyVersion"));
        Assert.Equal(historyBefore, await HistoryCountAsync([instance.Id]));
        Assert.Equal(SignalBroadcastStatuses.Expired, (await GetBroadcastAsync(broadcast.Id)).Status);
    }

    [Fact]
    public async Task CheckAfterTheLocks_AStartThatWaitedForADefaultChangeCreatesNoInstance()
    {
        var signal = UniqueSignal();
        var model = StartModel(signal);
        var workflowId = await CreateAsync(model);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        Assert.Equal(1, broadcast.StartCount);

        // An operation that changes the family's default holds its exclusive lock.
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var held = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(hashtext('workflow-family'), hashtext(@key))", connection, held))
        {
            command.Parameters.AddWithValue("key", model.Id);
            await command.ExecuteNonQueryAsync();
        }
        var processing = ProcessDeliveryJobsAsync(broadcast.Id);
        await WaitForLockWaitAsync(processing, "pg_advisory_xact_lock_shared");

        await MoveExpiryIntoThePastAsync(broadcast.Id);
        await held.RollbackAsync();
        await held.DisposeAsync();
        await processing;

        var delivery = Assert.Single((await DeliveriesAsync(broadcast.Id)).Items);
        Assert.Equal((SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.BroadcastExpired, SignalDeliveryKinds.Start),
            (delivery.Status, delivery.ResultCode, delivery.Kind));
        Assert.Null(delivery.InstanceId);
        Assert.Equal(workflowId, delivery.WorkflowDefinitionId);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowInstances.AnyAsync(item => item.WorkflowKey == model.Id));
        Assert.Equal(SignalBroadcastStatuses.Expired, (await GetBroadcastAsync(broadcast.Id)).Status);
    }

    [Fact]
    public async Task PartlyDelivered_FinishesExpiredWithTheDeliveredOneKept()
    {
        var signal = UniqueSignal();
        var first = SignalRuntimeTests.CatchModel(signal);
        var second = SignalRuntimeTests.CatchModel(signal);
        var delivered = await StartAsync(await CreateAsync(first));
        var waiting = await StartAsync(await CreateAsync(second));
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, AllWorkflows: true, Payload: Version(2), ExpiresAfter: "PT1H"));
        var jobs = await DeliveryJobsAsync(broadcast.Id);
        Assert.Equal(2, jobs.Count);
        var (deliveredJob, waitingJob) = await JobsByInstanceAsync(broadcast.Id, delivered.Id);

        await HoldJobAsync(waitingJob);
        await ProcessJobsAsync([deliveredJob]);
        Assert.Equal(SignalBroadcastStatuses.Delivering, (await GetBroadcastAsync(broadcast.Id)).Status);

        await MoveExpiryIntoThePastAsync(broadcast.Id);
        await ProcessJobsAsync([waitingJob]);

        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Expired, 1, 1, 1), (finished.Status, finished.DeliveredCount,
            finished.SkippedCount, finished.ExpiredCount));
        Assert.Equal(3, await ActiveTaskNodeAsync(delivered.Id));
        Assert.Equal(SignalSubscriptionStatuses.Active, Assert.Single(await SubscriptionsAsync(waiting.Id)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Boundary_AnExpiredDeliveryLeavesTheHostActiveAndTheBoundarySubscribed(bool cancelActivity)
    {
        var signal = UniqueSignal();
        var instance = await StartAsync(await CreateAsync(SignalRuntimeTests.BoundaryModel(signal, cancelActivity)));
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, AllWorkflows: true,
            Payload: JsonSerializer.SerializeToElement(new { reason = "Supplier recall" }), ExpiresAfter: "PT1H"));
        await MoveExpiryIntoThePastAsync(broadcast.Id);
        await ProcessDeliveryJobsAsync(broadcast.Id);

        Assert.Equal(SignalDeliveryResultCodes.BroadcastExpired, Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).ResultCode);
        Assert.Equal(2, await ActiveTaskNodeAsync(instance.Id));
        var subscription = Assert.Single(await SubscriptionsAsync(instance.Id));
        Assert.Equal((SignalSubscriptionStatuses.Active, 0L), (subscription.Status, subscription.Occurrence));
        Assert.False(await HasVariableAsync(instance.Id, "recallReason"));
    }

    // Settle races

    [Fact]
    public async Task SettleRace_WaitsForADeliveryInItsTransactionAndLeavesItAsItFinished()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflowId = await CreateAsync(model);
        await StartAsync(workflowId);
        await StartAsync(workflowId);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        var deliveryIds = (await DeliveriesAsync(broadcast.Id)).Items.Select(item => item.Id).Order().ToArray();
        await HoldAllDeliveryJobsAsync(broadcast.Id);

        // A delivery that passed its check before the expiry and has not committed yet.
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var delivering = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("""
            UPDATE flowbit.signal_deliveries
            SET "Status" = 'delivered', "ResultCode" = 'caught', "AttemptCount" = 1,
                "CompletedAt" = now(), "UpdatedAt" = now()
            WHERE "Id" = @id
            """, connection, delivering))
        {
            command.Parameters.AddWithValue("id", deliveryIds[0]);
            await command.ExecuteNonQueryAsync();
        }

        await MoveExpiryIntoThePastAsync(broadcast.Id);
        var settling = ExpireAsync(broadcast.Id);
        await WaitForLockWaitAsync(settling, "signal_deliveries");
        await delivering.CommitAsync();
        await delivering.DisposeAsync();
        var result = await settling;

        Assert.NotNull(result);
        Assert.Equal((1, SignalBroadcastStatuses.Expired), (result!.ExpiredDeliveries, result.Status));
        var deliveries = (await DeliveriesAsync(broadcast.Id)).Items.ToDictionary(item => item.Id);
        Assert.Equal(SignalDeliveryStatuses.Delivered, deliveries[deliveryIds[0]].Status);
        Assert.Equal(SignalDeliveryResultCodes.BroadcastExpired, deliveries[deliveryIds[1]].ResultCode);
        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((1, 1, 1), (finished.DeliveredCount, finished.SkippedCount, finished.ExpiredCount));
    }

    [Fact]
    public async Task Settle_TwiceChangesNothingTheSecondTimeAndBeforeTheExpiryChangesNothing()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        await HoldAllDeliveryJobsAsync(broadcast.Id);

        // Before the expiry: no delivery ends and the row is not written.
        var early = await ExpireAsync(broadcast.Id);
        Assert.Equal((0, SignalBroadcastStatuses.Delivering), (early!.ExpiredDeliveries, early.Status));
        var untouched = await BroadcastRowAsync(broadcast.Id);
        Assert.Equal(broadcast.UpdatedAt, untouched.UpdatedAt);
        Assert.Equal(SignalDeliveryStatuses.Pending, Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).Status);

        await MoveExpiryIntoThePastAsync(broadcast.Id);
        var settled = await ExpireAsync(broadcast.Id);
        Assert.Equal((1, SignalBroadcastStatuses.Expired), (settled!.ExpiredDeliveries, settled.Status));
        var expired = await BroadcastRowAsync(broadcast.Id);

        Assert.Null(await ExpireAsync(broadcast.Id));
        Assert.Equal(expired.UpdatedAt, (await BroadcastRowAsync(broadcast.Id)).UpdatedAt);
    }

    [Fact]
    public async Task Cancel_BeforeASettleWinsAndAfterItReturnsConflict()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));

        // A delivering broadcast past its expiry can still be cancelled; a settle then does nothing.
        var cancelledFirst = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        await HoldAllDeliveryJobsAsync(cancelledFirst.Id);
        await MoveExpiryIntoThePastAsync(cancelledFirst.Id);
        await CancelAsync(cancelledFirst.Id);
        Assert.Null(await ExpireAsync(cancelledFirst.Id));
        var cancelled = await GetBroadcastAsync(cancelledFirst.Id);
        Assert.Equal((SignalBroadcastStatuses.Cancelled, 1, 0), (cancelled.Status, cancelled.CancelledCount, cancelled.ExpiredCount));

        // After a settle the broadcast is finished, and a cancel returns 409.
        var settledFirst = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        await HoldAllDeliveryJobsAsync(settledFirst.Id);
        await MoveExpiryIntoThePastAsync(settledFirst.Id);
        await ExpireAsync(settledFirst.Id);
        using var refused = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{settledFirst.Id}/cancel", new { });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(SignalBroadcastStatuses.Expired, (await GetBroadcastAsync(settledFirst.Id)).Status);
    }

    [Fact]
    public async Task CancelRacingASettle_TheFirstToHoldTheRowDecides()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        await HoldAllDeliveryJobsAsync(broadcast.Id);
        await MoveExpiryIntoThePastAsync(broadcast.Id);

        // Both wait behind a holder of the broadcast's row, then race when it is released.
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var held = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
                         "SELECT 1 FROM flowbit.signal_broadcasts WHERE \"Id\" = @id FOR UPDATE", connection, held))
        {
            command.Parameters.AddWithValue("id", broadcast.Id);
            await command.ExecuteNonQueryAsync();
        }
        var settling = ExpireAsync(broadcast.Id);
        var cancelling = SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/cancel", new { });
        await WaitForLockWaitAsync(settling, "signal_broadcasts");
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await held.RollbackAsync();
        await held.DisposeAsync();

        var settled = await settling;
        using var cancel = await cancelling;
        var finished = await GetBroadcastAsync(broadcast.Id);
        if (finished.Status == SignalBroadcastStatuses.Expired)
        {
            Assert.Equal(1, settled!.ExpiredDeliveries);
            Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
            Assert.Equal((1, 0), (finished.ExpiredCount, finished.CancelledCount));
        }
        else
        {
            Assert.Equal(SignalBroadcastStatuses.Cancelled, finished.Status);
            Assert.Null(settled);
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            Assert.Equal((0, 1), (finished.ExpiredCount, finished.CancelledCount));
        }
    }

    // Sweep

    [Fact]
    public async Task Sweep_SettlesABroadcastWhoseJobWaitsInAnIncident()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        var job = Assert.Single(await DeliveryJobsAsync(broadcast.Id));
        await PutInIncidentAsync(job);
        await MoveExpiryIntoThePastAsync(broadcast.Id);

        Assert.Contains(broadcast.Id, await SweepUntilNoneAsync());
        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Expired, 1), (finished.Status, finished.ExpiredCount));

        // The job's incident stays open; retrying it runs a job that finds nothing pending.
        var incident = await OpenIncidentAsync(job);
        Assert.NotNull(incident);
        using (var retry = await SendAsync(HttpMethod.Post, $"/api/incidents/{incident!.Id}/retry"))
        {
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
        }
        await ProcessJobsAsync([job]);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowJobStatuses.Completed, await db.WorkflowJobs.Where(item => item.Id == job)
            .Select(item => item.Status).SingleAsync());
        Assert.Equal(SignalBroadcastStatuses.Expired, (await GetBroadcastAsync(broadcast.Id)).Status);
    }

    [Fact]
    public async Task Sweep_SkipsABroadcastAnotherTransactionHoldsWithoutWaiting()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        await HoldAllDeliveryJobsAsync(broadcast.Id);
        await MoveExpiryIntoThePastAsync(broadcast.Id);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var held = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
                         "SELECT 1 FROM flowbit.signal_broadcasts WHERE \"Id\" = @id FOR UPDATE", connection, held))
        {
            command.Parameters.AddWithValue("id", broadcast.Id);
            await command.ExecuteNonQueryAsync();
        }

        var sweeping = SweepUntilNoneAsync();
        Assert.Same(sweeping, await Task.WhenAny(sweeping, Task.Delay(TimeSpan.FromSeconds(15))));
        Assert.DoesNotContain(broadcast.Id, await sweeping);
        Assert.Equal(SignalBroadcastStatuses.Delivering, (await BroadcastRowAsync(broadcast.Id)).Status);

        await held.RollbackAsync();
        await held.DisposeAsync();
        Assert.Contains(broadcast.Id, await SweepUntilNoneAsync());
        Assert.Equal(SignalBroadcastStatuses.Expired, (await BroadcastRowAsync(broadcast.Id)).Status);
    }

    [Fact]
    public async Task Sweep_ThroughTheHostedServiceFinishesARealOneSecondExpiry()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var instance = await StartAsync(await CreateAsync(model));
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1S"));
        // The dispatcher leaves the delivery job alone: it is due an hour from now.
        await using (var db = fixture.CreateDbContext())
        {
            var jobs = await DeliveryJobsAsync(broadcast.Id);
            await db.WorkflowJobs.Where(job => jobs.Contains(job.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddHours(1)));
        }

        var service = new SignalBroadcastExpiryService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new WorkerOptions { SignalExpirySweepSeconds = 1 },
            NullLogger<SignalBroadcastExpiryService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if ((await BroadcastRowAsync(broadcast.Id)).Status == SignalBroadcastStatuses.Expired)
                {
                    break;
                }
                await Task.Delay(200);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Expired, 1), (finished.Status, finished.ExpiredCount));
        Assert.Equal(SignalSubscriptionStatuses.Active, Assert.Single(await SubscriptionsAsync(instance.Id)).Status);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    public void WorkerOptions_BoundTheSweepInterval(int seconds, bool valid)
    {
        var options = new WorkerOptions { SignalExpirySweepSeconds = seconds };
        if (valid)
        {
            options.Validate();
        }
        else
        {
            var refused = Assert.Throws<InvalidOperationException>(options.Validate);
            Assert.Contains("SignalExpirySweepSeconds", refused.Message, StringComparison.Ordinal);
        }
        Assert.Equal(5, new WorkerOptions().SignalExpirySweepSeconds);
    }

    // Lost lease

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostLease_EndsTheDeliveriesAsExpiredOnlyAfterTheExpiry(bool expired)
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        var job = Assert.Single(await DeliveryJobsAsync(broadcast.Id));
        await ExhaustLeaseAsync(job);
        if (expired)
        {
            await MoveExpiryIntoThePastAsync(broadcast.Id);
        }

        await OpenExhaustedLeaseIncidentsAsync(job);

        var delivery = Assert.Single((await DeliveriesAsync(broadcast.Id)).Items);
        var finished = await GetBroadcastAsync(broadcast.Id);
        if (expired)
        {
            Assert.Equal((SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.BroadcastExpired, 0, (string?)null),
                (delivery.Status, delivery.ResultCode, delivery.AttemptCount, delivery.ResultDescription));
            Assert.Equal((SignalBroadcastStatuses.Expired, 1, 0), (finished.Status, finished.ExpiredCount, finished.FailedCount));
        }
        else
        {
            Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.LeaseExhausted, 1),
                (delivery.Status, delivery.ResultCode, delivery.AttemptCount));
            Assert.Equal((SignalBroadcastStatuses.CompletedWithIssues, 0, 1), (finished.Status, finished.ExpiredCount, finished.FailedCount));
        }
        Assert.NotNull(await OpenIncidentAsync(job));
    }

    // Retry

    [Fact]
    public async Task Retry_AfterTheExpiryReturnsConflictAndChangesNothing()
    {
        var broadcast = await FailedBroadcastAsync("PT1H");
        await MoveExpiryIntoThePastAsync(broadcast.Id);
        var before = await BroadcastRowAsync(broadcast.Id);

        using var refused = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
            new RetrySignalDeliveriesRequest(before.UpdatedAt));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = await refused.Content.ReadAsStringAsync();
        Assert.Contains($"Signal broadcast #{broadcast.Id} expired at ", message, StringComparison.Ordinal);
        Assert.Contains("its failed deliveries cannot be retried. Throw the signal again to reach the waits that are still open.",
            message, StringComparison.Ordinal);

        var after = await BroadcastRowAsync(broadcast.Id);
        Assert.Equal((before.Status, before.UpdatedAt, before.FailedCount), (after.Status, after.UpdatedAt, after.FailedCount));
        Assert.Equal(SignalDeliveryStatuses.Failed, Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).Status);
    }

    [Fact]
    public async Task Retry_BeforeTheExpiryKeepsItAndRetriedDeliveriesThatRunLateExpire()
    {
        var broadcast = await FailedBroadcastAsync("PT1H");
        using (var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
                   new RetrySignalDeliveriesRequest(broadcast.UpdatedAt)))
        {
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            var reopened = (await retry.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
            Assert.Equal((SignalBroadcastStatuses.Delivering, broadcast.ExpiresAt), (reopened.Status, reopened.ExpiresAt));
        }

        await MoveExpiryIntoThePastAsync(broadcast.Id);
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Expired, 0, 1), (finished.Status, finished.FailedCount, finished.ExpiredCount));

        // An expired broadcast cannot be retried either.
        using var refused = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
            new RetrySignalDeliveriesRequest(finished.UpdatedAt));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("is expired", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // Limits

    [Fact]
    public async Task OpenBroadcastLimit_LeavesOutABroadcastPastItsExpiry()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        var open = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        await HoldAllDeliveryJobsAsync(open.Id);
        int openCount;
        await using (var db = fixture.CreateDbContext())
        {
            openCount = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM flowbit.signal_broadcasts
                WHERE "Status" = 'delivering' AND "ThrownByKind" IN ('user', 'client')
                  AND ("ExpiresAt" IS NULL OR "ExpiresAt" > now())
                """).SingleAsync();
        }

        var previous = await SetEngineSettingAsync("Signals", "MaxOpenBroadcasts", openCount.ToString());
        try
        {
            using (var refused = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                       new ThrowSignalRequest(UniqueSignal(), AllWorkflows: true)))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Contains("still delivering", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // Past its expiry and not settled, the broadcast no longer counts, also while no Worker runs.
            await MoveExpiryIntoThePastAsync(open.Id);
            Assert.Equal(SignalBroadcastStatuses.Delivering, (await BroadcastRowAsync(open.Id)).Status);
            await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), AllWorkflows: true));
        }
        finally
        {
            await SetEngineSettingAsync("Signals", "MaxOpenBroadcasts", previous);
            await CancelAsync(open.Id);
        }
    }

    // Status and checks

    [Fact]
    public async Task Status_ExpiredWinsOverFailedAndBothRecountsAgree()
    {
        var signal = UniqueSignal();
        var failing = await StartAsync(await CreateAsync(SignalRuntimeTests.CatchModel(signal)));
        await StartAsync(await CreateAsync(SignalRuntimeTests.CatchModel(signal)));
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, AllWorkflows: true,
            Payload: JsonSerializer.SerializeToElement(new { version = "bad" }), ExpiresAfter: "PT1H"));
        var (failingJob, otherJob) = await JobsByInstanceAsync(broadcast.Id, failing.Id);
        await HoldJobAsync(otherJob);
        await ProcessJobsAsync([failingJob]);
        var partial = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Delivering, 1), (partial.Status, partial.FailedCount));

        // The other delivery ends by its own check, and no recount has run since.
        await MoveExpiryIntoThePastAsync(broadcast.Id);
        var pending = (await DeliveriesAsync(broadcast.Id)).Items.Single(item => item.Status == SignalDeliveryStatuses.Pending);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            Assert.True(await scope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>()
                .ExpireDeliveryAsync(pending.Id, CancellationToken.None));
            await transaction.CommitAsync(CancellationToken.None);
        }
        Assert.Equal(SignalBroadcastStatuses.Delivering, (await BroadcastRowAsync(broadcast.Id)).Status);

        // RefreshCountsAsync, in a transaction that is rolled back.
        SignalBroadcastRecord? refreshed;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var broadcasts = scope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            await broadcasts.GetAsync(broadcast.Id, forUpdate: true, CancellationToken.None);
            refreshed = await broadcasts.RefreshCountsAsync(broadcast.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        Assert.Equal(SignalBroadcastStatuses.Delivering, (await BroadcastRowAsync(broadcast.Id)).Status);

        // The lost-lease reconciliation, from the same delivery rows.
        await ExhaustLeaseAsync(otherJob);
        await OpenExhaustedLeaseIncidentsAsync(otherJob);
        var reconciled = await BroadcastRowAsync(broadcast.Id);

        Assert.Equal(
            (SignalBroadcastStatuses.Expired, 0, 1, 1, 0, 1),
            (refreshed!.Status, refreshed.DeliveredCount, refreshed.SkippedCount, refreshed.FailedCount,
             refreshed.CancelledCount, refreshed.ExpiredCount));
        Assert.Equal(
            (refreshed.Status, refreshed.DeliveredCount, refreshed.SkippedCount, refreshed.FailedCount,
             refreshed.CancelledCount, refreshed.ExpiredCount),
            (reconciled.Status, reconciled.DeliveredCount, reconciled.SkippedCount, reconciled.FailedCount,
             reconciled.CancelledCount, reconciled.ExpiredCount));
    }

    [Fact]
    public async Task Checks_RefuseExpiredCountsAndStatusesThatCannotHappen()
    {
        var withExpiry = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), AllWorkflows: true, ExpiresAfter: "PT1H"));
        var without = await ThrowAsync(new ThrowSignalRequest(UniqueSignal(), AllWorkflows: true));

        await AssertRefusedAsync(
            "UPDATE flowbit.signal_broadcasts SET \"ExpiredCount\" = \"SkippedCount\" + 1 WHERE \"Id\" = @id", withExpiry.Id);
        await AssertRefusedAsync(
            "UPDATE flowbit.signal_broadcasts SET \"ExpiredCount\" = -1 WHERE \"Id\" = @id", withExpiry.Id);
        // The status expired without an expired delivery, and without an expiry.
        await AssertRefusedAsync(
            "UPDATE flowbit.signal_broadcasts SET \"Status\" = 'expired' WHERE \"Id\" = @id", withExpiry.Id);
        await AssertRefusedAsync(
            "UPDATE flowbit.signal_broadcasts SET \"Status\" = 'expired', \"SkippedCount\" = 0, \"ExpiredCount\" = 0 WHERE \"Id\" = @id",
            without.Id);
        // An expired delivery on a broadcast without an expiry.
        await AssertRefusedAsync(
            "UPDATE flowbit.signal_broadcasts SET \"TotalDeliveryCount\" = 1, \"SkippedCount\" = 1, \"ExpiredCount\" = 1 WHERE \"Id\" = @id",
            without.Id);

        async Task AssertRefusedAsync(string sql, long id)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", id);
            var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal((PostgresErrorCodes.CheckViolation, "CK_signal_broadcasts_expiry"), (refused.SqlState, refused.ConstraintName));
        }
    }

    // Retention and deletion

    [Fact]
    public async Task Retention_RemovesAnOldExpiredBroadcastAndKeepsOneNotYetSettled()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        var expired = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        await HoldAllDeliveryJobsAsync(expired.Id);
        await MoveExpiryIntoThePastAsync(expired.Id);
        await ExpireAsync(expired.Id);
        var unsettled = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        await HoldAllDeliveryJobsAsync(unsettled.Id);
        await MoveExpiryIntoThePastAsync(unsettled.Id);
        await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
            UPDATE flowbit.signal_broadcasts
            SET "CreatedAt" = now() - interval '10 days',
                "CompletedAt" = CASE WHEN "CompletedAt" IS NULL THEN NULL ELSE now() - interval '10 days' END
            WHERE "Id" = ANY(@ids)
            """, ("ids", new[] { expired.Id, unsettled.Id }));

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
                        WorkerId = "signal-expiry-retention", BatchSize = 100, MaxRunnableJobs = 1_000_000, MaxQueueLagSeconds = 3600
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
            Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.Id == expired.Id));
            Assert.False(await db.SignalDeliveries.AnyAsync(item => item.BroadcastId == expired.Id));
            Assert.True(await db.SignalBroadcasts.AnyAsync(item => item.Id == unsettled.Id));
            Assert.Equal(1, await db.SignalDeliveries.CountAsync(item => item.BroadcastId == unsettled.Id));
        }
        await ExpireAsync(unsettled.Id);
    }

    [Fact]
    public async Task VersionDeletion_IsRefusedWhileAnUnsettledExpiredDeliveryIsPendingAndAllowedAfterTheSettle()
    {
        var signal = UniqueSignal();
        var model = StartModel(signal);
        var workflowId = await CreateAsync(model);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        await HoldAllDeliveryJobsAsync(broadcast.Id);
        await MoveExpiryIntoThePastAsync(broadcast.Id);

        using (var refused = await SendAsync(HttpMethod.Delete, $"/api/workflows/{workflowId}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        await ExpireAsync(broadcast.Id);
        Assert.Equal(SignalBroadcastStatuses.Expired, (await GetBroadcastAsync(broadcast.Id)).Status);
        using (var deleted = await SendAsync(HttpMethod.Delete, $"/api/workflows/{workflowId}"))
        {
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        }
    }

    // Telemetry

    [Fact]
    public async Task Telemetry_ASettleCountsTheDeliveriesItEnded()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflowId = await CreateAsync(model);
        await StartAsync(workflowId);
        await StartAsync(workflowId);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1), ExpiresAfter: "PT1H"));
        await HoldAllDeliveryJobsAsync(broadcast.Id);
        await MoveExpiryIntoThePastAsync(broadcast.Id);

        long counted = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Flowbit.Runtime.Signals" && instrument.Name == "flowbit.signals.deliveries")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var status = tags.ToArray().FirstOrDefault(tag => tag.Key == "status").Value as string;
            var code = tags.ToArray().FirstOrDefault(tag => tag.Key == "result_code").Value as string;
            if (status == SignalDeliveryStatuses.Skipped && code == SignalDeliveryResultCodes.BroadcastExpired)
            {
                Interlocked.Add(ref counted, value);
            }
        });
        listener.Start();

        var result = await ExpireAsync(broadcast.Id);
        Assert.Equal(2, result!.ExpiredDeliveries);
        Assert.Equal(2, Interlocked.Read(ref counted));
    }

    // Migration

    [Fact]
    public async Task Migration_DownIsRefusedWhileABroadcastHasAnExpiryAndUpRestoresIt()
    {
        const string expiryMigration = "20261123194548_AddSignalBroadcastExpiry";
        const string previousMigration = "20261123120000_AddSignalBroadcastLifecycleEvents";
        var databaseName = "signal_expiry_migration_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
        await ExecuteOnAsync(adminBuilder.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        try
        {
            await MigrateToAsync(connectionString, expiryMigration);
            await ExecuteOnAsync(connectionString, """
                INSERT INTO flowbit.signal_broadcasts
                    ("SignalName", "Status", "TotalDeliveryCount", "DeliveredCount", "SkippedCount", "FailedCount",
                     "CancelledCount", "RecipientsJson", "RecipientsTruncated", "ThrownByKind", "ThrownBy",
                     "ThrownByRolesJson", "ExpiresAt")
                VALUES ('migration.signal', 'delivering', 0, 0, 0, 0, 0, '[]', false, 'user', 'tests', '[]',
                        now() + interval '1 hour')
                """);
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(connectionString, previousMigration));
            Assert.Contains("Signal broadcasts with an expiry exist", refused.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, await ScalarCountAsync(connectionString,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'signal_broadcasts' AND column_name = 'ExpiresAt'"));

            await ExecuteOnAsync(connectionString, "DELETE FROM flowbit.signal_broadcasts");
            await MigrateToAsync(connectionString, previousMigration);
            Assert.Equal(0, await ScalarCountAsync(connectionString,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'signal_broadcasts' AND column_name IN ('ExpiresAt', 'ExpiredCount')"));
            Assert.Equal(0, await ScalarCountAsync(connectionString,
                "SELECT count(*) FROM pg_catalog.pg_proc WHERE proname = 'capture_signal_broadcast_lifecycle' AND prosrc LIKE '%expired%'"));
            Assert.Equal(1, await ScalarCountAsync(connectionString,
                "SELECT count(*) FROM pg_catalog.pg_proc WHERE proname = 'capture_signal_broadcast_lifecycle'"));

            await MigrateToAsync(connectionString, expiryMigration);
            Assert.Equal(2, await ScalarCountAsync(connectionString,
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'signal_broadcasts' AND column_name IN ('ExpiresAt', 'ExpiredCount')"));
            Assert.Equal(1, await ScalarCountAsync(connectionString,
                "SELECT count(*) FROM pg_catalog.pg_proc WHERE proname = 'capture_signal_broadcast_lifecycle' AND prosrc LIKE '%signalBroadcast.expired%'"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnAsync(adminBuilder.ConnectionString, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
        }
    }

    // Helpers

    private static string UniqueSignal() => $"test.expiry-{Guid.NewGuid():N}";

    private static JsonElement Version(int value) => JsonSerializer.SerializeToElement(new { version = value });

    /// <summary>A signal start #1 on the signal that maps version into a number, task #2, and end #3.</summary>
    internal static WorkflowModel StartModel(string signal) => new()
    {
        Id = $"signal-expiry-start-{Guid.NewGuid():N}",
        Name = "Signal expiry start",
        InitialEventId = null,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Policy changed",
                Type = BpmnFlowNodeTypes.SignalStartEvent,
                Signal = new SignalEventModel
                {
                    Name = signal,
                    OutputMappings =
                    [
                        new MessageOutputMappingModel
                        {
                            Variable = "policyVersion",
                            Path = "version",
                            DataType = WorkflowVariableTypes.Number,
                            IsArray = false,
                            Required = true
                        }
                    ]
                }
            },
            new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Done" }
        ]
    };

    /// <summary>A broadcast to one wait whose payload fails the mapping, so it finishes with issues.</summary>
    private async Task<SignalBroadcastDetailDto> FailedBroadcastAsync(string expiresAfter)
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartAsync(await CreateAsync(model));
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id,
            Payload: JsonSerializer.SerializeToElement(new { version = "bad" }), ExpiresAfter: expiresAfter));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var failed = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.CompletedWithIssues, 1), (failed.Status, failed.FailedCount));
        return failed;
    }

    /// <summary>Moves a broadcast's expiry a second into the past by the database's clock.</summary>
    private Task MoveExpiryIntoThePastAsync(long broadcastId) =>
        WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
            UPDATE flowbit.signal_broadcasts
            SET "ExpiresAt" = clock_timestamp() - interval '1 second'
            WHERE "Id" = @id
            """, ("id", broadcastId));

    private async Task<DateTimeOffset> DatabaseTimeAsync()
    {
        await using var db = fixture.CreateDbContext();
        return await db.Database.SqlQuery<DateTimeOffset>($"""SELECT clock_timestamp() AS "Value" """).SingleAsync();
    }

    private async Task<SignalExpiryResult?> ExpireAsync(long broadcastId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISignalBroadcastExpirer>()
            .ExpireAsync(broadcastId, CancellationToken.None);
    }

    /// <summary>Runs the sweep's claims until none is due, and returns the broadcasts they settled.</summary>
    private async Task<List<long>> SweepUntilNoneAsync()
    {
        var settled = new List<long>();
        var databaseNow = await DatabaseTimeAsync();
        for (var pass = 0; pass < 500; pass++)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ISignalBroadcastExpirer>()
                .ExpireNextDueAsync(databaseNow, CancellationToken.None);
            if (result is null)
            {
                return settled;
            }
            settled.Add(result.BroadcastId);
        }

        Assert.Fail("The expiry sweep did not run out of due broadcasts.");
        return settled;
    }

    /// <summary>Waits until another session is blocked on a lock in a statement whose text contains the marker.</summary>
    private async Task WaitForLockWaitAsync(Task work, string marker)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (work.IsCompleted)
            {
                await work;
                Assert.Fail($"The work finished without waiting for a lock in a statement on {marker}.");
            }

            await using var db = fixture.CreateDbContext();
            var pattern = $"%{marker}%";
            var waiting = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM pg_stat_activity
                WHERE wait_event_type = 'Lock'
                  AND query LIKE {pattern}
                  AND pid <> pg_backend_pid()
                """).SingleAsync();
            if (waiting > 0)
            {
                return;
            }
            await Task.Delay(100);
        }

        Assert.Fail($"The work never waited for a lock in a statement on {marker}.");
    }

    private async Task<long> HistoryCountAsync(long[] instanceIds)
    {
        await using var db = fixture.CreateDbContext();
        return await db.InstanceHistory.LongCountAsync(item => instanceIds.Contains(item.InstanceId));
    }

    private async Task<bool> HasVariableAsync(long instanceId, string name)
    {
        await using var db = fixture.CreateDbContext();
        return await db.InstanceVariables.AnyAsync(item => item.InstanceId == instanceId && item.VariableName == name);
    }

    private async Task<int> ActiveTaskNodeAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.Where(task => task.InstanceId == instanceId && task.Status == UserTaskStatuses.Active)
            .Select(task => task.NodeId).SingleAsync();
    }

    private async Task<List<SignalSubscriptionEntity>> SubscriptionsAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SignalSubscriptions.AsNoTracking()
            .Where(item => item.InstanceId == instanceId).OrderBy(item => item.Id).ToListAsync();
    }

    private async Task<SignalBroadcastEntity> BroadcastRowAsync(long id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SignalBroadcasts.AsNoTracking().SingleAsync(item => item.Id == id);
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

    /// <summary>The job that delivers to the instance's version, and the broadcast's other job.</summary>
    private async Task<(long InstanceJob, long OtherJob)> JobsByInstanceAsync(long broadcastId, long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        var definitionId = await db.WorkflowInstances.Where(item => item.Id == instanceId)
            .Select(item => item.WorkflowDefinitionId).SingleAsync();
        var links = await db.SignalBroadcastJobs.AsNoTracking()
            .Where(link => link.BroadcastId == broadcastId && link.JobId != null).ToListAsync();
        return (links.Single(link => link.WorkflowDefinitionId == definitionId).JobId!.Value,
            links.Single(link => link.WorkflowDefinitionId != definitionId).JobId!.Value);
    }

    /// <summary>Keeps a queued job from being leased until a later ProcessJobsAsync makes it due.</summary>
    private async Task HoldJobAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        await db.WorkflowJobs.Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddYears(10)));
    }

    private async Task HoldAllDeliveryJobsAsync(long broadcastId)
    {
        foreach (var job in await DeliveryJobsAsync(broadcastId))
        {
            await HoldJobAsync(job);
        }
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
                new WorkflowJobLeaseRequest($"signal-expiry-test-{Guid.NewGuid():N}", 1, 1, 1, TimeSpan.FromMinutes(2)),
                CancellationToken.None);
            foreach (var lease in leases)
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
                    .ProcessAsync(lease, CancellationToken.None);
            }
        }

        Assert.Fail($"Jobs {string.Join(", ", jobIds)} did not finish.");
    }

    /// <summary>Leases the job and opens an incident on it, as a job that keeps failing ends.</summary>
    private async Task PutInIncidentAsync(long jobId)
    {
        var lease = await LeaseAsync(jobId);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().OpenIncidentAsync(
            new WorkflowJobFence(lease.Job.Id, lease.Job.WorkerId!, lease.LeaseToken, lease.LeaseGeneration),
            "signal_expiry_test",
            "The delivery job keeps failing.",
            null,
            CancellationToken.None);
        Assert.NotNull(await OpenIncidentAsync(jobId));
    }

    /// <summary>
    /// Makes the job due first and leases it. Another class's leftover that is leased first is
    /// processed as the Worker would.
    /// </summary>
    private async Task<WorkflowJobLeaseRecord> LeaseAsync(long jobId)
    {
        await using (var db = fixture.CreateDbContext())
        {
            await db.WorkflowJobs.Where(job => job.Id == jobId).ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Priority, 1_000_000)
                .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var lease = (await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                    new WorkflowJobLeaseRequest($"signal-expiry-lease-{Guid.NewGuid():N}", 1, 1, 1, TimeSpan.FromMinutes(2)),
                    CancellationToken.None))
                .SingleOrDefault();
            if (lease is null)
            {
                continue;
            }
            if (lease.Job.Id != jobId)
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
                continue;
            }

            return lease;
        }

        Assert.Fail($"Job #{jobId} was never leased.");
        return null!;
    }

    /// <summary>
    /// Scopes whose signal delivery executor fails unexpectedly for one delivery, after a step of
    /// the test; every other delivery and service is the application's.
    /// </summary>
    private sealed class FailingDeliveryScopeFactory(
        IServiceScopeFactory inner,
        long failingDeliveryId,
        Func<Task> beforeFailure)
        : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(inner.CreateScope(), failingDeliveryId, beforeFailure);

        private sealed class Scope(IServiceScope inner, long failingDeliveryId, Func<Task> beforeFailure)
            : IServiceScope, IServiceProvider, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) =>
                serviceType == typeof(ISignalDeliveryExecutor)
                    ? new Executor(
                        inner.ServiceProvider.GetRequiredService<ISignalDeliveryExecutor>(),
                        failingDeliveryId,
                        beforeFailure)
                    : inner.ServiceProvider.GetService(serviceType);

            public void Dispose() => inner.Dispose();

            public ValueTask DisposeAsync() =>
                inner is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
        }

        private sealed class Executor(ISignalDeliveryExecutor inner, long failingDeliveryId, Func<Task> beforeFailure)
            : ISignalDeliveryExecutor
        {
            public async Task<SignalDeliveryOutcome> DeliverSignalAsync(long deliveryId, CancellationToken cancellationToken)
            {
                if (deliveryId != failingDeliveryId)
                {
                    return await inner.DeliverSignalAsync(deliveryId, cancellationToken);
                }

                await beforeFailure();
                throw new InvalidOperationException("The test's delivery failed unexpectedly.");
            }
        }
    }

    private async Task<WorkflowIncidentEntity?> OpenIncidentAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowIncidents.AsNoTracking()
            .Where(incident => incident.JobId == jobId && incident.Status == WorkflowIncidentStatuses.Open)
            .SingleOrDefaultAsync();
    }

    /// <summary>Leaves a queued job running on its final attempt with a lease that expired long ago.</summary>
    private async Task ExhaustLeaseAsync(long jobId) =>
        await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
            UPDATE flowbit.workflow_jobs
            SET "Status" = 'running', "AttemptCount" = "MaxAttempts", "WorkerId" = 'signal-expiry-tests',
                "LeaseToken" = gen_random_uuid(), "LeaseGeneration" = "LeaseGeneration" + 1,
                "LeaseExpiresAt" = @expired, "HeartbeatAt" = @expired, "StartedAt" = now(), "UpdatedAt" = now()
            WHERE "Id" = @id
            """, ("expired", LongExpiredLease), ("id", jobId));

    /// <summary>
    /// Leases through the real repository, which first reconciles running jobs whose final attempt lost its lease.
    /// It leases no activity job; each call reconciles the job whose lease expired longest ago, so it repeats until
    /// the given job waits in its incident.
    /// </summary>
    private async Task OpenExhaustedLeaseIncidentsAsync(long jobId)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                    new WorkflowJobLeaseRequest($"signal-expiry-lease:{Guid.NewGuid():N}", 1, 0, 1, TimeSpan.FromMinutes(1)),
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

    private async Task<string> CreateClientAsync(string clientId)
    {
        using var create = await SendAsync(HttpMethod.Post, "/api/shared-variable-clients",
            new CreateSharedVariableClientRequest(clientId, "Signal expiry client",
                [SharedVariableClientScopes.SignalsThrow, SharedVariableClientScopes.SignalsRead]));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return (await create.Content.ReadFromJsonAsync<CreateSharedVariableClientResult>(JsonOptions))!.ClientSecret;
    }

    private async Task<long> CreateAsync(WorkflowModel model)
    {
        using var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return (await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!.Id;
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var start = await SendAsync(HttpMethod.Post, "/api/instances?detail=full", new StartInstanceRequest(workflowId, null, null, null));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        return (await start.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private async Task<SignalBroadcastDetailDto> ThrowAsync(ThrowSignalRequest request)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
    }

    private async Task<SignalBroadcastDetailDto> GetBroadcastAsync(long id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts/{id}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
    }

    private async Task<PagedResult<SignalDeliveryDto>> DeliveriesAsync(long id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts/{id}/deliveries");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResult<SignalDeliveryDto>>(JsonOptions))!;
    }

    private async Task CancelAsync(long id)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{id}/cancel", new { });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
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
        await context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task ExecuteOnAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarCountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, "signal-expiry", ["admin"]);
        return await fixture.Client.SendAsync(request);
    }
}
