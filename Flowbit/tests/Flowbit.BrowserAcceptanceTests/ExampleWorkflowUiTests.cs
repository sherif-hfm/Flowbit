using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

[Collection(ExampleAcceptanceCollection.Name)]
public sealed class ExampleWorkflowUiTests(ExampleAcceptanceFixture fixture)
{
    private BrowserStackFixture Stack => fixture.Stack;

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task IncidentRecoveryThroughOperations(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario($"example-incident-ui-{width}x{height}", width, height);
        await scenario.RunAsync("incident-recovery-ui", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "example-operator", ["admin", "ExampleRequester", "ExampleOperator"]);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(Catalog("durable-jobs/02-retries-and-incident-recovery.json"));
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id,
                new() { ["scenario"] = JsonSerializer.SerializeToElement("recover") });
            IncidentSummaryDto? incident = null;
            await WaitUntilAsync(async () =>
            {
                var page = await ReadAsync<PagedResult<IncidentSummaryDto>>(actor.Http, $"/api/incidents?instanceId={instance.Id}");
                incident = page.Items.SingleOrDefault(item => item.Status == "open");
                return incident is not null;
            });
            var failed = await ReadAsync<JobDetailDto>(actor.Http, $"/api/jobs/{incident!.JobId}");
            Assert.Equal(3, failed.Summary.AttemptCount);
            await scenario.OpenUiAsync("operations");
            await WaitForInteractiveAsync(scenario.Page);
            var row = scenario.Page.Locator("section[aria-labelledby='incident-heading'] tbody tr")
                .Filter(new LocatorFilterOptions { HasText = workflow.WorkflowKey });
            await Assertions.Expect(row).ToContainTextAsync($"Instance #{instance.Id}");
            await CaptureAsync(scenario, "open-incident");

