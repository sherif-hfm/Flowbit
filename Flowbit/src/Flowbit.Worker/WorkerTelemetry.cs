using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using Flowbit.Service.Models;

namespace Flowbit.Worker;

public sealed class WorkerTelemetry : IDisposable
{
    public const string MeterName = "Flowbit.Worker";

    private readonly Meter _meter = new(MeterName);
    private readonly MeterListener _runtimeListener = new();
    private readonly Counter<long> _acquired;
    private readonly Counter<long> _processed;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _leaseLost;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _cleanupJobs;
    private readonly Counter<long> _cleanupAttempts;
    private readonly Counter<long> _cleanupSnapshots;
    private readonly Counter<long> _cleanupIncidents;
    private readonly Counter<long> _timerStarts;
    private readonly Counter<long> _sharedWakeExpansionsAcquired;
    private readonly Counter<long> _sharedWakeDeliveriesAcquired;
    private readonly Counter<long> _sharedWakesCompleted;
    private readonly Counter<long> _sharedWakesFailed;
    private readonly Counter<long> _sharedWakeLeasesLost;
    private readonly Counter<long> _sharedWakeHeartbeatsSucceeded;
    private readonly Counter<long> _sharedWakeHeartbeatsFailed;
    private readonly Counter<long> _sharedWakeRetries;
    private readonly Counter<long> _sharedWakeFinalizationIncidents;
    private readonly Counter<long> _sharedWakeDeliveriesCreated;
    private readonly Counter<long> _sharedWakeCleanupWakes;
    private readonly Counter<long> _sharedWakeCleanupDeliveries;
    private readonly Counter<long> _sharedWakeCleanupIncidents;
    private readonly Histogram<double> _acquisitionLatency;
    private readonly Histogram<double> _processingDuration;
    private readonly Histogram<double> _queueLag;
    private readonly Histogram<double> _timerLateness;
    private readonly Histogram<double> _sharedWakeAcquisitionLatency;
    private readonly Histogram<double> _sharedWakeExpansionDuration;
    private readonly Histogram<double> _sharedWakeDeliveryDuration;
    private int _active;
    private int _activeActivities;
    private int _activeSharedWakeExpansions;
    private int _activeSharedWakeDeliveries;
    private int _ready;
    private long _runnableDepth;
    private long _openIncidents;
    private long _openSharedWakeIncidents;
    private double _oldestRunnableAgeMilliseconds;
    private long _acquiredTotal;
    private long _processedTotal;
    private long _failedTotal;
    private long _leaseLostTotal;
    private long _retriesTotal;
    private long _cleanupJobsTotal;
    private long _cleanupAttemptsTotal;
    private long _cleanupSnapshotsTotal;
    private long _cleanupIncidentsTotal;
    private long _timerStartsTotal;
    private long _sharedWakeExpansionsAcquiredTotal;
    private long _sharedWakeDeliveriesAcquiredTotal;
    private long _sharedWakesCompletedTotal;
    private long _sharedWakesFailedTotal;
    private long _sharedWakeLeasesLostTotal;
    private long _sharedWakeExpansionHeartbeatsSucceededTotal;
    private long _sharedWakeDeliveryHeartbeatsSucceededTotal;
    private long _sharedWakeExpansionHeartbeatsFailedTotal;
    private long _sharedWakeDeliveryHeartbeatsFailedTotal;
    private long _sharedWakeRetriesTotal;
    private long _sharedWakeFinalizationIncidentsTotal;
    private long _sharedWakeDeliveriesCreatedTotal;
    private long _sharedWakeCleanupWakesTotal;
    private long _sharedWakeCleanupDeliveriesTotal;
    private long _sharedWakeCleanupIncidentsTotal;
    private long _acquisitionSamples;
    private long _acquisitionMicroseconds;
    private long _processingSamples;
    private long _processingMicroseconds;
    private long _queueLagSamples;
    private long _queueLagMicroseconds;
    private long _timerLatenessSamples;
    private long _timerLatenessMicroseconds;
    private long _sharedWakeAcquisitionSamples;
    private long _sharedWakeAcquisitionMicroseconds;
    private long _sharedWakeExpansionSamples;
    private long _sharedWakeExpansionMicroseconds;
    private long _sharedWakeDeliverySamples;
    private long _sharedWakeDeliveryMicroseconds;
    private long _runtimeRetriesTotal;
    private long _runtimeConflictsTotal;
    private long _runtimeIncidentsTotal;
    private long _runtimeAutomaticLoopLimitsTotal;
    private long _instanceLockWaitSamples;
    private long _instanceLockWaitMicroseconds;

