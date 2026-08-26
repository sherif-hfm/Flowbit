extern alias FlowbitWorker;

using System.Collections.Concurrent;
using System.Reflection;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ISharedVariableWakeDeliveryProcessor = FlowbitWorker::Flowbit.Worker.ISharedVariableWakeDeliveryProcessor;
using JobCleanupService = FlowbitWorker::Flowbit.Worker.JobCleanupService;
using QueueTelemetryService = FlowbitWorker::Flowbit.Worker.QueueTelemetryService;
using SharedVariableWakeDispatcher = FlowbitWorker::Flowbit.Worker.SharedVariableWakeDispatcher;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

public sealed class SharedVariableWakeDispatcherTests
{
    [Fact]
    public async Task SharedWakeOpenIncidentGaugeIsSampledIndependently()
    {
        var state = new RepositoryState { OpenIncidentCount = 11 };
        var jobs = Proxy<IWorkflowJobRepository>(static (method, _) =>
            method.Name == nameof(IWorkflowJobRepository.GetQueueStatisticsAsync)
                ? Task.FromException<WorkflowJobQueueStatisticsRecord>(
                    new InvalidOperationException("job telemetry failed"))
                : throw new NotSupportedException($"Unexpected repository call {method.Name}."));
        await using var services = new ServiceCollection()
            .AddSingleton(jobs)
            .AddSingleton(CreateRepository(state))
            .BuildServiceProvider();
        using var telemetry = new WorkerTelemetry();
        using var sampler = new QueueTelemetryService(
            services.GetRequiredService<IServiceScopeFactory>(),
            telemetry,
            NullLogger<QueueTelemetryService>.Instance);

        await sampler.StartAsync(CancellationToken.None);
        await state.IncidentsSampled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sampler.StopAsync(CancellationToken.None);

        var query = Assert.IsType<SharedVariableWakeIncidentQuery>(state.IncidentQuery);
        Assert.Equal(SharedVariableWakeIncidentStatuses.Open, query.Status);
        Assert.Equal(1, query.Limit);
        Assert.Contains(
            "flowbit_worker_shared_wake_incidents_open 11",
            telemetry.ExportPrometheus(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedWakeCleanupUsesItsOwnRetentionAndSurvivesJobCleanupFailure()
    {
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        var state = new RepositoryState();
        var jobs = Proxy<IWorkflowJobRepository>(static (method, _) =>
            method.Name == nameof(IWorkflowJobRepository.CleanupAsync)
                ? Task.FromException<WorkflowJobCleanupResult>(
                    new InvalidOperationException("job cleanup failed"))
                : throw new NotSupportedException($"Unexpected repository call {method.Name}."));
        await using var services = new ServiceCollection()
            .AddSingleton(jobs)
            .AddSingleton(CreateRepository(state))
            .BuildServiceProvider();
        using var telemetry = new WorkerTelemetry();
        var options = NewOptions();
        options.SharedWakeCompletedRetentionDays = 14;
        options.SharedWakeResolvedIncidentRetentionDays = 45;
        options.SharedWakeCleanupBatchSize = 123;
        using var cleanup = new JobCleanupService(
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            new FixedTimeProvider(now),
            telemetry,
            NullLogger<JobCleanupService>.Instance);

        await cleanup.StartAsync(CancellationToken.None);
        await state.CleanupCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cleanup.StopAsync(CancellationToken.None);

        Assert.Equal(now.AddDays(-14), state.CompletedBefore);
        Assert.Equal(now.AddDays(-45), state.ResolvedIncidentsBefore);
        Assert.Equal(123, state.CleanupBatchSize);
        var metrics = telemetry.ExportPrometheus();
        Assert.Contains(
            "flowbit_worker_shared_wake_cleanup_wakes_total 2",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_cleanup_deliveries_total 3",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_cleanup_incidents_total 1",
            metrics,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpansionPagesBeforeFinalizationAndUsesDatabaseAuthoredLeaseContract()
    {
        var state = new RepositoryState(expansions: [NewWake(41)]);
        var page = 0;
        state.ExpandPage = (_, _, _) => Task.FromResult<SharedVariableWakeExpansionPageResult>(
            Interlocked.Increment(ref page) == 1
                ? new(false, 2, 10)
                : new(true, 1, 11));
        await using var services = Services(state, new DelegateDeliveryProcessor(
            static (_, _) => Task.CompletedTask));
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = NewDispatcher(services, telemetry);

        await dispatcher.StartAsync(CancellationToken.None);
        await state.ExpansionFinalized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.NotEmpty(state.ExpansionLeaseRequests);
        var request = state.ExpansionLeaseRequests.First();
        Assert.Equal(1, request.MaxCount);
        Assert.Equal(TimeSpan.FromSeconds(15), request.LeaseDuration);
        Assert.DoesNotContain(
            typeof(SharedVariableWakeLeaseRequest).GetProperties(),
            property => string.Equals(property.Name, "Now", StringComparison.Ordinal));
        Assert.Equal(2, state.ExpansionPageCalls);
        Assert.All(state.ExpansionPageSizes, value => Assert.Equal(3, value));
        var completed = Assert.Single(state.ExpansionCompletions);
        Assert.Null(completed.Failure);
        Assert.Equal(SharedVariableWakeWorkKinds.Expansion, completed.Fence.WorkKind);
        Assert.Equal(request.WorkerId, completed.Fence.WorkerId);
        Assert.Equal(41, completed.Fence.Id);
        Assert.Equal(1, completed.Fence.LeaseGeneration);

        var metrics = telemetry.ExportPrometheus();
        Assert.Contains(
            "flowbit_worker_shared_wake_deliveries_created_total 3",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_completed_total 1",
            metrics,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpansionConcurrencyIsBoundedIndependentlyFromTotalCapacity()
    {
        var state = new RepositoryState(
            expansions: [NewWake(1), NewWake(2), NewWake(3)]);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        state.ExpandPage = async (_, _, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref active);
            SetMaximum(ref maximumActive, current);
            firstStarted.TrySetResult();
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return new SharedVariableWakeExpansionPageResult(true, 0, 0);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        };
        await using var services = Services(state, new DelegateDeliveryProcessor(
            static (_, _) => Task.CompletedTask));
        using var telemetry = new WorkerTelemetry();
        var options = NewOptions();
        options.SharedWakeMaxConcurrency = 3;
        options.SharedWakeExpansionConcurrency = 1;
        using var dispatcher = NewDispatcher(services, telemetry, options);

        await dispatcher.StartAsync(CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(250);

        Assert.Equal(1, Volatile.Read(ref maximumActive));
        Assert.Equal(1, Volatile.Read(ref active));

        release.TrySetResult();
        await state.ThreeExpansionsFinalized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(1, maximumActive);
        Assert.Equal(3, state.ExpansionCompletions.Count);
    }

    [Fact]
    public async Task NonAdvancingExpansionCursorIsFailureFinalizedInsteadOfSpinning()
    {
        var state = new RepositoryState(expansions: [NewWake(31)]);
        state.ExpandPage = static (_, _, _) =>
            Task.FromResult<SharedVariableWakeExpansionPageResult>(
                new(false, 0, 0));
        await using var services = Services(state, new DelegateDeliveryProcessor(
            static (_, _) => Task.CompletedTask));
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = NewDispatcher(services, telemetry);

        await dispatcher.StartAsync(CancellationToken.None);
        await state.ExpansionFinalized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.StopAsync(CancellationToken.None);

        var completed = Assert.Single(state.ExpansionCompletions);
        var failure = Assert.IsType<SharedVariableWakeFailure>(completed.Failure);
        Assert.Equal("shared_variable_wake_expansion_failed", failure.Code);
        Assert.Equal(1, state.ExpansionPageCalls);
    }

    [Fact]
    public async Task ExplicitExpansionPageLeaseLossCancelsWorkWithoutFinalization()
    {
        var state = new RepositoryState(expansions: [NewWake(32)]);
        var pageCalled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        state.ExpandPage = (_, _, _) =>
        {
            pageCalled.TrySetResult();
            return Task.FromResult(new SharedVariableWakeExpansionPageResult(
                IsComplete: false,
                CreatedCount: 0,
                CursorTokenId: 0,
                Disposition: SharedVariableWakeExpansionPageDispositions.LeaseLost));
        };
        await using var services = Services(state, new DelegateDeliveryProcessor(
            static (_, _) => Task.CompletedTask));
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = NewDispatcher(services, telemetry);

        await dispatcher.StartAsync(CancellationToken.None);
        await pageCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Empty(state.ExpansionCompletions);
        Assert.Contains(
            "flowbit_worker_shared_wake_leases_lost_total 1",
            telemetry.ExportPrometheus(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeaseLossCancelsLocalExpansionAndSkipsFinalization()
    {
        var state = new RepositoryState(expansions: [NewWake(51)])
        {
            LeaseAlive = false
        };
        var cancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        state.ExpandPage = async (_, _, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new SharedVariableWakeExpansionPageResult(true, 0, 0);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
        };
        await using var services = Services(state, new DelegateDeliveryProcessor(
            static (_, _) => Task.CompletedTask));
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = NewDispatcher(services, telemetry);

        await dispatcher.StartAsync(CancellationToken.None);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.True(state.LeaseCheckCount > 0);
        Assert.Empty(state.ExpansionCompletions);
        Assert.Contains(
            "flowbit_worker_shared_wake_leases_lost_total 1",
            telemetry.ExportPrometheus(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task LeaseGuardDoesNotCompareDatabaseExpiryToApplicationWallClock(
        int observedExpiryOffsetHours)
    {
        var wake = NewWake(61) with
        {
            // Models a database timestamp observed with +/- one hour of
            // application clock skew. The database fence remains authoritative.
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddHours(observedExpiryOffsetHours)
        };
        var state = new RepositoryState(expansions: [wake]);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = 0;
        state.ExpandPage = async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return new SharedVariableWakeExpansionPageResult(true, 0, 0);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref cancelled, 1);
                throw;
            }
        };
        await using var services = Services(state, new DelegateDeliveryProcessor(
            static (_, _) => Task.CompletedTask));
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = NewDispatcher(services, telemetry);

        await dispatcher.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(250);

        Assert.Equal(0, Volatile.Read(ref cancelled));
        Assert.True(state.LeaseCheckCount > 0);

        release.TrySetResult();
        await state.ExpansionFinalized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeliveryFailureIsHandedBackToDatabaseRetryPolicy()
    {
        var state = new RepositoryState(deliveries: [NewDelivery(71)]);
        state.CompleteDelivery = (fence, failure, _) =>
        {
            state.DeliveryFinalized.TrySetResult();
            return Task.FromResult(new SharedVariableWakeFinalizationResult(
                SharedVariableWakeFinalizationDispositions.RetryScheduled,
                AvailableAt: DateTimeOffset.UtcNow.AddMinutes(1)));
        };
        const string message =
            "postgres://workflow:super-secret-password@database.internal/flowbit";
        await using var services = Services(
            state,
            new DelegateDeliveryProcessor((_, _) =>
                Task.FromException(new InvalidOperationException(message))));
        using var telemetry = new WorkerTelemetry();
        using var dispatcher = NewDispatcher(services, telemetry);

        await dispatcher.StartAsync(CancellationToken.None);
        await state.DeliveryFinalized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.StopAsync(CancellationToken.None);

        var completed = Assert.Single(state.DeliveryCompletions);
        var failure = Assert.IsType<SharedVariableWakeFailure>(completed.Failure);
        Assert.Equal("shared_variable_wake_delivery_failed", failure.Code);
        Assert.Equal("Failure type: InvalidOperationException.", failure.Description);
        Assert.DoesNotContain("super-secret-password", failure.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(message, failure.Description, StringComparison.Ordinal);
        Assert.Equal(SharedVariableWakeWorkKinds.Delivery, completed.Fence.WorkKind);
        Assert.Contains(
            "flowbit_worker_shared_wake_retries_scheduled_total 1",
            telemetry.ExportPrometheus(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeaseGuardRemainsActiveUntilDeliveryFinalizationCompletes()
    {
        var state = new RepositoryState(deliveries: [NewDelivery(76)]);
        var finalizationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinalization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        state.CompleteDelivery = async (_, _, cancellationToken) =>
        {
            finalizationStarted.TrySetResult();
            await releaseFinalization.Task.WaitAsync(cancellationToken);
            state.DeliveryFinalized.TrySetResult();
            return new SharedVariableWakeFinalizationResult(
                SharedVariableWakeFinalizationDispositions.Completed);
        };
        await using var services = Services(state, new DelegateDeliveryProcessor(
            static (_, _) => Task.CompletedTask));
        using var telemetry = new WorkerTelemetry();
        var options = NewOptions();
        options.HeartbeatSeconds = 1;
        using var dispatcher = NewDispatcher(services, telemetry, options);

        await dispatcher.StartAsync(CancellationToken.None);
        await finalizationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.FirstHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(state.HeartbeatCount > 0);

        releaseFinalization.TrySetResult();
        await state.DeliveryFinalized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeartbeatCommandOutcomesAreRecordedByWorkKind(bool expansion)
    {
        var state = new RepositoryState(
            expansions: expansion ? [NewWake(77)] : null,
            deliveries: expansion ? null : [NewDelivery(78)]);
        var processingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProcessing = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHeartbeat = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatAttempts = 0;
        state.HeartbeatWake = (_, _) =>
        {
            if (Interlocked.Increment(ref heartbeatAttempts) == 1)
            {
                return Task.FromException<bool>(
                    new InvalidOperationException("transient heartbeat failure"));
            }

            secondHeartbeat.TrySetResult();
            return Task.FromResult(true);
        };
        if (expansion)
        {
            state.ExpandPage = async (_, _, cancellationToken) =>
            {
                processingStarted.TrySetResult();
                await releaseProcessing.Task.WaitAsync(cancellationToken);
                return new SharedVariableWakeExpansionPageResult(true, 0, 0);
            };
        }

        var processor = new DelegateDeliveryProcessor(async (_, cancellationToken) =>
        {
            processingStarted.TrySetResult();
            await releaseProcessing.Task.WaitAsync(cancellationToken);
        });
        await using var services = Services(state, processor);
        using var telemetry = new WorkerTelemetry();
        var options = NewOptions();
        options.HeartbeatSeconds = 1;
        using var dispatcher = NewDispatcher(services, telemetry, options);

        await dispatcher.StartAsync(CancellationToken.None);
        await processingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await secondHeartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseProcessing.TrySetResult();
        if (expansion)
        {
            await state.ExpansionFinalized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        else
        {
            await state.DeliveryFinalized.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        await dispatcher.StopAsync(CancellationToken.None);

        var workKind = expansion ? "expansion" : "delivery";
        var metrics = telemetry.ExportPrometheus();
        Assert.Contains(
            $"flowbit_worker_shared_wake_heartbeat_failed_total{{work_kind=\"{workKind}\"}} 1",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            $"flowbit_worker_shared_wake_heartbeat_succeeded_total{{work_kind=\"{workKind}\"}} 1",
            metrics,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GracefulShutdownKeepsLeaseGuardAliveWhileWorkDrains()
    {
        var state = new RepositoryState(deliveries: [NewDelivery(81)]);
        var processor = new BlockingDeliveryProcessor();
        await using var services = Services(state, processor);
        using var telemetry = new WorkerTelemetry();
        var options = NewOptions();
        options.ShutdownDrainSeconds = 2;
        using var dispatcher = NewDispatcher(services, telemetry, options);

        await dispatcher.StartAsync(CancellationToken.None);
        await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopping = dispatcher.StopAsync(CancellationToken.None);
        await Task.Delay(250);

        Assert.False(stopping.IsCompleted);
        Assert.False(processor.WasCancelled);

        processor.Release.TrySetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Single(state.DeliveryCompletions);
        Assert.Null(state.DeliveryCompletions.Single().Failure);
    }

    [Fact]
    public async Task ShutdownDrainTimeoutCancelsWorkWithoutFailureFinalization()
    {
        var state = new RepositoryState(deliveries: [NewDelivery(91)]);
        var processor = new BlockingDeliveryProcessor();
        await using var services = Services(state, processor);
        using var telemetry = new WorkerTelemetry();
        var options = NewOptions();
        options.ShutdownDrainSeconds = 1;
        using var dispatcher = NewDispatcher(services, telemetry, options);

        await dispatcher.StartAsync(CancellationToken.None);
        await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.StopAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(processor.WasCancelled);
        Assert.Empty(state.DeliveryCompletions);
    }

    private static ServiceProvider Services(
        RepositoryState state,
        ISharedVariableWakeDeliveryProcessor processor) =>
        new ServiceCollection()
            .AddSingleton(CreateRepository(state))
            .AddSingleton(processor)
            .BuildServiceProvider();

    private static SharedVariableWakeDispatcher NewDispatcher(
        ServiceProvider services,
        WorkerTelemetry telemetry,
        WorkerOptions? options = null) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            options ?? NewOptions(),
            telemetry,
            NullLogger<SharedVariableWakeDispatcher>.Instance);

    private static WorkerOptions NewOptions() => new()
    {
        LeaseSeconds = 15,
        HeartbeatSeconds = 5,
        LeaseCheckMilliseconds = 100,
        HeartbeatCommandTimeoutMilliseconds = 100,
        PollMilliseconds = 50,
        IdleBackoffMilliseconds = 100,
        SharedWakeMaxConcurrency = 1,
        SharedWakeExpansionConcurrency = 1,
        SharedWakeBatchSize = 1,
        SharedWakeExpansionPageSize = 3,
        ShutdownDrainSeconds = 2
    };

    private static SharedVariableWakeRecord NewWake(long id)
    {
        var now = DateTimeOffset.UtcNow;
        return new SharedVariableWakeRecord(
            id,
            3,
            "catalog.threshold",
            5,
            7,
            SharedVariableWakeStatuses.Leased,
            Guid.NewGuid(),
            1,
            now.AddSeconds(15),
            1,
            5,
            0);
    }

    private static SharedVariableWakeDeliveryRecord NewDelivery(long id)
    {
        var now = DateTimeOffset.UtcNow;
        return new SharedVariableWakeDeliveryRecord(
            id,
            20,
            3,
            "catalog.threshold",
            7,
            101,
            102,
            103,
            Guid.NewGuid(),
            104,
            SharedVariableWakeStatuses.Leased,
            Guid.NewGuid(),
            1,
            now.AddSeconds(15),
            1,
            5);
    }

    private static ISharedVariableRepository CreateRepository(RepositoryState state) =>
        Proxy<ISharedVariableRepository>(state.Invoke);

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static void SetMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (observed >= value
                || Interlocked.CompareExchange(ref maximum, value, observed) == observed)
            {
                return;
            }
        }
    }

    public class StubProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args ?? []);
    }

    private sealed class RepositoryState
    {
        private readonly ConcurrentQueue<SharedVariableWakeRecord> _expansions;
        private readonly ConcurrentQueue<SharedVariableWakeDeliveryRecord> _deliveries;
        private int _leaseCheckCount;
        private int _heartbeatCount;
        private int _expansionPageCalls;

        public RepositoryState(
            IEnumerable<SharedVariableWakeRecord>? expansions = null,
            IEnumerable<SharedVariableWakeDeliveryRecord>? deliveries = null)
        {
            _expansions = new(expansions ?? []);
            _deliveries = new(deliveries ?? []);
        }

        public bool LeaseAlive { get; init; } = true;
        public long OpenIncidentCount { get; init; }
        public int LeaseCheckCount => Volatile.Read(ref _leaseCheckCount);
        public int HeartbeatCount => Volatile.Read(ref _heartbeatCount);
        public int ExpansionPageCalls => Volatile.Read(ref _expansionPageCalls);
        public ConcurrentQueue<SharedVariableWakeLeaseRequest> ExpansionLeaseRequests { get; } = new();
        public ConcurrentQueue<int> ExpansionPageSizes { get; } = new();
        public ConcurrentQueue<(SharedVariableWakeFence Fence, SharedVariableWakeFailure? Failure)> ExpansionCompletions { get; } = new();
        public ConcurrentQueue<(SharedVariableWakeFence Fence, SharedVariableWakeFailure? Failure)> DeliveryCompletions { get; } = new();
        public TaskCompletionSource ExpansionFinalized { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThreeExpansionsFinalized { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeliveryFinalized { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource IncidentsSampled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstHeartbeat { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset? CompletedBefore { get; private set; }
        public DateTimeOffset? ResolvedIncidentsBefore { get; private set; }
        public int? CleanupBatchSize { get; private set; }
        public SharedVariableWakeIncidentQuery? IncidentQuery { get; private set; }
        public Func<SharedVariableWakeFence, int, CancellationToken, Task<SharedVariableWakeExpansionPageResult>> ExpandPage { get; set; } =
            static (_, _, _) => Task.FromResult<SharedVariableWakeExpansionPageResult>(
                new(true, 0, 0));
        public Func<SharedVariableWakeFence, CancellationToken, Task<bool>>? HeartbeatWake { get; set; }
        public Func<SharedVariableWakeFence, SharedVariableWakeFailure?, CancellationToken, Task<SharedVariableWakeFinalizationResult>>? CompleteDelivery { get; set; }

        public object? Invoke(MethodInfo method, object?[] arguments) => method.Name switch
        {
            nameof(ISharedVariableRepository.LeaseWakeExpansionsAsync) =>
                LeaseExpansions((SharedVariableWakeLeaseRequest)arguments[0]!),
            nameof(ISharedVariableRepository.LeaseWakeDeliveriesAsync) =>
                LeaseDeliveries((SharedVariableWakeLeaseRequest)arguments[0]!),
            nameof(ISharedVariableRepository.ExpandWakePageAsync) => Expand(
                (SharedVariableWakeFence)arguments[0]!,
                (int)arguments[1]!,
                (CancellationToken)arguments[2]!),
            nameof(ISharedVariableRepository.IsWakeLeaseAliveAsync) => IsAlive(
                heartbeat: false),
            nameof(ISharedVariableRepository.HeartbeatWakeAsync) => Heartbeat(
                (SharedVariableWakeFence)arguments[0]!,
                (CancellationToken)arguments[2]!),
            nameof(ISharedVariableRepository.CompleteWakeExpansionAsync) =>
                CompleteExpansion(
                    (SharedVariableWakeFence)arguments[0]!,
                    (SharedVariableWakeFailure?)arguments[1]),
            nameof(ISharedVariableRepository.CompleteWakeDeliveryAsync) =>
                CompleteDeliveryInvocation(
                    (SharedVariableWakeFence)arguments[0]!,
                    (SharedVariableWakeFailure?)arguments[1],
                    (CancellationToken)arguments[2]!),
            nameof(ISharedVariableRepository.CleanupWakeOutboxAsync) =>
                Cleanup(
                    (DateTimeOffset)arguments[0]!,
                    (DateTimeOffset)arguments[1]!,
                    (int)arguments[2]!),
            nameof(ISharedVariableRepository.SearchWakeIncidentsAsync) =>
                SearchIncidents((SharedVariableWakeIncidentQuery)arguments[0]!),
            _ => throw new NotSupportedException($"Unexpected repository call {method.Name}.")
        };

        private Task<IReadOnlyList<SharedVariableWakeRecord>> LeaseExpansions(
            SharedVariableWakeLeaseRequest request)
        {
            ExpansionLeaseRequests.Enqueue(request);
            var result = Take(_expansions, request.MaxCount);
            return Task.FromResult<IReadOnlyList<SharedVariableWakeRecord>>(result);
        }

        private Task<IReadOnlyList<SharedVariableWakeDeliveryRecord>> LeaseDeliveries(
            SharedVariableWakeLeaseRequest request) =>
            Task.FromResult<IReadOnlyList<SharedVariableWakeDeliveryRecord>>(
                Take(_deliveries, request.MaxCount));

        private Task<SharedVariableWakeExpansionPageResult> Expand(
            SharedVariableWakeFence fence,
            int pageSize,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _expansionPageCalls);
            ExpansionPageSizes.Enqueue(pageSize);
            return ExpandPage(fence, pageSize, cancellationToken);
        }

        private Task<bool> IsAlive(bool heartbeat)
        {
            Interlocked.Increment(ref _leaseCheckCount);
            if (heartbeat)
            {
                Interlocked.Increment(ref _heartbeatCount);
                FirstHeartbeat.TrySetResult();
            }
            return Task.FromResult(LeaseAlive);
        }

        private Task<bool> Heartbeat(
            SharedVariableWakeFence fence,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _leaseCheckCount);
            Interlocked.Increment(ref _heartbeatCount);
            FirstHeartbeat.TrySetResult();
            return HeartbeatWake?.Invoke(fence, cancellationToken)
                ?? Task.FromResult(LeaseAlive);
        }

        private Task<SharedVariableWakeFinalizationResult> CompleteExpansion(
            SharedVariableWakeFence fence,
            SharedVariableWakeFailure? failure)
        {
            ExpansionCompletions.Enqueue((fence, failure));
            ExpansionFinalized.TrySetResult();
            if (ExpansionCompletions.Count == 3)
            {
                ThreeExpansionsFinalized.TrySetResult();
            }
            return Task.FromResult(new SharedVariableWakeFinalizationResult(
                SharedVariableWakeFinalizationDispositions.Completed));
        }

        private Task<SharedVariableWakeFinalizationResult> CompleteDeliveryInvocation(
            SharedVariableWakeFence fence,
            SharedVariableWakeFailure? failure,
            CancellationToken cancellationToken)
        {
            DeliveryCompletions.Enqueue((fence, failure));
            if (CompleteDelivery is not null)
            {
                return CompleteDelivery(fence, failure, cancellationToken);
            }
            DeliveryFinalized.TrySetResult();
            return Task.FromResult(new SharedVariableWakeFinalizationResult(
                SharedVariableWakeFinalizationDispositions.Completed));
        }

        private Task<SharedVariableWakeCleanupResult> Cleanup(
            DateTimeOffset completedBefore,
            DateTimeOffset resolvedIncidentsBefore,
            int batchSize)
        {
            CompletedBefore = completedBefore;
            ResolvedIncidentsBefore = resolvedIncidentsBefore;
            CleanupBatchSize = batchSize;
            CleanupCalled.TrySetResult();
            return Task.FromResult(new SharedVariableWakeCleanupResult(2, 3, 1));
        }

        private Task<(IReadOnlyList<SharedVariableWakeIncidentRecord> Items, long TotalCount)>
            SearchIncidents(SharedVariableWakeIncidentQuery query)
        {
            IncidentQuery = query;
            IncidentsSampled.TrySetResult();
            return Task.FromResult((
                (IReadOnlyList<SharedVariableWakeIncidentRecord>)[],
                OpenIncidentCount));
        }

        private static List<T> Take<T>(ConcurrentQueue<T> source, int maxCount)
        {
            var result = new List<T>(maxCount);
            while (result.Count < maxCount && source.TryDequeue(out var item))
            {
                result.Add(item);
            }
            return result;
        }
    }

    private sealed class DelegateDeliveryProcessor(
        Func<SharedVariableWakeDeliveryRecord, CancellationToken, Task> process) :
        ISharedVariableWakeDeliveryProcessor
    {
        public Task ProcessAsync(
            SharedVariableWakeDeliveryRecord delivery,
            CancellationToken cancellationToken) =>
            process(delivery, cancellationToken);
    }

    private sealed class BlockingDeliveryProcessor : ISharedVariableWakeDeliveryProcessor
    {
        private int _wasCancelled;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCancelled => Volatile.Read(ref _wasCancelled) == 1;

        public async Task ProcessAsync(
            SharedVariableWakeDeliveryRecord delivery,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _wasCancelled, 1);
                throw;
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
