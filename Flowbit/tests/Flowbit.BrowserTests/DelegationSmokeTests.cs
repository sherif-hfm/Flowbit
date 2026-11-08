using System.Globalization;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// R13: the UTC validity window typed on My delegations and User delegations
/// survives Blazor's change events and creates grants with exactly that window,
/// at desktop and phone widths.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class DelegationSmokeTests(BrowserStackFixture stack)
{
    private static readonly string[] AdminRoles = ["admin"];

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task R13_DelegationPagesKeepTheEnteredValidityWindow(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r13-delegations-{viewportWidth}x{viewportHeight}",
            viewportWidth,
            viewportHeight);
        await scenario.RunAsync("delegation-window", async () =>
        {
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-lifecycle.json");
            var (admin, _) = await RuntimeSupport.ApplyIdentityAsync(scenario, "r13-delegation-admin", AdminRoles);
            // Whole minutes away from the page's defaults (now and now + 7 days), on
            // days and hours that are typed with a leading zero.
            var month = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(2);
            var from = new DateTimeOffset(month.AddDays(1).AddHours(9).AddMinutes(15), TimeSpan.Zero);
            var until = new DateTimeOffset(month.AddDays(7).AddHours(17).AddMinutes(5), TimeSpan.Zero);

            // My delegations: the signed-in user grants their own work.
            var page = await scenario.OpenUiAsync("delegations");
            await page.Locator("#delegation-delegate").FillAsync("r13-delegate");
            await page.Locator("#delegation-workflows").FillAsync(workflow.WorkflowKey);
            await TypeWindowAsync(page, "#delegation-from", from, "#delegation-until", until);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create delegation" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-success")).ToHaveTextAsync("Delegation created.");
            await ExpectWindowAsync(page, "#delegation-from", from, "#delegation-until", until);
            var own = Assert.Single((await admin.ListOutgoingDelegationsAsync(workflow.WorkflowKey)).Items);
            Assert.Equal(from, own.ValidFrom);
            Assert.Equal(until, own.ValidUntil);
            await AssertNoOverflowAsync(page, "My delegations", viewportWidth);

            // User delegations: an administrator creates a grant for two other users.
            page = await scenario.OpenUiAsync("delegation-management");
            await page.Locator("#managed-delegator").FillAsync("r13-delegator");
            await page.Locator("#managed-delegate").FillAsync("r13-managed-delegate");
            await page.Locator("#managed-workflows").FillAsync(workflow.WorkflowKey);
            await TypeWindowAsync(page, "#managed-valid-from", from, "#managed-valid-until", until);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create delegations" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-success")).ToHaveTextAsync("Delegation created.");
            await ExpectWindowAsync(page, "#managed-valid-from", from, "#managed-valid-until", until);
            var managed = Assert.Single(
                (await admin.ListManagedDelegationsAsync("r13-delegator", workflow.WorkflowKey)).Items);
            Assert.Equal("r13-managed-delegate", managed.Delegate);
            Assert.Equal(from, managed.ValidFrom);
            Assert.Equal(until, managed.ValidUntil);
            await AssertNoOverflowAsync(page, "User delegations", viewportWidth);
        });
    }

    // The windows are UTC, as their labels say, so the UTC wall-clock time is typed.
    private static async Task TypeWindowAsync(
        IPage page, string fromSelector, DateTimeOffset from, string untilSelector, DateTimeOffset until)
    {
        await RuntimeSupport.TypeDateTimeLocalAsync(page, fromSelector, from.UtcDateTime);
        await RuntimeSupport.TypeDateTimeLocalAsync(page, untilSelector, until.UtcDateTime);
    }

    private static async Task ExpectWindowAsync(
        IPage page, string fromSelector, DateTimeOffset from, string untilSelector, DateTimeOffset until)
    {
        await Assertions.Expect(page.Locator(fromSelector)).ToHaveValueAsync(MinuteText(from));
        await Assertions.Expect(page.Locator(untilSelector)).ToHaveValueAsync(MinuteText(until));
    }

    private static string MinuteText(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    private static async Task AssertNoOverflowAsync(IPage page, string pageName, int viewportWidth) =>
        Assert.True(
            await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
            $"{pageName} overflows horizontally at {viewportWidth}px.");
}
