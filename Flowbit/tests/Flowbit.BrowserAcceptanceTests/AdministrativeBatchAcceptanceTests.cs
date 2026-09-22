using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>
/// Worker-enabled acceptance for the administrative display extraction. All
/// workflow state is created and inspected through HTTP. Response gates delay
/// authentic API responses; the recovery scenario stops only its owned Worker.
/// </summary>
[Collection(AcceptanceCollection.Name)]
public sealed class AdministrativeBatchAcceptanceTests(AcceptanceFixture fixture)
{
    private BrowserStackFixture Stack => fixture.Stack;

    [Fact]
    public async Task A1_OrdinaryLifecyclePagingPollingAndInstanceLinks()
    {
        await RunAsync("a1-ordinary-batch", async session =>
        {
            var workflow = await session.VariantAsync("ordinary");
            for (var i = 0; i < 51; i++) await session.Client.StartInstanceAsync(workflow.Id);
            await session.OpenPageAsync();
            await session.SelectWorkflowAsync(workflow);
            await session.Page.GetByRole(AriaRole.Button, new() { Name = "Search positions", Exact = true }).ClickAsync();
            await Assertions.Expect(session.Page.Locator("#admin-candidates-heading").Locator("..")).ToContainTextAsync("51");
            await session.Page.Locator("input[name='admin-selection-mode']").Nth(1).CheckAsync();
            await session.Page.Locator("#admin-reason").FillAsync("Acceptance: 51 ordinary positions");
            await session.Page.Locator("#admin-common-variables").FillAsync("{\"approvalNote\":\"Rendered by polling\"}");
            await session.Page.GetByRole(AriaRole.Button, new() { Name = "Prepare batch asynchronously", Exact = true }).ClickAsync();
            await session.StatusAsync("Preparing");
            await session.ScreenshotsAsync("ordinary-preparing");
            var batch = (await session.Client.GetAdministrativeActionBatchesAsync()).Items
                .Single(row => row.WorkflowDefinitionId == workflow.Id);
            await Stack.StartWorkerAsync();
            await session.StatusAsync("Ready"); // No Refresh or input while awaiting the real timer.
            await Stack.StopWorkerAsync();
            var ready = await session.Client.GetAdministrativeActionBatchAsync(batch.Id);
            Assert.Equal(51, ready.Summary.EligibleItemCount);
            Assert.Equal(50, await session.Current.Locator("tbody tr").CountAsync());
            await session.Current.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
            await Assertions.Expect(session.Current.Locator("tbody tr")).ToHaveCountAsync(1);
            await Assertions.Expect(session.Current).ToContainTextAsync("Page 2 of 2");
            await session.Page.GetByLabel("Filter batch items by status").SelectOptionAsync("failed");
            await Assertions.Expect(session.Current).ToContainTextAsync("No items in this view");
            await Assertions.Expect(session.Current.Locator(".pagination-bar")).ToHaveCountAsync(0);
            await session.ScreenshotsAsync("items-empty-filter");
            await session.Page.GetByLabel("Filter batch items by status").SelectOptionAsync("");
            await Assertions.Expect(session.Current.Locator("tbody tr")).ToHaveCountAsync(50);
            await session.ScreenshotsAsync("ordinary-ready");
            await session.ConfirmAsync();
            await session.StatusAsync("Queued");
            await session.ScreenshotsAsync("ordinary-queued");
            await Stack.StartWorkerAsync();
            await session.StatusAsync("Completed");
            var done = await session.Client.GetAdministrativeActionBatchAsync(batch.Id);
            Assert.Equal(51, done.Summary.SucceededItemCount);
            Assert.NotNull(done.Summary.CompletedAt);
            Assert.Equal(Session.Actor, done.Summary.PreparedBy);
            Assert.Equal(Session.Actor, done.Summary.ConfirmedBy);
            Assert.Equal("Rendered by polling", done.CommonVariables["approvalNote"].GetString());
            Assert.Equal(Session.Roles.Order(), done.PreparedByRoles.Order());
            Assert.Equal(Session.Roles.Order(), done.ConfirmedByRoles!.Order());
            await Assertions.Expect(session.Current).ToContainTextAsync(done.Summary.SourceNodeName);
            await Assertions.Expect(session.Current).ToContainTextAsync($"definition #{workflow.Id}");
            await session.CountAsync("Succeeded", 51);
            await Assertions.Expect(session.Current.Locator("tbody tr .status-badge").First).ToHaveTextAsync("Succeeded");
            await Assertions.Expect(session.History.Locator("tr.table-active")).ToContainTextAsync("51 / 51");
            await session.Current.Locator("details").Filter(new() { HasText = "Common variables" }).Locator("summary").PressAsync("Enter");
            await Assertions.Expect(session.Current.Locator("details[open] pre").First).ToContainTextAsync("Rendered by polling");
            await session.ScreenshotsAsync("ordinary-completed");
            await session.EvidenceAsync("ordinary", done);

            await session.OpenBatchAsync(batch.Id);
            var link = session.Current.Locator("tbody tr a").First;
            var href = await link.GetAttributeAsync("href");
            await link.ClickAsync();
            await Assertions.Expect(session.Page).ToHaveURLAsync(new Regex(Regex.Escape(href!) + "$"));
            await session.Page.GoBackAsync();
            await session.StatusAsync("Completed");
            await Assertions.Expect(session.Page).ToHaveURLAsync(new Regex($"administrative-actions\\?batchId={batch.Id}$"));
        });
    }

