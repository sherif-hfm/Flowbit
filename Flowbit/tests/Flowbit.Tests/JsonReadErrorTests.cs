using System.Text.Json;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class JsonReadErrorTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("""{ "httpStatusCodes": ["409"] }""", "The value has the wrong JSON type. Path: $.httpStatusCodes[0]")]
    [InlineData("""{ "httpStatusCodes": [null] }""", "The value has the wrong JSON type. Path: $.httpStatusCodes[0]")]
    [InlineData("""{ "httpStatusRangez": [] }""", "Unknown property 'httpStatusRangez'. Path: $.httpStatusRangez")]
    [InlineData("""{ "httpStatusRanges": [{ "from": 400, "name": "x" }] }""", "Unknown property 'name'. Path: $.httpStatusRanges[0].name")]
    public void DescribesARefusedMatcherWithoutNamingDotNetTypes(string json, string expected)
    {
        var refused = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ErrorBoundaryMatchModel>(json, WebDefaults));

        Assert.Equal(expected, JsonReadError.Describe(refused));
    }

    [Fact]
    public void KeepsTheReadersOwnMessageForMalformedJson()
    {
        var refused = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ErrorBoundaryMatchModel>("{ not json", WebDefaults));

        var described = JsonReadError.Describe(refused);

        Assert.StartsWith("'n' is an invalid start of a property name.", described);
        Assert.EndsWith("Path: $", described);
        Assert.DoesNotContain("LineNumber", described);
    }

    [Fact]
    public void ReplacesTheMessageForAMissingRequiredProperty()
    {
        var refused = Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<RequiredProbe>("{}", WebDefaults));

        Assert.Equal("Missing required properties: 'name'. Path: $", JsonReadError.Describe(refused));
    }

    private sealed class RequiredProbe
    {
        public required string Name { get; init; }
    }
}
