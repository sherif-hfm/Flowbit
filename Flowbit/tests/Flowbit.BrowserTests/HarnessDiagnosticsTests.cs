using Flowbit.BrowserTests.Infrastructure;
using Xunit;

namespace Flowbit.BrowserTests;

/// <summary>Fault-injection checks for the harness, separate from product scenarios.</summary>
[Collection(BrowserCollection.Name)]
public sealed class HarnessDiagnosticsTests(BrowserStackFixture stack)
{
    [Fact]
    public async Task AuxiliaryPageErrorsAndDialogsFailTheScenario()
    {
        await using var scenario = await stack.CreateScenario("harness-expected-auxiliary-errors");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.RunAsync("injected-errors", async () =>
        {
            var page = await scenario.Context.NewPageAsync();
            var pageError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            page.PageError += (_, _) => pageError.TrySetResult();
            await page.EvaluateAsync("""
                () => {
                    console.error('injected auxiliary error');
                    alert('injected unexpected dialog');
                    setTimeout(() => { throw new Error('injected uncaught error'); }, 0);
                }
                """);
            await pageError.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }));
        Assert.Contains("unexpected browser diagnostics", failure.Message);
        Assert.Contains(scenario.ConsoleIssues, message => message.Contains("injected auxiliary error"));
        Assert.Contains(scenario.PageErrors, message => message.Contains("injected uncaught error"));
        Assert.Contains("injected unexpected dialog", scenario.UnexpectedDialogs);
    }

    [Fact]
    public async Task SuccessfulScenarioRetainsWarningsAndTransportFailures()
    {
        await using var scenario = await stack.CreateScenario("harness-retained-diagnostics");
        await scenario.RunAsync("injected-warning", async () =>
        {
            var page = await scenario.Context.NewPageAsync();
            await page.GotoAsync(stack.EditorBaseAddress);
            await page.EvaluateAsync("console.warn('injected retained warning')");
            // Abort a navigation: this raises RequestFailed without requiring a
            // rendered resource error, and is explicitly handled by the test.
            await page.RouteAsync("**/injected-abort", route => route.AbortAsync());
            await Assert.ThrowsAsync<Microsoft.Playwright.PlaywrightException>(
                () => page.GotoAsync(stack.EditorBaseAddress + "/injected-abort"));
        });
        Assert.Contains(scenario.FailedRequests, message => message.Contains("injected-abort"));
        var diagnostics = await File.ReadAllTextAsync(Path.Combine(scenario.ArtifactDirectory, "diagnostics.json"));
        Assert.Contains("injected retained warning", diagnostics);
        Assert.Contains("injected-abort", diagnostics);
    }

    [Fact]
    public async Task TimeoutCancelsAndDrainsWorkBeforeReturning()
    {
        await using var scenario = await stack.CreateScenario("harness-expected-timeout",
            scenarioTimeout: TimeSpan.FromMilliseconds(100));
        var stopped = false;
        await Assert.ThrowsAsync<TimeoutException>(() => scenario.RunAsync("injected-timeout", async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ScenarioCancellation.Token);
            }
            finally
            {
                stopped = true;
            }
        }));
        Assert.True(stopped);
        Assert.Empty(scenario.Context.Pages);
    }

    [Fact]
    public async Task QueuedDialogExpectationsAcceptDismissAndFailCorrectly()
    {
        // An expected dialog answered with OK is consumed and returns true.
        await using var acceptScenario = await stack.CreateScenario("harness-dialog-accept");
        acceptScenario.ExpectDialogOnce("confirm", "injected accept confirm", accept: true);
        await acceptScenario.RunAsync("dialog-accept", async () =>
        {
            var page = await acceptScenario.Context.NewPageAsync();
            Assert.True(await page.EvaluateAsync<bool>("confirm('injected accept confirm')"));
        });
        Assert.Contains(
            acceptScenario.DialogResponses,
            response => response.Contains("accepted", StringComparison.Ordinal) &&
                response.Contains("injected accept confirm", StringComparison.Ordinal));

        // An expected dialog answered with Cancel is consumed and returns false.
        await using var dismissScenario = await stack.CreateScenario("harness-dialog-dismiss");
        dismissScenario.ExpectDialogOnce("confirm", "injected dismiss confirm", accept: false);
        await dismissScenario.RunAsync("dialog-dismiss", async () =>
        {
            var page = await dismissScenario.Context.NewPageAsync();
            Assert.False(await page.EvaluateAsync<bool>("confirm('injected dismiss confirm')"));
        });
        Assert.Contains(
            dismissScenario.DialogResponses,
            response => response.Contains("dismissed", StringComparison.Ordinal) &&
                response.Contains("injected dismiss confirm", StringComparison.Ordinal));

        // An unconsumed one-shot expectation fails the scenario body.
        await using var unconsumedScenario = await stack.CreateScenario("harness-dialog-unconsumed");
        unconsumedScenario.ExpectDialogOnce("confirm", "never shown", accept: true);
        var unconsumedFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => unconsumedScenario.RunAsync("dialog-unconsumed", async () =>
            {
                var page = await unconsumedScenario.Context.NewPageAsync();
                await page.EvaluateAsync("() => 1");
            }));
        Assert.Contains("Unconsumed dialog expectations", unconsumedFailure.Message);
        Assert.Contains("never shown", unconsumedFailure.Message);

        // A dialog that matches neither the queue head nor a prefix fails.
        await using var unexpectedScenario = await stack.CreateScenario("harness-dialog-unexpected");
        unexpectedScenario.ExpectDialogOnce("confirm", "a different message", accept: true);
        var unexpectedFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => unexpectedScenario.RunAsync("dialog-unexpected", async () =>
            {
                var page = await unexpectedScenario.Context.NewPageAsync();
                await page.EvaluateAsync("confirm('injected unexpected confirm')");
            }));
        Assert.Contains("Unexpected dialogs", unexpectedFailure.Message);
        Assert.Contains("injected unexpected confirm", unexpectedFailure.Message);
    }
}