    [Fact]
    public async Task A2_RecentBatchPagingKeyboardOpenAndReadyCancellation()
    {
        await RunAsync("a2-history-cancellation", async session =>
        {
            var workflow = await session.VariantAsync("history");
            await session.Client.StartInstanceAsync(workflow.Id);
            var batchIds = new List<long>();
            for (var i = 0; i < 26; i++) batchIds.Add((await session.PrepareAsync(workflow.Id)).Summary.Id);
            await Stack.StartWorkerAsync();
            foreach (var id in batchIds) await session.WaitBatchAsync(id, "ready");
            await Stack.StopWorkerAsync();
            await session.OpenBatchAsync(batchIds[^1]);
            await session.Page.GetByLabel("Filter batches by status").SelectOptionAsync("ready");
            await Assertions.Expect(session.History.Locator("tbody tr")).ToHaveCountAsync(25);
            await session.History.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
            await Assertions.Expect(session.History).ToContainTextAsync("Page 2 of");
            await Assertions.Expect(session.Current).ToContainTextAsync("Page 1 of 1");
            var firstRow = session.History.Locator("tbody tr").First;
            var openedId = long.Parse((await firstRow.Locator("td").First.InnerTextAsync()).TrimStart('#'));
            await firstRow.GetByRole(AriaRole.Button, new() { Name = "Open", Exact = true }).PressAsync("Enter");
            await Assertions.Expect(session.Current.Locator(".page-eyebrow")).ToHaveTextAsync($"Batch #{openedId}");
            await session.StatusAsync("Ready");
            await session.CancelAsync("Cancel ready acceptance batch");
            await session.StatusAsync("Cancelled");
            var cancelled = await session.Client.GetAdministrativeActionBatchAsync(openedId);
            Assert.Equal("Cancel ready acceptance batch", cancelled.CancellationReason);
            Assert.NotNull(cancelled.Summary.CompletedAt);
            Assert.Equal(1, cancelled.Summary.CancelledItemCount);
            Assert.Equal(0, cancelled.Summary.SucceededItemCount);
            await session.Page.GetByLabel("Filter batches by status").SelectOptionAsync("failed");
            await Assertions.Expect(session.History).ToContainTextAsync("No administrative batches");
            await Assertions.Expect(session.History.Locator(".pagination-bar")).ToHaveCountAsync(0);
            await session.ScreenshotsAsync("history-empty-filter", session.History);
            await session.Page.GetByLabel("Filter batches by status").SelectOptionAsync("");
            await Assertions.Expect(session.History.Locator("tbody tr")).ToHaveCountAsync(25);
            await session.EvidenceAsync("ready-cancelled", cancelled);
            await session.ScreenshotsAsync("history-and-ready-cancellation");
        });
    }

