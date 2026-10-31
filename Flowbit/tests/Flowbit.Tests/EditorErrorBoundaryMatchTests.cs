using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Jint;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class EditorErrorBoundaryMatchTests
{
    private const string Fixture = """
        const matcherFixture = (extraNodes = [], extraFlows = []) => ({
          id: 'matchers', name: 'Matchers', initialEventId: 1, variables: [], lanes: [],
          flowNodes: [
            { id: 1, name: 'Start', type: 'startEvent', x: 0, y: 0 },
            { id: 2, name: 'Call', type: 'serviceTask', x: 200, y: 0, service: { type: 'rest', method: 'GET', url: 'https://tests.local/call', timeoutSeconds: 5 } },
            { id: 3, name: 'End', type: 'endEvent', x: 500, y: 0 },
            ...extraNodes
          ],
          sequenceFlows: [
            { id: 101, sourceRef: 1, targetRef: 2 },
            { id: 201, sourceRef: 2, targetRef: 3 },
            ...extraFlows
          ]
        });
        const matcherBoundary = (id, errorMatch) => ({
          id, name: 'Boundary ' + id, type: 'errorBoundaryEvent', x: 0, y: 0, attachedToRef: 2,
          ...(errorMatch === undefined ? {} : { errorMatch })
        });
        const matcherFlow = id => ({ id: id * 100 + 1, name: '', sourceRef: id, targetRef: 3 });
        """;

    // Both sides must accept or reject the same matcher. JavaScript reading 409.0 as
    // 409 and web binding ignoring property casing are the two accepted differences,
    // so neither is listed here. Anything binding refuses appears as a binding case.
    public static TheoryData<string, bool> MatcherParityCases => new()
    {
        { """null""", true },
        { """{ "httpStatusCodes": [409] }""", true },
        { """{ "httpStatusCodes": [100, 599] }""", true },
        { """{ "httpStatusCodes": [409], "httpStatusRanges": null }""", true },
        { """{ "httpStatusCodes": [], "httpStatusRanges": [{ "from": 400, "to": 499 }] }""", true },
        { """{ "httpStatusCodes": [399], "httpStatusRanges": [{ "from": 400, "to": 449 }, { "from": 450, "to": 499 }] }""", true },
        { """{ }""", false },
        { """{ "httpStatusCodes": [], "httpStatusRanges": [] }""", false },
        { """{ "httpStatusCodes": null }""", false },
        { """{ "httpStatusCodes": [99] }""", false },
        { """{ "httpStatusCodes": [0] }""", false },
        { """{ "httpStatusCodes": [600] }""", false },
        { """{ "httpStatusCodes": [409, 409] }""", false },
        { """{ "httpStatusRanges": [null] }""", false },
        { """{ "httpStatusRanges": [{ "from": 400 }] }""", false },
        { """{ "httpStatusRanges": [{ "to": 499 }] }""", false },
        { """{ "httpStatusRanges": [{ "from": 499, "to": 400 }] }""", false },
        { """{ "httpStatusRanges": [{ "from": 400, "to": 400 }] }""", false },
        { """{ "httpStatusRanges": [{ "from": 400, "to": 450 }, { "from": 450, "to": 499 }] }""", false },
        { """{ "httpStatusCodes": [409], "httpStatusRanges": [{ "from": 400, "to": 499 }] }""", false }
    };

    [Theory]
    [MemberData(nameof(MatcherParityCases))]
    public void Validator_AgreesWithTheServerOnEveryMatcher(string matcher, bool valid)
    {
        var json = ModelJson($$"""[{ "id": 30, "name": "Boundary", "type": "errorBoundaryEvent", "attachedToRef": 2, "errorMatch": {{matcher}} }]""", [30]);

        Assert.Equal(valid, ServerAccepts(json));
        var errors = EditorErrors(json);
        Assert.Equal(valid, !errors.Any(error => error.Contains("errorMatch", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("""{ "httpStatusCodes": ["409"] }""")]
    [InlineData("""{ "httpStatusCodes": [409.5] }""")]
    [InlineData("""{ "httpStatusCodes": [true] }""")]
    [InlineData("""{ "httpStatusCodes": [null] }""")]
    [InlineData("""{ "httpStatusCodes": 409 }""")]
    [InlineData("""{ "httpStatusRanges": [{ "from": "400", "to": 499 }] }""")]
    [InlineData("""{ "httpStatusRangez": [{ "from": 400, "to": 499 }] }""")]
    [InlineData("""{ "httpStatusRanges": [{ "from": 400, "to": 499, "name": "x" }] }""")]
    [InlineData("""[409]""")]
    [InlineData("""409""")]
    public void Validator_RejectsWhatTheServerRefusesToBind(string matcher)
    {
        var json = ModelJson($$"""[{ "id": 30, "name": "Boundary", "type": "errorBoundaryEvent", "attachedToRef": 2, "errorMatch": {{matcher}} }]""", [30]);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkflowModel>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Contains(EditorErrors(json), error => error.Contains("errorMatch", StringComparison.Ordinal));
    }

    public static TheoryData<string, int[], bool> HostParityCases => new()
    {
        { """[{ "id": 30, "name": "a", "type": "errorBoundaryEvent", "attachedToRef": 2 }]""", [30], true },
        { """[{ "id": 30, "name": "a", "type": "errorBoundaryEvent", "attachedToRef": 2, "errorMatch": { "httpStatusCodes": [409] } }, { "id": 31, "name": "b", "type": "errorBoundaryEvent", "attachedToRef": 2, "errorMatch": { "httpStatusRanges": [{ "from": 400, "to": 499 }] } }, { "id": 32, "name": "c", "type": "errorBoundaryEvent", "attachedToRef": 2 }]""", [30, 31, 32], true },
        { """[{ "id": 30, "name": "a", "type": "errorBoundaryEvent", "attachedToRef": 2 }, { "id": 31, "name": "b", "type": "errorBoundaryEvent", "attachedToRef": 2 }]""", [30, 31], false },
        { """[{ "id": 30, "name": "a", "type": "errorBoundaryEvent", "attachedToRef": 2, "errorMatch": { "httpStatusCodes": [409] } }, { "id": 31, "name": "b", "type": "errorBoundaryEvent", "attachedToRef": 2, "errorMatch": { "httpStatusCodes": [409] } }]""", [30, 31], false },
        { """[{ "id": 30, "name": "a", "type": "errorBoundaryEvent", "attachedToRef": 2, "errorMatch": { "httpStatusRanges": [{ "from": 400, "to": 450 }] } }, { "id": 31, "name": "b", "type": "errorBoundaryEvent", "attachedToRef": 2, "errorMatch": { "httpStatusRanges": [{ "from": 450, "to": 499 }] } }]""", [30, 31], false },
        { """[{ "id": 30, "name": "a", "type": "errorBoundaryEvent", "attachedToRef": 2, "errorMatch": { "httpStatusRanges": [{ "from": 400, "to": 449 }] } }, { "id": 31, "name": "b", "type": "errorBoundaryEvent", "attachedToRef": 2, "errorMatch": { "httpStatusRanges": [{ "from": 450, "to": 499 }] } }]""", [30, 31], true }
    };

    [Theory]
    [MemberData(nameof(HostParityCases))]
    public void Validator_AgreesWithTheServerOnHostRules(string boundaries, int[] ids, bool valid)
    {
        var json = ModelJson(boundaries, ids);

        Assert.Equal(valid, ServerAccepts(json));
        Assert.Equal(valid, EditorErrors(json).Count == 0);
    }

    [Fact]
    public void Validator_LimitsARestHostToEightBoundaries()
    {
        static string Boundary(int id) =>
            $$"""{ "id": {{id}}, "name": "b{{id}}", "type": "errorBoundaryEvent", "attachedToRef": 2, "errorMatch": { "httpStatusCodes": [{{500 + id}}] } }""";

        var eight = Enumerable.Range(30, 8).ToArray();
        var nine = Enumerable.Range(30, 9).ToArray();

        Assert.Empty(EditorErrors(ModelJson("[" + string.Join(",", eight.Select(Boundary)) + "]", eight)));
        Assert.Contains(
            EditorErrors(ModelJson("[" + string.Join(",", nine.Select(Boundary)) + "]", nine)),
            error => error.Contains("at most 8", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsAMatcherOnANodeThatCannotUseIt()
    {
        var onTask = """
            { "id": 1, "name": "Start", "type": "startEvent" },
            { "id": 2, "name": "Script", "type": "scriptTask", "scriptFormat": "ncalc", "assignments": [] },
            { "id": 3, "name": "End", "type": "endEvent", "errorMatch": { "httpStatusCodes": [409] } },
            { "id": 30, "name": "On script", "type": "errorBoundaryEvent", "attachedToRef": 2, "errorMatch": { "httpStatusCodes": [409] } }
            """;
        var errors = EditorErrors($$"""
            {
              "id": "m", "name": "m", "initialEventId": 1, "variables": [], "lanes": [],
              "flowNodes": [{{onTask}}],
              "sequenceFlows": [
                { "id": 101, "sourceRef": 1, "targetRef": 2 },
                { "id": 201, "sourceRef": 2, "targetRef": 3 },
                { "id": 3001, "sourceRef": 30, "targetRef": 3 }
              ]
            }
            """);

        Assert.Contains("Flow node #3 defines errorMatch but is not an error boundary event.", errors);
        Assert.Contains(errors, error => error.Contains("#30", StringComparison.Ordinal) && error.Contains("not a REST service task", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadAndExport_PreserveValidAndInvalidMatchersWithoutTouchingTheDocument()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        engine.Execute(Fixture);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject(matcherFixture(
                [
                  matcherBoundary(30, { httpStatusCodes: [503, 409], httpStatusRanges: [] }),
                  matcherBoundary(31, null)
                ],
                [matcherFlow(30), matcherFlow(31)]));
              const liveValid = JSON.parse(JSON.stringify(model));
              const exported = JSON.parse(prepareWorkflowExport().json);
              const unchanged = JSON.stringify(liveValid) === JSON.stringify(model);

              loadFromObject(matcherFixture(
                [
                  { id: 40, name: 'Other', type: 'task', x: 0, y: 0, errorMatch: { httpStatusCodes: ['bad'] } },
                  matcherBoundary(32, {})
                ],
                [matcherFlow(32), { id: 4001, sourceRef: 40, targetRef: 3 }]));
              const invalid = prepareWorkflowExport();
              return JSON.stringify({
                liveValid: liveValid.flowNodes.find(n => n.id === 30).errorMatch,
                exportedValid: exported.flowNodes.find(n => n.id === 30).errorMatch,
                exportedCatchAllHasKey: 'errorMatch' in exported.flowNodes.find(n => n.id === 31),
                wrongNode: getNode(40).errorMatch,
                emptyKept: getNode(32).errorMatch,
                unchanged,
                invalidJson: invalid.json,
                invalidErrors: invalid.errors
              });
            })()
            """).AsString());
        var root = result.RootElement;

        Assert.Equal("[503,409]", root.GetProperty("liveValid").GetProperty("httpStatusCodes").GetRawText());
        Assert.Equal("[409,503]", root.GetProperty("exportedValid").GetProperty("httpStatusCodes").GetRawText());
        Assert.False(root.GetProperty("exportedValid").TryGetProperty("httpStatusRanges", out _));
        Assert.False(root.GetProperty("exportedCatchAllHasKey").GetBoolean());
        Assert.Equal("[\"bad\"]", root.GetProperty("wrongNode").GetProperty("httpStatusCodes").GetRawText());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("emptyKept").ValueKind);
        Assert.True(root.GetProperty("unchanged").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("invalidJson").ValueKind);
        var errors = root.GetProperty("invalidErrors").EnumerateArray().Select(error => error.GetString()!).ToArray();
        Assert.Contains(errors, error => error.Contains("Flow node #40 defines errorMatch", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("#32", StringComparison.Ordinal) && error.Contains("at least one", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectingNodes_LeavesInvalidMatcherDataUntouched()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        engine.Execute(Fixture);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject(matcherFixture(
                [
                  matcherBoundary(30, { httpStatusCodes: ['40x', 500, 500], other: 1 }),
                  matcherBoundary(31, 'malformed'),
                  { id: 40, name: 'Other', type: 'task', x: 0, y: 0, errorMatch: { httpStatusCodes: [] } }
                ],
                [matcherFlow(30), matcherFlow(31)]));
              const before = JSON.stringify(model.flowNodes.map(n => n.errorMatch));
              for (const id of [30, 31, 40, 2]) {
                selected = { kind: 'node', nodeId: id };
                selectedNodeIds = new Set([id]);
                renderInspector();
              }
              render();
              return JSON.stringify({ same: before === JSON.stringify(model.flowNodes.map(n => n.errorMatch)) });
            })()
            """).AsString());

        Assert.True(result.RootElement.GetProperty("same").GetBoolean());
    }

    [Fact]
    public void FlowLabels_ShowMatchersAndLeaveConnectorMetricsAlone()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        engine.Execute(Fixture);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const info = (id, name) => {
                const source = getNode(id);
                const flow = { id: id * 100 + 1, name: name || '', sourceRef: id, targetRef: 3 };
                return { ...flowLabelInfo(flow, source), routing: flowRoutingLabelMetrics(flow, source) };
              };
              const lone = () => {
                loadFromObject(matcherFixture([matcherBoundary(30)], [matcherFlow(30)]));
                return info(30);
              };
              const single = lone();
              const metrics = single.routing;
              loadFromObject(matcherFixture(
                [
                  matcherBoundary(30, { httpStatusCodes: [409] }),
                  matcherBoundary(31, { httpStatusRanges: [{ from: 400, to: 499 }] }),
                  matcherBoundary(32),
                  matcherBoundary(33, { httpStatusCodes: [500, 501, 502, 503] }),
                  matcherBoundary(34, { httpStatusCodes: ['bad'] }),
                  matcherBoundary(35, {})
                ],
                [30, 31, 32, 33, 34, 35].map(matcherFlow)));
              const summary = id => info(id).markers.map(marker => marker.text);
              return JSON.stringify({
                singleMarkers: single.markers.length,
                singleTitle: single.title,
                exact: summary(30),
                range: summary(31),
                catchAll: summary(32),
                many: summary(33),
                invalid: summary(34),
                empty: summary(35),
                manyTitle: info(33).title,
                width: info(31).markerWidth,
                sameRouting: JSON.stringify(info(30).routing) === JSON.stringify(metrics),
                withName: info(30, 'Duplicate').name
              });
            })()
            """).AsString());
        var root = result.RootElement;

        Assert.Equal(0, root.GetProperty("singleMarkers").GetInt32());
        Assert.DoesNotContain("Catches", root.GetProperty("singleTitle").GetString());
        Assert.Equal("""["409"]""", root.GetProperty("exact").GetRawText());
        Assert.Equal("""["400–499"]""", root.GetProperty("range").GetRawText().Replace("\\u2013", "–", StringComparison.Ordinal));
        Assert.Equal("""["All failures"]""", root.GetProperty("catchAll").GetRawText());
        Assert.Equal("""["500, 501 +2"]""", root.GetProperty("many").GetRawText());
        Assert.Equal("""["Invalid match"]""", root.GetProperty("invalid").GetRawText());
        Assert.Equal("""["Invalid match"]""", root.GetProperty("empty").GetRawText());
        Assert.Contains("HTTP 500, 501, 502, 503", root.GetProperty("manyTitle").GetString());
        Assert.True(root.GetProperty("width").GetDouble() > 12);
        Assert.True(root.GetProperty("sameRouting").GetBoolean());
        Assert.Equal("Duplicate", root.GetProperty("withName").GetString());
    }

    [Fact]
    public void TypeChanges_KeepTheMatcherContract()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        engine.Execute(Fixture);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const alerts = [];
              alert = message => alerts.push(String(message));
              loadFromObject(matcherFixture(
                [matcherBoundary(30, { httpStatusCodes: [409] }), matcherBoundary(31)],
                [matcherFlow(30), matcherFlow(31)]));
              const rejected = changeNodeTypeFromInspector(getNode(2), 'scriptTask');
              const typeAfterReject = getNode(2).type;

              loadFromObject(matcherFixture([matcherBoundary(31)], [matcherFlow(31)]));
              const accepted = changeNodeTypeFromInspector(getNode(2), 'scriptTask');
              const catchAllKept = !!getNode(31);

              loadFromObject(matcherFixture(
                [matcherBoundary(30, { httpStatusCodes: [409] }), matcherBoundary(31)],
                [matcherFlow(30), matcherFlow(31)]));
              changeNodeTypeFromInspector(getNode(2), 'userTask');
              const removedWithHost = !getNode(30) && !getNode(31);

              loadFromObject(matcherFixture([matcherBoundary(30, { httpStatusCodes: [409] })], [matcherFlow(30)]));
              const boundary = getNode(30);
              changeNodeTypeFromInspector(boundary, 'task');
              const converted = boundary.errorMatch === undefined;

              return JSON.stringify({ rejected, typeAfterReject, alert: alerts[0], accepted, catchAllKept, removedWithHost, converted });
            })()
            """).AsString());
        var root = result.RootElement;

        Assert.False(root.GetProperty("rejected").GetBoolean());
        Assert.Equal("serviceTask", root.GetProperty("typeAfterReject").GetString());
        Assert.Contains("#30", root.GetProperty("alert").GetString());
        Assert.True(root.GetProperty("accepted").GetBoolean());
        Assert.True(root.GetProperty("catchAllKept").GetBoolean());
        Assert.True(root.GetProperty("removedWithHost").GetBoolean());
        Assert.True(root.GetProperty("converted").GetBoolean());
    }

    [Fact]
    public void Inspector_AddsAnIncompleteMatcherAndRejectsASecondCatchAll()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        engine.Execute(Fixture);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const alerts = [];
              alert = message => alerts.push(String(message));
              loadFromObject(matcherFixture());
              const host = getNode(2);
              addErrorBoundary(host);
              const first = model.flowNodes.find(n => n.type === 'errorBoundaryEvent');
              setConnectMode(false);
              addErrorBoundary(host);
              const second = model.flowNodes.filter(n => n.type === 'errorBoundaryEvent')[1];
              setConnectMode(false);

              let modeChange = null;
              const originalSelectField = selectField;
              selectField = (label, options, value, onChange) => {
                if (label === 'Failures caught') modeChange = onChange;
                return originalSelectField(label, options, value, onChange);
              };
              selected = { kind: 'node', nodeId: second.id };
              selectedNodeIds = new Set([second.id]);
              renderInspector();
              selectField = originalSelectField;
              const incompleteErrors = errorMatchProblems(second.errorMatch);
              modeChange('all');
              const keptMatcher = second.errorMatch != null;

              model.flowNodes = model.flowNodes.filter(n => n.id !== first.id);
              modeChange('all');
              const cleared = second.errorMatch === undefined;

              return JSON.stringify({
                firstIsCatchAll: first.errorMatch === undefined,
                incomplete: incompleteErrors.length > 0,
                keptMatcher,
                alert: alerts[0],
                cleared
              });
            })()
            """).AsString());
        var root = result.RootElement;

        Assert.True(root.GetProperty("firstIsCatchAll").GetBoolean());
        Assert.True(root.GetProperty("incomplete").GetBoolean());
        Assert.True(root.GetProperty("keptMatcher").GetBoolean());
        Assert.Contains("already catches all failures", root.GetProperty("alert").GetString());
        Assert.True(root.GetProperty("cleared").GetBoolean());
    }

    private static string ModelJson(string boundaries, int[] ids)
    {
        var flows = string.Concat(ids.Select(id =>
            $$""", { "id": {{id * 100 + 1}}, "sourceRef": {{id}}, "targetRef": 3 }"""));
        return $$"""
            {
              "id": "m", "name": "m", "initialEventId": 1, "variables": [], "lanes": [],
              "flowNodes": [
                { "id": 1, "name": "Start", "type": "startEvent" },
                { "id": 2, "name": "Call", "type": "serviceTask", "service": { "type": "rest", "method": "GET", "url": "https://tests.local/call", "timeoutSeconds": 5 } },
                { "id": 3, "name": "End", "type": "endEvent" }
              ],
              "sequenceFlows": [
                { "id": 101, "sourceRef": 1, "targetRef": 2 },
                { "id": 201, "sourceRef": 2, "targetRef": 3 }{{flows}}
              ]
            }
            """.Replace(
            """{ "id": 3, "name": "End", "type": "endEvent" }""",
            """{ "id": 3, "name": "End", "type": "endEvent" }, """ + boundaries.Trim('[', ']'),
            StringComparison.Ordinal);
    }

    private static bool ServerAccepts(string json)
    {
        var model = JsonSerializer.Deserialize<WorkflowModel>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var validator = new WorkflowDefinitionValidator(
            new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance),
            new ServiceTaskOptions());
        try
        {
            validator.ValidateAuthored(model);
            WorkflowModelMigrator.Normalize(model);
            validator.ValidateNormalized(model);
            return true;
        }
        catch (WorkflowDomainException)
        {
            return false;
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
