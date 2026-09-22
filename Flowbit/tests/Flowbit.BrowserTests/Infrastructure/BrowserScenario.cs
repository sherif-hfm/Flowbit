using System.Text;
using System.Text.Json;
using Flowbit.BrowserTests.Support;
using Microsoft.Playwright;

namespace Flowbit.BrowserTests.Infrastructure;

/// <summary>
/// Owns one browser context, its pages, trace, console/error capture, and
/// failure artifacts. Scenarios never reuse cookies or local storage, and each
/// registers its page-error, console, failed-request, and dialog listeners
/// before navigation.
/// </summary>
public sealed class BrowserScenario : IAsyncDisposable
{
    /// <summary>
    /// Narrow, documented console.error prefixes that are recorded as warnings
    /// instead of failing the scenario. Keep empty unless a rerun produces a
    /// justified Blazor/Playwright noise string.
    /// </summary>
    private static readonly string[] AllowedConsoleErrorPrefixes = [];

    private readonly BrowserStackFixture fixture;
    private readonly List<string> consoleIssues = [];
    private readonly List<string> failedRequests = [];
    private readonly List<string> pageErrors = [];
    private readonly List<string> unexpectedDialogs = [];
    private readonly List<string> warnings = [];
    private readonly List<string> downloads = [];
    private readonly string scenarioDirectory;
    private readonly (TimeSpan Navigation, TimeSpan Assertion, TimeSpan Scenario, TimeSpan Teardown) timeouts;
    private IPage? page;
    private EventHandler<IDialog> dialogListener = null!;
    private EventHandler<IConsoleMessage> consoleListener = null!;
    private EventHandler<IResponse> responseListener = null!;
    private EventHandler<string> pageErrorListener = null!;
    private EventHandler<IDownload> downloadListener = null!;
    private List<string> expectedDialogPrefixes = [];
    private readonly Queue<ExpectedDialogResponse> expectedDialogQueue = new();
    private readonly List<string> dialogResponses = [];
    private Func<Task>? cleanupHook;
    private Exception? cleanupFailure;
    private bool bodySucceeded;
    private bool tracingStopped;
    private bool disposed;
    private bool contextClosed;
    private Task? runningBody;
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<IPage> observedPages = [];

    internal BrowserScenario(
        BrowserStackFixture fixture,
        IBrowserContext context,
        string name,
        string scenarioDirectory,
        TimeSpan navigationTimeout,
        TimeSpan assertionTimeout,
        TimeSpan scenarioTimeout,
        TimeSpan teardownTimeout)
    {
        this.fixture = fixture;
        Context = context;
        Name = name;
        this.scenarioDirectory = scenarioDirectory;
        timeouts = (navigationTimeout, assertionTimeout, scenarioTimeout, teardownTimeout);
    }

    public string Name { get; }

    /// <summary>The owning browser stack fixture (for UI base address, clients).</summary>
    public BrowserStackFixture Stack => fixture;

    public string ArtifactDirectory => scenarioDirectory;

    /// <summary>Path of the last downloaded save artifact (editor scenarios).</summary>
    public string? LastSavedFile { get; set; }

    public IBrowserContext Context { get; }

    public IPage Page => page ?? throw new InvalidOperationException("The scenario page was not prepared.");

    public TimeSpan NavigationTimeout => timeouts.Navigation;

    public TimeSpan AssertionTimeout => timeouts.Assertion;

    public TimeSpan ScenarioBudget => timeouts.Scenario;

    /// <summary>Console errors recorded so far (snapshot; not drained).</summary>
    public IReadOnlyList<string> ConsoleIssues
    {
        get { lock (consoleIssues) return [.. consoleIssues]; }
    }

    /// <summary>Failed requests (HTTP 400+) recorded so far.</summary>
    public IReadOnlyList<string> FailedRequests
    {
        get { lock (failedRequests) return [.. failedRequests]; }
    }

    /// <summary>Uncaught page errors recorded so far.</summary>
    public IReadOnlyList<string> PageErrors
    {
        get { lock (pageErrors) return [.. pageErrors]; }
    }

