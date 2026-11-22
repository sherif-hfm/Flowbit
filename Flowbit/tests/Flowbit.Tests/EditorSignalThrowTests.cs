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
/// The editor's signal throw and end events: its save validator reports the shared <see cref="SignalThrowFixtures"/>
/// with the server's first message and names what the server's binding refuses, and the two types' shapes, loader,
/// export, type changes, invariants, and inspector follow the plan.
/// </summary>
public sealed class EditorSignalThrowTests
{
    private const string Diagram = """
        // User task #2 leads to signal throw #7 (to this instance, with a required payload entry), signal catch #9,
        // and signal end #12 (to a named family). Process variables: cancelReason (string), amount (number), and
        // the read-only shared alias region (string); start #1 declares the string input orderNumber.
        const throwDiagram = () => ({
          id: 'signal-throw-editor', name: 'Signal throw editor', initialEventId: 1,
          variables: [
            { id: 1, name: 'cancelReason', dataType: 'string', isArray: false, nullable: true, required: false, defaultValue: null, validation: null },
            { id: 2, name: 'amount', dataType: 'number', isArray: false, nullable: true, required: false, defaultValue: null, validation: null },
            { id: 3, name: 'region', dataType: 'string', isArray: false, nullable: false, required: false, defaultValue: null,
              validation: null, scope: 'shared', sharedKey: 'examples.signals.region', access: 'read' }
          ],
          lanes: [{ id: 10, name: 'Team', x: 0, y: 0, w: 1400, h: 440 }],
          flowNodes: [
            { id: 1, name: 'Start', type: 'startEvent', laneId: 10, x: 40, y: 100,
              variables: [{ id: 11, name: 'orderNumber', dataType: 'string', isArray: false, required: false }] },
            { id: 2, name: 'Review', type: 'userTask', laneId: 10, x: 160, y: 90 },
            { id: 7, name: 'Announce the cancellation', type: 'intermediateSignalThrowEvent', laneId: 10, x: 420, y: 100,
              signal: { name: 'orders.cancelled', scope: { kind: 'thisInstance' },
                payload: [{ name: 'reason', variable: 'cancelReason', required: true }] } },
            { id: 8, name: 'Cancelled', type: 'endEvent', laneId: 10, x: 620, y: 100 },
            { id: 9, name: 'Wait for the cancellation', type: 'intermediateSignalCatchEvent', laneId: 10, x: 420, y: 220,
              signal: { name: 'orders.cancelled', outputMappings: [] } },
            { id: 10, name: 'Stopped', type: 'endEvent', laneId: 10, x: 620, y: 220 },
            { id: 12, name: 'Recall announced', type: 'signalEndEvent', laneId: 10, x: 420, y: 330,
              signal: { name: 'warehouse.product-recall',
                scope: { kind: 'workflow', workflowKey: 'example-signal-boundaries-recall' } } }
          ],
          sequenceFlows: [
            { id: 101, name: '', sourceRef: 1, targetRef: 2 },
            { id: 201, name: 'Cancel', sourceRef: 2, targetRef: 7 },
            { id: 202, name: 'Wait', sourceRef: 2, targetRef: 9 },
            { id: 203, name: 'Recall', sourceRef: 2, targetRef: 12 },
            { id: 701, name: '', sourceRef: 7, targetRef: 8 },
            { id: 901, name: '', sourceRef: 9, targetRef: 10 }
          ]
        });
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
        const controls = () => within(latestInspector());
        const marked = marker => controls().filter(item => item.attributes?.[marker] !== undefined);
        const button = text => controls().filter(item => item.tagName === 'BUTTON' && item.textContent === text).slice(-1)[0];
        const statusLines = () => controls().filter(item => item.className === 'signal-throw-notes').slice(-1)[0]
          .children.map(child => child.textContent);
        const copy = value => JSON.parse(JSON.stringify(value));
        """;

