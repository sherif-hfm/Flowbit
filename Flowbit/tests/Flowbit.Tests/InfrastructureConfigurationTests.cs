using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.DependencyInjection;
using Flowbit.Service.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class InfrastructureConfigurationTests
{
    [Fact]
    public void AddInfrastructurePrefersFlowbitConnectionString()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Flowbit"] = ConnectionString("flowbit_primary"),
            ["ConnectionStrings:WorkflowEngine"] = ConnectionString("legacy_fallback")
        });

        using var provider = BuildProvider(configuration);

        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        Assert.Equal("flowbit_primary", new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Database);
    }

    [Fact]
    public void AddInfrastructureSupportsLegacyConnectionString()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:WorkflowEngine"] = ConnectionString("legacy_fallback")
        });

        using var provider = BuildProvider(configuration);

        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        Assert.Equal("legacy_fallback", new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Database);
    }

    // A pool registered as a ready-made instance was never disposed, so each stopped test
    // host kept its idle connections until Npgsql pruned them, and a full test run ran out
    // of PostgreSQL connections. The container now owns and disposes both pools.
    [Fact]
    public async Task AddInfrastructure_DisposesItsConnectionPoolsWithTheContainer()
    {
        var provider = BuildProvider(BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Flowbit"] = ConnectionString("flowbit_primary")
        }));
        var runtime = provider.GetRequiredService<NpgsqlDataSource>();
        var retention = provider.GetRequiredService<RetentionDataSource>();

        await provider.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.OpenConnectionAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => retention.DataSource.OpenConnectionAsync().AsTask());
    }

    [Fact]
    public void AddInfrastructure_DisablesHttpClientGlobalTimeoutForNodeLevelTimeouts()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Flowbit"] = ConnectionString("flowbit_primary")
        });
        using var provider = BuildProvider(configuration);
        var clientFactory = provider.GetRequiredService<IHttpClientFactory>();

        using var client = clientFactory.CreateClient(nameof(IServiceTaskInvoker));

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static ServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static string ConnectionString(string database) =>
        $"Host=localhost;Database={database};Username=workflow;Password=workflow";
}
