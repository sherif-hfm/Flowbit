using System.Text.Json;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Jint;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The editor's signal start event: its registry entry and shape, the loader and export, type changes from and to the
/// other starts, and the inspector's status lines for a duplicate name and for a throw that would start the workflow.
/// The save messages themselves are shared with the server through <see cref="SignalStartFixtures"/>.
/// </summary>
public sealed class EditorSignalStartTests
{
    private const string Diagram = """
        // Plain start #1 (the default, string input recallNumber), signal start #2 on recalls.announced with the required
        // mapping recallNumber and the business key, message start #3, user task #4, signal end #5 throwing another name
        // to another family, and end #6.
        const startDiagram = () => ({
          id: 'signal-start-editor', name: 'Signal start editor', initialEventId: 1, variables: [],
          lanes: [{ id: 10, name: 'Team', x: 0, y: 0, w: 1400, h: 440 }],
          flowNodes: [
            { id: 1, name: 'Manual', type: 'startEvent', laneId: 10, x: 40, y: 60, roles: [],
              variables: [{ id: 11, name: 'recallNumber', dataType: 'string', isArray: false, required: true, defaultValue: null, validation: null }],
              businessKey: { variable: 'recallNumber', uniqueness: 'all' } },
            { id: 2, name: 'Recall announced', type: 'signalStartEvent', laneId: 10, x: 40, y: 180,
              signal: { name: 'recalls.announced', outputMappings: [
                { variable: 'recallNumber', path: 'recallNumber', dataType: 'string', isArray: false, required: true, defaultValue: null, validation: null }
              ] },
              businessKey: { variable: 'recallNumber', uniqueness: 'all' } },
            { id: 3, name: 'Message', type: 'messageStartEvent', laneId: 10, x: 40, y: 300,
              message: { clientId: 'svc', clientSecret: 'secret', headerName: 'X-Token', headerValue: 'token', headerValidation: null,
                outputMappings: [{ variable: 'recallNumber', path: 'number', dataType: 'string', isArray: false, required: true, defaultValue: null, validation: null }] },
              businessKey: { variable: 'recallNumber', uniqueness: 'all' } },
            { id: 4, name: 'Notify', type: 'userTask', laneId: 10, x: 240, y: 170, roles: [], variables: [] },
            { id: 5, name: 'Announced', type: 'signalEndEvent', laneId: 10, x: 600, y: 180,
              signal: { name: 'recalls.archived', scope: { kind: 'workflow', workflowKey: 'recall-archive' }, payload: [] } },
            { id: 6, name: 'Done', type: 'endEvent', laneId: 10, x: 600, y: 320 }
          ],
          sequenceFlows: [
            { id: 101, name: '', sourceRef: 1, targetRef: 4 },
            { id: 201, name: '', sourceRef: 2, targetRef: 4 },
            { id: 301, name: '', sourceRef: 3, targetRef: 4 },
            { id: 401, name: 'Announce', sourceRef: 4, targetRef: 5 },
            { id: 402, name: 'Close', sourceRef: 4, targetRef: 6 }
          ]
        });
        const descendants = element => [element, ...(element.children || []).flatMap(descendants)];
        const latestNodeGroups = () => nodesG.children.slice(-model.flowNodes.length);
        const nodeGroup = id => latestNodeGroups().find(group => group.attributes['data-id'] === String(id));
        const latestInspector = () => {
          const children = inspector.children;
          let start = 0;
          children.forEach((child, index) => {
            if (child.innerHTML === '<label>Node ID</label>') start = index;
          });
          return children.slice(start);
        };
        const controls = () => latestInspector().flatMap(descendants);
        const marked = marker => controls().filter(item => item.attributes?.[marker] !== undefined);
        const notes = className => (controls().filter(item => item.className === className).slice(-1)[0]?.children || [])
          .map(child => child.textContent);
        const copy = value => JSON.parse(JSON.stringify(value));
        """;

