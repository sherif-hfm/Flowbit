using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>
/// The editor assistant end to end with a scripted upstream model: the real API runs the agent, its file tools, Flowbit's checks,
/// layout and streaming; the loopback provider only decides which tools the model calls and what it answers.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class AiAuthoringSmokeTests(BrowserStackFixture stack)
{
    private static readonly string[] Tools = ["get_files_list", "get_file", "save_append_file", "edit_file", "validate_workflow", "validate_item"];
    private const string SyncNote = "workflow.json now contains the current diagram from the editor.";
    // Records the type of every blob the page turns into a download URL.
    private const string RecordBlobTypes = "() => { const create = URL.createObjectURL.bind(URL); window.flowbitBlobTypes = []; URL.createObjectURL = blob => { window.flowbitBlobTypes.push(blob.type); return create(blob); }; }";

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task H6_CreateApplyUndoSaveAnswerAndEdit(int width, int height)
    {
        await using var scenario = await stack.CreateScenario($"h6-ai-authoring-{width}", width, height);
        await scenario.RunAsync("ai-authoring", async () =>
        {
            await RuntimeSupport.ApplyIdentityAsync(scenario, "ai-author", ["admin"]);
            // The Workflows page offers the same skill download as the panel, for authors who use an outside agent.
            var page = await scenario.OpenUiAsync("workflows");
            await Assertions.Expect(page.Locator("#definitions-heading + p")).ToHaveTextAsync(new Regex(@"^[\d,]+ workflow families$"));
            await page.EvaluateAsync(RecordBlobTypes);
            var fromWorkflows = await page.RunAndWaitForDownloadAsync(() => page.Locator("#ai-skill").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Download Flowbit AI skill", Exact = true }).ClickAsync());
            Assert.Equal("flowbit-authoring.zip", fromWorkflows.SuggestedFilename);
            Assert.Equal(new[] { "application/zip" }, await page.EvaluateAsync<string[]>("window.flowbitBlobTypes"));
            await Assertions.Expect(page.Locator("#ai-skill-error")).ToHaveCountAsync(0);
            await page.Locator("#ai-skill").ScreenshotAsync(new LocatorScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "ai-skill-workflows.png") });
            page = await scenario.OpenUiAsync("workflows/new");
            await ReadyAsync(page);
            await OpenAssistantAsync(page);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "ai-connection.png"), FullPage = true });
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
            // The configured model's key travels only from the API to the provider; the author never types one.
            const string key = BrowserStackFixture.FlashKey;
            const string catalogValue = "never-send-this-catalog-value";
            string? catalogKey = null;
            if (width == 1440)
            {
                catalogKey = "ai-test-" + Guid.NewGuid().ToString("N");
                await stack.SetupClient.CreateSharedVariableAsync(new(catalogKey, "string", false, false, true,
                    JsonSerializer.SerializeToElement(catalogValue)));
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

            // A new diagram: the model reads the docs, saves workflow.json and open-questions.md, then ends its turn.
            const string request = "Create a purchase request with one review and then archive it.";
            var start = stack.AiProvider.Requests.Count;
            stack.AiProvider.EnqueueTools([("get_files_list", new { }), ("get_file", new { path = "node-reference.md", startLine = 1, lineCount = 40 })]);
            stack.AiProvider.EnqueueCreate("AI purchase review", "Which role reviews the request? Assumed: the Agent role, with a required claim.");
            stack.AiProvider.EnqueueText("I created the purchase review workflow. Review the open questions before applying it.");
            await SendAsync(page, request);
            await Assertions.Expect(page.Locator("#ai-apply")).ToBeEnabledAsync();
            await Assertions.Expect(page.Locator("#ai-open-questions")).ToContainTextAsync("Which role reviews the request?");
            await Assertions.Expect(page.Locator(".ai-message[data-role='assistant']").Last).ToContainTextAsync("I created the purchase review workflow.");
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync("New Workflow");
            var created = stack.AiProvider.Requests.Skip(start).ToArray();
            Assert.Equal(3, created.Length);
            AssertAgentRequests(created, key);
            Assert.Equal(created[0].Session, Assert.Single(created.Select(item => item.Session).Distinct()));
            Assert.Equal(2, created[0].Conversation.Length);
            if (width == 1440)
            {
                Assert.StartsWith(request + "\n\n--- requirements.pdf, page 1 ---\nCorrected requirement: one Agent reviews each purchase request.", created[0].LastUserMessage);
                Assert.Contains("\n\n--- Selected shared variables ---\n", created[0].LastUserMessage);
                Assert.Contains(catalogKey!, created[0].LastUserMessage);
            }
            else Assert.Equal(request, created[0].LastUserMessage);
            Assert.All(created, item => Assert.DoesNotContain(catalogValue, item.Body));
            // GLM's reasoning is replayed with its tool calls on the next request.
            Assert.Contains(created[1].Conversation, message => message.StartsWith("assistant", StringComparison.Ordinal)
                && message.Contains("reasoning:" + AiProviderTestHost.Reasoning, StringComparison.Ordinal));
            Assert.Equal("tool", created[1].LastRole);
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
            // The editor's workflow key wins over the key the model wrote, and the compact layout placed every node.
            Assert.StartsWith("workflow-", first.WorkflowKey);
            Assert.Equal(4, first.Definition.FlowNodes.Count);
            Assert.All(first.Definition.FlowNodes, node => Assert.True(node.X > 0 && node.Y > 0));

            // A new conversation about the saved diagram: a question gets an answer and no proposal.
            await OpenAssistantAsync(page);
            const string question = "What does this workflow do? Do not change it.";
            stack.AiProvider.EnqueueText("Each purchase request gets one Agent review and is then archived.");
            await SendAsync(page, question);
            await Assertions.Expect(page.Locator(".ai-message[data-role='assistant']").Last).ToContainTextAsync("one Agent review");
            await Assertions.Expect(page.Locator("#ai-apply")).ToHaveCountAsync(0);
            var answered = stack.AiProvider.Requests.Last();
            Assert.Equal(question, answered.LastUserMessage);
            Assert.NotEqual(created[0].Session, answered.Session);

            // The same conversation continues with an edit_file change of the saved workflow.
            start = stack.AiProvider.Requests.Count;
            stack.AiProvider.EnqueueRename("AI purchase review", "AI purchasing process");
            stack.AiProvider.EnqueueText("Renamed the workflow; nothing else changed.");
            await SendAsync(page, "Rename this workflow AI purchasing process; keep its behavior.");
            await Assertions.Expect(page.Locator("#ai-apply")).ToBeEnabledAsync();
            var edited = stack.AiProvider.Requests.Skip(start).ToArray();
            Assert.Equal(2, edited.Length);
            AssertAgentRequests(edited, key);
            Assert.Equal("Rename this workflow AI purchasing process; keep its behavior.", edited[0].LastUserMessage);
            Assert.Equal(answered.Session, edited[0].Session);
            Assert.Equal(answered.Conversation, edited[0].Conversation[..answered.Conversation.Length]);
            await page.Locator("#ai-apply").ClickAsync();
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("Applied to the editor");
            // The browser receives a ZIP, not JSON, and the conversation's notice stays.
            await page.EvaluateAsync(RecordBlobTypes);
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Download Flowbit AI skill", Exact = true }).ClickAsync());
            Assert.Equal("flowbit-authoring.zip", download.SuggestedFilename);
            Assert.Equal(new[] { "application/zip" }, await page.EvaluateAsync<string[]>("window.flowbitBlobTypes"));
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("Applied to the editor");
            await Assertions.Expect(page.Locator("#ai-skill-error")).ToHaveCountAsync(0);
            var zipPath = Path.Combine(scenario.ArtifactDirectory, "flowbit-authoring.zip");
            await download.SaveAsAsync(zipPath);
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/SKILL.md", StringComparison.Ordinal));
                Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/references/workflow.schema.json", StringComparison.Ordinal));
                Assert.Contains(archive.Entries, entry => entry.FullName == "flowbit-authoring/manifest.json");
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
            var storage = await page.EvaluateAsync<string>("JSON.stringify({local:{...localStorage},session:{...sessionStorage}})");
            Assert.DoesNotContain(key, storage);
            Assert.DoesNotContain(key, JsonSerializer.Serialize(second.Definition));
            Assert.DoesNotContain(stack.ApiOutputLines.Concat(stack.UiOutputLines), line => line.Contains(key, StringComparison.Ordinal));
            Assert.Equal(0, stack.AiProvider.UnexpectedRequests);
            await page.Locator("#fitViewBtn").ClickAsync();
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(scenario.ArtifactDirectory, "ai-applied-diagram.png"), FullPage = true });
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task H7_StaleProposalCancellationIdentityAndNavigationIsolation(int width, int height)
    {
        await using var scenario = await stack.CreateScenario($"h7-ai-lifecycle-{width}", width, height);
        await scenario.RunAsync("ai-lifecycle", async () =>
        {
            var original = await RuntimeSupport.PublishAsync(stack, "editor-basic.json");
            await RuntimeSupport.ApplyIdentityAsync(scenario, "ai-author", ["admin"]);
            var page = await scenario.OpenUiAsync($"workflows/{original.Id}/edit");
            await ReadyAsync(page);
            await OpenAssistantAsync(page);
            stack.AiProvider.EnqueueRename(original.Name, "Stale proposal");
            stack.AiProvider.EnqueueText("Renamed the workflow Stale proposal.");
            await SendAsync(page, "Rename the workflow Stale proposal.");
            await Assertions.Expect(page.Locator("#ai-apply")).ToBeEnabledAsync();
            await page.Locator("#wfName").FillAsync("Manual edit wins");
            await Assertions.Expect(page.Locator("#ai-apply")).ToBeDisabledAsync();
            await Assertions.Expect(page.GetByText("The editor changed.", new PageGetByTextOptions { Exact = false })).ToBeVisibleAsync();

            // The next message starts from the edited diagram; its model call is cancelled while the provider is still working.
            var delayed = stack.AiProvider.EnqueueText("This reply is never delivered.", delayed: true);
            await page.Locator("#ai-message").FillAsync("Wait while preparing another proposal.");
            await page.Locator("#ai-send").ClickAsync();
            await delayed.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(SyncNote + "\n\nWait while preparing another proposal.", stack.AiProvider.Requests.Last().LastUserMessage);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("AI request cancelled");
            await delayed.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            delayed.Complete();
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync("Manual edit wins");

            var other = await scenario.Context.NewPageAsync();
            await other.GotoAsync(stack.UiBaseAddress + $"/workflows/{original.Id}/edit");
            await ReadyAsync(other);
            await OpenAssistantAsync(other);
            // Each open assistant keeps its own model selection and conversation.
            await other.Locator("#ai-model").SelectOptionAsync(stack.Glm53ModelId);
            await Assertions.Expect(page.Locator("#ai-model")).ToHaveValueAsync(stack.FlashModelId);
            var identityPage = await scenario.Context.NewPageAsync();
            var identity = new IdentityScreen(identityPage, stack.UiBaseAddress);
            await identity.GenerateAndApplyIdentityAsync("ai-other-author", ["admin"]);
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("The conversation was cleared");
            await Assertions.Expect(page.Locator("#ai-apply, #ai-continue")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".ai-message")).ToHaveCountAsync(0);
            await identityPage.CloseAsync();
            await other.CloseAsync();
            await page.Locator("#ai-message").FillAsync("Unsent text that a new conversation clears.");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New conversation", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("#ai-message")).ToHaveValueAsync("");

            scenario.ExpectDialogOnce("confirm", "Leave the editor? Unsaved changes will be lost.", accept: true);
            if (width == 390)
            {
                // The narrow drawer fills the editor area, so the user leaves through the app navigation with the assistant still open.
                await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Open navigation", Exact = true }).ClickAsync();
                await page.Locator("#primary-navigation a[href='workflows']").ClickAsync();
            }
            else await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to Workflows", Exact = true }).Last.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("/workflows$"));
            // Let the list page finish loading, so the next navigation cannot be aborted by it.
            await Assertions.Expect(page.Locator("#definitions-heading + p")).ToHaveTextAsync(new Regex(@"^[\d,]+ workflow families$"));
            await scenario.OpenUiAsync($"workflows/{original.Id}/edit");
            await ReadyAsync(page);
            await OpenAssistantAsync(page);
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync(original.Name);
            Assert.Equal(0, stack.AiProvider.UnexpectedRequests);
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task H8_TruncationFailureCancellationAndContinue(int width, int height)
    {
        await using var scenario = await stack.CreateScenario($"h8-ai-recovery-{width}", width, height);
        await scenario.RunAsync("ai-recovery", async () =>
        {
            var original = await RuntimeSupport.PublishAsync(stack, "editor-basic.json");
            await RuntimeSupport.ApplyIdentityAsync(scenario, "ai-recovery-author", ["admin"]);
            var page = await scenario.OpenUiAsync($"workflows/{original.Id}/edit");
            await ReadyAsync(page);
            await OpenAssistantAsync(page);
            const string key = BrowserStackFixture.FlashKey;
            const string requirement = "Rename this workflow Incremental recovery, keeping every review rule unchanged.";
            var start = stack.AiProvider.Requests.Count;

            // The model reads the docs, then its next response reaches the output limit: the run pauses.
            stack.AiProvider.EnqueueTools([("get_files_list", new { folder = "examples" }), ("get_file", new { path = "node-reference.md", startLine = 1, lineCount = 40 })]);
            stack.AiProvider.EnqueueTruncation();
            await SendAsync(page, requirement);
            await Assertions.Expect(page.Locator("#ai-continue")).ToBeEnabledAsync();
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("output limit");
            await Assertions.Expect(page.Locator("#ai-progress-stage")).ToContainTextAsync("output limit");
            await Assertions.Expect(page.Locator("#ai-apply")).ToHaveCountAsync(0);

            // Continue repeats the stopped call; the provider is unavailable and nothing is retried.
            stack.AiProvider.EnqueueFailure(HttpStatusCode.ServiceUnavailable);
            await page.Locator("#ai-continue").ClickAsync();
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("temporarily unavailable");
            await Assertions.Expect(page.Locator("#ai-continue")).ToBeEnabledAsync();

            // Continue again: the model renames the workflow, and the call after it is cancelled while the provider works.
            stack.AiProvider.EnqueueRename(original.Name, "Incremental recovery");
            var waiting = stack.AiProvider.EnqueueText("Renamed the workflow; every review rule is unchanged.", delayed: true);
            await page.Locator("#ai-continue").ClickAsync();
            await waiting.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assertions.Expect(page.Locator("#ai-progress-stage")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#ai-model-wait")).ToContainTextAsync("Waiting for the model:");
            // The skill download is independent of the run, which keeps waiting for the model.
            var skill = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Download Flowbit AI skill", Exact = true }).ClickAsync());
            Assert.Equal("flowbit-authoring.zip", skill.SuggestedFilename);
            await Assertions.Expect(page.Locator("#ai-model-wait")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#ai-continue")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#ai-apply")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync(original.Name);
            await Assertions.Expect(page.Locator("#ai-progress-metrics")).ToContainTextAsync(new Regex(@"^(?:[3-9]|\d{2,}) s this run · \d+ of 40 model calls"));
            await Assertions.Expect(page.Locator("#ai-draft-progress")).ToHaveTextAsync("workflow.json: 4 nodes · 3 connections");
            var keptProgress = await page.Locator("#ai-draft-progress").InnerTextAsync();
            var elapsedBeforeCancel = int.Parse(Regex.Match(await page.Locator("#ai-progress-metrics").InnerTextAsync(), @"\d+").Value);
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 2"));
            await page.Locator(".ai-progress").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "ai-incremental-progress.png"), FullPage = true });
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await waiting.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            waiting.Complete();
            await Assertions.Expect(page.Locator("#ai-continue")).ToBeEnabledAsync();
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("cancelled");
            await Assertions.Expect(page.Locator("#ai-progress-stage")).ToContainTextAsync("cancelled");
            await Assertions.Expect(page.Locator("#ai-model-wait")).ToHaveCountAsync(0);
            var elapsedAfterCancel = int.Parse(Regex.Match(await page.Locator("#ai-progress-metrics").InnerTextAsync(), @"\d+").Value);
            Assert.True(elapsedAfterCancel >= elapsedBeforeCancel);
            await page.Locator(".ai-progress").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "ai-continuation.png"), FullPage = true });

            var initial = stack.AiProvider.Requests.Skip(start).ToArray();
            Assert.Equal(5, initial.Length);
            AssertAgentRequests(initial, key);
            Assert.Single(initial.Select(item => item.Session).Distinct());
            Assert.Equal(requirement, initial[0].LastUserMessage);
            Assert.Equal(2, initial[0].Conversation.Length);
            // Each Continue sends exactly the request that stopped: the results of the completed tool calls, not a new message.
            Assert.Equal("tool", initial[1].LastRole);
            Assert.Contains(initial[1].Conversation, message => message.Contains(":get_files_list:", StringComparison.Ordinal)
                && message.Contains("reasoning:" + AiProviderTestHost.Reasoning, StringComparison.Ordinal));
            Assert.Equal(initial[1].Conversation, initial[2].Conversation);
            Assert.Equal(initial[1].Conversation, initial[3].Conversation);
            Assert.Equal("tool", initial[4].LastRole);
            Assert.Contains(initial[4].Conversation, message => message.Contains(":edit_file:", StringComparison.Ordinal));

            // Changing the composer does not change what Continue sends.
            await page.Locator("#ai-message").FillAsync("This unsent text must not change the continuation.");
            var resumedWaiting = stack.AiProvider.EnqueueText("Renamed the workflow; every review rule is unchanged.", delayed: true);
            await page.Locator("#ai-continue").ClickAsync();
            await resumedWaiting.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assertions.Expect(page.Locator("#ai-draft-progress")).ToHaveTextAsync(keptProgress);
            await Assertions.Expect(page.Locator("#ai-model-wait")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#ai-progress-metrics")).ToContainTextAsync("s this run");
            await page.Locator(".ai-progress").ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "ai-resumed-progress.png"), FullPage = true });
            resumedWaiting.Complete();
            await Assertions.Expect(page.Locator("#ai-apply")).ToBeEnabledAsync();
            var resumed = stack.AiProvider.Requests.Last();
            Assert.Equal(initial[4].Conversation, resumed.Conversation);
            Assert.Equal(initial[0].Session, resumed.Session);
            Assert.DoesNotContain("This unsent text", resumed.Body);
            AssertAgentRequests([resumed], key);
            await Assertions.Expect(page.Locator("#ai-continue")).ToHaveCountAsync(0);
            await page.Locator("#ai-apply").ClickAsync();
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("Applied to the editor");
            await CloseAssistantAsync(page);
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync("Incremental recovery");
            await HistoryAsync(page, "undoBtn");
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync(original.Name);
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Saved");
            Assert.Equal(0, stack.AiProvider.UnexpectedRequests);
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task H9_FailureKeepsStateModelChangeClearsItAndEditorEditsSync(int width, int height)
    {
        await using var scenario = await stack.CreateScenario($"h9-ai-checkpoint-stale-{width}", width, height);
        await scenario.RunAsync("ai-checkpoint-stale", async () =>
        {
            var original = await RuntimeSupport.PublishAsync(stack, "editor-basic.json");
            await RuntimeSupport.ApplyIdentityAsync(scenario, "ai-checkpoint-author", ["admin"]);
            var page = await scenario.OpenUiAsync($"workflows/{original.Id}/edit");
            await ReadyAsync(page);
            await OpenAssistantAsync(page);
            stack.AiProvider.EnqueueRename(original.Name, "Private draft only");
            stack.AiProvider.EnqueueFailure(HttpStatusCode.Unauthorized);
            await SendAsync(page, "Rename this workflow Private draft only.");
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("rejected the key");
            await Assertions.Expect(page.Locator("#ai-continue")).ToBeEnabledAsync();
            await Assertions.Expect(page.Locator("#ai-apply")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync(original.Name);
            await page.Locator("#ai-model").SelectOptionAsync(stack.Glm53ModelId);
            await Assertions.Expect(page.Locator("#ai-continue")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#ai-model")).ToHaveValueAsync(stack.Glm53ModelId);
            await page.Locator("#ai-model").SelectOptionAsync(stack.FlashModelId);

            stack.AiProvider.EnqueueRename(original.Name, "Fresh private draft");
            stack.AiProvider.EnqueueFailure(HttpStatusCode.Unauthorized);
            await SendAsync(page, "Rename this workflow Fresh private draft.");
            await Assertions.Expect(page.Locator("#ai-notice")).ToContainTextAsync("rejected the key");
            await Assertions.Expect(page.Locator("#ai-continue")).ToBeEnabledAsync();
            var paused = stack.AiProvider.Requests.Last();
            Assert.Equal("tool", paused.LastRole);
            await page.Locator("#wfName").FillAsync("Manual edit after failure");
            await Assertions.Expect(page.Locator("#ai-continue")).ToBeDisabledAsync();
            await Assertions.Expect(page.GetByText("The editor changed. Send a message to continue", new() { Exact = false })).ToBeVisibleAsync();

            // A message replaces the agent's workflow.json with the edited diagram and keeps the paused tool result before it.
            stack.AiProvider.EnqueueText("workflow.json now has the name you typed; I changed nothing else.");
            await SendAsync(page, "Keep the name I typed.");
            await Assertions.Expect(page.Locator(".ai-message[data-role='assistant']").Last).ToContainTextAsync("the name you typed");
            await Assertions.Expect(page.Locator("#ai-apply, #ai-continue")).ToHaveCountAsync(0);
            var synced = stack.AiProvider.Requests.Last();
            Assert.Equal(SyncNote + "\n\nKeep the name I typed.", synced.LastUserMessage);
            Assert.Equal(paused.Conversation, synced.Conversation[..paused.Conversation.Length]);
            Assert.Equal(paused.Conversation.Length + 1, synced.Conversation.Length);
            Assert.Equal(paused.Session, synced.Session);

            await page.GetByRole(AriaRole.Button, new() { Name = "New conversation", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("#ai-continue, .ai-message")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#ai-model")).ToHaveValueAsync(stack.FlashModelId);
            await CloseAssistantAsync(page);
            await HistoryAsync(page, "undoBtn");
            await Assertions.Expect(page.Locator("#wfName")).ToHaveValueAsync(original.Name);
            await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Saved");
            Assert.Equal(0, stack.AiProvider.UnexpectedRequests);
        });
    }

    /// <summary>What every agent request carries: the configured model's key, Flowbit's user agent, the model's settings and the six tools.</summary>
    private static void AssertAgentRequests(IEnumerable<AiProviderTestHost.Request> requests, string key) => Assert.All(requests, item =>
    {
        Assert.Equal(AiProviderTestHost.HashKey(key), item.KeyHash);
        Assert.Equal("Flowbit/1.0", item.UserAgent);
        Assert.Matches("^[0-9a-f]{32}$", item.Session);
        Assert.Equal("glm-5.3-flash", item.Model);
        Assert.Equal(BrowserStackFixture.FlashReasoning, item.ReasoningEffort);
        Assert.Equal(32_768, item.MaxTokens);
        Assert.Equal(Tools, item.ToolNames);
        Assert.StartsWith("Generate a Flowbit workflow based on these requirements.", item.Instructions);
        Assert.EndsWith("If workflow.json is already saved, it is the current workflow: change it as requested and keep everything else unchanged.", item.Instructions);
        Assert.DoesNotContain(key, item.Body);
    });

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
    // Authors only pick a configured model: the first one is the default, and there is no provider choice or key field.
    private async Task OpenAssistantAsync(IPage page)
    {
        await page.Locator("#open-ai-assistant").ClickAsync();
        await Assertions.Expect(page.Locator("#ai-model")).ToHaveValueAsync(stack.FlashModelId);
        await Assertions.Expect(page.Locator("#ai-model option:checked")).ToHaveTextAsync("GLM-5.3-Flash");
        await Assertions.Expect(page.Locator("#ai-key, #ai-provider")).ToHaveCountAsync(0);
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
