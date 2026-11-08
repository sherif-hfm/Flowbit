using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;

namespace Flowbit.Worker;

/// <summary>
/// Turns recorded lifecycle events into deliveries, settles archived subscriptions, returns expired leases to
/// the queue, and retires signing-key material. It keeps running while HTTP delivery is disabled, so captured
/// work is preserved and archival can finish.
/// </summary>
public sealed class LifecycleEventFanoutService(
    IServiceScopeFactory scopeFactory,
    PostgresLifecycleWakeupSignal wakeup,
    WebhookOptions options,
    WebhookTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<LifecycleEventFanoutService> logger,
    DatabaseSchemaGate? schemaGate = null) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (schemaGate is not null)
            {
                await schemaGate.WaitUntilReadyAsync(stoppingToken);
            }
            var nextSweep = DateTimeOffset.MinValue;
            var nextSnapshot = DateTimeOffset.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                var busy = false;
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var repository = scope.ServiceProvider.GetRequiredService<IWebhookDispatchRepository>();
                    var fanout = await repository.FanoutAsync(options.FanoutBatchSize, stoppingToken);
                    telemetry.RecordFanout(fanout);
                    var discarded = await repository.SettleArchivedSubscriptionsAsync(
                        options.FanoutBatchSize,
                        options.SecretRemovalGrace,
                        stoppingToken);
                    telemetry.RecordArchiveDiscards(discarded);
                    var recovered = await repository.RecoverExpiredLeasesAsync(options.FanoutBatchSize, stoppingToken);
                    telemetry.RecordLeaseRecoveries(recovered);
                    if (recovered > 0)
                    {
                        logger.LogInformation("Recovered {Count} expired webhook delivery leases.", recovered);
                    }
                    var now = timeProvider.GetUtcNow();
                    if (now >= nextSweep)
                    {
                        await repository.SweepSigningKeysAsync(options.SecretRemovalGrace, stoppingToken);
                        nextSweep = now + SweepInterval;
                    }
                    if (now >= nextSnapshot)
                    {
                        telemetry.RecordSnapshot(await repository.GetQueueSnapshotAsync(stoppingToken));
                        nextSnapshot = now + SnapshotInterval;
                    }
                    if (fanout.Deliveries > 0)
                    {
                        wakeup.SignalDispatcher();
                    }
                    telemetry.RecordLoopSuccess(WebhookTelemetry.FanoutLoop);
                    busy = fanout.Events >= options.FanoutBatchSize
                        || discarded >= options.FanoutBatchSize
                        || recovered >= options.FanoutBatchSize;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    telemetry.RecordLoopFailure(WebhookTelemetry.FanoutLoop);
                    logger.LogError(exception, "Lifecycle event fanout failed; it will be retried.");
                }
                if (!busy)
                {
                    await wakeup.WaitForFanoutAsync(options.PollInterval, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
