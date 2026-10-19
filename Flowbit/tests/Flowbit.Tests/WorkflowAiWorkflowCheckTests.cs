using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Ai;
using Flowbit.Service.Services;
using Flowbit.Shared.Authoring;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The errors the authoring agent receives from validate_workflow, validate_item and the host's end-of-turn gate.</summary>
public sealed class WorkflowAiWorkflowCheckTests
{
    private const string Key = "sk-check-test-provider-key-0123456789";
    private const string Example = "examples/basics/02-two-step-approval.json";

    private static WorkflowAiWorkflowCheck Checker(WorkflowAiRedaction? redaction = null, WorkflowModel? baseline = null) => new(
        new WorkflowDefinitionValidator(new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance), new ServiceTaskOptions()),
        redaction ?? new WorkflowAiRedaction(), Key, baseline);

    private static JsonNode Workflow() => JsonNode.Parse(ExampleWorkflowData.Read(Example))!;

    [Fact]
    public void EveryExamplePassesAndEachOfItsItemsPassesTheItemCheck()
    {
        foreach (var path in ExampleWorkflowData.RelativePaths)
        {
            var text = ExampleWorkflowData.Read(path);
            var result = Checker().Check(text);
            Assert.True(result.Valid, path + ": " + string.Join("; ", result.Errors.Select(error => error.Describe())));
            Assert.NotNull(result.RestoredJson);
            var root = JsonNode.Parse(text)!;
            foreach (var (collection, kind) in new[] { ("flowNodes", "node"), ("sequenceFlows", "sequenceFlow"), ("variables", "variable"), ("lanes", "lane") })
            {
                var items = root[collection]?.AsArray() ?? [];
                if (items.Count == 0) continue;
                var checkedItems = WorkflowAiWorkflowCheck.CheckItems(kind, items.ToJsonString());
                Assert.True(checkedItems.Refusal is null && checkedItems.Errors.Count == 0, $"{path} {collection}: "
                    + string.Join("; ", checkedItems.Errors.Select(error => error.Describe())));
                foreach (var item in items)
                    Assert.Empty(WorkflowAiWorkflowCheck.CheckItems(kind, item!.ToJsonString()).Errors);
            }
        }
    }

    [Fact]
    public void EveryStructuralErrorIsReportedWithItsPathElementAndReason()
    {
        var workflow = Workflow();
        var approval = workflow["flowNodes"]![1]!.AsObject();
        approval["colour"] = "blue";
        approval["requiresClaim"] = "yes";
        approval["claimMode"] = "sometimes";
        workflow["flowNodes"]![2]!.AsObject().Remove("name");
        var text = workflow.ToJsonString().Replace("\"x\":367", "\"x\":367,\"x\":368", StringComparison.Ordinal);

        var result = Checker().Check(text);

        Assert.False(result.Valid);
        var lines = result.Errors.Select(error => error.Describe()).ToArray();
        Assert.Equal(5, lines.Length);
        Assert.StartsWith("unsupported-value at /flowNodes/1/claimMode (node 2 (userTask \"approval1\")): Value \"sometimes\" is not allowed; use one of: ", lines[0]);
        Assert.Contains("fresh", lines[0]);
        Assert.Equal("unknown-property at /flowNodes/1/colour (node 2 (userTask \"approval1\")): Unknown property \"colour\"; allowed properties: "
            + string.Join(", ", WorkflowAuthoringJson.PropertyNames(typeof(FlowNodeModel))) + ".", lines[1]);
        Assert.Equal("wrong-type at /flowNodes/1/requiresClaim (node 2 (userTask \"approval1\")): Expected a boolean; found the string \"yes\".", lines[2]);
        Assert.Equal("duplicate-property at /flowNodes/1/x (node 2 (userTask \"approval1\")): Property \"x\" appears more than once in this object.", lines[3]);
        Assert.Equal("missing-property at /flowNodes/2 (node 3 (userTask)): Missing required property \"name\".", lines[4]);

        // The strict parser used everywhere else still stops at the first problem with its original message.
        var exception = Assert.Throws<JsonException>(() => WorkflowAuthoringJson.Parse(text));
        Assert.Equal("Duplicate JSON property $.flowNodes[1].x.", exception.Message);
    }

    [Theory]
    [InlineData("{\"id\":\"x\",}", "line 1, byte 11", "Text near the error: \"{\"id\":\"x\",}\"")]
    [InlineData("```json\n{}\n```", "line 1, byte 1", "Text near the error: \"```json\"")]
    [InlineData("{\n  \"id\": \"x\",\n\n", "line 4, byte 1", "The content ends after line 2: \"  \"id\": \"x\",\"")]
    public void MalformedJsonGivesTheParsersReasonAndTheTextNearIt(string text, string position, string near)
    {
        var error = Assert.Single(Checker().Check(text).Errors);
        Assert.Equal(("malformed-json", "/", ""), (error.Code, error.Path, error.Where));
        Assert.Contains(position, error.Message);
        Assert.Contains(near, error.Message);
    }

    [Fact]
    public void FlowbitsFirstDefinitionErrorIsLocatedFromTheElementItNames()
    {
        var blank = Workflow();
        blank["flowNodes"]![1]!["name"] = " ";
        var error = Assert.Single(Checker().Check(blank.ToJsonString()).Errors);
        Assert.Equal(("definition-rule", "/flowNodes/1", "node 2 (userTask \" \")"), (error.Code, error.Path, error.Where));
        Assert.Contains("#2", error.Message);

        var dangling = Workflow();
        dangling["sequenceFlows"]![0]!["targetRef"] = 9999;
        error = Checker().Check(dangling.ToJsonString()).Errors.First(item => item.Code == "definition-rule");
        Assert.Equal(("/sequenceFlows/0", "flow 101 (1 -> 9999)"), (error.Path, error.Where));
        Assert.Contains("9999", error.Message);
    }

    [Theory]
    [InlineData("Sequence flow #102 has a missing targetRef #9999.", "/sequenceFlows/1")]
    [InlineData("Default sequence flow #104 must not have a condition.", "/sequenceFlows/3")]
    [InlineData("Sequence flow id #105 is used more than once.", "/sequenceFlows/4")]
    [InlineData("User task #3 must have at least one outgoing sequence flow.", "/flowNodes/2")]
    [InlineData("Every non-default sequence flow from Exclusive gateway #4 needs a condition.", "/flowNodes/3")]
    [InlineData("Attribute #2 on Flow node #5 has a blank key.", "/flowNodes/4")]
    [InlineData("Variable 'comment' on flow node #2 must have a data type.", "/flowNodes/1/variables/0")]
    [InlineData("Variable 'reason' on sequence flow #104 must have a data type.", "/sequenceFlows/3/variables/0")]
    [InlineData("Variable 'amount' on process variables has an invalid default.", "/variables/0")]
    [InlineData("Workflow name is required.", "")]
    [InlineData("Flow node #77 is not in this workflow.", "")]
    public void DefinitionMessagesPointToTheElementTheyName(string message, string pointer)
    {
        var workflow = Workflow();
        workflow["variables"] = JsonNode.Parse("[{\"id\":1,\"name\":\"amount\",\"dataType\":\"number\"}]");
        workflow["flowNodes"]![1]!["variables"] = JsonNode.Parse("[{\"id\":1,\"name\":\"comment\",\"dataType\":\"string\"}]");
        workflow["sequenceFlows"]![3]!["variables"] = JsonNode.Parse("[{\"id\":1,\"name\":\"reason\",\"dataType\":\"string\"}]");
        using var document = JsonDocument.Parse(workflow.ToJsonString());
        Assert.Equal(pointer, WorkflowAiDiagnostics.DefinitionPointer(document.RootElement, message));
    }

    [Fact]
    public void DuplicateIdsTheProviderKeyAndMovedPlaceholdersAreReported()
    {
        var duplicate = Workflow();
        duplicate["flowNodes"]![3]!["id"] = 2;
        var error = Assert.Single(Checker().Check(duplicate.ToJsonString()).Errors);
        Assert.Equal("duplicate-id at /flowNodes/3/id (node 2 (endEvent \"end\")): Id 2 is already used by the node at /flowNodes/1; every node needs its own id.",
            error.Describe());

        var leaked = Workflow();
        leaked["flowNodes"]![1]!["name"] = "Review " + Key;
        error = Assert.Single(Checker().Check(leaked.ToJsonString()).Errors);
        Assert.Equal(("provider-key", "/flowNodes/1/name"), (error.Code, error.Path));
        Assert.DoesNotContain(Key, error.Describe());

        // A credential placeholder must stay in the field it was issued for.
        var original = Workflow();
        original["variables"] = JsonNode.Parse("[{\"id\":1,\"name\":\"apiSecret\",\"dataType\":\"string\",\"defaultValue\":\"s3cret\"},{\"id\":2,\"name\":\"note\",\"dataType\":\"string\",\"nullable\":true}]");
        var redaction = new WorkflowAiRedaction();
        using var source = JsonDocument.Parse(original.ToJsonString());
        var redacted = JsonNode.Parse(redaction.Redact(source.RootElement).GetRawText())!;
        var intact = Checker(redaction).Check(redacted.ToJsonString());
        Assert.True(intact.Valid, string.Join("; ", intact.Errors.Select(item => item.Describe())));
        var placeholder = redacted["variables"]![0]!["defaultValue"]!.GetValue<string>();
        redacted["variables"]![0]!.AsObject().Remove("defaultValue");
        redacted["variables"]![1]!["defaultValue"] = placeholder;
        error = Assert.Single(Checker(redaction).Check(redacted.ToJsonString()).Errors);
        Assert.Equal(("credential-placeholder", "/variables/1/defaultValue", "process variable \"note\""), (error.Code, error.Path, error.Where));
        Assert.DoesNotContain("s3cret", error.Describe());
    }

    [Fact]
    public void EachNewlyUnreachableNodeIsItsOwnErrorWhileExistingIslandsAreKept()
    {
        var workflow = Workflow();
        workflow["flowNodes"]!.AsArray().Add(JsonNode.Parse("{\"id\":6,\"name\":\"Orphan\",\"type\":\"task\"}"));
        workflow["flowNodes"]!.AsArray().Add(JsonNode.Parse("{\"id\":7,\"name\":\"Also orphaned\",\"type\":\"endEvent\"}"));
        workflow["sequenceFlows"]!.AsArray().Add(JsonNode.Parse("{\"id\":106,\"sourceRef\":6,\"targetRef\":7}"));
        var text = workflow.ToJsonString();

        var unreachable = Checker().Check(text).Errors.Where(error => error.Code == "unreachable-node").ToArray();
        Assert.Equal(new[] { ("/flowNodes/5", "node 6 (task \"Orphan\")"), ("/flowNodes/6", "node 7 (endEvent \"Also orphaned\")") },
            unreachable.Select(error => (error.Path, error.Where)));
        Assert.Equal("Node 6 cannot be reached from any start event by following sequence flows and boundary attachments.", unreachable[0].Message);

        var baseline = WorkflowAuthoringJson.Parse(text);
        Assert.DoesNotContain(Checker(baseline: baseline).Check(text).Errors, error => error.Code == "unreachable-node");
    }

    [Fact]
    public void ItemChecksReportEveryItemOrRefuseWhatCannotBeChecked()
    {
        var result = WorkflowAiWorkflowCheck.CheckItems("Node",
            "[{\"id\":1,\"name\":\"Start\",\"type\":\"startEvent\"},{\"id\":2,\"type\":\"approval\",\"colour\":1}]");
        Assert.Null(result.Refusal);
        Assert.Equal(new[] { "/1", "/1/colour", "/1/type" }, result.Errors.Select(error => error.Path));
        Assert.All(result.Errors, error => Assert.StartsWith("item 1: node 2 (approval)", error.Where));

        var flow = Assert.Single(WorkflowAiWorkflowCheck.CheckItems("sequenceFlow", "{\"id\":101,\"sourceRef\":1,\"targetRef\":\"2\"}").Errors);
        Assert.Equal("wrong-type at /targetRef (flow 101 (1 -> \"2\")): Expected a 32-bit integer; found the string \"2\".", flow.Describe());

        Assert.Equal("Unknown kind \"gateway\". Use one of: node, variable, sequenceFlow, lane.", WorkflowAiWorkflowCheck.CheckItems("gateway", "{}").Refusal);
        Assert.Equal("The array is empty; give at least one item.", WorkflowAiWorkflowCheck.CheckItems("lane", "[]").Refusal);
        Assert.Equal("malformed-json", Assert.Single(WorkflowAiWorkflowCheck.CheckItems("lane", "{\"id\":1,").Errors).Code);
    }

    [Fact]
    public void RedactionPlaceholdersStayTheSameForAConversationsNonce()
    {
        var workflow = Workflow();
        workflow["variables"] = JsonNode.Parse("[{\"id\":1,\"name\":\"apiSecret\",\"dataType\":\"string\",\"defaultValue\":\"s3cret\"}]");
        using var source = JsonDocument.Parse(workflow.ToJsonString());
        var first = new WorkflowAiRedaction();
        var turnOne = first.Redact(source.RootElement).GetRawText();
        // Next turn: the same conversation redacts the editor workflow again with its saved nonce.
        var second = new WorkflowAiRedaction(first.Nonce);
        Assert.Equal(turnOne, second.Redact(source.RootElement).GetRawText());
        using var fromTurnOne = JsonDocument.Parse(turnOne);
        Assert.Equal(source.RootElement.GetRawText(), second.Restore(fromTurnOne.RootElement).GetRawText());
        Assert.NotEqual(turnOne, new WorkflowAiRedaction().Redact(source.RootElement).GetRawText());
        Assert.Throws<ArgumentException>(() => new WorkflowAiRedaction("not-a-nonce"));
    }
}