    /// <summary>Dialog texts (dialogs not matching an expected handler).</summary>
    public IReadOnlyList<string> UnexpectedDialogs
    {
        get { lock (unexpectedDialogs) return [.. unexpectedDialogs]; }
    }

    /// <summary>Consumed and dismissed dialog responses recorded so far.</summary>
    public IReadOnlyList<string> DialogResponses
    {
        get { lock (dialogResponses) return [.. dialogResponses]; }
    }

    /// <summary>Warnings recorded during the scenario (for diagnosis).</summary>
    public IReadOnlyList<string> Warnings
    {
        get { lock (warnings) return [.. warnings]; }
    }

    /// <summary>Download file names observed on the page so far.</summary>
    public IReadOnlyList<string> Downloads
    {
        get { lock (downloads) return [.. downloads]; }
    }

    /// <summary>
    /// Registers dialog prefixes the scenario expects (e.g. the editor's
    /// fallback-save alert). Dialogs matching a prefix are accepted; any other
    /// dialog is recorded as unexpected and fails the scenario.
    /// </summary>
    public void ExpectDialogStartingWith(params string[] prefixes)
    {
        expectedDialogPrefixes = [.. prefixes];
    }

    /// <summary>
    /// Queues a one-shot exact dialog expectation. The next dialog must match
    /// the exact dialog type and message; it is then accepted or dismissed per
    /// the flag. Queued expectations are checked before the fallback-prefix
    /// handling, and any unconsumed expectation fails the scenario after its
    /// body completes. Responses are recorded in the diagnostics.
    /// </summary>
    public void ExpectDialogOnce(string dialogType, string message, bool accept)
    {
        lock (expectedDialogQueue)
            expectedDialogQueue.Enqueue(new ExpectedDialogResponse(dialogType, message, accept));
    }

