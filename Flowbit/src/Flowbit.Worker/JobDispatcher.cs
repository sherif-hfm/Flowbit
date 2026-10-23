using System.Collections.Concurrent;
using System.Diagnostics;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;

namespace Flowbit.Worker;

public sealed class JobDispatcher(
    IServiceScopeFactory scopeFactory,
    IJobWakeupSignal wakeupSignal,
    WorkerOptions options,
    TimeProvider timeProvider,
    WorkerTelemetry telemetry,
    ILogger<JobDispatcher> logger,
    DatabaseSchemaGate? schemaGate = null) : BackgroundService
{
    // A captured result is finalized again after this database-timed delay
    // when finalization escapes an unexpected exception. The delay bounds a
    // failure that keeps recurring without ever repeating the external call.
    private static readonly TimeSpan ResultFinalizationRetryDelay = TimeSpan.FromSeconds(5);

    // After the drain window, cancelled jobs get this long to unwind and
    // release their leases. The host shutdown timeout leaves room for it.
    private static readonly TimeSpan HardStopGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseReleaseTimeout = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<RunningKey, RunningJob> _running = new();
    // Running jobs observe this source instead of the host stopping token, so
    // shutdown stops acquisition while in-flight work may still finish.
    private readonly CancellationTokenSource _hardStop = new();
    private readonly string _workerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private int _readyLogged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (schemaGate is not null)
            {
                await schemaGate.WaitUntilReadyAsync(stoppingToken);
            }

            logger.LogInformation(
                "Flowbit worker {WorkerId} started with concurrency {Concurrency} ({ActivityConcurrency} activity slots).",
                _workerId,
                options.MaxConcurrency,
                options.ActivityConcurrency);

            // Startup jitter avoids a polling herd when many replicas deploy together.
            await Task.Delay(
                Random.Shared.Next(0, Math.Max(1, options.PollMilliseconds)),
                stoppingToken);
            var idle = false;
            while (!stoppingToken.IsCancellationRequested)
            {
                ReapCompleted();
                var free = options.MaxConcurrency - _running.Count;
                var runningActivities = _running.Values.Count(item =>
                    item.QueueClass == WorkflowJobClasses.Activity);
                var activityFree = Math.Max(0, options.ActivityConcurrency - runningActivities);

                IReadOnlyList<WorkflowJobLeaseRecord> leases = [];
                DateTimeOffset? nextWakeAt = null;
                if (free > 0)
                {
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
                        leases = await repository.LeaseRunnableAsync(
                            new WorkflowJobLeaseRequest(
                                _workerId,
                                Math.Min(free, options.BatchSize),
                                Math.Min(activityFree, options.BatchSize),
                                options.MaxPerInstance,
                                options.LeaseDuration),
                            stoppingToken);
                        if (leases.Count == 0)
                        {
                            nextWakeAt = await repository.GetNextWakeAtAsync(stoppingToken);
                        }
                        telemetry.RecordAcquisition(
                            leases,
                            Stopwatch.GetElapsedTime(started),
                            timeProvider.GetUtcNow());
                        if (Interlocked.Exchange(ref _readyLogged, 1) == 0)
                        {
                            logger.LogInformation(
                                "Flowbit worker {WorkerId} is ready; authoritative PostgreSQL polling succeeded.",
                                _workerId);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Job acquisition failed.");
                        await Task.Delay(
                            Random.Shared.Next(0, Math.Max(1, options.PollMilliseconds / 2)),
                            stoppingToken);
                    }
                }

                foreach (var lease in leases)
                {
                    // A recovered lease generation may coexist briefly with a
                    // stale local invocation of the same durable job. Track the
                    // complete fence identity so the fresh generation is never
                    // leased and then accidentally dropped.
                    var key = new RunningKey(lease.Job.Id, lease.LeaseGeneration);
                    var running = new RunningJob(lease.Job.QueueClass, _hardStop.Token);
                    if (!_running.TryAdd(key, running))
                    {
                        running.Dispose();
                        continue;
                    }
                    var isActivity = lease.Job.QueueClass == WorkflowJobClasses.Activity;
                    telemetry.JobStarted(isActivity);
                    running.Task = ProcessLeaseAsync(lease, running);
                    // Refill the slot as soon as the job finishes instead of
                    // waiting for the next poll. The continuation runs only
                    // after the task has completed, which ReapCompleted needs.
                    _ = running.Task.ContinueWith(
                        static (_, signal) => ((IJobWakeupSignal)signal!).Signal(),
                        wakeupSignal,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                // One acquisition round intentionally returns at most one job
                // per instance/workflow fairness key. If capacity remains, run
                // another round immediately instead of making the second through
                // fourth per-instance slots wait for the one-second poll cadence.
                if (leases.Count > 0 && _running.Count < options.MaxConcurrency)
                {
                    continue;
                }

                idle = leases.Count == 0 && _running.IsEmpty;
                var now = timeProvider.GetUtcNow();
                var runnableDeadlineObserved = nextWakeAt is { } observedWake
                                               && observedWake <= now;
                var baselineDelay = idle && !runnableDeadlineObserved
                    ? TimeSpan.FromMilliseconds(options.IdleBackoffMilliseconds)
                    : TimeSpan.FromMilliseconds(options.PollMilliseconds);
                var dueDelay = nextWakeAt is { } wakeAt && wakeAt > now
                    ? wakeAt - now
                    : (TimeSpan?)null;
                var deadlineBound = dueDelay is { } untilDue
                                    && untilDue < baselineDelay;
                var delay = deadlineBound
                    ? TimeSpan.FromMilliseconds(Math.Max(10, dueDelay!.Value.TotalMilliseconds))
                    : baselineDelay + TimeSpan.FromMilliseconds(Random.Shared.Next(
                        0,
                        Math.Max(1, options.PollMilliseconds / 4)));
                await wakeupSignal.WaitAsync(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            await DrainAsync();
        }
    }

    private async Task ProcessLeaseAsync(
        WorkflowJobLeaseRecord lease,
        RunningJob running)
    {
        var started = Stopwatch.GetTimestamp();
        var isActivity = lease.Job.QueueClass == WorkflowJobClasses.Activity;
        var succeeded = false;
        var leaseCancellation = running.LeaseCancellation;
        var heartbeat = GuardLeaseAsync(lease, running);
        Exception? unhandled = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();
            await processor.ProcessAsync(lease, leaseCancellation.Token);
            succeeded = true;
        }
        catch (OperationCanceledException) when (leaseCancellation.IsCancellationRequested)
        {
            // Drain timeout, instance cancellation, or lease loss.
        }
        catch (Exception ex)
        {
            unhandled = ex;
            logger.LogError(ex, "Unhandled failure while processing job {JobId}.", lease.Job.Id);
        }
        finally
        {
            await leaseCancellation.CancelAsync();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException)
            {
                // Expected when processing completes.
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Heartbeat loop for job {JobId} generation {LeaseGeneration} stopped unexpectedly.",
                    lease.Job.Id,
                    lease.LeaseGeneration);
            }
            telemetry.JobFinished(
                isActivity,
                succeeded,
                Stopwatch.GetElapsedTime(started));
        }

        if (succeeded)
        {
            return;
        }

        if (_hardStop.IsCancellationRequested)
        {
            // The drain window ended before this job finished. Its local
            // invocation has stopped, so another replica may recover it now
            // instead of after lease expiry. A lost fence is left untouched.
            if (!running.LeaseLost)
            {
                await ReleaseStoppedLeaseAsync(lease);
            }
            return;
        }

        if (unhandled is null)
        {
            return;
        }

        try
        {
            await RecoverUnhandledFailureAsync(lease, unhandled, _hardStop.Token);
        }
        catch (OperationCanceledException) when (_hardStop.IsCancellationRequested)
        {
            // A hard stop leaves the lease for another replica to recover.
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Could not persist recovery for job {JobId}; its durable lease will recover after expiry.",
                lease.Job.Id);
        }
    }

    private async Task GuardLeaseAsync(
        WorkflowJobLeaseRecord lease,
        RunningJob running)
    {
        var leaseCancellation = running.LeaseCancellation;
        var localLeaseDeadline = lease.Job.LeaseExpiresAt
            ?? timeProvider.GetUtcNow() + options.LeaseDuration;
        var nextHeartbeatAt = timeProvider.GetUtcNow() + options.HeartbeatInterval;
        using var timer = new PeriodicTimer(options.LeaseCheckInterval);
        while (await timer.WaitForNextTickAsync(leaseCancellation.Token))
        {
            if (leaseCancellation.IsCancellationRequested)
            {
                return;
            }

            var now = timeProvider.GetUtcNow();
            var shouldHeartbeat = now >= nextHeartbeatAt;
            try
            {
                // Bound every guard call independently. In particular, a
                // heartbeat UPDATE can wait behind a finalizer holding the job
                // row; it must never consume the rest of the local lease.
                using var commandCancellation = CancellationTokenSource
                    .CreateLinkedTokenSource(leaseCancellation.Token);
                commandCancellation.CancelAfter(options.HeartbeatCommandTimeout);
                await using var scope = scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider
                    .GetRequiredService<IWorkflowJobRepository>();
                var alive = shouldHeartbeat
                    ? await repository.HeartbeatAsync(
                        Fence(lease),
                        options.LeaseDuration,
                        commandCancellation.Token)
                    : await repository.IsLeaseAliveAsync(
                        Fence(lease),
                        commandCancellation.Token);
                if (alive)
                {
                    if (shouldHeartbeat)
                    {
                        localLeaseDeadline =
                            timeProvider.GetUtcNow() + options.LeaseDuration;
                        nextHeartbeatAt = timeProvider.GetUtcNow()
                            + options.HeartbeatInterval;
                    }
                    continue;
                }

                telemetry.RecordLeaseLost();
                logger.LogWarning(
                    "Worker {WorkerId} lost the fence for job {JobId} generation {LeaseGeneration}; cancelling local execution.",
                    _workerId,
                    lease.Job.Id,
                    lease.LeaseGeneration);
                running.MarkLeaseLost();
                await leaseCancellation.CancelAsync();
                return;
            }
            catch (OperationCanceledException) when (leaseCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Lease guard failed for job {JobId} generation {LeaseGeneration}; it will retry on the next short check.",
                    lease.Job.Id,
                    lease.LeaseGeneration);
                if (timeProvider.GetUtcNow() < localLeaseDeadline)
                {
                    continue;
                }

                telemetry.RecordLeaseLost();
                logger.LogWarning(
                    "Worker {WorkerId} could not renew job {JobId} before its local lease deadline; cancelling local execution.",
                    _workerId,
                    lease.Job.Id);
                running.MarkLeaseLost();
                await leaseCancellation.CancelAsync();
                return;
            }
        }
    }

    private async Task RecoverUnhandledFailureAsync(
        WorkflowJobLeaseRecord lease,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var fence = Fence(lease);
        var description = exception.Message.Length <= 1000
            ? exception.Message
            : exception.Message[..1000];
        var failure = new WorkflowJobResultRecord(
            null,
            null,
            "unhandled_worker_failure",
            description);
        // A captured result is the durable invocation boundary. Never turn a
        // finalization/DB exception back into an activity retry, which would
        // repeat an already completed external call. Keep only a short,
        // database-timed lease so a replica recovers the result-ready attempt
        // soon, without a hot loop when finalization keeps failing.
        if (await repository.ReleaseResultReadyLeaseAsync(
                fence,
                ResultFinalizationRetryDelay,
                cancellationToken))
        {
            telemetry.RecordFinalizationRetry();
            return;
        }
        var retryIndex = Math.Max(0, lease.AttemptNumber - 1);
        if (lease.AttemptNumber < lease.Job.MaxAttempts
            && retryIndex < lease.Job.RetryDelays.Count)
        {
            var baseDelay = lease.Job.RetryDelays[retryIndex];
            var jitterFactor = 0.9 + Random.Shared.NextDouble() * 0.2;
            var dueAt = timeProvider.GetUtcNow()
                + TimeSpan.FromTicks((long)(baseDelay.Ticks * jitterFactor));
            if (await repository.ScheduleRetryAsync(
                    fence,
                    dueAt,
                    failure,
                    cancellationToken))
            {
                telemetry.RecordRetry();
                return;
            }
        }

        _ = await repository.OpenIncidentAsync(
            fence,
            "unhandled_worker_failure",
            $"Job #{lease.Job.Id} exhausted worker recovery.",
            description,
            cancellationToken);
    }

    private async Task ReleaseStoppedLeaseAsync(WorkflowJobLeaseRecord lease)
    {
        try
        {
            using var timeout = new CancellationTokenSource(LeaseReleaseTimeout);
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
            if (await repository.ReleaseLeaseAsync(Fence(lease), timeout.Token))
            {
                logger.LogInformation(
                    "Released job {JobId} generation {LeaseGeneration} after the shutdown drain window so another replica can recover it.",
                    lease.Job.Id,
                    lease.LeaseGeneration);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not release job {JobId} after the shutdown drain window; its lease recovers after expiry.",
                lease.Job.Id);
        }
    }

    private WorkflowJobFence Fence(WorkflowJobLeaseRecord lease) =>
        new(
            lease.Job.Id,
            lease.Job.WorkerId ?? _workerId,
            lease.LeaseToken,
            lease.LeaseGeneration);

    private void ReapCompleted()
    {
        foreach (var pair in _running)
        {
            if (pair.Value.Task?.IsCompleted == true)
            {
                if (!_running.TryRemove(pair.Key, out var removed))
                {
                    continue;
                }

                if (removed.Task?.IsFaulted == true)
                {
                    logger.LogError(
                        removed.Task.Exception,
                        "Job {JobId} generation {LeaseGeneration} task faulted after processing.",
                        pair.Key.JobId,
                        pair.Key.LeaseGeneration);
                }
                removed.Dispose();
            }
        }
    }

    private async Task DrainAsync()
    {
        var tasks = _running.Values
            .Select(item => item.Task)
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        // Acquisition has stopped. Leases keep their heartbeats, so running
        // jobs may finish normally within the drain window.
        logger.LogInformation(
            "Worker {WorkerId} stopped acquiring jobs; waiting up to {TimeoutSeconds}s for {Count} running jobs to finish.",
            _workerId,
            options.ShutdownDrainSeconds,
            tasks.Length);
        if (!await WaitForRunningAsync(tasks, TimeSpan.FromSeconds(options.ShutdownDrainSeconds)))
        {
            logger.LogWarning(
                "Worker shutdown reached its {TimeoutSeconds}s drain bound with {Count} jobs still running; cancelling them and releasing their leases.",
                options.ShutdownDrainSeconds,
                tasks.Count(task => !task.IsCompleted));
            await _hardStop.CancelAsync();
            if (!await WaitForRunningAsync(tasks, HardStopGrace))
            {
                logger.LogWarning(
                    "{Count} cancelled jobs did not stop within {GraceSeconds}s. Their leases remain durable and recover after expiry.",
                    tasks.Count(task => !task.IsCompleted),
                    HardStopGrace.TotalSeconds);
                return;
            }
        }

        ReapCompleted();
    }

    private async Task<bool> WaitForRunningAsync(Task[] tasks, TimeSpan timeout)
    {
        var all = Task.WhenAll(tasks);
        if (await Task.WhenAny(all, Task.Delay(timeout)) != all)
        {
            return false;
        }

        try
        {
            await all;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "One or more jobs faulted while the worker drained.");
        }
        return true;
    }

    public override void Dispose()
    {
        base.Dispose();
        _hardStop.Dispose();
    }

    private sealed class RunningJob : IDisposable
    {
        private int _leaseLost;

        public RunningJob(string queueClass, CancellationToken hardStopToken)
        {
            QueueClass = queueClass;
            LeaseCancellation = CancellationTokenSource
                .CreateLinkedTokenSource(hardStopToken);
        }

        public string QueueClass { get; }
        public CancellationTokenSource LeaseCancellation { get; }
        public Task? Task { get; set; }
        public bool LeaseLost => Volatile.Read(ref _leaseLost) == 1;

        public void MarkLeaseLost() => Volatile.Write(ref _leaseLost, 1);

        public void Dispose() => LeaseCancellation.Dispose();
    }

    private readonly record struct RunningKey(long JobId, long LeaseGeneration);
}
