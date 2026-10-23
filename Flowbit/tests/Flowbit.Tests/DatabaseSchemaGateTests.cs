extern alias FlowbitWorker;

using Flowbit.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DatabaseSchemaGate = FlowbitWorker::Flowbit.Worker.DatabaseSchemaGate;
using DatabaseSchemaStatus = FlowbitWorker::Flowbit.Worker.DatabaseSchemaStatus;
using TimerStartReconciliationService = FlowbitWorker::Flowbit.Worker.TimerStartReconciliationService;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerReadinessHealthCheck = FlowbitWorker::Flowbit.Worker.WorkerReadinessHealthCheck;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

public sealed class DatabaseSchemaGateTests
{
    [Fact]
    public async Task GateOpensOnlyAfterPendingMigrationsAreApplied()
    {
        var checks = 0;
        using var gate = new DatabaseSchemaGate(
            _ => Task.FromResult(Interlocked.Increment(ref checks) < 3
                ? new DatabaseSchemaStatus(["20990101000000_Pending"], [])
                : new DatabaseSchemaStatus([], [])),
            TimeProvider.System,
            NullLogger<DatabaseSchemaGate>.Instance,
            TimeSpan.FromMilliseconds(20));
        using var telemetry = new WorkerTelemetry();
        var readiness = new WorkerReadinessHealthCheck(telemetry, gate);

        var beforeStart = await readiness.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, beforeStart.Status);
        Assert.Equal(-1, gate.PendingMigrationCount);

        await gate.StartAsync(CancellationToken.None);
        await gate.WaitUntilReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(gate.IsReady);
        Assert.Equal(0, gate.PendingMigrationCount);
        Assert.Equal(3, Volatile.Read(ref checks));
        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReadinessReportsPendingMigrationsWhileProcessingWaits()
    {
        using var gate = new DatabaseSchemaGate(
            _ => Task.FromResult(new DatabaseSchemaStatus(
                ["20990101000000_First", "20990101000001_Second"],
                [])),
            TimeProvider.System,
            NullLogger<DatabaseSchemaGate>.Instance,
            TimeSpan.FromMinutes(1));
        using var telemetry = new WorkerTelemetry();
        // A successful queue query alone must not make the Worker ready.
        telemetry.RecordAcquisition([], TimeSpan.Zero, DateTimeOffset.UtcNow);
        var readiness = new WorkerReadinessHealthCheck(telemetry, gate);

        await gate.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => gate.PendingMigrationCount == 2);

        var result = await readiness.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("2 pending database migrations", result.Description, StringComparison.Ordinal);
        using var waiting = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.WaitUntilReadyAsync(waiting.Token));

        await gate.StopAsync(CancellationToken.None);
        Assert.False(gate.IsReady);
    }

    [Fact]
    public async Task NewerDatabaseMigrationsDoNotBlockProcessing()
    {
        using var gate = new DatabaseSchemaGate(
            _ => Task.FromResult(new DatabaseSchemaStatus(
                [],
                ["20990101000000_FromANewerBuild"])),
            TimeProvider.System,
            NullLogger<DatabaseSchemaGate>.Instance,
            TimeSpan.FromMilliseconds(20));
        using var telemetry = new WorkerTelemetry();
        telemetry.RecordAcquisition([], TimeSpan.Zero, DateTimeOffset.UtcNow);

        await gate.StartAsync(CancellationToken.None);
        await gate.WaitUntilReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var result = await new WorkerReadinessHealthCheck(telemetry, gate)
            .CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnreadableMigrationHistoryKeepsProcessingPausedUntilARetrySucceeds()
    {
        var checks = 0;
        using var gate = new DatabaseSchemaGate(
            _ => Interlocked.Increment(ref checks) == 1
                ? Task.FromException<DatabaseSchemaStatus>(
                    new InvalidOperationException("Migration history is unavailable."))
                : Task.FromResult(new DatabaseSchemaStatus([], [])),
            TimeProvider.System,
            NullLogger<DatabaseSchemaGate>.Instance,
            TimeSpan.FromMilliseconds(20));

        await gate.StartAsync(CancellationToken.None);
        await gate.WaitUntilReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(gate.IsReady);
        Assert.Equal(2, Volatile.Read(ref checks));
        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RepeatedWarningsAreLoggedAtMostOncePerMinutePerKind()
    {
        var checks = 0;
        var logger = new CountingLogger<DatabaseSchemaGate>();
        using var gate = new DatabaseSchemaGate(
            _ => Interlocked.Increment(ref checks) <= 5
                ? Task.FromException<DatabaseSchemaStatus>(
                    new InvalidOperationException("Migration history is unavailable."))
                : Task.FromResult(new DatabaseSchemaStatus(["20990101000000_Pending"], [])),
            TimeProvider.System,
            logger,
            TimeSpan.FromMilliseconds(10));

        await gate.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref checks) >= 10);
        await gate.StopAsync(CancellationToken.None);

        // Five history failures and several pending checks: one warning each.
        Assert.False(gate.IsReady);
        Assert.Equal(2, logger.WarningCount);
    }

    [Fact]
    public async Task TimerStartReconciliationWaitsForTheSchemaGate()
    {
        var schemaReady = 0;
        var passes = 0;
        using var gate = new DatabaseSchemaGate(
            _ => Task.FromResult(Volatile.Read(ref schemaReady) == 1
                ? new DatabaseSchemaStatus([], [])
                : new DatabaseSchemaStatus(["20990101000000_Pending"], [])),
            TimeProvider.System,
            NullLogger<DatabaseSchemaGate>.Instance,
            TimeSpan.FromMilliseconds(20));
        // Every reconciliation pass starts by resolving its DbContext; count
        // those starts and end the pass there.
        await using var services = new ServiceCollection()
            .AddScoped<AppDbContext>(_ =>
            {
                Interlocked.Increment(ref passes);
                throw new InvalidOperationException("Reconciliation pass started.");
            })
            .BuildServiceProvider();
        using var telemetry = new WorkerTelemetry();
        using var reconciler = new TimerStartReconciliationService(
            services.GetRequiredService<IServiceScopeFactory>(),
            new WorkerOptions(),
            TimeProvider.System,
            telemetry,
            NullLogger<TimerStartReconciliationService>.Instance,
            gate);

        await gate.StartAsync(CancellationToken.None);
        await reconciler.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, Volatile.Read(ref passes));

        Volatile.Write(ref schemaReady, 1);
        await WaitUntilAsync(() => Volatile.Read(ref passes) > 0);
        await reconciler.StopAsync(CancellationToken.None);
        await gate.StopAsync(CancellationToken.None);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The condition was not reached in time.");
            await Task.Delay(10);
        }
    }

    private sealed class CountingLogger<T> : ILogger<T>
    {
        private int _warnings;

        public int WarningCount => Volatile.Read(ref _warnings);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Interlocked.Increment(ref _warnings);
            }
        }
    }
}

[Collection(PostgresApiCollection.Name)]
public sealed class DatabaseSchemaGateMigrationHistoryTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task MigratedDatabaseHasNoPendingOrUnknownMigrations()
    {
        var status = await DatabaseSchemaGate.ReadMigrationStatusAsync(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            CancellationToken.None);

        Assert.Empty(status.PendingMigrations);
        Assert.Empty(status.UnknownAppliedMigrations);
    }
}
