using Flowbit.Service.Models;

namespace Flowbit.Worker;

public sealed class WorkerOptions
{
    public const string SectionName = "FlowbitWorker";

    public int MaxConcurrency { get; set; } = 8;
    public int ActivityConcurrency { get; set; } = 6;

    /// <summary>
    /// How many share jobs of split signal broadcasts (EVENT-04G) this Worker runs at once,
    /// inside its activity slots. Unset, it is the default number of jobs of one split
    /// definition (2), so at the defaults one split definition runs at a time; it is also never
    /// more than two fewer than <see cref="ActivityConcurrency"/>, and never below 1 unless that
    /// is 0. Every other job, the delivery jobs of small broadcasts included, ignores it.
    /// </summary>
    public int? SignalShareConcurrency { get; set; }

    /// <summary>The share slots this Worker has: <see cref="SignalShareConcurrency"/>, or its default.</summary>
    public int EffectiveSignalShareConcurrency =>
        SignalShareConcurrency ?? (ActivityConcurrency == 0
            ? 0
            : Math.Clamp(ActivityConcurrency - 2, 1, SignalSettings.DefaultMaxDeliveryJobsPerDefinition));
    public int BatchSize { get; set; } = 32;
    public int MaxPerInstance { get; set; } = 4;
    public int LeaseSeconds { get; set; } = 60;
    public int HeartbeatSeconds { get; set; } = 20;
    public int LeaseCheckMilliseconds { get; set; } = 1000;
    public int HeartbeatCommandTimeoutMilliseconds { get; set; } = 2000;
    public int PollMilliseconds { get; set; } = 1000;
    public int IdleBackoffMilliseconds { get; set; } = 5000;
    public int CompletedJobRetentionDays { get; set; } = 30;
    public int ResolvedIncidentRetentionDays { get; set; } = 90;
    public int RetentionBatchSize { get; set; } = 250;
    public int RetentionBatchDelayMilliseconds { get; set; } = 1000;
    public int RetentionIdleDelayMilliseconds { get; set; } = 5000;
    public int RetentionMaxRunnableJobs { get; set; } = 100;
    public int RetentionMaxQueueLagSeconds { get; set; } = 30;
    public int TimerStartReconcileSeconds { get; set; } = 1;
    public int TimerStartReconcileBatchSize { get; set; } = 100;

