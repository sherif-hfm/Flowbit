using System.Text.Json;
using Jint;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Characterizes the editor's read-only viewer mode on the real instance body.
/// Real-browser behavior is covered by the browser smoke suite (R8).
/// </summary>
public sealed class EditorViewerModeTests
{
    private const string LoadDiagram = """
        loadFromObject({
          id: 'viewer-smoke', name: 'Viewer smoke', initialEventId: 1, variables: [],
          lanes: [{ id: 10, name: 'Team', x: 0, y: 0, w: 900, h: 300 }],
          flowNodes: [
            { id: 1, name: 'Start', type: 'startEvent', laneId: 10, x: 40, y: 100 },
            { id: 2, name: 'Review', type: 'userTask', laneId: 10, x: 200, y: 90 },
            { id: 3, name: 'Wait for reply', type: 'intermediateMessageCatchEvent', laneId: 10, x: 460, y: 100 },
            { id: 4, name: 'Done', type: 'endEvent', laneId: 10, x: 640, y: 100 }
          ],
          sequenceFlows: [
            { id: 101, name: '', sourceRef: 1, targetRef: 2 },
            { id: 201, name: 'Ask', sourceRef: 2, targetRef: 3 },
            { id: 301, name: '', sourceRef: 3, targetRef: 4 }
          ]
        });
        const latestNodeGroups = () => nodesG.children.slice(-model.flowNodes.length);
        const nodeGroup = id => latestNodeGroups().find(group => group.attributes['data-id'] === String(id));
        const childWithClass = (parent, name) => parent.children.find(child => child.attributes?.class === name);
        const keydown = (target, key, extra = {}) => {
          const event = {
            type: 'keydown', key, code: key, target, ctrlKey: false, metaKey: false, altKey: false, shiftKey: false,
            defaultPrevented: false, preventDefault() { this.defaultPrevented = true; },
            stopPropagation() {}, ...extra
          };
          (target.dispatchEvent ? target : window).dispatchEvent(event);
          return event;
        };
        """;

