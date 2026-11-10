using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

public sealed partial class InstanceDiagramHistoryAcceptanceTests
{
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task DelayedExecutionPagingKeepsKeyboardFocusAndDiscardsRevokedDetail(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario($"ui14-pager-focus-{width}x{height}", width, height,
            scenarioTimeout: TimeSpan.FromMinutes(4));
        await scenario.RunAsync("keyboard paging and permission revalidation", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "history-pager", ["admin"]);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(PagingModel());
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id);
            for (var visit = 0; visit < 50; visit++)
                await actor.Runtime.TakeInstanceFlowAsync(instance.Id, 202);
            var page = await OpenAsync(scenario, instance.Id);
            await Toggle(page).CheckAsync();
            await WaitForHistoryAsync(page);
            await SelectHistoryAsync(page, "Review");
            var details = page.Locator(".instance-diagram-details");
            var rows = details.Locator(".instance-diagram-recorded-executions li");
            var pager = details.GetByRole(AriaRole.Navigation, new() { Name = "Recorded execution pages" });
            await Assertions.Expect(rows).ToHaveCountAsync(50);
            await Assertions.Expect(details).ToContainTextAsync("51 execution records available to you");

            var proxy = Stack.ApiProxy!;
            await MoveAsync("Next executions", "Enter", 1, "next");
            await MoveAsync("Previous executions", "Space", 50, "previous");

            // The historical response is genuinely authorized before the setting changes.
            // Its subsequent current-position lookup must observe the new role requirement.
            var setting = Assert.Single(await Stack.SetupClient.ListEngineSettingsAsync(),
                item => item.Namespace == "NodeExecution" && item.Key == "RequiredRole");
            EngineSettingDto? changed = null;
            try
            {
                using var gate = proxy.GateNextResponse(IsExecutionSearch, TimeSpan.FromSeconds(60));
                var next = pager.GetByRole(AriaRole.Button, new() { Name = "Next executions" });
                await next.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                Assert.Equal(200, (await gate.Entered.WaitAsync(TimeSpan.FromSeconds(15), ScenarioCancellation.Token)).StatusCode);
                changed = await Stack.SetupClient.UpdateEngineSettingAsync(setting.Id,
                    new("history-reader-only", setting.Description, setting.UpdatedAt));
                gate.Release();
                await Assertions.Expect(details).ToContainTextAsync("0 execution records available to you");
                await Assertions.Expect(details.Locator("a[href^='node-executions/']")).ToHaveCountAsync(0);
                await Assertions.Expect(Toggle(page)).ToBeCheckedAsync();
                await Assertions.Expect(Flow(page, 202)).ToHaveCountAsync(1);
                await CaptureAsync(scenario, "revoked-after-historical-response");
            }
            finally
            {
                proxy.ReleaseAllGates();
                if (changed is not null)
                    await Stack.SetupClient.UpdateEngineSettingAsync(setting.Id,
                        new(setting.Value, setting.Description, changed.UpdatedAt));
            }
            await File.WriteAllTextAsync(Path.Combine(scenario.ArtifactDirectory, "requests.json"),
                JsonSerializer.Serialize(proxy.Requests, Json), ScenarioCancellation.Token);

            async Task MoveAsync(string buttonName, string key, int expectedRows, string capture)
            {
                var before = proxy.Requests.Count(IsExecutionSearch);
                using var gate = proxy.GateNextResponse(IsExecutionSearch, TimeSpan.FromSeconds(60));
                var button = pager.GetByRole(AriaRole.Button, new() { Name = buttonName });
                await button.FocusAsync();
                await page.Keyboard.PressAsync(key);
                Assert.Equal(200, (await gate.Entered.WaitAsync(TimeSpan.FromSeconds(15), ScenarioCancellation.Token)).StatusCode);
                await Assertions.Expect(details).ToContainTextAsync("Loading recorded executions");
                await Assertions.Expect(button).ToBeFocusedAsync();
                await Assertions.Expect(button).ToHaveAttributeAsync("aria-disabled", "true");
                await Assertions.Expect(pager).ToHaveAttributeAsync("aria-busy", "true");
                // A focused, semantically disabled button must ignore keyboard activation.
                await page.Keyboard.PressAsync("Enter");
                await CaptureAsync(scenario, capture + "-loading");
                await CaptureFocusedPagerAsync(button, capture + "-loading-details");
                gate.Release();
                await Assertions.Expect(rows).ToHaveCountAsync(expectedRows);
                await Assertions.Expect(pager).ToHaveAttributeAsync("aria-busy", "false");
                await Assertions.Expect(button).ToBeFocusedAsync();
                await Assertions.Expect(button).ToHaveAttributeAsync("aria-disabled", "true");
                await UntilAsync(() => Task.FromResult(proxy.Requests.Where(IsExecutionSearch).Skip(before).ToArray()),
                    requests => requests.Length >= 2 && requests.All(request => request.CompletedAt is not null), "both execution lookups");
                Assert.Equal(2, proxy.Requests.Count(IsExecutionSearch) - before);
                await CaptureAsync(scenario, capture + "-boundary");
                await CaptureFocusedPagerAsync(button, capture + "-boundary-details");
            }

