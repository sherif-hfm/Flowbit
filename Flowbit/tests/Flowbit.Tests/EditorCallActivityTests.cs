using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Jint;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class EditorCallActivityTests
{
    private const string BaseModel = """
        {
          "id": "parent", "name": "Parent", "initialEventId": 1, "lanes": [],
          "variables": [
            { "id": 1, "name": "decision", "dataType": "string", "isArray": false, "nullable": true },
            { "id": 2, "name": "scores", "dataType": "number", "isArray": true, "nullable": true }
          ],
          "flowNodes": [
            { "id": 1, "name": "Start", "type": "startEvent", "variables": [
              { "id": 1, "name": "amount", "dataType": "number", "isArray": false, "required": true }
            ] },
            { "id": 2, "name": "Approve", "type": "callActivity", "callActivity": {
              "workflowKey": "child",
              "inputMappings": [{ "sourceVariable": "amount", "variable": "amount", "dataType": "number", "required": true }],
              "outputMappings": [{ "sourceVariable": "result", "variable": "decision", "dataType": "string" }]
            } },
            { "id": 3, "name": "End", "type": "endEvent" }
          ],
          "sequenceFlows": [
            { "id": 101, "sourceRef": 1, "targetRef": 2 },
            { "id": 201, "sourceRef": 2, "targetRef": 3 }
          ]
        }
        """;

    private static JsonObject Node(JsonObject model, int id) =>
        model["flowNodes"]!.AsArray().Select(node => node!.AsObject()).Single(node => (int)node["id"]! == id);

    private static JsonObject Call(JsonObject model) => Node(model, 2)["callActivity"]!.AsObject();

    private static JsonObject Input(JsonObject model) => Call(model)["inputMappings"]![0]!.AsObject();

    private static JsonObject Output(JsonObject model) => Call(model)["outputMappings"]![0]!.AsObject();

    private static void AddNode(JsonObject model, string json) =>
        model["flowNodes"]!.AsArray().Add(JsonNode.Parse(json));

    private static void AddFlow(JsonObject model, int id, int source, int target) =>
        model["sequenceFlows"]!.AsArray().Add(JsonNode.Parse(
            $$"""{ "id": {{id}}, "sourceRef": {{source}}, "targetRef": {{target}} }"""));

    private static void AddBoundary(JsonObject model, int id, string extra = "")
    {
        AddNode(model, $$"""{ "id": {{id}}, "name": "Boundary {{id}}", "type": "errorBoundaryEvent", "attachedToRef": 2{{extra}} }""");
        AddFlow(model, id * 100 + 1, id, 3);
    }

    private static readonly Dictionary<string, Action<JsonObject>> ValidCases = new()
    {
        ["base"] = _ => { },
        ["default binding written out"] = model => Call(model)["versionBinding"] = "publishedDefault",
        ["no mappings"] = model =>
        {
            Call(model).Remove("inputMappings");
            Call(model).Remove("outputMappings");
        },
        ["empty mappings"] = model =>
        {
            Call(model)["inputMappings"] = new JsonArray();
            Call(model)["outputMappings"] = new JsonArray();
        },
        ["same name both directions"] = model => Output(model)["sourceVariable"] = "amount",
        ["array output"] = model =>
        {
            Output(model)["variable"] = "scores";
            Output(model)["dataType"] = "number";
            Output(model)["isArray"] = true;
        },
        ["source produced by a service output"] = model =>
        {
            Input(model)["sourceVariable"] = "total";
            Node(model, 2)["callActivity"]!["inputMappings"]![0]!["variable"] = "total";
            AddNode(model, """
                { "id": 4, "name": "Load", "type": "serviceTask", "service": {
                  "type": "rest", "method": "GET", "url": "http://example.test/", "timeoutSeconds": 5,
                  "outputMappings": [{ "variable": "total", "path": "total", "dataType": "number", "isArray": false }]
                } }
                """);
            model["sequenceFlows"]![0]!["targetRef"] = 4;
            AddFlow(model, 401, 4, 2);
        },
        ["source from a process variable"] = model =>
        {
            Input(model)["sourceVariable"] = "decision";
            Input(model)["dataType"] = "string";
        },
        ["catch-all boundary"] = model => AddBoundary(model, 30),
        ["boundary with its own error variable"] = model =>
        {
            AddBoundary(model, 30, """, "errorVariable": "failure" """);
            model["variables"]!.AsArray().Add(JsonNode.Parse(
                """{ "id": 3, "name": "failure", "dataType": "string", "isArray": false, "nullable": true }"""));
        },
        ["call-only default start"] = model => Node(model, 1)["callOnly"] = true,
        ["explicit false call-only"] = model => Node(model, 1)["callOnly"] = false
    };

    private static readonly Dictionary<string, Action<JsonObject>> InvalidCases = new()
    {
        ["missing configuration"] = model => Node(model, 2).Remove("callActivity"),
        ["blank key"] = model => Call(model)["workflowKey"] = " ",
        ["padded key"] = model => Call(model)["workflowKey"] = " child",
        ["templated key"] = model => Call(model)["workflowKey"] = "${family}",
        ["long key"] = model => Call(model)["workflowKey"] = new string('k', 301),
        ["self call"] = model => Call(model)["workflowKey"] = "parent",
        ["unsupported binding"] = model => Call(model)["versionBinding"] = "latestPublished",
        ["null inputs"] = model => Call(model)["inputMappings"] = null,
        ["null outputs"] = model => Call(model)["outputMappings"] = null,
        ["blank source"] = model => Input(model)["sourceVariable"] = "",
        ["padded target"] = model => Input(model)["variable"] = "amount ",
        ["reserved source"] = model => Input(model)["sourceVariable"] = "sys.user",
        ["reserved target"] = model => Input(model)["variable"] = "gateway.count",
        ["reserved output source"] = model => Output(model)["sourceVariable"] = "SETTING.rate",
        ["unknown data type"] = model => Input(model)["dataType"] = "xml",
        ["duplicate input target"] = model => Call(model)["inputMappings"]!.AsArray().Add(
            JsonNode.Parse("""{ "sourceVariable": "amount", "variable": "AMOUNT", "dataType": "number" }""")),
        ["duplicate output target"] = model => Call(model)["outputMappings"]!.AsArray().Add(
            JsonNode.Parse("""{ "sourceVariable": "other", "variable": "DECISION", "dataType": "string" }""")),
        ["undeclared source"] = model => Input(model)["sourceVariable"] = "missing",
        ["source declared with another type"] = model => Input(model)["dataType"] = "string",
        ["source declared as a scalar"] = model => Input(model)["isArray"] = true,
        ["shared source"] = model =>
        {
            model["variables"]!.AsArray().Add(JsonNode.Parse(
                """{ "id": 3, "name": "rate", "scope": "shared", "sharedKey": "finance.rate", "access": "read", "dataType": "number", "isArray": false, "nullable": true }"""));
            Input(model)["sourceVariable"] = "rate";
        },
        ["undeclared output target"] = model => Output(model)["variable"] = "unknown",
        ["output target that is only an input"] = model => Output(model)["variable"] = "amount",
        ["shared output target"] = model =>
        {
            model["variables"]!.AsArray().Add(JsonNode.Parse(
                """{ "id": 3, "name": "rate", "scope": "shared", "sharedKey": "finance.rate", "access": "readWrite", "dataType": "string", "isArray": false, "nullable": true }"""));
            Output(model)["variable"] = "rate";
        },
        ["output target with another type"] = model => Output(model)["dataType"] = "number",
        ["output target with another shape"] = model => Output(model)["isArray"] = true,
        ["output target is the error variable"] = model => AddBoundary(model, 30, """, "errorVariable": "decision" """),
        ["async before"] = model => Node(model, 2)["asyncBefore"] = true,
        ["async after"] = model => Node(model, 2)["asyncAfter"] = true,
        ["job settings"] = model => Node(model, 2)["job"] = JsonNode.Parse(
            """{ "failureHandling": "retryFirst", "retryDelays": ["PT1M"] }"""),
        ["multi-instance"] = model => Node(model, 2)["multiInstance"] = JsonNode.Parse(
            """{ "mode": "parallel", "source": "cardinality", "cardinality": "2", "resultVariable": "items" }"""),
        ["roles"] = model => Node(model, 2)["roles"] = JsonNode.Parse("""["Manager"]"""),
        ["assignee"] = model => Node(model, 2)["assignee"] = "'alice'",
        ["due date"] = model => Node(model, 2)["dueDate"] = "P1D",
        ["node variables"] = model => Node(model, 2)["variables"] = JsonNode.Parse(
            """[{ "id": 9, "name": "extra", "dataType": "string", "isArray": false }]"""),
        ["no incoming flow"] = model => model["sequenceFlows"]![0]!["targetRef"] = 3,
        ["two outgoing flows"] = model => AddFlow(model, 202, 2, 3),
        ["no outgoing flow"] = model => model["sequenceFlows"]!.AsArray().RemoveAt(1),
        ["conditional outgoing flow"] = model => model["sequenceFlows"]![1]!["condition"] = "amount > 1",
        ["named user-action roles on the outgoing flow"] = model => model["sequenceFlows"]![1]!["roles"] = JsonNode.Parse("""["Manager"]"""),
        ["configuration on another node"] = model => Node(model, 3)["callActivity"] = JsonNode.Parse("""{ "workflowKey": "child" }"""),
        ["call-only on a task"] = model => Node(model, 3)["callOnly"] = true,
        ["call-only with roles"] = model =>
        {
            Node(model, 1)["callOnly"] = true;
            Node(model, 1)["roles"] = JsonNode.Parse("""["Requester"]""");
        },
        ["call-only with idempotency"] = model =>
        {
            Node(model, 1)["callOnly"] = true;
            Node(model, 1)["idempotency"] = JsonNode.Parse("""{ "headerName": "Idempotency-Key", "variable": "requestId" }""");
        },
        ["call-only on a start that is not the default"] = model =>
        {
            AddNode(model, """{ "id": 4, "name": "Other", "type": "startEvent", "callOnly": true }""");
            AddFlow(model, 401, 4, 2);
        },
        ["changed default leaves the flag"] = model =>
        {
            Node(model, 1)["callOnly"] = true;
            AddNode(model, """{ "id": 4, "name": "Other", "type": "startEvent" }""");
            AddFlow(model, 401, 4, 2);
            model["initialEventId"] = 4;
        },
        ["two catch-all boundaries"] = model =>
        {
            AddBoundary(model, 30);
            AddBoundary(model, 31);
        },
        ["HTTP matcher on a call boundary"] = model =>
            AddBoundary(model, 30, """, "errorMatch": { "httpStatusCodes": [409] } """),
        ["timer boundary on a call"] = model =>
        {
            AddNode(model, """{ "id": 30, "name": "Late", "type": "timerBoundaryEvent", "attachedToRef": 2, "timer": { "timeDuration": "PT1H" } }""");
            AddFlow(model, 3001, 30, 3);
        },
        ["conditional boundary on a call"] = model =>
        {
            AddNode(model, """{ "id": 30, "name": "When", "type": "conditionalBoundaryEvent", "attachedToRef": 2, "conditional": { "condition": "decision == 'x'" } }""");
            AddFlow(model, 3001, 30, 3);
        }
    };

    // The server's first error is one of the editor's errors, except where the two
    // deliberately differ: the editor reports a structural rule in its own phase.
    private static readonly HashSet<string> OwnWording =
    [
        "duplicate output target",
        "two outgoing flows",
        "no outgoing flow",
        "two catch-all boundaries",
        "HTTP matcher on a call boundary",
        "timer boundary on a call",
        "conditional boundary on a call"
    ];

    public static TheoryData<string> ValidCaseNames => new(ValidCases.Keys);

    public static TheoryData<string> InvalidCaseNames => new(InvalidCases.Keys);

    [Theory]
    [MemberData(nameof(ValidCaseNames))]
    public void Validator_AcceptsWhatTheServerAccepts(string name)
    {
        var json = ModelJson(ValidCases[name]);

        Assert.Null(ServerError(json));
        Assert.Empty(EditorErrors(json));
    }

    [Theory]
    [MemberData(nameof(InvalidCaseNames))]
    public void Validator_RejectsWhatTheServerRejects(string name)
    {
        var json = ModelJson(InvalidCases[name]);

        var serverError = ServerError(json);
        Assert.NotNull(serverError);
        var errors = EditorErrors(json);
        Assert.NotEmpty(errors);
        if (!OwnWording.Contains(name)) Assert.Contains(serverError, errors);
    }

    [Theory]
    [InlineData("""{ "workflowKey": "child", "inputMappings": "amount" }""")]
    [InlineData("""{ "workflowKey": "child", "version": 3 }""")]
    [InlineData("""{ "workflowKey": 7 }""")]
    [InlineData("""{ "workflowKey": "child", "inputMappings": [{ "sourceVariable": "amount", "variable": "amount", "dataType": "number", "isArray": "no" }] }""")]
    [InlineData("""{ "workflowKey": "child", "inputMappings": [{ "sourceVariable": "amount", "variable": "amount", "dataType": "number", "required": 1 }] }""")]
    [InlineData("""{ "workflowKey": "child", "inputMappings": [{ "sourceVariable": "amount", "variable": "amount", "dataType": "number", "defaultValue": 1 }] }""")]
    [InlineData("""{ "workflowKey": "child", "inputMappings": ["amount"] }""")]
    [InlineData("""{ "workflowKey": "child", "inputMappings": [null] }""")]
    [InlineData("\"child\"")]
    [InlineData("""[]""")]
    public void Validator_RejectsWhatTheServerRefusesToBind(string callActivity)
    {
        var json = ModelJson(model => Node(model, 2)["callActivity"] = JsonNode.Parse(callActivity));

        Assert.NotNull(ServerError(json));
        Assert.Contains(EditorErrors(json), error => error.StartsWith("Call activity #2", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsANonBooleanCallOnlyLikeBinding()
    {
        var json = ModelJson(model => Node(model, 1)["callOnly"] = "yes");

        Assert.Throws<JsonException>(() => Deserialize(json));
        Assert.Contains("Start event #1 callOnly must be true or false.", EditorErrors(json));
    }

    [Fact]
    public void Validator_EnforcesTheMappingLimitOnBothSides()
    {
        var json = ModelJson(model =>
        {
            var inputs = Call(model)["inputMappings"]!.AsArray();
            for (var index = 0; index < 100; index++)
            {
                inputs.Add(JsonNode.Parse($$"""{ "sourceVariable": "amount", "variable": "extra{{index}}", "dataType": "number" }"""));
            }
        });

        Assert.Contains("at most 100", ServerError(json));
        Assert.Contains(EditorErrors(json), error => error.Contains("at most 100", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_OmitsDefaultsAndKeepsAuthoredMappings()
    {
        var engine = EditorEngine();
        engine.SetValue("modelJson", ModelJson(model =>
        {
            Call(model)["versionBinding"] = "publishedDefault";
            Input(model)["isArray"] = false;
            Output(model)["required"] = false;
            Node(model, 1)["callOnly"] = false;
        }));
        using var exported = JsonDocument.Parse(engine.Evaluate("""
            (() => {
              loadFromObject(JSON.parse(modelJson));
              const before = JSON.stringify(model);
              const prepared = prepareWorkflowExport();
              if (prepared.errors && prepared.errors.length) throw new Error(prepared.errors.join(' | '));
              if (JSON.stringify(model) !== before) throw new Error('Export changed the live document.');
              return prepared.json;
            })()
            """).AsString());

        var nodes = exported.RootElement.GetProperty("flowNodes").EnumerateArray().ToArray();
        Assert.False(nodes[0].TryGetProperty("callOnly", out _));
        var call = nodes[1].GetProperty("callActivity");
        Assert.False(call.TryGetProperty("versionBinding", out _));
        var input = call.GetProperty("inputMappings")[0];
        Assert.Equal("amount", input.GetProperty("sourceVariable").GetString());
        Assert.True(input.GetProperty("required").GetBoolean());
        Assert.False(input.TryGetProperty("isArray", out _));
        Assert.False(call.GetProperty("outputMappings")[0].TryGetProperty("required", out _));
        foreach (var other in new[] { "roles", "variables", "assignments", "service", "message", "asyncBefore", "asyncAfter", "job", "multiInstance" })
        {
            var present = nodes[1].TryGetProperty(other, out var value);
            Assert.True(
                !present || value.ValueKind == JsonValueKind.Null || (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0),
                $"{other} should stay empty on a call.");
        }

        // The server accepts what the editor exports, as it stands.
        Assert.Null(ServerError(exported.RootElement.GetRawText()));
    }

    [Fact]
    public void Export_LeavesDefinitionsWithoutCallsUntouched()
    {
        var engine = EditorEngine();
        engine.SetValue("workflowJson", ExampleWorkflowData.Read("examples/user-tasks/01-roles-claim-and-bypass.json"));

        var json = engine.Evaluate("""
            (() => {
              loadFromObject(JSON.parse(workflowJson));
              return prepareWorkflowExport().json;
            })()
            """).AsString();

        Assert.DoesNotContain("callActivity", json);
        Assert.DoesNotContain("callOnly", json);
    }

    [Theory]
    [InlineData("""{ "flowNodes.2.asyncBefore": true }""", "cannot set asyncBefore")]
    [InlineData("""{ "flowNodes.2.asyncAfter": true }""", "cannot set asyncBefore")]
    [InlineData("""{ "flowNodes.2.multiInstance": { "mode": "parallel", "source": "cardinality", "cardinality": "2", "resultVariable": "items" } }""", "cannot define multiInstance")]
    [InlineData("""{ "flowNodes.2.callActivity.versionBinding": "exact" }""", "unsupported callActivity.versionBinding")]
    [InlineData("""{ "flowNodes.2.callActivity.inputMappings": "amount" }""", "must be an array")]
    [InlineData("""{ "flowNodes.2.callActivity.outputMappings": null }""", "must be an array")]
    [InlineData("""{ "flowNodes.2.callActivity.inputMappings": ["amount"] }""", "must be an object")]
    [InlineData("""{ "flowNodes.1.callOnly": "yes" }""", "callOnly must be true or false")]
    [InlineData("""{ "flowNodes.3.callActivity": { "workflowKey": "child" } }""", "is not a call activity")]
    [InlineData("""{ "flowNodes.3.callOnly": true }""", "is not a start event")]
    public void Load_KeepsInvalidImportsVisibleSoSaveCanReportThem(string change, string expected)
    {
        var engine = EditorEngine();
        var (path, value) = JsonDocument.Parse(change).RootElement.EnumerateObject()
            .Select(property => (property.Name, property.Value.GetRawText())).Single();
        engine.SetValue("modelJson", ModelJson(model =>
        {
            var segments = path.Split('.');
            JsonObject owner = Node(model, int.Parse(segments[1]));
            foreach (var segment in segments[2..^1]) owner = owner[segment]!.AsObject();
            owner[segments[^1]] = JsonNode.Parse(value);
        }));

        var errors = JsonSerializer.Deserialize<List<string>>(engine.Evaluate("""
            (() => {
              loadFromObject(JSON.parse(modelJson));
              selected = { kind: 'node', nodeId: 2 };
              renderInspector();
              selected = { kind: 'node', nodeId: 1 };
              renderInspector();
              const prepared = prepareWorkflowExport();
              return JSON.stringify(prepared.errors || []);
            })()
            """).AsString())!;

        Assert.Contains(errors, error => error.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Load_RefusesAMalformedDocumentAndKeepsTheCurrentOne()
    {
        var engine = EditorEngine();
        engine.SetValue("modelJson", BaseModel);

        var unchanged = engine.Evaluate("""
            (() => {
              loadFromObject(JSON.parse(modelJson));
              const before = JSON.stringify(model);
              try { loadFromObject({ id: 'broken', flowNodes: 'not a list' }); } catch (error) { }
              return JSON.stringify(model) === before;
            })()
            """).AsBoolean();

        Assert.True(unchanged);
    }

    [Fact]
    public void TypeChange_AddsAndClearsTheConfiguration()
    {
        var engine = EditorEngine();
        engine.SetValue("modelJson", BaseModel);
        using var result = JsonDocument.Parse(engine.Evaluate("""
            (() => {
              loadFromObject(JSON.parse(modelJson));
              const end = model.flowNodes.find(node => node.id === 3);
              const call = model.flowNodes.find(node => node.id === 2);
              const start = model.flowNodes.find(node => node.id === 1);
              start.callOnly = true;

              // An outgoing flow stays attached only where the type allows it.
              const toTask = changeNodeTypeFromInspector(call, 'task');
              const clearedOnTask = call.callActivity === undefined;
              const toCall = changeNodeTypeFromInspector(call, 'callActivity');
              const fresh = call.callActivity;
              const asyncFree = call.asyncBefore === undefined && call.asyncAfter === undefined;
              fresh.workflowKey = 'child';

              const startToTask = changeNodeTypeFromInspector(start, 'task');
              const flagCleared = start.callOnly === undefined;

              return JSON.stringify({
                toTask, clearedOnTask, toCall, asyncFree,
                fresh: { key: fresh.workflowKey, inputs: fresh.inputMappings.length, outputs: fresh.outputMappings.length },
                startToTask, flagCleared, endType: end.type
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("toTask").GetBoolean());
        Assert.True(root.GetProperty("clearedOnTask").GetBoolean());
        Assert.True(root.GetProperty("toCall").GetBoolean());
        Assert.True(root.GetProperty("asyncFree").GetBoolean());
        Assert.Equal(0, root.GetProperty("fresh").GetProperty("inputs").GetInt32());
        Assert.True(root.GetProperty("startToTask").GetBoolean());
        Assert.True(root.GetProperty("flagCleared").GetBoolean());
    }

    [Fact]
    public void TypeChange_HandlesAttachedBoundaries()
    {
        var engine = EditorEngine("var lastAlert = null; globalThis.alert = message => { lastAlert = message; };");
        engine.SetValue("modelJson", ModelJson(model =>
        {
            Node(model, 2)["type"] = "serviceTask";
            Node(model, 2).Remove("callActivity");
            Node(model, 2)["service"] = JsonNode.Parse("""{ "type": "rest", "method": "GET", "url": "http://example.test/", "timeoutSeconds": 5 }""");
            AddBoundary(model, 30, """, "errorMatch": { "httpStatusCodes": [409] } """);
            AddBoundary(model, 31);
        }));
        using var result = JsonDocument.Parse(engine.Evaluate("""
            (() => {
              loadFromObject(JSON.parse(modelJson));
              const host = model.flowNodes.find(node => node.id === 2);
              const refused = changeNodeTypeFromInspector(host, 'callActivity');
              const stayedService = host.type === 'serviceTask';
              const alerted = String(lastAlert);

              model.flowNodes = model.flowNodes.filter(node => node.id !== 30);
              model.sequenceFlows = model.sequenceFlows.filter(flow => flow.sourceRef !== 30);
              const accepted = changeNodeTypeFromInspector(host, 'callActivity');
              const keptCatchAll = model.flowNodes.some(node => node.id === 31);

              const away = changeNodeTypeFromInspector(host, 'task');
              const removedWithHost = !model.flowNodes.some(node => node.id === 31)
                && !model.sequenceFlows.some(flow => flow.sourceRef === 31);
              return JSON.stringify({ refused, stayedService, alerted, accepted, keptCatchAll, away, removedWithHost });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.False(root.GetProperty("refused").GetBoolean());
        Assert.True(root.GetProperty("stayedService").GetBoolean());
        Assert.Contains("call activity", root.GetProperty("alerted").GetString());
        Assert.True(root.GetProperty("accepted").GetBoolean());
        Assert.True(root.GetProperty("keptCatchAll").GetBoolean());
        Assert.True(root.GetProperty("away").GetBoolean());
        Assert.True(root.GetProperty("removedWithHost").GetBoolean());
    }

    [Fact]
    public void Inspector_EditsMappingsAndExplainsTheCall()
    {
        var engine = EditorEngine();
        engine.SetValue("modelJson", BaseModel);
        using var result = JsonDocument.Parse(engine.Evaluate("""
            (() => {
              loadFromObject(JSON.parse(modelJson));
              resetHistory();
              selected = { kind: 'node', nodeId: 2 };
              renderInspector();
              const descendants = root => {
                const found = [root];
                for (const child of root.children || []) found.push(...descendants(child));
                return found;
              };
              const all = () => descendants(inspector);
              const named = text => all().filter(element => element.textContent === text);
              const labels = () => all().filter(element => /^<label>/.test(element.innerHTML || '')).map(element => element.innerHTML);
              const call = model.flowNodes.find(node => node.id === 2).callActivity;

              const headings = all().filter(element => element.tagName === 'H2').map(element => element.textContent);
              const fieldLabels = labels();

              named('+ Add mapping')[0].onclick();
              const added = call.inputMappings.length;
              const newest = call.inputMappings[call.inputMappings.length - 1];
              commitHistory();
              // The DOM stub keeps earlier renders; the last three rows are the current ones.
              named('Delete mapping').slice(-3)[0].onclick();
              commitHistory();
              const afterDelete = call.inputMappings.length;
              undo();
              const undone = model.flowNodes.find(node => node.id === 2).callActivity.inputMappings.length;
              redo();
              const redone = model.flowNodes.find(node => node.id === 2).callActivity.inputMappings.length;

              return JSON.stringify({ headings, fieldLabels, added, newest, afterDelete, undone, redone });
            })()
            """).AsString());

        var root = result.RootElement;
        var headings = root.GetProperty("headings").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("Call activity", headings);
        Assert.Contains("Inputs", headings);
        Assert.Contains("Results", headings);
        Assert.Contains("Error boundary", headings);
        var labels = root.GetProperty("fieldLabels").GetRawText();
        foreach (var label in new[] { "Workflow key", "Version", "Variable in this workflow", "Input of the called workflow", "Variable in the called workflow", "Process variable in this workflow", "Data type" })
        {
            Assert.Contains(label, labels);
        }

        Assert.Equal(2, root.GetProperty("added").GetInt32());
        Assert.Equal("string", root.GetProperty("newest").GetProperty("dataType").GetString());
        Assert.Equal(1, root.GetProperty("afterDelete").GetInt32());
        Assert.Equal(2, root.GetProperty("undone").GetInt32());
        Assert.Equal(1, root.GetProperty("redone").GetInt32());
    }

    [Theory]
    [InlineData("""{ "workflowKey": "child" }""")]
    [InlineData("""{ "workflowKey": "child", "inputMappings": null, "outputMappings": "bad" }""")]
    public void Inspector_DrawingACallChangesNothingInTheDocument(string callActivity)
    {
        var engine = EditorEngine();
        engine.SetValue("modelJson", ModelJson(model => Node(model, 2)["callActivity"] = JsonNode.Parse(callActivity)));

        var unchanged = engine.Evaluate("""
            (() => {
              loadFromObject(JSON.parse(modelJson));
              const before = JSON.stringify(model);
              selected = { kind: 'node', nodeId: 2 };
              renderInspector();
              return JSON.stringify(model) === before;
            })()
            """).AsBoolean();

        Assert.True(unchanged);
    }

    [Fact]
    public void Inspector_ChangingTheDefaultStartKeepsTheFlagAndAsksForAFix()
    {
        var engine = EditorEngine();
        engine.SetValue("modelJson", ModelJson(model =>
        {
            Node(model, 1)["callOnly"] = true;
            AddNode(model, """{ "id": 4, "name": "Other", "type": "startEvent" }""");
            AddFlow(model, 401, 4, 2);
        }));
        using var result = JsonDocument.Parse(engine.Evaluate("""
            (() => {
              loadFromObject(JSON.parse(modelJson));
              const validBefore = prepareWorkflowExport().errors || [];
              model.initialEventId = 4;
              selected = { kind: 'node', nodeId: 1 };
              renderInspector();
              const descendants = root => {
                const found = [root];
                for (const child of root.children || []) found.push(...descendants(child));
                return found;
              };
              const notes = descendants(inspector).filter(element => element.className === 'empty-note').map(element => element.textContent);
              const start = model.flowNodes.find(node => node.id === 1);
              const flag = start.callOnly;
              const errors = prepareWorkflowExport().errors || [];
              return JSON.stringify({ validBefore: validBefore.length, notes, flag, errors });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(0, root.GetProperty("validBefore").GetInt32());
        Assert.True(root.GetProperty("flag").GetBoolean());
        Assert.Contains("not the default start", root.GetProperty("notes").GetRawText());
        Assert.Contains("is not the default start event", root.GetProperty("errors").GetRawText());
    }

    [Fact]
    public void Viewer_DrawsACallAsATaskThatCanBeMarkedAndSelected()
    {
        var engine = EditorEngine(mode: "viewer");
        engine.SetValue("modelJson", BaseModel);
        using var result = JsonDocument.Parse(engine.Evaluate("""
            (() => {
              loadFromObject(JSON.parse(modelJson));
              setInstanceOverlay({ nodes: [{ id: 2, state: 'active', label: 'Waiting for its child instance' }] });
              const group = nodesG.children.slice(-model.flowNodes.length)
                .find(candidate => candidate.attributes['data-id'] === '2');
              const tags = group.children.map(child => child.tagName.toLowerCase());
              const size = nodeSize(model.flowNodes.find(node => node.id === 2));
              select('node', 2);
              return JSON.stringify({
                className: group.attributes.class, tags, size,
                label: group.getAttribute('aria-label'),
                selected: selected?.nodeId, inspector: inspector.innerHTML
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Contains("callActivity", root.GetProperty("className").GetString());
        Assert.Contains("rect", root.GetProperty("tags").GetRawText());
        Assert.Equal(190, root.GetProperty("size").GetProperty("w").GetInt32());
        Assert.Equal(88, root.GetProperty("size").GetProperty("h").GetInt32());
        Assert.Contains("Waiting for its child instance", root.GetProperty("label").GetString());
        Assert.Equal(2, root.GetProperty("selected").GetInt32());
        Assert.Equal(string.Empty, root.GetProperty("inspector").GetString());
    }

    [Fact]
    public void Editor_HasACallShapeInEveryAsset()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "editor");
        var css = File.ReadAllText(Path.Combine(directory, "flowbit-editor.css"));
        var template = File.ReadAllText(Path.Combine(directory, "editor-template.js"));

        Assert.Contains(".node.callActivity rect", css);
        Assert.Matches(new Regex(@"\.node\.callActivity rect \{[^}]*stroke-width: 4"), css);
        Assert.Contains("id=\"icon-call\"", template);
    }

    private static Engine EditorEngine(string? beforeMainScript = null, string mode = "standalone") =>
        EditorRuntimeSmokeTests.CreateEditorEngine(beforeMainScript, mode);

    private static string ModelJson(Action<JsonObject> change)
    {
        var model = JsonNode.Parse(BaseModel)!.AsObject();
        change(model);
        return model.ToJsonString();
    }

    private static WorkflowModel Deserialize(string json) =>
        JsonSerializer.Deserialize<WorkflowModel>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static string? ServerError(string json)
    {
        var validator = new WorkflowDefinitionValidator(
            new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance),
            new ServiceTaskOptions());
        try
        {
            var model = Deserialize(json);
            validator.ValidateAuthored(model);
            WorkflowModelMigrator.Normalize(model);
            validator.ValidateNormalized(model);
            return null;
        }
        catch (Exception error) when (error is WorkflowDomainException or JsonException)
        {
            return error.Message;
        }
    }

    private static IReadOnlyList<string> EditorErrors(string json)
    {
        var source = EditorSource.Read();
        var validator = Regex.Match(
            source,
            @"// BEGIN WORKFLOW SAVE VALIDATOR(?<code>[\s\S]*?)// END WORKFLOW SAVE VALIDATOR");
        var normalizeRoles = Regex.Match(
            source,
            @"function normalizeRoles\(roles\) \{[\s\S]*?(?=function normalizeAttributesForLoad)");
        var engine = new Engine();
        engine.Execute(normalizeRoles.Value);
        engine.Execute(validator.Groups["code"].Value);
        engine.SetValue("candidateJson", json);
        var result = engine.Evaluate("JSON.stringify(validateModelForSave(JSON.parse(candidateJson)))").AsString();
        return JsonSerializer.Deserialize<List<string>>(result) ?? [];
    }
}
