using Flowbit.BrowserTests.Infrastructure;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Flowbit.BrowserAcceptanceTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AcceptanceCollection : ICollectionFixture<AcceptanceFixture>
{
    public const string Name = "worker-acceptance-stack";
}

public sealed class AcceptanceFixture : IAsyncLifetime
{
    public BrowserStackFixture Stack { get; } = BrowserStackFixture.CreateForAcceptance(new()
    {
        ApiHostDirectory = Environment.GetEnvironmentVariable("FLOWBIT_ACCEPTANCE_API_HOST"),
        UiHostDirectory = Environment.GetEnvironmentVariable("FLOWBIT_ACCEPTANCE_UI_HOST"),
        WorkerHostDirectory = Environment.GetEnvironmentVariable("FLOWBIT_ACCEPTANCE_WORKER_HOST"),
        ArtifactRoot = Environment.GetEnvironmentVariable("FLOWBIT_ACCEPTANCE_ARTIFACT_ROOT"),
        LegacyUiWithoutInteractiveMarkers = Environment.GetEnvironmentVariable("FLOWBIT_ACCEPTANCE_LEGACY_UI") == "1",
    });

    public Task InitializeAsync() => Stack.InitializeAsync();
    public Task DisposeAsync() => Stack.DisposeAsync();
}
