using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>UI-14 evidence from real HTTP, Worker transitions and delayed genuine responses.</summary>
[Collection(AcceptanceCollection.Name)]
public sealed partial class InstanceDiagramHistoryAcceptanceTests(AcceptanceFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private BrowserStackFixture Stack => fixture.Stack;

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task DurableMiEntryRefreshesAndWaitingVersionSwitchKeepsHistoryScope(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario($"ui14-durable-mi-{width}x{height}", width, height);
        await scenario.RunAsync("durable multi-instance diagram history", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "diagram-operator", ["admin"]);
            var model = Model(durableMi: true);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var started = await actor.Runtime.StartInstanceAsync(workflow.Id);
            var page = await OpenAsync(scenario, started.Id);
            await Toggle(page).CheckAsync();
            await WaitForHistoryAsync(page);
            var before = await SummaryAsync(actor, started.Id);
            Assert.DoesNotContain(before.Flows, flow => flow.FlowId == 501);
            await Assertions.Expect(page.Locator("#diagram .instance-diagram-coverage")).ToContainTextAsync("uncertain");
            await CaptureAsync(scenario, "before-durable-arrival");

            await Stack.StartWorkerAsync();
            var arrived = await UntilAsync(() => actor.Runtime.GetInstanceAsync(started.Id),
                detail => detail.UserTasks is { ActiveCount: 1, PendingCount: 1 }, "MI fan-out");
            await RefreshAsync(page);
            await Assertions.Expect(Flow(page, 501)).ToHaveCountAsync(1);
            var entered = await SummaryAsync(actor, started.Id);
            Assert.True(entered.InstanceUpdatedAt > before.InstanceUpdatedAt);
            Assert.Equal(1, Assert.Single(entered.Flows, flow => flow.FlowId == 501).TraversalCount);
            await SelectHistoryAsync(page, "Review items");
            await Assertions.Expect(page.Locator(".instance-diagram-recorded-executions li")).ToHaveCountAsync(2);
            await CaptureAsync(scenario, "mi-fanout-history");

            // Drive the real version-change form while history is on. Its success
            // must refresh history itself, without an extra manual Refresh.
            model.Name += " version 2";
            var next = await Stack.SetupClient.CreateAndPublishAsync(model);
            await page.GetByRole(AriaRole.Button, new() { Name = "Change version", Exact = true }).ClickAsync();
            await page.Locator("#version-change-target").SelectOptionAsync(next.Id.ToString());
            await page.Locator("#version-change-reason").FillAsync("UI-14 waiting version evidence");
            await page.GetByRole(AriaRole.Button, new() { Name = "Preview compatibility", Exact = true }).ClickAsync();
            var confirm = page.GetByRole(AriaRole.Button, new() { Name = "Confirm version change", Exact = true });
            await Assertions.Expect(confirm).ToBeEnabledAsync();
            scenario.ExpectDialogOnce("confirm", $"Change instance #{started.Id} from v{workflow.Version} to v{next.Version}?", accept: true);
            await confirm.ClickAsync();
            await Assertions.Expect(Toggle(page)).ToBeCheckedAsync();
            await Assertions.Expect(page.Locator(".instance-diagram-history-status")).ToContainTextAsync($"workflow version {next.Version}");
            var switched = await SummaryAsync(actor, started.Id);
            Assert.Equal(next.Id, switched.WorkflowId);
            Assert.DoesNotContain(switched.Flows, flow => flow.FlowId == 101);
            Assert.Equal(1, Assert.Single(switched.Flows, flow => flow.FlowId == 501).TraversalCount);
            await CaptureAsync(scenario, "waiting-version-history");

            for (var item = 0; item < 2; item++)
            {
                var task = Assert.Single(await actor.Runtime.GetInboxAsync(started.Id));
                await actor.Runtime.TakeUserTaskFlowAsync(task.UserTaskId, 201);
            }
            await RefreshAsync(page);
            await Assertions.Expect(Flow(page, 201)).ToHaveCountAsync(1);
            var finished = await SummaryAsync(actor, started.Id);
            Assert.Equal(1, Assert.Single(finished.Flows, flow => flow.FlowId == 201).TraversalCount);
            await CaptureAsync(scenario, "mi-outcome-once");
            await File.WriteAllTextAsync(Path.Combine(scenario.ArtifactDirectory, "evidence.json"),
                JsonSerializer.Serialize(new { before, arrived, entered, switched, finished }, Json), ScenarioCancellation.Token);
            await Stack.StopWorkerAsync();
        });
    }

    [Fact]
    public async Task IdentityReplacementDiscardsHeldSummaryAndAuthorizedExecutionResponses()
    {
        await using var scenario = await Stack.CreateScenario("ui14-held-identity", 1024, 768);
        await scenario.RunAsync("identity changes during genuine history responses", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "diagram-reader", ["admin"]);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(Model(durableMi: false));
            var started = await actor.Runtime.StartInstanceAsync(workflow.Id);
            var page = await OpenAsync(scenario, started.Id);
            var secondPage = await scenario.Context.NewPageAsync();
            var identity = new IdentityScreen(secondPage, Stack.UiBaseAddress);
            var proxy = Stack.ApiProxy!;
            try
            {
                using (var gate = proxy.GateNextResponse(request => request.Method == "GET"
                           && request.PathAndQuery == $"/api/instances/{started.Id}/diagram-history", TimeSpan.FromSeconds(60)))
                {
                    await Toggle(page).CheckAsync();
                    var held = await gate.Entered.WaitAsync(TimeSpan.FromSeconds(15), ScenarioCancellation.Token);
                    Assert.Equal(200, held.StatusCode);
                    await Assertions.Expect(page.Locator(".instance-diagram-history-status")).ToContainTextAsync("Loading recorded history");
                    await identity.GenerateAndApplyIdentityAsync("ordinary-reviewer", ["Reviewer"]);
                    await Assertions.Expect(Toggle(page)).Not.ToBeCheckedAsync();
                    gate.Release();
                    await UntilAsync(() => Task.FromResult(proxy.Requests.Single(request => request.Id == held.Id)),
                        request => request.CompletedAt is not null, "the held summary to finish");
                    await AssertHistoryStaysClearedAsync(page);
                    await CaptureAsync(scenario, "held-summary-discarded");
                }

                // Both lookups use POST search. Hold their genuine responses independently:
                // one is current-link lookup, the other is version-filtered history.
                await identity.GenerateAndApplyIdentityAsync("diagram-reader", ["admin"]);
                await Toggle(page).CheckAsync();
                await WaitForHistoryAsync(page);
                using (var gate = proxy.GateNextResponse(request => request.Method == "POST"
                           && request.PathAndQuery == "/api/node-executions/search", TimeSpan.FromSeconds(60)))
                using (var historyGate = proxy.GateNextResponse(request => request.Method == "POST"
                           && request.PathAndQuery == "/api/node-executions/search", TimeSpan.FromSeconds(60)))
                {
                    await SelectHistoryAsync(page, "Start");
                    var held = await gate.Entered.WaitAsync(TimeSpan.FromSeconds(15), ScenarioCancellation.Token);
                    var historyHeld = await historyGate.Entered.WaitAsync(TimeSpan.FromSeconds(15), ScenarioCancellation.Token);
                    Assert.Equal(200, held.StatusCode);
                    Assert.Equal(200, historyHeld.StatusCode);
                    await Assertions.Expect(page.Locator(".instance-diagram-details")).ToContainTextAsync("Loading recorded executions");
                    await identity.GenerateAndApplyIdentityAsync("ordinary-reviewer", ["Reviewer"]);
                    await Assertions.Expect(Toggle(page)).Not.ToBeCheckedAsync();
                    gate.Release();
                    historyGate.Release();
                    await UntilAsync(() => Task.FromResult(proxy.Requests),
                        requests => requests.Where(request => request.Id == held.Id || request.Id == historyHeld.Id)
                            .All(request => request.CompletedAt is not null), "the held execution responses to finish");
                    await AssertHistoryStaysClearedAsync(page);
                }
                await Toggle(page).CheckAsync();
                await WaitForHistoryAsync(page);
                await Assertions.Expect(Flow(page, 101)).ToHaveCountAsync(1);
                await SelectHistoryAsync(page, "Start");
                await Assertions.Expect(page.Locator(".instance-diagram-details")).ToContainTextAsync("0 execution records available to you");
                await Assertions.Expect(page.Locator(".instance-diagram-details a[href^='node-executions/']")).ToHaveCountAsync(0);
                await CaptureAsync(scenario, "ordinary-reviewer-history-without-execution-links");
                await File.WriteAllTextAsync(Path.Combine(scenario.ArtifactDirectory, "requests.json"),
                    JsonSerializer.Serialize(proxy.Requests, Json), ScenarioCancellation.Token);
            }
            finally
            {
                proxy.ReleaseAllGates();
                await secondPage.CloseAsync();
            }
        });
    }

    [Fact]
    public async Task DurableConditionalBoundaryShowsRecordedIncomingAndBoundaryOutgoingFlows()
    {
        await using var scenario = await Stack.CreateScenario("ui14-durable-boundary", 1440, 900);
        await scenario.RunAsync("durable conditional boundary history", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "diagram-boundary-reader", ["admin"]);
            var model = Model(durableMi: false);
            model.Variables.Add(new VariableModel { Id = 1, Name = "interrupt", DataType = "boolean", DefaultValue = JsonSerializer.SerializeToElement(true) });
            model.FlowNodes.Add(new FlowNodeModel
            {
                Id = 4, Name = "Conditional boundary", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent,
                AttachedToRef = 2, CancelActivity = true, X = 390, Y = 210,
                Conditional = new ConditionalDefinitionModel { Condition = "interrupt == true", DeliveryMode = "durableAsync" }
            });
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 3, Name = "Interrupted outcome" });
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var started = await actor.Runtime.StartInstanceAsync(workflow.Id);
            var page = await OpenAsync(scenario, started.Id);
            await Toggle(page).CheckAsync();
            await WaitForHistoryAsync(page);
            Assert.DoesNotContain((await SummaryAsync(actor, started.Id)).Flows, flow => flow.FlowId == 401);
            await Stack.StartWorkerAsync();
            await UntilAsync(() => actor.Runtime.GetInstanceAsync(started.Id), detail => detail.Status == "completed", "conditional boundary continuation");
            await RefreshAsync(page);
            await Assertions.Expect(Flow(page, 101)).ToHaveCountAsync(1);
            await Assertions.Expect(Flow(page, 401)).ToHaveCountAsync(1);
            var summary = await SummaryAsync(actor, started.Id);
            Assert.Equal(new[] { 101, 401 }, summary.Flows.Select(flow => flow.FlowId));
            Assert.All(summary.Flows, flow => Assert.Equal(1, flow.TraversalCount));
            await SelectHistoryAsync(page, "Conditional boundary");
            await CaptureAsync(scenario, "durable-boundary-history");
            await Stack.StopWorkerAsync();
        });
    }

    private static ILocator Toggle(IPage page) => page.GetByRole(AriaRole.Checkbox, new() { Name = "Show recorded history" });
    private static ILocator Flow(IPage page, int id) => page.Locator($"#diagram .edge[data-flow='{id}'] .instance-history-line");

    // Completion at the proxy precedes the UI continuation; observe that late answers stay discarded.
    private static async Task AssertHistoryStaysClearedAsync(IPage page)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            Assert.False(await Toggle(page).IsCheckedAsync());
            Assert.Equal(0, await page.Locator(".instance-history-line").CountAsync());
            Assert.Equal(0, await page.Locator(".instance-diagram-recorded-executions li").CountAsync());
            await Task.Delay(200, ScenarioCancellation.Token);
        }
    }

    private async Task<IPage> OpenAsync(BrowserScenario scenario, long instanceId)
    {
        var page = await scenario.OpenUiAsync($"instances/{instanceId}");
        await Assertions.Expect(page.Locator(".app-shell")).ToHaveAttributeAsync("data-interactive", "true");
        await Assertions.Expect(page.Locator("#diagram")).ToHaveAttributeAsync("data-diagram-ready", "true");
        return page;
    }

    private static async Task WaitForHistoryAsync(IPage page)
    {
        await Assertions.Expect(page.Locator(".instance-diagram-history-status")).ToContainTextAsync("Snapshot");
        await Assertions.Expect(page.Locator(".instance-diagram-history-status")).Not.ToContainTextAsync("Loading recorded history");
    }

    private static async Task RefreshAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh", Exact = true }).ClickAsync();
        await WaitForHistoryAsync(page);
    }

    private static async Task SelectHistoryAsync(IPage page, string name)
    {
        var list = page.Locator(".instance-diagram-history-list");
        if (await list.GetAttributeAsync("open") is null) await list.Locator("summary").ClickAsync();
        var exactName = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(name) + "$");
        var button = list.GetByRole(AriaRole.Button).Filter(new() { Has = page.Locator("strong", new() { HasTextRegex = exactName }) });
        await button.FocusAsync();
        await button.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#instance-diagram-details-heading")).ToHaveTextAsync(name);
    }

    private async Task<Actor> ApplyActorAsync(BrowserScenario scenario, string user, string[] roles)
    {
        var identity = new IdentityScreen(scenario.Page, Stack.UiBaseAddress);
        var token = await identity.GenerateAndApplyIdentityAsync(user, roles);
        scenario.OnCleanup(async () => { Stack.ApiProxy!.ReleaseAllGates(); await Stack.StopWorkerAsync(); await identity.ClearIdentityAsync(); });
        var http = new HttpClient { BaseAddress = new Uri(Stack.ApiBaseAddress) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new(http, Stack.CreateClient(user, roles, token, scenario.Name + "-http"));
    }

    private sealed record Actor(HttpClient Http, WorkflowFixtureClient Runtime) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Http.Dispose(); await Runtime.DisposeAsync(); }
    }

    private static Task<InstanceDiagramHistoryDto> SummaryAsync(Actor actor, long id) => ReadAsync<InstanceDiagramHistoryDto>(actor.Http, $"/api/instances/{id}/diagram-history");
    private static async Task<T> ReadAsync<T>(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path, ScenarioCancellation.Token);
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json, ScenarioCancellation.Token))!;
    }
    private static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> accept, string description)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            var item = await read();
            if (accept(item)) return item;
            await Task.Delay(250, ScenarioCancellation.Token);
        }
        throw new TimeoutException("Timed out waiting for " + description);
    }
    private static async Task CaptureAsync(BrowserScenario scenario, string name)
    {
        await PageScrolling.ScrollToTopAsync(scenario.Page);
        Assert.True(await scenario.Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 2"), "Horizontal page overflow");
        await scenario.Page.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, name + ".png"), FullPage = true });
    }

    private static WorkflowModel Model(bool durableMi)
    {
        var model = new WorkflowModel
        {
            Id = "ui14-acceptance-" + Guid.NewGuid().ToString("N"), Name = "Recorded history acceptance", InitialEventId = 1,
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = "startEvent", X = 60, Y = 130 },
                new() { Id = 2, Name = durableMi ? "Review items" : "Review", Type = "userTask", X = 340, Y = 130 },
                new() { Id = 3, Name = "End", Type = "endEvent", X = 760, Y = 130 }
            ],
            SequenceFlows = [new() { Id = 101, SourceRef = 1, TargetRef = 2 }, new() { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Complete" }]
        };
        if (!durableMi) return model;
        model.FlowNodes[1].X = 500;
        model.FlowNodes[2].X = 840;
        model.FlowNodes.Add(new() { Id = 5, Name = "Durable source", Type = "task", AsyncAfter = true, X = 240, Y = 130 });
        model.SequenceFlows[0].TargetRef = 5;
        model.SequenceFlows.Add(new() { Id = 501, SourceRef = 5, TargetRef = 2, Name = "Enter review" });
        model.Variables.Add(new() { Id = 1, Name = "results", DataType = "json", DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) });
        model.FlowNodes[1].AsyncBefore = true;
        model.FlowNodes[1].MultiInstance = new() { Mode = "sequential", Source = "cardinality", CardinalityExpression = "2", ResultVariable = "results", CompletionEvaluation = "afterAll" };
        model.SequenceFlows[1].CompletionCondition = "CountFlow(201) >= 2";
        model.SequenceFlows[1].CompletionPriority = 1;
        model.SequenceFlows.Add(new() { Id = 202, SourceRef = 2, TargetRef = 3, IsDefault = true, IsSelectable = false });
        return model;
    }
}
