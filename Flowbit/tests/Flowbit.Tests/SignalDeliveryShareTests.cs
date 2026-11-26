using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// EVENT-04G: a broadcast with enough waits of one workflow definition gets several delivery
/// jobs, each owning the deliveries whose instance hashes to its share. The tests set
/// Signals.MinDeliveriesPerJob to 2, so eight waits split into four jobs, and restore both split
/// settings afterwards.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed partial class SignalDeliveryShareTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private const string MaxJobsKey = "MaxDeliveryJobsPerDefinition";
    private const string MinDeliveriesKey = "MinDeliveriesPerJob";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset LongExpiredLease = new(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private string? _maxJobs;
    private string? _minDeliveries;

    public async Task InitializeAsync()
    {
        _maxJobs = await SetSettingAsync(MaxJobsKey, "4");
        _minDeliveries = await SetSettingAsync(MinDeliveriesKey, "2");
    }

    public async Task DisposeAsync()
    {
        await SetSettingAsync(MaxJobsKey, _maxJobs);
        await SetSettingAsync(MinDeliveriesKey, _minDeliveries);
        await SignalDeliveryJobLeftovers.RetireAsync(fixture);
    }

    // The throw

    [Fact]
    public async Task Throw_SplitsADefinitionWithEnoughWaitsAndKeepsASmallerOneAsToday()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var v1 = await CreateAsync(model);
        await StartManyAsync(v1.Id, 8);
        var v2 = await NewVersionAsync(v1.Id, model);
        await StartManyAsync(v2.Id, 3);

        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1)));
        Assert.Equal(11, broadcast.TotalDeliveryCount);

        var links = await LinksAsync(broadcast.Id);
        var split = links.Where(item => item.Link.WorkflowDefinitionId == v1.Id).OrderBy(item => item.Link.ShareIndex).ToList();
        Assert.Equal([0, 1, 2, 3], split.Select(item => item.Link.ShareIndex));
        Assert.All(split, item =>
        {
            Assert.Equal((4, SignalDeliveryShares.SharePhase, WorkflowJobKinds.SignalBroadcastDeliver, WorkflowJobClasses.Activity),
                (item.Link.ShareCount, item.Job.Phase, item.Job.Kind, item.Job.QueueClass));
            var payload = item.Job.PayloadJson!.RootElement.Deserialize<SignalBroadcastJobPayload>()!;
            Assert.Equal((broadcast.Id, v1.Id, item.Link.ShareIndex, 4),
                (payload.BroadcastId, payload.WorkflowDefinitionId, payload.Share, payload.Shares));
        });

        var whole = Assert.Single(links, item => item.Link.WorkflowDefinitionId == v2.Id);
        Assert.Equal((0, 1, SignalBroadcastWriter.DeliverPhase), (whole.Link.ShareIndex, whole.Link.ShareCount, whole.Job.Phase));
        Assert.False(whole.Job.PayloadJson!.RootElement.TryGetProperty("ShareIndex", out _));
        Assert.False(whole.Job.PayloadJson!.RootElement.TryGetProperty("ShareCount", out _));

        await ProcessJobsAsync(links.Select(item => item.Job.Id).ToArray());
        await AssertDeliveredOnceAsync(broadcast.Id, 11);
    }

    [Fact]
    public async Task Throw_WithAMaximumOfOneGivesOneUnsplitJob()
    {
        await SetSettingAsync(MaxJobsKey, "1");
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartManyAsync((await CreateAsync(model)).Id, 8);

        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1)));

        var link = Assert.Single(await LinksAsync(broadcast.Id));
        Assert.Equal((0, 1, SignalBroadcastWriter.DeliverPhase), (link.Link.ShareIndex, link.Link.ShareCount, link.Job.Phase));
        await ProcessJobsAsync([link.Job.Id]);
        await AssertDeliveredOnceAsync(broadcast.Id, 8);
    }

    [Fact]
    public async Task Throw_ASettingChangedAfterTheThrowAddsNoJob()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartManyAsync((await CreateAsync(model)).Id, 8);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1)));
        var jobs = (await LinksAsync(broadcast.Id)).Select(item => item.Job.Id).ToArray();
        Assert.Equal(4, jobs.Length);

        await SetSettingAsync(MaxJobsKey, "8");
        await SetSettingAsync(MinDeliveriesKey, "1");
        await ProcessJobsAsync(jobs);

        Assert.Equal(4, (await LinksAsync(broadcast.Id)).Count);
        await AssertDeliveredOnceAsync(broadcast.Id, 8);
    }

    [Fact]
    public async Task WorkflowThrow_SplitsTheSameWay()
    {
        var signal = UniqueSignal();
        var receiver = SignalRuntimeTests.CatchModel(signal);
        await StartManyAsync((await CreateAsync(receiver)).Id, 8);
        var sender = SenderModel(signal, receiver.Id);
        var thrower = await StartOneAsync((await CreateAsync(sender)).Id);

        long broadcastId;
        await using (var db = fixture.CreateDbContext())
        {
            broadcastId = await db.SignalBroadcasts.Where(item => item.ThrowerInstanceId == thrower)
                .Select(item => item.Id).SingleAsync();
        }

        var links = await LinksAsync(broadcastId);
        Assert.Equal([0, 1, 2, 3], links.Select(item => item.Link.ShareIndex).Order());
        Assert.All(links, item => Assert.Equal((4, SignalDeliveryShares.SharePhase), (item.Link.ShareCount, item.Job.Phase)));
        await ProcessJobsAsync(links.Select(item => item.Job.Id).ToArray());
        await AssertDeliveredOnceAsync(broadcastId, 8);
    }

    [Fact]
    public async Task Throw_ToOneWaitRunsNoNewStatementAndToTwoWaitsOfOneDefinitionRunsOne()
    {
        var oneSignal = UniqueSignal();
        var one = SignalRuntimeTests.CatchModel(oneSignal);
        await StartManyAsync((await CreateAsync(one)).Id, 1);
        var twoSignal = UniqueSignal();
        var two = SignalRuntimeTests.CatchModel(twoSignal);
        await StartManyAsync((await CreateAsync(two)).Id, 2);
        // MinDeliveriesPerJob is 2 here, so two waits are one job and only the read differs.
        await SetSettingAsync(MaxJobsKey, "1");

        var (oneTotal, oneSettings, oneBroadcast) = await CountThrowStatementsAsync(new ThrowSignalRequest(oneSignal, one.Id, Payload: Version(1)));
        var (twoTotal, twoSettings, twoBroadcast) = await CountThrowStatementsAsync(new ThrowSignalRequest(twoSignal, two.Id, Payload: Version(1)));

        Assert.Equal(oneTotal + 1, twoTotal);
        Assert.Equal(oneSettings + 1, twoSettings);
        await ProcessJobsAsync((await LinksAsync(oneBroadcast)).Concat(await LinksAsync(twoBroadcast)).Select(item => item.Job.Id).ToArray());
    }

    // The share each delivery belongs to

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShareCondition_SpreadsConsecutiveAndEvenInstanceIdsOverFourShares(int step)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT s.share, count(*)::int
            FROM (SELECT g::bigint AS "InstanceId" FROM generate_series(@step, @step * 10000, @step) AS g) AS d
            CROSS JOIN generate_series(0, @count - 1) AS s(share)
            WHERE {SignalDeliveryShareSql.Condition("d", "s.share", "@count")}
            GROUP BY s.share
            ORDER BY s.share
            """, connection);
        command.Parameters.AddWithValue("step", step);
        command.Parameters.AddWithValue("count", 4);
        var counts = new List<int>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                counts.Add(reader.GetInt32(1));
            }
        }

        Assert.Equal(4, counts.Count);
        Assert.Equal(10_000, counts.Sum());
        Assert.All(counts, count => Assert.InRange(count, 2_000, 3_000));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(32)]
    public async Task ShareCondition_PutsEveryInstanceInExactlyOneShareAndAStartInTheShareOfZero(int shareCount)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT d."InstanceId", count(s.share)::int, min(s.share)
            FROM (SELECT g::bigint AS "InstanceId" FROM generate_series(0, 2000) AS g
                  UNION ALL SELECT NULL::bigint) AS d
            LEFT JOIN generate_series(0, @count - 1) AS s(share)
                ON {SignalDeliveryShareSql.Condition("d", "s.share", "@count")}
            GROUP BY d."InstanceId"
            """, connection);
        command.Parameters.AddWithValue("count", shareCount);
        var rows = 0;
        int? shareOfZero = null;
        int? shareOfStart = null;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows++;
                Assert.Equal(1, reader.GetInt32(1));
                if (reader.IsDBNull(0))
                {
                    shareOfStart = reader.GetInt32(2);
                }
                else if (reader.GetInt64(0) == 0)
                {
                    shareOfZero = reader.GetInt32(2);
                }
            }
        }

        Assert.Equal(2_002, rows);
        // A start delivery has no instance until it is delivered, so it takes the share of 0.
        Assert.NotNull(shareOfStart);
        Assert.Equal(shareOfZero, shareOfStart);
    }

    [Fact]
    public async Task ShareQueries_ListEveryPendingDeliveryOnceAndKeepTheWaitsOfAnInstanceTogether()
    {
        var signal = UniqueSignal();
        var model = TwoCatchModel(signal);
        var workflow = await CreateAsync(model);
        var instances = await StartManyAsync(workflow.Id, 5);
        await HoldNewDeliveryJobsAsync(async () =>
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(1)));
            Assert.Equal(10, broadcast.TotalDeliveryCount);
            var deliveries = await DeliveriesAsync(broadcast.Id);
            var instanceOf = deliveries.ToDictionary(item => item.Id, item => item.InstanceId!.Value);

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>();
            var whole = await repository.ListPendingDeliveryIdsAsync(broadcast.Id, workflow.Id, 0, 1_000, CancellationToken.None);
            Assert.Equal(deliveries.Select(item => item.Id).Order(), whole);

            foreach (var shareCount in new[] { 2, 4, 32 })
            {
                var listed = new List<long>();
                for (var share = 0; share < shareCount; share++)
                {
                    var page = await repository.ListPendingDeliveryIdsAsync(
                        broadcast.Id, workflow.Id, 0, 1_000, CancellationToken.None, share, shareCount);
                    Assert.Equal(page.Order(), page);
                    listed.AddRange(page);
                    // Both waits of an instance are in the share its instance hashes to.
                    foreach (var instance in page.Select(id => instanceOf[id]).Distinct())
                    {
                        Assert.All(deliveries.Where(item => item.InstanceId == instance), item => Assert.Contains(item.Id, page));
                    }
                }

                Assert.Equal(deliveries.Select(item => item.Id).Order(), listed.Order());
            }

            // A cursor pages within one share.
            var first = await repository.ListPendingDeliveryIdsAsync(broadcast.Id, workflow.Id, 0, 1, CancellationToken.None, 0, 2);
            var rest = await repository.ListPendingDeliveryIdsAsync(broadcast.Id, workflow.Id, first.Single(), 1_000, CancellationToken.None, 0, 2);
            Assert.Equal(
                await repository.ListPendingDeliveryIdsAsync(broadcast.Id, workflow.Id, 0, 1_000, CancellationToken.None, 0, 2),
                first.Concat(rest));
            await CancelAsync(broadcast.Id);
        });
        Assert.Equal(5, instances.Length);
    }

    [Fact]
    public async Task ShareQueries_ListAPendingStartInExactlyOneShare()
    {
        var signal = UniqueSignal();
        var model = StartOnSignalModel(signal);
        var workflow = await CreateAsync(model);
        await HoldNewDeliveryJobsAsync(async () =>
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id));
            var start = Assert.Single(await DeliveriesAsync(broadcast.Id));
            Assert.Equal((SignalDeliveryKinds.Start, (long?)null), (start.Kind, start.InstanceId));

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>();
            foreach (var shareCount in new[] { 2, 4, 32 })
            {
                var listed = new List<long>();
                for (var share = 0; share < shareCount; share++)
                {
                    listed.AddRange(await repository.ListPendingDeliveryIdsAsync(
                        broadcast.Id, workflow.Id, 0, 100, CancellationToken.None, share, shareCount));
                }

                Assert.Equal([start.Id], listed);
            }

            await CancelAsync(broadcast.Id);
        });
    }

    // Delivery

    [Fact]
    public async Task FourShareJobsAtOnce_DeliverEveryWaitOnceWithoutADeadlock()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var instances = await StartManyAsync((await CreateAsync(model)).Id, 200);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3)));
        var jobs = (await LinksAsync(broadcast.Id)).Select(item => item.Job.Id).ToArray();
        Assert.Equal(4, jobs.Length);

        var leases = await LeaseAllAsync(jobs);
        await Task.WhenAll(leases.Select(lease => Task.Run(async () =>
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
        })));

        await AssertDeliveredOnceAsync(broadcast.Id, 200);
        await using var db = fixture.CreateDbContext();
        var received = await db.InstanceHistory.AsNoTracking()
            .Where(item => instances.Contains(item.InstanceId) && item.Note == InstanceHistoryNotes.SignalReceived)
            .GroupBy(item => item.InstanceId)
            .Select(group => group.Count())
            .ToListAsync();
        Assert.Equal(200, received.Count);
        Assert.All(received, count => Assert.Equal(1, count));
        // A deadlock would fail a delivery unexpectedly and give its job a second attempt.
        var attempts = await db.WorkflowJobAttempts.AsNoTracking().Where(item => jobs.Contains(item.JobId)).ToListAsync();
        Assert.Equal(4, attempts.Count);
        Assert.All(attempts, attempt => Assert.Equal((WorkflowJobAttemptStatuses.Completed, null), (attempt.Status, attempt.FailureCode)));
        Assert.All(await db.WorkflowJobs.AsNoTracking().Where(job => jobs.Contains(job.Id)).ToListAsync(),
            job => Assert.Equal(WorkflowJobStatuses.Completed, job.Status));
    }

    [Fact]
    public async Task RetryJobOverlappingAnEarlierJob_DeliversEachOnceAndTheLaterWritesNothing()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflow = await CreateAsync(model);
        var instances = await StartManyAsync(workflow.Id, 8);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3)));
        var shareJobs = (await LinksAsync(broadcast.Id)).OrderBy(item => item.Link.ShareIndex).Select(item => item.Job.Id).ToArray();

        // A job for the whole definition beside the four, as a retry enqueues while an earlier
        // job of the definition still waits.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SignalBroadcastWriter>().EnqueueDeliveryJobsAsync(
                broadcast.Id,
                [new SignalRecipientDefinitionRecord(workflow.Id, model.Id, 1)],
                SignalBroadcastJobReasons.Retry,
                "signal-share-tests",
                DateTimeOffset.UtcNow,
                CancellationToken.None));
            await transaction.CommitAsync(CancellationToken.None);
        }

        var whole = (await LinksAsync(broadcast.Id)).Single(item => item.Link.Reason == SignalBroadcastJobReasons.Retry);
        Assert.Equal((0, 1, SignalBroadcastWriter.DeliverPhase), (whole.Link.ShareIndex, whole.Link.ShareCount, whole.Job.Phase));
        var leases = await LeaseAllAsync([whole.Job.Id, shareJobs[0]]);
        await Task.WhenAll(leases.Select(lease => Task.Run(async () =>
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
        })));
        await ProcessJobsAsync(shareJobs);

        await AssertDeliveredOnceAsync(broadcast.Id, 8);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(8, await db.InstanceHistory.CountAsync(item =>
            instances.Contains(item.InstanceId) && item.Note == InstanceHistoryNotes.SignalReceived));
    }

    [Fact]
    public async Task UnexpectedFailureInOneShare_RetriesOnlyItsJobAndOnTheLastAttemptFailsTheDelivery()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflow = await CreateAsync(model);
        await StartManyAsync(workflow.Id, 8);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3)));
        var links = await LinksAsync(broadcast.Id);
        var failing = (await DeliveriesAsync(broadcast.Id)).OrderBy(item => item.Id).First();
        var failingShare = await ShareOfAsync(broadcast.Id, workflow.Id, failing.Id, 4);
        var failingJob = links.Single(item => item.Link.ShareIndex == failingShare).Job.Id;
        var otherJobs = links.Where(item => item.Job.Id != failingJob).Select(item => item.Job.Id).ToArray();

        var lease = (await LeaseAllAsync([failingJob])).Single();
        Assert.True(lease.AttemptNumber < lease.Job.MaxAttempts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessWithFailureAsync(lease, failing.Id));
        await ProcessJobsAsync(otherJobs);

        var midway = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Delivering, 1, 7), (midway.Status, midway.PendingCount, midway.DeliveredCount));
        Assert.Equal(SignalDeliveryStatuses.Pending, (await DeliveriesAsync(broadcast.Id)).Single(item => item.Id == failing.Id).Status);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.All(await db.WorkflowJobs.AsNoTracking().Where(job => otherJobs.Contains(job.Id)).ToListAsync(),
                job => Assert.Equal(WorkflowJobStatuses.Completed, job.Status));
        }

        // As the dispatcher does after the failure, then the last permitted attempt.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().ScheduleRetryAsync(
                Fence(lease), DateTimeOffset.UtcNow, new WorkflowJobResultRecord(null, null, "unhandled_worker_failure", "test"),
                CancellationToken.None));
        }
        await WebhookTestDatabase.ExecuteAsync(fixture.DataSource,
            """UPDATE flowbit.workflow_jobs SET "AttemptCount" = "MaxAttempts" - 1 WHERE "Id" = @id""", ("id", failingJob));
        var last = (await LeaseAllAsync([failingJob])).Single();
        Assert.Equal(last.Job.MaxAttempts, last.AttemptNumber);
        await ProcessWithFailureAsync(last, failing.Id);

        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.CompletedWithIssues, 1, 7), (finished.Status, finished.FailedCount, finished.DeliveredCount));
        Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.DeliveryError),
            (await DeliveriesAsync(broadcast.Id)).Where(item => item.Id == failing.Id).Select(item => (item.Status, item.ResultCode)).Single());
    }

    [Fact]
    public async Task Cancel_WaitsForADeliveryOfEveryShareLeavesThemAsTheyFinishedAndTheJobsComplete()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflow = await CreateAsync(model);
        await StartManyAsync(workflow.Id, 16);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3)));
        var jobs = (await LinksAsync(broadcast.Id)).Select(item => item.Job.Id).ToArray();
        Assert.Equal(4, jobs.Length);
        await HoldJobsAsync(jobs);

        // One delivery of every share that passed its checks and has not committed yet.
        var inFlight = new List<long>();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>();
            for (var share = 0; share < 4; share++)
            {
                var page = await repository.ListPendingDeliveryIdsAsync(broadcast.Id, workflow.Id, 0, 1, CancellationToken.None, share, 4);
                inFlight.AddRange(page);
            }
        }

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var delivering = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("""
            UPDATE flowbit.signal_deliveries
            SET "Status" = 'delivered', "ResultCode" = 'caught', "AttemptCount" = 1, "CompletedAt" = now(), "UpdatedAt" = now()
            WHERE "Id" = ANY(@ids)
            """, connection, delivering))
        {
            command.Parameters.AddWithValue("ids", inFlight.ToArray());
            Assert.Equal(inFlight.Count, await command.ExecuteNonQueryAsync());
        }

        var cancelling = SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/cancel", new CancelSignalBroadcastRequest("Wrong version"));
        await WaitForLockWaitAsync(cancelling, "signal_deliveries");
        await delivering.CommitAsync();
        await delivering.DisposeAsync();
        using (var cancelled = await cancelling)
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        }

        await ProcessJobsAsync(jobs);
        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Cancelled, inFlight.Count, 16 - inFlight.Count, 0),
            (finished.Status, finished.DeliveredCount, finished.CancelledCount, finished.PendingCount));
        var deliveries = await DeliveriesAsync(broadcast.Id);
        Assert.All(deliveries.Where(item => inFlight.Contains(item.Id)), item => Assert.Equal(SignalDeliveryStatuses.Delivered, item.Status));
        await using var db = fixture.CreateDbContext();
        Assert.All(await db.WorkflowJobs.AsNoTracking().Where(job => jobs.Contains(job.Id)).ToListAsync(),
            job => Assert.Equal(WorkflowJobStatuses.Completed, job.Status));
    }

    [Fact]
    public async Task Expiry_WhileShareJobsRunOneSettleEndsTheRestAndRaisesOneEvent()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflow = await CreateAsync(model);
        await StartManyAsync(workflow.Id, 8);
        await WebhookTestDatabase.WithCaptureAsync(fixture.DataSource, model.Id, [.. LifecycleEventTypes.SignalBroadcastTypes], async revision =>
        {
            var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3), ExpiresAfter: "PT1H"));
            var links = (await LinksAsync(broadcast.Id)).OrderBy(item => item.Link.ShareIndex).ToList();
            await ProcessJobsAsync([links[0].Job.Id]);
            var deliveredBefore = (await GetBroadcastAsync(broadcast.Id)).DeliveredCount;

            await MoveExpiryIntoThePastAsync(broadcast.Id);
            var leases = await LeaseAllAsync(links.Skip(1).Select(item => item.Job.Id).ToArray());
            await Task.WhenAll(leases.Select(lease => Task.Run(async () =>
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
            })));

            var finished = await GetBroadcastAsync(broadcast.Id);
            Assert.Equal((SignalBroadcastStatuses.Expired, deliveredBefore, 8 - deliveredBefore, 0),
                (finished.Status, finished.DeliveredCount, finished.ExpiredCount, finished.PendingCount));
            await using (var db = fixture.CreateDbContext())
            {
                var ids = links.Select(item => item.Job.Id).ToArray();
                Assert.All(await db.WorkflowJobs.AsNoTracking().Where(job => ids.Contains(job.Id)).ToListAsync(),
                    job => Assert.Equal(WorkflowJobStatuses.Completed, job.Status));
            }

            var events = (await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision))
                .Where(item => item.SourceTable == "signal_broadcasts" && item.SourceId == broadcast.Id)
                .ToList();
            Assert.Equal(LifecycleEventTypes.SignalBroadcastExpired, Assert.Single(events).Type);
        });
    }

    // A lost lease

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostLease_OfOneShareEndsOnlyThatShareAndTheOthersAreDelivered(bool expired)
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflow = await CreateAsync(model);
        await StartManyAsync(workflow.Id, 16);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3), ExpiresAfter: "PT1H"));
        var links = (await LinksAsync(broadcast.Id)).OrderBy(item => item.Link.ShareIndex).ToList();
        var (lostIndex, lostShare) = await LargestShareAsync(broadcast.Id, workflow.Id, 4);
        var lost = links[lostIndex].Job.Id;

        await HoldJobsAsync(links.Select(item => item.Job.Id).ToArray());
        await ExhaustLeaseAsync(lost);
        if (expired)
        {
            await MoveExpiryIntoThePastAsync(broadcast.Id);
        }
        await OpenExhaustedLeaseIncidentsAsync(lost);

        var deliveries = await DeliveriesAsync(broadcast.Id);
        foreach (var delivery in deliveries)
        {
            if (!lostShare.Contains(delivery.Id))
            {
                Assert.Equal(SignalDeliveryStatuses.Pending, delivery.Status);
            }
            else if (expired)
            {
                Assert.Equal((SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.BroadcastExpired), (delivery.Status, delivery.ResultCode));
            }
            else
            {
                Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.LeaseExhausted), (delivery.Status, delivery.ResultCode));
            }
        }
        Assert.Equal(SignalBroadcastStatuses.Delivering, (await GetBroadcastAsync(broadcast.Id)).Status);
        Assert.NotNull(await OpenIncidentAsync(lost));

        await ProcessJobsAsync(links.Where(item => item.Job.Id != lost).Select(item => item.Job.Id).ToArray());
        var finished = await GetBroadcastAsync(broadcast.Id);
        if (expired)
        {
            Assert.Equal((SignalBroadcastStatuses.Expired, 16, 0), (finished.Status, finished.ExpiredCount, finished.DeliveredCount));
        }
        else
        {
            Assert.Equal((SignalBroadcastStatuses.CompletedWithIssues, lostShare.Count, 16 - lostShare.Count),
                (finished.Status, finished.FailedCount, finished.DeliveredCount));
        }
    }

    [Fact]
    public async Task LostLease_OfAJobForTheWholeDefinitionFailsEveryDeliveryAsToday()
    {
        await SetSettingAsync(MaxJobsKey, "1");
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartManyAsync((await CreateAsync(model)).Id, 8);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3)));
        var job = Assert.Single(await LinksAsync(broadcast.Id)).Job.Id;

        await ExhaustLeaseAsync(job);
        await OpenExhaustedLeaseIncidentsAsync(job);

        Assert.All(await DeliveriesAsync(broadcast.Id), delivery =>
            Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.LeaseExhausted), (delivery.Status, delivery.ResultCode)));
        Assert.Equal((SignalBroadcastStatuses.CompletedWithIssues, 8),
            ((await GetBroadcastAsync(broadcast.Id)).Status, (await GetBroadcastAsync(broadcast.Id)).FailedCount));
    }

    // Retry of failed deliveries and of an incident

    [Fact]
    public async Task RetryFailed_AFewFailuresOfASplitBroadcastBecomeOneJob()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflow = await CreateAsync(model);
        var instances = await StartManyAsync(workflow.Id, 16);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3)));
        var links = (await LinksAsync(broadcast.Id)).OrderBy(item => item.Link.ShareIndex).ToList();
        var lost = links[(await LargestShareAsync(broadcast.Id, workflow.Id, 4)).Share].Job.Id;
        await HoldJobsAsync(links.Select(item => item.Job.Id).ToArray());
        await ExhaustLeaseAsync(lost);
        await OpenExhaustedLeaseIncidentsAsync(lost);
        await ProcessJobsAsync(links.Where(item => item.Job.Id != lost).Select(item => item.Job.Id).ToArray());
        var failed = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal(SignalBroadcastStatuses.CompletedWithIssues, failed.Status);
        Assert.InRange(failed.FailedCount, 1, 15);

        // With the settings of the retry: fewer than two jobs' worth of pending deliveries.
        await SetSettingAsync(MinDeliveriesKey, "100");
        await RetryFailedAsync(broadcast.Id, failed.UpdatedAt);

        var retry = Assert.Single(await LinksAsync(broadcast.Id), item => item.Link.Reason == SignalBroadcastJobReasons.Retry);
        Assert.Equal((0, 1, SignalBroadcastWriter.DeliverPhase), (retry.Link.ShareIndex, retry.Link.ShareCount, retry.Job.Phase));
        await ProcessJobsAsync([retry.Job.Id]);
        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Completed, 16), (finished.Status, finished.DeliveredCount));
        await AssertOneSignalReceivedEachAsync(instances);
    }

    [Fact]
    public async Task RetryFailed_ManyFailuresBecomeShareJobsWithTheSettingsOfThatMoment()
    {
        await SetSettingAsync(MaxJobsKey, "1");
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var instances = await StartManyAsync((await CreateAsync(model)).Id, 8);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3)));
        var job = Assert.Single(await LinksAsync(broadcast.Id)).Job.Id;
        await ExhaustLeaseAsync(job);
        await OpenExhaustedLeaseIncidentsAsync(job);
        var failed = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.CompletedWithIssues, 8), (failed.Status, failed.FailedCount));

        await SetSettingAsync(MaxJobsKey, "4");
        await RetryFailedAsync(broadcast.Id, failed.UpdatedAt);

        var retries = (await LinksAsync(broadcast.Id)).Where(item => item.Link.Reason == SignalBroadcastJobReasons.Retry).ToList();
        Assert.Equal([0, 1, 2, 3], retries.Select(item => item.Link.ShareIndex).Order());
        Assert.All(retries, item => Assert.Equal((4, SignalDeliveryShares.SharePhase), (item.Link.ShareCount, item.Job.Phase)));
        await ProcessJobsAsync(retries.Select(item => item.Job.Id).ToArray());
        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Completed, 8), (finished.Status, finished.DeliveredCount));
        await AssertOneSignalReceivedEachAsync(instances);
    }

    [Fact]
    public async Task IncidentRetry_OfAShareJobRunsThatShareAgainWithItsPhaseAndPayload()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var workflow = await CreateAsync(model);
        await StartManyAsync(workflow.Id, 8);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3)));
        var links = (await LinksAsync(broadcast.Id)).OrderBy(item => item.Link.ShareIndex).ToList();
        var stuckIndex = (await LargestShareAsync(broadcast.Id, workflow.Id, 4)).Share;
        var stuck = links[stuckIndex];
        var lease = (await LeaseAllAsync([stuck.Job.Id])).Single();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().OpenIncidentAsync(
                Fence(lease), "signal_share_test", "The share job keeps failing.", null, CancellationToken.None);
        }
        await ProcessJobsAsync(links.Where(item => item.Job.Id != stuck.Job.Id).Select(item => item.Job.Id).ToArray());
        Assert.Equal(SignalBroadcastStatuses.Delivering, (await GetBroadcastAsync(broadcast.Id)).Status);

        var incident = await OpenIncidentAsync(stuck.Job.Id);
        Assert.NotNull(incident);
        using (var retry = await SendAsync(HttpMethod.Post, $"/api/incidents/{incident!.Id}/retry"))
        {
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
        }

        var again = Assert.Single(await LinksAsync(broadcast.Id), item => item.Job.Id == stuck.Job.Id);
        Assert.Equal(SignalDeliveryShares.SharePhase, again.Job.Phase);
        var payload = again.Job.PayloadJson!.RootElement.Deserialize<SignalBroadcastJobPayload>()!;
        Assert.Equal((stuckIndex, 4), (payload.Share, payload.Shares));
        await ProcessJobsAsync([stuck.Job.Id]);
        await AssertDeliveredOnceAsync(broadcast.Id, 8);
    }

    // The recount

    [Fact]
    public async Task Recount_AShareJobRecountsAtMostEveryTwoSecondsAndAtItsEnd()
    {
        await SetSettingAsync(MaxJobsKey, "2");
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartManyAsync((await CreateAsync(model)).Id, 12);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3)));
        var links = (await LinksAsync(broadcast.Id)).OrderBy(item => item.Link.ShareIndex).ToList();
        Assert.Equal(2, links.Count);

        // The clock never moves: no page is two seconds after the last recount.
        var still = new ManualTime();
        var first = (await LeaseAllAsync([links[0].Job.Id])).Single();
        var stillCount = await CountRecountsAsync(first, still, pageSize: 2);
        Assert.Equal(1, stillCount);

        // Each delivery takes three seconds: every page is two seconds after the last recount.
        var moving = new ManualTime { StepPerTimestamp = TimeSpan.FromSeconds(3) };
        var second = (await LeaseAllAsync([links[1].Job.Id])).Single();
        var deliveriesInSecond = (await DeliveriesAsync(broadcast.Id)).Count(item => item.Status == SignalDeliveryStatuses.Pending);
        var movingCount = await CountRecountsAsync(second, moving, pageSize: 2);
        Assert.Equal((deliveriesInSecond + 1) / 2 + 1, movingCount);

        // The broadcast finished in the last job's final recount.
        var finished = await GetBroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Completed, 12, 0), (finished.Status, finished.DeliveredCount, finished.PendingCount));
    }

    [Fact]
    public async Task Recount_AnUnsplitJobRecountsAfterEveryPage()
    {
        await SetSettingAsync(MaxJobsKey, "1");
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await StartManyAsync((await CreateAsync(model)).Id, 5);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, Payload: Version(3)));
        var lease = (await LeaseAllAsync([Assert.Single(await LinksAsync(broadcast.Id)).Job.Id])).Single();

        // Pages of 2, 2, and 1, each followed by a recount, and the final one.
        Assert.Equal(4, await CountRecountsAsync(lease, new ManualTime(), pageSize: 2));
        Assert.Equal(SignalBroadcastStatuses.Completed, (await GetBroadcastAsync(broadcast.Id)).Status);
    }

    // Helpers

    private static string UniqueSignal() => $"test.share-{Guid.NewGuid():N}";

    private static JsonElement Version(int value) => JsonSerializer.SerializeToElement(new { version = value });

    private static WorkflowJobFence Fence(WorkflowJobLeaseRecord lease) =>
        new(lease.Job.Id, lease.Job.WorkerId!, lease.LeaseToken, lease.LeaseGeneration);

    /// <summary>Start #1, a parallel split #2 into two catches #3 and #4 of one signal, then their own ends.</summary>
    private static WorkflowModel TwoCatchModel(string signal)
    {
        FlowNodeModel Catch(int id) => new()
        {
            Id = id,
            Name = $"Wait {id}",
            Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
            Signal = new SignalEventModel { Name = signal }
        };

        return new WorkflowModel
        {
            Id = $"signal-share-two-{Guid.NewGuid():N}",
            Name = "Two waits for one signal",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway },
                Catch(3),
                Catch(4),
                new FlowNodeModel { Id = 5, Name = "First end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 6, Name = "Second end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 6 }
            ]
        };
    }

    /// <summary>A family that starts on the signal: signal start #1, task #2, end #3.</summary>
    private static WorkflowModel StartOnSignalModel(string signal) => new()
    {
        Id = $"signal-share-start-{Guid.NewGuid():N}",
        Name = "Starts on a signal",
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1, Name = "Signalled", Type = BpmnFlowNodeTypes.SignalStartEvent,
                Signal = new SignalEventModel { Name = signal }
            },
            new FlowNodeModel { Id = 2, Name = "Handle", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Done" }
        ]
    };

    /// <summary>Start #1, then throw #2 of the signal to one family with version from the default of v, then end #3.</summary>
    private static WorkflowModel SenderModel(string signal, string workflowKey) => new()
    {
        Id = $"signal-share-sender-{Guid.NewGuid():N}",
        Name = "Signal share sender",
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1, Name = "v", DataType = WorkflowVariableTypes.Number,
                DefaultValue = JsonSerializer.SerializeToElement(2)
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Send",
                Type = BpmnFlowNodeTypes.IntermediateSignalThrowEvent,
                Signal = new SignalEventModel
                {
                    Name = signal,
                    Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = workflowKey },
                    Payload = [new SignalPayloadEntryModel { Name = "version", Variable = "v" }]
                }
            },
            new FlowNodeModel { Id = 3, Name = "Sent", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
        ]
    };

    /// <summary>Sets a Signals engine setting, or removes it for null, and returns its previous value.</summary>
    private async Task<string?> SetSettingAsync(string key, string? value)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleOrDefaultAsync(item => item.Namespace == "Signals" && item.Key == key);
        var previous = setting?.Value;
        if (value is null)
        {
            if (setting is not null) db.EngineSettings.Remove(setting);
        }
        else if (setting is null)
        {
            db.EngineSettings.Add(new EngineSettingEntity
            {
                Namespace = "Signals", Key = key, Value = value,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            setting.Value = value;
        }
        await db.SaveChangesAsync();
        return previous;
    }

    private async Task<(int Total, int Settings, long BroadcastId)> CountThrowStatementsAsync(ThrowSignalRequest request)
    {
        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        try
        {
            var broadcast = await ThrowAsync(request);
            var statements = fixture.CommandCounter.ReaderCommandTexts.Concat(fixture.CommandCounter.NonQueryCommandTexts).ToArray();
            return (statements.Length, statements.Count(text => text.Contains("engine_settings", StringComparison.Ordinal)), broadcast.Id);
        }
        finally
        {
            fixture.CommandCounter.Reset();
        }
    }

    private async Task<WorkflowDetailDto> CreateAsync(WorkflowModel model)
    {
        using var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return (await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;
    }

    private async Task<WorkflowDetailDto> NewVersionAsync(long workflowId, WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/api/workflows/{workflowId}", new UpdateWorkflowRequest(model, true));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;
    }

    private async Task<long> StartOneAsync(long workflowId)
    {
        using var start = await SendAsync(HttpMethod.Post, "/api/instances?detail=full", new StartInstanceRequest(workflowId, null, null, null));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        return (await start.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!.Id;
    }

    /// <summary>Starts instances eight at a time.</summary>
    private async Task<long[]> StartManyAsync(long workflowId, int count)
    {
        var ids = new long[count];
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(Enumerable.Range(0, count).Select(async index =>
        {
            await gate.WaitAsync();
            try
            {
                ids[index] = await StartOneAsync(workflowId);
            }
            finally
            {
                gate.Release();
            }
        }));
        return ids;
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

    private async Task CancelAsync(long broadcastId)
    {
        using var cancel = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcastId}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
    }

    private async Task RetryFailedAsync(long broadcastId, DateTimeOffset expectedUpdatedAt)
    {
        using var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcastId}/retry-failed",
            new RetrySignalDeliveriesRequest(expectedUpdatedAt));
        Assert.True(retry.StatusCode == HttpStatusCode.OK, await retry.Content.ReadAsStringAsync());
    }

    private async Task<List<SignalDeliveryEntity>> DeliveriesAsync(long broadcastId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SignalDeliveries.AsNoTracking().Where(item => item.BroadcastId == broadcastId).OrderBy(item => item.Id).ToListAsync();
    }

    private async Task<List<(SignalBroadcastJobEntity Link, WorkflowJobEntity Job)>> LinksAsync(long broadcastId)
    {
        await using var db = fixture.CreateDbContext();
        var links = await db.SignalBroadcastJobs.AsNoTracking()
            .Where(link => link.BroadcastId == broadcastId && link.JobId != null)
            .OrderBy(link => link.Id)
            .ToListAsync();
        var ids = links.Select(link => link.JobId!.Value).ToArray();
        var jobs = await db.WorkflowJobs.AsNoTracking().Where(job => ids.Contains(job.Id)).ToDictionaryAsync(job => job.Id);
        return links.Select(link => (link, jobs[link.JobId!.Value])).ToList();
    }

    /// <summary>The share with the most pending deliveries, so a test never picks an empty one.</summary>
    private async Task<(int Share, List<long> Deliveries)> LargestShareAsync(long broadcastId, long workflowId, int shareCount)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>();
        var best = (Share: -1, Deliveries: new List<long>());
        for (var share = 0; share < shareCount; share++)
        {
            var page = await repository.ListPendingDeliveryIdsAsync(
                broadcastId, workflowId, 0, 1_000, CancellationToken.None, share, shareCount);
            if (page.Count > best.Deliveries.Count)
            {
                best = (share, [.. page]);
            }
        }

        Assert.NotEmpty(best.Deliveries);
        return best;
    }

    private async Task<int> ShareOfAsync(long broadcastId, long workflowId, long deliveryId, int shareCount)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>();
        for (var share = 0; share < shareCount; share++)
        {
            if ((await repository.ListPendingDeliveryIdsAsync(broadcastId, workflowId, 0, 1_000, CancellationToken.None, share, shareCount))
                .Contains(deliveryId))
            {
                return share;
            }
        }

        Assert.Fail($"Delivery #{deliveryId} is in no share.");
        return -1;
    }

    private async Task AssertDeliveredOnceAsync(long broadcastId, int count)
    {
        var finished = await GetBroadcastAsync(broadcastId);
        Assert.Equal((SignalBroadcastStatuses.Completed, count, count, 0),
            (finished.Status, finished.TotalDeliveryCount, finished.DeliveredCount, finished.PendingCount));
        var deliveries = await DeliveriesAsync(broadcastId);
        Assert.Equal(count, deliveries.Count);
        Assert.All(deliveries, delivery => Assert.Equal((SignalDeliveryStatuses.Delivered, SignalDeliveryResultCodes.Caught, 1),
            (delivery.Status, delivery.ResultCode, delivery.AttemptCount)));
    }

    private async Task AssertOneSignalReceivedEachAsync(long[] instances)
    {
        await using var db = fixture.CreateDbContext();
        var counts = await db.InstanceHistory.AsNoTracking()
            .Where(item => instances.Contains(item.InstanceId) && item.Note == InstanceHistoryNotes.SignalReceived)
            .GroupBy(item => item.InstanceId)
            .Select(group => group.Count())
            .ToListAsync();
        Assert.Equal(instances.Length, counts.Count);
        Assert.All(counts, count => Assert.Equal(1, count));
    }

    /// <summary>Runs <paramref name="body"/> with the delivery jobs it creates kept from other leases.</summary>
    private async Task HoldNewDeliveryJobsAsync(Func<Task> body)
    {
        try
        {
            await body();
        }
        finally
        {
            await SignalDeliveryJobLeftovers.RetireAsync(fixture);
        }
    }

    /// <summary>Keeps queued jobs from being leased until a later ProcessJobsAsync or LeaseAllAsync makes them due.</summary>
    private async Task HoldJobsAsync(long[] jobIds)
    {
        await using var db = fixture.CreateDbContext();
        await db.WorkflowJobs.Where(job => jobIds.Contains(job.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddYears(10)));
    }

    private async Task MakeDueFirstAsync(IReadOnlyCollection<long> jobIds)
    {
        await using var db = fixture.CreateDbContext();
        await db.WorkflowJobs
            .Where(job => jobIds.Contains(job.Id) && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Priority, 1_000_000)
                .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }

    /// <summary>
    /// Leases every given job, one a round as the fairness key allows for one family, without
    /// processing them. Another class's leftover that is leased first is processed as the Worker would.
    /// </summary>
    private async Task<List<WorkflowJobLeaseRecord>> LeaseAllAsync(IReadOnlyCollection<long> jobIds)
    {
        await MakeDueFirstAsync(jobIds);
        var leases = new List<WorkflowJobLeaseRecord>();
        for (var round = 0; round < 40 && leases.Count < jobIds.Count; round++)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            foreach (var lease in await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                         new WorkflowJobLeaseRequest($"signal-share-test-{Guid.NewGuid():N}", 1, 1, 1, TimeSpan.FromMinutes(2)),
                         CancellationToken.None))
            {
                if (jobIds.Contains(lease.Job.Id))
                {
                    leases.Add(lease);
                }
                else
                {
                    await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
                }
            }
        }

        Assert.Equal(jobIds.Order(), leases.Select(lease => lease.Job.Id).Order());
        return leases;
    }

    /// <summary>Makes the given queued jobs due first and processes leases until none of them is queued.</summary>
    private async Task ProcessJobsAsync(IReadOnlyCollection<long> jobIds)
    {
        for (var round = 0; round < 40; round++)
        {
            await using (var db = fixture.CreateDbContext())
            {
                if (!await db.WorkflowJobs.AnyAsync(job => jobIds.Contains(job.Id)
                        && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry)))
                {
                    return;
                }
            }

            await MakeDueFirstAsync(jobIds);
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            foreach (var lease in await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                         new WorkflowJobLeaseRequest($"signal-share-test-{Guid.NewGuid():N}", 1, 1, 1, TimeSpan.FromMinutes(2)),
                         CancellationToken.None))
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
            }
        }

        Assert.Fail($"Jobs {string.Join(", ", jobIds)} did not finish.");
    }

    /// <summary>Leaves a queued job running on its final attempt with a lease that expired long ago.</summary>
    private Task ExhaustLeaseAsync(long jobId) =>
        WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
            UPDATE flowbit.workflow_jobs
            SET "Status" = 'running', "AttemptCount" = "MaxAttempts", "WorkerId" = 'signal-share-tests',
                "LeaseToken" = gen_random_uuid(), "LeaseGeneration" = "LeaseGeneration" + 1,
                "LeaseExpiresAt" = @expired, "HeartbeatAt" = @expired, "StartedAt" = now(), "UpdatedAt" = now()
            WHERE "Id" = @id
            """, ("expired", LongExpiredLease), ("id", jobId));

    /// <summary>
    /// Leases through the real repository, which first reconciles running jobs whose final attempt lost its lease;
    /// it leases no activity job, and repeats until the given job waits in its incident.
    /// </summary>
    private async Task OpenExhaustedLeaseIncidentsAsync(long jobId)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                    new WorkflowJobLeaseRequest($"signal-share-lease:{Guid.NewGuid():N}", 1, 0, 1, TimeSpan.FromMinutes(1)),
                    CancellationToken.None);
            }
            await using var db = fixture.CreateDbContext();
            if (await db.WorkflowJobs.Where(job => job.Id == jobId).Select(job => job.Status).SingleAsync() == WorkflowJobStatuses.Incident)
            {
                return;
            }
        }

        Assert.Fail($"Job #{jobId} did not reach its lease_exhausted incident.");
    }

    private async Task<WorkflowIncidentEntity?> OpenIncidentAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowIncidents.AsNoTracking()
            .Where(incident => incident.JobId == jobId && incident.Status == WorkflowIncidentStatuses.Open)
            .SingleOrDefaultAsync();
    }

    private Task MoveExpiryIntoThePastAsync(long broadcastId) =>
        WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
            UPDATE flowbit.signal_broadcasts
            SET "ExpiresAt" = clock_timestamp() - interval '1 second'
            WHERE "Id" = @id
            """, ("id", broadcastId));

    /// <summary>Waits until another session is blocked on a lock in a statement that names <paramref name="marker"/>.</summary>
    private async Task WaitForLockWaitAsync(Task work, string marker)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (work.IsCompleted)
            {
                await work;
                Assert.Fail($"The work finished without waiting for a lock on {marker}.");
            }

            await using var db = fixture.CreateDbContext();
            var waiting = await db.Database.SqlQueryRaw<int>("""
                SELECT count(*)::int AS "Value"
                FROM pg_stat_activity
                WHERE wait_event_type = 'Lock' AND query LIKE '%' || {0} || '%' AND pid <> pg_backend_pid()
                """, marker).SingleAsync();
            if (waiting > 0)
            {
                return;
            }
            await Task.Delay(100);
        }

        Assert.Fail($"The work never waited for a lock on {marker}.");
    }

    /// <summary>Processes a lease with deliveries that fail unexpectedly for one delivery; every other one is the application's.</summary>
    private async Task ProcessWithFailureAsync(WorkflowJobLeaseRecord lease, long failingDeliveryId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var processor = new SignalBroadcastJobProcessor(
            new FailingDeliveryScopeFactory(fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(), failingDeliveryId),
            scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>(),
            TimeProvider.System,
            NullLogger<SignalBroadcastJobProcessor>.Instance);
        await processor.ProcessAsync(lease, CancellationToken.None);
    }

    /// <summary>Processes a lease with the given clock and page size, and returns how many recounts it ran.</summary>
    private async Task<int> CountRecountsAsync(WorkflowJobLeaseRecord lease, TimeProvider time, int pageSize)
    {
        var counter = new RecountCounter();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var processor = new SignalBroadcastJobProcessor(
            new CountingScopeFactory(fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(), counter),
            scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>(),
            time,
            NullLogger<SignalBroadcastJobProcessor>.Instance)
        {
            PageSize = pageSize
        };
        await processor.ProcessAsync(lease, CancellationToken.None);
        return counter.Count;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, "signal-share-tests", ["admin"]);
        return await fixture.Client.SendAsync(request);
    }

    /// <summary>A clock that moves forward by <see cref="StepPerTimestamp"/> each time a timestamp is read.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

        public TimeSpan StepPerTimestamp { get; init; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, StepPerTimestamp.Ticks);

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    }

    internal sealed class RecountCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Add() => Interlocked.Increment(ref _count);
    }

    /// <summary>Scopes whose signal broadcast repository counts recounts; everything else is the application's.</summary>
    private sealed class CountingScopeFactory(IServiceScopeFactory inner, RecountCounter counter) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(inner.CreateScope(), counter);

        private sealed class Scope(IServiceScope inner, RecountCounter counter) : IServiceScope, IServiceProvider, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) =>
                serviceType == typeof(ISignalBroadcastRepository)
                    ? RecountCountingProxy.Create(inner.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>(), counter)
                    : inner.ServiceProvider.GetService(serviceType);

            public void Dispose() => inner.Dispose();

            public ValueTask DisposeAsync() =>
                inner is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
        }
    }

    public class RecountCountingProxy : DispatchProxy
    {
        private ISignalBroadcastRepository _inner = null!;
        private RecountCounter _counter = null!;

        internal static ISignalBroadcastRepository Create(ISignalBroadcastRepository inner, RecountCounter counter)
        {
            var proxy = Create<ISignalBroadcastRepository, RecountCountingProxy>();
            var self = (RecountCountingProxy)(object)proxy;
            self._inner = inner;
            self._counter = counter;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ISignalBroadcastRepository.RefreshCountsAsync))
            {
                _counter.Add();
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>Scopes whose signal delivery executor fails unexpectedly for one delivery.</summary>
    private sealed class FailingDeliveryScopeFactory(IServiceScopeFactory inner, long failingDeliveryId) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(inner.CreateScope(), failingDeliveryId);

        private sealed class Scope(IServiceScope inner, long failingDeliveryId) : IServiceScope, IServiceProvider, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) =>
                serviceType == typeof(ISignalDeliveryExecutor)
                    ? new Executor(inner.ServiceProvider.GetRequiredService<ISignalDeliveryExecutor>(), failingDeliveryId)
                    : inner.ServiceProvider.GetService(serviceType);

            public void Dispose() => inner.Dispose();

            public ValueTask DisposeAsync() =>
                inner is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
        }

        private sealed class Executor(ISignalDeliveryExecutor inner, long failingDeliveryId) : ISignalDeliveryExecutor
        {
            public Task<SignalDeliveryOutcome> DeliverSignalAsync(long deliveryId, CancellationToken cancellationToken) =>
                deliveryId == failingDeliveryId
                    ? throw new InvalidOperationException("The test's delivery failed unexpectedly.")
                    : inner.DeliverSignalAsync(deliveryId, cancellationToken);
        }
    }
}
