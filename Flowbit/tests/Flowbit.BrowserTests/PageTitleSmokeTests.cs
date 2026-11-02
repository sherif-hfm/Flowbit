using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// Browser tab titles (R12): each page's title reaches the tab after a link click
/// and after Back or Forward, including between two instances, and neither a full
/// load nor a page change blanks the title or leaves a second title element.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class PageTitleSmokeTests(BrowserStackFixture stack)
{
    // Instances, settings, and retention all need the administrator role.
    private static readonly string[] ReaderRoles = ["User", "admin"];

    // The title each sidebar link's page sets. The test fails on a sidebar link
    // missing here, so a new page has to state its title.
    private static readonly Dictionary<string, string> SidebarTitles = new()
    {
        [""] = "Overview · Flowbit",
        ["inbox"] = "My Work · Flowbit",
        ["delegations"] = "My Delegations · Flowbit",
        ["instances"] = "Instances · Flowbit",
        ["activity"] = "Activity · Flowbit",
        ["operations"] = "Jobs & incidents · Flowbit",
        ["task-management"] = "Task Management · Flowbit",
        ["delegation-management"] = "User Delegations · Flowbit",
        ["workflows"] = "Workflows · Flowbit",
        ["workflows/new"] = "Workflow editor · Flowbit",
        ["instance-version-changes"] = "Batch instance version changes · Flowbit",
        ["instance-variable-updates"] = "Instance variable updates - Flowbit",
        ["administrative-actions"] = "Administrative actions · Flowbit",
        ["engine-settings"] = "Engine Settings · Flowbit",
        ["ai-models"] = "AI Models · Flowbit",
        ["business-calendars"] = "Business Calendars · Flowbit",
        ["retention"] = "Retention policies · Flowbit",
        ["workflow-settings"] = "Workflow Settings · Flowbit",
        ["shared-variables"] = "Shared Variables · Flowbit",
        ["shared-variable-clients"] = "Shared Variable API Clients · Flowbit",
        ["task-distribution"] = "Task Distribution Search · Flowbit",
        ["token"] = "Test Identity · Flowbit",
    };

    // Logs each change of the document's title, its number of title elements in
    // the head, and how many different title elements it has had. A mutation
    // callback runs after the task that changed the head, so a change undone
    // inside one task, which nobody can see, is not logged.
    private const string TitleRecorder =
        """
        (() => {
          const log = [];
          let current = null;
          let elements = 0;
          let last = null;
          const record = () => {
            const titles = document.head ? document.head.getElementsByTagName("title") : [];
            if (titles.length > 0 && titles[0] !== current) {
              current = titles[0];
              elements++;
            }
            const entry = JSON.stringify([document.title, titles.length, elements]);
            if (entry === last) return;
            last = entry;
            log.push({ at: Math.round(performance.now() * 10) / 10, title: document.title, count: titles.length, elements });
          };
          window.flowbitTitleLog = log;
          new MutationObserver(record).observe(document, { childList: true, subtree: true, characterData: true });
          record();
        })();
        """;

    [Fact]
    public async Task R12_TabTitleFollowsInAppNavigation()
    {
        await using var scenario = await stack.CreateScenario("r12-tab-titles");
        await scenario.RunAsync("tab-titles", async () =>
        {
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-navigation.json");
            var first = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);
            var second = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);
            var taskId = await RuntimeSupport.SoleUserTaskIdAsync(stack.SetupClient, first.Id);
            var firstTitle = $"Instance #{first.Id} · Flowbit";
            var secondTitle = $"Instance #{second.Id} · Flowbit";

            await RuntimeSupport.ApplyIdentityAsync(scenario, "title-reader", ReaderRoles);
            await scenario.Context.AddInitScriptAsync(TitleRecorder);

            // A full load shows the title once and keeps it while the page turns interactive.
            var page = await scenario.OpenUiAsync($"instances/{first.Id}");
            await AssertFullLoadKeptTitleAsync(scenario, firstTitle, "instance");
            var documentStart = await page.EvaluateAsync<double>("performance.timeOrigin");
            var firstHistoryLength = await page.EvaluateAsync<int>("history.length");

            // A link to a list, then a link to another instance.
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to instances" }).ClickAsync();
            await Assertions.Expect(page).ToHaveTitleAsync("Instances · Flowbit");
            await page.Locator($"a.data-title[href='instances/{second.Id}']").ClickAsync();
            await Assertions.Expect(page).ToHaveTitleAsync(secondTitle);

            // History can go straight from one instance to the other, which changes
            // only the route parameter.
            var hops = await page.EvaluateAsync<int>("history.length") - firstHistoryLength;
            await page.EvaluateAsync("steps => history.go(steps)", -hops);
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"instances/{first.Id}$"));
            await Assertions.Expect(page).ToHaveTitleAsync(firstTitle);
            await page.EvaluateAsync("steps => history.go(steps)", hops);
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"instances/{second.Id}$"));
            await Assertions.Expect(page).ToHaveTitleAsync(secondTitle);

            // A task reached from My work, and the instance reached from the task.
            await ClickSidebarLinkAsync(page, "inbox");
            await Assertions.Expect(page).ToHaveTitleAsync("My Work · Flowbit");
            await page.Locator($"a.data-title[href='user-tasks/{taskId}']").ClickAsync();
            await Assertions.Expect(page).ToHaveTitleAsync($"User Task #{taskId} · Flowbit");
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "View instance" }).ClickAsync();
            await Assertions.Expect(page).ToHaveTitleAsync(firstTitle);

            // Every sidebar link.
            var hrefs = await page.Locator(".nav-groups a.nav-link")
                .EvaluateAllAsync<string[]>("links => links.map(link => link.getAttribute('href'))");
            Assert.Equal(SidebarTitles.Keys.Order(), hrefs.Order());
            foreach (var href in hrefs)
            {
                await ClickSidebarLinkAsync(page, href);
                await Assertions.Expect(page).ToHaveTitleAsync(SidebarTitles[href]);
                if (href == "workflows/new")
                {
                    // A new workflow counts as unsaved, so the next link asks first.
                    await Assertions.Expect(page.Locator(".workflow-editor-page")).ToHaveAttributeAsync("data-editor-ready", "true");
                    scenario.ExpectDialogOnce("confirm", "Leave the editor? Unsaved changes will be lost.", accept: true);
                }
            }

            // The two detail pages that only a list links to.
            await ClickSidebarLinkAsync(page, "");
            await page.Locator($"a.quick-item[href='workflows/{workflow.Id}/start']").ClickAsync();
            await Assertions.Expect(page).ToHaveTitleAsync("Start Workflow · Flowbit");
            await ClickSidebarLinkAsync(page, "activity");
            var execution = page.Locator("a.mono-id[href^='node-executions/']").First;
            var executionId = (await execution.GetAttributeAsync("href"))!.Split('/')[^1];
            await execution.ClickAsync();
            await Assertions.Expect(page).ToHaveTitleAsync($"Node execution #{executionId} · Flowbit");

            // All of that stayed in one document.
            Assert.Equal(documentStart, await page.EvaluateAsync<double>("performance.timeOrigin"));
            await AssertNoBlankOrSecondTitleAsync(scenario, "navigation");

            // Another full load, of a page that lists data.
            await scenario.OpenUiAsync("");
            await AssertFullLoadKeptTitleAsync(scenario, "Overview · Flowbit", "overview");

            // The reconnect dialog can resume a circuit, which restores both interactive roots.
            Assert.True(await page.EvaluateAsync<bool>("Blazor.pauseCircuit()"));
            Assert.True(await page.EvaluateAsync<bool>("Blazor.resumeCircuit()"));
            await ClickSidebarLinkAsync(page, "activity");
            await Assertions.Expect(page).ToHaveTitleAsync("Activity · Flowbit");
            await AssertNoBlankOrSecondTitleAsync(scenario, "resume");
        });
    }

    [Fact]
    public async Task R12_LinkClickedBeforeTheCircuitConnectsSetsTheTitle()
    {
        await using var scenario = await stack.CreateScenario("r12-tab-titles-before-circuit");
        await scenario.RunAsync("tab-titles-before-circuit", async () =>
        {
            await RuntimeSupport.ApplyIdentityAsync(scenario, "title-reader", ReaderRoles);

            // Holding the circuit's negotiate request keeps the page prerendered, so
            // Blazor's enhanced navigation handles the link instead of the live router.
            var page = scenario.Page;
            var circuitReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteAsync("**/_blazor/negotiate**", async route =>
            {
                await circuitReleased.Task;
                await route.ContinueAsync();
            });
            await page.GotoAsync("workflows", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
            await Assertions.Expect(page.Locator(".app-shell")).ToHaveAttributeAsync("data-interactive", "false");
            await Assertions.Expect(page).ToHaveTitleAsync("Workflows · Flowbit");
            var documentStart = await page.EvaluateAsync<double>("performance.timeOrigin");

            await ClickSidebarLinkAsync(page, "activity");
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("activity$"));
            await Assertions.Expect(page).ToHaveTitleAsync("Activity · Flowbit");
            await Assertions.Expect(page.Locator(".app-shell")).ToHaveAttributeAsync("data-interactive", "false");
            await AssertOneTitleElementAsync(page);

            // Once the circuit connects, the live router takes over the same document.
            circuitReleased.SetResult();
            await RuntimeSupport.WaitUntilInteractiveAsync(page);
            await Assertions.Expect(page).ToHaveTitleAsync("Activity · Flowbit");
            await ClickSidebarLinkAsync(page, "operations");
            await Assertions.Expect(page).ToHaveTitleAsync("Jobs & incidents · Flowbit");
            await AssertOneTitleElementAsync(page);
            Assert.Equal(documentStart, await page.EvaluateAsync<double>("performance.timeOrigin"));
        });
    }

    private static Task ClickSidebarLinkAsync(IPage page, string href) =>
        page.Locator($".nav-groups a.nav-link[href='{href}']").ClickAsync();

    // Only the head: the diagram's SVG nodes have title elements of their own.
    private static async Task AssertOneTitleElementAsync(IPage page) =>
        Assert.Equal(1, await page.EvaluateAsync<int>("document.head.getElementsByTagName('title').length"));

    // The head outlet replaces the prerendered title element when it turns
    // interactive. From the first title on, the log may hold only this title, in
    // one element; anything else means the tab went blank or had two titles.
    private static async Task AssertFullLoadKeptTitleAsync(BrowserScenario scenario, string title, string name)
    {
        await Assertions.Expect(scenario.Page).ToHaveTitleAsync(title);
        await scenario.Page.WaitForFunctionAsync("window.flowbitTitleLog.at(-1).elements > 1");
        var shown = await ReadShownTitlesAsync(scenario, name);
        Assert.NotEmpty(shown);
        Assert.All(shown, entry =>
        {
            Assert.Equal(title, entry.GetProperty("title").GetString());
            Assert.Equal(1, entry.GetProperty("count").GetInt32());
        });
    }

    private static async Task AssertNoBlankOrSecondTitleAsync(BrowserScenario scenario, string name) =>
        Assert.All(await ReadShownTitlesAsync(scenario, name), entry =>
        {
            Assert.NotEqual(string.Empty, entry.GetProperty("title").GetString());
            Assert.Equal(1, entry.GetProperty("count").GetInt32());
        });

    // The log from the first title on, which is also kept as run evidence. The
    // document has no title before the parser reaches it.
    private static async Task<JsonElement[]> ReadShownTitlesAsync(BrowserScenario scenario, string name)
    {
        var log = await scenario.Page.EvaluateAsync<string>("JSON.stringify(window.flowbitTitleLog)");
        await File.WriteAllTextAsync(Path.Combine(scenario.ArtifactDirectory, $"title-log-{name}.json"), log);
        return JsonSerializer.Deserialize<JsonElement[]>(log)!
            .SkipWhile(entry => entry.GetProperty("title").GetString() == string.Empty)
            .ToArray();
    }
}
