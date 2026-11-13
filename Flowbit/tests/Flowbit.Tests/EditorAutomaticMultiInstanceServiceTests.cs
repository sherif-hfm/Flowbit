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
/// TASK-05B authoring rules for REST service bodies: item context references in the
/// request templates and mapping expressions, through the server validator and the
/// editor's save validator, plus the service inspector's item targets.
/// </summary>
public sealed class EditorAutomaticMultiInstanceServiceTests
{
    private const string Definition = """
        {
          "id":"service-items", "name":"Service items", "initialEventId":1, "lanes":[],
          "variables":[
            {"id":1,"name":"supplierIds","dataType":"string","isArray":true,"defaultValue":["supplier-a","supplier-b"]},
            {"id":2,"name":"region","dataType":"string","isArray":false,"defaultValue":"eu"},
            {"id":3,"name":"notificationResults","dataType":"json","isArray":false,"defaultValue":[]}
          ],
          "flowNodes":[
            {"id":1,"type":"startEvent","name":"Start","x":80,"y":200},
            {"id":20,"type":"serviceTask","name":"Notify suppliers","x":250,"y":180,"asyncBefore":true,
             "multiInstance":{"mode":"parallel","source":"collection","collectionVariable":"supplierIds","completionEvaluation":"afterAll",
               "maxConcurrency":4,"resultVariable":"notificationResults",
               "outputVariables":[{"name":"notificationId","dataType":"string","nullable":false},{"name":"responseStatus","dataType":"number","nullable":false}]},
             "service":{"type":"rest","method":"POST","url":"https://notifications.example.test/send/${mi.index}",
               "headers":[{"name":"Content-Type","value":"application/json"},{"name":"Idempotency-Key","value":"flowbit-supplier-${sys.jobId}"}],
               "body":"{\"supplier\":${mi.item},\"total\":${mi.total},\"region\":\"${region}\"}","timeoutSeconds":30,"statusVariable":"responseStatus",
               "outputMappings":[{"variable":"notificationId","path":"id","dataType":"string","isArray":false,"required":true,"defaultValue":null,"validation":null}]}},
            {"id":3,"type":"endEvent","name":"End","x":550,"y":200}
          ],
          "sequenceFlows":[{"id":101,"sourceRef":1,"targetRef":20},{"id":201,"sourceRef":20,"targetRef":3}]
        }
        """;

    public static TheoryData<string, string, string> ItemContextCases => new()
    {
        { "service/url", "\"https://notifications.example.test/${mi.completed}\"",
            "Automatic multi-instance activity #20 URL can read only [mi.item], [mi.index], and [mi.total]; 'mi.completed' is not item context." },
        { "service/headers/1/value", "\"key-${mi.remaining}\"",
            "Automatic multi-instance activity #20 header 'Idempotency-Key' can read only [mi.item], [mi.index], and [mi.total]; 'mi.remaining' is not item context." },
        // Both sides quote the header name as normalization trims it.
        { "service/headers/1", "{\"name\":\"  Idempotency-Key\\u00A0\",\"value\":\"key-${mi.remaining}\"}",
            "Automatic multi-instance activity #20 header 'Idempotency-Key' can read only [mi.item], [mi.index], and [mi.total]; 'mi.remaining' is not item context." },
        { "service/body", "\"{\\\"supplier\\\":${mi.item},\\\"done\\\":${mi.completed}}\"",
            "Automatic multi-instance activity #20 body can read only [mi.item], [mi.index], and [mi.total]; 'mi.completed' is not item context." },
        { "service/outputMappings/0/defaultValue", "\"${mi.completed}\"",
            "Automatic multi-instance activity #20 output mapping 'notificationId' defaultValue can read only [mi.item], [mi.index], and [mi.total]; 'mi.completed' is not item context." },
        { "service/outputMappings/0/validation", "\"Len(notificationId) > [mi.remaining]\"",
            "Automatic multi-instance activity #20 output mapping 'notificationId' validation can read only [mi.item], [mi.index], and [mi.total]; 'mi.remaining' is not item context." }
    };

    [Theory]
    [MemberData(nameof(ItemContextCases))]
    public void Item_context_rules_agree_between_server_and_editor(string path, string value, string expected)
    {
        var json = Set(path, value);
        Assert.Equal(expected, ServerError(json));
        Assert.Contains(expected, EditorErrors(json));
    }

    [Fact]
    public void The_example_body_with_item_index_total_and_a_stable_job_header_is_accepted()
    {
        Assert.Null(ServerError(Definition));
        Assert.Empty(EditorErrors(Definition));
    }

    [Fact]
    public void A_mapping_to_a_process_variable_is_refused_on_both_sides()
    {
        var json = Set("service/outputMappings/0/variable", "\"region\"");
        const string expected = "Automatic multi-instance activity #20 item target 'region' must be a declared local output or readWrite shared alias.";
        Assert.Equal(expected, ServerError(json));
        Assert.Contains(expected, EditorErrors(json));
    }

