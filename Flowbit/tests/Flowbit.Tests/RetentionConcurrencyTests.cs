using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class RetentionConcurrencyTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private readonly TestClock clock = new(DateTimeOffset.UtcNow);
    private readonly List<long> definitionIds = [];
    private RetentionDataSource source = null!;
    private RetentionRepository repository = null!;
    private static RetentionExecutionOptions Options(string worker) => new()
    {
        WorkerId = worker,
        BatchSize = 1,
        MaxRunnableJobs = int.MaxValue,
        MaxQueueLagSeconds = int.MaxValue
    };

    public async Task InitializeAsync()
    {
        await ResetAsync();
        source = new RetentionDataSource(fixture.ConnectionString);
        repository = new RetentionRepository(source, clock);
    }

    public async Task DisposeAsync()
    {
        await ResetAsync();
        await using var db = fixture.CreateDbContext();
        await db.WorkflowJobs.Where(job => definitionIds.Contains(job.WorkflowDefinitionId)).ExecuteDeleteAsync();
        await db.WorkflowInstances.Where(instance => definitionIds.Contains(instance.WorkflowDefinitionId)).ExecuteDeleteAsync();
        await db.WorkflowDefinitions.Where(definition => definitionIds.Contains(definition.Id)).ExecuteDeleteAsync();
        await source.DisposeAsync();
    }

    [Fact]
    public async Task LockedInstanceCanReopenWithoutHistoryBeingDeletedByConcurrentCleanup()
    {
        var instanceId = await CreateTerminalAsync(3);
        await EnableHistoryAsync();
        await repository.RequestRunAsync("admin", CancellationToken.None);

        await using (var runtimeDb = fixture.CreateDbContext())
        await using (var transaction = await runtimeDb.Database.BeginTransactionAsync())
        {
            _ = await runtimeDb.WorkflowInstances.FromSqlInterpolated(
                $"SELECT * FROM flowbit.workflow_instances WHERE \"Id\"={instanceId} FOR UPDATE").SingleAsync();
            var maintenance = await repository.ProcessBatchAsync(Options("worker-a"), CancellationToken.None);
            Assert.Equal(0, maintenance.DeletedRows);
            await new WorkflowRuntimeRepository(runtimeDb).SetInstanceStatusAsync(instanceId, "running", CancellationToken.None);
            await runtimeDb.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await DrainAsync("worker-a");
        await using var verify = fixture.CreateDbContext();
        var instance = await verify.WorkflowInstances.SingleAsync(item => item.Id == instanceId);
        Assert.Equal("running", instance.Status);
        Assert.Null(instance.FinishedAt);
        Assert.Null(instance.HistoryPrunedAt);
        Assert.Equal(3, await verify.InstanceHistory.CountAsync(item => item.InstanceId == instanceId));
    }

    [Fact]
    public async Task PolicyChangedDuringRunStopsLaterBatchesUsingTheOldCutoff()
    {
        var instanceId = await CreateTerminalAsync(3);
        await EnableHistoryAsync();
        await repository.RequestRunAsync("admin", CancellationToken.None);
        var first = await repository.ProcessBatchAsync(Options("worker-a"), CancellationToken.None);
        Assert.Equal(1, first.DeletedRows);

        var policy = (await repository.GetAsync(CancellationToken.None)).Policies.Single(item => item.Category == RetentionCategories.WorkflowHistory);
        await repository.UpdatePolicyAsync(policy.Category, new(null, policy.Revision), "admin", CancellationToken.None);
        var stopped = await repository.ProcessBatchAsync(Options("worker-a"), CancellationToken.None);
        Assert.Equal(0, stopped.DeletedRows);
        await DrainAsync("worker-a");

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(2, await verify.InstanceHistory.CountAsync(item => item.InstanceId == instanceId));
        var status = await repository.GetAsync(CancellationToken.None);
        Assert.Equal("policyChanged", status.LastRun!.Categories.Single(item => item.Category == policy.Category).Status);
        Assert.Equal(1, status.LastRun.Categories.Single(item => item.Category == policy.Category).DeletedRows);
    }

    [Fact]
    public async Task CompetingWorkersShareOneLease_AndExpiredLeaseResumesWithoutDuplicateDeletes()
    {
        var instanceId = await CreateTerminalAsync(3);
        await EnableHistoryAsync();
        var run = await repository.RequestRunAsync("admin", CancellationToken.None);

        var results = await Task.WhenAll(
            repository.ProcessBatchAsync(Options("worker-a"), CancellationToken.None),
            repository.ProcessBatchAsync(Options("worker-b"), CancellationToken.None));
        Assert.Equal(1, results.Sum(item => item.DeletedRows));
        await using var verify = fixture.CreateDbContext();
        var lease = await verify.RetentionCoordinators.AsNoTracking().SingleAsync();
        var originalWorker = lease.LeaseOwner!;
        var replacementWorker = originalWorker == "worker-a" ? "worker-b" : "worker-a";
        Assert.NotNull(lease.LeaseExpiresAt);
        clock.Advance(TimeSpan.FromSeconds(31));

        var takeover = await repository.ProcessBatchAsync(Options(replacementWorker), CancellationToken.None);
        Assert.Equal(1, takeover.DeletedRows);
        var oldOwner = await repository.ProcessBatchAsync(Options(originalWorker), CancellationToken.None);
        Assert.Equal(0, oldOwner.DeletedRows);
        Assert.False(oldOwner.DidWork);
        var renewed = await verify.RetentionCoordinators.AsNoTracking().SingleAsync();
        Assert.Equal(replacementWorker, renewed.LeaseOwner);
        Assert.True(renewed.LeaseGeneration > lease.LeaseGeneration);

        await DrainAsync(replacementWorker);
        Assert.Equal(0, await verify.InstanceHistory.CountAsync(item => item.InstanceId == instanceId));
        var status = await repository.GetAsync(CancellationToken.None);
        Assert.Equal(run.Id, status.LastRun!.Id);
        Assert.Equal(3, status.LastRun.Categories.Single(item => item.Category == RetentionCategories.WorkflowHistory).DeletedRows);
        Assert.NotNull((await verify.WorkflowInstances.AsNoTracking().SingleAsync(item => item.Id == instanceId)).HistoryPrunedAt);
    }

    [Fact]
    public async Task ExpiredRunningJobsPauseCleanupButLiveResultReadyJobsDoNot()
    {
        var instanceId = await CreateTerminalAsync(2);
        await EnableHistoryAsync();
        await repository.RequestRunAsync("admin", CancellationToken.None);
        var now = clock.GetUtcNow();
        int lagThreshold;
        long jobId;
        await using (var db = fixture.CreateDbContext())
        {
            // Other tests share this database. Put this probe beyond all existing
            // runnable lag so its lease state alone controls the assertion.
            var pending = await db.WorkflowJobs.AsNoTracking()
                .Where(job => ((job.Status == "queued" || job.Status == "retry") && job.DueAt <= now)
                    || ((job.Status == "running" || job.Status == "resultReady") && job.LeaseExpiresAt <= now))
                .Select(job => new { job.Status, job.DueAt, job.LeaseExpiresAt })
                .ToListAsync();
            var earliest = pending.Select(job => job.Status is "queued" or "retry" ? job.DueAt : job.LeaseExpiresAt!.Value)
                .DefaultIfEmpty(now).Min();
            lagThreshold = checked((int)Math.Ceiling((now - earliest).TotalSeconds) + 120);
            var instance = await db.WorkflowInstances.SingleAsync(item => item.Id == instanceId);
            var job = new WorkflowJobEntity
            {
                WorkflowDefinitionId = instance.WorkflowDefinitionId, WorkflowKey = instance.WorkflowKey,
                ActivationId = Guid.NewGuid(), NodeId = 1, NodeName = "workload probe", NodeType = "serviceTask",
                Kind = "asyncBefore", QueueClass = "activity", Phase = "before", Status = "resultReady",
                DueAt = now.AddSeconds(-lagThreshold - 60), CreatedAt = now.AddDays(-1), UpdatedAt = now,
                MaxAttempts = 3, AttemptCount = 1, WorkerId = "live-worker", LeaseToken = Guid.NewGuid(),
                LeaseGeneration = 1, LeaseExpiresAt = now.AddMinutes(1), ResultReadyAt = now
            };
            db.WorkflowJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var options = Options("worker-a") with { MaxQueueLagSeconds = lagThreshold };
        var healthy = await repository.ProcessBatchAsync(options, CancellationToken.None);
        Assert.False(healthy.IsPaused);
        Assert.Equal(1, healthy.DeletedRows);
        await using (var db = fixture.CreateDbContext())
        {
            await db.WorkflowJobs.Where(job => job.Id == jobId).ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, "running")
                .SetProperty(job => job.LeaseExpiresAt, now.AddSeconds(-lagThreshold - 60)));
        }

        var recoveryBacklog = await repository.ProcessBatchAsync(options, CancellationToken.None);
        Assert.True(recoveryBacklog.IsPaused);
        Assert.Equal(0, recoveryBacklog.DeletedRows);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(1, await verify.InstanceHistory.CountAsync(item => item.InstanceId == instanceId));
    }

    [Fact]
    public async Task FirstBatchSqlFailureRollsBackHistoryAndMarker_AndRecordsFailureUnderTheAcquiredLease()
    {
        var instanceId = await CreateTerminalAsync(1);
        await EnableHistoryAsync();
        await repository.RequestRunAsync("admin", CancellationToken.None);
        var suffix = Guid.NewGuid().ToString("N");
        var trigger = $"retention_test_fail_{suffix}";
        // Both identifiers are generated from a GUID and the only value interpolation is a database-generated integer.
        await using (var create = fixture.DataSource.CreateCommand($"""
            CREATE FUNCTION flowbit.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."InstanceId" = {instanceId} THEN
                    RAISE EXCEPTION 'Injected retention delete failure' USING ERRCODE = 'P0001';
                END IF;
                RETURN OLD;
            END $$;
            CREATE TRIGGER {trigger} BEFORE DELETE ON flowbit.instance_history
                FOR EACH ROW EXECUTE FUNCTION flowbit.{trigger}();
            """))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => repository.ProcessBatchAsync(Options("worker-a"), CancellationToken.None));
            Assert.Equal("P0001", error.SqlState);
            await using var verify = fixture.CreateDbContext();
            Assert.Equal(1, await verify.InstanceHistory.CountAsync(item => item.InstanceId == instanceId));
            Assert.Null((await verify.WorkflowInstances.SingleAsync(item => item.Id == instanceId)).HistoryPrunedAt);
            var status = await repository.GetAsync(CancellationToken.None);
            Assert.Equal("failed", status.CurrentRun!.Categories.Single(item => item.Category == RetentionCategories.WorkflowHistory).Status);
            Assert.Equal("worker-a", (await verify.RetentionCoordinators.AsNoTracking().SingleAsync()).LeaseOwner);
        }
        finally
        {
            await using var drop = fixture.DataSource.CreateCommand($"DROP TRIGGER {trigger} ON flowbit.instance_history; DROP FUNCTION flowbit.{trigger}();");
            await drop.ExecuteNonQueryAsync();
        }

        await DrainAsync("worker-a");
        Assert.Equal("failed", (await repository.GetAsync(CancellationToken.None)).LastRun!.Status);
    }

    private async Task EnableHistoryAsync()
    {
        var policy = (await repository.GetAsync(CancellationToken.None)).Policies.Single(item => item.Category == RetentionCategories.WorkflowHistory);
        await repository.UpdatePolicyAsync(policy.Category, new(1, policy.Revision), "admin", CancellationToken.None);
    }

    private async Task DrainAsync(string worker)
    {
        for (var tick = 0; tick < 40; tick++)
        {
            if ((await repository.GetAsync(CancellationToken.None)).CurrentRun is null) return;
            await repository.ProcessBatchAsync(Options(worker), CancellationToken.None);
        }
        Assert.Fail("Retention run did not finish within the bounded test tick count.");
    }

    private async Task<long> CreateTerminalAsync(int historyCount)
    {
        await using var db = fixture.CreateDbContext();
        var key = $"retention-concurrency-{Guid.NewGuid():N}";
        var definition = new WorkflowDefinitionEntity
        {
            WorkflowKey = key, Name = key, Version = 1,
            Definition = new WorkflowModel { Id = key, Name = key }
        };
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        definitionIds.Add(definition.Id);
        var instance = new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = definition.Id, WorkflowKey = key,
            Status = "completed", CreatedAt = clock.GetUtcNow().AddDays(-60),
            UpdatedAt = clock.GetUtcNow().AddDays(-30), FinishedAt = clock.GetUtcNow().AddDays(-30)
        };
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        db.InstanceHistory.AddRange(Enumerable.Range(1, historyCount).Select(index => new InstanceHistoryEntity
        {
            InstanceId = instance.Id, WorkflowDefinitionId = definition.Id,
            FromStepId = index, ToStepId = index + 1, Note = "retention-concurrency-test",
            PerformedAt = clock.GetUtcNow().AddDays(-30)
        }));
        await db.SaveChangesAsync();
        return instance.Id;
    }

    private async Task ResetAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.RetentionPolicies.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.RetentionDays, (int?)null)
            .SetProperty(item => item.IsInitialized, true)
            .SetProperty(item => item.Revision, item => item.Revision + 1));
        await db.RetentionCoordinators.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.CurrentRunJson, (string?)null)
            .SetProperty(item => item.LastRunJson, (string?)null)
            .SetProperty(item => item.LeaseOwner, (string?)null)
            .SetProperty(item => item.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(item => item.NextScheduledAt, DateTimeOffset.UtcNow.AddDays(1)));
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
