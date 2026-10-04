using System.Diagnostics;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Ai;

/// <summary>One bounded provider attempt. All purposes share admission, reservations and accounting.</summary>
internal sealed class WorkflowAiCallDispatcher(
    IAiWorkflowProvider provider, WorkflowAiOptions options, WorkflowAiConcurrencyGate gate,
    WorkflowAiSession state, AiTurnRequestDto request, AiExecutionSettings execution, string apiKey,
    Func<string, string, Task> progress) : IDisposable
{
    private readonly SemaphoreSlim slots = new(options.RequirementsReviewEnabled ? options.MaxParallelAnalysisCalls : 1);

    public async Task<AiCompletion> AttemptAsync(IReadOnlyList<AiChatMessageDto> messages, int tokens,
        string purpose, bool retry, CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken);
        try
        {
            await gate.ProviderCalls.WaitAsync(cancellationToken);
            try { return await DispatchAsync(messages, tokens, purpose, retry, cancellationToken); }
            finally { gate.ProviderCalls.Release(); }
        }
        finally { slots.Release(); }
    }

    private async Task<AiCompletion> DispatchAsync(IReadOnlyList<AiChatMessageDto> messages, int tokens,
        string purpose, bool retry, CancellationToken cancellationToken)
    {
        var profile = options.GetModelProfile(request.ModelId);
        if (messages.Sum(message => (long)message.Content.Length) > options.MaxContextCharacters
            || WorkflowAiContext.EstimateTokens(messages) > (long)(profile.ContextTokens * .9) - tokens)
            throw new WorkflowAiException("context_too_large", "Required authoring context exceeds this model's budget. Reduce the input or adjust the model profile.", 413);
        int ordinal;
        lock (state.AccountingSync)
        {
            if (state.Calls >= options.MaxProviderCalls || options.MaxRunOutputTokens - state.OutputTokens - state.ReservedOutputTokens < tokens)
                throw new WorkflowAiException("run_budget", "This run reached its model usage budget. Continue from the last completed draft step.");
            ordinal = ++state.Calls;
            if (retry) state.Retries++;
            state.ReservedOutputTokens += tokens;
            state.PeakCalls = Math.Max(state.PeakCalls, ++state.ActiveCalls);
        }
        using var activity = WorkflowAiTelemetry.Start("provider.attempt");
        activity?.SetTag("call.ordinal", ordinal);
        activity?.SetTag("call.purpose", purpose);
        activity?.SetTag("tokens.allowance", tokens);
        activity?.SetTag("outcome", "failed");
        AiCompletion? response = null;
        var clock = new Stopwatch();
        try
        {
            await progress(retry ? "retrying" : purpose == "builder" ? "generating" : purpose,
                retry ? "Retrying the selected provider." : purpose == "builder" ? "Building the next workflow step." : purpose == "analysis" ? "Analyzing requirements." : "Reviewing workflow requirements.");
            cancellationToken.ThrowIfCancellationRequested();
            WorkflowAiTelemetry.Calls.Add(1, new KeyValuePair<string, object?>("provider", request.ProviderId));
            clock.Start();
            // Enforce the timeout here as well as in the HTTP adapter, so every provider has the same boundary.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
            try { response = await provider.CompleteAsync(request.ModelId, request.ConversationId, messages, apiKey, tokens, execution, timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new WorkflowAiException("provider_unavailable", "The AI provider request timed out.", 504) { Retryable = true }; }
            activity?.SetTag("tokens.input", response.InputTokens);
            activity?.SetTag("tokens.output", response.OutputTokens);
            activity?.SetTag("usage.estimated", response.OutputTokens is null);
            activity?.SetTag("outcome", response.FinishReason == "length" ? "truncated" : "completed");
            return response;
        }
        catch (OperationCanceledException) { activity?.SetTag("outcome", "cancelled"); throw; }
        finally
        {
            clock.Stop();
            lock (state.AccountingSync)
            {
                state.ReservedOutputTokens -= tokens;
                state.OutputTokens += Math.Max(0, response?.OutputTokens ?? tokens);
                state.InputTokens += Math.Max(0, response?.InputTokens ?? 0);
                state.Estimated |= response?.OutputTokens is null;
                state.ActiveCalls--;
                state.LastCallSeconds = clock.Elapsed.TotalSeconds;
                state.TotalProviderSeconds += clock.Elapsed.TotalSeconds;
            }
            WorkflowAiTelemetry.CallSeconds.Record(clock.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("variant", execution.Variant));
            // Cancellation must not hide settlement or keep other workers alive to emit a completion frame.
            if (!cancellationToken.IsCancellationRequested)
                await progress(purpose == "builder" ? "processing" : purpose, "Processing the completed AI call.");
        }
    }

    public void Dispose() => slots.Dispose();
}
