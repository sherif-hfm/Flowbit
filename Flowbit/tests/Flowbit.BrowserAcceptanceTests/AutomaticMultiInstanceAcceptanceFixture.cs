using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AutomaticMultiInstanceAcceptanceCollection : ICollectionFixture<AutomaticMultiInstanceAcceptanceFixture>
{
    public const string Name = "automatic-multi-instance-worker-stack";
}

/// <summary>
/// The example stack and its controlled service, with the TASK-05A Development release
/// switch opened on API and Worker for the automatic multi-instance scenarios only. Every
/// other collection, and the smoke stack, keeps the switch closed.
/// </summary>
public sealed class AutomaticMultiInstanceAcceptanceFixture : ExampleAcceptanceFixture
{
    protected override IReadOnlyDictionary<string, string> HostSettings { get; } = new Dictionary<string, string>
    {
        // Delete this line when the integration change removes the release switch.
        ["WorkflowAutomaticMultiInstance__AllowInDevelopment"] = "true",
    };
}
