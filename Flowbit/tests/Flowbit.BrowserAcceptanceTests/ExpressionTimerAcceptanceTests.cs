using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>
/// EVENT-01 expression timers through real API, UI and Worker processes. Due
/// times come from instance data supplied at start; tests poll real state with
/// bounded waits and never change clocks, job rows, or API responses.
/// </summary>
[Collection(ExampleAcceptanceCollection.Name)]
public sealed class ExpressionTimerAcceptanceTests(ExampleAcceptanceFixture fixture)
{
    private static readonly TimeSpan ScenarioBudget = TimeSpan.FromMinutes(5);
    private static readonly string[] OperatorRoles = ["admin", "ExampleRequester", "ExampleOperator"];
    private BrowserStackFixture Stack => fixture.Stack;

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task ContractEndKeepsItsSavedScheduleAcrossAVariableEditAndWorkerRestart(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"expression-timer-contract-end-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("contract end schedule", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "timer-operator", OperatorRoles);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(Catalog("timers/07-contract-end-wait.json"));
            // Far enough ahead to inspect the wait, edit the input, and restart the Worker first.
            var contractEnd = WholeSeconds(DateTimeOffset.UtcNow.AddSeconds(75));
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id, Variables(("contractEnd", Iso(contractEnd))));
            var timer = Assert.Single(await JobsAsync(actor, instance.Id), job => job.Kind == "timer");
            Assert.Equal(contractEnd, timer.DueAt);

            await scenario.OpenUiAsync($"instances/{instance.Id}");
            await WaitForInteractiveAsync(scenario.Page);
            await Assertions.Expect(Summary(scenario.Page)).ToContainTextAsync("Wait Until Contract End");
            await WaitForDiagramAsync(scenario.Page);
            await CaptureAsync(scenario, "contract-waiting");
            await scenario.OpenUiAsync("operations");
            await WaitForInteractiveAsync(scenario.Page);
            var jobRow = JobRow(scenario.Page, instance.Id);
            await Assertions.Expect(jobRow).ToContainTextAsync("Wait Until Contract End");
            await Assertions.Expect(jobRow.Locator($"span[title='{timer.DueAt:O}']")).ToBeVisibleAsync();
            await CaptureAsync(scenario, "contract-operations");

            // Moving the input later does not move the saved due time, even across a Worker restart.
            await PatchVariablesAsync(actor, instance.Id, ("contractEnd", Iso(contractEnd.AddDays(1))));
            var afterEdit = Assert.Single(await JobsAsync(actor, instance.Id), job => job.Id == timer.Id);
            Assert.Equal(contractEnd, afterEdit.DueAt);
            Assert.True(afterEdit.Status == "queued", $"The timer fired before the restart; status {afterEdit.Status}.");
            await Stack.StopWorkerAsync();
            await Stack.StartWorkerAsync();
            await UntilAsync(() => actor.Runtime.GetInstanceAsync(instance.Id), AtNode(3),
                "the saved contract-end schedule to fire", TimeSpan.FromSeconds(150));

            var fired = Assert.Single(await JobsAsync(actor, instance.Id), job => job.Id == timer.Id);
            Assert.Equal("completed", fired.Status);
            Assert.Equal(contractEnd, fired.DueAt);
            var wait = Assert.Single(await ExecutionsAsync(actor, instance.Id), visit => visit.NodeId == 2);
            Assert.Equal("completed", wait.Status);
            // The database clock gates leasing; allow a few seconds of host/container skew.
            Assert.InRange(wait.CompletedAt!.Value, contractEnd.AddSeconds(-5), contractEnd.AddMinutes(3));

            await scenario.OpenUiAsync($"instances/{instance.Id}");
            await WaitForInteractiveAsync(scenario.Page);
            await Assertions.Expect(Summary(scenario.Page)).ToContainTextAsync("Renew or Close Contract");
            await AssertNoHorizontalOverflowAsync(scenario.Page);
            await WaitForDiagramAsync(scenario.Page);
            await CaptureAsync(scenario, "contract-fired");
            await ActAsync(actor, instance.Id, 3, 301);
            Assert.Equal("completed", (await actor.Runtime.GetInstanceAsync(instance.Id)).Status);
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task BoundariesRemindAndInterruptThenReactivationWarnsAboutThePastDeadline(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"expression-timer-boundaries-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("expression boundaries", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "timer-reviewer", OperatorRoles);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(Fixture("expression-timer-boundaries.json"));
            var before = DateTimeOffset.UtcNow;
            // Room to inspect both pages before the reminder fires.
            var deadline = WholeSeconds(before.AddSeconds(60));
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id,
                Variables(("reminderAfter", "PT25S"), ("deadline", Iso(deadline))));
            var after = DateTimeOffset.UtcNow;
            var jobs = await JobsAsync(actor, instance.Id);
            var reminder = Assert.Single(jobs, job => job.NodeId == 3);
            var escalation = Assert.Single(jobs, job => job.NodeId == 4);
            Assert.Equal("timerBoundary", reminder.Kind);
            Assert.InRange(reminder.DueAt, before.AddSeconds(25), after.AddSeconds(25));
            Assert.Equal(deadline, escalation.DueAt);

