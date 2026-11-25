using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The rules of a signal catalog entry's content (EVENT-04F), in the plan's order.</summary>
public sealed class SignalCatalogEntryRulesTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static SignalCatalogFieldDto Field(string name, string type = "string", bool isArray = false) =>
        new(name, type, isArray);

    [Theory]
    [InlineData(null, "Signal name is required.")]
    [InlineData("", "Signal name is required.")]
    [InlineData("Orders.Cancelled", "Signal name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.")]
    [InlineData("-orders", "Signal name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.")]
    public void Name_IsCheckedWithTheThrowNameRule(string? name, string expected) =>
        Assert.Equal(expected, SignalCatalogEntryRules.Problem(name, true, null, null, null));

    [Fact]
    public void Name_IsNotCheckedForAnUpdate() =>
        Assert.Null(SignalCatalogEntryRules.Problem(null, false, null, null, null));

    [Fact]
    public void EveryMessage_InThePlansOrder()
    {
        string? Problem(string? description = null, IReadOnlyList<SignalCatalogFieldDto?>? fields = null, IReadOnlyList<string?>? roles = null) =>
            SignalCatalogEntryRules.Problem("orders.cancelled", true, description, fields, roles);

        Assert.Null(Problem());
        Assert.Equal("description can have at most 1000 characters.", Problem(new string('d', 1001)));
        Assert.Null(Problem("  " + new string('d', 1000) + "  "));
        Assert.Equal("fields can have at most 100 entries.",
            Problem(fields: Enumerable.Range(0, 101).Select(index => Field($"f{index}")).ToArray()));
        Assert.Null(Problem(fields: Enumerable.Range(0, 100).Select(index => Field($"f{index}")).ToArray()));
        Assert.Equal("Field 2 cannot be null.", Problem(fields: [Field("a"), null]));
        Assert.Equal("Field 3 name must be 1 to 100 letters, digits, '_', or '-', starting with a letter or '_'.",
            Problem(fields: [Field("a"), Field("b"), Field("bad.name")]));
        Assert.Equal("Field 'Reason' appears more than once.", Problem(fields: [Field("reason"), Field("Reason")]));
        Assert.Equal("Field 'reason' dataType must be string, number, boolean, date, datetime, or json.",
            Problem(fields: [Field("reason", "text")]));
        Assert.Equal("Field 'reason' description can have at most 300 characters.",
            Problem(fields: [Field("reason") with { Description = new string('x', 301) }]));
        Assert.Equal("throwRoles can have at most 100 roles.",
            Problem(roles: Enumerable.Range(0, 101).Select(index => (string?)$"role-{index}").ToArray()));
        Assert.Null(Problem(roles: Enumerable.Range(0, 100).Select(index => (string?)$"role-{index}")
            .Concat(["ROLE-1", " role-2 "]).ToArray()));
        Assert.Equal("A throw role cannot be blank.", Problem(roles: ["clerk", "  "]));
        Assert.Equal("A throw role can have at most 300 characters.", Problem(roles: [new string('r', 301)]));

        // The first problem wins: a description problem hides a field problem after it.
        Assert.Equal("description can have at most 1000 characters.",
            Problem(new string('d', 1001), [Field("bad.name")], ["  "]));
    }

    [Fact]
    public void Lengths_CountUnicodeScalars()
    {
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 1000));
        Assert.Null(SignalCatalogEntryRules.Problem("a", true, emoji, null, null));
        Assert.Null(SignalCatalogEntryRules.Problem("a", true, null, null, [string.Concat(Enumerable.Repeat("\U0001F600", 300))]));
    }

    [Fact]
    public void Roles_AreTrimmedAndDeduplicatedIgnoringCase() =>
        Assert.Equal(["recall-manager", "auditor"],
            SignalCatalogEntryRules.NormalizeRoles([" recall-manager ", "auditor", "Recall-Manager", "AUDITOR "]));

    [Fact]
    public void Descriptions_AreTrimmedAndBlankMeansNull()
    {
        Assert.Null(SignalCatalogEntryRules.NormalizeDescription("   "));
        Assert.Equal("Recall", SignalCatalogEntryRules.NormalizeDescription("  Recall "));
        var fields = SignalCatalogEntryRules.NormalizeFields([Field("a") with { Description = "  " }, Field("b") with { Description = " Why " }]);
        Assert.Equal([null, "Why"], fields.Select(field => field.Description));
    }

    [Fact]
    public void Mode_IsRequiredOnlyForTheWordRequired()
    {
        Assert.Equal(SignalCatalogModes.Required, SignalCatalogModes.ParseMode("  Required "));
        Assert.Equal(SignalCatalogModes.Optional, SignalCatalogModes.ParseMode(null));
        Assert.Equal(SignalCatalogModes.Optional, SignalCatalogModes.ParseMode(""));
        Assert.Equal(SignalCatalogModes.Optional, SignalCatalogModes.ParseMode("requird"));
        Assert.Equal(SignalCatalogModes.Optional, SignalCatalogModes.ParseMode("optional"));
    }

    [Fact]
    public void FieldList_NamesAtMostTwentyFields()
    {
        Assert.Equal("none", SignalCatalogEntryRules.FieldList([]));
        Assert.Equal("reason (string), skus (string[]), details (json)",
            SignalCatalogEntryRules.FieldList([Field("reason"), Field("skus", isArray: true), Field("details", "json")]));
        var many = Enumerable.Range(1, 23).Select(index => Field($"f{index}", "number")).ToArray();
        var text = SignalCatalogEntryRules.FieldList(many);
        Assert.StartsWith("f1 (number), f2 (number)", text);
        Assert.EndsWith("f20 (number), and 3 more", text);
        Assert.DoesNotContain("f21", text);
    }

    [Theory]
    [InlineData("string", false, "a string")]
    [InlineData("string", true, "an array of strings")]
    [InlineData("number", false, "a number")]
    [InlineData("number", true, "an array of numbers")]
    [InlineData("boolean", false, "true or false")]
    [InlineData("boolean", true, "an array of true or false values")]
    [InlineData("date", false, "a date (YYYY-MM-DD)")]
    [InlineData("date", true, "an array of dates (YYYY-MM-DD)")]
    [InlineData("datetime", false, "a date and time (ISO-8601)")]
    [InlineData("datetime", true, "an array of dates and times (ISO-8601)")]
    [InlineData("json", false, "any JSON value")]
    [InlineData("json", true, "an array of JSON values")]
    public void TypeWords_ForEveryType(string type, bool isArray, string expected) =>
        Assert.Equal(expected, SignalCatalogEntryRules.TypeWords(type, isArray));

    [Fact]
    public void RequestBodies_AreStrict()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateSignalCatalogEntryRequest>(
            """{"name":"a","alowClients":false}""", JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateSignalCatalogEntryRequest>(
            """{"name":"a","allowClients":1}""", JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateSignalCatalogEntryRequest>(
            """{"name":"a","expectedUpdatedAt":"2026-11-24T09:00:00Z"}""", JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateSignalCatalogEntryRequest>(
            """{"name":"a","fields":[{"name":"x","dataType":"string","isArray":"true"}]}""", JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateSignalCatalogEntryRequest>(
            """{"name":"a","fields":[{"name":"x","dataType":"string","requred":true}]}""", JsonOptions));

        var defaults = JsonSerializer.Deserialize<CreateSignalCatalogEntryRequest>("""{"name":"a"}""", JsonOptions)!;
        Assert.True(defaults.AllowClients);
        Assert.True(defaults.AllowWorkflows);
        var field = JsonSerializer.Deserialize<SignalCatalogFieldDto>("""{"name":"x","dataType":"number"}""", JsonOptions)!;
        Assert.False(field.IsArray);
        Assert.False(field.Required);
    }
}
