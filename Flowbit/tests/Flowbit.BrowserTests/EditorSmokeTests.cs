using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// Automated editor matrix (E1-E9) against the real copied editor served over
/// localhost. E1-E9 run at 1440x900 and 1024x768; every scenario keeps fresh
/// state so a failed edit cannot invalidate later assertions. Editor open and
/// inspector pin run inside RunAsync so setup failures still capture traces.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class EditorSmokeTests(BrowserStackFixture stack)
{
    public static TheoryData<int, int> EditorViewports => new()
    {
        { 1440, 900 },
        { 1024, 768 },
    };

    private const string EndGuardAlert =
        "Remove all outgoing sequence flows before changing this node to an end event.";

    private const string GatewayClearsPrompt =
        "Changing this node's gateway semantics clears outgoing defaults, conditions, priorities, roles, and variables. Continue?";

    private const string TypeTransitionFixture = "editor-type-transition.json";

    private async Task<BrowserScenario> CreateEditorScenarioAsync(
        string name, int width, int height)
    {
        var scenario = await stack.CreateScenario(name, width, height, blockNativeFilePicker: true);
        scenario.ExpectDialogStartingWith(IdentityScreen.FallbackAlertPrefix);
        return scenario;
    }

    private async Task OpenEditorAsync(BrowserScenario scenario)
    {
        await scenario.OpenEditorAsync(stack.EditorBaseAddress);
        await EditorInteractions.PinInspectorOpenAsync(scenario.Page);
    }

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E1_LoadEditSaveAndReloadPreservesModel(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await CreateEditorScenarioAsync(
            $"e1-load-edit-save-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("load-edit-save", async () =>
        {
            await OpenEditorAsync(scenario);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath("editor-basic.json"));

            // Expected lanes, nodes, and flows render.
            await Assertions.Expect(page.Locator("#lanes .lane[data-id='1']")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#lanes .lane[data-id='2']")).ToBeVisibleAsync();
            foreach (var nodeId in new[] { 1, 2, 3, 4 })
            {
                await Assertions.Expect(page.Locator($"#nodes .node[data-id='{nodeId}']")).ToBeAttachedAsync();
            }
            foreach (var flowId in new[] { 101, 102, 103 })
            {
                await Assertions.Expect(page.Locator($"#edges .edge-path-layer .edge[data-flow='{flowId}']")).ToBeAttachedAsync();
            }

            // Select node 2 and edit its name through the inspector.
            var nodeBox = await EditorInteractions.GetNodeBoxAsync(page, 2);
            await EditorInteractions.ClickCenterAsync(page, nodeBox);
            await Assertions.Expect(page.Locator("#inspector")).ToContainTextAsync("Node #2");
            await EditorInteractions.FillInspectorNameAsync(page, "Renamed review");
            await Assertions.Expect(
                page.Locator("#nodes .node[data-id='2']").Locator(".node-title")).ToContainTextAsync(
                "Renamed review");

            // Edit the workflow name in the header field.
            await EditorInteractions.SetWorkflowNameAsync(page, "Browser editor renamed");

            // Save through the forced fallback download path.
            var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(
                page, scenario, scenario.ArtifactDirectory);
            var savedRoot = saved.RootElement;

            Assert.Equal("browser-editor-basic", savedRoot.GetProperty("id").GetString());
            Assert.Equal("Browser editor renamed", savedRoot.GetProperty("name").GetString());
            Assert.Equal(2, savedRoot.GetProperty("lanes").GetArrayLength());
            Assert.Equal(4, savedRoot.GetProperty("flowNodes").GetArrayLength());
            Assert.Equal(3, savedRoot.GetProperty("sequenceFlows").GetArrayLength());
            var savedNode2 = savedRoot.GetProperty("flowNodes")
                .EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 2);
            Assert.Equal("Renamed review", savedNode2.GetProperty("name").GetString());
            var savedFlow102 = savedRoot.GetProperty("sequenceFlows")
                .EnumerateArray().First(flow => flow.GetProperty("id").GetInt32() == 102);
            Assert.Equal(2, savedFlow102.GetProperty("sourceRef").GetInt32());
            Assert.Equal(3, savedFlow102.GetProperty("targetRef").GetInt32());

            // A fresh page loads the downloaded file and agrees with the model.
            var freshPage = await scenario.Context.NewPageAsync();
            try
            {
                await freshPage.GotoAsync($"{stack.EditorBaseAddress}/",
                    new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                await freshPage.WaitForSelectorAsync("#svg");
                await EditorInteractions.LoadSavedFileAsync(freshPage, scenario.LastSavedFile!);
                Assert.Equal(
                    "Browser editor renamed",
                    await freshPage.Locator("#wfName").InputValueAsync());
                await Assertions.Expect(
                    freshPage.Locator("#nodes .node[data-id='2']").Locator(".node-title")).ToContainTextAsync(
                    "Renamed review");
                await Assertions.Expect(
                    freshPage.Locator("#lanes .lane[data-id='2']")).ToBeVisibleAsync();
            }
            finally
            {
                await freshPage.CloseAsync();
            }
        });
    }

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E2_NodeDragMovesGeometryAndPersistedPosition(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await CreateEditorScenarioAsync(
            $"e2-node-drag-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("node-drag", async () =>
        {
            await OpenEditorAsync(scenario);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath("editor-basic.json"));
            await EditorInteractions.SelectMoveToolAsync(page);

            var targetBefore = await EditorInteractions.GetNodeBoxAsync(page, 2);
            var otherBefore = await EditorInteractions.GetNodeBoxAsync(page, 4);
            var zoom = await EditorInteractions.GetZoomAsync(page);
            Assert.True(zoom > 0.5 && zoom < 2, $"Unexpected initial zoom {zoom}.");

            const float dragScreenDx = 96;
            const float dragScreenDy = 48;
            await EditorInteractions.DragAsync(
                page,
                targetBefore.X + targetBefore.Width / 2,
                targetBefore.Y + targetBefore.Height / 2,
                targetBefore.X + targetBefore.Width / 2 + dragScreenDx,
                targetBefore.Y + targetBefore.Height / 2 + dragScreenDy);

            var targetAfter = await EditorInteractions.GetNodeBoxAsync(page, 2);
            var otherAfter = await EditorInteractions.GetNodeBoxAsync(page, 4);

            // The node moved by the dragged screen delta (zoom is 1 here), an
            // unrelated node stayed fixed, and connectors still attach.
            Assert.True(
                Math.Abs((targetAfter.X - targetBefore.X) - dragScreenDx) <= 2,
                $"Node 2 screen X moved by {(targetAfter.X - targetBefore.X):0.##}, expected ~{dragScreenDx}.");
            Assert.True(
                Math.Abs((targetAfter.Y - targetBefore.Y) - dragScreenDy) <= 2,
                $"Node 2 screen Y moved by {(targetAfter.Y - targetBefore.Y):0.##}, expected ~{dragScreenDy}.");
            Assert.True(
                Math.Abs(otherAfterDelta(otherAfter, otherBefore)) <= 1.5
                && Math.Abs(otherAfterDeltaY(otherAfter, otherBefore)) <= 1.5,
                "An unrelated node moved during a single-node drag.");
            await EditorInteractions.AssertEndpointTouchesNodeAsync(page, 101, 2, atStart: false);
            await EditorInteractions.AssertEndpointTouchesNodeAsync(page, 102, 2, atStart: true);

            // The diagram-space delta is visible in the saved JSON.
            var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(
                page, scenario, scenario.ArtifactDirectory);
            var nodes = saved.RootElement.GetProperty("flowNodes");
            var node2 = nodes.EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 2);
            var node4 = nodes.EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 4);
            var expectedModelDx = dragScreenDx / zoom;
            var expectedModelDy = dragScreenDy / zoom;
            Assert.True(
                Math.Abs(node2.GetProperty("x").GetDouble() - (200 + expectedModelDx)) <= 2,
                $"Saved node 2 x={node2.GetProperty("x").GetDouble():0.##}, expected ~{200 + expectedModelDx:0.##}.");
            Assert.True(
                Math.Abs(node2.GetProperty("y").GetDouble() - (280 + expectedModelDy)) <= 2,
                $"Saved node 2 y={node2.GetProperty("y").GetDouble():0.##}, expected ~{280 + expectedModelDy:0.##}.");
            Assert.Equal(620, node4.GetProperty("x").GetInt32());
            Assert.Equal(280, node4.GetProperty("y").GetInt32());
        });
    }

    private static double otherAfterDelta(LocatorBoundingBoxResult after, LocatorBoundingBoxResult before) =>
        after.X - before.X;

    private static double otherAfterDeltaY(LocatorBoundingBoxResult after, LocatorBoundingBoxResult before) =>
        after.Y - before.Y;

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E3_LaneDragMovesLaneWithChildren(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await CreateEditorScenarioAsync(
            $"e3-lane-drag-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("lane-drag", async () =>
        {
            await OpenEditorAsync(scenario);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath("editor-basic.json"));
            await EditorInteractions.SelectMoveToolAsync(page);

            var laneBefore = await EditorInteractions.GetLaneBoxAsync(page, 1);
            var childBefore = await EditorInteractions.GetNodeBoxAsync(page, 1);
            var otherLaneBefore = await EditorInteractions.GetNodeBoxAsync(page, 3);

            // Drag a safe header/body point away from the resize handle
            // (bottom-right), child nodes, and the left-edge authoring palette
            // handle (which sits over the lane's top-left corner).
            const float dragDx = 64;
            const float dragDy = 52;
            var startScreenX = laneBefore.X + laneBefore.Width * 0.6f;
            var startScreenY = laneBefore.Y + 16f;
            await EditorInteractions.DragAsync(
                page,
                startScreenX, startScreenY,
                startScreenX + dragDx, startScreenY + dragDy);

            var laneAfter = await EditorInteractions.GetLaneBoxAsync(page, 1);
            var childAfter = await EditorInteractions.GetNodeBoxAsync(page, 1);
            var otherLaneAfter = await EditorInteractions.GetNodeBoxAsync(page, 3);

            Assert.True(
                Math.Abs((laneAfter.X - laneBefore.X) - dragDx) <= 2 &&
                Math.Abs((laneAfter.Y - laneBefore.Y) - dragDy) <= 2,
                $"Lane 1 moved by ({laneAfter.X - laneBefore.X:0.##},{laneAfter.Y - laneBefore.Y:0.##}), expected ~({dragDx},{dragDy}).");
            Assert.True(
                Math.Abs((childAfter.X - laneAfter.X) - (childBefore.X - laneBefore.X)) <= 1.5 &&
                Math.Abs((childAfter.Y - laneAfter.Y) - (childBefore.Y - laneBefore.Y)) <= 1.5,
                "The child node's relative offset to its lane changed during the lane drag.");
            Assert.True(
                Math.Abs(otherLaneAfter.X - otherLaneBefore.X) <= 1.5 &&
                Math.Abs(otherLaneAfter.Y - otherLaneBefore.Y) <= 1.5,
                "A node in another lane moved during a lane drag.");

            // Persisted positions match the moved lane and its children. The
            // editor auto-fits the view on load, so convert the screen delta
            // through the current zoom.
            var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(
                page, scenario, scenario.ArtifactDirectory);
            var zoom = await EditorInteractions.GetZoomAsync(page);
            var expectedModelDx = dragDx / zoom;
            var expectedModelDy = dragDy / zoom;
            var lanes = saved.RootElement.GetProperty("lanes");
            var lane1 = lanes.EnumerateArray().First(lane => lane.GetProperty("id").GetInt32() == 1);
            Assert.True(
                Math.Abs(lane1.GetProperty("x").GetDouble() - (20 + expectedModelDx)) <= 2 &&
                Math.Abs(lane1.GetProperty("y").GetDouble() - (20 + expectedModelDy)) <= 2,
                $"Saved lane 1 at ({lane1.GetProperty("x").GetDouble():0.##},{lane1.GetProperty("y").GetDouble():0.##}), " +
                $"expected ~({20 + expectedModelDx:0.##},{20 + expectedModelDy:0.##}).");
            var nodes = saved.RootElement.GetProperty("flowNodes");
            var node1 = nodes.EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 1);
            Assert.True(
                Math.Abs(node1.GetProperty("x").GetDouble() - (60 + expectedModelDx)) <= 2 &&
                Math.Abs(node1.GetProperty("y").GetDouble() - (90 + expectedModelDy)) <= 2,
                $"Saved node 1 at ({node1.GetProperty("x").GetDouble():0.##},{node1.GetProperty("y").GetDouble():0.##}), " +
                $"expected ~({60 + expectedModelDx:0.##},{90 + expectedModelDy:0.##}).");
            var node3 = nodes.EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 3);
            Assert.Equal(420, node3.GetProperty("x").GetInt32());
            Assert.Equal(280, node3.GetProperty("y").GetInt32());
        });
    }

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E4_InvalidWorkflowShowsValidationDialogAndRecovers(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await CreateEditorScenarioAsync(
            $"e4-validation-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("validation-dialog", async () =>
        {
            await OpenEditorAsync(scenario);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath("editor-invalid.json"));

            await page.Locator("#fileMenuSummary").ClickAsync();
            await page.Locator("#saveBtn").ClickAsync();
            var errors = await EditorInteractions.WaitForValidationDialogAsync(page);
            await Assertions.Expect(page.Locator("#validation-title")).ToHaveTextAsync("Workflow cannot be saved");
            Assert.Contains("Exclusive gateway #3", errors, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("split", errors, StringComparison.OrdinalIgnoreCase);

            var activeElementId = await page.EvaluateAsync<string>("document.activeElement && document.activeElement.id");
            Assert.Equal("validation-close", activeElementId);
            Assert.Empty(scenario.Downloads);

            await EditorInteractions.CloseValidationDialogWithEscapeAsync(page);

            // The editor remains usable: load valid input and saving succeeds.
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath("editor-basic.json"));
            var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(
                page, scenario, scenario.ArtifactDirectory);
            Assert.Equal(
                "browser-editor-basic",
                saved.RootElement.GetProperty("id").GetString());
            Assert.Single(scenario.Downloads);
        });
    }

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E5_KeyboardFocusMenusSearchAndNativeInputEditing(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await CreateEditorScenarioAsync(
            $"e5-keyboard-focus-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("keyboard-focus", async () =>
        {
            await OpenEditorAsync(scenario);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath("editor-basic.json"));

            // Tab through toolbar controls until they receive focus.
            await page.Locator("body").ClickAsync(new LocatorClickOptions { Position = new Position { X = 2, Y = 2 } });
            var reachedIds = new List<string>();
            for (var tab = 0; tab < 14; tab++)
            {
                var id = await page.EvaluateAsync<string?>(
                    """
                    (() => {
                      const element = document.activeElement;
                      if (!element) return '';
                      if (element.id) return element.id;
                      const className = element.className;
                      if (typeof className === 'string') return className;
                      return String(className?.baseVal ?? '');
                    })()
                    """);
                reachedIds.Add(id ?? string.Empty);
                await page.Keyboard.PressAsync("Tab");
            }
            Assert.Contains(reachedIds, id => id.Contains("fileMenuSummary", StringComparison.Ordinal));
            Assert.Contains(reachedIds, id => id.Contains("editMenuSummary", StringComparison.Ordinal));
            Assert.Contains(reachedIds, id => id.Contains("viewMenuSummary", StringComparison.Ordinal));
            Assert.Contains(reachedIds, id => id.Contains("wfName", StringComparison.Ordinal));

            // Open the File menu with the keyboard and close it with Escape,
            // which restores the trigger focus.
            await page.Locator("#fileMenuSummary").FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            Assert.True(await page.EvaluateAsync<bool?>("document.getElementById('fileMenu').open"));
            await page.Keyboard.PressAsync("Escape");
            Assert.False(await page.EvaluateAsync<bool?>("document.getElementById('fileMenu').open"));
            Assert.Equal(
                "fileMenuSummary",
                await page.EvaluateAsync<string?>("document.activeElement && document.activeElement.id"));

            // Diagram search: "/" focuses it, typing shows matches, Enter
            // selects the best-ranked match (node 2, named "Review").
            await page.Mouse.MoveAsync(2, viewportHeight - 2);
            await page.WaitForFunctionAsync("getComputedStyle(document.querySelector('.diagram-tools-content')).visibility === 'hidden'");
            await page.Keyboard.PressAsync("/");
            await Assertions.Expect(page.Locator("#diagramSearchInput")).ToBeFocusedAsync();
            await page.Keyboard.TypeAsync("review");
            var results = page.Locator("#diagramSearchResults");
            await Assertions.Expect(results).ToBeVisibleAsync();
            await Assertions.Expect(
                results.Locator(".diagram-search-result").First).ToContainTextAsync("Review");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "search-shortcut.png"),
                Animations = ScreenshotAnimations.Disabled
            });
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(page.Locator("#inspector")).ToContainTextAsync("Node #2");

            // Typing tool-shortcut letters inside an inspector input edits the
            // text and never switches tools (the tool stays whatever it was).
            var toolBeforeTyping = await page.EvaluateAsync<string?>("svg.dataset.activeTool");
            var nameInput = await EditorInteractions.FillInspectorNameAsync(page, "rvh");
            Assert.Equal(
                toolBeforeTyping,
                await page.EvaluateAsync<string?>("svg.dataset.activeTool"));
            await Assertions.Expect(nameInput).ToHaveValueAsync("rvh");
            foreach (var (key, expected) in new[] { ("p", "rvhp"), ("a", "rvhpa"), ("n", "rvhpan") })
            {
                await page.Keyboard.PressAsync(key);
                await Assertions.Expect(nameInput).ToHaveValueAsync(
                    expected,
                    new LocatorAssertionsToHaveValueOptions { Timeout = 5_000f });
            }
            Assert.Equal(
                toolBeforeTyping,
                await page.EvaluateAsync<string?>("svg.dataset.activeTool"));

            // Native input editing: Ctrl+Z inside the input is the browser's
            // text undo (it re-fires the input event, so the model follows the
            // reverted text) and never touches editor history.
            await Assertions.Expect(page.Locator("#redoBtn")).ToBeDisabledAsync();
            await page.Keyboard.PressAsync("Control+z");
            var afterNativeUndo = await nameInput.InputValueAsync();
            Assert.NotEqual("rvhpan", afterNativeUndo);
            await Assertions.Expect(page.Locator("#redoBtn")).ToBeDisabledAsync();

            // Outside a text input, Ctrl+Z steps editor history back and
            // Ctrl+Y re-applies it (the round trip returns to the same name).
            await page.Keyboard.PressAsync("Escape");
            await page.EvaluateAsync("document.activeElement && document.activeElement.blur()");
            var renamed = page.Locator("#nodes .node[data-id='2']").Locator(".node-title");
            var titleBeforeUndo = await renamed.TextContentAsync();
            await page.Keyboard.PressAsync("Control+z");
            await Assertions.Expect(renamed).Not.ToHaveTextAsync(
                titleBeforeUndo ?? string.Empty);
            await page.Keyboard.PressAsync("Control+y");
            await Assertions.Expect(renamed).ToHaveTextAsync(
                titleBeforeUndo ?? string.Empty);
        });
    }

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E6_GuardedTypeChangesPromptRestoreAndConvert(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await CreateEditorScenarioAsync(
            $"e6-guarded-type-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        scenario.ExpectDialogOnce("alert", EndGuardAlert, accept: false);
        scenario.ExpectDialogOnce("confirm", GatewayClearsPrompt, accept: false);
        scenario.ExpectDialogOnce("confirm", GatewayClearsPrompt, accept: true);
        await scenario.RunAsync("guarded-type-changes", async () =>
        {
            await OpenEditorAsync(scenario);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath(TypeTransitionFixture));

            // 1) An end conversion on a node with outgoing flows is rejected by
            // an alert; the rebuilt inspector restores the prior Type value.
            await EditorInteractions.SelectNodeAsync(page, 2);
            await EditorInteractions.SelectTypeAsync(page, "endEvent");
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("userTask");
            await Assertions.Expect(
                page.Locator("#nodes .node[data-id='2'] > rect")).ToBeVisibleAsync();

            // 2) Gateway conversion with routing metadata: Cancel restores the
            // selector and keeps the gateway shape and edge metadata.
            await EditorInteractions.SelectNodeAsync(page, 3);
            await EditorInteractions.SelectTypeAsync(page, "userTask");
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("exclusiveGateway");
            await Assertions.Expect(
                page.Locator("#nodes .node[data-id='3'] > polygon")).ToBeVisibleAsync();
            var badgesBeforeAccept = await page.Locator("#inspector .inspector-card .badge").AllTextContentsAsync();
            Assert.Contains(badgesBeforeAccept, badge =>
                badge.Contains("[amount > 100]", StringComparison.Ordinal));
            Assert.Contains(badgesBeforeAccept, badge =>
                badge.Contains("default", StringComparison.Ordinal));

            // 3) Accepting the same conversion converts the node and clears
            // the outgoing routing metadata.
            await EditorInteractions.SelectTypeAsync(page, "userTask");
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("userTask");
            await Assertions.Expect(
                page.Locator("#nodes .node[data-id='3'] > rect")).ToBeVisibleAsync();
            var badgesAfterAccept = await page.Locator("#inspector .inspector-card .badge").AllTextContentsAsync();
            Assert.DoesNotContain(badgesAfterAccept, badge =>
                badge.Contains("[amount > 100]", StringComparison.Ordinal));
            Assert.DoesNotContain(badgesAfterAccept, badge =>
                badge.Contains("default", StringComparison.Ordinal));
            Assert.Equal(2, badgesAfterAccept.Count);

            // 4) An accepted change driven purely through the keyboard: focus
            // the Type select and press ArrowDown (user task -> task).
            var keyboardSelect = EditorInteractions.TypeSelect(page);
            await keyboardSelect.FocusAsync();
            await page.Keyboard.PressAsync("ArrowDown");
            await Assertions.Expect(keyboardSelect).ToHaveValueAsync("task");
            await EditorInteractions.SelectNodeAsync(page, 3);
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("task");
        });
    }

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E7_DestructiveConversionUndoRedoAndReload(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await CreateEditorScenarioAsync(
            $"e7-destructive-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("destructive-conversion-history", async () =>
        {
            await OpenEditorAsync(scenario);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath(TypeTransitionFixture));

            // Prune: the user task with two outgoing flows becomes a task and
            // keeps only the first array-ordered outgoing flow.
            await EditorInteractions.SelectNodeAsync(page, 2);
            await EditorInteractions.SelectTypeAsync(page, "task");
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("task");
            await Assertions.Expect(
                page.Locator("#edges .edge-path-layer .edge[data-flow='103']")).ToHaveCountAsync(0);
            await Assertions.Expect(
                page.Locator("#edges .edge-path-layer .edge[data-flow='102']")).ToHaveCountAsync(1);

            // Ctrl+Z restores the whole graph; Ctrl+Y re-applies the conversion.
            await page.Keyboard.PressAsync("Control+z");
            await Assertions.Expect(
                page.Locator("#edges .edge-path-layer .edge[data-flow='103']")).ToHaveCountAsync(1);

            // With the redo entry present, a rejected edit must consume no
            // history and must not lose the redo: the end guard alert fires
            // (node 2 still has outgoing flows), the selector is restored, and
            // Ctrl+Y re-applies the queued conversion.
            scenario.ExpectDialogOnce("alert", EndGuardAlert, accept: false);
            await EditorInteractions.SelectTypeAsync(page, "endEvent");
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("userTask");
            await page.Keyboard.PressAsync("Control+y");
            await Assertions.Expect(
                page.Locator("#edges .edge-path-layer .edge[data-flow='103']")).ToHaveCountAsync(0);
            await page.Keyboard.PressAsync("Control+z");
            await Assertions.Expect(
                page.Locator("#edges .edge-path-layer .edge[data-flow='103']")).ToHaveCountAsync(1);
            await page.Keyboard.PressAsync("Control+y");
            await Assertions.Expect(
                page.Locator("#edges .edge-path-layer .edge[data-flow='103']")).ToHaveCountAsync(0);

            // Boundary cleanup: converting the service task removes its error
            // boundary and the boundary's incident flow; undo/redo round-trips.
            await EditorInteractions.SelectNodeAsync(page, 7);
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("serviceTask");
            await Assertions.Expect(page.Locator("#nodes .node[data-id='8']")).ToBeVisibleAsync();
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "boundary-conversion-before.png")
            });
            await EditorInteractions.SelectTypeAsync(page, "userTask");
            await Assertions.Expect(
                page.Locator("#nodes .node[data-id='8']")).ToHaveCountAsync(0);
            await Assertions.Expect(
                page.Locator("#edges .edge-path-layer .edge[data-flow='801']")).ToHaveCountAsync(0);
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("userTask");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "boundary-conversion-after.png")
            });
            await page.Keyboard.PressAsync("Control+z");
            await Assertions.Expect(
                page.Locator("#nodes .node[data-id='8']")).ToHaveCountAsync(1);
            await page.Keyboard.PressAsync("Control+y");
            await Assertions.Expect(
                page.Locator("#nodes .node[data-id='8']")).ToHaveCountAsync(0);

            // The boundary node's disabled Type field stays read-only before
            // the destructive conversion; after it, the boundary is gone.
            await page.Keyboard.PressAsync("Control+z");
            await Assertions.Expect(
                page.Locator("#nodes .node[data-id='8']")).ToHaveCountAsync(1);
            await EditorInteractions.SelectNodeAsync(page, 8);
            var boundaryTypeInput = EditorInteractions.TypeDisabledValue(page);
            await Assertions.Expect(boundaryTypeInput).ToBeDisabledAsync();
            await Assertions.Expect(boundaryTypeInput).ToHaveValueAsync("Error Boundary Event");

            // Save the pruned, boundary-free result and reload it in a fresh page.
            await page.Keyboard.PressAsync("Control+y");
            await Assertions.Expect(
                page.Locator("#nodes .node[data-id='8']")).ToHaveCountAsync(0);
            var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(
                page, scenario, scenario.ArtifactDirectory);
            var nodes = saved.RootElement.GetProperty("flowNodes");
            Assert.Equal(
                "task",
                nodes.EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 2)
                    .GetProperty("type").GetString());
            Assert.Equal(
                "userTask",
                nodes.EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 7)
                    .GetProperty("type").GetString());
            Assert.DoesNotContain(nodes.EnumerateArray(), node => node.GetProperty("id").GetInt32() == 8);
            var flows = saved.RootElement.GetProperty("sequenceFlows");
            Assert.DoesNotContain(flows.EnumerateArray(), flow =>
                flow.GetProperty("id").GetInt32() is 103 or 801);

            var freshPage = await scenario.Context.NewPageAsync();
            try
            {
                await freshPage.GotoAsync($"{stack.EditorBaseAddress}/",
                    new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                await freshPage.WaitForSelectorAsync("#svg");
                await EditorInteractions.LoadSavedFileAsync(freshPage, scenario.LastSavedFile!);
                await Assertions.Expect(
                    freshPage.Locator("#nodes .node[data-id='8']")).ToHaveCountAsync(0);
                await Assertions.Expect(
                    freshPage.Locator("#edges .edge-path-layer .edge[data-flow='103']")).ToHaveCountAsync(0);
                await Assertions.Expect(
                    freshPage.Locator("#edges .edge-path-layer .edge[data-flow='801']")).ToHaveCountAsync(0);
                await Assertions.Expect(
                    freshPage.Locator("#nodes .node[data-id='2'] > rect")).ToBeVisibleAsync();
            }
            finally
            {
                await freshPage.CloseAsync();
            }
        });
    }

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E8_StartConversionSettingsAndDefaults(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await CreateEditorScenarioAsync(
            $"e8-start-conversion-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("start-and-settings-conversion", async () =>
        {
            await OpenEditorAsync(scenario);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath(TypeTransitionFixture));

            // Message start → ordinary start materializes the typed variables.
            await EditorInteractions.SelectNodeAsync(page, 9);
            await Assertions.Expect(page.Locator("#inspector")).ToContainTextAsync("Message (start)");
            await EditorInteractions.SelectTypeAsync(page, "startEvent");
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("startEvent");
            await Assertions.Expect(page.Locator("#inspector")).ToContainTextAsync("Variables");

            // Back to a message start: the message configuration is rebuilt
            // with defaults, so the required settings must be completed again.
            await EditorInteractions.SelectTypeAsync(page, "messageStartEvent");
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("messageStartEvent");
            var inspectorField = (string label) => page.Locator("#inspector .field")
                .Filter(new LocatorFilterOptions { HasText = label }).First.Locator("input").First;
            await Assertions.Expect(inspectorField("Client id")).ToHaveValueAsync(string.Empty);
            await inspectorField("Client id").FillAsync("svc-orders");
            await inspectorField("Client secret").FillAsync("svc-secret-123");
            await inspectorField("Header name").FillAsync("X-Token");
            await inspectorField("Header value").FillAsync("tok-123");
            var pathFields = page.Locator("#inspector .inspector-card .field")
                .Filter(new LocatorFilterOptions { HasText = "Body path" });
            // The optional no-default mapping is omitted by the reverse
            // conversion; only the required mapping remains and needs its path.
            await Assertions.Expect(pathFields).ToHaveCountAsync(1);
            await pathFields.Nth(0).Locator("input").FillAsync("amount");
            // Entering a timer type seeds the PT1H default in the inspector.
            await EditorInteractions.SelectNodeAsync(page, 5);
            await EditorInteractions.SelectTypeAsync(page, "intermediateTimerCatchEvent");
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("intermediateTimerCatchEvent");
            await Assertions.Expect(
                page.Locator("#inspector [data-duration-amount]")).ToHaveValueAsync("1");
            await Assertions.Expect(
                page.Locator("#inspector [data-duration-unit]")).ToHaveValueAsync("hours");

            // A conditional catch seeds a blank condition; complete it before saving.
            await EditorInteractions.SelectNodeAsync(page, 4);
            await EditorInteractions.SelectTypeAsync(page, "intermediateConditionalCatchEvent");
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("intermediateConditionalCatchEvent");
            var conditionInput = page.Locator("#inspector textarea").First;
            await Assertions.Expect(conditionInput).ToHaveValueAsync(string.Empty);
            await conditionInput.FillAsync("amount > 10");

            // Converting the default start to a task leaves the message start
            // as the only entry; the saved default becomes null.
            await EditorInteractions.SelectNodeAsync(page, 1);
            await EditorInteractions.SelectTypeAsync(page, "task");
            await Assertions.Expect(EditorInteractions.TypeSelect(page)).ToHaveValueAsync("task");

            var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(
                page, scenario, scenario.ArtifactDirectory);
            var savedRoot = saved.RootElement;
            Assert.Equal(JsonValueKind.Null, savedRoot.GetProperty("initialEventId").ValueKind);
            var nodes = savedRoot.GetProperty("flowNodes");
            var node9 = nodes.EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 9);
            Assert.Equal("messageStartEvent", node9.GetProperty("type").GetString());
            Assert.Equal(
                "svc-orders",
                node9.GetProperty("message").GetProperty("clientId").GetString());
            var mappings = node9.GetProperty("message").GetProperty("outputMappings").EnumerateArray().ToArray();
            Assert.Single(mappings);
            Assert.Equal(("requestedAmount", "amount", true), (
                mappings[0].GetProperty("variable").GetString(),
                mappings[0].GetProperty("path").GetString(),
                mappings[0].GetProperty("required").GetBoolean()));
            var node5 = nodes.EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 5);
            Assert.Equal("intermediateTimerCatchEvent", node5.GetProperty("type").GetString());
            Assert.Equal(
                "PT1H",
                node5.GetProperty("timer").GetProperty("timeDuration").GetString());
            var node4 = nodes.EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 4);
            Assert.Equal("intermediateConditionalCatchEvent", node4.GetProperty("type").GetString());
            Assert.Equal(
                "amount > 10",
                node4.GetProperty("conditional").GetProperty("condition").GetString());
            Assert.Equal(
                "task",
                nodes.EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 1)
                    .GetProperty("type").GetString());
        });
    }

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E9_VariableFormFieldsEditSaveAndReload(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await CreateEditorScenarioAsync(
            $"e9-variable-form-fields-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("variable-form-fields", async () =>
        {
            await OpenEditorAsync(scenario);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath("editor-form-fields.json"));
            await EditorInteractions.SelectNodeAsync(page, 1);

            // The Form section starts closed for a variable without form fields.
            var card = page.Locator("#inspector .inspector-card")
                .Filter(new LocatorFilterOptions { Has = page.Locator("details.variable-form") }).First;
            var form = card.Locator("details.variable-form");
            await Assertions.Expect(form).Not.ToHaveAttributeAsync("open", string.Empty);
            await form.Locator("summary").ClickAsync();
            await FormInput(form, "Label").FillAsync("  Amount (EUR)  ");
            await FormInput(form, "Help text").FillAsync("Whole euros only.\nAsk finance for larger amounts.");

            // Options appear for a single number; text that converts becomes a number.
            await FormField(card, "Data type").Locator("select").SelectOptionAsync("number");
            await form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "+ Add option" }).ClickAsync();
            await form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "+ Add option" }).ClickAsync();
            await FormInput(form, "Option 1 value").FillAsync("100");
            await FormInput(form, "Option 1 label (optional)").FillAsync("Small");
            await FormInput(form, "Option 2 value").FillAsync("250.5");

            // A value a JavaScript number would change is kept as typed and blocks the save.
            await form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "+ Add option" }).ClickAsync();
            await FormInput(form, "Option 3 value").FillAsync("9007199254740993");
            await page.Locator("#fileMenuSummary").ClickAsync();
            await page.Locator("#saveBtn").ClickAsync();
            var errors = await EditorInteractions.WaitForValidationDialogAsync(page);
            Assert.Contains(
                "Variable 'amount' option #3 value on flow node #1 must have at most 15 significant digits and 28 decimal places.",
                errors,
                StringComparison.Ordinal);
            await EditorInteractions.CloseValidationDialogWithEscapeAsync(page);
            await EditorInteractions.SelectNodeAsync(page, 2);
            await EditorInteractions.SelectNodeAsync(page, 1);
            await Assertions.Expect(FormInput(form, "Option 3 value")).ToHaveValueAsync("9007199254740993");
            await form.Locator(".variable-option").Nth(2)
                .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Remove option" }).ClickAsync();
            await Assertions.Expect(form.Locator(".variable-option")).ToHaveCountAsync(2);

            // Bring the expanded section just below the inspector's sticky title and
            // capture the panel, so neither covers the section's heading.
            await form.EvaluateAsync(
                """
                element => {
                  const scroller = element.closest("#inspector");
                  const title = scroller.querySelector(":scope > h2:first-child");
                  const top = scroller.getBoundingClientRect().top;
                  const covered = title ? title.getBoundingClientRect().bottom - top : 0;
                  scroller.scrollTop += element.getBoundingClientRect().top - top - covered - 8;
                }
                """);
            await page.Locator("#inspectorPanel").ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "variable-form-fields.png"),
            });

            var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(
                page, scenario, scenario.ArtifactDirectory);
            var variable = saved.RootElement.GetProperty("flowNodes")
                .EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 1)
                .GetProperty("variables")[0];
            Assert.Equal("number", variable.GetProperty("dataType").GetString());
            Assert.Equal("Amount (EUR)", variable.GetProperty("label").GetString());
            Assert.Equal("Whole euros only.\nAsk finance for larger amounts.", variable.GetProperty("helpText").GetString());
            var options = variable.GetProperty("options");
            Assert.Equal(2, options.GetArrayLength());
            Assert.Equal("100", options[0].GetProperty("value").GetRawText());
            Assert.Equal("Small", options[0].GetProperty("label").GetString());
            Assert.Equal("250.5", options[1].GetProperty("value").GetRawText());
            Assert.False(options[1].TryGetProperty("label", out _));

            // A fresh page reloads the fields into an open Form section.
            var freshPage = await scenario.Context.NewPageAsync();
            try
            {
                await freshPage.GotoAsync($"{stack.EditorBaseAddress}/",
                    new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                await freshPage.WaitForSelectorAsync("#svg");
                await EditorInteractions.LoadSavedFileAsync(freshPage, scenario.LastSavedFile!);
                await EditorInteractions.PinInspectorOpenAsync(freshPage);
                await EditorInteractions.SelectNodeAsync(freshPage, 1);
                var reloaded = freshPage.Locator("#inspector details.variable-form").First;
                await Assertions.Expect(reloaded).ToHaveAttributeAsync("open", string.Empty);
                await Assertions.Expect(FormInput(reloaded, "Label")).ToHaveValueAsync("Amount (EUR)");
                await Assertions.Expect(FormInput(reloaded, "Option 2 value")).ToHaveValueAsync("250.5");
            }
            finally
            {
                await freshPage.CloseAsync();
            }
        });
    }

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E10_UserTaskDueDateAndPriorityEditValidateSaveAndReload(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await CreateEditorScenarioAsync(
            $"e10-due-date-priority-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("due-date-priority", async () =>
        {
            await OpenEditorAsync(scenario);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath("editor-basic.json"));
            await EditorInteractions.SelectNodeAsync(page, 2);

            // A quoted due date that is not a duration or date-time is reported at once.
            var dueDate = page.Locator("#user-task-dueDate-2");
            var priority = page.Locator("#user-task-priority-2");
            await dueDate.FillAsync("'P2X'");
            await priority.FillAsync("75");
            await Assertions.Expect(dueDate).ToHaveAttributeAsync("aria-invalid", "true");
            await Assertions.Expect(page.Locator("#user-task-dueDate-2-status")).ToHaveTextAsync(
                "dueDate 'P2X' is not an ISO-8601 duration such as P2D or a date-time with an offset such as 2030-01-31T17:00:00Z.");
            await Assertions.Expect(priority).ToHaveAttributeAsync("aria-invalid", "false");

            // Saving is blocked with the same message.
            await page.Locator("#fileMenuSummary").ClickAsync();
            await page.Locator("#saveBtn").ClickAsync();
            var errors = await EditorInteractions.WaitForValidationDialogAsync(page);
            Assert.Contains(
                "dueDate 'P2X' is not an ISO-8601 duration such as P2D or a date-time with an offset such as 2030-01-31T17:00:00Z.",
                errors,
                StringComparison.Ordinal);
            await EditorInteractions.CloseValidationDialogWithEscapeAsync(page);

            // A calendar-year duration is evidently meant as a fixed value, so it
            // is reported too instead of being read as a variable name.
            await dueDate.FillAsync("P1Y");
            await Assertions.Expect(dueDate).ToHaveAttributeAsync("aria-invalid", "true");
            await Assertions.Expect(page.Locator("#user-task-dueDate-2-status")).ToHaveTextAsync(
                "dueDate 'P1Y' is not an ISO-8601 duration such as P2D or a date-time with an offset such as 2030-01-31T17:00:00Z.");

            await dueDate.FillAsync("  P3D  ");
            await Assertions.Expect(dueDate).ToHaveAttributeAsync("aria-invalid", "false");
            await Assertions.Expect(page.Locator("#user-task-dueDate-2-status")).ToHaveTextAsync(string.Empty);
            await page.Locator("#inspectorPanel").ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "due-date-priority.png"),
            });

            var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(
                page, scenario, scenario.ArtifactDirectory);
            var task = saved.RootElement.GetProperty("flowNodes")
                .EnumerateArray().First(node => node.GetProperty("id").GetInt32() == 2);
            Assert.Equal("P3D", task.GetProperty("dueDate").GetString());
            Assert.Equal("75", task.GetProperty("priority").GetString());
            Assert.All(
                saved.RootElement.GetProperty("flowNodes").EnumerateArray()
                    .Where(node => node.GetProperty("id").GetInt32() != 2),
                node => Assert.False(node.TryGetProperty("dueDate", out _)));

            // A fresh page reloads both values into the inspector.
            var freshPage = await scenario.Context.NewPageAsync();
            try
            {
                await freshPage.GotoAsync($"{stack.EditorBaseAddress}/",
                    new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                await freshPage.WaitForSelectorAsync("#svg");
                await EditorInteractions.LoadSavedFileAsync(freshPage, scenario.LastSavedFile!);
                await EditorInteractions.PinInspectorOpenAsync(freshPage);
                await EditorInteractions.SelectNodeAsync(freshPage, 2);
                await Assertions.Expect(freshPage.Locator("#user-task-dueDate-2")).ToHaveValueAsync("P3D");
                await Assertions.Expect(freshPage.Locator("#user-task-priority-2")).ToHaveValueAsync("75");
            }
            finally
            {
                await freshPage.CloseAsync();
            }
        });
    }

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E11_BusinessCalendarFieldsValidateSaveAndReload(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await CreateEditorScenarioAsync(
            $"e11-business-calendar-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("business-calendar", async () =>
        {
            const string weeksProblem =
                "dueDate 'P1W' cannot be counted in a business calendar; use whole working days and working time, such as P5D or P1DT4H.";
            const string nameProblem =
                "calendar 'bad name' is not a valid business calendar name; use 1 to 100 letters, digits, '.', '_', or '-', starting with a letter or digit.";
            await OpenEditorAsync(scenario);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath("editor-business-calendar.json"));

            // The due-date calendar's status follows the due date as well as its own name.
            await EditorInteractions.SelectNodeAsync(page, 2);
            var dueDate = page.Locator("#user-task-dueDate-2");
            var dueCalendar = page.Locator("#user-task-dueDateCalendar-2");
            await dueCalendar.FillAsync("examples.weekdays");
            await Assertions.Expect(dueCalendar).ToHaveAttributeAsync("aria-invalid", "false");
            await dueDate.FillAsync("P1W");
            await Assertions.Expect(dueCalendar).ToHaveAttributeAsync("aria-invalid", "true");
            await Assertions.Expect(page.Locator("#user-task-dueDateCalendar-2-status")).ToHaveTextAsync(weeksProblem);
            await dueDate.FillAsync("P2D");
            await Assertions.Expect(dueCalendar).ToHaveAttributeAsync("aria-invalid", "false");
            await Assertions.Expect(page.Locator("#user-task-dueDateCalendar-2-status")).ToHaveTextAsync(string.Empty);

            // A timer's calendar is shown for a duration and checked as it is typed.
            await EditorInteractions.SelectNodeAsync(page, 3);
            var timerCalendar = page.Locator("#timer-calendar-3");
            await timerCalendar.FillAsync("bad name");
            await Assertions.Expect(timerCalendar).ToHaveAttributeAsync("aria-invalid", "true");
            await Assertions.Expect(page.Locator("#timer-calendar-3-status")).ToHaveTextAsync(nameProblem);

            // Saving is blocked with the same message.
            await page.Locator("#fileMenuSummary").ClickAsync();
            await page.Locator("#saveBtn").ClickAsync();
            var errors = await EditorInteractions.WaitForValidationDialogAsync(page);
            Assert.Contains("Timer event #3 " + nameProblem, errors, StringComparison.Ordinal);
            await EditorInteractions.CloseValidationDialogWithEscapeAsync(page);

            await timerCalendar.FillAsync("  examples.weekdays ");
            await Assertions.Expect(timerCalendar).ToHaveAttributeAsync("aria-invalid", "false");
            await page.Locator("#inspectorPanel").ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "business-calendar.png"),
            });

            var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(
                page, scenario, scenario.ArtifactDirectory);
            var nodes = saved.RootElement.GetProperty("flowNodes").EnumerateArray().ToArray();
            var task = nodes.First(node => node.GetProperty("id").GetInt32() == 2);
            Assert.Equal("P2D", task.GetProperty("dueDate").GetString());
            Assert.Equal("examples.weekdays", task.GetProperty("dueDateCalendar").GetString());
            var timer = nodes.First(node => node.GetProperty("id").GetInt32() == 3).GetProperty("timer");
            Assert.Equal("P3D", timer.GetProperty("timeDuration").GetString());
            Assert.Equal("examples.weekdays", timer.GetProperty("calendar").GetString());

            // A fresh page reloads both names into the inspector.
            var freshPage = await scenario.Context.NewPageAsync();
            try
            {
                await freshPage.GotoAsync($"{stack.EditorBaseAddress}/",
                    new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                await freshPage.WaitForSelectorAsync("#svg");
                await EditorInteractions.LoadSavedFileAsync(freshPage, scenario.LastSavedFile!);
                await EditorInteractions.PinInspectorOpenAsync(freshPage);
                await EditorInteractions.SelectNodeAsync(freshPage, 2);
                await Assertions.Expect(freshPage.Locator("#user-task-dueDateCalendar-2")).ToHaveValueAsync("examples.weekdays");
                await EditorInteractions.SelectNodeAsync(freshPage, 3);
                await Assertions.Expect(freshPage.Locator("#timer-calendar-3")).ToHaveValueAsync("examples.weekdays");
            }
            finally
            {
                await freshPage.CloseAsync();
            }
        });
    }

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E12_ExpressionTimerScheduleValidateUndoSaveAndReload(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await CreateEditorScenarioAsync(
            $"e12-expression-timer-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("expression-timer", async () =>
        {
            const string templateProblem =
                "timeDuration 'Due ${days}D' inserts a value into text with ${...}, which timer schedules do not do; " +
                "write one NCalc expression, optionally wrapped in a single ${...}.";
            const string contextProblem =
                "timer.timeDuration references runtime context 'sys.now'; timer expressions can read only declared stored instance variables.";
            const string wrapped = "${if(urgent, 'PT4H', 'P2D')}";
            await OpenEditorAsync(scenario);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(
                page, EditorInteractions.FixturePath("editor-business-calendar.json"));

            // The boundary's fixed duration opens in Fixed value mode; Expression keeps its text.
            await EditorInteractions.SelectNodeAsync(page, 3);
            var inspector = page.Locator("#inspector");
            var schedule = FormField(inspector, "Schedule").Locator("select");
            var valueMode = FormField(inspector, "Value").Locator("select");
            await Assertions.Expect(schedule).ToHaveValueAsync("timeDuration");
            await Assertions.Expect(valueMode).ToHaveValueAsync("fixed");
            await valueMode.SelectOptionAsync("expression");
            var expression = page.Locator("#timer-expression-3");
            var status = page.Locator("#timer-expression-3-status");
            await Assertions.Expect(expression).ToHaveValueAsync("P3D");
            await Assertions.Expect(expression).ToHaveAttributeAsync("aria-invalid", "false");

            // Text with an inserted value and a runtime context name are reported as they are typed.
            await expression.FillAsync("Due ${days}D");
            await Assertions.Expect(expression).ToHaveAttributeAsync("aria-invalid", "true");
            await Assertions.Expect(status).ToHaveTextAsync(templateProblem);
            await expression.FillAsync("if([sys.now] > reviewBy, 'PT1H', 'P2D')");
            await Assertions.Expect(status).ToHaveTextAsync(contextProblem);

            // Saving is blocked with the same message, named for the boundary.
            await page.Locator("#fileMenuSummary").ClickAsync();
            await page.Locator("#saveBtn").ClickAsync();
            var errors = await EditorInteractions.WaitForValidationDialogAsync(page);
            Assert.Contains("Timer boundary event #3 " + contextProblem, errors, StringComparison.Ordinal);
            await EditorInteractions.CloseValidationDialogWithEscapeAsync(page);

            // A wrapped expression is accepted, and its duration can count working time.
            await expression.FillAsync(wrapped);
            await Assertions.Expect(expression).ToHaveAttributeAsync("aria-invalid", "false");
            await Assertions.Expect(status).ToHaveTextAsync(string.Empty);
            var calendar = page.Locator("#timer-calendar-3");
            await calendar.FillAsync("examples.weekdays");
            await Assertions.Expect(calendar).ToHaveAttributeAsync("aria-invalid", "false");
            await Assertions.Expect(inspector).ToContainTextAsync(
                "This standalone editor cannot check NCalc syntax or variable names");
            await page.Locator("#inspectorPanel").ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "expression-timer.png"),
            });

            // The fixed controls say the text is an expression rather than a malformed duration.
            await valueMode.SelectOptionAsync("fixed");
            await Assertions.Expect(page.Locator("#inspector details[data-advanced-iso='true'] p.duration-preview"))
                .ToHaveTextAsync("This text is an expression, not a fixed value. Choose Expression above to edit and check it as one.");
            await valueMode.SelectOptionAsync("expression");
            await Assertions.Expect(expression).ToHaveValueAsync(wrapped);

            // A schedule change is one history entry; undo and redo keep the expression text.
            await schedule.SelectOptionAsync("timeDate");
            await Assertions.Expect(page.Locator("#timer-expression-3")).ToHaveCountAsync(0);
            await page.Keyboard.PressAsync("Control+z");
            await Assertions.Expect(schedule).ToHaveValueAsync("timeDuration");
            await Assertions.Expect(valueMode).ToHaveValueAsync("expression");
            await Assertions.Expect(expression).ToHaveValueAsync(wrapped);
            await Assertions.Expect(calendar).ToHaveValueAsync("examples.weekdays");
            await page.Keyboard.PressAsync("Control+y");
            await Assertions.Expect(schedule).ToHaveValueAsync("timeDate");
            await page.Keyboard.PressAsync("Control+z");
            await Assertions.Expect(expression).ToHaveValueAsync(wrapped);

            var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(
                page, scenario, scenario.ArtifactDirectory);
            var timer = saved.RootElement.GetProperty("flowNodes").EnumerateArray()
                .First(node => node.GetProperty("id").GetInt32() == 3).GetProperty("timer");
            Assert.Equal(wrapped, timer.GetProperty("timeDuration").GetString());
            Assert.True(!timer.TryGetProperty("timeDate", out var timeDate) || timeDate.ValueKind == JsonValueKind.Null);
            Assert.Equal("examples.weekdays", timer.GetProperty("calendar").GetString());

            // A fresh page infers Expression mode from the saved text.
            var freshPage = await scenario.Context.NewPageAsync();
            try
            {
                await freshPage.GotoAsync($"{stack.EditorBaseAddress}/",
                    new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                await freshPage.WaitForSelectorAsync("#svg");
                await EditorInteractions.LoadSavedFileAsync(freshPage, scenario.LastSavedFile!);
                await EditorInteractions.PinInspectorOpenAsync(freshPage);
                await EditorInteractions.SelectNodeAsync(freshPage, 3);
                var freshInspector = freshPage.Locator("#inspector");
                await Assertions.Expect(FormField(freshInspector, "Value").Locator("select")).ToHaveValueAsync("expression");
                await Assertions.Expect(freshPage.Locator("#timer-expression-3")).ToHaveValueAsync(wrapped);
                await Assertions.Expect(freshPage.Locator("#timer-calendar-3")).ToHaveValueAsync("examples.weekdays");
            }
            finally
            {
                await freshPage.CloseAsync();
            }
        });
    }

    // Inspector fields put a static label before their control.
    private static ILocator FormField(ILocator scope, string label) =>
        scope.Locator(".field").Filter(new LocatorFilterOptions
        {
            Has = scope.Page.Locator($"xpath=./label[normalize-space(.)='{label}']"),
        }).First;

    private static ILocator FormInput(ILocator scope, string label) =>
        FormField(scope, label).Locator("input, textarea").First;
}
