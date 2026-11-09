using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>
/// EVENT-09 cron timer starts through real API, UI and Worker processes. Schedules fire on real minute
/// boundaries; tests poll real state with bounded waits and never change clocks, job rows, or API responses.
/// </summary>
[Collection(AcceptanceCollection.Name)]
public sealed class CronScheduleAcceptanceTests(AcceptanceFixture fixture)
{
    private const string PreviewPath = "/api/workflows/timer-schedules/preview";
    private const string IdleStatus = "Preview the next five runs as the server calculates them.";
    private static readonly string[] AuthorRoles = ["admin"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private BrowserStackFixture Stack => fixture.Stack;

    [Fact]
    public async Task ACronStartFiresEachMinuteSkipsDowntimeAndMovesToANewDefault()
    {
        await using var scenario = await Stack.CreateScenario(
            "cron-start-runtime", 1440, 900, scenarioTimeout: TimeSpan.FromMinutes(10));
        await scenario.RunAsync("cron start runtime", async () =>
        {
            try
            {
                await Stack.StartWorkerAsync();
                await using var actor = await ApplyActorAsync(scenario, "cron-operator");
                // Publishing well inside a minute keeps the first occurrence queued long enough to observe.
                if (DateTimeOffset.UtcNow.Second >= 30) await WaitUntilAsync(NextMinute(DateTimeOffset.UtcNow).AddSeconds(2));
                var published = DateTimeOffset.UtcNow;
                var first = await Stack.SetupClient.CreateAndPublishAsync(Model("* * * * *", "Etc/UTC"));
                Assert.True(first.IsDefault);
                var key = first.WorkflowKey;

                // Default activation queues the first whole minute after it.
                var activated = await UntilAsync(() => TimerStartJobsAsync(actor, key),
                    jobs => jobs.Any(job => job.Status == "queued"), "the first cron occurrence");
                var firstJob = Assert.Single(activated);
                Assert.Equal(first.Id, firstJob.WorkflowDefinitionId);
                AssertWholeMinute(firstJob.DueAt);
                Assert.InRange(firstJob.DueAt, published, published.AddSeconds(60));

                await scenario.OpenUiAsync("operations");
                await WaitForInteractiveAsync(scenario.Page);
                var firstRow = JobRow(scenario.Page, firstJob);
                await Assertions.Expect(firstRow).ToContainTextAsync(key);
                await Assertions.Expect(firstRow).ToContainTextAsync("Every minute");
                await CaptureAsync(scenario, "cron-first-occurrence");

                // Each occurrence starts one instance and queues the next minute from its nominal time.
                await UntilAsync(() => InstancesAsync(actor, key), items => items.Count >= 1,
                    "the first cron start", TimeSpan.FromSeconds(150));
                var running = await UntilAsync(() => TimerStartJobsAsync(actor, key),
                    jobs => jobs.Any(job => job.Status == "completed") && jobs.Any(job => job.Status == "queued"),
                    "the occurrence after the first start");
                var fired = running.Where(job => job.Status == "completed").ToArray();
                var successor = Assert.Single(running, job => job.Status == "queued");
                Assert.Equal(fired.Max(job => job.DueAt).AddMinutes(1), successor.DueAt);
                var starts = await InstancesAsync(actor, key);
                Assert.Equal(fired.Length, starts.Count(instance => instance.CreatedAt < successor.DueAt.AddSeconds(-5)));
                var startedInstance = starts.MinBy(instance => instance.Id)!;
                Assert.InRange(startedInstance.CreatedAt, firstJob.DueAt.AddSeconds(-5), firstJob.DueAt.AddSeconds(65));
                Assert.Equal("Review scheduled run", startedInstance.CurrentNodeName);

                // Stopped past the one-minute grace, the Worker skips the missed occurrences instead of replaying them.
                await Stack.StopWorkerAsync();
                var stopped = await TimerStartJobsAsync(actor, key);
                var missed = Assert.Single(stopped, job => job.Status == "queued");
                var startsBeforeStop = (await InstancesAsync(actor, key)).Count;
                await WaitUntilAsync(missed.DueAt.AddSeconds(75));
                Assert.Equal("queued", Assert.Single(await TimerStartJobsAsync(actor, key), job => job.Id == missed.Id).Status);
                var restartedAt = DateTimeOffset.UtcNow;
                await Stack.StartWorkerAsync();
                var resumed = await UntilAsync(() => TimerStartJobsAsync(actor, key),
                    jobs => jobs.Any(job => job.Id == missed.Id && job.Status == "completed")
                        && jobs.Any(job => job.Status == "queued"),
                    "the Worker to skip the missed occurrences");
                var afterDowntime = Assert.Single(resumed, job => job.Status == "queued");
                AssertWholeMinute(afterDowntime.DueAt);
                Assert.InRange(afterDowntime.DueAt, restartedAt, DateTimeOffset.UtcNow.AddMinutes(1));
                Assert.Equal(startsBeforeStop, (await InstancesAsync(actor, key))
                    .Count(instance => instance.CreatedAt < afterDowntime.DueAt.AddSeconds(-5)));

                // The schedule then carries on from the first occurrence after the restart.
                await UntilAsync(() => InstancesAsync(actor, key), items => items.Count > startsBeforeStop,
                    "the first start after the restart", TimeSpan.FromSeconds(150));
                var resumedStart = (await InstancesAsync(actor, key)).MaxBy(instance => instance.Id)!;
                Assert.InRange(resumedStart.CreatedAt, afterDowntime.DueAt.AddSeconds(-5), afterDowntime.DueAt.AddSeconds(65));

                // A new default retires the old schedule and activates its own zone and expression.
                var replacement = await CreateVersionAsync(actor, first.Id, Model("0 9 1 1 *", "Asia/Tokyo", key));
                await SetDefaultAsync(actor, replacement.Id);
                var switchedAt = DateTimeOffset.UtcNow;
                var switched = await UntilAsync(() => TimerStartJobsAsync(actor, key),
                    jobs => jobs.Any(job => job.WorkflowDefinitionId == replacement.Id && job.Status == "queued")
                        && !jobs.Any(job => job.WorkflowDefinitionId == first.Id && job.Status is "queued" or "running"),
                    "the new default's first occurrence");
                var replacementJob = Assert.Single(switched, job => job.WorkflowDefinitionId == replacement.Id);
                // 09:00 on 1 January in Tokyo is midnight UTC.
                Assert.Equal(new DateTimeOffset(switchedAt.Year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero), replacementJob.DueAt);
                var startsAtSwitch = (await InstancesAsync(actor, key)).Count;
                await WaitUntilAsync(NextMinute(switchedAt).AddSeconds(10));
                Assert.Equal(startsAtSwitch, (await InstancesAsync(actor, key)).Count);

                await scenario.Page.ReloadAsync();
                await WaitForInteractiveAsync(scenario.Page);
                var replacementRow = JobRow(scenario.Page, replacementJob);
                await Assertions.Expect(replacementRow).ToContainTextAsync(key);
                await AssertNoHorizontalOverflowAsync(scenario.Page);
                await CaptureAsync(scenario, "cron-new-default");
                await scenario.Page.SetViewportSizeAsync(390, 844);
                await Assertions.Expect(replacementRow).ToBeAttachedAsync();
                await AssertNoHorizontalOverflowAsync(scenario.Page);
                await CaptureAsync(scenario, "cron-new-default-narrow");
                Assert.Empty(scenario.Warnings);
                Assert.Empty(scenario.FailedRequests);
            }
            finally
            {
                await Stack.StopWorkerAsync();
            }
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    public async Task APreviewAnswerHeldPastAScheduleEditOrReselectionIsDiscarded(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"cron-stale-preview-{width}x{height}", width, height, scenarioTimeout: TimeSpan.FromMinutes(4));
        await scenario.RunAsync("cron stale preview", async () =>
        {
            var proxy = Stack.ApiProxy;
            try
            {
                // No Worker runs: a preview reads no schedule state, and the workflow stays unpublished.
                await using var actor = await ApplyActorAsync(scenario, "cron-author");
                var workflow = await Stack.SetupClient.CreateUnpublishedAsync(Model("30 1 * * *", "Europe/London"));
                var page = scenario.Page;
                await scenario.OpenUiAsync($"workflows/{workflow.Id}/edit");
                await page.WaitForSelectorAsync("[data-editor-ready='true']");
                var editor = page.Locator("#workflow-editor-root");
                await EditorInteractions.PinInspectorOpenAsync(page);
                await EditorInteractions.SelectNodeAsync(page, 1);
                var expression = editor.GetByLabel("Cron expression", new LocatorGetByLabelOptions { Exact = true });
                var reference = editor.GetByLabel("Preview after (UTC, optional)", new LocatorGetByLabelOptions { Exact = true });
                var preview = editor.Locator(".cron-preview");
                var button = preview.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Preview next 5" });
                var status = preview.Locator(".cron-preview-status");
                var rows = preview.Locator("tbody tr");
                await reference.FillAsync("2026-10-24T12:00:00Z");
                // The proxy records the whole run, so count from this scenario's start.
                var previewsBefore = proxy.Requests.Count(IsPreview);
                var held = new List<RecordingApiProxy.RecordedRequest>();

                // The real answer for the old expression is held while the expression changes.
                held.Add(await HoldPreviewAsync(proxy, button, status, async () =>
                {
                    await CaptureInspectorAsync(scenario, $"preview-held-{width}x{height}");
                    await expression.FillAsync("*/30 1 * * *");
                    await Assertions.Expect(status).ToHaveTextAsync(IdleStatus);
                }));
                await AssertStaysIdleAsync(rows, status);

                // A fresh request answers for the schedule on screen: both offsets of the repeated hour.
                await button.ClickAsync();
                await Assertions.Expect(rows.Locator("td:first-child")).ToHaveTextAsync(
                [
                    "Sun 2026-10-25 01:00 +01:00",
                    "Sun 2026-10-25 01:30 +01:00",
                    "Sun 2026-10-25 01:00 +00:00",
                    "Sun 2026-10-25 01:30 +00:00",
                    "Mon 2026-10-26 01:00 +00:00"
                ]);

                // Selecting another node and coming back drops a held answer too.
                held.Add(await HoldPreviewAsync(proxy, button, status, async () =>
                {
                    await EditorInteractions.SelectNodeAsync(page, 2);
                    await EditorInteractions.SelectNodeAsync(page, 1);
                    await Assertions.Expect(status).ToHaveTextAsync(IdleStatus);
                    await Assertions.Expect(rows).ToHaveCountAsync(0);
                    await Assertions.Expect(reference).ToHaveValueAsync("2026-10-24T12:00:00Z");
                }));
                await AssertStaysIdleAsync(rows, status);
                await CaptureInspectorAsync(scenario, $"preview-discarded-{width}x{height}");

                // Every held answer was the API's own success, forwarded unchanged and later than the edit.
                var previews = proxy.Requests.Where(IsPreview).ToArray();
                Assert.All(held, request => Assert.Equal(200, previews.Single(row => row.Id == request.Id).StatusCode));
                Assert.Equal(previewsBefore + 3, previews.Length);

                // Restoring the saved expression leaves nothing unsaved for cleanup.
                await expression.FillAsync("30 1 * * *");
                await expression.PressAsync("Tab");
                await Assertions.Expect(page.Locator("#editor-status")).ToHaveTextAsync("Saved");
                Assert.Empty(scenario.Warnings);
                Assert.Empty(scenario.FailedRequests);
            }
            finally
            {
                proxy.ReleaseAllGates();
            }
        });
    }

    private static bool IsPreview(RecordingApiProxy.RecordedRequest request) =>
        request.Method == "POST" && request.PathAndQuery == PreviewPath;

    // Holds the next preview answer, runs the change, then releases it and waits until the UI has read it.
    private static async Task<RecordingApiProxy.RecordedRequest> HoldPreviewAsync(
        RecordingApiProxy proxy, ILocator button, ILocator status, Func<Task> change)
    {
        RecordingApiProxy.RecordedRequest held;
        using (var gate = proxy.GateNextResponse(IsPreview))
        {
            await button.ClickAsync();
            held = await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10), ScenarioCancellation.Token);
            await Assertions.Expect(status).ToHaveTextAsync("Calculating the next five runs…");
            await Assertions.Expect(button).ToHaveAttributeAsync("aria-disabled", "true");
            await change();
            gate.Release();
        }

        await UntilAsync(() => Task.FromResult(proxy.Requests.Single(request => request.Id == held.Id)),
            request => request.CompletedAt is not null, "the held answer to reach the UI", TimeSpan.FromSeconds(15));
        return held;
    }

    // The discarded answer arrives through the UI server after the proxy finishes, so watch for a while.
    private static async Task AssertStaysIdleAsync(ILocator rows, ILocator status)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            Assert.Equal(0, await rows.CountAsync());
            Assert.Equal(IdleStatus, (await status.TextContentAsync())?.Trim());
            await Task.Delay(200, ScenarioCancellation.Token);
        }
    }

    private static WorkflowModel Model(string cron, string timeZone, string? workflowKey = null)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cron-every-minute.json")))!
            .AsObject();
        json["id"] = workflowKey ?? "acceptance-cron-" + Guid.NewGuid().ToString("N");
        json["flowNodes"]![0]!["timer"] = new JsonObject { ["cron"] = cron, ["timeZone"] = timeZone };
        return json.Deserialize<WorkflowModel>(WebJson)!;
    }

    private static void AssertWholeMinute(DateTimeOffset value) =>
        Assert.True(value.Ticks % TimeSpan.TicksPerMinute == 0, $"{value:O} is not a whole minute.");

    private static DateTimeOffset NextMinute(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMinute + TimeSpan.TicksPerMinute, value.Offset);

    private static async Task WaitUntilAsync(DateTimeOffset instant)
    {
        var delay = instant - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ScenarioCancellation.Token);
    }

    // A job's due time keeps the exact instant the API returned in its tooltip.
    private static ILocator JobRow(IPage page, JobSummaryDto job) =>
        page.Locator("section[aria-labelledby='job-heading'] tbody tr")
            .Filter(new LocatorFilterOptions { Has = page.Locator($"time[title='{job.DueAt:O}']") })
            .Filter(new LocatorFilterOptions { HasText = job.WorkflowKey });

    private async Task<UiActor> ApplyActorAsync(BrowserScenario scenario, string user)
    {
        var identity = new IdentityScreen(scenario.Page, Stack.UiBaseAddress);
        var token = await identity.GenerateAndApplyIdentityAsync(user, AuthorRoles);
        scenario.OnCleanup(identity.ClearIdentityAsync);
        var http = new HttpClient { BaseAddress = new Uri(Stack.ApiBaseAddress) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new(http);
    }

    private sealed record UiActor(HttpClient Http) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { Http.Dispose(); return ValueTask.CompletedTask; }
    }

    private static async Task<IReadOnlyList<JobSummaryDto>> TimerStartJobsAsync(UiActor actor, string workflowKey) =>
        (await ReadAsync<PagedResult<JobSummaryDto>>(actor.Http,
            $"/api/jobs?workflowKey={Uri.EscapeDataString(workflowKey)}&kind=timerStart&pageSize=200")).Items;

    private static async Task<IReadOnlyList<InstanceSummaryDto>> InstancesAsync(UiActor actor, string workflowKey) =>
        (await ReadAsync<PagedResult<InstanceSummaryDto>>(actor.Http,
            $"/api/instances?workflowKey={Uri.EscapeDataString(workflowKey)}&pageSize=200")).Items;

    private static async Task<WorkflowDetailDto> CreateVersionAsync(UiActor actor, long sourceId, WorkflowModel definition)
    {
        using var response = await actor.Http.PutAsJsonAsync(
            $"/api/workflows/{sourceId}", new UpdateWorkflowRequest(definition, true), WebJson, ScenarioCancellation.Token);
        Assert.True(response.IsSuccessStatusCode,
            $"PUT workflow: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<WorkflowDetailDto>(WebJson, ScenarioCancellation.Token))!;
    }

    private static async Task SetDefaultAsync(UiActor actor, long id)
    {
        using var response = await actor.Http.PostAsync($"/api/workflows/{id}/set-default", null, ScenarioCancellation.Token);
        Assert.True(response.IsSuccessStatusCode,
            $"POST set-default: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<T> ReadAsync<T>(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path, ScenarioCancellation.Token);
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(WebJson, ScenarioCancellation.Token))!;
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

    private static async Task AssertNoHorizontalOverflowAsync(IPage page) =>
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 2"),
            "The page overflows horizontally.");

    // The wheel would move the editor's canvas, so the editor is captured by its inspector alone.
    private static Task CaptureInspectorAsync(BrowserScenario scenario, string name) =>
        scenario.Page.Locator("#inspectorPanel").ScreenshotAsync(new LocatorScreenshotOptions
        {
            Path = Path.Combine(scenario.ArtifactDirectory, name + ".png"),
        });

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