    public static TheoryData<string> CaseNames() => new(SignalThrowFixtures.Cases().Select(item => item.Case));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void SaveValidation_ReportsEachFixtureWithTheServersFirstMessage(string name)
    {
        var (_, json, problem) = SignalThrowFixtures.Cases().Single(item => item.Case == name);

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

    [Fact]
    public void PayloadNamePattern_IsTheServersPattern()
    {
        var match = Regex.Match(EditorSource.Read(), @"const SIGNAL_PAYLOAD_NAME_PATTERN = /(?<pattern>[^\r\n]+)/;");
        Assert.True(match.Success, "SIGNAL_PAYLOAD_NAME_PATTERN was not found in the editor.");
        Assert.Equal(SignalEventRules.PayloadNamePattern, match.Groups["pattern"].Value);
    }

    [Fact]
    public void SaveValidation_NamesWhatTheServersBindingRefuses()
    {
        (string[] Editor, string? Server) Errors(Action<JsonObject> change)
        {
            var json = SignalThrowFixtures.Json(change);
            return ([.. EditorErrors(json)], ServerError(json));
        }

        void AssertRefused(string[] expected, Action<JsonObject> change)
        {
            var (editor, server) = Errors(change);
            Assert.Equal(expected, editor);
            Assert.NotNull(server);
        }

        AssertRefused(
            ["Signal throw event #7 signal.scope has an unknown member 'businesKeyVariable'."],
            model => SignalThrowFixtures.Scope(model, 7, new JsonObject
            {
                ["kind"] = "thisInstance", ["businesKeyVariable"] = "orderNumber"
            }));
        // The binding problem comes first; the rules still report what they find.
        AssertRefused(
            ["Signal throw event #7 payload entry 2 has an unknown member 'sorce'.",
             "Signal throw event #7 payload entry 'sender' must define either variable or source."],
            model => SignalThrowFixtures.Payload(model, 7).Add(new JsonObject { ["name"] = "sender", ["sorce"] = "instanceId" }));
        AssertRefused(
            ["Signal throw event #7 signal must be an object with a name, a scope, and an optional payload."],
            model => SignalThrowFixtures.Node(model, 7)["signal"] = "orders.cancelled");
        AssertRefused(
            ["Signal end event #12 signal.scope must be an object with a kind."],
            model => SignalThrowFixtures.Signal(model, 12)["scope"] = "allWorkflows");
        AssertRefused(
            ["Signal throw event #7 signal.payload must be a list."],
            model => SignalThrowFixtures.Signal(model, 7)["payload"] = new JsonObject { ["reason"] = "cancelReason" });
        AssertRefused(
            ["Signal throw event #7 payload entry 2 must be an object with a name and a variable or source."],
            model => SignalThrowFixtures.Payload(model, 7).Add("cancelReason"));
        AssertRefused(
            ["Signal end event #12 signal.scope.workflowKey must be text."],
            model => SignalThrowFixtures.Scope(model, 12, new JsonObject { ["kind"] = "workflow", ["workflowKey"] = 42 }));
        AssertRefused(
            ["Signal throw event #7 signal.scope.businessKeyVariable must be text."],
            model => SignalThrowFixtures.Scope(model, 7, new JsonObject
            {
                ["kind"] = "businessKey", ["workflowKey"] = "shipping", ["businessKeyVariable"] = true
            }));
        AssertRefused(
            ["Signal throw event #7 payload entry 1 required must be true or false."],
            model => SignalThrowFixtures.Payload(model, 7)[0]!["required"] = "yes");
        AssertRefused(
            ["Signal throw event #7 payload entry 1 variable must be text."],
            model => SignalThrowFixtures.Payload(model, 7)[0]!["variable"] = 3);
        AssertRefused(
            ["Signal throw event #7 signal.outputMappings must be a list."],
            model => SignalThrowFixtures.Signal(model, 7)["outputMappings"] = "amount");

        // Every problem of one throw, in the server's order after the binding problems.
        Assert.Equal(
            ["Signal throw event #7 signal.scope has an unknown member 'extra'.",
             "Signal throw event #7 signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.",
             "Signal throw event #7 signal.scope needs a workflowKey for kind 'businessKey'.",
             "Signal throw event #7 signal.scope needs a businessKeyVariable for kind 'businessKey'; use kind 'ownBusinessKey' for this instance's own business key.",
             "Signal throw event #7 payload sends 'REASON' more than once."],
            Errors(model =>
            {
                SignalThrowFixtures.Signal(model, 7)["name"] = "Orders";
                SignalThrowFixtures.Scope(model, 7, new JsonObject { ["kind"] = "businessKey", ["extra"] = 1 });
                SignalThrowFixtures.Payload(model, 7).Add(new JsonObject { ["name"] = "REASON", ["source"] = "instanceId" });
            }).Editor);
    }

    [Fact]
    public void Registry_DrawsBothEventsAndListsThemWhereThePlanSays()
    {
        var html = EditorSource.Read();
        Assert.Contains("<symbol id=\"icon-signal-throw\"", html, StringComparison.Ordinal);
        // Only the throw's own symbol counts: a lazy match across the template could find another's fill.
        var symbol = System.Text.RegularExpressions.Regex.Match(
            html, "<symbol id=\"icon-signal-throw\"[^>]*>([\\s\\S]*?)</symbol>");
        Assert.True(symbol.Success);
        Assert.Contains("fill=\"currentColor\"", symbol.Groups[1].Value, StringComparison.Ordinal);
        var catchSymbol = System.Text.RegularExpressions.Regex.Match(
            html, "<symbol id=\"icon-signal-catch\"[^>]*>([\\s\\S]*?)</symbol>");
        Assert.True(catchSymbol.Success);
        Assert.DoesNotContain("fill=\"currentColor\"", catchSymbol.Groups[1].Value, StringComparison.Ordinal);
        foreach (var rule in new[]
                 {
                     ".node.intermediateSignalThrowEvent circle.body", ".node.intermediateSignalThrowEvent circle.ring",
                     ".node.selected.intermediateSignalThrowEvent circle.ring", ".node.intermediateSignalThrowEvent .node-icon",
                     ".node.signalEndEvent circle.body", ".node.selected.signalEndEvent circle.body",
                     ".node.signalEndEvent .node-icon"
                 })
        {
            Assert.Contains(rule, html, StringComparison.Ordinal);
        }
        Assert.Matches(@"\.node\.signalEndEvent circle\.body \{[^}]*stroke-width: 5;", html);

        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(throwDiagram());
              render();
              const index = type => NODE_TYPE_OPTIONS.findIndex(option => option.value === type);
              const shape = id => {
                const parts = descendants(nodeGroup(id));
                return {
                  classes: String(nodeGroup(id).attributes.class),
                  rings: parts.filter(part => part.attributes?.class === 'ring').length,
                  icon: parts.find(part => String(part.attributes?.class) === 'node-icon')?.attributes.href
                };
              };
              const predicates = type => ({
                event: isEventType(type), end: isEndEventType(type), single: isSingleOutgoingType(type),
                passThrough: isPassThroughType(type), wait: isSignalWaitType(type), throws: throwsSignalType(type),
                signal: isSignalEventType(type), size: nodeSize({ type }).w
              });
              return JSON.stringify({
                order: {
                  catchAfterConditional: index('intermediateSignalCatchEvent') - index('intermediateConditionalCatchEvent'),
                  throwAfterCatch: index('intermediateSignalThrowEvent') - index('intermediateSignalCatchEvent'),
                  endAfterErrorEnd: index('signalEndEvent') - index('errorEndEvent')
                },
                labels: [typeLabel('intermediateSignalThrowEvent'), typeLabel('signalEndEvent')],
                missingIcons: NODE_TYPE_OPTIONS.filter(option =>
                  !Object.prototype.hasOwnProperty.call(NODE_ICON_SYMBOL, option.value)).map(option => option.value),
                throwPredicates: predicates('intermediateSignalThrowEvent'),
                endPredicates: predicates('signalEndEvent'),
                waitIsSignalEvent: [isSignalEventType('intermediateSignalCatchEvent'), isSignalEventType('signalBoundaryEvent')],
                throwShape: shape(7), endShape: shape(12),
                flowLabel: flowLabel(model.sequenceFlows.find(flow => flow.id === 701)),
                throwSearch: searchDiagram(model, 'signal throw').map(item => item.id),
                throwDetail: searchDiagram(model, 'signal throw')[0].detail,
                endSearch: searchDiagram(model, 'signal end').map(item => item.id),
                endOutgoing: connectionSourceProblem(getNode(12)),
                secondOutgoing: connectionSourceProblem(getNode(7)),
                incoming: connectionTargetProblem(getNode(7))
              });
            })()
            """).AsString());

        var root = result.RootElement;
        var order = root.GetProperty("order");
        Assert.Equal(1, order.GetProperty("catchAfterConditional").GetInt32());
        Assert.Equal(1, order.GetProperty("throwAfterCatch").GetInt32());
        Assert.Equal(1, order.GetProperty("endAfterErrorEnd").GetInt32());
        Assert.Equal(
            ["Signal Throw Event (broadcast a signal)", "Signal End Event (end and broadcast a signal)"],
            root.GetProperty("labels").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(root.GetProperty("missingIcons").EnumerateArray());

        var throwPredicates = root.GetProperty("throwPredicates");
        Assert.True(throwPredicates.GetProperty("event").GetBoolean());
        Assert.False(throwPredicates.GetProperty("end").GetBoolean());
        Assert.True(throwPredicates.GetProperty("single").GetBoolean());
        Assert.True(throwPredicates.GetProperty("passThrough").GetBoolean());
        Assert.False(throwPredicates.GetProperty("wait").GetBoolean());
        Assert.True(throwPredicates.GetProperty("throws").GetBoolean());
        Assert.True(throwPredicates.GetProperty("signal").GetBoolean());
        Assert.Equal(64, throwPredicates.GetProperty("size").GetInt32());
        var endPredicates = root.GetProperty("endPredicates");
        Assert.True(endPredicates.GetProperty("event").GetBoolean());
        Assert.True(endPredicates.GetProperty("end").GetBoolean());
        Assert.False(endPredicates.GetProperty("single").GetBoolean());
        Assert.False(endPredicates.GetProperty("passThrough").GetBoolean());
        Assert.False(endPredicates.GetProperty("wait").GetBoolean());
        Assert.True(endPredicates.GetProperty("throws").GetBoolean());
        Assert.True(endPredicates.GetProperty("signal").GetBoolean());
        Assert.Equal([true, true], root.GetProperty("waitIsSignalEvent").EnumerateArray().Select(item => item.GetBoolean()));

        var throwShape = root.GetProperty("throwShape");
        Assert.Contains("intermediateSignalThrowEvent", throwShape.GetProperty("classes").GetString());
        Assert.Equal(1, throwShape.GetProperty("rings").GetInt32());
        Assert.Equal("#icon-signal-throw", throwShape.GetProperty("icon").GetString());
        var endShape = root.GetProperty("endShape");
        Assert.Contains("signalEndEvent", endShape.GetProperty("classes").GetString());
        Assert.Equal(0, endShape.GetProperty("rings").GetInt32());
        Assert.Equal("#icon-signal-throw", endShape.GetProperty("icon").GetString());

        // The throw's flow shows only a name its author gives it.
        Assert.Equal("", root.GetProperty("flowLabel").GetString());
        Assert.Equal([7], root.GetProperty("throwSearch").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Contains("Signal Throw Event", root.GetProperty("throwDetail").GetString());
        Assert.Equal([12], root.GetProperty("endSearch").EnumerateArray().Select(item => item.GetInt32()));
        Assert.Equal("An end event cannot have an outgoing sequence flow.", root.GetProperty("endOutgoing").GetString());
        Assert.Equal("This node type can have exactly one outgoing flow.", root.GetProperty("secondOutgoing").GetString());
        Assert.Equal("", root.GetProperty("incoming").GetString());
    }

    [Fact]
    public void Viewer_DrawsBothShapes()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "viewer");
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(throwDiagram());
              setInstanceOverlay({ nodes: [{ id: 12, state: 'ended', label: 'Ended here' }] });
              const shape = id => {
                const parts = descendants(nodeGroup(id));
                return {
                  classes: String(nodeGroup(id).attributes.class),
                  rings: parts.filter(part => part.attributes?.class === 'ring').length,
                  icon: parts.find(part => String(part.attributes?.class) === 'node-icon')?.attributes.href
                };
              };
              return JSON.stringify({ throwShape: shape(7), endShape: shape(12) });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(1, root.GetProperty("throwShape").GetProperty("rings").GetInt32());
        Assert.Equal("#icon-signal-throw", root.GetProperty("throwShape").GetProperty("icon").GetString());
        Assert.Contains("instance-ended", root.GetProperty("endShape").GetProperty("classes").GetString());
        Assert.Equal(0, root.GetProperty("endShape").GetProperty("rings").GetInt32());
        Assert.Equal("#icon-signal-throw", root.GetProperty("endShape").GetProperty("icon").GetString());
    }

    [Fact]
    public void LoaderAndExport_KeepTheThrowAsAuthoredAndDropAnEmptyOutputMappings()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const diagram = throwDiagram();
              diagram.flowNodes.find(node => node.id === 12).signal.outputMappings = [];
              diagram.flowNodes.find(node => node.id === 7).signal.extra = 'dropped';
              loadFromObject(diagram);
              const exported = prepareWorkflowExport();
              loadFromObject(JSON.parse(exported.json));
              const again = prepareWorkflowExport();

              const invalid = throwDiagram();
              const thrower = invalid.flowNodes.find(node => node.id === 7);
              thrower.signal.scope.businesKeyVariable = 'orderNumber';
              thrower.signal.payload.push({ name: 'sender', sorce: 'instanceId' }, null);
              thrower.signal.outputMappings = [{ variable: 'amount', path: 'amount', dataType: 'number', isArray: false }];
              invalid.flowNodes.find(node => node.id === 12).signal.scope = 'allWorkflows';
              loadFromObject(invalid);
              const kept = { thrower: copy(getNode(7).signal), end: copy(getNode(12).signal) };
              const invalidExport = prepareWorkflowExport();
              // The loader works on a copy, so the imported object is never changed.
              const sourceUnchanged = thrower.signal.scope.businesKeyVariable === 'orderNumber';
              return JSON.stringify({
                errors: exported.errors, json: JSON.parse(exported.json), same: exported.json === again.json,
                kept, invalidErrors: invalidExport.errors, sourceUnchanged
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Empty(root.GetProperty("errors").EnumerateArray());
        Assert.True(root.GetProperty("same").GetBoolean());
        JsonElement NodeOf(JsonElement json, int id) => json.GetProperty("flowNodes").EnumerateArray()
            .Single(item => item.GetProperty("id").GetInt32() == id);
        var thrower = NodeOf(root.GetProperty("json"), 7);
        Assert.Equal(
            """{"name":"orders.cancelled","scope":{"kind":"thisInstance"},"payload":[{"name":"reason","variable":"cancelReason","required":true}]}""",
            thrower.GetProperty("signal").GetRawText());
        var end = NodeOf(root.GetProperty("json"), 12);
        Assert.Equal("signalEndEvent", end.GetProperty("type").GetString());
        Assert.False(end.GetProperty("signal").TryGetProperty("outputMappings", out _));
        Assert.False(end.GetProperty("signal").TryGetProperty("payload", out _));
        foreach (var property in new[] { "asyncBefore", "asyncAfter", "message", "conditional", "timer", "cancelActivity", "service" })
        {
            Assert.False(thrower.TryGetProperty(property, out _), $"The export kept '{property}' on the throw.");
        }
        Assert.Null(ServerError(root.GetProperty("json").GetRawText()));

        var kept = root.GetProperty("kept");
        Assert.Equal("orderNumber", kept.GetProperty("thrower").GetProperty("scope").GetProperty("businesKeyVariable").GetString());
        Assert.Equal("instanceId", kept.GetProperty("thrower").GetProperty("payload")[1].GetProperty("sorce").GetString());
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("thrower").GetProperty("payload")[2].ValueKind);
        Assert.Single(kept.GetProperty("thrower").GetProperty("outputMappings").EnumerateArray());
        Assert.Equal("allWorkflows", kept.GetProperty("end").GetProperty("scope").GetString());
        Assert.True(root.GetProperty("sourceUnchanged").GetBoolean());
        var invalidErrors = root.GetProperty("invalidErrors").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("Signal throw event #7 signal.scope has an unknown member 'businesKeyVariable'.", invalidErrors);
        Assert.Contains("Signal throw event #7 payload entry 2 has an unknown member 'sorce'.", invalidErrors);
        Assert.Contains("Signal throw event #7 cannot define output mappings; it sends signal.payload.", invalidErrors);
        Assert.Contains("Signal throw event #7 payload cannot contain null entries.", invalidErrors);
        Assert.Contains("Signal end event #12 signal.scope must be an object with a kind.", invalidErrors);
    }

    [Fact]
    public void TypeChanges_KeepTheNameButNeverCarryMappingsOrPayload()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(throwDiagram());
              const waiting = getNode(9);
              waiting.signal.outputMappings.push({ variable: 'amount', path: 'amount', dataType: 'number', isArray: false, required: false });
              const toThrow = changeNodeTypeFromInspector(waiting, 'intermediateSignalThrowEvent');
              const asThrow = copy(waiting.signal);
              waiting.signal.payload.push({ name: 'reason', variable: 'cancelReason' });
              changeNodeTypeFromInspector(waiting, 'intermediateSignalCatchEvent');
              const asCatch = copy(waiting.signal);

              const end = getNode(12);
              end.signal.payload = [{ name: 'reason', variable: 'cancelReason' }];
              changeNodeTypeFromInspector(end, 'intermediateSignalThrowEvent');
              const endAsThrow = copy(end.signal);
              const backToEnd = changeNodeTypeFromInspector(end, 'signalEndEvent');
              const throwAsEnd = copy(end.signal);

              // A throw with an outgoing flow cannot become an end event.
              const refused = changeNodeTypeFromInspector(getNode(7), 'signalEndEvent');
              const plainEnd = getNode(8);
              changeNodeTypeFromInspector(plainEnd, 'signalEndEvent');
              const seeded = copy(plainEnd.signal);
              changeNodeTypeFromInspector(plainEnd, 'endEvent');
              const thrower = getNode(7);
              changeNodeTypeFromInspector(thrower, 'task');
              return JSON.stringify({
                toThrow, asThrow, asCatch, endAsThrow, backToEnd, throwAsEnd, refused, seeded,
                plainEndHasSignal: 'signal' in plainEnd, taskHasSignal: 'signal' in thrower
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("toThrow").GetBoolean());
        Assert.Equal("""{"name":"orders.cancelled","scope":{"kind":""},"payload":[]}""", root.GetProperty("asThrow").GetRawText());
        Assert.Equal("""{"name":"orders.cancelled","outputMappings":[]}""", root.GetProperty("asCatch").GetRawText());
        // A throw and a signal end event keep the whole signal between them.
        var endAsThrow = root.GetProperty("endAsThrow");
        Assert.Equal("workflow", endAsThrow.GetProperty("scope").GetProperty("kind").GetString());
        Assert.Equal("reason", endAsThrow.GetProperty("payload")[0].GetProperty("name").GetString());
        Assert.True(root.GetProperty("backToEnd").GetBoolean());
        Assert.Equal(endAsThrow.GetRawText(), root.GetProperty("throwAsEnd").GetRawText());
        Assert.False(root.GetProperty("refused").GetBoolean());
        Assert.Equal("""{"name":"","scope":{"kind":""},"payload":[]}""", root.GetProperty("seeded").GetRawText());
        Assert.False(root.GetProperty("plainEndHasSignal").GetBoolean());
        Assert.False(root.GetProperty("taskHasSignal").GetBoolean());
    }

    [Fact]
    public void Invariants_ClearOtherSettingsAndKeepTheThrowsSignalInPlace()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(throwDiagram());
              const node = getNode(7);
              Object.assign(node, {
                roles: ['Clerk'], variables: [{ id: 1, name: 'x' }], assignee: 'alice', requiresClaim: true,
                service: { url: 'x' }, message: { clientId: 'x' }, scriptFormat: 'javascript', script: 'x',
                attachedToRef: 2, errorVariable: 'failure', conditional: { condition: 'x' }, gatewayRef: 4,
                asyncBefore: true, cancelActivity: true
              });
              const signal = node.signal;
              const scope = signal.scope;
              signal.extra = true;
              signal.outputMappings = [];
              applyTypeInvariants(node);
              const sameObjects = node.signal === signal && node.signal.scope === scope;

              const end = getNode(12);
              end.roles = ['Clerk'];
              end.signal.outputMappings = null;
              applyTypeInvariants(end);
              const plainEnd = getNode(8);
              plainEnd.signal = { name: 'orders.closed' };
              applyTypeInvariants(plainEnd);
              const malformed = getNode(9);
              malformed.type = 'intermediateSignalThrowEvent';
              malformed.signal = 'orders.cancelled';
              applyTypeInvariants(malformed);
              return JSON.stringify({ node, sameObjects, end, plainEndHasSignal: 'signal' in plainEnd, malformed: malformed.signal });
            })()
            """).AsString());

        var root = result.RootElement;
        var node = root.GetProperty("node");
        Assert.True(root.GetProperty("sameObjects").GetBoolean());
        Assert.Equal(
            """{"name":"orders.cancelled","scope":{"kind":"thisInstance"},"payload":[{"name":"reason","variable":"cancelReason","required":true}]}""",
            node.GetProperty("signal").GetRawText());
        foreach (var property in new[]
                 {
                     "service", "message", "scriptFormat", "script", "attachedToRef", "errorVariable", "conditional",
                     "gatewayRef", "asyncBefore", "cancelActivity"
                 })
        {
            Assert.False(node.TryGetProperty(property, out _), $"The signal throw kept '{property}'.");
        }
        Assert.Empty(node.GetProperty("roles").EnumerateArray());
        Assert.Empty(node.GetProperty("variables").EnumerateArray());
        Assert.False(node.GetProperty("requiresClaim").GetBoolean());
        Assert.Equal(JsonValueKind.Null, node.GetProperty("assignee").ValueKind);
        var end = root.GetProperty("end");
        Assert.Empty(end.GetProperty("roles").EnumerateArray());
        Assert.False(end.GetProperty("signal").TryGetProperty("outputMappings", out _));
        Assert.Equal("warehouse.product-recall", end.GetProperty("signal").GetProperty("name").GetString());
        Assert.False(root.GetProperty("plainEndHasSignal").GetBoolean());
        Assert.Equal("orders.cancelled", root.GetProperty("malformed").GetString());
    }

    [Fact]
    public void Inspector_OffersTheSixScopesAndShowsOnlyTheMembersEachKindRequires()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(throwDiagram());
              getNode(7).signal.scope = { kind: 'businessKey', workflowKey: 'shipping', businessKeyVariable: 'orderNumber' };
              select('node', 7);
              const scopeSelect = () => marked('data-signal-scope').slice(-1)[0];
              const options = scopeSelect().children.map(option => ({ value: option.value, label: option.textContent }));
              const keyList = () => {
                const input = marked('data-signal-business-key-variable').slice(-1)[0];
                const list = input && controls().find(item => item.tagName === 'DATALIST' && item.id === input.attributes.list);
                return list ? list.children.map(option => option.value) : null;
              };
              const suggestions = keyList();
              const steps = [];
              for (const kind of ['businessKey', 'workflow', 'ownBusinessKey', 'thisInstance', 'thisWorkflow', 'allWorkflows', '']) {
                const selectElement = scopeSelect();
                selectElement.value = kind;
                selectElement.onchange();
                steps.push({
                  kind,
                  value: scopeSelect().value,
                  workflowKey: marked('data-signal-workflow-key').length > 0,
                  businessKeyVariable: marked('data-signal-business-key-variable').length > 0,
                  scope: copy(getNode(7).signal.scope)
                });
              }
              // A throw without a scope gets one from its first choice.
              delete getNode(7).signal.scope;
              select('node', 7);
              const missing = scopeSelect().value;
              scopeSelect().value = 'allWorkflows';
              scopeSelect().onchange();
              const created = copy(getNode(7).signal.scope);
              // An imported kind keeps its value and is shown as unsupported.
              getNode(7).signal.scope = { kind: 'ThisInstance' };
              select('node', 7);
              const unsupported = scopeSelect().children.slice(-1)[0];
              return JSON.stringify({
                options, suggestions, steps, missing, created,
                unsupported: { value: unsupported.value, label: unsupported.textContent, selected: scopeSelect().value }
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(
            ["", "thisInstance", "thisWorkflow", "workflow", "businessKey", "ownBusinessKey", "allWorkflows"],
            root.GetProperty("options").EnumerateArray().Select(item => item.GetProperty("value").GetString()));
        Assert.Equal(
            ["Choose a scope", "This instance", "This workflow", "Another workflow family", "A business key from a variable",
             "This instance's business key", "All workflows"],
            root.GetProperty("options").EnumerateArray().Select(item => item.GetProperty("label").GetString()));
        // Declared single strings that are not shared aliases.
        Assert.Equal(["cancelReason", "orderNumber"], root.GetProperty("suggestions").EnumerateArray().Select(item => item.GetString()));

        var steps = root.GetProperty("steps").EnumerateArray().ToDictionary(item => item.GetProperty("kind").GetString()!);
        void AssertStep(string kind, bool workflowKey, bool businessKeyVariable, string scope)
        {
            var step = steps[kind];
            Assert.Equal(kind, step.GetProperty("value").GetString());
            Assert.Equal(workflowKey, step.GetProperty("workflowKey").GetBoolean());
            Assert.Equal(businessKeyVariable, step.GetProperty("businessKeyVariable").GetBoolean());
            Assert.Equal(scope, step.GetProperty("scope").GetRawText());
        }
        AssertStep("businessKey", true, true, """{"kind":"businessKey","workflowKey":"shipping","businessKeyVariable":"orderNumber"}""");
        AssertStep("workflow", true, false, """{"kind":"workflow","workflowKey":"shipping"}""");
        AssertStep("ownBusinessKey", true, false, """{"kind":"ownBusinessKey","workflowKey":"shipping"}""");
        AssertStep("thisInstance", false, false, """{"kind":"thisInstance"}""");
        AssertStep("thisWorkflow", false, false, """{"kind":"thisWorkflow"}""");
        AssertStep("allWorkflows", false, false, """{"kind":"allWorkflows"}""");
        AssertStep("", false, false, """{"kind":""}""");

        Assert.Equal("", root.GetProperty("missing").GetString());
        Assert.Equal("""{"kind":"allWorkflows"}""", root.GetProperty("created").GetRawText());
        var unsupported = root.GetProperty("unsupported");
        Assert.Equal("ThisInstance", unsupported.GetProperty("value").GetString());
        Assert.Equal("ThisInstance (unsupported imported value)", unsupported.GetProperty("label").GetString());
        Assert.Equal("ThisInstance", unsupported.GetProperty("selected").GetString());
    }

    [Fact]
    public void Inspector_PayloadRowsOfferTheDeclaredVariablesAndTheTwoInstanceValues()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(throwDiagram());
              select('node', 7);
              const valueSelects = () => marked('data-signal-payload-value');
              const nameInputs = () => marked('data-signal-payload-name');
              const entries = () => copy(getNode(7).signal.payload);
              const first = valueSelects()[0];
              const options = first.children.map(option => ({ value: option.value, label: option.textContent }));
              const firstValue = first.value;

              button('+ Add payload entry').onclick();
              const added = entries()[1];
              const second = valueSelects()[1];
              second.value = 'variable:amount';
              second.onchange();
              const chosen = { entry: entries()[1], shownName: nameInputs()[1].value };
              second.value = 'source:instanceId';
              second.onchange();
              const source = entries()[1];
              second.value = 'variable:orderNumber';
              second.onchange();
              const renamed = entries()[1];
              second.value = '';
              second.onchange();
              const cleared = entries()[1];

              const required = controls().filter(item => item.tagName === 'INPUT' && item.type === 'checkbox' &&
                (item.parentNode.children || []).some(child => child.textContent === 'Required'));
              required[0].checked = false;
              required[0].onchange();
              const notRequired = entries()[0];
              button('Delete entry').onclick();
              const afterDelete = entries();

              getNode(7).signal.payload.push({ name: 'sender', source: 'user' }, { name: 'zone', variable: 'region' },
                { name: 'note', variable: 'cancelNote' });
              select('node', 7);
              const imported = valueSelects().slice(-3).map(select => ({
                value: select.value, label: select.children.find(option => option.value === select.value)?.textContent
              }));
              return JSON.stringify({ options, firstValue, added, chosen, source, renamed, cleared, notRequired, afterDelete, imported });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(
            ["", "variable:cancelReason", "variable:amount", "variable:orderNumber", "source:instanceId", "source:businessKey"],
            root.GetProperty("options").EnumerateArray().Select(item => item.GetProperty("value").GetString()));
        Assert.Equal(
            ["Choose a value", "cancelReason (string)", "amount (number)", "orderNumber (string)", "This instance's ID",
             "This instance's business key"],
            root.GetProperty("options").EnumerateArray().Select(item => item.GetProperty("label").GetString()));
        Assert.Equal("variable:cancelReason", root.GetProperty("firstValue").GetString());
        Assert.Equal("""{"name":""}""", root.GetProperty("added").GetRawText());
        // Choosing a variable names an unnamed entry after it.
        Assert.Equal("""{"name":"amount","variable":"amount"}""", root.GetProperty("chosen").GetProperty("entry").GetRawText());
        Assert.Equal("amount", root.GetProperty("chosen").GetProperty("shownName").GetString());
        Assert.Equal("""{"name":"amount","source":"instanceId"}""", root.GetProperty("source").GetRawText());
        Assert.Equal("""{"name":"amount","variable":"orderNumber"}""", root.GetProperty("renamed").GetRawText());
        Assert.Equal("""{"name":"amount"}""", root.GetProperty("cleared").GetRawText());
        Assert.Equal("""{"name":"reason","variable":"cancelReason"}""", root.GetProperty("notRequired").GetRawText());
        Assert.Equal("""[{"name":"reason","variable":"cancelReason"}]""", root.GetProperty("afterDelete").GetRawText());
        var imported = root.GetProperty("imported").EnumerateArray().ToArray();
        Assert.Equal("source:user", imported[0].GetProperty("value").GetString());
        Assert.Equal("user (unsupported imported source)", imported[0].GetProperty("label").GetString());
        Assert.Equal("variable:region", imported[1].GetProperty("value").GetString());
        Assert.Equal("region (shared variable)", imported[1].GetProperty("label").GetString());
        Assert.Equal("cancelNote (not declared)", imported[2].GetProperty("label").GetString());
    }

    [Fact]
    public void Inspector_StatusRegionReportsTheFirstScopeAndPayloadProblemsAsTheyAreTyped()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              loadFromObject(throwDiagram());
              select('node', 7);
              const region = controls().filter(item => item.className === 'signal-throw-notes').slice(-1)[0];
              const valid = statusLines();
              const nameInput = marked('data-signal-name').slice(-1)[0];
              const nameStatus = () => controls().find(item => item.id === nameInput.attributes['aria-describedby'].split(' ')[0]).textContent;
              nameInput.value = 'orders.closed';
              nameInput.oninput();
              const unwaited = statusLines();
              nameInput.value = '';
              nameInput.oninput();
              const emptyName = nameStatus();
              nameInput.value = 'orders.cancelled';
              nameInput.oninput();
              const waited = statusLines();

              const payloadName = marked('data-signal-payload-name')[0];
              payloadName.value = 'order.reason';
              payloadName.oninput();
              const badPayloadName = statusLines();
              payloadName.value = 'reason';
              payloadName.oninput();

              const scopeSelect = marked('data-signal-scope').slice(-1)[0];
              scopeSelect.value = 'workflow';
              scopeSelect.onchange();
              const needsKey = statusLines();
              const keyInput = marked('data-signal-workflow-key').slice(-1)[0];
              keyInput.value = ' shipping';
              keyInput.oninput();
              const padded = statusLines();
              keyInput.value = 'shipping';
              keyInput.oninput();
              const keyed = statusLines();
              return JSON.stringify({
                role: region.attributes.role, live: region.attributes['aria-live'], valid, unwaited, emptyName, waited,
                badPayloadName, needsKey, padded, keyed, describedBy: keyInput.attributes['aria-describedby']
              });
            })()
            """).AsString());

        var root = result.RootElement;
        string[] Lines(string property) => root.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal("status", root.GetProperty("role").GetString());
        Assert.Equal("polite", root.GetProperty("live").GetString());
        Assert.Empty(Lines("valid"));
        Assert.Equal(
            ["Signal throw event #7 throws 'orders.closed' to this instance, but no signal catch or boundary event in this workflow waits for it."],
            Lines("unwaited"));
        Assert.Equal("Signal throw event #7 must define signal.name.", root.GetProperty("emptyName").GetString());
        Assert.Empty(Lines("waited"));
        Assert.Equal(
            ["Signal throw event #7 payload entry 1 name must be 1 to 100 letters, digits, '_', or '-', starting with a letter or '_'."],
            Lines("badPayloadName"));
        Assert.Equal(["Signal throw event #7 signal.scope needs a workflowKey for kind 'workflow'."], Lines("needsKey"));
        Assert.Equal(["Signal throw event #7 signal.scope.workflowKey cannot have surrounding whitespace."], Lines("padded"));
        Assert.Empty(Lines("keyed"));
        Assert.StartsWith("signal-throw-status-7", root.GetProperty("describedBy").GetString());
    }

    [Fact]
    public void Inspector_LabelsEveryControlExplainsTheThrowAndRemovesWhatSaveRefuses()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const diagram = throwDiagram();
              const thrower = diagram.flowNodes.find(node => node.id === 7);
              thrower.signal.scope = { kind: 'businessKey', workflowKey: 'shipping', businessKeyVariable: 'orderNumber', businesKeyVariable: 'x' };
              thrower.signal.payload[0].sorce = 'instanceId';
              thrower.signal.outputMappings = [{ variable: 'amount', path: 'amount', dataType: 'number', isArray: false }];
              diagram.flowNodes.find(node => node.id === 12).signal.scope.businessKeyVariable = 'orderNumber';
              loadFromObject(diagram);
              select('node', 7);
              const section = () => within(sectionFrom('Signal', ['Outgoing flows']));
              const fields = section().filter(item => ['INPUT', 'SELECT', 'TEXTAREA'].includes(item.tagName));
              const unlabeled = fields.filter(control =>
                !(control.parentNode && control.parentNode.tagName === 'LABEL') &&
                !/^<label>[^<]+<\/label>$/.test(String(control.parentNode && control.parentNode.innerHTML)) &&
                !control.attributes['aria-label']);
              const fieldLabels = fields.map(control => control.parentNode && control.parentNode.tagName === 'LABEL'
                ? control.parentNode.children.map(child => child.textContent).join('')
                : String(control.parentNode.innerHTML).replace(/<\/?label>/g, ''));
              const headings = section().filter(item => item.tagName === 'H2').map(item => item.textContent);
              const helpTexts = section().filter(item => item.className === 'inspector-help').map(item =>
                descendants(item).map(part => part.textContent || '').join(''));
              const buttons = section().filter(item => item.tagName === 'BUTTON').map(item => item.textContent);
              const warnings = section().filter(item => item.tagName === 'P' && item.style.color).map(item => item.textContent);

              button('Remove unknown scope members').onclick();
              button('Remove unknown members').onclick();
              button('Remove the output mappings').onclick();
              const repaired = copy(getNode(7).signal);

              select('node', 12);
              const endButtons = within(sectionFrom('Signal', ['Outgoing flows'])).filter(item => item.tagName === 'BUTTON')
                .map(item => item.textContent);
              button('Remove the settings this scope does not use').onclick();
              const endScope = copy(getNode(12).signal.scope);
              const endHeadings = latestInspector().filter(item => item.tagName === 'H2').map(item => item.textContent);

              select('node', 9);
              const catchHelp = within(sectionFrom('Signal', ['Output mappings'])).filter(item => item.className === 'inspector-help')
                .map(item => descendants(item).map(part => part.textContent || '').join(''));
              return JSON.stringify({
                unlabeled: unlabeled.length, fieldLabels, headings, helpTexts, buttons, warnings, repaired,
                endButtons, endScope, endHeadings, catchHelp, errors: prepareWorkflowExport().errors
              });
            })()
            """).AsString());

        var root = result.RootElement;
        string[] Texts(string property) => root.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(0, root.GetProperty("unlabeled").GetInt32());
        Assert.Equal(
            ["Signal name", "Scope", "Workflow key", "Business key variable", "Name in the payload", "Value", "Required"],
            Texts("fieldLabels"));
        Assert.Equal(["Signal", "Payload"], Texts("headings"));
        var help = string.Join("\n", Texts("helpTexts"));
        Assert.Contains("never waits for the deliveries", help, StringComparison.Ordinal);
        Assert.Contains("does not exist yet", help, StringComparison.Ordinal);
        Assert.Contains("its step fails", help, StringComparison.Ordinal);
        Assert.Contains("asyncBefore", help, StringComparison.Ordinal);
        Assert.Contains("Signal administrators can read", help, StringComparison.Ordinal);
        Assert.Contains("This instance's business key", help, StringComparison.Ordinal);
        Assert.Contains("Remove unknown scope members", Texts("buttons"));
        Assert.Contains("Remove unknown members", Texts("buttons"));
        Assert.Contains("Remove the output mappings", Texts("buttons"));
        Assert.Contains("+ Add payload entry", Texts("buttons"));
        Assert.Contains("signal.scope has unknown member 'businesKeyVariable', so the workflow cannot be saved.", Texts("warnings"));
        Assert.Contains("Payload entry 1 has unknown member 'sorce', so the workflow cannot be saved.", Texts("warnings"));
        Assert.Equal(
            """{"name":"orders.cancelled","scope":{"kind":"businessKey","workflowKey":"shipping","businessKeyVariable":"orderNumber"},"payload":[{"name":"reason","variable":"cancelReason","required":true}]}""",
            root.GetProperty("repaired").GetRawText());

        Assert.Contains("Remove the settings this scope does not use", Texts("endButtons"));
        Assert.Equal("""{"kind":"workflow","workflowKey":"example-signal-boundaries-recall"}""", root.GetProperty("endScope").GetRawText());
        // An end event has no outgoing flows to edit.
        Assert.DoesNotContain("Outgoing flows", Texts("endHeadings"));
        Assert.Contains("Payload", Texts("endHeadings"));
        var catchHelp = string.Join("\n", Texts("catchHelp"));
        Assert.Contains("signal throw event", catchHelp, StringComparison.Ordinal);
        Assert.Contains("signal end event", catchHelp, StringComparison.Ordinal);
        Assert.Empty(Texts("errors"));
    }

    /// <summary>
    /// An imported payload entry that names both a variable and a source shows the variable as
    /// its value, so the source is named beside it with a way to remove it.
    /// </summary>
    [Fact]
    public void Inspector_NamesAndRemovesTheSourceOfAnEntryThatAlsoNamesAVariable()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const diagram = throwDiagram();
              diagram.flowNodes.find(node => node.id === 7).signal.payload[0].source = 'instanceId';
              loadFromObject(diagram);
              const before = prepareWorkflowExport().errors;
              select('node', 7);
              const warnings = within(sectionFrom('Signal', ['Outgoing flows']))
                .filter(item => item.tagName === 'P' && item.style.color).map(item => item.textContent);
              button('Remove the source').onclick();
              const repaired = copy(getNode(7).signal.payload);
              const after = within(sectionFrom('Signal', ['Outgoing flows']))
                .filter(item => item.tagName === 'BUTTON').map(item => item.textContent);
              return JSON.stringify({ before, warnings, repaired, after, errors: prepareWorkflowExport().errors });
            })()
            """).AsString());

        var root = result.RootElement;
        string[] Texts(string property) => root.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.NotEmpty(Texts("before"));
        Assert.Contains(
            "Payload entry 1 also names the source 'instanceId', but an entry sends either a variable or a source, so the workflow cannot be saved.",
            Texts("warnings"));
        Assert.Equal("""[{"name":"reason","variable":"cancelReason","required":true}]""", root.GetProperty("repaired").GetRawText());
        Assert.DoesNotContain("Remove the source", Texts("after"));
        Assert.Empty(Texts("errors"));
    }

    [Fact]
    public void Inspector_RepairsAMissingSignalAndAMalformedScopeAndPayload()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(Diagram + """
            (() => {
              const diagram = throwDiagram();
              delete diagram.flowNodes.find(node => node.id === 7).signal;
              diagram.flowNodes.find(node => node.id === 12).signal.scope = 'allWorkflows';
              diagram.flowNodes.find(node => node.id === 12).signal.payload = { reason: 'cancelReason' };
              loadFromObject(diagram);
              select('node', 7);
              const missingButtons = controls().filter(item => item.tagName === 'BUTTON').map(item => item.textContent);
              button('Replace with a signal configuration').onclick();
              const replaced = copy(getNode(7).signal);

              select('node', 12);
              const malformedButtons = controls().filter(item => item.tagName === 'BUTTON').map(item => item.textContent);
              const keptScope = getNode(12).signal.scope;
              button('Replace it with a scope').onclick();
              button('Replace it with an empty list').onclick();
              const repaired = copy(getNode(12).signal);
              return JSON.stringify({ missingButtons, replaced, malformedButtons, keptScope, repaired });
            })()
            """).AsString());

        var root = result.RootElement;
        string[] Texts(string property) => root.GetProperty(property).EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Contains("Replace with a signal configuration", Texts("missingButtons"));
        Assert.Equal("""{"name":"","scope":{"kind":""},"payload":[]}""", root.GetProperty("replaced").GetRawText());
        Assert.Contains("Replace it with a scope", Texts("malformedButtons"));
        Assert.Contains("Replace it with an empty list", Texts("malformedButtons"));
        Assert.Equal("allWorkflows", root.GetProperty("keptScope").GetString());
        Assert.Equal("""{"name":"warehouse.product-recall","scope":{"kind":""},"payload":[]}""", root.GetProperty("repaired").GetRawText());
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
