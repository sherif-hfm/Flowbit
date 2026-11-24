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
public sealed partial class SignalAcceptanceTests(ExampleAcceptanceFixture fixture)
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
                .ToContainTextAsync("Reaches now101 waits");
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

    /// <summary>
    /// S6: two orders of the signal boundaries example. A price update thrown by the first order's
    /// business key adds a branch only there; a recall thrown by the second order's instance ID
    /// interrupts only its shipment task.
    /// </summary>
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task S6_BusinessKeyAndInstanceScopesReachOnlyTheirOrder(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"signal-s6-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("signal scopes", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var path = Path.Combine(Stack.RepositoryRoot, "examples", "signals", "02-signal-boundaries-recall.json");
            var model = JsonSerializer.Deserialize<WorkflowModel>(
                await File.ReadAllTextAsync(path, ScenarioCancellation.Token), JsonOptions)!;
            Assert.Equal("orderNumber", model.FlowNodes.Single(node => node.Id == 1).BusinessKey!.Variable);
            model.Id = $"signal-s6-{suffix}";
            var recall = $"accept.s6-recall-{suffix}";
            var price = $"accept.s6-price-{suffix}";
            model.FlowNodes.Single(node => node.Id == 3).Signal!.Name = recall;
            model.FlowNodes.Single(node => node.Id == 4).Signal!.Name = price;
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            // The orders' business keys are C-001 and C-002.
            var orders = await StartManyAsync(actor, workflow.Id, 2, "orderNumber");
            var page = scenario.Page;
            var current = page.Locator("section[aria-labelledby='signal-current-heading']");

            // The price update for C-001 only.
            await scenario.OpenUiAsync("signals");
            await FillSignalAsync(page, price);
            await page.Locator("#signal-workflow-key").FillAsync(workflow.WorkflowKey);
            await page.Locator("#signal-workflow-key").PressAsync("Tab");
            await page.Locator("#signal-business-key").FillAsync("C-001");
            await page.Locator("#signal-business-key").PressAsync("Tab");
            await Assertions.Expect(page.GetByText($"1 wait for '{price}' with business key C-001 in {workflow.WorkflowKey}."))
                .ToBeVisibleAsync();
            await page.Locator("#signal-payload").FillAsync("{ \"unitPrice\": 13 }");
            await page.Locator("#signal-payload").PressAsync("Tab");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            var review = page.Locator("section[aria-labelledby='signal-review-heading']");
            await Assertions.Expect(review).ToContainTextAsync($"{workflow.WorkflowKey} · business key C-001");
            await Assertions.Expect(review).ToContainTextAsync("Reaches now1 wait");
            await CaptureAsync(scenario, "s6-business-key-review");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-success")).ToContainTextAsync("froze 1 recipient; the Worker is delivering it.");
            await Assertions.Expect(current.Locator("tbody tr"))
                .ToContainTextAsync("Boundary added a branch", new LocatorAssertionsToContainTextOptions { Timeout = 60_000f });
            await Assertions.Expect(current).ToContainTextAsync($"Instance #{orders[0]}");

            var first = await actor.Runtime.GetInstanceAsync(orders[0]);
            Assert.Contains(first.ExecutionPositions, position => position.NodeId == 8 && position.TokenStatus == "active");
            var second = await actor.Runtime.GetInstanceAsync(orders[1]);
            Assert.DoesNotContain(second.ExecutionPositions, position => position.NodeId == 8 && position.TokenStatus == "active");
            Assert.Equal(2, second.SignalSubscriptions.Count);

            // The second order's detail still lists both waits.
            await scenario.OpenUiAsync($"instances/{orders[1]}");
            var waits = page.Locator("ul[aria-labelledby='instance-signal-waits-heading']");
            await Assertions.Expect(waits).ToContainTextAsync(recall);
            await Assertions.Expect(waits).ToContainTextAsync(price);

            // The recall for the second order by its instance ID.
            await scenario.OpenUiAsync("signals");
            await FillSignalAsync(page, recall);
            await page.GetByLabel("One instance", new PageGetByLabelOptions { Exact = true }).CheckAsync();
            await page.Locator("#signal-instance-id").FillAsync(orders[1].ToString(System.Globalization.CultureInfo.InvariantCulture));
            await page.Locator("#signal-instance-id").PressAsync("Tab");
            await Assertions.Expect(page.GetByText($"1 wait for '{recall}' in instance #{orders[1]}.")).ToBeVisibleAsync();
            await page.Locator("#signal-payload").FillAsync("{ \"reason\": \"Supplier recall\" }");
            await page.Locator("#signal-payload").PressAsync("Tab");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(review).ToContainTextAsync($"Instance #{orders[1]}");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
            await Assertions.Expect(current.Locator("tbody tr"))
                .ToContainTextAsync("Boundary interrupted its host", new LocatorAssertionsToContainTextOptions { Timeout = 60_000f });
            await Assertions.Expect(current).ToContainTextAsync($"Instance #{orders[1]} · {workflow.WorkflowKey}");
            await Assertions.Expect(current.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = $"Instance #{orders[1]}", Exact = true }).First)
                .ToHaveAttributeAsync("href", $"instances/{orders[1]}");
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s6-instance-broadcast");

            var interrupted = await actor.Runtime.GetInstanceAsync(orders[1]);
            Assert.DoesNotContain(interrupted.ExecutionPositions, position => position.NodeId == 2 && position.TokenStatus == "active");
            Assert.Contains(interrupted.ExecutionPositions, position => position.NodeId == 6 && position.TokenStatus == "active");
            Assert.Empty(interrupted.SignalSubscriptions);
            first = await actor.Runtime.GetInstanceAsync(orders[0]);
            Assert.Contains(first.ExecutionPositions, position => position.NodeId == 2 && position.TokenStatus == "active");
            Assert.Contains(first.SignalSubscriptions, subscription => subscription.Name == recall);
        });
    }

    /// <summary>
    /// S7 (EVENT-04B): two orders of the throw example. Cancelling the first from its review task throws
    /// to that instance only: the Worker interrupts its shipment task, and the second order keeps its own.
    /// The history row links the broadcast, and the Signals page shows it under Workflows with its thrower.
    /// </summary>
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task S7_ThrowEventInterruptsTheShipmentOfItsOwnOrderOnly(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"signal-s7-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("signal throw", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var path = Path.Combine(Stack.RepositoryRoot, "examples", "signals", "03-signal-throw-order-cancellation.json");
            var model = JsonSerializer.Deserialize<WorkflowModel>(
                await File.ReadAllTextAsync(path, ScenarioCancellation.Token), JsonOptions)!;
            model.Id = $"signal-s7-{suffix}";
            var signal = $"accept.s7-cancel-{suffix}";
            model.FlowNodes.Single(node => node.Id == 4).Signal!.Name = signal;
            model.FlowNodes.Single(node => node.Id == 9).Signal!.Name = signal;
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var orders = await StartManyAsync(actor, workflow.Id, 2, "orderNumber");

            var review = (await actor.Runtime.GetInboxAsync(orders[0])).Single(item => item.CurrentNodeId == 8);
            await actor.Runtime.TakeUserTaskFlowAsync(review.UserTaskId, 801, new Dictionary<string, JsonElement>
            {
                ["cancelReason"] = JsonSerializer.SerializeToElement("Customer request"),
            });
            var cancelled = await UntilAsync(() => actor.Runtime.GetInstanceAsync(orders[0]),
                detail => detail.ExecutionPositions.Any(position => position.NodeId == 6 && position.TokenStatus == "active"),
                "the Worker to interrupt the first order's shipment", TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250));
            Assert.DoesNotContain(cancelled.ExecutionPositions, position => position.NodeId == 3 && position.TokenStatus == "active");
            Assert.Contains(cancelled.ExecutionPositions, position => position.NodeId == 10 && position.TokenStatus == "active");
            var kept = await actor.Runtime.GetInstanceAsync(orders[1]);
            Assert.Contains(kept.ExecutionPositions, position => position.NodeId == 3 && position.TokenStatus == "active");

            // The history row names the throw and links the broadcast.
            var page = scenario.Page;
            await scenario.OpenUiAsync($"instances/{orders[0]}");
            var history = page.Locator("#history");
            await Assertions.Expect(history).ToContainTextAsync($"Threw signal '{signal}' to this instance: 1 recipient");
            var link = history.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { NameRegex = new Regex(@"^Broadcast #\d+$") }).First;
            var broadcastText = await link.InnerTextAsync();
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s7-history");

            // The Signals page lists it under Workflows, with its thrower linked to the order.
            await link.ClickAsync();
            var current = page.Locator("section[aria-labelledby='signal-current-heading']");
            await Assertions.Expect(current).ToContainTextAsync(broadcastText);
            await Assertions.Expect(current.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions
            {
                Name = $"Instance #{orders[0]} · Announce the cancellation", Exact = true
            })).ToHaveAttributeAsync("href", $"instances/{orders[0]}");
            var historyList = page.Locator("section[aria-labelledby='signal-history-heading']");
            await Assertions.Expect(historyList).Not.ToContainTextAsync(signal);
            await page.Locator(".signal-thrower-filter").SelectOptionAsync("workflows");
            await Assertions.Expect(historyList).ToContainTextAsync(signal);
            await Assertions.Expect(historyList).ToContainTextAsync($"Instance #{orders[0]} · Announce the cancellation");
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s7-signals");
        });
    }

    /// <summary>
    /// S8 (EVENT-04B): a recall decision of Example 04 ends in a signal end event that throws to the order of
    /// Example 02 with the same business key. The fixture rewrites Example 04's scope to Example 02's key for the run.
    /// </summary>
    [Fact]
    public async Task S8_SignalEndEventInterruptsTheOrderWithTheSameBusinessKey()
    {
        await using var scenario = await Stack.CreateScenario("signal-s8", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("signal end event", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var signal = $"accept.s8-recall-{suffix}";
            var ordersModel = JsonSerializer.Deserialize<WorkflowModel>(await File.ReadAllTextAsync(
                Path.Combine(Stack.RepositoryRoot, "examples", "signals", "02-signal-boundaries-recall.json"),
                ScenarioCancellation.Token), JsonOptions)!;
            ordersModel.Id = $"signal-s8-orders-{suffix}";
            ordersModel.FlowNodes.Single(node => node.Id == 3).Signal!.Name = signal;
            var orders = await Stack.SetupClient.CreateAndPublishAsync(ordersModel);
            var decisionModel = JsonSerializer.Deserialize<WorkflowModel>(await File.ReadAllTextAsync(
                Path.Combine(Stack.RepositoryRoot, "examples", "signals", "04-signal-end-recall-decision.json"),
                ScenarioCancellation.Token), JsonOptions)!;
            decisionModel.Id = $"signal-s8-decision-{suffix}";
            var end = decisionModel.FlowNodes.Single(node => node.Id == 3).Signal!;
            end.Name = signal;
            end.Scope!.WorkflowKey = orders.WorkflowKey;
            var decisions = await Stack.SetupClient.CreateAndPublishAsync(decisionModel);

            // Orders C-001 and C-002; the decision is for C-001.
            var order = await StartManyAsync(actor, orders.Id, 2, "orderNumber");
            var decision = (await StartManyAsync(actor, decisions.Id, 1, "orderNumber"))[0];
            var task = (await actor.Runtime.GetInboxAsync(decision)).Single(item => item.CurrentNodeId == 2);
            await actor.Runtime.TakeUserTaskFlowAsync(task.UserTaskId, 201, new Dictionary<string, JsonElement>
            {
                ["recallReason"] = JsonSerializer.SerializeToElement("Supplier recall"),
            });

            var finished = await actor.Runtime.GetInstanceAsync(decision);
            Assert.Equal("completed", finished.Status);
            Assert.Single(finished.History, item => item.Note == "signalThrown");
            var recalled = await UntilAsync(() => actor.Runtime.GetInstanceAsync(order[0]),
                detail => detail.ExecutionPositions.Any(position => position.NodeId == 6 && position.TokenStatus == "active"),
                "the Worker to interrupt the shipment of C-001", TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250));
            Assert.Equal("Supplier recall", recalled.Variables
                .Where(variable => variable.VariableName == "recallReason")
                .MaxBy(variable => variable.Id)!.Value.GetString());
            var other = await actor.Runtime.GetInstanceAsync(order[1]);
            Assert.Contains(other.ExecutionPositions, position => position.NodeId == 2 && position.TokenStatus == "active");

            await scenario.OpenUiAsync($"instances/{decision}");
            await Assertions.Expect(scenario.Page.Locator("#history"))
                .ToContainTextAsync($"Ended and threw signal '{signal}' to one business key in workflow {orders.WorkflowKey}: 1 recipient");
            await CaptureAsync(scenario, "s8-decision");
        });
    }

    /// <summary>
    /// S9 (EVENT-04B): with the Worker stopped, the workflow budget is set one above the broadcasts already
    /// delivering, so the first order's cancellation fills it. The second order's cancellation, taken in task
    /// detail, shows the refusal and changes nothing. Once the Worker delivers the first broadcast, the same
    /// action succeeds. The setting goes back to its earlier value, also when the scenario fails, because the
    /// suite's other scenarios share the stack.
    /// </summary>
    [Fact]
    public async Task S9_RefusedThrowShowsItsMessageThenSucceedsOnceTheWorkerDelivers()
    {
        await using var scenario = await Stack.CreateScenario("signal-s9", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("refused signal throw", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var path = Path.Combine(Stack.RepositoryRoot, "examples", "signals", "03-signal-throw-order-cancellation.json");
            var model = JsonSerializer.Deserialize<WorkflowModel>(
                await File.ReadAllTextAsync(path, ScenarioCancellation.Token), JsonOptions)!;
            model.Id = $"signal-s9-{suffix}";
            var signal = $"accept.s9-cancel-{suffix}";
            model.FlowNodes.Single(node => node.Id == 4).Signal!.Name = signal;
            model.FlowNodes.Single(node => node.Id == 9).Signal!.Name = signal;
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(model);
            var orders = await StartManyAsync(actor, workflow.Id, 2, "orderNumber");

            var setting = Assert.Single(await Stack.SetupClient.ListEngineSettingsAsync(),
                item => item.Namespace == "Signals" && item.Key == "MaxOpenWorkflowBroadcasts");
            var open = await OpenWorkflowBroadcastsAsync(actor.Http);
            var limit = open + 1;
            try
            {
                await Stack.SetupClient.UpdateEngineSettingAsync(setting.Id, new(
                    limit.ToString(System.Globalization.CultureInfo.InvariantCulture), setting.Description, setting.UpdatedAt));

                // The first cancellation fills the budget: its broadcast waits for the stopped Worker.
                var firstReview = (await actor.Runtime.GetInboxAsync(orders[0])).Single(item => item.CurrentNodeId == 8);
                await actor.Runtime.TakeUserTaskFlowAsync(firstReview.UserTaskId, 801, new Dictionary<string, JsonElement>
                {
                    ["cancelReason"] = JsonSerializer.SerializeToElement("Customer request"),
                });
                var thrown = Assert.Single((await actor.Runtime.GetInstanceAsync(orders[0])).History,
                    item => item.Note == "signalThrown");
                var firstBroadcast = thrown.Payload!["broadcastId"].GetInt64();
                Assert.Equal(limit, await OpenWorkflowBroadcastsAsync(actor.Http));

                // The second cancellation is refused in task detail, and nothing changes.
                var secondReview = (await actor.Runtime.GetInboxAsync(orders[1])).Single(item => item.CurrentNodeId == 8);
                var page = scenario.Page;
                await scenario.OpenUiAsync($"user-tasks/{secondReview.UserTaskId}");
                await WaitForInteractiveAsync(page);
                var card = page.Locator(".action-card").Filter(new LocatorFilterOptions { HasText = "Cancel the order" });
                var reason = card.Locator($"#task-{secondReview.UserTaskId}-flow-801-cancelReason");
                var takeAction = card.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Take action", Exact = true });
                await reason.FillAsync("Address unknown");
                await takeAction.ClickAsync();
                await Assertions.Expect(page.Locator(".alert-danger")).ToContainTextAsync(
                    $"Signal throw event #9 was not thrown: {limit} signal broadcasts thrown by workflows are still delivering; "
                    + "see Signals.MaxOpenWorkflowBroadcasts.");
                await AssertNoHorizontalOverflowAsync(page);
                await CaptureAsync(scenario, "s9-refused");
                var unchanged = await actor.Runtime.GetInstanceAsync(orders[1]);
                Assert.Contains(unchanged.ExecutionPositions, position => position.NodeId == 8 && position.TokenStatus == "active");
                Assert.Contains(unchanged.ExecutionPositions, position => position.NodeId == 3 && position.TokenStatus == "active");
                Assert.DoesNotContain(unchanged.History, item => item.Note == "signalThrown");
                Assert.DoesNotContain(unchanged.Variables, variable => variable.VariableName == "cancelReason");
                Assert.Equal(limit, await OpenWorkflowBroadcastsAsync(actor.Http));

                // The Worker delivers the first broadcast; then the same action succeeds.
                await Stack.StartWorkerAsync();
                await UntilAsync(() => GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{firstBroadcast}"),
                    item => item.Status == SignalBroadcastStatuses.Completed, "the Worker to deliver the first cancellation",
                    TimeSpan.FromSeconds(60));
                await takeAction.ClickAsync();
                await page.WaitForURLAsync(new Regex("/inbox$"));
                var cancelled = await UntilAsync(() => actor.Runtime.GetInstanceAsync(orders[1]),
                    detail => detail.ExecutionPositions.Any(position => position.NodeId == 6 && position.TokenStatus == "active"),
                    "the Worker to interrupt the second order's shipment", TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250));
                Assert.Single(cancelled.History, item => item.Note == "signalThrown");
                Assert.DoesNotContain(cancelled.ExecutionPositions, position => position.NodeId == 3 && position.TokenStatus == "active");

                await scenario.OpenUiAsync($"instances/{orders[1]}");
                await Assertions.Expect(page.Locator("#history"))
                    .ToContainTextAsync($"Threw signal '{signal}' to this instance: 1 recipient");
                await CaptureAsync(scenario, "s9-succeeded");
            }
            finally
            {
                // Read the row again, because a failure can come before or after the change.
                var current = Assert.Single(await Stack.SetupClient.ListEngineSettingsAsync(), item => item.Id == setting.Id);
                if (current.Value != setting.Value)
                {
                    await Stack.SetupClient.UpdateEngineSettingAsync(setting.Id,
                        new(setting.Value, setting.Description, current.UpdatedAt));
                }
            }
        });
    }

    /// <summary>
    /// S10 (EVENT-04C): Example 05 under its own family and signal name. The Signals page reviews one new
    /// instance, a throw to the family starts it, and the delivery links the instance, whose history names
    /// the broadcast. A throw to all workflows starts a second.
    /// </summary>
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task S10_ThrowToTheFamilyStartsAnInstanceAndAllWorkflowsStartsAnother(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"signal-s10-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("signal start", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var (notices, signal) = await PublishRecallNoticeAsync("s10");
            var page = scenario.Page;

            await scenario.OpenUiAsync("signals");
            await FillSignalUntilAsync(page, signal, "new instance in 1 family");
            await Assertions.Expect(page.GetByText($"1 new instance in 1 family: {notices.WorkflowKey} (1 start)")).ToBeVisibleAsync();
            await page.Locator("#signal-workflow-key").FillAsync(notices.WorkflowKey);
            await page.Locator("#signal-workflow-key").PressAsync("Tab");
            await page.Locator("#signal-payload").FillAsync("{ \"recallNumber\": \"R-1001\", \"product\": { \"name\": \"Kettle\" } }");
            await page.Locator("#signal-payload").PressAsync("Tab");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            await Assertions.Expect(page.Locator("section[aria-labelledby='signal-review-heading']"))
                .ToContainTextAsync("Reaches now1 new instance");
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s10-review");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-success"))
                .ToContainTextAsync(new Regex(@"Broadcast #\d+ froze 1 new instance; the Worker is delivering it\."));
            var broadcastId = long.Parse(Regex.Match(await page.Locator(".alert-success").InnerTextAsync(), @"#(\d+)").Groups[1].Value);

            // The page follows the broadcast until the Worker has started the instance.
            var current = page.Locator("section[aria-labelledby='signal-current-heading']");
            await Assertions.Expect(current.Locator(".status-badge, .badge").First)
                .ToHaveTextAsync("Completed", new LocatorAssertionsToHaveTextOptions { Timeout = 90_000f });
            var row = current.Locator("tbody tr").First;
            await Assertions.Expect(row).ToContainTextAsync("Started an instance");
            await Assertions.Expect(row).ToContainTextAsync("Start");
            await Assertions.Expect(current).ToContainTextAsync("1 start");
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s10-delivered");

            // The kind filter keeps the start under Starts and hides it under Waits.
            await page.Locator(".signal-kind-filter").SelectOptionAsync(SignalDeliveryKinds.Wait);
            await Assertions.Expect(current).ToContainTextAsync("0 matching deliveries");
            await page.Locator(".signal-kind-filter").SelectOptionAsync(SignalDeliveryKinds.Start);
            await Assertions.Expect(current).ToContainTextAsync("1 matching deliveries");

            // The started instance runs the notice task, and its history starts with the signal.
            var link = current.Locator("tbody tr a").First;
            var instanceId = long.Parse(Regex.Match(await link.InnerTextAsync(), @"#(\d+)").Groups[1].Value);
            var started = await actor.Runtime.GetInstanceAsync(instanceId);
            Assert.Equal(notices.Id, started.Workflow.Id);
            Assert.Equal("R-1001", LatestText(started, "recallNumber"));
            Assert.Equal("Kettle", LatestText(started, "product"));
            Assert.Equal("Not stated", LatestText(started, "reason"));
            Assert.Contains(await actor.Runtime.GetInboxAsync(instanceId), item => item.CurrentNodeId == 2);
            await link.ClickAsync();
            await Assertions.Expect(page.Locator("#history"))
                .ToContainTextAsync($"Started by signal '{signal}' from signal-operator (broadcast #{broadcastId})");
            await Assertions.Expect(page.Locator("section[aria-labelledby='instance-summary-heading']"))
                .ToContainTextAsync("Write the customer notice");
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s10-instance");
            await page.Locator("#history").GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = $"Broadcast #{broadcastId}" }).ClickAsync();
            await Assertions.Expect(page.Locator("section[aria-labelledby='signal-current-heading']"))
                .ToContainTextAsync($"Broadcast #{broadcastId}");

            // A throw to all workflows starts a second notice.
            var all = await ThrowAsync(actor.Http, new ThrowSignalRequest(signal, AllWorkflows: true,
                Payload: JsonSerializer.SerializeToElement(new { recallNumber = "R-1002", product = new { name = "Toaster" }, reason = "Overheating" })));
            Assert.Equal((1, 1), (all.TotalDeliveryCount, all.StartCount));
            var second = await StartedInstanceAsync(actor.Http, all.Id);
            Assert.NotEqual(instanceId, second);
            Assert.Equal("Overheating", LatestText(await actor.Runtime.GetInstanceAsync(second), "reason"));
        });
    }

    /// <summary>
    /// S11 (EVENT-04C): the first recall number again is skipped because an instance has the business key,
    /// and a payload without the product fails its mapping. Neither starts an instance.
    /// </summary>
    [Fact]
    public async Task S11_DuplicateBusinessKeyIsSkippedAndAMissingProductFails()
    {
        await using var scenario = await Stack.CreateScenario("signal-s11", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("signal start outcomes", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var (notices, signal) = await PublishRecallNoticeAsync("s11");
            var recall = JsonSerializer.SerializeToElement(new { recallNumber = "R-1001", product = new { name = "Kettle" } });
            var first = await ThrowAsync(actor.Http, new ThrowSignalRequest(signal, notices.WorkflowKey, Payload: recall));
            var owner = await StartedInstanceAsync(actor.Http, first.Id);

            // The same recall number again: skipped, naming the instance that has the key.
            var again = await ThrowAsync(actor.Http, new ThrowSignalRequest(signal, notices.WorkflowKey, Payload: recall));
            var skipped = await UntilAsync(() => GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{again.Id}"),
                item => item.Status != SignalBroadcastStatuses.Delivering, "the duplicate start to finish");
            Assert.Equal((1, 0), (skipped.SkippedCount, skipped.DeliveredCount));
            var page = scenario.Page;
            await scenario.OpenUiAsync($"signals?broadcastId={again.Id}");
            var current = page.Locator("section[aria-labelledby='signal-current-heading']");
            var row = current.Locator("tbody tr").First;
            await Assertions.Expect(row).ToContainTextAsync("An instance already has this business key");
            await Assertions.Expect(row).ToContainTextAsync("No instance");
            await Assertions.Expect(row.Locator(".signal-result-description")).ToContainTextAsync($"Instance #{owner} already has this start's business key.");
            await CaptureAsync(scenario, "s11-skipped");

            // A payload without the product fails its mapping; the broadcast completes with issues.
            var missing = await ThrowAsync(actor.Http, new ThrowSignalRequest(signal, notices.WorkflowKey,
                Payload: JsonSerializer.SerializeToElement(new { recallNumber = "R-1003" })));
            var failed = await UntilAsync(() => GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{missing.Id}"),
                item => item.Status != SignalBroadcastStatuses.Delivering, "the failing start to finish");
            Assert.Equal((SignalBroadcastStatuses.CompletedWithIssues, 1), (failed.Status, failed.FailedCount));
            await scenario.OpenUiAsync($"signals?broadcastId={missing.Id}");
            await Assertions.Expect(current.Locator(".status-badge, .badge").First).ToHaveTextAsync("Completed with issues");
            await Assertions.Expect(current.Locator("tbody tr").First).ToContainTextAsync("The payload failed a mapping");
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s11-failed");

            var family = await actor.Runtime.ListInstancesAsync(workflowId: notices.Id);
            Assert.Equal(1, family.TotalCount);
        });
    }

    /// <summary>
    /// S12 (EVENT-04C): with the Worker stopped, a throw freezes a start of the family; a new default whose task
    /// has another name is set before the Worker starts, and the start runs it. A default without the signal
    /// start set the same way leaves the next frozen start skipped.
    /// </summary>
    [Fact]
    public async Task S12_AStartRunsTheDefaultOfItsDeliveryOrIsSkippedWhenTheFamilyStoppedListening()
    {
        await using var scenario = await Stack.CreateScenario("signal-s12", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("signal start default change", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var (v1, signal) = await PublishRecallNoticeAsync("s12");
            var first = await ThrowAsync(actor.Http, new ThrowSignalRequest(signal, v1.WorkflowKey,
                Payload: JsonSerializer.SerializeToElement(new { recallNumber = "R-1201", product = new { name = "Kettle" } })));
            Assert.Equal(1, first.StartCount);

            // Version 2 renames the task and becomes the default before any delivery.
            var renamed = await LoadRecallNoticeAsync(v1.WorkflowKey, signal);
            renamed.FlowNodes.Single(node => node.Id == 2).Name = "Write the updated notice";
            var v2 = await actor.Runtime.CreateAndPublishAsync(renamed);
            await SetDefaultAsync(actor.Http, v2.Id);
            await Stack.StartWorkerAsync();
            var started = await actor.Runtime.GetInstanceAsync(await StartedInstanceAsync(actor.Http, first.Id));
            Assert.Equal(v2.Id, started.Workflow.Id);
            Assert.Contains(started.ExecutionPositions, position => position.NodeId == 2 && position.TokenStatus == "active");
            var delivery = Assert.Single((await GetAsync<PagedResult<SignalDeliveryDto>>(actor.Http,
                $"/api/signal-broadcasts/{first.Id}/deliveries?kind=start")).Items);
            Assert.Equal(v2.Id, delivery.WorkflowDefinitionId);
            await scenario.OpenUiAsync($"instances/{started.Id}");
            await Assertions.Expect(scenario.Page.Locator("section[aria-labelledby='instance-summary-heading']"))
                .ToContainTextAsync("Write the updated notice");
            await CaptureAsync(scenario, "s12-new-default");

            // Version 3 starts by hand; the family stops listening when it becomes the default.
            await Stack.StopWorkerAsync();
            var second = await ThrowAsync(actor.Http, new ThrowSignalRequest(signal, v1.WorkflowKey,
                Payload: JsonSerializer.SerializeToElement(new { recallNumber = "R-1202", product = new { name = "Toaster" } })));
            Assert.Equal(1, second.StartCount);
            var manual = await LoadRecallNoticeAsync(v1.WorkflowKey, signal);
            var entry = manual.FlowNodes.Single(node => node.Id == 1);
            entry.Type = BpmnFlowNodeTypes.StartEvent;
            entry.Signal = null;
            entry.Variables =
            [
                new VariableModel
                {
                    Id = 1, Name = "recallNumber", DataType = WorkflowVariableTypes.String, IsArray = false, Required = true,
                },
            ];
            manual.InitialEventId = 1;
            var v3 = await actor.Runtime.CreateAndPublishAsync(manual);
            await SetDefaultAsync(actor.Http, v3.Id);
            await Stack.StartWorkerAsync();
            var ended = await UntilAsync(() => GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{second.Id}"),
                item => item.Status != SignalBroadcastStatuses.Delivering, "the frozen start to be skipped");
            Assert.Equal((1, 0), (ended.SkippedCount, ended.DeliveredCount));
            await scenario.OpenUiAsync($"signals?broadcastId={second.Id}");
            var row = scenario.Page.Locator("section[aria-labelledby='signal-current-heading'] tbody tr").First;
            await Assertions.Expect(row).ToContainTextAsync("The workflow no longer starts on this signal");
            await CaptureAsync(scenario, "s12-stopped-listening");
            var summary = await GetAsync<SignalSubscriptionSummaryDto>(actor.Http,
                $"/api/signal-subscriptions/summary?name={Uri.EscapeDataString(signal)}");
            Assert.Empty(summary.Items);
        });
    }

    /// <summary>
    /// S13 (EVENT-04C): a recall decision of Example 04 ends in a signal end event that throws to this run's
    /// copy of Example 05. Completing the decision starts a notice; the broadcast and the notice's history name
    /// the deciding instance. The run's copy of Example 05 reads the product from a plain payload member,
    /// because a workflow's payload copies whole variables.
    /// </summary>
    [Fact]
    public async Task S13_SignalEndEventOfAnotherWorkflowStartsANotice()
    {
        await using var scenario = await Stack.CreateScenario("signal-s13", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("workflow signal start", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var signal = $"accept.s13-recall-{suffix}";
            var noticeModel = await LoadRecallNoticeAsync($"signal-s13-notices-{suffix}", signal);
            noticeModel.FlowNodes.Single(node => node.Id == 1).Signal!.OutputMappings
                .Single(mapping => mapping.Variable == "product").Path = "product";
            var notices = await Stack.SetupClient.CreateAndPublishAsync(noticeModel);
            var decisionModel = JsonSerializer.Deserialize<WorkflowModel>(await File.ReadAllTextAsync(
                Path.Combine(Stack.RepositoryRoot, "examples", "signals", "04-signal-end-recall-decision.json"),
                ScenarioCancellation.Token), JsonOptions)!;
            decisionModel.Id = $"signal-s13-decision-{suffix}";
            var end = decisionModel.FlowNodes.Single(node => node.Id == 3).Signal!;
            end.Name = signal;
            end.Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = notices.WorkflowKey };
            end.Payload =
            [
                new SignalPayloadEntryModel { Name = "recallNumber", Variable = "orderNumber" },
                new SignalPayloadEntryModel { Name = "product", Variable = "recallReason" },
                new SignalPayloadEntryModel { Name = "reason", Variable = "recallReason" },
            ];
            var decisions = await Stack.SetupClient.CreateAndPublishAsync(decisionModel);

            var decision = (await StartManyAsync(actor, decisions.Id, 1, "orderNumber"))[0];
            var task = (await actor.Runtime.GetInboxAsync(decision)).Single(item => item.CurrentNodeId == 2);
            await actor.Runtime.TakeUserTaskFlowAsync(task.UserTaskId, 201, new Dictionary<string, JsonElement>
            {
                ["recallReason"] = JsonSerializer.SerializeToElement("Faulty thermostat"),
            });
            var finished = await actor.Runtime.GetInstanceAsync(decision);
            Assert.Equal("completed", finished.Status);
            var thrown = Assert.Single(finished.History, item => item.Note == "signalThrown");
            var broadcastId = thrown.Payload!["broadcastId"].GetInt64();
            Assert.Equal(1, thrown.Payload["startCount"].GetInt32());

            var noticeId = await StartedInstanceAsync(actor.Http, broadcastId);
            var broadcast = await GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{broadcastId}");
            Assert.Equal((SignalThrowerKinds.Instance, decision), (broadcast.ThrownByKind, broadcast.ThrownByInstanceId));
            var notice = await actor.Runtime.GetInstanceAsync(noticeId);
            Assert.Equal(("C-001", "Faulty thermostat"), (LatestText(notice, "recallNumber"), LatestText(notice, "product")));
            var received = Assert.Single(notice.History, item => item.Note == "signalReceived");
            Assert.Equal(decision, received.Payload!["thrownByInstanceId"].GetInt64());

            await scenario.OpenUiAsync($"instances/{noticeId}");
            await Assertions.Expect(scenario.Page.Locator("#history"))
                .ToContainTextAsync($"Started by signal '{signal}' from instance #{decision} (broadcast #{broadcastId})");
            await CaptureAsync(scenario, "s13-notice");
            await scenario.OpenUiAsync($"instances/{decision}");
            await Assertions.Expect(scenario.Page.Locator("#history"))
                .ToContainTextAsync($"Ended and threw signal '{signal}' to workflow {notices.WorkflowKey}");
            await CaptureAsync(scenario, "s13-decision");
        });
    }

    private async Task<(WorkflowSummaryDto Workflow, string Signal)> PublishRecallNoticeAsync(string scenarioName)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var signal = $"accept.{scenarioName}-recall-{suffix}";
        var model = await LoadRecallNoticeAsync($"signal-{scenarioName}-notices-{suffix}", signal);
        return (await Stack.SetupClient.CreateAndPublishAsync(model), signal);
    }

    private async Task<WorkflowModel> LoadRecallNoticeAsync(string workflowKey, string signal)
    {
        var path = Path.Combine(Stack.RepositoryRoot, "examples", "signals", "05-signal-start-recall-notice.json");
        var model = JsonSerializer.Deserialize<WorkflowModel>(
            await File.ReadAllTextAsync(path, ScenarioCancellation.Token), JsonOptions)!;
        model.Id = workflowKey;
        model.FlowNodes.Single(node => node.Id == 1).Signal!.Name = signal;
        return model;
    }

    // Waits for the broadcast's start delivery and returns the instance it created.
    private static async Task<long> StartedInstanceAsync(HttpClient http, long broadcastId)
    {
        var delivery = await UntilAsync(async () => (await GetAsync<PagedResult<SignalDeliveryDto>>(http,
                $"/api/signal-broadcasts/{broadcastId}/deliveries?kind=start")).Items.SingleOrDefault(),
            item => item is not null && item.Status != SignalDeliveryStatuses.Pending,
            $"the start of broadcast #{broadcastId} to be delivered", TimeSpan.FromSeconds(60));
        Assert.True(delivery!.Status == SignalDeliveryStatuses.Delivered,
            $"The start ended {delivery.Status} with {delivery.ResultCode}: {delivery.ResultDescription}");
        Assert.Equal(SignalDeliveryResultCodes.Started, delivery.ResultCode);
        return delivery.InstanceId!.Value;
    }

    private static async Task SetDefaultAsync(HttpClient http, long workflowId)
    {
        using var response = await http.PostAsync($"/api/workflows/{workflowId}/set-default", null, ScenarioCancellation.Token);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent,
            $"POST set-default: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static string? LatestText(InstanceDetailDto detail, string name) => detail.Variables
        .Where(variable => variable.VariableName == name)
        .MaxBy(variable => variable.Id)?.Value.GetString();

    private static async Task FillSignalUntilAsync(IPage page, string signal, string expected)
    {
        // The circuit may not handle the first change yet; type again until the count appears.
        var name = page.Locator("#signal-name");
        var matched = page.GetByText(expected);
        for (var attempt = 0; attempt < 20 && await matched.CountAsync() == 0; attempt++)
        {
            await name.FillAsync(signal);
            await name.PressAsync("Tab");
            await Task.Delay(1_000, ScenarioCancellation.Token);
        }
        await Assertions.Expect(matched).ToHaveCountAsync(1);
    }

    private static async Task<long> OpenWorkflowBroadcastsAsync(HttpClient http) =>
        (await GetAsync<PagedResult<SignalBroadcastSummaryDto>>(http,
            $"/api/signal-broadcasts?status={SignalBroadcastStatuses.Delivering}&thrownByKind={SignalThrowerKinds.Instance}&page=1&pageSize=1")).TotalCount;

    private static async Task ThrowFromPageAsync(IPage page, BrowserScenario scenario, string signal, string workflowKey, string payload)
    {
        await scenario.OpenUiAsync("signals");
        await FillSignalAsync(page, signal);
        await page.Locator("#signal-workflow-key").FillAsync(workflowKey);
        await page.Locator("#signal-workflow-key").PressAsync("Tab");
        await page.Locator("#signal-payload").FillAsync(payload);
        await page.Locator("#signal-payload").PressAsync("Tab");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
        await Assertions.Expect(page.Locator("section[aria-labelledby='signal-review-heading']")).ToContainTextAsync("Reaches now1 wait");
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
