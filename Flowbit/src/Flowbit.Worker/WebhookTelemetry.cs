using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Worker;

/// <summary>
/// Webhook metrics. Labels are bounded event, state, outcome, and reason sets; never ids, URLs, or actors.
/// Receiver outages show up here as backlog and backoff, not as Worker liveness failures.
/// </summary>
public sealed class WebhookTelemetry : IDisposable
{
    public const string MeterName = "Flowbit.Webhooks";
    public const string FanoutLoop = "fanout";
    public const string DispatchLoop = "dispatch";
    public const string DeliveryLoop = "delivery";
    public const string SigningLoop = "signing";

    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(2);

    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> eventsFannedOut;
    private readonly Counter<long> deliveriesCreated;
    private readonly Counter<long> attempts;
    private readonly Counter<long> transitions;
    private readonly Counter<long> leaseRecoveries;
    private readonly Counter<long> automaticPauses;
    private readonly Counter<long> archiveDiscards;
    private readonly Counter<long> loopFailures;
    private readonly Histogram<double> sendDuration;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<string, long> attemptsByOutcome = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> pausesByReason = new(StringComparer.Ordinal);
    private WebhookQueueSnapshot? snapshot;
    private DateTimeOffset snapshotAt;
    // Each loop's latest success and failure, so a loop that keeps failing is not masked by another that succeeds.
    private readonly ConcurrentDictionary<string, LoopHealth> loops = new(StringComparer.Ordinal);
    private int inFlight;
    private long eventsFannedOutTotal;
    private long deliveriesCreatedTotal;
    private long leaseRecoveriesTotal;
    private long archiveDiscardsTotal;
    private long loopFailuresTotal;
    private long sendSamples;
    private long sendMicroseconds;

    public WebhookTelemetry(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
        eventsFannedOut = meter.CreateCounter<long>("flowbit.webhooks.events.fanned_out", description: "Lifecycle events whose deliveries were created.");
        deliveriesCreated = meter.CreateCounter<long>("flowbit.webhooks.deliveries.created", description: "Deliveries created by fanout.");
        attempts = meter.CreateCounter<long>("flowbit.webhooks.attempts", description: "Finished delivery attempts by outcome.");
        transitions = meter.CreateCounter<long>("flowbit.webhooks.deliveries.transitions", description: "Delivery states reached after attempts.");
        leaseRecoveries = meter.CreateCounter<long>("flowbit.webhooks.leases.recovered", description: "Expired delivery leases returned to the queue.");
        automaticPauses = meter.CreateCounter<long>("flowbit.webhooks.subscriptions.automatic_pauses", description: "Subscriptions paused automatically by receiver behavior.");
        archiveDiscards = meter.CreateCounter<long>("flowbit.webhooks.archive.discarded", description: "Outstanding deliveries discarded by archival.");
        loopFailures = meter.CreateCounter<long>("flowbit.webhooks.loop.failures", description: "Unexpected webhook loop failures.");
        sendDuration = meter.CreateHistogram<double>("flowbit.webhooks.send.duration", "ms", "Duration of webhook HTTP sends.");
        meter.CreateObservableGauge("flowbit.webhooks.capture.enabled", () => snapshot is { CaptureEnabled: true } ? 1 : 0,
            description: "Whether the database lifecycle capture switch is on.");
        meter.CreateObservableGauge("flowbit.webhooks.fanout.pending", () => snapshot?.PendingFanout ?? 0,
            description: "Recorded events waiting for fanout.");
        meter.CreateObservableGauge("flowbit.webhooks.fanout.oldest_age", () => AgeSeconds(snapshot?.OldestPendingFanoutAt),
            "s", "Age of the oldest event waiting for fanout.");
        meter.CreateObservableGauge("flowbit.webhooks.queue.depth", QueueDepth,
            description: "Outstanding deliveries by state.");
        meter.CreateObservableGauge("flowbit.webhooks.queue.oldest_due_age", () => AgeSeconds(snapshot?.OldestDueAt),
            "s", "How long the oldest due delivery has been waiting.");
        meter.CreateObservableGauge("flowbit.webhooks.subscriptions.backing_off", () => snapshot?.BackingOffSubscriptions ?? 0,
            description: "Subscriptions in receiver backoff.");
        meter.CreateObservableGauge("flowbit.webhooks.subscriptions.paused", () => snapshot?.PausedSubscriptions ?? 0,
            description: "Paused subscriptions.");
        meter.CreateObservableGauge("flowbit.webhooks.sends.in_flight", () => Volatile.Read(ref inFlight),
            description: "Webhook HTTP sends in flight on this Worker.");
    }

