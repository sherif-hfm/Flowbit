using Flowbit.Service.Abstractions;

namespace Flowbit.Worker;

/// <summary>
/// Settles the signal broadcasts past their expiry that no delivery job is working on, such as
/// a job in an incident, in retry backoff, or behind other activity jobs (EVENT-04E). Each pass
/// reads the database's time once and settles at most <see cref="MaxBroadcastsPerPass"/>
/// broadcasts, each in its own transaction that claims one with <c>SKIP LOCKED</c>, so replicas
/// never queue behind each other. A pass that fails logs a warning, and the next pass runs.
/// </summary>
public sealed class SignalBroadcastExpiryService(
    IServiceScopeFactory scopeFactory,
    WorkerOptions options,
    ILogger<SignalBroadcastExpiryService> logger,
    DatabaseSchemaGate? schemaGate = null) : BackgroundService
{
    public const int MaxBroadcastsPerPass = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (schemaGate is not null)
        {
            await schemaGate.WaitUntilReadyAsync(stoppingToken);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SignalExpirySweepSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Signal broadcast expiry sweep failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }

    /// <summary>Runs one pass and returns how many broadcasts it settled.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        // Read once: PostgreSQL does not use clock_timestamp() as an index condition, and a
        // value that is slightly old can only delay a claim to the next pass. The settle
        // statement still decides with clock_timestamp().
        DateTimeOffset databaseNow;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            databaseNow = await scope.ServiceProvider
                .GetRequiredService<ISignalBroadcastRepository>()
                .GetDatabaseTimeAsync(cancellationToken);
        }

        var settled = 0;
        while (settled < MaxBroadcastsPerPass)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = scopeFactory.CreateAsyncScope();
            var result = await scope.ServiceProvider
                .GetRequiredService<ISignalBroadcastExpirer>()
                .ExpireNextDueAsync(databaseNow, cancellationToken);
            if (result is null)
            {
                break;
            }

            settled++;
        }

        return settled;
    }
}
