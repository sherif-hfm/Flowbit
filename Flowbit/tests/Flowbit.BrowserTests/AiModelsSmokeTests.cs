using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// The AI models page end to end: a settings administrator adds, edits and deletes a model; its key is written once and never shown;
/// the editor assistant offers the model by name and the API calls the loopback provider with the stored key.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class AiModelsSmokeTests(BrowserStackFixture stack)
{
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task H10_AdministratorsManageModelsWithWriteOnlyKeys(int width, int height)
    {
        await using var scenario = await stack.CreateScenario($"h10-ai-models-{width}", width, height);
        await scenario.RunAsync("ai-models", async () =>
        {
            var name = $"Browser model {width}";
            var key = $"synthetic-h10-provider-key-{width}-0123456789";
            try
            {
                var original = await RuntimeSupport.PublishAsync(stack, "editor-basic.json");
                await RuntimeSupport.ApplyIdentityAsync(scenario, "ai-models-admin", ["admin"]);
                var page = await scenario.OpenUiAsync("ai-models");
                await Assertions.Expect(page.Locator("tbody tr").First).ToContainTextAsync("GLM-5.3-Flash");
                await Assertions.Expect(page.Locator("tbody tr").First).ToContainTextAsync("Assistant default");

                // Add a model; its key is sent once and never displayed again.
                await page.GetByRole(AriaRole.Button, new() { Name = "Add model", Exact = true }).First.ClickAsync();
                await page.Locator("#ai-model-setting-name").FillAsync(name);
                await page.Locator("#ai-model-setting-model").FillAsync("glm-5.3");
                await page.Locator("#ai-model-setting-endpoint").FillAsync(stack.AiProvider.BaseAddress);
                await page.Locator("#ai-model-setting-reasoning").SelectOptionAsync("medium");
                await page.Locator("#ai-model-setting-key").FillAsync(key);
                await page.Locator("#ai-model-save").ClickAsync();
                await Assertions.Expect(page.Locator(".alert-success")).ToContainTextAsync($"AI model {name} added.");
                var row = page.Locator("tbody tr", new() { HasText = name });
                await Assertions.Expect(row).ToContainTextAsync("Medium");
                await Assertions.Expect(row).ToContainTextAsync("Key stored");
                Assert.DoesNotContain(key, await page.ContentAsync());
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 2"));
                await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "ai-models.png"), FullPage = true });

                // Editing with a blank key keeps the stored key; the form never shows it.
                await row.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("#ai-model-setting-key")).ToHaveValueAsync("");
                await Assertions.Expect(page.Locator("#ai-model-setting-endpoint")).ToHaveValueAsync(stack.AiProvider.BaseAddress);
                await page.Locator("#ai-model-setting-name").FillAsync(name + " renamed");
                await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "ai-model-edit.png"), FullPage = true });
                await page.Locator("#ai-model-save").ClickAsync();
                await Assertions.Expect(page.Locator(".alert-success")).ToContainTextAsync($"AI model {name} renamed updated.");
                var created = Assert.Single(await stack.SetupClient.ListAiModelsAsync(), model => model.Name == name + " renamed");
                Assert.Equal(2, created.Revision);

                // The engine settings page does not show the stored models.
                var settings = await scenario.Context.NewPageAsync();
                await settings.GotoAsync(stack.UiBaseAddress + "/engine-settings");
                await RuntimeSupport.WaitUntilInteractiveAsync(settings);
                await Assertions.Expect(settings.Locator("#engine-setting-results-heading + p")).ToContainTextAsync("settings");
                Assert.DoesNotContain("AiAuthoring", await settings.ContentAsync());
                await settings.CloseAsync();

                // The assistant offers the model by name, and the API calls the provider with the key stored before the edit.
                var editor = await scenario.Context.NewPageAsync();
                await editor.GotoAsync(stack.UiBaseAddress + $"/workflows/{original.Id}/edit");
                await RuntimeSupport.WaitUntilInteractiveAsync(editor);
                await Assertions.Expect(editor.Locator(".workflow-editor-page")).ToHaveAttributeAsync("data-editor-ready", "true");
                await editor.Locator("#open-ai-assistant").ClickAsync();
                await Assertions.Expect(editor.Locator("#ai-model option", new() { HasText = name + " renamed" })).ToHaveCountAsync(1);
                await editor.Locator("#ai-model").SelectOptionAsync(created.Id);
                stack.AiProvider.EnqueueText("The renamed model answered without changing the workflow.");
                await editor.Locator("#ai-message").FillAsync("Which model answers? Do not change the workflow.");
                await editor.Locator("#ai-send").ClickAsync();
                await Assertions.Expect(editor.Locator(".ai-message[data-role='assistant']").Last)
                    .ToContainTextAsync("The renamed model answered", new() { Timeout = 30_000 });
                var request = stack.AiProvider.Requests.Last();
                Assert.Equal((AiProviderTestHost.HashKey(key), "glm-5.3", "medium"), (request.KeyHash, request.Model, request.ReasoningEffort));
                Assert.DoesNotContain(key, request.Body);
                Assert.DoesNotContain(key, await editor.ContentAsync());
                await editor.CloseAsync();

                // Delete it after confirming.
                scenario.ExpectDialogOnce("confirm",
                    $"Delete AI model '{name} renamed'? Authors can no longer select it, and its stored key is removed.", accept: true);
                await page.Locator("tbody tr", new() { HasText = name + " renamed" })
                    .GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".alert-success")).ToContainTextAsync($"AI model {name} renamed deleted.");
                await Assertions.Expect(page.Locator("tbody tr", new() { HasText = name })).ToHaveCountAsync(0);
                Assert.DoesNotContain(stack.ApiOutputLines.Concat(stack.UiOutputLines), line => line.Contains(key, StringComparison.Ordinal));

                // Only settings administrators manage models.
                await RuntimeSupport.ApplyIdentityAsync(scenario, "ai-models-viewer", ["viewer"]);
                await scenario.OpenUiAsync("ai-models");
                await Assertions.Expect(page.Locator(".alert-danger")).ToContainTextAsync("does not have the configured settings administrator role");
                await Assertions.Expect(page.Locator("tbody tr")).ToHaveCountAsync(0);
                Assert.Equal(0, stack.AiProvider.UnexpectedRequests);
            }
            finally
            {
                foreach (var leftover in (await stack.SetupClient.ListAiModelsAsync()).Where(model => model.Name.StartsWith(name, StringComparison.Ordinal)))
                    await stack.SetupClient.DeleteAiModelAsync(leftover.Id, leftover.Revision);
            }
        });
    }
}
