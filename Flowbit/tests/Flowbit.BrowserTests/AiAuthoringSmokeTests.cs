using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

[Collection(BrowserCollection.Name)]
public sealed class AiAuthoringSmokeTests(BrowserStackFixture stack)
{
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task H6_ClarifyCreateApplyUndoSaveAndModify(int width, int height)
    {
        await using var scenario = await stack.CreateScenario($"h6-ai-authoring-{width}", width, height);
        await scenario.RunAsync("ai-authoring", async () =>
        {
            await RuntimeSupport.ApplyIdentityAsync(scenario, "ai-author", ["admin"]);
            var page = await scenario.OpenUiAsync("workflows/new");
            await ReadyAsync(page);
            await OpenAssistantAsync(page);
            if (width == 390)
            {
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Close AI assistant", Exact = true }).FocusAsync();
                await page.Keyboard.PressAsync("Shift+Tab");
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Download Flowbit AI skill", Exact = true })).ToBeFocusedAsync();
                await page.Keyboard.PressAsync("Escape");
                await Assertions.Expect(page.Locator(".workflow-ai-panel")).ToHaveCountAsync(0);
                await Assertions.Expect(page.Locator("#open-ai-assistant")).ToBeFocusedAsync();
                await OpenAssistantAsync(page);
            }
            const string key = "synthetic-browser-provider-key";
            await page.Locator("#ai-key").FillAsync(key);
            string? catalogKey = null;
            if (width == 1440)
            {
                catalogKey = "ai-test-" + Guid.NewGuid().ToString("N");
                await stack.SetupClient.CreateSharedVariableAsync(new(catalogKey, "string", false, false, true,
                    JsonSerializer.SerializeToElement("never-send-this-catalog-value")));
                await page.Locator(".ai-catalog > summary").ClickAsync();
                await page.Locator("#ai-catalog-search").FillAsync(catalogKey);
                await page.Locator("#ai-catalog-load").ClickAsync();
                var selection = page.Locator(".ai-catalog-option input");
                await Assertions.Expect(selection).ToHaveCountAsync(1);
                await Assertions.Expect(selection).Not.ToBeCheckedAsync();
                await selection.CheckAsync();
                await page.Locator(".ai-catalog > summary").ClickAsync();
                var pdfPath = Path.Combine(scenario.ArtifactDirectory, "requirements.pdf");
                await File.WriteAllBytesAsync(pdfPath, TextPdf());
                await page.Locator(".ai-document > summary").ClickAsync();
                await page.Locator("#ai-pdf").SetInputFilesAsync(pdfPath);
                await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("Extracted 1 page");
                await page.Locator(".ai-page > summary").ClickAsync();
                await Assertions.Expect(page.Locator("#ai-page-1")).ToHaveValueAsync(new Regex("purchase request"));
                await page.Locator("#ai-page-1").FillAsync("Corrected requirement: one Agent reviews each purchase request.");
            }
            stack.AiProvider.Enqueue("AI purchase review", clarification: true);
            await SendAsync(page, "Create a purchase request with one review and then archive it.");
            await Assertions.Expect(page.GetByText("Which role reviews the request?", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            if (width == 1440) Assert.Contains("Corrected requirement", Assert.Single(stack.AiProvider.Requests.Last().SourceTexts));
            if (width == 1440) Assert.Equal(catalogKey, Assert.Single(stack.AiProvider.Requests.Last().CatalogKeys));
            Assert.False(stack.AiProvider.Requests.Last().IncludesCatalogValue);
            await Assertions.Expect(page.Locator("#nodes .node")).ToHaveCountAsync(0);

            stack.AiProvider.Enqueue("AI purchase review");
            await SendAsync(page, "Use the Agent role; a claim is required.");
            await Assertions.Expect(page.Locator("#ai-apply")).ToBeEnabledAsync();
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync("New Workflow");
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 2"));
            await page.Locator("#ai-apply").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "ai-proposal.png"), FullPage = true });
            await page.Locator("#ai-apply").ClickAsync();
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("Applied to the editor");
            await CloseAssistantAsync(page);
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync("AI purchase review");
            await HistoryAsync(page, "undoBtn");
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync("New Workflow");
            await Assertions.Expect(page.Locator("#nodes .node")).ToHaveCountAsync(0);
            await HistoryAsync(page, "redoBtn");
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync("AI purchase review");
            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#editor-notice")).ToContainTextAsync("Saved unpublished version 1");
            var first = await stack.SetupClient.GetWorkflowAsync(CurrentId(page));
            Assert.False(first.IsPublished);
            Assert.Equal(stack.AiProvider.Requests.Last().WorkflowKey, first.WorkflowKey);
            Assert.StartsWith("workflow-", first.WorkflowKey);
            Assert.Equal(4, first.Definition.FlowNodes.Count);
            Assert.All(first.Definition.FlowNodes, node => Assert.True(node.X > 0 && node.Y > 0));

            await OpenAssistantAsync(page);
            await Assertions.Expect(page.Locator("#ai-key")).ToHaveValueAsync("");
            await page.Locator("#ai-key").FillAsync(key);
            stack.AiProvider.Enqueue("AI purchasing process");
            await SendAsync(page, "Rename this workflow AI purchasing process; keep its behavior.");
            await Assertions.Expect(page.Locator("#ai-apply")).ToBeEnabledAsync();
            await page.Locator("#ai-apply").ClickAsync();
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("Applied to the editor");
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Download Flowbit AI skill", Exact = true }).ClickAsync());
            var zipPath = Path.Combine(scenario.ArtifactDirectory, "flowbit-authoring.zip");
            await download.SaveAsAsync(zipPath);
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/SKILL.md", StringComparison.Ordinal));
                Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/references/workflow.schema.json", StringComparison.Ordinal));
            }
            await CloseAssistantAsync(page);
            await HistoryAsync(page, "undoBtn");
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Saved");
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync(first.Name);
            await HistoryAsync(page, "redoBtn");
            await page.Locator("#save-workflow-version").ClickAsync();
            await Assertions.Expect(page.Locator("#editor-notice")).ToContainTextAsync("Saved unpublished version 2");
            var second = await stack.SetupClient.GetWorkflowAsync(CurrentId(page));
            Assert.Equal(first.WorkflowKey, second.WorkflowKey);
            Assert.False(second.IsPublished);
            Assert.Equal("AI purchasing process", second.Name);
            Assert.Equal(first.Definition.FlowNodes.Select(node => (node.Id, node.X, node.Y)), second.Definition.FlowNodes.Select(node => (node.Id, node.X, node.Y)));
            Assert.Equal("AI purchase review", (await stack.SetupClient.GetWorkflowAsync(first.Id)).Name);
            Assert.All(stack.AiProvider.Requests.Where(request => request.WorkflowKey == first.WorkflowKey), request =>
                Assert.Equal(AiProviderTestHost.HashKey(key), request.KeyHash));
            var storage = await page.EvaluateAsync<string>("JSON.stringify({local:{...localStorage},session:{...sessionStorage}})");
            Assert.DoesNotContain(key, storage);
            Assert.DoesNotContain(key, JsonSerializer.Serialize(second.Definition));
            Assert.DoesNotContain(stack.ApiOutputLines.Concat(stack.UiOutputLines), line => line.Contains(key, StringComparison.Ordinal));
            await page.Locator("#fitViewBtn").ClickAsync();
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "ai-applied-diagram.png"), FullPage = true });
        });
    }

    [Fact]
    public async Task H7_StaleProposalCancellationIdentityAndNavigationIsolation()
    {
        await using var scenario = await stack.CreateScenario("h7-ai-lifecycle");
        await scenario.RunAsync("ai-lifecycle", async () =>
        {
            var original = await RuntimeSupport.PublishAsync(stack, "editor-basic.json");
            await RuntimeSupport.ApplyIdentityAsync(scenario, "ai-author", ["admin"]);
            var page = await scenario.OpenUiAsync($"workflows/{original.Id}/edit");
            await ReadyAsync(page);
            await OpenAssistantAsync(page);
            await page.Locator("#ai-key").FillAsync("synthetic-first-session-key");
            stack.AiProvider.Enqueue("Stale proposal");
            await SendAsync(page, "Rename the workflow Stale proposal.");
            await Assertions.Expect(page.Locator("#ai-apply")).ToBeEnabledAsync();
            await page.Locator("#wfName").FillAsync("Manual edit wins");
            await Assertions.Expect(page.Locator("#ai-apply")).ToBeDisabledAsync();
            await Assertions.Expect(page.GetByText("The editor changed.", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();

            var delayed = stack.AiProvider.Enqueue("Cancelled proposal", delayed: true);
            await page.Locator("#ai-message").FillAsync("Wait while preparing another proposal.");
            await page.Locator("#ai-send").ClickAsync();
            await delayed.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("AI request cancelled");
            await delayed.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            delayed.Complete();
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync("Manual edit wins");

            var other = await scenario.Context.NewPageAsync();
            await other.GotoAsync(stack.UiBaseAddress + $"/workflows/{original.Id}/edit");
            await ReadyAsync(other);
            await OpenAssistantAsync(other);
            await Assertions.Expect(other.Locator("#ai-key")).ToHaveValueAsync("");
            await other.Locator("#ai-key").FillAsync("synthetic-second-session-key");
            await Assertions.Expect(page.Locator("#ai-key")).ToHaveValueAsync("synthetic-first-session-key");
            var identityPage = await scenario.Context.NewPageAsync();
            var identity = new IdentityScreen(identityPage, stack.UiBaseAddress);
            await identity.GenerateAndApplyIdentityAsync("ai-other-author", ["admin"]);
            await Assertions.Expect(page.Locator("#ai-key")).ToHaveValueAsync("");
            await Assertions.Expect(other.Locator("#ai-key")).ToHaveValueAsync("");
            await Assertions.Expect(page.Locator("#ai-apply")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".ai-message")).ToHaveCountAsync(0);
            await identityPage.CloseAsync();
            await other.CloseAsync();
            await page.Locator("#ai-key").FillAsync("synthetic-reset-key");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New conversation", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("#ai-key")).ToHaveValueAsync("");

            scenario.ExpectDialogOnce("confirm", "Leave the editor? Unsaved changes will be lost.", accept: true);
            await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to Workflows", Exact = true }).Last.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/workflows$"));
            await scenario.OpenUiAsync($"workflows/{original.Id}/edit");
            await ReadyAsync(page);
            await OpenAssistantAsync(page);
            await Assertions.Expect(page.Locator("#ai-key")).ToHaveValueAsync("");
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync(original.Name);
        });
    }

    private static long CurrentId(IPage page) => long.Parse(Regex.Match(page.Url, @"/workflows/(\d+)/edit").Groups[1].Value);
    private static byte[] TextPdf()
    {
        const string content = "BT /F1 12 Tf 40 750 Td (Each purchase request requires one review and then archival.) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ];
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
    private static async Task ReadyAsync(IPage page)
    {
        await RuntimeSupport.WaitUntilInteractiveAsync(page);
        await Assertions.Expect(page.Locator(".workflow-editor-page")).ToHaveAttributeAsync("data-editor-ready", "true");
    }
    private static async Task OpenAssistantAsync(IPage page)
    {
        await page.Locator("#open-ai-assistant").ClickAsync();
        await Assertions.Expect(page.Locator("#ai-provider")).ToHaveValueAsync("opencode-go");
    }
    private static Task CloseAssistantAsync(IPage page) => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Close AI assistant", Exact = true }).ClickAsync();
    private static async Task SendAsync(IPage page, string text)
    {
        await page.Locator("#ai-message").FillAsync(text);
        await page.Locator("#ai-send").ClickAsync();
        await Assertions.Expect(page.Locator("#ai-message")).ToHaveValueAsync("", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
    }
    private static async Task HistoryAsync(IPage page, string button)
    {
        await page.Locator("#editMenuSummary").ClickAsync();
        await page.Locator("#" + button).ClickAsync();
    }
}
