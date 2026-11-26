using System.Reflection;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// EVENT-04G: a lease round counts only the share jobs of split signal broadcasts against the
/// Worker's share slots, and the queue statistics, retention's overload check, and the next wake
/// time leave held-back share jobs out.
/// </summary>
public sealed partial class DurableJobRepositoryTests
{
    private static readonly DateTimeOffset AncientDueAt = new(1990, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SignalShares_WithNoneAllowedARoundLeasesTheFamilysOtherJobsAndNoShareJob()
    {
        var prefix = $"share-none-{Guid.NewGuid():N}";
        var family = $"{prefix}-a";
        var other = $"{prefix}-b";
        try
        {
            long shareJob, unsplitJob, instanceJob, otherJob;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(family);
                var otherDefinition = NewDefinition(other);
                setup.WorkflowDefinitions.AddRange(definition, otherDefinition);
                await setup.SaveChangesAsync();
                var instance = NewInstance(definition, family);
                setup.WorkflowInstances.Add(instance);
                await setup.SaveChangesAsync();

                // The share job ranks first in its family; the filter must remove it before the ranking.
                var share = NewSignalJob(definition, family, 0, SignalDeliveryShares.SharePhase, priority: 1_000_002);
                var unsplit = NewSignalJob(definition, family, 1, SignalBroadcastWriter.DeliverPhase, priority: 1_000_001);
                var withInstance = NewJob(definition, instance.Id, family, 2, priority: 1_000_000);
                withInstance.QueueClass = WorkflowJobClasses.Activity;
                var otherFamily = NewJob(otherDefinition, null, other, 3, priority: 1_000_000);
                otherFamily.QueueClass = WorkflowJobClasses.Activity;
                setup.WorkflowJobs.AddRange(share, unsplit, withInstance, otherFamily);
                await setup.SaveChangesAsync();
                (shareJob, unsplitJob, instanceJob, otherJob) = (share.Id, unsplit.Id, withInstance.Id, otherFamily.Id);
            }

            var leased = await LeaseAsync(maxSignalShareCount: 0);

            Assert.Contains(unsplitJob, leased);
            Assert.Contains(instanceJob, leased);
            Assert.Contains(otherJob, leased);
            Assert.DoesNotContain(shareJob, leased);
        }
        finally
        {
            await DeleteWorkflowAsync(family);
            await DeleteWorkflowAsync(other);
        }
    }

    [Fact]
    public async Task SignalShares_ARoundTakesNoMoreShareJobsThanAllowedAndTheDefaultTakesAll()
    {
        var prefix = $"share-cap-{Guid.NewGuid():N}";
        var families = Enumerable.Range(0, 4).Select(index => $"{prefix}-{index}").ToArray();
        try
        {
            var shareJobs = await AddShareJobsAsync(families);

            var capped = (await LeaseAsync(maxSignalShareCount: 2)).Intersect(shareJobs).ToArray();
            Assert.Equal(2, capped.Length);

            // A request that sets no allowance leases share jobs as before the split existed.
            var rest = (await LeaseAsync(maxSignalShareCount: null)).Intersect(shareJobs).ToArray();
            Assert.Equal(shareJobs.Except(capped).Order(), rest.Order());
        }
        finally
        {
            foreach (var family in families)
            {
                await DeleteWorkflowAsync(family);
            }
        }
    }

