using System.Globalization;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// EVENT-04 signal events in a real browser. R16 drives the Signals page through Flowbit.Ui:
/// a refused payload, a reviewed throw with no waiting events, the broadcast and its empty
/// delivery list, the history row, an unknown workflow family refused by the API, an instance
/// scope with its link, a business key whose edit ends the review, the API's refusals of a
/// business key without business keys and of an unknown instance, and the permission screen
/// for an identity without the signal role. The smoke stack has no Worker
/// and refuses durable publication, so delivery to a waiting instance is the Worker
/// acceptance suite's scenario.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class SignalSmokeTests(BrowserStackFixture stack)
{
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    public async Task E23_SignalCatchIsNamedMappedValidatedUndoneAndSaved(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"e23-signal-catch-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight, blockNativeFilePicker: true);
        scenario.ExpectDialogStartingWith(IdentityScreen.FallbackAlertPrefix);
        await scenario.RunAsync("signal-catch", async () =>
        {
            await scenario.OpenEditorAsync(stack.EditorBaseAddress);
            await EditorInteractions.PinInspectorOpenAsync(scenario.Page);
            var page = scenario.Page;
            await EditorInteractions.LoadWorkflowAsync(page, EditorInteractions.FixturePath("editor-signal-events.json"));
            var inspector = page.Locator("#inspector");
            var catchNode = page.Locator("#nodes .node[data-id='2']");

            // The automatic task becomes a signal catch with a Signal section and a solid outgoing flow.
            await EditorInteractions.SelectNodeAsync(page, 2);
            await EditorInteractions.SelectTypeAsync(page, "intermediateSignalCatchEvent");
            await Assertions.Expect(catchNode).ToHaveClassAsync(new Regex("intermediateSignalCatchEvent"));
            // The heading also holds its help button, so match its start.
            await Assertions.Expect(inspector.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { NameRegex = new Regex(@"^Signal\b") }))
                .ToHaveCountAsync(1);
            var flow = page.Locator("#edges .edge-path-layer .edge[data-flow='201']");
            await Assertions.Expect(flow).ToHaveCountAsync(1);
            await Assertions.Expect(flow).Not.ToHaveClassAsync(new Regex(@"\bauto\b"));

            // An uppercase name is reported as typed, with the reason, and blocks saving.
            var name = inspector.GetByLabel("Signal name", new LocatorGetByLabelOptions { Exact = true });
            var status = inspector.Locator("#signal-name-status-2");
            await name.FillAsync("Claims.Policy-Changed");
            await Assertions.Expect(status).ToContainTextAsync(
                "signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.");
            await Assertions.Expect(status).ToContainTextAsync("Use lowercase letters");
            await Assertions.Expect(name).ToHaveAttributeAsync("aria-invalid", "true");
            await name.PressAsync("Tab");
            await page.Locator("#fileMenuSummary").ClickAsync();
            await page.Locator("#saveBtn").ClickAsync();
            var errors = await EditorInteractions.WaitForValidationDialogAsync(page);
            Assert.Contains(
                "Signal catch event #2 signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.",
                errors, StringComparison.Ordinal);
            await EditorInteractions.CloseValidationDialogWithEscapeAsync(page);
            await name.FillAsync("claims.policy-changed");
            await name.PressAsync("Tab");
            await Assertions.Expect(status).ToHaveTextAsync(string.Empty);
            await Assertions.Expect(name).ToHaveAttributeAsync("aria-invalid", "false");

            // A new mapping takes focus; choosing the declared variable adopts its type.
            await inspector.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "+ Add mapping" }).ClickAsync();
            var variable = inspector.Locator("[data-signal-mapping='0']");
            await Assertions.Expect(variable).ToBeFocusedAsync();
            await page.Keyboard.TypeAsync("policyVersion");
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(inspector.GetByLabel("Data type", new LocatorGetByLabelOptions { Exact = true })).ToHaveValueAsync("number");
            var path = inspector.GetByLabel("Payload path", new LocatorGetByLabelOptions { Exact = true });
            await path.FillAsync("version");
            await path.PressAsync("Tab");
            await page.Locator("#inspectorPanel").ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "signal-catch-inspector.png"),
            });
            await catchNode.ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "signal-catch-node.png"),
            });

            // Undo removes the path; redo restores it.
            await page.EvaluateAsync("document.activeElement?.blur()");
            await page.Keyboard.PressAsync("Control+z");
            await Assertions.Expect(path).ToHaveValueAsync(string.Empty);
            await page.Keyboard.PressAsync("Control+y");
            await Assertions.Expect(path).ToHaveValueAsync("version");

            // Two signal boundaries on the review task, each added from the host and connected to the end.
            await page.Locator("#fitViewBtn").ClickAsync();
            var boundaries = page.Locator("#nodes .node.signalBoundaryEvent");
            async Task<string> AddBoundaryAsync(int expectedCount)
            {
                await EditorInteractions.SelectNodeAsync(page, 3);
                await inspector.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "+ Add signal boundary event" }).ClickAsync();
                // The new boundary is selected and starts a connection; the next node clicked is its target.
                await EditorInteractions.ClickCenterAsync(page, await EditorInteractions.GetNodeBoxAsync(page, 4));
                await Assertions.Expect(boundaries).ToHaveCountAsync(expectedCount);
                return (await boundaries.Nth(expectedCount - 1).GetAttributeAsync("data-id"))!;
            }

            // Search selects by id at any zoom, which a click on a small boundary circle cannot.
            async Task SelectBySearchAsync(string nodeId)
            {
                await page.EvaluateAsync("document.activeElement?.blur()");
                await page.Keyboard.PressAsync("/");
                await Assertions.Expect(page.Locator("#diagramSearchInput")).ToBeFocusedAsync();
                await page.Keyboard.PressAsync("Control+a");
                await page.Keyboard.TypeAsync($"#{nodeId}");
                await page.Keyboard.PressAsync("Enter");
                await Assertions.Expect(inspector.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = $"Node #{nodeId}", Exact = true }))
                    .ToHaveCountAsync(1);
            }

            var recallId = await AddBoundaryAsync(1);
            await SelectBySearchAsync(recallId);
            await inspector.GetByLabel("Signal name", new LocatorGetByLabelOptions { Exact = true }).FillAsync("claims.recall");
            await inspector.GetByLabel("Signal name", new LocatorGetByLabelOptions { Exact = true }).PressAsync("Tab");
            var interrupt = inspector.GetByLabel("Interrupt attached activity", new LocatorGetByLabelOptions { Exact = true });
            await Assertions.Expect(interrupt).ToBeCheckedAsync();
            await interrupt.UncheckAsync();
            var recall = page.Locator($"#nodes .node[data-id='{recallId}']");
            await Assertions.Expect(recall).ToHaveClassAsync(new Regex(@"\bnon-interrupting\b"));
            await Assertions.Expect(recall.Locator("circle.ring")).ToHaveCSSAsync("stroke-dasharray", new Regex(@"^\d"));

            // A second boundary with the same name is reported as typed and blocks saving.
            var duplicateId = await AddBoundaryAsync(2);
            await SelectBySearchAsync(duplicateId);
            var duplicateName = inspector.GetByLabel("Signal name", new LocatorGetByLabelOptions { Exact = true });
            await duplicateName.FillAsync("claims.recall");
            await duplicateName.PressAsync("Tab");
            var duplicateMessage =
                $"Signal boundary event #{duplicateId} uses signal 'claims.recall', which signal boundary event #{recallId} on host #3 already uses.";
            await Assertions.Expect(inspector.Locator($"#signal-name-status-{duplicateId}")).ToContainTextAsync(duplicateMessage);
            await page.Locator("#fileMenuSummary").ClickAsync();
            await page.Locator("#saveBtn").ClickAsync();
            Assert.Contains(duplicateMessage, await EditorInteractions.WaitForValidationDialogAsync(page), StringComparison.Ordinal);
            await EditorInteractions.CloseValidationDialogWithEscapeAsync(page);
            await duplicateName.FillAsync("claims.price-update");
            await duplicateName.PressAsync("Tab");
            await page.Locator("#svg").ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "signal-boundaries.png"),
            });

            using var saved = await EditorInteractions.SaveAndCaptureDownloadAsync(page, scenario, scenario.ArtifactDirectory);
            var savedBoundaries = saved.RootElement.GetProperty("flowNodes").EnumerateArray()
                .Where(node => node.GetProperty("type").GetString() == "signalBoundaryEvent")
                .ToDictionary(node => node.GetProperty("signal").GetProperty("name").GetString()!);
            Assert.Equal(["claims.price-update", "claims.recall"], savedBoundaries.Keys.Order());
            Assert.All(savedBoundaries.Values, node => Assert.Equal(3, node.GetProperty("attachedToRef").GetInt32()));
            Assert.False(savedBoundaries["claims.recall"].GetProperty("cancelActivity").GetBoolean());
            Assert.True(savedBoundaries["claims.price-update"].GetProperty("cancelActivity").GetBoolean());
            var savedCatch = saved.RootElement.GetProperty("flowNodes").EnumerateArray()
                .Single(node => node.GetProperty("id").GetInt32() == 2);
            Assert.Equal("intermediateSignalCatchEvent", savedCatch.GetProperty("type").GetString());
            var signal = savedCatch.GetProperty("signal");
            Assert.Equal("claims.policy-changed", signal.GetProperty("name").GetString());
            var mapping = Assert.Single(signal.GetProperty("outputMappings").EnumerateArray().ToArray());
            Assert.Equal("policyVersion", mapping.GetProperty("variable").GetString());
            Assert.Equal("version", mapping.GetProperty("path").GetString());
            Assert.Equal("number", mapping.GetProperty("dataType").GetString());
            Assert.False(mapping.GetProperty("isArray").GetBoolean());

            // A fresh page reloads the catch.
            var freshPage = await scenario.Context.NewPageAsync();
            try
            {
                await freshPage.GotoAsync($"{stack.EditorBaseAddress}/", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                await EditorInteractions.PinInspectorOpenAsync(freshPage);
                await EditorInteractions.LoadSavedFileAsync(freshPage, scenario.LastSavedFile!);
                await EditorInteractions.SelectNodeAsync(freshPage, 2);
                await Assertions.Expect(freshPage.Locator("#inspector").GetByLabel("Signal name", new LocatorGetByLabelOptions { Exact = true }))
                    .ToHaveValueAsync("claims.policy-changed");
                await Assertions.Expect(freshPage.Locator("#inspector").GetByLabel("Payload path", new LocatorGetByLabelOptions { Exact = true }))
                    .ToHaveValueAsync("version");
                await Assertions.Expect(freshPage.Locator($"#nodes .node[data-id='{recallId}']"))
                    .ToHaveClassAsync(new Regex(@"\bnon-interrupting\b"));
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
    public async Task R16_SignalsPageReviewsThrowsAndListsABroadcast(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r16-signals-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("signals-page", async () =>
        {
            // admin satisfies the default Signals.RequiredRole setting.
            await RuntimeSupport.ApplyIdentityAsync(scenario, "signal-operator", ["admin"]);
            var page = await scenario.OpenUiAsync("signals");
            var signalName = $"browser.r16-{viewportWidth}-{Guid.NewGuid():N}"[..40];
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Signals", Level = 1 }))
                .ToBeVisibleAsync();

            // The name is read on change; nothing waits for it yet.
            var name = page.Locator("#signal-name");
            await RuntimeSupport.ClickUntilAsync(
                async () =>
                {
                    await name.FillAsync(signalName);
                    await name.PressAsync("Tab");
                },
                async () => await page.GetByText($"No instance is waiting for '{signalName}' now.").CountAsync() == 1,
                TimeSpan.FromSeconds(20));
            await page.GetByLabel("All workflows", new PageGetByLabelOptions { Exact = true }).CheckAsync();
            await Assertions.Expect(page.Locator("#signal-workflow-key")).ToHaveCountAsync(0);

            // A payload that is not a JSON object is refused before any request.
            var payload = page.Locator("#signal-payload");
            await payload.FillAsync("[1, 2]");
            await payload.PressAsync("Tab");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(page.GetByText("The payload must be a JSON object")).ToBeVisibleAsync();

            // The review counts the waits; Back returns to the form.
            await payload.FillAsync("{ \"version\": 7 }");
            await payload.PressAsync("Tab");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            var review = page.Locator("section[aria-labelledby='signal-review-heading']");
            await Assertions.Expect(review).ToContainTextAsync("Waiting now0 waits");
            await Assertions.Expect(review).ToContainTextAsync("ScopeAll workflows");
            await review.ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "signals-review.png"),
            });

            // Throwing completes at once with no deliveries and opens the broadcast.
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
            var success = page.Locator(".alert-success");
            await Assertions.Expect(success).ToContainTextAsync(new Regex(@"Broadcast #\d+ found no waiting events; it completed with no deliveries\."));
            var broadcastId = Regex.Match(await success.InnerTextAsync(), @"#(\d+)").Groups[1].Value;
            var current = page.Locator("section[aria-labelledby='signal-current-heading']");
            await Assertions.Expect(current).ToContainTextAsync($"Broadcast #{broadcastId}");
            await Assertions.Expect(current).ToContainTextAsync("Completed");
            await Assertions.Expect(current).ToContainTextAsync("0 matching deliveries");
            await Assertions.Expect(current).ToContainTextAsync("Thrown bysignal-operator");
            await Assertions.Expect(current.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel pending deliveries" }))
                .ToHaveCountAsync(0);
            await current.Locator("summary", new LocatorLocatorOptions { HasText = "Payload" }).ClickAsync();
            await Assertions.Expect(current.Locator(".signal-json")).ToContainTextAsync("\"version\": 7");

            // The history lists the broadcast as the open one.
            var history = page.Locator("section[aria-labelledby='signal-history-heading']");
            var row = history.Locator("tr.table-active");
            await Assertions.Expect(row).ToContainTextAsync($"#{broadcastId}");
            await Assertions.Expect(row).ToContainTextAsync(signalName);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "signals-page.png"),
                FullPage = true,
            });
            Assert.True(
                await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
                $"The Signals page overflows horizontally at {viewportWidth}px.");

            // A family scope must name an existing workflow key; the API refuses an unknown one.
            await page.GetByLabel("One workflow family", new PageGetByLabelOptions { Exact = true }).CheckAsync();
            var workflowKey = page.Locator("#signal-workflow-key");
            await workflowKey.FillAsync("browser-r16-missing-family");
            await workflowKey.PressAsync("Tab");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-danger"))
                .ToContainTextAsync("No workflow definition has the key 'browser-r16-missing-family'.");

            // One instance: a running instance that is not waiting completes with no deliveries.
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-lifecycle.json");
            var instance = (await RuntimeSupport.StartInstanceAsync(stack, workflow.Id)).Id;
            await page.GetByLabel("One instance", new PageGetByLabelOptions { Exact = true }).CheckAsync();
            await Assertions.Expect(page.GetByText("Enter an instance ID, a positive whole number, to see how many waits match."))
                .ToBeVisibleAsync();
            var instanceId = page.Locator("#signal-instance-id");
            await instanceId.FillAsync(instance.ToString(CultureInfo.InvariantCulture));
            await instanceId.PressAsync("Tab");
            await Assertions.Expect(page.GetByText($"No wait for '{signalName}' in instance #{instance} now.")).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(review).ToContainTextAsync($"ScopeInstance #{instance}");
            await Assertions.Expect(review).ToContainTextAsync("Waiting now0 waits");
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
            await Assertions.Expect(success).ToContainTextAsync("found no waiting events");
            var instanceBroadcastId = Regex.Match(await success.InnerTextAsync(), @"#(\d+)").Groups[1].Value;
            Assert.NotEqual(broadcastId, instanceBroadcastId);
            await Assertions.Expect(current).ToContainTextAsync($"Instance #{instance} · {workflow.WorkflowKey}");
            await Assertions.Expect(current.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = $"Instance #{instance}", Exact = true }))
                .ToHaveAttributeAsync("href", $"instances/{instance}");
            await Assertions.Expect(history.Locator("tr.table-active")).ToContainTextAsync($"Instance #{instance} · {workflow.WorkflowKey}");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "signals-instance-scope.png"),
                FullPage = true,
            });
            Assert.True(
                await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
                $"The Signals page with an instance scope overflows horizontally at {viewportWidth}px.");

            // A business key narrows a family; editing it ends the review, and the API refuses a
            // business key for a family that has not enabled business keys.
            await page.GetByLabel("One workflow family", new PageGetByLabelOptions { Exact = true }).CheckAsync();
            await workflowKey.FillAsync(workflow.WorkflowKey);
            await workflowKey.PressAsync("Tab");
            var businessKey = page.Locator("#signal-business-key");
            await businessKey.FillAsync("ORD-R16");
            await businessKey.PressAsync("Tab");
            await Assertions.Expect(page.GetByText($"No wait for '{signalName}' with business key ORD-R16 in {workflow.WorkflowKey} now."))
                .ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(review).ToContainTextAsync($"Scope{workflow.WorkflowKey} · business key ORD-R16");
            await businessKey.FillAsync("ORD-R16-2");
            await businessKey.PressAsync("Tab");
            await Assertions.Expect(review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Review signal" })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(review).ToContainTextAsync("business key ORD-R16-2");
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-danger"))
                .ToContainTextAsync($"Workflow '{workflow.WorkflowKey}' has not enabled business keys");

            // The API refuses an instance ID that no instance has.
            await page.GetByLabel("One instance", new PageGetByLabelOptions { Exact = true }).CheckAsync();
            await instanceId.FillAsync("987654321987");
            await instanceId.PressAsync("Tab");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-danger")).ToContainTextAsync("No workflow instance has the ID 987654321987.");

            // A link opens the broadcast on a fresh page.
            page = await scenario.OpenUiAsync($"signals?broadcastId={broadcastId}");
            await Assertions.Expect(current).ToContainTextAsync($"Broadcast #{broadcastId}");
            await Assertions.Expect(current).ToContainTextAsync(signalName);

            // An identity without the signal role sees the permission screen and no signal data.
            await RuntimeSupport.ApplyIdentityAsync(scenario, "signal-reader", ["reviewer"]);
            page = await scenario.OpenUiAsync($"signals?broadcastId={broadcastId}");
            await Assertions.Expect(page.GetByText("Signal permission is required")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#signal-name")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByText(signalName)).ToHaveCountAsync(0);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "signals-permission.png"),
            });

            // Replacing the identity with a signal operator again loads the broadcast.
            await RuntimeSupport.ApplyIdentityAsync(scenario, "signal-operator", ["admin"]);
            page = await scenario.OpenUiAsync($"signals?broadcastId={broadcastId}");
            await Assertions.Expect(current).ToContainTextAsync($"Broadcast #{broadcastId}");
            await Assertions.Expect(history.Locator("tr.table-active")).ToContainTextAsync(signalName);
        });
    }
}
