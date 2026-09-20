using Flowbit.BrowserTests.Infrastructure;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// Every browser scenario shares one disposable stack (PostgreSQL container,
/// published API/UI processes, editor host, Playwright). xUnit serializes the
/// assembly so scenarios never share a UI identity implicitly.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BrowserCollection : ICollectionFixture<BrowserStackFixture>
{
    public const string Name = "browser-stack";
}
