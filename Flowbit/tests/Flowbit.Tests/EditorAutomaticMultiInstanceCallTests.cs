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
/// TASK-05D authoring rules for call bodies: the item sources <c>mi.item</c>, <c>mi.index</c>,
/// and <c>mi.total</c>, shared input sources, and local or shared output targets, through the
/// server validator and the editor's save validator, plus the call-only editor reviews.
/// </summary>
public sealed class EditorAutomaticMultiInstanceCallTests
{
    // The plan's example, with a readable shared input and a readWrite shared result.
    private const string Definition = """
        {
          "id":"call-items", "name":"Call items", "initialEventId":1, "lanes":[],
          "variables":[
            {"id":1,"name":"orders","dataType":"json","isArray":true,"defaultValue":[{"id":"a"},{"id":"b"}]},
            {"id":2,"name":"orderResults","dataType":"json","isArray":false,"defaultValue":[]},
            {"id":3,"name":"region","dataType":"string","isArray":false,"scope":"shared","sharedKey":"tests.call.region","access":"read"},
            {"id":4,"name":"lastApproved","dataType":"boolean","isArray":false,"nullable":true,"scope":"shared","sharedKey":"tests.call.lastApproved","access":"readWrite"}
          ],
          "flowNodes":[
            {"id":1,"type":"startEvent","name":"Start","x":80,"y":200},
            {"id":20,"type":"callActivity","name":"Check each order","x":250,"y":180,
             "multiInstance":{"mode":"parallel","source":"collection","collectionVariable":"orders","completionEvaluation":"afterAll",
               "maxConcurrency":4,"resultVariable":"orderResults",
               "outputVariables":[{"name":"itemApproved","dataType":"boolean","nullable":false}]},
             "callActivity":{"workflowKey":"check-order",
               "inputMappings":[
                 {"sourceVariable":"mi.item","variable":"order","dataType":"json","required":true},
                 {"sourceVariable":"mi.index","variable":"orderIndex","dataType":"number","required":true},
                 {"sourceVariable":"mi.total","variable":"orderCount","dataType":"number","required":true},
                 {"sourceVariable":"region","variable":"region","dataType":"string","required":true}],
               "outputMappings":[
                 {"sourceVariable":"approved","variable":"itemApproved","dataType":"boolean","required":true},
                 {"sourceVariable":"approved","variable":"lastApproved","dataType":"boolean","required":true}]}},
            {"id":3,"type":"endEvent","name":"End","x":550,"y":200}
          ],
          "sequenceFlows":[{"id":101,"sourceRef":1,"targetRef":20},{"id":201,"sourceRef":20,"targetRef":3}]
        }
        """;

    public static TheoryData<string, string, string> ItemSourceCases => new()
    {
        // Item sources are spelled exactly; any other mi.* name keeps the reserved-prefix rule.
        { "callActivity/inputMappings/1/sourceVariable", "\"mi.Index\"",
            "Call activity #20 callActivity.inputMappings[1].sourceVariable 'mi.Index' uses a reserved context prefix." },
        { "callActivity/inputMappings/1/sourceVariable", "\"mi.completed\"",
            "Call activity #20 callActivity.inputMappings[1].sourceVariable 'mi.completed' uses a reserved context prefix." },
        // mi.item takes the collection's element type; mi.index and mi.total are numbers.
        { "callActivity/inputMappings/0/dataType", "\"string\"",
            "Call activity #20 input source 'mi.item' is not declared as string." },
        { "callActivity/inputMappings/2/isArray", "true",
            "Call activity #20 input source 'mi.total' is not declared as number[]." },
        // Items never read their body's aggregate, so it is no input source.
        { "callActivity/inputMappings/1/sourceVariable", "\"orderResults\"",
            "Call activity #20 input source 'orderResults' is this body's aggregate target, which its items never read." },
        // A child's result never reads parent item context.
        { "callActivity/outputMappings/0/sourceVariable", "\"mi.item\"",
            "Call activity #20 callActivity.outputMappings[0].sourceVariable 'mi.item' uses a reserved context prefix." },
        // An item writes its locals or readWrite shared aliases, never a process variable.
        { "callActivity/outputMappings/1/variable", "\"orderResults\"",
            "Automatic multi-instance activity #20 item target 'orderResults' overlaps its collection, aggregate, or boundary error target." }
    };

