using Flowbit.BrowserTests.Infrastructure;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests.Support;

/// <summary>
/// Drives the real development identity flow through the /token screen: clear
/// any previous identity, generate and apply a new one, verify the applied user
/// and roles in the rendered "Current identity" panel, and read back the raw
/// JWT for HTTP fixture clients.
/// </summary>
public sealed class IdentityScreen(IPage page, string uiBaseAddress)
{
    public const string FallbackAlertPrefix = "Your browser does not support direct file saving.";

    public IPage Page => page;

    public async Task<string> GenerateAndApplyIdentityAsync(string user, string[] roles)
    {
        await page.GotoAsync(new Uri(new Uri(uiBaseAddress), "/token").ToString(),
            new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await page.WaitForSelectorAsync("#token-user", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
        });

        await WaitUntilInteractiveAsync();

        // Clear any previous process-wide identity so the requested actor
        // starts from a known state.
        await ClearIfAppliedAsync();

        await page.Locator("#token-user").FillAsync(user);
        await page.Locator("#token-roles").FillAsync(string.Join(", ", roles));

        // Generate and apply; the identity panel must confirm the user.
        var generate = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Generate and apply" });
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ScenarioCancellation.Token.ThrowIfCancellationRequested();
            await generate.ClickAsync();
            try
            {
                await ExpectAppliedAsync(user, roles);
                return await page.Locator("#raw-token").InputValueAsync();
            }
            catch (Exception failure) when (
                attempt < 4 && failure is not OperationCanceledException)
            {
                // Not confirmed yet; click again.
            }
        }

        throw new TimeoutException(
            $"The /token page never confirmed the generated identity for '{user}'.");
    }

    private async Task ExpectAppliedAsync(string user, string[] roles)
    {
        var section = page.Locator("#current-token-heading")
            .Locator("xpath=ancestor::section");
        await Assertions.Expect(section.Locator(".status-badge")).ToContainTextAsync("Applied");

        var userSummary = page.Locator(".summary-item", new PageLocatorOptions { HasText = "User" })
            .First;
        await Assertions.Expect(userSummary).ToContainTextAsync(user);

        if (roles.Length > 0)
        {
            var rolesSummary = page.Locator(".summary-item", new PageLocatorOptions { HasText = "Roles" })
                .First;
            await Assertions.Expect(rolesSummary).ToContainTextAsync(roles[^1]);
        }
    }

    public async Task ExpectNotAppliedAsync()
    {
        var section = page.Locator("#current-token-heading")
            .Locator("xpath=ancestor::section");
        await Assertions.Expect(section.Locator(".status-badge")).ToContainTextAsync(
            "Not set",
            new LocatorAssertionsToContainTextOptions { Timeout = 10_000f });
    }

    public async Task ClearIdentityAsync()
    {
        await page.GotoAsync(new Uri(new Uri(uiBaseAddress), "/token").ToString(),
            new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await page.WaitForSelectorAsync("#token-user", new PageWaitForSelectorOptions
        {
            State = WaitForSelectorState.Visible,
        });
        await WaitUntilInteractiveAsync();
        await ClearIfAppliedAsync();
    }

    private async Task WaitUntilInteractiveAsync()
    {
        await RuntimeSupport.WaitUntilInteractiveAsync(page);
        // Blazor Server re-renders from server state once the circuit is
        // interactive, wiping DOM changes made earlier. Probe interactivity by
        // clicking a suggestion chip and waiting for the server-driven value.
        // Recreate locators each attempt: prerender/hydration detaches nodes.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            ScenarioCancellation.Token.ThrowIfCancellationRequested();
            try
            {
                var rolesInput = page.Locator("#token-roles");
                var roleChip = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "+ User" });
                await roleChip.ClickAsync(new LocatorClickOptions { Timeout = 3_000f });
                await Assertions.Expect(rolesInput).ToHaveValueAsync(
                    "User",
                    new LocatorAssertionsToHaveValueOptions { Timeout = 5_000f });
                return;
            }
            catch (Exception failure) when (
                attempt < 9 &&
                failure is not OperationCanceledException)
            {
                // Circuit not interactive yet, or a prerender node was replaced.
            }
        }

        throw new TimeoutException("The /token page never became interactive.");
    }

    private async Task ClearIfAppliedAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ScenarioCancellation.Token.ThrowIfCancellationRequested();
            try
            {
                var clearButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Clear identity" });
                if (!await clearButton.IsEnabledAsync())
                {
                    await ExpectNotAppliedAsync();
                    return;
                }

                await clearButton.ClickAsync(new LocatorClickOptions { Timeout = 3_000f });
                await ExpectNotAppliedAsync();
                return;
            }
            catch (Exception failure) when (
                attempt < 4 && failure is not OperationCanceledException)
            {
                // Circuit click not yet applied, or a prerender node was replaced.
            }
        }

        throw new TimeoutException("Clear identity never reached the Not set state.");
    }
}