    public WebhookQueueSnapshot? Snapshot => snapshot;

    public int InFlight => Volatile.Read(ref inFlight);

    /// <summary>Whether any webhook loop failed recently without a later success of that same loop.</summary>
    public bool HasRecentFailure
    {
        get
        {
            var now = timeProvider.GetUtcNow().UtcTicks;
            return loops.Values.Any(loop =>
            {
                var failure = Interlocked.Read(ref loop.FailureTicks);
                return failure > Interlocked.Read(ref loop.SuccessTicks) && now - failure < FailureWindow.Ticks;
            });
        }
    }

    public void RecordSnapshot(WebhookQueueSnapshot value)
    {
        snapshotAt = timeProvider.GetUtcNow();
        snapshot = value;
    }

    public void RecordFanout(WebhookFanoutResult result)
    {
        if (result.Events > 0)
        {
            eventsFannedOut.Add(result.Events);
            Interlocked.Add(ref eventsFannedOutTotal, result.Events);
        }
        if (result.Deliveries > 0)
        {
            deliveriesCreated.Add(result.Deliveries);
            Interlocked.Add(ref deliveriesCreatedTotal, result.Deliveries);
        }
    }

    public void RecordLeaseRecoveries(int count)
    {
        if (count > 0)
        {
            leaseRecoveries.Add(count);
            Interlocked.Add(ref leaseRecoveriesTotal, count);
        }
    }

    public void RecordArchiveDiscards(int count)
    {
        if (count > 0)
        {
            archiveDiscards.Add(count);
            Interlocked.Add(ref archiveDiscardsTotal, count);
        }
    }

    public void SendStarted() => Interlocked.Increment(ref inFlight);

    public void SendFinished() => Interlocked.Decrement(ref inFlight);

    public void RecordAttempt(WebhookSendOutcome outcome, WebhookFinalizeResult result)
    {
        CountAttempt(outcome.Outcome);
        sendDuration.Record(outcome.DurationMilliseconds, new KeyValuePair<string, object?>("outcome", outcome.Outcome));
        Interlocked.Increment(ref sendSamples);
        Interlocked.Add(ref sendMicroseconds, outcome.DurationMilliseconds * 1000L);
        if (result.Applied && result.State is not null)
        {
            transitions.Add(1, new KeyValuePair<string, object?>("state", result.State));
        }
    }

    public void RecordSettledWithoutSend(string outcome) => CountAttempt(outcome);

    public void RecordAutomaticPause(string reason)
    {
        automaticPauses.Add(1, new KeyValuePair<string, object?>("reason", reason));
        pausesByReason.AddOrUpdate(reason, 1, (_, value) => value + 1);
    }

    public void RecordLoopSuccess(string loop) =>
        Interlocked.Exchange(ref Loop(loop).SuccessTicks, timeProvider.GetUtcNow().UtcTicks);

    public void RecordLoopFailure(string loop)
    {
        loopFailures.Add(1, new KeyValuePair<string, object?>("loop", loop));
        Interlocked.Increment(ref loopFailuresTotal);
        Interlocked.Exchange(ref Loop(loop).FailureTicks, timeProvider.GetUtcNow().UtcTicks);
    }

    private LoopHealth Loop(string loop) => loops.GetOrAdd(loop, static _ => new LoopHealth());

