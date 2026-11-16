using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// EVENT-06 link events in a real browser. E22 authors a link in the standalone editor: type changes, pairing
/// through <b>Continues at</b>, the partner ring, <b>Go to catch</b>, refused connections, a broken name in the
/// status line and the save dialog, undo and redo, and a save and reload. R15 publishes the canonical link example
/// over HTTP, completes it through Flowbit.Ui, and reads the link on instance detail.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class LinkEventSmokeTests(BrowserStackFixture stack)
{
    public static TheoryData<int, int> EditorViewports => new()
    {
        { 1440, 900 },
        { 1024, 768 },
    };

    [Theory]
    [MemberData(nameof(EditorViewports))]
    public async Task E22_LinkEventsPairNavigateRefuseConnectionsValidateUndoAndSave(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"e22-link-events-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight, blockNativeFilePicker: true);
        scenario.ExpectDialogStartingWith(IdentityScreen.FallbackAlertPrefix);
        await scenario.RunAsync("link-events", async () =>
        {
            await scenario.OpenEditorAsync(stack.EditorBaseAddress);
            await EditorInteractions.PinInspectorOpenAsync(scenario.Page);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(page, EditorInteractions.FixturePath("editor-link-events.json"));
            var inspector = page.Locator("#inspector");
            var throwNode = page.Locator("#nodes .node[data-id='3']");
            var catchNode = page.Locator("#nodes .node[data-id='4']");

            // Turn the dangling task into a throw and the unreached task into a catch.
            await EditorInteractions.SelectNodeAsync(page, 3);
            await EditorInteractions.SelectTypeAsync(page, "intermediateLinkThrowEvent");
            await Assertions.Expect(throwNode).ToHaveClassAsync(new Regex("intermediateLinkThrowEvent"));
            await Assertions.Expect(inspector.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Outgoing flows" }))
                .ToHaveCountAsync(0);
            await EditorInteractions.SelectNodeAsync(page, 4);
            await EditorInteractions.SelectTypeAsync(page, "intermediateLinkCatchEvent");
            await Assertions.Expect(catchNode).ToHaveClassAsync(new Regex("intermediateLinkCatchEvent"));
            await Assertions.Expect(inspector).ToContainTextAsync("No link throw event uses the name 'Receive' yet.");

            // Pair them by choosing the catch on the throw; its name is copied.
            await EditorInteractions.SelectNodeAsync(page, 3);
            var status = inspector.Locator(".link-status");
            await Assertions.Expect(status).ToHaveTextAsync(
                "No link catch event is named 'Hand off'. Choose one above, or give a link catch event this name.");
            var continuesAt = page.GetByLabel("Continues at", new PageGetByLabelOptions { Exact = true });
            await continuesAt.SelectOptionAsync("4");
            await Assertions.Expect(status).ToHaveTextAsync("Tokens continue at link catch event #4 ('Receive').");
            // The choice redraws the inspector; focus returns to the new list.
            await Assertions.Expect(continuesAt).ToBeFocusedAsync();
            await Assertions.Expect(FormInput(inspector, "Name")).ToHaveValueAsync("Receive");
            await Assertions.Expect(catchNode).ToHaveClassAsync(new Regex("link-partner"));
            await Assertions.Expect(catchNode.Locator(".link-partner-ring")).ToHaveCountAsync(1);
            await Assertions.Expect(throwNode.Locator("title")).ToContainTextAsync("Continues at #4");
            await page.Locator("#inspectorPanel").ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "link-throw-inspector.png"),
            });
            await page.Locator("#svg").ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "link-partner-ring.png"),
            });

            // Go to catch selects the catch; the catch lists its throw and rings it. The pressed button is gone
            // after the redraw, so focus moves to the catch's heading and stays inside the editor.
            await inspector.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Go to catch #4" }).ClickAsync();
            await Assertions.Expect(inspector.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Node #4", Exact = true }))
                .ToBeFocusedAsync();
            await Assertions.Expect(status).ToHaveTextAsync("Tokens continue here from 1 link throw event:");
            await Assertions.Expect(throwNode).ToHaveClassAsync(new Regex("link-partner"));
            var backToThrow = inspector.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Go to link throw event #3 · Receive" });
            await backToThrow.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(inspector.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Node #3", Exact = true }))
                .ToBeFocusedAsync();

            // A throw cannot start a flow, and a catch cannot receive one. Go to zoomed in, so fit first.
            var hint = page.Locator("#hint");
            await page.Locator("#fitViewBtn").ClickAsync();
            await StartConnectModeAsync(page);
            await EditorInteractions.ClickCenterAsync(page, await EditorInteractions.GetNodeBoxAsync(page, 3));
            await Assertions.Expect(hint).ToHaveTextAsync("Connect: A link throw event cannot have an outgoing sequence flow.");
            await StartConnectModeAsync(page);
            await EditorInteractions.ClickCenterAsync(page, await EditorInteractions.GetNodeBoxAsync(page, 2));
            await EditorInteractions.ClickCenterAsync(page, await EditorInteractions.GetNodeBoxAsync(page, 4));
            await Assertions.Expect(hint).ToHaveTextAsync("Connect: A link catch event cannot have an incoming sequence flow.");
            await Assertions.Expect(page.Locator("#edges .edge-path-layer [data-flow]")).ToHaveCountAsync(3);

            // A broken name shows in the status line as it is typed and blocks saving.
            await EditorInteractions.SelectNodeAsync(page, 4);
            await EditorInteractions.FillInspectorNameAsync(page, "Received");
            await Assertions.Expect(status).ToHaveTextAsync("No link throw event uses the name 'Received' yet.");
            await Assertions.Expect(throwNode).Not.ToHaveClassAsync(new Regex("link-partner"));
            await page.Locator("#fileMenuSummary").ClickAsync();
            await page.Locator("#saveBtn").ClickAsync();
            var errors = await EditorInteractions.WaitForValidationDialogAsync(page);
            Assert.Contains("Link throw event #3 has no link catch event named 'Receive'.", errors, StringComparison.Ordinal);
            Assert.Contains("Link catch event #4 has no link throw event named 'Received'.", errors, StringComparison.Ordinal);
            await EditorInteractions.CloseValidationDialogWithEscapeAsync(page);

            // Undo restores the pair; redo breaks it again; undo once more.
            await page.EvaluateAsync("document.activeElement?.blur()");
            await page.Keyboard.PressAsync("Control+z");
            await Assertions.Expect(FormInput(inspector, "Name")).ToHaveValueAsync("Receive");
            await Assertions.Expect(status).ToHaveTextAsync("Tokens continue here from 1 link throw event:");
            await page.Keyboard.PressAsync("Control+y");
            await Assertions.Expect(FormInput(inspector, "Name")).ToHaveValueAsync("Received");
            await page.Keyboard.PressAsync("Control+z");
            await Assertions.Expect(FormInput(inspector, "Name")).ToHaveValueAsync("Receive");

            using var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(page, scenario, scenario.ArtifactDirectory);
            var nodes = saved.RootElement.GetProperty("flowNodes").EnumerateArray().ToArray();
            var savedThrow = nodes.Single(node => node.GetProperty("id").GetInt32() == 3);
            var savedCatch = nodes.Single(node => node.GetProperty("id").GetInt32() == 4);
            Assert.Equal("intermediateLinkThrowEvent", savedThrow.GetProperty("type").GetString());
            Assert.Equal("intermediateLinkCatchEvent", savedCatch.GetProperty("type").GetString());
            Assert.Equal("Receive", savedThrow.GetProperty("name").GetString());
            Assert.Equal("Receive", savedCatch.GetProperty("name").GetString());
            Assert.DoesNotContain(saved.RootElement.GetProperty("sequenceFlows").EnumerateArray(),
                flow => flow.GetProperty("sourceRef").GetInt32() == 3 || flow.GetProperty("targetRef").GetInt32() == 4);

            // A fresh page reloads the pair.
            var freshPage = await scenario.Context.NewPageAsync();
            try
            {
                await freshPage.GotoAsync($"{stack.EditorBaseAddress}/", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                await EditorInteractions.PinInspectorOpenAsync(freshPage);
                await EditorInteractions.LoadSavedFileAsync(freshPage, scenario.LastSavedFile!);
                await EditorInteractions.SelectNodeAsync(freshPage, 3);
                await Assertions.Expect(freshPage.Locator("#inspector .link-status"))
                    .ToHaveTextAsync("Tokens continue at link catch event #4 ('Receive').");
                await Assertions.Expect(freshPage.Locator("#nodes .node[data-id='4']")).ToHaveClassAsync(new Regex("link-partner"));
            }
            finally
            {
                await freshPage.CloseAsync();
            }
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task R15_LinkExampleRunsThroughTheUiAndInstanceDetailShowsTheLink(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r15-link-events-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("link-example", async () =>
        {
            var workflow = await RuntimeSupport.PublishAsync(stack, "example-link-events.json");
            var instance = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);
            // admin satisfies the default NodeExecution.RequiredRole read setting.
            await RuntimeSupport.ApplyIdentityAsync(scenario, "link-operator", ["admin"]);
            var page = scenario.Page;

            // Approve, then file the decision, with real clicks on instance detail.
            await TakeActionAsync(scenario, instance.Id, "Approve");
            await TakeActionAsync(scenario, instance.Id, "Filed");
            await Assertions.Expect(page.Locator("#instance-summary-heading").Locator("xpath=ancestor::section"))
                .ToContainTextAsync("Completed", new LocatorAssertionsToContainTextOptions { Timeout = 20_000f });

            // History describes both steps of the link.
            var history = page.Locator("#history");
            await Assertions.Expect(history).ToContainTextAsync("Link throw #5 continued token");
            await Assertions.Expect(history).ToContainTextAsync("Continued from link catch #7");

            // Recorded history marks both events and counts the flows on either side of the link.
            var diagram = page.Locator("#diagram");
            await Assertions.Expect(diagram).ToHaveAttributeAsync("data-diagram-ready", "true");
            await diagram.ScrollIntoViewIfNeededAsync();
            var viewer = diagram.Locator(".flowbit-editor[data-mode='viewer']");
            await diagram.GetByRole(AriaRole.Checkbox, new LocatorGetByRoleOptions { Name = "Show recorded history" }).CheckAsync();
            await Assertions.Expect(viewer.Locator(".node.instance-reached[data-id='5']")).ToHaveCountAsync(1);
            await Assertions.Expect(viewer.Locator(".node.instance-reached[data-id='7']")).ToHaveCountAsync(1);
            await Assertions.Expect(viewer.Locator(".node.instance-reached[data-id='6']")).ToHaveCountAsync(0);
            await Assertions.Expect(viewer.Locator(".edge-label-layer [data-flow='301'] .instance-flow-count"))
                .ToHaveAttributeAsync("data-traversal-count", "1");
            await Assertions.Expect(viewer.Locator(".edge-label-layer [data-flow='701'] .instance-flow-count"))
                .ToHaveAttributeAsync("data-traversal-count", "1");

            // Selecting the throw rings the catch and links to it; the link selects the catch.
            var details = diagram.Locator(".instance-diagram-details");
            var historyList = diagram.Locator(".instance-diagram-history-list");
            await historyList.Locator("summary").ClickAsync();
            await historyList.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameRegex = new Regex("Link throw") })
                .First.ClickAsync();
            await Assertions.Expect(details.Locator("h3")).ToHaveTextAsync("Close the expense");
            await Assertions.Expect(details).ToContainTextAsync("Node #5 · Link throw");
            await Assertions.Expect(viewer.Locator(".node[data-id='7'].link-partner .link-partner-ring")).ToHaveCountAsync(1);
            await Assertions.Expect(viewer.Locator(".node[data-id='6']")).Not.ToHaveClassAsync(new Regex("link-partner"));
            await diagram.ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "instance-diagram-link.png"),
            });
            var toCatch = details.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Select link catch event Close the expense (#7)" });
            await toCatch.ClickAsync();
            await Assertions.Expect(details).ToContainTextAsync("Node #7 · Link catch");
            await Assertions.Expect(details.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Select link throw event Close the expense (#5)" }))
                .ToHaveCountAsync(1);
            await Assertions.Expect(details.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Select link throw event Close the expense (#6)" }))
                .ToHaveCountAsync(1);
            await Assertions.Expect(viewer.Locator(".node[data-id='5'].link-partner")).ToHaveCountAsync(1);
            await Assertions.Expect(viewer.Locator(".node[data-id='6'].link-partner")).ToHaveCountAsync(1);
            // Throw #6 was never reached. Its dotted partner ring must not read as the dashed "Reached" halo of throw #5.
            await Assertions.Expect(viewer.Locator(".node[data-id='5'] .instance-history-halo")).ToHaveCountAsync(1);
            await Assertions.Expect(viewer.Locator(".node[data-id='6'] .instance-history-halo")).ToHaveCountAsync(0);
            const string strokeStyle = "element => { const style = getComputedStyle(element); return style.stroke + ' / ' + style.strokeDasharray; }";
            var ringStyle = await viewer.Locator(".node[data-id='6'] .link-partner-ring").EvaluateAsync<string>(strokeStyle);
            var haloStyle = await viewer.Locator(".node[data-id='5'] .instance-history-halo").EvaluateAsync<string>(strokeStyle);
            Assert.NotEqual(haloStyle, ringStyle);
            await viewer.Locator("#svg").ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "instance-diagram-link-catch.png"),
            });
            Assert.True(
                await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
                $"Instance detail overflows horizontally at {viewportWidth}px.");
        });
    }

    private static async Task TakeActionAsync(BrowserScenario scenario, long instanceId, string action)
    {
        await scenario.OpenUiAsync($"instances/{instanceId}");
        var card = scenario.Page.Locator("#actions .card", new PageLocatorOptions { HasText = action });
        await Assertions.Expect(card).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 20_000f });
        await RuntimeSupport.ClickUntilAsync(
            async () => await card.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Take action" }).ClickAsync(),
            async () => await card.CountAsync() == 0,
            TimeSpan.FromSeconds(20));
    }

    private static ILocator FormInput(ILocator scope, string label) =>
        scope.Locator(".field").Filter(new LocatorFilterOptions
        {
            Has = scope.Page.Locator($"xpath=./label[normalize-space(.)='{label}']"),
        }).First.Locator("input, textarea").First;

    private static async Task StartConnectModeAsync(IPage page)
    {
        await page.Locator("#authoringPaletteHandle").HoverAsync();
        await page.WaitForFunctionAsync("document.getElementById('authoringPalette').classList.contains('is-open')");
        await page.Locator("#connectBtn").ClickAsync();
        await Assertions.Expect(page.Locator("#connectBtn")).ToHaveAttributeAsync("aria-pressed", "true");
    }
}