    private async Task HandleDialogAsync(IDialog dialog)
    {
        var text = dialog.Message ?? string.Empty;
        ExpectedDialogResponse? expected = null;
        lock (expectedDialogQueue)
        {
            if (expectedDialogQueue.Count > 0)
            {
                var head = expectedDialogQueue.Peek();
                if (head.Matches(dialog.Type, text))
                {
                    expected = head;
                    expectedDialogQueue.Dequeue();
                }
            }
        }
        if (expected is not null)
        {
            RecordDialogResponse(dialog.Type, expected.Accept ? "accepted" : "dismissed", text);
            if (expected.Accept)
            {
                await dialog.AcceptAsync();
            }
            else
            {
                await dialog.DismissAsync();
            }
            return;
        }
        if (expectedDialogPrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal)))
        {
            RecordDialogResponse(dialog.Type, "prefix accepted", text);
            await dialog.AcceptAsync();
            return;
        }
        lock (unexpectedDialogs) unexpectedDialogs.Add(text);
        RecordDialogResponse(dialog.Type, "dismissed unexpected", text);
        await dialog.DismissAsync();
    }

    private void RecordDialogResponse(string dialogType, string response, string text)
    {
        lock (dialogResponses) dialogResponses.Add($"{dialogType} {response}: {text}");
    }

    private sealed record ExpectedDialogResponse(string DialogType, string Message, bool Accept)
    {
        public bool Matches(string dialogType, string message) =>
            StringComparer.Ordinal.Equals(DialogType, dialogType) &&
            StringComparer.Ordinal.Equals(Message, message);
    }

    internal async Task PrepareAsync()
    {
        Directory.CreateDirectory(scenarioDirectory);

        consoleListener = (sender, message) =>
        {
            if (message.Type == "error")
            {
                var text = message.Text ?? string.Empty;
                if (AllowedConsoleErrorPrefixes.Any(prefix =>
                    text.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    lock (warnings) warnings.Add($"console.error (allowed): {text}");
                    return;
                }
                lock (consoleIssues) consoleIssues.Add($"console.error: {text}");
            }
            else if (message.Type == "warning")
            {
                lock (warnings) warnings.Add($"console.warn: {message.Text}");
            }
        };

        pageErrorListener = (sender, exception) =>
        {
            lock (pageErrors) pageErrors.Add($"pageerror: {exception}");
        };

        downloadListener = (sender, download) =>
        {
            lock (downloads) downloads.Add(download.SuggestedFilename ?? "(unnamed)");
        };

        responseListener = (sender, response) =>
        {
            if (response.Status >= 400)
            {
                lock (failedRequests) failedRequests.Add($"request failed: {response.Status} {response.Url}");
            }
        };

        dialogListener = (sender, dialog) =>
        {
            _ = HandleDialogAsync(dialog);
        };
        Context.SetDefaultTimeout((float)timeouts.Assertion.TotalMilliseconds);
        Context.SetDefaultNavigationTimeout((float)timeouts.Navigation.TotalMilliseconds);
        Context.Page += (_, openedPage) => ObservePage(openedPage);
        page = await Context.NewPageAsync();
        ObservePage(page);

        // Tracing starts before the tested navigation; successful scenarios
        // discard it while failures persist it under the scenario directory.
        await Context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true,
        });
    }

    private void ObservePage(IPage openedPage)
    {
        lock (observedPages)
        {
            if (observedPages.Contains(openedPage)) return;
            observedPages.Add(openedPage);
        }
        openedPage.Console += consoleListener;
        openedPage.PageError += pageErrorListener;
        openedPage.Download += downloadListener;
        openedPage.Response += responseListener;
        openedPage.Dialog += dialogListener;
        openedPage.RequestFailed += (_, request) =>
        {
            lock (failedRequests)
                failedRequests.Add($"transport failed: {request.Failure} {request.Method} {request.Url}");
        };
    }

    /// <summary>Opens the editor at the given base address on the scenario page.</summary>
    public async Task<IPage> OpenEditorAsync(string editorBaseAddress)
    {
        await Page.GotoAsync($"{editorBaseAddress}/", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        await Page.WaitForSelectorAsync("#svg", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached });
        await Page.WaitForSelectorAsync("#inspector", new PageWaitForSelectorOptions { State = WaitForSelectorState.Attached });
        return Page;
    }

    /// <summary>Navigates the scenario page to a UI path relative to the UI base.</summary>
    public async Task<IPage> OpenUiAsync(string path)
    {
        await Page.GotoAsync(new Uri(new Uri(fixture.UiBaseAddress), path).ToString(),
            new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        if (fixture.RequiresInteractiveMarkers) await RuntimeSupport.WaitUntilInteractiveAsync(Page);
        else await WaitForLegacyInteractivityAsync();
        if (fixture.RequiresInteractiveMarkers && System.Text.RegularExpressions.Regex.IsMatch(path, @"^/?instances/\d+(?:[?#].*)?$"))
            await Assertions.Expect(Page.Locator("section[aria-labelledby='instance-summary-heading']"))
                .ToHaveAttributeAsync("data-interactive", "true", new() { Timeout = 30_000 });
        return Page;
    }

    private async Task WaitForLegacyInteractivityAsync()
    {
        // Historical visual references predate the explicit readiness markers.
        // Prove a real server-side event round trip without modifying old HTML
        // or typing into an input that prerender hydration could overwrite.
        await Assertions.Expect(Page.Locator(".app-shell")).ToBeVisibleAsync();
        var viewport = Page.ViewportSize;
        try
        {
            if (viewport is null || viewport.Width > 390)
                await Page.SetViewportSizeAsync(390, viewport?.Height ?? 844);
            var toggle = Page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Open navigation", Exact = true });
            await RuntimeSupport.ClickUntilAsync(
                () => toggle.ClickAsync(),
                async () => await toggle.GetAttributeAsync("aria-expanded") == "true",
                TimeSpan.FromSeconds(30));
            var close = Page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Close navigation", Exact = true });
            // The full-screen scrim's center can sit behind the sidebar at
            // narrow widths. Keyboard activation targets its real button.
            await close.FocusAsync();
            await close.PressAsync("Enter");
            await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false", new() { Timeout = 10_000 });
        }
        finally
        {
            if (viewport is not null)
                await Page.SetViewportSizeAsync(viewport.Width, viewport.Height);
        }
    }

    /// <summary>Persists failure artifacts (screenshot, URL, logs, trace).</summary>
    public async Task CaptureFailureArtifactsAsync(string step, Exception failure)
    {
        try
        {
            if (!tracingStopped)
            {
                await Context.Tracing.StopAsync(new TracingStopOptions
                {
                    Path = Path.Combine(scenarioDirectory, "trace.zip"),
                });
                tracingStopped = true;
            }
        }
        catch
        {
        }
        try
        {
            if (page is not null && !page.IsClosed)
            {
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(scenarioDirectory, $"failure-{Sanitize(step)}.png"),
                    FullPage = true,
                });
            }
        }
        catch
        {
        }
        try
        {
            var viewport = page?.ViewportSize;
            var viewportText = viewport is null ? "unknown" : $"{viewport.Width}x{viewport.Height}";
            var log = new StringBuilder()
                .AppendLine($"Scenario: {Name}")
                .AppendLine($"Step: {step}")
                .AppendLine($"URL: {page?.Url}")
                .AppendLine($"Viewport: {viewportText}")
                .AppendLine($"Browser: Chromium {fixture.BrowserVersion}")
                .AppendLine()
                .AppendLine($"Failure: {failure}")
                .AppendLine("--- page errors ---").AppendLine(string.Join(Environment.NewLine, PageErrors))
                .AppendLine("--- console issues ---").AppendLine(string.Join(Environment.NewLine, ConsoleIssues))
                .AppendLine("--- failed requests ---").AppendLine(string.Join(Environment.NewLine, FailedRequests))
                .AppendLine("--- unexpected dialogs ---").AppendLine(string.Join(Environment.NewLine, UnexpectedDialogs))
                .AppendLine("--- downloads ---").AppendLine(string.Join(Environment.NewLine, Downloads))
                .AppendLine("--- warnings ---").AppendLine(string.Join(Environment.NewLine, Warnings))
                .ToString();
            await File.WriteAllTextAsync(Path.Combine(scenarioDirectory, "failure.log"), log, Encoding.UTF8);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Runs the scenario body within the scenario budget, capturing failure
    /// artifacts on any exception, then discards the trace on success. A
    /// successful body still fails when uncaught page errors, unexpected
    /// console errors, or unexpected dialogs were recorded.
    /// </summary>
    public async Task RunAsync(string step, Func<Task> body)
    {
        runningBody = ScenarioCancellation.RunAsync(body, cancellation.Token);
        try
        {
            try
            {
                await runningBody.WaitAsync(timeouts.Scenario);
            }
            catch (TimeoutException) when (!runningBody.IsCompleted)
            {
                cancellation.Cancel();
                await CaptureFailureArtifactsAsync(step, new TimeoutException("Scenario budget exceeded."));
                // Playwright operations do not accept cancellation tokens. Closing
                // the context interrupts them; HTTP and retries use the token.
                try
                {
                    await Context.CloseAsync().WaitAsync(timeouts.Teardown);
                    contextClosed = true;
                    await runningBody.WaitAsync(timeouts.Teardown);
                }
                catch (Exception) when (runningBody.IsCompleted)
                {
                    // The timeout remains the primary failure after draining.
                }
                catch (Exception)
                {
                    fixture.Invalidate("A timed-out scenario did not stop; remaining scenarios cannot share this stack.");
                    await fixture.DisposeAsync();
                    _ = runningBody.ContinueWith(task => _ = task.Exception,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    throw;
                }
                // Recreate no pages in a cancelled scenario. The next runtime
                // scenario must establish identity again through /token.
                throw new TimeoutException($"Scenario '{Name}' exceeded {timeouts.Scenario.TotalSeconds}s; outstanding work was stopped.");
            }
            AssertNoUnexpectedBrowserFailures();
            bodySucceeded = true;
        }
        catch (Exception failure)
        {
            await CaptureFailureArtifactsAsync(step, failure);
            throw;
        }
        finally
        {
            await WriteDiagnosticsAsync();
            try
            {
                if (!tracingStopped)
                {
                    await Context.Tracing.StopAsync();
                    tracingStopped = true;
                }
            }
            catch
            {
            }
        }
    }

    private Task WriteDiagnosticsAsync()
    {
        IPage[] pages;
        lock (observedPages)
            pages = observedPages.ToArray();

        return File.WriteAllTextAsync(
        Path.Combine(scenarioDirectory, "diagnostics.json"),
        JsonSerializer.Serialize(new
        {
            scenario = Name,
            browser = fixture.BrowserVersion,
            pages = pages.Select(observed => new { url = observed.Url, viewport = observed.ViewportSize }).ToArray(),
            pageErrors = PageErrors,
            consoleErrors = ConsoleIssues,
            warnings = Warnings,
            failedRequests = FailedRequests,
            unexpectedDialogs = UnexpectedDialogs,
            dialogResponses = DialogResponses
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Registers a per-scenario cleanup hook executed during disposal (e.g.
    /// clearing the UI identity before the context closes).
    /// </summary>
    public void OnCleanup(Func<Task> hook)
    {
        var previous = cleanupHook;
        cleanupHook = async () =>
        {
            if (previous is not null)
            {
                await previous();
            }
            await hook();
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        try
        {
            if (cleanupHook is not null && !contextClosed)
            {
                try
                {
                    await cleanupHook();
                }
                catch (Exception failure)
                {
                    cleanupFailure = failure;
                    await fixture.WriteSetupLogAsync(
                        $"Scenario '{Name}' identity cleanup failed: {failure}");
                    try
                    {
                        await File.AppendAllTextAsync(
                            Path.Combine(scenarioDirectory, "failure.log"),
                            $"{Environment.NewLine}--- cleanup failure ---{Environment.NewLine}{failure}{Environment.NewLine}",
                            Encoding.UTF8);
                    }
                    catch
                    {
                    }
                }
            }
            if (page is not null && !page.IsClosed)
            {
                try
                {
                    await page.CloseAsync();
                }
                catch
                {
                }
            }
            await Context.DisposeAsync();
        }
        catch
        {
        }
        await WriteDiagnosticsAsync();
        cancellation.Dispose();

        if (bodySucceeded && cleanupFailure is not null)
        {
            throw new InvalidOperationException(
                $"Scenario '{Name}' passed its assertions but identity cleanup failed. " +
                "The next scenario still establishes its own identity through /token.",
                cleanupFailure);
        }
    }

    private void AssertNoUnexpectedBrowserFailures()
    {
        var errors = PageErrors;
        var console = ConsoleIssues;
        var dialogs = UnexpectedDialogs;
        List<string> unconsumed;
        lock (expectedDialogQueue)
            unconsumed = expectedDialogQueue
                .Select(expected => $"{expected.DialogType}: {expected.Message}")
                .ToList();
        if (errors.Count == 0 && console.Count == 0 && dialogs.Count == 0 && unconsumed.Count == 0)
        {
            return;
        }

        var detail = new StringBuilder("The scenario recorded unexpected browser diagnostics.");
        if (errors.Count > 0)
        {
            detail.AppendLine().Append("Page errors: ").Append(string.Join("; ", errors));
        }
        if (console.Count > 0)
        {
            detail.AppendLine().Append("Console errors: ").Append(string.Join("; ", console));
        }
        if (dialogs.Count > 0)
        {
            detail.AppendLine().Append("Unexpected dialogs: ").Append(string.Join("; ", dialogs));
        }
        if (unconsumed.Count > 0)
        {
            detail.AppendLine().Append("Unconsumed dialog expectations: ").Append(string.Join("; ", unconsumed));
        }
        throw new InvalidOperationException(detail.ToString());
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