    [Fact]
    public void Service_inspector_suggests_item_targets_for_mappings_and_number_targets_for_status()
    {
        using var engine = Engine();
        var model = JsonNode.Parse(Definition)!;
        model["variables"]!.AsArray().Add(JsonNode.Parse("""{"id":5,"name":"lastNotice","dataType":"string","isArray":false,"scope":"shared","sharedKey":"tests.notice","access":"readWrite"}"""));
        model["variables"]!.AsArray().Add(JsonNode.Parse("""{"id":6,"name":"rate","dataType":"number","isArray":false,"scope":"shared","sharedKey":"tests.rate","access":"read"}"""));
        model["variables"]!.AsArray().Add(JsonNode.Parse("""{"id":7,"name":"sharedStatus","dataType":"number","isArray":false,"scope":"shared","sharedKey":"tests.status","access":"readWrite"}"""));
        engine.SetValue("definition", model.ToJsonString());
        engine.Execute("loadFromObject(JSON.parse(definition)); select('node', 20); render();");

        engine.Execute("""
            var descendants = root => [root, ...(root.children || []).flatMap(child => descendants(child))];
            var itemLists = () => descendants(inspector).filter(element => element.tagName === 'DATALIST'
              && String(element.id).startsWith('automatic-item-target-')).slice(-2);
            """);
        var lists = JsonSerializer.Deserialize<string[][]>(engine.Evaluate("""
            JSON.stringify(itemLists().map(list => list.children.map(option => option.value + '|' + option.label)))
            """).AsString())!;
        Assert.Equal(2, lists.Length);
        // The status field offers number targets only; the mapping offers every item target.
        Assert.Equal(new[] { "responseStatus|number · item-local output", "sharedStatus|number · shared readWrite" }, lists[0]);
        Assert.Equal(new[]
        {
            "notificationId|string · item-local output",
            "responseStatus|number · item-local output",
            "lastNotice|string · shared readWrite",
            "sharedStatus|number · shared readWrite"
        }, lists[1]);
        // Redrawing never rewrites the imported mapping.
        engine.Execute("render(); renderInspector();");
        Assert.Equal("notificationId|string", engine.Evaluate(
            "(m => m.variable + '|' + m.dataType)(model.flowNodes.find(node => node.id === 20).service.outputMappings[0])").AsString());
    }

    // Normalization fills a mapping's missing type only from a process variable, on both
    // sides, so a mapping to an item-local output must state its type: the server and the
    // editor refuse the untyped one with the same message rather than one side repairing it.
    [Fact]
    public void A_mapping_to_an_item_local_output_without_a_data_type_is_refused_on_both_sides()
    {
        var json = Set("service/outputMappings/0", """{"variable":"notificationId","path":"id","required":true}""");
        const string expected = "Service task #20 output mapping 'notificationId' must match process variable 'notificationId' type string.";
        Assert.Equal(expected, ServerError(json));
        Assert.Contains(expected, EditorErrors(json));
    }

    // Infinity and NaN have no JSON number, so a number default that parses to one is
    // refused at save on both sides rather than failing every item at run time. The two
    // sides already word default type errors differently, so each is matched on its own.
    [Theory]
    [InlineData("1e9999")]
    [InlineData("-1e9999")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void A_number_default_that_is_not_a_finite_number_is_refused_on_both_sides(string text)
    {
        var model = JsonNode.Parse(Definition)!;
        var body = model["flowNodes"]![1]!;
        body["multiInstance"]!["outputVariables"]!.AsArray().Add(JsonNode.Parse(
            $$"""{"name":"quantity","dataType":"number","nullable":false,"defaultValue":"{{text}}"}"""));
        var local = model.ToJsonString();
        Assert.Equal("Automatic multi-instance activity #20 local output 'quantity' defaultValue must be number.", ServerError(local));
        Assert.Contains(EditorErrors(local), error => error.Contains("local output 'quantity' defaultValue"));

        model = JsonNode.Parse(Definition)!;
        body = model["flowNodes"]![1]!;
        body["multiInstance"]!["outputVariables"]!.AsArray().Add(JsonNode.Parse(
            """{"name":"quantity","dataType":"number","nullable":false}"""));
        body["service"]!["outputMappings"]!.AsArray().Add(JsonNode.Parse(
            $$"""{"variable":"quantity","path":"quantity","dataType":"number","isArray":false,"required":false,"defaultValue":"{{text}}","validation":null}"""));
        var mapping = model.ToJsonString();
        Assert.Equal("Service task #20 output mapping default for 'quantity' must be number.", ServerError(mapping));
        Assert.Contains(EditorErrors(mapping), error => error.Contains("output mapping default for 'quantity'"));
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
