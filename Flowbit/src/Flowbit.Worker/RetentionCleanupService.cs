using Flowbit.Service.Abstractions;

namespace Flowbit.Worker;

/// <summary>
/// Processes one bounded retention batch at a time. Scheduling, the replica
/// fence, and workflow-load checks live in PostgreSQL through the repository.
/// </summary>
public sealed class RetentionCleanupService(
    IServiceScopeFactory scopeFactory,
    WorkerOptions options,
    TimeProvider timeProvider,
    WorkerTelemetry telemetry,
    ILogger<RetentionCleanupService> logger) : BackgroundService
{
    private readonly string workerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialized = false;
        var wasPaused = false;
        var executionOptions = new RetentionExecutionOptions
        {
            WorkerId = workerId,
            BatchSize = options.RetentionBatchSize,
            MaxRunnableJobs = options.RetentionMaxRunnableJobs,
            MaxQueueLagSeconds = options.RetentionMaxQueueLagSeconds
        };

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(options.RetentionIdleDelayMilliseconds);
                try
                {
                    // Each call gets fresh policy and run state; the retention
                    // repository uses its own small database connection pool.
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var repository = scope.ServiceProvider.GetRequiredService<IRetentionRepository>();
                    if (!initialized)
                    {
                        await repository.InitializeAsync(
                            options.CompletedJobRetentionDays,
                            options.ResolvedIncidentRetentionDays,
                            stoppingToken);
                        initialized = true;
                    }

                    var result = await repository.ProcessBatchAsync(executionOptions, stoppingToken);
                    telemetry.RecordRetention(result);
                    if (result.IsPaused != wasPaused)
                    {
                        logger.LogInformation(
                            "Retention cleanup {State}. {Message}",
                            result.IsPaused ? "paused" : "resumed",
                            result.Message);
                        wasPaused = result.IsPaused;
                    }
                    if (result.DeletedRows > 0)
                    {
                        logger.LogDebug("Retention cleanup deleted {Rows} rows in one batch.", result.DeletedRows);
                    }

                    if (result.HasActiveRun && !result.IsPaused)
                    {
                        delay = TimeSpan.FromMilliseconds(options.RetentionBatchDelayMilliseconds);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    telemetry.RecordRetentionFailure();
                    logger.LogWarning(exception, "Retention cleanup could not complete its batch; it will retry.");
                }

                await Task.Delay(delay, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown; unfinished database work remains resumable.
        }
    }
}
