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

/// <summary>
/// The editor's signal events: its save validator reports the shared <see cref="SignalEventFixtures"/> with the
/// server's first message, and the catch's and the boundary's types, shapes, inspectors, loader, and boundary hosts
/// follow the plan.
/// </summary>
public sealed class EditorSignalEventTests
{
    private const string Diagram = """
        const signalDiagram = () => ({
          id: 'signal-editor', name: 'Signal editor', initialEventId: 1,
          variables: [
            { id: 1, name: 'policyVersion', dataType: 'number', isArray: false, nullable: true, required: false, defaultValue: null, validation: null },
            { id: 2, name: 'tags', dataType: 'string', isArray: true, nullable: true, required: false, defaultValue: null, validation: null },
            { id: 3, name: 'policyOwner', dataType: 'string', isArray: false, nullable: false, required: false, defaultValue: null,
              validation: null, scope: 'shared', sharedKey: 'examples.signals.policy-owner', access: 'readWrite' }
          ],
          lanes: [{ id: 10, name: 'Team', x: 0, y: 0, w: 1400, h: 400 }],
          flowNodes: [
            { id: 1, name: 'Start', type: 'startEvent', laneId: 10, x: 40, y: 100 },
            { id: 2, name: 'Review', type: 'userTask', laneId: 10, x: 160, y: 90, roles: ['Clerk'] },
            { id: 9, name: 'Wait for the new policy', type: 'intermediateSignalCatchEvent', laneId: 10, x: 420, y: 100,
              signal: { name: 'claims.policy-changed', outputMappings: [
                { variable: 'policyVersion', path: 'version', dataType: 'number', isArray: false, required: true }
              ] } },
            { id: 10, name: 'Done', type: 'endEvent', laneId: 10, x: 620, y: 100 },
            { id: 11, name: 'Recall', type: 'intermediateSignalCatchEvent', laneId: 10, x: 420, y: 280,
              signal: { name: 'claims.recall', outputMappings: [] } },
            { id: 12, name: 'Archive', type: 'endEvent', laneId: 10, x: 620, y: 280 }
          ],
          sequenceFlows: [
            { id: 101, name: '', sourceRef: 1, targetRef: 2 },
            { id: 201, name: 'Wait', sourceRef: 2, targetRef: 9 },
            { id: 202, name: 'Recall', sourceRef: 2, targetRef: 11 },
            { id: 901, name: '', sourceRef: 9, targetRef: 10 },
            { id: 1101, name: '', sourceRef: 11, targetRef: 12 }
          ]
        });
        // The signal diagram with a non-interrupting signal boundary #14 and an interrupting #16 on
        // user task #2, both continuing to end #15.
        const boundaryDiagram = () => {
          const diagram = signalDiagram();
          diagram.flowNodes.push(
            { id: 14, name: 'Recall announced', type: 'signalBoundaryEvent', attachedToRef: 2, cancelActivity: false,
              signal: { name: 'claims.recall-announced', outputMappings: [
                { variable: 'policyVersion', path: 'version', dataType: 'number', isArray: false, required: false }
              ] } },
            { id: 16, name: 'Withdrawn', type: 'signalBoundaryEvent', attachedToRef: 2, cancelActivity: true,
              signal: { name: 'claims.withdrawn', outputMappings: [] } },
            { id: 15, name: 'Recalled', type: 'endEvent', laneId: 10, x: 420, y: 200 });
          diagram.sequenceFlows.push(
            { id: 1401, name: '', sourceRef: 14, targetRef: 15 },
            { id: 1601, name: '', sourceRef: 16, targetRef: 15 });
          return diagram;
        };
        const descendants = element => [element, ...(element.children || []).flatMap(descendants)];
        const latestNodeGroups = () => nodesG.children.slice(-model.flowNodes.length);
        const nodeGroup = id => latestNodeGroups().find(group => group.attributes['data-id'] === String(id));
        // The inspector stub keeps earlier renders' children, so read only the latest render's,
        // which starts with its Node ID field.
        const latestInspector = () => {
          const children = inspector.children;
          let start = 0;
          children.forEach((child, index) => {
            if (child.innerHTML === '<label>Node ID</label>') start = index;
          });
          return children.slice(start);
        };
        const sectionFrom = (title, until) => {
          const children = latestInspector();
          const start = children.findIndex(child => child.tagName === 'H2' && child.textContent === title);
          if (start < 0) return [];
          const end = children.findIndex((child, index) => index > start && child.tagName === 'H2' && until.includes(child.textContent));
          return children.slice(start, end < 0 ? children.length : end);
        };
        const within = elements => elements.flatMap(descendants);
        """;