    [Fact]
    public void Registry_DrawsTheStartAndListsItAfterTheMessageStart()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(startDiagram());
              render();
              const index = type => NODE_TYPE_OPTIONS.findIndex(option => option.value === type);
              const parts = descendants(nodeGroup(2));
              return JSON.stringify({
                order: index('signalStartEvent') - index('messageStartEvent'),
                label: typeLabel('signalStartEvent'),
                missingIcons: NODE_TYPE_OPTIONS.filter(option =>
                  !Object.prototype.hasOwnProperty.call(NODE_ICON_SYMBOL, option.value)).map(option => option.value),
                predicates: {
                  start: isSignalStartEventType('signalStartEvent'), entry: isEntryEventType('signalStartEvent'),
                  event: isEventType('signalStartEvent'), single: isSingleOutgoingType('signalStartEvent'),
                  passThrough: isPassThroughType('signalStartEvent'), receives: receivesSignalType('signalStartEvent'),
                  signal: isSignalEventType('signalStartEvent'), wait: isSignalWaitType('signalStartEvent'),
                  throws: throwsSignalType('signalStartEvent'), size: nodeSize({ type: 'signalStartEvent' }).w,
                  plainEntry: isEntryEventType('startEvent'), messageEntry: isEntryEventType('messageStartEvent'),
                  timerEntry: isEntryEventType('timerStartEvent'), taskEntry: isEntryEventType('task')
                },
                classes: String(nodeGroup(2).attributes.class),
                rings: parts.filter(part => part.attributes?.class === 'ring').length,
                icon: parts.find(part => String(part.attributes?.class) === 'node-icon')?.attributes.href,
                incoming: connectionTargetProblem(getNode(2)),
                search: searchDiagram(model, 'signal start').map(item => item.id)
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(1, root.GetProperty("order").GetInt32());
        Assert.Equal("Signal Start Event (start on a broadcast signal)", root.GetProperty("label").GetString());
        Assert.Empty(root.GetProperty("missingIcons").EnumerateArray());
        var predicates = root.GetProperty("predicates");
        foreach (var name in new[] { "start", "entry", "event", "single", "passThrough", "receives", "signal", "plainEntry", "messageEntry", "timerEntry" })
        {
            Assert.True(predicates.GetProperty(name).GetBoolean(), name);
        }
        foreach (var name in new[] { "wait", "throws", "taskEntry" })
        {
            Assert.False(predicates.GetProperty(name).GetBoolean(), name);
        }
        Assert.Equal(64, predicates.GetProperty("size").GetInt32());
        Assert.Contains("signalStartEvent", root.GetProperty("classes").GetString());
        Assert.Equal(0, root.GetProperty("rings").GetInt32());
        Assert.Equal("#icon-signal-catch", root.GetProperty("icon").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("incoming").GetString()));
        Assert.Equal([2], root.GetProperty("search").EnumerateArray().Select(item => item.GetInt32()));
    }

