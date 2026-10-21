using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

public sealed class LocalAuthenticationSmokeTests
{
    [Theory]
    [InlineData(1440, 900)]
    [InlineData(1024, 768)]
    [InlineData(390, 844)]
    public async Task Local_login_users_attributes_passwords_and_isolation(int width, int height)
    {
        await using var stack = new OidcBrowserStack();
        await stack.StartAsync(usePkce: true, useLocal: true);
        await using var context = await stack.CreateContextAsync(width, height);
        var page = await context.NewPageAsync();
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, message) => errors.Enqueue(message);
        page.Console += (_, message) => { if (message.Type is "error" or "warning") errors.Enqueue(message.Text); };
        try
        {
            await page.GotoAsync(stack.UiOrigin + "/auth/login?returnUrl=/users");
            await Screenshot(page, stack, "local-login");
            await Login(page, "admin", "Local-browser-password-2026!");
            await Interactive(page);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Users", Exact = true })).ToBeVisibleAsync();
            await page.GotoAsync(stack.UiOrigin + "/user-attributes"); await Interactive(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Add attribute", Exact = true }).ClickAsync();
            await page.GetByLabel("Key", new() { Exact = true }).FillAsync("department");
            await page.GetByLabel("Label", new() { Exact = true }).FillAsync("Department");
            await page.GetByLabel("JWT claim name (optional)").FillAsync("depId");
            await page.GetByLabel("Required when creating or editing a user").CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Save attribute" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Attribute saved");
            await Screenshot(page, stack, "local-attributes");
            await page.GotoAsync(stack.UiOrigin + "/users"); await Interactive(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Add user", Exact = true }).ClickAsync();
            await page.GetByLabel("Username", new() { Exact = true }).FillAsync("alice");
            await page.GetByLabel("First name", new() { Exact = true }).FillAsync("Alice");
            await page.GetByLabel("Last name", new() { Exact = true }).FillAsync("Example");
            await page.GetByLabel("Password", new() { Exact = true }).FillAsync("Local-alice-password-2026!");
            await page.GetByLabel("Roles", new() { Exact = true }).FillAsync("Reviewer");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save user", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Add user", Exact = true })).ToBeVisibleAsync();
            await page.GetByLabel("Department *", new() { Exact = true }).FillAsync("Finance");
            await page.GetByRole(AriaRole.Button, new() { Name = "Add claim", Exact = true }).ClickAsync();
            await page.GetByLabel("Claim name", new() { Exact = true }).FillAsync("team");
            await page.GetByLabel("Claim value", new() { Exact = true }).FillAsync("one");
            await Screenshot(page, stack, "local-user-form");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save user", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("User saved");
            var aliceRow = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "alice" });
            await aliceRow.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
            await page.GetByLabel("First name", new() { Exact = true }).FillAsync("Alicia");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save user", Exact = true }).ClickAsync();
            await Assertions.Expect(aliceRow).ToContainTextAsync("Alicia");
            await Screenshot(page, stack, "local-users");
            await using var other = await stack.CreateContextAsync(width, height);
            var alice = await other.NewPageAsync();
            await alice.GotoAsync(stack.UiOrigin + "/auth/login?returnUrl=/inbox");
            await Login(alice, "alice", "Local-alice-password-2026!"); await Interactive(alice);
            await Assertions.Expect(alice.Locator(".topbar-profile")).ToContainTextAsync("alice");
            await Assertions.Expect(page.Locator(".topbar-profile")).ToContainTextAsync("admin");
            var tokenPage = await other.NewPageAsync(); await tokenPage.GotoAsync(stack.UiOrigin + "/_info");
            using (var info = JsonDocument.Parse(await tokenPage.Locator("body").InnerTextAsync()))
            {
                var token = new JwtSecurityTokenHandler().ReadJwtToken(info.RootElement.GetProperty("accessToken").GetString());
                Assert.Equal("alice", token.Claims.Single(c => c.Type == "Username").Value);
                Assert.Equal("Finance", token.Claims.Single(c => c.Type == "depId").Value);
            }
            await tokenPage.CloseAsync();
            await aliceRow.GetByRole(AriaRole.Button, new() { Name = "Deactivate", Exact = true }).ClickAsync();
            await Assertions.Expect(aliceRow).ToContainTextAsync("Inactive");
            await alice.GotoAsync(stack.UiOrigin + "/inbox");
            await Assertions.Expect(alice.GetByRole(AriaRole.Heading, new() { Name = "Sign in", Exact = true })).ToBeVisibleAsync();
            await aliceRow.GetByRole(AriaRole.Button, new() { Name = "Activate", Exact = true }).ClickAsync();
            await aliceRow.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
            await page.GetByLabel("New password", new() { Exact = true }).FillAsync("Local-reset-password-2026!");
            await page.GetByRole(AriaRole.Button, new() { Name = "Reset password", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Password reset");
            await Login(alice, "alice", "Local-reset-password-2026!"); await Interactive(alice);
            await alice.GotoAsync(stack.UiOrigin + "/change-password"); await Interactive(alice);
            await alice.GetByLabel("Current password", new() { Exact = true }).FillAsync("Local-reset-password-2026!");
            await alice.GetByLabel("New password", new() { Exact = true }).FillAsync("Local-changed-password-2026!");
            await alice.GetByLabel("Confirm new password", new() { Exact = true }).FillAsync("Local-changed-password-2026!");
            await alice.GetByRole(AriaRole.Button, new() { Name = "Change password", Exact = true }).ClickAsync();
            await Assertions.Expect(alice.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Signed out");
            await page.Locator(".topbar-actions").GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Signed out");
            Assert.Empty(errors);
        }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(stack.RunDirectory, "local-console.txt"), errors);
            await Screenshot(page, stack, "local-final");
        }
    }
    private static async Task Login(IPage page, string user, string password)
    {
        await page.GetByLabel("Username", new() { Exact = true }).FillAsync(user);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).FocusAsync();
        await page.Keyboard.PressAsync("Enter");
    }
    private static Task Interactive(IPage page) => Assertions.Expect(page.Locator(".app-shell")).ToHaveAttributeAsync("data-interactive", "true");
    private static Task Screenshot(IPage page, OidcBrowserStack stack, string name) => page.ScreenshotAsync(new()
        { Path = Path.Combine(stack.RunDirectory, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
}