    [Theory]
    [MemberData(nameof(ItemSourceCases))]
    public void Item_source_rules_agree_between_server_and_editor(string path, string value, string expected)
    {
        var json = Set(path, value);
        Assert.Equal(expected, ServerError(json));
        Assert.Contains(expected, EditorErrors(json));
    }

    [Fact]
    public void The_example_body_with_item_sources_and_shared_mappings_is_accepted_on_both_sides()
    {
        Assert.Null(ServerError(Definition));
        Assert.Empty(EditorErrors(Definition));
    }

    [Fact]
    public void An_ordinary_call_reads_no_item_source_on_either_side()
    {
        var model = JsonNode.Parse(Definition)!;
        var call = model["flowNodes"]![1]!.AsObject();
        call.Remove("multiInstance");
        call["callActivity"]!["inputMappings"] = JsonNode.Parse("""[{"sourceVariable":"mi.item","variable":"order","dataType":"json","required":true}]""");
        call["callActivity"]!["outputMappings"] = JsonNode.Parse("[]");
        var json = model.ToJsonString();
        const string expected = "Call activity #20 callActivity.inputMappings[0].sourceVariable 'mi.item' uses a reserved context prefix.";
        Assert.Equal(expected, ServerError(json));
        Assert.Contains(expected, EditorErrors(json));
    }

