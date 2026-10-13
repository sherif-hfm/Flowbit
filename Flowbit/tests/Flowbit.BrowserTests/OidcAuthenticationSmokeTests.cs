using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserTests;

public sealed class OidcAuthenticationSmokeTests
{
    [Fact]
    public async Task O4_TrustedTlsProxyPreservesPublicCallbacksReloadAndRealCircuitReconnect()
    {
        await using var stack = new OidcBrowserStack();
        await stack.StartAsync(usePkce: true, useTrustedProxy: true);
        Assert.StartsWith("http://", stack.UiBackendOrigin);
        Assert.StartsWith("https://", stack.UiOrigin);
        await using var context = await stack.CreateContextAsync(1024, 768);
        var errors = new ConcurrentQueue<string>();
        var warnings = new ConcurrentQueue<string>();
        var expectedDisconnects = new ConcurrentQueue<string>();
        var expectTransportDrop = false;
        var page = await context.NewPageAsync();
        page.PageError += (_, message) => errors.Enqueue(message);
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                if (expectTransportDrop && message.Text.Contains("Connection disconnected", StringComparison.Ordinal)
                    && message.Text.Contains("WebSocket closed", StringComparison.Ordinal)) expectedDisconnects.Enqueue(message.Text);
                else errors.Enqueue(message.Text);
            }
            if (message.Type == "warning") warnings.Enqueue(message.Text);
        };
        var logoutRedirect = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Request += (_, request) =>
        {
            if (request.Url.StartsWith(stack.Provider.Authority + "/logout?", StringComparison.Ordinal))
                logoutRedirect.TrySetResult(Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(request.Url).Query)
                    ["post_logout_redirect_uri"].ToString());
        };
        try
        {
            // An unauthenticated operational route challenges through the public
            // TLS proxy. The IdP receives the HTTPS callback, never the HTTP backend.
            await page.GotoAsync(stack.UiOrigin + "/inbox");
            var authorize = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(page.Url).Query);
            Assert.Equal(stack.UiOrigin + "/signin-oidc", authorize["redirect_uri"].ToString());
            Assert.Equal("S256", authorize["code_challenge_method"].ToString());
            await page.GetByLabel("User", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync("alice");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continue", Exact = true }).ClickAsync();
            await WaitInteractiveAsync(page);
            await Assertions.Expect(page.Locator(".topbar-profile")).ToContainTextAsync("alice");
            Assert.Equal(stack.UiOrigin + "/inbox", page.Url);
            var cookie = Assert.Single(await context.CookiesAsync(), candidate => candidate.Name == "__Host-Flowbit.Session");
            Assert.True(cookie.HttpOnly && cookie.Secure);
            Assert.Single(stack.Provider.CodeExchanges);

            await page.ReloadAsync();
            await WaitInteractiveAsync(page);
            await Assertions.Expect(page.Locator(".topbar-profile")).ToContainTextAsync("alice");
            Assert.Single(stack.Provider.CodeExchanges);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Show filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Hide filters", Exact = true })).ToBeVisibleAsync();

            var connectionsBefore = stack.Proxy!.WebSocketConnections;
            Assert.True(connectionsBefore > 0);
            expectTransportDrop = true;
            stack.Proxy.DropWebSocketConnections();
            await WaitUntilAsync(() => stack.Proxy.WebSocketConnections > connectionsBefore,
                "Blazor did not open a replacement WebSocket through the proxy.");
            await Assertions.Expect(page.Locator("#components-reconnect-modal")).Not.ToBeVisibleAsync();
            expectTransportDrop = false;
            // An open filter panel survives the circuit transport break; the
            // next server-side button action proves the reconnected circuit works.
            await Assertions.Expect(page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Hide filters", Exact = true })).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Hide filters", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Show filters", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".topbar-profile")).ToContainTextAsync("alice");
            Assert.Single(stack.Provider.CodeExchanges);
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(stack.RunDirectory, "proxy-reconnected.png"), FullPage = true,
                Animations = ScreenshotAnimations.Disabled
            });
            var additionalTab = await context.NewPageAsync();
            await additionalTab.GotoAsync(stack.UiOrigin + "/inbox");
            await WaitInteractiveAsync(additionalTab);
            await Assertions.Expect(additionalTab.Locator(".topbar-profile")).ToContainTextAsync("alice");
            Assert.Single(stack.Provider.CodeExchanges);
            await additionalTab.CloseAsync();

            await page.Locator(".topbar-actions").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Sign out", Exact = true }).ClickAsync();
            Assert.Equal(stack.UiOrigin + "/signout-callback-oidc", await logoutRedirect.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            await Assertions.Expect(page.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Signed out");
            Assert.Equal(stack.UiOrigin + "/auth/signed-out", page.Url);
            Assert.DoesNotContain(await context.CookiesAsync(), candidate => candidate.Name == "__Host-Flowbit.Session");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(stack.RunDirectory, "proxy-signed-out.png"), FullPage = true
            });
            Assert.Empty(errors);
        }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(stack.RunDirectory, "console-errors.txt"), errors);
            await File.WriteAllLinesAsync(Path.Combine(stack.RunDirectory, "console-warnings.txt"), warnings);
            await File.WriteAllLinesAsync(Path.Combine(stack.RunDirectory, "expected-disconnect-errors.txt"), expectedDisconnects);
        }
    }

    [Fact]
    public async Task O3_AiStreamSurvivesRefreshAndClearsOnSessionLossWithoutReplacingEditor()
    {
        await using var stack = new OidcBrowserStack();
        await stack.StartAsync(usePkce: true, enableAi: true);
        await using var context = await stack.CreateContextAsync();
        var errors = new ConcurrentQueue<string>();
        var warnings = new ConcurrentQueue<string>();
        var editor = await ObservePageAsync(context, errors, warnings);
        editor.Dialog += async (_, dialog) =>
        {
            if (dialog.Type == "confirm" && dialog.Message.StartsWith("Replace this workflow?", StringComparison.Ordinal))
                await dialog.AcceptAsync();
            else
            {
                errors.Enqueue("Unexpected dialog: " + dialog.Type + ": " + dialog.Message);
                await dialog.DismissAsync();
            }
        };
        try
        {
            await LoginAsync(editor, stack, "alice", "/workflows/new");
            await Assertions.Expect(editor.Locator(".workflow-editor-page")).ToHaveAttributeAsync("data-editor-ready", "true");
            await EditorInteractions.LoadWorkflowAsync(editor, EditorInteractions.FixturePath("editor-basic.json"));
            await editor.Locator("#wfName").FillAsync("Unsaved AI OIDC draft");
            await editor.Locator("#open-ai-assistant").ClickAsync();
            await Assertions.Expect(editor.Locator("#ai-provider")).ToHaveValueAsync("opencode-go");
            const string providerKey = "synthetic-oidc-ai-provider-key";
            await editor.Locator("#ai-key").FillAsync(providerKey);
            stack.AiProvider.EnqueueNameEdit("Proposal after token refresh");
            var refreshingStream = stack.AiProvider.EnqueueFinish(delayed: true);
            await editor.Locator("#ai-message").FillAsync("Rename this workflow Proposal after token refresh, keeping its review rules.");
            await editor.Locator("#ai-send").ClickAsync();
            await refreshingStream.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assertions.Expect(editor.Locator("#ai-draft-progress")).ToContainTextAsync("1 completed draft step");
            var retainedProgress = await editor.Locator("#ai-draft-progress").InnerTextAsync();

            var otherTab = await ObservePageAsync(context, errors, warnings);
            await otherTab.GotoAsync(stack.UiOrigin + "/inbox");
            await WaitInteractiveAsync(otherTab);
            var refreshes = stack.Provider.RefreshCount("alice");
            var refreshDue = stack.Provider.AccessTokenExpiresAt("alice").AddSeconds(-59);
            if (refreshDue > DateTimeOffset.UtcNow) await Task.Delay(refreshDue - DateTimeOffset.UtcNow);
            await otherTab.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh inbox" }).ClickAsync();
            await WaitUntilAsync(() => stack.Provider.RefreshCount("alice") > refreshes);
            await Assertions.Expect(editor.Locator("#ai-key")).ToHaveValueAsync(providerKey);
            await Assertions.Expect(editor.Locator("#ai-draft-progress")).ToHaveTextAsync(retainedProgress);
            await Assertions.Expect(editor.Locator("#ai-model-wait")).ToBeVisibleAsync();
            Assert.False(refreshingStream.Cancelled.Task.IsCompleted);
            await Assertions.Expect(editor.Locator("#wfName")).ToHaveValueAsync("Unsaved AI OIDC draft");
            await editor.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(stack.RunDirectory, "ai-stream-after-refresh.png"), FullPage = true
            });
            refreshingStream.Complete();
            await Assertions.Expect(editor.Locator("#ai-apply")).ToBeEnabledAsync();
            await Assertions.Expect(editor.Locator("#ai-key")).ToHaveValueAsync(providerKey);
            await Assertions.Expect(editor.Locator("#wfName")).ToHaveValueAsync("Unsaved AI OIDC draft");

            stack.AiProvider.EnqueueNameEdit("Cancelled private draft");
            var revokedStream = stack.AiProvider.EnqueueFinish(delayed: true);
            await editor.Locator("#ai-message").FillAsync("Rename this workflow Cancelled private draft, keeping its review rules.");
            await editor.Locator("#ai-send").ClickAsync();
            await revokedStream.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Assertions.Expect(editor.Locator("#ai-model-wait")).ToBeVisibleAsync();
            await otherTab.Locator(".topbar-actions").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Sign out", Exact = true }).ClickAsync();
            await Assertions.Expect(otherTab.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Signed out");
            await revokedStream.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            revokedStream.Complete();
            await Assertions.Expect(editor.Locator(".workflow-ai-panel")).ToHaveCountAsync(0);
            await Assertions.Expect(editor.Locator("#ai-key, #ai-apply, #ai-continue")).ToHaveCountAsync(0);
            await Assertions.Expect(editor.Locator("#wfName")).ToHaveValueAsync("Unsaved AI OIDC draft");
            await Assertions.Expect(editor.Locator("#editor-status")).ToHaveTextAsync("Unsaved changes");
            await Assertions.Expect(editor.Locator("#save-workflow-version")).ToBeDisabledAsync();
            await editor.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(stack.RunDirectory, "ai-cancelled-editor-retained.png"), FullPage = true
            });
            await LoginAsync(otherTab, stack, "alice", "/workflows/new");
            await Assertions.Expect(otherTab.Locator(".workflow-editor-page")).ToHaveAttributeAsync("data-editor-ready", "true");
            await otherTab.Locator("#open-ai-assistant").ClickAsync();
            await Assertions.Expect(otherTab.Locator("#ai-key")).ToHaveValueAsync("");
            await Assertions.Expect(otherTab.Locator("#ai-apply, #ai-continue, .ai-message")).ToHaveCountAsync(0);
            Assert.All(stack.AiProvider.Requests, request => Assert.Equal(AiProviderTestHost.HashKey(providerKey), request.KeyHash));
            Assert.Empty(errors);
        }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(stack.RunDirectory, "console-errors.txt"), errors);
            await File.WriteAllLinesAsync(Path.Combine(stack.RunDirectory, "console-warnings.txt"), warnings);
        }
    }

    [Fact]
    public async Task O2_ProtocolFailuresAndRevokedRefreshTokenNeverGrantUiAccess()
    {
        await using var stack = new OidcBrowserStack();
        await stack.StartAsync(usePkce: true);
        foreach (var failure in new[] { OidcProviderTestHost.AuthorizationFailure.InvalidState,
            OidcProviderTestHost.AuthorizationFailure.InvalidNonce, OidcProviderTestHost.AuthorizationFailure.WrongIssuer })
        {
            var viewport = failure switch
            {
                OidcProviderTestHost.AuthorizationFailure.InvalidNonce => (Width: 1024, Height: 768),
                OidcProviderTestHost.AuthorizationFailure.WrongIssuer => (Width: 390, Height: 844),
                _ => (Width: 1440, Height: 900)
            };
            await using var context = await stack.CreateContextAsync(viewport.Width, viewport.Height);
            var page = await context.NewPageAsync();
            stack.Provider.FailNextAuthorization(failure);
            await BeginLoginAsync(page, stack, "alice", "/inbox");
            await Assertions.Expect(page.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Sign-in unavailable");
            Assert.DoesNotContain(await context.CookiesAsync(), cookie => cookie.Name == "__Host-Flowbit.Session");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(stack.RunDirectory, "failure-" + failure + ".png"), FullPage = true
            });
        }

        await using var signedIn = await stack.CreateContextAsync();
        var signedInPage = await signedIn.NewPageAsync();
        // An external return URL is reduced to the local overview.
        await LoginAsync(signedInPage, stack, "alice", "https://example.invalid/untrusted-return");
        Assert.Equal(stack.UiOrigin + "/", signedInPage.Url);
        var authorization = stack.Provider.LastAuthorizationResponse!;
        await using var replay = await stack.CreateContextAsync();
        var replayPage = await replay.NewPageAsync();
        await replayPage.GotoAsync(stack.UiOrigin + "/signin-oidc?code=" + Uri.EscapeDataString(authorization.Code)
            + "&state=" + Uri.EscapeDataString(authorization.State));
        await Assertions.Expect(replayPage.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Sign-in unavailable");
        Assert.DoesNotContain(await replay.CookiesAsync(), cookie => cookie.Name == "__Host-Flowbit.Session");

        await signedInPage.GotoAsync(stack.UiOrigin + "/inbox");
        await WaitInteractiveAsync(signedInPage);
        stack.Provider.RevokeRefreshTokens("alice");
        var refreshDue = stack.Provider.AccessTokenExpiresAt("alice").AddSeconds(-59);
        if (refreshDue > DateTimeOffset.UtcNow) await Task.Delay(refreshDue - DateTimeOffset.UtcNow);
        await signedInPage.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh inbox" }).ClickAsync();
        await Assertions.Expect(signedInPage.GetByRole(AriaRole.Heading,
            new PageGetByRoleOptions { Name = "Sign in to Flowbit", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(signedInPage.Locator(".topbar-actions").GetByRole(AriaRole.Link,
            new LocatorGetByRoleOptions { Name = "Sign in", Exact = true })).ToBeVisibleAsync();
        await signedInPage.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(stack.RunDirectory, "refresh-revoked.png"), FullPage = true
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task O1_RealLoginRefreshIsolationAndLogoutPreserveUnsavedEditor(bool usePkce)
    {
        await using var stack = new OidcBrowserStack();
        await stack.StartAsync(usePkce);
        await using var alice = await stack.CreateContextAsync();
        await using var bob = await stack.CreateContextAsync(390, 844);
        var errors = new ConcurrentQueue<string>();
        var warnings = new ConcurrentQueue<string>();
        var alicePage = await ObservePageAsync(alice, errors, warnings);
        var bobPage = await ObservePageAsync(bob, errors, warnings);
        var expectLeavePrompt = false;
        var leavePromptObserved = false;
        var acceptLeavePrompt = false;
        alicePage.Dialog += async (_, dialog) =>
        {
            if (expectLeavePrompt && (dialog.Type == "beforeunload"
                || dialog.Type == "confirm" && dialog.Message == "Leave the editor? Unsaved changes will be lost."))
            {
                leavePromptObserved = true;
                if (acceptLeavePrompt) await dialog.AcceptAsync();
                else await dialog.DismissAsync();
            }
            else if (dialog.Type == "confirm" && dialog.Message.StartsWith("Replace this workflow?", StringComparison.Ordinal)
                || dialog.Type == "alert" && dialog.Message.StartsWith(IdentityScreen.FallbackAlertPrefix, StringComparison.Ordinal))
                await dialog.AcceptAsync();
            else
            {
                errors.Enqueue("Unexpected dialog: " + dialog.Type + ": " + dialog.Message);
                await dialog.DismissAsync();
            }
        };
        try
        {
            // Both identities authenticate through actual browser redirects,
            // signed ID/access tokens, middleware validation, and API actor resolution.
            await LoginAsync(alicePage, stack, "alice", "/inbox");
            await LoginAsync(bobPage, stack, "bob", "/inbox");
            await Assertions.Expect(alicePage.Locator(".topbar-profile")).ToContainTextAsync("alice");
            await Assertions.Expect(bobPage.Locator(".topbar-profile")).ToContainTextAsync("bob");
            Assert.Equal(new[] { usePkce, usePkce }, stack.Provider.CodeExchanges);
            await AssertApiActorAsync(stack, "alice");
            await AssertApiActorAsync(stack, "bob");

            var aliceCookie = Assert.Single(await alice.CookiesAsync(), cookie => cookie.Name == "__Host-Flowbit.Session");
            var bobCookie = Assert.Single(await bob.CookiesAsync(), cookie => cookie.Name == "__Host-Flowbit.Session");
            Assert.True(aliceCookie.HttpOnly);
            Assert.True(aliceCookie.Secure);
            Assert.Equal(SameSiteAttribute.Lax, aliceCookie.SameSite);
            Assert.NotEqual(aliceCookie.Value, bobCookie.Value);
            Assert.DoesNotContain(stack.Provider.LastAccessToken("alice"), aliceCookie.Value, StringComparison.Ordinal);
            Assert.Equal("{}", await alicePage.EvaluateAsync<string>("JSON.stringify({...localStorage})"));
            Assert.Equal("{}", await alicePage.EvaluateAsync<string>("JSON.stringify({...sessionStorage})"));
            Assert.DoesNotContain(stack.Provider.LastAccessToken("alice"), await alicePage.ContentAsync(), StringComparison.Ordinal);
            var forbiddenTokenPage = await alice.APIRequest.GetAsync("/token");
            Assert.Equal(404, forbiddenTokenPage.Status);
            var missingAntiforgery = await alice.APIRequest.PostAsync("/auth/logout");
            Assert.Equal(400, missingAntiforgery.Status);

            await bobPage.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(stack.RunDirectory, "signed-in-mobile.png"), FullPage = true
            });
            await bobPage.SetViewportSizeAsync(1024, 768);
            await Assertions.Expect(bobPage.Locator(".topbar-actions").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Sign out", Exact = true })).ToBeVisibleAsync();
            await bobPage.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(stack.RunDirectory, "signed-in-tablet.png"), FullPage = true,
                Animations = ScreenshotAnimations.Disabled
            });
            await alicePage.GotoAsync(stack.UiOrigin + "/workflows/new");
            await Assertions.Expect(alicePage.Locator(".workflow-editor-page")).ToHaveAttributeAsync("data-editor-ready", "true");
            await EditorInteractions.LoadWorkflowAsync(alicePage, EditorInteractions.FixturePath("editor-basic.json"));
            var editorName = alicePage.Locator("#workflow-editor-root #wfName");
            await editorName.FillAsync("Keep unsaved OIDC draft");
            await Assertions.Expect(alicePage.Locator("#editor-status")).ToHaveTextAsync("Unsaved changes");

            // A second circuit in Alice's cookie session refreshes tokens while
            // her editor remains mounted. The provider rotates one-use refresh tokens.
            var secondTab = await ObservePageAsync(alice, errors, warnings);
            await secondTab.GotoAsync(stack.UiOrigin + "/inbox");
            await WaitInteractiveAsync(secondTab);
            var refreshes = stack.Provider.RefreshCount("alice");
            var refreshDue = stack.Provider.AccessTokenExpiresAt("alice").AddSeconds(-59);
            if (refreshDue > DateTimeOffset.UtcNow) await Task.Delay(refreshDue - DateTimeOffset.UtcNow);
            await secondTab.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh inbox" }).ClickAsync();
            await WaitUntilAsync(() => stack.Provider.RefreshCount("alice") > refreshes);
            await Assertions.Expect(editorName).ToHaveValueAsync("Keep unsaved OIDC draft");
            await Assertions.Expect(alicePage.Locator("#editor-status")).ToHaveTextAsync("Unsaved changes");
            await Assertions.Expect(alicePage.Locator("#save-workflow-version")).ToBeEnabledAsync();
            await Assertions.Expect(bobPage.Locator(".topbar-profile")).ToContainTextAsync("bob");

            await alicePage.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(stack.RunDirectory, "editor-after-refresh.png"), FullPage = true
            });
            // Submitting the real logout form from a dirty editor also warns;
            // cancelling must preserve the current login and local document.
            expectLeavePrompt = true;
            await alicePage.Locator(".topbar-actions").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Sign out", Exact = true }).ClickAsync();
            await WaitUntilAsync(() => leavePromptObserved, "The logout form did not warn before leaving the dirty editor.");
            await Assertions.Expect(editorName).ToHaveValueAsync("Keep unsaved OIDC draft");
            await Assertions.Expect(alicePage.Locator(".topbar-profile")).ToContainTextAsync("alice");
            expectLeavePrompt = false;
            leavePromptObserved = false;
            // Browser form, real antiforgery token, local revocation, provider
            // logout and signed-out callback; Alice's existing editor is retained.
            var signOut = secondTab.Locator(".topbar-actions").GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Sign out", Exact = true });
            await signOut.FocusAsync();
            await Assertions.Expect(signOut).ToBeFocusedAsync();
            await secondTab.Keyboard.PressAsync("Enter");
            await Assertions.Expect(secondTab.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Signed out");
            await secondTab.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(stack.RunDirectory, "signed-out-desktop.png"), FullPage = true
            });
            await secondTab.SetViewportSizeAsync(390, 844);
            await secondTab.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(stack.RunDirectory, "signed-out-mobile.png"), FullPage = true
            });
            await Assertions.Expect(alicePage.GetByRole(AriaRole.Alert).Filter(new LocatorFilterOptions
                { HasText = "Your session has expired" }).First).ToBeVisibleAsync();
            await Assertions.Expect(editorName).ToHaveValueAsync("Keep unsaved OIDC draft");
            await Assertions.Expect(alicePage.Locator("#save-workflow-version")).ToBeDisabledAsync();
            await Assertions.Expect(alicePage.Locator("#open-ai-assistant")).ToBeDisabledAsync();
            await Assertions.Expect(alicePage.Locator(".topbar-actions").GetByRole(AriaRole.Link,
                new LocatorGetByRoleOptions { Name = "Sign in", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(alicePage.Locator("#workflow-editor-root #fileMenuSummary")).ToBeEnabledAsync();
            await Assertions.Expect(bobPage.Locator(".topbar-profile")).ToContainTextAsync("bob");
            await bobPage.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Refresh inbox" }).ClickAsync();
            await Assertions.Expect(bobPage.Locator(".topbar-profile")).ToContainTextAsync("bob");
            await alicePage.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(stack.RunDirectory, "editor-after-logout.png"), FullPage = true
            });
            var download = await alicePage.RunAndWaitForDownloadAsync(async () =>
            {
                await alicePage.Locator("#fileMenuSummary").ClickAsync();
                await alicePage.Locator("#saveBtn").ClickAsync();
            });
            var exportPath = Path.Combine(stack.RunDirectory, "retained-unsaved-workflow.json");
            await download.SaveAsAsync(exportPath);
            using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(exportPath));
            Assert.Equal("Keep unsaved OIDC draft", exported.RootElement.GetProperty("name").GetString());
            Assert.Equal(4, exported.RootElement.GetProperty("flowNodes").GetArrayLength());
            expectLeavePrompt = true;
            await alicePage.Locator(".topbar-actions").GetByRole(AriaRole.Link,
                new LocatorGetByRoleOptions { Name = "Sign in", Exact = true }).ClickAsync();
            await WaitUntilAsync(() => leavePromptObserved, "The editor did not present its unsaved-work navigation warning.");
            await Assertions.Expect(editorName).ToHaveValueAsync("Keep unsaved OIDC draft");
            acceptLeavePrompt = true;
            await alicePage.Locator(".topbar-actions").GetByRole(AriaRole.Link,
                new LocatorGetByRoleOptions { Name = "Sign in", Exact = true }).ClickAsync();
            await Assertions.Expect(alicePage.GetByLabel("User", new PageGetByLabelOptions { Exact = true })).ToBeVisibleAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(stack.RunDirectory, "console-errors.txt"), errors);
            await File.WriteAllLinesAsync(Path.Combine(stack.RunDirectory, "console-warnings.txt"), warnings);
            await File.WriteAllTextAsync(Path.Combine(stack.RunDirectory, "provider-outcomes.json"), JsonSerializer.Serialize(new
            {
                codeExchanges = stack.Provider.CodeExchanges,
                aliceRefreshes = stack.Provider.RefreshCount("alice"), bobRefreshes = stack.Provider.RefreshCount("bob"),
                browser = stack.BrowserVersion, viewports = new[] { "1440x900", "1024x768", "390x844" }
            }));
        }
    }

    private static async Task<IPage> ObservePageAsync(IBrowserContext context, ConcurrentQueue<string> errors, ConcurrentQueue<string> warnings)
    {
        var page = await context.NewPageAsync();
        page.PageError += (_, message) => errors.Enqueue(message);
        page.Console += (_, message) =>
        {
            if (message.Type == "error") errors.Enqueue(message.Text);
            if (message.Type == "warning") warnings.Enqueue(message.Text);
        };
        return page;
    }

    private static async Task LoginAsync(IPage page, OidcBrowserStack stack, string user, string returnUrl)
    {
        await BeginLoginAsync(page, stack, user, returnUrl);
        await Assertions.Expect(page.Locator(".topbar-profile")).ToContainTextAsync(user);
        await WaitInteractiveAsync(page);
    }

    private static async Task BeginLoginAsync(IPage page, OidcBrowserStack stack, string user, string returnUrl)
    {
        await page.GotoAsync(stack.UiOrigin + "/auth/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        await page.GetByLabel("User", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync(user);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Continue", Exact = true }).FocusAsync();
        await page.Keyboard.PressAsync("Enter");
    }

    private static async Task WaitInteractiveAsync(IPage page) =>
        await Assertions.Expect(page.Locator(".app-shell")).ToHaveAttributeAsync("data-interactive", "true");

    private static async Task AssertApiActorAsync(OidcBrowserStack stack, string user)
    {
        using var client = new HttpClient { BaseAddress = new Uri(stack.ApiOrigin) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stack.Provider.LastAccessToken(user));
        using var response = await client.GetAsync("/api/auth/context");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(user, json.RootElement.GetProperty("user").GetString());
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string timeoutMessage = "The provider did not observe a refresh-token exchange.")
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException(timeoutMessage);
            await Task.Delay(100);
        }
    }
}
