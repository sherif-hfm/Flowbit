using Flowbit.Service.Abstractions;

namespace Flowbit.Worker;

public sealed class JobCleanupService(
    IServiceScopeFactory scopeFactory,
    WorkerOptions options,
    TimeProvider timeProvider,
    WorkerTelemetry telemetry,
    ILogger<JobCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Durable job retention cleanup failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CleanupJobsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Durable job retention cleanup failed.");
        }

        try
        {
            await CleanupSharedVariableWakesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Shared-variable wake retention cleanup failed.");
        }
    }

    private async Task CleanupJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider
            .GetRequiredService<IWorkflowJobRepository>();
        var now = timeProvider.GetUtcNow();
        var result = await repository.CleanupAsync(
            now.AddDays(-options.CompletedJobRetentionDays),
            now.AddDays(-options.ResolvedIncidentRetentionDays),
            options.CleanupBatchSize,
            cancellationToken);
        telemetry.RecordCleanup(
            result.JobsDeleted,
            result.IncidentsDeleted,
            result.AttemptsDeleted,
            result.SnapshotsDeleted);
        if (result.JobsDeleted + result.IncidentsDeleted > 0)
        {
            logger.LogInformation(
                "Job cleanup deleted {Jobs} jobs, {Attempts} attempts, {Snapshots} snapshots, and {Incidents} incidents.",
                result.JobsDeleted,
                result.AttemptsDeleted,
                result.SnapshotsDeleted,
                result.IncidentsDeleted);
        }
    }

    private async Task CleanupSharedVariableWakesAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider
            .GetRequiredService<ISharedVariableRepository>();
        var now = timeProvider.GetUtcNow();
        var result = await repository.CleanupWakeOutboxAsync(
            now.AddDays(-options.SharedWakeCompletedRetentionDays),
            now.AddDays(-options.SharedWakeResolvedIncidentRetentionDays),
            options.SharedWakeCleanupBatchSize,
            cancellationToken);
        telemetry.RecordSharedWakeCleanup(
            result.WakesDeleted,
            result.DeliveriesDeleted,
            result.IncidentsDeleted);
        if (result.WakesDeleted + result.DeliveriesDeleted + result.IncidentsDeleted > 0)
        {
            logger.LogInformation(
                "Shared-variable wake cleanup deleted {Wakes} wakes, {Deliveries} deliveries, and {Incidents} incidents.",
                result.WakesDeleted,
                result.DeliveriesDeleted,
                result.IncidentsDeleted);
        }
    }
}
