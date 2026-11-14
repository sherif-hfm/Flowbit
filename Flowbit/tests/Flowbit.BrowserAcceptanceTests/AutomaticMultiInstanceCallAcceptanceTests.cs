using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;
using static Flowbit.BrowserAcceptanceTests.AutomaticMi;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>
/// TASK-05D call bodies through real API, UI and Worker processes. These belong with the
/// call-activity scenarios, but they need the stack whose Development release switch is
/// open, and an xUnit class binds to exactly one collection fixture, so they live in their
/// own class in that collection. Children are real instances of real published workflows,
/// and every child action is a real task action.
/// </summary>
[Collection(AutomaticMultiInstanceAcceptanceCollection.Name)]
public sealed class AutomaticMultiInstanceCallAcceptanceTests(AutomaticMultiInstanceAcceptanceFixture fixture)
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(6);
    private const int ChildTask = 2;
    private const int ApproveFlow = 201;
    private const int RejectFlow = 202;
    private BrowserStackFixture Stack => fixture.Stack;

    [Fact]
    public async Task ACallBodyQueuesThenLinksItemsChildrenAndParentWithItsPinnedVersion()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-call-navigation", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("call body", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var childKey = Unique("auto-mi-child");
            var child = await Stack.SetupClient.CreateAndPublishAsync(Child(childKey));
            var parent = await Stack.SetupClient.CreateAndPublishAsync(Parent(Unique("auto-mi-parent"), childKey, "parallel", "a", "b", "c"));
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, new());

            // Queued: the body's entry job waits; no body, item or child exists.
            var queued = await InstanceAsync(actor, instance.Id);
            Assert.Empty(queued.AutomaticMultiInstances);
            Assert.Equal("multiInstanceStart", Assert.Single(queued.ExecutionPositions, position => position.NodeId == 2).WaitState);
            Assert.Empty(await CallsAsync(actor, instance.Id));
            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(AutomaticSection(page)).ToHaveCountAsync(0);
            await CaptureAtViewportsAsync(scenario, "call-queued");

            await Stack.StartWorkerAsync();
            var calls = await UntilAsync(() => CallsAsync(actor, instance.Id),
                rows => rows.Count == 3 && rows.All(row => row.ChildInstanceId is not null), "three children");
            Assert.All(calls, call => Assert.Equal(child.Id, call.ChildDefinitionId));
            Assert.Equal(3, calls.Select(call => call.ChildInstanceId).Distinct().Count());
            var running = await InstanceAsync(actor, instance.Id);
            var body = Body(running, 2)!;
            Assert.Equal(child.Version, body.PinnedChildDefinitionVersion);
            // The parent has no human work of its own.
            Assert.Empty(await actor.Runtime.GetInboxAsync(instance.Id));

            await ReloadInstanceAsync(page);
            var section = AutomaticSection(page);
            await Assertions.Expect(section).ToContainTextAsync($"Calls version {child.Version} for every item");
            await Assertions.Expect(page.Locator("section, .panel").Filter(new LocatorFilterOptions
            {
                Has = page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Administrative actions" })
            }).First).ToContainTextAsync("No active human-task positions");
            await ShowItemsAsync(page, body.ExecutionId);
            await CaptureAtViewportsAsync(scenario, "call-running");

            // Item 1 → its child → back to the parent.
            var items = (await ItemsAsync(actor, body.ExecutionId)).Items;
            var second = items.Single(item => item.ItemIndex == 1);
            var childId = second.ChildInstanceId!.Value;
            await ItemRow(page, second.Id).GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = $"Child instance #{childId}" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"/instances/{childId}$"));
            await Assertions.Expect(page.Locator("h1")).ToContainTextAsync($"Instance #{childId}");
            var childCalls = page.Locator("#call-activities");
            await Assertions.Expect(childCalls).ToContainTextAsync($"#{second.Id} · Index 1 · Body #{body.ExecutionId}");
            await CaptureAtViewportsAsync(scenario, "call-child-of-item");

            // A person completes this child through its own page; the others over HTTP, out of order.
            var approve = page.Locator(".card-body").Filter(new LocatorFilterOptions
            {
                Has = page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Approve", Exact = true })
            });
            await approve.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Take action" }).ClickAsync();
            await UntilInstanceAsync(actor, childId, detail => detail.Status == "completed", "the child to complete");
            await ActAsync(actor, items.Single(item => item.ItemIndex == 2).ChildInstanceId!.Value, ApproveFlow);
            await ActAsync(actor, items.Single(item => item.ItemIndex == 0).ChildInstanceId!.Value, ApproveFlow);

            await childCalls.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = $"Instance #{instance.Id}" }).First.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new Regex($"/instances/{instance.Id}$"));
            var done = await UntilInstanceAsync(actor, instance.Id, AtNode(4), "the parent to collect every result");
            var results = Latest(done, "orderResults").EnumerateArray().ToArray();
            Assert.Equal(["a", "b", "c"], results.Select(row => row.GetProperty("item").GetProperty("id").GetString()));
            Assert.Equal([0, 1, 2], results.Select(row => row.GetProperty("variables").GetProperty("itemIndex").GetInt32()));
            await ReloadInstanceAsync(page);
            await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync("3 / 3 completed");
            await Assertions.Expect(Summary(page)).ToContainTextAsync("Review results");
            await CaptureAtViewportsAsync(scenario, "call-completed");
            // No child was started twice.
            Assert.Equal(3, (await CallsAsync(actor, instance.Id)).Select(call => call.ChildInstanceId).Distinct().Count());
        });
    }

    [Fact]
    public async Task ASequentialCallBodyKeepsItsPinAcrossADefaultChangeAndSeesANewSharedInput()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-call-pinned", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("pinned version", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var labelKey = $"acceptance.mi.label.{Guid.NewGuid():N}";
            await CreateSharedAsync(actor, labelKey, "string", "first label");
            var childKey = Unique("auto-mi-child");
            var childModel = Child(childKey, withLabel: true);
            var v1 = await Stack.SetupClient.CreateAndPublishAsync(childModel);
            var parentModel = Parent(Unique("auto-mi-parent"), childKey, "sequential", "a", "b");
            parentModel.Variables.Add(SharedBinding(5, "label", labelKey, "string", access: "read"));
            parentModel.FlowNodes.Single(node => node.Id == 2).CallActivity!.InputMappings.Add(
                new() { SourceVariable = "label", Variable = "label", DataType = "string", Required = true });
            var parent = await Stack.SetupClient.CreateAndPublishAsync(parentModel);
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, new());

            var first = Assert.Single(await UntilAsync(() => CallsAsync(actor, instance.Id),
                rows => rows.Count == 1 && rows[0].ChildInstanceId is not null, "the first child"));
            Assert.Equal(v1.Id, first.ChildDefinitionId);
            Assert.Equal("first label", Latest(await InstanceAsync(actor, first.ChildInstanceId!.Value), "label").GetString());

            // A new default version, and a new shared value, before the second admission.
            var v2 = await Stack.SetupClient.CreateAndPublishAsync(Child(childKey, withLabel: true));
            Assert.True((await actor.Runtime.GetWorkflowAsync(v2.Id)).IsDefault || await SetDefaultAsync(actor, v2.Id));
            await SetSharedAsync(actor, labelKey, "second label");
            // The pinned version cannot be deleted while the body holds it.
            var (deleted, deleteBody) = await SendAsync(actor.Http, HttpMethod.Delete, $"/api/workflows/{v1.Id}");
            Assert.True(deleted == 409, $"Deleting the pinned version returned {deleted}: {deleteBody}");

            await ActAsync(actor, first.ChildInstanceId!.Value, ApproveFlow);
            var calls = await UntilAsync(() => CallsAsync(actor, instance.Id),
                rows => rows.Count == 2 && rows.All(row => row.ChildInstanceId is not null), "the second child");
            var second = calls.Single(call => call.ItemIndex == 1);
            Assert.Equal(v1.Id, second.ChildDefinitionId);
            Assert.Equal(v1.Version, second.PinnedChildDefinitionVersion);
            Assert.Equal("second label", Latest(await InstanceAsync(actor, second.ChildInstanceId!.Value), "label").GetString());

            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync($"Calls version {v1.Version} for every item");
            await CaptureAtViewportsAsync(scenario, "call-pinned-sequential");
            await ActAsync(actor, second.ChildInstanceId!.Value, ApproveFlow);
            await UntilInstanceAsync(actor, instance.Id, AtNode(4), "the sequential body to finish");
        });
    }

    [Fact]
    public async Task CancellingTheParentFencesChildrenAtOnceAndKeepsACompletedChild()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-call-cancel", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("cancellation", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var childKey = Unique("auto-mi-child");
            await Stack.SetupClient.CreateAndPublishAsync(Child(childKey));
            var parentModel = Parent(Unique("auto-mi-parent"), childKey, "parallel", "a", "b", "c");
            parentModel.CancelRoles = ["admin"];
            var parent = await Stack.SetupClient.CreateAndPublishAsync(parentModel);
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, new());
            var calls = await UntilAsync(() => CallsAsync(actor, instance.Id),
                rows => rows.Count == 3 && rows.All(row => row.ChildInstanceId is not null), "three children");
            var finished = calls.Single(call => call.ItemIndex == 0).ChildInstanceId!.Value;
            await ActAsync(actor, finished, ApproveFlow);
            await UntilAsync(() => CallsAsync(actor, instance.Id), rows => rows.Single(row => row.ItemIndex == 0).State == "completed",
                "the first child's result");
            await Stack.StopWorkerAsync();

            var page = await OpenInstanceAsync(scenario, instance.Id);
            var (cancelled, cancelBody) = await SendAsync(actor.Http, HttpMethod.Post, $"/api/instances/{instance.Id}/cancel");
            Assert.True(cancelled is >= 200 and < 300, cancelBody);
            Assert.Equal("cancelled", (await InstanceAsync(actor, instance.Id)).Status);
            // Waiting children refuse actions at once, before any Worker runs.
            var waiting = calls.Where(call => call.ItemIndex != 0).Select(call => call.ChildInstanceId!.Value).ToArray();
            foreach (var childId in waiting)
            {
                var (refused, _) = await SendAsync(actor.Http, HttpMethod.Post, $"/api/instances/{childId}/flows/{ApproveFlow}", new { });
                Assert.Equal(409, refused);
            }
            await ReloadInstanceAsync(page);
            await CaptureAtViewportsAsync(scenario, "call-cancelled-parent");
            var childPage = await OpenInstanceAsync(scenario, waiting[0]);
            await Assertions.Expect(childPage.Locator("#call-activities")).ToContainTextAsync("is being cancelled");
            await CaptureAtViewportsAsync(scenario, "call-child-cancelling");

            await Stack.StartWorkerAsync();
            foreach (var childId in waiting)
                await UntilInstanceAsync(actor, childId, detail => detail.Status == "cancelled", $"child {childId} to stop");
            Assert.Equal("completed", (await InstanceAsync(actor, finished)).Status);
            await ReloadInstanceAsync(childPage);
            await Assertions.Expect(childPage.Locator("#call-activities")).Not.ToContainTextAsync("is being cancelled");
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATimerWhileChildrenWaitInterruptsOrReminds(bool interrupting)
    {
        var name = interrupting ? "interrupting" : "reminder";
        await using var scenario = await Stack.CreateScenario($"auto-mi-call-timer-{name}", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync($"{name} timer", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var childKey = Unique("auto-mi-child");
            await Stack.SetupClient.CreateAndPublishAsync(Child(childKey));
            var model = Parent(Unique("auto-mi-parent"), childKey, "parallel", "a", "b");
            AddBoundary(model, TimerBoundary(5, "PT15S", interrupting), interrupting ? "Too slow" : "Chase approvers");
            var parent = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, new());
            var calls = await UntilAsync(() => CallsAsync(actor, instance.Id),
                rows => rows.Count == 2 && rows.All(row => row.ChildInstanceId is not null), "both children");
            var children = calls.Select(call => call.ChildInstanceId!.Value).ToArray();

            var fired = await UntilInstanceAsync(actor, instance.Id, AtNode(6), "the timer", TimeSpan.FromSeconds(60));
            var page = await OpenInstanceAsync(scenario, instance.Id);
            if (interrupting)
            {
                Assert.NotEqual("active", Body(fired, 2)!.Status);
                Assert.Equal(0, Latest(fired, "orderResults").GetArrayLength());
                // Children are fenced at once and stopped by the Worker.
                foreach (var childId in children)
                {
                    var (refused, _) = await SendAsync(actor.Http, HttpMethod.Post, $"/api/instances/{childId}/flows/{ApproveFlow}", new { });
                    Assert.NotEqual(200, refused);
                    await UntilInstanceAsync(actor, childId, detail => detail.Status == "cancelled", $"child {childId} to stop");
                }
                await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync("Cancelled");
                await CaptureAtViewportsAsync(scenario, "call-timer-cancelled");
                Assert.False(Visited(await InstanceAsync(actor, instance.Id), 4));
            }
            else
            {
                Assert.Equal("active", Body(fired, 2)!.Status);
                await Assertions.Expect(Summary(page)).ToContainTextAsync("Chase approvers");
                await CaptureAtViewportsAsync(scenario, "call-timer-reminder");
                foreach (var childId in children) await ActAsync(actor, childId, ApproveFlow);
                var done = await UntilInstanceAsync(actor, instance.Id, detail => AtNode(4)(detail) && AtNode(6)(detail), "the body beside its reminder");
                Assert.Equal(2, Latest(done, "orderResults").GetArrayLength());
            }
        });
    }

    [Fact]
    public async Task AConditionInterruptsAQueuedCallEntryBeforeAnyChildStarts()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-call-condition", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("queued condition", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var childKey = Unique("auto-mi-child");
            await Stack.SetupClient.CreateAndPublishAsync(Child(childKey));
            var model = Parent(Unique("auto-mi-parent"), childKey, "parallel", "a", "b");
            model.Variables.Add(Process(6, "stop", "boolean", false));
            AddBoundary(model, ConditionalBoundary(5, "stop == true", interrupting: true), "Stopped before calling");
            var parent = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, new());
            await SetVariableAsync(actor, instance.Id, "stop", true);
            await UntilInstanceAsync(actor, instance.Id, AtNode(6), "the condition to interrupt the entry");

            await Stack.StartWorkerAsync();
            await Task.Delay(TimeSpan.FromSeconds(4), ScenarioCancellation.Token);
            var after = await InstanceAsync(actor, instance.Id);
            Assert.Empty(after.AutomaticMultiInstances);
            Assert.Empty(await CallsAsync(actor, instance.Id));
            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(Summary(page)).ToContainTextAsync("Stopped before calling");
            await CaptureAtViewportsAsync(scenario, "call-condition-queued-entry");
        });
    }

    [Fact]
    public async Task AMissingTargetOpensAnEntryIncidentWhoseRetryStartsEachChildOnce()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-call-missing-target", 1440, 900, scenarioTimeout: TimeSpan.FromMinutes(12));
        await scenario.RunAsync("missing target", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var childKey = Unique("auto-mi-child");
            var placeholder = await Stack.SetupClient.CreateAndPublishAsync(Child(childKey));
            var parentModel = Parent(Unique("auto-mi-parent"), childKey, "parallel", "a", "b");
            var parent = await Stack.SetupClient.CreateAndPublishAsync(parentModel);
            // The target family loses its only version before the body starts. A call body has no
            // job policy of its own, so its entry retries on the control schedule (10 s, 1 min, 5 min)
            // before the incident opens.
            await Stack.SetupClient.DeleteWorkflowAsync(placeholder.Id);
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, new());

            var incident = Assert.Single(await UntilAsync(() => IncidentsAsync(actor, instance.Id),
                rows => rows.Any(row => row.Status == "open"), "the entry incident after its retries", TimeSpan.FromMinutes(8)));
            Assert.Empty((await InstanceAsync(actor, instance.Id)).AutomaticMultiInstances);
            Assert.Empty(await CallsAsync(actor, instance.Id));
            var page = await OpenInstanceAsync(scenario, instance.Id);
            await CaptureAtViewportsAsync(scenario, "call-entry-blocked");

            var child = await Stack.SetupClient.CreateAndPublishAsync(Child(childKey));
            await RetryFromOperationsAsync(scenario, incident, "call-entry-incident-operations");
            var calls = await UntilAsync(() => CallsAsync(actor, instance.Id),
                rows => rows.Count == 2 && rows.All(row => row.ChildInstanceId is not null), "both children after the retry");
            Assert.All(calls, call => Assert.Equal(child.Id, call.ChildDefinitionId));
            Assert.Equal(2, calls.Select(call => call.ChildInstanceId).Distinct().Count());
            // Operations navigated this tab away; open the instance again.
            page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(AutomaticSection(page)).ToContainTextAsync($"Calls version {child.Version} for every item");
        });
    }

    [Fact]
    public async Task ASharedOutputConflictRetriesDeliveryWithoutStartingAnotherChild()
    {
        await using var scenario = await Stack.CreateScenario("auto-mi-call-shared-conflict", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync("shared output conflict", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var key = $"acceptance.mi.lastindex.{Guid.NewGuid():N}";
            await CreateSharedAsync(actor, key, "number", -1);
            var childKey = Unique("auto-mi-child");
            await Stack.SetupClient.CreateAndPublishAsync(Child(childKey));
            var model = Parent(Unique("auto-mi-parent"), childKey, "parallel", "a");
            model.Variables.Add(SharedBinding(6, "lastIndex", key, "number"));
            model.FlowNodes.Single(node => node.Id == 2).CallActivity!.OutputMappings.Add(
                new() { SourceVariable = "orderIndex", Variable = "lastIndex", DataType = "number", Required = true });
            var parent = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, new());
            var call = Assert.Single(await UntilAsync(() => CallsAsync(actor, instance.Id),
                rows => rows.Count == 1 && rows[0].ChildInstanceId is not null, "the child"));
            var childId = call.ChildInstanceId!.Value;

            // Someone else changes the shared target while the child waits.
            await SetSharedAsync(actor, key, 99);
            await ActAsync(actor, childId, ApproveFlow);
            var incident = Assert.Single(await UntilAsync(() => IncidentsAsync(actor, childId),
                rows => rows.Any(row => row.Status == "open"), "the delivery conflict"));
            Assert.Equal("output_version_conflict", incident.Type);
            Assert.Equal(instance.Id, incident.CallTargetInstanceId);
            Assert.Equal(99, (await SharedValueAsync(actor, key)).GetInt32());

            await RetryFromOperationsAsync(scenario, incident, "call-shared-conflict-operations");
            await UntilInstanceAsync(actor, instance.Id, AtNode(4), "the accepted delivery");
            Assert.Equal(0, (await SharedValueAsync(actor, key)).GetInt32());
            var after = Assert.Single(await CallsAsync(actor, instance.Id));
            Assert.Equal(childId, after.ChildInstanceId);
            Assert.Equal(call.InvocationId, after.InvocationId);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFaultedOrCancelledChildTakesTheBoundaryWithThePartialResult(bool cancelChild)
    {
        var name = cancelChild ? "cancelled" : "faulted";
        await using var scenario = await Stack.CreateScenario($"auto-mi-call-{name}", 1440, 900, scenarioTimeout: Budget);
        await scenario.RunAsync($"{name} child", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "mi-operator", OperatorRoles);
            var childKey = Unique("auto-mi-child");
            var childModel = Child(childKey);
            childModel.CancelRoles = ["admin"];
            await Stack.SetupClient.CreateAndPublishAsync(childModel);
            var model = Parent(Unique("auto-mi-parent"), childKey, "parallel", "a", "b");
            model.Variables.Add(NullableString(7, "failure"));
            AddBoundary(model, ErrorBoundary(5, "failure"), "Handle rejection");
            var parent = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, new());
            var calls = await UntilAsync(() => CallsAsync(actor, instance.Id),
                rows => rows.Count == 2 && rows.All(row => row.ChildInstanceId is not null), "both children");

            await ActAsync(actor, calls.Single(call => call.ItemIndex == 0).ChildInstanceId!.Value, ApproveFlow);
            await UntilAsync(() => CallsAsync(actor, instance.Id), rows => rows.Single(row => row.ItemIndex == 0).State == "completed", "the first result");
            var secondChild = calls.Single(call => call.ItemIndex == 1).ChildInstanceId!.Value;
            if (cancelChild)
            {
                // Someone cancels the child itself, not the parent.
                var (status, body) = await SendAsync(actor.Http, HttpMethod.Post, $"/api/instances/{secondChild}/cancel");
                Assert.True(status is >= 200 and < 300, body);
            }
            else await ActAsync(actor, secondChild, RejectFlow);
            var handled = await UntilInstanceAsync(actor, instance.Id, AtNode(6), "the error boundary");
            Assert.StartsWith(cancelChild ? CallFailureCodes.ChildCancelled : CallFailureCodes.ChildFaulted, Latest(handled, "failure").GetString());
            var partial = Latest(handled, "orderResults").EnumerateArray().ToArray();
            var completed = Assert.Single(partial, row => row.GetProperty("status").GetString() == "completed");
            Assert.Equal(0, completed.GetProperty("variables").GetProperty("itemIndex").GetInt32());
            var page = await OpenInstanceAsync(scenario, instance.Id);
            await Assertions.Expect(Summary(page)).ToContainTextAsync("Handle rejection");
            await CaptureAtViewportsAsync(scenario, $"call-{name}-partial-result");
        });
    }

    private static async Task<bool> SetDefaultAsync(MiActor actor, long workflowId)
    {
        var (status, body) = await SendAsync(actor.Http, HttpMethod.Post, $"/api/workflows/{workflowId}/set-default");
        Assert.True(status is >= 200 and < 300, body);
        return true;
    }

    private static async Task ActAsync(MiActor actor, long childId, int flowId)
    {
        var task = Assert.Single(await actor.Runtime.GetInboxAsync(childId));
        await actor.Runtime.TakeUserTaskFlowAsync(task.UserTaskId, flowId);
    }

    /// <summary>A child that waits for a person: Approve ends normally, Reject ends in an error.</summary>
    private static WorkflowModel Child(string key, bool withLabel = false) => new()
    {
        Id = key,
        Name = key,
        InitialEventId = 1,
        FlowNodes =
        [
            new()
            {
                Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent, X = 60, Y = 152,
                Variables =
                [
                    new() { Id = 1, Name = "order", DataType = "json", Required = true },
                    new() { Id = 2, Name = "orderIndex", DataType = "number", Required = true },
                    .. withLabel ? [new VariableModel { Id = 3, Name = "label", DataType = "string", Required = true }] : Array.Empty<VariableModel>(),
                ]
            },
            new() { Id = ChildTask, Name = "Approve order", Type = BpmnFlowNodeTypes.UserTask, X = 200, Y = 140 },
            new() { Id = 3, Name = "Approved", Type = BpmnFlowNodeTypes.EndEvent, X = 480, Y = 152 },
            new() { Id = 4, Name = "Rejected", Type = BpmnFlowNodeTypes.ErrorEndEvent, ErrorCode = "order.rejected", X = 480, Y = 300 },
        ],
        SequenceFlows =
        [
            new() { Id = 101, SourceRef = 1, TargetRef = ChildTask },
            new() { Id = ApproveFlow, Name = "Approve", SourceRef = ChildTask, TargetRef = 3 },
            new() { Id = RejectFlow, Name = "Reject", SourceRef = ChildTask, TargetRef = 4 },
        ]
    };

    /// <summary>One child per order, with the item and its index as inputs and the index copied back.</summary>
    private static WorkflowModel Parent(string key, string childKey, string mode, params string[] orderIds)
    {
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Check each order", Type = BpmnFlowNodeTypes.CallActivity, X = 200, Y = 140,
            MultiInstance = Multi(mode, "orders", "orderResults", 4, Local("itemIndex")),
            CallActivity = new()
            {
                WorkflowKey = childKey,
                InputMappings =
                [
                    new() { SourceVariable = "mi.item", Variable = "order", DataType = "json", Required = true },
                    new() { SourceVariable = "mi.index", Variable = "orderIndex", DataType = "number", Required = true },
                ],
                OutputMappings = [new() { SourceVariable = "orderIndex", Variable = "itemIndex", DataType = "number", Required = true }]
            }
        };
        return BodyWorkflow(key, body,
            Process(1, "orders", "json", orderIds.Select(id => new { id }).ToArray(), isArray: true),
            Process(2, "orderResults", "json", Array.Empty<object>()));
    }
}
