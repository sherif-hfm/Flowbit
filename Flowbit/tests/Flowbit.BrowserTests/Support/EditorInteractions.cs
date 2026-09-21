using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Microsoft.Playwright;

namespace Flowbit.BrowserTests.Support;

/// <summary>
/// Small shared helpers for driving the real editor: load via the File menu and
/// the OS file chooser, save/download through the fallback alert path, SVG
/// geometry reads, and real pointer/keyboard gestures. All interactions go
/// through Playwright input events — no application-global JS mutations.
/// </summary>
public static class EditorInteractions
{
    public static async Task AssertEndpointTouchesNodeAsync(IPage page, int flowId, int nodeId, bool atStart)
    {
        var distance = await page.EvaluateAsync<double>("""
            ({flowId, nodeId, atStart}) => {
                const path = document.querySelector(`#edges .edge-path-layer [data-flow='${flowId}'] path`);
                const rect = document.querySelector(`#nodes [data-id='${nodeId}'] > rect`).getBoundingClientRect();
                const length = path.getTotalLength();
                const local = path.getPointAtLength(atStart ? 0 : length);
                if (!atStart) {
                    // The editor deliberately trims five diagram units from
                    // the target endpoint to leave room for the arrow marker.
                    const previous = path.getPointAtLength(length - 0.1);
                    const dx = local.x - previous.x, dy = local.y - previous.y;
                    const tangent = Math.hypot(dx, dy);
                    local.x += 5 * dx / tangent;
                    local.y += 5 * dy / tangent;
                }
                const point = new DOMPoint(local.x, local.y).matrixTransform(path.getScreenCTM());
                const dx = Math.max(rect.left - point.x, 0, point.x - rect.right);
                const dy = Math.max(rect.top - point.y, 0, point.y - rect.bottom);
                if (dx || dy) return Math.hypot(dx, dy);
                return Math.min(point.x - rect.left, rect.right - point.x, point.y - rect.top, rect.bottom - point.y);
            }
            """, new { flowId, nodeId, atStart });
        Xunit.Assert.InRange(distance, 0, 2);
    }

