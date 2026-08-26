using Flowbit.Service.Abstractions;

namespace Flowbit.Worker;

/// <summary>
/// Samples authoritative queue gauges independently of dispatcher activity, so
/// depth and oldest-age metrics continue to move while every slot is busy.
/// </summary>
public sealed class QueueTelemetryService(
    IServiceScopeFactory scopeFactory,
    WorkerTelemetry telemetry,
    ILogger<QueueTelemetryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await SampleJobQueueAsync(stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task SampleJobQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider
                .GetRequiredService<IWorkflowJobRepository>();
            telemetry.RecordQueueSnapshot(
                await repository.GetQueueStatisticsAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "Could not sample durable queue telemetry.");
        }
    }
}
