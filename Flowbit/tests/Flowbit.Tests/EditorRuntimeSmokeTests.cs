using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Xunit;

namespace Flowbit.Tests;

public sealed class EditorRuntimeSmokeTests
{
    [Fact]
    public void VariableRoleReferencesRoundTripAndRenderAsReferences()
    {
        var engine = CreateEditorEngine();
        engine.SetValue("roleExampleJson", ExampleWorkflowData.Read(
            "examples/user-tasks/05-variable-roles-and-role-management.json"));
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject(JSON.parse(roleExampleJson));
              render();
              const first = JSON.stringify(model);
              const task = model.flowNodes.find(node => node.id === 2);
              const flow = model.sequenceFlows.find(item => item.id === 201);
              const label = flowLabelInfo(flow, task);
              roleSourceField(task, () => {});
              loadFromObject(JSON.parse(first));
              return JSON.stringify({
                taskVariable: model.flowNodes.find(node => node.id === 2).rolesVariable,
                flowVariable: model.sequenceFlows.find(flow => flow.id === 201).rolesVariable,
                managerRoles: model.taskRoleManagementRoles,
                flowLabel: label.name,
                errors: validateModelForSave(model)
              });
            })()
            """).AsString());
        var value = result.RootElement;
        Assert.Equal("reviewRoles", value.GetProperty("taskVariable").GetString());
        Assert.Equal("approvalRoles", value.GetProperty("flowVariable").GetString());
        Assert.Equal("RoleManager", value.GetProperty("managerRoles")[0].GetString());
        Assert.Contains("$approvalRoles", value.GetProperty("flowLabel").GetString());
        Assert.Empty(value.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public void ToolTransitionsFollowFileAndCreationActions()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const startup = {
                activeTool,
                svgTool: svg.dataset.activeTool,
                handleTool: authoringPaletteHandle.dataset.activeTool,
                handleExpanded: authoringPaletteHandle.getAttribute('aria-expanded'),
                selectPressed: selectToolBtn.getAttribute('aria-pressed'),
                rectanglePressed: rectangleSelectToolBtn.getAttribute('aria-pressed'),
                panPressed: panToolBtn.getAttribute('aria-pressed')
              };

              model.flowNodes.push({
                id: 99,
                name: 'Existing node',
                type: NODE_TYPE.TASK,
                laneId: null,
                x: 100,
                y: 100,
                attributes: [],
                roles: [],
                variables: []
              });
              fileMenu.open = true;
              document.getElementById('newBtn').onclick();
              const afterNew = {
                activeTool,
                menuClosed: !fileMenu.open,
                nodesCleared: model.flowNodes.length === 0,
                connectMode
              };

              setActiveTool('pan');
              document.getElementById('addStepBtn').onclick();
              const afterAddNode = {
                activeTool,
                selectedKind: selected?.kind,
                nodeCount: model.flowNodes.length
              };

              setActiveTool('pan');
              document.getElementById('addPhaseBtn').onclick();
              const afterAddLane = {
                activeTool,
                selectedKind: selected?.kind,
                laneCount: model.lanes.length
              };

              setActiveTool('pan');
              document.getElementById('connectBtn').onclick();
              const afterConnect = {
                activeTool,
                connectMode,
                connectPressed: document.getElementById('connectBtn').getAttribute('aria-pressed')
              };
              setConnectMode(false);

              setActiveTool('select');
              const originalFileReader = FileReader;
              FileReader = function() {
                this.readAsText = file => {
                  this.result = file.contents;
                  this.onload();
                };
              };
              const fileInput = document.getElementById('fileInput');

              fileInput.files = [];
              fileInput.onchange({ target: fileInput });
              const afterCanceledLoad = { activeTool };

              const modelBeforeInvalidLoad = model;
              fileInput.files = [{ contents: '{ invalid json' }];
              fileInput.value = 'invalid-workflow.json';
              fileInput.onchange({ target: fileInput });
              const afterInvalidLoad = {
                activeTool,
                modelUnchanged: model === modelBeforeInvalidLoad,
                inputReset: fileInput.value === ''
              };

              fileInput.files = [{ contents: JSON.stringify(newModel()) }];
              fileInput.value = 'loaded-workflow.json';
              fileInput.onchange({ target: fileInput });
              const afterLoad = {
                activeTool,
                inputReset: fileInput.value === '',
                connectMode
              };
              FileReader = originalFileReader;

              return JSON.stringify({
                startup,
                afterNew,
                afterAddNode,
                afterAddLane,
                afterConnect,
                afterCanceledLoad,
                afterInvalidLoad,
                afterLoad
              });
            })()
            """).AsString());

        var root = result.RootElement;
        var startup = root.GetProperty("startup");
        Assert.Equal("pan", startup.GetProperty("activeTool").GetString());
        Assert.Equal("pan", startup.GetProperty("svgTool").GetString());
        Assert.Equal("pan", startup.GetProperty("handleTool").GetString());
        Assert.Equal("false", startup.GetProperty("handleExpanded").GetString());
        Assert.Equal("false", startup.GetProperty("selectPressed").GetString());
        Assert.Equal("false", startup.GetProperty("rectanglePressed").GetString());
        Assert.Equal("true", startup.GetProperty("panPressed").GetString());

        var afterNew = root.GetProperty("afterNew");
        Assert.Equal("select", afterNew.GetProperty("activeTool").GetString());
        Assert.True(afterNew.GetProperty("menuClosed").GetBoolean());
        Assert.True(afterNew.GetProperty("nodesCleared").GetBoolean());
        Assert.False(afterNew.GetProperty("connectMode").GetBoolean());

        var afterAddNode = root.GetProperty("afterAddNode");
        Assert.Equal("select", afterAddNode.GetProperty("activeTool").GetString());
        Assert.Equal("node", afterAddNode.GetProperty("selectedKind").GetString());
        Assert.Equal(1, afterAddNode.GetProperty("nodeCount").GetInt32());

        var afterAddLane = root.GetProperty("afterAddLane");
        Assert.Equal("select", afterAddLane.GetProperty("activeTool").GetString());
        Assert.Equal("lane", afterAddLane.GetProperty("selectedKind").GetString());
        Assert.Equal(1, afterAddLane.GetProperty("laneCount").GetInt32());

        var afterConnect = root.GetProperty("afterConnect");
        Assert.Equal("select", afterConnect.GetProperty("activeTool").GetString());
        Assert.True(afterConnect.GetProperty("connectMode").GetBoolean());
        Assert.Equal("true", afterConnect.GetProperty("connectPressed").GetString());

        Assert.Equal(
            "select",
            root.GetProperty("afterCanceledLoad").GetProperty("activeTool").GetString());

        var afterInvalidLoad = root.GetProperty("afterInvalidLoad");
        Assert.Equal("select", afterInvalidLoad.GetProperty("activeTool").GetString());
        Assert.True(afterInvalidLoad.GetProperty("modelUnchanged").GetBoolean());
        Assert.True(afterInvalidLoad.GetProperty("inputReset").GetBoolean());

        var afterLoad = root.GetProperty("afterLoad");
        Assert.Equal("pan", afterLoad.GetProperty("activeTool").GetString());
        Assert.True(afterLoad.GetProperty("inputReset").GetBoolean());
        Assert.False(afterLoad.GetProperty("connectMode").GetBoolean());
    }

    [Fact]
    public void ToolbarMenusCloseWithoutChangingSelection()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              selected = { kind: 'node', nodeId: 42 };
              setActiveTool('select');

              fileMenu.open = true;
              editMenu.open = true;
              viewMenu.open = true;
              const helperClosedSibling = closeToolbarMenus(viewMenu);
              const helperState = {
                fileOpen: fileMenu.open,
                editOpen: editMenu.open,
                viewOpen: viewMenu.open
              };

              fileMenu.open = true;
              editMenu.open = true;
              viewMenu.open = true;
              fileMenu.dispatchEvent({ type: 'toggle', target: fileMenu, bubbles: false });
              const toggleState = {
                fileOpen: fileMenu.open,
                editOpen: editMenu.open,
                viewOpen: viewMenu.open
              };

              let fileInputClicked = false;
              document.getElementById('fileInput').click = () => {
                fileInputClicked = true;
              };
              fileMenu.open = true;
              document.getElementById('loadBtn').onclick();
              const fileActionClosed = !fileMenu.open;

              editMenu.open = true;
              document.getElementById('undoBtn').onclick();
              const editActionClosed = !editMenu.open;

              viewMenu.open = true;
              labelModeSelect.value = 'all';
              labelModeSelect.dispatchEvent({
                type: 'change', target: labelModeSelect, bubbles: false
              });
              const viewControlState = {
                stayedOpen: viewMenu.open,
                labelMode
              };
              viewMenu.open = false;
              setActiveTool('pan');
              selected = { kind: 'node', nodeId: 42 };

              const focusedEditSummary = editMenuSummary;
              editMenu.contains = target => target === focusedEditSummary;
              fileMenu.open = true;
              document.dispatchEvent({
                type: 'focusin', target: focusedEditSummary, bubbles: false
              });
              const focusMovedClosedSibling = !fileMenu.open;

              const historyCountBeforeInputUndo = undoHistory.length;
              const inputUndo = {
                type: 'keydown',
                key: 'z',
                code: 'KeyZ',
                target: fakeElement('input'),
                ctrlKey: true,
                metaKey: false,
                altKey: false,
                shiftKey: false,
                defaultPrevented: false,
                preventDefault() { this.defaultPrevented = true; }
              };
              window.dispatchEvent(inputUndo);
              const nativeInputUndoState = {
                prevented: inputUndo.defaultPrevented,
                historyUnchanged: undoHistory.length === historyCountBeforeInputUndo
              };

              const insideTarget = document.getElementById('newBtn');
              fileMenu.contains = target => target === insideTarget;
              fileMenu.open = true;
              document.dispatchEvent(fakePointerEvent(
                'pointerdown', insideTarget, 301, 0, 0));
              const insidePointerKeptMenuOpen = fileMenu.open;

              document.dispatchEvent(fakePointerEvent(
                'pointerdown', fakeElement('div'), 302, 0, 0));
              const outsidePointerClosedMenus = toolbarMenus.every(menu => !menu.open);

              const focusedViewItem = document.getElementById('themeToggleBtn');
              let viewSummaryFocused = false;
              viewMenu.contains = target => target === focusedViewItem;
              viewMenuSummary.focus = () => { viewSummaryFocused = true; };
              document.activeElement = focusedViewItem;
              viewMenu.open = true;
              document.getElementById('validation-modal').style.display = 'none';
              const escape = {
                type: 'keydown',
                key: 'Escape',
                code: 'Escape',
                target: svg,
                ctrlKey: false,
                metaKey: false,
                altKey: false,
                shiftKey: false,
                defaultPrevented: false,
                preventDefault() { this.defaultPrevented = true; }
              };
              window.dispatchEvent(escape);

              return JSON.stringify({
                helperClosedSibling,
                helperState,
                toggleState,
                fileActionClosed,
                fileInputClicked,
                editActionClosed,
                viewControlState,
                focusMovedClosedSibling,
                nativeInputUndoState,
                insidePointerKeptMenuOpen,
                outsidePointerClosedMenus,
                escapeClosedMenu: !viewMenu.open,
                escapePrevented: escape.defaultPrevented,
                viewSummaryFocused,
                activeToolAfterEscape: activeTool,
                selectedAfterEscape: selected
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("helperClosedSibling").GetBoolean());
        Assert.False(root.GetProperty("helperState").GetProperty("fileOpen").GetBoolean());
        Assert.False(root.GetProperty("helperState").GetProperty("editOpen").GetBoolean());
        Assert.True(root.GetProperty("helperState").GetProperty("viewOpen").GetBoolean());
        Assert.True(root.GetProperty("toggleState").GetProperty("fileOpen").GetBoolean());
        Assert.False(root.GetProperty("toggleState").GetProperty("editOpen").GetBoolean());
        Assert.False(root.GetProperty("toggleState").GetProperty("viewOpen").GetBoolean());

        Assert.True(root.GetProperty("fileActionClosed").GetBoolean());
        Assert.True(root.GetProperty("fileInputClicked").GetBoolean());

        Assert.True(root.GetProperty("editActionClosed").GetBoolean());
        Assert.True(root.GetProperty("viewControlState").GetProperty("stayedOpen").GetBoolean());
        Assert.Equal("all", root.GetProperty("viewControlState").GetProperty("labelMode").GetString());
        Assert.True(root.GetProperty("focusMovedClosedSibling").GetBoolean());
        Assert.False(root.GetProperty("nativeInputUndoState").GetProperty("prevented").GetBoolean());
        Assert.True(root.GetProperty("nativeInputUndoState").GetProperty("historyUnchanged").GetBoolean());
        Assert.True(root.GetProperty("insidePointerKeptMenuOpen").GetBoolean());
        Assert.True(root.GetProperty("outsidePointerClosedMenus").GetBoolean());
        Assert.True(root.GetProperty("escapeClosedMenu").GetBoolean());
        Assert.True(root.GetProperty("escapePrevented").GetBoolean());
        Assert.True(root.GetProperty("viewSummaryFocused").GetBoolean());
        Assert.Equal("pan", root.GetProperty("activeToolAfterEscape").GetString());
        Assert.Equal(42, root.GetProperty("selectedAfterEscape").GetProperty("nodeId").GetInt32());
    }

    [Fact]
    public void AuthoringPaletteOpensPinsPersistsAndClosesOnEscape()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const paletteClasses = new Set();
              authoringPalette.classList = {
                add(name) { paletteClasses.add(name); },
                remove(name) { paletteClasses.delete(name); },
                contains(name) { return paletteClasses.has(name); },
                toggle(name, force) {
                  const enabled = force === undefined ? !paletteClasses.has(name) : Boolean(force);
                  if (enabled) paletteClasses.add(name);
                  else paletteClasses.delete(name);
                  return enabled;
                }
              };
              const handleClasses = new Set();
              authoringPaletteHandle.classList = {
                add(name) { handleClasses.add(name); },
                remove(name) { handleClasses.delete(name); },
                contains(name) { return handleClasses.has(name); },
                toggle(name, force) {
                  const enabled = force === undefined ? !handleClasses.has(name) : Boolean(force);
                  if (enabled) handleClasses.add(name);
                  else handleClasses.delete(name);
                  return enabled;
                }
              };
              authoringPaletteContent.contains = target =>
                [authoringPalettePin, addStepBtn, addPhaseBtn, connectBtn,
                 selectToolBtn, rectangleSelectToolBtn, panToolBtn].includes(target);

              let handleFocused = false;
              let pinBlurred = false;
              authoringPaletteHandle.focus = () => {
                handleFocused = true;
                document.activeElement = authoringPaletteHandle;
              };
              selectToolBtn.focus = () => { document.activeElement = selectToolBtn; };
              authoringPalettePin.blur = () => {
                pinBlurred = true;
                document.activeElement = null;
              };

              setActiveTool('select');
              const initial = {
                expanded: authoringPaletteHandle.getAttribute('aria-expanded'),
                pinPressed: authoringPalettePin.getAttribute('aria-pressed')
              };

              authoringPaletteHandle.dispatchEvent({
                type: 'pointerenter', target: authoringPaletteHandle, bubbles: false
              });
              const hoverOpened = {
                openClass: paletteClasses.has('is-open'),
                expanded: authoringPaletteHandle.getAttribute('aria-expanded')
              };
              authoringPaletteHandle.dispatchEvent({
                type: 'pointerleave', target: authoringPaletteHandle, bubbles: false
              });
              const hoverStayedOpen = paletteClasses.has('is-open');
              selectToolBtn.onclick();
              const hoverActionClosed = !paletteClasses.has('is-open');

              authoringPaletteHandle.onclick();
              const opened = {
                openClass: paletteClasses.has('is-open'),
                expanded: authoringPaletteHandle.getAttribute('aria-expanded'),
                focusedTool: document.activeElement === selectToolBtn
              };

              document.activeElement = authoringPalettePin;
              authoringPalettePin.onclick();
              const pinned = {
                pinnedClass: paletteClasses.has('is-pinned'),
                openClass: paletteClasses.has('is-open'),
                expanded: authoringPaletteHandle.getAttribute('aria-expanded'),
                pinPressed: authoringPalettePin.getAttribute('aria-pressed'),
                stored: localStorage.getItem('flowbit.authoringPalettePinned')
              };

              const nodesBeforePinnedAction = model.flowNodes.length;
              addStepBtn.onclick();
              const pinnedAction = {
                stillPinned: paletteClasses.has('is-pinned'),
                nodeAdded: model.flowNodes.length === nodesBeforePinnedAction + 1
              };

              document.activeElement = authoringPalettePin;
              authoringPalettePin.onclick();
              const unpinned = {
                pinnedClass: paletteClasses.has('is-pinned'),
                expanded: authoringPaletteHandle.getAttribute('aria-expanded'),
                pinPressed: authoringPalettePin.getAttribute('aria-pressed'),
                stored: localStorage.getItem('flowbit.authoringPalettePinned'),
                pinBlurred
              };

              setConnectMode(true);
              const connectHandle = {
                active: handleClasses.has('active'),
                label: authoringPaletteHandle.getAttribute('aria-label')
              };
              setConnectMode(false);

              setActiveTool('pan');
              selected = { kind: 'node', nodeId: 42 };
              setAuthoringPaletteOpen(true);
              document.activeElement = selectToolBtn;
              document.getElementById('validation-modal').style.display = 'none';
              const escape = {
                type: 'keydown',
                key: 'Escape',
                code: 'Escape',
                target: selectToolBtn,
                ctrlKey: false,
                metaKey: false,
                altKey: false,
                shiftKey: false,
                defaultPrevented: false,
                preventDefault() { this.defaultPrevented = true; }
              };
              window.dispatchEvent(escape);
              const afterEscape = {
                openClass: paletteClasses.has('is-open'),
                expanded: authoringPaletteHandle.getAttribute('aria-expanded'),
                handleFocused,
                prevented: escape.defaultPrevented,
                activeTool,
                selected
              };

              return JSON.stringify({
                initial,
                hoverOpened,
                hoverStayedOpen,
                hoverActionClosed,
                opened,
                pinned,
                pinnedAction,
                unpinned,
                connectHandle,
                afterEscape
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("false", root.GetProperty("initial").GetProperty("expanded").GetString());
        Assert.Equal("false", root.GetProperty("initial").GetProperty("pinPressed").GetString());

        var hoverOpened = root.GetProperty("hoverOpened");
        Assert.True(hoverOpened.GetProperty("openClass").GetBoolean());
        Assert.Equal("true", hoverOpened.GetProperty("expanded").GetString());
        Assert.True(root.GetProperty("hoverStayedOpen").GetBoolean());
        Assert.True(root.GetProperty("hoverActionClosed").GetBoolean());

        var opened = root.GetProperty("opened");
        Assert.True(opened.GetProperty("openClass").GetBoolean());
        Assert.Equal("true", opened.GetProperty("expanded").GetString());
        Assert.True(opened.GetProperty("focusedTool").GetBoolean());

        var pinned = root.GetProperty("pinned");
        Assert.True(pinned.GetProperty("pinnedClass").GetBoolean());
        Assert.False(pinned.GetProperty("openClass").GetBoolean());
        Assert.Equal("true", pinned.GetProperty("expanded").GetString());
        Assert.Equal("true", pinned.GetProperty("pinPressed").GetString());
        Assert.Equal("true", pinned.GetProperty("stored").GetString());
        Assert.True(root.GetProperty("pinnedAction").GetProperty("stillPinned").GetBoolean());
        Assert.True(root.GetProperty("pinnedAction").GetProperty("nodeAdded").GetBoolean());

        var unpinned = root.GetProperty("unpinned");
        Assert.False(unpinned.GetProperty("pinnedClass").GetBoolean());
        Assert.Equal("false", unpinned.GetProperty("expanded").GetString());
        Assert.Equal("false", unpinned.GetProperty("pinPressed").GetString());
        Assert.Equal("false", unpinned.GetProperty("stored").GetString());
        Assert.True(unpinned.GetProperty("pinBlurred").GetBoolean());

        var connectHandle = root.GetProperty("connectHandle");
        Assert.True(connectHandle.GetProperty("active").GetBoolean());
        Assert.Equal(
            "Show authoring tools; connect flow mode active",
            connectHandle.GetProperty("label").GetString());

        var afterEscape = root.GetProperty("afterEscape");
        Assert.False(afterEscape.GetProperty("openClass").GetBoolean());
        Assert.Equal("false", afterEscape.GetProperty("expanded").GetString());
        Assert.True(afterEscape.GetProperty("handleFocused").GetBoolean());
        Assert.True(afterEscape.GetProperty("prevented").GetBoolean());
        Assert.Equal("pan", afterEscape.GetProperty("activeTool").GetString());
        Assert.Equal(42, afterEscape.GetProperty("selected").GetProperty("nodeId").GetInt32());

        var restoredEngine = CreateEditorEngine(
            "localStorage.setItem('flowbit.authoringPalettePinned', 'true');");
        Assert.Equal(
            "true",
            restoredEngine.Evaluate(
                "authoringPaletteHandle.getAttribute('aria-expanded')").AsString());
        Assert.Equal(
            "true",
            restoredEngine.Evaluate(
                "authoringPalettePin.getAttribute('aria-pressed')").AsString());
    }

    [Fact]
    public void RectangleSelectToolSelectsIntersectingNodesAndMovesThemAsOneUndoableGroup()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'rectangle-select-smoke',
                name: 'Rectangle select smoke',
                initialEventId: null,
                variables: [],
                lanes: [
                  { id: 10, name: 'Left', x: 0, y: 0, w: 300, h: 300 },
                  { id: 20, name: 'Right', x: 300, y: 0, w: 400, h: 300 }
                ],
                flowNodes: [
                  { id: 1, name: 'One', type: NODE_TYPE.TASK, laneId: null, x: 100, y: 100 },
                  { id: 2, name: 'Two', type: NODE_TYPE.TASK, laneId: null, x: 350, y: 100 },
                  { id: 3, name: 'Outside', type: NODE_TYPE.TASK, laneId: null, x: 800, y: 500 }
                ],
                sequenceFlows: []
              };
              viewState = { x: 25, y: 35, zoom: 1.5 };
              resetHistory();
              setActiveTool('rectangle');

              const viewBefore = JSON.parse(JSON.stringify(viewState));
              svg.dispatchEvent(fakePointerEvent(
                'pointerdown', gridSurface, 301, 80, 80));
              svg.dispatchEvent(fakePointerEvent(
                'pointermove', svg, 301, 600, 250));
              const previewDrawn = rectangleSelection !== null &&
                selectionOverlayG.children.length > 0;
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 301, 600, 250));

              const afterSelection = {
                ids: [...selectedNodeIds],
                primary: selected,
                previewCleared: rectangleSelection === null && selectionOverlayG.innerHTML === '',
                viewUnchanged: JSON.stringify(viewState) === JSON.stringify(viewBefore),
                canvasPanStopped: canvasPan === null
              };

              onNodePointerDown(
                fakePointerEvent('pointerdown', fakeElement('g'), 302, 110, 110),
                getNode(1));
              svg.dispatchEvent(fakePointerEvent(
                'pointermove', svg, 302, 160, 150));
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 302, 160, 150));
              commitHistory();
              const moved = model.flowNodes.map(node => ({
                id: node.id, x: node.x, y: node.y, laneId: node.laneId
              }));
              const oneHistoryEntry = undoHistory.length === 1;

              undo();
              const undone = model.flowNodes.map(node => ({
                id: node.id, x: node.x, y: node.y, laneId: node.laneId
              }));
              redo();
              const redone = model.flowNodes.map(node => ({
                id: node.id, x: node.x, y: node.y, laneId: node.laneId
              }));

              return JSON.stringify({
                activeTool,
                svgTool: svg.dataset.activeTool,
                selectPressed: selectToolBtn.getAttribute('aria-pressed'),
                rectanglePressed: rectangleSelectToolBtn.getAttribute('aria-pressed'),
                panPressed: panToolBtn.getAttribute('aria-pressed'),
                handleTool: authoringPaletteHandle.dataset.activeTool,
                handleLabel: authoringPaletteHandle.getAttribute('aria-label'),
                hintHidden: hintEl.hidden,
                hintText: hintEl.innerHTML,
                previewDrawn,
                afterSelection,
                moved,
                oneHistoryEntry,
                undone,
                redone,
                selectedIdsAfterRedo: [...selectedNodeIds]
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("rectangle", root.GetProperty("activeTool").GetString());
        Assert.Equal("rectangle", root.GetProperty("svgTool").GetString());
        Assert.Equal("false", root.GetProperty("selectPressed").GetString());
        Assert.Equal("true", root.GetProperty("rectanglePressed").GetString());
        Assert.Equal("false", root.GetProperty("panPressed").GetString());
        Assert.Equal("rectangle", root.GetProperty("handleTool").GetString());
        Assert.Contains("Rectangle select", root.GetProperty("handleLabel").GetString());
        Assert.True(root.GetProperty("hintHidden").GetBoolean());
        Assert.Equal(string.Empty, root.GetProperty("hintText").GetString());
        Assert.True(root.GetProperty("previewDrawn").GetBoolean());

        var selection = root.GetProperty("afterSelection");
        Assert.Equal(new[] { 1, 2 }, selection.GetProperty("ids").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(1, selection.GetProperty("primary").GetProperty("nodeId").GetInt32());
        Assert.True(selection.GetProperty("previewCleared").GetBoolean());
        Assert.True(selection.GetProperty("viewUnchanged").GetBoolean());
        Assert.True(selection.GetProperty("canvasPanStopped").GetBoolean());

        var moved = root.GetProperty("moved").EnumerateArray().ToArray();
        Assert.Equal((150, 140, 10), (
            moved[0].GetProperty("x").GetInt32(),
            moved[0].GetProperty("y").GetInt32(),
            moved[0].GetProperty("laneId").GetInt32()));
        Assert.Equal((400, 140, 20), (
            moved[1].GetProperty("x").GetInt32(),
            moved[1].GetProperty("y").GetInt32(),
            moved[1].GetProperty("laneId").GetInt32()));
        Assert.Equal((800, 500), (
            moved[2].GetProperty("x").GetInt32(),
            moved[2].GetProperty("y").GetInt32()));
        Assert.True(root.GetProperty("oneHistoryEntry").GetBoolean());

        var undone = root.GetProperty("undone").EnumerateArray().ToArray();
        Assert.Equal((100, 100), (
            undone[0].GetProperty("x").GetInt32(),
            undone[0].GetProperty("y").GetInt32()));
        Assert.Equal((350, 100), (
            undone[1].GetProperty("x").GetInt32(),
            undone[1].GetProperty("y").GetInt32()));
        Assert.Equal(
            root.GetProperty("moved").GetRawText(),
            root.GetProperty("redone").GetRawText());
        Assert.Equal(new[] { 1, 2 }, root.GetProperty("selectedIdsAfterRedo")
            .EnumerateArray().Select(value => value.GetInt32()));
    }

    [Fact]
    public void RectangleSelectShortcutAndGridSnapPreserveGroupSpacing()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const shortcut = {
                type: 'keydown',
                key: 'r',
                code: 'KeyR',
                target: fakeElement('div'),
                ctrlKey: false,
                metaKey: false,
                altKey: false,
                shiftKey: false,
                preventDefault() { this.defaultPrevented = true; }
              };
              setActiveTool('pan');
              window.dispatchEvent(shortcut);
              const afterShortcut = {
                activeTool,
                prevented: shortcut.defaultPrevented === true,
                rectanglePressed: rectangleSelectToolBtn.getAttribute('aria-pressed')
              };

              const inputShortcut = {
                ...shortcut,
                target: fakeElement('input'),
                defaultPrevented: false
              };
              setActiveTool('pan');
              window.dispatchEvent(inputShortcut);
              const editableIgnored = activeTool === 'pan' && !inputShortcut.defaultPrevented;

              model = {
                id: 'rectangle-grid-smoke',
                name: 'Rectangle grid smoke',
                initialEventId: null,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Anchor', type: NODE_TYPE.TASK, laneId: null, x: 101, y: 109 },
                  { id: 2, name: 'Follower', type: NODE_TYPE.TASK, laneId: null, x: 349, y: 137 }
                ],
                sequenceFlows: []
              };
              setGridSize(24, false);
              setSnapToGrid(true, false);
              selectNodes([1, 2], 1);
              setActiveTool('rectangle');
              onNodePointerDown(
                fakePointerEvent('pointerdown', fakeElement('g'), 311, 111, 119),
                getNode(1));
              svg.dispatchEvent(fakePointerEvent(
                'pointermove', svg, 311, 161, 160));
              const moved = model.flowNodes.map(node => ({ x: node.x, y: node.y }));
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 311, 161, 160));

              return JSON.stringify({
                afterShortcut,
                editableIgnored,
                moved,
                spacing: {
                  x: model.flowNodes[1].x - model.flowNodes[0].x,
                  y: model.flowNodes[1].y - model.flowNodes[0].y
                }
              });
            })()
            """).AsString());

        var root = result.RootElement;
        var shortcut = root.GetProperty("afterShortcut");
        Assert.Equal("rectangle", shortcut.GetProperty("activeTool").GetString());
        Assert.True(shortcut.GetProperty("prevented").GetBoolean());
        Assert.Equal("true", shortcut.GetProperty("rectanglePressed").GetString());
        Assert.True(root.GetProperty("editableIgnored").GetBoolean());

        var moved = root.GetProperty("moved").EnumerateArray().ToArray();
        Assert.Equal((144, 144), (
            moved[0].GetProperty("x").GetInt32(),
            moved[0].GetProperty("y").GetInt32()));
        Assert.Equal((392, 172), (
            moved[1].GetProperty("x").GetInt32(),
            moved[1].GetProperty("y").GetInt32()));
        Assert.Equal(248, root.GetProperty("spacing").GetProperty("x").GetInt32());
        Assert.Equal(28, root.GetProperty("spacing").GetProperty("y").GetInt32());
    }

    [Fact]
    public void PanToolMovesOnlyTheViewportAndPreservesWorkflowInteractionState()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'pan-tool-smoke',
                name: 'Pan tool smoke',
                initialEventId: 1,
                variables: [],
                lanes: [{ id: 10, name: 'Operations', x: 40, y: 50, w: 700, h: 280 }],
                flowNodes: [{
                  id: 1,
                  name: 'Review',
                  type: NODE_TYPE.USER_TASK,
                  laneId: 10,
                  x: 120,
                  y: 130
                }],
                sequenceFlows: []
              };
              selected = { kind: 'node', nodeId: 1 };
              connectMode = true;
              connectSource = 1;
              viewState = { x: 20, y: 30, zoom: 2 };
              const before = JSON.parse(JSON.stringify({
                lanes: model.lanes,
                flowNodes: model.flowNodes,
                selected
              }));

              setActiveTool('pan');
              const target = fakeElement('g');
              target.parentNode = svg;
              let targetClickReached = false;
              target.addEventListener('pointerdown', event => onNodePointerDown(event, model.flowNodes[0]));
              target.addEventListener('click', () => { targetClickReached = true; });
              target.dispatchEvent(fakePointerEvent('pointerdown', target, 101, 200, 160));
              svg.dispatchEvent(fakePointerEvent('pointermove', svg, 101, 260, 200));
              svg.dispatchEvent(fakePointerEvent('pointerup', svg, 101, 260, 200));
              const click = fakePointerEvent('click', target, 101, 260, 200);
              target.dispatchEvent(click);

              return JSON.stringify({
                before,
                after: {
                  lanes: model.lanes,
                  flowNodes: model.flowNodes,
                  selected
                },
                viewState,
                activeTool,
                svgActiveTool: svg.dataset.activeTool,
                selectPressed: selectToolBtn.getAttribute('aria-pressed'),
                panPressed: panToolBtn.getAttribute('aria-pressed'),
                connectMode,
                connectSource,
                hintHidden: hintEl.hidden,
                hintText: hintEl.innerHTML,
                panFinished: canvasPan === null,
                itemDragBlocked: drag === null,
                clickPrevented: click.defaultPrevented,
                targetClickReached
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("pan", root.GetProperty("activeTool").GetString());
        Assert.Equal("pan", root.GetProperty("svgActiveTool").GetString());
        Assert.Equal("false", root.GetProperty("selectPressed").GetString());
        Assert.Equal("true", root.GetProperty("panPressed").GetString());
        Assert.False(root.GetProperty("connectMode").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("connectSource").ValueKind);
        Assert.True(root.GetProperty("hintHidden").GetBoolean());
        Assert.Equal(string.Empty, root.GetProperty("hintText").GetString());
        Assert.True(root.GetProperty("panFinished").GetBoolean());
        Assert.True(root.GetProperty("itemDragBlocked").GetBoolean());
        Assert.True(root.GetProperty("clickPrevented").GetBoolean());
        Assert.False(root.GetProperty("targetClickReached").GetBoolean());
        Assert.Equal(
            root.GetProperty("before").GetRawText(),
            root.GetProperty("after").GetRawText());

        var viewState = root.GetProperty("viewState");
        Assert.Equal(-10, viewState.GetProperty("x").GetDouble());
        Assert.Equal(10, viewState.GetProperty("y").GetDouble());
        Assert.Equal(2, viewState.GetProperty("zoom").GetDouble());
    }

    [Fact]
    public void PanToolInspectsNodesAndFlowsWithoutMovingOrDeletingThem()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'pan-inspection-smoke',
                name: 'Pan inspection smoke',
                initialEventId: null,
                variables: [],
                lanes: [],
                flowNodes: [
                  {
                    id: 1,
                    name: 'Review',
                    type: NODE_TYPE.USER_TASK,
                    laneId: null,
                    x: 120,
                    y: 130
                  },
                  {
                    id: 2,
                    name: 'Archive',
                    type: NODE_TYPE.TASK,
                    laneId: null,
                    x: 360,
                    y: 130
                  }
                ],
                sequenceFlows: [{
                  id: 101,
                  name: 'Approved',
                  sourceRef: 1,
                  targetRef: 2,
                  attributes: [],
                  roles: [],
                  variables: []
                }]
              };
              selected = null;
              selectedNodeIds.clear();
              viewState = { x: 20, y: 30, zoom: 2 };
              render();
              setActiveTool('pan');

              const descendants = root => [
                root,
                ...(root.children || []).flatMap(descendants)
              ];
              const latestInspectorButton = text => descendants(inspector)
                .filter(element => element.textContent === text)
                .at(-1);
              const panTarget = (kind, id) => {
                const group = fakeElement('g');
                const target = fakeElement(kind === 'node' ? 'rect' : 'path');
                group.parentNode = svg;
                target.parentNode = group;
                if (kind === 'node') group.dataset.id = String(id);
                else group.dataset.flow = String(id);
                target.closest = selector => {
                  if (kind === 'node' && selector === '.node[data-id]') return group;
                  if (kind === 'flow' && selector === '.edge[data-flow]') return group;
                  return null;
                };
                return target;
              };
              const stationaryPanClick = (target, pointerId, x, y, jitter = false) => {
                target.dispatchEvent(fakePointerEvent(
                  'pointerdown', target, pointerId, x, y));
                const endX = x + (jitter ? 2 : 0);
                const endY = y + (jitter ? 1 : 0);
                if (jitter) {
                  svg.dispatchEvent(fakePointerEvent(
                    'pointermove', svg, pointerId, endX, endY));
                }
                svg.dispatchEvent(fakePointerEvent(
                  'pointerup', svg, pointerId, endX, endY));
                const click = fakePointerEvent('click', target, pointerId, endX, endY);
                target.dispatchEvent(click);
              };
              const pressDelete = () => {
                const event = {
                  type: 'keydown',
                  key: 'Delete',
                  code: 'Delete',
                  target: document.body,
                  ctrlKey: false,
                  metaKey: false,
                  altKey: false,
                  shiftKey: false,
                  defaultPrevented: false,
                  preventDefault() { this.defaultPrevented = true; }
                };
                window.dispatchEvent(event);
                return event;
              };

              setActiveTool('select');
              select('flow', 101);
              const preexistingFlowDeleteButton = latestInspectorButton('Delete flow');
              const beforeModeSwitchDelete = JSON.stringify({
                flowNodes: model.flowNodes,
                sequenceFlows: model.sequenceFlows,
                selected
              });
              const enabledInSelect = !preexistingFlowDeleteButton.disabled;
              setActiveTool('pan');
              const disabledAfterSwitchToPan = preexistingFlowDeleteButton.disabled;
              preexistingFlowDeleteButton.onclick();
              const modeSwitchProtection = {
                enabledInSelect,
                disabledAfterSwitchToPan,
                deletionProtected: JSON.stringify({
                  flowNodes: model.flowNodes,
                  sequenceFlows: model.sequenceFlows,
                  selected
                }) === beforeModeSwitchDelete
              };

              const nodeTarget = panTarget('node', 2);
              stationaryPanClick(nodeTarget, 201, 400, 180, true);
              const nodeDeleteButton = latestInspectorButton('Delete node');
              const beforeNodeDelete = JSON.stringify({
                flowNodes: model.flowNodes,
                sequenceFlows: model.sequenceFlows,
                selected
              });
              nodeDeleteButton.onclick();
              pressDelete();
              const nodeInspection = {
                selected,
                inspectorShowsNode: inspector.innerHTML.includes('<h2>Node #2</h2>'),
                deleteDisabled: nodeDeleteButton.disabled,
                deleteProtected: JSON.stringify({
                  flowNodes: model.flowNodes,
                  sequenceFlows: model.sequenceFlows,
                  selected
                }) === beforeNodeDelete
              };

              const flowTarget = panTarget('flow', 101);
              stationaryPanClick(flowTarget, 202, 280, 180);
              const flowDeleteButton = latestInspectorButton('Delete flow');
              const beforeFlowDelete = JSON.stringify({
                flowNodes: model.flowNodes,
                sequenceFlows: model.sequenceFlows,
                selected
              });
              flowDeleteButton.onclick();
              pressDelete();
              const flowInspection = {
                selected,
                inspectorShowsFlow: inspector.innerHTML.includes('<h2>Flow #101</h2>'),
                deleteDisabled: flowDeleteButton.disabled,
                deleteProtected: JSON.stringify({
                  flowNodes: model.flowNodes,
                  sequenceFlows: model.sequenceFlows,
                  selected
                }) === beforeFlowDelete
              };

              const beforeDrag = JSON.stringify({
                flowNodes: model.flowNodes,
                sequenceFlows: model.sequenceFlows,
                selected
              });
              const viewBeforeDrag = JSON.stringify(viewState);
              nodeTarget.dispatchEvent(fakePointerEvent(
                'pointerdown', nodeTarget, 203, 400, 180));
              [1, 2, 3, 4, 5].forEach(offset => {
                svg.dispatchEvent(fakePointerEvent(
                  'pointermove', svg, 203, 400 + offset, 180));
              });
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 203, 405, 180));
              const dragClick = fakePointerEvent('click', nodeTarget, 203, 405, 180);
              nodeTarget.dispatchEvent(dragClick);
              const dragResult = {
                modelAndSelectionPreserved: JSON.stringify({
                  flowNodes: model.flowNodes,
                  sequenceFlows: model.sequenceFlows,
                  selected
                }) === beforeDrag,
                viewportMoved: JSON.stringify(viewState) !== viewBeforeDrag,
                itemDragBlocked: drag === null,
                clickPrevented: dragClick.defaultPrevented
              };

              const beforeFlowDrag = JSON.stringify({
                flowNodes: model.flowNodes,
                sequenceFlows: model.sequenceFlows,
                selected
              });
              const viewBeforeFlowDrag = JSON.stringify(viewState);
              flowTarget.dispatchEvent(fakePointerEvent(
                'pointerdown', flowTarget, 204, 280, 180));
              svg.dispatchEvent(fakePointerEvent(
                'pointermove', svg, 204, 330, 220));
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 204, 330, 220));
              const flowDragClick = fakePointerEvent('click', flowTarget, 204, 330, 220);
              flowTarget.dispatchEvent(flowDragClick);
              const flowDragResult = {
                modelAndSelectionPreserved: JSON.stringify({
                  flowNodes: model.flowNodes,
                  sequenceFlows: model.sequenceFlows,
                  selected
                }) === beforeFlowDrag,
                viewportMoved: JSON.stringify(viewState) !== viewBeforeFlowDrag,
                itemDragBlocked: drag === null,
                clickPrevented: flowDragClick.defaultPrevented
              };

              setActiveTool('select');
              const selectModeDeleteEnabled = !flowDeleteButton.disabled;
              flowDeleteButton.onclick();
              const selectModeDeletion = {
                flowRemoved: model.sequenceFlows.length === 0,
                sourceSelected: selected?.kind === 'node' && selected.nodeId === 1
              };

              return JSON.stringify({
                activeTool,
                modeSwitchProtection,
                nodeInspection,
                flowInspection,
                dragResult,
                flowDragResult,
                selectModeDeleteEnabled,
                selectModeDeletion,
                nodeCount: model.flowNodes.length,
                flowCount: model.sequenceFlows.length
              });
            })()
            """).AsString());

        var root = result.RootElement;

        var modeSwitchProtection = root.GetProperty("modeSwitchProtection");
        Assert.True(modeSwitchProtection.GetProperty("enabledInSelect").GetBoolean());
        Assert.True(modeSwitchProtection.GetProperty("disabledAfterSwitchToPan").GetBoolean());
        Assert.True(modeSwitchProtection.GetProperty("deletionProtected").GetBoolean());

        var nodeInspection = root.GetProperty("nodeInspection");
        Assert.Equal("node", nodeInspection.GetProperty("selected").GetProperty("kind").GetString());
        Assert.Equal(2, nodeInspection.GetProperty("selected").GetProperty("nodeId").GetInt32());
        Assert.True(nodeInspection.GetProperty("inspectorShowsNode").GetBoolean());
        Assert.True(nodeInspection.GetProperty("deleteDisabled").GetBoolean());
        Assert.True(nodeInspection.GetProperty("deleteProtected").GetBoolean());

        var flowInspection = root.GetProperty("flowInspection");
        Assert.Equal("flow", flowInspection.GetProperty("selected").GetProperty("kind").GetString());
        Assert.Equal(101, flowInspection.GetProperty("selected").GetProperty("flowId").GetInt32());
        Assert.True(flowInspection.GetProperty("inspectorShowsFlow").GetBoolean());
        Assert.True(flowInspection.GetProperty("deleteDisabled").GetBoolean());
        Assert.True(flowInspection.GetProperty("deleteProtected").GetBoolean());

        var dragResult = root.GetProperty("dragResult");
        Assert.True(dragResult.GetProperty("modelAndSelectionPreserved").GetBoolean());
        Assert.True(dragResult.GetProperty("viewportMoved").GetBoolean());
        Assert.True(dragResult.GetProperty("itemDragBlocked").GetBoolean());
        Assert.True(dragResult.GetProperty("clickPrevented").GetBoolean());

        var flowDragResult = root.GetProperty("flowDragResult");
        Assert.True(flowDragResult.GetProperty("modelAndSelectionPreserved").GetBoolean());
        Assert.True(flowDragResult.GetProperty("viewportMoved").GetBoolean());
        Assert.True(flowDragResult.GetProperty("itemDragBlocked").GetBoolean());
        Assert.True(flowDragResult.GetProperty("clickPrevented").GetBoolean());

        Assert.Equal("select", root.GetProperty("activeTool").GetString());
        Assert.True(root.GetProperty("selectModeDeleteEnabled").GetBoolean());
        Assert.True(root.GetProperty("selectModeDeletion").GetProperty("flowRemoved").GetBoolean());
        Assert.True(root.GetProperty("selectModeDeletion").GetProperty("sourceSelected").GetBoolean());
        Assert.Equal(2, root.GetProperty("nodeCount").GetInt32());
        Assert.Equal(0, root.GetProperty("flowCount").GetInt32());
    }

    [Fact]
    public void PanToolInspectsLanesAndWorkflowWithoutMovingOrDeletingThem()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'pan-lane-workflow-smoke',
                name: 'Pan lane and workflow smoke',
                initialEventId: null,
                variables: [],
                cancelRoles: [],
                unclaimRoles: [],
                taskAssignmentRoles: [],
                lanes: [{ id: 10, name: 'Operations', x: 80, y: 90, w: 640, h: 260 }],
                flowNodes: [{
                  id: 1,
                  name: 'Review',
                  type: NODE_TYPE.USER_TASK,
                  laneId: 10,
                  x: 180,
                  y: 150
                }],
                sequenceFlows: []
              };
              selected = { kind: 'node', nodeId: 1 };
              selectedNodeIds = new Set([1]);
              viewState = { x: 15, y: 25, zoom: 1.5 };
              render();
              setActiveTool('pan');

              const laneGroup = fakeElement('g');
              laneGroup.dataset.id = '10';
              laneGroup.parentNode = svg;
              const laneTarget = fakeElement('rect');
              laneTarget.parentNode = laneGroup;
              laneTarget.closest = selector =>
                selector === '.lane[data-id]' ? laneGroup : null;
              laneGroup.addEventListener('pointerdown', event =>
                onLanePointerDown(event, getLane(10)));

              const workflowTarget = fakeElement('rect');
              workflowTarget.parentNode = svg;
              workflowTarget.closest = () => null;

              const stationaryPanClick = (target, pointerId, x, y, jitter = false) => {
                target.dispatchEvent(fakePointerEvent(
                  'pointerdown', target, pointerId, x, y));
                const endX = x + (jitter ? 2 : 0);
                const endY = y + (jitter ? 1 : 0);
                if (jitter) {
                  svg.dispatchEvent(fakePointerEvent(
                    'pointermove', svg, pointerId, endX, endY));
                }
                svg.dispatchEvent(fakePointerEvent(
                  'pointerup', svg, pointerId, endX, endY));
                target.dispatchEvent(fakePointerEvent(
                  'click', target, pointerId, endX, endY));
              };
              const pressDelete = () => {
                window.dispatchEvent({
                  type: 'keydown',
                  key: 'Delete',
                  code: 'Delete',
                  target: document.body,
                  ctrlKey: false,
                  metaKey: false,
                  altKey: false,
                  shiftKey: false,
                  defaultPrevented: false,
                  preventDefault() { this.defaultPrevented = true; }
                });
              };

              const modelBeforeLaneClick = JSON.stringify(model);
              const viewBeforeLaneClick = JSON.stringify(viewState);
              stationaryPanClick(laneTarget, 301, 260, 180, true);
              const laneDeleteButton = inspectorItemDeleteButtons.at(-1);
              const laneInspection = {
                selectedKind: selected?.kind,
                selectedId: selected?.laneId,
                inspectorShowsLane: inspector.innerHTML.includes('<h2>Lane #10</h2>'),
                deleteDisabled: laneDeleteButton?.disabled === true,
                modelPreserved: JSON.stringify(model) === modelBeforeLaneClick,
                viewportPreserved: JSON.stringify(viewState) === viewBeforeLaneClick,
                itemGesturesBlocked: laneDrag === null && laneResize === null
              };

              const beforeLaneDelete = JSON.stringify({ model, selected });
              laneDeleteButton.onclick();
              pressDelete();
              const laneDeletionProtected =
                JSON.stringify({ model, selected }) === beforeLaneDelete;

              const beforeLaneDrag = JSON.stringify({ model, selected });
              const viewBeforeLaneDrag = JSON.stringify(viewState);
              laneTarget.dispatchEvent(fakePointerEvent(
                'pointerdown', laneTarget, 302, 260, 180));
              svg.dispatchEvent(fakePointerEvent(
                'pointermove', svg, 302, 315, 220));
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 302, 315, 220));
              const laneDragClick = fakePointerEvent(
                'click', laneTarget, 302, 315, 220);
              laneTarget.dispatchEvent(laneDragClick);
              const laneDragResult = {
                modelAndSelectionPreserved:
                  JSON.stringify({ model, selected }) === beforeLaneDrag,
                viewportMoved: JSON.stringify(viewState) !== viewBeforeLaneDrag,
                itemGesturesBlocked: laneDrag === null && laneResize === null,
                clickPrevented: laneDragClick.defaultPrevented
              };

              const beforeWorkflowDrag = JSON.stringify({ model, selected });
              const viewBeforeWorkflowDrag = JSON.stringify(viewState);
              workflowTarget.dispatchEvent(fakePointerEvent(
                'pointerdown', workflowTarget, 303, 800, 520));
              svg.dispatchEvent(fakePointerEvent(
                'pointermove', svg, 303, 850, 555));
              svg.dispatchEvent(fakePointerEvent(
                'pointerup', svg, 303, 850, 555));
              const workflowDragClick = fakePointerEvent(
                'click', workflowTarget, 303, 850, 555);
              workflowTarget.dispatchEvent(workflowDragClick);
              const workflowDragResult = {
                modelAndSelectionPreserved:
                  JSON.stringify({ model, selected }) === beforeWorkflowDrag,
                viewportMoved: JSON.stringify(viewState) !== viewBeforeWorkflowDrag,
                clickPrevented: workflowDragClick.defaultPrevented
              };

              const modelBeforeWorkflowClick = JSON.stringify(model);
              const viewBeforeWorkflowClick = JSON.stringify(viewState);
              stationaryPanClick(workflowTarget, 304, 800, 520, true);
              const workflowInspection = {
                selectedCleared: selected === null && selectedNodeIds.size === 0,
                inspectorShowsWorkflow:
                  inspector.innerHTML.includes('<h2>Workflow</h2>'),
                inspectorMentionsLanes:
                  inspector.innerHTML.includes('node, flow, or lane'),
                viewportPreserved:
                  JSON.stringify(viewState) === viewBeforeWorkflowClick,
                modelPreserved: JSON.stringify(model) === modelBeforeWorkflowClick,
                noItemDeleteButtons: inspectorItemDeleteButtons.length === 0
              };
              pressDelete();

              return JSON.stringify({
                activeTool,
                laneInspection,
                laneDeletionProtected,
                laneDragResult,
                workflowDragResult,
                workflowInspection,
                workflowDeleteProtected:
                  selected === null && JSON.stringify(model) === modelBeforeWorkflowClick,
                laneCount: model.lanes.length,
                nodeCount: model.flowNodes.length
              });
            })()
            """).AsString());

        var root = result.RootElement;

        Assert.Equal("pan", root.GetProperty("activeTool").GetString());

        var laneInspection = root.GetProperty("laneInspection");
        Assert.Equal("lane", laneInspection.GetProperty("selectedKind").GetString());
        Assert.Equal(10, laneInspection.GetProperty("selectedId").GetInt32());
        Assert.True(laneInspection.GetProperty("inspectorShowsLane").GetBoolean());
        Assert.True(laneInspection.GetProperty("deleteDisabled").GetBoolean());
        Assert.True(laneInspection.GetProperty("modelPreserved").GetBoolean());
        Assert.True(laneInspection.GetProperty("viewportPreserved").GetBoolean());
        Assert.True(laneInspection.GetProperty("itemGesturesBlocked").GetBoolean());
        Assert.True(root.GetProperty("laneDeletionProtected").GetBoolean());

        var laneDragResult = root.GetProperty("laneDragResult");
        Assert.True(laneDragResult.GetProperty("modelAndSelectionPreserved").GetBoolean());
        Assert.True(laneDragResult.GetProperty("viewportMoved").GetBoolean());
        Assert.True(laneDragResult.GetProperty("itemGesturesBlocked").GetBoolean());
        Assert.True(laneDragResult.GetProperty("clickPrevented").GetBoolean());

        var workflowDragResult = root.GetProperty("workflowDragResult");
        Assert.True(workflowDragResult.GetProperty("modelAndSelectionPreserved").GetBoolean());
        Assert.True(workflowDragResult.GetProperty("viewportMoved").GetBoolean());
        Assert.True(workflowDragResult.GetProperty("clickPrevented").GetBoolean());

        var workflowInspection = root.GetProperty("workflowInspection");
        Assert.True(workflowInspection.GetProperty("selectedCleared").GetBoolean());
        Assert.True(workflowInspection.GetProperty("inspectorShowsWorkflow").GetBoolean());
        Assert.True(workflowInspection.GetProperty("inspectorMentionsLanes").GetBoolean());
        Assert.True(workflowInspection.GetProperty("viewportPreserved").GetBoolean());
        Assert.True(workflowInspection.GetProperty("modelPreserved").GetBoolean());
        Assert.True(workflowInspection.GetProperty("noItemDeleteButtons").GetBoolean());
        Assert.True(root.GetProperty("workflowDeleteProtected").GetBoolean());
        Assert.Equal(1, root.GetProperty("laneCount").GetInt32());
        Assert.Equal(1, root.GetProperty("nodeCount").GetInt32());
    }

    [Fact]
    public void SwitchingToolsFinishesItemAndCanvasGestures()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'pan-tool-switch-smoke',
                name: 'Pan tool switch smoke',
                initialEventId: 1,
                variables: [],
                lanes: [{ id: 10, name: 'Operations', x: 40, y: 50, w: 700, h: 280 }],
                flowNodes: [{
                  id: 1,
                  name: 'Review',
                  type: NODE_TYPE.USER_TASK,
                  laneId: 10,
                  x: 120,
                  y: 130
                }],
                sequenceFlows: []
              };
              let node = model.flowNodes[0];
              let lane = model.lanes[0];
              resetHistory();

              setActiveTool('select');
              onNodePointerDown(fakePointerEvent('pointerdown', fakeElement('g'), 201, 130, 140), node);
              const nodeDragStarted = drag !== null;
              svg.dispatchEvent(fakePointerEvent('pointermove', svg, 201, 180, 190));
              const nodeWasMoved = node.x === 170 && node.y === 180;
              setActiveTool('pan');
              const nodeDragStopped = drag === null;
              const itemMoveCommitted = undoHistory.length === 1;
              undo();
              const itemMoveUndoable = getNode(1).x === 120 && getNode(1).y === 130;
              node = getNode(1);
              lane = getLane(10);

              setActiveTool('select');
              onLanePointerDown(fakePointerEvent('pointerdown', fakeElement('g'), 202, 50, 60), lane);
              const laneDragStarted = laneDrag !== null;
              setActiveTool('pan');
              const laneDragStopped = laneDrag === null;

              setActiveTool('select');
              onLaneResizePointerDown(fakePointerEvent('pointerdown', fakeElement('rect'), 203, 740, 330), lane);
              const laneResizeStarted = laneResize !== null;
              setActiveTool('pan');
              const laneResizeStopped = laneResize === null;

              viewState = { x: 0, y: 0, zoom: 1 };
              const target = fakeElement('g');
              svg.dispatchEvent(fakePointerEvent('pointerdown', target, 204, 200, 160));
              svg.dispatchEvent(fakePointerEvent('pointermove', target, 204, 240, 190));
              const viewBeforeSelect = JSON.parse(JSON.stringify(viewState));
              setActiveTool('select');
              const canvasPanStopped = canvasPan === null;
              const toolSwitchSuppressCleared = suppressCanvasClick === false;
              svg.dispatchEvent(fakePointerEvent('pointermove', target, 204, 300, 240));
              const viewAfterSelect = JSON.parse(JSON.stringify(viewState));

              setActiveTool('pan');
              svg.dispatchEvent(fakePointerEvent('pointerdown', target, 205, 200, 160));
              svg.dispatchEvent(fakePointerEvent('pointermove', target, 205, 240, 190));
              window.dispatchEvent({ type: 'blur' });
              const blurCanvasPanStopped = canvasPan === null;
              const blurSuppressCleared = suppressCanvasClick === false;

              svg.dispatchEvent(fakePointerEvent('pointerdown', target, 206, 200, 160));
              svg.dispatchEvent(fakePointerEvent('pointermove', target, 206, 240, 190));
              svg.dispatchEvent(fakePointerEvent(
                'lostpointercapture', svg, 206, 240, 190));
              const lostCaptureCanvasPanStopped = canvasPan === null;
              const lostCaptureSuppressCleared = suppressCanvasClick === false;
              setActiveTool('select');

              return JSON.stringify({
                nodeDragStarted,
                nodeWasMoved,
                nodeDragStopped,
                itemMoveCommitted,
                itemMoveUndoable,
                laneDragStarted,
                laneDragStopped,
                laneResizeStarted,
                laneResizeStopped,
                canvasPanStopped,
                toolSwitchSuppressCleared,
                blurCanvasPanStopped,
                blurSuppressCleared,
                lostCaptureCanvasPanStopped,
                lostCaptureSuppressCleared,
                viewBeforeSelect,
                viewAfterSelect,
                activeTool
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("nodeDragStarted").GetBoolean());
        Assert.True(root.GetProperty("nodeWasMoved").GetBoolean());
        Assert.True(root.GetProperty("nodeDragStopped").GetBoolean());
        Assert.True(root.GetProperty("itemMoveCommitted").GetBoolean());
        Assert.True(root.GetProperty("itemMoveUndoable").GetBoolean());
        Assert.True(root.GetProperty("laneDragStarted").GetBoolean());
        Assert.True(root.GetProperty("laneDragStopped").GetBoolean());
        Assert.True(root.GetProperty("laneResizeStarted").GetBoolean());
        Assert.True(root.GetProperty("laneResizeStopped").GetBoolean());
        Assert.True(root.GetProperty("canvasPanStopped").GetBoolean());
        Assert.True(root.GetProperty("toolSwitchSuppressCleared").GetBoolean());
        Assert.True(root.GetProperty("blurCanvasPanStopped").GetBoolean());
        Assert.True(root.GetProperty("blurSuppressCleared").GetBoolean());
        Assert.True(root.GetProperty("lostCaptureCanvasPanStopped").GetBoolean());
        Assert.True(root.GetProperty("lostCaptureSuppressCleared").GetBoolean());
        Assert.Equal("select", root.GetProperty("activeTool").GetString());
        Assert.Equal(
            root.GetProperty("viewBeforeSelect").GetRawText(),
            root.GetProperty("viewAfterSelect").GetRawText());
    }

    [Fact]
    public void NodeDragSnapsToTheConfiguredGridOnlyWhenEnabled()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'grid-snap-smoke',
                name: 'Grid snap smoke',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [{
                  id: 1,
                  name: 'Review',
                  type: NODE_TYPE.USER_TASK,
                  laneId: null,
                  x: 120,
                  y: 130,
                  attributes: [],
                  roles: [],
                  variables: []
                }],
                sequenceFlows: []
              };
              setActiveTool('select');

              const moveFromOrigin = pointerId => {
                const node = getNode(1);
                node.x = 120;
                node.y = 130;
                onNodePointerDown(
                  fakePointerEvent('pointerdown', fakeElement('g'), pointerId, 130, 140),
                  node);
                svg.dispatchEvent(
                  fakePointerEvent('pointermove', svg, pointerId, 187, 203));
                const position = { x: node.x, y: node.y };
                svg.dispatchEvent(
                  fakePointerEvent('pointerup', svg, pointerId, 187, 203));
                return position;
              };

              snapToGridInput.checked = false;
              snapToGridInput.dispatchEvent({
                type: 'change', target: snapToGridInput, bubbles: false
              });
              const free = moveFromOrigin(401);

              gridSizeInput.value = '20';
              gridSizeInput.dispatchEvent({
                type: 'change', target: gridSizeInput, bubbles: false
              });
              snapToGridInput.checked = true;
              snapToGridInput.dispatchEvent({
                type: 'change', target: snapToGridInput, bubbles: false
              });
              const snapped20 = moveFromOrigin(402);

              gridSizeInput.value = '25';
              gridSizeInput.dispatchEvent({
                type: 'change', target: gridSizeInput, bubbles: false
              });
              const snapped25 = moveFromOrigin(403);

              return JSON.stringify({
                free,
                snapped20,
                snapped25,
                snapEnabled: snapToGrid,
                configuredSize: gridSize,
                sizeInputValue: gridSizeInput.value,
                patternWidth: gridPattern.getAttribute('width'),
                patternHeight: gridPattern.getAttribute('height'),
                storedSnap: localStorage.getItem('flowbit.snapToGrid'),
                storedSize: localStorage.getItem('flowbit.gridSize'),
                dragCleared: drag === null
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(177, root.GetProperty("free").GetProperty("x").GetInt32());
        Assert.Equal(193, root.GetProperty("free").GetProperty("y").GetInt32());
        Assert.Equal(180, root.GetProperty("snapped20").GetProperty("x").GetInt32());
        Assert.Equal(200, root.GetProperty("snapped20").GetProperty("y").GetInt32());
        Assert.Equal(175, root.GetProperty("snapped25").GetProperty("x").GetInt32());
        Assert.Equal(200, root.GetProperty("snapped25").GetProperty("y").GetInt32());
        Assert.True(root.GetProperty("snapEnabled").GetBoolean());
        Assert.Equal(25, root.GetProperty("configuredSize").GetInt32());
        Assert.Equal("25", root.GetProperty("sizeInputValue").GetString());
        Assert.Equal("25", root.GetProperty("patternWidth").GetString());
        Assert.Equal("25", root.GetProperty("patternHeight").GetString());
        Assert.Equal("true", root.GetProperty("storedSnap").GetString());
        Assert.Equal("25", root.GetProperty("storedSize").GetString());
        Assert.True(root.GetProperty("dragCleared").GetBoolean());
    }

    [Fact]
    public void GridPreferencesRestoreOnEditorStartup()
    {
        var engine = CreateEditorEngine(
            """
            localStorage.setItem('flowbit.snapToGrid', 'true');
            localStorage.setItem('flowbit.gridSize', '36');
            """);

        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            JSON.stringify({
              snapToGrid,
              gridSize,
              checked: snapToGridInput.checked,
              inputValue: gridSizeInput.value,
              patternWidth: gridPattern.getAttribute('width'),
              patternHeight: gridPattern.getAttribute('height')
            })
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("snapToGrid").GetBoolean());
        Assert.Equal(36, root.GetProperty("gridSize").GetInt32());
        Assert.True(root.GetProperty("checked").GetBoolean());
        Assert.Equal("36", root.GetProperty("inputValue").GetString());
        Assert.Equal("36", root.GetProperty("patternWidth").GetString());
        Assert.Equal("36", root.GetProperty("patternHeight").GetString());
    }

    [Fact]
    public void UserTaskInspectorRendersWithoutLeakingTypeChangeCallbackState()
    {
        var engine = CreateEditorEngine();

        var exception = Record.Exception(() => engine.Execute(
            """
            model = {
              id: 1,
              name: 'Inspector smoke',
              initialEventId: null,
              variables: [],
              lanes: [],
              flowNodes: [{
                id: 1,
                name: 'Approve',
                type: NODE_TYPE.USER_TASK,
                laneId: null,
                x: 0,
                y: 0,
                roles: [],
                variables: [],
                requiresClaim: false,
                claimMode: 'fresh',
                requiresAssignment: false,
                assignmentMode: 'fresh',
                asyncBefore: true
              }],
              sequenceFlows: []
            };
            selected = { kind: 'node', nodeId: 1 };
            renderInspector();
            """));

        Assert.Null(exception);
    }

    [Fact]
    public void EightTimerBoundariesAreDistributedAroundCompactHost()
    {
        var engine = CreateEditorEngine();
        using var positions = JsonDocument.Parse(engine.Evaluate(
            """
            model = {
              id: 1,
              name: 'Boundary layout',
              initialEventId: null,
              variables: [],
              lanes: [],
              flowNodes: [
                {
                  id: 1,
                  name: 'Wait',
                  type: NODE_TYPE.TIMER_CATCH_EVENT,
                  laneId: null,
                  x: 100,
                  y: 100,
                  timer: { timeDuration: 'PT1H' }
                },
                ...Array.from({ length: 8 }, (_, index) => ({
                  id: index + 2,
                  name: 'Timer ' + (index + 1),
                  type: NODE_TYPE.TIMER_BOUNDARY_EVENT,
                  laneId: null,
                  x: 100,
                  y: 100,
                  attachedToRef: 1,
                  cancelActivity: false,
                  timer: { timeDuration: 'PT1H' }
                }))
              ],
              sequenceFlows: []
            };
            JSON.stringify(model.flowNodes.slice(1).map(nodePosition));
            """).AsString());

        var coordinates = positions.RootElement
            .EnumerateArray()
            .Select(point => (
                X: point.GetProperty("x").GetDouble(),
                Y: point.GetProperty("y").GetDouble()))
            .ToArray();

        Assert.Equal(8, coordinates.Distinct().Count());
        Assert.Contains(coordinates, point => point.X < 100);
        Assert.Contains(coordinates, point => point.X > 136);
        Assert.Contains(coordinates, point => point.Y < 100);
        Assert.Contains(coordinates, point => point.Y > 136);
    }

    [Fact]
    public void TimerBoundaryFlowRoutesOutsideItsAttachedHost()
    {
        var engine = CreateEditorEngine();
        using var route = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = {
                id: 'boundary-route',
                name: 'Boundary route',
                initialEventId: null,
                variables: [],
                lanes: [],
                flowNodes: [
                  {
                    id: 2,
                    name: 'First approval',
                    type: NODE_TYPE.USER_TASK,
                    laneId: null,
                    x: 367,
                    y: 109
                  },
                  {
                    id: 3,
                    name: 'Second approval',
                    type: NODE_TYPE.USER_TASK,
                    laneId: null,
                    x: 810,
                    y: 128
                  },
                  {
                    id: 5,
                    name: 'Second approval timer',
                    type: NODE_TYPE.TIMER_BOUNDARY_EVENT,
                    laneId: null,
                    x: 814,
                    y: 113,
                    attachedToRef: 3,
                    cancelActivity: true,
                    timer: { timeDuration: 'PT1M' }
                  }
                ],
                sequenceFlows: [{
                  id: 105,
                  name: 'auto-back',
                  sourceRef: 5,
                  targetRef: 2
                }]
              };
              const flow = model.sequenceFlows[0];
              const group = { a: 2, b: 5, flows: [flow], laneSpacing: 0 };
              const geometry = routePairGeometries(group).get(flow.id);
              const hostBounds = edgeNodeBounds(getNode(3));
              return JSON.stringify({
                startsInsideHost: edgePointInsideBox(geometry.start, hostBounds),
                crossesHost: edgeCurveHitsBox(geometry, hostBounds)
              });
            })()
            """).AsString());

        Assert.False(route.RootElement.GetProperty("startsInsideHost").GetBoolean());
        Assert.False(route.RootElement.GetProperty("crossesHost").GetBoolean());
    }

    [Fact]
    public void LoaderPreservesMalformedDurableMetadataForValidation()
    {
        var engine = CreateEditorEngine();
        using var loaded = JsonDocument.Parse(engine.Evaluate(
            """
            loadFromObject({
              id: 'malformed-import',
              name: 'Malformed import',
              variables: [],
              lanes: [],
              flowNodes: [
                {
                  id: 1,
                  name: 'Host',
                  type: 'task',
                  asyncBefore: 'false',
                  asyncAfter: null,
                  job: {
                    failureHandling: 7,
                    retryDelays: 'PT1S'
                  }
                },
                {
                  id: 2,
                  name: 'Timer',
                  type: 'timerBoundaryEvent',
                  attachedToRef: 1,
                  cancelActivity: 'false',
                  timer: {
                    timeDate: 5,
                    timeDuration: 'PT1H'
                  }
                }
              ],
              sequenceFlows: []
            });
            model.flowNodes.forEach(applyTypeInvariants);
            JSON.stringify(model.flowNodes);
            """).AsString());

        var host = loaded.RootElement[0];
        Assert.Equal("false", host.GetProperty("asyncBefore").GetString());
        Assert.Equal(JsonValueKind.Null, host.GetProperty("asyncAfter").ValueKind);
        Assert.Equal(7, host.GetProperty("job").GetProperty("failureHandling").GetInt32());
        Assert.Equal(
            "PT1S",
            host.GetProperty("job").GetProperty("retryDelays").GetString());

        var timer = loaded.RootElement[1];
        Assert.Equal("false", timer.GetProperty("cancelActivity").GetString());
        Assert.Equal(5, timer.GetProperty("timer").GetProperty("timeDate").GetInt32());
        Assert.Equal(
            "PT1H",
            timer.GetProperty("timer").GetProperty("timeDuration").GetString());
    }

    [Fact]
    public void LoaderDropsLegacyAdministrativeFlagsAndPreservesNormalFlowRoles()
    {
        var engine = CreateEditorEngine();
        using var loaded = JsonDocument.Parse(engine.Evaluate(
            """
            loadFromObject({
              id: 'legacy-administrative-flags',
              name: 'Legacy administrative flags',
              variables: [],
              lanes: [],
              flowNodes: [
                { id: 1, name: 'Start', type: 'startEvent', x: 0, y: 0 },
                { id: 2, name: 'First approval', type: 'userTask', x: 200, y: 0 },
                { id: 3, name: 'Second approval', type: 'userTask', x: 400, y: 0 }
              ],
              sequenceFlows: [
                { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                {
                  id: 201,
                  name: 'Back',
                  sourceRef: 3,
                  targetRef: 2,
                  roles: ['admin'],
                  isAdministrative: true,
                  isBatchable: true
                }
              ]
            });
            JSON.stringify(model.sequenceFlows.find(flow => flow.id === 201));
            """).AsString());

        Assert.Equal("admin", loaded.RootElement.GetProperty("roles")[0].GetString());
        Assert.False(loaded.RootElement.TryGetProperty("isAdministrative", out _));
        Assert.False(loaded.RootElement.TryGetProperty("isBatchable", out _));
    }

    [Fact]
    public void LoaderPreservesOrderedAttributesAndCanonicalizesOnlyKeys()
    {
        var engine = CreateEditorEngine();
        using var loaded = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'attribute-round-trip',
                name: 'Attribute round trip',
                variables: [],
                lanes: [],
                flowNodes: [
                  {
                    id: 1,
                    name: 'Start',
                    type: 'startEvent',
                    attributes: [
                      { key: '  Owner  ', value: '  Alice\nBob  ' },
                      { key: 'Second', value: '' },
                      { key: '\uFEFFkeep-bom\uFEFF', value: 'bom remains part of the key' },
                      { key: '\u0085trim-nel\u0085', value: 'NEL is .NET whitespace' }
                    ]
                  },
                  { id: 2, name: 'Review', type: 'userTask', attributes: null },
                  { id: 3, name: 'End', type: 'endEvent' }
                ],
                sequenceFlows: [
                  {
                    id: 101,
                    name: 'Continue',
                    sourceRef: 1,
                    targetRef: 2,
                    attributes: [{ key: '  route  ', value: ' keep spaces ' }]
                  },
                  { id: 201, name: 'Finish', sourceRef: 2, targetRef: 3 }
                ]
              });
              const before = JSON.parse(JSON.stringify({
                node: model.flowNodes[0].attributes,
                nullNode: model.flowNodes[1].attributes,
                missingNode: model.flowNodes[2].attributes,
                flow: model.sequenceFlows[0].attributes,
                missingFlow: model.sequenceFlows[1].attributes
              }));
              model.flowNodes.forEach(canonicalizeAttributeKeys);
              model.sequenceFlows.forEach(canonicalizeAttributeKeys);
              model.flowNodes[1].type = 'exclusiveGateway';
              applyTypeInvariants(model.flowNodes[1]);
              return JSON.stringify({
                before,
                canonicalNode: model.flowNodes[0].attributes,
                canonicalFlow: model.sequenceFlows[0].attributes,
                afterTypeChange: model.flowNodes[1].attributes
              });
            })()
            """).AsString());

        var before = loaded.RootElement.GetProperty("before");
        var nodeAttributes = before.GetProperty("node");
        Assert.Equal("  Owner  ", nodeAttributes[0].GetProperty("key").GetString());
        Assert.Equal("  Alice\nBob  ", nodeAttributes[0].GetProperty("value").GetString());
        Assert.Equal("Second", nodeAttributes[1].GetProperty("key").GetString());
        Assert.Equal(string.Empty, nodeAttributes[1].GetProperty("value").GetString());
        Assert.Equal("\uFEFFkeep-bom\uFEFF", nodeAttributes[2].GetProperty("key").GetString());
        Assert.Equal("\u0085trim-nel\u0085", nodeAttributes[3].GetProperty("key").GetString());
        Assert.Empty(before.GetProperty("nullNode").EnumerateArray());
        Assert.Empty(before.GetProperty("missingNode").EnumerateArray());
        Assert.Empty(before.GetProperty("missingFlow").EnumerateArray());

        Assert.Equal(
            "Owner",
            loaded.RootElement.GetProperty("canonicalNode")[0].GetProperty("key").GetString());
        Assert.Equal(
            "\uFEFFkeep-bom\uFEFF",
            loaded.RootElement.GetProperty("canonicalNode")[2].GetProperty("key").GetString());
        Assert.Equal(
            "trim-nel",
            loaded.RootElement.GetProperty("canonicalNode")[3].GetProperty("key").GetString());
        Assert.Equal(
            "  Alice\nBob  ",
            loaded.RootElement.GetProperty("canonicalNode")[0].GetProperty("value").GetString());
        Assert.Equal(
            "route",
            loaded.RootElement.GetProperty("canonicalFlow")[0].GetProperty("key").GetString());
        Assert.Equal(
            " keep spaces ",
            loaded.RootElement.GetProperty("canonicalFlow")[0].GetProperty("value").GetString());
        Assert.Empty(loaded.RootElement.GetProperty("afterTypeChange").EnumerateArray());
    }

    [Fact]
    public void LegacyLoaderPreservesStepAndActionAttributes()
    {
        var engine = CreateEditorEngine();
        using var loaded = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'legacy-attributes',
                name: 'Legacy attributes',
                initialStepId: 1,
                phases: [],
                steps: [
                  {
                    id: 1,
                    name: 'Review',
                    type: 'task',
                    attributes: [{ key: 'node-key', value: 'node-value' }],
                    actions: [{
                      id: 201,
                      name: 'Finish',
                      toStepId: 2,
                      attributes: [{ key: 'flow-key', value: 'flow-value' }]
                    }]
                  },
                  { id: 2, name: 'End', type: 'end', attributes: null, actions: [] }
                ]
              });
              return JSON.stringify({
                node: model.flowNodes.find(node => node.id === 1).attributes,
                end: model.flowNodes.find(node => node.id === 2).attributes,
                flow: model.sequenceFlows.find(flow => flow.id === 201).attributes
              });
            })()
            """).AsString());

        Assert.Equal(
            "node-value",
            loaded.RootElement.GetProperty("node")[0].GetProperty("value").GetString());
        Assert.Empty(loaded.RootElement.GetProperty("end").EnumerateArray());
        Assert.Equal(
            "flow-value",
            loaded.RootElement.GetProperty("flow")[0].GetProperty("value").GetString());
    }

    [Fact]
    public void MalformedImportedAttributesDoNotCrashInspectorsAndRemainInvalid()
    {
        var engine = CreateEditorEngine();
        var exception = Record.Exception(() => engine.Execute(
            """
            loadFromObject({
              id: 'malformed-attributes',
              name: 'Malformed attributes',
              initialEventId: 1,
              variables: [],
              lanes: [],
              flowNodes: [
                { id: 1, name: 'Start', type: 'startEvent', attributes: 'not-a-list' },
                { id: 2, name: 'End', type: 'endEvent' }
              ],
              sequenceFlows: [{
                id: 101,
                name: '',
                sourceRef: 1,
                targetRef: 2,
                attributes: [null, { key: 7, value: null }]
              }]
            });
            selected = { kind: 'node', nodeId: 1 };
            renderInspector();
            selected = { kind: 'flow', flowId: 101 };
            renderInspector();
            """));

        Assert.Null(exception);
        using var errors = JsonDocument.Parse(engine.Evaluate(
            "JSON.stringify(validateModelForSave(model));").AsString());
        var messages = errors.RootElement.EnumerateArray()
            .Select(error => error.GetString() ?? string.Empty)
            .ToArray();
        Assert.Contains(messages, message =>
            message.Contains("Flow node #1 attributes must be an array", StringComparison.Ordinal));
        Assert.Contains(messages, message =>
            message.Contains("Sequence flow #101 attribute #1 must be an object", StringComparison.Ordinal));
        Assert.Contains(messages, message =>
            message.Contains("Sequence flow #101 attribute #2 key must be a string", StringComparison.Ordinal));
        Assert.Contains(messages, message =>
            message.Contains("Sequence flow #101 attribute #2 value must be a non-null string", StringComparison.Ordinal));
    }

    [Fact]
    public void NewNodesBoundariesAndFlowsStartWithEmptyAttributes()
    {
        var engine = CreateEditorEngine();
        using var created = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              model = newModel();
              addNode();
              const host = model.flowNodes[0];
              const flow = createFlow(host.id, host.id);
              addTimerBoundary(host);
              addErrorBoundary(host);
              return JSON.stringify({
                node: host.attributes,
                flow: flow.attributes,
                timerBoundary: model.flowNodes.find(node => node.type === 'timerBoundaryEvent').attributes,
                errorBoundary: model.flowNodes.find(node => node.type === 'errorBoundaryEvent').attributes
              });
            })()
            """).AsString());

        Assert.All(created.RootElement.EnumerateObject(), property =>
            Assert.Empty(property.Value.EnumerateArray()));
    }

    [Fact]
    public void AttributeInspectorAddsEditsAndDeletesRowsInOrder()
    {
        var engine = CreateEditorEngine();
        using var edited = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'attribute-editor',
                name: 'Attribute editor',
                variables: [],
                lanes: [],
                flowNodes: [{
                  id: 1,
                  name: 'Task',
                  type: 'userTask',
                  attributes: [
                    { key: 'first', value: 'one' },
                    { key: 'second', value: 'two' }
                  ]
                }],
                sequenceFlows: []
              });
              selected = { kind: 'node', nodeId: 1 };
              renderInspector();

              const descendants = root => {
                const result = [root];
                for (const child of root.children || []) result.push(...descendants(child));
                return result;
              };
              const latest = predicate => descendants(inspector).filter(predicate).pop();
              latest(element => element.textContent === '+ Add attribute').onclick();

              const deleteButtons = descendants(inspector)
                .filter(element => element.textContent === 'Delete attribute');
              deleteButtons.slice(-3)[1].onclick();

              const keyField = latest(element => element.innerHTML === '<label>Key</label>');
              const valueField = latest(element => element.innerHTML === '<label>Value</label>');
              keyField.children[0].value = 'third';
              keyField.children[0].oninput();
              valueField.children[0].value = 'three\nlines';
              valueField.children[0].oninput();
              return JSON.stringify(model.flowNodes[0].attributes);
            })()
            """).AsString());

        Assert.Equal(2, edited.RootElement.GetArrayLength());
        Assert.Equal("first", edited.RootElement[0].GetProperty("key").GetString());
        Assert.Equal("one", edited.RootElement[0].GetProperty("value").GetString());
        Assert.Equal("third", edited.RootElement[1].GetProperty("key").GetString());
        Assert.Equal("three\nlines", edited.RootElement[1].GetProperty("value").GetString());
    }

    [Fact]
    public void AttributeEditsRoundTripThroughUndoAndRedoHistory()
    {
        var engine = CreateEditorEngine();
        using var history = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'attribute-history',
                name: 'Attribute history',
                variables: [],
                lanes: [],
                flowNodes: [{
                  id: 1,
                  name: 'Task',
                  type: 'userTask',
                  attributes: [{ key: 'state', value: 'before' }]
                }],
                sequenceFlows: []
              });
              model.flowNodes[0].attributes[0].value = 'after';
              commitHistory();
              undo();
              const undone = model.flowNodes[0].attributes[0].value;
              redo();
              const redone = model.flowNodes[0].attributes[0].value;
              return JSON.stringify({ undone, redone });
            })()
            """).AsString());

        Assert.Equal("before", history.RootElement.GetProperty("undone").GetString());
        Assert.Equal("after", history.RootElement.GetProperty("redone").GetString());
    }

    [Fact]
    public void FriendlyDurationCycleAndLocalDateHelpersProduceCanonicalIsoValues()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const local = '2027-01-15T12:34:56';
              const utc = browserLocalDateTimeToUtcIso(local);
              return JSON.stringify({
                seconds: formatSimpleIsoDuration('10', 'seconds'),
                minutes: formatSimpleIsoDuration('5', 'minutes'),
                hours: formatSimpleIsoDuration('3', 'hours'),
                days: formatSimpleIsoDuration('2', 'days'),
                weeks: formatSimpleIsoDuration('1', 'weeks'),
                decimal: formatSimpleIsoDuration('0.5', 'seconds'),
                forever: formatSimpleIsoCycle('forever', null, 'P2D'),
                limited: formatSimpleIsoCycle('limited', 5, 'PT1H'),
                compoundIsAdvanced: parseSimpleIsoDuration('P1DT2H') === null,
                utc,
                localRoundTrip: isoToBrowserLocalDateTime(utc)
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("PT10S", root.GetProperty("seconds").GetString());
        Assert.Equal("PT5M", root.GetProperty("minutes").GetString());
        Assert.Equal("PT3H", root.GetProperty("hours").GetString());
        Assert.Equal("P2D", root.GetProperty("days").GetString());
        Assert.Equal("P1W", root.GetProperty("weeks").GetString());
        Assert.Equal("PT0.5S", root.GetProperty("decimal").GetString());
        Assert.Equal("R/P2D", root.GetProperty("forever").GetString());
        Assert.Equal("R5/PT1H", root.GetProperty("limited").GetString());
        Assert.True(root.GetProperty("compoundIsAdvanced").GetBoolean());
        Assert.EndsWith("Z", root.GetProperty("utc").GetString(), StringComparison.Ordinal);
        Assert.Equal("2027-01-15T12:34:56", root.GetProperty("localRoundTrip").GetString());
    }

    [Fact]
    public void FriendlyEditorsPreserveImportedValuesUntilTheUserEditsThem()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              function descendants(root) {
                const result = [root];
                root.children.forEach(child => result.push(...descendants(child)));
                return result;
              }
              function byData(root, key) {
                return descendants(root).find(element => element.dataset[key] === 'true');
              }

              let durationValue = 'P1DT2H';
              let durationChanges = 0;
              const durationParent = fakeElement();
              const duration = renderDurationEditor(
                durationParent,
                'Delay',
                durationValue,
                value => { durationValue = value; durationChanges++; });
              const durationAdvancedInitiallyOpen = byData(duration, 'advancedIso').open === true;
              const amount = byData(duration, 'durationAmount');
              const unit = byData(duration, 'durationUnit');
              amount.value = '2';
              unit.value = 'days';
              amount.oninput();

              let cycleValue = 'R/P1DT2H';
              let cycleChanges = 0;
              const cycle = renderCycleEditor(
                fakeElement(),
                cycleValue,
                value => { cycleValue = value; cycleChanges++; });

              let dateValue = '2027-01-15T12:34:56+03:00';
              let dateChanges = 0;
              const absolute = renderAbsoluteTimerEditor(
                fakeElement(),
                dateValue,
                value => { dateValue = value; dateChanges++; });

              return JSON.stringify({
                durationAdvancedInitiallyOpen,
                durationValue,
                durationChanges,
                cycleAdvancedOpen: byData(cycle, 'advancedIso').open === true,
                cycleValue,
                cycleChanges,
                dateAdvancedOpen: byData(absolute, 'advancedIso').open === true,
                dateValue,
                dateChanges
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.True(root.GetProperty("durationAdvancedInitiallyOpen").GetBoolean());
        Assert.Equal("P2D", root.GetProperty("durationValue").GetString());
        Assert.Equal(1, root.GetProperty("durationChanges").GetInt32());
        Assert.True(root.GetProperty("cycleAdvancedOpen").GetBoolean());
        Assert.Equal("R/P1DT2H", root.GetProperty("cycleValue").GetString());
        Assert.Equal(0, root.GetProperty("cycleChanges").GetInt32());
        Assert.False(root.GetProperty("dateAdvancedOpen").GetBoolean());
        Assert.Equal("2027-01-15T12:34:56+03:00", root.GetProperty("dateValue").GetString());
        Assert.Equal(0, root.GetProperty("dateChanges").GetInt32());
    }

    [Fact]
    public void TimerInspectorFriendlyControlsUpdateTheExistingIsoProperties()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              function descendants(root) {
                const result = [root];
                root.children.forEach(child => result.push(...descendants(child)));
                return result;
              }
              function byData(root, key) {
                return descendants(root).find(element => element.dataset[key] === 'true');
              }
              function renderTimer(node) {
                inspector.replaceChildren();
                renderTimerSection(node);
              }

              const durationNode = { timer: { timeDate: null, timeDuration: 'PT1H', timeCycle: null } };
              renderTimer(durationNode);
              let amount = byData(inspector, 'durationAmount');
              let unit = byData(inspector, 'durationUnit');
              amount.value = '2';
              unit.value = 'days';
              amount.oninput();

              const cycleNode = { timer: { timeDate: null, timeDuration: null, timeCycle: 'R/P2D' } };
              renderTimer(cycleNode);
              const mode = byData(inspector, 'cycleMode');
              const count = byData(inspector, 'cycleCount');
              count.value = '3';
              mode.value = 'limited';
              mode.onchange();

              const dateNode = {
                timer: { timeDate: '2027-01-15T12:34:56+03:00', timeDuration: null, timeCycle: null }
              };
              const dateBeforeRender = dateNode.timer.timeDate;
              renderTimer(dateNode);
              const dateAfterRender = dateNode.timer.timeDate;
              const local = byData(inspector, 'localDateTime');
              local.value = '2027-02-16T08:15:30';
              local.oninput();

              const compoundNode = {
                timer: { timeDate: null, timeDuration: 'P1DT2H', timeCycle: null }
              };
              const compoundBefore = JSON.stringify(compoundNode.timer);
              renderTimer(compoundNode);

              return JSON.stringify({
                duration: durationNode.timer.timeDuration,
                cycle: cycleNode.timer.timeCycle,
                dateBeforeRender,
                dateAfterRender,
                editedDate: dateNode.timer.timeDate,
                compoundPreserved: JSON.stringify(compoundNode.timer) === compoundBefore,
                compoundAdvancedOpen: byData(inspector, 'advancedIso').open === true
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("P2D", root.GetProperty("duration").GetString());
        Assert.Equal("R3/P2D", root.GetProperty("cycle").GetString());
        Assert.Equal("2027-01-15T12:34:56+03:00", root.GetProperty("dateBeforeRender").GetString());
        Assert.Equal(root.GetProperty("dateBeforeRender").GetString(), root.GetProperty("dateAfterRender").GetString());
        Assert.EndsWith("Z", root.GetProperty("editedDate").GetString(), StringComparison.Ordinal);
        Assert.True(root.GetProperty("compoundPreserved").GetBoolean());
        Assert.True(root.GetProperty("compoundAdvancedOpen").GetBoolean());
    }

    [Fact]
    public void RetryDelayCardsAddRemoveReorderAndEnforceTheLimit()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              function descendants(root) {
                const result = [root];
                root.children.forEach(child => result.push(...descendants(child)));
                return result;
              }
              function renderPolicy(node) {
                inspector.replaceChildren();
                renderAsyncJobSection(node);
                return descendants(inspector);
              }
              function button(elements, text) {
                return elements.find(element => element.tagName === 'BUTTON' && element.textContent === text);
              }

              const node = {
                id: 901,
                type: NODE_TYPE.TASK,
                asyncBefore: true,
                asyncAfter: false,
                job: { failureHandling: 'boundaryFirst', retryDelays: ['PT10S', 'PT1M', 'PT5M'] }
              };
              let elements = renderPolicy(node);
              const firstCard = elements.find(element => element.dataset.retryDelayIndex === '0');
              button(descendants(firstCard), 'Move down').onclick();
              const reordered = [...node.job.retryDelays];

              elements = renderPolicy(node);
              button(elements, 'Remove').onclick();
              const afterRemove = [...node.job.retryDelays];

              node.job.retryDelays = [];
              elements = renderPolicy(node);
              button(elements, '+ Add retry delay').onclick();
              const afterAdd = [...node.job.retryDelays];

              node.job.retryDelays = Array.from({ length: 10 }, () => 'PT1S');
              elements = renderPolicy(node);
              const limitButton = button(elements, 'Retry limit reached');

              return JSON.stringify({
                reordered,
                afterRemove,
                afterAdd,
                limitDisabled: limitButton.disabled,
                limitCount: node.job.retryDelays.length
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(new[] { "PT1M", "PT10S", "PT5M" },
            root.GetProperty("reordered").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "PT10S", "PT5M" },
            root.GetProperty("afterRemove").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "PT10S" },
            root.GetProperty("afterAdd").EnumerateArray().Select(value => value.GetString()));
        Assert.True(root.GetProperty("limitDisabled").GetBoolean());
        Assert.Equal(10, root.GetProperty("limitCount").GetInt32());
    }

    [Theory]
    [MemberData(nameof(ExampleWorkflowData.All), MemberType = typeof(ExampleWorkflowData))]
    public void ExampleWorkflowLoadsValidatesAndRendersInEditor(string fileName)
    {
        var json = ExampleWorkflowData.Read(fileName);
        var engine = CreateEditorEngine();
        engine.SetValue("exampleWorkflowJson", json);
        string? validationJson = null;

        var exception = Record.Exception(() =>
        {
            engine.Execute(
                """
                loadFromObject(JSON.parse(exampleWorkflowJson));
                render();
                """);
            validationJson = engine.Evaluate(
                "JSON.stringify(validateModelForSave(model));").AsString();
        });

        Assert.Null(exception);
        using var validation = JsonDocument.Parse(validationJson!);
        Assert.Empty(validation.RootElement.EnumerateArray());
        using var authored = JsonDocument.Parse(json);
        Assert.Equal(authored.RootElement.GetProperty("id").GetString(), engine.Evaluate("model.id").AsString());
    }

    [Fact]
    public void InboxVisibilityCondition_LoadsEditsPersistsAndClearsWithNodeType()
    {
        var engine = CreateEditorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'inbox-condition-editor',
                name: 'Inbox condition editor',
                variables: [
                  { id: 1, name: 'department', dataType: 'string', isArray: false }
                ],
                lanes: [],
                flowNodes: [
                  {
                    id: 1,
                    name: 'Automatic',
                    type: 'task',
                    inboxVisibilityCondition: '[sys.user] == \'must-be-cleared\''
                  },
                  {
                    id: 2,
                    name: 'Review',
                    type: 'userTask',
                    inboxVisibilityCondition: '[sys.claim.department] == [department]'
                  },
                  { id: 3, name: 'Legacy review', type: 'userTask' }
                ],
                sequenceFlows: []
              });

              const automatic = getNode(1);
              const review = getNode(2);
              const legacy = getNode(3);
              selected = { kind: 'node', nodeId: review.id };
              selectedNodeIds.clear();
              selectedNodeIds.add(review.id);
              renderInspector();

              const conditionField = inspector.children.find(child =>
                String(child.innerHTML).includes('Inbox visibility condition'));
              const input = conditionField.children.find(child => child.tagName === 'TEXTAREA');
              const status = conditionField.children.find(child => child.tagName === 'P');
              const initial = {
                value: input.value,
                invalid: input.getAttribute('aria-invalid'),
                status: status.textContent,
                automaticOmitted: JSON.stringify(automatic).indexOf('inboxVisibilityCondition') < 0,
                legacyOmitted: JSON.stringify(legacy).indexOf('inboxVisibilityCondition') < 0,
                helpVisible: inspector.children.some(child =>
                  String(child.className).includes('expression-help') &&
                  String(child.innerHTML).includes('PostgreSQL'))
              };

              input.value = "Contains([department], 'ops')";
              input.oninput();
              const invalid = {
                stored: review.inboxVisibilityCondition,
                aria: input.getAttribute('aria-invalid'),
                status: status.textContent
              };

              input.value = '[sys.claim.department] == [department] && ' +
                'Number([config.minimum]) > 10';
              input.oninput();
              const valid = {
                stored: review.inboxVisibilityCondition,
                aria: input.getAttribute('aria-invalid'),
                persisted: JSON.stringify(model).includes('inboxVisibilityCondition')
              };

              input.value = '   ';
              input.oninput();
              const blankIsNull = review.inboxVisibilityCondition === null;

              review.inboxVisibilityCondition = '[sys.user] == \'alice\'';
              review.type = NODE_TYPE.TASK;
              applyTypeInvariants(review);

              return JSON.stringify({
                initial,
                invalid,
                valid,
                blankIsNull,
                clearedOnTypeChange: !Object.prototype.hasOwnProperty.call(
                  review, 'inboxVisibilityCondition')
              });
            })()
            """).AsString());

        var root = result.RootElement;
        var initial = root.GetProperty("initial");
        Assert.Equal("[sys.claim.department] == [department]",
            initial.GetProperty("value").GetString());
        Assert.Equal("false", initial.GetProperty("invalid").GetString());
        Assert.Equal(string.Empty, initial.GetProperty("status").GetString());
        Assert.True(initial.GetProperty("automaticOmitted").GetBoolean());
        Assert.True(initial.GetProperty("legacyOmitted").GetBoolean());
        Assert.True(initial.GetProperty("helpVisible").GetBoolean());

        var invalid = root.GetProperty("invalid");
        Assert.Equal("Contains([department], 'ops')", invalid.GetProperty("stored").GetString());
        Assert.Equal("true", invalid.GetProperty("aria").GetString());
        Assert.Contains("Number(expr)", invalid.GetProperty("status").GetString(),
            StringComparison.Ordinal);

        var valid = root.GetProperty("valid");
        Assert.Equal("false", valid.GetProperty("aria").GetString());
        Assert.True(valid.GetProperty("persisted").GetBoolean());
        Assert.True(root.GetProperty("blankIsNull").GetBoolean());
        Assert.True(root.GetProperty("clearedOnTypeChange").GetBoolean());
    }

    // BEGIN STAGE 10 â€” node-type transition characterization. These tests
    // capture the live Type selector callback by temporarily wrapping
    // selectField while the node inspector is built, then restore the original
    // builder before invoking the captured callback. That exercises the same
    // entry point before and after the transition-owner extraction. Dialog
    // overrides only record/answer prompts; real invariants and the full
    // redraw pipeline run for every model assertion. History checks replay the
    // registered document change event, matching the browser commit path.
    private const string TypeTransitionHarnessJs = """
      function captureTypeCallback(nodeId) {
        const originalSelectField = selectField;
        let captured = null;
        selectField = (label, options, value, onChange) => {
          if (label === 'Type') { captured = onChange; return; }
          return originalSelectField(label, options, value, onChange);
        };
        selected = { kind: 'node', nodeId };
        selectedNodeIds = new Set([nodeId]);
        renderInspector();
        selectField = originalSelectField;
        if (!captured) throw new Error('Type callback was not captured for node ' + nodeId);
        return captured;
      }
      const typeDialogs = [];
      let typeConfirmResponse = true;
      const typeOriginalAlert = alert;
      const typeOriginalConfirm = confirm;
      alert = message => { typeDialogs.push({ kind: 'alert', message: String(message) }); };
      confirm = message => { typeDialogs.push({ kind: 'confirm', message: String(message) }); return typeConfirmResponse; };
      let typeInspectorRedraws = 0;
      let typeFullRenders = 0;
      const typeOriginalRenderInspector = renderInspector;
      const typeOriginalRender = render;
      renderInspector = () => { typeInspectorRedraws++; typeOriginalRenderInspector(); };
      render = () => { typeFullRenders++; typeOriginalRender(); };
      function resetTypeRedrawCounters() {
        typeInspectorRedraws = 0;
        typeFullRenders = 0;
      }
      function clearTypeDialogs() { typeDialogs.length = 0; }
      function replayTypeChangeCommit() {
        document.dispatchEvent({ type: 'change', target: fakeElement('select'), bubbles: false });
      }
      """;

    [Theory]
    [InlineData("endEvent")]
    [InlineData("errorEndEvent")]
    [InlineData("terminateEndEvent")]
    public void TypeTransition_RejectedEndTargetsPreserveModelAndPromptOrder(string targetType)
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        engine.SetValue("targetType", targetType);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'type-transition-reject',
                name: 'Type transition reject',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Review', type: 'userTask', x: 200, y: 0, roles: ['Agent'] },
                  { id: 3, name: 'Auto', type: 'task', x: 400, y: 0 },
                  { id: 4, name: 'Route', type: 'exclusiveGateway', x: 600, y: 0 },
                  { id: 5, name: 'Left', type: 'task', x: 800, y: -60 },
                  { id: 6, name: 'Right', type: 'task', x: 800, y: 60 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: 'Finish', sourceRef: 2, targetRef: 3 },
                  { id: 401, name: 'Left', sourceRef: 4, targetRef: 5, condition: 'a > 1', conditionPriority: 1 },
                  { id: 402, name: 'Right', sourceRef: 4, targetRef: 6, isDefault: true }
                ]
              });
              render();
              const userTaskCallback = captureTypeCallback(2);
              replayTypeChangeCommit();
              const settledHistory = undoHistory.length;
              const baseline = JSON.stringify(model);
              resetTypeRedrawCounters();
              clearTypeDialogs();
              userTaskCallback(targetType);
              const userTask = {
                dialogs: typeDialogs.slice(),
                typePreserved: getNode(2).type === 'userTask',
                modelPreserved: JSON.stringify(model) === baseline,
                renders: typeFullRenders,
                inspectorRedraws: typeInspectorRedraws,
                historyUnchanged: (replayTypeChangeCommit(), undoHistory.length) === settledHistory
              };
              clearTypeDialogs();
              const gatewayCallback = captureTypeCallback(4);
              resetTypeRedrawCounters();
              gatewayCallback(targetType);
              const gateway = {
                dialogs: typeDialogs.slice(),
                typePreserved: getNode(4).type === 'exclusiveGateway',
                modelPreserved: JSON.stringify(model) === baseline,
                renders: typeFullRenders,
                inspectorRedraws: typeInspectorRedraws
              };
              return JSON.stringify({ userTask, gateway });
            })()
            """).AsString());

        var root = result.RootElement;
        const string endGuardMessage =
            "Remove all outgoing sequence flows before changing this node to an end event.";
        foreach (var sectionName in new[] { "userTask", "gateway" })
        {
            var section = root.GetProperty(sectionName);
            var dialogs = section.GetProperty("dialogs");
            Assert.Equal(1, dialogs.GetArrayLength());
            Assert.Equal("alert", dialogs[0].GetProperty("kind").GetString());
            Assert.Equal(endGuardMessage, dialogs[0].GetProperty("message").GetString());
            Assert.True(section.GetProperty("typePreserved").GetBoolean());
            Assert.True(section.GetProperty("modelPreserved").GetBoolean());
            Assert.Equal(0, section.GetProperty("renders").GetInt32());
            Assert.Equal(1, section.GetProperty("inspectorRedraws").GetInt32());
        }
        Assert.True(root.GetProperty("userTask").GetProperty("historyUnchanged").GetBoolean());
    }

    [Theory]
    [InlineData("endEvent")]
    [InlineData("errorEndEvent")]
    [InlineData("terminateEndEvent")]
    public void TypeTransition_AcceptedEndTargetsNormalizeNode(string targetType)
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        engine.SetValue("targetType", targetType);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'type-transition-end',
                name: 'Type transition end',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  {
                    id: 2,
                    name: 'Review',
                    type: 'userTask',
                    x: 200,
                    y: 0,
                    roles: ['Agent'],
                    requiresClaim: true,
                    claimMode: 'previous',
                    inheritClaimFromNodeId: 1,
                    rolesVariable: 'reviewRoles'
                  }
                ],
                sequenceFlows: [{ id: 101, name: '', sourceRef: 1, targetRef: 2 }]
              });
              render();
              replayTypeChangeCommit();
              const settledHistory = undoHistory.length;
              const callback = captureTypeCallback(2);
              resetTypeRedrawCounters();
              clearTypeDialogs();
              callback(targetType);
              const node = getNode(2);
              return JSON.stringify({
                type: node.type,
                dialogs: typeDialogs.slice(),
                renders: typeFullRenders,
                roles: node.roles,
                rolesVariableAbsent: !Object.prototype.hasOwnProperty.call(node, 'rolesVariable'),
                requiresClaim: node.requiresClaim,
                claimMode: node.claimMode,
                inheritClaimFromNodeId: node.inheritClaimFromNodeId,
                variables: node.variables,
                assignee: node.assignee,
                errorCodePresent: Object.prototype.hasOwnProperty.call(node, 'errorCode'),
                errorCode: node.errorCode,
                errorDescription: node.errorDescription,
                settledHistory,
                historyCount: (replayTypeChangeCommit(), undoHistory.length)
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal(targetType, root.GetProperty("type").GetString());
        Assert.Empty(root.GetProperty("dialogs").EnumerateArray());
        Assert.Equal(1, root.GetProperty("renders").GetInt32());
        Assert.Empty(root.GetProperty("roles").EnumerateArray());
        Assert.True(root.GetProperty("rolesVariableAbsent").GetBoolean());
        Assert.False(root.GetProperty("requiresClaim").GetBoolean());
        Assert.Equal("fresh", root.GetProperty("claimMode").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("inheritClaimFromNodeId").ValueKind);
        Assert.Empty(root.GetProperty("variables").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("assignee").ValueKind);
        Assert.Equal(
            root.GetProperty("settledHistory").GetInt32() + 1,
            root.GetProperty("historyCount").GetInt32());

        if (targetType == "errorEndEvent")
        {
            Assert.True(root.GetProperty("errorCodePresent").GetBoolean());
            Assert.Equal(string.Empty, root.GetProperty("errorCode").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("errorDescription").ValueKind);
        }
        else
        {
            Assert.False(root.GetProperty("errorCodePresent").GetBoolean());
        }
    }

    [Fact]
    public void TypeTransition_GatewayDeclineKeepsModelHistoryAndRedo()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const loadFixture = () => loadFromObject({
                id: 'type-transition-gateway',
                name: 'Gateway decline',
                initialEventId: 1,
                variables: [{ id: 1, name: 'amount', dataType: 'number', isArray: false }],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Route', type: 'exclusiveGateway', x: 200, y: 0 },
                  { id: 3, name: 'Big', type: 'userTask', x: 400, y: -60 },
                  { id: 4, name: 'Small', type: 'userTask', x: 400, y: 60 },
                  { id: 5, name: 'Done', type: 'endEvent', x: 600, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  {
                    id: 201,
                    name: 'Large',
                    sourceRef: 2,
                    targetRef: 3,
                    condition: 'amount > 100',
                    conditionPriority: 1,
                    roles: ['Manager'],
                    variables: [{ id: 3, name: 'comment', dataType: 'string', isArray: false, required: false }]
                  },
                  { id: 202, name: 'Fallback', sourceRef: 2, targetRef: 4, isDefault: true },
                  { id: 301, name: '', sourceRef: 3, targetRef: 5 },
                  { id: 302, name: '', sourceRef: 4, targetRef: 5 }
                ]
              });

              // Establish an already-normalized baseline, then create a redo entry.
              loadFixture();
              render();
              model.flowNodes[1].name = 'Renamed route';
              replayTypeChangeCommit();
              undo();
              const settledHistory = undoHistory.length;
              const redoCount = redoHistory.length;
              const baseline = JSON.stringify(model);

              typeConfirmResponse = false;
              const declineCallback = captureTypeCallback(2);
              resetTypeRedrawCounters();
              clearTypeDialogs();
              declineCallback('task');
              const decline = {
                dialogs: typeDialogs.slice(),
                typePreserved: getNode(2).type === 'exclusiveGateway',
                modelPreserved: JSON.stringify(model) === baseline,
                renders: typeFullRenders,
                inspectorRedraws: typeInspectorRedraws,
                historyUnchanged: (replayTypeChangeCommit(), undoHistory.length) === settledHistory,
                redoPreserved: redoHistory.length === redoCount
              };

              // Accepted conversion of the same gateway to a single-outgoing task.
              clearTypeDialogs();
              loadFixture();
              render();
              replayTypeChangeCommit();
              const acceptSettled = undoHistory.length;
              const incomingBefore = JSON.stringify(model.sequenceFlows.find(f => f.id === 101));
              const acceptCallback = captureTypeCallback(2);
              resetTypeRedrawCounters();
              typeConfirmResponse = true;
              acceptCallback('task');
              const flow201 = model.sequenceFlows.find(f => f.id === 201);
              const accept = {
                dialogs: typeDialogs.slice(),
                type: getNode(2).type,
                renders: typeFullRenders,
                flow201PrunedIn: model.sequenceFlows.some(f => f.id === 201),
                flow202Removed: !model.sequenceFlows.some(f => f.id === 202),
                flow201: {
                  isDefault: flow201.isDefault,
                  condition: flow201.condition,
                  conditionPriority: flow201.conditionPriority,
                  roles: flow201.roles,
                  variables: flow201.variables
                },
                incomingPreserved: JSON.stringify(model.sequenceFlows.find(f => f.id === 101)) === incomingBefore,
                historyCount: (replayTypeChangeCommit(), undoHistory.length)
              };
              return JSON.stringify({ decline, accept, acceptSettled });
            })()
            """).AsString());

        var root = result.RootElement;
        const string gatewayPrompt =
            "Changing this node's gateway semantics clears outgoing defaults, conditions, priorities, roles, and variables. Continue?";

        var decline = root.GetProperty("decline");
        var dialogs = decline.GetProperty("dialogs");
        Assert.Equal(1, dialogs.GetArrayLength());
        Assert.Equal("confirm", dialogs[0].GetProperty("kind").GetString());
        Assert.Equal(gatewayPrompt, dialogs[0].GetProperty("message").GetString());
        Assert.True(decline.GetProperty("typePreserved").GetBoolean());
        Assert.True(decline.GetProperty("modelPreserved").GetBoolean());
        Assert.Equal(0, decline.GetProperty("renders").GetInt32());
        Assert.Equal(1, decline.GetProperty("inspectorRedraws").GetInt32());
        Assert.True(decline.GetProperty("historyUnchanged").GetBoolean());
        Assert.True(decline.GetProperty("redoPreserved").GetBoolean());

        var accept = root.GetProperty("accept");
        var acceptDialogs = accept.GetProperty("dialogs");
        Assert.Equal(1, acceptDialogs.GetArrayLength());
        Assert.Equal("confirm", acceptDialogs[0].GetProperty("kind").GetString());
        Assert.Equal(gatewayPrompt, acceptDialogs[0].GetProperty("message").GetString());
        Assert.Equal("task", accept.GetProperty("type").GetString());
        Assert.Equal(1, accept.GetProperty("renders").GetInt32());
        Assert.True(accept.GetProperty("flow201PrunedIn").GetBoolean());
        Assert.True(accept.GetProperty("flow202Removed").GetBoolean());
        var flow201 = accept.GetProperty("flow201");
        Assert.False(flow201.GetProperty("isDefault").GetBoolean());
        Assert.Equal(JsonValueKind.Null, flow201.GetProperty("condition").ValueKind);
        Assert.Equal(JsonValueKind.Null, flow201.GetProperty("conditionPriority").ValueKind);
        Assert.Empty(flow201.GetProperty("roles").EnumerateArray());
        Assert.Empty(flow201.GetProperty("variables").EnumerateArray());
        Assert.True(accept.GetProperty("incomingPreserved").GetBoolean());
        Assert.Equal(
            root.GetProperty("acceptSettled").GetInt32() + 1,
            accept.GetProperty("historyCount").GetInt32());
    }

    [Fact]
    public void TypeTransition_GatewayAcceptClearsRoutingAndKeepsReferences()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              // Leaving a gateway: routing metadata cleared, references preserved.
              loadFromObject({
                id: 'type-transition-gateway-leave',
                name: 'Gateway leave',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Route', type: 'exclusiveGateway', x: 200, y: 0 },
                  { id: 3, name: 'Big', type: 'userTask', x: 400, y: -60 },
                  { id: 4, name: 'Small', type: 'userTask', x: 400, y: 60 },
                  { id: 5, name: 'Done', type: 'endEvent', x: 600, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: 'Large', sourceRef: 2, targetRef: 3, condition: 'amount > 100', conditionPriority: 1, roles: ['Manager'], variables: [{ id: 3, name: 'comment', dataType: 'string', isArray: false, required: false }] },
                  { id: 202, name: 'Fallback', sourceRef: 2, targetRef: 4, isDefault: true },
                  { id: 301, name: '', sourceRef: 3, targetRef: 5 },
                  { id: 302, name: '', sourceRef: 4, targetRef: 5 }
                ]
              });
              render();
              const leaveCallback = captureTypeCallback(2);
              resetTypeRedrawCounters();
              clearTypeDialogs();
              typeConfirmResponse = true;
              leaveCallback('userTask');
              const flow201 = model.sequenceFlows.find(f => f.id === 201);
              const flow202 = model.sequenceFlows.find(f => f.id === 202);
              const leave = {
                type: getNode(2).type,
                flow201: { isDefault: flow201.isDefault, condition: flow201.condition, conditionPriority: flow201.conditionPriority, roles: flow201.roles, variables: flow201.variables },
                flow202: { isDefault: flow202.isDefault, condition: flow202.condition, conditionPriority: flow202.conditionPriority, roles: flow202.roles, variables: flow202.variables }
              };

            // Entering a gateway with routing metadata exercises the
            // target-gateway side of the semantics check: Cancel preserves the
            // configured outgoing roles/variables; Accept clears them.
            clearTypeDialogs();
            loadFromObject({
              id: 'type-transition-gateway-enter-metadata',
              name: 'Gateway enter metadata',
              initialEventId: 1,
              variables: [],
              lanes: [],
              flowNodes: [
                { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                { id: 2, name: 'Review', type: 'userTask', x: 200, y: 0 },
                { id: 3, name: 'Big', type: 'userTask', x: 400, y: -60 },
                { id: 4, name: 'Small', type: 'userTask', x: 400, y: 60 },
                { id: 5, name: 'Done', type: 'endEvent', x: 600, y: 0 }
              ],
              sequenceFlows: [
                { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                { id: 201, name: 'Large', sourceRef: 2, targetRef: 3, roles: ['Manager'], variables: [{ id: 2, name: 'note', dataType: 'string', isArray: false, required: false, defaultValue: null, validation: null }] },
                { id: 202, name: 'Small', sourceRef: 2, targetRef: 4 },
                { id: 301, name: '', sourceRef: 3, targetRef: 5 },
                { id: 302, name: '', sourceRef: 4, targetRef: 5 }
              ]
            });
            render();
            const enterMetadataBaseline = JSON.stringify(model);
            typeConfirmResponse = false;
            const enterDeclineCallback = captureTypeCallback(2);
            resetTypeRedrawCounters();
            clearTypeDialogs();
            enterDeclineCallback('exclusiveGateway');
            const enterDecline = {
              dialogs: typeDialogs.slice(),
              typePreserved: getNode(2).type === 'userTask',
              modelPreserved: JSON.stringify(model) === enterMetadataBaseline,
              renders: typeFullRenders
            };
            clearTypeDialogs();
            typeConfirmResponse = true;
            const enterAcceptCallback = captureTypeCallback(2);
            enterAcceptCallback('exclusiveGateway');
            const enteredFlow201 = model.sequenceFlows.find(f => f.id === 201);
            const enterAccept = {
              type: getNode(2).type,
              flow201: {
                roles: enteredFlow201.roles,
                variables: enteredFlow201.variables,
                condition: enteredFlow201.condition,
                isDefault: enteredFlow201.isDefault
              },
              priorities: model.sequenceFlows.filter(f => f.sourceRef === 2).map(f => f.conditionPriority)
            };

              // Entering a gateway: clean flows skip the prompt and the full
              // render repopulates exclusive priorities.
              clearTypeDialogs();
              loadFromObject({
                id: 'type-transition-gateway-enter',
                name: 'Gateway enter',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Route', type: 'userTask', x: 200, y: 0 },
                  { id: 3, name: 'Big', type: 'userTask', x: 400, y: -60 },
                  { id: 4, name: 'Small', type: 'userTask', x: 400, y: 60 },
                  { id: 5, name: 'Done', type: 'endEvent', x: 600, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: 'Large', sourceRef: 2, targetRef: 3 },
                  { id: 202, name: 'Small', sourceRef: 2, targetRef: 4 },
                  { id: 301, name: '', sourceRef: 3, targetRef: 5 },
                  { id: 302, name: '', sourceRef: 4, targetRef: 5 }
                ]
              });
              render();
              const enterCallback = captureTypeCallback(2);
              resetTypeRedrawCounters();
              clearTypeDialogs();
              enterCallback('exclusiveGateway');
              const priorities = model.sequenceFlows
                .filter(f => f.sourceRef === 2)
                .map(f => ({ id: f.id, conditionPriority: f.conditionPriority, condition: f.condition, isDefault: f.isDefault }));
              const enter = {
                dialogs: typeDialogs.slice(),
                type: getNode(2).type,
                priorities
              };

              // Gateway to gateway: joinCancellation retained.
              clearTypeDialogs();
              loadFromObject({
                id: 'type-transition-gateway-switch',
                name: 'Gateway switch',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Fork', type: 'parallelGateway', x: 200, y: 0 },
                  { id: 3, name: 'A', type: 'userTask', x: 400, y: -60 },
                  { id: 4, name: 'B', type: 'userTask', x: 400, y: 60 },
                  { id: 5, name: 'Join', type: 'parallelGateway', x: 600, y: 0, joinCancellation: { gatewayRef: 2 } },
                  { id: 6, name: 'Done', type: 'endEvent', x: 800, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: '', sourceRef: 2, targetRef: 3 },
                  { id: 202, name: '', sourceRef: 2, targetRef: 4 },
                  { id: 301, name: '', sourceRef: 3, targetRef: 5 },
                  { id: 302, name: '', sourceRef: 4, targetRef: 5 },
                  { id: 401, name: '', sourceRef: 5, targetRef: 6 }
                ]
              });
              render();
              const switchCallback = captureTypeCallback(5);
              resetTypeRedrawCounters();
              clearTypeDialogs();
              switchCallback('inclusiveGateway');
              const node5 = getNode(5);
              const gatewaySwitch = {
                type: node5.type,
                joinCancellationKept: node5.joinCancellation != null && node5.joinCancellation.gatewayRef === 2
              };
              return JSON.stringify({ leave, enterDecline, enterAccept, enter, gatewaySwitch });
            })()
            """).AsString());

        var root = result.RootElement;
        var leave = root.GetProperty("leave");
        Assert.Equal("userTask", leave.GetProperty("type").GetString());
        foreach (var flowName in new[] { "flow201", "flow202" })
        {
            var flow = leave.GetProperty(flowName);
            Assert.False(flow.GetProperty("isDefault").GetBoolean());
            Assert.Equal(JsonValueKind.Null, flow.GetProperty("condition").ValueKind);
            Assert.Equal(JsonValueKind.Null, flow.GetProperty("conditionPriority").ValueKind);
            Assert.Empty(flow.GetProperty("roles").EnumerateArray());
            Assert.Empty(flow.GetProperty("variables").EnumerateArray());
        }

        var enter = root.GetProperty("enter");
        Assert.Empty(enter.GetProperty("dialogs").EnumerateArray());
        Assert.Equal("exclusiveGateway", enter.GetProperty("type").GetString());
        var priorities = enter.GetProperty("priorities").EnumerateArray().ToArray();
        Assert.Equal(2, priorities.Length);
        Assert.Equal((201, 1, false), (
            priorities[0].GetProperty("id").GetInt32(),
            priorities[0].GetProperty("conditionPriority").GetInt32(),
            priorities[0].GetProperty("isDefault").GetBoolean()));
        Assert.Equal((202, 2, false), (
            priorities[1].GetProperty("id").GetInt32(),
            priorities[1].GetProperty("conditionPriority").GetInt32(),
            priorities[1].GetProperty("isDefault").GetBoolean()));
        Assert.Equal(
            JsonValueKind.Null,
            priorities[0].GetProperty("condition").ValueKind);

        var gatewaySwitch = root.GetProperty("gatewaySwitch");
        Assert.Equal("inclusiveGateway", gatewaySwitch.GetProperty("type").GetString());
        Assert.True(gatewaySwitch.GetProperty("joinCancellationKept").GetBoolean());

        const string gatewayPrompt =
            "Changing this node's gateway semantics clears outgoing defaults, conditions, priorities, roles, and variables. Continue?";
        var enterDecline = root.GetProperty("enterDecline");
        var declineDialogs = enterDecline.GetProperty("dialogs");
        Assert.Equal(1, declineDialogs.GetArrayLength());
        Assert.Equal("confirm", declineDialogs[0].GetProperty("kind").GetString());
        Assert.Equal(gatewayPrompt, declineDialogs[0].GetProperty("message").GetString());
        Assert.True(enterDecline.GetProperty("typePreserved").GetBoolean());
        Assert.True(enterDecline.GetProperty("modelPreserved").GetBoolean());
        Assert.Equal(0, enterDecline.GetProperty("renders").GetInt32());

        var enterAccept = root.GetProperty("enterAccept");
        Assert.Equal("exclusiveGateway", enterAccept.GetProperty("type").GetString());
        var enteredFlow = enterAccept.GetProperty("flow201");
        Assert.Empty(enteredFlow.GetProperty("roles").EnumerateArray());
        Assert.Empty(enteredFlow.GetProperty("variables").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, enteredFlow.GetProperty("condition").ValueKind);
        Assert.False(enteredFlow.GetProperty("isDefault").GetBoolean());
        Assert.Equal(new[] { 1, 2 }, enterAccept.GetProperty("priorities")
            .EnumerateArray().Select(value => value.GetInt32()).ToArray());
    }

    [Fact]
    public void TypeTransition_GatewayPromptPredicateEdgeCases()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const loadFixture = () => loadFromObject({
                id: 'type-transition-predicate',
                name: 'Gateway predicate',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Fork', type: 'parallelGateway', x: 200, y: 0 },
                  { id: 3, name: 'A', type: 'userTask', x: 400, y: -60 },
                  { id: 4, name: 'B', type: 'userTask', x: 400, y: 60 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 301, name: 'First', sourceRef: 2, targetRef: 3 },
                  { id: 302, name: 'Second', sourceRef: 2, targetRef: 4 }
                ]
              });
              const cases = [
                { key: 'isDefault', prompt: true, mutate: flow => { flow.isDefault = true; } },
                { key: 'condition', prompt: true, mutate: flow => { flow.condition = 'amount > 10'; } },
                { key: 'emptyCondition', prompt: true, mutate: flow => { flow.condition = ''; } },
                { key: 'conditionPriority', prompt: true, mutate: flow => { flow.conditionPriority = 2; } },
                { key: 'roles', prompt: true, mutate: flow => { flow.roles = ['Manager']; } },
                {
                  key: 'variables',
                  prompt: true,
                  mutate: flow => {
                    flow.variables = [{ id: 9, name: 'note', dataType: 'string', isArray: false, required: false, defaultValue: null, validation: null }];
                  }
                },
                { key: 'clean', prompt: false, mutate: () => {} },
                { key: 'rolesVariable', prompt: false, mutate: flow => { flow.rolesVariable = 'approvalRoles'; } },
                {
                  key: 'claimBypass',
                  prompt: false,
                  mutate: flow => { flow.canActWithoutClaim = true; flow.canActWithoutClaimRoles = ['Helper']; }
                },
                {
                  key: 'miMetadata',
                  prompt: false,
                  mutate: flow => {
                    flow.completionCondition = 'CountFlow(302) > 0';
                    flow.completionPriority = 1;
                    flow.cancelRemainingInstances = true;
                  }
                }
              ];
              const results = {};
              for (const item of cases) {
                loadFixture();
                render();
                item.mutate(model.sequenceFlows.find(f => f.id === 301));
                const baseline = JSON.stringify(model);
                const callback = captureTypeCallback(2);
                resetTypeRedrawCounters();
                clearTypeDialogs();
                typeConfirmResponse = false;
                callback('task');
                results[item.key] = {
                  prompt: typeDialogs.some(dialog => dialog.kind === 'confirm'),
                  type: getNode(2).type,
                  modelPreserved: JSON.stringify(model) === baseline
                };
              }
              return JSON.stringify(results);
            })()
            """).AsString());

        var root = result.RootElement;
        foreach (var property in root.EnumerateObject())
        {
            var expectedPrompt = property.Name is "isDefault" or "condition" or "emptyCondition" or
                "conditionPriority" or "roles" or "variables";
            Assert.Equal(expectedPrompt, property.Value.GetProperty("prompt").GetBoolean());
            if (expectedPrompt)
            {
                // A declined prompt redraws only the inspector and leaves the model.
                Assert.Equal("parallelGateway", property.Value.GetProperty("type").GetString());
                Assert.True(property.Value.GetProperty("modelPreserved").GetBoolean());
            }
            else
            {
                // Without routing metadata the transition proceeds without prompting.
                Assert.Equal("task", property.Value.GetProperty("type").GetString());
            }
        }
    }

    [Fact]
    public void TypeTransition_RoleReferencesFollowUserTaskBoundary()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const loadFixture = () => loadFromObject({
                id: 'type-transition-roles',
                name: 'Role references',
                initialEventId: 1,
                variables: [
                  { id: 1, name: 'reviewRoles', dataType: 'string', isArray: true },
                  { id: 2, name: 'otherRoles', dataType: 'string', isArray: true }
                ],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Review', type: 'userTask', x: 200, y: 0, rolesVariable: 'reviewRoles' },
                  { id: 4, name: 'Other', type: 'userTask', x: 400, y: 0, rolesVariable: 'otherRoles' },
                  { id: 6, name: 'Done', type: 'endEvent', x: 600, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: 'Pass', sourceRef: 2, targetRef: 4, rolesVariable: 'approvalRoles' },
                  { id: 401, name: 'Finish', sourceRef: 4, targetRef: 6, rolesVariable: 'outboundRoles' }
                ]
              });

              loadFixture();
              render();
              const leavingCallback = captureTypeCallback(2);
              resetTypeRedrawCounters();
              leavingCallback('task');
              const leaving = {
                type: getNode(2).type,
                nodeRolesVariableAbsent: !Object.prototype.hasOwnProperty.call(getNode(2), 'rolesVariable'),
                otherNodeKept: getNode(4).rolesVariable === 'otherRoles',
                flow201Absent: !Object.prototype.hasOwnProperty.call(
                  model.sequenceFlows.find(f => f.id === 201), 'rolesVariable'),
                flow401Kept: model.sequenceFlows.find(f => f.id === 401).rolesVariable === 'outboundRoles'
              };

              loadFixture();
              render();
              const sameTypeCallback = captureTypeCallback(2);
              sameTypeCallback('userTask');
              const sameType = {
                nodeKept: getNode(2).rolesVariable === 'reviewRoles',
                flowKept: model.sequenceFlows.find(f => f.id === 201).rolesVariable === 'approvalRoles'
              };
              return JSON.stringify({ leaving, sameType });
            })()
            """).AsString());

        var root = result.RootElement;
        var leaving = root.GetProperty("leaving");
        Assert.Equal("task", leaving.GetProperty("type").GetString());
        Assert.True(leaving.GetProperty("nodeRolesVariableAbsent").GetBoolean());
        Assert.True(leaving.GetProperty("otherNodeKept").GetBoolean());
        Assert.True(leaving.GetProperty("flow201Absent").GetBoolean());
        Assert.True(leaving.GetProperty("flow401Kept").GetBoolean());

        var sameType = root.GetProperty("sameType");
        Assert.True(sameType.GetProperty("nodeKept").GetBoolean());
        Assert.True(sameType.GetProperty("flowKept").GetBoolean());
    }

    [Fact]
    public void TypeTransition_JoinCancellationReferencesFollowGatewayBoundary()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const loadFixture = () => loadFromObject({
                id: 'type-transition-join',
                name: 'Join references',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Fork', type: 'parallelGateway', x: 200, y: 0 },
                  { id: 3, name: 'A', type: 'userTask', x: 400, y: -60 },
                  { id: 4, name: 'B', type: 'userTask', x: 400, y: 60 },
                  { id: 5, name: 'Join', type: 'parallelGateway', x: 600, y: 0, joinCancellation: { gatewayRef: 2 } },
                  { id: 6, name: 'Done', type: 'endEvent', x: 800, y: 0 },
                  { id: 8, name: 'Other join', type: 'parallelGateway', x: 800, y: 200, joinCancellation: { gatewayRef: 5 } },
                  { id: 9, name: 'Unrelated join', type: 'parallelGateway', x: 800, y: 320, joinCancellation: { gatewayRef: 2 } },
                  { id: 10, name: 'Interrupt', type: 'scopedInterruptEvent', x: 800, y: 440, gatewayRef: 2 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: '', sourceRef: 2, targetRef: 3 },
                  { id: 202, name: '', sourceRef: 2, targetRef: 4 },
                  { id: 301, name: '', sourceRef: 3, targetRef: 5 },
                  { id: 302, name: '', sourceRef: 4, targetRef: 5 },
                  { id: 401, name: '', sourceRef: 5, targetRef: 6 }
                ]
              });

              loadFixture();
              render();
              const leavingCallback = captureTypeCallback(5);
              resetTypeRedrawCounters();
              leavingCallback('task');
              const node8 = getNode(8);
              const leaving = {
                type: getNode(5).type,
                ownJoinCancelledAbsent: !Object.prototype.hasOwnProperty.call(getNode(5), 'joinCancellation'),
                referencingNulled: node8.joinCancellation != null && node8.joinCancellation.gatewayRef === null,
                unrelatedKept: getNode(9).joinCancellation != null && getNode(9).joinCancellation.gatewayRef === 2,
                scopedInterruptUntouched: getNode(10).gatewayRef === 2
              };

              loadFixture();
              render();
              const gatewayCallback = captureTypeCallback(5);
              gatewayCallback('inclusiveGateway');
              const node5 = getNode(5);
              const gatewaySwitch = {
                type: node5.type,
                ownKept: node5.joinCancellation != null && node5.joinCancellation.gatewayRef === 2,
                referencingUntouched: getNode(8).joinCancellation != null && getNode(8).joinCancellation.gatewayRef === 5
              };
              return JSON.stringify({ leaving, gatewaySwitch });
            })()
            """).AsString());

        var root = result.RootElement;
        var leaving = root.GetProperty("leaving");
        Assert.Equal("task", leaving.GetProperty("type").GetString());
        Assert.True(leaving.GetProperty("ownJoinCancelledAbsent").GetBoolean());
        Assert.True(leaving.GetProperty("referencingNulled").GetBoolean());
        Assert.True(leaving.GetProperty("unrelatedKept").GetBoolean());
        Assert.True(leaving.GetProperty("scopedInterruptUntouched").GetBoolean());

        var gatewaySwitch = root.GetProperty("gatewaySwitch");
        Assert.Equal("inclusiveGateway", gatewaySwitch.GetProperty("type").GetString());
        Assert.True(gatewaySwitch.GetProperty("ownKept").GetBoolean());
        Assert.True(gatewaySwitch.GetProperty("referencingUntouched").GetBoolean());
    }

    [Fact]
    public void TypeTransition_PrunesOutgoingFlowsKeepingFirstArrayOrder()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'type-transition-prune',
                name: 'Outgoing pruning',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Review', type: 'userTask', x: 200, y: 0 },
                  { id: 3, name: 'A', type: 'userTask', x: 400, y: -60 },
                  { id: 4, name: 'B', type: 'userTask', x: 400, y: 60 },
                  { id: 6, name: 'First target', type: 'userTask', x: 400, y: 180 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 203, name: 'First', sourceRef: 2, targetRef: 6, attributes: [{ key: 'keep', value: 'yes' }] },
                  { id: 201, name: 'Second', sourceRef: 2, targetRef: 3, condition: 'a > 1' },
                  { id: 202, name: 'Third', sourceRef: 2, targetRef: 4 }
                ]
              });
              render();
              const callback = captureTypeCallback(2);
              const flow203Before = JSON.stringify(model.sequenceFlows.find(f => f.id === 203));
              resetTypeRedrawCounters();
              callback('task');
              const flow203 = model.sequenceFlows.find(f => f.id === 203);
              return JSON.stringify({
                type: getNode(2).type,
                renders: typeFullRenders,
                flowIds: model.sequenceFlows.map(f => f.id),
                flow203Id: flow203.id,
                flow203Name: flow203.name,
                flow203Attributes: flow203.attributes,
                flow203: { condition: flow203.condition, conditionPriority: flow203.conditionPriority, isDefault: flow203.isDefault, roles: flow203.roles, variables: flow203.variables }
              });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("task", root.GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("renders").GetInt32());
        Assert.Equal(new[] { 101, 203 }, root.GetProperty("flowIds").EnumerateArray()
            .Select(value => value.GetInt32()).ToArray());
        Assert.Equal(203, root.GetProperty("flow203Id").GetInt32());
        Assert.Equal("First", root.GetProperty("flow203Name").GetString());
        var attributes = root.GetProperty("flow203Attributes");
        Assert.Equal(1, attributes.GetArrayLength());
        Assert.Equal("keep", attributes[0].GetProperty("key").GetString());
        Assert.Equal("yes", attributes[0].GetProperty("value").GetString());
        var flow203 = root.GetProperty("flow203");
        Assert.Equal(JsonValueKind.Null, flow203.GetProperty("condition").ValueKind);
        Assert.Equal(JsonValueKind.Null, flow203.GetProperty("conditionPriority").ValueKind);
        Assert.False(flow203.GetProperty("isDefault").GetBoolean());
        Assert.Empty(flow203.GetProperty("roles").EnumerateArray());
        Assert.Empty(flow203.GetProperty("variables").EnumerateArray());
    }

    [Fact]
    public void TypeTransition_ErrorBoundaryCleanupFollowsHostType()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const loadFixture = () => loadFromObject({
                id: 'type-transition-error-boundary',
                name: 'Error boundary cleanup',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Call credit', type: 'serviceTask', x: 200, y: 0 },
                  { id: 3, name: 'Review', type: 'userTask', x: 400, y: 0 },
                  { id: 6, name: 'Done', type: 'endEvent', x: 600, y: 0 },
                  { id: 9, name: 'Local calc', type: 'scriptTask', x: 200, y: 200 },
                  { id: 10, name: 'Done too', type: 'endEvent', x: 400, y: 200 },
                  { id: 20, name: 'Credit failure', type: 'errorBoundaryEvent', x: 260, y: 90, attachedToRef: 2, errorVariable: 'err' },
                  { id: 21, name: 'Calc failure', type: 'errorBoundaryEvent', x: 260, y: 290, attachedToRef: 9, errorVariable: 'calcErr' }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: '', sourceRef: 2, targetRef: 3 },
                  { id: 301, name: '', sourceRef: 3, targetRef: 6 },
                  { id: 901, name: '', sourceRef: 9, targetRef: 10 },
                  { id: 2001, name: '', sourceRef: 20, targetRef: 6 },
                  { id: 2002, name: '', sourceRef: 21, targetRef: 10 }
                ]
              });

              loadFixture();
              render();
              const toScriptCallback = captureTypeCallback(2);
              resetTypeRedrawCounters();
              toScriptCallback('scriptTask');
              const toScript = {
                type: getNode(2).type,
                boundaryKept: getNode(20) != null && getNode(20).errorVariable === 'err',
                flowKept: model.sequenceFlows.some(f => f.id === 2001)
              };

              const toUserCallback = captureTypeCallback(2);
              toUserCallback('userTask');
              const toUser = {
                type: getNode(2).type,
                boundaryRemoved: getNode(20) == null,
                flowRemoved: !model.sequenceFlows.some(f => f.id === 2001),
                otherHostKept: getNode(21) != null && getNode(21).errorVariable === 'calcErr',
                otherFlowKept: model.sequenceFlows.some(f => f.id === 2002)
              };
              return JSON.stringify({ toScript, toUser });
            })()
            """).AsString());

        var root = result.RootElement;
        var toScript = root.GetProperty("toScript");
        Assert.Equal("scriptTask", toScript.GetProperty("type").GetString());
        Assert.True(toScript.GetProperty("boundaryKept").GetBoolean());
        Assert.True(toScript.GetProperty("flowKept").GetBoolean());

        var toUser = root.GetProperty("toUser");
        Assert.Equal("userTask", toUser.GetProperty("type").GetString());
        Assert.True(toUser.GetProperty("boundaryRemoved").GetBoolean());
        Assert.True(toUser.GetProperty("flowRemoved").GetBoolean());
        Assert.True(toUser.GetProperty("otherHostKept").GetBoolean());
        Assert.True(toUser.GetProperty("otherFlowKept").GetBoolean());
    }

    [Fact]
    public void TypeTransition_ObservableBoundaryEligibilityUsesNormalizedHost()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const loadFixture = () => loadFromObject({
                id: 'type-transition-boundary',
                name: 'Observable boundaries',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Wait', type: 'userTask', x: 200, y: 0 },
                  { id: 3, name: 'Done', type: 'endEvent', x: 400, y: 0 },
                  { id: 30, name: 'Reminder', type: 'timerBoundaryEvent', x: 260, y: 90, attachedToRef: 2, cancelActivity: false, timer: { timeDuration: 'PT30M' } },
                  { id: 31, name: 'Flag flip', type: 'conditionalBoundaryEvent', x: 340, y: 90, attachedToRef: 2, conditional: { condition: 'flag == true' } }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: '', sourceRef: 2, targetRef: 3 },
                  { id: 2001, name: '', sourceRef: 30, targetRef: 3 },
                  { id: 2002, name: '', sourceRef: 31, targetRef: 3 }
                ]
              });

              // Durable user task keeps its boundaries; same-type keeps settings.
              loadFixture();
              render();
              const sameTypeCallback = captureTypeCallback(2);
              resetTypeRedrawCounters();
              sameTypeCallback('userTask');
              const sameType = {
                timerKept: getNode(30) != null && getNode(30).cancelActivity === false,
                conditionalKept: getNode(31) != null && getNode(31).conditional.condition === 'flag == true',
                flowsKept: model.sequenceFlows.some(f => f.id === 2001) && model.sequenceFlows.some(f => f.id === 2002)
              };

              // Automatic target without asyncBefore removes both boundaries.
              const toTaskCallback = captureTypeCallback(2);
              toTaskCallback('task');
              const toTask = {
                type: getNode(2).type,
                timerRemoved: getNode(30) == null,
                conditionalRemoved: getNode(31) == null,
                flowsRemoved: !model.sequenceFlows.some(f => f.id === 2001) && !model.sequenceFlows.some(f => f.id === 2002)
              };

              // Message catch stays durable and keeps the boundaries.
              loadFixture();
              render();
              const toMessageCallback = captureTypeCallback(2);
              toMessageCallback('intermediateMessageCatchEvent');
              const toMessage = {
                type: getNode(2).type,
                timerKept: getNode(30) != null,
                conditionalKept: getNode(31) != null
              };

              // Timer catches are durable hosts too; preserve both boundary
              // contracts and all their incident flows through conversion.
              // Boundary coordinates follow the new host shape during render.
              const boundaryContract = boundary => {
                if (!boundary) return null;
                const { x, y, ...contract } = boundary;
                return contract;
              };
              loadFixture();
              render();
              const timerBoundaryBefore = JSON.stringify(boundaryContract(getNode(30)));
              const conditionalBoundaryBefore = JSON.stringify(boundaryContract(getNode(31)));
              const boundaryFlowsBefore = JSON.stringify(model.sequenceFlows.filter(f => f.sourceRef === 30 || f.sourceRef === 31));
              captureTypeCallback(2)('intermediateTimerCatchEvent');
              const toTimer = {
                type: getNode(2).type,
                timerUnchanged: JSON.stringify(boundaryContract(getNode(30))) === timerBoundaryBefore,
                conditionalUnchanged: JSON.stringify(boundaryContract(getNode(31))) === conditionalBoundaryBefore,
                flowsUnchanged: JSON.stringify(model.sequenceFlows.filter(f => f.sourceRef === 30 || f.sourceRef === 31)) === boundaryFlowsBefore
              };

              // Conditional catch is not in the eligible host set.
              loadFixture();
              render();
              const toConditionalCallback = captureTypeCallback(2);
              toConditionalCallback('intermediateConditionalCatchEvent');
              const toConditional = {
                type: getNode(2).type,
                timerRemoved: getNode(30) == null,
                conditionalRemoved: getNode(31) == null
              };

              // Async automatic hosts keep boundaries through normalization.
              const loadAsyncFixture = (asyncBefore) => loadFromObject({
                id: 'type-transition-async-boundary',
                name: 'Async boundaries',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 40, name: 'Enrich', type: 'task', x: 200, y: 0, asyncBefore: asyncBefore },
                  { id: 42, name: 'Done', type: 'endEvent', x: 400, y: 0 },
                  { id: 41, name: 'Timeout', type: 'timerBoundaryEvent', x: 260, y: 90, attachedToRef: 40, cancelActivity: true, timer: { timeDuration: 'PT1H' } },
                  { id: 43, name: 'Ready', type: 'conditionalBoundaryEvent', x: 340, y: 90, attachedToRef: 40, cancelActivity: false, conditional: { condition: 'ready == true' } }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 40 },
                  { id: 401, name: '', sourceRef: 40, targetRef: 42 },
                  { id: 2003, name: '', sourceRef: 41, targetRef: 42 },
                  { id: 2004, name: '', sourceRef: 43, targetRef: 42 }
                ]
              });
              loadAsyncFixture(true);
              render();
              const asyncKeptCallback = captureTypeCallback(40);
              asyncKeptCallback('serviceTask');
              const asyncKept = {
                type: getNode(40).type,
                asyncBefore: getNode(40).asyncBefore,
                boundaryKept: getNode(41) != null && getNode(41).cancelActivity === true,
                flowKept: model.sequenceFlows.some(f => f.id === 2003)
              };
              loadAsyncFixture(false);
              render();
              const asyncRemovedCallback = captureTypeCallback(40);
              asyncRemovedCallback('serviceTask');
              const asyncRemoved = {
                type: getNode(40).type,
                asyncBefore: getNode(40).asyncBefore,
                boundaryRemoved: getNode(41) == null,
                flowRemoved: !model.sequenceFlows.some(f => f.id === 2003)
              };
              // Each automatic host family retains both observable boundaries
              // when asyncBefore remains true after node normalization.
              const asyncTargets = ['task', 'serviceTask', 'scriptTask'].map(target => {
                loadAsyncFixture(true);
                render();
                const boundariesBefore = JSON.stringify(model.flowNodes.filter(n => n.attachedToRef === 40).map(boundaryContract));
                const flowsBefore = JSON.stringify(model.sequenceFlows.filter(f => f.sourceRef === 41 || f.sourceRef === 43));
                captureTypeCallback(40)(target);
                return {
                  type: getNode(40).type,
                  asyncBefore: getNode(40).asyncBefore,
                  boundariesUnchanged: JSON.stringify(model.flowNodes.filter(n => n.attachedToRef === 40).map(boundaryContract)) === boundariesBefore,
                  flowsUnchanged: JSON.stringify(model.sequenceFlows.filter(f => f.sourceRef === 41 || f.sourceRef === 43)) === flowsBefore
                };
              });
              return JSON.stringify({ sameType, toTask, toMessage, toTimer, toConditional, asyncKept, asyncRemoved, asyncTargets });
            })()
            """).AsString());

        var root = result.RootElement;
        var sameType = root.GetProperty("sameType");
        Assert.True(sameType.GetProperty("timerKept").GetBoolean());
        Assert.True(sameType.GetProperty("conditionalKept").GetBoolean());
        Assert.True(sameType.GetProperty("flowsKept").GetBoolean());

        var toTask = root.GetProperty("toTask");
        Assert.Equal("task", toTask.GetProperty("type").GetString());
        Assert.True(toTask.GetProperty("timerRemoved").GetBoolean());
        Assert.True(toTask.GetProperty("conditionalRemoved").GetBoolean());
        Assert.True(toTask.GetProperty("flowsRemoved").GetBoolean());

        var toMessage = root.GetProperty("toMessage");
        Assert.Equal("intermediateMessageCatchEvent", toMessage.GetProperty("type").GetString());
        Assert.True(toMessage.GetProperty("timerKept").GetBoolean());
        Assert.True(toMessage.GetProperty("conditionalKept").GetBoolean());

        var toTimer = root.GetProperty("toTimer");
        Assert.Equal("intermediateTimerCatchEvent", toTimer.GetProperty("type").GetString());
        Assert.True(toTimer.GetProperty("timerUnchanged").GetBoolean());
        Assert.True(toTimer.GetProperty("conditionalUnchanged").GetBoolean());
        Assert.True(toTimer.GetProperty("flowsUnchanged").GetBoolean());

        var toConditional = root.GetProperty("toConditional");
        Assert.Equal("intermediateConditionalCatchEvent", toConditional.GetProperty("type").GetString());
        Assert.True(toConditional.GetProperty("timerRemoved").GetBoolean());
        Assert.True(toConditional.GetProperty("conditionalRemoved").GetBoolean());

        var asyncKept = root.GetProperty("asyncKept");
        Assert.Equal("serviceTask", asyncKept.GetProperty("type").GetString());
        Assert.True(asyncKept.GetProperty("asyncBefore").GetBoolean());
        Assert.True(asyncKept.GetProperty("boundaryKept").GetBoolean());
        Assert.True(asyncKept.GetProperty("flowKept").GetBoolean());

        var asyncRemoved = root.GetProperty("asyncRemoved");
        Assert.Equal("serviceTask", asyncRemoved.GetProperty("type").GetString());
        Assert.False(asyncRemoved.GetProperty("asyncBefore").GetBoolean());
        Assert.True(asyncRemoved.GetProperty("boundaryRemoved").GetBoolean());
        Assert.True(asyncRemoved.GetProperty("flowRemoved").GetBoolean());

        var asyncTargets = root.GetProperty("asyncTargets").EnumerateArray().ToArray();
        Assert.Equal(new[] { "task", "serviceTask", "scriptTask" },
            asyncTargets.Select(target => target.GetProperty("type").GetString()).ToArray());
        Assert.All(asyncTargets, target =>
        {
            Assert.True(target.GetProperty("asyncBefore").GetBoolean());
            Assert.True(target.GetProperty("boundariesUnchanged").GetBoolean());
            Assert.True(target.GetProperty("flowsUnchanged").GetBoolean());
        });
    }

    [Fact]
    public void TypeTransition_MessageStartConversionMaterializesAndRebuilds()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              loadFromObject({
                id: 'type-transition-message-start',
                name: 'Message start conversion',
                initialEventId: null,
                variables: [],
                lanes: [],
                flowNodes: [
                  {
                    id: 1,
                    name: 'Webhook start',
                    type: 'messageStartEvent',
                    x: 0,
                    y: 0,
                    // The idempotency variable names a mapping that also has a
                    // configured default, so the reverse conversion would
                    // rebuild it if the idempotency exclusion broke; 'extra'
                    // separately pins the optional-no-default omission rule.
                    idempotency: { headerName: 'Idempotency-Key', variable: 'tag' },
                    message: {
                      clientId: 'svc-orders',
                      clientSecret: 'topsecret',
                      headerName: 'X-Token',
                      headerValue: 'v1',
                      outputMappings: [
                        { variable: 'amount', path: 'a', dataType: 'number', isArray: false, required: true, defaultValue: null, validation: 'amount > 0' },
                        { variable: 'tag', path: 't', dataType: 'string', isArray: false, required: false, defaultValue: 'x', validation: null },
                        { variable: 'extra', path: 'e', dataType: 'json', isArray: false, required: false, defaultValue: null, validation: null }
                      ]
                    }
                  },
                  { id: 2, name: 'Work', type: 'task', x: 200, y: 0 },
                  { id: 3, name: 'Done', type: 'endEvent', x: 400, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: '', sourceRef: 2, targetRef: 3 }
                ]
              });
              render();
              const callback = captureTypeCallback(1);
              resetTypeRedrawCounters();
              callback('startEvent');
              const forwardNode = getNode(1);
              const forward = {
                type: forwardNode.type,
                variables: forwardNode.variables,
                messageAbsent: !Object.prototype.hasOwnProperty.call(forwardNode, 'message'),
                idempotencyKept: forwardNode.idempotency != null && forwardNode.idempotency.variable === 'tag'
              };
              callback('messageStartEvent');
              const reverseNode = getNode(1);
              const reverse = {
                type: reverseNode.type,
                variables: reverseNode.variables,
                outputMappings: reverseNode.message.outputMappings,
                clientId: reverseNode.message.clientId,
                headerName: reverseNode.message.headerName,
                idempotencyKept: reverseNode.idempotency != null && reverseNode.idempotency.variable === 'tag'
              };
              return JSON.stringify({ forward, reverse });
            })()
            """).AsString());

        var root = result.RootElement;
        var forward = root.GetProperty("forward");
        Assert.Equal("startEvent", forward.GetProperty("type").GetString());
        var variables = forward.GetProperty("variables").EnumerateArray().ToArray();
        Assert.Equal(3, variables.Length);
        Assert.Equal((1, "amount", "number", true), (
            variables[0].GetProperty("id").GetInt32(),
            variables[0].GetProperty("name").GetString(),
            variables[0].GetProperty("dataType").GetString(),
            variables[0].GetProperty("required").GetBoolean()));
        Assert.Equal((2, "tag", "string", false), (
            variables[1].GetProperty("id").GetInt32(),
            variables[1].GetProperty("name").GetString(),
            variables[1].GetProperty("dataType").GetString(),
            variables[1].GetProperty("required").GetBoolean()));
        Assert.Equal("x", variables[1].GetProperty("defaultValue").GetString());
        Assert.Equal((3, "extra", "json", false), (
            variables[2].GetProperty("id").GetInt32(),
            variables[2].GetProperty("name").GetString(),
            variables[2].GetProperty("dataType").GetString(),
            variables[2].GetProperty("required").GetBoolean()));
        Assert.True(forward.GetProperty("messageAbsent").GetBoolean());
        Assert.True(forward.GetProperty("idempotencyKept").GetBoolean());

        var reverse = root.GetProperty("reverse");
        Assert.Equal("messageStartEvent", reverse.GetProperty("type").GetString());
        Assert.Empty(reverse.GetProperty("variables").EnumerateArray());
        // 'tag' is excluded because it is the entry's idempotency variable (it
        // carries a configured default, so only the idempotency rule omits it);
        // 'extra' is excluded by the optional-no-default rule.
        var mappings = reverse.GetProperty("outputMappings").EnumerateArray().ToArray();
        Assert.Single(mappings);
        Assert.Equal(("amount", string.Empty, "number", true), (
            mappings[0].GetProperty("variable").GetString(),
            mappings[0].GetProperty("path").GetString(),
            mappings[0].GetProperty("dataType").GetString(),
            mappings[0].GetProperty("required").GetBoolean()));
        Assert.Equal(string.Empty, reverse.GetProperty("clientId").GetString());
        Assert.Equal(string.Empty, reverse.GetProperty("headerName").GetString());
        Assert.True(reverse.GetProperty("idempotencyKept").GetBoolean());
    }

    [Fact]
    public void TypeTransition_EntryIdentityAndBusinessKeyDefaults()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              // Default start repair: first remaining ordinary start in array order.
              loadFromObject({
                id: 'type-transition-default-start',
                name: 'Default start repair',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'First start', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Review', type: 'userTask', x: 200, y: 0 },
                  { id: 4, name: 'Second start', type: 'startEvent', x: 0, y: 200 },
                  { id: 3, name: 'Lower ID, later start', type: 'startEvent', x: 0, y: 400 }
                ],
                sequenceFlows: [{ id: 101, name: '', sourceRef: 1, targetRef: 2 }]
              });
              render();
              const repairCallback = captureTypeCallback(1);
              resetTypeRedrawCounters();
              repairCallback('userTask');
              const repaired = {
                type: getNode(1).type,
                initialEventId: model.initialEventId
              };

              // No alternative ordinary start: default becomes null.
              loadFromObject({
                id: 'type-transition-default-null',
                name: 'Default start null',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'First start', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Work', type: 'task', x: 200, y: 0 }
                ],
                sequenceFlows: [{ id: 101, name: '', sourceRef: 1, targetRef: 2 }]
              });
              render();
              const nullCallback = captureTypeCallback(1);
              nullCallback('task');
              const nulled = { initialEventId: model.initialEventId };

              // Timer starts never substitute as the default.
              loadFromObject({
                id: 'type-transition-default-timer',
                name: 'Default start timer',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'First start', type: 'startEvent', x: 0, y: 0 },
                  { id: 5, name: 'Scheduled', type: 'timerStartEvent', x: 0, y: 200, timer: { timeDuration: 'PT1H' } },
                  { id: 2, name: 'Work', type: 'task', x: 200, y: 0 }
                ],
                sequenceFlows: [{ id: 101, name: '', sourceRef: 1, targetRef: 2 }]
              });
              render();
              const timerCallback = captureTypeCallback(1);
              timerCallback('task');
              const timerSkipped = { initialEventId: model.initialEventId };

              // Business keys: initialization only when already enabled.
              loadFromObject({
                id: 'type-transition-business-keys',
                name: 'Business keys',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Keyed start', type: 'startEvent', x: 0, y: 0, businessKey: { variable: 'caseId', uniqueness: 'active' } },
                  { id: 5, name: 'Plain start', type: 'startEvent', x: 0, y: 200 },
                  { id: 3, name: 'Work', type: 'task', x: 200, y: 0 }
                ],
                sequenceFlows: []
              });
              render();
              const initCallback = captureTypeCallback(5);
              initCallback('messageStartEvent');
              const node5 = getNode(5);
              const initialized = {
                type: node5.type,
                businessKey: node5.businessKey
              };
              const returnCallback = captureTypeCallback(5);
              returnCallback('startEvent');
              const roundTripped = {
                type: getNode(5).type,
                businessKey: getNode(5).businessKey
              };

              // Existing key settings survive conversion to a message start.
              const preservedCallback = captureTypeCallback(1);
              preservedCallback('messageStartEvent');
              const preserved = {
                type: getNode(1).type,
                businessKey: getNode(1).businessKey
              };

              // Timer starts do not participate in business keys.
              const timerKeyCallback = captureTypeCallback(3);
              timerKeyCallback('timerStartEvent');
              const timerKey = {
                type: getNode(3).type,
                businessKeyUnset: getNode(3).businessKey == null
              };

              // No keys enabled anywhere: message start stays unkeyed.
              loadFromObject({
                id: 'type-transition-no-keys',
                name: 'No keys',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'First start', type: 'startEvent', x: 0, y: 0 },
                  { id: 5, name: 'Second start', type: 'startEvent', x: 0, y: 200 }
                ],
                sequenceFlows: []
              });
              render();
              const unkeyedCallback = captureTypeCallback(5);
              unkeyedCallback('messageStartEvent');
              const unkeyed = {
                type: getNode(5).type,
                businessKeyUnset: getNode(5).businessKey == null
              };
              return JSON.stringify({ repaired, nulled, timerSkipped, initialized, roundTripped, preserved, timerKey, unkeyed });
            })()
            """).AsString());

        var root = result.RootElement;
        Assert.Equal("userTask", root.GetProperty("repaired").GetProperty("type").GetString());
        Assert.Equal(4, root.GetProperty("repaired").GetProperty("initialEventId").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nulled").GetProperty("initialEventId").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("timerSkipped").GetProperty("initialEventId").ValueKind);

        var initialized = root.GetProperty("initialized");
        Assert.Equal("messageStartEvent", initialized.GetProperty("type").GetString());
        Assert.Equal(string.Empty, initialized.GetProperty("businessKey").GetProperty("variable").GetString());
        Assert.Equal("active", initialized.GetProperty("businessKey").GetProperty("uniqueness").GetString());

        var roundTripped = root.GetProperty("roundTripped");
        Assert.Equal("startEvent", roundTripped.GetProperty("type").GetString());
        Assert.Equal(
            initialized.GetProperty("businessKey").GetRawText(),
            roundTripped.GetProperty("businessKey").GetRawText());

        var preserved = root.GetProperty("preserved");
        Assert.Equal("messageStartEvent", preserved.GetProperty("type").GetString());
        Assert.Equal("caseId", preserved.GetProperty("businessKey").GetProperty("variable").GetString());
        Assert.Equal("active", preserved.GetProperty("businessKey").GetProperty("uniqueness").GetString());

        Assert.Equal("timerStartEvent", root.GetProperty("timerKey").GetProperty("type").GetString());
        Assert.True(root.GetProperty("timerKey").GetProperty("businessKeyUnset").GetBoolean());
        Assert.Equal("messageStartEvent", root.GetProperty("unkeyed").GetProperty("type").GetString());
        Assert.True(root.GetProperty("unkeyed").GetProperty("businessKeyUnset").GetBoolean());
    }

    [Fact]
    public void TypeTransition_TimerAndConditionalDefaults()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const loadFixture = (node) => loadFromObject({
                id: 'type-transition-defaults',
                name: 'Type defaults',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  node,
                  { id: 3, name: 'Done', type: 'endEvent', x: 400, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: '', sourceRef: 2, targetRef: 3 }
                ]
              });

              loadFixture({ id: 2, name: 'Work', type: 'task', x: 200, y: 0 });
              render();
              const toTimerCallback = captureTypeCallback(2);
              resetTypeRedrawCounters();
              toTimerCallback('intermediateTimerCatchEvent');
              const toTimer = {
                type: getNode(2).type,
                timer: getNode(2).timer,
                renders: typeFullRenders
              };

              loadFixture({ id: 2, name: 'Wait', type: 'intermediateTimerCatchEvent', x: 200, y: 0, timer: { timeDuration: 'PT5M' } });
              render();
              const timerKeptCallback = captureTypeCallback(2);
              timerKeptCallback('timerStartEvent');
              const timerKept = {
                type: getNode(2).type,
                timer: getNode(2).timer
              };

              loadFixture({ id: 2, name: 'Wait', type: 'intermediateTimerCatchEvent', x: 200, y: 0, timer: { timeDuration: 'PT5M' } });
              render();
              const timerGoneCallback = captureTypeCallback(2);
              timerGoneCallback('task');
              const timerGone = {
                type: getNode(2).type,
                timerAbsent: !Object.prototype.hasOwnProperty.call(getNode(2), 'timer')
              };

              loadFixture({ id: 2, name: 'Observe', type: 'task', x: 200, y: 0 });
              render();
              const toConditionalCallback = captureTypeCallback(2);
              toConditionalCallback('intermediateConditionalCatchEvent');
              const toConditional = {
                type: getNode(2).type,
                conditional: getNode(2).conditional
              };

              loadFixture({ id: 2, name: 'Observe', type: 'intermediateConditionalCatchEvent', x: 200, y: 0, conditional: { condition: 'ready == true' } });
              render();
              const conditionalKeptCallback = captureTypeCallback(2);
              conditionalKeptCallback('intermediateConditionalCatchEvent');
              const conditionalKept = {
                type: getNode(2).type,
                conditional: getNode(2).conditional
              };

              loadFixture({ id: 2, name: 'Observe', type: 'intermediateConditionalCatchEvent', x: 200, y: 0, conditional: { condition: 'ready == true' } });
              render();
              const conditionalGoneCallback = captureTypeCallback(2);
              conditionalGoneCallback('task');
              const conditionalGone = {
                type: getNode(2).type,
                conditionalAbsent: !Object.prototype.hasOwnProperty.call(getNode(2), 'conditional')
              };
              return JSON.stringify({ toTimer, timerKept, timerGone, toConditional, conditionalKept, conditionalGone });
            })()
            """).AsString());

        var root = result.RootElement;
        var toTimer = root.GetProperty("toTimer");
        Assert.Equal("intermediateTimerCatchEvent", toTimer.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, toTimer.GetProperty("timer").GetProperty("timeDate").ValueKind);
        Assert.Equal("PT1H", toTimer.GetProperty("timer").GetProperty("timeDuration").GetString());
        Assert.Equal(JsonValueKind.Null, toTimer.GetProperty("timer").GetProperty("timeCycle").ValueKind);
        Assert.Equal(1, toTimer.GetProperty("renders").GetInt32());

        var timerKept = root.GetProperty("timerKept");
        Assert.Equal("timerStartEvent", timerKept.GetProperty("type").GetString());
        Assert.Equal("PT5M", timerKept.GetProperty("timer").GetProperty("timeDuration").GetString());
        Assert.Equal(JsonValueKind.Null, timerKept.GetProperty("timer").GetProperty("timeDate").ValueKind);

        Assert.Equal("task", root.GetProperty("timerGone").GetProperty("type").GetString());
        Assert.True(root.GetProperty("timerGone").GetProperty("timerAbsent").GetBoolean());

        var toConditional = root.GetProperty("toConditional");
        Assert.Equal("intermediateConditionalCatchEvent", toConditional.GetProperty("type").GetString());
        Assert.Equal(string.Empty, toConditional.GetProperty("conditional").GetProperty("condition").GetString());
        Assert.False(toConditional.GetProperty("conditional").TryGetProperty("deliveryMode", out _));

        var conditionalKept = root.GetProperty("conditionalKept");
        Assert.Equal("intermediateConditionalCatchEvent", conditionalKept.GetProperty("type").GetString());
        Assert.Equal("ready == true", conditionalKept.GetProperty("conditional").GetProperty("condition").GetString());

        Assert.Equal("task", root.GetProperty("conditionalGone").GetProperty("type").GetString());
        Assert.True(root.GetProperty("conditionalGone").GetProperty("conditionalAbsent").GetBoolean());
    }

    [Fact]
    public void TypeTransition_UserTaskMetadataCleanupAndReturn()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const loadFixture = (node) => loadFromObject({
                id: 'type-transition-user-metadata',
                name: 'User task metadata',
                initialEventId: 1,
                variables: [{ id: 1, name: 'reviewers', dataType: 'string', isArray: true }],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  node,
                  { id: 5, name: 'Other', type: 'userTask', x: 400, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: 'Pass', sourceRef: 2, targetRef: 5 }
                ]
              });

              // Claim/assignment metadata is cleared by node normalization.
              loadFixture({
                id: 2,
                name: 'Review',
                type: 'userTask',
                x: 200,
                y: 0,
                requiresClaim: true,
                claimMode: 'previous',
                roles: ['Manager'],
                assignee: 'alice'
              });
              render();
              const claimCallback = captureTypeCallback(2);
              resetTypeRedrawCounters();
              claimCallback('task');
              const claimNode = getNode(2);
              const flow201 = model.sequenceFlows.find(f => f.id === 201);
              const claim = {
                type: claimNode.type,
                requiresClaim: claimNode.requiresClaim,
                claimMode: claimNode.claimMode,
                roles: claimNode.roles,
                assignee: claimNode.assignee,
                flow: { roles: flow201.roles, canActWithoutClaim: flow201.canActWithoutClaim, canActWithoutClaimRoles: flow201.canActWithoutClaimRoles }
              };

              // Required assignment and inbox visibility clear with the type.
              loadFixture({
                id: 2,
                name: 'Review',
                type: 'userTask',
                x: 200,
                y: 0,
                requiresAssignment: true,
                assignmentMode: 'fromNode',
                inheritAssignmentFromNodeId: 5,
                inboxVisibilityCondition: '[sys.user] == \'alice\''
              });
              render();
              const assignmentCallback = captureTypeCallback(2);
              assignmentCallback('task');
              const assignmentNode = getNode(2);
              const assignment = {
                type: assignmentNode.type,
                requiresAssignment: assignmentNode.requiresAssignment,
                assignmentMode: assignmentNode.assignmentMode,
                inheritAssignmentFromNodeId: assignmentNode.inheritAssignmentFromNodeId,
                inboxVisibilityAbsent: !Object.prototype.hasOwnProperty.call(assignmentNode, 'inboxVisibilityCondition')
              };

              // Engine-only outcomes and claim-bypass values reset on the flows.
              loadFixture({
                id: 2,
                name: 'Review',
                type: 'userTask',
                x: 200,
                y: 0,
                multiInstance: { mode: 'parallel', source: 'collection', collectionVariable: 'reviewers', resultVariable: '' }
              });
              render();
              model.sequenceFlows.find(f => f.id === 201).canActWithoutClaim = true;
              model.sequenceFlows.find(f => f.id === 201).canActWithoutClaimRoles = ['Helper'];
              model.sequenceFlows.find(f => f.id === 201).isSelectable = false;
              const engineOnlyCallback = captureTypeCallback(2);
              engineOnlyCallback('task');
              const engineOnlyFlow = model.sequenceFlows.find(f => f.id === 201);
              const engineOnly = {
                type: getNode(2).type,
                multiInstanceAbsent: !Object.prototype.hasOwnProperty.call(getNode(2), 'multiInstance'),
                flow: {
                  isSelectable: engineOnlyFlow.isSelectable,
                  roles: engineOnlyFlow.roles,
                  canActWithoutClaim: engineOnlyFlow.canActWithoutClaim,
                  canActWithoutClaimRoles: engineOnlyFlow.canActWithoutClaimRoles
                }
              };

              // Returning to a user task initializes fresh metadata.
              const returnCallback = captureTypeCallback(2);
              returnCallback('userTask');
              const returnNode = getNode(2);
              const returned = {
                type: returnNode.type,
                requiresClaim: returnNode.requiresClaim,
                claimMode: returnNode.claimMode,
                requiresAssignment: returnNode.requiresAssignment,
                assignmentMode: returnNode.assignmentMode,
                multiInstanceUnset: returnNode.multiInstance == null
              };
              return JSON.stringify({ claim, assignment, engineOnly, returned });
            })()
            """).AsString());

        var root = result.RootElement;
        var claim = root.GetProperty("claim");
        Assert.Equal("task", claim.GetProperty("type").GetString());
        Assert.False(claim.GetProperty("requiresClaim").GetBoolean());
        Assert.Equal("fresh", claim.GetProperty("claimMode").GetString());
        Assert.Empty(claim.GetProperty("roles").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, claim.GetProperty("assignee").ValueKind);
        Assert.Empty(claim.GetProperty("flow").GetProperty("roles").EnumerateArray());
        Assert.False(claim.GetProperty("flow").GetProperty("canActWithoutClaim").GetBoolean());
        Assert.Empty(claim.GetProperty("flow").GetProperty("canActWithoutClaimRoles").EnumerateArray());

        var assignment = root.GetProperty("assignment");
        Assert.Equal("task", assignment.GetProperty("type").GetString());
        Assert.False(assignment.GetProperty("requiresAssignment").GetBoolean());
        Assert.Equal("fresh", assignment.GetProperty("assignmentMode").GetString());
        Assert.Equal(JsonValueKind.Null, assignment.GetProperty("inheritAssignmentFromNodeId").ValueKind);
        Assert.True(assignment.GetProperty("inboxVisibilityAbsent").GetBoolean());

        var engineOnly = root.GetProperty("engineOnly");
        Assert.Equal("task", engineOnly.GetProperty("type").GetString());
        Assert.True(engineOnly.GetProperty("multiInstanceAbsent").GetBoolean());
        Assert.True(engineOnly.GetProperty("flow").GetProperty("isSelectable").GetBoolean());
        Assert.Empty(engineOnly.GetProperty("flow").GetProperty("roles").EnumerateArray());
        Assert.False(engineOnly.GetProperty("flow").GetProperty("canActWithoutClaim").GetBoolean());
        Assert.Empty(engineOnly.GetProperty("flow").GetProperty("canActWithoutClaimRoles").EnumerateArray());

        var returned = root.GetProperty("returned");
        Assert.Equal("userTask", returned.GetProperty("type").GetString());
        Assert.False(returned.GetProperty("requiresClaim").GetBoolean());
        Assert.Equal("fresh", returned.GetProperty("claimMode").GetString());
        Assert.False(returned.GetProperty("requiresAssignment").GetBoolean());
        Assert.Equal("fresh", returned.GetProperty("assignmentMode").GetString());
        Assert.True(returned.GetProperty("multiInstanceUnset").GetBoolean());
    }

    [Fact]
    public void TypeTransition_SameTypeAndSequentialActionsFollowBaseline()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              const loadFixture = () => loadFromObject({
                id: 'type-transition-sequence',
                name: 'Same type sequence',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Review', type: 'userTask', x: 200, y: 0, roles: ['Agent'] },
                  { id: 3, name: 'Done', type: 'endEvent', x: 400, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: 'Finish', sourceRef: 2, targetRef: 3 }
                ]
              });

              loadFixture();
              render();
              replayTypeChangeCommit();
              const settledHistory = undoHistory.length;
              const baseline = JSON.stringify(model);
              const sameTypeCallback = captureTypeCallback(2);
              resetTypeRedrawCounters();
              clearTypeDialogs();
              sameTypeCallback('userTask');
              const sameType = {
                renders: typeFullRenders,
                dialogs: typeDialogs.length,
                modelPreserved: JSON.stringify(model) === baseline,
                historyUnchanged: (replayTypeChangeCommit(), undoHistory.length) === settledHistory
              };

              clearTypeDialogs();
              const toTaskCallback = captureTypeCallback(2);
              toTaskCallback('task');
              replayTypeChangeCommit();
              const afterConvert = JSON.stringify(model);
              const converted = {
                type: getNode(2).type,
                historyCount: undoHistory.length
              };
              undo();
              const afterUndo = {
                type: getNode(2).type,
                modelRestored: JSON.stringify(model) === baseline
              };
              const againCallback = captureTypeCallback(2);
              againCallback('task');
              replayTypeChangeCommit();
              const afterAgain = {
                type: getNode(2).type,
                modelSame: JSON.stringify(model) === afterConvert,
                historyCount: undoHistory.length
              };
              return JSON.stringify({ sameType, converted, afterUndo, afterAgain, settledHistory });
            })()
            """).AsString());

        var root = result.RootElement;
        var sameType = root.GetProperty("sameType");
        Assert.Equal(1, sameType.GetProperty("renders").GetInt32());
        Assert.Equal(0, sameType.GetProperty("dialogs").GetInt32());
        Assert.True(sameType.GetProperty("modelPreserved").GetBoolean());
        Assert.True(sameType.GetProperty("historyUnchanged").GetBoolean());

        var converted = root.GetProperty("converted");
        Assert.Equal("task", converted.GetProperty("type").GetString());
        Assert.Equal(root.GetProperty("settledHistory").GetInt32() + 1, converted.GetProperty("historyCount").GetInt32());

        var afterUndo = root.GetProperty("afterUndo");
        Assert.Equal("userTask", afterUndo.GetProperty("type").GetString());
        Assert.True(afterUndo.GetProperty("modelRestored").GetBoolean());

        var afterAgain = root.GetProperty("afterAgain");
        Assert.Equal("task", afterAgain.GetProperty("type").GetString());
        Assert.True(afterAgain.GetProperty("modelSame").GetBoolean());
        Assert.Equal(converted.GetProperty("historyCount").GetInt32(), afterAgain.GetProperty("historyCount").GetInt32());
    }

    [Fact]
    public void TypeTransition_SaveRoundTripAndValidationFeedback()
    {
        var engine = CreateEditorEngine();
        engine.Execute(TypeTransitionHarnessJs);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            (() => {
              // Destructive conversion then save/reload stability.
              loadFromObject({
                id: 'type-transition-roundtrip',
                name: 'Round trip',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Review', type: 'userTask', x: 200, y: 0, roles: ['Agent'], requiresClaim: true },
                  { id: 3, name: 'A', type: 'userTask', x: 400, y: -60 },
                  { id: 4, name: 'B', type: 'userTask', x: 400, y: 60 },
                  { id: 6, name: 'Done', type: 'endEvent', x: 600, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 203, name: 'First', sourceRef: 2, targetRef: 6 },
                  { id: 201, name: 'Second', sourceRef: 2, targetRef: 3 },
                  { id: 202, name: 'Third', sourceRef: 2, targetRef: 4 },
                  { id: 601, name: '', sourceRef: 3, targetRef: 6 },
                  { id: 602, name: '', sourceRef: 4, targetRef: 6 }
                ]
              });
              render();
              const callback = captureTypeCallback(2);
              resetTypeRedrawCounters();
              callback('task');
              const saved = JSON.stringify(model);
              loadFromObject(JSON.parse(saved));
              render();
              const reloaded = JSON.stringify(model);
              loadFromObject(JSON.parse(reloaded));
              render();
              const twice = JSON.stringify(model);
              const roundTrip = {
                savedOmitsPruned: !saved.includes('"id":201') && !saved.includes('"id":202'),
                stable: reloaded === twice
              };

              // Valid final fixture saves without validation feedback.
              loadFromObject({
                id: 'type-transition-valid',
                name: 'Valid conversion',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Route', type: 'exclusiveGateway', x: 200, y: 0 },
                  { id: 3, name: 'Big', type: 'userTask', x: 400, y: -60 },
                  { id: 4, name: 'Small', type: 'userTask', x: 400, y: 60 },
                  { id: 5, name: 'Done', type: 'endEvent', x: 600, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: 'Large', sourceRef: 2, targetRef: 3, condition: 'amount > 100', conditionPriority: 1 },
                  { id: 202, name: 'Fallback', sourceRef: 2, targetRef: 4, isDefault: true },
                  { id: 301, name: '', sourceRef: 3, targetRef: 5 },
                  { id: 302, name: '', sourceRef: 4, targetRef: 5 }
                ]
              });
              render();
              const validCallback = captureTypeCallback(2);
              typeConfirmResponse = true;
              validCallback('userTask');
              const valid = {
                errors: validateModelForSave(model),
                type: getNode(2).type
              };

              // Cleared gateway conditions leave intentionally invalid feedback.
              loadFromObject({
                id: 'type-transition-invalid',
                name: 'Invalid intermediate',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Route', type: 'exclusiveGateway', x: 200, y: 0 },
                  { id: 3, name: 'Big', type: 'userTask', x: 400, y: -60 },
                  { id: 4, name: 'Small', type: 'userTask', x: 400, y: 60 },
                  { id: 5, name: 'Done', type: 'endEvent', x: 600, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: 'Large', sourceRef: 2, targetRef: 3, condition: 'amount > 100', conditionPriority: 1 },
                  { id: 202, name: 'Fallback', sourceRef: 2, targetRef: 4, isDefault: true },
                  { id: 301, name: '', sourceRef: 3, targetRef: 5 },
                  { id: 302, name: '', sourceRef: 4, targetRef: 5 }
                ]
              });
              render();
              const invalidCallback = captureTypeCallback(2);
              typeConfirmResponse = true;
              invalidCallback('inclusiveGateway');
              const invalid = {
                errors: validateModelForSave(model),
                type: getNode(2).type
              };

              // Blank conditional expression is reported by save validation.
              loadFromObject({
                id: 'type-transition-blank-conditional',
                name: 'Blank conditional',
                initialEventId: 1,
                variables: [],
                lanes: [],
                flowNodes: [
                  { id: 1, name: 'Submitted', type: 'startEvent', x: 0, y: 0 },
                  { id: 2, name: 'Observe', type: 'task', x: 200, y: 0 },
                  { id: 3, name: 'Done', type: 'endEvent', x: 400, y: 0 }
                ],
                sequenceFlows: [
                  { id: 101, name: '', sourceRef: 1, targetRef: 2 },
                  { id: 201, name: '', sourceRef: 2, targetRef: 3 }
                ]
              });
              render();
              const blankCallback = captureTypeCallback(2);
              blankCallback('intermediateConditionalCatchEvent');
              const blank = {
                errors: validateModelForSave(model),
                type: getNode(2).type
              };
              return JSON.stringify({ roundTrip, valid, invalid, blank });
            })()
            """).AsString());

        var root = result.RootElement;
        var roundTrip = root.GetProperty("roundTrip");
        Assert.True(roundTrip.GetProperty("savedOmitsPruned").GetBoolean());
        Assert.True(roundTrip.GetProperty("stable").GetBoolean());

        var valid = root.GetProperty("valid");
        Assert.Equal("userTask", valid.GetProperty("type").GetString());
        Assert.Empty(valid.GetProperty("errors").EnumerateArray());

        var invalid = root.GetProperty("invalid");
        Assert.Equal("inclusiveGateway", invalid.GetProperty("type").GetString());
        var invalidErrors = invalid.GetProperty("errors").EnumerateArray()
            .Select(error => error.GetString() ?? string.Empty)
            .ToArray();
        Assert.NotEmpty(invalidErrors);
        Assert.Contains(invalidErrors, error =>
            error.Contains("default outgoing sequence flow", StringComparison.Ordinal));
        Assert.Contains(invalidErrors, error =>
            error.Contains("must define a condition", StringComparison.Ordinal));

        var blank = root.GetProperty("blank");
        Assert.Equal("intermediateConditionalCatchEvent", blank.GetProperty("type").GetString());
        var blankErrors = blank.GetProperty("errors").EnumerateArray()
            .Select(error => error.GetString() ?? string.Empty)
            .ToArray();
        Assert.Contains(blankErrors, error =>
            error.Contains("condition must not be blank", StringComparison.Ordinal));
    }

    private static Engine CreateEditorEngine(string? beforeMainScript = null)
    {
        var html = ReadEditorSource();
        var scripts = Regex.Matches(
            html,
            @"<script(?:\s[^>]*)?>(?<code>[\s\S]*?)</script>");
        Assert.True(scripts.Count >= 2, "The editor's main inline script was not found.");

        var engine = new Engine();
        engine.Execute(DomStubs);
        if (!string.IsNullOrWhiteSpace(beforeMainScript)) engine.Execute(beforeMainScript);
        engine.Execute(scripts[^1].Groups["code"].Value);
        return engine;
    }

    private const string DomStubs =
        """
        const fakeClassList = {
          add() {},
          remove() {},
          toggle() { return false; },
          contains() { return false; }
        };
        function fakeElement(tag = 'div') {
          return {
            tagName: String(tag).toUpperCase(),
            style: { setProperty() {} },
            dataset: {},
            classList: fakeClassList,
            children: [],
            attributes: {},
            eventListeners: {},
            value: '',
            checked: false,
            disabled: false,
            hidden: false,
            textContent: '',
            innerHTML: '',
            clientWidth: 1000,
            clientHeight: 700,
            appendChild(child) {
              this.children.push(child);
              if (child) child.parentNode = this;
              return child;
            },
            replaceChildren(...children) {
              this.children = children;
              children.forEach(child => { if (child) child.parentNode = this; });
            },
            remove() {},
            addEventListener(type, listener, options) {
              const capture = options === true || !!(options && options.capture);
              (this.eventListeners[type] ||= []).push({ listener, capture });
            },
            dispatchEvent(event) {
              event.target ||= this;
              const path = [];
              for (let element = this; element; element = element.parentNode) path.push(element);
              const invoke = (element, capture) => {
                event.currentTarget = element;
                const listeners = element.eventListeners?.[event.type] || [];
                for (const entry of listeners) {
                  if (entry.capture !== capture) continue;
                  entry.listener.call(element, event);
                  if (event.immediatePropagationStopped) break;
                }
              };
              for (let index = path.length - 1;
                   index > 0 && !event.propagationStopped;
                   index--) invoke(path[index], true);
              if (!event.propagationStopped) {
                invoke(path[0], true);
                if (!event.immediatePropagationStopped) invoke(path[0], false);
              }
              if (event.bubbles !== false) {
                for (let index = 1;
                     index < path.length && !event.propagationStopped;
                     index++) invoke(path[index], false);
              }
              return !event.defaultPrevented;
            },
            setAttribute(key, value) { this.attributes[key] = String(value); },
            setAttributeNS(_namespace, key, value) { this.attributes[key] = String(value); },
            getAttribute(key) { return this.attributes[key] ?? null; },
            removeAttribute(key) { delete this.attributes[key]; },
            querySelectorAll() { return []; },
            querySelector() { return null; },
            closest() { return null; },
            matches() { return false; },
            contains() { return false; },
            focus() {},
            blur() {},
            click() {},
            setPointerCapture() {},
            releasePointerCapture() {},
            getBoundingClientRect() {
              return {
                x: 0, y: 0, left: 0, top: 0,
                right: 1000, bottom: 700, width: 1000, height: 700
              };
            },
            getTotalLength() { return 100; },
            getPointAtLength(value) { return { x: value, y: 0 }; },
            createSVGPoint() {
              return {
                x: 0,
                y: 0,
                matrixTransform() { return { x: this.x, y: this.y }; }
              };
            },
            getScreenCTM() {
              return { inverse() { return {}; } };
            }
          };
        }
        function fakePointerEvent(type, target, pointerId, clientX, clientY, button = 0) {
          return {
            type,
            target,
            pointerId,
            clientX,
            clientY,
            button,
            bubbles: true,
            defaultPrevented: false,
            propagationStopped: false,
            immediatePropagationStopped: false,
            preventDefault() { this.defaultPrevented = true; },
            stopPropagation() { this.propagationStopped = true; },
            stopImmediatePropagation() {
              this.immediatePropagationStopped = true;
              this.propagationStopped = true;
            }
          };
        }
        const fakeElements = new Map();
        const documentEventListeners = {};
        const document = {
          documentElement: fakeElement('html'),
          body: fakeElement('body'),
          activeElement: null,
          getElementById(id) {
            if (!fakeElements.has(id)) {
              fakeElements.set(id, fakeElement(id === 'svg' ? 'svg' : 'div'));
            }
            return fakeElements.get(id);
          },
          querySelector(selector) {
            if (selector === 'main') return this.getElementById('main');
            if (selector === '.canvas-wrap') return this.getElementById('canvas-wrap');
            return fakeElement();
          },
          querySelectorAll() { return []; },
          createElement: fakeElement,
          createElementNS(_namespace, tag) { return fakeElement(tag); },
          createTextNode(text) {
            const node = fakeElement('#text');
            node.textContent = String(text);
            return node;
          },
          addEventListener(type, listener, options) {
            const capture = options === true || !!(options && options.capture);
            (documentEventListeners[type] ||= []).push({ listener, capture });
          },
          dispatchEvent(event) {
            event.target ||= this;
            event.currentTarget = this;
            const listeners = documentEventListeners[event.type] || [];
            for (const capture of [true, false]) {
              for (const entry of listeners) {
                if (entry.capture === capture) entry.listener.call(this, event);
              }
            }
            return !event.defaultPrevented;
          }
        };
        const localStorageValues = new Map();
        const localStorage = {
          getItem(key) {
            return localStorageValues.has(String(key))
              ? localStorageValues.get(String(key))
              : null;
          },
          setItem(key, value) {
            localStorageValues.set(String(key), String(value));
          }
        };
        const fakeUrl = {
          createObjectURL() { return 'blob:test'; },
          revokeObjectURL() {}
        };
        const windowEventListeners = {};
        const window = {
          document,
          localStorage,
          URL: fakeUrl,
          innerWidth: 1400,
          innerHeight: 900,
          addEventListener(type, listener) {
            (windowEventListeners[type] ||= []).push(listener);
          },
          removeEventListener(type, listener) {
            const listeners = windowEventListeners[type] || [];
            const index = listeners.indexOf(listener);
            if (index >= 0) listeners.splice(index, 1);
          },
          dispatchEvent(event) {
            event.target ||= this;
            event.currentTarget = this;
            for (const listener of windowEventListeners[event.type] || []) {
              listener.call(this, event);
            }
            return !event.defaultPrevented;
          },
          matchMedia() {
            return { matches: false, addEventListener() {} };
          },
          showSaveFilePicker: null
        };
        function Blob() {}
        function FileReader() {}
        function ResizeObserver() { this.observe = function() {}; }
        function alert() {}
        function confirm() { return true; }
        function setTimeout() { return 0; }
        function clearTimeout() {}
        function requestAnimationFrame(callback) { callback(); return 0; }
        function structuredClone(value) {
          return JSON.parse(JSON.stringify(value));
        }
        const navigator = {};
        const URL = fakeUrl;
        window.requestAnimationFrame = requestAnimationFrame;
        """;

    private static string ReadEditorSource()
    {
        var editorPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "flowbit-editor.html");
        return File.ReadAllText(editorPath);
    }
}