    [Fact]
    public void Item_sources_are_typed_from_the_collection_and_join_the_workflows_declarations_only_on_a_call_body()
    {
        var definition = JsonSerializer.Deserialize<WorkflowModel>(Definition, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var body = definition.FlowNodes.Single(node => node.Id == 20);
        var sources = CallActivityRules.InputSourceDeclarations(definition, body);
        var item = Assert.Single(sources["mi.item"]);
        Assert.Equal((WorkflowVariableTypes.Json, false, true), (item.DataType, item.IsArray, item.MayBeNull));
        Assert.All(new[] { "mi.index", "mi.total" }, name =>
            Assert.Equal((WorkflowVariableTypes.Number, false, false),
                (sources[name].Single().DataType, sources[name].Single().IsArray, sources[name].Single().MayBeNull)));
        // The workflow's own declarations never include item context, for any node.
        Assert.DoesNotContain("mi.item", CallActivityRules.SourceDeclarations(definition).Keys);
        body.MultiInstance = null;
        Assert.DoesNotContain("mi.item", CallActivityRules.InputSourceDeclarations(definition, body).Keys);
    }

    [Fact]
    public void An_optional_json_item_mapping_needs_a_child_input_that_accepts_null()
    {
        var definition = JsonSerializer.Deserialize<WorkflowModel>(Definition, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var body = definition.FlowNodes.Single(node => node.Id == 20);
        body.CallActivity!.InputMappings[0].Required = false;
        var child = new WorkflowModel
        {
            Id = "check-order", Name = "Check order", InitialEventId = 1,
            Variables = [new() { Id = 1, Name = "approved", DataType = "boolean", Nullable = false, DefaultValue = JsonSerializer.SerializeToElement(false) }],
            FlowNodes =
            [
                new()
                {
                    Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent,
                    Variables =
                    [
                        new() { Id = 1, Name = "order", DataType = "json", Required = true },
                        new() { Id = 2, Name = "orderIndex", DataType = "number", Required = true },
                        new() { Id = 3, Name = "orderCount", DataType = "number", Required = true },
                        new() { Id = 4, Name = "region", DataType = "string", Required = true }
                    ]
                },
                new() { Id = 2, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new() { Id = 101, SourceRef = 1, TargetRef = 2 }]
        };
        var problems = CallActivityRules.FindTargetProblems(definition, body, child);
        Assert.Contains(problems, problem => problem.Message.Contains("source 'mi.item' can be null", StringComparison.Ordinal));
        // Required, the same mapping fits.
        body.CallActivity.InputMappings[0].Required = true;
        Assert.Empty(CallActivityRules.FindTargetProblems(definition, body, child));
    }

    [Fact]
    public void Disabling_multi_instance_on_a_call_reviews_its_item_and_shared_mappings_first()
    {
        using var engine = Engine();
        engine.SetValue("definition", Definition);
        engine.Execute("""
            loadFromObject(JSON.parse(definition));
            var alerts = [];
            alert = message => alerts.push(message);
            confirm = () => true;
            var node = model.flowNodes.find(n => n.id === 20);
            node.callActivity.outputMappings = node.callActivity.outputMappings.filter(m => m.variable !== 'itemApproved');
            node.multiInstance.outputVariables = [];
            var refused = setAutomaticMultiInstance(node, false);
            """);
        Assert.False(engine.Evaluate("refused").AsBoolean());
        Assert.True(engine.Evaluate("model.flowNodes.find(n => n.id === 20).multiInstance != null").AsBoolean());
        var alert = engine.Evaluate("alerts[0]").AsString();
        Assert.Contains("input mi.item", alert);
        Assert.Contains("input region", alert);
        Assert.Contains("result lastApproved", alert);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("\"parallel\"")]
    public void An_imported_multi_instance_that_is_not_an_object_is_kept_for_validation_and_never_shown_as_a_body(string value)
    {
        var json = Set("multiInstance", value);
        using var engine = Engine();
        engine.SetValue("definition", json);
        engine.Execute("""
            loadFromObject(JSON.parse(definition));
            var descendants = root => [root, ...(root.children || []).flatMap(child => descendants(child))];
            selected = { kind: 'node', nodeId: 20 };
            renderInspector();
            """);
        // The value is kept as imported, so save validation reports it.
        Assert.Equal(JsonNode.Parse(value)!.ToJsonString(),
            engine.Evaluate("JSON.stringify(model.flowNodes.find(n => n.id === 20).multiInstance)").AsString());
        Assert.False(engine.Evaluate("isCallBodyNode(model.flowNodes.find(n => n.id === 20))").AsBoolean());
        // An ordinary call cannot host timer or conditional boundaries, so none is offered.
        Assert.Equal(0, (int)engine.Evaluate(
            "descendants(inspector).filter(element => element.tagName === 'H2' && element.textContent === 'Boundary events').length").AsNumber());
        Assert.NotEmpty(EditorErrors(engine));
    }

    [Fact]
    public void A_call_body_offers_its_boundary_section()
    {
        using var engine = Engine();
        engine.SetValue("definition", Definition);
        engine.Execute("""
            loadFromObject(JSON.parse(definition));
            var descendants = root => [root, ...(root.children || []).flatMap(child => descendants(child))];
            selected = { kind: 'node', nodeId: 20 };
            renderInspector();
            """);
        Assert.Equal(1, (int)engine.Evaluate(
            "descendants(inspector).filter(element => element.tagName === 'H2' && element.textContent === 'Boundary events').length").AsNumber());
    }

    private static string Set(string path, string value)
    {
        var model = JsonNode.Parse(Definition)!;
        JsonNode node = model["flowNodes"]![1]!;
        var segments = path.Split('/');
        foreach (var segment in segments[..^1])
            node = int.TryParse(segment, out var index) ? node[index]! : node[segment]!;
        if (int.TryParse(segments[^1], out var last)) node[last] = JsonNode.Parse(value);
        else node[segments[^1]] = JsonNode.Parse(value);
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
