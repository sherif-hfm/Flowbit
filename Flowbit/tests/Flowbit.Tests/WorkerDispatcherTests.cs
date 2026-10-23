extern alias FlowbitWorker;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DatabaseSchemaGate = FlowbitWorker::Flowbit.Worker.DatabaseSchemaGate;
using DatabaseSchemaStatus = FlowbitWorker::Flowbit.Worker.DatabaseSchemaStatus;
using JobDispatcher = FlowbitWorker::Flowbit.Worker.JobDispatcher;
using IJobWakeupSignal = FlowbitWorker::Flowbit.Worker.IJobWakeupSignal;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

public sealed class WorkerDispatcherTests
{
    [Fact]
    public async Task RepeatsFairAcquisitionRoundsImmediatelyWhileSlotsRemain()
    {
        var leases = Enumerable.Range(1, 4).Select(id => NewLease(id)).ToArray();
        var repository = new DispatcherJobRepository(leases);
        var processor = new BlockingProcessor();
        await using var services = new ServiceCollection()
            .AddSingleton<IWorkflowJobRepository>(repository)
            .AddSingleton<IWorkflowJobProcessor>(processor)
            .BuildServiceProvider();
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = new JobDispatcher(
            services.GetRequiredService<IServiceScopeFactory>(),
            new DelayWakeupSignal(),
            NewOptions(maxConcurrency: 4),
            TimeProvider.System,
            telemetry,
            NullLogger<JobDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await repository.FirstAcquisition.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var started = DateTimeOffset.UtcNow;
        await processor.FourthStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(500));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromMilliseconds(500));
        Assert.Equal(4, processor.StartedCount);
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CompletedJobRefillsItsSlotWithoutWaitingForThePoll()
    {
        var repository = new DispatcherJobRepository([NewLease(1), NewLease(2)]);
        var processor = new ImmediateProcessor();
        await using var services = NewServices(repository, processor);
        using var telemetry = new WorkerTelemetry();
        var options = NewOptions(maxConcurrency: 1);
        options.PollMilliseconds = 2000;
        using var dispatcher = NewDispatcher(
            services,
            new ChannelWakeupSignal(),
            options,
            telemetry);

        await dispatcher.StartAsync(CancellationToken.None);
        await processor.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);

