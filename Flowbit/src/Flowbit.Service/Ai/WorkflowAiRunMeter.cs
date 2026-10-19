namespace Flowbit.Service.Ai;

/// <summary>Ends an agent run early. The run returns a paused result that Continue resumes from the last completed step.</summary>
public sealed class WorkflowAiStopException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Model-request accounting for one run, ported from Flowbit AI Lab v3's CaptureHandler: a request beyond the cap is refused
/// before it is sent, and reported usage is summed. Nothing is ever retried.
/// </summary>
public sealed class WorkflowAiRunMeter(int maxRequests)
{
    private readonly object gate = new();
    private int requests;
    private long inputTokens, cachedInputTokens, outputTokens;
    private bool usageMissing;

    public int MaxRequests { get; } = maxRequests;
    public int Requests { get { lock (gate) return requests; } }
    public int RemainingRequests { get { lock (gate) return Math.Max(0, MaxRequests - requests); } }
    public long InputTokens { get { lock (gate) return inputTokens; } }
    public long CachedInputTokens { get { lock (gate) return cachedInputTokens; } }
    public long OutputTokens { get { lock (gate) return outputTokens; } }
    /// <summary>True when a completed response did not report its usage, so the totals are lower bounds.</summary>
    public bool UsageMissing { get { lock (gate) return usageMissing; } }

    /// <summary>Counts the next request, or refuses it when the run's allowance is used up.</summary>
    public int Reserve()
    {
        lock (gate)
        {
            if (requests >= MaxRequests)
                throw new WorkflowAiStopException("run_budget",
                    $"The run reached its limit of {MaxRequests} model requests. Continue to resume from the last completed step.");
            return ++requests;
        }
    }

    public void Record(long? input, long? cachedInput, long? output)
    {
        lock (gate)
        {
            if (input is null || output is null) usageMissing = true;
            inputTokens += input ?? 0;
            cachedInputTokens += cachedInput ?? 0;
            outputTokens += output ?? 0;
        }
    }
}
