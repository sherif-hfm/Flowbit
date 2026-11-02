using System.Diagnostics;
using System.Net;
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

/// <summary>
/// TASK-01 call activities through real API, UI and Worker processes, from the canonical
/// paired examples. Setup uses authenticated HTTP; state comes only from real commands,
/// Worker runs and restarts.
/// </summary>
[Collection(ExampleAcceptanceCollection.Name)]
public sealed class CallActivityAcceptanceTests(ExampleAcceptanceFixture fixture)
{
    private static readonly TimeSpan ScenarioBudget = TimeSpan.FromMinutes(5);
    private static readonly string[] OperatorRoles = ["admin", "Manager", "Purchasing"];
    private BrowserStackFixture Stack => fixture.Stack;

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task ParentAndChildLinkToEachOtherAndTheResultReachesTheParent(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"call-parent-child-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("parent and child", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "call-operator", OperatorRoles);
            var (_, parent) = await PublishPairAsync("call-activities/02-purchase-request-call.json");
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, Inputs());
            var call = await UntilAsync(() => SoleCallAsync(actor, instance.Id),
                item => item.ChildInstanceId is not null, "the Worker to start the child");
            Assert.Equal("running", call.State);
            var childId = call.ChildInstanceId!.Value;

            var page = scenario.Page;
            await scenario.OpenUiAsync($"instances/{instance.Id}");
            await WaitForInteractiveAsync(page);
            var section = page.Locator("#call-activities");
            await Assertions.Expect(section).ToContainTextAsync("Running");
            await Assertions.Expect(section).ToContainTextAsync("The child is running.");
            await Assertions.Expect(section).ToContainTextAsync(call.TargetWorkflowKey);
            await Assertions.Expect(section).ToContainTextAsync("Version 1");
            await Assertions.Expect(Summary(page)).ToContainTextAsync("Waiting for its child instance");
            await WaitForDiagramAsync(page);
            var marked = page.Locator(".flowbit-editor[data-mode='viewer'] .node.callActivity.instance-marked");
            await Assertions.Expect(marked).ToBeVisibleAsync();

