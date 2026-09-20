using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// Instance-detail smoke matrix (R4-R6): gateway/complex and multi-instance
/// rendering with section navigation, genuine five-second polling with an
/// out-of-band HTTP completion, and responsive control coverage at the two
/// narrower declared widths.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class InstanceDetailSmokeTests(BrowserStackFixture stack)
{
    private static readonly string[] ReviewerRoles = ["Reviewer"];

    // The instances list/search is gated by the WorkflowInstances.RequiredRole
    // engine setting (default admin), so the navigating actor carries it.
    private static readonly string[] WorkerRoles = ["User", "admin"];

    [Fact]
    public async Task R4_GatewayComplexAndMultiInstanceDetailSections()
    {
        await using var scenario = await stack.CreateScenario("r4-detail-sections");
        await scenario.RunAsync("gateway-and-mi-sections", async () =>
        {
            // ---- Gateway fixture: drive both branches over HTTP. ----
            var gatewayWorkflow = await RuntimeSupport.PublishAsync(stack, "runtime-gateway.json");
            var gatewayInstance = await RuntimeSupport.StartInstanceAsync(stack, gatewayWorkflow.Id);
            var branchDriver = await CreateActorClientAsync(scenario, "branch-driver", ReviewerRoles);
            var branchInbox = await branchDriver.GetInboxAsync(gatewayInstance.Id);
            Assert.Equal(2, branchInbox.Count);
            foreach (var item in branchInbox)
            {
                var flows = await branchDriver.GetUserTaskFlowsAsync(item.UserTaskId);
                var single = Assert.Single(flows);
                await branchDriver.TakeUserTaskFlowAsync(item.UserTaskId, single.Id);
            }

            var gatewayAfter = await stack.SetupClient.GetInstanceAsync(gatewayInstance.Id);
            Assert.Equal("Finalize", gatewayAfter.CurrentNodeName);

            // ---- MI fixture: complete two of three children. ----
            var miWorkflow = await RuntimeSupport.PublishAsync(stack, "runtime-mi.json");
            var miInstance = await RuntimeSupport.StartInstanceAsync(stack, miWorkflow.Id);
            var beta = await CreateActorClientAsync(scenario, "beta", ReviewerRoles);
            var gamma = await CreateActorClientAsync(scenario, "gamma", ReviewerRoles);
            await CompleteMultiInstanceItemAsync(beta, miInstance.Id, itemIndex: 1, "Reviewed by beta");
            await CompleteMultiInstanceItemAsync(gamma, miInstance.Id, itemIndex: 2, "Reviewed by gamma");
            var miAfter = await stack.SetupClient.GetInstanceAsync(miInstance.Id);
            Assert.Equal(2, miAfter.MultiInstances.Single().Completed);
            Assert.Equal(3, miAfter.MultiInstances.Single().Total);

            // ---- Browser: quorum actor opens the gateway detail. ----
            await RuntimeSupport.ApplyIdentityAsync(scenario, "quorum", ReviewerRoles);
            await scenario.OpenUiAsync($"instances/{gatewayInstance.Id}");
            var page = scenario.Page;

            var gatewaySection = page.Locator("#gateway-scopes");
            await Assertions.Expect(gatewaySection).ToBeVisibleAsync();
            var gatewayRows = gatewaySection.Locator("tbody tr");
            // Ordered by execution id (newest first): the merge row leads.
            await Assertions.Expect(gatewayRows.First).ToContainTextAsync("Merge after both (#5)");
            await Assertions.Expect(gatewaySection).ToContainTextAsync("Fork reviews (#2)");
            await Assertions.Expect(gatewaySection).ToContainTextAsync("parallelGateway");
            await Assertions.Expect(gatewaySection).ToContainTextAsync("complexGateway");
            await Assertions.Expect(gatewaySection).ToContainTextAsync("2 total");
            foreach (var execution in gatewayAfter.GatewayExecutions)
            {
                var row = gatewayRows.Filter(new LocatorFilterOptions { HasText = $"(#{execution.GatewayNodeId})" });
                await Assertions.Expect(row.Locator("td").Nth(2)).ToHaveTextAsync(
                    char.ToUpperInvariant(execution.Status[0]) + execution.Status[1..]);
            }

            var complexSection = page.Locator("#complex-gateway-states");
            await Assertions.Expect(complexSection).ToBeVisibleAsync();
            await Assertions.Expect(complexSection).ToContainTextAsync("Merge after both (#5)");
            await Assertions.Expect(complexSection).ToContainTextAsync("#301, #401");
            var state = gatewayAfter.ComplexGatewayStates.Single();
            await Assertions.Expect(complexSection.Locator("tbody tr")).ToHaveCountAsync(1);
            var stateCells = complexSection.Locator("tbody tr td");
            await Assertions.Expect(stateCells.Nth(1)).ToHaveTextAsync(char.ToUpperInvariant(state.Phase[0]) + state.Phase[1..]);
            await Assertions.Expect(stateCells.Nth(2)).ToHaveTextAsync(state.Cycle.ToString());

            // Section links scroll to the intended section (asserted by scroll
            // position, not a URL hash).
            var gatewayTab = page.Locator(".status-tab[href='#gateway-scopes']");
            await RuntimeSupport.ClickUntilAsync(
                async () => await gatewayTab.ClickAsync(),
                async () => await IsSectionInViewportAsync(page, "#gateway-scopes"),
                TimeSpan.FromSeconds(30));
            await InstanceDetailSmokeTests.AssertScrolledIntoViewAsync(page, "#gateway-scopes");
            await page.Locator(".status-tab[href='#complex-gateway-states']").ClickAsync();
            await InstanceDetailSmokeTests.AssertScrolledIntoViewAsync(page, "#complex-gateway-states");

            // ---- MI detail with submitted result JSON. ----
            await scenario.OpenUiAsync($"instances/{miInstance.Id}");
            await Assertions.Expect(
                page.Locator(".summary-item", new PageLocatorOptions { HasText = "Multi-instance" }))
                .ToContainTextAsync("2 / 3 completed");
            var miSection = page.Locator("#multi-instance-results");
            await Assertions.Expect(miSection).ToBeVisibleAsync();
            await Assertions.Expect(miSection).ToContainTextAsync("beta");
            await Assertions.Expect(miSection).ToContainTextAsync("Reviewed by beta");
            await Assertions.Expect(miSection).ToContainTextAsync("gamma");
            await Assertions.Expect(miSection).ToContainTextAsync("Reviewed by gamma");
            await Assertions.Expect(miSection).ToContainTextAsync("Complete review (#201)");
            var miRows = miSection.Locator("tbody tr");
            await Assertions.Expect(miRows).ToHaveCountAsync(2);
            // Rows render newest first: gamma (item 3) before beta (item 2).
            await Assertions.Expect(miRows.Nth(0)).ToContainTextAsync("gamma");
            await Assertions.Expect(miRows.Nth(1)).ToContainTextAsync("beta");

            await page.Locator(".status-tab[href='#multi-instance-results']").ClickAsync();
            await InstanceDetailSmokeTests.AssertScrolledIntoViewAsync(page, "#multi-instance-results");

            // Cross-check HTTP: gateway rows and complex state values match.
            var gatewayDetail = await stack.SetupClient.GetInstanceAsync(gatewayInstance.Id);
            Assert.Contains(
                gatewayDetail.GatewayExecutions,
                execution => execution.GatewayNodeId == 2 && execution.Direction == "split");
            Assert.Contains(
                gatewayDetail.GatewayExecutions,
                execution => execution.GatewayNodeId == 5 && execution.Direction == "merge");
            var complexState = gatewayDetail.ComplexGatewayStates.Single();
            Assert.Equal(5, complexState.GatewayNodeId);
            Assert.Equal(1, complexState.Cycle);
        });
    }

    [Fact]
    public async Task R5_RealPollingUpdatesProgressWithoutManualReload()
    {
        await using var scenario = await stack.CreateScenario("r5-real-polling");
        var uiLogBaseline = stack.UiOutputLines.Count;
        var apiLogBaseline = stack.ApiOutputLines.Count;
        await scenario.RunAsync("polling-and-disposal", async () =>
        {
            var miWorkflow = await RuntimeSupport.PublishAsync(stack, "runtime-mi.json");
            var miInstance = await RuntimeSupport.StartInstanceAsync(stack, miWorkflow.Id);

            // Alpha (viewer) and beta (actor) tokens both come from the real
            // token screen before the detail page is opened.
            var beta = await CreateActorClientAsync(scenario, "beta", ReviewerRoles);
            var (alpha, _) = await RuntimeSupport.ApplyIdentityAsync(scenario, "alpha", ReviewerRoles);

            await scenario.OpenUiAsync($"instances/{miInstance.Id}");
            var page = scenario.Page;
            await Assertions.Expect(
                page.Locator(".summary-item", new PageLocatorOptions { HasText = "Multi-instance" }))
                .ToContainTextAsync("0 / 3 completed");

            // Beta completes one child through a separately authenticated HTTP
            // client; the open page must pick this up via its five-second poll.
            await CompleteMultiInstanceItemAsync(beta, miInstance.Id, itemIndex: 1, "Reviewed by beta");
            var betaAck = await beta.GetInstanceAsync(miInstance.Id);
            Assert.Equal(1, betaAck.MultiInstances.Single().Completed);

            var summary = page.Locator(".summary-item", new PageLocatorOptions { HasText = "Multi-instance" });
            await Assertions.Expect(summary).ToContainTextAsync(
                "1 / 3 completed",
                new LocatorAssertionsToContainTextOptions
                {
                    Timeout = (float)stack.PollingObservationBudget.TotalMilliseconds,
                });

            // Navigate away and return; current data is shown without a stale
            // prior display.
            var documentStart = await page.EvaluateAsync<double>("performance.timeOrigin");
            await page.Locator("#primary-navigation").GetByRole(AriaRole.Link,
                new LocatorGetByRoleOptions { Name = "My work", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("#inbox-results-heading")).ToBeVisibleAsync();
            await page.GoBackAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"instances/{miInstance.Id}$"));
            Assert.Equal(documentStart, await page.EvaluateAsync<double>("performance.timeOrigin"));
            await Assertions.Expect(
                page.Locator(".summary-item", new PageLocatorOptions { HasText = "Multi-instance" }))
                .ToContainTextAsync("1 / 3 completed");

            // Host logs contain no new error-level circuit or disposal issues.
            // Routine INFO-level circuit teardown (POST /_blazor/disconnect)
            // is normal navigation and is not a failure.
            var uiNew = stack.UiOutputLines.Skip(uiLogBaseline).ToList();
            var apiNew = stack.ApiOutputLines.Skip(apiLogBaseline).ToList();
            var disposalPattern = new Regex(
                @"\b(ERR|FTL)\]|unhandled exception|error during|circuit error",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var uiIssues = uiNew.Where(line => disposalPattern.IsMatch(line)).ToList();
            var apiIssues = apiNew.Where(line => disposalPattern.IsMatch(line)).ToList();
            Assert.True(
                uiIssues.Count == 0,
                "UI host logs recorded new circuit/disposal issues:\n" + string.Join("\n", uiIssues));
            Assert.True(
                apiIssues.Count == 0,
                "API host logs recorded new issues:\n" + string.Join("\n", apiIssues));
        });
    }

    [Theory]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task R6_ResponsiveControlsAndFocus(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r6-responsive-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("responsive-controls", async () =>
        {
            var navigationWorkflow = await RuntimeSupport.PublishAsync(stack, "runtime-navigation.json");
            var instance = await RuntimeSupport.StartInstanceAsync(stack, navigationWorkflow.Id);

            var gatewayWorkflow = await RuntimeSupport.PublishAsync(stack, "runtime-gateway.json");
            var gatewayInstance = await RuntimeSupport.StartInstanceAsync(stack, gatewayWorkflow.Id);

            var miWorkflow = await RuntimeSupport.PublishAsync(stack, "runtime-mi.json");
            var miInstance = await RuntimeSupport.StartInstanceAsync(stack, miWorkflow.Id);

            var beta = await CreateActorClientAsync(scenario, "beta", ReviewerRoles);
            await CompleteMultiInstanceItemAsync(beta, miInstance.Id, 1, "Responsive result: <review> & approved");
            await RuntimeSupport.ApplyIdentityAsync(scenario, "worker", WorkerRoles);
            var page = scenario.Page;

            // Navigate using the actual responsive navigation.
            await scenario.OpenUiAsync("");
            var mobileMenu = page.Locator(".mobile-menu");
            if (viewportWidth <= 991)
            {
                await Assertions.Expect(mobileMenu).ToBeVisibleAsync();
                await RuntimeSupport.ClickUntilAsync(
                    async () => await mobileMenu.ClickAsync(),
                    async () => await page.EvaluateAsync<bool>(
                        "document.getElementById('primary-navigation').classList.contains('is-open')"),
                    TimeSpan.FromSeconds(30));
            }
            await page.Locator("#primary-navigation")
                .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Instances", Exact = true })
                .ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("instances$"));
            if (viewportWidth <= 991)
            {
                await Assertions.Expect(mobileMenu).ToHaveAttributeAsync("aria-expanded", "false");
            }

            // Normal detail remains usable: headings, section targets, links.
            await scenario.OpenUiAsync($"instances/{instance.Id}?source=browser-smoke");
            await InstanceDetailSmokeTests.AssertScrolledIntoViewAfterClickAsync(page, "#variables");
            await InstanceDetailSmokeTests.AssertScrolledIntoViewAfterClickAsync(page, "#history");
            var sectionLink = page.Locator(".status-tab[href='#variables']");
            await sectionLink.FocusAsync();
            var focusedClass = await page.EvaluateAsync<string?>(
                "document.activeElement ? (document.activeElement.className || '') : ''");
            Assert.Contains("status-tab", focusedClass ?? string.Empty, StringComparison.Ordinal);
            await page.Keyboard.PressAsync("Tab");
            var focusedTag = await page.EvaluateAsync<string?>(
                "document.activeElement && document.activeElement.tagName");
            Assert.Contains(
                focusedTag,
                ["A", "BUTTON", "INPUT", "SELECT", "TEXTAREA", "SUMMARY"],
                StringComparer.OrdinalIgnoreCase);

            // Gateway detail renders and its table is horizontally contained:
            // the responsive container may scroll internally, but the document
            // itself must not overflow horizontally.
            await scenario.OpenUiAsync($"instances/{gatewayInstance.Id}");
            var gatewayTable = page.Locator("#gateway-scopes .table-responsive").First;
            await Assertions.Expect(gatewayTable).ToBeVisibleAsync();
            await AssertTableCanScrollAsync(page, gatewayTable);
            var contained = await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2");
            Assert.True(
                contained,
                $"The gateway page overflows horizontally at {viewportWidth}px.");

            // Submitted JSON must remain readable and reachable at narrow widths.
            await scenario.OpenUiAsync($"instances/{miInstance.Id}");
            await Assertions.Expect(page.Locator("#multi-instance-results")).ToBeVisibleAsync();
            var submittedJson = page.Locator("#multi-instance-results code").First;
            using var submitted = JsonDocument.Parse(await submittedJson.InnerTextAsync());
            Assert.Equal("Responsive result: <review> & approved",
                submitted.RootElement.GetProperty("reviewComment").GetString());
            await AssertTableCanScrollAsync(page, page.Locator("#multi-instance-results .table-responsive").First);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "responsive-results.png"), FullPage = true
            });
        });
    }

    /// <summary>True when the section's top edge is inside the viewport.</summary>
    internal static async Task<bool> IsSectionInViewportAsync(IPage page, string selector)
    {
        var height = page.ViewportSize?.Height ?? 0;
        var box = await page.Locator(selector).BoundingBoxAsync();
        return box is not null && box.Y >= 0 && box.Y < height;
    }

    internal static async Task AssertScrolledIntoViewAfterClickAsync(IPage page, string selector)
    {
        var tab = page.Locator($".status-tab[href='#{selector.TrimStart('#')}']");
        await RuntimeSupport.WaitUntilInteractiveAsync(page);
        var url = page.Url;
        await tab.ClickAsync();
        await AssertScrolledIntoViewAsync(page, selector);
        Assert.Equal(new Uri(url).GetLeftPart(UriPartial.Query), new Uri(page.Url).GetLeftPart(UriPartial.Query));
        await Assertions.Expect(page).ToHaveURLAsync(new Regex($"{Regex.Escape(selector)}$"));
    }

    private static async Task AssertTableCanScrollAsync(IPage page, ILocator container)
    {
        await container.ScrollIntoViewIfNeededAsync();
        if (await container.EvaluateAsync<bool>("el => el.scrollWidth > el.clientWidth + 2"))
        {
            await container.HoverAsync();
            await page.Mouse.WheelAsync(10_000, 0);
            await Assertions.Expect(container.Locator("th").Last).ToBeInViewportAsync();
            var moved = await container.EvaluateAsync<bool>("el => el.scrollLeft > 0");
            Assert.True(moved, "Horizontal wheel input did not reveal the clipped columns.");
        }
        Assert.True(await container.EvaluateAsync<bool>("""
            el => el.querySelector('tr').lastElementChild.getBoundingClientRect().right
                <= el.getBoundingClientRect().right + 2
            """), "The final table column is clipped after horizontal scrolling.");
    }

    internal static async Task AssertScrolledIntoViewAsync(IPage page, string selector)
    {
        var section = page.Locator(selector);
        await Assertions.Expect(section).ToBeVisibleAsync();
        // Smooth scrolling settles after a short animation.
        await Assertions.Expect(section).ToBeInViewportAsync(
            new LocatorAssertionsToBeInViewportOptions { Timeout = 5_000f });
    }

    private static async Task<WorkflowFixtureClient> CreateActorClientAsync(
        BrowserScenario scenario, string user, string[] roles)
    {
        // Mint the actor's token through the real token screen on an auxiliary
        // page. The caller applies the final scenario identity afterwards.
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

    private static async Task CompleteMultiInstanceItemAsync(
        WorkflowFixtureClient actor, long instanceId, int itemIndex, string comment)
    {
        var taskId = await actor.FindInboxTaskIdByItemIndexAsync(instanceId, itemIndex);
        await actor.TakeUserTaskFlowAsync(taskId, 201, new()
        {
            ["reviewComment"] = JsonSerializer.SerializeToElement(comment),
        });
    }
}

