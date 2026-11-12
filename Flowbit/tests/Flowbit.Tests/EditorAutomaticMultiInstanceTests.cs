using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Xunit;

namespace Flowbit.Tests;

public sealed class EditorAutomaticMultiInstanceTests
{
    internal const string Definition = """
        {
          "id":"automatic-items", "name":"Automatic items", "initialEventId":1, "lanes":[],
          "variables":[
            {"id":1,"name":"items","dataType":"number","isArray":true,"defaultValue":[2,2,3]},
            {"id":2,"name":"results","dataType":"json","isArray":false,"defaultValue":[]}
          ],
          "flowNodes":[
            {"id":1,"type":"startEvent","name":"Start","x":80,"y":200},
            {"id":2,"type":"serviceTask","name":"Notify","x":250,"y":180,"asyncBefore":true,
             "service":{"type":"rest","method":"POST","url":"https://example.test/notify","timeoutSeconds":30,"headers":[],"outputMappings":[]},
             "multiInstance":{"mode":"parallel","source":"collection","collectionVariable":"items","completionEvaluation":"afterAll","maxConcurrency":4,"outputVariables":[],"resultVariable":"results"}},
            {"id":3,"type":"endEvent","name":"End","x":550,"y":200}
          ],
          "sequenceFlows":[{"id":101,"sourceRef":1,"targetRef":2},{"id":201,"sourceRef":2,"targetRef":3}]
        }
        """;

