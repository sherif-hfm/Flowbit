using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Flowbit.Worker;

/// <summary>
/// Reports webhook problems as degraded on <c>/health/ready</c> without failing workflow-job processing.
/// A receiver outage affects backlog and backoff metrics, not readiness.
/// </summary>
public sealed class WebhookReadinessHealthCheck(
    WebhookOptions options,
    IWebhookSecretProtector protector,
    WebhookTelemetry telemetry) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (telemetry.HasRecentFailure)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "A webhook fanout or delivery loop failed recently; see the Worker logs."));
        }
        if (!options.DeliveryEnabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Webhook delivery is disabled on this Worker; capture and queued deliveries are preserved."));
        }
        if (!protector.IsAvailable)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                protector.UnavailableReason ?? "Webhook key protection is unavailable."));
        }
        return Task.FromResult(HealthCheckResult.Healthy("Webhook delivery is enabled and its keys are usable."));
    }
}
