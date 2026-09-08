using Npgsql;

namespace Flowbit.Infrastructure.Data;

/// <summary>Maintenance has its own small pool; it cannot consume the runtime pool's connections.</summary>
public sealed class RetentionDataSource : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; }

    public RetentionDataSource(string connectionString)
    {
        var options = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 2, MinPoolSize = 0, ApplicationName = "Flowbit.Retention",
            CommandTimeout = 5, Timeout = 5
        };
        DataSource = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
    }

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}