            await scenario.OpenUiAsync($"instances/{instance.Id}");
            await WaitForInteractiveAsync(scenario.Page);
            await Assertions.Expect(Summary(scenario.Page)).ToContainTextAsync("Review Request");
            await WaitForDiagramAsync(scenario.Page);
            await CaptureAsync(scenario, "boundaries-waiting");

            // Administrative actions lists the waiting review with both saved due times.
            await scenario.OpenUiAsync("administrative-actions");
            await WaitForInteractiveAsync(scenario.Page);
            await scenario.Page.Locator("#admin-workflow-family").SelectOptionAsync(workflow.WorkflowKey);
            await Assertions.Expect(scenario.Page.Locator("#admin-workflow-version")).ToHaveValueAsync(workflow.Id.ToString());
            await scenario.Page.Locator("#admin-source-node").SelectOptionAsync("2");
            await scenario.Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Search positions", Exact = true }).ClickAsync();
            var candidate = scenario.Page.Locator("tbody tr").Filter(new LocatorFilterOptions { HasText = $"Instance #{instance.Id}" });
            await Assertions.Expect(candidate).ToContainTextAsync("Boundary #3");
            await Assertions.Expect(candidate).ToContainTextAsync($"due {reminder.DueAt.ToLocalTime():g}");
            await Assertions.Expect(candidate).ToContainTextAsync("Boundary #4");
            await Assertions.Expect(candidate).ToContainTextAsync($"due {escalation.DueAt.ToLocalTime():g}");
            await CaptureAsync(scenario, "administrative-boundaries");

            // The non-interrupting reminder adds work and leaves the review open.
            await UntilAsync(() => actor.Runtime.GetInstanceAsync(instance.Id),
                detail => AtNode(5)(detail) && AtNode(2)(detail), "the reminder to fire beside the review");
            await scenario.OpenUiAsync($"instances/{instance.Id}");
            await WaitForInteractiveAsync(scenario.Page);
            await Assertions.Expect(Summary(scenario.Page)).ToContainTextAsync("Send Reminder");
            await Assertions.Expect(Summary(scenario.Page)).ToContainTextAsync("Review Request");
            await WaitForDiagramAsync(scenario.Page);
            await CaptureAsync(scenario, "reminder-fired");

            // The interrupting deadline ends the review and starts the escalation.
            await UntilAsync(() => actor.Runtime.GetInstanceAsync(instance.Id),
                detail => AtNode(6)(detail) && !AtNode(2)(detail), "the deadline to interrupt the review");
            var review = Assert.Single(await ExecutionsAsync(actor, instance.Id), visit => visit.NodeId == 2);
            Assert.Equal("cancelled", review.Status);
            await scenario.Page.ReloadAsync();
            await WaitForInteractiveAsync(scenario.Page);
            await Assertions.Expect(Summary(scenario.Page)).ToContainTextAsync("Escalated Review");
            await WaitForDiagramAsync(scenario.Page);
            await CaptureAsync(scenario, "deadline-fired");
            await ActAsync(actor, instance.Id, 5, 501);
            await ActAsync(actor, instance.Id, 6, 601);
            Assert.Equal("completed", (await actor.Runtime.GetInstanceAsync(instance.Id)).Status);

            // Reactivating the review would re-enter a deadline that has passed.
            var preview = await ReadAsync<InstanceReactivationPreviewDto>(actor.Http, $"/api/instances/{instance.Id}/reactivation");
            Assert.Contains(preview.Targets, target => target.NodeId == 2);
            var warning = Assert.Single(preview.Warnings, issue => issue.Code == "timer_boundary_already_due");
            Assert.Equal(2, warning.NodeId);
            Assert.Equal(4, warning.StateId);
            Assert.Empty(preview.Blockers);
            await scenario.Page.ReloadAsync();
            await WaitForInteractiveAsync(scenario.Page);
            var section = scenario.Page.Locator("#reactivation");
            await Assertions.Expect(section).ToContainTextAsync("Preview warnings");
            await Assertions.Expect(section).ToContainTextAsync("timer_boundary_already_due:");
            await Assertions.Expect(section).ToContainTextAsync("(node #2, runtime state #4)");
            await AssertNoHorizontalOverflowAsync(scenario.Page);
            await WaitForDiagramAsync(scenario.Page);
            await CaptureAsync(scenario, "reactivation-warning");
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task AMissingInputOpensAnIncidentThatRecoversAfterTheValueIsRepaired(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"expression-timer-incident-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("expression timer incident", async () =>
        {
            const string failure = "Timer boundary event #3 timer.timeDuration cannot be scheduled: variable 'slaDuration' has no value.";
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "timer-operator", OperatorRoles);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(Fixture("expression-timer-incident.json"));
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id);
            var incident = await UntilAsync(
                async () => (await IncidentsAsync(actor, instance.Id)).SingleOrDefault(item => item.Status == "open"),
                item => item is not null, "the scheduling incident");
            var failed = await ReadAsync<JobDetailDto>(actor.Http, $"/api/jobs/{incident!.JobId}");
            Assert.Equal(2, failed.Summary.AttemptCount);
            Assert.Equal(failure, failed.LastFailureDescription);
            Assert.DoesNotContain(await JobsAsync(actor, instance.Id), job => job.Kind == "timerBoundary");