    /// <summary>
    /// How often the signal broadcast expiry sweep runs, in seconds (EVENT-04E). It settles
    /// broadcasts past their expiry whose delivery job is not running.
    /// </summary>
    public int SignalExpirySweepSeconds { get; set; } = 5;
    public int ShutdownDrainSeconds { get; set; } = 30;
    public string HealthListenUrl { get; set; } = "http://0.0.0.0:8081";

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseSeconds);
    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(HeartbeatSeconds);
    public TimeSpan LeaseCheckInterval => TimeSpan.FromMilliseconds(LeaseCheckMilliseconds);
    public TimeSpan HeartbeatCommandTimeout =>
        TimeSpan.FromMilliseconds(HeartbeatCommandTimeoutMilliseconds);

    public void Validate()
    {
        if (MaxConcurrency is < 1 or > 1024)
            throw new InvalidOperationException($"{SectionName}:MaxConcurrency must be between 1 and 1024.");
        if (ActivityConcurrency < 0 || ActivityConcurrency > MaxConcurrency)
            throw new InvalidOperationException($"{SectionName}:ActivityConcurrency must be between 0 and MaxConcurrency.");
        if (SignalShareConcurrency is { } shares && (shares < 0 || shares > ActivityConcurrency))
            throw new InvalidOperationException($"{SectionName}:SignalShareConcurrency must be between 0 and ActivityConcurrency.");
        if (BatchSize is < 1 or > 1000)
            throw new InvalidOperationException($"{SectionName}:BatchSize must be between 1 and 1000.");
        if (MaxPerInstance is < 1 or > 1000)
            throw new InvalidOperationException($"{SectionName}:MaxPerInstance must be between 1 and 1000.");
        if (LeaseSeconds is < 15 or > 1800)
            throw new InvalidOperationException($"{SectionName}:LeaseSeconds must be between 15 and 1800.");
        if (HeartbeatSeconds < 1 || HeartbeatSeconds * 2 >= LeaseSeconds)
            throw new InvalidOperationException($"{SectionName}:HeartbeatSeconds must be less than half LeaseSeconds.");
        if (LeaseCheckMilliseconds is < 100 or > 60_000)
            throw new InvalidOperationException($"{SectionName}:LeaseCheckMilliseconds must be between 100 and 60000.");
        if (LeaseCheckInterval > HeartbeatInterval)
            throw new InvalidOperationException($"{SectionName}:LeaseCheckMilliseconds must not exceed HeartbeatSeconds.");
        if (HeartbeatCommandTimeoutMilliseconds is < 100 or > 30_000)
            throw new InvalidOperationException($"{SectionName}:HeartbeatCommandTimeoutMilliseconds must be between 100 and 30000.");
        if (HeartbeatCommandTimeout >= LeaseDuration - HeartbeatInterval)
            throw new InvalidOperationException($"{SectionName}:HeartbeatCommandTimeoutMilliseconds must leave time to retry before the lease expires.");
        if (HeartbeatInterval
            + LeaseCheckInterval
            + HeartbeatCommandTimeout
            + HeartbeatCommandTimeout >= LeaseDuration)
        {
            throw new InvalidOperationException(
                $"{SectionName}: heartbeat timing must allow a failed heartbeat command to retry before lease expiry.");
        }
        if (PollMilliseconds is < 50 or > 60_000)
            throw new InvalidOperationException($"{SectionName}:PollMilliseconds must be between 50 and 60000.");
        if (IdleBackoffMilliseconds < PollMilliseconds || IdleBackoffMilliseconds > 300_000)
            throw new InvalidOperationException($"{SectionName}:IdleBackoffMilliseconds must be between PollMilliseconds and 300000.");
        if (CompletedJobRetentionDays is < 1 or > 36500)
            throw new InvalidOperationException($"{SectionName}:CompletedJobRetentionDays must be between 1 and 36500.");
        if (ResolvedIncidentRetentionDays is < 1 or > 36500)
            throw new InvalidOperationException($"{SectionName}:ResolvedIncidentRetentionDays must be between 1 and 36500.");
        if (RetentionBatchSize is < 1 or > 1000)
            throw new InvalidOperationException($"{SectionName}:RetentionBatchSize must be between 1 and 1000.");
        if (RetentionBatchDelayMilliseconds is < 100 or > 60_000)
            throw new InvalidOperationException($"{SectionName}:RetentionBatchDelayMilliseconds must be between 100 and 60000.");
        if (RetentionIdleDelayMilliseconds < RetentionBatchDelayMilliseconds
            || RetentionIdleDelayMilliseconds > 300_000)
            throw new InvalidOperationException($"{SectionName}:RetentionIdleDelayMilliseconds must be between RetentionBatchDelayMilliseconds and 300000.");
        if (RetentionMaxRunnableJobs is < 1 or > 1_000_000)
            throw new InvalidOperationException($"{SectionName}:RetentionMaxRunnableJobs must be between 1 and 1000000.");
        if (RetentionMaxQueueLagSeconds is < 1 or > 3600)
            throw new InvalidOperationException($"{SectionName}:RetentionMaxQueueLagSeconds must be between 1 and 3600.");
        if (TimerStartReconcileSeconds is < 1 or > 3600)
            throw new InvalidOperationException($"{SectionName}:TimerStartReconcileSeconds must be between 1 and 3600.");
        if (TimerStartReconcileBatchSize is < 1 or > 1000)
            throw new InvalidOperationException($"{SectionName}:TimerStartReconcileBatchSize must be between 1 and 1000.");
        if (SignalExpirySweepSeconds is < 1 or > 3600)
            throw new InvalidOperationException($"{SectionName}:SignalExpirySweepSeconds must be between 1 and 3600.");
        if (ShutdownDrainSeconds is < 1 or > 300)
            throw new InvalidOperationException($"{SectionName}:ShutdownDrainSeconds must be between 1 and 300.");
        if (!Uri.TryCreate(HealthListenUrl, UriKind.Absolute, out var healthUri)
            || (healthUri.Scheme != Uri.UriSchemeHttp
                && healthUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{SectionName}:HealthListenUrl must be an absolute HTTP or HTTPS URL.");
        }
    }
}
