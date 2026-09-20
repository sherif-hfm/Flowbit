using Flowbit.BrowserTests.Infrastructure;
using Flowbit.Shared.Dtos;
using Microsoft.Playwright;

namespace Flowbit.BrowserTests.Support;

/// <summary>
/// Shared setup helpers for runtime smoke scenarios: publish fixtures and start
/// instances through authenticated HTTP, reset the UI identity through the real
/// token screen, and capture setup IDs for browser assertions.
/// </summary>
internal static class RuntimeSupport
{
    public static Task WaitUntilInteractiveAsync(IPage page) =>
        Assertions.Expect(page.Locator(".app-shell")).ToHaveAttributeAsync(
            "data-interactive", "true", new LocatorAssertionsToHaveAttributeOptions { Timeout = 30_000 });

    public static async Task OpenInstanceFromListAsync(IPage page, long instanceId, string status)
    {
        await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = "Back to instances" }).ClickAsync();
        await Assertions.Expect(page.Locator("#instance-results-heading")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions { Name = status, Exact = true }).ClickAsync();
        await OpenFilterPanelAsync(page, "#instance-filter-fields");
        await page.Locator("#instance-id-filter").FillAsync(instanceId.ToString());
        await page.Keyboard.PressAsync("Tab");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply filters", Exact = true }).ClickAsync();
        await page.Locator($"a.data-title[href='instances/{instanceId}']").ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Level = 1 }))
            .ToContainTextAsync($"Instance #{instanceId}");
    }

    /// <summary>Publishes a runtime fixture and returns its workflow version.</summary>
    public static Task<WorkflowSummaryDto> PublishAsync(
        BrowserStackFixture stack, string fixtureName) =>
        stack.SetupClient.CreateAndPublishAsync(EditorInteractions.FixturePath(fixtureName));

    /// <summary>Starts an instance of a published workflow and returns its ID.</summary>
    public static Task<StartInstanceResultDto> StartInstanceAsync(
        BrowserStackFixture stack, long workflowId) =>
        stack.SetupClient.StartInstanceAsync(workflowId);

    /// <summary>
    /// Establishes the scenario identity through the real /token screen (clears
    /// any previous process identity first), returns an HTTP client bound to
    /// that explicit token, and registers a cleanup hook that clears the UI
    /// identity when the scenario disposes.
    /// </summary>
    public static async Task<(WorkflowFixtureClient Client, IdentityScreen Identity)> ApplyIdentityAsync(
        BrowserScenario scenario, string user, string[] roles)
    {
        var identity = new IdentityScreen(scenario.Page, scenario.Stack.UiBaseAddress);
        var token = await identity.GenerateAndApplyIdentityAsync(user, roles);
        scenario.OnCleanup(() => identity.ClearIdentityAsync());
        var client = scenario.Stack.CreateClient(user, roles, token, $"{scenario.Name}-http");
        return (client, identity);
    }

    /// <summary>The single active user-task work item of a normal task instance.</summary>
    public static async Task<long> SoleUserTaskIdAsync(
        WorkflowFixtureClient client, long instanceId)
    {
        var detail = await client.GetInstanceAsync(instanceId);
        return detail.UserTasks?.SoleUserTaskId
            ?? throw new InvalidOperationException(
                $"Instance {instanceId} has no sole active user task " +
                $"(active={detail.UserTasks?.ActiveCount ?? 0}, pending={detail.UserTasks?.PendingCount ?? 0}).");
    }

    /// <summary>
    /// Opens a page's collapsed filter panel through its real toggle. Blazor
    /// Server needs the circuit before clicks act, so the toggle is retried
    /// until the fields become visible.
    /// </summary>
    public static async Task OpenFilterPanelAsync(IPage page, string fieldsSelector)
    {
        var toggle = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Show filters" });
        var fields = page.Locator(fieldsSelector);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            ScenarioCancellation.Token.ThrowIfCancellationRequested();
            if (await fields.IsVisibleAsync())
            {
                return;
            }
            await toggle.ClickAsync();
            try
            {
                await fields.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 2_000f,
                });
                return;
            }
            catch (TimeoutException) when (attempt < 9)
            {
                // Circuit not interactive yet; probe again.
            }
        }
        throw new TimeoutException($"The filter panel {fieldsSelector} never opened.");
    }

    /// <summary>
    /// Clicks a button and waits for a server-driven effect, retrying until the
    /// Blazor circuit handles the click (used for the first action on a page).
    /// </summary>
    public static async Task ClickUntilAsync(
        Func<Task> action, Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastFailure = null;
        while (DateTime.UtcNow <= deadline)
        {
            ScenarioCancellation.Token.ThrowIfCancellationRequested();
            try
            {
                // Check first: a successful previous attempt may have removed
                // the clicked control (e.g. Claim becomes Unclaim).
                if (await condition())
                {
                    return;
                }
                await action();
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                lastFailure = failure;
            }
            await Task.Delay(1_000, ScenarioCancellation.Token);
        }
        throw new TimeoutException(
            $"A server-driven click effect was never confirmed within {timeout.TotalSeconds:0}s." +
            (lastFailure is null ? string.Empty : $" Last failure: {lastFailure.Message}"));
    }

    /// <summary>
    /// Clicks a button and waits for a server-driven effect, retrying until the
    /// Blazor circuit handles the click. The diagnose hook runs once with the
    /// final timeout failure so test authors can include HTTP ground truth.
    /// </summary>
    public static async Task ClickUntilAsync(
        Func<Task> action,
        Func<Task<bool>> condition,
        TimeSpan timeout,
        Func<Task<string>>? describeOnFailure)
    {
        try
        {
            await ClickUntilAsync(action, condition, timeout);
        }
        catch (TimeoutException failure)
        {
            var detail = string.Empty;
            if (describeOnFailure is not null)
            {
                try
                {
                    var diagnosticText = await describeOnFailure();
                    if (!string.IsNullOrWhiteSpace(diagnosticText))
                    {
                        detail = " HTTP ground truth: " + diagnosticText;
                    }
                }
                catch (Exception diagnosticFailure)
                {
                    detail = $" (diagnostics failed: {diagnosticFailure.Message})";
                }
            }
            throw new TimeoutException(failure.Message + detail, failure);
        }
    }
}
