extern alias FlowbitWorker;

using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using WorkerConnectionPool = FlowbitWorker::Flowbit.Worker.WorkerConnectionPool;

namespace Flowbit.Tests;

public sealed class WorkerConnectionPoolTests
{
    [Fact]
    public void DefaultConcurrencyKeepsTheSixteenConnectionPool()
    {
        var size = WorkerConnectionPool.Size("Host=db;Username=flowbit", maxConcurrency: 8);

        var connection = new NpgsqlConnectionStringBuilder(size.ConnectionString);
        Assert.Equal(16, size.MaxPoolSize);
        Assert.Equal(16, connection.MaxPoolSize);
        Assert.Equal("Flowbit.Worker", connection.ApplicationName);
        Assert.False(size.IsConstrained);
    }

    [Fact]
    public void HigherConcurrencyGrowsThePoolWithItsReserve()
    {
        var size = WorkerConnectionPool.Size("Host=db;Username=flowbit", maxConcurrency: 32);

        Assert.Equal(40, size.MaxPoolSize);
        Assert.Equal(40, new NpgsqlConnectionStringBuilder(size.ConnectionString).MaxPoolSize);
        Assert.False(size.IsConstrained);
    }

    [Fact]
    public void ExplicitSmallerPoolIsKeptAndReportedAsConstrained()
    {
        var size = WorkerConnectionPool.Size(
            "Host=db;Username=flowbit;Maximum Pool Size=10;Minimum Pool Size=12",
            maxConcurrency: 8);

        var connection = new NpgsqlConnectionStringBuilder(size.ConnectionString);
        Assert.Equal(10, size.MaxPoolSize);
        Assert.Equal(10, connection.MaxPoolSize);
        Assert.Equal(10, connection.MinPoolSize);
        Assert.Equal(16, size.RecommendedMaxPoolSize);
        Assert.True(size.IsConstrained);
    }

    [Fact]
    public void ApplyRewritesTheConfiguredConnectionStringIncludingTheLegacyKey()
    {
        var configuration = new ConfigurationManager();
        configuration["ConnectionStrings:WorkflowEngine"] = "Host=db;Username=flowbit;Maximum Pool Size=100";

        var size = WorkerConnectionPool.Apply(configuration, maxConcurrency: 20);

        Assert.Equal(28, size.MaxPoolSize);
        Assert.Equal(
            28,
            new NpgsqlConnectionStringBuilder(
                configuration.GetConnectionString("WorkflowEngine")).MaxPoolSize);
        Assert.Throws<InvalidOperationException>(() =>
            WorkerConnectionPool.Apply(new ConfigurationManager(), maxConcurrency: 8));
    }
}
