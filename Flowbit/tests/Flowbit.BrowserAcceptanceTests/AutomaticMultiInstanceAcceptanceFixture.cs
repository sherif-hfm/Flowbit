using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AutomaticMultiInstanceAcceptanceCollection : ICollectionFixture<AutomaticMultiInstanceAcceptanceFixture>
{
    public const string Name = "automatic-multi-instance-worker-stack";
}

/// <summary>
/// The example stack and its controlled service on a stack of their own, so the automatic
/// multi-instance scenarios' Worker load and held responses never reach other collections.
/// </summary>
public sealed class AutomaticMultiInstanceAcceptanceFixture : ExampleAcceptanceFixture;
