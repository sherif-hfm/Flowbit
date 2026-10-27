using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Shared.Authoring;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class VariablePresentationModelTests
{
    [Fact]
    public void Declaration_without_presentation_serializes_exactly_as_before()
    {
        var variable = new VariableModel
        {
            Id = 1,
            Name = "amount",
            DataType = WorkflowVariableTypes.Number,
            Required = true
        };

        Assert.Equal(
            """{"id":1,"name":"amount","dataType":"number","isArray":false,"required":true}""",
            JsonSerializer.Serialize(variable));
    }

    [Fact]
    public void Presentation_round_trips_with_typed_option_values()
    {
        const string json =
            """{"id":2,"name":"score","dataType":"number","isArray":false,"required":false,"label":"Score","helpText":"Line one.\nLine two.","options":[{"value":1.50,"label":"Low"},{"value":5}]}""";

        var variable = JsonSerializer.Deserialize<VariableModel>(json)!;

        Assert.Equal("Score", variable.Label);
        Assert.Equal("Line one.\nLine two.", variable.HelpText);
        Assert.Equal("1.50", variable.Options![0].Value!.Value.GetRawText());
        Assert.Null(variable.Options[1].Label);
        Assert.Equal(json, JsonSerializer.Serialize(variable));
    }

    [Fact]
    public void Migrator_trims_presentation_text_removes_blanks_and_keeps_option_values()
    {
        var model = PresentationModel();
        var start = model.FlowNodes.Single(node => node.Id == 1).Variables;
        start[0].Label = "  Request title  ";
        start[0].HelpText = "  Keep it short.\nNo codes.  ";
        start[1].Label = " \t ";
        start[1].HelpText = string.Empty;
        start[1].Options = [];
        var action = model.SequenceFlows.Single(flow => flow.Id == 201).Variables.Single();
        action.Options =
        [
            new VariableOptionModel { Value = JsonSerializer.SerializeToElement(" padded "), Label = "  Padded  " },
            new VariableOptionModel { Value = JsonSerializer.SerializeToElement("plain"), Label = "   " }
        ];

        WorkflowModelMigrator.Normalize(model);
        var once = JsonSerializer.Serialize(model);
        WorkflowModelMigrator.Normalize(model);

        Assert.Equal(once, JsonSerializer.Serialize(model));
        Assert.Equal("Request title", start[0].Label);
        Assert.Equal("Keep it short.\nNo codes.", start[0].HelpText);
        Assert.Null(start[1].Label);
        Assert.Null(start[1].HelpText);
        Assert.Null(start[1].Options);
        Assert.Equal(" padded ", action.Options[0].Value!.Value.GetString());
        Assert.Equal("Padded", action.Options[0].Label);
        Assert.Null(action.Options[1].Label);
        var serializedBlank = JsonNode.Parse(JsonSerializer.Serialize(start[1]))!.AsObject();
        Assert.False(serializedBlank.ContainsKey("label"));
        Assert.False(serializedBlank.ContainsKey("helpText"));
        Assert.False(serializedBlank.ContainsKey("options"));
    }

    [Fact]
    public void Migrator_adds_no_presentation_properties_to_older_documents()
    {
        var model = PresentationModel();
        var before = JsonSerializer.Serialize(model.FlowNodes.Single(node => node.Id == 1).Variables);

        WorkflowModelMigrator.Normalize(model);

        Assert.Equal(before, JsonSerializer.Serialize(model.FlowNodes.Single(node => node.Id == 1).Variables));
    }

    // Range is checked separately, so a large exponent still counts as exact.
    [Theory]
    [InlineData("0", true)]
    [InlineData("-0.0", true)]
    [InlineData("1.50", true)]
    [InlineData("-2.5", true)]
    [InlineData("123456789012345", true)]
    [InlineData("1234567890123450000", true)]
    [InlineData("0.000000000000000000000000000100", true)]
    [InlineData("1e-28", true)]
    [InlineData("0.1e-27", true)]
    [InlineData("1.25E-26", true)]
    [InlineData("1e+28", true)]
    [InlineData("7.92281625142643e28", true)]
    [InlineData("1e99999999999", true)]
    [InlineData("1e-29", false)]
    [InlineData("1.5e-28", false)]
    [InlineData("0.00000000000000000000000000001", false)]
    [InlineData("9007199254740993", false)]
    [InlineData("1234567890123456", false)]
    [InlineData("79228162514264337593543950335", false)]
    [InlineData("1e-99999999999", false)]
    [InlineData("1.", false)]
    [InlineData("\"5\"", false)]
    public void Number_options_must_keep_their_exact_value(string number, bool expected) =>
        Assert.Equal(expected, VariablePresentationConstraints.IsExactOptionNumber(number));

    [Fact]
    public void Strict_authoring_contract_accepts_presentation_and_requires_option_values()
    {
        var workflow = JsonSerializer.SerializeToNode(PresentationModel())!.AsObject();
        var input = workflow["flowNodes"]![0]!["variables"]![0]!.AsObject();
        input["label"] = "Request title";
        input["helpText"] = "Keep it short.";
        input["options"] = new JsonArray(new JsonObject { ["value"] = "a", ["label"] = "A" });

        var parsed = WorkflowAuthoringJson.Parse(workflow.ToJsonString());

        var option = Assert.Single(parsed.FlowNodes[0].Variables[0].Options!);
        Assert.Equal("a", option.Value!.Value.GetString());
        input["options"] = new JsonArray(new JsonObject { ["label"] = "A" });
        using var document = JsonDocument.Parse(workflow.ToJsonString());
        var issue = Assert.Single(WorkflowAuthoringJson.Inspect(document.RootElement));
        Assert.Equal("missing-property", issue.Code);
        Assert.Equal("/flowNodes/0/variables/0/options/0", issue.Pointer);

        var schema = JsonNode.Parse(WorkflowAuthoringJson.CreateSchema())!["$defs"]!;
        var variableProperties = schema["VariableModel"]!["properties"]!.AsObject();
        Assert.True(variableProperties.ContainsKey("label"));
        Assert.True(variableProperties.ContainsKey("helpText"));
        Assert.True(variableProperties.ContainsKey("options"));
        Assert.Equal("value", Assert.Single(schema["VariableOptionModel"]!["required"]!.AsArray())!.GetValue<string>());
    }

    private static WorkflowModel PresentationModel() => new()
    {
        Id = "presentation-model",
        Name = "Presentation model",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Variables =
                [
                    new VariableModel { Id = 1, Name = "title", DataType = WorkflowVariableTypes.String, Required = true },
                    new VariableModel { Id = 2, Name = "note", DataType = WorkflowVariableTypes.String }
                ]
            },
            new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel
            {
                Id = 201,
                Name = "Decide",
                SourceRef = 2,
                TargetRef = 3,
                Variables = [new VariableModel { Id = 1, Name = "decision", DataType = WorkflowVariableTypes.String }]
            }
        ]
    };
}