    [Fact]
    public async Task SignalShares_AShareJobWhoseLeaseExpiredIsRecoveredOnlyInsideTheAllowance()
    {
        var family = $"share-expired-{Guid.NewGuid():N}";
        try
        {
            long jobId;
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(family);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();
                var job = NewSignalJob(definition, family, 0, SignalDeliveryShares.SharePhase, priority: 1_000_000);
                var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
                job.Status = WorkflowJobStatuses.Running;
                job.AttemptCount = 1;
                job.WorkerId = "expired-share-worker";
                job.LeaseToken = Guid.NewGuid();
                job.LeaseGeneration = 1;
                job.LeaseExpiresAt = expiredAt;
                job.HeartbeatAt = expiredAt.AddMinutes(-1);
                job.StartedAt = expiredAt.AddMinutes(-2);
                job.Attempts.Add(new WorkflowJobAttemptEntity
                {
                    AttemptNumber = 1,
                    Status = WorkflowJobAttemptStatuses.Running,
                    WorkerId = job.WorkerId,
                    LeaseGeneration = 1,
                    StartedAt = job.StartedAt.Value
                });
                setup.WorkflowJobs.Add(job);
                await setup.SaveChangesAsync();
                jobId = job.Id;
            }

            Assert.DoesNotContain(jobId, await LeaseAsync(maxSignalShareCount: 0));
            Assert.Contains(jobId, await LeaseAsync(maxSignalShareCount: 1));
        }
        finally
        {
            await DeleteWorkflowAsync(family);
        }
    }

    [Fact]
    public async Task SignalShares_QueueStatisticsLeaveOutShareJobsAndCountAnUnsplitDeliveryJob()
    {
        var family = $"share-statistics-{Guid.NewGuid():N}";
        try
        {
            await using var db = fixture.CreateDbContext();
            var repository = new WorkflowJobRepository(db, fixture.DataSource);
            var before = await repository.GetQueueStatisticsAsync(CancellationToken.None);

            WorkflowDefinitionEntity definition;
            await using (var setup = fixture.CreateDbContext())
            {
                definition = NewDefinition(family);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();
                setup.WorkflowJobs.AddRange(
                    NewSignalJob(definition, family, 0, SignalDeliveryShares.SharePhase, priority: 0, dueAt: AncientDueAt),
                    NewSignalJob(definition, family, 1, SignalDeliveryShares.SharePhase, priority: 0, dueAt: AncientDueAt));
                await setup.SaveChangesAsync();
            }

            var withShares = await repository.GetQueueStatisticsAsync(CancellationToken.None);
            Assert.Equal(before.RunnableDepth, withShares.RunnableDepth);
            Assert.Equal(before.OldestRunnableDueAt, withShares.OldestRunnableDueAt);
            Assert.False(await IsRuntimeBusyAsync());

            await using (var setup = fixture.CreateDbContext())
            {
                setup.WorkflowJobs.Add(NewSignalJob(definition, family, 2, SignalBroadcastWriter.DeliverPhase, priority: 0, dueAt: AncientDueAt));
                await setup.SaveChangesAsync();
            }

            var withUnsplit = await repository.GetQueueStatisticsAsync(CancellationToken.None);
            Assert.Equal(before.RunnableDepth + 1, withUnsplit.RunnableDepth);
            Assert.Equal(AncientDueAt, withUnsplit.OldestRunnableDueAt);
            Assert.True(await IsRuntimeBusyAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(family);
        }
    }

    [Fact]
    public async Task SignalShares_AWorkerWithoutShareSlotsGetsNoWakeTimeFromDueShareJobs()
    {
        var family = $"share-wake-{Guid.NewGuid():N}";
        try
        {
            await using (var setup = fixture.CreateDbContext())
            {
                var definition = NewDefinition(family);
                setup.WorkflowDefinitions.Add(definition);
                await setup.SaveChangesAsync();
                setup.WorkflowJobs.Add(NewSignalJob(definition, family, 0, SignalDeliveryShares.SharePhase, priority: 0, dueAt: AncientDueAt));
                await setup.SaveChangesAsync();
            }

            await using var db = fixture.CreateDbContext();
            var repository = new WorkflowJobRepository(db, fixture.DataSource);
            Assert.Equal(AncientDueAt, await repository.GetNextWakeAtAsync(includeSignalShares: true, CancellationToken.None));
            Assert.Equal(AncientDueAt, await repository.GetNextWakeAtAsync(CancellationToken.None));
            Assert.NotEqual(AncientDueAt, await repository.GetNextWakeAtAsync(includeSignalShares: false, CancellationToken.None));
        }
        finally
        {
            await DeleteWorkflowAsync(family);
        }
    }

    private async Task<long[]> AddShareJobsAsync(string[] families)
    {
        await using var setup = fixture.CreateDbContext();
        var definitions = families.Select(NewDefinition).ToArray();
        setup.WorkflowDefinitions.AddRange(definitions);
        await setup.SaveChangesAsync();
        var jobs = definitions.Select((definition, index) =>
            NewSignalJob(definition, families[index], index, SignalDeliveryShares.SharePhase, priority: 1_000_000)).ToArray();
        setup.WorkflowJobs.AddRange(jobs);
        await setup.SaveChangesAsync();
        return jobs.Select(job => job.Id).ToArray();
    }

    /// <summary>One lease round as a Worker with this many share slots left; null sets no allowance.</summary>
    private async Task<long[]> LeaseAsync(int? maxSignalShareCount)
    {
        await using var db = fixture.CreateDbContext();
        var repository = new WorkflowJobRepository(db, fixture.DataSource);
        var request = new WorkflowJobLeaseRequest(
            $"share-lease-test-{Guid.NewGuid():N}",
            MaxCount: 16,
            MaxActivityCount: 16,
            MaxPerInstance: 4,
            LeaseDuration: TimeSpan.FromMinutes(1));
        if (maxSignalShareCount is int allowance)
        {
            request = request with { MaxSignalShareCount = allowance };
        }

        return (await repository.LeaseRunnableAsync(request, CancellationToken.None)).Select(lease => lease.Job.Id).ToArray();
    }

    /// <summary>
    /// Retention's overload check at a moment long ago, so only this test's jobs, due before it,
    /// can make the runtime look busy.
    /// </summary>
    private async Task<bool> IsRuntimeBusyAsync()
    {
        var check = typeof(RetentionRepository).GetMethod("IsRuntimeBusyAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var options = new RetentionExecutionOptions { MaxRunnableJobs = 100, MaxQueueLagSeconds = 30 };
        var busy = await (Task<bool>)check.Invoke(null,
            [connection, transaction, AncientDueAt.AddYears(1), options, CancellationToken.None])!;
        await transaction.RollbackAsync();
        return busy;
    }

    private static WorkflowJobEntity NewSignalJob(
        WorkflowDefinitionEntity definition,
        string workflowKey,
        int index,
        string phase,
        int priority,
        DateTimeOffset? dueAt = null)
    {
        var job = NewJob(definition, null, workflowKey, index, priority, WorkflowJobKinds.SignalBroadcastDeliver);
        job.QueueClass = WorkflowJobClasses.Activity;
        job.Phase = phase;
        job.FailureHandling = WorkflowJobFailureHandling.RetryFirst;
        if (dueAt is DateTimeOffset due)
        {
            job.DueAt = due;
        }

        return job;
    }
}
