using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Flowbit.Service.Ai;

/// <summary>The stored conversation and the messages not stored yet, at the start of one model call.</summary>
public sealed record WorkflowAiCallStart(JsonElement Session, IReadOnlyList<ChatMessage> Pending);

/// <summary>
/// Snapshots the conversation at the start of every model call. The per-call persistence layer below it prepends the stored history,
/// so the tool loop hands this layer only messages that are not stored yet: the user's message on a turn's first call, tool results
/// on later calls. The stored session plus those messages is always a complete conversation that a later run can resume, whether
/// the call then succeeds, fails or is cancelled. While a call is in flight it reports the wait at a fixed interval.
/// </summary>
internal sealed class WorkflowAiCheckpointingChatClient(IChatClient inner, Func<WorkflowAiCallStart, CancellationToken, Task> callStarting,
    Func<TimeSpan, CancellationToken, Task> waiting, TimeSpan waitInterval) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var pending = messages.ToList();
        var run = AIAgent.CurrentRunContext ?? throw new InvalidOperationException("Checkpoints need a running authoring agent.");
        var session = await run.Agent.SerializeSessionAsync(run.Session!, cancellationToken: cancellationToken);
        await callStarting(new(session, pending), cancellationToken);
        WorkflowAiTelemetry.Calls.Add(1);
        using var activity = WorkflowAiTelemetry.Start("provider.attempt");
        activity?.SetTag("outcome", "failed");
        var clock = Stopwatch.StartNew();
        try
        {
            var call = base.GetResponseAsync(pending, options, cancellationToken);
            while (!call.IsCompleted)
            {
                var delay = Task.Delay(waitInterval, cancellationToken);
                if (await Task.WhenAny(call, delay) != delay || delay.IsCanceled) break;
                await waiting(clock.Elapsed, cancellationToken);
            }
            var response = await call;
            activity?.SetTag("outcome", "completed");
            return response;
        }
        catch (WorkflowAiStopException stop)
        {
            activity?.SetTag("outcome", stop.Code);
            throw;
        }
        finally
        {
            WorkflowAiTelemetry.CallSeconds.Record(clock.Elapsed.TotalSeconds);
        }
    }

    // The agent runs non-streaming; a streaming caller would bypass the checkpoints, so it is refused.
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The authoring agent runs without streaming so every model call is checkpointed.");
}
