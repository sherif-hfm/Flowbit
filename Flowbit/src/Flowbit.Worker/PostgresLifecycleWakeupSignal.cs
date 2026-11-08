using System.Threading.Channels;
using Flowbit.Infrastructure.Data;
using Npgsql;

namespace Flowbit.Worker;

/// <summary>
/// Listens on <c>flowbit_lifecycle_events</c>, which capture, fanout, retry, and archive notify after they commit,
/// and wakes the fanout loop and the dispatcher. It is only a hint; both loops poll PostgreSQL authoritatively.
/// </summary>
public sealed class PostgresLifecycleWakeupSignal(
    WebhookDataSource dataSource,
    ILogger<PostgresLifecycleWakeupSignal> logger) : BackgroundService
{
    public const string ChannelName = "flowbit_lifecycle_events";

    private readonly Channel<bool> fanout = CreateChannel();
    private readonly Channel<bool> dispatch = CreateChannel();

    public void Signal()
    {
        fanout.Writer.TryWrite(true);
        dispatch.Writer.TryWrite(true);
    }

    /// <summary>Wakes only the dispatcher, for example when a local send frees a slot.</summary>
    public void SignalDispatcher() => dispatch.Writer.TryWrite(true);

    public Task WaitForFanoutAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        WaitAsync(fanout, timeout, cancellationToken);

    public Task WaitForDispatchAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        WaitAsync(dispatch, timeout, cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.DataSource.OpenConnectionAsync(stoppingToken);
                connection.Notification += (_, _) => Signal();
                await using (var listen = new NpgsqlCommand($"LISTEN {ChannelName}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "PostgreSQL lifecycle notification listener disconnected ({FailureType}); polling remains authoritative.",
                    exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private static Channel<bool> CreateChannel() =>
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    private static async Task WaitAsync(Channel<bool> channel, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            _ = await channel.Reader.ReadAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Normal polling timeout.
        }
    }
}
