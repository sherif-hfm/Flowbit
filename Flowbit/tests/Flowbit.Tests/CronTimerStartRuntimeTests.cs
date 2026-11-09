extern alias FlowbitWorker;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using TimerStartReconciliationService = FlowbitWorker::Flowbit.Worker.TimerStartReconciliationService;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

/// <summary>
/// Cron timer starts on PostgreSQL, from default activation through firing, misfires, recovery, and retirement. Clocks
/// are fixed and activation times set directly so every due time is deterministic. They lie in the future: a
/// reconciliation pass schedules every family in the shared database, and a future clock never leaves another test's
/// family with a job that is already due.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class CronTimerStartRuntimeTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ReconciliationSchedulesTheFirstOccurrenceStrictlyAfterTheDefaultActivation()
    {
        var repeatedHour = await PublishAsync("30 1 * * *", "Europe/London");
        await SetActivationAsync(repeatedHour.Id, Instant("2027-10-30T12:00:00Z"));
        // An occurrence at the activation instant itself is excluded.
        var exactMatch = await PublishAsync("30 1 * * *", "Europe/London");
        await SetActivationAsync(exactMatch.Id, Instant("2027-10-31T00:30:00Z"));
        try
        {
            await ReconcileAsync(Instant("2027-10-30T12:00:01Z"));

            var subscription = await SubscriptionAsync(repeatedHour.Key);
            Assert.Equal(TimerScheduleKinds.Cron, subscription.ScheduleKind);
            Assert.Equal("30 1 * * *", subscription.ScheduleExpression);
            Assert.Equal("Europe/London", subscription.ScheduleTimeZone);
            Assert.Null(subscription.ScheduleCalendar);
            Assert.Equal(TimerSubscriptionStatuses.Active, subscription.Status);
            Assert.Equal(0, subscription.Occurrence);
            // 01:30 British Summer Time, the first of the two 01:30s that night.
            Assert.Equal(Instant("2027-10-31T00:30:00Z"), subscription.NextDueAt);
            var job = Assert.Single(await JobsAsync(subscription.Id));
            Assert.Equal(WorkflowJobKinds.TimerStart, job.Kind);
            Assert.Equal(WorkflowJobStatuses.Queued, job.Status);
            Assert.Equal(subscription.NextDueAt, job.ScheduledOccurrenceAt);

            await ReconcileAsync(Instant("2027-10-31T00:30:01Z"));
            Assert.Equal(Instant("2027-11-01T01:30:00Z"), (await SubscriptionAsync(exactMatch.Key)).NextDueAt);
        }
        finally
        {
            await RetireAsync(repeatedHour.Id, exactMatch.Id);
        }
    }

    [Fact]
    public async Task ReconciliationKeepsAFirstOccurrenceAMinuteLateAndSkipsALaterBacklog()
    {
        // A pass schedules every family, so the second is published only after the first pass.
        var oneMinuteLate = await PublishAsync("* * * * *", "Etc/UTC");
        await SetActivationAsync(oneMinuteLate.Id, Instant("2027-01-04T12:00:30Z"));
        (long Id, string Key) longLate = default;
        try
        {
            // The first occurrence, 12:01, is exactly one minute late and still eligible.
            await ReconcileAsync(Instant("2027-01-04T12:02:00Z"));
            Assert.Equal(Instant("2027-01-04T12:01:00Z"), (await SubscriptionAsync(oneMinuteLate.Key)).NextDueAt);

            // A millisecond more and the schedule skips the backlog to the first occurrence after now.
            longLate = await PublishAsync("* * * * *", "Etc/UTC");
            await SetActivationAsync(longLate.Id, Instant("2027-01-04T12:00:30Z"));
            await ReconcileAsync(Instant("2027-01-04T12:02:00.001Z"));
            var skipped = await SubscriptionAsync(longLate.Key);
            Assert.Equal(Instant("2027-01-04T12:03:00Z"), skipped.NextDueAt);
            Assert.Equal(0, skipped.Occurrence);
        }
        finally
        {
            await RetireAsync(longLate.Id == 0 ? [oneMinuteLate.Id] : [oneMinuteLate.Id, longLate.Id]);
        }
    }

    [Fact]
    public async Task FiringStartsOneInstanceAndSchedulesTheSuccessorFromTheNominalTime()
    {
        var workflow = await PublishAsync("30 1 * * *", "Europe/London");
        await SetActivationAsync(workflow.Id, Instant("2027-10-30T12:00:00Z"));
        try
        {
            await ReconcileAsync(Instant("2027-10-30T12:00:01Z"));
            var first = await SubscriptionAsync(workflow.Key);
            var firstJob = Assert.Single(await JobsAsync(first.Id));

            await ProcessAsync(firstJob.Id, Instant("2027-10-31T00:30:40Z"));

            Assert.Equal(1, await InstanceCountAsync(workflow.Key));
            var advanced = await SubscriptionAsync(workflow.Key);
            Assert.Equal(first.Id, advanced.Id);
            Assert.Equal(TimerSubscriptionStatuses.Active, advanced.Status);
            Assert.Equal(1, advanced.Occurrence);
            // The repeated 01:30 that night, in Greenwich Mean Time, is not a second occurrence.
            Assert.Equal(Instant("2027-11-01T01:30:00Z"), advanced.NextDueAt);
            var jobs = await JobsAsync(first.Id);
            Assert.Equal(WorkflowJobStatuses.Completed, jobs.Single(job => job.Id == firstJob.Id).Status);
            var successor = jobs.Single(job => job.Id != firstJob.Id);
            Assert.Equal(WorkflowJobStatuses.Queued, successor.Status);
            Assert.Equal(advanced.NextDueAt, successor.ScheduledOccurrenceAt);
        }
        finally
        {
            await RetireAsync(workflow.Id);
        }
    }

    [Fact]
    public async Task AnOccurrenceExactlyAMinuteLateFiresAndOneMoreLateStartsNothing()
    {
        var workflow = await PublishAsync("* * * * *", "Etc/UTC");
        await SetActivationAsync(workflow.Id, Instant("2027-01-04T12:00:30Z"));
        try
        {
            await ReconcileAsync(Instant("2027-01-04T12:00:31Z"));
            var subscription = await SubscriptionAsync(workflow.Key);
            Assert.Equal(Instant("2027-01-04T12:01:00Z"), subscription.NextDueAt);

            // One minute late: it fires, and the successor follows the nominal time within the grace window.
            await ProcessAsync((await QueuedJobAsync(subscription.Id)).Id, Instant("2027-01-04T12:02:00Z"));
            Assert.Equal(1, await InstanceCountAsync(workflow.Key));
            subscription = await SubscriptionAsync(workflow.Key);
            Assert.Equal((1L, Instant("2027-01-04T12:02:00Z")), (subscription.Occurrence, subscription.NextDueAt));

            // More than one minute late: it starts nothing and skips to the first occurrence after now.
            var misfired = await QueuedJobAsync(subscription.Id);
            await ProcessAsync(misfired.Id, Instant("2027-01-04T12:03:00.001Z"));
            Assert.Equal(1, await InstanceCountAsync(workflow.Key));
            subscription = await SubscriptionAsync(workflow.Key);
            Assert.Equal((2L, Instant("2027-01-04T12:04:00Z")), (subscription.Occurrence, subscription.NextDueAt));
            Assert.Equal(WorkflowJobStatuses.Completed, (await JobAsync(misfired.Id)).Status);
            Assert.Equal(subscription.NextDueAt, (await QueuedJobAsync(subscription.Id)).ScheduledOccurrenceAt);
        }
        finally
        {
            await RetireAsync(workflow.Id);
        }
    }

    [Fact]
    public async Task ASavedDueTimeSurvivesRestartsAndMissingJobRepair()
    {
        var workflow = await PublishAsync("0 9 * * 1-5", "America/New_York");
        await SetActivationAsync(workflow.Id, Instant("2027-01-08T12:00:00Z"));
        try
        {
            await ReconcileAsync(Instant("2027-01-08T12:00:01Z"));
            var saved = await SubscriptionAsync(workflow.Key);
            // Friday 09:00 Eastern Standard Time.
            Assert.Equal(Instant("2027-01-08T14:00:00Z"), saved.NextDueAt);

            // A later pass, as after a restart, neither recalculates the due time nor adds a job.
            await ReconcileAsync(Instant("2027-01-10T00:00:00Z"));
            var again = await SubscriptionAsync(workflow.Key);
            Assert.Equal((saved.Id, saved.NextDueAt, saved.Occurrence), (again.Id, again.NextDueAt, again.Occurrence));
            var job = Assert.Single(await JobsAsync(saved.Id));

            // Without its occurrence job, as after a crash before the job was written, the next pass enqueues the saved
            // due time again.
            await using (var db = fixture.CreateDbContext())
            {
                Assert.Equal(1, await db.WorkflowJobs.Where(row => row.Id == job.Id).ExecuteDeleteAsync());
            }

            await ReconcileAsync(Instant("2027-01-10T00:00:01Z"));
            var repaired = await QueuedJobAsync(saved.Id);
            Assert.NotEqual(job.Id, repaired.Id);
            Assert.Equal(saved.NextDueAt, repaired.ScheduledOccurrenceAt);
            Assert.Equal(saved.NextDueAt, (await SubscriptionAsync(workflow.Key)).NextDueAt);
        }
        finally
        {
            await RetireAsync(workflow.Id);
        }
    }

    [Fact]
    public async Task AZoneTheWorkerCannotResolvePausesTheStartInAnIncidentThatARetryReschedules()
    {
        // A definition saved where the zone resolved, read on a host whose time-zone data lacks it.
        var workflowKey = $"cron-zone-{Guid.NewGuid():N}";
        long workflowId;
        await using (var seed = fixture.CreateDbContext())
        {
            var entity = new WorkflowDefinitionEntity
            {
                Name = workflowKey,
                WorkflowKey = workflowKey,
                Version = 1,
                Definition = Model(workflowKey, "0 8 * * *", "Mars/Olympus"),
                IsPublished = true,
                IsDefault = true,
                DefaultActivationId = Guid.NewGuid(),
                DefaultActivatedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            seed.WorkflowDefinitions.Add(entity);
            await seed.SaveChangesAsync();
            workflowId = entity.Id;
        }

        try
        {
            await ReconcileAsync(DateTimeOffset.UtcNow);
            var paused = await SubscriptionAsync(workflowKey);
            Assert.Equal(TimerSubscriptionStatuses.Paused, paused.Status);
            Assert.Equal((TimerScheduleKinds.Cron, "0 8 * * *", "Mars/Olympus"),
                (paused.ScheduleKind, paused.ScheduleExpression, paused.ScheduleTimeZone));
            var job = Assert.Single(await JobsAsync(paused.Id));
            Assert.Equal(WorkflowJobStatuses.Incident, job.Status);
            Assert.Null(job.ScheduledOccurrenceAt);
            long incidentId;
            await using (var db = fixture.CreateDbContext())
            {
                var incident = await db.WorkflowIncidents.AsNoTracking().SingleAsync(row => row.JobId == job.Id);
                Assert.Equal(WorkflowIncidentTypes.TimerScheduleFailed, incident.Type);
                Assert.Contains("timer.timeZone 'Mars/Olympus' is not an IANA time zone", incident.Details);
                incidentId = incident.Id;
            }

            // Retrying cancels the paused subscription; the next pass tries again and, still unable, opens a new incident.
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                Assert.NotNull(await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>()
                    .RetryIncidentAsync(incidentId, "operator", DateTimeOffset.UtcNow, CancellationToken.None));
            }

            await ProcessAsync(job.Id, DateTimeOffset.UtcNow);
            await ReconcileAsync(DateTimeOffset.UtcNow);
            await using var verification = fixture.CreateDbContext();
            var subscriptions = await verification.TimerSubscriptions.AsNoTracking()
                .Where(row => row.WorkflowKey == workflowKey)
                .OrderBy(row => row.Id)
                .ToListAsync();
            Assert.Equal([TimerSubscriptionStatuses.Cancelled, TimerSubscriptionStatuses.Paused], subscriptions.Select(row => row.Status));
            Assert.All(subscriptions, row => Assert.Equal("Mars/Olympus", row.ScheduleTimeZone));
        }
        finally
        {
            await RetireAsync(workflowId);
        }
    }

    [Fact]
    public async Task ASuccessorThatCannotBeCalculatedRollsBackTheAttemptedStart()
    {
        // The last 31 December the calendar can show has no successor.
        var workflow = await PublishAsync("0 8 31 12 *", "Etc/UTC");
        await SetActivationAsync(workflow.Id, Instant("9999-12-30T00:00:00Z"));
        try
        {
            // A future first occurrence is never late, so the pass can use the real clock.
            await ReconcileAsync(DateTimeOffset.UtcNow);
            var subscription = await SubscriptionAsync(workflow.Key);
            Assert.Equal(Instant("9999-12-31T08:00:00Z"), subscription.NextDueAt);
            var job = Assert.Single(await JobsAsync(subscription.Id));

            await ProcessAsync(job.Id, Instant("9999-12-31T08:00:30Z"));

            Assert.Equal(0, await InstanceCountAsync(workflow.Key));
            var unchanged = await SubscriptionAsync(workflow.Key);
            Assert.Equal(
                (TimerSubscriptionStatuses.Active, 0L, subscription.NextDueAt),
                (unchanged.Status, unchanged.Occurrence, unchanged.NextDueAt));
            var failed = await JobAsync(job.Id);
            Assert.Equal(WorkflowJobStatuses.Retry, failed.Status);
            Assert.Contains("has no occurrence within 36,525 days", failed.LastFailureDescription);
        }
        finally
        {
            await RetireAsync(workflow.Id);
        }
    }

    [Fact]
    public async Task TheDatabaseRefusesToCompleteACronSubscriptionAndTheAttemptedStartRollsBack()
    {
        var workflow = await PublishAsync("0 8 * * *", "Etc/UTC");
        try
        {
            await ReconcileAsync(DateTimeOffset.UtcNow);
            var subscription = await SubscriptionAsync(workflow.Key);

            // A Worker that cannot read cron would start an instance and then complete the subscription.
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Flowbit.Infrastructure.Data.AppDbContext>();
                var subscriptions = scope.ServiceProvider.GetRequiredService<ITimerSubscriptionRepository>();
                await using var transaction = await db.Database.BeginTransactionAsync();
                db.WorkflowInstances.Add(new WorkflowInstanceEntity
                {
                    WorkflowDefinitionId = workflow.Id,
                    WorkflowKey = workflow.Key,
                    Status = WorkflowInstanceStatuses.Running,
                    StartedBy = "timer"
                });
                await db.SaveChangesAsync();
                var error = await Assert.ThrowsAnyAsync<Exception>(() => subscriptions.AdvanceAsync(
                    subscription.Id,
                    subscription.Occurrence,
                    subscription.Occurrence + 1,
                    subscription.NextDueAt,
                    complete: true,
                    CancellationToken.None));
                var refused = error as PostgresException ?? Assert.IsType<PostgresException>(error.InnerException);
                Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
                Assert.Equal("CK_timer_subscriptions_cron_not_completed", refused.ConstraintName);
            }

            Assert.Equal(0, await InstanceCountAsync(workflow.Key));
            var unchanged = await SubscriptionAsync(workflow.Key);
            Assert.Equal((TimerSubscriptionStatuses.Active, subscription.Occurrence), (unchanged.Status, unchanged.Occurrence));

            // Cancellation remains the way a cron schedule ends.
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                Assert.True(await scope.ServiceProvider.GetRequiredService<ITimerSubscriptionRepository>()
                    .CancelByIdAsync(subscription.Id, CancellationToken.None));
            }
        }
        finally
        {
            await RetireAsync(workflow.Id);
        }
    }

    [Theory]
    [InlineData("cron", null, null)]
    [InlineData("cron", " ", null)]
    [InlineData("cron", "Etc/UTC", "examples.weekdays")]
    [InlineData("timeDuration", "Etc/UTC", null)]
    public async Task TheShapeConstraintRefusesCronWithoutAZoneAndAZoneWithoutCron(
        string kind,
        string? zone,
        string? calendar)
    {
        var workflow = await PublishAsync("0 8 * * *", "Etc/UTC");
        try
        {
            await using var db = fixture.CreateDbContext();
            db.TimerSubscriptions.Add(new TimerSubscriptionEntity
            {
                WorkflowDefinitionId = workflow.Id,
                WorkflowKey = workflow.Key,
                ActivationId = Guid.NewGuid(),
                TimerNodeId = 99,
                TimerNodeName = "Shape probe",
                ScheduleKind = kind,
                ScheduleExpression = kind == "cron" ? "0 8 * * *" : "PT1H",
                ScheduleTimeZone = zone,
                ScheduleCalendar = calendar,
                CancelActivity = true,
                Status = TimerSubscriptionStatuses.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                NextDueAt = DateTimeOffset.UtcNow
            });
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var postgres = Assert.IsType<PostgresException>(refused.InnerException);
            Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
            Assert.Equal("CK_timer_subscriptions_cron_shape", postgres.ConstraintName);
        }
        finally
        {
            await RetireAsync(workflow.Id);
        }
    }

    [Fact]
    public async Task ADefaultSwitchRetiresTheScheduleAndReturningToItStartsANewActivation()
    {
        var cron = await PublishAsync("*/5 * * * *", "Etc/UTC");
        long? plainId = null;
        try
        {
            await ReconcileAsync(DateTimeOffset.UtcNow);
            var original = await SubscriptionAsync(cron.Key);
            var originalJob = Assert.Single(await JobsAsync(original.Id));

            // A Worker leases the occurrence, and the default changes before it fires.
            await PromoteAsync(originalJob.Id);
            await using var factory = ClockFactory(original.NextDueAt.AddSeconds(5));
            WorkflowJobLeaseRecord lease;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                lease = Assert.Single(await Lease(scope));
                Assert.Equal(originalJob.Id, lease.Job.Id);
            }

            plainId = await CreateVersionAsync(cron.Id, PlainModel(cron.Key));
            await SetDefaultAsync(plainId.Value);

            // The firing is stale: it never uses the new default's schedule under the old activation, or starts anything.
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
            }

            Assert.Equal(0, await InstanceCountAsync(cron.Key));
            Assert.Contains((await JobAsync(originalJob.Id)).Status, new[] { WorkflowJobStatuses.Completed, WorkflowJobStatuses.Cancelled });
            Assert.DoesNotContain(await JobsAsync(original.Id), job => job.Status == WorkflowJobStatuses.Queued);

            await ReconcileAsync(DateTimeOffset.UtcNow);
            Assert.Equal(TimerSubscriptionStatuses.Cancelled, (await SubscriptionAsync(cron.Key)).Status);

            await SetDefaultAsync(cron.Id);
            await ReconcileAsync(DateTimeOffset.UtcNow);
            await using var db = fixture.CreateDbContext();
            var subscriptions = await db.TimerSubscriptions.AsNoTracking()
                .Where(row => row.WorkflowKey == cron.Key)
                .OrderBy(row => row.Id)
                .ToListAsync();
            Assert.Equal([TimerSubscriptionStatuses.Cancelled, TimerSubscriptionStatuses.Active], subscriptions.Select(row => row.Status));
            Assert.NotEqual(subscriptions[0].ActivationId, subscriptions[1].ActivationId);
            Assert.Equal(TimerScheduleKinds.Cron, subscriptions[1].ScheduleKind);
        }
        finally
        {
            await RetireAsync(plainId is null ? [cron.Id] : [cron.Id, plainId.Value]);
        }
    }

    [Fact]
    public async Task ConcurrentReconcilersAndAStaleDuplicateDeliveryCommitOneStartPerOccurrence()
    {
        var workflow = await PublishAsync("* * * * *", "Etc/UTC");
        await SetActivationAsync(workflow.Id, Instant("2027-01-04T12:00:30Z"));
        try
        {
            await Task.WhenAll(
                ReconcileAsync(Instant("2027-01-04T12:00:31Z")),
                ReconcileAsync(Instant("2027-01-04T12:00:31Z")));
            await ReconcileAsync(Instant("2027-01-04T12:00:32Z"));
            var subscription = await SubscriptionAsync(workflow.Key);
            var job = Assert.Single(await JobsAsync(subscription.Id));

            // The first lease expires; its Worker resumes after another Worker took the job over.
            await PromoteAsync(job.Id);
            await using var factory = ClockFactory(Instant("2027-01-04T12:01:10Z"));
            WorkflowJobLeaseRecord stale;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                stale = Assert.Single(await Lease(scope));
            }

            await using (var db = fixture.CreateDbContext())
            {
                await db.WorkflowJobs.Where(row => row.Id == job.Id).ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-5)));
            }

            WorkflowJobLeaseRecord current;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                current = Assert.Single(await Lease(scope));
                Assert.Equal(job.Id, current.Job.Id);
                Assert.True(current.LeaseGeneration > stale.LeaseGeneration);
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(current, CancellationToken.None);
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(stale, CancellationToken.None);
            }

            Assert.Equal(1, await InstanceCountAsync(workflow.Key));
            var advanced = await SubscriptionAsync(workflow.Key);
            Assert.Equal((1L, Instant("2027-01-04T12:02:00Z")), (advanced.Occurrence, advanced.NextDueAt));
            Assert.Equal(WorkflowJobStatuses.Completed, (await JobAsync(job.Id)).Status);
        }
        finally
        {
            await RetireAsync(workflow.Id);
        }
    }

    private async Task<(long Id, string Key)> PublishAsync(string cron, string zone)
    {
        var key = $"cron-runtime-{Guid.NewGuid():N}";
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(Model(key, cron, zone), true));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>(), key);
    }

    private async Task<long> CreateVersionAsync(long sourceId, WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/api/workflows/{sourceId}", new UpdateWorkflowRequest(model, true));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
    }

    private async Task SetDefaultAsync(long workflowId)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/set-default");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static WorkflowModel Model(string key, string cron, string zone) => new()
    {
        Id = key,
        Name = key,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Scheduled start",
                Type = BpmnFlowNodeTypes.TimerStartEvent,
                Timer = new TimerDefinitionModel { Cron = cron, TimeZone = zone }
            },
            new FlowNodeModel { Id = 2, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
    };

    private async Task SetActivationAsync(long workflowId, DateTimeOffset activatedAt)
    {
        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.WorkflowDefinitions
            .Where(row => row.Id == workflowId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DefaultActivatedAt, activatedAt)));
    }

    // Each pass reconciles every timer-start family in the shared database, so a test publishes a family only when the
    // next pass should schedule it.
    private async Task ReconcileAsync(DateTimeOffset now)
    {
        using var telemetry = new WorkerTelemetry();
        var service = new TimerStartReconciliationService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new WorkerOptions { TimerStartReconcileBatchSize = 100_000 },
            new FixedClock(now),
            telemetry,
            NullLogger<TimerStartReconciliationService>.Instance);
        var reconcile = typeof(TimerStartReconciliationService).GetMethod(
                "ReconcileAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ReconcileAsync was not found.");
        await Assert.IsAssignableFrom<Task>(reconcile.Invoke(service, [CancellationToken.None]));
    }

    private async Task ProcessAsync(long jobId, DateTimeOffset now)
    {
        await PromoteAsync(jobId);
        await using var factory = ClockFactory(now);
        await using var scope = factory.Services.CreateAsyncScope();
        var lease = Assert.Single(await Lease(scope));
        Assert.Equal(jobId, lease.Job.Id);
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
    }

    // Due a minute ago by this machine's clock, so the job is due even when the database clock runs behind it.
    private async Task PromoteAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.WorkflowJobs
            .Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Priority, int.MaxValue)
                .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
    }

    private static Task<IReadOnlyList<WorkflowJobLeaseRecord>> Lease(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"cron-test:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 0,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            CancellationToken.None);

    private WebApplicationFactory<Program> ClockFactory(DateTimeOffset now) =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedClock(now));
        }));

    private async Task<TimerSubscriptionEntity> SubscriptionAsync(string workflowKey)
    {
        await using var db = fixture.CreateDbContext();
        return await db.TimerSubscriptions.AsNoTracking()
            .Where(row => row.WorkflowKey == workflowKey)
            .OrderByDescending(row => row.Id)
            .FirstAsync();
    }

    private async Task<List<WorkflowJobEntity>> JobsAsync(long subscriptionId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.AsNoTracking()
            .Where(row => row.TimerSubscriptionId == subscriptionId)
            .OrderBy(row => row.Id)
            .ToListAsync();
    }

    private async Task<WorkflowJobEntity> QueuedJobAsync(long subscriptionId) =>
        (await JobsAsync(subscriptionId)).Single(job => job.Status == WorkflowJobStatuses.Queued);

    private async Task<WorkflowJobEntity> JobAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.AsNoTracking().SingleAsync(row => row.Id == jobId);
    }

    private async Task<int> InstanceCountAsync(string workflowKey)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowInstances.CountAsync(row => row.WorkflowKey == workflowKey);
    }

    // Once a plain version is the default, a family's leftover occurrence is stale if any later test leases it, and
    // reconciliation cancels it.
    private async Task RetireAsync(params long[] workflowIds)
    {
        foreach (var workflowId in workflowIds)
        {
            string workflowKey;
            await using (var db = fixture.CreateDbContext())
            {
                var definition = await db.WorkflowDefinitions.AsNoTracking().SingleAsync(row => row.Id == workflowId);
                if (!definition.IsDefault)
                {
                    continue;
                }

                workflowKey = definition.WorkflowKey;
            }

            await SetDefaultAsync(await CreateVersionAsync(workflowId, PlainModel(workflowKey)));
        }
    }

    private static WorkflowModel PlainModel(string key) => new()
    {
        Id = key,
        Name = key,
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
    };

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        ApiTestAuth.Authorize(request, "cron-author");
        return await fixture.Client.SendAsync(request);
    }

    private static DateTimeOffset Instant(string text) => DateTimeOffset.Parse(text).ToUniversalTime();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
