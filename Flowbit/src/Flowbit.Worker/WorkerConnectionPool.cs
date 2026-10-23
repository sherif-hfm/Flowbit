using Npgsql;

namespace Flowbit.Worker;

/// <summary>
/// Sizes the Worker's runtime connection pool from its dispatcher slots. Each
/// slot can hold a transaction while its lease guard briefly needs another
/// connection; the reserve also covers the notification listener, queue
/// acquisition, queue telemetry, and timer-start reconciliation.
/// </summary>
public static class WorkerConnectionPool
{
    public const int MinimumMaxPoolSize = 16;
    public const int ReservedConnections = 8;

    public static WorkerConnectionPoolSize Apply(
        IConfiguration configuration,
        int maxConcurrency)
    {
        var key = configuration.GetConnectionString("Flowbit") is not null
            ? "Flowbit"
            : "WorkflowEngine";
        var configured = configuration.GetConnectionString(key)
            ?? throw new InvalidOperationException(
                "Connection string 'Flowbit' is missing (legacy key 'WorkflowEngine' is also supported).");
        var size = Size(configured, maxConcurrency);
        configuration[$"ConnectionStrings:{key}"] = size.ConnectionString;
        return size;
    }

    public static WorkerConnectionPoolSize Size(
        string connectionString,
        int maxConcurrency)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var recommended = checked(maxConcurrency + ReservedConnections);
        // Never exceed an operator's explicit Maximum Pool Size; an otherwise
        // unconstrained pool keeps the historical 16-connection floor.
        var maximum = Math.Min(
            builder.MaxPoolSize,
            Math.Max(MinimumMaxPoolSize, recommended));
        builder.MinPoolSize = Math.Min(builder.MinPoolSize, maximum);
        builder.MaxPoolSize = maximum;
        builder.ApplicationName = "Flowbit.Worker";
        return new WorkerConnectionPoolSize(
            builder.ConnectionString,
            maximum,
            recommended);
    }
}

public sealed record WorkerConnectionPoolSize(
    string ConnectionString,
    int MaxPoolSize,
    int RecommendedMaxPoolSize)
{
    public bool IsConstrained => MaxPoolSize < RecommendedMaxPoolSize;
}