            await scenario.OpenUiAsync("operations");
            await WaitForInteractiveAsync(scenario.Page);
            var row = scenario.Page.Locator("section[aria-labelledby='incident-heading'] tbody tr")
                .Filter(new LocatorFilterOptions { HasText = $"Instance #{instance.Id}" });
            // Operations lists the incident with its generic summary; the cause is on the job's API record.
            await Assertions.Expect(row).ToContainTextAsync("Workflow job failed at node #2.");
            await Assertions.Expect(row).ToContainTextAsync("job_execution_failed");
            await CaptureAsync(scenario, "incident-open");

            // Repair the input through the supported API, then retry from the Operations page.
            await PatchVariablesAsync(actor, instance.Id, ("slaDuration", "PT1H"));
            var retriedAt = DateTimeOffset.UtcNow;
            var retry = row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retry", Exact = true });
            await retry.FocusAsync();
            await retry.PressAsync("Enter");
            await UntilAsync(() => actor.Runtime.GetInstanceAsync(instance.Id), AtNode(4), "the repaired intake to continue");
            var resolved = await ReadAsync<IncidentDetailDto>(actor.Http, $"/api/incidents/{incident.Id}");
            Assert.Equal("resolved", resolved.Summary.Status);
            // The boundary was scheduled from the repaired value, then cancelled when its host completed.
            var boundary = Assert.Single(await JobsAsync(actor, instance.Id), job => job.Kind == "timerBoundary");
            Assert.Equal("cancelled", boundary.Status);
            Assert.InRange(boundary.DueAt, retriedAt.AddHours(1).AddSeconds(-5), DateTimeOffset.UtcNow.AddHours(1).AddSeconds(5));

            await scenario.Page.GetByLabel("Incident status", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync("resolved");
            await Assertions.Expect(row).ToContainTextAsync("Resolved");
            await AssertNoHorizontalOverflowAsync(scenario.Page);
            await CaptureAsync(scenario, "incident-resolved");
            await ActAsync(actor, instance.Id, 4, 401);
            Assert.Equal("completed", (await actor.Runtime.GetInstanceAsync(instance.Id)).Status);
        });
    }

    private string Catalog(string path) => Path.Combine(Stack.RepositoryRoot, "examples", path);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static DateTimeOffset WholeSeconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static Dictionary<string, JsonElement> Variables(params (string Name, object Value)[] values) =>
        values.ToDictionary(item => item.Name, item => JsonSerializer.SerializeToElement(item.Value));

    private static Func<InstanceDetailDto, bool> AtNode(int nodeId) => detail =>
        detail.ExecutionPositions.Any(position => position.NodeId == nodeId && position.TokenStatus == "active");

    private static ILocator Summary(IPage page) =>
        page.Locator("section[aria-labelledby='instance-summary-heading']");

    private static ILocator JobRow(IPage page, long instanceId) =>
        page.Locator("section[aria-labelledby='job-heading'] tbody tr")
            .Filter(new LocatorFilterOptions { HasText = $"Instance #{instanceId}" });

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

    private static async Task<IReadOnlyList<JobSummaryDto>> JobsAsync(UiActor actor, long instanceId) =>
        (await ReadAsync<PagedResult<JobSummaryDto>>(actor.Http, $"/api/jobs?instanceId={instanceId}&pageSize=200")).Items;

    private static async Task<IReadOnlyList<IncidentSummaryDto>> IncidentsAsync(UiActor actor, long instanceId) =>
        (await ReadAsync<PagedResult<IncidentSummaryDto>>(actor.Http, $"/api/incidents?instanceId={instanceId}&pageSize=200")).Items;

    private static async Task<IReadOnlyList<NodeExecutionSummaryDto>> ExecutionsAsync(UiActor actor, long instanceId) =>
        (await ReadAsync<PagedResult<NodeExecutionSummaryDto>>(actor.Http, $"/api/node-executions?instanceId={instanceId}&pageSize=200")).Items;

    private static async Task PatchVariablesAsync(UiActor actor, long instanceId, params (string Name, object Value)[] values)
    {
        var request = new UpdateInstanceVariablesRequest(
            values.Select(item => new InstanceVariableWriteDto(item.Name, JsonSerializer.SerializeToElement(item.Value))).ToArray(),
            "EVENT-01 acceptance input change",
            null);
        using var response = await actor.Http.PatchAsJsonAsync(
            $"/api/instances/{instanceId}/variables", request, ScenarioCancellation.Token);
        Assert.True(response.IsSuccessStatusCode,
            $"PATCH variables: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

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
        await page.Locator("h1").HoverAsync();
        await page.Mouse.WheelAsync(0, -await page.EvaluateAsync<int>("() => document.documentElement.scrollHeight"));
        await page.WaitForFunctionAsync("() => window.scrollY <= 1");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(scenario.ArtifactDirectory, name + ".png"), FullPage = true,
        });
    }
}
