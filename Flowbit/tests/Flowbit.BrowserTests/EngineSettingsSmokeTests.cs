using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>Standard settings editing, authenticated attribution and the protected lifecycle capture switch.</summary>
[Collection(BrowserCollection.Name)]
public sealed class EngineSettingsSmokeTests(BrowserStackFixture stack)
{
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task S1_SettingsTrackActorsAndManageLifecycleCapture(int width, int height)
    {
        await using var scenario = await stack.CreateScenario($"s1-engine-settings-{width}", width, height);
        await scenario.RunAsync("engine-settings", async () =>
        {
            var key = $"Browser{width}_{Guid.NewGuid():N}";
            var original = await CaptureAsync();
            try
            {
                await RuntimeSupport.ApplyIdentityAsync(scenario, "settings-creator", ["admin"]);
                var page = await scenario.OpenUiAsync("engine-settings");
                await RuntimeSupport.WaitUntilInteractiveAsync(page);
                await page.GetByRole(AriaRole.Button, new() { Name = "Add setting", Exact = true }).ClickAsync();
                await page.Locator("#engine-setting-namespace").FillAsync("Browser");
                await page.Locator("#engine-setting-key").FillAsync(key);
                await page.Locator("#engine-setting-value").FillAsync("created in browser");
                await page.GetByRole(AriaRole.Button, new() { Name = "Create setting", Exact = true }).ClickAsync();
                var row = page.Locator("tbody tr", new() { HasText = key });
                await Assertions.Expect(row).ToContainTextAsync("Created by settings-creator");
                await Assertions.Expect(row).ToContainTextAsync("Updated by settings-creator");
                var created = Assert.Single(await stack.SetupClient.ListEngineSettingsAsync(), item => item.Key == key);
                Assert.Equal("settings-creator", created.CreatedBy);
                Assert.Equal("settings-creator", created.UpdatedBy);

                await RuntimeSupport.ApplyIdentityAsync(scenario, "settings-updater", ["admin"]);
                await scenario.OpenUiAsync("engine-settings");
                await RuntimeSupport.WaitUntilInteractiveAsync(page);
                await row.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
                await page.Locator("#engine-setting-value").FillAsync("updated in browser");
                await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
                await Assertions.Expect(row).ToContainTextAsync("Updated by settings-updater");
                await Assertions.Expect(row).ToContainTextAsync("Created by settings-creator");
                var updated = Assert.Single(await stack.SetupClient.ListEngineSettingsAsync(), item => item.Id == created.Id);
                Assert.Equal("settings-creator", updated.CreatedBy);
                Assert.Equal("settings-updater", updated.UpdatedBy);
                Assert.Equal(created.CreatedAt, updated.CreatedAt);
                Assert.True(updated.UpdatedAt > created.UpdatedAt);
                await page.Locator("#engine-setting-filter").FillAsync(key);
                await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "setting-attribution.png"), FullPage = true });

                // Capture uses a Boolean control and cannot be deleted from the page.
                await page.Locator("#engine-setting-filter").FillAsync("Webhooks.CaptureEnabled");
                var captureRow = page.Locator("tbody tr", new() { HasText = "Webhooks.CaptureEnabled" });
                await Assertions.Expect(captureRow.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true })).ToHaveCountAsync(0);
                await captureRow.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("select#engine-setting-value")).ToBeVisibleAsync();
                await page.Locator("#engine-setting-value").SelectOptionAsync("true");
                await page.Locator("#engine-setting-description").FillAsync("Browser capture edit");

                // A real concurrent API edit must retain the browser draft and require a reload.
                var beforeConflict = await CaptureAsync();
                await stack.SetupClient.UpdateEngineSettingAsync(beforeConflict.Id,
                    new(beforeConflict.Value, "Concurrent administrator edit", beforeConflict.UpdatedAt));
                await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".alert-danger")).ToContainTextAsync("Your edits are preserved");
                await Assertions.Expect(page.Locator("#engine-setting-value")).ToHaveValueAsync("true");
                await Assertions.Expect(page.Locator("#engine-setting-description")).ToHaveValueAsync("Browser capture edit");
                await page.GetByRole(AriaRole.Button, new() { Name = "Reload latest", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("#engine-setting-description")).ToHaveValueAsync("Concurrent administrator edit");
                await page.Locator("#engine-setting-value").SelectOptionAsync("true");
                await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "capture-edit.png"), FullPage = true });
                await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
                await Assertions.Expect(captureRow).ToContainTextAsync("Updated by settings-updater");
                Assert.Equal("true", (await CaptureAsync()).Value);
                Assert.True((await stack.SetupClient.GetWebhookEventCatalogAsync()).Capture.Enabled);

                await scenario.OpenUiAsync("webhooks");
                await RuntimeSupport.WaitUntilInteractiveAsync(page);
                await Assertions.Expect(page.GetByText("No webhook subscriptions", new() { Exact = true })).ToBeVisibleAsync();
                await Assertions.Expect(page.Locator(".alert-danger")).ToHaveCountAsync(0);
                await Assertions.Expect(page.GetByText("Lifecycle capture is off.", new() { Exact = true })).ToHaveCountAsync(0);
                await scenario.OpenUiAsync("engine-settings");
                await RuntimeSupport.WaitUntilInteractiveAsync(page);
                await page.Locator("#engine-setting-filter").FillAsync("Webhooks.CaptureEnabled");
                await captureRow.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
                await page.Locator("#engine-setting-value").SelectOptionAsync("false");
                await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".alert-success")).ToContainTextAsync("updated");
                Assert.False((await stack.SetupClient.GetWebhookEventCatalogAsync()).Capture.Enabled);
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 2"));
                await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "engine-settings.png"), FullPage = true });

                await scenario.OpenUiAsync("webhooks");
                await RuntimeSupport.WaitUntilInteractiveAsync(page);
                var warning = page.Locator(".alert-warning", new() { HasText = "Lifecycle capture is off" });
                await Assertions.Expect(warning).ToBeVisibleAsync();
                await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "capture-off.png"), FullPage = true });
                await warning.GetByRole(AriaRole.Link, new() { Name = "Engine settings", Exact = true }).ClickAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Engine settings", Level = 1 })).ToBeVisibleAsync();
            }
            finally
            {
                var capture = await CaptureAsync();
                await stack.SetupClient.UpdateEngineSettingAsync(capture.Id,
                    new(original.Value, original.Description, capture.UpdatedAt));
                foreach (var setting in (await stack.SetupClient.ListEngineSettingsAsync()).Where(item => item.Key == key))
                    await stack.SetupClient.DeleteEngineSettingAsync(setting.Id, setting.UpdatedAt);
            }
        });
    }

    private async Task<EngineSettingDto> CaptureAsync() =>
        Assert.Single(await stack.SetupClient.ListEngineSettingsAsync(), item => item.Namespace == "Webhooks" && item.Key == "CaptureEnabled");
}
