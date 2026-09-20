using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// Automated runtime smoke matrix (R1-R3) against the real published API and
/// Blazor UI over localhost. HTTP setup uses explicit actor tokens minted
/// through the /token screen; the interactions under test happen in the
/// browser.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class RuntimeSmokeTests(BrowserStackFixture stack)
{
    private static readonly string[] AgentRoles = ["Agent"];

    // The instances list/search is gated by the WorkflowInstances.RequiredRole
    // engine setting (default admin), so the navigating actor carries it.
    private static readonly string[] WorkerRoles = ["User", "admin"];

    [Fact]
    public async Task R1_NormalTaskLifecycleFromInbox()
    {
        await using var scenario = await stack.CreateScenario("r1-normal-task-lifecycle");
        await scenario.RunAsync("normal-task-lifecycle", async () =>
        {
            // Setup through authenticated HTTP with the admin fixture client.
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-lifecycle.json");
            var instance = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);
            var taskId = await RuntimeSupport.SoleUserTaskIdAsync(stack.SetupClient, instance.Id);
            var detail = await stack.SetupClient.GetInstanceAsync(instance.Id);
            Assert.Equal("Review request", detail.CurrentNodeName);

            // Apply the authorized actor through the real token screen.
            var (alice, _) = await RuntimeSupport.ApplyIdentityAsync(scenario, "alice", AgentRoles);

            // Open the inbox and reach this exact task (setup task ID).
            await scenario.OpenUiAsync("inbox");
            var inboxPage = scenario.Page;
            await Assertions.Expect(inboxPage.Locator("#inbox-results-heading")).ToBeVisibleAsync();
            await RuntimeSupport.OpenFilterPanelAsync(inboxPage, "#inbox-filter-fields");
            await inboxPage.Locator("#inbox-instance-id").FillAsync(instance.Id.ToString());
            await inboxPage.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters" }).ClickAsync();
            var taskLink = inboxPage.Locator($"a.data-title[href='user-tasks/{taskId}']");
            await Assertions.Expect(taskLink).ToBeVisibleAsync(
                new LocatorAssertionsToBeVisibleOptions { Timeout = 20_000f });

            await taskLink.ClickAsync();
            await Assertions.Expect(inboxPage).ToHaveURLAsync(
                new System.Text.RegularExpressions.Regex($"/user-tasks/{taskId}$"));

            // Claim/action controls follow ownership: the required action is
            // hidden until the authorized actor claims the task.
            var claimButton = inboxPage.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Claim task" });
            await Assertions.Expect(claimButton).ToBeVisibleAsync();
            await Assertions.Expect(inboxPage.Locator("#task-actions-heading").Locator("xpath=ancestor::section"))
                .ToContainTextAsync("No actions available");
            await Assertions.Expect(
                    inboxPage.Locator(".action-card", new PageLocatorOptions { HasText = "Approve" }))
                .ToHaveCountAsync(0);

            await RuntimeSupport.ClickUntilAsync(
                async () => await claimButton.ClickAsync(),
                async () => await inboxPage
                    .GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Unclaim" })
                    .IsVisibleAsync(),
                TimeSpan.FromSeconds(30));
            await Assertions.Expect(inboxPage.Locator("#task-summary-heading").Locator("xpath=ancestor::section"))
                .ToContainTextAsync("alice");
            await Assertions.Expect(
                    inboxPage.Locator(".action-card", new PageLocatorOptions { HasText = "Approve" }))
                .ToBeVisibleAsync();

            // Enter the declared action variable and complete the action.
            var decisionInput = inboxPage.Locator($"#task-{taskId}-flow-102-decisionNote");
            await Assertions.Expect(decisionInput).ToBeVisibleAsync();
            await decisionInput.FillAsync("Approved by alice");
            await RuntimeSupport.ClickUntilAsync(
                async () => await inboxPage.Locator(".action-card", new PageLocatorOptions { HasText = "Approve" })
                    .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Take action" })
                    .ClickAsync(),
                async () => (inboxPage.Url ?? string.Empty)
                    .EndsWith($"instances/{instance.Id}", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30));
            await Assertions.Expect(inboxPage).ToHaveURLAsync(
                new System.Text.RegularExpressions.Regex($"instances/{instance.Id}$"));

            // Instance detail shows the resulting state, history, and value.
            await Assertions.Expect(
                inboxPage.Locator("#instance-summary-heading")).ToBeVisibleAsync();
            await Assertions.Expect(
                inboxPage.Locator("#instance-summary-heading").Locator("xpath=ancestor::section")
                    .Locator(".section-heading .status-badge")).ToContainTextAsync("Completed");
            await Assertions.Expect(inboxPage.Locator("#history")).ToContainTextAsync("alice");
            await Assertions.Expect(inboxPage.Locator("#history")).ToContainTextAsync("102");
            await Assertions.Expect(inboxPage.Locator("#variables")).ToContainTextAsync("decisionNote");
            await Assertions.Expect(inboxPage.Locator("#variables")).ToContainTextAsync("Approved by alice");

            // HTTP read-back: the action persisted exactly once and the task
            // left the inbox.
            var after = await alice.GetInstanceAsync(instance.Id);
            Assert.Equal("completed", after.Status);
            var noteVariable = after.Variables.Single(variable =>
                variable.VariableName == "decisionNote");
            Assert.Equal("Approved by alice", noteVariable.Value.GetString());
            Assert.Equal(102, noteVariable.SourceFlowId);
            Assert.Equal("alice", noteVariable.SetBy);
            var actionRows = after.History.Where(row => row.SequenceFlowId == 102).ToList();
            Assert.Single(actionRows);
            Assert.Equal("alice", actionRows[0].PerformedBy);
            var remainingInbox = await alice.GetInboxAsync(instance.Id);
            Assert.Empty(remainingInbox);
        });
    }

    [Fact]
    public async Task R2_InstanceNavigationAndTerminalStates()
    {
        await using var scenario = await stack.CreateScenario("r2-instance-navigation");
        await scenario.RunAsync("instance-navigation", async () =>
        {
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-navigation.json");

            var running = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);
            var terminal = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);
            await stack.SetupClient.TakeInstanceFlowAsync(terminal.Id, 102);
            await stack.SetupClient.TakeInstanceFlowAsync(terminal.Id, 103);
            var terminalAfter = await stack.SetupClient.GetInstanceAsync(terminal.Id);
            Assert.Equal("completed", terminalAfter.Status);
            var runningCheck = await stack.SetupClient.GetInstanceAsync(running.Id);
            Assert.Equal("running", runningCheck.Status);
            Assert.Equal("approval1", runningCheck.CurrentNodeName);

            await RuntimeSupport.ApplyIdentityAsync(scenario, "worker", WorkerRoles);

            // Open the running instance from the list.
            await scenario.OpenUiAsync("instances");
            var page = scenario.Page;
            await Assertions.Expect(page.Locator("#instance-results-heading")).ToBeVisibleAsync();
            await RuntimeSupport.OpenFilterPanelAsync(page, "#instance-filter-fields");
            await page.Locator("#instance-id-filter").FillAsync(running.Id.ToString());
            await page.Keyboard.PressAsync("Tab");
            var runningLink = page.Locator($"a.data-title[href='instances/{running.Id}']");
            var pausedNotice = page.GetByText("Apply the changed filters");
            await RuntimeSupport.ClickUntilAsync(
                async () => await page
                    .GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters" })
                    .ClickAsync(),
                async () => await runningLink.IsVisibleAsync() && !await pausedNotice.IsVisibleAsync(),
                TimeSpan.FromSeconds(30),
                async () =>
                {
                    var filtered = await stack.SetupClient.ListInstancesAsync(
                        status: "running", instanceId: running.Id);
                    var unfiltered = await stack.SetupClient.ListInstancesAsync();
                    return $"filtered(running,{running.Id})={filtered.TotalCount}; " +
                        $"all={unfiltered.TotalCount} " +
                        $"[{string.Join(", ", unfiltered.Items.Select(item => $"{item.Id}:{item.Status}"))}]";
                });

            await runningLink.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(
                new System.Text.RegularExpressions.Regex($"instances/{running.Id}$"));
            await Assertions.Expect(
                page.Locator("#instance-summary-heading")).ToBeVisibleAsync();
            await Assertions.Expect(
                page.Locator("#instance-summary-heading").Locator("xpath=ancestor::section")
                    .Locator(".section-heading .status-badge")).ToContainTextAsync("Running");
            var actionCard = page.Locator("#actions .card", new PageLocatorOptions { HasText = "approval" }).First;
            await Assertions.Expect(actionCard).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#variables")).ToContainTextAsync("No variables captured.");

            // Follow the real section links; targets scroll into view.
            foreach (var section in new[] { "variables", "history", "actions" })
            {
                await page.Locator($".status-tab[href='#{section}']").ClickAsync();
                await AssertScrolledIntoViewAsync(page, $"#{section}");
            }

            // Real in-app navigation must dispose the previous detail without
            // replacing the document/circuit or leaving its rows behind.
            var documentStart = await page.EvaluateAsync<double>("performance.timeOrigin");
            await RuntimeSupport.OpenInstanceFromListAsync(page, terminal.Id, "Completed");
            Assert.Equal(documentStart, await page.EvaluateAsync<double>("performance.timeOrigin"));
            await Assertions.Expect(page).ToHaveURLAsync(
                new System.Text.RegularExpressions.Regex($"instances/{terminal.Id}$"));
            await Assertions.Expect(
                page.Locator("#instance-summary-heading").Locator("xpath=ancestor::section")
                    .Locator(".section-heading .status-badge")).ToContainTextAsync("Completed");
            await Assertions.Expect(page.Locator("#actions")).ToContainTextAsync(
                "No user actions are available.");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 }))
                .ToContainTextAsync($"Instance #{terminal.Id}");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 }))
                .Not.ToContainTextAsync($"Instance #{running.Id}");
            await Assertions.Expect(
                    page.Locator("#actions .card", new PageLocatorOptions { HasText = "approval" }))
                .ToHaveCountAsync(0);

            // Navigate back to the list, then reopen the terminal instance from it.
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to instances" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("instances$"));

            var completedTab = page.Locator("button.status-tab", new PageLocatorOptions { HasText = "Completed" });
            await RuntimeSupport.ClickUntilAsync(
                async () => await completedTab.ClickAsync(),
                async () =>
                {
                    var tabClass = await completedTab.GetAttributeAsync("class") ?? string.Empty;
                    return tabClass.Contains("active", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(30));
            await RuntimeSupport.OpenFilterPanelAsync(page, "#instance-filter-fields");
            await page.Locator("#instance-id-filter").FillAsync(terminal.Id.ToString());
            await page.Keyboard.PressAsync("Tab");
            var terminalLink = page.Locator($"a.data-title[href='instances/{terminal.Id}']");
            var terminalPausedNotice = page.GetByText("Apply the changed filters");
            await RuntimeSupport.ClickUntilAsync(
                async () => await page
                    .GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters" })
                    .ClickAsync(),
                async () => await terminalLink.IsVisibleAsync() && !await terminalPausedNotice.IsVisibleAsync(),
                TimeSpan.FromSeconds(30));
            await terminalLink.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(
                new System.Text.RegularExpressions.Regex($"instances/{terminal.Id}$"));
            await Assertions.Expect(
                page.Locator("#instance-summary-heading")).ToBeVisibleAsync();
            await Assertions.Expect(
                page.Locator("#instance-summary-heading").Locator("xpath=ancestor::section")
                    .Locator(".section-heading .status-badge")).ToContainTextAsync("Completed");
            await Assertions.Expect(page.Locator("body")).ToContainTextAsync("Completed normally");
            await Assertions.Expect(page.Locator("#actions")).ToContainTextAsync(
                "No user actions are available.");

            // Route changes do not leave prior-instance rows behind: the
            // completed instance only appears under the Completed status tab.
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to instances" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("instances$"));
            var terminalRow = page.Locator("tbody tr").Filter(new LocatorFilterOptions
            {
                Has = page.Locator($"a.data-title[href='instances/{terminal.Id}']"),
            });
            await Assertions.Expect(terminalRow).ToHaveCountAsync(0);
            var completedStatusTab = page.Locator("button.status-tab", new PageLocatorOptions { HasText = "Completed" });
            await RuntimeSupport.ClickUntilAsync(
                async () => await completedStatusTab.ClickAsync(),
                async () => await terminalRow.Locator("td").Nth(3).Locator(".status-badge").IsVisibleAsync(),
                TimeSpan.FromSeconds(30));
            await Assertions.Expect(terminalRow.Locator("td").Nth(3).Locator(".status-badge")).ToContainTextAsync("Completed");
        });
    }

    [Fact]
    public async Task R3_IdentityReplacementUpdatesProtectedActions()
    {
        await using var scenario = await stack.CreateScenario("r3-identity-replacement");
        await scenario.RunAsync("identity-replacement", async () =>
        {
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-lifecycle.json");
            var instance = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);

            var (alice, _) = await RuntimeSupport.ApplyIdentityAsync(scenario, "alice", AgentRoles);

            // Alice claims the claim-required task; actions become visible.
            var page = scenario.Page;
            await scenario.OpenUiAsync($"instances/{instance.Id}");
            var claimAlice = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Claim as alice" });
            var approveCard = page.Locator("#actions .card", new PageLocatorOptions { HasText = "Approve" });
            await RuntimeSupport.ClickUntilAsync(
                async () => await claimAlice.ClickAsync(),
                async () => await approveCard.IsVisibleAsync(),
                TimeSpan.FromSeconds(30));

            // A second page in the same UI process changes the identity to Bob,
            // who has no task authority. The open detail page refreshes.
            var secondPage = await scenario.Context.NewPageAsync();
            try
            {
                var bobIdentity = new IdentityScreen(secondPage, stack.UiBaseAddress);
                await bobIdentity.GenerateAndApplyIdentityAsync("bob", []);

                await Assertions.Expect(page.Locator(".summary-item", new PageLocatorOptions
                {
                    HasText = "Acting as",
                })).ToContainTextAsync("bob", new LocatorAssertionsToContainTextOptions { Timeout = 20_000f });
                await Assertions.Expect(
                    page.Locator("#actions .card", new PageLocatorOptions { HasText = "Approve" }))
                    .ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator("#actions")).ToContainTextAsync(
                    "No user actions are available.");

                // Refreshing the page does not revive A's actions for Bob.
                await page.ReloadAsync();
                await Assertions.Expect(
                    page.Locator(".summary-item", new PageLocatorOptions { HasText = "Acting as" }))
                    .ToContainTextAsync("bob");
                await Assertions.Expect(
                    page.Locator("#actions .card", new PageLocatorOptions { HasText = "Approve" }))
                    .ToHaveCountAsync(0);

                // Clearing the identity leaves protected operations unavailable.
                await bobIdentity.ClearIdentityAsync();
                await Assertions.Expect(page.Locator("#actions")).Not.ToContainTextAsync(
                    "Approve",
                    new LocatorAssertionsToContainTextOptions { Timeout = 20_000f });

                // Re-applying Alice through the second page restores legitimate
                // controls: the earlier claim persisted, so actions recover
                // without a new claim and the first page stays on its route.
                await bobIdentity.GenerateAndApplyIdentityAsync("alice", AgentRoles);
                await Assertions.Expect(
                    page.Locator("#actions .card", new PageLocatorOptions { HasText = "Approve" }))
                    .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000f });
                await Assertions.Expect(
                    page.Locator("#instance-summary-heading").Locator("xpath=ancestor::section"))
                    .ToContainTextAsync("Claimed by alice");
            }
            finally
            {
                await secondPage.CloseAsync();
            }
        });
    }

    internal static async Task AssertScrolledIntoViewAsync(IPage page, string selector)
    {
        var viewport = page.ViewportSize ?? throw new InvalidOperationException("No viewport size.");
        var section = page.Locator(selector);
        await Assertions.Expect(section).ToBeVisibleAsync();
        // Smooth scrolling needs a beat before the section settles in view.
        await Assertions.Expect(section).ToBeInViewportAsync(
            new LocatorAssertionsToBeInViewportOptions { Timeout = 5_000f });
    }
}