        var starts = processor.StartTimestamps;
        Assert.Equal(2, starts.Length);
        // Without a completion wake-up the second job waits for the 2-second
        // poll even though its slot was free after a few milliseconds.
        Assert.True(
            Stopwatch.GetElapsedTime(starts[0], starts[1]) < TimeSpan.FromMilliseconds(750),
            "The second job must start when the first frees its slot.");
    }

    [Fact]
    public async Task DispatcherWaitsForTheSchemaGateBeforeAcquiring()
    {
        var schemaReady = 0;
        using var gate = new DatabaseSchemaGate(
            _ => Task.FromResult(Volatile.Read(ref schemaReady) == 1
                ? new DatabaseSchemaStatus([], [])
                : new DatabaseSchemaStatus(["20990101000000_Pending"], [])),
            TimeProvider.System,
            NullLogger<DatabaseSchemaGate>.Instance,
            TimeSpan.FromMilliseconds(20));
        var repository = new DispatcherJobRepository([NewLease(1)]);
        var processor = new ImmediateProcessor();
        await using var services = NewServices(repository, processor);
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = NewDispatcher(
            services,
            new DelayWakeupSignal(),
            NewOptions(maxConcurrency: 1),
            telemetry,
            gate);

        await gate.StartAsync(CancellationToken.None);
        await dispatcher.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, repository.AcquisitionCount);

        Volatile.Write(ref schemaReady, 1);
        await repository.FirstAcquisition.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);
        await gate.StopAsync(CancellationToken.None);
        Assert.Single(processor.StartTimestamps);
    }

    [Fact]
    public async Task CancelsLocalInvocationOnShortFenceCheckCadence()
    {
        var repository = new DispatcherJobRepository([NewLease(1)])
        {
            LeaseAlive = false
        };
        var processor = new BlockingProcessor();
        await using var services = new ServiceCollection()
            .AddSingleton<IWorkflowJobRepository>(repository)
            .AddSingleton<IWorkflowJobProcessor>(processor)
            .BuildServiceProvider();
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = new JobDispatcher(
            services.GetRequiredService<IServiceScopeFactory>(),
            new DelayWakeupSignal(),
            NewOptions(maxConcurrency: 1),
            TimeProvider.System,
            telemetry,
            NullLogger<JobDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await repository.FirstAcquisition.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var started = DateTimeOffset.UtcNow;
        await processor.Cancelled.Task.WaitAsync(TimeSpan.FromMilliseconds(750));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromMilliseconds(750));
        Assert.True(repository.LeaseCheckCount > 0);
        Assert.Equal(0, repository.HeartbeatCount);
        await dispatcher.StopAsync(CancellationToken.None);
        // A lost fence belongs to another replica and is never released.
        Assert.Empty(repository.ReleasedLeases);
    }

    [Fact]
    public async Task RetriesAHeartbeatThatBlocksPastTheCommandTimeout()
    {
        var repository = new DispatcherJobRepository([NewLease(1)])
        {
            BlockHeartbeats = true
        };
        var processor = new BlockingProcessor();
        await using var services = new ServiceCollection()
            .AddSingleton<IWorkflowJobRepository>(repository)
            .AddSingleton<IWorkflowJobProcessor>(processor)
            .BuildServiceProvider();
        using var telemetry = new WorkerTelemetry();
        var options = NewOptions(maxConcurrency: 1);
        options.HeartbeatSeconds = 1;
        using var dispatcher = new JobDispatcher(
            services.GetRequiredService<IServiceScopeFactory>(),
            new DelayWakeupSignal(),
            options,
            TimeProvider.System,
            telemetry,
            NullLogger<JobDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await repository.FirstAcquisition.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await repository.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await repository.SecondHeartbeat.Task.WaitAsync(TimeSpan.FromMilliseconds(750));

        Assert.True(repository.HeartbeatCount >= 2);
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnhandledResultReadyFailureRetriesFinalizationAfterADelay()
    {
        var repository = new DispatcherJobRepository(
            [NewLease(1, WorkflowJobStatuses.ResultReady)])
        {
            ReleaseResultReady = true
        };
        await using var services = NewServices(repository, new ThrowingProcessor());
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = NewDispatcher(
            services,
            new DelayWakeupSignal(),
            NewOptions(maxConcurrency: 1),
            telemetry);

        await dispatcher.StartAsync(CancellationToken.None);
        await repository.RecoveryRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);

        // The captured result keeps its lease for a database-timed delay
        // instead of being re-leased in a tight loop or re-invoked.
        var release = Assert.Single(repository.ResultReadyReleases);
        Assert.Equal(1, release.Fence.JobId);
        Assert.Equal(TimeSpan.FromSeconds(5), release.RetryAfter);
        Assert.Empty(repository.ScheduledRetries);
        Assert.Empty(repository.OpenedIncidents);
        Assert.Contains(
            "flowbit_worker_finalization_retries_total 1",
            telemetry.ExportPrometheus(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnhandledFailureSchedulesAJitteredRetryWhileAttemptsRemain()
    {
        var repository = new DispatcherJobRepository([NewLease(1)])
        {
            ScheduleRetryResult = true
        };
        await using var services = NewServices(repository, new ThrowingProcessor());
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = NewDispatcher(
            services,
            new DelayWakeupSignal(),
            NewOptions(maxConcurrency: 1),
            telemetry);
        var before = DateTimeOffset.UtcNow;

        await dispatcher.StartAsync(CancellationToken.None);
        await repository.RecoveryRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);

        var retry = Assert.Single(repository.ScheduledRetries);
        Assert.Equal("unhandled_worker_failure", retry.Failure.FailureCode);
        Assert.Equal("Finalizer defect.", retry.Failure.FailureDescription);
        Assert.InRange(
            retry.DueAt,
            before + TimeSpan.FromMilliseconds(900),
            DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1100));
        Assert.Single(repository.ResultReadyReleases);
        Assert.Empty(repository.OpenedIncidents);
        Assert.Contains(
            "flowbit_worker_retries_scheduled_total 1",
            telemetry.ExportPrometheus(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnhandledFailureOnTheFinalAttemptOpensAnIncident()
    {
        var repository = new DispatcherJobRepository([NewLease(1, attemptNumber: 4)]);
        await using var services = NewServices(repository, new ThrowingProcessor());
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = NewDispatcher(
            services,
            new DelayWakeupSignal(),
            NewOptions(maxConcurrency: 1),
            telemetry);

        await dispatcher.StartAsync(CancellationToken.None);
        await repository.RecoveryRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);

        var incident = Assert.Single(repository.OpenedIncidents);
        Assert.Equal(1, incident.Fence.JobId);
        Assert.Equal("unhandled_worker_failure", incident.Type);
        Assert.Empty(repository.ScheduledRetries);
    }

    [Fact]
    public async Task ShutdownLetsARunningJobFinishWithinTheDrainWindow()
    {
        var repository = new DispatcherJobRepository([NewLease(1)]);
        var processor = new GatedProcessor();
        await using var services = NewServices(repository, processor);
        using var telemetry = new WorkerTelemetry();
        var options = NewOptions(maxConcurrency: 1);
        options.ShutdownDrainSeconds = 5;
        using var dispatcher = NewDispatcher(
            services,
            new DelayWakeupSignal(),
            options,
            telemetry);

        await dispatcher.StartAsync(CancellationToken.None);
        await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = dispatcher.StopAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // Stopping ends acquisition but neither cancels nor abandons the job.
        Assert.False(processor.Cancelled.Task.IsCompleted);
        Assert.False(stopping.IsCompleted);
        processor.Release.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(processor.Completed.Task.IsCompletedSuccessfully);
        Assert.False(processor.Cancelled.Task.IsCompleted);
        Assert.Empty(repository.ReleasedLeases);
        Assert.Contains(
            "flowbit_worker_jobs_processed_total 1",
            telemetry.ExportPrometheus(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShutdownCancelsJobsAfterTheDrainWindowAndReleasesTheirLeases()
    {
        var repository = new DispatcherJobRepository([NewLease(1)]);
        var processor = new BlockingProcessor();
        await using var services = NewServices(repository, processor);
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = NewDispatcher(
            services,
            new DelayWakeupSignal(),
            NewOptions(maxConcurrency: 1),
            telemetry);

        await dispatcher.StartAsync(CancellationToken.None);
        await processor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopRequested = Stopwatch.GetTimestamp();
        await dispatcher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(
            Stopwatch.GetElapsedTime(stopRequested) >= TimeSpan.FromMilliseconds(900),
            "Shutdown must wait for the drain window before cancelling.");
        Assert.True(processor.Cancelled.Task.IsCompletedSuccessfully);
        var released = Assert.Single(repository.ReleasedLeases);
        Assert.Equal(1, released.JobId);
        Assert.Equal(1, released.LeaseGeneration);
        Assert.Empty(repository.ScheduledRetries);
        Assert.Empty(repository.OpenedIncidents);
    }

    private static WorkerOptions NewOptions(int maxConcurrency) => new()
    {
        MaxConcurrency = maxConcurrency,
        ActivityConcurrency = maxConcurrency,
        BatchSize = maxConcurrency,
        MaxPerInstance = maxConcurrency,
        LeaseSeconds = 15,
        HeartbeatSeconds = 5,
        LeaseCheckMilliseconds = 100,
        HeartbeatCommandTimeoutMilliseconds = 100,
        PollMilliseconds = 1000,
        IdleBackoffMilliseconds = 5000,
        ShutdownDrainSeconds = 1
    };

    private static ServiceProvider NewServices(
        DispatcherJobRepository repository,
        IWorkflowJobProcessor processor) =>
        new ServiceCollection()
            .AddSingleton<IWorkflowJobRepository>(repository)
            .AddSingleton(processor)
            .BuildServiceProvider();

    private static JobDispatcher NewDispatcher(
        ServiceProvider services,
        IJobWakeupSignal wakeupSignal,
        WorkerOptions options,
        WorkerTelemetry telemetry,
        DatabaseSchemaGate? schemaGate = null) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            wakeupSignal,
            options,
            TimeProvider.System,
            telemetry,
            NullLogger<JobDispatcher>.Instance,
            schemaGate);

    private static WorkflowJobLeaseRecord NewLease(
        int id,
        string status = WorkflowJobStatuses.Running,
        int attemptNumber = 1)
    {
        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid();
        return new WorkflowJobLeaseRecord(
            new WorkflowJobRecord(
                id,
                1,
                1,
                "dispatcher-test",
                id,
                null,
                null,
                null,
                Guid.NewGuid(),
                id,
                $"job-{id}",
                "serviceTask",
                WorkflowJobKinds.AsyncBefore,
                WorkflowJobClasses.Activity,
                "before",
                status,
                0,
                attemptNumber,
                4,
                WorkflowJobFailureHandling.BoundaryFirst,
                [TimeSpan.FromSeconds(1)],
                now,
                null,
                null,
                null,
                "dispatcher-worker",
                token,
                1,
                now.AddSeconds(15),
                now,
                null,
                null,
                null,
                null,
                null,
                null,
                now,
                now,
                now,
                null),
            token,
            1,
            attemptNumber);
    }

    private sealed class BlockingProcessor : IWorkflowJobProcessor
    {
        private int _startedCount;

        public int StartedCount => Volatile.Read(ref _startedCount);
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FourthStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ProcessAsync(
            WorkflowJobLeaseRecord lease,
            CancellationToken cancellationToken)
        {
            var started = Interlocked.Increment(ref _startedCount);
            if (started == 1)
            {
                FirstStarted.TrySetResult();
            }
            if (started == 4)
            {
                FourthStarted.TrySetResult();
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ImmediateProcessor : IWorkflowJobProcessor
    {
        private readonly ConcurrentQueue<long> _startTimestamps = new();

        public long[] StartTimestamps => _startTimestamps.ToArray();
        public TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProcessAsync(
            WorkflowJobLeaseRecord lease,
            CancellationToken cancellationToken)
        {
            _startTimestamps.Enqueue(Stopwatch.GetTimestamp());
            if (_startTimestamps.Count == 2)
            {
                SecondStarted.TrySetResult();
            }
            return Task.CompletedTask;
        }
    }

    private sealed class GatedProcessor : IWorkflowJobProcessor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ProcessAsync(
            WorkflowJobLeaseRecord lease,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
            Completed.TrySetResult();
        }
    }

    private sealed class ThrowingProcessor : IWorkflowJobProcessor
    {
        public Task ProcessAsync(
            WorkflowJobLeaseRecord lease,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Finalizer defect.");
    }

    private sealed class DelayWakeupSignal : IJobWakeupSignal
    {
        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await Task.Delay(timeout, cancellationToken);
        }

        public void Signal()
        {
        }
    }

    private sealed class ChannelWakeupSignal : IJobWakeupSignal
    {
        private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite
            });

        public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                _ = await _signals.Reader.ReadAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Polling timeout.
            }
        }

        public void Signal() => _signals.Writer.TryWrite(true);
    }

    private sealed class DispatcherJobRepository(
        IEnumerable<WorkflowJobLeaseRecord> leases) : IWorkflowJobRepository
    {
        private readonly ConcurrentQueue<WorkflowJobLeaseRecord> _leases = new(leases);
        private int _acquisitionCount;
        private int _leaseCheckCount;
        private int _heartbeatCount;

        public bool LeaseAlive { get; init; } = true;
        public bool BlockHeartbeats { get; init; }
        public bool ReleaseResultReady { get; init; }
        public bool ScheduleRetryResult { get; init; }
        public int AcquisitionCount => Volatile.Read(ref _acquisitionCount);
        public int LeaseCheckCount => Volatile.Read(ref _leaseCheckCount);
        public int HeartbeatCount => Volatile.Read(ref _heartbeatCount);
        public ConcurrentQueue<WorkflowJobFence> ReleasedLeases { get; } = new();
        public ConcurrentQueue<(WorkflowJobFence Fence, TimeSpan RetryAfter)> ResultReadyReleases { get; } = new();
        public ConcurrentQueue<(WorkflowJobFence Fence, DateTimeOffset DueAt, WorkflowJobResultRecord Failure)> ScheduledRetries { get; } = new();
        public ConcurrentQueue<(WorkflowJobFence Fence, string Type)> OpenedIncidents { get; } = new();
        public TaskCompletionSource FirstAcquisition { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstHeartbeat { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondHeartbeat { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecoveryRecorded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<WorkflowJobLeaseRecord>> LeaseRunnableAsync(
            WorkflowJobLeaseRequest request,
            CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _acquisitionCount);
            if (count == 1)
            {
                FirstAcquisition.TrySetResult();
            }
            IReadOnlyList<WorkflowJobLeaseRecord> result =
                _leases.TryDequeue(out var lease) ? [lease] : [];
            return Task.FromResult(result);
        }

        public async Task<bool> HeartbeatAsync(
            WorkflowJobFence fence,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _heartbeatCount);
            if (count == 1)
            {
                FirstHeartbeat.TrySetResult();
            }
            if (count == 2)
            {
                SecondHeartbeat.TrySetResult();
            }
            if (BlockHeartbeats)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return LeaseAlive;
        }

        public Task<bool> IsLeaseAliveAsync(
            WorkflowJobFence fence,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _leaseCheckCount);
            return Task.FromResult(LeaseAlive);
        }

        public Task<DateTimeOffset?> GetNextWakeAtAsync(CancellationToken cancellationToken) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task<bool> ReleaseResultReadyLeaseAsync(
            WorkflowJobFence fence,
            TimeSpan retryAfter,
            CancellationToken cancellationToken)
        {
            ResultReadyReleases.Enqueue((fence, retryAfter));
            if (ReleaseResultReady)
            {
                RecoveryRecorded.TrySetResult();
            }
            return Task.FromResult(ReleaseResultReady);
        }

        public Task<bool> ReleaseLeaseAsync(
            WorkflowJobFence fence,
            CancellationToken cancellationToken)
        {
            ReleasedLeases.Enqueue(fence);
            return Task.FromResult(true);
        }

        public Task<bool> ScheduleRetryAsync(
            WorkflowJobFence fence,
            DateTimeOffset dueAt,
            WorkflowJobResultRecord failure,
            CancellationToken cancellationToken)
        {
            ScheduledRetries.Enqueue((fence, dueAt, failure));
            if (ScheduleRetryResult)
            {
                RecoveryRecorded.TrySetResult();
            }
            return Task.FromResult(ScheduleRetryResult);
        }

        public Task<WorkflowIncidentRecord?> OpenIncidentAsync(
            WorkflowJobFence fence,
            string type,
            string summary,
            string? details,
            CancellationToken cancellationToken)
        {
            OpenedIncidents.Enqueue((fence, type));
            RecoveryRecorded.TrySetResult();
            return Task.FromResult<WorkflowIncidentRecord?>(null);
        }

        public Task<WorkflowJobRecord> EnqueueAsync(WorkflowJobCreateRecord create, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobRecord> EnqueueIncidentAsync(WorkflowJobCreateRecord create, string type, string summary, string? details, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobRecord?> GetAsync(long jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobRecord?> GetForUpdateAsync(long jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowJobRecord>> ListOpenByInstanceAsync(long instanceId, bool forUpdate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkflowIncidentRecord>> ListOpenIncidentsByInstanceAsync(long instanceId, bool forUpdate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobSnapshotRecord?> SaveStageAsync(WorkflowJobFence fence, WorkflowJobStageRecord stage, int maxSnapshotBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobSnapshotRecord?> GetSnapshotAsync(long snapshotId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SaveResultReadyAsync(WorkflowJobFence fence, WorkflowJobResultRecord result, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CompleteAsync(WorkflowJobFence fence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelByInstanceAsync(long instanceId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelByTokenIdsAsync(long instanceId, IReadOnlyCollection<long> tokenIds, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelByTimerSubscriptionIdsAsync(IReadOnlyCollection<long> timerSubscriptionIds, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelOtherJobsByTokenIdsAsync(long instanceId, IReadOnlyCollection<long> tokenIds, long? exceptJobId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CancelTimerJobsByTokenIdsAsync(long instanceId, IReadOnlyCollection<long> tokenIds, long? exceptJobId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<long> CountOpenByInstanceAsync(long instanceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobQueueStatisticsRecord> GetQueueStatisticsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<long, WorkflowInstanceJobSummaryRecord>> GetInstanceJobSummariesAsync(IReadOnlyCollection<long> instanceIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<WorkflowJobRecord>> SearchJobsAsync(WorkflowJobQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<WorkflowJobAttemptRecord>> ListAttemptsAsync(long jobId, string? cursor, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<WorkflowIncidentRecord>> SearchIncidentsAsync(WorkflowIncidentQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowIncidentRecord?> GetIncidentAsync(long incidentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobRecord?> RetryIncidentAsync(long incidentId, string resolvedBy, DateTimeOffset dueAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkflowJobCleanupResult> CleanupAsync(DateTimeOffset completedJobsBefore, DateTimeOffset resolvedIncidentsBefore, int batchSize, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