    [Theory]
    [InlineData("forceParent", 51, 153, "Force parent")]
    [InlineData("completeAllChildren", 1, 3, "Complete all unfinished children")]
    public async Task A3_MultiInstanceFrozenAuditAndCounts(string mode, int positions, int affected, string label)
    {
        await RunAsync($"a3-mi-{mode}", async session =>
        {
            var workflow = await session.Client.CreateAndPublishAsync(session.FixturePath("runtime-mi.json"));
            for (var i = 0; i < positions; i++) await session.Client.StartInstanceAsync(workflow.Id);
            var batch = await session.PrepareAsync(workflow.Id, 201, mode,
                new() { ["reviewComment"] = JsonSerializer.SerializeToElement("MI Worker acceptance") });
            await Stack.StartWorkerAsync();
            await session.OpenBatchAsync(batch.Summary.Id);
            await session.StatusAsync("Ready");
            await Assertions.Expect(session.Current).ToContainTextAsync(label);
            await session.ConfirmAsync();
            await session.StatusAsync("Completed");
            var done = await session.Client.GetAdministrativeActionBatchAsync(batch.Summary.Id);
            Assert.Equal(positions, done.Summary.SucceededItemCount);
            Assert.Equal(affected, done.Summary.TotalAffectedTaskCount);
            Assert.Equal(mode, done.Summary.MultiInstanceMode);
            await session.CountAsync("Affected tasks", affected);
            await session.EvidenceAsync("mi-completed", done);
            await session.ScreenshotsAsync("mi-completed");
        });
    }

    [Fact]
    public async Task A4_TimerBoundaryRetainsSubscriptionAndVariableContract()
    {
        await RunAsync("a4-timer-boundary", async session =>
        {
            var workflow = await session.VariantAsync("timer", definition =>
            {
                definition["flowNodes"]!.AsArray().Add(JsonNode.Parse("""{"id":4,"name":"Deadline","type":"timerBoundaryEvent","attachedToRef":2,"cancelActivity":true,"timer":{"timeDuration":"PT1H"},"x":270,"y":160}"""));
                definition["sequenceFlows"]!.AsArray().Add(JsonNode.Parse("""{"id":103,"name":"Timeout","sourceRef":4,"targetRef":3}"""));
            });
            await session.Client.StartInstanceAsync(workflow.Id);
            var batch = await session.PrepareAsync(workflow.Id, 103, boundary: 4);
            await Stack.StartWorkerAsync();
            await session.OpenBatchAsync(batch.Summary.Id);
            await session.StatusAsync("Ready");
            await Assertions.Expect(session.Current).ToContainTextAsync("Timer boundary");
            await session.ConfirmAsync();
            await session.StatusAsync("Completed");
            await Assertions.Expect(session.Current).ToContainTextAsync("timer subscription");
            var done = await session.Client.GetAdministrativeActionBatchAsync(batch.Summary.Id);
            Assert.Empty(done.CommonVariables);
            var item = Assert.Single((await session.Client.GetAdministrativeActionBatchItemsAsync(batch.Summary.Id)).Items);
            Assert.NotNull(item.TimerSubscriptionId);
            Assert.Equal("succeeded", item.Status);
            await session.EvidenceAsync("timer-completed", new { batch = done, item });
            await session.ScreenshotsAsync("timer-completed");
        });
    }

