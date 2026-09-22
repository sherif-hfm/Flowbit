namespace Flowbit.BrowserTests.Infrastructure;

/// <summary>Flows cancellation through the test's async HTTP and retry helpers.</summary>
public static class ScenarioCancellation
{
    private static readonly AsyncLocal<CancellationToken> Current = new();
    public static CancellationToken Token => Current.Value;

    public static async Task RunAsync(Func<Task> body, CancellationToken token)
    {
        var previous = Current.Value;
        Current.Value = token;
        try
        {
            token.ThrowIfCancellationRequested();
            await body();
        }
        finally
        {
            Current.Value = previous;
        }
    }
}
