using System.Globalization;
using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// R10: task due dates and priorities in My work. Covers the due column with
/// overdue and due-soon badges, sorting by due date and priority, the due
/// filter, and task detail, at desktop and phone widths.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class TaskDueDateSmokeTests(BrowserStackFixture stack)
{
    private static readonly string[] ReviewerRoles = ["Reviewer"];

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task R10_MyWorkShowsSortsAndFiltersDueDatesAndPriorities(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r10-due-dates-{viewportWidth}x{viewportHeight}",
            viewportWidth,
            viewportHeight);
        await scenario.RunAsync("due-dates", async () =>
        {
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-due-dates.json");
            var instance = await stack.SetupClient.StartInstanceAsync(
                workflow.Id,
                new Dictionary<string, JsonElement> { ["amount"] = JsonSerializer.SerializeToElement(500) });
            var (reviewer, _) = await RuntimeSupport.ApplyIdentityAsync(scenario, "reviewer", ReviewerRoles);
            var inbox = await reviewer.GetInboxAsync(instance.Id);
            Assert.Equal(4, inbox.Count);
            var dueSoon = inbox.Single(item => item.CurrentNodeName == "Due soon review");
            Assert.Equal(UserTaskDueStatuses.DueSoon, dueSoon.DueStatus);
            Assert.Equal(60, dueSoon.Priority);

            var page = scenario.Page;
            await scenario.OpenUiAsync("inbox");
            await Assertions.Expect(page.Locator("#inbox-results-heading")).ToBeVisibleAsync();
            await RuntimeSupport.OpenFilterPanelAsync(page, "#inbox-filter-fields");
            await page.Locator("#inbox-instance-id").FillAsync(instance.Id.ToString(CultureInfo.InvariantCulture));
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters" }).ClickAsync();
            var titles = page.Locator("tbody a.data-title");
            await Assertions.Expect(titles).ToHaveCountAsync(4, new LocatorAssertionsToHaveCountOptions { Timeout = 20_000f });

            // Each row shows its due time, badge, and priority.
            await Assertions.Expect(Row(page, "Overdue review").Locator(".task-due-date .status-badge"))
                .ToHaveTextAsync("Overdue");
            await Assertions.Expect(Row(page, "Overdue review").Locator(".task-due-date [title]"))
                .ToHaveAttributeAsync("title", "2020-01-01T09:00:00.0000000+00:00");
            await Assertions.Expect(Row(page, "Overdue review")).ToContainTextAsync("Priority 90");
            await Assertions.Expect(Row(page, "Due soon review").Locator(".task-due-date .status-badge"))
                .ToHaveTextAsync("Due soon");
            await Assertions.Expect(Row(page, "Due soon review").Locator(".task-due-date [title]"))
                .ToHaveAttributeAsync("title", dueSoon.DueAt!.Value.ToString("O", CultureInfo.InvariantCulture));
            await Assertions.Expect(Row(page, "Upcoming review").Locator(".task-due-date")).ToHaveCountAsync(1);
            await Assertions.Expect(Row(page, "Upcoming review").Locator(".status-badge", new LocatorLocatorOptions { HasText = "Due" }))
                .ToHaveCountAsync(0);
            await Assertions.Expect(Row(page, "Unscheduled review")).ToContainTextAsync("—");

            // Sort by due date, soonest first; tasks without one come last.
            await SortAsync(page, "dueAt", "asc");
            await Assertions.Expect(titles).ToHaveTextAsync(
                ["Overdue review", "Due soon review", "Upcoming review", "Unscheduled review"]);

            // Sort by priority, most urgent first.
            await SortAsync(page, "priority", "desc");
            await Assertions.Expect(titles).ToHaveTextAsync(
                ["Overdue review", "Due soon review", "Upcoming review", "Unscheduled review"]);
            await SortAsync(page, "priority", "asc");
            await Assertions.Expect(titles).ToHaveTextAsync(
                ["Upcoming review", "Due soon review", "Overdue review", "Unscheduled review"]);

            Assert.True(
                await page.EvaluateAsync<bool>(
                    "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
                $"My work overflows horizontally at {viewportWidth}px.");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "my-work-due-dates.png"),
                FullPage = true,
            });

            // The due filter narrows the list to overdue work.
            await page.Locator("#inbox-due-status").SelectOptionAsync(UserTaskDueStatuses.Overdue);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters" }).ClickAsync();
            await Assertions.Expect(titles).ToHaveTextAsync(["Overdue review"]);
            await page.Locator("#inbox-due-status").SelectOptionAsync(string.Empty);
            await page.Locator("#inbox-min-priority").FillAsync("50");
            await page.Locator("#inbox-max-priority").FillAsync("70");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters" }).ClickAsync();
            await Assertions.Expect(titles).ToHaveTextAsync(["Due soon review"]);

            // Task detail shows both values.
            await titles.First.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(
                new System.Text.RegularExpressions.Regex($"/user-tasks/{dueSoon.UserTaskId}$"));
            var summary = page.Locator("#task-summary-heading").Locator("xpath=ancestor::section");
            var detailBadge = summary.Locator(".summary-item", new LocatorLocatorOptions { HasText = "Due" })
                .Locator(".status-badge");
            await Assertions.Expect(detailBadge).ToHaveTextAsync("Due soon");
            // The summary label styles must not stretch the badge or recolor it.
            await Assertions.Expect(detailBadge).Not.ToHaveCSSAsync("display", "block");
            Assert.True(
                await detailBadge.EvaluateAsync<bool>(
                    "badge => badge.getBoundingClientRect().width < badge.closest('.summary-item').getBoundingClientRect().width / 2"),
                $"The task detail due badge is stretched at {viewportWidth}px.");
            await Assertions.Expect(summary.Locator(".summary-item", new LocatorLocatorOptions { HasText = "Priority" }))
                .ToContainTextAsync("60");
            Assert.True(
                await page.EvaluateAsync<bool>(
                    "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
                $"Task detail overflows horizontally at {viewportWidth}px.");
        });
    }

    private static ILocator Row(IPage page, string taskName) =>
        page.Locator("tbody tr", new PageLocatorOptions
        {
            Has = page.Locator("a.data-title", new PageLocatorOptions { HasText = taskName })
        });

    private static async Task SortAsync(IPage page, string field, string direction)
    {
        await page.Locator("#inbox-sort-field-0").SelectOptionAsync(field);
        await page.Locator("#inbox-sort-direction-0").SelectOptionAsync(direction);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply sorting" }).ClickAsync();
    }
}
