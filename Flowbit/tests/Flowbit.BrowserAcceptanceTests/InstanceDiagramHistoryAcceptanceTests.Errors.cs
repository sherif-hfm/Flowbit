using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

public sealed partial class InstanceDiagramHistoryAcceptanceTests
{
    [Fact]
    public async Task ExpiredTokenDuringLocalRetryShowsUnavailableWithoutAnIdentityChange()
    {
        await using var scenario = await Stack.CreateScenario("ui14-expired-token-retry-390x844", 390, 844,
            scenarioTimeout: TimeSpan.FromMinutes(3));
        scenario.OnCleanup(Stack.ResumeDatabaseAfterFailureAsync);
        await scenario.RunAsync("real token expiry during diagram-local retry", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var setup = await ApplyActorAsync(scenario, "history-expiry-setup", ["admin"]);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(PagingModel());
            var instance = await setup.Runtime.StartInstanceAsync(workflow.Id);
            const string user = "history-expiring-reader";
            var expires = DateTimeOffset.UtcNow.AddSeconds(5);
            var jwt = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer: Stack.JwtIssuer, audience: Stack.JwtAudience,
                claims: [new("Username", user), new(ClaimTypes.Name, user), new(JwtRegisteredClaimNames.Sub, user), new(ClaimTypes.Role, "admin")],
                notBefore: DateTime.UtcNow.AddSeconds(-1), expires: expires.UtcDateTime,
                signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Stack.JwtKey)), SecurityAlgorithms.HmacSha256)));
            // Apply through the supported token screen. The API's normal 60-second
            // clock skew gives time to load the instance before actual expiration.
            await scenario.Page.Locator("#raw-token").FillAsync(jwt);
            await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Apply pasted token", Exact = true }).ClickAsync();
            await Assertions.Expect(scenario.Page.Locator("#current-token-heading").Locator("xpath=ancestor::section"))
                .ToContainTextAsync(user);
            using var tokenProbe = new HttpClient { BaseAddress = new Uri(Stack.ApiBaseAddress) };
            tokenProbe.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            using (var accepted = await tokenProbe.GetAsync("/api/auth/context", ScenarioCancellation.Token))
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

            var page = await OpenAsync(scenario, instance.Id);
            await Toggle(page).CheckAsync();
            await WaitForHistoryAsync(page);
            var requestStart = Stack.ApiProxy.Requests.Count;
            var detailPath = $"/api/instances/{instance.Id}";
            try
            {
                await Stack.StopDatabaseForFailureAsync();
                await page.GetByRole(AriaRole.Button, new() { Name = "Refresh", Exact = true }).ClickAsync();
                var status = page.Locator(".instance-diagram-history-status");
                await Assertions.Expect(status).ToContainTextAsync("Recorded history could not be refreshed.", new() { Timeout = 45000 });
                Assert.Contains(Stack.ApiProxy.Requests.Skip(requestStart), request =>
                    request.PathAndQuery == detailPath && request.StatusCode == 500);
                await Stack.ResumeDatabaseAfterFailureAsync();

                // Observe authentication itself rejecting the unchanged token. No
                // UI identity action, clock override, or synthetic response occurs.
                var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
                HttpStatusCode observed;
                do
                {
                    using var response = await tokenProbe.GetAsync("/api/auth/context", ScenarioCancellation.Token);
                    observed = response.StatusCode;
                    Assert.True(observed is HttpStatusCode.OK or HttpStatusCode.Unauthorized);
                    if (observed == HttpStatusCode.Unauthorized) break;
                    if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The real API did not reject the expired fixture token.");
                    await Task.Delay(1000, ScenarioCancellation.Token);
                } while (true);
                await Assertions.Expect(Toggle(page)).ToBeCheckedAsync();
                var retryStart = Stack.ApiProxy.Requests.Count;
                var retry = status.GetByRole(AriaRole.Button, new() { Name = "Retry recorded history", Exact = true });
                await retry.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 }))
                    .ToHaveTextAsync($"Instance #{instance.Id} unavailable");
                var main = page.Locator("#main-content");
                await Assertions.Expect(main.GetByRole(AriaRole.Alert)).ToHaveTextAsync(
                    "This workflow instance is not available to this identity. Sign in again or check your permissions.");
                await Assertions.Expect(main.Locator(".page-header")).ToContainTextAsync("Check your identity and retry loading this instance.");
                await Assertions.Expect(main).Not.ToContainTextAsync("Loading instance");
                await Assertions.Expect(main.Locator("#diagram")).ToHaveCountAsync(0);
                await Assertions.Expect(main.GetByRole(AriaRole.Button, new() { Name = "Retry instance", Exact = true })).ToBeVisibleAsync();
                Assert.Contains(Stack.ApiProxy.Requests.Skip(retryStart), request =>
                    request.Method == "GET" && request.PathAndQuery == detailPath && request.StatusCode == 401);
                await CaptureAsync(scenario, "expired-token-unavailable");
                await main.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "expired-token-unavailable-panel.png") });
                await File.WriteAllTextAsync(Path.Combine(scenario.ArtifactDirectory, "evidence.json"),
                    JsonSerializer.Serialize(new { instanceId = instance.Id, expires, rejectedAt = DateTimeOffset.UtcNow, observed }, Json), ScenarioCancellation.Token);
                await SaveErrorRequestsAsync(scenario, requestStart);
            }
            finally { await Stack.ResumeDatabaseAfterFailureAsync(); }
        });
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task MissingInstanceShowsFriendlyNotFoundWithoutIdentityAdvice(int width, int height)
    {
        await using var scenario = await Stack.CreateScenario($"ui14-missing-instance-{width}x{height}", width, height);
        await scenario.RunAsync("real instance 404", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "history-missing-reader", ["admin"]);
            const long missingId = long.MaxValue;
            var before = Stack.ApiProxy.Requests.Count;
            // OpenUiAsync intentionally waits for a loaded instance summary;
            // a genuine 404 has no summary, so use ordinary browser navigation.
            var page = scenario.Page;
            await page.GotoAsync($"{Stack.UiBaseAddress}/instances/{missingId}", new() { WaitUntil = WaitUntilState.Load });
            await Assertions.Expect(page.Locator(".app-shell")).ToHaveAttributeAsync("data-interactive", "true");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 }))
                .ToHaveTextAsync($"Instance #{missingId} unavailable");
            var main = page.Locator("#main-content");
            await Assertions.Expect(main.GetByRole(AriaRole.Alert)).ToHaveTextAsync("The workflow instance no longer exists.");
            await Assertions.Expect(main.Locator(".page-header")).ToContainTextAsync("Check the instance address, or return to the instance list.");
            await Assertions.Expect(main).Not.ToContainTextAsync("Check your identity");
            await Assertions.Expect(main).Not.ToContainTextAsync("Response status code");
            await Assertions.Expect(main.Locator("#diagram")).ToHaveCountAsync(0);
            await Assertions.Expect(main.GetByRole(AriaRole.Button, new() { Name = "Retry instance", Exact = true })).ToBeVisibleAsync();
            Assert.Contains(Stack.ApiProxy.Requests.Skip(before), request =>
                request.Method == "GET" && request.PathAndQuery == $"/api/instances/{missingId}" && request.StatusCode == 404);
            await CaptureAsync(scenario, "friendly-instance-not-found");
            await main.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "friendly-instance-not-found-panel.png") });
            await SaveErrorRequestsAsync(scenario, before);
        });
    }

    [Theory]
    [InlineData(1440, 900, false)]
    [InlineData(1024, 768, false)]
    [InlineData(390, 844, false)]
    [InlineData(1440, 900, true)]
    [InlineData(1024, 768, true)]
    [InlineData(390, 844, true)]
    public async Task RealRefreshFailureShowsLocalRetryAndReloadsDetailWithHistory(int width, int height, bool afterAction)
    {
        var mode = afterAction ? "action-flows" : "manual-detail";
        await using var scenario = await Stack.CreateScenario($"ui14-error-{mode}-{width}x{height}", width, height,
            scenarioTimeout: TimeSpan.FromMinutes(4));
        // Register recovery before identity cleanup, including assertion/timeout failures.
        scenario.OnCleanup(Stack.ResumeDatabaseAfterFailureAsync);
        await scenario.RunAsync("real API failure and diagram-local recovery", async () =>
        {
            await Stack.StopWorkerAsync();
            await using var actor = await ApplyActorAsync(scenario, "history-error-reader", ["admin"]);
            var workflow = await Stack.SetupClient.CreateAndPublishAsync(PagingModel());
            var instance = await actor.Runtime.StartInstanceAsync(workflow.Id);
            await actor.Runtime.TakeInstanceFlowAsync(instance.Id, 202);
            var page = await OpenAsync(scenario, instance.Id);
            await Toggle(page).CheckAsync();
            await WaitForHistoryAsync(page);
            var diagram = page.Locator("#diagram");
            var status = diagram.Locator(".instance-diagram-history-status");
            var before = await SummaryAsync(actor, instance.Id);
            await Assertions.Expect(diagram.Locator("[data-flow='202'] .instance-flow-count"))
                .ToHaveAttributeAsync("data-traversal-count", "1");
            var proxy = Stack.ApiProxy;
            var requestStart = proxy.Requests.Count;
            var detailPath = $"/api/instances/{instance.Id}";
            var failurePath = afterAction ? detailPath + "/flows" : detailPath;
            try
            {
                if (afterAction)
                {
                    // The real POST has committed and the following detail GET has
                    // succeeded. Hold its unchanged response while stopping only our
                    // PostgreSQL container; the next flow lookup must fail in the API.
                    using var gate = proxy.GateNextResponse(request => request.Method == "GET"
                        && request.PathAndQuery == detailPath, TimeSpan.FromSeconds(60));
                    var action = page.Locator(".card").Filter(new()
                    {
                        Has = page.GetByRole(AriaRole.Heading, new() { Name = "Review again", Exact = true })
                    }).GetByRole(AriaRole.Button, new() { Name = "Take action", Exact = true });
                    await action.ClickAsync();
                    var held = await gate.Entered.WaitAsync(TimeSpan.FromSeconds(20), ScenarioCancellation.Token);
                    Assert.Equal(200, held.StatusCode);
                    Assert.Contains(proxy.Requests.Skip(requestStart), request => request.Method == "POST"
                        && request.PathAndQuery == detailPath + "/flows/202" && request.StatusCode == 200);
                    await Stack.StopDatabaseForFailureAsync();
                    gate.Release();
                }
                else
                {
                    await Stack.StopDatabaseForFailureAsync();
                    await page.GetByRole(AriaRole.Button, new() { Name = "Refresh", Exact = true }).ClickAsync();
                }

                await Assertions.Expect(status).ToContainTextAsync(
                    "Recorded history could not be refreshed. Retry to reload the instance and its history.",
                    new() { Timeout = 45000 });
                await Assertions.Expect(status).Not.ToContainTextAsync("Loading recorded history");
                await Assertions.Expect(status).Not.ToContainTextAsync("Snapshot");
                await Assertions.Expect(Toggle(page)).ToBeCheckedAsync();
                await Assertions.Expect(diagram.Locator(".instance-flow-count")).ToHaveCountAsync(0);
                var retry = status.GetByRole(AriaRole.Button, new() { Name = "Retry recorded history", Exact = true });
                await Assertions.Expect(retry).ToBeVisibleAsync();
                var failure = Assert.Single(proxy.Requests.Skip(requestStart), request =>
                    request.Method == "GET" && request.PathAndQuery == failurePath && request.StatusCode >= 500);
                await CaptureAsync(scenario, "local-refresh-error");
                await status.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "local-refresh-error-panel.png") });

                await Stack.ResumeDatabaseAfterFailureAsync();
                var retryStart = proxy.Requests.Count;
                await retry.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await WaitForHistoryAsync(page);
                await Assertions.Expect(status.GetByRole(AriaRole.Button, new() { Name = "Retry recorded history", Exact = true }))
                    .ToHaveCountAsync(0);
                await Assertions.Expect(diagram.Locator("[data-flow='202'] .instance-flow-count"))
                    .ToHaveAttributeAsync("data-traversal-count", afterAction ? "2" : "1");
                var retried = proxy.Requests.Skip(retryStart).ToArray();
                foreach (var path in new[] { detailPath, detailPath + "/flows", detailPath + "/diagram-history" })
                    Assert.Contains(retried, request => request.Method == "GET" && request.PathAndQuery == path && request.StatusCode == 200);
                var restored = await SummaryAsync(actor, instance.Id);
                Assert.Equal(afterAction ? 2 : 1, Assert.Single(restored.Flows, flow => flow.FlowId == 202).TraversalCount);
                if (afterAction) Assert.True(restored.InstanceUpdatedAt > before.InstanceUpdatedAt);
                else Assert.Equal(before.InstanceUpdatedAt, restored.InstanceUpdatedAt);
                await CaptureAsync(scenario, "local-retry-restored-history");
                await status.ScreenshotAsync(new() { Path = Path.Combine(scenario.ArtifactDirectory, "local-retry-restored-panel.png") });
                await File.WriteAllTextAsync(Path.Combine(scenario.ArtifactDirectory, "evidence.json"),
                    JsonSerializer.Serialize(new { mode, failure, before, restored }, Json), ScenarioCancellation.Token);
                await SaveErrorRequestsAsync(scenario, requestStart);
            }
            finally
            {
                proxy.ReleaseAllGates();
                await Stack.ResumeDatabaseAfterFailureAsync();
            }
        });
    }

    private Task SaveErrorRequestsAsync(BrowserScenario scenario, int start) =>
        File.WriteAllTextAsync(Path.Combine(scenario.ArtifactDirectory, "requests.json"),
            JsonSerializer.Serialize(Stack.ApiProxy.Requests.Skip(start), Json), ScenarioCancellation.Token);
}