    [Fact]
    public async Task A5_SkippedIneligibleFailedResultsAndNavigationDuringPolling()
    {
        await RunAsync("a5-result-states", async session =>
        {
            var workflow = await session.VariantAsync("stale");
            var instance = await session.Client.StartInstanceAsync(workflow.Id);
            var skipped = await session.PrepareAsync(workflow.Id);
            await Stack.StartWorkerAsync();
            await session.OpenBatchAsync(skipped.Summary.Id);
            await session.StatusAsync("Ready");
            await session.Client.TakeInstanceFlowAsync(instance.Id, 102);
            await session.ConfirmAsync();
            await session.StatusAsync("CompletedWithIssues");
            var skippedItem = Assert.Single((await session.Client.GetAdministrativeActionBatchItemsAsync(skipped.Summary.Id)).Items);
            Assert.Equal("skipped", skippedItem.Status);
            Assert.False(string.IsNullOrWhiteSpace(skippedItem.ErrorDescription));
            await Assertions.Expect(session.Current.Locator("tbody tr")).ToContainTextAsync(skippedItem.ErrorDescription!);
            await Assertions.Expect(session.Current.Locator("tbody tr details")).ToHaveCountAsync(0);
            await session.ScreenshotsAsync("skipped-item");

            await Stack.StopWorkerAsync();
            var staleInstance = await session.Client.StartInstanceAsync(workflow.Id);
            var ineligible = await session.PrepareAsync(workflow.Id);
            await session.Client.TakeInstanceFlowAsync(staleInstance.Id, 102);
            await session.OpenBatchAsync(ineligible.Summary.Id);
            await session.StatusAsync("Preparing");
            await session.Scenario.OpenUiAsync("instances");
            await Stack.StartWorkerAsync();
            await session.WaitBatchAsync(ineligible.Summary.Id, "ready");
            await session.OpenBatchAsync(ineligible.Summary.Id);
            await session.StatusAsync("Ready");
            var ineligibleItem = Assert.Single((await session.Client.GetAdministrativeActionBatchItemsAsync(ineligible.Summary.Id)).Items);
            Assert.Equal("ineligible", ineligibleItem.Status);
            await Assertions.Expect(session.Current.Locator("tbody tr .status-badge")).ToHaveTextAsync("Ineligible");
            await session.Current.Locator("tbody tr details summary").PressAsync("Enter");
            await Assertions.Expect(session.Current.Locator("tbody tr pre")).ToBeVisibleAsync();
            await session.ScreenshotsAsync("ineligible-item-with-issues");

            var failWorkflow = await session.VariantAsync("failure", definition =>
            {
                definition["flowNodes"]!.AsArray().Add(JsonNode.Parse("""{"id":4,"name":"Controlled failure","type":"scriptTask","scriptFormat":"javascript","script":"throw new Error('Controlled acceptance failure');","x":400,"y":80}"""));
                definition["sequenceFlows"]!.AsArray()[1]!["targetRef"] = 4;
                definition["sequenceFlows"]!.AsArray().Add(JsonNode.Parse("""{"id":104,"sourceRef":4,"targetRef":3}"""));
            });
            await session.Client.StartInstanceAsync(failWorkflow.Id);
            var failed = await session.PrepareAsync(failWorkflow.Id);
            await session.OpenBatchAsync(failed.Summary.Id);
            await session.StatusAsync("Ready");
            await session.ConfirmAsync();
            await session.StatusAsync("CompletedWithIssues");
            var failedItem = Assert.Single((await session.Client.GetAdministrativeActionBatchItemsAsync(failed.Summary.Id)).Items);
            Assert.Equal("failed", failedItem.Status);
            await Assertions.Expect(session.Current.Locator("tbody tr")).ToContainTextAsync("Controlled acceptance failure");
            await session.EvidenceAsync("result-states", new { skippedItem, ineligibleItem, failedItem });
            await session.ScreenshotsAsync("failed-item");
        });
    }

