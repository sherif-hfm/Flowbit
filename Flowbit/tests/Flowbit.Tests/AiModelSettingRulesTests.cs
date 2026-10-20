using Flowbit.Service.Ai;
using Flowbit.Service.Services;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The rules the AI models page and the authoring runtime share: endpoints, fields, keys and the stored array.</summary>
public sealed class AiModelSettingRulesTests
{
    private const string Stored = """
        [{"id":"a1","name":"Flash","endpoint":"https://opencode.ai/zen/v1","model":"glm-5.3-flash","reasoningEffort":"high",
          "apiKey":"sk-0123456789abcdef","revision":2,"createdAt":"2026-10-01T00:00:00+00:00","updatedAt":"2026-10-02T00:00:00+00:00",
          "futureField":{"enabled":true}}]
        """;

    [Theory]
    [InlineData("https://opencode.ai/zen/v1", "https://opencode.ai/zen/v1/")]
    [InlineData(" https://OpenCode.ai/zen/v1/ ", "https://opencode.ai/zen/v1/")]
    [InlineData("http://127.0.0.1:11434/v1", "http://127.0.0.1:11434/v1/")]
    [InlineData("http://localhost:5999", "http://localhost:5999/")]
    public void Endpoints_AreNormalizedBaseUrls(string value, string expected)
    {
        Assert.True(AiModelSettingRules.TryNormalizeEndpoint(value, out var endpoint, out var error));
        Assert.Equal((expected, ""), (endpoint, error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://opencode.example/v1/")]
    [InlineData("ftp://opencode.example/v1/")]
    [InlineData("https://user:secret@opencode.example/v1/")]
    [InlineData("https://opencode.example/v1/?debug=1")]
    [InlineData("https://opencode.example/v1/#part")]
    [InlineData("https://opencode.example/v1/chat/completions")]
    [InlineData("https://opencode.example/v1/chat/completions/")]
    public void Endpoints_RefuseUnsafeOrNonBaseUrls(string value)
    {
        Assert.False(AiModelSettingRules.TryNormalizeEndpoint(value, out var endpoint, out var error));
        Assert.Equal("", endpoint);
        Assert.StartsWith("endpoint ", error);
        Assert.DoesNotContain("secret", error);
        Assert.False(AiModelSettingRules.TryNormalizeEndpoint("https://opencode.example/" + new string('v', 2000), out _, out _));
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData(" none ", true, null)]
    [InlineData("High", true, "high")]
    [InlineData("medium", true, "medium")]
    [InlineData("max", false, null)]
    public void ReasoningEfforts_AreLowMediumHighOrNotSent(string? value, bool valid, string? expected)
    {
        Assert.Equal(valid, AiModelSettingRules.TryNormalizeReasoningEffort(value, out var effort));
        if (valid) Assert.Equal(expected, effort);
    }

    [Fact]
    public void AdministratorInput_IsTrimmedAndCheckedWithoutEchoingValues()
    {
        var definition = AiModelSettingRules.NormalizeDefinition("  Flash  ", "https://opencode.ai/zen/v1", " glm-5.3-flash ", "HIGH");
        Assert.Equal(("Flash", "https://opencode.ai/zen/v1/", "glm-5.3-flash", "high"),
            (definition.Name, definition.Endpoint, definition.Model, definition.ReasoningEffort));

        Assert.Throws<WorkflowDomainException>(() => AiModelSettingRules.NormalizeDefinition(" ", "https://x.example/", "m", null));
        Assert.Throws<WorkflowDomainException>(() => AiModelSettingRules.NormalizeDefinition(new string('n', 101), "https://x.example/", "m", null));
        Assert.Throws<WorkflowDomainException>(() => AiModelSettingRules.NormalizeDefinition("Name\u0007", "https://x.example/", "m", null));
        Assert.Throws<WorkflowDomainException>(() => AiModelSettingRules.NormalizeDefinition("Name", "https://x.example/", new string('m', 201), null));
        Assert.Throws<WorkflowDomainException>(() => AiModelSettingRules.NormalizeDefinition("Name", "https://x.example/", "m", "max"));
        Assert.Throws<WorkflowDomainException>(() => AiModelSettingRules.NormalizeDefinition("Name", "http://remote.example/", "m", null));

        Assert.Equal("sk-0123456789abcdef", AiModelSettingRules.NormalizeApiKey("  sk-0123456789abcdef  ", required: true));
        Assert.Null(AiModelSettingRules.NormalizeApiKey("   ", required: false));
        Assert.Throws<WorkflowDomainException>(() => AiModelSettingRules.NormalizeApiKey(null, required: true));
        var shortKey = Assert.Throws<WorkflowDomainException>(() => AiModelSettingRules.NormalizeApiKey("too-short-key", required: true));
        Assert.DoesNotContain("too-short-key", shortKey.Message);
        Assert.Throws<WorkflowDomainException>(() => AiModelSettingRules.NormalizeApiKey("sk-0123456789\nabcdef", required: true));
        Assert.Throws<WorkflowDomainException>(() => AiModelSettingRules.NormalizeApiKey(new string('k', 4097), required: true));
    }

    [Fact]
    public void TheStoredArray_KeepsUnknownFieldsAndIsCheckedWhenRead()
    {
        Assert.True(AiModelSettingRules.TryParse(Stored, out var models));
        var model = Assert.Single(models);
        Assert.Equal(("a1", "https://opencode.ai/zen/v1/", "high", 2L), (model.Id, model.Endpoint, model.ReasoningEffort, model.Revision));
        var written = AiModelSettingRules.Serialize(models);
        Assert.Contains("\"futureField\":{\"enabled\":true}", written);
        Assert.Contains("\"apiKey\":\"sk-0123456789abcdef\"", written);

        Assert.True(AiModelSettingRules.TryParse(null, out var none));
        Assert.Empty(none);
        Assert.True(AiModelSettingRules.TryParse("  ", out _));
        var entry = Stored.Trim()[1..^1];
        var tooMany = "[" + string.Join(",", Enumerable.Range(0, 101).Select(index =>
            $$"""{"id":"m{{index}}","name":"Model {{index}}","endpoint":"https://x.example/","model":"m","apiKey":"sk-0123456789abcdef","revision":1}""")) + "]";
        foreach (var invalid in new[]
        {
            "{}", "not json", "[null]", "[" + entry + "," + entry + "]", tooMany,
            Stored.Replace("\"revision\":2", "\"revision\":0", StringComparison.Ordinal),
            Stored.Replace("sk-0123456789abcdef", "short", StringComparison.Ordinal),
            Stored.Replace("https://opencode.ai/zen/v1", "http://remote.example/v1", StringComparison.Ordinal),
            Stored.Replace("\"high\"", "\"max\"", StringComparison.Ordinal)
        })
            Assert.False(AiModelSettingRules.TryParse(invalid, out _), invalid);
    }

    [Fact]
    public void AConnectionNeverPrintsItsKey()
    {
        var connection = new AiModelConnection("a1", "Flash", "https://opencode.ai/zen/v1/", "glm-5.3-flash", "high", "sk-0123456789abcdef");
        Assert.DoesNotContain("sk-0123456789abcdef", connection.ToString());
        Assert.Contains("glm-5.3-flash", connection.ToString());
    }

    [Theory]
    [InlineData("AiAuthoring", "Models", true)]
    [InlineData(" aiauthoring ", " MODELS ", true)]
    [InlineData(null, "AiAuthoring.Models", true)]
    [InlineData("", "aiauthoring.models", true)]
    [InlineData("AiAuthoring", "Other", false)]
    [InlineData("Settings", "RequiredRole", false)]
    public void TheReservedEngineSettingIsMatchedIgnoringCase(string? settingNamespace, string key, bool reserved) =>
        Assert.Equal(reserved, AiModelSettingRules.IsReservedEngineSetting(settingNamespace, key));
}
