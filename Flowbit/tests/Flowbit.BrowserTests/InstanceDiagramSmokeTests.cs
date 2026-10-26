using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
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

            // A finished instance marks only where it ended.
            diagram = await OpenDiagramAsync(scenario, finished.Id);
            await Assertions.Expect(diagram.Locator(".node.instance-marked")).ToHaveCountAsync(1);
            await Assertions.Expect(diagram.Locator(".node.instance-ended[data-id='4']")).ToHaveCountAsync(1);
            await diagram.ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "instance-diagram-finished.png"),
            });
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
