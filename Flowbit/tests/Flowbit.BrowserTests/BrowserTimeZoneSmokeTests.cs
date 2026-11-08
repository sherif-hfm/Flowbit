using System.Globalization;
using System.Net;
using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// R12: browser time zone. Flowbit.Ui renders on the server, so a browser fixed to a
/// zone the hosts do not use proves that times are shown and typed times are read in
/// the browser's zone: My work, instance detail, and Activity, the Activity time
/// filters with a skipped daylight-saving time, the Webhooks filter labels, and the
/// labelled UTC of a prerender, at desktop and phone widths.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class BrowserTimeZoneSmokeTests(BrowserStackFixture stack)
{
    private const string Zone = "America/New_York";
    // Node-execution reads and the Webhooks page need the default administrator role.
    private static readonly string[] Roles = ["Reviewer", "admin"];
    private static readonly string[] TimeFormats = ["g", "G"];
    private static readonly System.Text.RegularExpressions.Regex Found = new("^[1-9][0-9,]* matching node executions$");
    private const string NoneFound = "0 matching node executions";

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task R12_TimesAreShownAndReadInTheBrowsersTimeZone(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r12-browser-time-zone-{viewportWidth}x{viewportHeight}",
            viewportWidth,
            viewportHeight,
            timezoneId: Zone);
        await scenario.RunAsync("browser-time-zone", async () =>
        {
            // The hosts run in this machine's zone; the checks prove the browser's zone
            // is used wherever that is not New York's.
            var newYork = TimeZoneInfo.FindSystemTimeZoneById(Zone);
            var workflow = await RuntimeSupport.PublishAsync(stack, "runtime-due-dates.json");
            var instance = await stack.SetupClient.StartInstanceAsync(
                workflow.Id,
                new Dictionary<string, JsonElement> { ["amount"] = JsonSerializer.SerializeToElement(500) });
            var (reviewer, _) = await RuntimeSupport.ApplyIdentityAsync(scenario, "zone-reviewer", Roles);
            var createdAt = (await reviewer.GetInstanceAsync(instance.Id)).CreatedAt;
            var page = scenario.Page;

            // A prerender has no browser to ask, so its times are UTC and say so.
            var prerendered = WebUtility.HtmlDecode(await (await page.APIRequest.GetAsync("inbox")).TextAsync());
            Assert.Contains($"{new DateTime(2020, 1, 1, 9, 0, 0):g} UTC</time>", prerendered, StringComparison.Ordinal);

            // My work: the 2020-01-01T09:00Z due time is 04:00 in New York.
            await scenario.OpenUiAsync("inbox");
            await RuntimeSupport.OpenFilterPanelAsync(page, "#inbox-filter-fields");
            await page.Locator("#inbox-instance-id").FillAsync(instance.Id.ToString(CultureInfo.InvariantCulture));
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters" }).ClickAsync();
            await Assertions.Expect(page.Locator("tbody a.data-title")).ToHaveCountAsync(4, new LocatorAssertionsToHaveCountOptions { Timeout = 20_000f });
            var overdue = page.Locator("tbody tr", new PageLocatorOptions
            {
                Has = page.Locator("a.data-title", new PageLocatorOptions { HasText = "Overdue review" })
            }).Locator(".task-due-date time");
            await Assertions.Expect(overdue).ToHaveTextAsync(new DateTime(2020, 1, 1, 4, 0, 0).ToString("g"));
            await Assertions.Expect(overdue).ToHaveAttributeAsync("title", "2020-01-01T09:00:00.0000000+00:00");
            await AssertTimesShownInAsync(page, newYork);

            // Instance detail: summary, history, and every other time.
            await scenario.OpenUiAsync($"instances/{instance.Id}");
            var created = page.Locator(".summary-item", new PageLocatorOptions
            {
                Has = page.Locator("span", new PageLocatorOptions { HasTextRegex = new("^Created$") })
            }).Locator("time");
            await Assertions.Expect(created).ToHaveTextAsync(
                TimeZoneInfo.ConvertTimeFromUtc(createdAt.UtcDateTime, newYork).ToString("g"));
            await AssertTimesShownInAsync(page, newYork);

            // Activity names the zone of its time filters and reads them in it.
            await scenario.OpenUiAsync("activity");
            await RuntimeSupport.OpenFilterPanelAsync(page, "#activity-filter-fields");
            await Assertions.Expect(page.Locator("#activity-time-zone"))
                .ToHaveTextAsync($"Times use your browser's time zone, {Zone}.");
            await Assertions.Expect(page.Locator("#activity-created-from"))
                .ToHaveAttributeAsync("aria-describedby", "activity-time-zone");
            await page.Locator("#activity-instance-id").FillAsync(instance.Id.ToString(CultureInfo.InvariantCulture));
            var summary = page.Locator("#activity-results-heading + p");
            var before = Minute(TimeZoneInfo.ConvertTimeFromUtc(createdAt.UtcDateTime.AddMinutes(-2), newYork));
            var after = Minute(TimeZoneInfo.ConvertTimeFromUtc(createdAt.UtcDateTime.AddMinutes(2), newYork));

            // Either bound read in another zone moves by hours, so these four searches
            // fail whichever side of New York the hosts are on.
            await RuntimeSupport.TypeDateTimeLocalAsync(page, "#activity-created-from", before);
            await ApplyAsync(page);
            await Assertions.Expect(summary).ToHaveTextAsync(Found);
            await AssertTimesShownInAsync(page, newYork);

            await RuntimeSupport.TypeDateTimeLocalAsync(page, "#activity-created-from", after);
            await ApplyAsync(page);
            await Assertions.Expect(summary).ToHaveTextAsync(NoneFound);

            await page.Locator("#activity-created-from").FillAsync(string.Empty);
            await RuntimeSupport.TypeDateTimeLocalAsync(page, "#activity-created-to", before);
            await ApplyAsync(page);
            await Assertions.Expect(summary).ToHaveTextAsync(NoneFound);

            await RuntimeSupport.TypeDateTimeLocalAsync(page, "#activity-created-to", after);
            await ApplyAsync(page);
            await Assertions.Expect(summary).ToHaveTextAsync(Found);
            Assert.True(
                await page.EvaluateAsync<bool>(
                    "() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
                $"Activity overflows horizontally at {viewportWidth}px.");
            await page.Locator("#activity-time-zone").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "activity-time-filters.png"),
                FullPage = true,
            });

            // 02:30 on 8 March 2026 does not exist in New York.
            await RuntimeSupport.TypeDateTimeLocalAsync(page, "#activity-created-from", new DateTime(2026, 3, 8, 2, 30, 0));
            await ApplyAsync(page);
            await Assertions.Expect(page.Locator(".alert-danger")).ToHaveTextAsync(
                $"Created from: 2026-03-08 02:30 does not exist in {Zone} because of a daylight-saving change.");

            // The webhook delivery filters say which zone they read.
            await scenario.OpenUiAsync("webhooks");
            await Assertions.Expect(page.Locator("label[for='delivery-from']")).ToHaveTextAsync($"From ({Zone})");
            await Assertions.Expect(page.Locator("label[for='delivery-to']")).ToHaveTextAsync($"To ({Zone})");
        });
    }

    private static Task ApplyAsync(IPage page) =>
        page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters" }).ClickAsync();

    private static DateTime Minute(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0);

    /// <summary>Every rendered time shows its instant in the zone, in a format the UI uses, with no UTC label.</summary>
    private static async Task AssertTimesShownInAsync(IPage page, TimeZoneInfo zone)
    {
        var times = await page.Locator("time").EvaluateAllAsync<string[][]>(
            "elements => elements.map(element => [element.getAttribute('datetime'), element.textContent.trim()])");
        Assert.NotEmpty(times);
        foreach (var time in times)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTimeOffset.Parse(time[0], CultureInfo.InvariantCulture).UtcDateTime, zone);
            Assert.Contains(time[1], TimeFormats.Select(format => local.ToString(format)));
        }
    }
}
