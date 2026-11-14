using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;
using static Flowbit.BrowserAcceptanceTests.AutomaticMi;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>
/// TASK-05B and TASK-05C service and script bodies through real API, UI and Worker
/// processes, on a stack of their own. The
/// controlled service holds, orders and fails real HTTP answers per item; every other state
/// comes from authenticated HTTP commands, Worker runs and restarts.
/// </summary>
[Collection(AutomaticMultiInstanceAcceptanceCollection.Name)]
public sealed class AutomaticMultiInstanceAcceptanceTests(AutomaticMultiInstanceAcceptanceFixture fixture)
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(6);
    private BrowserStackFixture Stack => fixture.Stack;

    private ExampleItemGroup Items(BrowserScenario scenario, string group)
    {
        var items = fixture.Service.Items(group);
        scenario.OnCleanup(() => { items.ReleaseAll(); return Task.CompletedTask; });
        return items;
    }

    [Fact]
    public async Task ServiceBodyQueuesWhileTheWorkerIsStoppedThenOrdersOutOfOrderResults()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-service-out-of-order", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("service body", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var group = Unique("receipts");
            var items = Items(scenario, group).Hold(0, 1, 2);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(
                BodyWorkflow(Unique("auto-mi-service"), ServiceBody(), ServiceVariables(group, 7, 8, 9)));
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, new());

            // Queued: the entry job waits for a Worker, so no body or item exists yet.
            var queued = await InstanceAsync(actor, instance.Id);
            Assert.Empty(queued.AutomaticMultiInstances);
            Assert.Equal("asyncBefore", Assert.Single(queued.ExecutionPositions, position => position.NodeId == 2).WaitState);
            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(await DiagramDetailsAsync(page, 2)).ToContainTextAsync("Queued for background processing");
            await Assertions.Expect(AutomaticSection(page)).ToHaveCountAsync(0);
            // The read-only diagram marks the body with its parallel multi-instance marker.
            await Assertions.Expect(page.Locator(".flowbit-editor[data-mode='viewer'] .node[data-id='2'] .mi-marker")).ToHaveCountAsync(1);
            await CaptureAtViewportsAsync(scenario, "service-queued");
            Assert.Empty(items.Calls);

            await Stack.StartWorkerAsync();
            foreach (var index in new[] { 0, 1, 2 }) await items.ArrivedAsync(index);
            var running = await UntilInstanceAsync(actor, instance.Id, detail => Body(detail, 2) is { ActiveCount: 3 },
                "all three items to run");
            var body = Body(running, 2)!;
            Assert.Equal((3, "active", 3), (body.TotalCount, body.Status, body.EffectiveConcurrency));
            // Nobody can act on an automatic item.
            Assert.Empty(await actor.Runtime.GetInboxAsync(instance.Id));

            await ReloadInstanceAsync(page);
            var bodyDetails = await DiagramDetailsAsync(page, 2);
            await Assertions.Expect(bodyDetails).ToContainTextAsync("Waiting for automatic service task items");
            await Assertions.Expect(bodyDetails).ToContainTextAsync("Automatic items: 0 of 3 completed · 3 active");
            var section = AutomaticSection(page);
            await Assertions.Expect(section).ToContainTextAsync("0 / 3 completed");
            await Assertions.Expect(section).ToContainTextAsync("3 active");
            await ShowItemsAsync(page, body.ExecutionId);
            await CaptureAtViewportsAsync(scenario, "service-running");

            // Answer in reverse order. Each item settles while the earlier ones still wait,
            // and the page's own polling shows the count move.
            var completedOrder = new List<int>();
            foreach (var index in new[] { 2, 1, 0 })
            {
                items.Release(index);
                var settled = await UntilAsync(() => ItemsAsync(actor, body.ExecutionId),
                    result => result.Items.Single(item => item.ItemIndex == index).Status == "completed",
                    $"item {index} to complete");
                completedOrder.Add(index);
                Assert.All(settled.Items.Where(item => !completedOrder.Contains(item.ItemIndex)),
                    item => Assert.NotEqual("completed", item.Status));
                if (index == 2)
                    await Assertions.Expect(section).ToContainTextAsync("1 / 3 completed",
                        new() { Timeout = (float)Stack.PollingObservationBudget.TotalMilliseconds });
            }

            var done = await UntilInstanceAsync(actor, instance.Id, AtNode(4), "the review task");
            var receipts = Latest(done, "receipts").EnumerateArray().ToArray();
            Assert.Equal([0, 1, 2], receipts.Select(row => row.GetProperty("index").GetInt32()));
            Assert.Equal([7, 8, 9], receipts.Select(row => row.GetProperty("item").GetInt32()));
            Assert.Equal(["receipt-0", "receipt-1", "receipt-2"],
                receipts.Select(row => row.GetProperty("variables").GetProperty("receipt").GetString()));
            Assert.All(receipts, row => Assert.Equal(200, row.GetProperty("variables").GetProperty("httpStatus").GetInt32()));
            // Item locals never become instance variables.
            Assert.DoesNotContain(done.Variables, variable => variable.VariableName is "receipt" or "httpStatus");

            // One call per item, each keyed by its own stable job id.
            Assert.Equal(3, items.Calls.Count);
            Assert.Equal(3, items.Calls.Select(call => call.IdempotencyKey).Distinct().Count());
            var itemPage = await ItemsAsync(actor, body.ExecutionId);
            foreach (var item in itemPage.Items)
            {
                var call = Assert.Single(items.Calls, call => call.Index == item.ItemIndex);
                Assert.Matches("^item-[0-9]+$", call.IdempotencyKey);
                if (item.JobId is { } jobId) Assert.Equal($"item-{jobId}", call.IdempotencyKey);
                using var posted = JsonDocument.Parse(call.Body);
                Assert.Equal(3, posted.RootElement.GetProperty("total").GetInt32());
            }
            Assert.Equal([2, 1, 0], items.Calls.OrderBy(call => call.AnsweredAt).Select(call => call.Index));

            await ReloadInstanceAsync(page);
            await Assertions.Expect(section).ToContainTextAsync("3 / 3 completed");
            await Assertions.Expect(Summary(page)).ToContainTextAsync("Review results");
            await ShowItemsAsync(page, body.ExecutionId);
            await ItemRow(page, itemPage.Items.Single(item => item.ItemIndex == 1).Id)
                .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "View item 1" }).ClickAsync();
            var detail = section.Locator("section[aria-label='Automatic item detail']");
            await Assertions.Expect(detail).ToContainTextAsync("8");
            await Assertions.Expect(detail).ToContainTextAsync("receipt-1");
            await CaptureAtViewportsAsync(scenario, "service-completed");
            await WriteEvidenceAsync(scenario, "service-calls", items.Calls);
        });
    }

    [Fact]
    public async Task SequentialServiceBodyCallsOneItemAtATimeAndAnEmptyCollectionCompletesAtOnce()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-service-sequential", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("sequential and empty", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var group = Unique("sequence");
            var items = Items(scenario, group).Hold(0, 1);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(
                BodyWorkflow(Unique("auto-mi-sequential"), ServiceBody("sequential"), ServiceVariables(group, 4, 5)));
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, new());

            await items.ArrivedAsync(0);
            // Nothing is sent for the second item while the first one is still open.
            await Task.Delay(TimeSpan.FromSeconds(3), ScenarioCancellation.Token);
            Assert.Equal(0, items.CallCount(1));
            var waiting = Body(await InstanceAsync(actor, instance.Id), 2)!;
            Assert.Equal((1, 1, 1), (waiting.ActiveCount, waiting.PendingCount, waiting.EffectiveConcurrency));
            items.Release(0);
            var second = await items.ArrivedAsync(1);
            Assert.True(second.ArrivedAt >= items.Calls.Single(call => call.Index == 0).AnsweredAt);
            items.Release(1);
            var done = await UntilInstanceAsync(actor, instance.Id, AtNode(4), "the review task");
            Assert.Equal(["receipt-0", "receipt-1"], Latest(done, "receipts").EnumerateArray()
                .Select(row => row.GetProperty("variables").GetProperty("receipt").GetString()));

            // An empty collection sends nothing and completes with an empty aggregate.
            var emptyGroup = Unique("empty");
            var empty = await Stack.SetupClient.CreateAndPublishAsync(
                BodyWorkflow(Unique("auto-mi-empty"), ServiceBody(), ServiceVariables(emptyGroup)));
            var emptyInstance = await actor.Runtime.StartInstanceAsync(empty.Id, new());
            var emptyDone = await UntilInstanceAsync(actor, emptyInstance.Id, AtNode(4), "the empty body to complete");
            Assert.Equal(0, Latest(emptyDone, "receipts").GetArrayLength());
            Assert.Empty(fixture.Service.Items(emptyGroup).Calls);
            var emptyBody = Body(emptyDone, 2);
            Assert.NotNull(emptyBody);
            Assert.Equal((0, "completed"), (emptyBody.TotalCount, emptyBody.Status));
            var page = await OpenInstanceAsync(scenario, emptyInstance.Id);
            await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync("Empty collection: no item work was created.");
            await CaptureAtViewportsAsync(scenario, "service-empty-completed");
        });
    }

    [Fact]
    public async Task ScriptBodiesPublishOrderedResultsPageTheirItemsAndHideDeniedPayloads()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-script-results", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("script bodies", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);

            // NCalc, sequential: each assignment sees the earlier one's write.
            var ncalc = new FlowNodeModel
            {
                Id = 2, Name = "Scale amounts", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
                ScriptFormat = ScriptFormats.NCalc, X = 200, Y = 140,
                MultiInstance = Multi("sequential", "amounts", "scaled", 1, Local("scaledAmount"), Local("adjusted")),
                Assignments =
                [
                    new() { Variable = "scaledAmount", Expression = "[mi.item] * factor" },
                    new() { Variable = "adjusted", Expression = "scaledAmount + [mi.index]" },
                ]
            };
            var ncalcWorkflow = await Stack.SetupClient.CreateAndPublishAsync(BodyWorkflow(Unique("auto-mi-ncalc"), ncalc,
                Process(1, "amounts", "number", new[] { 10, 20, 30 }, isArray: true),
                Process(2, "factor", "number", 2),
                Process(3, "scaled", "json", Array.Empty<object>())));
            var ncalcInstance = await actor.Runtime.StartInstanceAsync(ncalcWorkflow.Id, new());
            var ncalcDone = await UntilInstanceAsync(actor, ncalcInstance.Id, AtNode(4), "the NCalc body");
            var scaled = Latest(ncalcDone, "scaled").EnumerateArray().ToArray();
            Assert.Equal([20, 40, 60], scaled.Select(row => row.GetProperty("variables").GetProperty("scaledAmount").GetInt32()));
            Assert.Equal([20, 41, 62], scaled.Select(row => row.GetProperty("variables").GetProperty("adjusted").GetInt32()));

            // JavaScript, parallel over 60 JSON items: more than one page of items.
            var lines = Enumerable.Range(0, 60).Select(index => new { sku = $"SKU-{index:00}", quantity = index % 4 + 1, price = 3 }).ToArray();
            var javascript = new FlowNodeModel
            {
                Id = 2, Name = "Price lines", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
                ScriptFormat = ScriptFormats.JavaScript, X = 200, Y = 140,
                MultiInstance = Multi("parallel", "lines", "priced", 8, Local("lineTotal"), Local("summary", "json")),
                Script = "const line = execution.getVariable('mi.item');\n" +
                    "execution.setVariable('lineTotal', line.quantity * line.price * execution.getVariable('factor'));\n" +
                    "execution.setVariable('summary', { index: execution.getVariable('mi.index'), sku: line.sku });"
            };
            var jsWorkflow = await Stack.SetupClient.CreateAndPublishAsync(BodyWorkflow(Unique("auto-mi-js"), javascript,
                Process(1, "lines", "json", lines, isArray: true),
                Process(2, "factor", "number", 2),
                Process(3, "priced", "json", Array.Empty<object>())));
            var jsInstance = await actor.Runtime.StartInstanceAsync(jsWorkflow.Id, new());
            var jsDone = await UntilInstanceAsync(actor, jsInstance.Id, AtNode(4), "the JavaScript body", TimeSpan.FromMinutes(3));
            var priced = Latest(jsDone, "priced").EnumerateArray().ToArray();
            Assert.Equal(60, priced.Length);
            Assert.Equal(lines.Select(line => line.sku), priced.Select(row => row.GetProperty("variables").GetProperty("summary").GetProperty("sku").GetString()));
            Assert.Equal(lines.Select(line => line.quantity * line.price * 2), priced.Select(row => row.GetProperty("variables").GetProperty("lineTotal").GetInt32()));
            var body = Body(jsDone, 2)!;

            var page = await OpenInstanceAsync(scenario, jsInstance.Id);
            var section = AutomaticSection(page);
            await Assertions.Expect(section).ToContainTextAsync("60 / 60 completed");
            var table = await ShowItemsAsync(page, body.ExecutionId);
            await Assertions.Expect(table.Locator("tbody tr")).ToHaveCountAsync(50);
            await Assertions.Expect(section).ToContainTextAsync("Page 1 · 60 items");
            await section.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Next" }).ClickAsync();
            await Assertions.Expect(section).ToContainTextAsync("Page 2 · 60 items");
            await Assertions.Expect(table.Locator("tbody tr")).ToHaveCountAsync(10);
            await Assertions.Expect(table.Locator("tbody tr").First).ToContainTextAsync("50");
            var second = (await ItemsAsync(actor, body.ExecutionId, page: 2)).Items;
            Assert.Equal(Enumerable.Range(50, 10), second.Select(item => item.ItemIndex));
            await ItemRow(page, second[5].Id).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "View item 55" }).ClickAsync();
            var detail = section.Locator("section[aria-label='Automatic item detail']");
            await Assertions.Expect(detail).ToContainTextAsync("SKU-55");
            await Assertions.Expect(detail).ToContainTextAsync("lineTotal");
            await CaptureAtViewportsAsync(scenario, "script-paged-items");

            // The proxy holds the real item page while another tab changes identity: the
            // page resets at once, and the released stale answer never appears.
            using (var gate = Stack.ApiProxy.GateNextResponse(request =>
                       request.PathAndQuery.StartsWith($"/api/multi-instance-executions/{body.ExecutionId}/items?page=1", StringComparison.Ordinal)))
            {
                await section.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Previous" }).ClickAsync();
                await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));
                await Assertions.Expect(section.GetByRole(AriaRole.Status)).ToContainTextAsync("Loading automatic items");
                await page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "script-items-loading.png"), FullPage = true });
                await using var reviewer = await SwitchIdentityInAnotherTabAsync(scenario, "mi-reviewer", "Reviewer");
                await Assertions.Expect(section.Locator("h3")).ToHaveCountAsync(0, new() { Timeout = 20_000 });
                gate.Release();
                await Task.Delay(TimeSpan.FromSeconds(2), ScenarioCancellation.Token);
                await Assertions.Expect(section.Locator("h3")).ToHaveCountAsync(0);

                // The reviewer sees progress but not payloads; the API agrees.
                await ReloadInstanceAsync(page);
                await ShowItemsAsync(page, body.ExecutionId);
                await Assertions.Expect(section).ToContainTextAsync("Node-activity permission required");
                await Assertions.Expect(section.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "View item 0" })).ToHaveCountAsync(0);
                var hidden = (await ItemsAsync(reviewer, body.ExecutionId)).Items;
                Assert.All(hidden, item => Assert.False(item.CanReadPayload));
                var (status, _) = await SendAsync(reviewer.Http, HttpMethod.Get,
                    $"/api/multi-instance-executions/{body.ExecutionId}/items/{hidden[0].Id}");
                Assert.True(status is 403 or 404, $"Denied item detail returned {status}.");
                await CaptureAtViewportsAsync(scenario, "script-items-denied");
            }

            // Back to the operator: Show items again loads payloads.
            await SwitchIdentityInAnotherTabAsync(scenario, "mi-operator", OperatorRoles);
            await ReloadInstanceAsync(page);
            await ShowItemsAsync(page, body.ExecutionId);
            await Assertions.Expect(section.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "View item 0" })).ToBeVisibleAsync();
        });
    }

    [Fact]
    public async Task AnItemIncidentRetriesOnlyTheFailedItemFromOperations()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-item-incident", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("item incident", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var group = Unique("incident");
            // One retry two seconds later: item 2 recovers on it, item 1 fails both attempts.
            var items = Items(scenario, group).Answer(1, 503, 503, 200).Answer(2, 503, 200);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(BodyWorkflow(Unique("auto-mi-incident"),
                ServiceBody(retryDelays: ["PT2S"]), ServiceVariables(group, 1, 2, 3)));
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, new());

            var incident = Assert.Single(await UntilAsync(() => IncidentsAsync(actor, instance.Id),
                rows => rows.Any(row => row.Status == "open"), "the failed item's incident"));
            Assert.NotNull(incident.AutomaticMultiInstanceItemId);
            var blocked = await UntilInstanceAsync(actor, instance.Id,
                detail => Body(detail, 2) is { CompletedCount: 2, FailedCount: 1 }, "the siblings to finish");
            var body = Body(blocked, 2)!;
            var failedItem = (await ItemsAsync(actor, body.ExecutionId)).Items.Single(item => item.ItemIndex == 1);
            Assert.Equal(incident.Id, failedItem.IncidentId);
            Assert.Equal(incident.AutomaticMultiInstanceItemId, failedItem.Id);

            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync("2 / 3 completed");
            await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync("1 blocked/failed");
            await ShowItemsAsync(page, body.ExecutionId);
            await Assertions.Expect(ItemRow(page, failedItem.Id)).ToContainTextAsync($"Incident #{incident.Id}");
            await CaptureAtViewportsAsync(scenario, "service-item-blocked");

            // A person without Operations authority cannot retry it.
            await using (var reviewer = await SwitchIdentityInAnotherTabAsync(scenario, "mi-reviewer", "Reviewer"))
            {
                var (denied, _) = await SendAsync(reviewer.Http, HttpMethod.Post, $"/api/incidents/{incident.Id}/retry");
                Assert.Equal(403, denied);
            }
            await SwitchIdentityInAnotherTabAsync(scenario, "mi-operator", OperatorRoles);

            await RetryFromOperationsAsync(scenario, incident, "service-item-incident-operations");
            var done = await UntilInstanceAsync(actor, instance.Id, AtNode(4), "the retried item to finish the body");
            Assert.Equal([1, 3, 2], new[] { 0, 1, 2 }.Select(items.CallCount));
            Assert.Equal(["receipt-0", "receipt-1", "receipt-2"], Latest(done, "receipts").EnumerateArray()
                .Select(row => row.GetProperty("variables").GetProperty("receipt").GetString()));
            Assert.Equal("resolved", Assert.Single(await IncidentsAsync(actor, instance.Id)).Status);
            // The retry rendered the same request, with the same idempotency key.
            var calls = items.Calls.Where(call => call.Index == 1).ToArray();
            Assert.Single(calls.Select(call => call.IdempotencyKey).Distinct());
        });
    }

    [Fact]
    public async Task ACaughtItemFailurePublishesThePartialResultAndDiscardsALateAnswer()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-partial-result", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("partial result", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var group = Unique("partial");
            var items = Items(scenario, group).Answer(1, 409).Hold(1, 2);
            var model = BodyWorkflow(Unique("auto-mi-partial"), ServiceBody(), ServiceVariables(group, 1, 2, 3));
            model.Variables.Add(NullableString(9, "failure"));
            AddBoundary(model, ErrorBoundary(5, "failure"), "Handle failure");
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, new());

            await UntilAsync(() => ItemsAsyncFor(actor, instance.Id),
                result => result.Any(item => item.ItemIndex == 0 && item.Status == "completed"), "item 0 to complete");
            await items.ArrivedAsync(2);
            items.Release(1);
            var handled = await UntilInstanceAsync(actor, instance.Id, AtNode(6), "the error boundary");
            Assert.Equal("service_task_failed", Latest(handled, "failure").GetString());
            var partial = Latest(handled, "receipts").EnumerateArray().ToArray();
            var completed = Assert.Single(partial, row => row.GetProperty("status").GetString() == "completed");
            Assert.Equal(0, completed.GetProperty("index").GetInt32());
            Assert.Equal("receipt-0", completed.GetProperty("variables").GetProperty("receipt").GetString());
            Assert.False(Visited(handled, 4));

            // The held item answers only now; its result is discarded.
            items.Release(2);
            await UntilAsync(() => Task.FromResult(items.Calls.Single(call => call.Index == 2)),
                call => call.AnsweredAt is not null || call.Abandoned, "the late answer");
            await Task.Delay(TimeSpan.FromSeconds(3), ScenarioCancellation.Token);
            var after = await InstanceAsync(actor, instance.Id);
            Assert.Equal(Latest(handled, "receipts").GetRawText(), Latest(after, "receipts").GetRawText());
            Assert.False(Visited(after, 4));
            var body = Body(after, 2)!;
            Assert.NotEqual("completed", (await ItemsAsync(actor, body.ExecutionId)).Items.Single(item => item.ItemIndex == 2).Status);

            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(Summary(page)).ToContainTextAsync("Handle failure");
            await ShowItemsAsync(page, body.ExecutionId);
            var failedItem = (await ItemsAsync(actor, body.ExecutionId)).Items.Single(item => item.ItemIndex == 1);
            await ItemRow(page, failedItem.Id).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "View item 1" }).ClickAsync();
            await Assertions.Expect(AutomaticSection(page).Locator("section[aria-label='Automatic item detail']"))
                .ToContainTextAsync("service_task_failed");
            await CaptureAtViewportsAsync(scenario, "service-partial-result");
        });
    }

    [Fact]
    public async Task AnOversizedAnswerFailsOnlyItsItemAndIsNeverTruncated()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-oversized-answer", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("oversized answer", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var group = Unique("oversized");
            // Item 1 answers 200 with a body above WorkflowServiceTasks.MaxResponseBodyBytes (1 MiB).
            var items = Items(scenario, group).Hold(1).Oversize(1);
            var model = BodyWorkflow(Unique("auto-mi-oversized"), ServiceBody(), ServiceVariables(group, 1, 2));
            model.Variables.Add(NullableString(9, "failure"));
            AddBoundary(model, ErrorBoundary(5, "failure"), "Handle failure");
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, new());

            await UntilAsync(() => ItemsAsyncFor(actor, instance.Id),
                result => result.Any(item => item.ItemIndex == 0 && item.Status == "completed"), "item 0 to complete");
            items.Release(1);
            var handled = await UntilInstanceAsync(actor, instance.Id, AtNode(6), "the error boundary");
            Assert.Equal("service_task_failed", Latest(handled, "failure").GetString());
            var rows = Latest(handled, "receipts").EnumerateArray().ToArray();
            Assert.Equal("receipt-0", Assert.Single(rows, row => row.GetProperty("status").GetString() == "completed")
                .GetProperty("variables").GetProperty("receipt").GetString());
            // No truncated success was published for the oversized answer, and nothing retried it.
            Assert.DoesNotContain(rows, row => row.GetProperty("index").GetInt32() == 1 && row.GetProperty("status").GetString() == "completed");
            Assert.Equal(1, items.CallCount(1));
            Assert.Empty((await IncidentsAsync(actor, instance.Id)).Where(row => row.Status == "open"));
            var body = Body(handled, 2)!;
            var failed = (await ItemsAsync(actor, body.ExecutionId)).Items.Single(item => item.ItemIndex == 1);
            var detail = await ReadAsync<AutomaticMultiInstanceItemDetailDto>(actor.Http,
                $"/api/multi-instance-executions/{body.ExecutionId}/items/{failed.Id}");
            Assert.Equal("service_task_failed", detail.ErrorCode);
            Assert.DoesNotContain("padding", detail.ErrorDescription ?? string.Empty, StringComparison.Ordinal);
            var page = await OpenInstanceAsync(scenario, instance.Id);
            await ShowItemsAsync(page, body.ExecutionId);
            await ItemRow(page, failed.Id).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "View item 1" }).ClickAsync();
            await Assertions.Expect(AutomaticSection(page).Locator("section[aria-label='Automatic item detail']"))
                .ToContainTextAsync("service_task_failed");
            await CaptureAtViewportsAsync(scenario, "service-oversized-answer");
        });
    }

    [Fact]
    public async Task ASharedOutputConflictRetriesPublicationWithoutANewRequest()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-shared-item-conflict", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("shared item conflict", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var key = $"acceptance.mi.tally.{Guid.NewGuid():N}";
            await CreateSharedAsync(actor, key, "number", 0);
            var group = Unique("shared");
            var items = Items(scenario, group).Hold(0);
            var body = ServiceBody();
            body.Service!.OutputMappings.Add(new() { Variable = "tally", Path = "amount", DataType = "number", IsArray = false, Required = true });
            // Item 0 answers amount 0 into the shared tally.
            var variables = ServiceVariables(group, 3).Append(SharedBinding(4, "tally", key, "number")).ToArray();
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(BodyWorkflow(Unique("auto-mi-shared"), body, variables));
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, new());

            await items.ArrivedAsync(0);
            // Someone else changes the shared target while the request is open.
            await SetSharedAsync(actor, key, 41);
            items.Release(0);
            var incident = Assert.Single(await UntilAsync(() => IncidentsAsync(actor, instance.Id),
                rows => rows.Any(row => row.Status == "open"), "the output conflict"));
            Assert.Equal("output_version_conflict", incident.Type);
            Assert.NotNull(incident.AutomaticMultiInstanceItemId);
            Assert.Equal(41, (await SharedValueAsync(actor, key)).GetInt32());

            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync("1 blocked/failed");
            await RetryFromOperationsAsync(scenario, incident, "service-shared-item-conflict");

            var done = await UntilInstanceAsync(actor, instance.Id, AtNode(4), "publication-only recovery");
            Assert.Equal(1, items.CallCount(0));
            // Recovery accepted the saved candidate over the newer value.
            Assert.Equal(0, (await SharedValueAsync(actor, key)).GetInt32());
            Assert.Equal("receipt-0", Latest(done, "receipts")[0].GetProperty("variables").GetProperty("receipt").GetString());
        });
    }

    [Fact]
    public async Task AnAggregateConflictRetriesPublicationWithoutRepeatingItems()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-aggregate-conflict", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("aggregate conflict", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var key = $"acceptance.mi.aggregate.{Guid.NewGuid():N}";
            await CreateSharedAsync(actor, key, "json", Array.Empty<object>());
            var group = Unique("aggregate");
            var items = Items(scenario, group).Hold(1);
            var body = ServiceBody();
            body.MultiInstance!.ResultVariable = "sharedReceipts";
            var variables = ServiceVariables(group, 1, 2).Take(2).Append(SharedBinding(3, "sharedReceipts", key, "json")).ToArray();
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(BodyWorkflow(Unique("auto-mi-aggregate"), body, variables));
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, new());

            await items.ArrivedAsync(1);
            await SetSharedAsync(actor, key, new[] { "changed elsewhere" });
            items.Release(1);
            var incident = Assert.Single(await UntilAsync(() => IncidentsAsync(actor, instance.Id),
                rows => rows.Any(row => row.Status == "open"), "the aggregate conflict"));
            Assert.Equal("output_version_conflict", incident.Type);
            Assert.Null(incident.AutomaticMultiInstanceItemId);
            var blocked = Body(await InstanceAsync(actor, instance.Id), 2)!;
            Assert.Equal(incident.Id, blocked.ControlIncidentId);
            Assert.Equal(2, blocked.CompletedCount);

            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync("Needs attention");
            await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync($"Incident #{incident.Id}");
            await CaptureAtViewportsAsync(scenario, "service-aggregate-blocked");
            await RetryFromOperationsAsync(scenario, incident, "service-aggregate-conflict-operations");

            await UntilInstanceAsync(actor, instance.Id, AtNode(4), "aggregate publication");
            Assert.Equal([1, 1], new[] { 0, 1 }.Select(items.CallCount));
            var published = await SharedValueAsync(actor, key);
            Assert.Equal(["receipt-0", "receipt-1"], published.EnumerateArray()
                .Select(row => row.GetProperty("variables").GetProperty("receipt").GetString()));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimerBoundariesInterruptOrRemindWhileItemsRun(bool interrupting)
    {
        var name = interrupting ? "interrupting" : "reminder";
        await using var scenario = await Stack.CreateScenario($"auto-mi-timer-{name}", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync($"{name} timer", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var group = Unique("timer");
            var items = Items(scenario, group).Hold(0, 1);
            var model = BodyWorkflow(Unique($"auto-mi-timer-{name}"), ServiceBody(), ServiceVariables(group, 1, 2));
            AddBoundary(model, TimerBoundary(5, "PT12S", interrupting), interrupting ? "Follow up" : "Send reminder");
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, new());

            // The entry waits eight seconds for a Worker. Had the timer started on entry, it
            // would fire four seconds after the Worker starts; it starts when the body does.
            await Task.Delay(TimeSpan.FromSeconds(8), ScenarioCancellation.Token);
            Assert.False(Visited(await InstanceAsync(actor, instance.Id), 5));
            await Stack.StartWorkerAsync();
            var workerStarted = DateTimeOffset.UtcNow;
            await items.ArrivedAsync(0);
            await items.ArrivedAsync(1);
            var fired = await UntilInstanceAsync(actor, instance.Id, AtNode(6), "the timer to fire", TimeSpan.FromSeconds(60));
            var firedAfter = DateTimeOffset.UtcNow - workerStarted;
            Assert.True(firedAfter >= TimeSpan.FromSeconds(10), $"The timer fired {firedAfter.TotalSeconds:0.0}s after the Worker started.");
            var page = await OpenInstanceAsync(scenario, instance.Id);
            if (interrupting)
            {
                var body = Body(fired, 2)!;
                Assert.NotEqual("active", body.Status);
                Assert.Equal(0, Latest(fired, "receipts").GetArrayLength());
                await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync("Cancelled");
                await ShowItemsAsync(page, body.ExecutionId);
                await CaptureAtViewportsAsync(scenario, "service-timer-cancelled");

                // The answers arrive late and change nothing.
                items.ReleaseAll();
                await UntilAsync(() => Task.FromResult(items.Calls), calls => calls.All(call => call.AnsweredAt is not null || call.Abandoned), "the late answers");
                await Task.Delay(TimeSpan.FromSeconds(3), ScenarioCancellation.Token);
                var after = await InstanceAsync(actor, instance.Id);
                Assert.Equal(0, Latest(after, "receipts").GetArrayLength());
                Assert.False(Visited(after, 4));
                Assert.All((await ItemsAsync(actor, body.ExecutionId)).Items, item => Assert.NotEqual("completed", item.Status));
                Assert.Empty((await IncidentsAsync(actor, instance.Id)).Where(row => row.Status == "open"));
            }
            else
            {
                // The reminder runs beside the body, which keeps its items.
                Assert.Equal("active", Body(fired, 2)!.Status);
                await Assertions.Expect(Summary(page)).ToContainTextAsync("Send reminder");
                await Assertions.Expect(await DiagramDetailsAsync(page, 2)).ToContainTextAsync("Waiting for automatic");
                await CaptureAtViewportsAsync(scenario, "service-timer-reminder");
                items.ReleaseAll();
                var done = await UntilInstanceAsync(actor, instance.Id, detail => AtNode(4)(detail) && AtNode(6)(detail),
                    "the body to finish beside the reminder");
                Assert.Equal(2, Latest(done, "receipts").GetArrayLength());
            }
        });
    }

    [Fact]
    public async Task AConditionInterruptsAQueuedEntryBeforeAnyItemRuns()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-condition-queued", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("queued condition", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var group = Unique("queued");
            var items = Items(scenario, group);
            var model = BodyWorkflow(Unique("auto-mi-condition"), ServiceBody(), ServiceVariables(group, 1, 2));
            model.Variables.Add(Process(8, "stop", "boolean", false));
            AddBoundary(model, ConditionalBoundary(5, "stop == true", interrupting: true), "Stopped before start");
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, new());
            Assert.Equal("asyncBefore", Assert.Single((await InstanceAsync(actor, instance.Id)).ExecutionPositions,
                position => position.NodeId == 2).WaitState);

            await SetVariableAsync(actor, instance.Id, "stop", true);
            var stopped = await UntilInstanceAsync(actor, instance.Id, AtNode(6), "the condition to interrupt the entry");
            Assert.DoesNotContain(stopped.ExecutionPositions, position => position.NodeId == 2 && position.TokenStatus == "active");

            await Stack.StartWorkerAsync();
            await Task.Delay(TimeSpan.FromSeconds(4), ScenarioCancellation.Token);
            var after = await InstanceAsync(actor, instance.Id);
            Assert.Empty(after.AutomaticMultiInstances);
            Assert.Empty(items.Calls);
            Assert.DoesNotContain(await JobsAsync(actor, instance.Id), job => job.Status is "queued" or "running" or "retry");
            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(Summary(page)).ToContainTextAsync("Stopped before start");
            await CaptureAtViewportsAsync(scenario, "service-condition-queued-entry");
        });
    }

    [Fact]
    public async Task AnotherTokensConditionCannotCancelThisBodyAndANonInterruptingOneLeavesItRunning()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-condition-running", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("running conditions", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var group = Unique("left");
            var otherGroup = Unique("right");
            var left = Items(scenario, group).Hold(0);
            var right = Items(scenario, otherGroup).Hold(0);

            // Two parallel bodies: body 2 has a non-interrupting condition, body 9 an interrupting one.
            var model = BodyWorkflow(Unique("auto-mi-two-bodies"), ServiceBody(), ServiceVariables(group, 1));
            model.Variables.Add(Process(5, "otherGroup", "string", otherGroup));
            model.Variables.Add(Process(6, "otherReceipts", "json", Array.Empty<object>()));
            model.Variables.Add(Process(7, "notice", "boolean", false));
            model.Variables.Add(Process(8, "stopOther", "boolean", false));
            AddBoundary(model, ConditionalBoundary(5, "notice == true", interrupting: false), "Noticed");
            var other = ServiceBody();
            other.Id = 9;
            other.Name = "Send other receipts";
            other.Y = 420;
            other.Service!.Url = "${config.exampleApiBaseUrl}/items/${otherGroup}/${mi.index}";
            other.MultiInstance!.ResultVariable = "otherReceipts";
            model.FlowNodes.Add(other);
            model.FlowNodes.Add(new() { Id = 20, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway, X = 130, Y = 152 });
            model.FlowNodes.Add(new() { Id = 21, Name = "Other done", Type = BpmnFlowNodeTypes.EndEvent, X = 480, Y = 452 });
            model.FlowNodes.Add(new()
            {
                Id = 22, Name = "Other stopped", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = 9,
                Conditional = new() { Condition = "stopOther == true" }, X = 270, Y = 494
            });
            model.FlowNodes.Add(new() { Id = 23, Name = "Other stopped end", Type = BpmnFlowNodeTypes.EndEvent, X = 480, Y = 560 });
            model.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 20;
            model.SequenceFlows.Add(new() { Id = 2001, SourceRef = 20, TargetRef = 2 });
            model.SequenceFlows.Add(new() { Id = 2002, SourceRef = 20, TargetRef = 9 });
            model.SequenceFlows.Add(new() { Id = 901, SourceRef = 9, TargetRef = 21 });
            model.SequenceFlows.Add(new() { Id = 2201, SourceRef = 22, TargetRef = 23 });
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, new());
            await left.ArrivedAsync(0);
            await right.ArrivedAsync(0);

            // Stopping the other body leaves this one running.
            await SetVariableAsync(actor, instance.Id, "stopOther", true);
            var afterStop = await UntilInstanceAsync(actor, instance.Id, detail => Visited(detail, 23), "the other body to stop");
            Assert.Equal("active", Body(afterStop, 2)!.Status);
            Assert.NotEqual("active", Body(afterStop, 9)!.Status);

            // A non-interrupting condition starts its handler beside the running body.
            await SetVariableAsync(actor, instance.Id, "notice", true);
            var noticed = await UntilInstanceAsync(actor, instance.Id, AtNode(6), "the non-interrupting handler");
            Assert.Equal("active", Body(noticed, 2)!.Status);
            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(Summary(page)).ToContainTextAsync("Noticed");
            await Assertions.Expect(await DiagramDetailsAsync(page, 2)).ToContainTextAsync("Waiting for automatic");
            await CaptureAtViewportsAsync(scenario, "service-conditions-running");

            left.Release(0);
            right.Release(0);
            var done = await UntilInstanceAsync(actor, instance.Id, detail => AtNode(4)(detail) && AtNode(6)(detail),
                "the body to finish beside its handler");
            Assert.Equal(1, Latest(done, "receipts").GetArrayLength());
            Assert.Equal(0, Latest(done, "otherReceipts").GetArrayLength());
            Assert.False(Visited(done, 21));
        });
    }

    [Fact]
    public async Task AnInterruptingTimerEndsABodyWaitingOnAnItemIncident()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-timer-incident", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("timer during incident", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var group = Unique("stuck");
            var items = Items(scenario, group).Answer(0, 503);
            var model = BodyWorkflow(Unique("auto-mi-timer-incident"), ServiceBody(), ServiceVariables(group, 1));
            AddBoundary(model, TimerBoundary(5, "PT15S", interrupting: true), "Escalate");
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, new());
            var incident = Assert.Single(await UntilAsync(() => IncidentsAsync(actor, instance.Id),
                rows => rows.Any(row => row.Status == "open"), "the item incident"));
            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync("1 blocked/failed");

            var escalated = await UntilInstanceAsync(actor, instance.Id, AtNode(6), "the timer to end the stuck body", TimeSpan.FromSeconds(60));
            Assert.NotEqual("active", Body(escalated, 2)!.Status);
            Assert.NotEqual("open", (await IncidentsAsync(actor, instance.Id)).Single(row => row.Id == incident.Id).Status);
            Assert.Equal(1, items.CallCount(0));
            await ReloadInstanceAsync(page);
            await Assertions.Expect(Summary(page)).ToContainTextAsync("Escalate");
            await CaptureAtViewportsAsync(scenario, "service-timer-during-incident");
        });
    }

    [Fact]
    public async Task AScriptItemIncidentEndsWhenAConditionInterruptsTheBody()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-script-incident", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("script incident and condition", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var body = new FlowNodeModel
            {
                Id = 2, Name = "Divide", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
                ScriptFormat = ScriptFormats.JavaScript, X = 200, Y = 140,
                Job = new() { FailureHandling = JobFailureHandling.BoundaryFirst, RetryDelays = [] },
                MultiInstance = Multi("parallel", "values", "quotients", 4, Local("quotient")),
                Script = "const value = execution.getVariable('mi.item');\n" +
                    "if (value === 0) throw new Error('cannot divide by zero');\n" +
                    "execution.setVariable('quotient', 100 / value);"
            };
            var model = BodyWorkflow(Unique("auto-mi-script-incident"), body,
                Process(1, "values", "number", new[] { 4, 0, 5 }, isArray: true),
                Process(2, "quotients", "json", Array.Empty<object>()),
                Process(3, "stop", "boolean", false));
            AddBoundary(model, ConditionalBoundary(5, "stop == true", interrupting: true), "Stop dividing");
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, new());

            var incident = Assert.Single(await UntilAsync(() => IncidentsAsync(actor, instance.Id),
                rows => rows.Any(row => row.Status == "open"), "the script item incident"));
            Assert.NotNull(incident.AutomaticMultiInstanceItemId);
            var blocked = await UntilInstanceAsync(actor, instance.Id, detail => Body(detail, 2) is { CompletedCount: 2 }, "the siblings");
            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync("1 blocked/failed");
            await ShowItemsAsync(page, Body(blocked, 2)!.ExecutionId);
            await CaptureAtViewportsAsync(scenario, "script-item-blocked");

            await SetVariableAsync(actor, instance.Id, "stop", true);
            var stopped = await UntilInstanceAsync(actor, instance.Id, AtNode(6), "the condition to end the body");
            Assert.NotEqual("active", Body(stopped, 2)!.Status);
            Assert.NotEqual("open", (await IncidentsAsync(actor, instance.Id)).Single(row => row.Id == incident.Id).Status);
            Assert.Equal(0, Latest(stopped, "quotients").GetArrayLength());
            await ReloadInstanceAsync(page);
            await CaptureAtViewportsAsync(scenario, "script-condition-cancelled");
        });
    }

    [Fact]
    public async Task AnOpenBodyBlocksAVersionChangeAndHumanMultiInstanceScreensAreUnchanged()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-version-blocker", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("version blocker", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var group = Unique("version");
            var items = Items(scenario, group).Hold(0);
            var key = Unique("auto-mi-version");
            var first = await Stack.SetupClient.CreateAndPublishAsync(BodyWorkflow(key, ServiceBody(), ServiceVariables(group, 1)));
            var second = await Stack.SetupClient.CreateAndPublishAsync(BodyWorkflow(key, ServiceBody(), ServiceVariables(group, 1)));
            var instance = await actor.Runtime.StartInstanceAsync(first.Id, new());
            await items.ArrivedAsync(0);

            var page = await OpenInstanceAsync(scenario, instance.Id);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Change version" }).ClickAsync();
            await page.Locator("#version-change-target").SelectOptionAsync(second.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await page.Locator("#version-change-reason").FillAsync("Move to the new receipts version");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Preview compatibility" }).ClickAsync();
            var panel = page.Locator("section[aria-labelledby='version-change-heading']");
            await Assertions.Expect(panel).ToContainTextAsync("Blocked");
            await Assertions.Expect(panel).ToContainTextAsync("automatic_multi_instance_open");
            await Assertions.Expect(panel.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Confirm version change" })).ToBeDisabledAsync();
            await CaptureAtViewportsAsync(scenario, "service-version-blocked");

            items.Release(0);
            await UntilInstanceAsync(actor, instance.Id, AtNode(4), "the body to finish");
            await ReloadInstanceAsync(page);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Change version" }).ClickAsync();
            await page.Locator("#version-change-target").SelectOptionAsync(second.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await page.Locator("#version-change-reason").FillAsync("Move to the new receipts version");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Preview compatibility" }).ClickAsync();
            await Assertions.Expect(panel).Not.ToContainTextAsync("automatic_multi_instance_open");

            // A human multi-instance task (the smoke suite's fixture) keeps its own screens.
            var humanModel = JsonSerializer.Deserialize<WorkflowModel>(
                await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-mi.json")),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            humanModel.Id = Unique("auto-mi-human-regression");
            var human = await Stack.SetupClient.CreateAndPublishAsync(humanModel);
            var humanInstance = await actor.Runtime.StartInstanceAsync(human.Id, new());
            var humanDetail = await InstanceAsync(actor, humanInstance.Id);
            Assert.Equal(3, Assert.Single(humanDetail.MultiInstances).Total);
            Assert.Empty(humanDetail.AutomaticMultiInstances);
            var humanPage = await OpenInstanceAsync(scenario, humanInstance.Id);
            await Assertions.Expect(humanPage.Locator("#multi-instance-results, .alert-info").First).ToBeVisibleAsync();
            await Assertions.Expect(AutomaticSection(humanPage)).ToHaveCountAsync(0);
            await CaptureAtViewportsAsync(scenario, "human-multi-instance-unchanged");
        });
    }

    private static async Task<IReadOnlyList<AutomaticMultiInstanceItemSummaryDto>> ItemsAsyncFor(MiActor actor, long instanceId)
    {
        var body = Body(await InstanceAsync(actor, instanceId), 2);
        return body is null ? [] : (await ItemsAsync(actor, body.ExecutionId)).Items;
    }
}
