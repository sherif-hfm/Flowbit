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
using WebhookTelemetry = FlowbitWorker::Flowbit.Worker.WebhookTelemetry;

namespace Flowbit.Tests;

public sealed class WorkerOperationalSurfaceTests
{
    [Fact]
    public async Task OperationalEndpointsExposeLivenessReadinessAndMetrics()
    {
        var builder = WebApplication.CreateBuilder();
        TestWebHostDefaults.IsolateFromMachine(builder);
        builder.WebHost.UseTestServer();
        var telemetry = new WorkerTelemetry();
        builder.Services.AddSingleton(telemetry);
        using var webhooks = new WebhookTelemetry(TimeProvider.System);
        webhooks.RecordAttempt(
            new WebhookSendOutcome("http_retryable", 503, null, 120),
            new WebhookFinalizeResult(true, "retryScheduled", false));
        builder.Services.AddSingleton(webhooks);
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
        Assert.DoesNotContain(
            "flowbit_worker_shared_wake",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains("flowbit_webhooks_attempts_total{outcome=\"http_retryable\"} 1", metrics, StringComparison.Ordinal);
        Assert.Contains("flowbit_webhooks_queue_depth{state=\"failed\"} 0", metrics, StringComparison.Ordinal);
        Assert.Contains("flowbit_webhooks_send_duration_seconds_sum 0.12", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public void CallActivityMeasurementsReachThePrometheusExport()
    {
        using var telemetry = new WorkerTelemetry();
        using var callMeter = new Meter("Flowbit.Runtime.CallActivities");

        // Real calls in other tests can add to the unlabeled totals, never the unique label.
        var events = callMeter.CreateCounter<long>("flowbit.call.events");
        events.Add(
            3,
            new KeyValuePair<string, object?>("event", "workerSurfaceTest"),
            new KeyValuePair<string, object?>("failure_code", "call_surface_test"));
        callMeter.CreateCounter<long>("flowbit.call.fence_stops").Add(2);
        callMeter.CreateHistogram<double>("flowbit.call.start_queue_age", "s").Record(1.5);
        callMeter.CreateHistogram<double>("flowbit.call.cancel_age", "s").Record(0.25);

        var metrics = telemetry.ExportPrometheus();

        Assert.Contains("# TYPE flowbit_call_events_total counter", metrics, StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_call_events_total{event=\"workerSurfaceTest\",failure_code=\"call_surface_test\"} 3",
            metrics,
            StringComparison.Ordinal);
        Assert.True(ReadMetric(metrics, "flowbit_call_fence_stops_total") >= 2);
        Assert.True(ReadMetric(metrics, "flowbit_call_start_queue_age_seconds_count") >= 1);
        Assert.True(ReadMetric(metrics, "flowbit_call_start_queue_age_seconds_sum") >= 1.5);
        Assert.True(ReadMetric(metrics, "flowbit_call_cancel_age_seconds_count") >= 1);
        Assert.True(ReadMetric(metrics, "flowbit_call_cancel_age_seconds_sum") >= 0.25);
    }

    private static double ReadMetric(string metrics, string name)
    {
        var line = Assert.Single(
            metrics.Split('\n'),
            candidate => candidate.StartsWith(name + " ", StringComparison.Ordinal));
        return double.Parse(line[(name.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void HealthListenUrlMustBeAnAbsoluteHttpEndpoint()
    {
        var options = new WorkerOptions { HealthListenUrl = "not-a-url" };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("HealthListenUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LeaseBoundsAreValidated()
    {
        Assert.DoesNotContain(
            typeof(WorkerOptions).GetProperties(),
            property => property.Name.StartsWith("SharedWake", StringComparison.Ordinal));

        var shortLease = new WorkerOptions { LeaseSeconds = 14 };
        var longLease = new WorkerOptions { LeaseSeconds = 1801 };
        var lateLeaseCheck = new WorkerOptions
        {
            LeaseSeconds = 15,
            HeartbeatSeconds = 1,
            LeaseCheckMilliseconds = 60_000
        };

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
