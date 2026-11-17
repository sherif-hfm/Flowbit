using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>
/// EVENT-04 signal events through real API, UI and Worker processes. Each scenario publishes
/// the canonical signal example under its own family and signal name, so waits from other
/// scenarios never match. Tests poll real state with bounded waits and never edit rows.
/// </summary>
[Collection(ExampleAcceptanceCollection.Name)]
public sealed class SignalAcceptanceTests(ExampleAcceptanceFixture fixture)
{
    private static readonly TimeSpan ScenarioBudget = TimeSpan.FromMinutes(8);
    private static readonly string[] OperatorRoles = ["admin"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private BrowserStackFixture Stack => fixture.Stack;

    /// <summary>S1: a page throw to 101 waits, delivered in two pages, followed to one delivered instance.</summary>
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task S1_PageThrowReachesEveryWaitAndLinksToTheDeliveredInstance(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"signal-s1-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("signal broadcast", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var (workflow, signal) = await PublishExampleAsync("s1");
            var instances = await StartManyAsync(actor, workflow.Id, 101);
            var page = scenario.Page;

            await scenario.OpenUiAsync("signals");
            await FillSignalAsync(page, signal);
            await Assertions.Expect(page.GetByText($"101 waits in 1 family: {workflow.WorkflowKey} (101)")).ToBeVisibleAsync();
            await page.Locator("#signal-workflow-key").FillAsync(workflow.WorkflowKey);
            await page.Locator("#signal-workflow-key").PressAsync("Tab");
            await page.Locator("#signal-payload").FillAsync("{ \"version\": 7, \"effectiveDate\": \"2026-12-01\" }");
            await page.Locator("#signal-payload").PressAsync("Tab");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(page.Locator("section[aria-labelledby='signal-review-heading']"))
                .ToContainTextAsync("Waiting now101 waits");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-success"))
                .ToContainTextAsync(new Regex(@"Broadcast #\d+ froze 101 recipients; the Worker is delivering it\."));
            var broadcastId = long.Parse(Regex.Match(await page.Locator(".alert-success").InnerTextAsync(), @"#(\d+)").Groups[1].Value);

            // The page polls the live broadcast until the Worker has delivered every page of recipients.
            var current = page.Locator("section[aria-labelledby='signal-current-heading']");
            await Assertions.Expect(current.Locator(".status-badge, .badge").First)
                .ToHaveTextAsync("Completed", new LocatorAssertionsToHaveTextOptions { Timeout = 90_000f });
            await Assertions.Expect(current).ToContainTextAsync("Delivered101");
            await Assertions.Expect(current).ToContainTextAsync("101 matching deliveries");
            await Assertions.Expect(current).ToContainTextAsync("Page 1 of 3");
            await CaptureAsync(scenario, "s1-completed");
            await current.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Next" }).ClickAsync();
            await Assertions.Expect(current).ToContainTextAsync("Page 2 of 3");
            await current.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Next" }).ClickAsync();
            await Assertions.Expect(current).ToContainTextAsync("Page 3 of 3");
            await Assertions.Expect(current.Locator("tbody tr")).ToHaveCountAsync(1);
            await Assertions.Expect(current.Locator("tbody tr").First).ToContainTextAsync("Catch continued");

            // Every instance received the signal once and continued to the recalculation task.
            foreach (var id in new[] { instances[0], instances[50], instances[^1] })
            {
                var detail = await actor.Runtime.GetInstanceAsync(id);
                Assert.Contains(detail.ExecutionPositions, position => position.NodeId == 4 && position.TokenStatus == "active");
                Assert.Empty(detail.SignalSubscriptions);
                Assert.Single(detail.History, item => item.Note == "signalReceived");
            }

            // Open the delivered instance from its row; history names the broadcast and links back.
            var link = current.Locator("tbody tr a").First;
            var instanceText = await link.InnerTextAsync();
            await link.ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 }))
                .ToContainTextAsync(instanceText.Replace("Instance ", "Instance "));
            await Assertions.Expect(page.Locator("#history"))
                .ToContainTextAsync($"Received signal '{signal}' from signal-operator (broadcast #{broadcastId})");
            await Assertions.Expect(page.Locator("section[aria-labelledby='instance-summary-heading']"))
                .ToContainTextAsync("Recalculate under the new policy");
            await Assertions.Expect(page.Locator(".flowbit-editor[data-mode='viewer'] .node.instance-marked[data-id='4']"))
                .ToBeVisibleAsync();
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s1-instance");
            await page.Locator("#history").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = $"Broadcast #{broadcastId}" }).ClickAsync();
            await Assertions.Expect(page.Locator("section[aria-labelledby='signal-current-heading']"))
                .ToContainTextAsync($"Broadcast #{broadcastId}");
        });
    }

    /// <summary>
    /// S2: one task with an interrupting and a non-interrupting signal boundary. The first signal adds a
    /// branch and keeps the task in My work; the second interrupts it.
    /// </summary>
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task S2_BoundariesAddABranchThenInterruptTheTask(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"signal-s2-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("signal boundaries", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var path = Path.Combine(Stack.RepositoryRoot, "examples", "signals", "02-signal-boundaries-recall.json");
            var model = JsonSerializer.Deserialize<WorkflowModel>(
                await File.ReadAllTextAsync(path, ScenarioCancellation.Token), JsonOptions)!;
            model.Id = $"signal-s2-{suffix}";
            var recall = $"accept.s2-recall-{suffix}";
            var price = $"accept.s2-price-{suffix}";
            model.FlowNodes.Single(node => node.Id == 3).Signal!.Name = recall;
            model.FlowNodes.Single(node => node.Id == 4).Signal!.Name = price;
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = (await StartManyAsync(actor, workflow.Id, 1, "orderNumber"))[0];
            var page = scenario.Page;

            // Instance detail lists both armed boundaries.
            await scenario.OpenUiAsync($"instances/{instance}");
            var waits = page.Locator("ul[aria-labelledby='instance-signal-waits-heading']");
            await Assertions.Expect(waits).ToContainTextAsync(recall);
            await Assertions.Expect(waits).ToContainTextAsync("Interrupting boundary on node #2");
            await Assertions.Expect(waits).ToContainTextAsync(price);
            await Assertions.Expect(waits).ToContainTextAsync("Non-interrupting boundary on node #2");
            await CaptureAsync(scenario, "s2-armed");

            // The price update adds a branch; the shipment task stays in My work and the boundary stays armed.
            await ThrowFromPageAsync(page, scenario, price, workflow.WorkflowKey, "{ \"unitPrice\": 12.5 }");
            await Assertions.Expect(page.Locator("section[aria-labelledby='signal-current-heading'] tbody tr"))
                .ToContainTextAsync("Boundary added a branch", new LocatorAssertionsToContainTextOptions { Timeout = 60_000f });
            var branched = await actor.Runtime.GetInstanceAsync(instance);
            Assert.Contains(branched.ExecutionPositions, position => position.NodeId == 2 && position.TokenStatus == "active");
            Assert.Contains(branched.ExecutionPositions, position => position.NodeId == 8 && position.TokenStatus == "active");
            Assert.Equal(2, branched.SignalSubscriptions.Count);
            await scenario.OpenUiAsync("inbox");
            var inbox = page.Locator("main");
            // The inbox loads its rows after the page turns interactive.
            var loaded = new LocatorAssertionsToContainTextOptions { Timeout = 30_000f };
            await Assertions.Expect(inbox).ToContainTextAsync("Prepare the shipment", loaded);
            await Assertions.Expect(inbox).ToContainTextAsync("Update the invoice", loaded);
            await CaptureAsync(scenario, "s2-branched-inbox");

            // The recall interrupts the shipment task, ends both waits, and opens the stop task.
            await ThrowFromPageAsync(page, scenario, recall, workflow.WorkflowKey, "{ \"reason\": \"Supplier recall\" }");
            await Assertions.Expect(page.Locator("section[aria-labelledby='signal-current-heading'] tbody tr"))
                .ToContainTextAsync("Boundary interrupted its host", new LocatorAssertionsToContainTextOptions { Timeout = 60_000f });
            var interrupted = await actor.Runtime.GetInstanceAsync(instance);
            Assert.DoesNotContain(interrupted.ExecutionPositions, position => position.NodeId == 2 && position.TokenStatus == "active");
            Assert.Contains(interrupted.ExecutionPositions, position => position.NodeId == 6 && position.TokenStatus == "active");
            Assert.Contains(interrupted.ExecutionPositions, position => position.NodeId == 8 && position.TokenStatus == "active");
            Assert.Empty(interrupted.SignalSubscriptions);
            Assert.Equal("Supplier recall", interrupted.Variables
                .Where(variable => variable.VariableName == "recallReason")
                .MaxBy(variable => variable.Id)!.Value.GetString());
            await scenario.OpenUiAsync("inbox");
            await Assertions.Expect(inbox).ToContainTextAsync("Stop the shipment", loaded);
            await Assertions.Expect(inbox).Not.ToContainTextAsync("Prepare the shipment");

            await scenario.OpenUiAsync($"instances/{instance}");
            await Assertions.Expect(page.Locator("#history")).ToContainTextAsync($"Received signal '{recall}'");
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s2-interrupted");
        });
    }

    private static async Task ThrowFromPageAsync(IPage page, BrowserScenario scenario, string signal, string workflowKey, string payload)
    {
        await scenario.OpenUiAsync("signals");
        await FillSignalAsync(page, signal);
        await page.Locator("#signal-workflow-key").FillAsync(workflowKey);
        await page.Locator("#signal-workflow-key").PressAsync("Tab");
        await page.Locator("#signal-payload").FillAsync(payload);
        await page.Locator("#signal-payload").PressAsync("Tab");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
        await Assertions.Expect(page.Locator("section[aria-labelledby='signal-review-heading']")).ToContainTextAsync("Waiting now1 wait");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
        await Assertions.Expect(page.Locator(".alert-success")).ToContainTextAsync("froze 1 recipient; the Worker is delivering it.");
    }

    /// <summary>S3: a payload that fails a mapping fails its delivery; a corrected version and Retry failed deliver it.</summary>
    [Fact]
    public async Task S3_FailedMappingIsRetriedAfterAVersionChange()
    {
        await using var scenario = await Stack.CreateScenario("signal-s3-1440x900", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("signal retry", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var (v1, signal) = await PublishExampleAsync("s3");
            var instance = (await StartManyAsync(actor, v1.Id, 1))[0];

            // A text version fails the number mapping; the instance keeps waiting.
            var broadcast = await ThrowAsync(actor.Http, new ThrowSignalRequest(signal, v1.WorkflowKey,
                Payload: JsonSerializer.SerializeToElement(new { version = "seven" })));
            var failed = await UntilAsync(() => GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{broadcast.Id}"),
                item => item.Status == SignalBroadcastStatuses.CompletedWithIssues, "the failing delivery to finish");
            Assert.Equal(1, failed.FailedCount);
            var waiting = await actor.Runtime.GetInstanceAsync(instance);
            Assert.Single(waiting.SignalSubscriptions);

            await scenario.OpenUiAsync($"signals?broadcastId={broadcast.Id}");
            var current = scenario.Page.Locator("section[aria-labelledby='signal-current-heading']");
            await Assertions.Expect(current.Locator("tbody tr")).ToContainTextAsync("The payload failed a mapping");
            await Assertions.Expect(current.Locator(".signal-result-description")).ToContainTextAsync("policyVersion");
            await CaptureAsync(scenario, "s3-failed");

            // Version 2 reads the version as text into a new variable; the change only warns.
            var corrected = await LoadExampleAsync(v1.WorkflowKey, signal);
            var catchNode = corrected.FlowNodes.Single(node => node.Id == 2);
            catchNode.Signal!.OutputMappings =
            [
                new MessageOutputMappingModel
                {
                    Variable = "policyLabel", Path = "version", DataType = WorkflowVariableTypes.String,
                    IsArray = false, Required = true,
                },
            ];
            var v2 = await actor.Runtime.CreateAndPublishAsync(corrected);
            var preview = await PostAsync<InstanceVersionChangePreviewDto>(actor.Http,
                $"/api/instances/{instance}/version-change/preview", new { targetWorkflowId = v2.Id });
            Assert.Empty(preview.Blockers);
            Assert.Contains(preview.Warnings, issue => issue.Code == "signal_mapping_contract_changed");
            await PostAsync<JsonElement>(actor.Http, $"/api/instances/{instance}/version-change",
                new ChangeInstanceVersionRequest(v2.Id, v1.Id, waiting.UpdatedAt, "EVENT-04 S3 corrected mapping"));

            await scenario.Page.ReloadAsync();
            await WaitForInteractiveAsync(scenario.Page);
            await current.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retry 1 failed" }).ClickAsync();
            await Assertions.Expect(scenario.Page.Locator(".alert-success"))
                .ToContainTextAsync($"The failed deliveries of broadcast #{broadcast.Id} are queued again.");
            await Assertions.Expect(current.Locator("tbody tr"))
                .ToContainTextAsync("Catch continued", new LocatorAssertionsToContainTextOptions { Timeout = 60_000f });
            await Assertions.Expect(current).ToContainTextAsync("Completed");
            var delivered = await actor.Runtime.GetInstanceAsync(instance);
            Assert.Equal("seven", delivered.Variables
                .Where(variable => variable.VariableName == "policyLabel")
                .MaxBy(variable => variable.Id)!.Value.GetString());
            Assert.Contains(delivered.ExecutionPositions, position => position.NodeId == 4 && position.TokenStatus == "active");
            await CaptureAsync(scenario, "s3-retried");
        });
    }

    /// <summary>S4: cancellation while the Worker is stopped, then a lease that expires mid-delivery.</summary>
    [Fact]
    public async Task S4_CancelWithTheWorkerStoppedThenRecoverAnExpiredLeaseWithoutDuplicates()
    {
        await using var scenario = await Stack.CreateScenario("signal-s4-1440x900", 1440, 900,
            scenarioTimeout: TimeSpan.FromMinutes(12));
        await scenario.RunAsync("signal cancellation", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var (workflow, signal) = await PublishExampleAsync("s4");
            var instances = await StartManyAsync(actor, workflow.Id, 400);
            var first = await ThrowAsync(actor.Http, new ThrowSignalRequest(signal, workflow.WorkflowKey,
                Payload: JsonSerializer.SerializeToElement(new { version = 3 })));
            Assert.Equal(400, first.TotalDeliveryCount);

            // Cancel from the page; no Worker has touched the broadcast.
            await scenario.OpenUiAsync($"signals?broadcastId={first.Id}");
            var current = scenario.Page.Locator("section[aria-labelledby='signal-current-heading']");
            await current.GetByLabel("Cancellation reason").FillAsync("S4 cancelled before delivery");
            await current.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel pending deliveries" }).ClickAsync();
            await Assertions.Expect(scenario.Page.Locator(".alert-success"))
                .ToContainTextAsync($"Broadcast #{first.Id} was cancelled");
            await Assertions.Expect(current).ToContainTextAsync("Cancelled400");
            await Assertions.Expect(current).ToContainTextAsync("signal-operator · S4 cancelled before delivery");
            await CaptureAsync(scenario, "s4-cancelled");

            // A restarted Worker delivers none of the cancelled work; every instance still waits.
            await Stack.StartWorkerAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), ScenarioCancellation.Token);
            var cancelled = await GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{first.Id}");
            Assert.Equal((SignalBroadcastStatuses.Cancelled, 400, 0), (cancelled.Status, cancelled.CancelledCount, cancelled.DeliveredCount));
            Assert.Single((await actor.Runtime.GetInstanceAsync(instances[0])).SignalSubscriptions);

            // Stop the Worker while it delivers the next broadcast; its lease expires and a new Worker finishes.
            var second = await ThrowAsync(actor.Http, new ThrowSignalRequest(signal, workflow.WorkflowKey,
                Payload: JsonSerializer.SerializeToElement(new { version = 4 })));
            var partial = await UntilAsync(() => DeliveredCountAsync(actor.Http, second.Id), count => count > 0,
                "the Worker to deliver the first recipients", TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(50));
            await Stack.StopWorkerAsync();
            var atStop = await DeliveredCountAsync(actor.Http, second.Id);
            Assert.True(atStop < 400, $"The Worker delivered all 400 recipients before it stopped (first read {partial}).");
            await Stack.StartWorkerAsync();
            var done = await UntilAsync(() => GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{second.Id}"),
                item => item.Status != SignalBroadcastStatuses.Delivering, "the expired lease to recover", TimeSpan.FromMinutes(4));
            Assert.Equal((SignalBroadcastStatuses.Completed, 400, 0, 0), (done.Status, done.DeliveredCount, done.FailedCount, done.SkippedCount));
            foreach (var id in instances)
            {
                var detail = await actor.Runtime.GetInstanceAsync(id);
                Assert.Single(detail.History, item => item.Note == "signalReceived");
            }

            await scenario.OpenUiAsync($"signals?broadcastId={second.Id}");
            await Assertions.Expect(current).ToContainTextAsync("Delivered400");
            await CaptureAsync(scenario, "s4-recovered");
        });
    }

    /// <summary>S5: a managed client throws twice with one idempotency key; the page shows one broadcast.</summary>
    [Fact]
    public async Task S5_ManagedClientThrowIsIdempotentAndShownAsTheClient()
    {
        await using var scenario = await Stack.CreateScenario("signal-s5-1440x900", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("client throw", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var (workflow, signal) = await PublishExampleAsync("s5");
            await StartManyAsync(actor, workflow.Id, 2);
            var clientId = $"signal-s5-{Guid.NewGuid():N}"[..20];
            var created = await PostAsync<CreateSharedVariableClientResult>(actor.Http, "/api/shared-variable-clients",
                new CreateSharedVariableClientRequest(clientId, "Signal S5 client",
                    [SharedVariableClientScopes.SignalsThrow, SharedVariableClientScopes.SignalsRead]));

            using var client = new HttpClient { BaseAddress = new Uri(Stack.ApiBaseAddress) };
            client.DefaultRequestHeaders.Add("X-Client-Id", clientId);
            client.DefaultRequestHeaders.Add("X-Client-Secret", created.ClientSecret);
            var request = new ThrowSignalRequest(signal, workflow.WorkflowKey,
                Payload: JsonSerializer.SerializeToElement(new { version = 5 }), IdempotencyKey: "s5-change-5");
            using var firstResponse = await client.PostAsJsonAsync("/api/signal-broadcasts", request, JsonOptions, ScenarioCancellation.Token);
            Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
            var first = (await firstResponse.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
            using var replayResponse = await client.PostAsJsonAsync("/api/signal-broadcasts", request, JsonOptions, ScenarioCancellation.Token);
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            Assert.Equal(first.Id, (await replayResponse.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!.Id);
            using var conflict = await client.PostAsJsonAsync("/api/signal-broadcasts",
                request with { Payload = JsonSerializer.SerializeToElement(new { version = 6 }) }, JsonOptions, ScenarioCancellation.Token);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            await UntilAsync(() => GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{first.Id}"),
                item => item.Status == SignalBroadcastStatuses.Completed, "the client's broadcast to finish");

            await scenario.OpenUiAsync("signals");
            var rows = scenario.Page.Locator("section[aria-labelledby='signal-history-heading'] tbody tr")
                .Filter(new LocatorFilterOptions { HasText = signal });
            await Assertions.Expect(rows).ToHaveCountAsync(1);
            await Assertions.Expect(rows).ToContainTextAsync($"{clientId} (API client)");
            await rows.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Open" }).ClickAsync();
            var current = scenario.Page.Locator("section[aria-labelledby='signal-current-heading']");
            await Assertions.Expect(current).ToContainTextAsync($"Thrown by{clientId} (API client)");
            await Assertions.Expect(current).ToContainTextAsync("Delivered2");
            await CaptureAsync(scenario, "s5-client-broadcast");
        });
    }

    private async Task<(WorkflowSummaryDto Workflow, string Signal)> PublishExampleAsync(string scenarioName)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var model = await LoadExampleAsync($"signal-{scenarioName}-{suffix}", $"accept.{scenarioName}-{suffix}");
        return (await Stack.SetupClient.CreateAndPublishAsync(model), model.FlowNodes.Single(node => node.Id == 2).Signal!.Name!);
    }

    private async Task<WorkflowModel> LoadExampleAsync(string workflowKey, string signal)
    {
        var path = Path.Combine(Stack.RepositoryRoot, "examples", "signals", "01-signal-catch-policy-change.json");
        var model = JsonSerializer.Deserialize<WorkflowModel>(
            await File.ReadAllTextAsync(path, ScenarioCancellation.Token), JsonOptions)!;
        model.Id = workflowKey;
        model.FlowNodes.Single(node => node.Id == 2).Signal!.Name = signal;
        return model;
    }

    private static async Task<long[]> StartManyAsync(
        UiActor actor, long workflowId, int count, string keyVariable = "claimNumber")
    {
        var ids = new long[count];
        for (var index = 0; index < count; index++)
        {
            var started = await actor.Runtime.StartInstanceAsync(workflowId, new Dictionary<string, JsonElement>
            {
                [keyVariable] = JsonSerializer.SerializeToElement($"C-{index + 1:000}"),
            });
            ids[index] = started.Id;
        }
        return ids;
    }

    private static async Task FillSignalAsync(IPage page, string signal)
    {
        // The circuit may not handle the first change yet; type again until the count appears.
        var name = page.Locator("#signal-name");
        var matched = page.GetByText(new Regex("waits? in 1 famil"));
        for (var attempt = 0; attempt < 20 && await matched.CountAsync() == 0; attempt++)
        {
            await name.FillAsync(signal);
            await name.PressAsync("Tab");
            await Task.Delay(1_000, ScenarioCancellation.Token);
        }
        await Assertions.Expect(matched).ToHaveCountAsync(1);
    }

    private static async Task<SignalBroadcastDetailDto> ThrowAsync(HttpClient http, ThrowSignalRequest request)
    {
        using var response = await http.PostAsJsonAsync("/api/signal-broadcasts", request, JsonOptions, ScenarioCancellation.Token);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted,
            $"POST /api/signal-broadcasts: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
    }

    private static async Task<long> DeliveredCountAsync(HttpClient http, long broadcastId) =>
        (await GetAsync<PagedResult<SignalDeliveryDto>>(http,
            $"/api/signal-broadcasts/{broadcastId}/deliveries?status=delivered&page=1&pageSize=1")).TotalCount;

    private async Task<UiActor> ApplyActorAsync(BrowserScenario scenario, string user, string[] roles)
    {
        var identity = new IdentityScreen(scenario.Page, Stack.UiBaseAddress);
        var token = await identity.GenerateAndApplyIdentityAsync(user, roles);
        scenario.OnCleanup(identity.ClearIdentityAsync);
        var http = new HttpClient { BaseAddress = new Uri(Stack.ApiBaseAddress) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new(Stack.CreateClient(user, roles, token, $"{scenario.Name}-{user}"), http);
    }

    private sealed record UiActor(WorkflowFixtureClient Runtime, HttpClient Http) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { Http.Dispose(); await Runtime.DisposeAsync(); }
    }

    private static async Task<T> GetAsync<T>(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path, ScenarioCancellation.Token);
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, ScenarioCancellation.Token))!;
    }

    private static async Task<T> PostAsync<T>(HttpClient http, string path, object body)
    {
        using var response = await http.PostAsJsonAsync(path, body, JsonOptions, ScenarioCancellation.Token);
        Assert.True(response.IsSuccessStatusCode, $"POST {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, ScenarioCancellation.Token))!;
    }

    private static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> accepted, string description,
        TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var timer = Stopwatch.StartNew();
        var budget = timeout ?? TimeSpan.FromSeconds(90);
        while (true)
        {
            var value = await read();
            if (accepted(value)) return value;
            if (timer.Elapsed >= budget) throw new TimeoutException($"Timed out waiting for {description}.");
            await Task.Delay(interval ?? TimeSpan.FromMilliseconds(250), ScenarioCancellation.Token);
        }
    }

    private static Task WaitForInteractiveAsync(IPage page) => Assertions.Expect(page.Locator(".app-shell"))
        .ToHaveAttributeAsync("data-interactive", "true");

    private static async Task AssertNoHorizontalOverflowAsync(IPage page) =>
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 2"),
            "The page overflows horizontally.");

    private static async Task CaptureAsync(BrowserScenario scenario, string name)
    {
        var page = scenario.Page;
        await PageScrolling.ScrollToTopAsync(page);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(scenario.ArtifactDirectory, name + ".png"), FullPage = true,
        });
    }
}
