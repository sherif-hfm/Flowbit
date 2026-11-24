using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// R17: the Webhooks subscription form offers the four signal broadcast event types (EVENT-04D, and
/// signalBroadcast.expired from EVENT-04E) with their labels and type names, notes the family rule while one of them
/// is ticked and a workflow family is typed, and lists them in the delivery filter. The smoke stack has no webhook key ring, so the scenario never saves a subscription.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class WebhooksSmokeTests(BrowserStackFixture stack)
{
    private const string FamilyNote =
        "A signal broadcast to all workflows matches only a subscription without a workflow family.";

    private static readonly (string Type, string Label)[] SignalBroadcastTypes =
    [
        ("signalBroadcast.completed", "Signal broadcast completed"),
        ("signalBroadcast.completedWithIssues", "Signal broadcast completed with issues"),
        ("signalBroadcast.cancelled", "Signal broadcast cancelled"),
        ("signalBroadcast.expired", "Signal broadcast expired")
    ];

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task R17_TheFormOffersSignalBroadcastTypesAndNotesTheFamilyRule(int viewportWidth, int viewportHeight)
    {
        await using var scenario = await stack.CreateScenario(
            $"r17-webhooks-form-{viewportWidth}x{viewportHeight}", viewportWidth, viewportHeight);
        await scenario.RunAsync("webhooks-form", async () =>
        {
            await RuntimeSupport.ApplyIdentityAsync(scenario, "webhook-admin", ["admin"]);
            var page = await scenario.OpenUiAsync("webhooks");
            await RuntimeSupport.WaitUntilInteractiveAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Add subscription" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Add webhook subscription" })).ToBeVisibleAsync();

            foreach (var (type, label) in SignalBroadcastTypes)
            {
                var id = "webhook-event-" + type.Replace('.', '-');
                await Assertions.Expect(page.Locator($"#{id}")).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator($"#{id}")).Not.ToBeCheckedAsync();
                var checkboxLabel = page.Locator($"label[for='{id}']");
                await Assertions.Expect(checkboxLabel).ToContainTextAsync(label);
                await Assertions.Expect(checkboxLabel).ToContainTextAsync(type);
                // The longest type name stays inside the viewport, wrapping when it must.
                var box = await checkboxLabel.BoundingBoxAsync();
                Assert.NotNull(box);
                Assert.True(box.X >= 0 && box.X + box.Width <= viewportWidth + 1,
                    $"{type} overflows at {viewportWidth}px: {box.X} + {box.Width}.");
            }

            var family = page.Locator("#webhook-workflow-key");
            var note = page.Locator("#webhook-workflow-key-signal-note");
            await Assertions.Expect(note).ToHaveCountAsync(0);

            // A family alone shows no note; ticking a broadcast type with the mouse adds it.
            await family.ClickAsync();
            await family.PressSequentiallyAsync("orders", new() { Delay = 30 });
            await Assertions.Expect(note).ToHaveCountAsync(0);
            await page.Locator("label[for='webhook-event-signalBroadcast-completedWithIssues']").ClickAsync();
            await Assertions.Expect(page.Locator("#webhook-event-signalBroadcast-completedWithIssues")).ToBeCheckedAsync();
            await Assertions.Expect(note).ToHaveTextAsync(FamilyNote);
            await Assertions.Expect(family).ToHaveAttributeAsync(
                "aria-describedby", "webhook-workflow-key-help webhook-workflow-key-signal-note");
            Assert.True(
                await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
                $"The Webhooks form overflows horizontally at {viewportWidth}px.");
            await family.ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "webhooks-signal-types.png"),
                FullPage = true
            });

            // Clearing the family with the keyboard hides the note again.
            await family.ClickAsync();
            await page.Keyboard.PressAsync("Control+A");
            await page.Keyboard.PressAsync("Backspace");
            await Assertions.Expect(family).ToHaveValueAsync(string.Empty);
            await Assertions.Expect(note).ToHaveCountAsync(0);
            await Assertions.Expect(family).ToHaveAttributeAsync("aria-describedby", "webhook-workflow-key-help");

            // The delivery filter offers the four types too.
            var options = await page.Locator("#delivery-type option").EvaluateAllAsync<string[]>(
                "options => options.map(option => option.value)");
            foreach (var (type, _) in SignalBroadcastTypes)
            {
                Assert.Contains(type, options);
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Add webhook subscription" })).ToHaveCountAsync(0);
            Assert.True(
                await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"),
                $"The Webhooks page overflows horizontally at {viewportWidth}px.");
        });
    }
}