    public WorkerTelemetry()
    {
        _runtimeListener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Flowbit.Runtime.Jobs")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _runtimeListener.SetMeasurementEventCallback<long>(OnRuntimeLongMeasurement);
        _runtimeListener.SetMeasurementEventCallback<double>(OnRuntimeDoubleMeasurement);
        _runtimeListener.Start();

        _acquired = _meter.CreateCounter<long>(
            "flowbit.worker.jobs.acquired",
            description: "Jobs leased by this worker replica.");
        _processed = _meter.CreateCounter<long>(
            "flowbit.worker.jobs.processed",
            description: "Leased jobs whose processor returned normally.");
        _failed = _meter.CreateCounter<long>(
            "flowbit.worker.jobs.failed",
            description: "Leased jobs whose processor escaped an exception.");
        _leaseLost = _meter.CreateCounter<long>(
            "flowbit.worker.leases.lost",
            description: "Fenced leases rejected by heartbeat.");
        _retries = _meter.CreateCounter<long>(
            "flowbit.worker.retries.scheduled",
            description: "Unhandled worker failures scheduled for retry.");
        _cleanupJobs = _meter.CreateCounter<long>(
            "flowbit.worker.cleanup.jobs",
            description: "Terminal jobs removed by retention cleanup.");
        _cleanupAttempts = _meter.CreateCounter<long>(
            "flowbit.worker.cleanup.attempts",
            description: "Job attempts removed by retention cleanup.");
        _cleanupSnapshots = _meter.CreateCounter<long>(
            "flowbit.worker.cleanup.snapshots",
            description: "Immutable job snapshots removed by retention cleanup.");
        _cleanupIncidents = _meter.CreateCounter<long>(
            "flowbit.worker.cleanup.incidents",
            description: "Resolved incidents removed by retention cleanup.");
        _timerStarts = _meter.CreateCounter<long>(
            "flowbit.worker.timer_start.subscriptions",
            description: "Timer-start subscriptions created or repaired.");
        _sharedWakeExpansionsAcquired = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.expansions.acquired",
            description: "Shared-variable wake expansion rows leased by this worker replica.");
        _sharedWakeDeliveriesAcquired = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.deliveries.acquired",
            description: "Shared-variable wake delivery rows leased by this worker replica.");
        _sharedWakesCompleted = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.completed",
            description: "Shared-variable wake work finalized as completed.");
        _sharedWakesFailed = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.failed",
            description: "Shared-variable wake work that did not complete in this invocation.");
        _sharedWakeLeasesLost = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.leases.lost",
            description: "Shared-variable wake fences rejected or lost before finalization.");
        _sharedWakeHeartbeatsSucceeded = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.heartbeat.succeeded",
            description: "Shared-variable wake lease-renewal commands accepted by PostgreSQL, tagged by work_kind.");
        _sharedWakeHeartbeatsFailed = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.heartbeat.failed",
            description: "Shared-variable wake lease-renewal commands rejected or failed, tagged by work_kind.");
        _sharedWakeRetries = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.retries.scheduled",
            description: "Shared-variable wake failures scheduled durably for retry.");
        _sharedWakeFinalizationIncidents = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.incidents.finalization_opened",
            description: "Incidents opened by fenced work finalization after attempt exhaustion; acquisition-sweep incidents are reflected by the open-incident gauge.");
        _sharedWakeDeliveriesCreated = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.deliveries.created",
            description: "Per-instance delivery rows materialized by wake expansion pages.");
        _sharedWakeCleanupWakes = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.cleanup.wakes",
            description: "Terminal shared-variable wake rows removed by retention cleanup.");
        _sharedWakeCleanupDeliveries = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.cleanup.deliveries",
            description: "Terminal shared-variable wake delivery rows removed by retention cleanup.");
        _sharedWakeCleanupIncidents = _meter.CreateCounter<long>(
            "flowbit.worker.shared_wake.cleanup.incidents",
            description: "Resolved shared-variable wake incidents removed by retention cleanup.");
        _acquisitionLatency = _meter.CreateHistogram<double>(
            "flowbit.worker.acquisition.duration",
            "ms",
            "Job lease acquisition latency.");
        _processingDuration = _meter.CreateHistogram<double>(
            "flowbit.worker.processing.duration",
            "ms",
            "End-to-end processing duration for a leased job.");
        _queueLag = _meter.CreateHistogram<double>(
            "flowbit.worker.queue.lag",
            "ms",
            "Elapsed time from a job's due time until lease acquisition.");
        _timerLateness = _meter.CreateHistogram<double>(
            "flowbit.worker.timer.lateness",
            "ms",
            "Elapsed time from a timer occurrence until lease acquisition.");
        _sharedWakeAcquisitionLatency = _meter.CreateHistogram<double>(
            "flowbit.worker.shared_wake.acquisition.duration",
            "ms",
            "Shared-variable wake lease acquisition latency.");
        _sharedWakeExpansionDuration = _meter.CreateHistogram<double>(
            "flowbit.worker.shared_wake.expansion.duration",
            "ms",
            "End-to-end processing duration for a leased wake expansion.");
        _sharedWakeDeliveryDuration = _meter.CreateHistogram<double>(
            "flowbit.worker.shared_wake.delivery.duration",
            "ms",
            "End-to-end processing duration for a leased wake delivery.");
        _meter.CreateObservableGauge(
            "flowbit.worker.jobs.active",
            () => Volatile.Read(ref _active),
            description: "Jobs currently executing in this worker replica.");
        _meter.CreateObservableGauge(
            "flowbit.worker.jobs.activity.active",
            () => Volatile.Read(ref _activeActivities),
            description: "External activity jobs currently executing in this worker replica.");
        _meter.CreateObservableGauge(
            "flowbit.worker.shared_wake.expansions.active",
            () => Volatile.Read(ref _activeSharedWakeExpansions),
            description: "Shared-variable wake expansions currently executing in this worker replica.");
        _meter.CreateObservableGauge(
            "flowbit.worker.shared_wake.deliveries.active",
            () => Volatile.Read(ref _activeSharedWakeDeliveries),
            description: "Shared-variable wake deliveries currently executing in this worker replica.");
        _meter.CreateObservableGauge(
            "flowbit.worker.ready",
            () => Volatile.Read(ref _ready),
            description: "One after this worker has successfully queried the durable queue.");
        _meter.CreateObservableGauge(
            "flowbit.worker.queue.runnable.depth",
            () => Interlocked.Read(ref _runnableDepth),
            description: "Current database-observed runnable job depth.");
        _meter.CreateObservableGauge(
            "flowbit.worker.queue.oldest.age",
            () => Volatile.Read(ref _oldestRunnableAgeMilliseconds),
            "ms",
            "Current age of the oldest runnable job.");
        _meter.CreateObservableGauge(
            "flowbit.worker.incidents.open",
            () => Interlocked.Read(ref _openIncidents),
            description: "Current database-observed open incident count.");
        _meter.CreateObservableGauge(
            "flowbit.worker.shared_wake.incidents.open",
            () => Interlocked.Read(ref _openSharedWakeIncidents),
            description: "Current database-observed open shared-variable wake incident count.");
    }

    public void RecordAcquisition(
        IReadOnlyList<WorkflowJobLeaseRecord> leases,
        TimeSpan elapsed,
        DateTimeOffset now)
    {
        _acquisitionLatency.Record(elapsed.TotalMilliseconds);
        RecordSample(
            ref _acquisitionSamples,
            ref _acquisitionMicroseconds,
            elapsed.TotalMilliseconds);
        Volatile.Write(ref _ready, 1);
        if (leases.Count == 0)
        {
            return;
        }

        _acquired.Add(leases.Count);
        Interlocked.Add(ref _acquiredTotal, leases.Count);
        foreach (var lease in leases)
        {
            var queueLag = Math.Max(0, (now - lease.Job.DueAt).TotalMilliseconds);
            _queueLag.Record(queueLag);
            RecordSample(ref _queueLagSamples, ref _queueLagMicroseconds, queueLag);
            if (lease.Job.ScheduledOccurrenceAt is DateTimeOffset occurrence)
            {
                var lateness = Math.Max(0, (now - occurrence).TotalMilliseconds);
                _timerLateness.Record(lateness);
                RecordSample(
                    ref _timerLatenessSamples,
                    ref _timerLatenessMicroseconds,
                    lateness);
            }
        }
    }

    public void JobStarted(bool activity)
    {
        Interlocked.Increment(ref _active);
        if (activity)
        {
            Interlocked.Increment(ref _activeActivities);
        }
    }

    public void JobFinished(bool activity, bool succeeded, TimeSpan elapsed)
    {
        Interlocked.Decrement(ref _active);
        if (activity)
        {
            Interlocked.Decrement(ref _activeActivities);
        }
        _processingDuration.Record(elapsed.TotalMilliseconds);
        RecordSample(
            ref _processingSamples,
            ref _processingMicroseconds,
            elapsed.TotalMilliseconds);
        if (succeeded)
        {
            _processed.Add(1);
            Interlocked.Increment(ref _processedTotal);
        }
        else
        {
            _failed.Add(1);
            Interlocked.Increment(ref _failedTotal);
        }
    }

    public void RecordLeaseLost()
    {
        _leaseLost.Add(1);
        Interlocked.Increment(ref _leaseLostTotal);
    }

    public void RecordRetry()
    {
        _retries.Add(1);
        Interlocked.Increment(ref _retriesTotal);
    }

    public void RecordCleanup(
        int jobs,
        int incidents,
        int attempts = 0,
        int snapshots = 0)
    {
        if (jobs > 0)
        {
            _cleanupJobs.Add(jobs);
            Interlocked.Add(ref _cleanupJobsTotal, jobs);
        }
        if (incidents > 0)
        {
            _cleanupIncidents.Add(incidents);
            Interlocked.Add(ref _cleanupIncidentsTotal, incidents);
        }
        if (attempts > 0)
        {
            _cleanupAttempts.Add(attempts);
            Interlocked.Add(ref _cleanupAttemptsTotal, attempts);
        }
        if (snapshots > 0)
        {
            _cleanupSnapshots.Add(snapshots);
            Interlocked.Add(ref _cleanupSnapshotsTotal, snapshots);
        }
    }

    public void RecordQueueSnapshot(WorkflowJobQueueStatisticsRecord statistics)
    {
        Interlocked.Exchange(ref _runnableDepth, statistics.RunnableDepth);
        Interlocked.Exchange(ref _openIncidents, statistics.OpenIncidentCount);
        Volatile.Write(
            ref _oldestRunnableAgeMilliseconds,
            statistics.OldestRunnableDueAt is { } dueAt
                ? Math.Max(0, (statistics.ObservedAt - dueAt).TotalMilliseconds)
                : 0);
    }

    public void RecordTimerStart()
    {
        _timerStarts.Add(1);
        Interlocked.Increment(ref _timerStartsTotal);
    }

    public void RecordSharedWakeAcquisition(
        bool expansion,
        int count,
        TimeSpan elapsed)
    {
        _sharedWakeAcquisitionLatency.Record(elapsed.TotalMilliseconds);
        RecordSample(
            ref _sharedWakeAcquisitionSamples,
            ref _sharedWakeAcquisitionMicroseconds,
            elapsed.TotalMilliseconds);
        if (count <= 0)
        {
            return;
        }

        if (expansion)
        {
            _sharedWakeExpansionsAcquired.Add(count);
            Interlocked.Add(ref _sharedWakeExpansionsAcquiredTotal, count);
        }
        else
        {
            _sharedWakeDeliveriesAcquired.Add(count);
            Interlocked.Add(ref _sharedWakeDeliveriesAcquiredTotal, count);
        }
    }

    public void SharedWakeStarted(bool expansion)
    {
        if (expansion)
        {
            Interlocked.Increment(ref _activeSharedWakeExpansions);
        }
        else
        {
            Interlocked.Increment(ref _activeSharedWakeDeliveries);
        }
    }

    public void SharedWakeFinished(
        bool expansion,
        bool succeeded,
        TimeSpan elapsed)
    {
        if (expansion)
        {
            Interlocked.Decrement(ref _activeSharedWakeExpansions);
            _sharedWakeExpansionDuration.Record(elapsed.TotalMilliseconds);
            RecordSample(
                ref _sharedWakeExpansionSamples,
                ref _sharedWakeExpansionMicroseconds,
                elapsed.TotalMilliseconds);
        }
        else
        {
            Interlocked.Decrement(ref _activeSharedWakeDeliveries);
            _sharedWakeDeliveryDuration.Record(elapsed.TotalMilliseconds);
            RecordSample(
                ref _sharedWakeDeliverySamples,
                ref _sharedWakeDeliveryMicroseconds,
                elapsed.TotalMilliseconds);
        }

        if (succeeded)
        {
            _sharedWakesCompleted.Add(1);
            Interlocked.Increment(ref _sharedWakesCompletedTotal);
        }
        else
        {
            _sharedWakesFailed.Add(1);
            Interlocked.Increment(ref _sharedWakesFailedTotal);
        }
    }

    public void RecordSharedWakeLeaseLost()
    {
        _sharedWakeLeasesLost.Add(1);
        Interlocked.Increment(ref _sharedWakeLeasesLostTotal);
    }

    public void RecordSharedWakeHeartbeatSucceeded(string workKind)
    {
        _sharedWakeHeartbeatsSucceeded.Add(
            1,
            new KeyValuePair<string, object?>("work_kind", workKind));
        if (string.Equals(
                workKind,
                SharedVariableWakeWorkKinds.Expansion,
                StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _sharedWakeExpansionHeartbeatsSucceededTotal);
        }
        else if (string.Equals(
                     workKind,
                     SharedVariableWakeWorkKinds.Delivery,
                     StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _sharedWakeDeliveryHeartbeatsSucceededTotal);
        }
    }

    public void RecordSharedWakeHeartbeatFailed(string workKind)
    {
        _sharedWakeHeartbeatsFailed.Add(
            1,
            new KeyValuePair<string, object?>("work_kind", workKind));
        if (string.Equals(
                workKind,
                SharedVariableWakeWorkKinds.Expansion,
                StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _sharedWakeExpansionHeartbeatsFailedTotal);
        }
        else if (string.Equals(
                     workKind,
                     SharedVariableWakeWorkKinds.Delivery,
                     StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _sharedWakeDeliveryHeartbeatsFailedTotal);
        }
    }

    public void RecordSharedWakeRetry()
    {
        _sharedWakeRetries.Add(1);
        Interlocked.Increment(ref _sharedWakeRetriesTotal);
    }

    public void RecordSharedWakeFinalizationIncident()
    {
        _sharedWakeFinalizationIncidents.Add(1);
        Interlocked.Increment(ref _sharedWakeFinalizationIncidentsTotal);
    }

    public void RecordSharedWakeIncidentSnapshot(long openIncidents) =>
        Interlocked.Exchange(ref _openSharedWakeIncidents, openIncidents);

    public void RecordSharedWakeDeliveriesCreated(int count)
    {
        if (count <= 0)
        {
            return;
        }
        _sharedWakeDeliveriesCreated.Add(count);
        Interlocked.Add(ref _sharedWakeDeliveriesCreatedTotal, count);
    }

    public void RecordSharedWakeCleanup(int wakes, int deliveries, int incidents)
    {
        if (wakes > 0)
        {
            _sharedWakeCleanupWakes.Add(wakes);
            Interlocked.Add(ref _sharedWakeCleanupWakesTotal, wakes);
        }
        if (deliveries > 0)
        {
            _sharedWakeCleanupDeliveries.Add(deliveries);
            Interlocked.Add(ref _sharedWakeCleanupDeliveriesTotal, deliveries);
        }
        if (incidents > 0)
        {
            _sharedWakeCleanupIncidents.Add(incidents);
            Interlocked.Add(ref _sharedWakeCleanupIncidentsTotal, incidents);
        }
    }

    public bool IsReady => Volatile.Read(ref _ready) == 1;

    public string ExportPrometheus()
    {
        var output = new StringBuilder(2048);
        WriteGauge(output, "flowbit_worker_ready", Volatile.Read(ref _ready));
        WriteGauge(output, "flowbit_worker_jobs_active", Volatile.Read(ref _active));
        WriteGauge(
            output,
            "flowbit_worker_jobs_activity_active",
            Volatile.Read(ref _activeActivities));
        WriteGauge(
            output,
            "flowbit_worker_shared_wake_expansions_active",
            Volatile.Read(ref _activeSharedWakeExpansions));
        WriteGauge(
            output,
            "flowbit_worker_shared_wake_deliveries_active",
            Volatile.Read(ref _activeSharedWakeDeliveries));
        WriteGauge(
            output,
            "flowbit_worker_queue_runnable_depth",
            Interlocked.Read(ref _runnableDepth));
        WriteGauge(
            output,
            "flowbit_worker_queue_oldest_age_milliseconds",
            Volatile.Read(ref _oldestRunnableAgeMilliseconds));
        WriteGauge(
            output,
            "flowbit_worker_incidents_open",
            Interlocked.Read(ref _openIncidents));
        WriteGauge(
            output,
            "flowbit_worker_shared_wake_incidents_open",
            Interlocked.Read(ref _openSharedWakeIncidents));

        WriteCounter(output, "flowbit_worker_jobs_acquired_total", _acquiredTotal);
        WriteCounter(output, "flowbit_worker_jobs_processed_total", _processedTotal);
        WriteCounter(output, "flowbit_worker_jobs_failed_total", _failedTotal);
        WriteCounter(output, "flowbit_worker_leases_lost_total", _leaseLostTotal);
        WriteCounter(output, "flowbit_worker_retries_scheduled_total", _retriesTotal);
        WriteCounter(output, "flowbit_worker_cleanup_jobs_total", _cleanupJobsTotal);
        WriteCounter(output, "flowbit_worker_cleanup_attempts_total", _cleanupAttemptsTotal);
        WriteCounter(output, "flowbit_worker_cleanup_snapshots_total", _cleanupSnapshotsTotal);
        WriteCounter(output, "flowbit_worker_cleanup_incidents_total", _cleanupIncidentsTotal);
        WriteCounter(output, "flowbit_worker_timer_start_subscriptions_total", _timerStartsTotal);
        WriteCounter(output, "flowbit_worker_shared_wake_expansions_acquired_total", _sharedWakeExpansionsAcquiredTotal);
        WriteCounter(output, "flowbit_worker_shared_wake_deliveries_acquired_total", _sharedWakeDeliveriesAcquiredTotal);
        WriteCounter(output, "flowbit_worker_shared_wake_completed_total", _sharedWakesCompletedTotal);
        WriteCounter(output, "flowbit_worker_shared_wake_failed_total", _sharedWakesFailedTotal);
        WriteCounter(output, "flowbit_worker_shared_wake_leases_lost_total", _sharedWakeLeasesLostTotal);
        WriteWorkKindCounter(
            output,
            "flowbit_worker_shared_wake_heartbeat_succeeded_total",
            _sharedWakeExpansionHeartbeatsSucceededTotal,
            _sharedWakeDeliveryHeartbeatsSucceededTotal);
        WriteWorkKindCounter(
            output,
            "flowbit_worker_shared_wake_heartbeat_failed_total",
            _sharedWakeExpansionHeartbeatsFailedTotal,
            _sharedWakeDeliveryHeartbeatsFailedTotal);
        WriteCounter(output, "flowbit_worker_shared_wake_retries_scheduled_total", _sharedWakeRetriesTotal);
        WriteCounter(output, "flowbit_worker_shared_wake_incidents_finalization_opened_total", _sharedWakeFinalizationIncidentsTotal);
        WriteCounter(output, "flowbit_worker_shared_wake_deliveries_created_total", _sharedWakeDeliveriesCreatedTotal);
        WriteCounter(output, "flowbit_worker_shared_wake_cleanup_wakes_total", _sharedWakeCleanupWakesTotal);
        WriteCounter(output, "flowbit_worker_shared_wake_cleanup_deliveries_total", _sharedWakeCleanupDeliveriesTotal);
        WriteCounter(output, "flowbit_worker_shared_wake_cleanup_incidents_total", _sharedWakeCleanupIncidentsTotal);
        WriteCounter(output, "flowbit_jobs_retries_total", _runtimeRetriesTotal);
        WriteCounter(output, "flowbit_jobs_output_conflicts_total", _runtimeConflictsTotal);
        WriteCounter(output, "flowbit_jobs_incidents_opened_total", _runtimeIncidentsTotal);
        WriteCounter(
            output,
            "flowbit_jobs_automatic_loop_limit_total",
            _runtimeAutomaticLoopLimitsTotal);

        WriteSummary(
            output,
            "flowbit_worker_acquisition_duration_seconds",
            _acquisitionSamples,
            _acquisitionMicroseconds);
        WriteSummary(
            output,
            "flowbit_worker_processing_duration_seconds",
            _processingSamples,
            _processingMicroseconds);
        WriteSummary(
            output,
            "flowbit_worker_queue_lag_seconds",
            _queueLagSamples,
            _queueLagMicroseconds);
        WriteSummary(
            output,
            "flowbit_worker_timer_lateness_seconds",
            _timerLatenessSamples,
            _timerLatenessMicroseconds);
        WriteSummary(
            output,
            "flowbit_worker_shared_wake_acquisition_duration_seconds",
            _sharedWakeAcquisitionSamples,
            _sharedWakeAcquisitionMicroseconds);
        WriteSummary(
            output,
            "flowbit_worker_shared_wake_expansion_duration_seconds",
            _sharedWakeExpansionSamples,
            _sharedWakeExpansionMicroseconds);
        WriteSummary(
            output,
            "flowbit_worker_shared_wake_delivery_duration_seconds",
            _sharedWakeDeliverySamples,
            _sharedWakeDeliveryMicroseconds);
        WriteSummary(
            output,
            "flowbit_jobs_instance_lock_wait_seconds",
            _instanceLockWaitSamples,
            _instanceLockWaitMicroseconds);
        return output.ToString();
    }

    private void OnRuntimeLongMeasurement(
        Instrument instrument,
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        switch (instrument.Name)
        {
            case "flowbit.jobs.retries":
                Interlocked.Add(ref _runtimeRetriesTotal, measurement);
                break;
            case "flowbit.jobs.output_conflicts":
                Interlocked.Add(ref _runtimeConflictsTotal, measurement);
                break;
            case "flowbit.jobs.incidents.opened":
                Interlocked.Add(ref _runtimeIncidentsTotal, measurement);
                break;
            case "flowbit.jobs.automatic_loop_limit":
                Interlocked.Add(ref _runtimeAutomaticLoopLimitsTotal, measurement);
                break;
        }
    }

    private void OnRuntimeDoubleMeasurement(
        Instrument instrument,
        double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (instrument.Name == "flowbit.jobs.instance_lock.wait")
        {
            RecordSample(
                ref _instanceLockWaitSamples,
                ref _instanceLockWaitMicroseconds,
                measurement);
        }
    }

    private static void RecordSample(
        ref long count,
        ref long microseconds,
        double milliseconds)
    {
        Interlocked.Increment(ref count);
        Interlocked.Add(
            ref microseconds,
            Math.Max(0, checked((long)Math.Round(milliseconds * 1000d))));
    }

    private static void WriteGauge(StringBuilder output, string name, long value)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" gauge");
        output.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).AppendLine();
    }

    private static void WriteGauge(StringBuilder output, string name, double value)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" gauge");
        output.Append(name).Append(' ').Append(value.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
    }

    private static void WriteCounter(StringBuilder output, string name, long value)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" counter");
        output.Append(name).Append(' ')
            .Append(Interlocked.Read(ref value).ToString(CultureInfo.InvariantCulture))
            .AppendLine();
    }

    private static void WriteWorkKindCounter(
        StringBuilder output,
        string name,
        long expansionValue,
        long deliveryValue)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" counter");
        output.Append(name).Append("{work_kind=\"expansion\"} ")
            .Append(Interlocked.Read(ref expansionValue).ToString(CultureInfo.InvariantCulture))
            .AppendLine();
        output.Append(name).Append("{work_kind=\"delivery\"} ")
            .Append(Interlocked.Read(ref deliveryValue).ToString(CultureInfo.InvariantCulture))
            .AppendLine();
    }

    private static void WriteSummary(
        StringBuilder output,
        string name,
        long count,
        long microseconds)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" summary");
        output.Append(name).Append("_count ")
            .Append(Interlocked.Read(ref count).ToString(CultureInfo.InvariantCulture))
            .AppendLine();
        output.Append(name).Append("_sum ")
            .Append((Interlocked.Read(ref microseconds) / 1_000_000d)
                .ToString("R", CultureInfo.InvariantCulture))
            .AppendLine();
    }

    public void Dispose()
    {
        _runtimeListener.Dispose();
        _meter.Dispose();
    }
}
