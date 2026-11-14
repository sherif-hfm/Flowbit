using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;
using static Flowbit.BrowserAcceptanceTests.AutomaticMi;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>
/// Hosted authoring of service, NCalc, JavaScript and call bodies in the real Flowbit.Ui
/// editor, whose capability comes from the API with the release switch open. Saves go
/// through the real API; read-back is authenticated HTTP. Assertions use labels and
/// behaviour, not the inspector's help wording.
/// </summary>
[Collection(AutomaticMultiInstanceAcceptanceCollection.Name)]
public sealed class AutomaticMultiInstanceAuthoringAcceptanceTests(AutomaticMultiInstanceAcceptanceFixture fixture)
{
    private BrowserStackFixture Stack => fixture.Stack;

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    public async Task TheHostedEditorAuthorsSavesAndReopensEveryBodyKindAndKeepsInvalidImports(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario($"auto-mi-hosted-authoring-{width}x{height}", width, height,
            scenarioTimeout: TimeSpan.FromMinutes(6));
        await scenario.RunAsync("hosted authoring", async () =>
        {
            await using var actor = await ApplyActorAsync(scenario, "mi-author", OperatorRoles);
            var childKey = Unique("auto-mi-authoring-child");
            await Stack.SetupClient.CreateAndPublishAsync(new WorkflowModel
            {
                Id = childKey, Name = childKey, InitialEventId = 1,
                FlowNodes =
                [
                    new()
                    {
                        Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent,
                        Variables = [new() { Id = 1, Name = "order", DataType = "json", Required = true }]
                    },
                    new() { Id = 2, Name = "End", Type = BpmnFlowNodeTypes.EndEvent },
                ],
                SequenceFlows = [new() { Id = 101, SourceRef = 1, TargetRef = 2 }]
            });
            var draft = await Stack.SetupClient.CreateUnpublishedAsync(AuthoringModel(Unique("auto-mi-authoring"), childKey));

            var page = await OpenEditorAsync(scenario, draft.Id, width, height);
            var editor = page.Locator("#workflow-editor-root");
            var inspector = editor.Locator("#inspector");
            var status = page.Locator("#editor-status");

            // Every body shows its section, mode, collection and a canvas marker.
            foreach (var (id, mode, collection) in new[]
                     { (2, "parallel", "items"), (5, "sequential", "amounts"), (6, "parallel", "lines"), (7, "parallel", "orders") })
            {
                await SelectBySearchAsync(page, id);
                await Assertions.Expect(inspector.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Automatic multi-instance" })).ToBeVisibleAsync();
                await Assertions.Expect(inspector.GetByLabel("Multi-instance", new LocatorGetByLabelOptions { Exact = true })).ToBeCheckedAsync();
                await Assertions.Expect(inspector.GetByLabel("Execution mode", new LocatorGetByLabelOptions { Exact = true })).ToHaveValueAsync(mode);
                await Assertions.Expect(inspector.GetByLabel("Collection variable", new LocatorGetByLabelOptions { Exact = true })).ToHaveValueAsync(collection);
                await Assertions.Expect(editor.Locator($"#nodes .node[data-id='{id}'] .mi-marker")).ToHaveCountAsync(1);
            }
            await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "authoring-call-body.png") });

            // Undo and redo a concurrency edit on the service body.
            await SelectBySearchAsync(page, 2);
            var concurrency = inspector.GetByLabel("Maximum concurrency (optional)", new LocatorGetByLabelOptions { Exact = true });
            await Assertions.Expect(concurrency).ToHaveValueAsync("3");
            await concurrency.FillAsync("2");
            await concurrency.PressAsync("Tab");
            await Assertions.Expect(status).ToHaveTextAsync("Unsaved changes");
            await editor.Locator("#editMenuSummary").ClickAsync();
            await editor.Locator("#undoBtn").ClickAsync();
            await Assertions.Expect(concurrency).ToHaveValueAsync("3");
            await Assertions.Expect(status).ToHaveTextAsync("Saved");
            await editor.Locator("#editMenuSummary").ClickAsync();
            await editor.Locator("#redoBtn").ClickAsync();
            await Assertions.Expect(concurrency).ToHaveValueAsync("2");

            // A sequential call body accepts only concurrency 1; the live status says so.
            await SelectBySearchAsync(page, 7);
            await inspector.GetByLabel("Execution mode", new LocatorGetByLabelOptions { Exact = true }).SelectOptionAsync("sequential");
            var bodyStatus = inspector.Locator("button:text-is('+ Add local output') + .expression-validation");
            await Assertions.Expect(bodyStatus).Not.ToHaveTextAsync("");
            await inspector.GetByLabel("Maximum concurrency (optional)", new LocatorGetByLabelOptions { Exact = true }).FillAsync("1");
            await Assertions.Expect(bodyStatus).ToHaveTextAsync("");
            await Assertions.Expect(editor.Locator("#nodes .node[data-id='7'] .mi-marker title")).ToHaveTextAsync("Sequential multi-instance");

            // A non-interrupting conditional boundary on the call body, connected on the canvas.
            await inspector.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "+ Add conditional boundary event" }).ClickAsync();
            await editor.Locator("#fitViewBtn").ClickAsync();
            await EditorInteractions.ClickCenterAsync(page, await EditorInteractions.GetNodeBoxAsync(page, 3));
            var boundaryId = int.Parse(await editor.Locator("#nodes .node.conditionalBoundaryEvent").GetAttributeAsync("data-id") ?? "0",
                System.Globalization.CultureInfo.InvariantCulture);
            await SelectBySearchAsync(page, boundaryId);
            await inspector.GetByLabel("Condition (NCalc)", new LocatorGetByLabelOptions { Exact = true }).FillAsync("stop == true");
            await inspector.GetByLabel("Interrupt attached activity", new LocatorGetByLabelOptions { Exact = true }).UncheckAsync();
            var ring = editor.Locator($"#nodes .node[data-id='{boundaryId}']");
            // Finding: the toggle redraws only the inspector, so the canvas keeps the solid
            // ring until the next full render (see the skipped test below). Record it, then
            // reselect, which redraws the canvas.
            var classAfterToggle = await ring.GetAttributeAsync("class");
            await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "boundary-ring-after-toggle.png") });
            await SelectBySearchAsync(page, 7);
            await Assertions.Expect(inspector).ToContainTextAsync("non-interrupting");
            await SelectBySearchAsync(page, boundaryId);
            await Assertions.Expect(ring).ToHaveClassAsync(new Regex("non-interrupting"));
            await WriteEvidenceAsync(scenario, "boundary-ring", new { boundaryId, classAfterToggle, classAfterRedraw = await ring.GetAttributeAsync("class") });
            await CaptureEditorAsync(scenario, "authoring-boundary");

            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#editor-notice")).ToContainTextAsync("Saved unpublished version 2");
            var savedId = SavedId(page);
            var saved = (await Stack.SetupClient.GetWorkflowAsync(savedId)).Definition;
            Assert.Equal(2, Node(saved, 2).MultiInstance!.MaxConcurrency);
            Assert.Equal(("sequential", 1), (Node(saved, 7).MultiInstance!.Mode, Node(saved, 7).MultiInstance!.MaxConcurrency));
            var savedBoundary = Node(saved, boundaryId);
            Assert.Equal((7, false, "stop == true"), (savedBoundary.AttachedToRef!.Value, savedBoundary.CancelActivity!.Value, savedBoundary.Conditional!.Condition));
            Assert.Contains(saved.SequenceFlows, flow => flow.SourceRef == boundaryId && flow.TargetRef == 3);

            // Reopen the saved version: the values come back from the server.
            page = await OpenEditorAsync(scenario, savedId, width, height);
            editor = page.Locator("#workflow-editor-root");
            inspector = editor.Locator("#inspector");
            await SelectBySearchAsync(page, 7);
            await Assertions.Expect(inspector.GetByLabel("Execution mode", new LocatorGetByLabelOptions { Exact = true })).ToHaveValueAsync("sequential");
            await Assertions.Expect(inspector.GetByLabel("Maximum concurrency (optional)", new LocatorGetByLabelOptions { Exact = true })).ToHaveValueAsync("1");
            await Assertions.Expect(inspector).ToContainTextAsync("non-interrupting");
            await SelectBySearchAsync(page, 2);
            await Assertions.Expect(inspector.GetByLabel("Maximum concurrency (optional)", new LocatorGetByLabelOptions { Exact = true })).ToHaveValueAsync("2");
            await CaptureEditorAsync(scenario, "authoring-reopened");

            // An import with invalid values keeps them for save validation to report.
            var invalid = JsonNode.Parse(JsonSerializer.Serialize(saved, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
            NodeJson(invalid, 2)["multiInstance"] = true;
            NodeJson(invalid, 6)["multiInstance"]!["mode"] = "everywhere";
            var importPath = Path.Combine(scenario.ArtifactDirectory, "invalid-bodies.json");
            await File.WriteAllTextAsync(importPath, invalid.ToJsonString(), ScenarioCancellation.Token);
            var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
            {
                await editor.Locator("#fileMenuSummary").ClickAsync();
                await editor.Locator("#loadBtn").ClickAsync();
            });
            await chooser.SetFilesAsync(importPath);
            await SelectBySearchAsync(page, 2);
            var removeInvalid = inspector.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Remove the invalid value" });
            await Assertions.Expect(removeInvalid).ToBeVisibleAsync();
            await Assertions.Expect(inspector.GetByLabel("Multi-instance", new LocatorGetByLabelOptions { Exact = true })).Not.ToBeCheckedAsync();
            await SelectBySearchAsync(page, 6);
            await Assertions.Expect(inspector.Locator("button:text-is('+ Add local output') + .expression-validation")).Not.ToHaveTextAsync("");
            await page.Locator("#save-workflow-version").ClickAsync();
            var errors = await EditorInteractions.WaitForValidationDialogAsync(page);
            Assert.Contains("#2", errors);
            Assert.Contains("#6", errors);
            await CaptureEditorAsync(scenario, "authoring-invalid-import");
            await EditorInteractions.CloseValidationDialogWithEscapeAsync(page);

            // Repair both, save, and the server keeps exactly the repair.
            await inspector.GetByLabel("Execution mode", new LocatorGetByLabelOptions { Exact = true }).SelectOptionAsync("parallel");
            await SelectBySearchAsync(page, 2);
            await inspector.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Remove the invalid value" }).ClickAsync();
            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#editor-notice")).ToContainTextAsync("Saved unpublished version 3");
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Saved");
            var repaired = (await Stack.SetupClient.GetWorkflowAsync(SavedId(page))).Definition;
            Assert.Null(Node(repaired, 2).MultiInstance);
            Assert.Equal("parallel", Node(repaired, 6).MultiInstance!.Mode);
            Assert.Equal("sequential", Node(repaired, 7).MultiInstance!.Mode);
        });
    }

    // Found 2026-11-14: the toggle redrew only the inspector, so the canvas kept the old ring.
    [Fact]
    public async Task TogglingInterruptionRedrawsTheConditionalBoundaryRingAtOnce()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-boundary-ring", 1440, 900);
        await scenario.RunAsync("boundary ring", async () =>
        {
            await using var actor = await ApplyActorAsync(scenario, "mi-author", OperatorRoles);
            var draft = await Stack.SetupClient.CreateUnpublishedAsync(AuthoringModel(Unique("auto-mi-ring"), Unique("auto-mi-ring-child")));
            var page = await OpenEditorAsync(scenario, draft.Id, 1440, 900);
            var editor = page.Locator("#workflow-editor-root");
            await SelectBySearchAsync(page, 7);
            await editor.Locator("#inspector").GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "+ Add conditional boundary event" }).ClickAsync();
            await EditorInteractions.ClickCenterAsync(page, await EditorInteractions.GetNodeBoxAsync(page, 3));
            var ring = editor.Locator("#nodes .node.conditionalBoundaryEvent");
            await SelectBySearchAsync(page, int.Parse(await ring.GetAttributeAsync("data-id") ?? "0", System.Globalization.CultureInfo.InvariantCulture));
            await editor.Locator("#inspector").GetByLabel("Interrupt attached activity", new LocatorGetByLabelOptions { Exact = true }).UncheckAsync();
            await Assertions.Expect(ring).ToHaveClassAsync(new Regex("non-interrupting"));
            // Leave the unsaved draft through the editor's own guard so cleanup can navigate.
            scenario.ExpectDialogOnce("confirm", "Leave the editor? Unsaved changes will be lost.", accept: true);
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to Workflows" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/workflows$"));
        });
    }

    private static async Task<IPage> OpenEditorAsync(BrowserScenario scenario, long workflowId, int width, int height)
    {
        // The canvas needs a wide viewport to pin the inspector; narrow it afterwards.
        await scenario.Page.SetViewportSizeAsync(1440, 900);
        var page = await scenario.OpenUiAsync($"workflows/{workflowId}/edit");
        await page.WaitForSelectorAsync("[data-editor-ready='true']");
        await EditorInteractions.PinInspectorOpenAsync(page);
        await page.SetViewportSizeAsync(width, height);
        await page.Locator("#workflow-editor-root #fitViewBtn").ClickAsync();
        return page;
    }

    private static async Task CaptureEditorAsync(BrowserScenario scenario, string name)
    {
        var page = scenario.Page;
        var original = page.ViewportSize!;
        try
        {
            foreach (var (width, height) in Viewports)
            {
                await page.SetViewportSizeAsync(width, height);
                await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
                await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, $"{name}-{width}x{height}.png") });
            }
        }
        finally { await page.SetViewportSizeAsync(original.Width, original.Height); }
    }

    private static long SavedId(IPage page) =>
        long.Parse(Regex.Match(page.Url, @"/workflows/(\d+)/edit").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    private static FlowNodeModel Node(WorkflowModel model, int id) => model.FlowNodes.Single(node => node.Id == id);

    private static JsonNode NodeJson(JsonNode model, int id) =>
        model["flowNodes"]!.AsArray().Single(node => node!["id"]!.GetValue<int>() == id)!;

    /// <summary>A service, an NCalc, a JavaScript and a call body in a row.</summary>
    private static WorkflowModel AuthoringModel(string key, string childKey)
    {
        var service = ServiceBody();
        var ncalc = new FlowNodeModel
        {
            Id = 5, Name = "Scale amounts", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
            ScriptFormat = ScriptFormats.NCalc, X = 440, Y = 140,
            MultiInstance = Multi("sequential", "amounts", "scaled", 1, Local("scaledAmount")),
            Assignments = [new() { Variable = "scaledAmount", Expression = "[mi.item] * factor" }]
        };
        var javascript = new FlowNodeModel
        {
            Id = 6, Name = "Price lines", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
            ScriptFormat = ScriptFormats.JavaScript, X = 680, Y = 140,
            MultiInstance = Multi("parallel", "lines", "priced", 4, Local("lineTotal")),
            Script = "const line = execution.getVariable('mi.item');\nexecution.setVariable('lineTotal', line.quantity * line.price);"
        };
        var call = new FlowNodeModel
        {
            Id = 7, Name = "Check orders", Type = BpmnFlowNodeTypes.CallActivity, X = 920, Y = 140,
            MultiInstance = Multi("parallel", "orders", "orderResults", 4),
            CallActivity = new()
            {
                WorkflowKey = childKey,
                InputMappings = [new() { SourceVariable = "mi.item", Variable = "order", DataType = "json", Required = true }]
            }
        };
        var model = BodyWorkflow(key, service, ServiceVariables("authoring", 1, 2));
        model.Variables.AddRange(
        [
            Process(4, "amounts", "number", new[] { 10, 20 }, isArray: true),
            Process(5, "factor", "number", 2),
            Process(6, "scaled", "json", Array.Empty<object>()),
            Process(7, "lines", "json", new[] { new { quantity = 1, price = 2 } }, isArray: true),
            Process(8, "priced", "json", Array.Empty<object>()),
            Process(9, "orders", "json", new[] { new { id = "a" } }, isArray: true),
            Process(10, "orderResults", "json", Array.Empty<object>()),
            Process(11, "stop", "boolean", false),
        ]);
        // Replace the review task with the other three bodies: 2 → 5 → 6 → 7 → end.
        model.FlowNodes.RemoveAll(node => node.Id == 4);
        model.FlowNodes.Single(node => node.Id == 3).X = 1180;
        model.FlowNodes.AddRange([ncalc, javascript, call]);
        model.SequenceFlows =
        [
            new() { Id = 101, SourceRef = 1, TargetRef = 2 },
            new() { Id = 201, SourceRef = 2, TargetRef = 5 },
            new() { Id = 501, SourceRef = 5, TargetRef = 6 },
            new() { Id = 601, SourceRef = 6, TargetRef = 7 },
            new() { Id = 701, SourceRef = 7, TargetRef = 3 },
        ];
        return model;
    }
}
