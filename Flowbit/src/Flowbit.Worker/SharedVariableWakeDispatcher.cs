using System.Collections.Concurrent;
using System.Diagnostics;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;

namespace Flowbit.Worker;

/// <summary>
/// Expands deployment-wide shared-variable revisions into fenced conditional
/// deliveries and processes each affected instance independently. PostgreSQL
/// owns lease time, retry scheduling, attempt exhaustion, and incidents; this
/// service only owns bounded local execution and lease guarding.
/// </summary>
public sealed class SharedVariableWakeDispatcher(
    IServiceScopeFactory scopeFactory,
    WorkerOptions options,
    WorkerTelemetry telemetry,
    ILogger<SharedVariableWakeDispatcher> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<RunningKey, RunningWake> _running = new();
    private readonly string _workerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:shared:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Shared-variable wake worker {WorkerId} started with concurrency {Concurrency} ({ExpansionConcurrency} expansion slots).",
            _workerId,
            options.SharedWakeMaxConcurrency,
            options.SharedWakeExpansionConcurrency);

        try
        {
            await Task.Delay(
                Random.Shared.Next(0, Math.Max(1, options.PollMilliseconds)),
                stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                ReapCompleted();
                var acquired = 0;
                var free = options.SharedWakeMaxConcurrency - _running.Count;
                var runningExpansions = _running.Values.Count(item => item.IsExpansion);
                var expansionFree = Math.Max(
                    0,
                    options.SharedWakeExpansionConcurrency - runningExpansions);

                if (free > 0 && expansionFree > 0)
                {
                    var expansions = await LeaseExpansionsAsync(
                        Math.Min(
                            Math.Min(free, expansionFree),
                            options.SharedWakeBatchSize),
                        stoppingToken);
                    acquired += expansions.Count;
                    foreach (var expansion in expansions)
                    {
                        StartExpansion(expansion);
                    }
                }

                free = options.SharedWakeMaxConcurrency - _running.Count;
                if (free > 0)
                {
                    var deliveries = await LeaseDeliveriesAsync(
                        Math.Min(free, options.SharedWakeBatchSize),
                        stoppingToken);
                    acquired += deliveries.Count;
                    foreach (var delivery in deliveries)
                    {
                        StartDelivery(delivery);
                    }
                }

                if (acquired > 0
                    && _running.Count < options.SharedWakeMaxConcurrency)
                {
                    continue;
                }

                var delay = TimeSpan.FromMilliseconds(
                    options.PollMilliseconds
                    + Random.Shared.Next(0, Math.Max(1, options.PollMilliseconds / 4)));
                await Task.Delay(delay, stoppingToken);
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

    private async Task<IReadOnlyList<SharedVariableWakeRecord>> LeaseExpansionsAsync(
        int maxCount,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider
                .GetRequiredService<ISharedVariableRepository>();
            var leased = await repository.LeaseWakeExpansionsAsync(
                LeaseRequest(maxCount),
                cancellationToken);
            telemetry.RecordSharedWakeAcquisition(
                expansion: true,
                leased.Count,
                Stopwatch.GetElapsedTime(started));
            if (leased.Count <= maxCount)
            {
                return leased;
            }

            logger.LogError(
                "Shared-variable expansion leasing returned {ActualCount} rows for a {RequestedCount}-row capacity. Excess leases will recover after expiry.",
                leased.Count,
                maxCount);
            return leased.Take(maxCount).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Shared-variable wake expansion acquisition failed.");
            return [];
        }
    }

    private async Task<IReadOnlyList<SharedVariableWakeDeliveryRecord>> LeaseDeliveriesAsync(
        int maxCount,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider
                .GetRequiredService<ISharedVariableRepository>();
            var leased = await repository.LeaseWakeDeliveriesAsync(
                LeaseRequest(maxCount),
                cancellationToken);
            telemetry.RecordSharedWakeAcquisition(
                expansion: false,
                leased.Count,
                Stopwatch.GetElapsedTime(started));
            if (leased.Count <= maxCount)
            {
                return leased;
            }

            logger.LogError(
                "Shared-variable delivery leasing returned {ActualCount} rows for a {RequestedCount}-row capacity. Excess leases will recover after expiry.",
                leased.Count,
                maxCount);
            return leased.Take(maxCount).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Shared-variable wake delivery acquisition failed.");
            return [];
        }
    }

    private void StartExpansion(SharedVariableWakeRecord wake)
    {
        var key = new RunningKey(
            SharedVariableWakeWorkKinds.Expansion,
            wake.Id,
            wake.LeaseGeneration);
        var running = new RunningWake(isExpansion: true);
        if (!_running.TryAdd(key, running))
        {
            running.Dispose();
            logger.LogWarning(
                "Duplicate local shared-variable wake expansion {WakeId} generation {LeaseGeneration} was ignored; its lease will recover after expiry.",
                wake.Id,
                wake.LeaseGeneration);
            return;
        }

        telemetry.SharedWakeStarted(expansion: true);
        running.Task = ProcessExpansionAsync(wake, running);
    }

    private void StartDelivery(SharedVariableWakeDeliveryRecord delivery)
    {
        var key = new RunningKey(
            SharedVariableWakeWorkKinds.Delivery,
            delivery.Id,
            delivery.LeaseGeneration);
        var running = new RunningWake(isExpansion: false);
        if (!_running.TryAdd(key, running))
        {
            running.Dispose();
            logger.LogWarning(
                "Duplicate local shared-variable wake delivery {DeliveryId} generation {LeaseGeneration} was ignored; its lease will recover after expiry.",
                delivery.Id,
                delivery.LeaseGeneration);
            return;
        }

        telemetry.SharedWakeStarted(expansion: false);
        running.Task = ProcessDeliveryAsync(delivery, running);
    }

    private async Task ProcessExpansionAsync(
        SharedVariableWakeRecord wake,
        RunningWake running)
    {
        var started = Stopwatch.GetTimestamp();
        var fence = Fence(wake);
        var heartbeat = GuardLeaseAsync(fence, running);
        SharedVariableWakeFailure? failure = null;
        var shouldFinalize = false;
        var succeeded = false;
        var cursorTokenId = wake.ExpansionCursorTokenId;
        try
        {
            try
            {
                while (!running.WorkCancellation.IsCancellationRequested)
                {
                    SharedVariableWakeExpansionPageResult page;
                    await using (var scope = scopeFactory.CreateAsyncScope())
                    {
                        var repository = scope.ServiceProvider
                            .GetRequiredService<ISharedVariableRepository>();
                        page = await repository.ExpandWakePageAsync(
                            fence,
                            options.SharedWakeExpansionPageSize,
                            running.WorkCancellation.Token);
                    }

                    if (page.Disposition == SharedVariableWakeExpansionPageDispositions.LeaseLost)
                    {
                        await LoseLeaseAsync(
                            fence,
                            running,
                            "the expansion page fence was rejected");
                        break;
                    }
                    if (page.Disposition != SharedVariableWakeExpansionPageDispositions.Page)
                    {
                        throw new InvalidOperationException(
                            $"Wake expansion page returned unsupported disposition '{page.Disposition}'.");
                    }

                    if (page.CreatedCount < 0)
                    {
                        throw new InvalidOperationException(
                            "Wake expansion page returned a negative delivery count.");
                    }
                    if (!page.IsComplete && page.CursorTokenId <= cursorTokenId)
                    {
                        throw new InvalidOperationException(
                            $"Wake expansion page did not advance its durable cursor from token {cursorTokenId}.");
                    }

                    telemetry.RecordSharedWakeDeliveriesCreated(page.CreatedCount);
                    cursorTokenId = page.CursorTokenId;
                    if (page.IsComplete)
                    {
                        shouldFinalize = true;
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
                when (running.WorkCancellation.IsCancellationRequested)
            {
                // Lease loss or the bounded shutdown drain owns recovery.
            }
            catch (Exception exception)
            {
                failure = Failure("shared_variable_wake_expansion_failed", exception);
                shouldFinalize = true;
                logger.LogError(
                    exception,
                    "Shared-variable wake expansion {WakeId} failed.",
                    wake.Id);
            }

            if (shouldFinalize && !running.ShouldLeaveLeaseForRecovery)
            {
                succeeded = await FinalizeExpansionAsync(fence, failure, running);
            }
        }
        finally
        {
            try
            {
                await StopLeaseGuardAsync(fence, running, heartbeat);
            }
            finally
            {
                telemetry.SharedWakeFinished(
                    expansion: true,
                    succeeded,
                    Stopwatch.GetElapsedTime(started));
            }
        }
    }

    private async Task ProcessDeliveryAsync(
        SharedVariableWakeDeliveryRecord delivery,
        RunningWake running)
    {
        var started = Stopwatch.GetTimestamp();
        var fence = Fence(delivery);
        var heartbeat = GuardLeaseAsync(fence, running);
        SharedVariableWakeFailure? failure = null;
        var shouldFinalize = false;
        var succeeded = false;
        try
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider
                    .GetRequiredService<ISharedVariableWakeDeliveryProcessor>();
                await processor.ProcessAsync(
                    delivery,
                    running.WorkCancellation.Token);
                shouldFinalize = true;
            }
            catch (OperationCanceledException)
                when (running.WorkCancellation.IsCancellationRequested)
            {
                // Lease loss or the bounded shutdown drain owns recovery.
            }
            catch (Exception exception)
            {
                failure = Failure("shared_variable_wake_delivery_failed", exception);
                shouldFinalize = true;
                logger.LogError(
                    exception,
                    "Shared-variable wake delivery {DeliveryId} for instance {InstanceId} failed.",
                    delivery.Id,
                    delivery.InstanceId);
            }

            if (shouldFinalize && !running.ShouldLeaveLeaseForRecovery)
            {
                succeeded = await FinalizeDeliveryAsync(fence, failure, running);
            }
        }
        finally
        {
            try
            {
                await StopLeaseGuardAsync(fence, running, heartbeat);
            }
            finally
            {
                telemetry.SharedWakeFinished(
                    expansion: false,
                    succeeded,
                    Stopwatch.GetElapsedTime(started));
            }
        }
    }

    private async Task GuardLeaseAsync(
        SharedVariableWakeFence fence,
        RunningWake running)
    {
        var renewedAt = Stopwatch.GetTimestamp();
        var remaining = options.LeaseDuration;
        var lastHeartbeatAt = renewedAt;
        using var timer = new PeriodicTimer(options.LeaseCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(running.GuardCancellation.Token))
            {
                var shouldHeartbeat =
                    Stopwatch.GetElapsedTime(lastHeartbeatAt) >= options.HeartbeatInterval;
                try
                {
                    using var commandCancellation = CancellationTokenSource
                        .CreateLinkedTokenSource(running.GuardCancellation.Token);
                    commandCancellation.CancelAfter(options.HeartbeatCommandTimeout);
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var repository = scope.ServiceProvider
                        .GetRequiredService<ISharedVariableRepository>();
                    var alive = shouldHeartbeat
                        ? await repository.HeartbeatWakeAsync(
                            fence,
                            options.LeaseDuration,
                            commandCancellation.Token)
                        : await repository.IsWakeLeaseAliveAsync(
                            fence,
                            commandCancellation.Token);
                    if (shouldHeartbeat)
                    {
                        if (alive)
                        {
                            telemetry.RecordSharedWakeHeartbeatSucceeded(fence.WorkKind);
                        }
                        else
                        {
                            telemetry.RecordSharedWakeHeartbeatFailed(fence.WorkKind);
                        }
                    }
                    if (!alive)
                    {
                        await LoseLeaseAsync(fence, running, "the database rejected the lease fence");
                        return;
                    }

                    if (shouldHeartbeat)
                    {
                        renewedAt = Stopwatch.GetTimestamp();
                        lastHeartbeatAt = renewedAt;
                        remaining = options.LeaseDuration;
                    }
                }
                catch (OperationCanceledException)
                    when (running.GuardCancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    if (shouldHeartbeat)
                    {
                        telemetry.RecordSharedWakeHeartbeatFailed(fence.WorkKind);
                    }
                    logger.LogWarning(
                        exception,
                        "Lease guard failed for shared-variable {WorkKind} {WorkId} generation {LeaseGeneration}; it will retry before the local lease deadline.",
                        fence.WorkKind,
                        fence.Id,
                        fence.LeaseGeneration);
                    if (Stopwatch.GetElapsedTime(renewedAt) < remaining)
                    {
                        continue;
                    }

                    await LoseLeaseAsync(
                        fence,
                        running,
                        "the lease could not be renewed before its local deadline");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
            when (running.GuardCancellation.IsCancellationRequested)
        {
            // Normal completion, lease loss, or shutdown cancellation.
        }
    }

    private async Task StopLeaseGuardAsync(
        SharedVariableWakeFence fence,
        RunningWake running,
        Task heartbeat)
    {
        await running.GuardCancellation.CancelAsync();
        try
        {
            await heartbeat;
        }
        catch (OperationCanceledException)
        {
            // Expected after processing stops.
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Lease guard for shared-variable {WorkKind} {WorkId} generation {LeaseGeneration} stopped unexpectedly.",
                fence.WorkKind,
                fence.Id,
                fence.LeaseGeneration);
            if (running.TryMarkLeaseLost())
            {
                telemetry.RecordSharedWakeLeaseLost();
            }
        }
    }

    private async Task<bool> FinalizeExpansionAsync(
        SharedVariableWakeFence fence,
        SharedVariableWakeFailure? failure,
        RunningWake running)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider
                .GetRequiredService<ISharedVariableRepository>();
            var result = await repository.CompleteWakeExpansionAsync(
                fence,
                failure,
                running.WorkCancellation.Token);
            return HandleFinalization(fence, result, running);
        }
        catch (OperationCanceledException)
            when (running.WorkCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not finalize shared-variable wake expansion {WakeId}; its durable lease will recover after expiry.",
                fence.Id);
            return false;
        }
    }

    private async Task<bool> FinalizeDeliveryAsync(
        SharedVariableWakeFence fence,
        SharedVariableWakeFailure? failure,
        RunningWake running)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider
                .GetRequiredService<ISharedVariableRepository>();
            var result = await repository.CompleteWakeDeliveryAsync(
                fence,
                failure,
                running.WorkCancellation.Token);
            return HandleFinalization(fence, result, running);
        }
        catch (OperationCanceledException)
            when (running.WorkCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not finalize shared-variable wake delivery {DeliveryId}; its durable lease will recover after expiry.",
                fence.Id);
            return false;
        }
    }

    private bool HandleFinalization(
        SharedVariableWakeFence fence,
        SharedVariableWakeFinalizationResult result,
        RunningWake running)
    {
        switch (result.Disposition)
        {
            case SharedVariableWakeFinalizationDispositions.Completed:
                return true;
            case SharedVariableWakeFinalizationDispositions.RetryScheduled:
                telemetry.RecordSharedWakeRetry();
                logger.LogWarning(
                    "Shared-variable {WorkKind} {WorkId} was scheduled for retry at {AvailableAt}.",
                    fence.WorkKind,
                    fence.Id,
                    result.AvailableAt);
                return false;
            case SharedVariableWakeFinalizationDispositions.IncidentOpened:
                telemetry.RecordSharedWakeFinalizationIncident();
                logger.LogError(
                    "Shared-variable {WorkKind} {WorkId} exhausted its attempts and opened incident {IncidentId}.",
                    fence.WorkKind,
                    fence.Id,
                    result.IncidentId);
                return false;
            case SharedVariableWakeFinalizationDispositions.LeaseLost:
                if (running.TryMarkLeaseLost())
                {
                    telemetry.RecordSharedWakeLeaseLost();
                }
                logger.LogWarning(
                    "Shared-variable {WorkKind} {WorkId} lost its lease before finalization.",
                    fence.WorkKind,
                    fence.Id);
                return false;
            default:
                logger.LogError(
                    "Shared-variable {WorkKind} {WorkId} returned unsupported finalization disposition {Disposition}.",
                    fence.WorkKind,
                    fence.Id,
                    result.Disposition);
                return false;
        }
    }

    private async Task LoseLeaseAsync(
        SharedVariableWakeFence fence,
        RunningWake running,
        string reason)
    {
        if (!running.TryMarkLeaseLost())
        {
            return;
        }

        telemetry.RecordSharedWakeLeaseLost();
        logger.LogWarning(
            "Worker {WorkerId} lost shared-variable {WorkKind} {WorkId} generation {LeaseGeneration}: {Reason}; cancelling local execution.",
            _workerId,
            fence.WorkKind,
            fence.Id,
            fence.LeaseGeneration,
            reason);
        await running.WorkCancellation.CancelAsync();
    }

    private SharedVariableWakeLeaseRequest LeaseRequest(int maxCount) => new(
        _workerId,
        maxCount,
        options.LeaseDuration);

    private SharedVariableWakeFence Fence(SharedVariableWakeRecord wake) => new(
        SharedVariableWakeWorkKinds.Expansion,
        wake.Id,
        _workerId,
        wake.LeaseToken,
        wake.LeaseGeneration);

    private SharedVariableWakeFence Fence(SharedVariableWakeDeliveryRecord delivery) => new(
        SharedVariableWakeWorkKinds.Delivery,
        delivery.Id,
        _workerId,
        delivery.LeaseToken,
        delivery.LeaseGeneration);

    private static SharedVariableWakeFailure Failure(string code, Exception exception)
    {
        // Exception messages remain in the structured error log above, where
        // normal log access and retention controls apply. Durable queue state is
        // exposed through the administrator API, so persist only a bounded type
        // discriminator and the stable worker failure code.
        var exceptionType = string.Concat(exception.GetType().Name
            .Where(character => char.IsAsciiLetterOrDigit(character)
                || character is '_' or '.')
            .Take(200));
        if (exceptionType.Length == 0)
        {
            exceptionType = nameof(Exception);
        }

        return new SharedVariableWakeFailure(
            code,
            $"Failure type: {exceptionType}.");
    }

    private void ReapCompleted()
    {
        foreach (var pair in _running)
        {
            if (pair.Value.Task?.IsCompleted != true
                || !_running.TryRemove(pair.Key, out var removed))
            {
                continue;
            }

            if (removed.Task?.IsFaulted == true)
            {
                logger.LogError(
                    removed.Task.Exception,
                    "Shared-variable {WorkKind} {WorkId} generation {LeaseGeneration} task faulted after processing.",
                    pair.Key.WorkKind,
                    pair.Key.Id,
                    pair.Key.LeaseGeneration);
            }
            removed.Dispose();
        }
    }

    private async Task DrainAsync()
    {
        var running = _running.Values
            .Where(item => item.Task is not null)
            .ToArray();
        if (running.Length == 0)
        {
            return;
        }

        var all = Task.WhenAll(running.Select(item => item.Task!));
        var drainTimeout = Task.Delay(
            TimeSpan.FromSeconds(options.ShutdownDrainSeconds));
        if (await Task.WhenAny(all, drainTimeout) == all)
        {
            await ObserveDrainAsync(all);
            ReapCompleted();
            return;
        }

        logger.LogWarning(
            "Worker shutdown reached its {TimeoutSeconds}s shared-variable wake drain bound with {Count} items still running. Local work will be cancelled and durable leases will recover after expiry.",
            options.ShutdownDrainSeconds,
            running.Count(item => item.Task?.IsCompleted != true));
        foreach (var item in running.Where(item => item.Task?.IsCompleted != true))
        {
            item.MarkShutdownCancellation();
        }
        await Task.WhenAll(running
            .Where(item => item.Task?.IsCompleted != true)
            .Select(item => item.WorkCancellation.CancelAsync()));

        var cancellationBound = Task.Delay(
            options.HeartbeatCommandTimeout + TimeSpan.FromSeconds(1));
        if (await Task.WhenAny(all, cancellationBound) == all)
        {
            await ObserveDrainAsync(all);
            ReapCompleted();
            return;
        }

        logger.LogWarning(
            "{Count} shared-variable wake tasks did not observe cancellation before worker shutdown completed.",
            running.Count(item => item.Task?.IsCompleted != true));
    }

    private async Task ObserveDrainAsync(Task all)
    {
        try
        {
            await all;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "One or more shared-variable wake tasks faulted while the worker drained.");
        }
    }

    private sealed class RunningWake : IDisposable
    {
        private int _leaseLost;
        private int _shutdownCancellation;

        public RunningWake(bool isExpansion)
        {
            IsExpansion = isExpansion;
            GuardCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                WorkCancellation.Token);
        }

        public bool IsExpansion { get; }
        public CancellationTokenSource WorkCancellation { get; } = new();
        public CancellationTokenSource GuardCancellation { get; }
        public Task? Task { get; set; }
        public bool ShouldLeaveLeaseForRecovery =>
            Volatile.Read(ref _leaseLost) == 1
            || Volatile.Read(ref _shutdownCancellation) == 1;

        public bool TryMarkLeaseLost() =>
            Interlocked.Exchange(ref _leaseLost, 1) == 0;

        public void MarkShutdownCancellation() =>
            Interlocked.Exchange(ref _shutdownCancellation, 1);

        public void Dispose()
        {
            GuardCancellation.Dispose();
            WorkCancellation.Dispose();
        }
    }

    private readonly record struct RunningKey(
        string WorkKind,
        long Id,
        long LeaseGeneration);
}
