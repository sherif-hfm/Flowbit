extern alias FlowbitWorker;

using System.Threading.Channels;
using System.Diagnostics.Metrics;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RetentionCleanupService = FlowbitWorker::Flowbit.Worker.RetentionCleanupService;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

public sealed class RetentionWorkerTests
{
    [Fact]
    public async Task PausedAndIdleWorkUsesBackoffAndActiveWorkRemainsThrottled()
    {
        var repository = new StubRepository(
            new(true, false, true, Message: "Workflow queue is busy."),
            new(true, true, false, DeletedRows: 12),
            new(false, false, false));
        var clock = new ControlledTimeProvider();
        await using var services = new ServiceCollection()
            .AddSingleton<IRetentionRepository>(repository)
            .BuildServiceProvider();
        using var telemetry = new WorkerTelemetry();
        var options = new WorkerOptions
        {
            CompletedJobRetentionDays = 42,
            ResolvedIncidentRetentionDays = 120
        };
        using var worker = NewWorker(services, options, clock, telemetry);

        await worker.StartAsync(CancellationToken.None);
        var pausedDelay = await clock.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), pausedDelay.DueTime);
        Assert.Equal(1, repository.BatchCalls);
        Assert.Contains("flowbit_worker_retention_paused 1", telemetry.ExportPrometheus());

        pausedDelay.Fire();
        var activeDelay = await clock.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), activeDelay.DueTime);
        Assert.Equal(2, repository.BatchCalls);
        Assert.Contains("flowbit_worker_retention_rows_total 12", telemetry.ExportPrometheus());
        Assert.Contains("flowbit_worker_retention_batches_total 1", telemetry.ExportPrometheus());
        Assert.Contains("flowbit_worker_retention_paused 0", telemetry.ExportPrometheus());

        activeDelay.Fire();
        var idleDelay = await clock.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), idleDelay.DueTime);
        Assert.Equal(3, repository.BatchCalls);
        Assert.Equal(1, repository.InitializeCalls);
        Assert.Equal((42, 120), repository.InitializedDays);
        Assert.NotNull(repository.LastOptions);
        Assert.Equal(250, repository.LastOptions.BatchSize);
        Assert.Equal(100, repository.LastOptions.MaxRunnableJobs);
        Assert.Equal(30, repository.LastOptions.MaxQueueLagSeconds);
        Assert.False(string.IsNullOrWhiteSpace(repository.LastOptions.WorkerId));

        await worker.StopAsync(CancellationToken.None);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(3, repository.BatchCalls);
    }

    [Fact]
    public async Task FailedInitializationBacksOffAndRetriesBeforeAnyCleanup()
    {
        var repository = new StubRepository(new RetentionTickResult(false, false, false))
        {
            InitializationFailuresRemaining = 1
        };
        var clock = new ControlledTimeProvider();
        await using var services = new ServiceCollection()
            .AddSingleton<IRetentionRepository>(repository)
            .BuildServiceProvider();
        using var telemetry = new WorkerTelemetry();
        using var worker = NewWorker(services, new WorkerOptions(), clock, telemetry);

        await worker.StartAsync(CancellationToken.None);
        var retryDelay = await clock.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), retryDelay.DueTime);
        Assert.Equal(0, repository.BatchCalls);
        Assert.Contains("flowbit_worker_retention_failures_total 1", telemetry.ExportPrometheus());

        retryDelay.Fire();
        _ = await clock.NextAsync();
        Assert.Equal(2, repository.InitializeCalls);
        Assert.Equal(1, repository.BatchCalls);
        await worker.StopAsync(CancellationToken.None);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(0, 1000, 5000, 100, 30, "RetentionBatchSize")]
    [InlineData(1001, 1000, 5000, 100, 30, "RetentionBatchSize")]
    [InlineData(250, 0, 5000, 100, 30, "RetentionBatchDelayMilliseconds")]
    [InlineData(250, 1000, 500, 100, 30, "RetentionIdleDelayMilliseconds")]
    [InlineData(250, 1000, 5000, 0, 30, "RetentionMaxRunnableJobs")]
    [InlineData(250, 1000, 5000, 100, 0, "RetentionMaxQueueLagSeconds")]
    public void ExecutionBoundsCannotDisableBatchOrWorkflowLoadProtection(
        int batchSize, int delay, int idleDelay, int backlog, int lag, string field)
    {
        var options = new WorkerOptions
        {
            RetentionBatchSize = batchSize,
            RetentionBatchDelayMilliseconds = delay,
            RetentionIdleDelayMilliseconds = idleDelay,
            RetentionMaxRunnableJobs = backlog,
            RetentionMaxQueueLagSeconds = lag
        };
        Assert.Contains(field, Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public async Task FailedBatchBacksOffWithoutReinitializingPolicies()
    {
        var repository = new StubRepository(new RetentionTickResult(true, true, false, 1))
        {
            BatchFailuresRemaining = 1
        };
        var clock = new ControlledTimeProvider();
        await using var services = new ServiceCollection()
            .AddSingleton<IRetentionRepository>(repository)
            .BuildServiceProvider();
        using var telemetry = new WorkerTelemetry();
        using var worker = NewWorker(services, new WorkerOptions(), clock, telemetry);

        await worker.StartAsync(CancellationToken.None);
        var retryDelay = await clock.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), retryDelay.DueTime);
        retryDelay.Fire();
        var resumedDelay = await clock.NextAsync();

        Assert.Equal(TimeSpan.FromSeconds(1), resumedDelay.DueTime);
        Assert.Equal(1, repository.InitializeCalls);
        Assert.Equal(2, repository.BatchCalls);
        Assert.Contains("flowbit_worker_retention_failures_total 1", telemetry.ExportPrometheus());
        Assert.Contains("flowbit_worker_retention_rows_total 1", telemetry.ExportPrometheus());
        await worker.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(0, 90, "CompletedJobRetentionDays")]
    [InlineData(36501, 90, "CompletedJobRetentionDays")]
    [InlineData(30, 0, "ResolvedIncidentRetentionDays")]
    [InlineData(30, 36501, "ResolvedIncidentRetentionDays")]
    public void UnsupportedBootstrapPeriodsFailAtStartupInsteadOfDisablingCleanup(
        int jobs, int incidents, string field)
    {
        var options = new WorkerOptions
        {
            CompletedJobRetentionDays = jobs,
            ResolvedIncidentRetentionDays = incidents
        };
        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(field, exception.Message);
        Assert.Contains("between 1 and 36500", exception.Message);
    }

    [Theory]
    [InlineData(1, 36500)]
    [InlineData(36500, 1)]
    public void BootstrapPeriodsAcceptBothPublishedPolicyBounds(int jobs, int incidents) =>
        new WorkerOptions
        {
            CompletedJobRetentionDays = jobs,
            ResolvedIncidentRetentionDays = incidents
        }.Validate();

    [Fact]
    public void RetentionTelemetryPreservesOperationalCleanupCountersAndCategoryLabels()
    {
        var observedCategories = new List<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == WorkerTelemetry.MeterName
                && instrument.Name == "flowbit.worker.retention.rows")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            observedCategories.Add(tags.ToArray().FirstOrDefault(tag => tag.Key == "category").Value as string));
        listener.Start();
        using var telemetry = new WorkerTelemetry();

        telemetry.RecordRetention(new(true, true, false, 7)
        {
            Category = RetentionCategories.CompletedJobs,
            DeletedByTable = new Dictionary<string, long>
            {
                ["workflow_jobs"] = 2,
                ["workflow_job_attempts"] = 5
            }
        });
        telemetry.RecordRetention(new(true, true, false, 2)
        {
            Category = RetentionCategories.CompletedJobs,
            DeletedByTable = new Dictionary<string, long> { ["workflow_job_snapshots"] = 2 }
        });
        telemetry.RecordRetention(new(true, true, false, 3)
        {
            Category = RetentionCategories.ResolvedIncidents,
            DeletedByTable = new Dictionary<string, long> { ["workflow_incidents"] = 3 }
        });

        var metrics = telemetry.ExportPrometheus();
        Assert.Contains("flowbit_worker_cleanup_jobs_total 2", metrics);
        Assert.Contains("flowbit_worker_cleanup_attempts_total 5", metrics);
        Assert.Contains("flowbit_worker_cleanup_snapshots_total 2", metrics);
        Assert.Contains("flowbit_worker_cleanup_incidents_total 3", metrics);
        Assert.Contains("flowbit_worker_retention_rows_total 12", metrics);
        Assert.Equal([RetentionCategories.CompletedJobs, RetentionCategories.CompletedJobs,
            RetentionCategories.ResolvedIncidents], observedCategories);
    }

    private static RetentionCleanupService NewWorker(
        ServiceProvider services,
        WorkerOptions options,
        TimeProvider clock,
        WorkerTelemetry telemetry) => new(
        services.GetRequiredService<IServiceScopeFactory>(),
        options, clock, telemetry, NullLogger<RetentionCleanupService>.Instance);

    private sealed class StubRepository(params RetentionTickResult[] results) : IRetentionRepository
    {
        private int returnedResults;
        public int InitializationFailuresRemaining { get; set; }
        public int BatchFailuresRemaining { get; set; }
        public int InitializeCalls { get; private set; }
        public int BatchCalls { get; private set; }
        public (int Jobs, int Incidents) InitializedDays { get; private set; }
        public RetentionExecutionOptions? LastOptions { get; private set; }

        public Task InitializeAsync(int completedJobDays, int resolvedIncidentDays, CancellationToken cancellationToken)
        {
            InitializeCalls++;
            InitializedDays = (completedJobDays, resolvedIncidentDays);
            if (InitializationFailuresRemaining-- > 0)
                throw new InvalidOperationException("Database unavailable during initialization.");
            return Task.CompletedTask;
        }

        public Task<RetentionTickResult> ProcessBatchAsync(RetentionExecutionOptions options, CancellationToken cancellationToken)
        {
            LastOptions = options;
            BatchCalls++;
            if (BatchFailuresRemaining-- > 0)
                throw new InvalidOperationException("Database unavailable during a retention batch.");
            return Task.FromResult(results[returnedResults++]);
        }

        public Task<RetentionStatusDto> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RetentionPolicyDto> UpdatePolicyAsync(string category, UpdateRetentionPolicyRequest request, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RetentionPreviewDto> PreviewAsync(PreviewRetentionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RetentionRunDto> RequestRunAsync(string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly Channel<PendingTimer> timers = Channel.CreateUnbounded<PendingTimer>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new PendingTimer(callback, state, dueTime);
            Assert.True(timers.Writer.TryWrite(timer));
            return timer;
        }

        public Task<PendingTimer> NextAsync() =>
            timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class PendingTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private int disposed;
        public TimeSpan DueTime { get; } = dueTime;
        public void Fire()
        {
            if (Volatile.Read(ref disposed) == 0)
                callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