    public static TheoryData<string> CaseNames() => new(SignalEventFixtures.Cases().Select(item => item.Case));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void SaveValidation_ReportsEachFixtureWithTheServersFirstMessage(string name)
    {
        var (_, json, problem) = SignalEventFixtures.Cases().Single(item => item.Case == name);

        var errors = EditorErrors(json);

        if (problem is null)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.NotEmpty(errors);
            Assert.Equal(problem, errors[0]);
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Server_ReportsEachFixtureWithTheSameFirstMessage(string name)
    {
        var (_, json, problem) = SignalEventFixtures.Cases().Single(item => item.Case == name);

        Assert.Equal(problem, ServerError(json));
    }

    [Fact]
    public void NamePattern_IsTheServersPattern()
    {
        var match = Regex.Match(EditorSource.Read(), @"const SIGNAL_NAME_PATTERN = /(?<pattern>[^\r\n]+)/;");
        Assert.True(match.Success, "SIGNAL_NAME_PATTERN was not found in the editor.");
        Assert.Equal(SignalEventRules.NamePattern, match.Groups["pattern"].Value);
    }

    [Fact]
    public void SaveValidation_ReportsMalformedConfigurationsAndTypedMappingsInTheEditorsWords()
    {
        string[] Errors(Action<JsonObject> change)
        {
            var model = SignalEventFixtures.Base();
            change(model);
            return [.. EditorErrors(model.ToJsonString())];
        }
        JsonObject Signal(JsonObject model) => model["flowNodes"]![2]!["signal"]!.AsObject();

        Assert.Equal(
            ["Signal catch event #9 signal must be an object with a name and output mappings."],
            Errors(model => model["flowNodes"]![2]!["signal"] = "claims.policy-changed"));
        Assert.Equal(
            ["Signal catch event #9 signal.outputMappings must be a list."],
            Errors(model => Signal(model)["outputMappings"] = "version"));
        Assert.Equal(
            ["Signal catch event #9 output mappings cannot contain null entries."],
            Errors(model => Signal(model)["outputMappings"]!.AsArray().Add(null)));
        // Typed mapping problems keep the wording the editor uses for message mappings.
        Assert.Equal(
            ["Signal catch event #9 output mapping 'note' needs a payload path unless defaultValue is configured."],
            Errors(model => Signal(model)["outputMappings"]!.AsArray().Add(new JsonObject
            {
                ["variable"] = "note", ["path"] = "", ["dataType"] = "string", ["isArray"] = false
            })));
        Assert.Equal(
            ["Signal catch event #9 output mapping 'policyVersion' must match process variable 'policyVersion' type number."],
            Errors(model => Signal(model)["outputMappings"]![0]!["dataType"] = "string"));
        Assert.Equal(
            ["Signal catch event #9 output mapping variable 'sys.user' uses a reserved context prefix."],
            Errors(model => Signal(model)["outputMappings"]!.AsArray().Add(new JsonObject
            {
                ["variable"] = "sys.user", ["path"] = "user", ["dataType"] = "string", ["isArray"] = false
            })));
        // Both errors of one catch, in the server's order.
        Assert.Equal(
            ["Signal catch event #9 must define signal.name.",
             "Signal catch event #9 must have at least one incoming sequence flow."],
            Errors(model =>
            {
                Signal(model)["name"] = "";
                model["sequenceFlows"]![1]!["targetRef"] = 10;
            }));
    }

    [Fact]
    public void SaveValidation_ListsSignalMappingsWhereverMessageMappingsAreListed()
    {
        var engine = CreateValidatorEngine();
        var model = SignalEventFixtures.Base();
        var mapping = model["flowNodes"]![2]!["signal"]!["outputMappings"]![0]!.AsObject();
        mapping["validation"] = "FlowInfo(201, 'actions.count') > 0 or IncomingCount(201) > 0";
        engine.SetValue("candidateJson", model.ToJsonString());

        using var result = JsonDocument.Parse(engine.Evaluate("""
            (() => {
              const candidate = JSON.parse(candidateJson);
              const symbols = buildInboxVisibilitySymbols(candidate);
              const declarations = callSourceDeclarations(candidate);
              return JSON.stringify({
                errors: validateModelForSave(candidate),
                symbol: symbols.get('policyversion'),
                declaration: declarations.get('policyversion')
              });
            })()
            """).AsString());

        var root = result.RootElement;
        var errors = root.GetProperty("errors").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Contains(errors, error => error.StartsWith(
            "Flow node #9 signal output validation cannot use FlowInfo", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith(
            "Flow node #9 signal output validation has invalid Complex gateway expression usage", StringComparison.Ordinal));
        // The process variable declares it first; the signal mapping is a second producer of the same contract.
        Assert.Equal("policyVersion", root.GetProperty("symbol").GetProperty("name").GetString());
        Assert.False(root.GetProperty("symbol").GetProperty("conflict").GetBoolean());
        Assert.Equal(2, root.GetProperty("declaration").GetArrayLength());
        Assert.Equal("number", root.GetProperty("declaration")[1].GetProperty("dataType").GetString());
    }

    [Fact]
    public void SaveValidation_AcceptsTimerAndConditionalBoundariesOnASignalCatch()
    {
        var model = SignalEventFixtures.Base();
        var nodes = model["flowNodes"]!.AsArray();
        nodes.Add(new JsonObject
        {
            ["id"] = 12, ["name"] = "Too late", ["type"] = "timerBoundaryEvent", ["attachedToRef"] = 9,
            ["cancelActivity"] = true, ["timer"] = new JsonObject { ["timeDuration"] = "P2D" }
        });
        nodes.Add(new JsonObject
        {
            ["id"] = 13, ["name"] = "Withdrawn", ["type"] = "conditionalBoundaryEvent", ["attachedToRef"] = 9,
            ["cancelActivity"] = false, ["conditional"] = new JsonObject { ["condition"] = "policyVersion > 1" }
        });
        nodes.Add(new JsonObject { ["id"] = 14, ["name"] = "Expired", ["type"] = "endEvent" });
        var flows = model["sequenceFlows"]!.AsArray();
        flows.Add(new JsonObject { ["id"] = 1201, ["sourceRef"] = 12, ["targetRef"] = 14 });
        flows.Add(new JsonObject { ["id"] = 1301, ["sourceRef"] = 13, ["targetRef"] = 14 });
        var json = model.ToJsonString();

        Assert.Empty(EditorErrors(json));
        Assert.Null(ServerError(json));
    }

    [Fact]
    public void Registry_DrawsAnIntermediateEventWithTheSignalSymbolAndASolidFlow()
    {
        var html = EditorSource.Read();
        Assert.Contains("<symbol id=\"icon-signal-catch\"", html, StringComparison.Ordinal);
        Assert.Contains(".node.intermediateSignalCatchEvent circle.body", html, StringComparison.Ordinal);
        Assert.Contains(".node.intermediateSignalCatchEvent circle.ring", html, StringComparison.Ordinal);
        Assert.Contains(".node.selected.intermediateSignalCatchEvent circle.ring", html, StringComparison.Ordinal);
        Assert.Contains(".node.intermediateSignalCatchEvent .node-icon", html, StringComparison.Ordinal);

        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(signalDiagram());
              render();
              const group = nodeGroup(9);
              const parts = descendants(group);
              const type = 'intermediateSignalCatchEvent';
              return JSON.stringify({
                label: typeLabel(type),
                option: NODE_TYPE_OPTIONS.findIndex(option => option.value === type) -
                  NODE_TYPE_OPTIONS.findIndex(option => option.value === 'intermediateConditionalCatchEvent'),
                event: isEventType(type), single: isSingleOutgoingType(type), passThrough: isPassThroughType(type),
                signalEvent: isSignalWaitType(type), size: nodeSize(getNode(9)),
                classes: String(group.attributes.class),
                rings: parts.filter(part => part.attributes?.class === 'ring').length,
                icon: parts.find(part => String(part.attributes?.class) === 'node-icon')?.attributes.href,
                search: searchDiagram(model, 'signal catch').map(item => item.id),
                searchDetail: searchDiagram(model, 'signal catch')[0].detail
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("Signal Catch Event (wait for a broadcast)", root.GetProperty("label").GetString());
        Assert.Equal(1, root.GetProperty("option").GetInt32());
        Assert.True(root.GetProperty("event").GetBoolean());
        Assert.True(root.GetProperty("single").GetBoolean());
        Assert.False(root.GetProperty("passThrough").GetBoolean());
        Assert.True(root.GetProperty("signalEvent").GetBoolean());
        Assert.Equal(64, root.GetProperty("size").GetProperty("w").GetInt32());
        Assert.Contains("intermediateSignalCatchEvent", root.GetProperty("classes").GetString());
        Assert.Equal(1, root.GetProperty("rings").GetInt32());
        Assert.Equal("#icon-signal-catch", root.GetProperty("icon").GetString());
        Assert.Equal([9, 11], root.GetProperty("search").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Contains("Signal Catch Event", root.GetProperty("searchDetail").GetString());
    }

    [Fact]
    public void Viewer_DrawsAndMarksTheSignalCatchAsAnIntermediateEvent()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "viewer");
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(signalDiagram());
              setInstanceOverlay({ nodes: [{ id: 9, state: 'active', label: 'Waiting for a signal' }] });
              const parts = descendants(nodeGroup(9));
              return JSON.stringify({
                classes: String(nodeGroup(9).attributes.class),
                rings: parts.filter(part => part.attributes?.class === 'ring').length,
                icon: parts.find(part => String(part.attributes?.class) === 'node-icon')?.attributes.href
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Contains("instance-active", root.GetProperty("classes").GetString());
        Assert.Equal(1, root.GetProperty("rings").GetInt32());
        Assert.Equal("#icon-signal-catch", root.GetProperty("icon").GetString());
    }

    [Fact]
    public void TypeChanges_SeedTheSignalKeepWaitBoundariesAndClearItOnLeaving()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(signalDiagram());
              const review = getNode(2);
              addTimerBoundary(review);
              const timerId = model.flowNodes[model.flowNodes.length - 1].id;
              addConditionalBoundary(review);
              const conditionalId = model.flowNodes[model.flowNodes.length - 1].id;
              review.service = { type: 'rest' };
              review.message = { clientId: 'x' };
              review.script = 'x';
              const accepted = changeNodeTypeFromInspector(review, 'intermediateSignalCatchEvent');
              const asCatch = JSON.parse(JSON.stringify(review));
              const outgoing = outgoingFlows(2).map(flow => flow.id);
              const boundariesKept = [timerId, conditionalId].every(id => !!getNode(id));

              const recall = getNode(11);
              changeNodeTypeFromInspector(recall, 'task');
              const asTask = JSON.parse(JSON.stringify(recall));

              changeNodeTypeFromInspector(review, 'task');
              const boundariesAfterTask = [timerId, conditionalId].filter(id => !!getNode(id));
              return JSON.stringify({ accepted, asCatch, outgoing, boundariesKept, asTask, boundariesAfterTask });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("accepted").GetBoolean());
        var asCatch = root.GetProperty("asCatch");
        Assert.Equal("intermediateSignalCatchEvent", asCatch.GetProperty("type").GetString());
        Assert.Equal("", asCatch.GetProperty("signal").GetProperty("name").GetString());
        Assert.Empty(asCatch.GetProperty("signal").GetProperty("outputMappings").EnumerateArray());
        Assert.Empty(asCatch.GetProperty("roles").EnumerateArray());
        foreach (var property in new[] { "service", "message", "script", "asyncBefore", "asyncAfter", "multiInstance" })
        {
            Assert.False(asCatch.TryGetProperty(property, out _), $"The signal catch kept '{property}'.");
        }
        Assert.Single(root.GetProperty("outgoing").EnumerateArray());
        Assert.True(root.GetProperty("boundariesKept").GetBoolean());
        Assert.False(root.GetProperty("asTask").TryGetProperty("signal", out _));
        Assert.Empty(root.GetProperty("boundariesAfterTask").EnumerateArray());
    }

    [Fact]
    public void Invariants_NormalizeTheSignalAsAuthoredAndKeepItOnlyWhereItBelongs()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(signalDiagram());
              const node = getNode(9);
              Object.assign(node, {
                roles: ['Clerk'], variables: [{ id: 1, name: 'x' }], assignee: 'alice', requiresClaim: true,
                service: { url: 'x' }, message: { clientId: 'x' }, scriptFormat: 'javascript', script: 'x',
                attachedToRef: 2, errorVariable: 'failure', conditional: { condition: 'x' }, gatewayRef: 4
              });
              node.signal = { name: ' Claims.Policy ', extra: true, outputMappings: [
                { variable: 'POLICYVERSION', path: 'version' }, null
              ] };
              applyTypeInvariants(node);
              const userTask = getNode(2);
              userTask.signal = { name: 'claims.x', outputMappings: [] };
              applyTypeInvariants(userTask);
              const malformed = getNode(11);
              malformed.signal = 'claims.recall';
              applyTypeInvariants(malformed);
              return JSON.stringify({ node, userTaskHasSignal: 'signal' in userTask, malformed: malformed.signal });
            })()
            """).AsString());

        var node = result.RootElement.GetProperty("node");
        var signal = node.GetProperty("signal");
        Assert.Equal(" Claims.Policy ", signal.GetProperty("name").GetString());
        Assert.False(signal.TryGetProperty("extra", out _));
        var mappings = signal.GetProperty("outputMappings").EnumerateArray().ToArray();
        Assert.Equal(2, mappings.Length);
        Assert.Equal("policyVersion", mappings[0].GetProperty("variable").GetString());
        Assert.Equal(JsonValueKind.Null, mappings[0].GetProperty("dataType").ValueKind);
        Assert.Equal(JsonValueKind.Null, mappings[0].GetProperty("isArray").ValueKind);
        Assert.Equal("", mappings[1].GetProperty("variable").GetString());
        foreach (var property in new[]
                 {
                     "service", "message", "scriptFormat", "script", "attachedToRef", "errorVariable", "conditional",
                     "gatewayRef"
                 })
        {
            Assert.False(node.TryGetProperty(property, out _), $"The signal catch kept '{property}'.");
        }
        Assert.Empty(node.GetProperty("roles").EnumerateArray());
        Assert.Empty(node.GetProperty("variables").EnumerateArray());
        Assert.False(node.GetProperty("requiresClaim").GetBoolean());
        Assert.Equal(JsonValueKind.Null, node.GetProperty("assignee").ValueKind);
        Assert.False(result.RootElement.GetProperty("userTaskHasSignal").GetBoolean());
        Assert.Equal("claims.recall", result.RootElement.GetProperty("malformed").GetString());
    }

    [Fact]
    public void Loader_RoundTripsTheSignalAndKeepsWhatSaveMustReport()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const diagram = signalDiagram();
              diagram.flowNodes.find(node => node.id === 9).signal.outputMappings[0].validation = 'policyVersion > 0';
              loadFromObject(diagram);
              const exported = prepareWorkflowExport();
              loadFromObject(JSON.parse(exported.json));
              const again = prepareWorkflowExport();

              const missing = signalDiagram();
              delete missing.flowNodes.find(node => node.id === 11).signal;
              missing.flowNodes.push({ id: 20, name: 'Call', type: 'callActivity',
                callActivity: { workflowKey: 'child', inputMappings: [], outputMappings: [] },
                signal: { name: 'claims.call', outputMappings: [] } });
              missing.flowNodes.push({ id: 21, name: 'Task', type: 'userTask', signal: { name: 'claims.task' } });
              loadFromObject(missing);
              const loaded = {
                catchSignal: getNode(11).signal === undefined,
                callSignal: getNode(20).signal,
                taskSignal: getNode(21).signal !== undefined
              };
              return JSON.stringify({
                errors: exported.errors, json: JSON.parse(exported.json), same: exported.json === again.json,
                loaded, missingErrors: prepareWorkflowExport().errors
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Empty(root.GetProperty("errors").EnumerateArray());
        Assert.True(root.GetProperty("same").GetBoolean());
        var node = root.GetProperty("json").GetProperty("flowNodes").EnumerateArray()
            .Single(item => item.GetProperty("id").GetInt32() == 9);
        Assert.Equal("intermediateSignalCatchEvent", node.GetProperty("type").GetString());
        Assert.Equal("claims.policy-changed", node.GetProperty("signal").GetProperty("name").GetString());
        var mapping = node.GetProperty("signal").GetProperty("outputMappings")[0];
        Assert.Equal("policyVersion", mapping.GetProperty("variable").GetString());
        Assert.Equal("version", mapping.GetProperty("path").GetString());
        Assert.Equal("number", mapping.GetProperty("dataType").GetString());
        Assert.False(mapping.GetProperty("isArray").GetBoolean());
        Assert.True(mapping.GetProperty("required").GetBoolean());
        Assert.Equal("policyVersion > 0", mapping.GetProperty("validation").GetString());
        foreach (var property in new[] { "asyncBefore", "asyncAfter", "message", "conditional", "timer", "cancelActivity" })
        {
            Assert.False(node.TryGetProperty(property, out _), $"The export kept '{property}'.");
        }
        Assert.Null(ServerError(root.GetProperty("json").GetRawText()));

        var loaded = root.GetProperty("loaded");
        Assert.True(loaded.GetProperty("catchSignal").GetBoolean());
        Assert.Equal("claims.call", loaded.GetProperty("callSignal").GetProperty("name").GetString());
        Assert.False(loaded.GetProperty("taskSignal").GetBoolean());
        var missingErrors = root.GetProperty("missingErrors").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("Signal catch event #11 must define signal.name.", missingErrors);
        Assert.Contains("Flow node #20 defines signal but is not a signal event.", missingErrors);
    }

    [Fact]
    public void Inspector_EditsTheNameAndMappingsWithLabeledControlsAndHelp()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(signalDiagram());
              select('node', 9);
              const section = () => within(sectionFrom('Signal', ['Boundary events', 'Outgoing flows']));
              const controls = section().filter(item => ['INPUT', 'SELECT', 'TEXTAREA'].includes(item.tagName));
              const unlabeled = controls.filter(control =>
                !(control.parentNode && control.parentNode.tagName === 'LABEL') &&
                !/^<label>[^<]+<\/label>$/.test(String(control.parentNode && control.parentNode.innerHTML)) &&
                !control.attributes['aria-label']);
              const fieldLabels = controls.map(control => control.parentNode && control.parentNode.tagName === 'LABEL'
                ? control.parentNode.children.map(child => child.textContent).join('')
                : String(control.parentNode.innerHTML).replace(/<\/?label>/g, ''));
              const nameInput = section().find(item => item.attributes?.['data-signal-name'] === 'true');
              const status = section().find(item => item.id === nameInput.attributes['aria-describedby'].split(' ')[0]);
              const suggestions = section().find(item => item.tagName === 'DATALIST' && item.id === nameInput.attributes.list);
              const helpTexts = section().filter(item => item.className === 'inspector-help').map(item =>
                descendants(item).map(part => part.textContent || '').join(''));
              const headings = section().filter(item => item.tagName === 'H2').map(item => item.textContent);
              const valid = status.textContent;

              nameInput.value = 'Claims.Policy';
              nameInput.oninput();
              const uppercase = { status: status.textContent, invalid: nameInput.attributes['aria-invalid'], stored: getNode(9).signal.name };
              nameInput.value = '';
              nameInput.oninput();
              const empty = status.textContent;

              const button = text => section().filter(item => item.tagName === 'BUTTON' && item.textContent === text).slice(-1)[0];
              button('+ Add mapping').onclick();
              const afterAdd = getNode(9).signal.outputMappings.length;
              button('Delete mapping').onclick();
              const afterDelete = getNode(9).signal.outputMappings.map(mapping => mapping.variable);
              return JSON.stringify({
                unlabeled: unlabeled.length, controls: controls.length, fieldLabels, valid, uppercase, empty,
                suggestions: suggestions ? suggestions.children.map(option => option.value) : null,
                placeholder: nameInput.placeholder, helpTexts, headings, afterAdd, afterDelete,
                statusRole: status.attributes.role
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(0, root.GetProperty("unlabeled").GetInt32());
        Assert.True(root.GetProperty("controls").GetInt32() >= 8);
        var labels = root.GetProperty("fieldLabels").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(
            ["Signal name", "Variable", "Payload path", "Data type", "Array", "Required final value",
             "Default value (optional)", "Validation (NCalc, optional)"],
            labels);
        Assert.Equal("", root.GetProperty("valid").GetString());
        Assert.Equal("status", root.GetProperty("statusRole").GetString());
        var uppercase = root.GetProperty("uppercase");
        Assert.Equal(
            "Signal catch event #9 signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit. " +
            "Use lowercase letters, so letter case never makes a signal miss a wait.",
            uppercase.GetProperty("status").GetString());
        Assert.Equal("true", uppercase.GetProperty("invalid").GetString());
        Assert.Equal("Claims.Policy", uppercase.GetProperty("stored").GetString());
        Assert.Equal("Signal catch event #9 must define signal.name.", root.GetProperty("empty").GetString());
        Assert.Equal(["claims.recall"], root.GetProperty("suggestions").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("claims.policy-changed", root.GetProperty("placeholder").GetString());
        Assert.Equal(["Signal", "Output mappings"], root.GetProperty("headings").EnumerateArray().Select(item => item.GetString()));
        var help = string.Join("\n", root.GetProperty("helpTexts").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("only the waits that exist when it is thrown", help, StringComparison.Ordinal);
        Assert.Contains("Signals page", help, StringComparison.Ordinal);
        Assert.Contains("POST /api/signal-broadcasts", help, StringComparison.Ordinal);
        Assert.Contains("claims.policy-changed", help, StringComparison.Ordinal);
        Assert.Contains("never write or read shared variables", help, StringComparison.Ordinal);
        Assert.Equal(2, root.GetProperty("afterAdd").GetInt32());
        Assert.Equal(["policyVersion"], root.GetProperty("afterDelete").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void Inspector_SuggestsOnlyInstanceTargetsAndAdoptsTheirTypeInPlace()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(signalDiagram());
              getNode(9).signal.outputMappings.push({ variable: '', path: 'tags', dataType: 'number', isArray: false });
              // The shared suggestion helper attaches its list through parentElement, which the stub lacks.
              const created = [];
              const createElement = document.createElement;
              document.createElement = tag => {
                const element = createElement(tag);
                created.push(element);
                return element;
              };
              select('node', 9);
              document.createElement = createElement;
              const section = within(sectionFrom('Signal', ['Boundary events', 'Outgoing flows']));
              const variableInputs = section.filter(item => item.attributes?.['data-signal-mapping'] !== undefined);
              const list = created.find(item => item.tagName === 'DATALIST' && item.id === variableInputs[1].attributes.list);
              const input = variableInputs[1];
              input.value = 'tags';
              input.oninput();
              (input.eventListeners.change || []).forEach(entry => entry.listener.call(input, { type: 'change' }));
              const adopted = getNode(9).signal.outputMappings[1];
              const typeSelect = section.filter(item => item.tagName === 'SELECT').slice(-1)[0];
              const shared = getNode(9).signal.outputMappings[0];
              const sharedInput = variableInputs[0];
              sharedInput.value = 'policyOwner';
              sharedInput.oninput();
              (sharedInput.eventListeners.change || []).forEach(entry => entry.listener.call(sharedInput, { type: 'change' }));
              return JSON.stringify({
                options: list.children.map(option => option.value),
                adopted, selectValue: typeSelect.value,
                sharedType: getNode(9).signal.outputMappings[0].dataType
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(["policyVersion", "tags"], root.GetProperty("options").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("tags", root.GetProperty("adopted").GetProperty("variable").GetString());
        Assert.Equal("string", root.GetProperty("adopted").GetProperty("dataType").GetString());
        Assert.True(root.GetProperty("adopted").GetProperty("isArray").GetBoolean());
        Assert.Equal("string", root.GetProperty("selectValue").GetString());
        // A shared alias is never adopted: its write is refused instead.
        Assert.Equal("number", root.GetProperty("sharedType").GetString());
    }

    [Fact]
    public void Inspector_RepairsAMalformedImportAndShowsTheHostSection()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const diagram = signalDiagram();
              diagram.flowNodes.find(node => node.id === 11).signal = 'claims.recall';
              diagram.flowNodes.push({ id: 20, name: 'Call', type: 'callActivity',
                callActivity: { workflowKey: 'child', inputMappings: [], outputMappings: [] },
                signal: { name: 'claims.call', outputMappings: [] } });
              loadFromObject(diagram);
              const buttons = () => within(latestInspector()).filter(item => item.tagName === 'BUTTON').map(item => item.textContent);
              const button = text => within(latestInspector()).filter(item => item.tagName === 'BUTTON' && item.textContent === text).slice(-1)[0];

              select('node', 11);
              const malformedButtons = buttons();
              const kept = getNode(11).signal;
              button('Replace with a signal configuration').onclick();
              const repaired = getNode(11).signal;

              select('node', 9);
              const hostButtons = buttons();
              const hostHeadings = latestInspector().filter(item => item.tagName === 'H2').map(item => item.textContent);
              button('+ Add timer boundary event').onclick();
              const boundary = model.flowNodes[model.flowNodes.length - 1];

              select('node', 20);
              const callButtons = buttons();
              button('Remove this signal').onclick();
              const callSignal = 'signal' in getNode(20);
              return JSON.stringify({ malformedButtons, kept, repaired, hostButtons, hostHeadings,
                boundary: { type: boundary.type, attachedToRef: boundary.attachedToRef }, callButtons, callSignal });
            })()
            """).AsString());

        var root = result.RootElement;
        string[] Texts(string property) => root.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Contains("Replace with a signal configuration", Texts("malformedButtons"));
        Assert.Equal("claims.recall", root.GetProperty("kept").GetString());
        Assert.Equal("", root.GetProperty("repaired").GetProperty("name").GetString());
        Assert.Contains("+ Add timer boundary event", Texts("hostButtons"));
        Assert.Contains("+ Add conditional boundary event", Texts("hostButtons"));
        Assert.Contains("+ Add signal boundary event", Texts("hostButtons"));
        Assert.Contains("Boundary events", Texts("hostHeadings"));
        Assert.Equal("timerBoundaryEvent", root.GetProperty("boundary").GetProperty("type").GetString());
        Assert.Equal(9, root.GetProperty("boundary").GetProperty("attachedToRef").GetInt32());
        Assert.Contains("Remove this signal", Texts("callButtons"));
        Assert.False(root.GetProperty("callSignal").GetBoolean());
    }

    [Fact]
    public void HostSection_ReplacesTheAddButtonsWithTheLimitNoteAtEightBoundariesOfAnyKind()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const diagram = boundaryDiagram();
              // Signal boundaries #14 and #16 are on task #2; six timer and conditional ones make eight.
              for (let index = 0; index < 6; index++) {
                const id = 30 + index;
                diagram.flowNodes.push(index % 2 === 0
                  ? { id, name: 'Wait ' + id, type: 'timerBoundaryEvent', attachedToRef: 2, cancelActivity: false,
                      timer: { timeDuration: 'P2D' } }
                  : { id, name: 'Wait ' + id, type: 'conditionalBoundaryEvent', attachedToRef: 2, cancelActivity: false,
                      conditional: { condition: 'policyVersion > ' + index } });
                diagram.sequenceFlows.push({ id: id * 100 + 1, name: '', sourceRef: id, targetRef: 15 });
              }
              loadFromObject(diagram);
              const texts = tag => within(latestInspector()).filter(item => item.tagName === tag).map(item => item.textContent);

              select('node', 2);
              const atEight = { buttons: texts('BUTTON'), notes: texts('P') };
              model.flowNodes = model.flowNodes.filter(node => node.id !== 35);
              model.sequenceFlows = model.sequenceFlows.filter(flow => flow.sourceRef !== 35);
              select('node', 2);
              const atSeven = { buttons: texts('BUTTON'), notes: texts('P') };
              return JSON.stringify({ atEight, atSeven });
            })()
            """).AsString());

        const string Limit = "The combined limit of eight timer, conditional, and signal boundaries on this host has been reached.";
        string[] Texts(string state, string property) => result.RootElement.GetProperty(state).GetProperty(property)
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Contains(Limit, Texts("atEight", "notes"));
        Assert.DoesNotContain(Texts("atEight", "buttons"), text => text.EndsWith("boundary event", StringComparison.Ordinal));
        Assert.DoesNotContain(Limit, Texts("atSeven", "notes"));
        Assert.Contains("+ Add signal boundary event", Texts("atSeven", "buttons"));
    }

    [Fact]
    public void Boundary_IsDrawnOnItsHostWithADashedRingWhenItDoesNotInterrupt()
    {
        var html = EditorSource.Read();
        Assert.Contains(".node.signalBoundaryEvent circle.body", html, StringComparison.Ordinal);
        Assert.Contains(".node.signalBoundaryEvent.non-interrupting circle.ring", html, StringComparison.Ordinal);
        Assert.Contains(".node.selected.signalBoundaryEvent circle.body", html, StringComparison.Ordinal);
        Assert.Contains(".node.signalBoundaryEvent .node-icon", html, StringComparison.Ordinal);

        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(boundaryDiagram());
              render();
              const type = 'signalBoundaryEvent';
              const parts = id => descendants(nodeGroup(id));
              const host = getNode(2);
              const hostSize = nodeSize(host);
              const near = (left, right) => Math.abs(left - right) < 0.01;
              const onHostBorder = id => {
                const point = center(getNode(id));
                return point.x >= host.x - 0.01 && point.x <= host.x + hostSize.w + 0.01 &&
                  point.y >= host.y - 0.01 && point.y <= host.y + hostSize.h + 0.01 &&
                  (near(point.x, host.x) || near(point.x, host.x + hostSize.w) ||
                   near(point.y, host.y) || near(point.y, host.y + hostSize.h));
              };
              const drawn = () => ({
                nonInterrupting: String(nodeGroup(14).attributes.class),
                interrupting: String(nodeGroup(16).attributes.class)
              });
              const before = drawn();

              select('node', 16);
              const toggle = within(latestInspector()).filter(element => element.tagName === 'LABEL' &&
                  descendants(element).some(child => String(child.textContent).includes('Interrupt attached activity')))
                .slice(-1)[0].children.find(child => child.type === 'checkbox');
              const checkedBefore = toggle.checked;
              toggle.checked = false;
              toggle.onchange();
              return JSON.stringify({
                label: typeLabel(type),
                inTypeList: NODE_TYPE_OPTIONS.some(option => option.value === type),
                boundary: isBoundaryEventType(type), event: isEventType(type), single: isSingleOutgoingType(type),
                passThrough: isPassThroughType(type), signalEvent: isSignalWaitType(type),
                size: nodeSize(getNode(14)),
                before,
                rings: parts(14).filter(part => part.attributes?.class === 'ring').length,
                icon: parts(14).find(part => String(part.attributes?.class) === 'node-icon')?.attributes.href,
                onBorder: [onHostBorder(14), onHostBorder(16)],
                flowLabel: flowLabel(model.sequenceFlows.find(flow => flow.id === 1401)),
                search: searchDiagram(model, 'signal boundary').map(item => item.id).sort((a, b) => a - b),
                incoming: connectionTargetProblem(getNode(14)),
                secondOutgoing: connectionSourceProblem(getNode(14)),
                checkedBefore,
                after: { cancelActivity: getNode(16).cancelActivity, classes: String(nodeGroup(16).attributes.class) }
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("Signal Boundary Event", root.GetProperty("label").GetString());
        // Like the other boundaries, it is added from its host, never chosen as a Type.
        Assert.False(root.GetProperty("inTypeList").GetBoolean());
        Assert.True(root.GetProperty("boundary").GetBoolean());
        Assert.False(root.GetProperty("event").GetBoolean());
        Assert.True(root.GetProperty("single").GetBoolean());
        Assert.True(root.GetProperty("passThrough").GetBoolean());
        Assert.True(root.GetProperty("signalEvent").GetBoolean());
        Assert.Equal(28, root.GetProperty("size").GetProperty("w").GetInt32());
        Assert.Contains("non-interrupting", root.GetProperty("before").GetProperty("nonInterrupting").GetString());
        Assert.DoesNotContain("non-interrupting", root.GetProperty("before").GetProperty("interrupting").GetString());
        Assert.Equal(1, root.GetProperty("rings").GetInt32());
        Assert.Equal("#icon-signal-catch", root.GetProperty("icon").GetString());
        Assert.Equal([true, true], root.GetProperty("onBorder").EnumerateArray().Select(item => item.GetBoolean()));
        Assert.Equal("signal", root.GetProperty("flowLabel").GetString());
        Assert.Equal([14, 16], root.GetProperty("search").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Equal("A boundary event cannot have an incoming sequence flow.", root.GetProperty("incoming").GetString());
        Assert.Equal("This node type can have exactly one outgoing flow.", root.GetProperty("secondOutgoing").GetString());
        Assert.True(root.GetProperty("checkedBefore").GetBoolean());
        Assert.False(root.GetProperty("after").GetProperty("cancelActivity").GetBoolean());
        Assert.Contains("non-interrupting", root.GetProperty("after").GetProperty("classes").GetString());
    }

    [Fact]
    public void Viewer_DrawsTheBoundaryAndItsDashedRing()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "viewer");
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(boundaryDiagram());
              setInstanceOverlay({ nodes: [{ id: 2, state: 'active', label: 'Waiting for review' }] });
              const parts = id => descendants(nodeGroup(id));
              return JSON.stringify({
                nonInterrupting: String(nodeGroup(14).attributes.class),
                interrupting: String(nodeGroup(16).attributes.class),
                rings: parts(14).filter(part => part.attributes?.class === 'ring').length,
                icon: parts(14).find(part => String(part.attributes?.class) === 'node-icon')?.attributes.href
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Contains("signalBoundaryEvent", root.GetProperty("nonInterrupting").GetString());
        Assert.Contains("non-interrupting", root.GetProperty("nonInterrupting").GetString());
        Assert.DoesNotContain("non-interrupting", root.GetProperty("interrupting").GetString());
        Assert.Equal(1, root.GetProperty("rings").GetInt32());
        Assert.Equal("#icon-signal-catch", root.GetProperty("icon").GetString());
    }

    [Fact]
    public void HostSection_ListsAndAddsSignalBoundariesUpToTheCombinedLimit()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const diagram = boundaryDiagram();
              const body = { mode: 'parallel', source: 'collection', collectionVariable: '', completionEvaluation: 'afterAll',
                outputVariables: [], resultVariable: '' };
              diagram.flowNodes.push(
                { id: 30, name: 'Score', type: 'scriptTask', asyncBefore: true, scriptFormat: 'ncalc', assignments: [],
                  multiInstance: JSON.parse(JSON.stringify(body)), laneId: 10, x: 700, y: 90 },
                { id: 31, name: 'Ask', type: 'callActivity', laneId: 10, x: 900, y: 90,
                  callActivity: { workflowKey: 'child', inputMappings: [], outputMappings: [] },
                  multiInstance: JSON.parse(JSON.stringify(body)) },
                { id: 32, name: 'Plain script', type: 'scriptTask', asyncBefore: true, scriptFormat: 'ncalc', assignments: [],
                  laneId: 10, x: 700, y: 280 });
              loadFromObject(diagram);
              const inspectorItems = () => within(latestInspector());
              const buttons = () => inspectorItems().filter(item => item.tagName === 'BUTTON').map(item => item.textContent);
              const button = text => inspectorItems().filter(item => item.tagName === 'BUTTON' && item.textContent === text).slice(-1)[0];
              const notes = () => inspectorItems().filter(item => item.className === 'empty-note').map(item => item.textContent);
              const cards = () => inspectorItems().filter(item => item.className === 'field inspector-card clickable')
                .map(item => item.innerHTML).filter(text => /^<div class="badge">(timer|conditional|signal) #/.test(text));

              select('node', 2);
              const hostButtons = buttons();
              const hostCards = cards();
              button('+ Add signal boundary event').onclick();
              const added = model.flowNodes[model.flowNodes.length - 1];
              const addedState = {
                type: added.type, attachedToRef: added.attachedToRef, cancelActivity: added.cancelActivity,
                signal: added.signal, name: added.name, selected: selected && selected.nodeId === added.id,
                connecting: connectMode && connectSource === added.id,
                observable: findObservableBoundaries(2).length
              };
              setConnectMode(false);
              for (let index = 0; index < 5; index++) addTimerBoundary(getNode(2));
              setConnectMode(false);
              select('node', 2);
              const fullButtons = buttons();
              const fullNotes = notes();

              select('node', 30);
              const bodyButtons = buttons();
              const bodyNotes = notes();
              select('node', 31);
              const callButtons = buttons();
              model.flowNodes.push({ id: 33, name: 'Imported', type: 'signalBoundaryEvent', attachedToRef: 30,
                cancelActivity: true, signal: { name: 'claims.imported', outputMappings: [] } });
              select('node', 30);
              const bodyWithSignalNotes = notes();

              const alerts = [];
              alert = message => alerts.push(String(message));
              model.flowNodes.push({ id: 34, name: 'On the plain script', type: 'signalBoundaryEvent', attachedToRef: 32,
                cancelActivity: true, signal: { name: 'claims.plain', outputMappings: [] } });
              const enabled = setAutomaticMultiInstance(getNode(32), true);
              return JSON.stringify({ hostButtons, hostCards, addedState, fullButtons, fullNotes, bodyButtons, bodyNotes,
                callButtons, bodyWithSignalNotes, enabled, alerts, plainBody: getNode(32).multiInstance ?? null });
            })()
            """).AsString());

        var root = result.RootElement;
        string[] Texts(string property) => root.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Contains("+ Add timer boundary event", Texts("hostButtons"));
        Assert.Contains("+ Add conditional boundary event", Texts("hostButtons"));
        Assert.Contains("+ Add signal boundary event", Texts("hostButtons"));
        Assert.Equal(
            ["<div class=\"badge\">signal #14 · non-interrupting → #15 Recalled</div>",
             "<div class=\"badge\">signal #16 · interrupting → #15 Recalled</div>"],
            Texts("hostCards"));

        var added = root.GetProperty("addedState");
        Assert.Equal("signalBoundaryEvent", added.GetProperty("type").GetString());
        Assert.Equal(2, added.GetProperty("attachedToRef").GetInt32());
        Assert.True(added.GetProperty("cancelActivity").GetBoolean());
        Assert.Equal("", added.GetProperty("signal").GetProperty("name").GetString());
        Assert.Empty(added.GetProperty("signal").GetProperty("outputMappings").EnumerateArray());
        Assert.Equal("Review signal", added.GetProperty("name").GetString());
        Assert.True(added.GetProperty("selected").GetBoolean());
        Assert.True(added.GetProperty("connecting").GetBoolean());
        Assert.Equal(3, added.GetProperty("observable").GetInt32());

        // Three signal boundaries and five timers reach the combined limit of eight.
        Assert.DoesNotContain(Texts("fullButtons"), text => text.StartsWith("+ Add", StringComparison.Ordinal) &&
            text.EndsWith("boundary event", StringComparison.Ordinal));
        Assert.Contains(
            "The combined limit of eight timer, conditional, and signal boundaries on this host has been reached.",
            Texts("fullNotes"));

        // Multi-instance bodies host timer and conditional boundaries, never signal boundaries.
        foreach (var property in new[] { "bodyButtons", "callButtons" })
        {
            Assert.Contains("+ Add timer boundary event", Texts(property));
            Assert.Contains("+ Add conditional boundary event", Texts(property));
            Assert.DoesNotContain("+ Add signal boundary event", Texts(property));
        }
        Assert.Contains("A multi-instance service, script, or call activity cannot host signal boundaries.", Texts("bodyNotes"));
        Assert.Contains(
            "A multi-instance service, script, or call activity cannot host signal boundaries. Delete the attached signal boundaries before saving.",
            Texts("bodyWithSignalNotes"));

        // Enabling multi-instance never removes a signal boundary silently.
        Assert.False(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(
            ["Delete this activity's signal boundaries before enabling multi-instance; a multi-instance service, script, or call activity cannot host them."],
            Texts("alerts"));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("plainBody").ValueKind);
    }

    [Fact]
    public void TypeChanges_KeepSignalBoundariesOnHostsThatCanHoldThemAndMoveBetweenBoundaryKinds()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const present = ids => ids.filter(id => !!getNode(id));
              loadFromObject(boundaryDiagram());
              const review = getNode(2);
              changeNodeTypeFromInspector(review, 'intermediateSignalCatchEvent');
              const onCatch = present([14, 16]);
              changeNodeTypeFromInspector(review, 'task');
              const onPlainTask = present([14, 16]);
              const flowsLeft = model.sequenceFlows.filter(flow => [1401, 1601].includes(flow.id)).length;

              loadFromObject(boundaryDiagram());
              addTimerBoundary(getNode(2));
              setConnectMode(false);
              const timer = model.flowNodes[model.flowNodes.length - 1];
              timer.cancelActivity = false;
              changeNodeTypeFromInspector(timer, 'signalBoundaryEvent');
              const timerToSignal = JSON.parse(JSON.stringify(timer));
              const signalBoundary = getNode(14);
              changeNodeTypeFromInspector(signalBoundary, 'conditionalBoundaryEvent');
              const signalToConditional = JSON.parse(JSON.stringify(signalBoundary));

              // Clearing Async before on an automatic host removes its signal boundaries too, once confirmed.
              loadFromObject(boundaryDiagram());
              const host = getNode(2);
              host.type = 'task';
              host.asyncBefore = true;
              applyTypeInvariants(host);
              const confirms = [];
              confirm = message => { confirms.push(String(message)); return true; };
              select('node', 2);
              const asyncToggle = within(latestInspector()).filter(element => element.tagName === 'LABEL' &&
                  descendants(element).some(child => String(child.textContent).includes('Async before')))
                .slice(-1)[0].children.find(child => child.type === 'checkbox');
              asyncToggle.checked = false;
              asyncToggle.onchange();
              return JSON.stringify({ onCatch, onPlainTask, flowsLeft, timerToSignal, signalToConditional,
                confirms, afterAsync: present([14, 16]), asyncBefore: getNode(2).asyncBefore });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal([14, 16], root.GetProperty("onCatch").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Empty(root.GetProperty("onPlainTask").EnumerateArray());
        Assert.Equal(0, root.GetProperty("flowsLeft").GetInt32());

        var timerToSignal = root.GetProperty("timerToSignal");
        Assert.Equal("signalBoundaryEvent", timerToSignal.GetProperty("type").GetString());
        Assert.Equal(2, timerToSignal.GetProperty("attachedToRef").GetInt32());
        Assert.False(timerToSignal.GetProperty("cancelActivity").GetBoolean());
        Assert.False(timerToSignal.TryGetProperty("timer", out _));
        Assert.Equal("", timerToSignal.GetProperty("signal").GetProperty("name").GetString());

        var signalToConditional = root.GetProperty("signalToConditional");
        Assert.Equal(2, signalToConditional.GetProperty("attachedToRef").GetInt32());
        Assert.False(signalToConditional.GetProperty("cancelActivity").GetBoolean());
        Assert.False(signalToConditional.TryGetProperty("signal", out _));

        Assert.Equal(
            ["Disabling async before removes attached timer, conditional, and signal boundary events and their outgoing flows. Continue?"],
            root.GetProperty("confirms").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(root.GetProperty("afterAsync").EnumerateArray());
        Assert.False(root.GetProperty("asyncBefore").GetBoolean());
    }

    [Fact]
    public void Invariants_KeepTheBoundarysHostInterruptionAndSignalOnly()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(boundaryDiagram());
              const node = getNode(16);
              Object.assign(node, {
                roles: ['Clerk'], variables: [{ id: 1, name: 'x' }], assignee: 'alice', requiresClaim: true,
                service: { url: 'x' }, message: { clientId: 'x' }, scriptFormat: 'javascript', script: 'x',
                errorVariable: 'failure', conditional: { condition: 'x' }, gatewayRef: 4, asyncBefore: true,
                asyncAfter: true, job: { failureHandling: 'retryFirst' }, activationCondition: 'x'
              });
              delete node.cancelActivity;
              node.signal = { name: ' Claims.Withdrawn ', extra: true, outputMappings: [{ variable: 'POLICYVERSION', path: 'version' }] };
              applyTypeInvariants(node);
              const detached = getNode(14);
              detached.attachedToRef = 'two';
              applyTypeInvariants(detached);
              const userTask = getNode(2);
              userTask.cancelActivity = false;
              applyTypeInvariants(userTask);
              return JSON.stringify({ node, detached: detached.attachedToRef, userTaskHasCancel: 'cancelActivity' in userTask });
            })()
            """).AsString());

        var node = result.RootElement.GetProperty("node");
        Assert.Equal(2, node.GetProperty("attachedToRef").GetInt32());
        Assert.True(node.GetProperty("cancelActivity").GetBoolean());
        var signal = node.GetProperty("signal");
        Assert.Equal(" Claims.Withdrawn ", signal.GetProperty("name").GetString());
        Assert.False(signal.TryGetProperty("extra", out _));
        Assert.Equal("policyVersion", signal.GetProperty("outputMappings")[0].GetProperty("variable").GetString());
        foreach (var property in new[]
                 {
                     "service", "message", "scriptFormat", "script", "errorVariable", "conditional", "gatewayRef",
                     "asyncBefore", "asyncAfter", "job", "activationCondition"
                 })
        {
            Assert.False(node.TryGetProperty(property, out _), $"The signal boundary kept '{property}'.");
        }
        Assert.Empty(node.GetProperty("roles").EnumerateArray());
        Assert.Empty(node.GetProperty("variables").EnumerateArray());
        Assert.False(node.GetProperty("requiresClaim").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("detached").ValueKind);
        Assert.False(result.RootElement.GetProperty("userTaskHasCancel").GetBoolean());
    }

    [Fact]
    public void Loader_RoundTripsTheBoundaryAndTheServerAcceptsItsExport()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const diagram = boundaryDiagram();
              delete diagram.flowNodes.find(node => node.id === 16).cancelActivity;
              diagram.flowNodes.find(node => node.id === 14).asyncBefore = true;
              loadFromObject(diagram);
              const exported = prepareWorkflowExport();
              loadFromObject(JSON.parse(exported.json));
              const again = prepareWorkflowExport();
              return JSON.stringify({ errors: exported.errors, json: JSON.parse(exported.json), same: exported.json === again.json });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Empty(root.GetProperty("errors").EnumerateArray());
        Assert.True(root.GetProperty("same").GetBoolean());
        var nodes = root.GetProperty("json").GetProperty("flowNodes").EnumerateArray().ToArray();
        var nonInterrupting = nodes.Single(item => item.GetProperty("id").GetInt32() == 14);
        Assert.Equal("signalBoundaryEvent", nonInterrupting.GetProperty("type").GetString());
        Assert.Equal(2, nonInterrupting.GetProperty("attachedToRef").GetInt32());
        Assert.False(nonInterrupting.GetProperty("cancelActivity").GetBoolean());
        Assert.Equal("claims.recall-announced", nonInterrupting.GetProperty("signal").GetProperty("name").GetString());
        var mapping = nonInterrupting.GetProperty("signal").GetProperty("outputMappings")[0];
        Assert.Equal("policyVersion", mapping.GetProperty("variable").GetString());
        Assert.Equal("version", mapping.GetProperty("path").GetString());
        Assert.Equal("number", mapping.GetProperty("dataType").GetString());
        foreach (var property in new[] { "asyncBefore", "asyncAfter", "message", "conditional", "timer" })
        {
            Assert.False(nonInterrupting.TryGetProperty(property, out _), $"The export kept '{property}'.");
        }
        // A missing cancelActivity loads as interrupting, as the server reads it.
        var interrupting = nodes.Single(item => item.GetProperty("id").GetInt32() == 16);
        Assert.True(interrupting.GetProperty("cancelActivity").GetBoolean());
        Assert.Null(ServerError(root.GetProperty("json").GetRawText()));
    }

    [Fact]
    public void BoundaryInspector_LabelsItsControlsAndReportsNamesItsHostAlreadyUses()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(boundaryDiagram());
              select('node', 14);
              const all = within(latestInspector());
              const controls = all.filter(item => ['INPUT', 'SELECT', 'TEXTAREA'].includes(item.tagName));
              const unlabeled = controls.filter(control =>
                !(control.parentNode && control.parentNode.tagName === 'LABEL') &&
                !/^<label>[^<]+<\/label>$/.test(String(control.parentNode && control.parentNode.innerHTML)) &&
                !control.attributes['aria-label']);
              const fieldLabels = controls.map(control => control.parentNode && control.parentNode.tagName === 'LABEL'
                ? control.parentNode.children.map(child => child.textContent).join('')
                : String(control.parentNode.innerHTML).replace(/<\/?label>/g, ''));
              const valueOf = label => controls[fieldLabels.indexOf(label)];
              const helpTexts = all.filter(item => item.className === 'inspector-help').map(item =>
                descendants(item).map(part => part.textContent || '').join(''));
              const nameInput = all.find(item => item.attributes?.['data-signal-name'] === 'true');
              const status = all.find(item => item.id === nameInput.attributes['aria-describedby'].split(' ')[0]);
              const suggestions = all.find(item => item.tagName === 'DATALIST' && item.id === nameInput.attributes.list);
              const typed = text => { nameInput.value = text; nameInput.oninput(); return status.textContent; };
              const state = {
                type: valueOf('Type').value, typeDisabled: valueOf('Type').disabled,
                attached: valueOf('Attached to').value, interrupts: valueOf('Interrupt attached activity').checked,
                suggestions: suggestions.children.map(option => option.value),
                empty: typed(''), sibling: typed('claims.withdrawn'), unique: typed('claims.recall-announced')
              };

              // On a signal catch, the catch's own name is refused and not suggested.
              getNode(14).attachedToRef = 9;
              select('node', 14);
              const onCatch = within(latestInspector());
              const catchName = onCatch.find(item => item.attributes?.['data-signal-name'] === 'true');
              const catchStatus = onCatch.find(item => item.id === catchName.attributes['aria-describedby'].split(' ')[0]);
              const catchList = onCatch.find(item => item.tagName === 'DATALIST' && item.id === catchName.attributes.list);
              catchName.value = 'claims.policy-changed';
              catchName.oninput();
              return JSON.stringify({ unlabeled: unlabeled.length, fieldLabels, helpTexts, state,
                catchSuggestions: catchList.children.map(option => option.value), catchStatus: catchStatus.textContent });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(0, root.GetProperty("unlabeled").GetInt32());
        var labels = root.GetProperty("fieldLabels").EnumerateArray().Select(item => item.GetString()).ToArray();
        foreach (var label in new[]
                 {
                     "Type", "Signal name", "Variable", "Payload path", "Data type", "Attached to",
                     "Interrupt attached activity"
                 })
        {
            Assert.Contains(label, labels);
        }
        // The host and interruption fields come before the Signal section.
        Assert.True(Array.IndexOf(labels, "Interrupt attached activity") < Array.IndexOf(labels, "Signal name"));
        var state = root.GetProperty("state");
        Assert.Equal("Signal Boundary Event", state.GetProperty("type").GetString());
        Assert.True(state.GetProperty("typeDisabled").GetBoolean());
        Assert.Equal("#2 Review", state.GetProperty("attached").GetString());
        Assert.False(state.GetProperty("interrupts").GetBoolean());
        // A sibling's name on the same host is not offered; the catches' names are.
        Assert.Equal(
            ["claims.policy-changed", "claims.recall"],
            state.GetProperty("suggestions").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("Signal boundary event #14 must define signal.name.", state.GetProperty("empty").GetString());
        // Saving reports the repeat on the later boundary, and the line shows it on both.
        Assert.Equal(
            "Signal boundary event #16 uses signal 'claims.withdrawn', which signal boundary event #14 on host #2 already uses.",
            state.GetProperty("sibling").GetString());
        Assert.Equal("", state.GetProperty("unique").GetString());
        Assert.Equal(["claims.recall", "claims.withdrawn"],
            root.GetProperty("catchSuggestions").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            "Signal boundary event #14 uses signal 'claims.policy-changed', the signal its host signal catch event #9 waits for.",
            root.GetProperty("catchStatus").GetString());

        var help = string.Join("\n", root.GetProperty("helpTexts").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("while the attached activity is active", help, StringComparison.Ordinal);
        Assert.Contains("only the waits that exist when it is thrown", help, StringComparison.Ordinal);
        Assert.Contains("The activity this boundary watches", help, StringComparison.Ordinal);
        Assert.Contains("each time a signal with this name arrives", help, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> EditorErrors(string json)
    {
        var engine = CreateValidatorEngine();
        engine.SetValue("candidateJson", json);
        return JsonSerializer.Deserialize<string[]>(engine.Evaluate(
            "JSON.stringify(validateModelForSave(JSON.parse(candidateJson)))").AsString())!;
    }

    private static string? ServerError(string json)
    {
        var validator = new WorkflowDefinitionValidator(
            new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance),
            new ServiceTaskOptions());
        try
        {
            var model = JsonSerializer.Deserialize<WorkflowModel>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
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

    private static Engine CreateValidatorEngine()
    {
        var html = EditorSource.Read();
        var match = Regex.Match(html, @"// BEGIN WORKFLOW SAVE VALIDATOR(?<code>[\s\S]*?)// END WORKFLOW SAVE VALIDATOR");
        Assert.True(match.Success, "The marked workflow save validator was not found.");
        var normalizeRoles = Regex.Match(html, @"function normalizeRoles\(roles\) \{[\s\S]*?(?=function normalizeAttributesForLoad)");
        Assert.True(normalizeRoles.Success, "The role-normalization helper was not found.");
        var engine = new Engine();
        engine.Execute(normalizeRoles.Value);
        engine.Execute(match.Groups["code"].Value);
        return engine;
    }
}
