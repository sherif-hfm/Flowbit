using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Jint;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// TASK-05C authoring rules for script bodies: item context references and the
/// local-validation language, through the server validator and the editor's save
/// validator, plus the script inspector's item targets.
/// </summary>
public sealed class EditorAutomaticMultiInstanceScriptTests
{
    private const string Definition = """
        {
          "id":"script-items", "name":"Script items", "initialEventId":1, "lanes":[],
          "variables":[
            {"id":1,"name":"amounts","dataType":"number","isArray":true,"defaultValue":[10,20]},
            {"id":2,"name":"multiplier","dataType":"number","isArray":false,"defaultValue":2},
            {"id":3,"name":"calculationResults","dataType":"json","isArray":false,"defaultValue":[]}
          ],
          "flowNodes":[
            {"id":1,"type":"startEvent","name":"Start","x":80,"y":200},
            {"id":40,"type":"scriptTask","name":"Scale each amount","x":250,"y":180,"asyncBefore":true,"scriptFormat":"ncalc",
             "multiInstance":{"mode":"sequential","source":"collection","collectionVariable":"amounts","completionEvaluation":"afterAll",
               "maxConcurrency":1,"resultVariable":"calculationResults",
               "outputVariables":[{"name":"scaledAmount","dataType":"number","nullable":false},{"name":"adjustedAmount","dataType":"number","nullable":false}]},
             "assignments":[{"variable":"scaledAmount","expression":"[mi.item] * multiplier"},{"variable":"adjustedAmount","expression":"scaledAmount + [mi.index]"}]},
            {"id":3,"type":"endEvent","name":"End","x":550,"y":200}
          ],
          "sequenceFlows":[{"id":101,"sourceRef":1,"targetRef":40},{"id":201,"sourceRef":40,"targetRef":3}]
        }
        """;

    public static TheoryData<string, string, string> ItemContextCases => new()
    {
        { "assignments/1/expression", "\"scaledAmount + [mi.completed]\"",
            "Automatic multi-instance activity #40 assignment for 'adjustedAmount' can read only [mi.item], [mi.index], and [mi.total]; 'mi.completed' is not item context." },
        { "multiInstance/outputVariables/0/validation", "\"scaledAmount < [mi.remaining]\"",
            "Automatic multi-instance activity #40 local output 'scaledAmount' validation can read only [mi.item], [mi.index], and [mi.total]; 'mi.remaining' is not item context." },
        { "multiInstance/outputVariables/0/validation", "\"CountFlow(201) > 0\"",
            "Automatic multi-instance activity #40 local output 'scaledAmount' validation cannot use CountFlow or PercentFlow." },
        { "multiInstance/outputVariables/1/defaultValue", "\"${mi.completed}\"",
            "Automatic multi-instance activity #40 local output 'adjustedAmount' defaultValue can read only [mi.item], [mi.index], and [mi.total]; 'mi.completed' is not item context." }
    };

    [Theory]
    [MemberData(nameof(ItemContextCases))]
    public void Item_context_rules_agree_between_server_and_editor(string path, string value, string expected)
    {
        var json = Set(path, value);
        Assert.Equal(expected, ServerError(json));
        Assert.Contains(expected, EditorErrors(json));
    }

    [Theory]
    [InlineData("\"scaledAmount < FlowInfo(201, 'actions.count')\"", "FlowInfo")]
    [InlineData("\"IncomingCount(201) > 0\"", "Complex gateway")]
    public void Local_validations_reject_flow_evidence_and_gateway_helpers_on_both_sides(string validation, string server)
    {
        var json = Set("multiInstance/outputVariables/0/validation", validation);
        Assert.Contains(server, ServerError(json));
        Assert.Contains(EditorErrors(json), error => error.Contains("local output 'scaledAmount' validation", StringComparison.Ordinal));
    }

    [Fact]
    public void The_canonical_examples_and_the_three_item_names_are_accepted()
    {
        var json = Set("multiInstance/outputVariables/0/validation", "\"[mi.index] < [mi.total] and scaledAmount >= 0\"");
        Assert.Null(ServerError(json));
        Assert.Empty(EditorErrors(json));
        // A name inside a string literal is data, not a reference.
        json = Set("assignments/0/expression", "\"if('[mi.completed]' == 'x', 1, [mi.item])\"");
        Assert.Null(ServerError(json));
        Assert.Empty(EditorErrors(json));
    }

