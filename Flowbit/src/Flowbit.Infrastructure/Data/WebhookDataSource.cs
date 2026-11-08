using Npgsql;

namespace Flowbit.Infrastructure.Data;

/// <summary>
/// The webhook queue's own small pool, so receiver outages and fanout bursts never take connections from workflow
/// jobs, timers, or call results. Its transactions are short and none is held during remote I/O.
/// </summary>
public sealed class WebhookDataSource : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; }

    public WebhookDataSource(string connectionString, int maxPoolSize)
    {
        var options = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = maxPoolSize,
            MinPoolSize = 0,
            ApplicationName = "Flowbit.Webhooks"
        };
        DataSource = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}
