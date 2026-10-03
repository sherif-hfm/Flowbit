using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Service.Ai;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowAiDraftTests
{
    private const int MaxBytes = 100_000;
    private const string Baseline = """
        {"id":"draft","name":"Draft","initialEventId":1,
         "lanes":[{"id":1,"name":"Operations","x":10,"y":20,"w":800,"h":300}],
         "variables":[{"id":1,"name":"payload","dataType":"json","defaultValue":{"items":[1,null],"keep":true}}],
         "flowNodes":[
           {"id":1,"name":"Call","type":"serviceTask","laneId":1,"x":70,"y":90,
            "service":{"url":"https://example.invalid","timeoutSeconds":45,"body":"unchanged","headers":[]},
            "attributes":[{"key":"business","value":"retained"}]},
           {"id":2,"name":"Review","type":"userTask","variables":[{"id":1,"name":"nodeInput","dataType":"string"}]}],
         "sequenceFlows":[{"id":1,"sourceRef":1,"targetRef":2,"variables":[{"id":1,"name":"flowInput","dataType":"number"}]}]}
        """;

    [Fact]
    public void PropertyEdits_PreserveOmittedAdvancedFieldsCoordinatesAndOrder()
    {
        var result = Apply("""
            [{"op":"set","target":"node","id":1,"path":"/name","value":"Updated"},
             {"op":"set","target":"node","id":1,"path":"/service/timeoutSeconds","value":60},
             {"op":"create","target":"node","id":3,"value":{"id":3,"name":"Finish","type":"endEvent"}}]
            """);
        var nodes = result.GetProperty("flowNodes");
        Assert.Equal(new[] { 1, 2, 3 }, nodes.EnumerateArray().Select(node => node.GetProperty("id").GetInt32()));
        Assert.Equal("Updated", nodes[0].GetProperty("name").GetString());
        Assert.Equal("https://example.invalid", nodes[0].GetProperty("service").GetProperty("url").GetString());
        Assert.Equal("unchanged", nodes[0].GetProperty("service").GetProperty("body").GetString());
        Assert.Equal(60, nodes[0].GetProperty("service").GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(70, nodes[0].GetProperty("x").GetInt32());
        Assert.Equal("retained", nodes[0].GetProperty("attributes")[0].GetProperty("value").GetString());
    }

    [Fact]
    public void InvalidBatch_IsAtomicAndDoesNotModifyTheInput()
    {
        var original = Json(Baseline);
        var raw = original.GetRawText();
        var operations = Json("""
            [{"op":"create","target":"node","id":3,"value":{"id":3,"name":"New","type":"task"}},
             {"op":"set","target":"node","id":1,"path":"/unknownProperty","value":true}]
            """);
        Assert.Throws<JsonException>(() => WorkflowAiDraft.Apply(original, operations, new(), "draft", MaxBytes, 20));
        Assert.Equal(raw, original.GetRawText());
        Assert.Equal(2, original.GetProperty("flowNodes").GetArrayLength());
    }

    [Fact]
    public void JsonNullAndPropertyRemoval_AreDifferentOperations()
    {
        var withNull = Apply("""
            [{"op":"set","target":"variable","owner":"workflow","id":1,"path":"/defaultValue","value":null}]
            """);
        Assert.Equal(JsonValueKind.Null, withNull.GetProperty("variables")[0].GetProperty("defaultValue").ValueKind);
        var removed = Apply("""
            [{"op":"remove","target":"variable","owner":"workflow","id":1,"path":"/defaultValue"}]
            """, withNull);
        Assert.False(removed.GetProperty("variables")[0].TryGetProperty("defaultValue", out _));
    }

    [Fact]
    public void ArbitraryBusinessJson_SupportsEscapedAndEmptyKeysWithoutChangingEntityIdentity()
    {
        var result = Apply("""
            [{"op":"set","target":"variable","owner":"workflow","id":1,"path":"/defaultValue/a~1b~0c","value":{"id":999,"x":7,"__proto__":{"ok":true}}},
             {"op":"set","target":"variable","owner":"workflow","id":1,"path":"/defaultValue/","value":false}]
            """);
        var variable = result.GetProperty("variables")[0];
        Assert.Equal(1, variable.GetProperty("id").GetInt32());
        var value = variable.GetProperty("defaultValue");
        Assert.Equal(999, value.GetProperty("a/b~c").GetProperty("id").GetInt32());
        Assert.True(value.GetProperty("a/b~c").GetProperty("__proto__").GetProperty("ok").GetBoolean());
        Assert.False(value.GetProperty("").GetBoolean());
    }

    [Fact]
    public void NestedArrayOperations_ReplaceAppendAndRemoveWithoutImplicitInsertion()
    {
        var result = Apply("""
            [{"op":"set","target":"variable","owner":"workflow","id":1,"path":"/defaultValue/items/0","value":2},
             {"op":"set","target":"variable","owner":"workflow","id":1,"path":"/defaultValue/items/-","value":{"nested":true}},
             {"op":"remove","target":"variable","owner":"workflow","id":1,"path":"/defaultValue/items/1"}]
            """);
        var items = result.GetProperty("variables")[0].GetProperty("defaultValue").GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(2, items[0].GetInt32());
        Assert.True(items[1].GetProperty("nested").GetBoolean());
    }

    [Fact]
    public void VariableTargets_ResolveTheirOwnerAndAllowSameIdsInOtherScopes()
    {
        var result = Apply("""
            [{"op":"set","target":"variable","owner":"node","ownerId":2,"id":1,"path":"/name","value":"nodeChanged"},
             {"op":"set","target":"variable","owner":"flow","ownerId":1,"id":1,"path":"/name","value":"flowChanged"},
             {"op":"create","target":"variable","owner":"node","ownerId":1,"id":1,"value":{"id":1,"name":"newInput","dataType":"string"}}]
            """);
        Assert.Equal("payload", result.GetProperty("variables")[0].GetProperty("name").GetString());
        Assert.Equal("nodeChanged", result.GetProperty("flowNodes")[1].GetProperty("variables")[0].GetProperty("name").GetString());
        Assert.Equal("flowChanged", result.GetProperty("sequenceFlows")[0].GetProperty("variables")[0].GetProperty("name").GetString());
        Assert.Equal("newInput", result.GetProperty("flowNodes")[0].GetProperty("variables")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void IntermediateDrafts_AllowIncompleteTopologyAndDeleteWithoutCascading()
    {
        var result = Apply("""
            [{"op":"delete","target":"node","id":1},
             {"op":"create","target":"flow","id":2,"value":{"id":2,"sourceRef":999,"targetRef":888}}]
            """);
        Assert.Single(result.GetProperty("flowNodes").EnumerateArray());
        Assert.Equal(2, result.GetProperty("sequenceFlows").GetArrayLength());
        Assert.Equal(1, result.GetProperty("initialEventId").GetInt32());
        WorkflowAiDraft.ValidateCandidate(result, "draft", MaxBytes);
    }

    [Theory]
    [InlineData("{\"op\":\"set\",\"target\":\"workflow\",\"path\":\"/id\",\"value\":\"changed\"}")]
    [InlineData("{\"op\":\"remove\",\"target\":\"node\",\"id\":1,\"path\":\"/id\"}")]
    [InlineData("{\"op\":\"set\",\"target\":\"workflow\",\"path\":\"/flowNodes/0/name\",\"value\":\"changed\"}")]
    [InlineData("{\"op\":\"set\",\"target\":\"node\",\"id\":2,\"path\":\"/variables/0/id\",\"value\":99}")]
    [InlineData("{\"op\":\"set\",\"target\":\"node\",\"id\":1,\"path\":\"/x\",\"value\":55}")]
    [InlineData("{\"op\":\"remove\",\"target\":\"lane\",\"id\":1,\"path\":\"/w\"}")]
    [InlineData("{\"op\":\"create\",\"target\":\"node\",\"id\":3,\"value\":{\"id\":4,\"name\":\"new\",\"type\":\"task\"}}")]
    [InlineData("{\"op\":\"create\",\"target\":\"node\",\"id\":3,\"value\":{\"id\":3,\"name\":\"new\",\"type\":\"task\",\"y\":0}}")]
    [InlineData("{\"op\":\"create\",\"target\":\"node\",\"id\":1,\"value\":{\"id\":1,\"name\":\"new\",\"type\":\"task\"}}")]
    [InlineData("{\"op\":\"delete\",\"target\":\"workflow\"}")]
    public void IdentityCollectionAndLayoutChanges_AreRejected(string operation) =>
        Assert.Throws<JsonException>(() => Apply("[" + operation + "]"));

    [Theory]
    [InlineData("{\"op\":\"set\",\"target\":\"node\",\"id\":1,\"path\":\"/name\",\"value\":null}")]
    [InlineData("{\"op\":\"remove\",\"target\":\"node\",\"id\":1,\"path\":\"/name\"}")]
    [InlineData("{\"op\":\"set\",\"target\":\"node\",\"id\":1,\"path\":\"/unknown\",\"value\":1}")]
    [InlineData("{\"op\":\"set\",\"target\":\"node\",\"id\":1,\"path\":\"/multiInstance/mode\",\"value\":\"parallel\"}")]
    [InlineData("{\"op\":\"set\",\"target\":\"node\",\"id\":1,\"path\":\"/type\",\"value\":\"invented\"}")]
    [InlineData("{\"op\":\"set\",\"target\":\"variable\",\"id\":1,\"path\":\"/name\",\"value\":\"ambiguous\"}")]
    [InlineData("{\"op\":\"delete\",\"target\":\"node\",\"id\":999}")]
    [InlineData("{\"op\":\"delete\",\"target\":\"node\",\"id\":1,\"path\":\"/name\"}")]
    [InlineData("{\"op\":\"set\",\"target\":\"node\",\"id\":1,\"owner\":\"workflow\",\"path\":\"/name\",\"value\":\"x\"}")]
    [InlineData("{\"op\":\"set\",\"target\":\"workflow\",\"id\":1,\"path\":\"/name\",\"value\":\"x\"}")]
    [InlineData("{\"op\":\"set\",\"target\":\"node\",\"id\":1,\"path\":\"/name\",\"value\":\"x\",\"extra\":true}")]
    [InlineData("{\"op\":\"set\",\"op\":\"set\",\"target\":\"node\",\"id\":1,\"path\":\"/name\",\"value\":\"x\"}")]
    [InlineData("{\"op\":\"set\",\"target\":\"variable\",\"owner\":\"workflow\",\"id\":1,\"path\":\"/defaultValue\",\"value\":{\"a\":1,\"a\":2}}")]
    public void MalformedOperationsAndCanonicalShapes_AreRejected(string operation) =>
        Assert.Throws<JsonException>(() => Apply("[" + operation + "]"));

    [Theory]
    [InlineData("")]
    [InlineData("defaultValue")]
    [InlineData("/defaultValue/~2")]
    [InlineData("/defaultValue/~")]
    [InlineData("/defaultValue/items/01")]
    [InlineData("/defaultValue/items/-1")]
    [InlineData("/defaultValue/items/+1")]
    [InlineData("/defaultValue/items/2")]
    [InlineData("/defaultValue/items/2147483648")]
    [InlineData("/defaultValue/items/-/child")]
    [InlineData("/defaultValue/items/1/child")]
    public void InvalidPointersAndArrayIndices_AreRejected(string path)
    {
        var operation = JsonSerializer.Serialize(new[] { new { op = "set", target = "variable", owner = "workflow", id = 1, path, value = true } });
        Assert.Throws<JsonException>(() => Apply(operation));
    }

    [Fact]
    public void RedactedCredentials_RemainRedactedAcrossAcceptedBatchesAndRejectRelocation()
    {
        var original = JsonNode.Parse(Baseline)!;
        original["flowNodes"]![0]!["service"]!["headers"] = JsonNode.Parse("""[{"name":"Authorization","value":"literal-secret"}]""");
        var redaction = new WorkflowAiRedaction();
        var redacted = redaction.Redact(JsonSerializer.SerializeToElement(original));
        var accepted = WorkflowAiDraft.Apply(redacted,
            Json("""[{"op":"set","target":"node","id":1,"path":"/name","value":"Renamed"}]"""), redaction, "draft", MaxBytes, 10);
        Assert.DoesNotContain("literal-secret", accepted.GetRawText());
        Assert.Equal("literal-secret", redaction.Restore(accepted).GetProperty("flowNodes")[0].GetProperty("service").GetProperty("headers")[0].GetProperty("value").GetString());
        var placeholder = accepted.GetProperty("flowNodes")[0].GetProperty("service").GetProperty("headers")[0].GetProperty("value").GetString();
        var moved = JsonSerializer.SerializeToElement(new[] { new { op = "set", target = "node", id = 1, path = "/service/body", value = placeholder } });
        Assert.Throws<JsonException>(() => WorkflowAiDraft.Apply(accepted, moved, redaction, "draft", MaxBytes, 10));
    }

    [Fact]
    public void CandidateValidation_RejectsDuplicateIdentitiesWithinEachOwnerButNotAcrossOwners()
    {
        WorkflowAiDraft.ValidateCandidate(Json(Baseline), "draft", MaxBytes);
        foreach (var collection in new[] { "lanes", "flowNodes", "sequenceFlows", "variables" })
        {
            var candidate = JsonNode.Parse(Baseline)!;
            var array = candidate[collection]!.AsArray();
            array.Add(array[0]!.DeepClone());
            Assert.Throws<JsonException>(() => WorkflowAiDraft.ValidateCandidate(JsonSerializer.SerializeToElement(candidate), "draft", MaxBytes));
        }
        var nested = JsonNode.Parse(Baseline)!;
        var variables = nested["flowNodes"]![1]!["variables"]!.AsArray();
        variables.Add(variables[0]!.DeepClone());
        Assert.Throws<JsonException>(() => WorkflowAiDraft.ValidateCandidate(JsonSerializer.SerializeToElement(nested), "draft", MaxBytes));
        Assert.Throws<JsonException>(() => WorkflowAiDraft.ValidateCandidate(Json(Baseline), "another-workflow", MaxBytes));
    }

    [Fact]
    public void CandidateDuplicateMembersAndConfiguredLimits_AreEnforced()
    {
        var duplicate = Json(Baseline.Replace("\"name\":\"Draft\"", "\"name\":\"first\",\"name\":\"second\"", StringComparison.Ordinal));
        Assert.Throws<JsonException>(() => Apply("[]", duplicate));
        var operations = Json("""
            [{"op":"set","target":"workflow","path":"/name","value":"a"},
             {"op":"set","target":"workflow","path":"/name","value":"b"}]
            """);
        Assert.Throws<JsonException>(() => WorkflowAiDraft.Apply(Json(Baseline), operations, new(), "draft", MaxBytes, 1));
        Assert.Throws<JsonException>(() => WorkflowAiDraft.ValidateCandidate(Json(Baseline), "draft", 100));
        var large = JsonSerializer.SerializeToElement(new[] { new { op = "set", target = "workflow", path = "/name", value = new string('x', MaxBytes) } });
        Assert.Throws<JsonException>(() => WorkflowAiDraft.Apply(Json(Baseline), large, new(), "draft", MaxBytes, 10));
    }

    private static JsonElement Apply(string operations, JsonElement? candidate = null) =>
        WorkflowAiDraft.Apply(candidate ?? Json(Baseline), Json(operations), new WorkflowAiRedaction(), "draft", MaxBytes, 20);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