    [Fact]
    public void Script_inspector_offers_item_targets_and_keeps_an_unavailable_value_visible()
    {
        using var engine = Engine();
        var model = JsonNode.Parse(Definition)!;
        model["variables"]!.AsArray().Add(JsonNode.Parse("""{"id":4,"name":"total","dataType":"number","isArray":false,"defaultValue":0}"""));
        model["variables"]!.AsArray().Add(JsonNode.Parse("""{"id":5,"name":"counter","dataType":"number","isArray":false,"scope":"shared","sharedKey":"tests.counter","access":"readWrite"}"""));
        model["variables"]!.AsArray().Add(JsonNode.Parse("""{"id":6,"name":"rate","dataType":"number","isArray":false,"scope":"shared","sharedKey":"tests.rate","access":"read"}"""));
        model["flowNodes"]![1]!["assignments"]![1]!["variable"] = "total";
        engine.SetValue("definition", model.ToJsonString());
        engine.Execute("loadFromObject(JSON.parse(definition)); select('node', 40); render();");

        engine.Execute("""
            var descendants = root => [root, ...(root.children || []).flatMap(child => descendants(child))];
            var targetSelects = () => descendants(inspector).filter(element => element.tagName === 'SELECT'
              && (element.children || []).some(option => String(option.textContent).startsWith('— select item-local'))).slice(-2);
            """);
        var options = JsonSerializer.Deserialize<string[][]>(engine.Evaluate("""
            JSON.stringify(targetSelects().map(select => select.children.map(option => option.value + '|' + option.textContent)))
            """).AsString())!;
        Assert.Equal(2, options.Length);
        Assert.Equal(new[]
        {
            "|— select item-local output or shared alias —",
            "scaledAmount|scaledAmount (number · item-local output)",
            "adjustedAmount|adjustedAmount (number · item-local output)",
            "counter|counter (number · shared readWrite)"
        }, options[0]);
        Assert.Equal("total|total (not an item target)", options[1][^1]);
        Assert.Equal("total", engine.Evaluate("targetSelects()[1].value").AsString());
        // Redrawing never rewrites the imported target.
        engine.Execute("render(); renderInspector();");
        Assert.Equal("total", engine.Evaluate("model.flowNodes.find(node => node.id === 40).assignments[1].variable").AsString());
        Assert.Contains(EditorErrors(engine), error => error.Contains("item target 'total' must be a declared local output or readWrite shared alias", StringComparison.Ordinal));
    }

    private static string Set(string path, string value)
    {
        var model = JsonNode.Parse(Definition)!;
        JsonNode node = model["flowNodes"]![1]!;
        var segments = path.Split('/');
        foreach (var segment in segments[..^1])
            node = int.TryParse(segment, out var index) ? node[index]! : node[segment]!;
        node[segments[^1]] = JsonNode.Parse(value);
        return model.ToJsonString();
    }

    private static string? ServerError(string json)
    {
        var options = new AutomaticMultiInstanceOptions { AllowInDevelopment = true };
        options.ResolveForEnvironment(isDevelopment: true);
        var validator = new WorkflowDefinitionValidator(
            new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance),
            new ServiceTaskOptions(), automaticMultiInstanceOptions: options);
        var definition = JsonSerializer.Deserialize<WorkflowModel>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        try
        {
            validator.ValidateAuthored(definition);
            WorkflowModelMigrator.Normalize(definition);
            validator.ValidateNormalized(definition);
            return null;
        }
        catch (WorkflowDomainException error)
        {
            return error.Message;
        }
    }

    private static Engine Engine()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "hosted");
        engine.Execute("options.automaticMultiInstanceEnabled = true;");
        return engine;
    }

    private static string[] EditorErrors(string json)
    {
        using var engine = Engine();
        engine.SetValue("definition", json);
        engine.Execute("loadFromObject(JSON.parse(definition));");
        return EditorErrors(engine);
    }

    private static string[] EditorErrors(Engine engine) => JsonSerializer.Deserialize<string[]>(
        engine.Evaluate("JSON.stringify(prepareWorkflowExport().errors || [])").AsString())!;
}