            async Task CaptureFocusedPagerAsync(ILocator button, string name)
            {
                // The full page includes the complete audit table. Keep a readable
                // real-browser panel capture with the focused pager in view as well.
                await button.ScrollIntoViewIfNeededAsync();
                await Assertions.Expect(button).ToBeFocusedAsync();
                await details.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, name + ".png") });
                await pager.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, name + "-pager.png") });
            }
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task ReactivationWithHistoryEnabledRetainsPriorRunsAndRefreshesLatestPositions(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario($"ui14-reactivation-{width}x{height}", width, height);
        await scenario.RunAsync("recorded history across real reactivation", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "history-reactivator", ["admin"]);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(Model(durableMi: false));
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id);
            await actor.Runtime.TakeInstanceFlowAsync(instance.Id, 201);
            var page = await OpenAsync(scenario, instance.Id);
            await Toggle(page).CheckAsync();
            await WaitForHistoryAsync(page);
            await SelectHistoryAsync(page, "Review");
            await Assertions.Expect(page.Locator(".instance-diagram-recorded-executions li")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#diagram .node[data-id='3'].instance-marked")).ToHaveCountAsync(1);
            var before = await SummaryAsync(actor, instance.Id);
            Assert.Equal(1, Assert.Single(before.Flows, flow => flow.FlowId == 201).TraversalCount);
            await CaptureAsync(scenario, "completed-first-run");

            await page.Locator("#reactivation-target").SelectOptionAsync("2");
            await page.Locator("#reactivation-reason").FillAsync("Verify retained diagram history across runs");
            scenario.ExpectDialogOnce("confirm",
                $"Reactivate instance #{instance.Id} at 'Review' (node #2)? Variables and history will be retained. Completed work and external side effects will not be reversed, so work after this task may repeat.", accept: true);
            await page.GetByRole(AriaRole.Button, new() { Name = "Confirm reactivation", Exact = true }).ClickAsync();
            await Assertions.Expect(Toggle(page)).ToBeCheckedAsync();
            await Assertions.Expect(page.Locator("#diagram .node[data-id='2'].instance-marked")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#diagram .node[data-id='3'].instance-marked")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".instance-diagram-recorded-executions li")).ToHaveCountAsync(2);
            var reactivated = await SummaryAsync(actor, instance.Id);
            Assert.True(reactivated.InstanceUpdatedAt > before.InstanceUpdatedAt);
            Assert.Equal(1, Assert.Single(reactivated.Flows, flow => flow.FlowId == 101).TraversalCount);
            Assert.Equal(1, Assert.Single(reactivated.Flows, flow => flow.FlowId == 201).TraversalCount);
            await CaptureAsync(scenario, "reactivated-with-prior-history");

            await page.GetByRole(AriaRole.Button, new() { Name = "Take action", Exact = true }).ClickAsync();
            await Assertions.Expect(page.Locator("#diagram [data-flow='201'] .instance-flow-count"))
                .ToHaveAttributeAsync("data-traversal-count", "2");
            await Assertions.Expect(page.Locator("#diagram .node[data-id='3'].instance-marked")).ToHaveCountAsync(1);
            await Assertions.Expect(Toggle(page)).ToBeCheckedAsync();
            var completedAgain = await SummaryAsync(actor, instance.Id);
            Assert.Equal(2, Assert.Single(completedAgain.Flows, flow => flow.FlowId == 201).TraversalCount);
            Assert.Equal(1, Assert.Single(completedAgain.Flows, flow => flow.FlowId == 101).TraversalCount);
            await CaptureAsync(scenario, "completed-second-run");
            await File.WriteAllTextAsync(Path.Combine(scenario.ArtifactDirectory, "evidence.json"),
                JsonSerializer.Serialize(new { before, reactivated, completedAgain }, Json), ScenarioCancellation.Token);
        });
    }

    private static bool IsExecutionSearch(RecordingApiProxy.RecordedRequest request) =>
        request.Method == "POST" && request.PathAndQuery == "/api/node-executions/search";

    private static WorkflowModel PagingModel()
    {
        var model = Model(durableMi: false);
        model.TaskAssignmentRoles = [];
        model.FlowNodes[2].X = 900;
        model.FlowNodes.Add(new() { Id = 4, Name = "Repeat review", Type = BpmnFlowNodeTypes.ExclusiveGateway, X = 600, Y = 130 });
        model.SequenceFlows.Add(new() { Id = 202, Name = "Review again", SourceRef = 2, TargetRef = 4 });
        model.SequenceFlows.Add(new() { Id = 401, Name = "Return", SourceRef = 4, TargetRef = 2, Condition = "true", ConditionPriority = 1 });
        model.SequenceFlows.Add(new() { Id = 402, Name = "Fallback", SourceRef = 4, TargetRef = 3, IsDefault = true });
        return model;
    }
}
