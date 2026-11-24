using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.Shared.Dtos;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>
/// EVENT-04E signal broadcast expiry through real API, UI and Worker processes. S14 throws with an expiry while the
/// Worker is stopped and sees the broadcast expire when it returns, its waits still open; S15 refuses a retry once the
/// expiry has passed.
/// </summary>
public sealed partial class SignalAcceptanceTests
{
    /// <summary>
    /// S14: Example 01 under its own family, three waiting instances, and no Worker. A page throw with an expiry of
    /// five seconds stays Delivering and, once the expiry passes, notes that a Worker will end its deliveries; it no
    /// longer counts toward Signals.MaxOpenBroadcasts, so a second throw is accepted at a limit the first had filled.
    /// When the Worker starts, the broadcast expires, every delivery says why, instance detail still lists the wait,
    /// and a throw without an expiry then reaches every instance. The setting goes back to its earlier value, also
    /// when the scenario fails, because the suite's other scenarios share the stack.
    /// </summary>
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(390, 844)]
    public async Task S14_ExpiryDuringAnOutageSkipsTheDeliveriesAndLeavesTheWaitsOpen(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario(
            $"signal-s14-{width}x{height}", width, height, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("signal expiry during an outage", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var (workflow, signal) = await PublishExampleAsync($"s14w{width}");
            var instances = await StartManyAsync(actor, workflow.Id, 3);
            var page = scenario.Page;

            // Throw from the page with an expiry of five seconds.
            await scenario.OpenUiAsync("signals");
            await FillSignalAsync(page, signal);
            await page.Locator("#signal-workflow-key").FillAsync(workflow.WorkflowKey);
            await page.Locator("#signal-workflow-key").PressAsync("Tab");
            await page.Locator("#signal-payload").FillAsync("{ \"version\": 9 }");
            await page.Locator("#signal-payload").PressAsync("Tab");
            var amount = page.Locator("#signal-expiry-amount");
            await amount.ClickAsync();
            await amount.PressSequentiallyAsync("5", new LocatorPressSequentiallyOptions { Delay = 30 });
            await amount.PressAsync("Tab");
            await page.Locator("#signal-expiry-unit").SelectOptionAsync("Seconds");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Review signal" }).ClickAsync();
            var review = page.Locator("section[aria-labelledby='signal-review-heading']");
            await Assertions.Expect(review).ToContainTextAsync("Reaches now3 waits");
            await Assertions.Expect(review).ToContainTextAsync("Expires5 seconds after the throw");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Throw signal" }).ClickAsync();
            await Assertions.Expect(page.Locator(".alert-success"))
                .ToContainTextAsync(new Regex(@"Broadcast #\d+ froze 3 recipients; the Worker is delivering it\."));
            var broadcastId = long.Parse(Regex.Match(await page.Locator(".alert-success").InnerTextAsync(), @"#(\d+)").Groups[1].Value);
            var current = page.Locator("section[aria-labelledby='signal-current-heading']");
            await Assertions.Expect(current.Locator(".status-badge").First).ToHaveTextAsync("Delivering");
            await Assertions.Expect(current.Locator("dd.signal-expiry time")).ToHaveCountAsync(1);
            var first = await GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{broadcastId}");
            Assert.NotNull(first.ExpiresAt);

            // At the limit that the broadcasts still able to act fill, measured while the first one counts.
            var setting = Assert.Single(await Stack.SetupClient.ListEngineSettingsAsync(),
                item => item.Namespace == "Signals" && item.Key == "MaxOpenBroadcasts");
            try
            {
                var open = await OpenApiBroadcastsAsync(actor.Http);
                Assert.Contains(broadcastId, open.Select(item => item.Id));
                await Stack.SetupClient.UpdateEngineSettingAsync(setting.Id, new(
                    open.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), setting.Description, setting.UpdatedAt));
                if (first.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(1))
                {
                    using var refused = await actor.Http.PostAsJsonAsync("/api/signal-broadcasts",
                        new ThrowSignalRequest($"{signal}-early", AllWorkflows: true), JsonOptions, ScenarioCancellation.Token);
                    Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                }

                // The page's poll shows the note once the expiry is behind its clock.
                await Assertions.Expect(current.Locator(".signal-expiry-note"))
                    .ToHaveTextAsync("The expiry has passed. A Worker ends the pending deliveries when it runs.",
                        new LocatorAssertionsToHaveTextOptions { Timeout = 30_000f });
                await Assertions.Expect(current.Locator(".status-badge").First).ToHaveTextAsync("Delivering");
                await Assertions.Expect(current.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel pending deliveries" }))
                    .ToBeVisibleAsync();
                await AssertNoHorizontalOverflowAsync(page);
                await CaptureAsync(scenario, $"s14-expiry-passed-{width}");

                // Past its expiry the first broadcast no longer counts: the next throw is accepted. The database's
                // clock decides, and it can lag this host's, so a 409 is tried again for a few seconds.
                var accepted = await UntilAsync(async () =>
                    {
                        using var response = await actor.Http.PostAsJsonAsync("/api/signal-broadcasts",
                            new ThrowSignalRequest($"{signal}-second", AllWorkflows: true), JsonOptions, ScenarioCancellation.Token);
                        return response.StatusCode;
                    },
                    status => status != HttpStatusCode.Conflict, "the first broadcast to stop counting", TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(1));
                Assert.Equal(HttpStatusCode.Accepted, accepted);
            }
            finally
            {
                // Read the row again, because a failure can come before or after the change.
                var latest = Assert.Single(await Stack.SetupClient.ListEngineSettingsAsync(), item => item.Id == setting.Id);
                if (latest.Value != setting.Value)
                {
                    await Stack.SetupClient.UpdateEngineSettingAsync(setting.Id,
                        new(setting.Value, setting.Description, latest.UpdatedAt));
                }
            }

            // Nothing was delivered while the Worker was stopped.
            Assert.Equal(0, await DeliveredCountAsync(actor.Http, broadcastId));

            // The Worker settles the broadcast; every delivery says why, and every wait stays open.
            await Stack.StartWorkerAsync();
            var expired = await UntilAsync(() => GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{broadcastId}"),
                item => item.Status == SignalBroadcastStatuses.Expired, "the Worker to settle the expired broadcast", TimeSpan.FromSeconds(60));
            Assert.Equal((3, 3, 0), (expired.ExpiredCount, expired.SkippedCount, expired.DeliveredCount));
            await Assertions.Expect(current.Locator(".status-badge").First)
                .ToHaveTextAsync("Expired", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000f });
            await Assertions.Expect(current).ToContainTextAsync("3 expired");
            await Assertions.Expect(current.Locator("tbody tr")).ToHaveCountAsync(3);
            foreach (var row in await current.Locator("tbody tr").AllAsync())
            {
                await Assertions.Expect(row).ToContainTextAsync("The broadcast expired before delivery");
            }
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, $"s14-expired-{width}");

            await scenario.OpenUiAsync($"instances/{instances[0]}");
            await Assertions.Expect(page.Locator("#instance-signal-waits-heading")).ToHaveTextAsync("Waiting for signals");
            await Assertions.Expect(page.Locator("[aria-labelledby='instance-signal-waits-heading']")).ToContainTextAsync(signal);
            foreach (var id in instances)
            {
                var detail = await actor.Runtime.GetInstanceAsync(id);
                Assert.Single(detail.SignalSubscriptions);
                Assert.DoesNotContain(detail.History, item => item.Note == "signalReceived");
            }

            // A throw without an expiry then reaches every instance.
            var again = await ThrowAsync(actor.Http, new ThrowSignalRequest(signal, workflow.WorkflowKey,
                Payload: JsonSerializer.SerializeToElement(new { version = 10 })));
            Assert.Null(again.ExpiresAt);
            var delivered = await UntilAsync(() => GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{again.Id}"),
                item => item.Status == SignalBroadcastStatuses.Completed, "the throw without an expiry to finish", TimeSpan.FromSeconds(60));
            Assert.Equal(3, delivered.DeliveredCount);
            foreach (var id in instances)
            {
                Assert.Empty((await actor.Runtime.GetInstanceAsync(id)).SignalSubscriptions);
            }
        });
    }

    /// <summary>
    /// S15: a payload that fails the mapping, thrown with an expiry of ten seconds while the Worker runs, finishes with
    /// issues and offers Retry failed. Once the expiry has passed and the page is refreshed, the button is gone with its
    /// reason, and the retry route returns 409.
    /// </summary>
    [Fact]
    public async Task S15_RetryIsRefusedOnceTheExpiryHasPassed()
    {
        await using var scenario = await Stack.CreateScenario("signal-s15-1440x900", 1440, 900, scenarioTimeout: ScenarioBudget);
        await scenario.RunAsync("signal retry after the expiry", async () =>
        {
            await Stack.StartWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "signal-operator", OperatorRoles);
            var (workflow, signal) = await PublishExampleAsync("s15");
            await StartManyAsync(actor, workflow.Id, 1);

            var broadcast = await ThrowAsync(actor.Http, new ThrowSignalRequest(signal, workflow.WorkflowKey,
                Payload: JsonSerializer.SerializeToElement(new { version = "seven" }), ExpiresAfter: "PT10S"));
            var failed = await UntilAsync(() => GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{broadcast.Id}"),
                item => item.Status == SignalBroadcastStatuses.CompletedWithIssues, "the failing delivery to finish", TimeSpan.FromSeconds(60));
            Assert.Equal(1, failed.FailedCount);

            var page = scenario.Page;
            await scenario.OpenUiAsync($"signals?broadcastId={broadcast.Id}");
            var current = page.Locator("section[aria-labelledby='signal-current-heading']");
            var retry = current.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retry 1 failed" });
            if (failed.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(2))
            {
                await Assertions.Expect(retry).ToBeVisibleAsync();
                await CaptureAsync(scenario, "s15-retry-offered");
            }

            // After the expiry and a refresh, the button is gone and the page says why.
            var wait = failed.ExpiresAt!.Value - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ScenarioCancellation.Token);
            }
            await page.ReloadAsync();
            await WaitForInteractiveAsync(page);
            await Assertions.Expect(current).ToContainTextAsync("Completed with issues");
            await Assertions.Expect(retry).ToHaveCountAsync(0);
            await Assertions.Expect(current.Locator(".signal-retry-expired")).ToHaveTextAsync(
                "This broadcast has expired, so its failed deliveries cannot be retried. Throw the signal again to reach the waits that are still open.");
            await AssertNoHorizontalOverflowAsync(page);
            await CaptureAsync(scenario, "s15-retry-refused");

            // The API is the rule: the retry returns 409 and changes nothing.
            using var response = await actor.Http.PostAsJsonAsync($"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
                new RetrySignalDeliveriesRequest(failed.UpdatedAt), JsonOptions, ScenarioCancellation.Token);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains($"Signal broadcast #{broadcast.Id} expired at ", await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            var unchanged = await GetAsync<SignalBroadcastDetailDto>(actor.Http, $"/api/signal-broadcasts/{broadcast.Id}");
            Assert.Equal((SignalBroadcastStatuses.CompletedWithIssues, failed.UpdatedAt), (unchanged.Status, unchanged.UpdatedAt));
        });
    }

    /// <summary>The broadcasts thrown through the API that are delivering and not past their expiry by this host's clock.</summary>
    private static async Task<List<SignalBroadcastSummaryDto>> OpenApiBroadcastsAsync(HttpClient http)
    {
        var page = await GetAsync<PagedResult<SignalBroadcastSummaryDto>>(http,
            $"/api/signal-broadcasts?status={SignalBroadcastStatuses.Delivering}&thrownByKind={SignalThrowerKinds.User}"
            + $"&thrownByKind={SignalThrowerKinds.Client}&page=1&pageSize=200");
        return page.Items.Where(item => item.ExpiresAt is null || item.ExpiresAt > DateTimeOffset.UtcNow).ToList();
    }
}
