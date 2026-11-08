using System.Globalization;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// R14: date-times typed on the keyboard into the Activity time filters and the
/// shared-variable client expiry keep their value and take effect, while Clear
/// and Edit still replace what those inputs show, at desktop and phone widths.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class TypedDateTimeSmokeTests(BrowserStackFixture stack)
{
    // A half-hour offset unlike the hosts', so the expiry must be read in the browser's zone.
    private const string Zone = "Asia/Kolkata";
    private static readonly string[] AdminRoles = ["admin"];

    // Far from any run's clock, with leading zeros in the month, day, and hour.
    private static readonly DateTime FarFuture = new(2099, 1, 2, 3, 4, 0);
    private static readonly DateTime LaterFarFuture = new(2099, 2, 3, 4, 5, 0);

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task R14_TypedDateTimesFilterActivityAndSetClientExpiry(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r14-typed-date-times-{viewportWidth}x{viewportHeight}",
            viewportWidth,
            viewportHeight,
            timezoneId: Zone);
        await scenario.RunAsync("typed-date-times", async () =>
        {
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-lifecycle.json");
            var instance = await RuntimeSupport.StartInstanceAsync(stack, workflow.Id);
            var (admin, _) = await RuntimeSupport.ApplyIdentityAsync(scenario, "r14-admin", AdminRoles);

            // Activity: the instance's executions, then the same search bounded by a typed time.
            var page = await scenario.OpenUiAsync("activity");
            await RuntimeSupport.OpenFilterPanelAsync(page, "#activity-filter-fields");
            var summary = page.Locator("#activity-results-heading + p");
            await page.Locator("#activity-instance-id").FillAsync(instance.Id.ToString(CultureInfo.InvariantCulture));
            await ApplyAsync(page);
            await Assertions.Expect(summary).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^[1-9]\d* matching node executions$"));
            var instanceSummary = await summary.TextContentAsync();

            await RuntimeSupport.TypeDateTimeLocalAsync(page, "#activity-created-from", FarFuture);
            await Assertions.Expect(page.Locator("#activity-created-from")).ToHaveValueAsync("2099-01-02T03:04");
            await ApplyAsync(page);
            await Assertions.Expect(summary).ToHaveTextAsync("0 matching node executions");
            await Assertions.Expect(page.Locator("#activity-created-from")).ToHaveValueAsync("2099-01-02T03:04");

            // Clear empties the typed input as well as the other filters.
            await page.Locator("#activity-filter-fields .filter-actions")
                .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Clear", Exact = true })
                .ClickAsync();
            await Assertions.Expect(page.Locator("#activity-created-from")).ToHaveValueAsync(string.Empty);
            await Assertions.Expect(page.Locator("#activity-instance-id")).ToHaveValueAsync(string.Empty);

            await page.Locator("#activity-instance-id").FillAsync(instance.Id.ToString(CultureInfo.InvariantCulture));
            await RuntimeSupport.TypeDateTimeLocalAsync(page, "#activity-created-to", FarFuture);
            await ApplyAsync(page);
            await Assertions.Expect(summary).ToHaveTextAsync(instanceSummary!);
            await Assertions.Expect(page.Locator("#activity-created-to")).ToHaveValueAsync("2099-01-02T03:04");
            await AssertNoOverflowAsync(page, "Activity", viewportWidth);

            // Shared-variable clients: a typed expiry is created, shown again on Edit, and replaced.
            var clientId = $"r14-client-{viewportWidth}";
            page = await scenario.OpenUiAsync("shared-variable-clients");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add client" }).ClickAsync();
            // The label names the zone as the browser reports it: Chromium still says Asia/Calcutta.
            var reported = await page.EvaluateAsync<string>("() => Intl.DateTimeFormat().resolvedOptions().timeZone");
            await Assertions.Expect(page.Locator("label[for='shared-client-expires']")).ToHaveTextAsync($"Expires (optional, {reported})");
            await page.Locator("#shared-client-id").FillAsync(clientId);
            await page.Locator("#shared-client-name").FillAsync("R14 typed expiry");
            await RuntimeSupport.TypeDateTimeLocalAsync(page, "#shared-client-expires", FarFuture);
            await Assertions.Expect(page.Locator("#shared-client-expires")).ToHaveValueAsync("2099-01-02T03:04");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create and reveal secret" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-success")).ToContainTextAsync($"Created API client {clientId}.");
            Assert.Equal(InBrowserZone(FarFuture), await ExpiresAtAsync(admin, clientId));

            var row = page.Locator("tbody tr", new PageLocatorOptions { HasText = clientId });
            await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Edit", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("#shared-variable-client-form-heading")).ToHaveTextAsync($"Edit {clientId}");
            await Assertions.Expect(page.Locator("#shared-client-expires")).ToHaveValueAsync("2099-01-02T03:04");
            await RuntimeSupport.TypeDateTimeLocalAsync(page, "#shared-client-expires", LaterFarFuture);
            await Assertions.Expect(page.Locator("#shared-client-expires")).ToHaveValueAsync("2099-02-03T04:05");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save client" }).ClickAsync();
            await Assertions.Expect(page.Locator("#shared-variable-client-form-heading")).ToHaveCountAsync(0);
            Assert.Equal(InBrowserZone(LaterFarFuture), await ExpiresAtAsync(admin, clientId));

            // Add client starts again from an empty expiry.
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add client" }).ClickAsync();
            await Assertions.Expect(page.Locator("#shared-client-expires")).ToHaveValueAsync(string.Empty);
            await AssertNoOverflowAsync(page, "Shared-variable clients", viewportWidth);
        });
    }

    private static Task ApplyAsync(IPage page) =>
        page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters", Exact = true }).ClickAsync();

    // The page reads the expiry in the browser's time zone, which this scenario fixes.
    private static DateTimeOffset InBrowserZone(DateTime value) =>
        new(TimeZoneInfo.ConvertTimeToUtc(value, TimeZoneInfo.FindSystemTimeZoneById(Zone)));

    private static async Task<DateTimeOffset?> ExpiresAtAsync(WorkflowFixtureClient admin, string clientId) =>
        Assert.Single((await admin.ListSharedVariableClientsAsync()).Items, client => client.ClientId == clientId).ExpiresAt;

    private static async Task AssertNoOverflowAsync(IPage page, string pageName, int viewportWidth) =>
        Assert.True(
            await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
            $"{pageName} overflows horizontally at {viewportWidth}px.");
}
