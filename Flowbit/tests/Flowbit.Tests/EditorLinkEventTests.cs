using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.Shared.Models;
using Jint;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The editor's link events: its save validator reports the shared fixtures with the server's messages in the
/// server's order, its one pairing index agrees with <see cref="LinkEventRules"/>, and the canvas, trace, and
/// inspector follow a link in authoring and viewer modes.
/// </summary>
public sealed class EditorLinkEventTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string Diagram = """
        const linkDiagram = () => ({
          id: 'link-editor', name: 'Link editor', initialEventId: 1, variables: [],
          lanes: [{ id: 10, name: 'Team', x: 0, y: 0, w: 1400, h: 400 }],
          flowNodes: [
            { id: 1, name: 'Start', type: 'startEvent', laneId: 10, x: 40, y: 100 },
            { id: 2, name: 'Review', type: 'userTask', laneId: 10, x: 160, y: 90 },
            { id: 3, name: 'Fulfilment', type: 'intermediateLinkThrowEvent', laneId: 10, x: 420, y: 100 },
            { id: 4, name: 'Fulfilment', type: 'intermediateLinkCatchEvent', laneId: 10, x: 40, y: 280 },
            { id: 5, name: 'Done', type: 'endEvent', laneId: 10, x: 300, y: 280 },
            { id: 6, name: 'Rework', type: 'userTask', laneId: 10, x: 600, y: 90 },
            { id: 7, name: 'Other', type: 'intermediateLinkThrowEvent', laneId: 10, x: 860, y: 100 },
            { id: 8, name: 'Fulfilment', type: 'intermediateLinkThrowEvent', laneId: 10, x: 860, y: 280 }
          ],
          sequenceFlows: [
            { id: 101, name: '', sourceRef: 1, targetRef: 2 },
            { id: 201, name: 'Send', sourceRef: 2, targetRef: 3 },
            { id: 202, name: 'Rework', sourceRef: 2, targetRef: 6 },
            { id: 401, name: '', sourceRef: 4, targetRef: 5 },
            { id: 601, name: 'Park', sourceRef: 6, targetRef: 7 },
            { id: 602, name: 'Done', sourceRef: 6, targetRef: 8 }
          ]
        });
        const latestNodeGroups = () => nodesG.children.slice(-model.flowNodes.length);
        const nodeGroup = id => latestNodeGroups().find(group => group.attributes['data-id'] === String(id));
        const classOf = id => String(nodeGroup(id).attributes.class);
        const descendants = element => [element, ...(element.children || []).flatMap(descendants)];
        const textOf = element => descendants(element).map(item => item.textContent || '').join(' ');
        """;

    public static TheoryData<string> CaseNames() => new(LinkEventFixtures.Cases().Select(item => item.Case));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void SaveValidation_ReportsEachFixtureWithTheServersMessagesInOrder(string name)
    {
        var (_, model, problems) = LinkEventFixtures.Cases().Single(item => item.Case == name);
        var engine = CreateValidatorEngine();
        engine.SetValue("candidateJson", JsonSerializer.Serialize(model, Web));

        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const candidate = JSON.parse(candidateJson);
              const ctx = createSaveValidationContext(candidate);
              validateSaveLinkEvents(ctx);
              return JSON.stringify({
                phase: ctx.errors,
                all: validateModelForSave(candidate),
                edges: linkEventIndex(candidate).edges
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(problems, root.GetProperty("phase").EnumerateArray().Select(item => item.GetString()!));
        var all = root.GetProperty("all").EnumerateArray().Select(item => item.GetString()!).ToList();
        Assert.Equal(problems, all.Where(problems.Contains).Distinct());
        Assert.Equal(
            LinkEventRules.ImplicitEdges(model).Select(edge => (edge.ThrowId, edge.CatchId)),
            root.GetProperty("edges").EnumerateArray()
                .Select(edge => (edge.GetProperty("throwId").GetInt32(), edge.GetProperty("catchId").GetInt32())));
    }

    [Fact]
    public void SaveValidation_FollowsALinkForScopedInterruptReachability()
    {
        var engine = CreateValidatorEngine();
        engine.SetValue("candidateJson", """
            {
              "id": "links", "name": "Links", "initialEventId": 1, "variables": [], "lanes": [],
              "flowNodes": [
                { "id": 1, "name": "Start", "type": "startEvent" },
                { "id": 2, "name": "Fork", "type": "parallelGateway" },
                { "id": 3, "name": "Work", "type": "userTask" },
                { "id": 4, "name": "Other", "type": "userTask" },
                { "id": 5, "name": "Stop", "type": "intermediateLinkThrowEvent" },
                { "id": 6, "name": "Stop", "type": "intermediateLinkCatchEvent" },
                { "id": 7, "name": "Interrupt", "type": "scopedInterruptEvent", "gatewayRef": 2 },
                { "id": 8, "name": "End", "type": "endEvent" },
                { "id": 9, "name": "End", "type": "endEvent" }
              ],
              "sequenceFlows": [
                { "id": 101, "sourceRef": 1, "targetRef": 2 }, { "id": 201, "sourceRef": 2, "targetRef": 3 },
                { "id": 202, "sourceRef": 2, "targetRef": 4 }, { "id": 301, "sourceRef": 3, "targetRef": 8 },
                { "id": 401, "sourceRef": 4, "targetRef": 5 }, { "id": 601, "sourceRef": 6, "targetRef": 7 },
                { "id": 701, "sourceRef": 7, "targetRef": 9 }
              ]
            }
            """);

        var errors = JsonSerializer.Deserialize<string[]>(engine.Evaluate(
            "JSON.stringify(validateModelForSave(JSON.parse(candidateJson)))").AsString())!;

        Assert.DoesNotContain(errors, error => error.Contains("structurally reachable", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("Link ", StringComparison.Ordinal));
    }

    [Fact]
    public void Connections_TypeChangesAndInvariantsKeepTheLinkTopology()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const alerts = [];
              alert = message => alerts.push(String(message));
              loadFromObject(linkDiagram());
              const fromThrow = connectionSourceProblem(getNode(3));
              const intoCatch = connectionTargetProblem(getNode(4));
              const catchOut = connectionSourceProblem(getNode(4));
              const rejected = changeNodeTypeFromInspector(getNode(2), 'intermediateLinkThrowEvent');
              const typeAfterReject = getNode(2).type;

              const rework = getNode(6);
              rework.roles = ['Clerk'];
              const accepted = changeNodeTypeFromInspector(rework, 'intermediateLinkCatchEvent');
              const catchFlows = outgoingFlows(6).map(flow => flow.id);
              const incomingKept = incomingFlows(6).map(flow => flow.id);

              const throwNode = getNode(3);
              throwNode.variables = [{ id: 1, name: 'x', dataType: 'string' }];
              throwNode.script = 'x';
              applyTypeInvariants(throwNode);
              getFlow(401).condition = 'true';
              getFlow(401).isDefault = true;
              applyFlowInvariants(getFlow(401));
              return JSON.stringify({
                fromThrow, intoCatch, catchOut, rejected, typeAfterReject, alerts, accepted, catchFlows, incomingKept,
                roles: rework.roles, throwVariables: throwNode.variables, throwScript: throwNode.script ?? null,
                flowCondition: getFlow(401).condition, flowDefault: getFlow(401).isDefault,
                typeLabels: [typeLabel('intermediateLinkThrowEvent'), typeLabel('intermediateLinkCatchEvent')],
                single: [isSingleOutgoingType('intermediateLinkCatchEvent'), isSingleOutgoingType('intermediateLinkThrowEvent')],
                passThrough: [isPassThroughType('intermediateLinkCatchEvent'), isPassThroughType('intermediateLinkThrowEvent')],
                size: nodeSize(getNode(3)),
                search: searchDiagram(model, 'link catch').map(result => result.id)
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("A link throw event cannot have an outgoing sequence flow.", root.GetProperty("fromThrow").GetString());
        Assert.Equal("A link catch event cannot have an incoming sequence flow.", root.GetProperty("intoCatch").GetString());
        Assert.Equal("This node type can have exactly one outgoing flow.", root.GetProperty("catchOut").GetString());
        Assert.False(root.GetProperty("rejected").GetBoolean());
        Assert.Equal("userTask", root.GetProperty("typeAfterReject").GetString());
        Assert.Equal(["Remove all outgoing sequence flows before changing this node to a link throw event."],
            root.GetProperty("alerts").EnumerateArray().Select(item => item.GetString()));
        Assert.True(root.GetProperty("accepted").GetBoolean());
        Assert.Equal([601], root.GetProperty("catchFlows").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Equal([202], root.GetProperty("incomingKept").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Empty(root.GetProperty("roles").EnumerateArray());
        Assert.Empty(root.GetProperty("throwVariables").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("throwScript").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("flowCondition").ValueKind);
        Assert.False(root.GetProperty("flowDefault").GetBoolean());
        Assert.Equal(["Link Throw Event (go to)", "Link Catch Event (continue here)"],
            root.GetProperty("typeLabels").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal([true, false], root.GetProperty("single").EnumerateArray().Select(item => item.GetBoolean()));
        Assert.Equal([true, false], root.GetProperty("passThrough").EnumerateArray().Select(item => item.GetBoolean()));
        Assert.Equal(64, root.GetProperty("size").GetProperty("w").GetInt32());
        Assert.Equal([4, 6], root.GetProperty("search").EnumerateArray().Select(item => item.GetInt32()));
    }

    [Fact]
    public void Trace_FollowsALinkAndShowsPartnersAsContext()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(linkDiagram());
              const edges = linkEventIndex(model).edges;
              const trace = (nodeId, mode) => buildDiagramTraceContext(model, { kind: 'node', nodeId }, mode, edges);
              select('node', 3);
              return JSON.stringify({
                edges,
                downstream: trace(3, 'downstream'),
                upstream: trace(4, 'upstream'),
                local: trace(3, 'local'),
                catchLocal: trace(4, 'local'),
                withoutLinks: buildDiagramTraceContext(model, { kind: 'node', nodeId: 3 }, 'downstream'),
                active: activeTraceContext
              });
            })()
            """).AsString());

        var root = result.RootElement;
        int[] Ids(string path, string property) =>
            root.GetProperty(path).GetProperty(property).EnumerateArray().Select(item => item.GetInt32()).ToArray();
        Assert.Equal([3, 4, 5], Ids("downstream", "nodeIds"));
        Assert.Equal([401], Ids("downstream", "flowIds"));
        Assert.Equal([1, 2, 3, 4, 6, 8], Ids("upstream", "nodeIds"));
        Assert.Equal([2, 3], Ids("local", "nodeIds"));
        Assert.Equal([4], Ids("local", "contextNodeIds"));
        Assert.Equal([3, 8], Ids("catchLocal", "contextNodeIds"));
        Assert.Equal([3], Ids("withoutLinks", "nodeIds"));
        Assert.Equal([4], Ids("active", "contextNodeIds"));
    }

    [Fact]
    public void NavigationBlock_TakesTheLinksAsDataAndNeverPairsNames()
    {
        var html = EditorSource.Read();
        var navigation = Regex.Match(html,
            @"// BEGIN DIAGRAM NAVIGATION HELPERS(?<code>[\s\S]*?)// END DIAGRAM NAVIGATION HELPERS").Groups["code"].Value;
        Assert.DoesNotContain("linkEventIndex", navigation, StringComparison.Ordinal);
        var engine = new Engine();
        engine.Execute(navigation);

        var nodeIds = engine.Evaluate("""
            JSON.stringify(buildDiagramTraceContext({
              flowNodes: [{ id: 1 }, { id: 2 }, { id: 3 }],
              sequenceFlows: [{ id: 101, sourceRef: 1, targetRef: 2 }]
            }, { kind: 'node', nodeId: 1 }, 'downstream', [{ throwId: 2, catchId: 3 }]).nodeIds)
            """).AsString();

        Assert.Equal("[1,2,3]", nodeIds);
    }

    [Theory]
    [InlineData("standalone")]
    [InlineData("viewer")]
    public void Canvas_RingsThePartnersOfTheSelectedLinkEvent(string mode)
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: mode);
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(linkDiagram());
              const ringed = () => model.flowNodes.map(node => node.id)
                .filter(id => classOf(id).includes('link-partner'));
              const hasRing = id => descendants(nodeGroup(id)).some(item => item.attributes?.class === 'link-partner-ring');
              select('node', 3);
              const forThrow = ringed();
              const throwRing = hasRing(4);
              const throwTitle = textOf(nodeGroup(3));
              select('node', 4);
              const forCatch = ringed();
              const catchTitle = textOf(nodeGroup(4));
              select('node', 7);
              const forUnresolved = ringed();
              const unresolvedTitle = textOf(nodeGroup(7));
              select('node', 2);
              const forTask = ringed();
              return JSON.stringify({ forThrow, throwRing, throwTitle, forCatch, catchTitle, forUnresolved, unresolvedTitle, forTask });
            })()
            """).AsString());

        var root = result.RootElement;
        int[] Ids(string property) => root.GetProperty(property).EnumerateArray().Select(item => item.GetInt32()).ToArray();
        Assert.Equal([4], Ids("forThrow"));
        Assert.True(root.GetProperty("throwRing").GetBoolean());
        Assert.Contains("Continues at #4", root.GetProperty("throwTitle").GetString());
        Assert.Equal([3, 8], Ids("forCatch"));
        Assert.Contains("Continued from #3, #8", root.GetProperty("catchTitle").GetString());
        Assert.Empty(Ids("forUnresolved"));
        Assert.Contains("No link catch event has this name", root.GetProperty("unresolvedTitle").GetString());
        Assert.Empty(Ids("forTask"));
    }

    [Fact]
    public void Inspector_ShowsPartnersPairsByChoiceAndNavigates()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(linkDiagram());
              const latest = (predicate) => descendants(inspector).filter(predicate).slice(-1)[0];
              const statusText = () => latest(item => String(item.className || '').includes('link-status'))?.textContent;
              const button = text => latest(item => item.tagName === 'BUTTON' && item.textContent === text);
              const throwSelect = () => latest(item => item.tagName === 'SELECT' &&
                (item.children || []).some(option => option.textContent === '— choose a link catch event —'));
              const headings = () => descendants(inspector).filter(item => item.tagName === 'H2').map(item => item.textContent);

              select('node', 3);
              const throwStatus = statusText();
              const throwValue = throwSelect().value;
              const throwOptions = throwSelect().children.map(option => [option.value, option.textContent]);
              const outgoingShown = headings().slice(-4).includes('Outgoing flows');
              button('Go to catch').onclick();
              const afterGoTo = selected.nodeId;

              const catchStatus = statusText();
              const throwButtons = descendants(inspector).filter(item => item.tagName === 'BUTTON' &&
                String(item.className) === 'link-partner-button').slice(-2).map(item => item.textContent);
              button('#8 · Fulfilment').onclick();
              const afterThrowButton = selected.nodeId;

              select('node', 7);
              const unresolvedStatus = statusText();
              const unresolvedGoTo = button('Go to catch').disabled;
              const choose = throwSelect();
              choose.value = '4';
              choose.onchange();
              const chosenName = getNode(7).name;
              const chosenStatus = statusText();

              getNode(7).name = 'fulfilment';
              select('node', 7);
              const caseStatus = statusText();
              return JSON.stringify({ throwStatus, throwValue, throwOptions, outgoingShown, afterGoTo, catchStatus,
                throwButtons, afterThrowButton, unresolvedStatus, unresolvedGoTo, chosenName, chosenStatus, caseStatus });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("Tokens continue at link catch event #4 ('Fulfilment').", root.GetProperty("throwStatus").GetString());
        Assert.Equal("4", root.GetProperty("throwValue").GetString());
        Assert.Equal(2, root.GetProperty("throwOptions").GetArrayLength());
        Assert.Equal("Fulfilment · #4", root.GetProperty("throwOptions")[1][1].GetString());
        Assert.False(root.GetProperty("outgoingShown").GetBoolean());
        Assert.Equal(4, root.GetProperty("afterGoTo").GetInt32());
        Assert.Equal("Tokens continue here from 2 link throw events:", root.GetProperty("catchStatus").GetString());
        Assert.Equal(["#3 · Fulfilment", "#8 · Fulfilment"],
            root.GetProperty("throwButtons").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(8, root.GetProperty("afterThrowButton").GetInt32());
        Assert.Equal("No link catch event is named 'Other'. Choose one above, or give a link catch event this name.",
            root.GetProperty("unresolvedStatus").GetString());
        Assert.True(root.GetProperty("unresolvedGoTo").GetBoolean());
        Assert.Equal("Fulfilment", root.GetProperty("chosenName").GetString());
        Assert.Equal("Tokens continue at link catch event #4 ('Fulfilment').", root.GetProperty("chosenStatus").GetString());
        Assert.Equal("No link catch event is named 'fulfilment'. Link catch event #4 is named 'Fulfilment'; names must match exactly, including letter case.",
            root.GetProperty("caseStatus").GetString());
    }

    [Fact]
    public void Inspector_KeepsItsListStatusAndNamesTrueInEdgeStates()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const diagram = linkDiagram();
              diagram.flowNodes.push(
                { id: 9, name: 'Fulfilment', type: 'intermediateLinkCatchEvent', laneId: 10, x: 40, y: 340 },
                { id: 11, name: '  ', type: 'intermediateLinkCatchEvent', laneId: 10, x: 600, y: 340 });
              loadFromObject(diagram);
              const all = predicate => descendants(inspector).filter(predicate);
              const latest = predicate => all(predicate).slice(-1)[0];
              const notesRegion = () => latest(item => String(item.className || '') === 'link-notes');
              const notes = () => (notesRegion().children || []).map(item => item.textContent);
              const button = text => latest(item => item.tagName === 'BUTTON' && item.textContent === text);
              const throwSelect = () => latest(item => item.tagName === 'SELECT' &&
                (item.children || []).some(option => option.textContent === '— choose a link catch event —'));
              const nameInput = value => latest(item => item.tagName === 'INPUT' && item.value === value);

              // A paired throw: the list keeps its value when the placeholder or an unnamed catch is picked.
              select('node', 3);
              const region = { role: notesRegion().attributes?.role, live: notesRegion().attributes?.['aria-live'] };
              const pairedNotes = notes();
              const goToLabel = button('Go to catch').attributes?.['aria-label'];
              const unnamedOption = throwSelect().children.find(option => option.value === '11');
              const unnamed = { text: unnamedOption.textContent, disabled: unnamedOption.disabled === true };
              let list = throwSelect();
              list.value = '';
              list.onchange();
              const afterPlaceholder = { value: list.value, name: getNode(3).name };
              list.value = '11';
              list.onchange();
              const afterUnnamed = { value: list.value, name: getNode(3).name };

              // The second catch of a shared name says where its name's throws continue, as save validation counts them.
              select('node', 9);
              const duplicateNotes = notes();
              const duplicateTitle = textOf(nodeGroup(9));
              select('node', 4);
              const firstNotes = notes();
              const throwLabels = all(item => item.tagName === 'BUTTON' && String(item.className) === 'link-partner-button')
                .slice(-2).map(item => item.attributes?.['aria-label']);

              // Typing a name that pairs a throw refreshes the trace as well as the ring.
              select('node', 7);
              const before = classOf(4);
              const input = nameInput('Other');
              input.value = 'Fulfilment';
              input.oninput();
              const after = classOf(4);
              return JSON.stringify({ region, pairedNotes, goToLabel, unnamed, afterPlaceholder, afterUnnamed,
                duplicateNotes, duplicateTitle, firstNotes, throwLabels, before, after });
            })()
            """).AsString());

        var root = result.RootElement;
        string[] Texts(string property) => root.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal("status", root.GetProperty("region").GetProperty("role").GetString());
        Assert.Equal("polite", root.GetProperty("region").GetProperty("live").GetString());
        Assert.Equal(
            ["Tokens continue at link catch event #4 ('Fulfilment').",
             "Link catch event #9 is also named 'Fulfilment'; link catch names must be unique, ignoring letter case."],
            Texts("pairedNotes"));
        Assert.Equal("Go to catch #4", root.GetProperty("goToLabel").GetString());
        Assert.Equal("(unnamed) · #11", root.GetProperty("unnamed").GetProperty("text").GetString());
        Assert.True(root.GetProperty("unnamed").GetProperty("disabled").GetBoolean());
        Assert.Equal("4", root.GetProperty("afterPlaceholder").GetProperty("value").GetString());
        Assert.Equal("Fulfilment", root.GetProperty("afterPlaceholder").GetProperty("name").GetString());
        Assert.Equal("4", root.GetProperty("afterUnnamed").GetProperty("value").GetString());
        Assert.Equal("Fulfilment", root.GetProperty("afterUnnamed").GetProperty("name").GetString());
        Assert.Equal(
            ["Link catch event #4 is also named 'Fulfilment'; link catch names must be unique, ignoring letter case.",
             "Link throw events named 'Fulfilment' continue at link catch event #4, the first with this name."],
            Texts("duplicateNotes"));
        Assert.Contains("Link throw events with this name continue at #4", root.GetProperty("duplicateTitle").GetString());
        Assert.Equal(
            ["Link catch event #9 is also named 'Fulfilment'; link catch names must be unique, ignoring letter case.",
             "Tokens continue here from 2 link throw events:"],
            Texts("firstNotes"));
        Assert.Equal(["Go to link throw event #3 · Fulfilment", "Go to link throw event #8 · Fulfilment"], Texts("throwLabels"));
        Assert.Contains("trace-muted", root.GetProperty("before").GetString());
        Assert.DoesNotContain("trace-muted", root.GetProperty("after").GetString());
        Assert.Contains("link-partner", root.GetProperty("after").GetString());
    }

    [Fact]
    public void Loader_RoundTripsLinkEventsWithTheirNames()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        var exported = engine.Evaluate(Diagram + """
            (() => {
              const diagram = linkDiagram();
              diagram.flowNodes.find(node => node.id === 3).name = ' Fulfilment ';
              loadFromObject(diagram);
              const first = JSON.stringify(model);
              loadFromObject(JSON.parse(first));
              return JSON.stringify({ same: first === JSON.stringify(model), model: JSON.parse(first) });
            })()
            """).AsString();

        using var result = JsonDocument.Parse(exported);
        Assert.True(result.RootElement.GetProperty("same").GetBoolean());
        var nodes = result.RootElement.GetProperty("model").GetProperty("flowNodes").EnumerateArray().ToArray();
        var throwNode = nodes.Single(node => node.GetProperty("id").GetInt32() == 3);
        Assert.Equal("intermediateLinkThrowEvent", throwNode.GetProperty("type").GetString());
        Assert.Equal(" Fulfilment ", throwNode.GetProperty("name").GetString());
        Assert.False(throwNode.TryGetProperty("gatewayRef", out _));
        Assert.False(throwNode.TryGetProperty("asyncBefore", out _));
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
