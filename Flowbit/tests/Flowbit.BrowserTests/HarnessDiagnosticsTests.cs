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
}
