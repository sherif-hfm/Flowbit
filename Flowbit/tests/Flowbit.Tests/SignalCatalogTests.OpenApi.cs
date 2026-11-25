using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

public sealed partial class SignalCatalogTests
{
    [Fact]
    public async Task OpenApi_ListsTheNineRoutesWithTheirSchemesAndBodies()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync();

        var expected = new (string Path, HttpMethod Method, bool AcceptsClients, string? Body, string? Response)[]
        {
            ("/api/signal-catalog/entries", HttpMethod.Get, true, null, "SignalCatalogListDto"),
            ("/api/signal-catalog/entries/{name}", HttpMethod.Get, true, null, "SignalCatalogEntryDto"),
            ("/api/signal-catalog/entries", HttpMethod.Post, false, "CreateSignalCatalogEntryRequest", "SignalCatalogEntryDto"),
            ("/api/signal-catalog/entries/{name}", HttpMethod.Put, false, "UpdateSignalCatalogEntryRequest", "SignalCatalogEntryDto"),
            ("/api/signal-catalog/entries/{name}/archive", HttpMethod.Post, false, "SignalCatalogLifecycleRequest", "SignalCatalogEntryDto"),
            ("/api/signal-catalog/entries/{name}/reactivate", HttpMethod.Post, false, "SignalCatalogLifecycleRequest", "SignalCatalogEntryDto"),
            ("/api/signal-catalog/entries/{name}/usage", HttpMethod.Get, false, null, "SignalCatalogUsageDto"),
            ("/api/signal-catalog/preview", HttpMethod.Post, false, "CreateSignalCatalogEntryRequest", "SignalCatalogImpactDto"),
            ("/api/signal-catalog/uncataloged-names", HttpMethod.Get, false, null, "UncatalogedSignalNameListDto")
        };

        var catalogOperations = document.Paths
            .Where(path => path.Key.StartsWith("/api/signal-catalog", StringComparison.Ordinal))
            .Sum(path => path.Value.Operations!.Count);
        Assert.Equal(expected.Length, catalogOperations);
        foreach (var (path, method, acceptsClients, body, response) in expected)
        {
            var operation = document.Paths[path].Operations![method];
            Assert.Equal("Signal catalog", Assert.Single(operation.Tags!).Name);
            Assert.False(string.IsNullOrWhiteSpace(operation.Description), $"{method} {path}");
            var schemes = operation.Security!.SelectMany(requirement => requirement.Keys)
                .Select(scheme => scheme.Reference.Id)
                .ToHashSet();
            Assert.Contains("Bearer", schemes);
            Assert.Equal(acceptsClients, schemes.Contains("SharedVariableClientId"));
            Assert.Equal(acceptsClients, schemes.Contains("SharedVariableClientSecret"));
            if (body is not null)
            {
                var schema = operation.RequestBody!.Content!["application/json"].Schema;
                Assert.Equal(body, Assert.IsType<Microsoft.OpenApi.OpenApiSchemaReference>(schema).Reference.Id);
            }

            var success = operation.Responses!.Keys.First(code => code.StartsWith('2'));
            Assert.Equal(response, Assert.IsType<Microsoft.OpenApi.OpenApiSchemaReference>(
                operation.Responses[success].Content!["application/json"].Schema).Reference.Id);
        }
    }
}