    [Fact]
    public void LoaderAndExport_KeepTheSignalAndBusinessKeyAndDropVariablesAndIdempotency()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const diagram = startDiagram();
              const start = diagram.flowNodes.find(node => node.id === 2);
              start.variables = [{ id: 21, name: 'ignored', dataType: 'string', isArray: false, required: false }];
              start.idempotency = { headerName: 'Idempotency-Key', variable: 'requestId' };
              start.roles = ['Requester'];
              start.signal.extra = 'dropped';
              loadFromObject(diagram);
              // Without selecting the node: the loader alone drops what the server refuses.
              const loaded = copy(getNode(2));
              const errors = validateModelForSave(model);
              const exported = prepareWorkflowExport();
              loadFromObject(JSON.parse(exported.json));
              const again = prepareWorkflowExport();
              return JSON.stringify({
                errors: exported.errors, json: JSON.parse(exported.json), same: exported.json === again.json,
                loadedRoles: loaded.roles, loadedVariables: loaded.variables, loadErrors: errors
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Empty(root.GetProperty("errors").EnumerateArray());
        Assert.Empty(root.GetProperty("loadErrors").EnumerateArray());
        Assert.Empty(root.GetProperty("loadedRoles").EnumerateArray());
        Assert.Empty(root.GetProperty("loadedVariables").EnumerateArray());
        Assert.True(root.GetProperty("same").GetBoolean());
        var start = root.GetProperty("json").GetProperty("flowNodes").EnumerateArray().Single(item => item.GetProperty("id").GetInt32() == 2);
        Assert.Equal("signalStartEvent", start.GetProperty("type").GetString());
        Assert.False(start.TryGetProperty("variables", out _));
        Assert.False(start.TryGetProperty("idempotency", out _));
        Assert.True(!start.TryGetProperty("roles", out var roles) || roles.GetArrayLength() == 0);
        Assert.Equal("recallNumber", start.GetProperty("businessKey").GetProperty("variable").GetString());
        var signal = start.GetProperty("signal");
        Assert.Equal("recalls.announced", signal.GetProperty("name").GetString());
        Assert.False(signal.TryGetProperty("extra", out _));
        Assert.False(signal.TryGetProperty("scope", out _));
        Assert.Single(signal.GetProperty("outputMappings").EnumerateArray());
        Assert.Null(ServerError(root.GetProperty("json").GetRawText()));
    }

    [Fact]
    public void TypeChanges_CarryStartDeclarationsBetweenTheThreeStartsAndDropTheSignalElsewhere()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(startDiagram());
              // A plain start's variables become rows reading the payload member of the same name.
              const plain = getNode(1);
              model.initialEventId = 3;
              plain.variables.push({ id: 12, name: 'product', dataType: 'string', isArray: false, required: false, defaultValue: 'none', validation: null });
              const toSignal = changeNodeTypeFromInspector(plain, 'signalStartEvent');
              const fromPlain = copy(plain.signal);
              const plainKeptKey = copy(plain.businessKey);
              const plainHasVariables = Array.isArray(plain.variables) && plain.variables.length > 0;
              // And back: the rows become the plain start's variables.
              changeNodeTypeFromInspector(plain, 'startEvent');
              const backToPlain = plain.variables.map(variable => [variable.name, variable.dataType, !!variable.required]);
              const plainHasSignal = 'signal' in plain;

              // A message start's mappings cross to a signal start and back.
              const message = getNode(3);
              changeNodeTypeFromInspector(message, 'signalStartEvent');
              const fromMessage = copy(message.signal);
              const messageHasMessage = message.message != null;
              changeNodeTypeFromInspector(message, 'messageStartEvent');
              const backToMessage = copy(message.message.outputMappings);

              // Any other type loses the signal.
              const start = getNode(2);
              changeNodeTypeFromInspector(start, 'task');
              const taskHasSignal = 'signal' in start;

              // A signal catch keeps its name and not its mappings; a throw keeps its name and
              // not its scope or payload.
              const catchNode = { id: 7, name: 'Wait', type: 'intermediateSignalCatchEvent', laneId: 10, x: 400, y: 60,
                roles: [], variables: [],
                signal: { name: 'recalls.waited', outputMappings: [
                  { variable: 'note', path: 'note', dataType: 'string', isArray: false, required: false, defaultValue: null, validation: null }] } };
              model.flowNodes.push(catchNode);
              changeNodeTypeFromInspector(catchNode, 'signalStartEvent');
              const fromCatch = copy(catchNode.signal);
              const throwNode = getNode(5);
              model.sequenceFlows = model.sequenceFlows.filter(flow => flow.targetRef !== 5);
              changeNodeTypeFromInspector(throwNode, 'signalStartEvent');
              const fromThrow = copy(throwNode.signal);
              return JSON.stringify({
                toSignal, fromPlain, plainKeptKey, plainHasVariables, backToPlain, plainHasSignal,
                fromMessage, messageHasMessage, backToMessage, taskHasSignal, fromCatch, fromThrow
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("toSignal").GetBoolean());
        var fromPlain = root.GetProperty("fromPlain");
        Assert.Equal("", fromPlain.GetProperty("name").GetString());
        var rows = fromPlain.GetProperty("outputMappings").EnumerateArray().ToArray();
        Assert.Equal(["recallNumber", "product"], rows.Select(row => row.GetProperty("variable").GetString()));
        Assert.Equal(["recallNumber", "product"], rows.Select(row => row.GetProperty("path").GetString()));
        Assert.True(rows[0].GetProperty("required").GetBoolean());
        Assert.Equal("none", rows[1].GetProperty("defaultValue").GetString());
        Assert.Equal("recallNumber", root.GetProperty("plainKeptKey").GetProperty("variable").GetString());
        Assert.False(root.GetProperty("plainHasVariables").GetBoolean());
        Assert.Equal(
            [["recallNumber", "string", "True"], ["product", "string", "False"]],
            root.GetProperty("backToPlain").EnumerateArray()
                .Select(item => item.EnumerateArray().Select(part => part.ToString()).ToArray()));
        Assert.False(root.GetProperty("plainHasSignal").GetBoolean());

        var fromMessage = root.GetProperty("fromMessage");
        Assert.Equal("number", Assert.Single(fromMessage.GetProperty("outputMappings").EnumerateArray().ToArray()).GetProperty("path").GetString());
        Assert.False(root.GetProperty("messageHasMessage").GetBoolean());
        Assert.Equal("number", Assert.Single(root.GetProperty("backToMessage").EnumerateArray().ToArray()).GetProperty("path").GetString());
        Assert.False(root.GetProperty("taskHasSignal").GetBoolean());

        static string Shape(JsonElement signal) => string.Join(",",
            signal.EnumerateObject().Select(member => member.Name + "=" + member.Value.GetRawText()));
        Assert.Equal("name=\"recalls.waited\",outputMappings=[]", Shape(root.GetProperty("fromCatch")));
        Assert.Equal("name=\"recalls.archived\",outputMappings=[]", Shape(root.GetProperty("fromThrow")));
    }

    [Fact]
    public void Inspector_ReportsADuplicateNameAndASelfStartingThrowAsTheyAreTyped()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(startDiagram());
              select('node', 2);
              const region = controls().filter(item => item.className === 'signal-start-notes').slice(-1)[0];
              const clean = notes('signal-start-notes');
              const heading = controls().some(item => item.tagName === 'H2' && String(item.textContent).startsWith('Start variables from the payload'));
              const keySection = controls().some(item => item.tagName === 'H2' && String(item.textContent).startsWith('Domain business key'));

              // The end event throws the start's name to this workflow.
              select('node', 5);
              const endName = marked('data-signal-name').slice(-1)[0];
              endName.value = 'recalls.announced';
              endName.oninput();
              const scope = marked('data-signal-scope').slice(-1)[0];
              scope.value = 'thisWorkflow';
              scope.onchange();
              const throwLines = notes('signal-throw-notes');
              select('node', 2);
              const startLines = notes('signal-start-notes');

              // A second signal start on the name: both starts show the line that saving reports on #3.
              changeNodeTypeFromInspector(getNode(3), 'signalStartEvent');
              getNode(3).signal.name = 'recalls.announced';
              select('node', 3);
              const duplicateLines = notes('signal-start-notes');
              select('node', 2);
              const earlierLines = notes('signal-start-notes');

              // A name that contains "signal.name" still shows its duplicate line.
              getNode(2).signal.name = 'orders.signal.name-changed';
              getNode(3).signal.name = 'orders.signal.name-changed';
              select('node', 3);
              const dottedLines = notes('signal-start-notes');
              return JSON.stringify({
                role: region.attributes.role, live: region.attributes['aria-live'], id: region.id, clean, heading, keySection,
                throwLines, startLines, duplicateLines, earlierLines, dottedLines
              });
            })()
            """).AsString());

        var root = result.RootElement;
        string[] Lines(string property) => root.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(("status", "polite", "signal-start-status-2"),
            (root.GetProperty("role").GetString(), root.GetProperty("live").GetString(), root.GetProperty("id").GetString()));
        Assert.Empty(Lines("clean"));
        Assert.True(root.GetProperty("heading").GetBoolean());
        Assert.True(root.GetProperty("keySection").GetBoolean());
        const string selfStart =
            "Signal end event #5 throws 'recalls.announced' to a scope that includes this workflow, which starts on it at signal start event #2; every instance would start another.";
        Assert.Contains(selfStart, Lines("throwLines"));
        Assert.Equal([selfStart], Lines("startLines"));
        const string duplicate =
            "Signal start event #3 starts on 'recalls.announced', which signal start event #2 already starts on; a workflow starts once per signal.";
        Assert.Contains(duplicate, Lines("duplicateLines"));
        Assert.Contains(duplicate, Lines("earlierLines"));
        Assert.Contains(
            "Signal start event #3 starts on 'orders.signal.name-changed', which signal start event #2 already starts on; a workflow starts once per signal.",
            Lines("dottedLines"));
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
}