    /// <summary>Appends the webhook series to the Worker's Prometheus text exposition.</summary>
    public void WritePrometheus(StringBuilder output)
    {
        var current = snapshot;
        Gauge(output, "flowbit_webhooks_capture_enabled", current is { CaptureEnabled: true } ? 1 : 0);
        Gauge(output, "flowbit_webhooks_fanout_pending", current?.PendingFanout ?? 0);
        Gauge(output, "flowbit_webhooks_fanout_oldest_age_seconds", AgeSeconds(current?.OldestPendingFanoutAt));
        output.AppendLine("# TYPE flowbit_webhooks_queue_depth gauge");
        foreach (var (state, value) in new[]
                 {
                     (WebhookDeliveryStates.Pending, current?.Pending ?? 0),
                     (WebhookDeliveryStates.RetryScheduled, current?.RetryScheduled ?? 0),
                     (WebhookDeliveryStates.Delivering, current?.Delivering ?? 0),
                     (WebhookDeliveryStates.Failed, current?.Failed ?? 0)
                 })
        {
            output.Append("flowbit_webhooks_queue_depth{state=\"").Append(state).Append("\"} ")
                .Append(value.ToString(CultureInfo.InvariantCulture)).AppendLine();
        }
        Gauge(output, "flowbit_webhooks_queue_oldest_due_age_seconds", AgeSeconds(current?.OldestDueAt));
        Gauge(output, "flowbit_webhooks_subscriptions_backing_off", current?.BackingOffSubscriptions ?? 0);
        Gauge(output, "flowbit_webhooks_subscriptions_paused", current?.PausedSubscriptions ?? 0);
        Gauge(output, "flowbit_webhooks_sends_in_flight", Volatile.Read(ref inFlight));
        Counter(output, "flowbit_webhooks_events_fanned_out_total", Interlocked.Read(ref eventsFannedOutTotal));
        Counter(output, "flowbit_webhooks_deliveries_created_total", Interlocked.Read(ref deliveriesCreatedTotal));
        Counter(output, "flowbit_webhooks_leases_recovered_total", Interlocked.Read(ref leaseRecoveriesTotal));
        Counter(output, "flowbit_webhooks_archive_discarded_total", Interlocked.Read(ref archiveDiscardsTotal));
        Counter(output, "flowbit_webhooks_loop_failures_total", Interlocked.Read(ref loopFailuresTotal));
        Labeled(output, "flowbit_webhooks_attempts_total", "outcome", attemptsByOutcome);
        Labeled(output, "flowbit_webhooks_automatic_pauses_total", "reason", pausesByReason);
        output.AppendLine("# TYPE flowbit_webhooks_send_duration_seconds summary");
        output.Append("flowbit_webhooks_send_duration_seconds_count ")
            .Append(Interlocked.Read(ref sendSamples).ToString(CultureInfo.InvariantCulture)).AppendLine();
        output.Append("flowbit_webhooks_send_duration_seconds_sum ")
            .Append((Interlocked.Read(ref sendMicroseconds) / 1_000_000d).ToString("R", CultureInfo.InvariantCulture))
            .AppendLine();
    }

    public void Dispose() => meter.Dispose();

    private void CountAttempt(string outcome)
    {
        attempts.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        attemptsByOutcome.AddOrUpdate(outcome, 1, (_, value) => value + 1);
    }

    private IEnumerable<Measurement<long>> QueueDepth()
    {
        var current = snapshot;
        if (current is null)
        {
            yield break;
        }
        yield return new(current.Pending, new KeyValuePair<string, object?>("state", WebhookDeliveryStates.Pending));
        yield return new(current.RetryScheduled, new KeyValuePair<string, object?>("state", WebhookDeliveryStates.RetryScheduled));
        yield return new(current.Delivering, new KeyValuePair<string, object?>("state", WebhookDeliveryStates.Delivering));
        yield return new(current.Failed, new KeyValuePair<string, object?>("state", WebhookDeliveryStates.Failed));
    }

    private double AgeSeconds(DateTimeOffset? since) =>
        since is null ? 0 : Math.Max(0, (snapshotAt - since.Value).TotalSeconds);

    private static void Gauge(StringBuilder output, string name, double value)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" gauge");
        output.Append(name).Append(' ').Append(value.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
    }

    private static void Counter(StringBuilder output, string name, long value)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" counter");
        output.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).AppendLine();
    }

    private sealed class LoopHealth
    {
        public long SuccessTicks;
        public long FailureTicks;
    }

    private static void Labeled(StringBuilder output, string name, string label, ConcurrentDictionary<string, long> values)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" counter");
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            output.Append(name).Append('{').Append(label).Append("=\"").Append(pair.Key).Append("\"} ")
                .Append(pair.Value.ToString(CultureInfo.InvariantCulture)).AppendLine();
        }
    }
}