    /// <summary>Fixture JSON paths copied next to the test assembly.</summary>
    public static string FixturePath(string fileName)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        if (!File.Exists(candidate))
        {
            throw new InvalidOperationException($"Editor fixture not found: {candidate}");
        }
        return candidate;
    }

    /// <summary>Loads a workflow through File → Load JSON and the file chooser.</summary>
    public static async Task LoadWorkflowAsync(IPage page, string fixturePath)
    {
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await page.Locator("#fileMenuSummary").ClickAsync();
            await page.Locator("#loadBtn").ClickAsync();
        });
        await chooser.SetFilesAsync(fixturePath);
        using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath));
        await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync(
            expected.RootElement.GetProperty("name").GetString()!);
        await page.WaitForSelectorAsync("#nodes .node", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Attached,
        });
    }

    /// <summary>
    /// Saves via File → Save JSON with the download fallback. Requires
    /// ExpectDialogStartingWith(IdentityScreen.FallbackAlertPrefix) registered
    /// and the native picker blocked by the context init script. Returns the
    /// parsed saved JSON (persisted under the scenario artifact directory).
    /// </summary>
    public static async Task<JsonDocument> SaveAndCaptureDownloadAsync(
        IPage page,
        BrowserScenario scenario,
        string artifactDirectory)
    {
        // The scenario context must have blocked the native picker via its init
        // script; otherwise save() would open the native dialog instead of the
        // expected fallback alert + download.
        if (await page.EvaluateAsync<bool?>("typeof window.showSaveFilePicker === 'function'") == true)
        {
            throw new InvalidOperationException(
                "The native file-picker override did not apply; cannot capture the fallback download.");
        }
        try
        {
            var download = await page.RunAndWaitForDownloadAsync(async () =>
            {
                await page.Locator("#fileMenuSummary").ClickAsync();
                await page.Locator("#saveBtn").ClickAsync();
            });
            var targetPath = Path.Combine(
                artifactDirectory,
                SanitizeFileName(download.SuggestedFilename ?? "workflow.json"));
            await download.SaveAsAsync(targetPath);
            await using var stream = File.OpenRead(targetPath);
            var json = await JsonDocument.ParseAsync(stream);
            scenario.LastSavedFile = targetPath;
            return json;
        }
        catch (TimeoutException failure)
        {
            var validation = await page.EvaluateAsync<JsonElement>(
                "() => window.validateModelForSave ? validateModelForSave(model) : ['validateModelForSave unavailable']");
            throw new TimeoutException(
                $"No download appeared after Save. Validation errors: {JsonSerializer.Serialize(validation)}", failure);
        }
    }

    /// <summary>Loads a previously saved file into the (fresh) page.</summary>
    public static async Task LoadSavedFileAsync(IPage page, string savedPath) =>
        await LoadWorkflowAsync(page, savedPath);

    /// <summary>
    /// Selects the move/select tool through the real toolbar control. The
    /// authoring palette is a hover dock, so the handle is hovered first (a
    /// real pointer gesture); after the tool click the palette is closed so it
    /// cannot overlay subsequent canvas gestures.
    /// </summary>
    public static async Task SelectMoveToolAsync(IPage page)
    {
        var handle = page.Locator("#authoringPaletteHandle");
        await handle.HoverAsync();
        await page.WaitForFunctionAsync(
            "document.getElementById('authoringPalette').classList.contains('is-open')");
        await page.Locator("#selectToolBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#svg")).ToHaveAttributeAsync("data-active-tool", "select");
        var palette = page.Locator("#authoringPalette");
        if (await page.EvaluateAsync<bool?>(
                "document.getElementById('authoringPalette').classList.contains('is-open')") == true)
        {
            await page.Keyboard.PressAsync("Escape");
        }
        await page.WaitForFunctionAsync(
            "!document.getElementById('authoringPalette').classList.contains('is-open')");
        // Park the pointer on empty canvas space away from docked overlays.
        var viewport = page.ViewportSize!;
        await page.Mouse.MoveAsync(viewport.Width / 2f, viewport.Height / 2f);
    }

    /// <summary>
    /// Pins the inspector dock open through its real pin control so inspector
    /// fields stay visible without a pointer hover (headless automation). The
    /// auto-hidden dock must be revealed by a real pointer hover first; the
    /// hover targets the dock's right-edge peek strip.
    /// </summary>
    public static async Task PinInspectorOpenAsync(IPage page)
    {
        var pin = page.Locator("#inspectorPin");
        if (await pin.GetAttributeAsync("aria-pressed") != "true")
        {
            var viewport = page.ViewportSize!;
            await page.Mouse.MoveAsync(viewport.Width / 2f, viewport.Height / 2f);
            var pointerX = viewport.Width / 2f;
            while (pointerX < viewport.Width - 3f)
            {
                pointerX = Math.Min(viewport.Width - 3f, pointerX + 20f);
                await page.Mouse.MoveAsync(pointerX, viewport.Height / 2f);
            }
            await page.WaitForFunctionAsync(
                "document.querySelector('main').classList.contains('inspector-revealed')");
            await pin.ClickAsync();
        }
        await Assertions.Expect(pin).ToHaveAttributeAsync("aria-pressed", "true");
    }

    /// <summary>Screen bounding box of an editor node's SVG group (throws when absent).</summary>
    public static async Task<LocatorBoundingBoxResult> GetNodeBoxAsync(IPage page, int nodeId)
    {
        var box = await page.Locator($"#nodes .node[data-id='{nodeId}']").BoundingBoxAsync();
        return box ?? throw new TimeoutException($"Node {nodeId} is not rendered on the canvas.");
    }

    /// <summary>Screen bounding box of an editor lane's SVG group (throws when absent).</summary>
    public static async Task<LocatorBoundingBoxResult> GetLaneBoxAsync(IPage page, int laneId)
    {
        var box = await page.Locator($"#lanes .lane[data-id='{laneId}']").BoundingBoxAsync();
        return box ?? throw new TimeoutException($"Lane {laneId} is not rendered on the canvas.");
    }

    /// <summary>Diagram-space (model) coordinates from a screen point.</summary>
    public static async Task<(double X, double Y)> ToDiagramPointAsync(
        IPage page, double screenX, double screenY) =>
        await page.EvaluateAsync<(double X, double Y)>(
            /* JavaScript: maps a client point through the svg CTM inverse */
            """
            ([x, y]) => {
              const svg = document.getElementById('svg');
              const pt = svg.createSVGPoint();
              pt.x = x; pt.y = y;
              const matrix = svg.getScreenCTM();
              if (!matrix) return [0, 0];
              const transformed = pt.matrixTransform(matrix.inverse());
              return [transformed.x, transformed.y];
            }
            """,
            new[] { screenX, screenY });

    /// <summary>Current diagram zoom factor (viewState.zoom).</summary>
    public static Task<double> GetZoomAsync(IPage page) =>
        page.EvaluateAsync<double>("viewState.zoom");

    /// <summary>Performs a real pointer drag with intermediate movement steps.</summary>
    public static async Task DragAsync(
        IPage page,
        float fromX, float fromY,
        float toX, float toY,
        int steps = 6)
    {
        await page.Mouse.MoveAsync(fromX, fromY);
        await page.Mouse.DownAsync();
        for (var step = 1; step <= steps; step++)
        {
            var fraction = (float)step / steps;
            await page.Mouse.MoveAsync(
                fromX + (toX - fromX) * fraction,
                fromY + (toY - fromY) * fraction);
        }
        await page.Mouse.UpAsync();
    }

    /// <summary>Clicks a point on the SVG canvas (used for node selection).</summary>
    public static async Task ClickCenterAsync(IPage page, LocatorBoundingBoxResult box)
    {
        await page.Mouse.ClickAsync(
            box.X + box.Width / 2,
            box.Y + box.Height / 2);
    }

    /// <summary>
    /// Types into the inspector "Name" field of the current selection and
    /// returns the input element locator.
    /// </summary>
    public static async Task<ILocator> FillInspectorNameAsync(IPage page, string value)
    {
        var nameInput = page.Locator("#inspector .field").Filter(new LocatorFilterOptions
        {
            HasText = "Name",
        }).First.Locator("input").First;
        await nameInput.FillAsync(value);
        return nameInput;
    }

    /// <summary>Reads the workflow name from the header input.</summary>
    public static async Task<string> ReadWorkflowNameAsync(IPage page) =>
        await page.Locator("#wfName").InputValueAsync();

    /// <summary>The inspector "Type" select for the currently selected node.</summary>
    public static ILocator TypeSelect(IPage page) =>
        page.Locator("#inspector .field").Filter(new LocatorFilterOptions
        {
            HasText = "Type",
        }).First.Locator("select").First;

    /// <summary>
    /// The read-only "Type" text input shown for boundary events (no select).
    /// </summary>
    public static ILocator TypeDisabledValue(IPage page) =>
        page.Locator("#inspector .field").Filter(new LocatorFilterOptions
        {
            HasText = "Type",
        }).First.Locator("input").First;

    /// <summary>
    /// Selects a node with a stationary pan-tool click on its center so the
    /// inspector shows it (the editor's pan mode inspects without moving).
    /// </summary>
    public static async Task SelectNodeAsync(IPage page, int nodeId)
    {
        var box = await GetNodeBoxAsync(page, nodeId);
        await ClickCenterAsync(page, box);
        await Assertions.Expect(page.Locator("#inspector")).ToContainTextAsync($"Node #{nodeId}");
    }

    /// <summary>
    /// Changes the selected node's Type through the real select control. Any
    /// guard dialog is answered by the scenario's registered dialog handlers.
    /// </summary>
    public static async Task SelectTypeAsync(IPage page, string value) =>
        await TypeSelect(page).SelectOptionAsync(value);

    public static async Task SetWorkflowNameAsync(IPage page, string value) =>
        await page.Locator("#wfName").FillAsync(value);

    /// <summary>Waits until the validation dialog is open and returns its error list text.</summary>
    public static async Task<string> WaitForValidationDialogAsync(IPage page)
    {
        await page.WaitForSelectorAsync("#validation-modal", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
        });
        return (await page.Locator("#validation-errors").InnerTextAsync()).Trim();
    }

    public static async Task CloseValidationDialogWithEscapeAsync(IPage page)
    {
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForSelectorAsync("#validation-modal", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Hidden,
        });
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
