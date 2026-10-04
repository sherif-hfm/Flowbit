using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

[CollectionDefinition("AI requirements browser", DisableParallelization = true)]
public sealed class AiRequirementsBrowserCollection : ICollectionFixture<AiRequirementsBrowserFixture>;

public sealed class AiRequirementsBrowserFixture : IAsyncLifetime
{
    public BrowserStackFixture Stack { get; } = BrowserStackFixture.CreateForAiReview();
    public Task InitializeAsync() => Stack.InitializeAsync();
    public Task DisposeAsync() => Stack.DisposeAsync();
}

[Collection("AI requirements browser")]
public sealed class AiRequirementsSmokeTests(AiRequirementsBrowserFixture fixture)
{
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task H10_ParallelReviewCancelContinueApplyAndUndo(int width, int height)
    {
        var stack = fixture.Stack;
        await using var scenario = await stack.CreateScenario($"h10-ai-requirements-{width}", width, height);
        await scenario.RunAsync("reviewed-authoring", async () =>
        {
            var original = await RuntimeSupport.PublishAsync(stack, "editor-basic.json");
            await RuntimeSupport.ApplyIdentityAsync(scenario, "ai-reviewed-author", ["admin"]);
            var page = await scenario.OpenUiAsync($"workflows/{original.Id}/edit");
            await OpenAsync(page);
            stack.AiProvider.EnqueueAnalysis();
            stack.AiProvider.EnqueueNameEdit("Reviewed workflow");
            stack.AiProvider.EnqueueFinish();
            var first = stack.AiProvider.EnqueueReview(delayed: true);
            var second = stack.AiProvider.EnqueueReview(delayed: true);
            await page.Locator("#ai-message").FillAsync("Rename this workflow Reviewed workflow.");
            await page.Locator("#ai-send").ClickAsync();
            await Task.WhenAll(first.Arrived.Task, second.Arrived.Task).WaitAsync(TimeSpan.FromSeconds(30));
            await Assertions.Expect(page.Locator("#ai-active-calls")).ToContainTextAsync("2 active AI calls");
            await Assertions.Expect(page.Locator("#ai-model-wait")).ToContainTextAsync("group of calls");
            await Assertions.Expect(page.Locator("#ai-apply")).ToHaveCountAsync(0);
            await page.Locator("#ai-active-calls").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "parallel-review.png"), FullPage = true });
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Task.WhenAll(first.Cancelled.Task, second.Cancelled.Task).WaitAsync(TimeSpan.FromSeconds(20));
            await Assertions.Expect(page.Locator("#ai-continue")).ToBeEnabledAsync();
            await Assertions.Expect(page.Locator("#ai-active-calls")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync(original.Name);

            stack.AiProvider.EnqueueAnalysis();
            stack.AiProvider.EnqueueFinish();
            first = stack.AiProvider.EnqueueReview(delayed: true);
            second = stack.AiProvider.EnqueueReview(delayed: true);
            await page.Locator("#ai-continue").ClickAsync();
            await Task.WhenAll(first.Arrived.Task, second.Arrived.Task).WaitAsync(TimeSpan.FromSeconds(30));
            second.Complete(); first.Complete();
            await Assertions.Expect(page.Locator("#ai-apply")).ToBeEnabledAsync();
            await page.Locator("#ai-requirements-review > summary").ClickAsync();
            await Assertions.Expect(page.Locator("#ai-requirements-review")).ToContainTextAsync("Routing and roles: covered");
            await page.Locator("#ai-requirements-review").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "requirements-review.png"), FullPage = true });
            await page.Locator("#ai-apply").ClickAsync();
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync("Reviewed workflow");
            await page.GetByRole(AriaRole.Button, new() { Name = "Close AI assistant", Exact = true }).ClickAsync();
            await page.Locator("#editMenuSummary").ClickAsync();
            await page.Locator("#undoBtn").ClickAsync();
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync(original.Name);
        });
    }

    [Fact]
    public async Task H11_UnresolvedRequirementsBlockApplyAndMissingBehaviorIsRepaired()
    {
        var stack = fixture.Stack;
        await using var scenario = await stack.CreateScenario("h11-ai-requirements-blocked");
        await scenario.RunAsync("review-blocking", async () =>
        {
            var original = await RuntimeSupport.PublishAsync(stack, "editor-basic.json");
            await RuntimeSupport.ApplyIdentityAsync(scenario, "ai-review-blocking", ["admin"]);
            var page = await scenario.OpenUiAsync($"workflows/{original.Id}/edit");
            await OpenAsync(page);
            stack.AiProvider.EnqueueAnalysis(); stack.AiProvider.EnqueueFinish();
            stack.AiProvider.EnqueueReview("uncertain"); stack.AiProvider.EnqueueReview();
            await page.Locator("#ai-message").FillAsync("Use the approved workflow name.");
            await page.Locator("#ai-send").ClickAsync();
            await Assertions.Expect(page.GetByText("Which workflow name should be used?", new() { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#ai-apply")).ToHaveCountAsync(0);
            await page.Locator("#ai-requirements-review > summary").ClickAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "requirements-blocked.png"), FullPage = true });
            await page.GetByRole(AriaRole.Button, new() { Name = "New conversation", Exact = true }).ClickAsync();
            await page.Locator("#ai-key").FillAsync("synthetic-review-browser-key");
            stack.AiProvider.EnqueueAnalysis(); stack.AiProvider.EnqueueFinish();
            stack.AiProvider.EnqueueReview("missing"); stack.AiProvider.EnqueueReview();
            stack.AiProvider.EnqueueNameEdit("Repaired requirements"); stack.AiProvider.EnqueueFinish();
            stack.AiProvider.EnqueueReview(); stack.AiProvider.EnqueueReview();
            await page.Locator("#ai-message").FillAsync("Rename this workflow Repaired requirements.");
            await page.Locator("#ai-send").ClickAsync();
            await Assertions.Expect(page.Locator("#ai-apply")).ToBeEnabledAsync();
            await page.Locator("#ai-apply").ClickAsync();
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync("Repaired requirements");
            await page.GetByRole(AriaRole.Button, new() { Name = "Close AI assistant", Exact = true }).ClickAsync();
            await page.Locator("#editMenuSummary").ClickAsync();
            await page.Locator("#undoBtn").ClickAsync();
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync(original.Name);
        });
    }

    private static async Task OpenAsync(IPage page)
    {
        await RuntimeSupport.WaitUntilInteractiveAsync(page);
        await Assertions.Expect(page.Locator(".workflow-editor-page")).ToHaveAttributeAsync("data-editor-ready", "true");
        await page.Locator("#open-ai-assistant").ClickAsync();
        await Assertions.Expect(page.Locator("#ai-provider")).ToHaveValueAsync("opencode-go");
        await page.Locator("#ai-key").FillAsync("synthetic-review-browser-key");
    }
}
