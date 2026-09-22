using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// Administrative-action batch display smoke coverage (R7): a synchronous
/// instance administrative action creates a completed one-item audit batch, so
/// the real `/administrative-actions` screen can be exercised without a Worker.
/// The scenario executes the action through the instance-detail panel, follows
/// the real audit batch link, verifies frozen request, counts, item results and
/// recent history against HTTP reads, exercises the Open callback with real
/// clicks and keyboard activation, and records responsive screenshots.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class AdministrativeActionsSmokeTests(BrowserStackFixture stack)
{
    private static readonly string[] AdminRoles = ["admin"];
    private const int ApproveFlowId = 102;

    private static readonly string[] CountLabels =
    [
        "Positions", "Affected tasks", "Eligible", "Ineligible", "Succeeded",
        "Queued", "Skipped", "Failed", "Cancelled"
    ];

    [Fact]
    public async Task R7_AdministrativeBatchDisplayAndOpenCallback()
    {
        await using var scenario = await stack.CreateScenario("r7-administrative-batches");
        await scenario.RunAsync("administrative-batch-display", async () =>
        {
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-administrative-r7.json");
            var instanceA = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);
            var instanceB = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);

            var (admin, _) = await RuntimeSupport.ApplyIdentityAsync(scenario, "supervisor", AdminRoles);
            var page = scenario.Page;

            // Two distinct completed audits through the real instance panel.
            var reasonA = "Supervisor override for ticket <A>";
            var noteA = "Approved <after> & checked";
            var reasonB = "Supervisor \"override\" for ticket B";
            var noteB = "Approved by override B";
            var batchA = await ExecuteInstanceActionAsync(scenario, instanceA.Id, reasonA, noteA);
            var batchB = await ExecuteInstanceActionAsync(scenario, instanceB.Id, reasonB, noteB);
            Assert.NotEqual(batchA, batchB);

            var batchBHttp = await admin.GetAdministrativeActionBatchAsync(batchB);
            var itemsBHttp = await admin.GetAdministrativeActionBatchItemsAsync(batchB);
            Assert.Equal("completed", batchBHttp.Summary.Status);
            Assert.Equal(1, itemsBHttp.TotalCount);

            // Follow the real audit batch link from the instance panel.
            await page.GetByRole(AriaRole.Link,
                new PageGetByRoleOptions { Name = $"View audit batch #{batchB}" }).ClickAsync();
            await RuntimeSupport.WaitUntilInteractiveAsync(page);
            await Assertions.Expect(page).ToHaveURLAsync(
                new Regex($"administrative-actions\\?batchId={batchB}$"));

            await AssertAuditAsync(page, batchBHttp, itemsBHttp,
                reasonB, instanceB.Id, batchB, batchA);
            await AssertRecentHistoryAsync(page, admin, batchA, batchB);

            // Expand the audit JSON controls through real keyboard activation.
            var audit = page.Locator("section[aria-labelledby='admin-current-batch-heading']");
            var variablesJson = await ExpandDetailsJsonAsync(audit, "Common variables");
            Assert.Equal(noteB, variablesJson.RootElement.GetProperty("approvalNote").GetString());
            var selectionJson = await ExpandDetailsJsonAsync(audit, "Selection snapshot");
            Assert.Equal(
                JsonText(batchBHttp.Selection, "mode"),
                JsonText(selectionJson.RootElement, "mode"));
            var itemRow = audit.Locator("tbody tr")
                .Filter(new LocatorFilterOptions { HasText = $"Instance #{instanceB.Id}" });
            var resultJson = await ExpandDetailsJsonAsync(itemRow.First, "Result");
            Assert.Equal(
                JsonText(itemsBHttp.Items.Single().Result, "instanceStatus"),
                JsonText(resultJson.RootElement, "instanceStatus"));

            // Zero-match item status filter and restore.
            var itemFilter = page.GetByRole(AriaRole.Combobox,
                new PageGetByRoleOptions { Name = "Filter batch items by status" });
            await itemFilter.SelectOptionAsync("failed");
            await Assertions.Expect(audit).ToContainTextAsync("No items in this view");
            await itemFilter.SelectOptionAsync("");
            await Assertions.Expect(audit).ToContainTextAsync($"Instance #{instanceB.Id}");

            // Zero-match recent-batch status filter and restore.
            var history = page.Locator("section[aria-labelledby='admin-batches-heading']");
            var batchFilter = page.GetByRole(AriaRole.Combobox,
                new PageGetByRoleOptions { Name = "Filter batches by status" });
            await batchFilter.SelectOptionAsync("failed");
            await Assertions.Expect(history).ToContainTextAsync("No administrative batches");
            await batchFilter.SelectOptionAsync("");
            await Assertions.Expect(RecentRowFor(history, batchA).First).ToBeVisibleAsync();
            await Assertions.Expect(RecentRowFor(history, batchB).First).ToBeVisibleAsync();

            // Click the other row's actual Open button and prove the audit,
            // items and highlight switch to that batch.
            var batchAHttp = await admin.GetAdministrativeActionBatchAsync(batchA);
            var itemsAHttp = await admin.GetAdministrativeActionBatchItemsAsync(batchA);
            await RecentRowFor(history, batchA).First
                .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Open", Exact = true })
                .ClickAsync();
            await AssertAuditAsync(page, batchAHttp, itemsAHttp,
                reasonA, instanceA.Id, batchA, batchB);

            // Open the second batch with the keyboard and confirm the switch.
            var rowB = RecentRowFor(history, batchB).First;
            await rowB.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Open", Exact = true })
                .FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await AssertAuditAsync(page, batchBHttp, itemsBHttp,
                reasonB, instanceB.Id, batchB, batchA);

            await ExpandDetailsAsync(audit, "Common variables");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "administrative-batch-audit-1440x900.png"),
                FullPage = true,
            });
        });
    }

    [Theory]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task R7_AdministrativeResponsiveDisplay(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r7-administrative-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("administrative-responsive-display", async () =>
        {
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-administrative-r7.json");
            var instance = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);

            var (admin, _) = await RuntimeSupport.ApplyIdentityAsync(scenario, "supervisor", AdminRoles);
            var page = scenario.Page;

            var batchId = await ExecuteInstanceActionAsync(
                scenario, instance.Id, "Responsive supervisor override", "Narrow viewport note");
            var batchHttp = await admin.GetAdministrativeActionBatchAsync(batchId);
            var itemsHttp = await admin.GetAdministrativeActionBatchItemsAsync(batchId);

            await page.GetByRole(AriaRole.Link,
                new PageGetByRoleOptions { Name = $"View audit batch #{batchId}" }).ClickAsync();
            await RuntimeSupport.WaitUntilInteractiveAsync(page);
            await Assertions.Expect(page).ToHaveURLAsync(
                new Regex($"administrative-actions\\?batchId={batchId}$"));

            await AssertAuditAsync(page, batchHttp, itemsHttp,
                "Responsive supervisor override", instance.Id, batchId, batchId);
            var history = page.Locator("section[aria-labelledby='admin-batches-heading']");
            await Assertions.Expect(RecentRowFor(history, batchId).First).ToBeVisibleAsync();

            // Narrow widths keep the document overflow-free; clipped table
            // columns remain reachable through the responsive containers.
            var contained = await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2");
            Assert.True(
                contained,
                $"The administrative page overflows horizontally at {viewportWidth}px.");
            await AssertTableCanScrollAsync(page,
                page.Locator("section[aria-labelledby='admin-current-batch-heading'] .table-responsive").First);
            await AssertTableCanScrollAsync(page, history.Locator(".table-responsive").First);

            await ExpandDetailsAsync(
                page.Locator("section[aria-labelledby='admin-current-batch-heading']"), "Common variables");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, $"administrative-batch-audit-{viewportWidth}x{viewportHeight}.png"),
                FullPage = true,
            });
        });
    }

    /// <summary>
    /// Executes one immediate administrative action through the real
    /// instance-detail panel and returns the created audit batch ID.
    /// </summary>
    private static async Task<long> ExecuteInstanceActionAsync(
        BrowserScenario scenario, long instanceId, string reason, string approvalNote)
    {
        var page = scenario.Page;
        await scenario.OpenUiAsync($"instances/{instanceId}");
        var panel = page.Locator("#administrative-actions");
        await Assertions.Expect(panel).ToBeVisibleAsync();

        await RuntimeSupport.ClickUntilAsync(
            async () => await panel.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Approve" })
                .First.ClickAsync(),
            async () => await panel.Locator(".admin-action-inputs").IsVisibleAsync(),
            TimeSpan.FromSeconds(30));

        await page.GetByLabel("approvalNote").FillAsync(approvalNote);
        await page.GetByLabel("Reason (optional)").FillAsync(reason);

        await RuntimeSupport.ClickUntilAsync(
            async () => await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review action" })
                .ClickAsync(),
            async () => await page.Locator(".admin-action-inputs .alert-warning").IsVisibleAsync(),
            TimeSpan.FromSeconds(30));

        await RuntimeSupport.ClickUntilAsync(
            async () => await page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Confirm administrative action" }).ClickAsync(),
            async () => await page.GetByRole(AriaRole.Link,
                new PageGetByRoleOptions { Name = "View audit batch" }).IsVisibleAsync(),
            TimeSpan.FromSeconds(30));

        var href = await page.GetByRole(AriaRole.Link,
            new PageGetByRoleOptions { Name = "View audit batch" }).First
            .GetAttributeAsync("href");
        var match = Regex.Match(href ?? string.Empty, @"batchId=(\d+)");
        Assert.True(match.Success, $"The audit batch link was missing or malformed: {href}");
        return long.Parse(match.Groups[1].Value);
    }

    /// <summary>
    /// Asserts the frozen audit against the HTTP-read batch and item data:
    /// count order/values, frozen request, item row, and highlight state
    /// relative to the other recent batch.
    /// </summary>
    private static async Task AssertAuditAsync(
        IPage page,
        AdministrativeActionBatchDetailDto batch,
        PagedResult<AdministrativeActionBatchItemDto> items,
        string expectedReason,
        long instanceId,
        long currentBatchId,
        long otherBatchId)
    {
        var audit = page.Locator("section[aria-labelledby='admin-current-batch-heading']");
        await Assertions.Expect(audit).ToContainTextAsync("Preparation and execution");
        await Assertions.Expect(audit).ToContainTextAsync($"Batch #{currentBatchId}");
        await Assertions.Expect(audit).ToContainTextAsync(
            $"{batch.Summary.WorkflowKey} · v{batch.Summary.WorkflowVersion} · definition #{batch.Summary.WorkflowDefinitionId}");

        var summaryItems = audit.Locator(".summary-grid .summary-item");
        await Assertions.Expect(summaryItems).ToHaveCountAsync(CountLabels.Length);
        var summary = batch.Summary;
        var expectedCounts = new[]
        {
            summary.TotalItemCount, summary.TotalAffectedTaskCount, summary.EligibleItemCount,
            summary.IneligibleItemCount, summary.SucceededItemCount, summary.QueuedItemCount,
            summary.SkippedItemCount, summary.FailedItemCount, summary.CancelledItemCount,
        };
        var actualLabels = await summaryItems.Locator("span").AllInnerTextsAsync();
        var actualCounts = await summaryItems.Locator("strong").AllInnerTextsAsync();
        Assert.Equal(CountLabels, actualLabels);
        Assert.Equal(
            expectedCounts.Select(count => count.ToString("N0")).ToArray(),
            actualCounts);

        await Assertions.Expect(audit).ToContainTextAsync("Frozen request");
        await Assertions.Expect(audit).ToContainTextAsync($"{batch.Action.SourceNodeName} · node #{batch.Action.SourceNodeId}");
        await Assertions.Expect(audit).ToContainTextAsync(
            $"{batch.Action.Name} · Task action · {batch.Action.SourceNodeName} → {batch.Action.TargetNodeName} · flow #{batch.Action.FlowId}");
        await Assertions.Expect(audit).ToContainTextAsync("Not applicable");
        await Assertions.Expect(audit).ToContainTextAsync(expectedReason);
        await Assertions.Expect(audit).ToContainTextAsync("Prepared by");
        await Assertions.Expect(audit).ToContainTextAsync(
            $"{batch.Summary.PreparedBy} ({RoleSnapshot(batch.PreparedByRoles)})");
        await Assertions.Expect(audit).ToContainTextAsync("Confirmed by");
        await Assertions.Expect(audit).ToContainTextAsync(batch.Summary.ConfirmedBy is null
            ? "Not confirmed"
            : $"{batch.Summary.ConfirmedBy} ({RoleSnapshot(batch.ConfirmedByRoles)})");

        await Assertions.Expect(audit).ToContainTextAsync("Paged item results");
        await Assertions.Expect(audit).ToContainTextAsync($"{items.TotalCount:N0} matching items");
        var itemRow = audit.Locator("tbody tr")
            .Filter(new LocatorFilterOptions { HasText = $"Instance #{instanceId}" });
        await Assertions.Expect(itemRow).ToHaveCountAsync(1);
        var item = items.Items.Single();
        await Assertions.Expect(itemRow.First.Locator("a").First)
            .ToHaveAttributeAsync("href", $"instances/{instanceId}");
        await Assertions.Expect(itemRow.First).ToContainTextAsync(
            $"User task #{item.PositionId} · token #{item.TokenId}");
        await Assertions.Expect(itemRow.First).ToContainTextAsync(
            $"Definition #{item.WorkflowDefinitionId} · node #{item.SourceNodeId}");
        await Assertions.Expect(itemRow.First).ToContainTextAsync($"Flow #{item.FlowId}");
        await Assertions.Expect(itemRow.First).ToContainTextAsync($"{item.AffectedTaskCount:N0} task(s)");
        await Assertions.Expect(itemRow.First).ToContainTextAsync("Succeeded");

        // The current row is highlighted and the other recent batch is not.
        var history = page.Locator("section[aria-labelledby='admin-batches-heading']");
        var activeRow = history.Locator("tr.table-active");
        await Assertions.Expect(activeRow).ToHaveCountAsync(1);
        await Assertions.Expect(activeRow).ToContainTextAsync($"#{currentBatchId}");
        if (otherBatchId != currentBatchId)
        {
            await Assertions.Expect(RecentRowFor(history, otherBatchId))
                .Not.ToHaveClassAsync(new Regex("table-active"));
        }
    }

    private static async Task AssertRecentHistoryAsync(
        IPage page, WorkflowFixtureClient admin, long batchA, long batchB)
    {
        var history = page.Locator("section[aria-labelledby='admin-batches-heading']");
        await Assertions.Expect(history).ToContainTextAsync("Recent batches");
        var recent = await admin.GetAdministrativeActionBatchesAsync();
        // Other smoke scenarios never create batches, but the list is not
        // workflow-filtered, so only require both of this scenario's batches.
        Assert.True(recent.TotalCount >= 2, $"Expected at least 2 recent batches, got {recent.TotalCount}.");
        await AssertRecentRowAsync(RecentRowFor(history, batchA).First,
            recent.Items.Single(item => item.Id == batchA));
        await AssertRecentRowAsync(RecentRowFor(history, batchB).First,
            recent.Items.Single(item => item.Id == batchB));
    }

    /// <summary>Reads a property by name, ignoring the stored casing.</summary>
    private static string? JsonText(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new KeyNotFoundException($"Property '{name}' was not found: the JSON element is null.");
        }
        foreach (var property in element.Value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.GetRawText();
            }
        }
        throw new KeyNotFoundException($"Property '{name}' was not found in {element.Value.ValueKind} element.");
    }

    /// <summary>
    /// A recent-batch row selected by its first cell, so small batch ids
    /// cannot collide with node/flow references in other columns.
    /// </summary>
    private static ILocator RecentRowFor(ILocator history, long batchId) =>
        history.Locator("tbody tr").Filter(new LocatorFilterOptions
        {
            Has = history.Page.Locator("td:first-child", new PageLocatorOptions { HasTextRegex = new Regex($"^#{batchId}$") }),
        });

    private static async Task AssertRecentRowAsync(ILocator row, AdministrativeActionBatchSummaryDto summary)
    {
        await Assertions.Expect(row).ToContainTextAsync($"#{summary.Id}");
        await Assertions.Expect(row).ToContainTextAsync($"{summary.WorkflowKey} · v{summary.WorkflowVersion}");
        await Assertions.Expect(row).ToContainTextAsync(
            $"Definition #{summary.WorkflowDefinitionId} · {summary.SourceNodeName} (#{summary.SourceNodeId})");
        await Assertions.Expect(row).ToContainTextAsync($"Task action · flow #{summary.FlowId}");
        await Assertions.Expect(row).ToContainTextAsync("Standard position");
        await Assertions.Expect(row).ToContainTextAsync(summary.PreparedBy);
        await Assertions.Expect(row).ToContainTextAsync($"{summary.SucceededItemCount:N0} / {summary.TotalItemCount:N0}");
        await Assertions.Expect(row).ToContainTextAsync("Completed");
        await Assertions.Expect(row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Open", Exact = true }))
            .ToHaveCountAsync(1);
    }

    private static string RoleSnapshot(IReadOnlyList<string>? roles) =>
        roles is null || roles.Count == 0 ? "no roles" : string.Join(", ", roles);

    private static async Task ExpandDetailsAsync(ILocator scope, string summaryText)
    {
        var details = scope.Locator("details").Filter(new LocatorFilterOptions { HasText = summaryText });
        if (await details.Locator("pre.admin-json").IsVisibleAsync())
        {
            return;
        }
        await details.Locator("summary").FocusAsync();
        await details.Locator("summary").PressAsync("Enter");
    }

    private static async Task<JsonDocument> ExpandDetailsJsonAsync(ILocator scope, string summaryText)
    {
        await ExpandDetailsAsync(scope, summaryText);
        var details = scope.Locator("details").Filter(new LocatorFilterOptions { HasText = summaryText });
        var json = await details.Locator("pre.admin-json").InnerTextAsync();
        return JsonDocument.Parse(json);
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
}