            // The node can be selected with the keyboard, and its details link the child.
            await marked.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            var details = page.Locator(".instance-diagram-details");
            await Assertions.Expect(details).ToContainTextAsync("Waiting for its child instance");
            await Assertions.Expect(details.Locator($"a[href='instances/{childId}']")).ToBeVisibleAsync();
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "parent-waiting");

            // The link replaces the page's whole state, not just its address.
            await section.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = $"Instance #{childId}" }).ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/instances/{childId}$"));
            await Assertions.Expect(page.Locator("h1")).ToContainTextAsync($"Instance #{childId}");
            var childSection = page.Locator("#call-activities");
            await Assertions.Expect(childSection).ToContainTextAsync("Parent instance");
            await Assertions.Expect(childSection).ToContainTextAsync("Root instance");
            await Assertions.Expect(Summary(page)).ToContainTextAsync("Called by");
            await WaitForDiagramAsync(page);
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "child-waiting");

            await ActAsync(actor, childId, 2, 201);
            await UntilAsync(() => actor.Runtime.GetInstanceAsync(instance.Id), AtNode(3),
                "the result to reach the parent");

            await childSection.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = $"Instance #{instance.Id}" })
                .First.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/instances/{instance.Id}$"));
            var finished = page.Locator("#call-activities");
            await Assertions.Expect(finished).ToContainTextAsync("Completed");
            await Assertions.Expect(finished).ToContainTextAsync("its results were delivered");
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "parent-after-result");

            var detail = await actor.Runtime.GetInstanceAsync(instance.Id);
            var delivered = Assert.Single(detail.History, item => item.Note == "callCompleted");
            Assert.Equal("callActivity", delivered.PerformedBy);
            Assert.Equal("true", detail.Variables
                .Where(variable => variable.VariableName == "managerApproved")
                .OrderByDescending(variable => variable.Id)
                .First().Value.GetRawText());
            await ActAsync(actor, instance.Id, 3, 301);
            Assert.Equal("completed", (await actor.Runtime.GetInstanceAsync(instance.Id)).Status);
        });
    }

    [Fact]
    public async Task ARejectedChildFaultsTheParentOrTakesItsBoundary()
    {
        await using var scenario = await Stack.CreateScenario(
            "call-rejection", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("rejection", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "call-operator", OperatorRoles);

            var (_, faulting) = await PublishPairAsync("call-activities/02-purchase-request-call.json");
            var faulted = await actor.Runtime.StartInstanceAsync(faulting.Id, Inputs());
            var faultedCall = await UntilAsync(() => SoleCallAsync(actor, faulted.Id),
                item => item.ChildInstanceId is not null, "the first child");
            await ActAsync(actor, faultedCall.ChildInstanceId!.Value, 2, 202);
            await UntilAsync(() => actor.Runtime.GetInstanceAsync(faulted.Id),
                detail => detail.Status == "faulted", "the parent to fault");
            await scenario.OpenUiAsync($"instances/{faulted.Id}");
            await WaitForInteractiveAsync(scenario.Page);
            var failedSection = scenario.Page.Locator("#call-activities");
            await Assertions.Expect(failedSection).ToContainTextAsync("Failed");
            await Assertions.Expect(failedSection).ToContainTextAsync("faulted with code approval.rejected");
            await Assertions.Expect(failedSection).ToContainTextAsync("call_child_faulted");
            await CaptureAsync(scenario, "parent-faulted");

            var (_, catching) = await PublishPairAsync("call-activities/03-purchase-request-call-failure.json");
            var caught = await actor.Runtime.StartInstanceAsync(catching.Id, Inputs());
            var caughtCall = await UntilAsync(() => SoleCallAsync(actor, caught.Id),
                item => item.ChildInstanceId is not null, "the second child");
            await ActAsync(actor, caughtCall.ChildInstanceId!.Value, 2, 202);
            var handled = await UntilAsync(() => actor.Runtime.GetInstanceAsync(caught.Id), AtNode(6),
                "the boundary to catch the fault");
            var failure = handled.Variables.Where(variable => variable.VariableName == "callFailure")
                .OrderByDescending(variable => variable.Id).First().Value.GetString();
            Assert.StartsWith("call_child_faulted:", failure);
            await ActAsync(actor, caught.Id, 6, 601);
            Assert.Equal("completed", (await actor.Runtime.GetInstanceAsync(caught.Id)).Status);
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task ACancelledParentFencesItsChildUntilAWorkerStopsIt(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"call-cancel-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("cancellation in progress", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "call-operator", OperatorRoles);
            var (_, parent) = await PublishPairAsync("call-activities/02-purchase-request-call.json", cancelRoles: ["admin"]);
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, Inputs());
            var call = await UntilAsync(() => SoleCallAsync(actor, instance.Id),
                item => item.ChildInstanceId is not null, "the Worker to start the child");
            var childId = call.ChildInstanceId!.Value;
            await Stack.StopWorkerAsync();

            using (var cancelled = await actor.Http.PostAsync($"/api/instances/{instance.Id}/cancel", null, ScenarioCancellation.Token))
            {
                Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
            }

            Assert.Equal("cancelled", (await actor.Runtime.GetInstanceAsync(instance.Id)).Status);
            Assert.Equal("running", (await actor.Runtime.GetInstanceAsync(childId)).Status);
            using (var refused = await actor.Http.PostAsJsonAsync(
                       $"/api/instances/{childId}/flows/201", new { }, ScenarioCancellation.Token))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            }

            var page = scenario.Page;
            await scenario.OpenUiAsync($"instances/{childId}");
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator("#call-activities")).ToContainTextAsync("is being cancelled");
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "child-cancellation-in-progress");

            await scenario.OpenUiAsync($"instances/{instance.Id}");
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator("#call-activities")).ToContainTextAsync("still stopping the child");
            await CaptureAsync(scenario, "parent-cancelled-child-running");

            await Stack.StartWorkerAsync();
            await UntilAsync(() => actor.Runtime.GetInstanceAsync(childId),
                detail => detail.Status == "cancelled", "the restarted Worker to cancel the child");
            await scenario.OpenUiAsync($"instances/{childId}");
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator("#call-activities")).Not.ToContainTextAsync("is being cancelled");
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task ADeepDescendantExplainsACancellationAboveItsOwnRunningCall(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"call-deep-cancel-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("deep cancellation in progress", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "call-operator", OperatorRoles);
            var (_, middle) = await PublishPairAsync("call-activities/02-purchase-request-call.json");

            // The same example calls the example that calls the child.
            var topModel = ReadModel("call-activities/02-purchase-request-call.json");
            topModel.Id = $"{topModel.Id}-top-{Guid.NewGuid().ToString("N")[..8]}";
            topModel.CancelRoles = ["admin"];
            var topCall = topModel.FlowNodes.Single(node => node.Type == BpmnFlowNodeTypes.CallActivity).CallActivity!;
            topCall.WorkflowKey = middle.WorkflowKey;
            topCall.InputMappings.Single(mapping => mapping.Variable == "amount").Variable = "requestedAmount";
            topCall.OutputMappings.Single().SourceVariable = "managerApproved";
            var top = await Stack.SetupClient.CreateAndPublishAsync(topModel);

            var instance = await actor.Runtime.StartInstanceAsync(top.Id, Inputs());
            var call = await UntilAsync(() => SoleCallAsync(actor, instance.Id),
                item => item.ChildInstanceId is not null, "the Worker to start the middle instance");
            var middleId = call.ChildInstanceId!.Value;
            var nested = await UntilAsync(() => SoleCallAsync(actor, middleId),
                item => item.ChildInstanceId is not null, "the Worker to start the deepest instance");
            var deepestId = nested.ChildInstanceId!.Value;
            await Stack.StopWorkerAsync();

            using (var cancelled = await actor.Http.PostAsync($"/api/instances/{instance.Id}/cancel", null, ScenarioCancellation.Token))
            {
                Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
            }

            using (var refused = await actor.Http.PostAsJsonAsync(
                       $"/api/instances/{deepestId}/flows/201", new { }, ScenarioCancellation.Token))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            }

            var page = scenario.Page;
            await scenario.OpenUiAsync($"instances/{deepestId}");
            await WaitForInteractiveAsync(page);
            var section = page.Locator("#call-activities");
            await Assertions.Expect(section).ToContainTextAsync("is being cancelled");
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "deepest-cancellation-in-progress");

            // Each Worker job stops one level, so the notice stays until the last one runs.
            await Stack.StartWorkerAsync();
            await UntilAsync(() => actor.Runtime.GetInstanceAsync(deepestId),
                detail => detail.Status == "cancelled", "the cascade to reach the deepest instance");
            await scenario.OpenUiAsync($"instances/{deepestId}");
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(page.Locator("#call-activities")).Not.ToContainTextAsync("is being cancelled");
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task AnIncidentOnACallResultCancelsTheParentItActsOn(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"call-result-incident-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("result incident", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "call-operator", OperatorRoles);

            // The parent's own rule refuses the approval its child reports.
            var (_, parent) = await PublishPairAsync(
                "call-activities/02-purchase-request-call.json",
                cancelRoles: ["admin"],
                customizeParent: model => model.Variables.Single(variable => variable.Name == "managerApproved").Validation = "managerApproved == false");
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, Inputs());
            var call = await UntilAsync(() => SoleCallAsync(actor, instance.Id),
                item => item.ChildInstanceId is not null, "the Worker to start the child");
            var childId = call.ChildInstanceId!.Value;
            await ActAsync(actor, childId, 2, 201);

            var incident = (await UntilAsync(() => IncidentsAsync(actor, childId),
                items => items.Count > 0, "the delivery's incident")).Single();
            Assert.Equal(CallFailureCodes.OutputInvalid, incident.Type);
            Assert.Equal(childId, incident.InstanceId);
            Assert.Equal(instance.Id, incident.CallTargetInstanceId);
            Assert.Equal("completed", (await actor.Runtime.GetInstanceAsync(childId)).Status);

            var page = scenario.Page;
            await scenario.OpenUiAsync("operations");
            await WaitForInteractiveAsync(page);
            var row = page.Locator("section[aria-labelledby='incident-heading'] tbody tr")
                .Filter(new LocatorFilterOptions { HasText = $"Instance #{childId}" });
            await Assertions.Expect(row).ToContainTextAsync($"Acts on instance #{instance.Id}");
            var cancel = row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Cancel instance #{instance.Id}", Exact = true });
            await Assertions.Expect(cancel).ToBeVisibleAsync();
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "call-result-incident");

            await cancel.ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new LocatorFilterOptions
            {
                HasText = $"Instance #{instance.Id} was cancelled"
            })).ToBeVisibleAsync();

            // The parent is what stopped, and the finished child keeps its outcome.
            Assert.Equal("cancelled", (await actor.Runtime.GetInstanceAsync(instance.Id)).Status);
            Assert.Equal("completed", (await actor.Runtime.GetInstanceAsync(childId)).Status);
            await Assertions.Expect(row).ToHaveCountAsync(0);
            var after = await IncidentsAsync(actor, childId);
            Assert.Equal("resolved", Assert.Single(after).Status);
            await CaptureAsync(scenario, "call-result-incident-closed");
        });
    }

    [Fact]
    public async Task ACallWithNoTargetOpensAnIncidentWhoseRetryStartsTheChild()
    {
        await using var scenario = await Stack.CreateScenario(
            "call-target-incident", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("failed start", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "call-operator", OperatorRoles);
            var (child, parent) = await PublishPairAsync("call-activities/02-purchase-request-call.json");
            await Stack.SetupClient.DeleteWorkflowAsync(child.Id);

            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, Inputs());
            var incident = (await UntilAsync(() => IncidentsAsync(actor, instance.Id),
                items => items.Count > 0, "the failed start's incident")).Single();
            Assert.Equal(CallFailureCodes.TargetUnavailable, incident.Type);
            var call = await SoleCallAsync(actor, instance.Id);
            Assert.Equal("queued", call.State);
            Assert.Null(call.ChildInstanceId);
            Assert.Equal(1, call.OpenIncidentCount);

            var page = scenario.Page;
            await scenario.OpenUiAsync($"instances/{instance.Id}");
            await WaitForInteractiveAsync(page);
            var section = page.Locator("#call-activities");
            await Assertions.Expect(section).ToContainTextAsync("the child could not be started");
            await Assertions.Expect(section).ToContainTextAsync(CallFailureCodes.TargetUnavailable);
            await Assertions.Expect(section).ToContainTextAsync("Version chosen when the child starts");
            await Assertions.Expect(section).ToContainTextAsync("No child");
            await CaptureAsync(scenario, "call-with-incident");

            await Stack.SetupClient.CreateAndPublishAsync(ChildModel(call.TargetWorkflowKey));
            using (var retried = await actor.Http.PostAsync($"/api/incidents/{incident.Id}/retry", null, ScenarioCancellation.Token))
            {
                Assert.True(retried.IsSuccessStatusCode, await retried.Content.ReadAsStringAsync());
            }

            var started = await UntilAsync(() => SoleCallAsync(actor, instance.Id),
                item => item.ChildInstanceId is not null, "the retried start");
            Assert.Equal("running", started.State);
            Assert.Equal(0, started.OpenIncidentCount);
        });
    }

    [Fact]
    public async Task ACallOnlyStartIsExplainedOnTheStartPageAndStillStartsFromACall()
    {
        await using var scenario = await Stack.CreateScenario(
            "call-only-start", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("call-only start", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "call-operator", OperatorRoles);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var childKey = $"example-call-only-{suffix}";
            var childModel = ChildModel(childKey);
            childModel.FlowNodes.Single(node => node.Id == 1).CallOnly = true;
            var child = await Stack.SetupClient.CreateAndPublishAsync(childModel);

            var parentModel = ReadModel("call-activities/02-purchase-request-call.json");
            parentModel.Id = $"{parentModel.Id}-{suffix}";
            parentModel.FlowNodes.Single(node => node.Type == BpmnFlowNodeTypes.CallActivity).CallActivity!.WorkflowKey = childKey;
            var parent = await Stack.SetupClient.CreateAndPublishAsync(parentModel);

            using (var refused = await actor.Http.PostAsJsonAsync(
                       "/api/instances",
                       new { workflowId = child.Id, variables = new { amount = 5, requestId = "REQ-2" } },
                       ScenarioCancellation.Token))
            {
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            }

            await scenario.OpenUiAsync($"workflows/{child.Id}/start");
            await WaitForInteractiveAsync(scenario.Page);
            await Assertions.Expect(scenario.Page.Locator(".panel").First).ToContainTextAsync("Started only by call activities");
            await Assertions.Expect(scenario.Page.Locator("#start-configuration-heading")).ToHaveCountAsync(0);
            await CaptureAsync(scenario, "call-only-start-page");

            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, Inputs());
            var call = await UntilAsync(() => SoleCallAsync(actor, instance.Id),
                item => item.ChildInstanceId is not null, "the call to start the call-only child");
            Assert.Equal(child.Id, call.ChildDefinitionId);
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task TheWorkflowsPageShowsCallersBeforeTheDefaultChanges(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"call-callers-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("caller confirmation", async () =>
        {
            await using var actor = await ApplyActorAsync(scenario, "call-operator", OperatorRoles);
            var (child, parent) = await PublishPairAsync("call-activities/02-purchase-request-call.json");

            // A later child version drops an input the caller maps.
            var renamed = ChildModel(child.WorkflowKey);
            renamed.FlowNodes.Single(node => node.Id == 1).Variables.Single(variable => variable.Name == "amount").Name = "total";
            var candidate = await Stack.SetupClient.CreateAndPublishAsync(renamed);
            using (var moved = await actor.Http.PostAsync($"/api/workflows/{child.Id}/set-default", null, ScenarioCancellation.Token))
            {
                Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
            }

            var page = scenario.Page;
            await scenario.OpenUiAsync("workflows");
            await WaitForInteractiveAsync(page);
            await page.Locator(".accordion-button", new PageLocatorOptions { HasText = child.WorkflowKey }).ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);

            // Escape leaves everything as it was, and focus returns to the button.
            var setDefault = page.Locator($"#set-default-{candidate.Id}");
            await setDefault.ClickAsync();
            await Assertions.Expect(dialog).ToContainTextAsync($"Make version {candidate.Version} the default?");
            await Assertions.Expect(dialog).ToContainTextAsync("Incompatible");
            await Assertions.Expect(dialog).ToContainTextAsync(parent.WorkflowKey);
            await Assertions.Expect(dialog).ToContainTextAsync("does not declare input 'amount'");
            await Assertions.Expect(dialog).ToContainTextAsync("checks its own rules again");
            await AssertNoHorizontalOverflowAsync(page);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "set-default-confirmation.png")
            });
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(dialog).ToHaveCountAsync(0);
            await Assertions.Expect(setDefault).ToBeFocusedAsync();
            Assert.True((await actor.Runtime.GetWorkflowAsync(child.Id)).IsDefault);

            // Compatibility warns but does not block.
            await setDefault.ClickAsync();
            await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Set default", Exact = true }).ClickAsync();
            await Assertions.Expect(dialog).ToHaveCountAsync(0);
            await UntilAsync(() => actor.Runtime.GetWorkflowAsync(candidate.Id), workflow => workflow.IsDefault, "the new default");

            // Deleting the default names its successor, and cancelling keeps it.
            await page.Locator($"#delete-version-{candidate.Id}").ClickAsync();
            await Assertions.Expect(dialog).ToContainTextAsync($"Version {child.Version} would become the default.");
            await Assertions.Expect(dialog).ToContainTextAsync("Compatible");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "delete-default-confirmation.png")
            });
            await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel", Exact = true }).ClickAsync();
            Assert.True((await actor.Runtime.GetWorkflowAsync(candidate.Id)).IsDefault);
        });
    }

    [Fact]
    public async Task ADefaultDraftCanBeDeletedFromTheWorkflowsPage()
    {
        await using var scenario = await Stack.CreateScenario(
            "call-delete-default-draft", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("default draft deletion", async () =>
        {
            await using var actor = await ApplyActorAsync(scenario, "call-operator", OperatorRoles);

            // A family's first version is its default even before it is published.
            var key = $"example-call-draft-{Guid.NewGuid().ToString("N")[..8]}";
            var draft = await Stack.SetupClient.CreateUnpublishedAsync(ChildModel(key));
            Assert.True(draft.IsDefault);
            Assert.False(draft.IsPublished);

            var page = scenario.Page;
            await scenario.OpenUiAsync("workflows");
            await WaitForInteractiveAsync(page);
            await page.Locator(".accordion-button", new PageLocatorOptions { HasText = key }).ClickAsync();
            await page.Locator($"#delete-version-{draft.Id}").ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);
            await Assertions.Expect(dialog).ToContainTextAsync($"Delete default version {draft.Version}?");
            await Assertions.Expect(dialog).ToContainTextAsync("No published default would remain after this deletion");
            await Assertions.Expect(dialog).ToContainTextAsync("No published workflow calls this family.");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(scenario.ArtifactDirectory, "delete-default-draft-confirmation.png")
            });

            await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete version", Exact = true }).ClickAsync();
            await Assertions.Expect(dialog).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator($"#delete-version-{draft.Id}")).ToHaveCountAsync(0);
            using var read = await actor.Http.GetAsync($"/api/workflows/{draft.Id}", ScenarioCancellation.Token);
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        });
    }

    // An ordinary single-token instance is not polled, so the action itself has to reload the page.
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task ATaskActionThatEndsInAFailedCallShowsItWithoutARefresh(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"call-action-ends-{width}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("action-ends", async () =>
        {
            await using var actor = await ApplyActorAsync(scenario, "call-operator", OperatorRoles);
            var childKey = await PublishRequiredAmountChildAsync();
            var parent = await Stack.SetupClient.CreateAndPublishAsync(TaskThenFailingCall(childKey));
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, new Dictionary<string, JsonElement>());
            await scenario.OpenUiAsync($"instances/{instance.Id}");
            await WaitForInteractiveAsync(scenario.Page);
            var page = scenario.Page;
            await Assertions.Expect(page.Locator("#call-activities")).ToHaveCountAsync(0);

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Take action" }).First.ClickAsync();

            var section = page.Locator("#call-activities");
            await Assertions.Expect(section).ToContainTextAsync("Failed");
            await Assertions.Expect(section).ToContainTextAsync("call_input_invalid");
            await Assertions.Expect(section).Not.ToContainTextAsync("Loading calls");
            await Assertions.Expect(Summary(page)).ToContainTextAsync("Completed");
            await Assertions.Expect(page.Locator("section, .panel").Filter(new LocatorFilterOptions
            {
                Has = page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Administrative actions" })
            }).First).ToContainTextAsync("No active human-task positions");
            await CaptureAsync(scenario, $"action-ends-{width}");
        });
    }

    [Fact]
    public async Task ATaskActionAddsALaterCallBesideAnEarlierOneWhileTheInstanceRuns()
    {
        await using var scenario = await Stack.CreateScenario(
            "call-action-running", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("action-running", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "call-operator", OperatorRoles);
            var childKey = await PublishRequiredAmountChildAsync();
            var earlierKey = $"review-child-{Guid.NewGuid().ToString("N")[..8]}";
            await Stack.SetupClient.CreateAndPublishAsync(RequiredAmountChild(earlierKey, requiresAmount: false));
            var model = TaskThenFailingCall(childKey);

            // An admitted call first, then the task, and a second task once the later call fails.
            model.FlowNodes.Add(new FlowNodeModel
            {
                Id = 9,
                Name = "Earlier call",
                Type = BpmnFlowNodeTypes.CallActivity,
                CallActivity = new CallActivityModel { WorkflowKey = earlierKey }
            });
            model.FlowNodes.Add(new FlowNodeModel { Id = 7, Name = "Handle it", Type = BpmnFlowNodeTypes.UserTask });
            model.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 9;
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 901, SourceRef = 9, TargetRef = 6 });
            model.SequenceFlows.Single(flow => flow.SourceRef == 4).TargetRef = 7;
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 701, Name = "Done", SourceRef = 7, TargetRef = 5 });
            var parent = await Stack.SetupClient.CreateAndPublishAsync(model);
            var instance = await actor.Runtime.StartInstanceAsync(parent.Id, new Dictionary<string, JsonElement>());
            await UntilAsync(() => actor.Runtime.GetInstanceAsync(instance.Id), AtNode(6), "the first task");

            await scenario.OpenUiAsync($"instances/{instance.Id}");
            await WaitForInteractiveAsync(scenario.Page);
            var page = scenario.Page;
            var section = page.Locator("#call-activities");
            await Assertions.Expect(section).ToContainTextAsync("Earlier call");
            await Assertions.Expect(section).Not.ToContainTextAsync("Later call");

            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Take action" }).First.ClickAsync();

            await Assertions.Expect(section).ToContainTextAsync("Later call");
            await Assertions.Expect(section).ToContainTextAsync("Earlier call");
            await Assertions.Expect(section).ToContainTextAsync("call_input_invalid");
            await Assertions.Expect(Summary(page)).ToContainTextAsync("Handle it");
            await Assertions.Expect(page.Locator("section, .panel").Filter(new LocatorFilterOptions
            {
                Has = page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Administrative actions" })
            }).First).ToContainTextAsync("Handle it");
            await CaptureAsync(scenario, "action-running");
        });
    }

    private async Task<string> PublishRequiredAmountChildAsync()
    {
        var key = $"review-child-{Guid.NewGuid().ToString("N")[..8]}";
        await Stack.SetupClient.CreateAndPublishAsync(RequiredAmountChild(key, requiresAmount: true));
        return key;
    }

    private static WorkflowModel RequiredAmountChild(string key, bool requiresAmount) => new()
    {
        Id = key,
        Name = key,
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Variables = requiresAmount
                    ? [new VariableModel { Id = 1, Name = "amount", DataType = WorkflowVariableTypes.Number, Required = true }]
                    : []
            },
            new FlowNodeModel { Id = 2, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
    };

    // Start -> task -> call that fails on entry -> boundary -> end, with the mapped input never supplied.
    private static WorkflowModel TaskThenFailingCall(string childKey)
    {
        var key = $"review-parent-{Guid.NewGuid().ToString("N")[..8]}";
        return new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    Variables = [new VariableModel { Id = 1, Name = "amount", DataType = WorkflowVariableTypes.Number, Required = false }]
                },
                new FlowNodeModel { Id = 6, Name = "Continue", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Later call",
                    Type = BpmnFlowNodeTypes.CallActivity,
                    CallActivity = new CallActivityModel
                    {
                        WorkflowKey = childKey,
                        InputMappings =
                        [
                            new CallVariableMappingModel
                            {
                                SourceVariable = "amount",
                                Variable = "amount",
                                DataType = WorkflowVariableTypes.Number,
                                Required = true
                            }
                        ]
                    }
                },
                new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 4, Name = "Call failed", Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, AttachedToRef = 2 },
                new FlowNodeModel { Id = 5, Name = "Handled", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 6 },
                new SequenceFlowModel { Id = 601, Name = "Continue to call", SourceRef = 6, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 }
            ]
        };
    }

    private async Task<(WorkflowSummaryDto Child, WorkflowSummaryDto Parent)> PublishPairAsync(
        string parentPath,
        IReadOnlyList<string>? cancelRoles = null,
        Action<WorkflowModel>? customizeParent = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var childKey = $"example-call-manager-approval-{suffix}";
        var child = await Stack.SetupClient.CreateAndPublishAsync(ChildModel(childKey));
        var parent = ReadModel(parentPath);
        parent.Id = $"{parent.Id}-{suffix}";
        parent.FlowNodes.Single(node => node.Type == BpmnFlowNodeTypes.CallActivity).CallActivity!.WorkflowKey = childKey;
        if (cancelRoles is not null) parent.CancelRoles = cancelRoles.ToList();
        customizeParent?.Invoke(parent);
        return (child, await Stack.SetupClient.CreateAndPublishAsync(parent));
    }

    private WorkflowModel ChildModel(string key)
    {
        var child = ReadModel("call-activities/01-manager-approval-child.json");
        child.Id = key;
        return child;
    }

    private WorkflowModel ReadModel(string path) =>
        JsonSerializer.Deserialize<WorkflowModel>(
            File.ReadAllText(Path.Combine(Stack.RepositoryRoot, "examples", path)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static Dictionary<string, JsonElement> Inputs() => new()
    {
        ["requestedAmount"] = JsonSerializer.SerializeToElement(250),
        ["requestId"] = JsonSerializer.SerializeToElement("REQ-1")
    };

    private static Func<InstanceDetailDto, bool> AtNode(int nodeId) => detail =>
        detail.ExecutionPositions.Any(position => position.NodeId == nodeId && position.TokenStatus == "active");

    private static ILocator Summary(IPage page) =>
        page.Locator("section[aria-labelledby='instance-summary-heading']");

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

    private static async Task<CallActivityExecutionDto> SoleCallAsync(UiActor actor, long instanceId) =>
        Assert.Single((await ReadAsync<PagedResult<CallActivityExecutionDto>>(
            actor.Http, $"/api/instances/{instanceId}/calls")).Items);

    private static async Task<IReadOnlyList<IncidentSummaryDto>> IncidentsAsync(UiActor actor, long instanceId) =>
        (await ReadAsync<PagedResult<IncidentSummaryDto>>(actor.Http, $"/api/incidents?instanceId={instanceId}&pageSize=200")).Items;

    private static async Task ActAsync(UiActor actor, long instanceId, int nodeId, int flowId)
    {
        var task = Assert.Single(await actor.Runtime.GetInboxAsync(instanceId), item => item.CurrentNodeId == nodeId);
        await actor.Runtime.TakeUserTaskFlowAsync(task.UserTaskId, flowId);
    }

    private static async Task<T> ReadAsync<T>(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path, ScenarioCancellation.Token);
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ScenarioCancellation.Token))!;
    }

    private static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> accepted, string description,
        TimeSpan? timeout = null)
    {
        var timer = Stopwatch.StartNew();
        var budget = timeout ?? TimeSpan.FromSeconds(90);
        while (true)
        {
            var value = await read();
            if (accepted(value)) return value;
            if (timer.Elapsed >= budget) throw new TimeoutException($"Timed out waiting for {description}.");
            await Task.Delay(250, ScenarioCancellation.Token);
        }
    }

    private static Task WaitForInteractiveAsync(IPage page) => Assertions.Expect(page.Locator(".app-shell"))
        .ToHaveAttributeAsync("data-interactive", "true");

    // The read-only diagram mounts after the page; capture once it marks a node.
    private static Task WaitForDiagramAsync(IPage page) => Assertions.Expect(
        page.Locator(".flowbit-editor[data-mode='viewer'] .node.instance-marked").First).ToBeVisibleAsync();

    private static async Task AssertNoHorizontalOverflowAsync(IPage page) =>
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 2"),
            "The page overflows horizontally.");

    private static async Task CaptureAsync(BrowserScenario scenario, string name)
    {
        // A scrolled page would draw the sticky app shell mid-capture; return to the top with the wheel.
        var page = scenario.Page;
        await PageScrolling.ScrollToTopAsync(page);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(scenario.ArtifactDirectory, name + ".png"), FullPage = true,
        });
    }
}