    [Fact]
    public async Task A6_CancelledBatchKeepsPollingUntilInterruptedWorkerResumes()
    {
        await RunAsync("a6-cancelled-recovery", async session =>
        {
            await using var service = await ControlledService.StartAsync();
            var workflow = await session.VariantAsync("cancelled-recovery", definition =>
            {
                definition["flowNodes"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = 4, ["name"] = "Controlled wait", ["type"] = "serviceTask", ["x"] = 400, ["y"] = 80,
                    ["service"] = new JsonObject { ["type"] = "rest", ["method"] = "POST", ["url"] = service.Url + "/step", ["timeoutSeconds"] = 120, ["outputMappings"] = new JsonArray() }
                });
                definition["sequenceFlows"]!.AsArray()[1]!["targetRef"] = 4;
                definition["sequenceFlows"]!.AsArray().Add(JsonNode.Parse("""{"id":104,"sourceRef":4,"targetRef":3}"""));
            });
            for (var i = 0; i < 4; i++) await session.Client.StartInstanceAsync(workflow.Id);
            var batch = await session.PrepareAsync(workflow.Id);
            await Stack.StartWorkerAsync();
            await session.OpenBatchAsync(batch.Summary.Id);
            await session.StatusAsync("Ready");
            await session.ConfirmAsync();
            await service.Entered.WaitAsync(TimeSpan.FromSeconds(30), ScenarioCancellation.Token);
            // StartedAt is already committed. Stopping the owned process rolls
            // back only its in-flight action and releases the cancellation lock.
            await Stack.StopWorkerAsync();
            await session.CancelAsync("Preserve completed work and settle the interrupted item");
            await session.StatusAsync("Cancelled");
            var pending = await session.Client.GetAdministrativeActionBatchAsync(batch.Summary.Id);
            Assert.Null(pending.Summary.CompletedAt);
            Assert.Equal(1, pending.Summary.SucceededItemCount);
            Assert.Equal(1, pending.Summary.QueuedItemCount);
            Assert.Equal(2, pending.Summary.CancelledItemCount);
            var pendingItems = await session.Client.GetAdministrativeActionBatchItemsAsync(batch.Summary.Id);
            Assert.Single(pendingItems.Items, item => item.Status == "succeeded");
            var interrupted = Assert.Single(pendingItems.Items, item => item.Status == "queued");
            Assert.NotNull(interrupted.StartedAt);
            Assert.Equal(2, pendingItems.Items.Count(item => item.Status == "cancelled"));
            await session.EvidenceAsync("cancelled-awaiting-worker", new { batch = pending, items = pendingItems });
            var detailPath = $"/api/administrative-action-batches/{batch.Summary.Id}";
            var observed = Stack.ApiProxy!.Requests.Count(request => request.PathAndQuery == detailPath);
            await WaitUntilAsync(() => Task.FromResult(Stack.ApiProxy.Requests.Count(request => request.PathAndQuery == detailPath) >= observed + 2), TimeSpan.FromSeconds(12));
            await session.CountAsync("Queued", 1);
            await session.ScreenshotsAsync("cancelled-awaiting-worker");

            service.Release();
            await Stack.StartWorkerAsync();
            // Use the ordinary durable lease/retry path; do not mutate a job or
            // shorten the production lease just to make acceptance faster.
            await WaitUntilAsync(async () => (await session.Client.GetAdministrativeActionBatchAsync(batch.Summary.Id)).Summary.CompletedAt is not null, TimeSpan.FromSeconds(100));
            await session.CountAsync("Queued", 0, timeout: 20_000);
            await session.CountAsync("Succeeded", 2);
            await session.StatusAsync("Cancelled");
            var settled = await session.Client.GetAdministrativeActionBatchAsync(batch.Summary.Id);
            Assert.Equal(2, settled.Summary.SucceededItemCount);
            Assert.Equal(2, settled.Summary.CancelledItemCount);
            var settledItems = await session.Client.GetAdministrativeActionBatchItemsAsync(batch.Summary.Id);
            Assert.Equal(2, settledItems.Items.Count(item => item.Status == "succeeded"));
            Assert.DoesNotContain(settledItems.Items, item => item.Status == "queued");
            var executionEvidence = new List<InstanceDetailDto>();
            foreach (var item in settledItems.Items)
            {
                var detail = await session.Client.GetInstanceAsync(item.InstanceId);
                executionEvidence.Add(detail);
                var selections = detail.History.Where(history => history.AdministrativeActionBatchId == batch.Summary.Id && history.SequenceFlowId == 102).ToArray();
                if (item.Status == "succeeded") Assert.Single(selections);
                else Assert.Empty(selections);
            }
            Assert.Equal(3, service.Calls); // Harmless external request retried after rollback.
            await Task.Delay(500, ScenarioCancellation.Token);
            var terminalReads = Stack.ApiProxy.Requests.Count(request => request.PathAndQuery == detailPath);
            await Task.Delay(TimeSpan.FromSeconds(7), ScenarioCancellation.Token);
            Assert.Equal(terminalReads, Stack.ApiProxy.Requests.Count(request => request.PathAndQuery == detailPath));
            await session.EvidenceAsync("cancelled-settled", new { batch = settled, items = settledItems, externalCalls = service.Calls });
            await session.EvidenceAsync("cancelled-committed-actions", executionEvidence);
            await session.ScreenshotsAsync("cancelled-settled");
        });
    }

    [Fact]
    public async Task A7_IdentityReplacementClearsAdministrativeDataAndAllowsRecovery()
    {
        await RunAsync("a7-identity-replacement", async session =>
        {
            var workflow = await session.VariantAsync("identity");
            await session.Client.StartInstanceAsync(workflow.Id);
            var batch = await session.PrepareAsync(workflow.Id);
            await Stack.StartWorkerAsync();
            await session.OpenBatchAsync(batch.Summary.Id);
            await session.StatusAsync("Ready");
            await session.ConfirmAsync();
            await session.StatusAsync("Completed");
            var secondPage = await session.Scenario.Context.NewPageAsync();
            try
            {
                var identity = new IdentityScreen(secondPage, Stack.UiBaseAddress);
                await identity.GenerateAndApplyIdentityAsync("unprivileged", ["User"]);
                await Assertions.Expect(session.Page.GetByText("Workflow administrator permission is required", new() { Exact = true })).ToBeVisibleAsync();
                await Assertions.Expect(session.Current).ToHaveCountAsync(0);
                await session.ScreenshotsAsync("identity-permission-denied",
                    session.Page.GetByText("Workflow administrator permission is required", new() { Exact = true }));
                await identity.ClearIdentityAsync();
                await Assertions.Expect(session.Page.GetByText("An authenticated identity is required", new() { Exact = true })).ToBeVisibleAsync();
                await session.ScreenshotsAsync("identity-cleared",
                    session.Page.GetByText("An authenticated identity is required", new() { Exact = true }));
                await identity.GenerateAndApplyIdentityAsync(Session.Actor, Session.Roles);
                await session.OpenBatchAsync(batch.Summary.Id);
                await session.StatusAsync("Completed");
                await session.ScreenshotsAsync("identity-recovered");
            }
            finally { await secondPage.CloseAsync(); }
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task A8_RealResponseGatesExposeInitialHistoryAndItemLoading(int width, int height)
    {
        await RunAsync($"a8-loading-{width}x{height}", async session =>
        {
            var workflow = await session.VariantAsync("loading");
            var instance = await session.Client.StartInstanceAsync(workflow.Id);
            var batch = await session.PrepareAsync(workflow.Id);
            await Stack.StartWorkerAsync();
            await session.WaitBatchAsync(batch.Summary.Id, "ready");
            await Stack.StopWorkerAsync();
            var proxy = Stack.ApiProxy!;
            bool IsHistoryRequest(RecordingApiProxy.RecordedRequest request) =>
                request.Method == "GET" && request.PathAndQuery.StartsWith("/api/administrative-action-batches?", StringComparison.Ordinal);
            bool IsItemsRequest(RecordingApiProxy.RecordedRequest request) =>
                request.Method == "GET" && request.PathAndQuery == $"/api/administrative-action-batches/{batch.Summary.Id}/items?page=1&pageSize=50";
            var historyRequestsBefore = proxy.Requests.Count(IsHistoryRequest);
            var itemsRequestsBefore = proxy.Requests.Count(IsItemsRequest);
            // Navigation starts from the interactive token screen, so SSR
            // quiescence cannot conceal the intermediate interactive render.
            using (var historyGate = proxy.GateNextResponse(IsHistoryRequest))
            {
                if (width < 768) await session.Page.GetByRole(AriaRole.Button, new() { Name = "Open navigation", Exact = true }).ClickAsync();
                await session.Page.GetByRole(AriaRole.Link, new() { Name = "Administrative actions", Exact = true }).ClickAsync();
                await historyGate.Entered.WaitAsync(TimeSpan.FromSeconds(10), ScenarioCancellation.Token);
                await Assertions.Expect(session.History.Locator(".skeleton")).ToBeVisibleAsync();
                await Assertions.Expect(session.History.Locator(".pagination-bar")).ToHaveCountAsync(0);
                await session.ScreenshotAsync($"history-loading-{width}x{height}", session.History);
                historyGate.Release();
                await Assertions.Expect(session.History.Locator("tbody tr").First).ToBeVisibleAsync();
            }
            Assert.Equal(historyRequestsBefore + 1, proxy.Requests.Count(IsHistoryRequest));
            var row = session.History.Locator("tbody tr").Filter(new() { Has = session.Page.Locator("td:first-child", new() { HasTextRegex = new Regex($"^#{batch.Summary.Id}$") }) });
            using (var itemsGate = proxy.GateNextResponse(IsItemsRequest))
            {
                await row.GetByRole(AriaRole.Button, new() { Name = "Open", Exact = true }).ClickAsync();
                await itemsGate.Entered.WaitAsync(TimeSpan.FromSeconds(10), ScenarioCancellation.Token);
                // A real oninput event requests an ordinary Blazor render after
                // detail resolves and while its authentic items response waits.
                await session.Page.Locator("#admin-reason").FillAsync("Loading-state visual acceptance");
                await Assertions.Expect(session.Current.GetByText("Loading items…", new() { Exact = true })).ToBeVisibleAsync();
                await Assertions.Expect(session.Current.Locator(".skeleton")).ToBeVisibleAsync();
                await Assertions.Expect(session.Current.Locator(".pagination-bar")).ToHaveCountAsync(0);
                await session.ScreenshotAsync($"items-loading-{width}x{height}", session.Current);
                itemsGate.Release();
                await Assertions.Expect(session.Current.Locator("tbody tr")).ToHaveCountAsync(1);
                await Assertions.Expect(session.Current.Locator(".skeleton")).ToHaveCountAsync(0);
                await Assertions.Expect(session.Current.Locator(".pagination-bar")).ToBeVisibleAsync();
                await Assertions.Expect(session.Current.Locator("tbody tr")
                    .GetByRole(AriaRole.Link, new() { Name = $"Instance #{instance.Id}", Exact = true }))
                    .ToHaveAttributeAsync("href", $"instances/{instance.Id}");
            }
            await session.ScreenshotAsync($"loading-recovered-{width}x{height}", session.Current);
            Assert.Equal(itemsRequestsBefore + 1, proxy.Requests.Count(IsItemsRequest));
            Assert.Equal(historyRequestsBefore + 1, proxy.Requests.Count(IsHistoryRequest));
            await session.EvidenceAsync("loading-http", await session.Client.GetAdministrativeActionBatchItemsAsync(batch.Summary.Id));
        }, width, height);
    }

    private async Task RunAsync(string name, Func<Session, Task> body, int width = 1440, int height = 900)
    {
        await using var scenario = await Stack.CreateScenario(name, width, height, scenarioTimeout: TimeSpan.FromMinutes(5));
        await scenario.RunAsync(name, async () =>
        {
            await using var session = await Session.CreateAsync(scenario);
            try
            {
                await body(session);
                Assert.Empty(scenario.Warnings);
                Assert.Empty(scenario.FailedRequests);
            }
            finally
            {
                Stack.ApiProxy!.ReleaseAllGates();
                await Stack.StopWorkerAsync();
                await session.EvidenceAsync("ui-api-requests", Stack.ApiProxy.Requests);
            }
        });
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            ScenarioCancellation.Token.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Administrative acceptance condition exceeded {timeout}.");
            await Task.Delay(200, ScenarioCancellation.Token);
        }
    }

    private sealed class Session(BrowserScenario scenario, WorkflowFixtureClient client, HttpClient http) : IAsyncDisposable
    {
        internal const string Actor = "acceptance-supervisor";
        internal static readonly string[] Roles = ["admin", "Reviewer", "auditor"];
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        internal BrowserScenario Scenario => scenario;
        internal WorkflowFixtureClient Client => client;
        internal IPage Page => scenario.Page;
        internal ILocator Current => Page.Locator("section[aria-labelledby='admin-current-batch-heading']");
        internal ILocator History => Page.Locator("section[aria-labelledby='admin-batches-heading']");

        internal static async Task<Session> CreateAsync(BrowserScenario scenario)
        {
            var identity = new IdentityScreen(scenario.Page, scenario.Stack.UiBaseAddress);
            var token = await identity.GenerateAndApplyIdentityAsync(Actor, Roles);
            scenario.OnCleanup(() => identity.ClearIdentityAsync());
            var client = scenario.Stack.CreateClient(Actor, Roles, token, scenario.Name);
            var http = new HttpClient { BaseAddress = new Uri(scenario.Stack.ApiBaseAddress) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return new Session(scenario, client, http);
        }

        internal string FixturePath(string name) => Path.Combine(scenario.Stack.RepositoryRoot, "Flowbit/tests/Flowbit.BrowserTests/Fixtures", name);
        internal async Task<WorkflowSummaryDto> VariantAsync(string name, Action<JsonNode>? edit = null)
        {
            var definition = JsonNode.Parse(await File.ReadAllTextAsync(FixturePath("runtime-administrative-r7.json"), ScenarioCancellation.Token))!;
            definition["id"] = "acceptance-admin-" + name;
            definition["name"] = "Acceptance administrative " + name;
            edit?.Invoke(definition);
            var path = Path.Combine(scenario.ArtifactDirectory, name + ".json");
            await File.WriteAllTextAsync(path, definition.ToJsonString(Json), ScenarioCancellation.Token);
            return await client.CreateAndPublishAsync(path);
        }

        internal async Task<AdministrativeActionBatchDetailDto> PrepareAsync(long workflowId, int flow = 102, string? mode = null, Dictionary<string, JsonElement>? variables = null, int? boundary = null)
        {
            using var response = await http.PostAsJsonAsync("/api/administrative-action-batches", new CreateAdministrativeActionBatchRequest(
                workflowId, 2, boundary is null ? "directFlow" : "timerBoundary", flow, boundary, mode,
                "Worker browser acceptance", variables,
                new("allMatching", null, new() { WorkflowDefinitionId = workflowId, SourceNodeId = 2 }, null), Guid.NewGuid().ToString("N")), ScenarioCancellation.Token);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ScenarioCancellation.Token));
            return (await response.Content.ReadFromJsonAsync<AdministrativeActionBatchDetailDto>(ScenarioCancellation.Token))!;
        }

        internal async Task OpenPageAsync() => await scenario.OpenUiAsync("administrative-actions");
        internal async Task OpenBatchAsync(long id)
        {
            await scenario.OpenUiAsync($"administrative-actions?batchId={id}");
            await Assertions.Expect(Current.Locator(".page-eyebrow")).ToHaveTextAsync($"Batch #{id}");
        }
        internal async Task SelectWorkflowAsync(WorkflowSummaryDto workflow)
        {
            await Page.Locator("#admin-workflow-family").SelectOptionAsync(workflow.WorkflowKey);
            await Assertions.Expect(Page.Locator("#admin-workflow-version")).ToHaveValueAsync(workflow.Id.ToString());
            await Assertions.Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Search positions", Exact = true })).ToBeEnabledAsync();
        }
        internal Task StatusAsync(string status) => Assertions.Expect(Current.Locator(".section-heading .status-badge").First).ToHaveTextAsync(status, new() { Timeout = 60_000 });
        internal Task CountAsync(string label, int value, float timeout = 10_000) => Assertions.Expect(Current.Locator(".summary-item").Filter(new() { Has = Page.Locator("span", new() { HasTextRegex = new Regex("^" + Regex.Escape(label) + "$") }) }).Locator("strong")).ToHaveTextAsync(value.ToString("N0"), new() { Timeout = timeout });
        internal Task ConfirmAsync() => Current.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Confirm ") }).ClickAsync();
        internal async Task CancelAsync(string reason)
        {
            await Current.GetByLabel("Cancellation reason").FillAsync(reason);
            await Page.Keyboard.PressAsync("Tab");
            await Current.GetByRole(AriaRole.Button, new() { Name = "Cancel unstarted positions", Exact = true }).ClickAsync();
        }
        internal Task WaitBatchAsync(long id, string status) => WaitUntilAsync(async () => (await client.GetAdministrativeActionBatchAsync(id)).Summary.Status == status, TimeSpan.FromSeconds(90));
        internal Task EvidenceAsync(string name, object value) => File.WriteAllTextAsync(Path.Combine(scenario.ArtifactDirectory, name + ".json"), JsonSerializer.Serialize(value, Json));
        internal async Task ScreenshotAsync(string name, ILocator target)
        {
            // Resizing across the mobile breakpoint animates the real sidebar.
            // Wait for finite transitions without changing application styles
            // or waiting for the loading skeleton's infinite animation.
            await Page.WaitForFunctionAsync("""
                () => document.getAnimations().every(animation =>
                    animation.playState !== 'running' ||
                    animation.effect?.getComputedTiming().iterations === Infinity)
                """, null, new() { Timeout = 5_000 });
            Assert.True(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 2"));
            await Page.Locator("h1").HoverAsync();
            await Page.Mouse.WheelAsync(0, -await Page.EvaluateAsync<int>("() => document.documentElement.scrollHeight"));
            await Page.WaitForFunctionAsync("() => window.scrollY <= 1");
            await Page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, name + ".png"), FullPage = true });
            // A whole tall section may be centered with its status out of view.
            var heading = target.Locator(".section-heading").First;
            await (await heading.CountAsync() > 0 ? heading : target).ScrollIntoViewIfNeededAsync();
            await Page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, name + "-viewport.png") });
        }
        internal async Task ScreenshotsAsync(string name, ILocator? target = null)
        {
            foreach (var (width, height) in new[] { (1440, 900), (1024, 768), (390, 844) })
            {
                await Page.SetViewportSizeAsync(width, height);
                await ScreenshotAsync($"{name}-{width}x{height}", target ?? Current);
            }
            await Page.SetViewportSizeAsync(1440, 900);
        }
        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            await client.DisposeAsync();
        }
    }

    /// <summary>A harmless external HTTP destination whose second call can be interrupted.</summary>
    private sealed class ControlledService(WebApplication app) : IAsyncDisposable
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        internal string Url => app.Urls.Single();
        internal Task Entered => entered.Task;
        internal int Calls => Volatile.Read(ref calls);
        internal void Release() => release.TrySetResult();
        internal static async Task<ControlledService> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
            var app = builder.Build();
            var service = new ControlledService(app);
            app.MapPost("/step", async (HttpContext context) =>
            {
                if (Interlocked.Increment(ref service.calls) == 2)
                {
                    service.entered.TrySetResult();
                    await service.release.Task.WaitAsync(TimeSpan.FromMinutes(3), context.RequestAborted);
                }
                return Results.Json(new { ok = true });
            });
            await app.StartAsync(ScenarioCancellation.Token);
            return service;
        }
        public async ValueTask DisposeAsync()
        {
            Release();
            await app.DisposeAsync();
        }
    }
}
