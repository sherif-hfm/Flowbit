extern alias FlowbitWorker;

using System.Diagnostics.Metrics;
using System.Net;
using Flowbit.Service.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerOperationalEndpoints = FlowbitWorker::Flowbit.Worker.WorkerOperationalEndpoints;
using WorkerReadinessHealthCheck = FlowbitWorker::Flowbit.Worker.WorkerReadinessHealthCheck;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

public sealed class WorkerOperationalSurfaceTests
{
    [Fact]
    public async Task OperationalEndpointsExposeLivenessReadinessAndMetrics()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var telemetry = new WorkerTelemetry();
        builder.Services.AddSingleton(telemetry);
        builder.Services.AddHealthChecks()
            .AddCheck(
                "self",
                static () => HealthCheckResult.Healthy(),
                tags: ["live"])
            .AddCheck<WorkerReadinessHealthCheck>("durable-queue", tags: ["ready"]);
        await using var app = builder.Build();
        WorkerOperationalEndpoints.MapWorkerOperationalEndpoints(app);
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await client.GetAsync("/health/ready")).StatusCode);

        telemetry.RecordAcquisition(
            Array.Empty<WorkflowJobLeaseRecord>(),
            TimeSpan.FromMilliseconds(12.5),
            DateTimeOffset.UtcNow);
        telemetry.RecordSharedWakeAcquisition(
            expansion: true,
            count: 2,
            TimeSpan.FromMilliseconds(5));
        telemetry.SharedWakeStarted(expansion: true);
        telemetry.RecordSharedWakeDeliveriesCreated(7);
        telemetry.RecordSharedWakeHeartbeatSucceeded(
            SharedVariableWakeWorkKinds.Expansion);
        telemetry.RecordSharedWakeHeartbeatSucceeded(
            SharedVariableWakeWorkKinds.Delivery);
        telemetry.RecordSharedWakeHeartbeatFailed(
            SharedVariableWakeWorkKinds.Delivery);
        telemetry.RecordSharedWakeRetry();
        telemetry.RecordSharedWakeFinalizationIncident();
        telemetry.RecordSharedWakeIncidentSnapshot(9);
        telemetry.RecordSharedWakeCleanup(wakes: 3, deliveries: 4, incidents: 1);
        telemetry.SharedWakeFinished(
            expansion: true,
            succeeded: true,
            TimeSpan.FromMilliseconds(20));
        using var runtimeMeter = new Meter("Flowbit.Runtime.Jobs");
        runtimeMeter.CreateCounter<long>("flowbit.jobs.output_conflicts").Add(2);
        runtimeMeter.CreateCounter<long>("flowbit.jobs.automatic_loop_limit").Add(1);
        runtimeMeter.CreateHistogram<double>("flowbit.jobs.instance_lock.wait").Record(25);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var metricsResponse = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, metricsResponse.StatusCode);
        Assert.StartsWith("text/plain", metricsResponse.Content.Headers.ContentType?.MediaType);
        var metrics = await metricsResponse.Content.ReadAsStringAsync();
        Assert.Contains("flowbit_worker_ready 1", metrics, StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_acquisition_duration_seconds_count 1",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_acquisition_duration_seconds_sum 0.0125",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_jobs_output_conflicts_total 2",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_jobs_automatic_loop_limit_total 1",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_jobs_instance_lock_wait_seconds_sum 0.025",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_expansions_acquired_total 2",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_deliveries_created_total 7",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_retries_scheduled_total 1",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_heartbeat_succeeded_total{work_kind=\"expansion\"} 1",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_heartbeat_succeeded_total{work_kind=\"delivery\"} 1",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_heartbeat_failed_total{work_kind=\"expansion\"} 0",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_heartbeat_failed_total{work_kind=\"delivery\"} 1",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_incidents_finalization_opened_total 1",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_incidents_open 9",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_cleanup_deliveries_total 4",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_shared_wake_expansion_duration_seconds_sum 0.02",
            metrics,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HealthListenUrlMustBeAnAbsoluteHttpEndpoint()
    {
        var options = new WorkerOptions { HealthListenUrl = "not-a-url" };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("HealthListenUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedWakeDefaultsAndBoundsAreValidated()
    {
        var defaults = new WorkerOptions();

        Assert.Equal(8, defaults.SharedWakeMaxConcurrency);
        Assert.Equal(2, defaults.SharedWakeExpansionConcurrency);
        Assert.Equal(32, defaults.SharedWakeBatchSize);
        Assert.Equal(500, defaults.SharedWakeExpansionPageSize);

        var concurrency = new WorkerOptions
        {
            SharedWakeMaxConcurrency = 1,
            SharedWakeExpansionConcurrency = 2
        };
        var page = new WorkerOptions { SharedWakeExpansionPageSize = 501 };
        var shortLease = new WorkerOptions { LeaseSeconds = 14 };
        var longLease = new WorkerOptions { LeaseSeconds = 1801 };
        var lateLeaseCheck = new WorkerOptions
        {
            LeaseSeconds = 15,
            HeartbeatSeconds = 1,
            LeaseCheckMilliseconds = 60_000
        };

        Assert.Contains(
            "SharedWakeExpansionConcurrency",
            Assert.Throws<InvalidOperationException>(concurrency.Validate).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "SharedWakeExpansionPageSize",
            Assert.Throws<InvalidOperationException>(page.Validate).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "LeaseSeconds",
            Assert.Throws<InvalidOperationException>(shortLease.Validate).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "LeaseSeconds",
            Assert.Throws<InvalidOperationException>(longLease.Validate).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "LeaseCheckMilliseconds",
            Assert.Throws<InvalidOperationException>(lateLeaseCheck.Validate).Message,
            StringComparison.Ordinal);
    }
}