            fixture.Service.Recover(incident.JobId);
            var retry = row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retry", Exact = true });
            await retry.FocusAsync();
            await retry.PressAsync("Enter");
            await WaitUntilAsync(async () =>
                (await actor.Runtime.GetInstanceAsync(instance.Id)).ExecutionPositions.Any(position => position.NodeId == 3 && position.TokenStatus == "active"));
            var resolved = await ReadAsync<IncidentDetailDto>(actor.Http, $"/api/incidents/{incident.Id}");
            Assert.Equal("resolved", resolved.Summary.Status);
            Assert.Equal(incident.JobId, resolved.Summary.JobId);
            await scenario.Page.GetByLabel("Incident status", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync("resolved");
            await Assertions.Expect(row).ToContainTextAsync("Resolved");
            await CaptureAsync(scenario, "resolved-incident");

            var task = Assert.Single(await actor.Runtime.GetInboxAsync(instance.Id));
            await scenario.OpenUiAsync($"user-tasks/{task.UserTaskId}");
            await WaitForInteractiveAsync(scenario.Page);
            await scenario.Page.Locator(".action-card").Filter(new LocatorFilterOptions { HasText = "Confirm recovery" })
                .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Take action", Exact = true }).ClickAsync();
            await WaitUntilAsync(async () => (await actor.Runtime.GetInstanceAsync(instance.Id)).Status == "completed");
            await scenario.OpenUiAsync($"instances/{instance.Id}");
            await WaitForInteractiveAsync(scenario.Page);
            await Assertions.Expect(scenario.Page.Locator("#variables")).ToContainTextAsync("recovered");
            await CaptureAsync(scenario, "recovery-completed");
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task MultiInstanceRoleManagementThroughUi(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario($"example-mi-roles-ui-{width}x{height}", width, height);
        await scenario.RunAsync("multi-instance-role-ui", async () =>
        {
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(Catalog("multi-instance/07-role-management-active-and-pending.json"));
            await using var analyst = await ApplyActorAsync(scenario, "analyst", ["ReviewCoordinator", "Reviewer", "Approver"]);
            var instance = await analyst.Runtime.StartInstanceAsync(workflow.Id);
            var first = Assert.Single(await analyst.Runtime.GetInboxAsync(instance.Id));
            var executionId = first.MultiInstance!.ExecutionId;
            await analyst.Runtime.TakeUserTaskFlowAsync(first.UserTaskId, 201);
            var completedFirst = await analyst.Runtime.GetUserTaskAsync(first.UserTaskId);

            await using var manager = await ApplyActorAsync(scenario, "role-manager", ["RoleManager"]);
            var previous = await ReadAsync<UserTaskRolePolicyDto>(manager.Http, $"/api/multi-instance-executions/{executionId}/roles");
            Assert.Equal(1, previous.ActiveTaskCount);
            Assert.Equal(1, previous.PendingTaskCount);
            await scenario.OpenUiAsync("task-management");
            var page = scenario.Page;
            await WaitForInteractiveAsync(page);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Show filters", Exact = true }).ClickAsync();
            await page.Locator("#managed-instance-id").FillAsync(instance.Id.ToString());
            await page.Locator("#managed-instance-id").PressAsync("Tab");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters", Exact = true }).ClickAsync();
            var edit = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Edit roles", Exact = true }).First;
            await edit.FocusAsync();
            await edit.PressAsync("Enter");
            var dialog = page.GetByRole(AriaRole.Dialog);
            await Assertions.Expect(dialog).ToContainTextAsync("1 active and 1 pending");
            await Assertions.Expect(page.Locator("#task-role-values")).ToHaveValueAsync("Reviewer");
            await page.Locator("#task-role-values").FillAsync("SeniorReviewer");
            await page.Locator("#flow-role-201").FillAsync("SeniorApprover");
            await page.Locator("#roles-reason").FillAsync("Move unfinished reviews to the senior team");
            await CaptureAsync(scenario, "role-replacement-dialog");
            await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save roles", Exact = true }).ClickAsync();
            await Assertions.Expect(dialog).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".alert-success")).ToContainTextAsync("Waiting task roles updated.");
            var changed = await ReadAsync<UserTaskRolePolicyDto>(manager.Http, $"/api/multi-instance-executions/{executionId}/roles");
            Assert.NotEqual(previous.RolePolicyId, changed.RolePolicyId);
            Assert.Equal(["SeniorReviewer"], changed.Roles);
            Assert.Equal(["SeniorApprover"], Assert.Single(changed.Flows).Roles);
            await CaptureAsync(scenario, "unfinished-roles-updated");

            foreach (var username in new[] { "manager", "director" })
            {
                await using var reviewer = await ApplyActorAsync(scenario, username, ["SeniorReviewer", "SeniorApprover"]);
                var task = Assert.Single(await reviewer.Runtime.GetInboxAsync(instance.Id));
                var detail = await reviewer.Runtime.GetUserTaskAsync(task.UserTaskId);
                Assert.Equal(username, detail.Assignee);
                await scenario.OpenUiAsync($"user-tasks/{task.UserTaskId}");
                await WaitForInteractiveAsync(page);
                await page.Locator(".action-card").Filter(new LocatorFilterOptions { HasText = "Complete current review" })
                    .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Take action", Exact = true }).ClickAsync();
                await WaitUntilAsync(async () => (await reviewer.Runtime.GetUserTaskAsync(task.UserTaskId)).Status == "completed");
            }
            Assert.Equal("completed", (await analyst.Runtime.GetInstanceAsync(instance.Id)).Status);
            var firstAfter = await analyst.Runtime.GetUserTaskAsync(first.UserTaskId);
            Assert.Equal(completedFirst.CompletedBy, firstAfter.CompletedBy);
            Assert.Equal(completedFirst.CompletedAt, firstAfter.CompletedAt);
            Assert.Equal(completedFirst.Assignee, firstAfter.Assignee);
            Assert.Equal(completedFirst.Roles, firstAfter.Roles);
            await scenario.OpenUiAsync($"instances/{instance.Id}");
            await WaitForInteractiveAsync(page);
            await CaptureAsync(scenario, "managed-review-completed");
        });
    }

    private string Catalog(string path) => Path.Combine(Stack.RepositoryRoot, "examples", path);

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

    private static async Task<T> ReadAsync<T>(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path, ScenarioCancellation.Token);
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ScenarioCancellation.Token))!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (await predicate()) return;
            await Task.Delay(200, ScenarioCancellation.Token);
        }
        throw new TimeoutException("Expected example state was not observed within 60 seconds.");
    }

    private static Task WaitForInteractiveAsync(IPage page) => Assertions.Expect(page.Locator(".app-shell"))
        .ToHaveAttributeAsync("data-interactive", "true");

    private static Task CaptureAsync(BrowserScenario scenario, string name) => scenario.Page.ScreenshotAsync(new PageScreenshotOptions
    {
        Path = Path.Combine(scenario.ArtifactDirectory, name + ".png"), FullPage = true,
    });
}
