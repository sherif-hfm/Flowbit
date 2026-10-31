using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

[Collection(BrowserCollection.Name)]
public sealed class HostedEditorSmokeTests(BrowserStackFixture stack)
{
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    public async Task H1_EditSaveAndGuardNavigation(int width, int height)
    {
        await using var scenario = await stack.CreateScenario($"h1-hosted-editor-{width}", width, height);
        await scenario.RunAsync("hosted-edit-save", async () =>
        {
            var original = await RuntimeSupport.PublishAsync(stack, "editor-basic.json");
            await RuntimeSupport.ApplyIdentityAsync(scenario, "editor-admin", ["admin"]);
            var page = await scenario.OpenUiAsync("workflows");
            await page.Locator(".accordion-button", new PageLocatorOptions { HasText = original.WorkflowKey }).ClickAsync();
            await page.Locator($"a[href='workflows/{original.Id}/edit']").ClickAsync();
            await ReadyAsync(page);
            await Assertions.Expect(page.Locator("iframe")).ToHaveCountAsync(0);
            var editor = page.Locator("#workflow-editor-root");
            var name = editor.Locator("#wfName");
            await Assertions.Expect(name).ToHaveValueAsync(original.Name);
            var key = editor.Locator("#inspector .field", new LocatorLocatorOptions { HasText = "Workflow ID" }).Locator("input");
            var frameBox = await page.Locator("#workflow-editor-root").BoundingBoxAsync();
            await page.Mouse.MoveAsync(frameBox!.X + frameBox.Width - 8, frameBox.Y + 220);
            await Assertions.Expect(key).ToBeDisabledAsync();

            await name.FillAsync("Hosted edited workflow");
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Unsaved changes");
            await Assertions.Expect(editor.Locator("#undoBtn")).ToBeEnabledAsync();
            await editor.Locator("#editMenuSummary").ClickAsync();
            await editor.Locator("#undoBtn").ClickAsync();
            await Assertions.Expect(name).ToHaveValueAsync(original.Name);
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Saved");
            await editor.Locator("#editMenuSummary").ClickAsync();
            await editor.Locator("#redoBtn").ClickAsync();
            await Assertions.Expect(name).ToHaveValueAsync("Hosted edited workflow");
            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#editor-notice")).ToContainTextAsync("Saved unpublished version 2");
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Saved");
            var savedId = long.Parse(Regex.Match(page.Url, @"/workflows/(\d+)/edit").Groups[1].Value);
            var saved = await stack.SetupClient.GetWorkflowAsync(savedId);
            Assert.False(saved.IsPublished);
            Assert.Equal(original.WorkflowKey, saved.WorkflowKey);
            Assert.Equal("Hosted edited workflow", saved.Name);
            Assert.Equal(original.Name, (await stack.SetupClient.GetWorkflowAsync(original.Id)).Name);
            Assert.Equal(4, saved.Definition.FlowNodes.Count);

            // Guard immediately after input, before the 500 ms history commit.
            await name.FillAsync("Keep unsaved name");
            scenario.ExpectDialogOnce("confirm", "Leave the editor? Unsaved changes will be lost.", accept: false);
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to Workflows", Exact = true }).Last.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"/workflows/{savedId}/edit$"));
            await Assertions.Expect(name).ToHaveValueAsync("Keep unsaved name");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "hosted-editor.png"), FullPage = true });
            scenario.ExpectDialogOnce("confirm", "Leave the editor? Unsaved changes will be lost.", accept: true);
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to Workflows", Exact = true }).Last.ClickAsync();
            await page.Locator($".accordion-button", new PageLocatorOptions { HasText = original.WorkflowKey }).ClickAsync();
            await page.Locator($"a[href='workflows/{savedId}/edit']").ClickAsync();
            await ReadyAsync(page);
            await Assertions.Expect(page.Locator("#workflow-editor-root").Locator("#wfName")).ToHaveValueAsync("Hosted edited workflow");
        });
    }

    [Fact]
    public async Task H2_ImportLargeDocumentAndKeepEditsOnServerRejection()
    {
        await using var scenario = await stack.CreateScenario("h2-hosted-large-and-errors");
        await scenario.RunAsync("hosted-large-errors", async () =>
        {
            await RuntimeSupport.ApplyIdentityAsync(scenario, "editor-admin", ["admin"]);
            var page = await scenario.OpenUiAsync("workflows/new");
            await ReadyAsync(page);
            await Assertions.Expect(page.Locator("iframe")).ToHaveCountAsync(0);
            var editor = page.Locator("#workflow-editor-root");
            var definition = JsonNode.Parse(await File.ReadAllTextAsync(EditorInteractions.FixturePath("editor-basic.json")))!.AsObject();
            definition["id"] = "hosted-large-" + Guid.NewGuid().ToString("N");
            definition["variables"]![0]!["defaultValue"] = new string('x', 70_000);
            var file = Path.Combine(scenario.ArtifactDirectory, "large-workflow.json");
            await File.WriteAllTextAsync(file, definition.ToJsonString());
            scenario.ExpectDialogOnce("confirm", "Replace this workflow? Unsaved changes will be lost.", accept: true);
            await ImportAsync(page, file);
            var box = await page.Locator("#workflow-editor-root").BoundingBoxAsync();
            await page.Mouse.MoveAsync(box!.X + box.Width - 8, box.Y + 220);
            await editor.Locator("#inspectorPin").ClickAsync();
            var key = editor.Locator("#inspector .field", new LocatorLocatorOptions { HasText = "Workflow ID" }).Locator("input");
            var firstSavedKey = "hosted-edited-key-" + Guid.NewGuid().ToString("N");
            await key.FillAsync(firstSavedKey);
            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#editor-notice")).ToContainTextAsync("Saved unpublished version 1");
            var savedId = long.Parse(Regex.Match(page.Url, @"/workflows/(\d+)/edit").Groups[1].Value);
            var saved = await stack.SetupClient.GetWorkflowAsync(savedId);
            Assert.Equal(70_000, saved.Definition.Variables[0].DefaultValue!.Value.GetString()!.Length);
            Assert.Equal(firstSavedKey, saved.WorkflowKey);
            await editor.Locator("#editMenuSummary").ClickAsync();
            await editor.Locator("#undoBtn").ClickAsync();
            await Assertions.Expect(key).ToBeDisabledAsync();
            await Assertions.Expect(key).ToHaveValueAsync(firstSavedKey);
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Saved");

            await editor.Locator("#wfName").FillAsync("Retained after forbidden save");
            var identityPage = await scenario.Context.NewPageAsync();
            var identity = new IdentityScreen(identityPage, stack.UiBaseAddress);
            await identity.GenerateAndApplyIdentityAsync("editor-reader", ["Agent"]);
            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#editor-error")).ToContainTextAsync("Forbidden");
            await Assertions.Expect(editor.Locator("#wfName")).ToHaveValueAsync("Retained after forbidden save");
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Unsaved changes");
            await identity.GenerateAndApplyIdentityAsync("editor-admin", ["admin"]);
            await identityPage.CloseAsync();

            // Oversized local files stay editable/exportable, but cannot be saved to the API.
            definition["variables"]![0]!["defaultValue"] = new string('x', 2 * 1024 * 1024);
            await File.WriteAllTextAsync(file, definition.ToJsonString());
            scenario.ExpectDialogOnce("confirm", "Replace this workflow? Unsaved changes will be lost.", accept: true);
            await ImportAsync(page, file);
            await Assertions.Expect(page.Locator("#editor-version")).ToHaveTextAsync("New workflow");
            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#editor-error")).ToContainTextAsync("2 MiB limit");
            await Assertions.Expect(editor.Locator("#wfName")).ToHaveValueAsync("Browser editor basic workflow");
            scenario.ExpectDialogOnce("confirm", "Leave the editor? Unsaved changes will be lost.", accept: true);
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to Workflows", Exact = true }).Last.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/workflows$"));
        });
    }

    [Fact]
    public async Task H3_ScriptDraftValidationAndMissingVersion()
    {
        await using var scenario = await stack.CreateScenario("h3-hosted-script-draft");
        await scenario.RunAsync("hosted-script-draft", async () =>
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(EditorInteractions.FixturePath("editor-basic.json")))!.AsObject();
            json["id"] = "hosted-script-" + Guid.NewGuid().ToString("N");
            var script = json["flowNodes"]![2]!;
            script["type"] = "scriptTask";
            script["scriptFormat"] = "javascript";
            script["script"] = "// initial script";
            var model = json.Deserialize<WorkflowModel>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var workflow = await stack.SetupClient.CreateAndPublishAsync(model);
            await RuntimeSupport.ApplyIdentityAsync(scenario, "editor-admin", ["admin"]);
            var page = await scenario.OpenUiAsync($"workflows/{workflow.Id}/edit");
            await ReadyAsync(page);
            await Assertions.Expect(page.Locator("iframe")).ToHaveCountAsync(0);
            var editor = page.Locator("#workflow-editor-root");
            // Use the shared toolbar and SVG directly in the app workspace.
            await editor.Locator("#authoringPaletteHandle").ClickAsync();
            await editor.Locator("#selectToolBtn").ClickAsync();
            await editor.Locator("#nodes [data-id='3']").ClickAsync();
            var box = await page.Locator("#workflow-editor-root").BoundingBoxAsync();
            await page.Mouse.MoveAsync(box!.X + box.Width - 8, box.Y + 220);
            await editor.Locator("#inspectorPin").ClickAsync();
            await editor.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "✎ Edit in big space" }).ClickAsync();
            await editor.Locator("#js-editor-textarea").FillAsync("// unsaved script draft");
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Unsaved changes");
            // The modal covers the integrated toolbar. Keyboard navigation stays
            // in the dialog until the draft is saved or explicitly discarded.
            await editor.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save & Close" }).FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            Assert.True(await page.EvaluateAsync<bool>("document.querySelector('#js-editor-modal').contains(document.activeElement)"));
            scenario.ExpectDialogOnce("confirm", "Discard unsaved changes?", accept: false);
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(editor.Locator("#js-editor-textarea")).ToHaveValueAsync("// unsaved script draft");
            await editor.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save & Close" }).ClickAsync();
            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#editor-notice")).ToContainTextAsync("Saved unpublished version 2");
            var id = long.Parse(Regex.Match(page.Url, @"/workflows/(\d+)/edit").Groups[1].Value);
            Assert.Equal("// unsaved script draft", (await stack.SetupClient.GetWorkflowAsync(id)).Definition.FlowNodes[2].Script);

            await page.SetViewportSizeAsync(390, 844);
            await page.WaitForFunctionAsync("document.querySelector('#primary-navigation').getBoundingClientRect().right <= 0");
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 2"));
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "hosted-editor-narrow.png"), FullPage = true });
            await page.SetViewportSizeAsync(1440, 900);

            await scenario.OpenUiAsync("workflows/new");
            await ReadyAsync(page);
            var freshEditor = page.Locator("#workflow-editor-root");
            var freshKey = freshEditor.Locator("#inspector .field", new LocatorLocatorOptions { HasText = "Workflow ID" }).Locator("input");
            var initialKey = await freshKey.InputValueAsync();
            Assert.StartsWith("workflow-", initialKey);
            await freshEditor.Locator("#wfName").FillAsync("Undo fresh workflow edit");
            await freshEditor.Locator("#editMenuSummary").ClickAsync();
            await freshEditor.Locator("#undoBtn").ClickAsync();
            await Assertions.Expect(freshEditor.Locator("#wfName")).ToHaveValueAsync("New Workflow");
            await Assertions.Expect(freshKey).ToHaveValueAsync(initialKey);
            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#workflow-editor-root").Locator("#validation-modal")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#editor-error")).ToContainTextAsync("Workflow cannot be saved");
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(page.Locator("#validation-modal")).ToBeHiddenAsync();
            scenario.ExpectDialogOnce("confirm", "Leave the editor? Unsaved changes will be lost.", accept: true);
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to Workflows", Exact = true }).Last.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/workflows$"));
            // The URL changes first. The editor's unsaved-changes guard lasts until it is removed.
            await Assertions.Expect(page.Locator("#workflow-editor-root")).ToHaveCountAsync(0);
            await scenario.OpenUiAsync("workflows/9223372036854775807/edit");
            await Assertions.Expect(page.Locator("#editor-error")).ToContainTextAsync("was not found");
            await Assertions.Expect(page.Locator("#save-workflow-version")).ToBeDisabledAsync();
        });
    }

    [Fact]
    public async Task H4_RootLauncherAndStandaloneFileOpening()
    {
        await using var scenario = await stack.CreateScenario("h4-standalone-file-opening", blockNativeFilePicker: true);
        scenario.ExpectDialogStartingWith(IdentityScreen.FallbackAlertPrefix);
        await scenario.RunAsync("standalone-file-opening", async () =>
        {
            var page = scenario.Page;
            await page.GotoAsync(new Uri(Path.Combine(stack.RepositoryRoot, "flowbit-editor.html")).AbsoluteUri);
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync("New Workflow");
            Assert.Contains("/Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.html", page.Url);
            await EditorInteractions.LoadWorkflowAsync(page, EditorInteractions.FixturePath("editor-basic.json"));
            await page.Locator("#wfName").FillAsync("Standalone shared assets");
            using var exported = await EditorInteractions.SaveAndCaptureDownloadAsync(page, scenario, scenario.ArtifactDirectory);
            Assert.Equal("Standalone shared assets", exported.RootElement.GetProperty("name").GetString());
            Assert.Equal(4, exported.RootElement.GetProperty("flowNodes").GetArrayLength());
        });
    }

    [Fact]
    public async Task H5_InlinePointerGeometryFocusAndRemount()
    {
        await using var scenario = await stack.CreateScenario("h5-inline-geometry-and-lifecycle");
        await scenario.RunAsync("inline-geometry-and-lifecycle", async () =>
        {
            var original = await RuntimeSupport.PublishAsync(stack, "editor-basic.json");
            await RuntimeSupport.ApplyIdentityAsync(scenario, "editor-admin", ["admin"]);
            var page = await scenario.OpenUiAsync($"workflows/{original.Id}/edit");
            await ReadyAsync(page);
            await EditorInteractions.SelectMoveToolAsync(page);
            var before = await EditorInteractions.GetNodeBoxAsync(page, 2);
            await EditorInteractions.DragAsync(page, before.X + before.Width / 2, before.Y + before.Height / 2,
                before.X + before.Width / 2 + 60, before.Y + before.Height / 2 + 30);
            var after = await EditorInteractions.GetNodeBoxAsync(page, 2);
            Assert.InRange(after.X - before.X, 58, 62);
            Assert.InRange(after.Y - before.Y, 28, 32);
            await EditorInteractions.AssertEndpointTouchesNodeAsync(page, 101, 2, atStart: false);

            // Focus in the app shell must not delete the selected diagram node.
            await page.Locator("#primary-navigation a[href='workflows/new']").FocusAsync();
            await page.Keyboard.PressAsync("Delete");
            await page.Keyboard.PressAsync("h");
            await Assertions.Expect(page.Locator("#nodes .node")).ToHaveCountAsync(4);
            await Assertions.Expect(page.Locator("#svg")).ToHaveAttributeAsync("data-active-tool", "select");
            await EditorInteractions.PinInspectorOpenAsync(page);
            var panel = page.Locator("#inspector");
            var panelBefore = await panel.BoundingBoxAsync();
            var resizer = await page.Locator(".resizer").BoundingBoxAsync();
            await EditorInteractions.DragAsync(page, resizer!.X + resizer.Width / 2, resizer.Y + 200,
                resizer.X + resizer.Width / 2 - 70, resizer.Y + 200);
            var panelAfter = await panel.BoundingBoxAsync();
            Assert.InRange(panelAfter!.Width - panelBefore!.Width, 65, 80);
            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#editor-notice")).ToContainTextAsync("Saved unpublished version 2");
            var savedId = long.Parse(Regex.Match(page.Url, @"/workflows/(\d+)/edit").Groups[1].Value);
            var saved = await stack.SetupClient.GetWorkflowAsync(savedId);
            Assert.Equal(260, saved.Definition.FlowNodes[1].X);

            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to Workflows" }).ClickAsync();
            await Assertions.Expect(page.Locator("#workflow-editor-root")).ToHaveCountAsync(0);
            await page.Locator("#primary-navigation a[href='workflows/new']").ClickAsync();
            await ReadyAsync(page);
            await Assertions.Expect(page.Locator(".flowbit-editor")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#nodes .node")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#svg")).ToHaveAttributeAsync("data-active-tool", "pan");
            await page.Locator("#wfName").FillAsync("Fresh after navigation");
            await page.Locator("#editMenuSummary").ClickAsync();
            await page.Locator("#undoBtn").ClickAsync();
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync("New Workflow");
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth && document.documentElement.scrollHeight <= innerHeight"));
            scenario.ExpectDialogOnce("confirm", "Leave the editor? Unsaved changes will be lost.", accept: true);
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to Workflows" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/workflows$"));
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    public async Task H11_ExpressionTimerServerRejectionKeepsDraftThenSaves(int width, int height)
    {
        await using var scenario = await stack.CreateScenario($"h11-hosted-expression-timer-{width}", width, height);
        await scenario.RunAsync("hosted-expression-timer", async () =>
        {
            const string typo = "if(urgnet, 'PT4H', 'P2D')";
            const string corrected = "if(urgent, 'PT4H', 'P2D')";
            var json = JsonNode.Parse(await File.ReadAllTextAsync(
                EditorInteractions.FixturePath("editor-business-calendar.json")))!.AsObject();
            json["id"] = "hosted-expression-timer-" + Guid.NewGuid().ToString("N");
            json["flowNodes"]![0]!["variables"] = new JsonArray(new JsonObject
            {
                ["id"] = 101,
                ["name"] = "urgent",
                ["dataType"] = "boolean",
                ["isArray"] = false,
                ["required"] = true,
            });
            var model = json.Deserialize<WorkflowModel>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            // Unpublished: this Worker-free stack refuses to publish timer definitions.
            var workflow = await stack.SetupClient.CreateUnpublishedAsync(model);
            await RuntimeSupport.ApplyIdentityAsync(scenario, "editor-admin", ["admin"]);
            var page = await scenario.OpenUiAsync($"workflows/{workflow.Id}/edit");
            await ReadyAsync(page);
            var editor = page.Locator("#workflow-editor-root");
            await EditorInteractions.PinInspectorOpenAsync(page);
            await EditorInteractions.SelectNodeAsync(page, 3);
            await InspectorSelect(editor, "Value").SelectOptionAsync("expression");
            var expression = editor.Locator("#timer-expression-3");

            // The editor cannot check variable names; it says so, and the server catches the typo.
            await expression.FillAsync(typo);
            await Assertions.Expect(expression).ToHaveAttributeAsync("aria-invalid", "false");
            var expressionField = editor.Locator("#inspector .field")
                .Filter(new LocatorFilterOptions { Has = page.Locator("#timer-expression-3") });
            var expressionHelp = expressionField.Locator(".inspector-help");
            await expressionField.Locator(".inspector-help-toggle").ClickAsync();
            await Assertions.Expect(expressionHelp).ToBeVisibleAsync();
            await Assertions.Expect(expressionHelp).ToContainTextAsync(
                "Saving checks the expression's syntax and the variables it names");
            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#editor-error")).ToContainTextAsync(
                "Timer boundary event #3 timer.timeDuration references undeclared stored variable 'urgnet'.");
            await Assertions.Expect(expression).ToHaveValueAsync(typo);
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Unsaved changes");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "hosted-expression-rejected.png"),
            });

            await expression.FillAsync(corrected);
            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#editor-notice")).ToContainTextAsync("Saved unpublished version 2");
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Saved");
            var savedId = long.Parse(Regex.Match(page.Url, @"/workflows/(\d+)/edit").Groups[1].Value);
            var saved = await stack.SetupClient.GetWorkflowAsync(savedId);
            Assert.Equal(corrected, saved.Definition.FlowNodes.Single(node => node.Id == 3).Timer!.TimeDuration);

            // The saved version reopens in Expression mode.
            await page.ReloadAsync();
            await ReadyAsync(page);
            await EditorInteractions.PinInspectorOpenAsync(page);
            await EditorInteractions.SelectNodeAsync(page, 3);
            await Assertions.Expect(InspectorSelect(editor, "Value")).ToHaveValueAsync("expression");
            await Assertions.Expect(expression).ToHaveValueAsync(corrected);

            // At phone width the field, its help, and the page stay within the viewport.
            await page.SetViewportSizeAsync(390, 844);
            await page.WaitForFunctionAsync("document.querySelector('#primary-navigation').getBoundingClientRect().right <= 0");
            await expression.ScrollIntoViewIfNeededAsync();
            var box = await expression.BoundingBoxAsync();
            Assert.NotNull(box);
            Assert.InRange(box!.X, 0, 390);
            Assert.InRange(box.X + box.Width, 0, 390);
            await expressionField.Locator(".inspector-help-toggle").ClickAsync();
            await Assertions.Expect(expressionHelp).ToBeVisibleAsync();
            var helpBox = await expressionHelp.BoundingBoxAsync();
            Assert.NotNull(helpBox);
            Assert.InRange(helpBox!.X, 0, 390);
            Assert.InRange(helpBox.X + helpBox.Width, 0, 390);
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 2"));
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "hosted-expression-narrow.png"),
            });
            await page.SetViewportSizeAsync(width, height);
        });
    }

    // The page also loads Bootstrap, whose .badge would stop the summary wrapping.
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task H12_BoundaryListFitsTheInspectorAndKeepsKeyboardFocus(int width, int height)
    {
        // The canvas needs a wide viewport to reach a node, so narrow it once the inspector is pinned.
        await using var scenario = await stack.CreateScenario($"h12-hosted-boundary-list-{width}", 1440, 900);
        await scenario.RunAsync("hosted-boundary-list", async () =>
        {
            var workflow = await stack.SetupClient.CreateAndPublishAsync(
                Path.Combine(stack.RepositoryRoot, "examples", "service-tasks", "04-rest-http-status-routing.json"));
            await RuntimeSupport.ApplyIdentityAsync(scenario, "editor-admin", ["admin"]);
            var page = await scenario.OpenUiAsync($"workflows/{workflow.Id}/edit");
            await ReadyAsync(page);
            var editor = page.Locator("#workflow-editor-root");
            await Assertions.Expect(editor.Locator("#wfName")).ToHaveValueAsync(workflow.Name);
            await EditorInteractions.PinInspectorOpenAsync(page);
            await EditorInteractions.SelectMoveToolAsync(page);
            var inspector = editor.Locator("#inspector");
            await editor.Locator("g.node[data-id='2']").ClickAsync(new LocatorClickOptions { Force = true });

            // Other inspector cards use the same class, so they lose the pill too.
            var flowBadge = inspector.Locator(".inspector-card .badge").Filter(new LocatorFilterOptions { HasText = "flow #" }).First;
            await Assertions.Expect(flowBadge).ToHaveCSSAsync("white-space", "normal");
            await Assertions.Expect(flowBadge).ToHaveCSSAsync("padding-left", "0px");
            await Assertions.Expect(flowBadge).ToHaveCSSAsync("display", "block");

            var heading = inspector.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Error boundaries" });
            await Assertions.Expect(heading).ToBeVisibleAsync();
            await page.SetViewportSizeAsync(width, height);
            await heading.ScrollIntoViewIfNeededAsync();

            var layout = await page.EvaluateAsync<JsonElement>("""
                () => {
                  const inspector = document.querySelector('#inspector');
                  const rows = [...inspector.querySelectorAll('.error-boundary-row')].map(row => {
                    const badge = row.querySelector('.badge');
                    const text = document.createRange();
                    text.selectNodeContents(badge);
                    return {
                      textRight: text.getBoundingClientRect().right,
                      removeLeft: row.querySelector('button:last-child').getBoundingClientRect().left,
                      whiteSpace: getComputedStyle(badge).whiteSpace,
                      textAlign: getComputedStyle(badge).textAlign
                    };
                  });
                  return { overflow: inspector.scrollWidth - inspector.clientWidth, pageOverflow: document.documentElement.scrollWidth - innerWidth, rows };
                }
                """);
            var rows = layout.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(4, rows.Length);
            Assert.All(rows, row =>
            {
                Assert.True(row.GetProperty("textRight").GetDouble() <= row.GetProperty("removeLeft").GetDouble(), row.GetRawText());
                Assert.Equal("normal", row.GetProperty("whiteSpace").GetString());
                Assert.Equal("left", row.GetProperty("textAlign").GetString());
            });
            Assert.True(layout.GetProperty("overflow").GetDouble() <= 0, layout.GetRawText());
            Assert.True(layout.GetProperty("pageOverflow").GetDouble() <= 2, layout.GetRawText());
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "hosted-boundary-list.png"),
            });

            // Focus stays inside the editor, which is what lets its shortcuts apply here.
            ILocator Remove(int id) => inspector.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Remove error boundary event #{id}" });
            await Remove(5).FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(Remove(6)).ToBeFocusedAsync();
            await Assertions.Expect(inspector.Locator(".error-boundary-row")).ToHaveCountAsync(3);
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Unsaved changes");
            await page.Keyboard.PressAsync("Control+z");
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Saved");

            await page.SetViewportSizeAsync(1440, 900);
            await editor.Locator("g.node[data-id='2']").ClickAsync(new LocatorClickOptions { Force = true });
            await page.SetViewportSizeAsync(width, height);
            await inspector.Locator(".error-boundary-open").First.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(inspector.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Node #3" })).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(inspector.Locator(".field").Filter(new LocatorFilterOptions
            {
                Has = page.Locator("xpath=./label[normalize-space(.)='Name']"),
            }).First.Locator("input")).ToBeFocusedAsync();
        });
    }

    [Fact]
    public async Task H13_ImportPageNamesWhatIsWrongWithARejectedMatcher()
    {
        await using var scenario = await stack.CreateScenario("h13-import-rejected-matcher", 1440, 900);
        await scenario.RunAsync("import-rejected-matcher", async () =>
        {
            await RuntimeSupport.ApplyIdentityAsync(scenario, "editor-admin", ["admin"]);
            var page = await scenario.OpenUiAsync("workflows");
            await Assertions.Expect(page.Locator(".app-shell")).ToHaveAttributeAsync("data-interactive", "true");

            string Document(string matcher)
            {
                var document = JsonNode.Parse(File.ReadAllText(EditorInteractions.FixturePath("editor-error-boundaries.json")))!;
                document["id"] = $"browser-import-{Guid.NewGuid():N}";
                var nodes = document["flowNodes"]!.AsArray();
                // Ends 4 and 5 have no incoming flow in this fixture, so they would fail first.
                foreach (var unused in nodes.Where(node => node!["id"]!.GetValue<int>() is 4 or 5).ToArray())
                {
                    nodes.Remove(unused);
                }
                nodes.Add(JsonNode.Parse(
                    $$"""{ "id": 7, "name": "Boundary", "type": "errorBoundaryEvent", "laneId": 1, "x": 0, "y": 0, "attachedToRef": 2, "errorMatch": {{matcher}} }"""));
                document["sequenceFlows"]!.AsArray().Add(JsonNode.Parse("""{ "id": 701, "name": "", "sourceRef": 7, "targetRef": 6 }"""));
                return document.ToJsonString();
            }

            var alert = page.Locator(".alert.alert-danger");
            var index = 0;
            foreach (var (matcher, expected) in new (string Matcher, string? Expected)[]
            {
                ("""{ "httpStatusCodes": ["409"] }""", "The value has the wrong JSON type. Path: $.flowNodes[4].errorMatch.httpStatusCodes[0]"),
                ("""{ "httpStatusRangez": [{ "from": 400, "to": 499 }] }""", "Unknown property 'httpStatusRangez'. Path: $.flowNodes[4].errorMatch.httpStatusRangez"),
                ("""{ "httpStatusCodes": [null] }""", "The value has the wrong JSON type. Path: $.flowNodes[4].errorMatch.httpStatusCodes[0]"),
                ("""{ }""", "Error boundary event #7 on host node #2: errorMatch must list at least one HTTP status or range; remove errorMatch to catch every failure."),
                ("""{ "httpStatusCodes": [700] }""", "Error boundary event #7 on host node #2: errorMatch.httpStatusCodes[0] is 700; a status must be from 100 through 599."),
                ("""{ "httpStatusCodes": [503, 409] }""", null)
            })
            {
                var name = $"import-{index++}.json";
                var path = Path.Combine(scenario.ArtifactDirectory, name);
                await File.WriteAllTextAsync(path, Document(matcher));
                await page.Locator("#workflow-json-file").SetInputFilesAsync(path);
                await Assertions.Expect(page.Locator(".summary-item").Filter(new LocatorFilterOptions { HasText = name })).ToHaveCountAsync(1);
                var import = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = new Regex("^Import ") });
                await Assertions.Expect(import).ToBeEnabledAsync();
                await import.ClickAsync();
                if (expected is null)
                {
                    await Assertions.Expect(alert).ToHaveCountAsync(0);
                }
                else
                {
                    await Assertions.Expect(alert).ToHaveTextAsync(expected);
                }
            }
        });
    }

    // A large file reads slowly enough that a quick click lands before the read ends.
    [Fact]
    public async Task H14_ImportOnlySubmitsTheFileLastSelected()
    {
        await using var scenario = await stack.CreateScenario("h14-import-file-race", 1440, 900);
        await scenario.RunAsync("import-file-race", async () =>
        {
            await RuntimeSupport.ApplyIdentityAsync(scenario, "editor-admin", ["admin"]);
            var page = await scenario.OpenUiAsync("workflows");
            await Assertions.Expect(page.Locator(".app-shell")).ToHaveAttributeAsync("data-interactive", "true");
            var picker = page.Locator("#workflow-json-file");
            var import = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = new Regex("^Import ") });
            var summary = page.Locator(".summary-item");
            var run = Guid.NewGuid().ToString("N");

            async Task<(string Key, string Path)> WriteAsync(string label, int padding)
            {
                var document = JsonNode.Parse(File.ReadAllText(EditorInteractions.FixturePath("editor-basic.json")))!;
                var key = $"browser-import-{label}-{run}";
                document["id"] = key;
                document["name"] = key;
                var path = Path.Combine(scenario.ArtifactDirectory, $"{label}.json");
                await File.WriteAllTextAsync(path, document.ToJsonString() + new string(' ', padding));
                return (key, path);
            }

            async Task<string[]> ImportedKeysAsync() =>
                (await stack.SetupClient.ListWorkflowsAsync())
                    .Select(workflow => workflow.WorkflowKey)
                    .Where(key => key.EndsWith(run, StringComparison.Ordinal))
                    .ToArray();

            const int Large = 1_900_000;

            // A second selection takes the first file's contents off the page at once.
            var first = await WriteAsync("first", 0);
            await picker.SetInputFilesAsync(first.Path);
            await Assertions.Expect(summary.Filter(new LocatorFilterOptions { HasText = "first.json" })).ToHaveCountAsync(1);
            await Assertions.Expect(import).ToBeEnabledAsync();

            // Records whether Import stayed available for the first file during the read of the
            // second, and whether the page locked itself during the import.
            await page.EvaluateAsync("""
                firstKey => {
                    const section = document.querySelector('#import-workflow');
                    const picker = section.querySelector('#workflow-json-file');
                    const button = [...section.querySelectorAll('button')].find(item => item.textContent.trim().startsWith('Import '));
                    const watch = { firstImportable: false, pickerLocked: false };
                    picker.addEventListener('change', () => {
                        new MutationObserver(() => {
                            const preview = section.querySelector('pre');
                            if (!button.disabled && preview && preview.textContent.includes(firstKey)) watch.firstImportable = true;
                        }).observe(section, { subtree: true, childList: true, attributes: true });
                        new MutationObserver(() => {
                            if (picker.disabled) watch.pickerLocked = true;
                        }).observe(picker, { attributes: true, attributeFilter: ['disabled'] });
                    });
                    window.importWatch = watch;
                }
                """, first.Key);
            var second = await WriteAsync("second", Large);
            await picker.SetInputFilesAsync(second.Path);

            // The click waits for the button, so it cannot reach the old file.
            await import.ClickAsync();
            await Assertions.Expect(summary).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".alert.alert-danger")).ToHaveCountAsync(0);
            Assert.Equal(new[] { second.Key }, await ImportedKeysAsync());
            Assert.False(await page.EvaluateAsync<bool>("window.importWatch.firstImportable"));
            Assert.True(await page.EvaluateAsync<bool>("window.importWatch.pickerLocked"));

            // A slow read that a newer selection overtakes is ignored when it finishes.
            var slow = await WriteAsync("slow", Large);
            var quick = await WriteAsync("quick", 0);
            await picker.SetInputFilesAsync(slow.Path);
            await picker.SetInputFilesAsync(quick.Path);
            var quickSummary = summary.Filter(new LocatorFilterOptions { HasText = "quick.json" });
            await Assertions.Expect(quickSummary).ToHaveCountAsync(1);
            await page.WaitForTimeoutAsync(1500);
            await Assertions.Expect(quickSummary).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#import-workflow pre")).ToContainTextAsync(quick.Key);
            await import.ClickAsync();
            await Assertions.Expect(summary).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".alert.alert-danger")).ToHaveCountAsync(0);
            Assert.Equal(new[] { quick.Key, second.Key }, (await ImportedKeysAsync()).Order(StringComparer.Ordinal));
        });
    }

    private static ILocator InspectorSelect(ILocator editor, string label) =>
        editor.Locator("#inspector .field").Filter(new LocatorFilterOptions
        {
            Has = editor.Page.Locator($"xpath=./label[normalize-space(.)='{label}']"),
        }).First.Locator("select");

    private static Task ReadyAsync(IPage page) => page.WaitForSelectorAsync("[data-editor-ready='true']");

    private static async Task ImportAsync(IPage page, string path)
    {
        var editor = page.Locator("#workflow-editor-root");
        var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
        {
            await editor.Locator("#fileMenuSummary").ClickAsync();
            await editor.Locator("#loadBtn").ClickAsync();
        });
        await chooser.SetFilesAsync(path);
        await Assertions.Expect(editor.Locator("#wfName")).ToHaveValueAsync("Browser editor basic workflow");
    }
}
