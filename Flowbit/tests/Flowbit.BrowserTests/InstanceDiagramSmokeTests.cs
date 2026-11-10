using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// R8: the live instance diagram on instance detail, drawn by the shared
/// editor's read-only viewer. Covers markers for running, multi-instance and
/// finished instances, role-scoped node-execution links, mouse and keyboard
/// selection, and read-only pointer and keyboard input at desktop and phone
/// widths.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class InstanceDiagramSmokeTests(BrowserStackFixture stack)
{
    private static readonly string[] ReviewerRoles = ["Reviewer"];

    // admin satisfies the default NodeExecution.RequiredRole read setting.
    private static readonly string[] OperatorRoles = ["Reviewer", "admin"];

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task R8_LiveDiagramMarksPositionsLinksWorkAndStaysReadOnly(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r8-instance-diagram-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("instance-diagram", async () =>
        {
            // HTTP setup: two parallel user tasks, a partly completed
            // multi-instance task, and a completed instance.
            var gatewayWorkflow = await RuntimeSupport.PublishAsync(stack, "runtime-gateway.json");
            var parallel = await RuntimeSupport.StartInstanceAsync(stack, gatewayWorkflow.Id);
            var miWorkflow = await RuntimeSupport.PublishAsync(stack, "runtime-mi.json");
            var multiInstance = await RuntimeSupport.StartInstanceAsync(stack, miWorkflow.Id);
            var beta = await CreateActorClientAsync(scenario, "beta", ReviewerRoles);
            var betaTask = await beta.FindInboxTaskIdByItemIndexAsync(multiInstance.Id, 1);
            await beta.TakeUserTaskFlowAsync(betaTask, 201, new()
            {
                ["reviewComment"] = JsonSerializer.SerializeToElement("Reviewed by beta"),
            });
            var navigationWorkflow = await RuntimeSupport.PublishAsync(stack, "runtime-navigation.json");
            var finished = await RuntimeSupport.StartInstanceAsync(stack, navigationWorkflow.Id);
            await stack.SetupClient.TakeInstanceFlowAsync(finished.Id, 102);
            await stack.SetupClient.TakeInstanceFlowAsync(finished.Id, 103);
            Assert.Equal("completed", (await stack.SetupClient.GetInstanceAsync(finished.Id)).Status);

            await RuntimeSupport.ApplyIdentityAsync(scenario, "operator", OperatorRoles);
            var page = scenario.Page;
            var diagram = await OpenDiagramAsync(scenario, parallel.Id);
            var viewer = diagram.Locator(".flowbit-editor[data-mode='viewer']");
            var details = diagram.Locator(".instance-diagram-details");

            // Exactly the two parallel user tasks are marked, and no flow is highlighted.
            await Assertions.Expect(viewer.Locator(".node.instance-marked")).ToHaveCountAsync(2);
            await Assertions.Expect(viewer.Locator(".node.instance-active[data-id='3']")).ToHaveCountAsync(1);
            await Assertions.Expect(viewer.Locator(".node.instance-active[data-id='4']")).ToHaveCountAsync(1);
            await Assertions.Expect(viewer.Locator(".edge.trace-active, .edge.selected")).ToHaveCountAsync(0);
            // The viewer adds no authoring markup, such as the editor's own heading, to the page.
            await Assertions.Expect(viewer.Locator(".app-toolbar, .authoring-palette, .diagram-tools, .inspector-dock"))
                .ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("h1")).ToHaveCountAsync(1);
            await Assertions.Expect(viewer.Locator(".canvas-controls")).ToBeVisibleAsync();
            await Assertions.Expect(viewer.Locator(".connect-handle")).ToHaveCountAsync(0);

            // Read-only input: dragging a node pans the canvas without moving
            // the node, and Delete removes nothing.
            var reviewerA = viewer.Locator(".node[data-id='3']");
            var transform = await reviewerA.GetAttributeAsync("transform");
            var viewBox = await viewer.Locator("#svg").GetAttributeAsync("viewBox");
            var start = await CenterAsync(reviewerA);
            await page.Mouse.MoveAsync(start.X, start.Y);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(start.X + 40, start.Y + 16, new MouseMoveOptions { Steps = 6 });
            await page.Mouse.UpAsync();
            Assert.Equal(transform, await reviewerA.GetAttributeAsync("transform"));
            Assert.NotEqual(viewBox, await viewer.Locator("#svg").GetAttributeAsync("viewBox"));
            await page.Keyboard.PressAsync("Delete");
            await Assertions.Expect(viewer.Locator(".node")).ToHaveCountAsync(7);

            // Clicking a marked node lists its task and, for this operator, its node execution.
            await SelectNodeAsync(page, diagram, reviewerA, "Reviewer A");
            await Assertions.Expect(details).ToContainTextAsync("Waiting for user work");
            await Assertions.Expect(details.Locator("a[href^='user-tasks/']")).ToHaveCountAsync(1);
            await Assertions.Expect(details.Locator("a[href^='node-executions/']")).ToHaveCountAsync(1);
            await Assertions.Expect(reviewerA.Locator(".viewer-selection-ring")).ToHaveCountAsync(1);
            await Assertions.Expect(reviewerA).ToHaveAttributeAsync("aria-current", "true");
            await diagram.ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "instance-diagram-running.png"),
            });
            var branchHistory = diagram.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Show recorded history" });
            await branchHistory.CheckAsync();
            await Assertions.Expect(viewer.Locator(".edge-label-layer .instance-flow-count")).ToHaveCountAsync(3);
            await diagram.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "history-branch.png") });
            await branchHistory.UncheckAsync();
            await Assertions.Expect(viewer.Locator(".instance-flow-count")).ToHaveCountAsync(0);

            // Keyboard: Tab from the canvas reaches the marked nodes (never the flows),
            // Enter selects the focused node, and Escape clears the selection.
            await viewer.FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            Assert.Equal("Reviewer A, User Task: Waiting for user work",
                await page.EvaluateAsync<string?>("document.activeElement?.getAttribute('aria-label')"));
            await page.Keyboard.PressAsync("Tab");
            Assert.Equal("Reviewer B, User Task: Waiting for user work",
                await page.EvaluateAsync<string?>("document.activeElement?.getAttribute('aria-label')"));
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(details.Locator("h3")).ToHaveTextAsync("Reviewer B");
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(details.Locator("h3")).ToHaveTextAsync("Selected node");

            // The node-execution link opens that execution; Back returns to the diagram.
            await SelectNodeAsync(page, diagram, reviewerA, "Reviewer A");
            await details.Locator("a[href^='node-executions/']").ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex(@"node-executions/\d+$"));
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 }))
                .ToContainTextAsync("Reviewer A");
            await page.GoBackAsync();
            await Assertions.Expect(page.Locator("#diagram")).ToHaveAttributeAsync("data-diagram-ready", "true");

            // Multi-instance progress appears as a badge.
            diagram = await OpenDiagramAsync(scenario, multiInstance.Id);
            await Assertions.Expect(diagram.Locator(".node.instance-active[data-id='2'] .instance-badge text"))
                .ToHaveTextAsync("1/3");
            await diagram.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Show recorded history" }).CheckAsync();
            await Assertions.Expect(diagram.Locator(".edge-label-layer [data-flow='101'] .instance-flow-count"))
                .ToHaveAttributeAsync("data-traversal-count", "1");
            await Assertions.Expect(diagram.Locator(".node.instance-active[data-id='2'] .instance-badge text"))
                .ToHaveTextAsync("1/3");
            await diagram.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "history-mi-entry.png") });

            // A finished instance marks only where it ended.
            diagram = await OpenDiagramAsync(scenario, finished.Id);
            await Assertions.Expect(diagram.Locator(".node.instance-marked")).ToHaveCountAsync(1);
            await Assertions.Expect(diagram.Locator(".node.instance-ended[data-id='4']")).ToHaveCountAsync(1);
            await Assertions.Expect(diagram.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Show recorded history" })).Not.ToBeCheckedAsync();
            await diagram.ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "instance-diagram-finished.png"),
            });
            await diagram.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Show recorded history" }).CheckAsync();
            await Assertions.Expect(diagram.Locator(".node.instance-reached")).ToHaveCountAsync(4);
            await Assertions.Expect(diagram.Locator(".edge-label-layer .instance-flow-count")).ToHaveCountAsync(3);
            await diagram.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "history-finished.png") });
            Assert.True(
                await page.EvaluateAsync<bool>(
                    "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
                $"Instance detail overflows horizontally at {viewportWidth}px.");

            // A reviewer without node-execution read access keeps only the task link.
            await RuntimeSupport.ApplyIdentityAsync(scenario, "reviewer", ReviewerRoles);
            diagram = await OpenDiagramAsync(scenario, parallel.Id);
            details = diagram.Locator(".instance-diagram-details");
            await SelectNodeAsync(page, diagram, diagram.Locator(".node[data-id='3']"), "Reviewer A");
            await Assertions.Expect(details.Locator("a[href^='user-tasks/']")).ToHaveCountAsync(1);
            await Assertions.Expect(details)
                .ToContainTextAsync("No node execution details for this node are available to you.");
            await Assertions.Expect(details.Locator("a[href^='node-executions/']")).ToHaveCountAsync(0);
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task R8_RecordedHistoryKeepsPositionsAndProvidesCountsPagingAndKeyboardAccess(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r8-diagram-history-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight,
            scenarioTimeout: TimeSpan.FromMinutes(4));
        await scenario.RunAsync("recorded-history", async () =>
        {
            var workflow = await stack.SetupClient.CreateAndPublishAsync(HistoryLoopModel());
            var instance = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);
            // Real committed actions create 51 visits; the historical page must not replace
            // the independent current-position link with the one old visit on page two.
            for (var index = 0; index < 50; index++)
                await stack.SetupClient.TakeInstanceFlowAsync(instance.Id, 201);
            await RuntimeSupport.ApplyIdentityAsync(scenario, "history-reader", OperatorRoles);
            var page = scenario.Page;
            var diagram = await OpenDiagramAsync(scenario, instance.Id);
            var viewer = diagram.Locator(".flowbit-editor[data-mode='viewer']");
            var canvas = viewer.Locator("#svg");
            var details = diagram.Locator(".instance-diagram-details");
            var historyToggle = diagram.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Show recorded history" });
            await Assertions.Expect(historyToggle).Not.ToBeCheckedAsync();
            await Assertions.Expect(viewer.Locator(".instance-flow-count")).ToHaveCountAsync(0);
            await diagram.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "history-off.png") });

            await viewer.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Zoom in", Exact = true }).ClickAsync();
            var beforeHistory = await canvas.GetAttributeAsync("viewBox");
            await historyToggle.CheckAsync();
            await Assertions.Expect(viewer.Locator(".edge-label-layer [data-flow='201'] .instance-flow-count"))
                .ToHaveAttributeAsync("data-traversal-count", "50");
            Assert.Equal(beforeHistory, await canvas.GetAttributeAsync("viewBox"));
            await Assertions.Expect(viewer.Locator(".node.instance-marked")).ToHaveCountAsync(1);
            await Assertions.Expect(viewer.Locator(".node.instance-reached")).ToHaveCountAsync(3);
            await Assertions.Expect(viewer.Locator(".node[tabindex='0']")).ToHaveCountAsync(1);
            await Assertions.Expect(viewer.Locator(".node[data-id='1']")).Not.ToHaveAttributeAsync("tabindex", "0");
            await Assertions.Expect(viewer.Locator(".edge.trace-active")).ToHaveCountAsync(0);
            await Assertions.Expect(diagram).ToContainTextAsync("across all retained runs");
            await diagram.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "history-loop.png") });

            var historyList = diagram.Locator(".instance-diagram-history-list");
            await historyList.Locator("summary").ClickAsync();
            await page.Keyboard.PressAsync("Tab");
            Assert.True(await page.EvaluateAsync<bool>("document.activeElement?.classList.contains('instance-diagram-history-node') === true"));
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(details.Locator("h3")).ToHaveTextAsync("Repeat review");
            var historicalStart = historyList.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameRegex = new Regex("^Start") });
            await historicalStart.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(details.Locator("h3")).ToHaveTextAsync("Start");
            await Assertions.Expect(viewer.Locator(".node[data-id='1'] .viewer-selection-ring")).ToHaveCountAsync(1);
            await Assertions.Expect(details.Locator(".instance-diagram-flow-counts")).ToContainTextAsync("1");
            await Assertions.Expect(details.Locator(".instance-diagram-recorded-executions a[href^='node-executions/']")).ToHaveCountAsync(1);
            await historyList.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "history-text-list.png") });
            var reviewButton = historyList.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameRegex = new Regex("^Review history") });
            await reviewButton.FocusAsync();
            await page.Keyboard.PressAsync("Space");
            await Assertions.Expect(details.Locator("h3")).ToHaveTextAsync("Review history");
            var currentLink = details.Locator(".instance-diagram-positions a[href^='node-executions/']");
            await Assertions.Expect(currentLink).ToHaveCountAsync(1);
            var currentHref = await currentLink.GetAttributeAsync("href");
            await Assertions.Expect(details.Locator(".instance-diagram-recorded-executions a[href^='node-executions/']")).ToHaveCountAsync(50);
            await Assertions.Expect(details).ToContainTextAsync("51 execution records");
            await details.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Next executions" }).ClickAsync();
            await Assertions.Expect(details.Locator(".instance-diagram-recorded-executions a[href^='node-executions/']")).ToHaveCountAsync(1);
            Assert.Equal(currentHref, await currentLink.GetAttributeAsync("href"));
            await diagram.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "history-selected-paged.png") });
            await details.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Previous executions" }).ClickAsync();
            await Assertions.Expect(details.Locator(".instance-diagram-recorded-executions a[href^='node-executions/']")).ToHaveCountAsync(50);

            // Count updates do not move the user's viewport; actions are real API actions.
            var currentNode = viewer.Locator(".node[data-id='2']");
            await canvas.ScrollIntoViewIfNeededAsync();
            var point = await CenterAsync(currentNode);
            var originalTransform = await currentNode.GetAttributeAsync("transform");
            await page.Mouse.MoveAsync(point.X, point.Y);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(point.X + 35, point.Y + 15, new MouseMoveOptions { Steps = 5 });
            await page.Mouse.UpAsync();
            var panned = await canvas.GetAttributeAsync("viewBox");
            await stack.SetupClient.TakeInstanceFlowAsync(instance.Id, 201);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh", Exact = true }).ClickAsync();
            await Assertions.Expect(viewer.Locator(".edge-label-layer [data-flow='201'] .instance-flow-count"))
                .ToHaveAttributeAsync("data-traversal-count", "51");
            Assert.Equal(panned, await canvas.GetAttributeAsync("viewBox"));
            Assert.Equal(originalTransform, await currentNode.GetAttributeAsync("transform"));
            await canvas.ScrollIntoViewIfNeededAsync();
            var canvasPoint = await CenterAsync(canvas);
            await page.Mouse.MoveAsync(canvasPoint.X, canvasPoint.Y);
            var scrollBefore = await page.EvaluateAsync<double>("window.scrollY");
            await page.Mouse.WheelAsync(0, 240);
            await page.WaitForFunctionAsync("before => Math.abs(window.scrollY - before) > 1", scrollBefore);
            Assert.Equal(panned, await canvas.GetAttributeAsync("viewBox"));
            await canvas.ScrollIntoViewIfNeededAsync();
            canvasPoint = await CenterAsync(canvas);
            await page.Mouse.MoveAsync(canvasPoint.X, canvasPoint.Y);
            await page.Keyboard.DownAsync("Control");
            await page.Mouse.WheelAsync(0, -240);
            await page.Keyboard.UpAsync("Control");
            await Assertions.Expect(canvas).Not.ToHaveAttributeAsync("viewBox", panned!);
            await viewer.FocusAsync();
            await page.Keyboard.PressAsync("Delete");
            await page.Keyboard.PressAsync("Control+z");
            await page.Keyboard.PressAsync("c");
            await Assertions.Expect(viewer.Locator(".node")).ToHaveCountAsync(4);
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(details.Locator("h3")).ToHaveTextAsync("Selected node");
            await historyToggle.UncheckAsync();
            await Assertions.Expect(viewer.Locator(".instance-flow-count, .instance-history-halo")).ToHaveCountAsync(0);
            await Assertions.Expect(viewer.Locator(".node.instance-active")).ToHaveCountAsync(1);

            // The ordinary reviewer receives the same counts and no restricted execution links.
            await RuntimeSupport.ApplyIdentityAsync(scenario, "history-reviewer", ReviewerRoles);
            diagram = await OpenDiagramAsync(scenario, instance.Id);
            viewer = diagram.Locator(".flowbit-editor[data-mode='viewer']");
            details = diagram.Locator(".instance-diagram-details");
            await diagram.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Show recorded history" }).CheckAsync();
            await Assertions.Expect(viewer.Locator(".edge-label-layer [data-flow='201'] .instance-flow-count"))
                .ToHaveAttributeAsync("data-traversal-count", "51");
            await SelectNodeAsync(page, diagram, viewer.Locator(".node[data-id='2']"), "Review history");
            await Assertions.Expect(details.Locator("a[href^='node-executions/']")).ToHaveCountAsync(0);
            await Assertions.Expect(details).ToContainTextAsync("No node execution details");
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"));
            await diagram.ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "history-reviewer.png") });
        });
    }

    private static WorkflowModel HistoryLoopModel() => new()
    {
        Id = $"browser-diagram-history-{Guid.NewGuid():N}", Name = "Recorded history review", InitialEventId = 1,
        FlowNodes =
        [
            new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent, X = 40, Y = 100 },
            new() { Id = 2, Name = "Review history", Type = BpmnFlowNodeTypes.UserTask, X = 230, Y = 90 },
            new() { Id = 3, Name = "Repeat review", Type = BpmnFlowNodeTypes.ExclusiveGateway, X = 530, Y = 100 },
            new() { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent, X = 760, Y = 100 }
        ],
        SequenceFlows =
        [
            new() { Id = 101, SourceRef = 1, TargetRef = 2 },
            new() { Id = 201, Name = "Review again", SourceRef = 2, TargetRef = 3 },
            new() { Id = 202, Name = "Finish", SourceRef = 2, TargetRef = 4 },
            new() { Id = 301, Name = "Return", SourceRef = 3, TargetRef = 2, Condition = "true", ConditionPriority = 1 },
            new() { Id = 302, Name = "Fallback", SourceRef = 3, TargetRef = 4, IsDefault = true }
        ]
    };

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task R8_MultiInstanceEntryBeforeFanOutShowsEvidenceAndCoverageLimits(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r8-diagram-mi-coverage-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("mi-history-coverage", async () =>
        {
            await RuntimeSupport.ApplyIdentityAsync(scenario, "history-operator", OperatorRoles);
            foreach (var multiInstanceSource in new[] { false, true })
            {
                var workflow = await stack.SetupClient.CreateAndPublishAsync(InterruptedMiModel(multiInstanceSource));
                var instance = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);
                if (multiInstanceSource)
                {
                    var task = Assert.Single(await stack.SetupClient.GetInboxAsync(instance.Id));
                    await stack.SetupClient.TakeUserTaskFlowAsync(task.UserTaskId, 601);
                }
                Assert.Equal("completed", (await stack.SetupClient.GetInstanceAsync(instance.Id)).Status);
                var diagram = await OpenDiagramAsync(scenario, instance.Id);
                await diagram.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Show recorded history" }).CheckAsync();
                var viewer = diagram.Locator(".flowbit-editor[data-mode='viewer']");
                await Assertions.Expect(viewer.Locator(".edge-label-layer [data-flow='401'] .instance-flow-count"))
                    .ToHaveAttributeAsync("data-traversal-count", "1");
                await Assertions.Expect(viewer.Locator(".node.instance-ended[data-id='5']")).ToHaveCountAsync(1);
                if (multiInstanceSource)
                {
                    await Assertions.Expect(diagram.Locator(".instance-diagram-coverage"))
                        .ToContainTextAsync("An entry that ended before its children existed may be unrecorded");
                    await Assertions.Expect(viewer.Locator(".node.instance-reached[data-id='2']")).ToHaveCountAsync(0);
                    await Assertions.Expect(viewer.Locator(".edge-label-layer [data-flow='601'] .instance-flow-count")).ToHaveCountAsync(0);
                }
                else
                {
                    await Assertions.Expect(viewer.Locator(".node.instance-reached[data-id='2']")).ToHaveCountAsync(1);
                    await Assertions.Expect(viewer.Locator(".edge-label-layer [data-flow='101'] .instance-flow-count"))
                        .ToHaveAttributeAsync("data-traversal-count", "1");
                    await Assertions.Expect(diagram.Locator(".instance-diagram-coverage")).ToHaveCountAsync(0);
                    var list = diagram.Locator(".instance-diagram-history-list");
                    await list.Locator("summary").ClickAsync();
                    await list.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameRegex = new Regex("^Interrupted review") }).ClickAsync();
                    await Assertions.Expect(diagram.Locator(".instance-diagram-details h3")).ToHaveTextAsync("Interrupted review");
                    await Assertions.Expect(diagram.Locator(".instance-diagram-details"))
                        .ToContainTextAsync("No retained execution detail");
                }
                await diagram.ScreenshotAsync(new LocatorScreenshotOptions
                {
                    Path = Path.Combine(scenario.ArtifactDirectory, multiInstanceSource ? "history-coverage-notice.png" : "history-mi-before-fanout.png")
                });
                Assert.True(await scenario.Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"));
            }
        });
    }

    private static WorkflowModel InterruptedMiModel(bool multiInstanceSource)
    {
        var model = new WorkflowModel
        {
            Id = $"browser-diagram-interrupted-mi-{Guid.NewGuid():N}", Name = "Interrupted MI history", InitialEventId = 1,
            Variables =
            [
                new() { Id = 1, Name = "stop", DataType = "boolean", DefaultValue = JsonSerializer.SerializeToElement(true) },
                new() { Id = 2, Name = "results", DataType = "json", DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) }
            ],
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent, X = 40, Y = 100 },
                new() { Id = 2, Name = "Interrupted review", Type = BpmnFlowNodeTypes.UserTask, X = 500, Y = 90,
                    MultiInstance = new() { Mode = "parallel", Source = "cardinality", CardinalityExpression = "2", ResultVariable = "results", CompletionEvaluation = "afterAll" } },
                new() { Id = 3, Name = "Normal end", Type = BpmnFlowNodeTypes.EndEvent, X = 780, Y = 90 },
                new() { Id = 4, Name = "Stop review", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = 2, CancelActivity = true,
                    Conditional = new() { Condition = "stop == true", DeliveryMode = "atomic" } },
                new() { Id = 5, Name = "Interrupted end", Type = BpmnFlowNodeTypes.EndEvent, X = 780, Y = 300 }
            ],
            SequenceFlows =
            [
                new() { Id = 101, SourceRef = 1, TargetRef = multiInstanceSource ? 6 : 2 },
                new() { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Approve", CompletionCondition = "CountFlow(201) >= 2", CompletionPriority = 1 },
                new() { Id = 202, SourceRef = 2, TargetRef = 3, IsDefault = true, IsSelectable = false },
                new() { Id = 401, SourceRef = 4, TargetRef = 5, Name = "Interrupted" }
            ]
        };
        if (multiInstanceSource)
        {
            model.Variables.Add(new() { Id = 3, Name = "sourceResults", DataType = "json", DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) });
            model.FlowNodes.Add(new() { Id = 6, Name = "First review", Type = BpmnFlowNodeTypes.UserTask, X = 230, Y = 90,
                MultiInstance = new() { Mode = "parallel", Source = "cardinality", CardinalityExpression = "1", ResultVariable = "sourceResults", CompletionEvaluation = "afterAll" } });
            model.SequenceFlows.Add(new() { Id = 601, SourceRef = 6, TargetRef = 2, Name = "Next review", CompletionCondition = "CountFlow(601) >= 1", CompletionPriority = 1 });
            model.SequenceFlows.Add(new() { Id = 602, SourceRef = 6, TargetRef = 2, IsDefault = true, IsSelectable = false });
        }
        return model;
    }

    private static async Task<ILocator> OpenDiagramAsync(BrowserScenario scenario, long instanceId)
    {
        await scenario.OpenUiAsync($"instances/{instanceId}");
        var diagram = scenario.Page.Locator("#diagram");
        await Assertions.Expect(diagram).ToHaveAttributeAsync("data-diagram-ready", "true");
        await diagram.ScrollIntoViewIfNeededAsync();
        return diagram;
    }

    // A stationary click selects. It is retried until the details panel names
    // the node, because the first click can precede the circuit's handler.
    private static Task SelectNodeAsync(IPage page, ILocator diagram, ILocator node, string name) =>
        RuntimeSupport.ClickUntilAsync(
            async () =>
            {
                var center = await CenterAsync(node);
                await page.Mouse.ClickAsync(center.X, center.Y);
            },
            async () => (await diagram.Locator(".instance-diagram-details h3").InnerTextAsync()).Trim() == name,
            TimeSpan.FromSeconds(30));

    private static async Task<(float X, float Y)> CenterAsync(ILocator node)
    {
        await node.ScrollIntoViewIfNeededAsync();
        var box = await node.BoundingBoxAsync()
            ?? throw new InvalidOperationException("The diagram node is not rendered.");
        return (box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    private static async Task<WorkflowFixtureClient> CreateActorClientAsync(
        BrowserScenario scenario, string user, string[] roles)
    {
        // Mint the actor's token through the real token screen on an auxiliary
        // page. The scenario applies its own identity afterwards.
        var auxiliary = await scenario.Context.NewPageAsync();
        try
        {
            var identity = new IdentityScreen(auxiliary, scenario.Stack.UiBaseAddress);
            var token = await identity.GenerateAndApplyIdentityAsync(user, roles);
            return scenario.Stack.CreateClient(user, roles, token, $"{scenario.Name}-{user}");
        }
        finally
        {
            await auxiliary.CloseAsync();
        }
    }
}
