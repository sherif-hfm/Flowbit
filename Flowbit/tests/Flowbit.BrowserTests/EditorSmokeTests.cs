using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// Automated editor matrix (E1-E5) against the real copied editor served over
/// localhost. E1-E5 run at 1440x900 and 1024x768; every scenario keeps fresh
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
            var historyBefore = await page.EvaluateAsync<int>("undoHistory.length");
            await page.Keyboard.PressAsync("Control+z");
            var afterNativeUndo = await nameInput.InputValueAsync();
            Assert.NotEqual("rvhpan", afterNativeUndo);
            Assert.Equal(
                historyBefore,
                await page.EvaluateAsync<int>("undoHistory.length"));

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
}
