using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// R18 (EVENT-04E): the Signals page's optional expiry. An administrator types an amount and chooses a unit, the
/// review states when the signal expires, an edit of the unit ends the review, an amount of 0 is refused before any
/// request, and the thrown broadcast shows its Expiry row in the browser's zone. The smoke stack has no Worker, so the
/// broadcast is thrown to all workflows under a name nothing waits for and completes at once; expiry during an outage
/// and the refused retry are the Worker acceptance suite's S14 and S15.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class SignalExpirySmokeTests(BrowserStackFixture stack)
{
    private const string Zone = "America/New_York";

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task R18_SignalsPageReviewsAndThrowsASignalWithAnExpiry(int viewportWidth, int viewportHeight)
    {
        // A browser fixed to New York, so the Expiry row proves it uses the browser's zone, not the host's.
        await using var scenario = await stack.CreateScenario(
            $"r18-signal-expiry-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight, timezoneId: Zone);
        await scenario.RunAsync("signal-expiry", async () =>
        {
            // admin satisfies the default Signals.RequiredRole setting.
            await RuntimeSupport.ApplyIdentityAsync(scenario, "signal-operator", ["admin"]);
            var page = await scenario.OpenUiAsync("signals");
            await RuntimeSupport.WaitUntilInteractiveAsync(page);
            var signalName = $"browser.r18-{viewportWidth}-{Guid.NewGuid():N}"[..40];

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

            // The expiry row: a number box and a unit list, Minutes first.
            var amount = page.Locator("#signal-expiry-amount");
            var unit = page.Locator("#signal-expiry-unit");
            await Assertions.Expect(page.GetByLabel("Expires after (optional)", new PageGetByLabelOptions { Exact = true }))
                .ToBeVisibleAsync();
            await Assertions.Expect(unit).ToHaveValueAsync("Minutes");
            await Assertions.Expect(page.Locator("#signal-expiry-help"))
                .ToContainTextAsync("their waits stay open for a later signal");

            // An amount of 0 is refused before any request.
            await amount.ClickAsync();
            await amount.PressSequentiallyAsync("0", new LocatorPressSequentiallyOptions { Delay = 30 });
            await amount.PressAsync("Tab");
            var review = page.Locator("section[aria-labelledby='signal-review-heading']");
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-danger"))
                .ToContainTextAsync("Enter the expiry as a whole number of 1 or more, or leave it empty.");
            await Assertions.Expect(review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Throw signal" }))
                .ToHaveCountAsync(0);

            // Ten minutes: the review says when it expires.
            await amount.ClickAsync();
            await page.Keyboard.PressAsync("Control+A");
            await amount.PressSequentiallyAsync("10", new LocatorPressSequentiallyOptions { Delay = 30 });
            await amount.PressAsync("Tab");
            await unit.SelectOptionAsync("Minutes");
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(review).ToContainTextAsync("Expires10 minutes after the throw");

            // Text the number box cannot read reaches the page as an empty value. It ends the review,
            // and Review signal refuses it instead of reading it as a signal that does not expire.
            await amount.ClickAsync();
            await page.Keyboard.PressAsync("Control+A");
            await amount.PressSequentiallyAsync("5-", new LocatorPressSequentiallyOptions { Delay = 30 });
            await amount.PressAsync("Tab");
            Assert.True(await amount.EvaluateAsync<bool>("element => element.validity.badInput"));
            await Assertions.Expect(review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Review signal" }))
                .ToBeVisibleAsync();
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-danger"))
                .ToContainTextAsync("Enter the expiry as a whole number of 1 or more, or leave it empty.");
            await Assertions.Expect(review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Throw signal" }))
                .ToHaveCountAsync(0);
            // The page never writes the box back, so the text the browser could not read is still there.
            Assert.True(await amount.EvaluateAsync<bool>("element => element.validity.badInput"));
            await amount.ClickAsync();
            await page.Keyboard.PressAsync("Control+A");
            await amount.PressSequentiallyAsync("10", new LocatorPressSequentiallyOptions { Delay = 30 });
            await amount.PressAsync("Tab");
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(review).ToContainTextAsync("Expires10 minutes after the throw");

            // Changing the unit ends the review.
            await unit.SelectOptionAsync("Hours");
            await Assertions.Expect(review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Review signal" }))
                .ToBeVisibleAsync();
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(review).ToContainTextAsync("Expires10 hours after the throw");
            await unit.SelectOptionAsync("Minutes");
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(review).ToContainTextAsync("Expires10 minutes after the throw");
            await review.ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "signals-expiry-review.png"),
            });

            // The thrown broadcast reaches nothing, completes at once, and shows its expiry in the browser's zone.
            await review.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
            var success = page.Locator(".alert-success");
            await Assertions.Expect(success).ToContainTextAsync(new Regex(@"Broadcast #\d+ found no waiting events"));
            var current = page.Locator("section[aria-labelledby='signal-current-heading']");
            var expiry = current.Locator("dd.signal-expiry");
            await Assertions.Expect(expiry).ToBeVisibleAsync();
            var time = expiry.Locator("time");
            await Assertions.Expect(time).ToHaveCountAsync(1);
            var expiresAt = DateTimeOffset.Parse(
                (await time.GetAttributeAsync("datetime"))!, System.Globalization.CultureInfo.InvariantCulture);
            var newYork = TimeZoneInfo.FindSystemTimeZoneById(Zone);
            await Assertions.Expect(time).ToHaveTextAsync(TimeZoneInfo.ConvertTime(expiresAt, newYork).DateTime.ToString("g"));
            // About ten minutes after the throw, by the database's clock.
            var minutesAhead = (expiresAt - DateTimeOffset.UtcNow).TotalMinutes;
            Assert.InRange(minutesAhead, 8, 11);
            await Assertions.Expect(current).ToContainTextAsync("Completed");
            await Assertions.Expect(current.Locator(".signal-expiry-note")).ToHaveCountAsync(0);

            // The status filter of the history lists Expired.
            var statuses = await page.Locator("section[aria-labelledby='signal-history-heading'] .signal-status-filter option")
                .EvaluateAllAsync<string[]>("options => options.map(option => option.textContent.trim())");
            Assert.Contains("Expired", statuses);

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "signals-expiry-thrown.png"),
                FullPage = true,
            });
            Assert.True(
                await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
                $"The Signals page with an expiry overflows horizontally at {viewportWidth}px.");
        });
    }
}