    [Fact]
    public void ViewerKeepsThePanToolAndIgnoresEditingShortcuts()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "viewer");
        using var result = JsonDocument.Parse(engine.Evaluate(LoadDiagram + """
            (() => {
              setActiveTool('select');
              const toolAfterSelect = activeTool;
              select('node', 2);
              const before = JSON.stringify(model);
              const windowKey = (key, extra = {}) => {
                const event = {
                  type: 'keydown', key, code: key, target: fakeElement('div'), ctrlKey: false, metaKey: false,
                  altKey: false, shiftKey: false, defaultPrevented: false,
                  preventDefault() { this.defaultPrevented = true; }, ...extra
                };
                window.dispatchEvent(event);
                return event;
              };
              const ignored = [
                windowKey('Delete'), windowKey('z', { ctrlKey: true }), windowKey('y', { ctrlKey: true }),
                windowKey('v'), windowKey('r'), windowKey('/'), windowKey(' ', { code: 'Space' })
              ];
              const unchanged = before === JSON.stringify(model);
              const stillSelected = selected?.nodeId;
              const escape = windowKey('Escape');
              return JSON.stringify({
                toolAfterSelect, tool: activeTool, svgTool: svg.dataset.activeTool, unchanged, stillSelected,
                ignoredPrevented: ignored.map(event => event.defaultPrevented),
                spacePressed, escapePrevented: escape.defaultPrevented, selectedAfterEscape: selected,
                svgRole: svg.getAttribute('role'), svgLabel: svg.getAttribute('aria-label'),
                theme: root.dataset.theme, inspector: inspector.innerHTML
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("pan", root.GetProperty("toolAfterSelect").GetString());
        Assert.Equal("pan", root.GetProperty("tool").GetString());
        Assert.Equal("pan", root.GetProperty("svgTool").GetString());
        Assert.True(root.GetProperty("unchanged").GetBoolean());
        Assert.Equal(2, root.GetProperty("stillSelected").GetInt32());
        Assert.All(root.GetProperty("ignoredPrevented").EnumerateArray(), value => Assert.False(value.GetBoolean()));
        Assert.False(root.GetProperty("spacePressed").GetBoolean());
        Assert.True(root.GetProperty("escapePrevented").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("selectedAfterEscape").ValueKind);
        Assert.Equal("group", root.GetProperty("svgRole").GetString());
        Assert.Equal("Test diagram", root.GetProperty("svgLabel").GetString());
        Assert.Equal("light", root.GetProperty("theme").GetString());
        Assert.Equal(string.Empty, root.GetProperty("inspector").GetString());
    }

    [Fact]
    public void OverlayMarksValidNodesAsFocusableButtonsWithBadgesAndLabels()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "viewer");
        using var result = JsonDocument.Parse(engine.Evaluate(LoadDiagram + """
            (() => {
              setInstanceOverlay({ nodes: [
                { id: 2, state: 'active', badge: '2/3', label: 'Multi-instance work: 2 of 3 completed, 1 active' },
                { id: 3, state: 'active', badge: null, label: 'Waiting for a message' },
                { id: 4, state: 'bogus', label: 'Unknown state' },
                { id: '1', state: 'ended', label: 'String id' },
                { id: 2, state: 'ended', label: 'Duplicate' },
                null
              ]});
              const describe = id => {
                const group = nodeGroup(id);
                const badge = childWithClass(group, 'instance-badge');
                return {
                  className: group.attributes.class,
                  tabindex: group.attributes.tabindex ?? null,
                  role: group.attributes.role ?? null,
                  ariaLabel: group.attributes['aria-label'] ?? null,
                  title: group.children[0].textContent,
                  // The state ring sits right after the title, behind the shape.
                  halo: group.children[1].attributes.class === 'instance-halo' ? group.children[1].tagName : null,
                  badge: badge ? badge.children[1].textContent : null,
                  handles: group.children.filter(child => String(child.attributes?.class ?? '').startsWith('connect-handle')).length
                };
              };
              const long = normalizeInstanceOverlay({ nodes: [{ id: 2, state: 'active', badge: '1234567890', label: 'x'.repeat(400) }] }).get(2);
              return JSON.stringify({
                marked: [...instanceOverlay.keys()],
                review: describe(2), wait: describe(3), done: describe(4), start: describe(1),
                longBadge: long.badge, longLabelLength: Array.from(long.label).length
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(new[] { 2, 3 }, root.GetProperty("marked").EnumerateArray().Select(value => value.GetInt32()));

        var review = root.GetProperty("review");
        Assert.Contains("instance-marked instance-active", review.GetProperty("className").GetString());
        Assert.Equal("0", review.GetProperty("tabindex").GetString());
        Assert.Equal("button", review.GetProperty("role").GetString());
        Assert.Equal("Review, User Task: Multi-instance work: 2 of 3 completed, 1 active",
            review.GetProperty("ariaLabel").GetString());
        Assert.EndsWith(" · Multi-instance work: 2 of 3 completed, 1 active", review.GetProperty("title").GetString());
        Assert.Equal("2/3", review.GetProperty("badge").GetString());
        Assert.Equal("RECT", review.GetProperty("halo").GetString());
        Assert.Equal(0, review.GetProperty("handles").GetInt32());

        var wait = root.GetProperty("wait");
        Assert.Contains("instance-active", wait.GetProperty("className").GetString());
        Assert.Equal("CIRCLE", wait.GetProperty("halo").GetString());
        Assert.Equal(JsonValueKind.Null, wait.GetProperty("badge").ValueKind);

        foreach (var name in new[] { "done", "start" })
        {
            var unmarked = root.GetProperty(name);
            Assert.DoesNotContain("instance-marked", unmarked.GetProperty("className").GetString());
            Assert.Equal(JsonValueKind.Null, unmarked.GetProperty("halo").ValueKind);
            Assert.Equal(JsonValueKind.Null, unmarked.GetProperty("tabindex").ValueKind);
            Assert.Equal(JsonValueKind.Null, unmarked.GetProperty("role").ValueKind);
            Assert.Equal(0, unmarked.GetProperty("handles").GetInt32());
        }

        Assert.Equal(8, root.GetProperty("longBadge").GetString()!.Length);
        Assert.Equal(300, root.GetProperty("longLabelLength").GetInt32());
    }

    [Fact]
    public void SelectionNotifiesTheHostAndOnlyNodesAreInspectable()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "viewer");
        using var result = JsonDocument.Parse(engine.Evaluate(LoadDiagram + """
            (() => {
              setInstanceOverlay({ nodes: [{ id: 3, state: 'active', label: 'Waiting for a message' }] });
              const target = (selector, id) => {
                const group = fakeElement('g');
                const element = fakeElement('path');
                element.closest = candidate => candidate === selector ? group : null;
                group.getAttribute = () => String(id);
                group.dataset = selector === '.node[data-id]' ? { id: String(id) } : { flow: String(id) };
                return element;
              };
              const flowTarget = panInspectionTarget(target('.edge[data-flow]', 201));
              const nodeTarget = panInspectionTarget(target('.node[data-id]', 2));

              select('node', 2);
              select('node', 2);
              const ringOnSelected = !!childWithClass(nodeGroup(2), 'viewer-selection-ring');
              const trace = activeTraceContext;
              const enter = keydown(nodeGroup(3), 'Enter');
              const selectedByKeyboard = selected?.nodeId;
              const unmarkedEnter = keydown(nodeGroup(4), 'Enter');
              clearSelection();
              return JSON.stringify({
                selections: viewerSelections, flowTarget, nodeTarget, ringOnSelected, trace,
                enterPrevented: enter.defaultPrevented, selectedByKeyboard,
                unmarkedEnterPrevented: unmarkedEnter.defaultPrevented
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(new int?[] { 2, 3, null }, root.GetProperty("selections").EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.Null ? (int?)null : value.GetInt32()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("flowTarget").ValueKind);
        Assert.Equal(2, root.GetProperty("nodeTarget").GetProperty("id").GetInt32());
        Assert.True(root.GetProperty("ringOnSelected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("trace").ValueKind);
        Assert.True(root.GetProperty("enterPrevented").GetBoolean());
        Assert.Equal(3, root.GetProperty("selectedByKeyboard").GetInt32());
        Assert.False(root.GetProperty("unmarkedEnterPrevented").GetBoolean());
    }

    [Fact]
    public void SelectedMarkedNodeIsCurrentAndFocusStaysInTheDiagramAcrossUpdates()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "viewer");
        using var result = JsonDocument.Parse(engine.Evaluate(LoadDiagram + """
            (() => {
              // The fake DOM neither tracks focus nor queries children; record both here.
              const focusLog = [];
              const createElementNS = document.createElementNS;
              document.createElementNS = (namespace, tag) => {
                const element = createElementNS.call(document, namespace, tag);
                element.focus = () => { document.activeElement = element; focusLog.push(element.attributes['data-id']); };
                element.closest = selector => selector === '.node[data-id]' && element.attributes['data-id'] ? element : null;
                return element;
              };
              root.focus = () => { document.activeElement = root; focusLog.push('root'); };
              nodesG.contains = element => nodesG.children.includes(element);
              nodesG.querySelectorAll = () => latestNodeGroups();

              const marked = label => ({ nodes: [
                { id: 2, state: 'active', label: 'Waiting for user work' },
                { id: 3, state: 'active', label }
              ]});
              setInstanceOverlay(marked('Waiting for a message'));
              select('node', 2);
              const current = id => nodeGroup(id).attributes['aria-current'] ?? null;
              const selectedMarked = { review: current(2), wait: current(3) };
              select('node', 4);
              const selectedUnmarked = current(4);

              nodeGroup(3).focus();
              setInstanceOverlay(marked('Waiting for a reply'));
              setInstanceOverlay({ nodes: [{ id: 2, state: 'active', label: 'Waiting for user work' }] });
              return JSON.stringify({ selectedMarked, selectedUnmarked, focusLog });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("true", root.GetProperty("selectedMarked").GetProperty("review").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("selectedMarked").GetProperty("wait").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("selectedUnmarked").ValueKind);
        // Focus follows the re-rendered node, then falls back to the diagram once it is no longer marked.
        Assert.Equal(new[] { "3", "3", "root" }, root.GetProperty("focusLog").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void DraggingFromABoundaryEventPansInsteadOfSelectingIt()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "viewer");
        using var result = JsonDocument.Parse(engine.Evaluate(LoadDiagram + """
            (() => {
              model.flowNodes.push({ id: 5, name: 'Reminder', type: 'timerBoundaryEvent', attachedToRef: 2, cancelActivity: false, x: 0, y: 0 });
              renderNodes();
              const boundary = nodeGroup(5);
              const down = fakePointerEvent('pointerdown', boundary, 7, 300, 200);
              boundary.eventListeners.pointerdown.forEach(entry => entry.listener.call(boundary, down));
              return JSON.stringify({ stopped: down.propagationStopped, selected });
            })()
            """).AsString());

        var root = result.RootElement;
        // The event reaches the canvas, which pans and inspects the boundary on release.
        Assert.False(root.GetProperty("stopped").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("selected").ValueKind);
    }

    [Fact]
    public void FlowsAndLanesOfferNoEditingAffordances()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "viewer");
        using var result = JsonDocument.Parse(engine.Evaluate(LoadDiagram + """
            (() => {
              renderEdges();
              renderLanes();
              const pathLayer = edgesG.children.slice(-2)[0];
              const lane = lanesG.children.slice(-model.lanes.length)[0];
              return JSON.stringify({
                edges: pathLayer.children.map(edge => ({
                  tabindex: edge.attributes.tabindex ?? null,
                  role: edge.attributes.role ?? null,
                  // Focus listeners alone make an SVG element focusable in Chrome.
                  keyboardListeners: ['keydown', 'focus', 'blur']
                    .reduce((count, type) => count + (edge.eventListeners[type] || []).length, 0)
                })),
                laneResize: lane.children.some(child => child.attributes?.class === 'resize')
              });
            })()
            """).AsString());

        var root = result.RootElement;
        var edges = root.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(3, edges.Length);
        Assert.All(edges, edge =>
        {
            Assert.Equal(JsonValueKind.Null, edge.GetProperty("tabindex").ValueKind);
            Assert.Equal(JsonValueKind.Null, edge.GetProperty("role").ValueKind);
            Assert.Equal(0, edge.GetProperty("keyboardListeners").GetInt32());
        });
        Assert.False(root.GetProperty("laneResize").GetBoolean());
    }

    [Fact]
    public void WheelZoomsOnlyWithAModifierSoThePageCanScroll()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "viewer");
        using var result = JsonDocument.Parse(engine.Evaluate("""
            (() => {
              const wheel = extra => {
                const event = {
                  type: 'wheel', deltaY: -200, clientX: 200, clientY: 150, ctrlKey: false, metaKey: false,
                  defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...extra
                };
                svg.dispatchEvent(event);
                return event;
              };
              const zoomBefore = viewState.zoom;
              const plain = wheel({});
              const zoomAfterPlain = viewState.zoom;
              const ctrl = wheel({ ctrlKey: true });
              const zoomAfterCtrl = viewState.zoom;
              const meta = wheel({ metaKey: true });
              return JSON.stringify({
                plainPrevented: plain.defaultPrevented, plainKeptZoom: zoomAfterPlain === zoomBefore,
                ctrlPrevented: ctrl.defaultPrevented, ctrlZoomed: zoomAfterCtrl > zoomBefore,
                metaPrevented: meta.defaultPrevented
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.False(root.GetProperty("plainPrevented").GetBoolean());
        Assert.True(root.GetProperty("plainKeptZoom").GetBoolean());
        Assert.True(root.GetProperty("ctrlPrevented").GetBoolean());
        Assert.True(root.GetProperty("ctrlZoomed").GetBoolean());
        Assert.True(root.GetProperty("metaPrevented").GetBoolean());
    }

    [Fact]
    public void InitialFitWaitsForACanvasAndBringsMarkedNodesIntoView()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "viewer");
        using var result = JsonDocument.Parse(engine.Evaluate(LoadDiagram + """
            (() => {
              model.flowNodes.push({ id: 9, name: 'Far away', type: 'userTask', laneId: null, x: 4000, y: 2600 });
              instanceOverlay = normalizeInstanceOverlay({ nodes: [{ id: 9, state: 'active', label: 'Waiting for user work' }] });
              const measure = canvasWrap.getBoundingClientRect;
              canvasWrap.getBoundingClientRect = () => ({ left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0 });
              fitViewerContent();
              const deferred = pendingViewerFit;
              canvasWrap.getBoundingClientRect = measure;
              fitViewerContent();
              const { width, height } = canvasDimensions();
              const node = getNode(9);
              const visible = () => node.x >= viewState.x && node.y >= viewState.y &&
                node.x + NODE_W <= viewState.x + width / viewState.zoom &&
                node.y + NODE_H <= viewState.y + height / viewState.zoom;
              const offCanvas = { visible: visible(), zoom: viewState.zoom };

              // A whole-diagram fit at the unreadable overview level centers the
              // marked nodes at the smallest readable zoom instead.
              instanceOverlay = normalizeInstanceOverlay({ nodes: [{ id: 2, state: 'active', label: 'Waiting for user work' }] });
              const realFitView = fitView;
              fitView = () => { viewState = { x: -2000, y: -2000, zoom: .4 }; };
              fitViewerContent();
              fitView = realFitView;
              const review = getNode(2);
              const centered = Math.abs(viewState.x + width / viewState.zoom / 2 - (review.x + NODE_W / 2)) < 1 &&
                Math.abs(viewState.y + height / viewState.zoom / 2 - (review.y + NODE_H / 2)) < 1;
              return JSON.stringify({ deferred, pending: pendingViewerFit, offCanvas, overviewZoom: viewState.zoom, centered });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("deferred").GetBoolean());
        Assert.False(root.GetProperty("pending").GetBoolean());
        Assert.True(root.GetProperty("offCanvas").GetProperty("visible").GetBoolean());
        Assert.True(root.GetProperty("offCanvas").GetProperty("zoom").GetDouble() <= 1);
        Assert.Equal(0.6, root.GetProperty("overviewZoom").GetDouble(), 3);
        Assert.True(root.GetProperty("centered").GetBoolean());
    }

    [Fact]
    public void MarkedNodesTooFarApartToReadTogetherOpenOnTheFirstOfThemReadably()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine(mode: "viewer");
        using var result = JsonDocument.Parse(engine.Evaluate(LoadDiagram + """
            (() => {
              // Two parallel branches far apart, on a phone-sized canvas.
              model.flowNodes.push({ id: 9, name: 'Far branch', type: 'userTask', laneId: null, x: 1800, y: 90 });
              instanceOverlay = normalizeInstanceOverlay({ nodes: [
                { id: 9, state: 'active', label: 'Waiting for user work' },
                { id: 2, state: 'active', label: 'Waiting for user work' }
              ]});
              canvasWrap.getBoundingClientRect = () => ({ left: 0, top: 0, right: 307, bottom: 320, width: 307, height: 320 });
              fitViewerContent();
              const { width, height } = canvasDimensions();
              const visible = id => {
                const node = getNode(id);
                return node.x >= viewState.x && node.y >= viewState.y &&
                  node.x + NODE_W <= viewState.x + width / viewState.zoom &&
                  node.y + NODE_H <= viewState.y + height / viewState.zoom;
              };
              return JSON.stringify({
                zoom: viewState.zoom, semantic: classifySemanticZoom(viewState.zoom),
                first: visible(2), far: visible(9)
              });
            })()
            """).AsString());

        var root = result.RootElement;
        // Fitting both would need about 35%, where captions hide; the leftmost opens readably instead.
        Assert.Equal(0.6, root.GetProperty("zoom").GetDouble(), 3);
        Assert.Equal("medium", root.GetProperty("semantic").GetString());
        Assert.True(root.GetProperty("first").GetBoolean());
        Assert.False(root.GetProperty("far").GetBoolean());
    }

    [Fact]
    public void StandaloneEditorIgnoresInstanceOverlays()
    {
        var engine = EditorRuntimeSmokeTests.CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(LoadDiagram + """
            (() => {
              setInstanceOverlay({ nodes: [{ id: 2, state: 'active', badge: '1/2', label: 'Waiting for user work' }] });
              const group = nodeGroup(2);
              return JSON.stringify({
                className: group.attributes.class,
                tabindex: group.attributes.tabindex ?? null,
                handles: group.children.some(child => String(child.attributes?.class ?? '').startsWith('connect-handle')),
                edgeTabStops: edgesG.children.slice(-2)[0].children.every(edge => edge.attributes.tabindex === '0')
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.DoesNotContain("instance-marked", root.GetProperty("className").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("tabindex").ValueKind);
        Assert.True(root.GetProperty("handles").GetBoolean());
        Assert.True(root.GetProperty("edgeTabStops").GetBoolean());
    }
}
