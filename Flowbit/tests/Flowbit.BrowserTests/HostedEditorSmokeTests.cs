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