    [Theory]
    [InlineData("standalone", false)]
    [InlineData("standalone", true)]
    [InlineData("hosted", false)]
    public void ClosedGatePreservesImportAndReportsExplicitDiagnostic(string mode, bool enabled)
    {
        using var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: mode);
        engine.SetValue("enabled", enabled);
        engine.SetValue("definition", Definition);
        engine.Execute("options.automaticMultiInstanceEnabled = enabled; loadFromObject(JSON.parse(definition)); render();");
        Assert.Equal("items", engine.Evaluate("model.flowNodes.find(node => node.id === 2).multiInstance.collectionVariable").AsString());
        Assert.Contains("Flow node #2 automatic multiInstance is not enabled.", Errors(engine));
        Assert.False(engine.Evaluate("saveAutomaticMultiInstanceEnabled()").AsBoolean());
    }

    [Theory]
    [InlineData("mode", "null", "mode must be")]
    [InlineData("source", "\"cardinality\"", "source must be")]
    [InlineData("cardinalityExpression", "\"1\"", "cannot define cardinalityExpression")]
    [InlineData("onePerActor", "true", "cannot set onePerActor")]
    [InlineData("maxConcurrency", "1.5", "integer from 1 to 1000")]
    [InlineData("completionEvaluation", "\"afterEach\"", "afterAll")]
    [InlineData("outputVariables", "null", "outputVariables array")]
    public void DevelopmentImportKeepsInvalidAutomaticSettingsUntilEdited(string property, string value, string diagnostic)
    {
        using var engine = DevelopmentEngine();
        var model = JsonNode.Parse(Definition)!;
        model["flowNodes"]![1]!["multiInstance"]![property] = JsonNode.Parse(value);
        engine.SetValue("definition", model.ToJsonString());
        engine.SetValue("property", property);
        engine.Execute("loadFromObject(JSON.parse(definition)); select('node', 2); render();");
        var preserved = engine.Evaluate("JSON.stringify(model.flowNodes.find(node => node.id === 2).multiInstance[property])").AsString();
        Assert.Equal(JsonNode.Parse(value)?.ToJsonString() ?? "null", preserved);
        Assert.Contains(Errors(engine), error => error.Contains(diagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public void DevelopmentLocalOutputsAreIndependentOfProcessProducersAndRoundTrip()
    {
        using var engine = DevelopmentEngine();
        engine.SetValue("definition", Definition);
        engine.Execute("""
            loadFromObject(JSON.parse(definition));
            const task = model.flowNodes.find(node => node.id === 2);
            task.multiInstance.outputVariables = [{name:'receipt', dataType:'string', isArray:false, nullable:false}];
            task.service.outputMappings = [{variable:'receipt', path:'receipt', dataType:'string', isArray:false}];
            select('node', 2); render();
            """);
        Assert.Empty(Errors(engine));
        Assert.False(engine.Evaluate("callSourceDeclarations(model).has('receipt')").AsBoolean());
        engine.Execute("loadFromObject(JSON.parse(prepareWorkflowExport().json));");
        Assert.Equal("receipt", engine.Evaluate("model.flowNodes.find(node => node.id === 2).multiInstance.outputVariables[0].name").AsString());
        Assert.Empty(Errors(engine));
    }

    [Fact]
    public void InboxVisibilityCannotReadAnItemLocalOutput()
    {
        using var engine = DevelopmentEngine();
        engine.SetValue("definition", Definition);
        engine.Execute("""
            loadFromObject(JSON.parse(definition));
            const task = model.flowNodes.find(node => node.id === 2);
            task.multiInstance.outputVariables = [{name:'receipt', dataType:'string', isArray:false, nullable:false}];
            task.service.outputMappings = [{variable:'receipt', path:'receipt', dataType:'string', isArray:false}];
            model.variables.push({id:3, name:'region', dataType:'string', isArray:false, defaultValue:'north'});
            model.flowNodes.push({id:4, type:'userTask', name:'Review', x:400, y:180, roles:[], variables:[],
              inboxVisibilityCondition:"[region] == 'north'"});
            model.sequenceFlows.find(flow => flow.id === 201).targetRef = 4;
            model.sequenceFlows.push({id:401, name:'Done', sourceRef:4, targetRef:3, roles:[], variables:[]});
            """);
        Assert.Empty(Errors(engine));
        engine.Execute("model.flowNodes.find(node => node.id === 4).inboxVisibilityCondition = \"[receipt] == 'x'\";");
        Assert.Contains(Errors(engine), error => error.Contains("receipt", StringComparison.Ordinal));
    }

    [Fact]
    public void EnableRefusesImplicitInstanceWritesAndDisablingRefusesLocalPromotion()
    {
        using var engine = DevelopmentEngine();
        engine.SetValue("definition", Definition);
        engine.Execute("""
            loadFromObject(JSON.parse(definition));
            const task = model.flowNodes.find(node => node.id === 2);
            delete task.multiInstance;
            task.service.statusVariable = 'items';
            """);
        Assert.False(engine.Evaluate("setAutomaticMultiInstance(task, true)").AsBoolean());
        engine.Execute("task.service.statusVariable = ''; setAutomaticMultiInstance(task, true);");
        Assert.Equal("afterAll", engine.Evaluate("task.multiInstance.completionEvaluation").AsString());
        Assert.True(engine.Evaluate("task.asyncBefore").AsBoolean());
        engine.Execute("task.multiInstance.outputVariables = [{name:'status',dataType:'number'}]; task.service.statusVariable = 'status';");
        Assert.False(engine.Evaluate("setAutomaticMultiInstance(task, false)").AsBoolean());
        Assert.True(engine.Evaluate("task.multiInstance != null").AsBoolean());
    }

    [Fact]
    public void UnsupportedImportedNodeKeepsItsConfigurationForDiagnostics()
    {
        using var engine = DevelopmentEngine();
        var definition = JsonNode.Parse(Definition)!;
        definition["flowNodes"]![1]!["type"] = "task";
        definition["flowNodes"]![1]!["multiInstance"]!["unknownField"] = "keep-me";
        engine.SetValue("definition", definition.ToJsonString());
        engine.Execute("loadFromObject(JSON.parse(definition)); select('node', 2); render();");
        Assert.Equal("keep-me", engine.Evaluate("model.flowNodes.find(node => node.id === 2).multiInstance.unknownField").AsString());
        Assert.Contains(Errors(engine), error => error.Contains("multiInstance configuration", StringComparison.Ordinal));
    }

    private const string HumanDefinition = """
        {
          "id":"human-items", "name":"Human items", "initialEventId":1, "lanes":[],
          "variables":[
            {"id":1,"name":"approvers","dataType":"string","isArray":true,"defaultValue":["a","b"]},
            {"id":2,"name":"votes","dataType":"json","isArray":false,"defaultValue":[]}
          ],
          "flowNodes":[
            {"id":1,"type":"startEvent","name":"Start","x":80,"y":200},
            {"id":2,"type":"userTask","name":"Review","x":250,"y":180,
             "multiInstance":{"mode":"parallel","source":"collection","collectionVariable":"approvers","completionEvaluation":"afterEach","resultVariable":"votes"}},
            {"id":3,"type":"endEvent","name":"End","x":550,"y":200}
          ],
          "sequenceFlows":[
            {"id":101,"sourceRef":1,"targetRef":2},
            {"id":201,"name":"Approve","sourceRef":2,"targetRef":3,"isSelectable":true},
            {"id":202,"sourceRef":2,"targetRef":3,"isDefault":true,"isSelectable":false}
          ]
        }
        """;

    [Theory]
    [InlineData("serviceTask", false)]
    [InlineData("scriptTask", false)]
    [InlineData("serviceTask", true)]
    [InlineData("callActivity", true)]
    public void TypeChangeNeverTurnsHumanMultiInstanceIntoAnAutomaticBody(string nextType, bool enabled)
    {
        using var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "hosted");
        engine.SetValue("enabled", enabled);
        engine.SetValue("definition", HumanDefinition);
        engine.SetValue("nextType", nextType);
        engine.Execute("""
            options.automaticMultiInstanceEnabled = enabled;
            loadFromObject(JSON.parse(definition));
            const node = model.flowNodes.find(candidate => candidate.id === 2);
            selected = { kind: 'node', nodeId: 2 };
            var changed = changeNodeTypeFromInspector(node, nextType);
            render();
            """);
        Assert.True(engine.Evaluate("changed").AsBoolean());
        Assert.True(engine.Evaluate("model.flowNodes.find(candidate => candidate.id === 2).multiInstance == null").AsBoolean());
        Assert.Equal(1, (int)engine.Evaluate("outgoingFlows(2).length").AsNumber());
        Assert.DoesNotContain(Errors(engine), error => error.Contains("multiInstance", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("standalone", false)]
    [InlineData("hosted", false)]
    public void ClosedGateOffersExplicitRemovalOfAnImportedServiceBody(string mode, bool enabled)
    {
        using var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: mode);
        engine.SetValue("enabled", enabled);
        engine.SetValue("definition", Definition);
        engine.Execute("""
            options.automaticMultiInstanceEnabled = enabled;
            loadFromObject(JSON.parse(definition));
            var descendants = root => [root, ...(root.children || []).flatMap(child => descendants(child))];
            var removal = () => descendants(inspector).filter(element => element.textContent === 'Remove unsupported multi-instance');
            selected = { kind: 'node', nodeId: 2 };
            renderInspector();
            """);
        Assert.Equal(1, (int)engine.Evaluate("removal().length").AsNumber());
        // Selecting and redrawing keep the imported configuration for diagnostics.
        Assert.Contains("Flow node #2 automatic multiInstance is not enabled.", Errors(engine));
        engine.Execute("removal()[0].onclick();");
        Assert.True(engine.Evaluate("model.flowNodes.find(node => node.id === 2).multiInstance == null").AsBoolean());
        Assert.DoesNotContain(Errors(engine), error => error.Contains("multiInstance", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenGateKeepsTheAutomaticSectionInsteadOfTheRemovalButton()
    {
        using var engine = DevelopmentEngine();
        engine.SetValue("definition", Definition);
        engine.Execute("""
            loadFromObject(JSON.parse(definition));
            var descendants = root => [root, ...(root.children || []).flatMap(child => descendants(child))];
            selected = { kind: 'node', nodeId: 2 };
            renderInspector();
            """);
        Assert.Equal(0, (int)engine.Evaluate(
            "descendants(inspector).filter(element => element.textContent === 'Remove unsupported multi-instance').length").AsNumber());
        Assert.Equal(1, (int)engine.Evaluate(
            "descendants(inspector).filter(element => element.tagName === 'H2' && element.textContent === 'Automatic multi-instance').length").AsNumber());
    }

    private static Engine DevelopmentEngine()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "hosted");
        engine.Execute("options.automaticMultiInstanceEnabled = true;");
        return engine;
    }

    private static string[] Errors(Engine engine) => JsonSerializer.Deserialize<string[]>(
        engine.Evaluate("JSON.stringify(prepareWorkflowExport().errors || [])").AsString())!;
}
