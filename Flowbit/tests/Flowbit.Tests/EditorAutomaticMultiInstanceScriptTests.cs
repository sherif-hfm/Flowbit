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
            "Automatic multi-instance activity #40 local output 'adjustedAmount' defaultValue can read only [mi.item], [mi.index], and [mi.total]; 'mi.completed' is not item context." },
        // Staging leaves the body's aggregate out, so an item could never read it.
        { "assignments/1/expression", "\"scaledAmount + Len(calculationResults)\"",
            "Automatic multi-instance activity #40 assignment for 'adjustedAmount' reads 'calculationResults', this body's aggregate target, which its items never receive." },
        { "multiInstance/outputVariables/0/validation", "\"[CalculationResults] != null\"",
            "Automatic multi-instance activity #40 local output 'scaledAmount' validation reads 'calculationResults', this body's aggregate target, which its items never receive." },
        { "multiInstance/outputVariables/1/defaultValue", "\"${calculationResults}\"",
            "Automatic multi-instance activity #40 local output 'adjustedAmount' defaultValue reads 'calculationResults', this body's aggregate target, which its items never receive." }
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
        // The aggregate's name as quoted text or as part of another name is no read.
        json = Set("assignments/0/expression", "\"if('calculationResults' == 'x', [calculationResults_count], [mi.item])\"");
        json = SetIn(json, "variables", """{"id":4,"name":"calculationResults_count","dataType":"number","isArray":false,"defaultValue":0}""");
        Assert.Null(ServerError(json));
        Assert.Empty(EditorErrors(json));
    }

    // Each side reports one problem per body, so the editor must name the problem the
    // server reports first, in the server's words.
    public static TheoryData<string, string> FirstErrorCases => new()
    {
        { """{"multiInstance/outputVariables/0/defaultValue":"\"abc\""}""",
            "Automatic multi-instance activity #40 local output 'scaledAmount' defaultValue must be number." },
        { """{"multiInstance/outputVariables/0/defaultValue":"[1]"}""",
            "Automatic multi-instance activity #40 local output 'scaledAmount' defaultValue must be number." },
        // A non-numeric value never reaches these rules: request binding refuses it.
        { """{"multiInstance/maxConcurrency":"null","multiInstance/source":"\"cardinality\""}""",
            "Automatic multi-instance activity #40 maxConcurrency must be an integer from 1 to 1000." },
        { """{"multiInstance/maxConcurrency":"0","multiInstance/collectionVariable":"\"missing\""}""",
            "Automatic multi-instance activity #40 maxConcurrency must be an integer from 1 to 1000." },
        { """{"multiInstance/mode":"\"both\"","multiInstance/collectionVariable":"\"missing\""}""",
            "Automatic multi-instance activity #40 mode must be 'parallel' or 'sequential'." },
        { """{"multiInstance/outputVariables/0/defaultValue":"\"abc\"","assignments/1/variable":"\"multiplier\""}""",
            "Automatic multi-instance activity #40 item target 'multiplier' must be a declared local output or readWrite shared alias." }
    };

    [Theory]
    [MemberData(nameof(FirstErrorCases))]
    public void The_editor_reports_the_servers_first_body_problem(string edits, string expected)
    {
        var json = Definition;
        foreach (var (path, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(edits)!)
            json = Set(json, path, value);
        Assert.Equal(expected, ServerError(json));
        Assert.Contains(expected, EditorErrors(json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Switching_a_bodys_script_format_asks_before_discarding_its_assignments(bool accept)
    {
        using var engine = Engine();
        engine.SetValue("definition", Definition);
        engine.SetValue("accept", accept);
        engine.Execute("""
            loadFromObject(JSON.parse(definition));
            var dialogs = [];
            confirm = message => { dialogs.push(String(message)); return accept; };
            selected = { kind: 'node', nodeId: 40 };
            renderInspector();
            var descendants = root => [root, ...(root.children || []).flatMap(child => descendants(child))];
            // Options are written through innerHTML, which the DOM stub keeps as text.
            var format = descendants(inspector).filter(element => element.tagName === 'SELECT'
              && String(element.innerHTML).includes('value="javascript"')).slice(-1)[0];
            format.value = 'javascript';
            format.onchange();
            """);
        Assert.Equal(1, (int)engine.Evaluate("dialogs.length").AsNumber());
        Assert.Equal("Switching this multi-instance body to JavaScript removes its 2 NCalc assignments. Undo restores them. Switch the format?",
            engine.Evaluate("dialogs[0]").AsString());
        Assert.Equal(accept ? "javascript" : "ncalc",
            engine.Evaluate("model.flowNodes.find(n => n.id === 40).scriptFormat").AsString());
        Assert.Equal(accept ? 0 : 2, (int)engine.Evaluate("model.flowNodes.find(n => n.id === 40).assignments.length").AsNumber());
    }

    [Fact]
    public void Switching_an_ordinary_script_does_not_ask()
    {
        using var engine = Engine();
        var model = JsonNode.Parse(Definition)!;
        // An ordinary script (no multi-instance) keeps its earlier behaviour.
        model["flowNodes"]![1]!.AsObject().Remove("multiInstance");
        engine.SetValue("definition", model.ToJsonString());
        engine.Execute("""
            loadFromObject(JSON.parse(definition));
            var dialogs = [];
            confirm = message => { dialogs.push(String(message)); return false; };
            selected = { kind: 'node', nodeId: 40 };
            renderInspector();
            var descendants = root => [root, ...(root.children || []).flatMap(child => descendants(child))];
            var format = () => descendants(inspector).filter(element => element.tagName === 'SELECT'
              && String(element.innerHTML).includes('value="javascript"')).slice(-1)[0];
            var switchTo = value => { var select = format(); select.value = value; select.onchange(); };
            switchTo('javascript');
            """);
        Assert.Equal(0, (int)engine.Evaluate("dialogs.length").AsNumber());
        Assert.Equal("javascript", engine.Evaluate("model.flowNodes.find(n => n.id === 40).scriptFormat").AsString());
    }

    [Fact]
    public void Typing_an_assignment_refreshes_the_bodys_status()
    {
        using var engine = Engine();
        engine.SetValue("definition", Definition);
        engine.Execute("""
            loadFromObject(JSON.parse(definition));
            selected = { kind: 'node', nodeId: 40 };
            renderInspector();
            var descendants = root => [root, ...(root.children || []).flatMap(child => descendants(child))];
            var status = () => descendants(inspector).filter(element => element.tagName === 'P'
              && element.getAttribute && element.getAttribute('aria-live') === 'polite'
              && element.className === 'expression-validation').slice(-1)[0];
            var before = status().textContent;
            // A field's own handler has written the model by the time the event reaches the root.
            model.flowNodes.find(n => n.id === 40).assignments[1].expression = 'scaledAmount + [mi.completed]';
            inspector.contains = () => true;
            // The test harness registers the editor root's listeners on the document.
            var typed = { tagName: 'INPUT' };
            document.dispatchEvent({ type: 'input', target: typed, bubbles: true });
            var after = status().textContent;
            """);
        Assert.Equal("", engine.Evaluate("before").AsString());
        Assert.Equal("Automatic multi-instance activity #40 assignment for 'adjustedAmount' can read only [mi.item], [mi.index], and [mi.total]; 'mi.completed' is not item context.",
            engine.Evaluate("after").AsString());
    }

    [Fact]
    public void Toggling_a_boundarys_interruption_redraws_the_canvas()
    {
        using var engine = Engine();
        var model = JsonNode.Parse(Definition)!;
        model["flowNodes"]!.AsArray().Add(JsonNode.Parse("""
            {"id":41,"type":"conditionalBoundaryEvent","name":"Approved","attachedToRef":40,"cancelActivity":true,"x":300,"y":250,
             "conditional":{"condition":"multiplier > 2"}}
            """));
        model["flowNodes"]!.AsArray().Add(JsonNode.Parse("""{"id":5,"type":"endEvent","name":"Approved end","x":450,"y":320}"""));
        model["sequenceFlows"]!.AsArray().Add(JsonNode.Parse("""{"id":4101,"sourceRef":41,"targetRef":5}"""));
        engine.SetValue("definition", model.ToJsonString());
        engine.Execute("""
            loadFromObject(JSON.parse(definition));
            selected = { kind: 'node', nodeId: 41 };
            renderInspector();
            var nodeRedraws = 0;
            var originalRenderNodes = renderNodes;
            renderNodes = () => { nodeRedraws++; originalRenderNodes(); };
            var descendants = root => [root, ...(root.children || []).flatMap(child => descendants(child))];
            var toggle = descendants(inspector).filter(element => element.tagName === 'LABEL'
              && (element.children || []).some(child => child.type === 'checkbox')
              && descendants(element).some(child => String(child.textContent).includes('Interrupt attached activity'))).slice(-1)[0]
              .children.find(child => child.type === 'checkbox');
            toggle.checked = false;
            toggle.onchange();
            """);
        Assert.False(engine.Evaluate("model.flowNodes.find(n => n.id === 41).cancelActivity").AsBoolean());
        // The ring is drawn solid or dashed from cancelActivity, so the canvas must redraw.
        Assert.True(engine.Evaluate("nodeRedraws").AsNumber() > 0);
    }

    // A body writes its error code through a declared contract, so save refuses an
    // undeclared error target rather than letting the route fail at runtime.
    [Fact]
    public void A_bodys_error_boundary_target_must_be_declared_on_both_sides()
    {
        var json = SetIn(Definition, "flowNodes", """
            {"id":41,"type":"errorBoundaryEvent","name":"Failed","attachedToRef":40,"errorVariable":"failure","x":300,"y":250}
            """);
        json = SetIn(json, "flowNodes", """{"id":6,"type":"endEvent","name":"Handled","x":450,"y":320}""");
        json = SetIn(json, "sequenceFlows", """{"id":4101,"sourceRef":41,"targetRef":6}""");
        const string expected = "Automatic multi-instance activity #40 boundary error target 'failure' must be a declared process variable.";
        Assert.Equal(expected, ServerError(json));
        Assert.Contains(expected, EditorErrors(json));

        json = SetIn(json, "variables", """{"id":9,"name":"failure","dataType":"string","isArray":false,"nullable":true}""");
        Assert.Null(ServerError(json));
        Assert.Empty(EditorErrors(json));
    }

    [Fact]
    public void A_JavaScript_body_is_not_checked_for_aggregate_reads()
    {
        var model = JsonNode.Parse(Definition)!;
        var body = model["flowNodes"]![1]!;
        body["scriptFormat"] = "javascript";
        body.AsObject().Remove("assignments");
        body["script"] = "execution.setVariable('scaledAmount', 1); execution.setVariable('adjustedAmount', execution.hasVariable('calculationResults') ? 1 : 0);";
        var json = model.ToJsonString();
        Assert.Null(ServerError(json));
        Assert.Empty(EditorErrors(json));
    }

    private static string SetIn(string json, string array, string entry)
    {
        var model = JsonNode.Parse(json)!;
        model[array]!.AsArray().Add(JsonNode.Parse(entry));
        return model.ToJsonString();
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

    private static string Set(string path, string value) => Set(Definition, path, value);

    private static string Set(string json, string path, string value)
    {
        var model = JsonNode.Parse(json)!;
        JsonNode node = model["flowNodes"]![1]!;
        var segments = path.Split('/');
        foreach (var segment in segments[..^1])
            node = int.TryParse(segment, out var index) ? node[index]! : node[segment]!;
        node[segments[^1]] = JsonNode.Parse(value);
        return model.ToJsonString();
    }

    private static string? ServerError(string json)
    {
        var validator = new WorkflowDefinitionValidator(
            new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance),
            new ServiceTaskOptions());
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
