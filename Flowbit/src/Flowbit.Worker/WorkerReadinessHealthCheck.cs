using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Flowbit.Worker;

public sealed class WorkerReadinessHealthCheck(
    WorkerTelemetry telemetry,
    DatabaseSchemaGate? schemaGate = null) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (schemaGate is { IsReady: false })
        {
            var pending = schemaGate.PendingMigrationCount;
            return Task.FromResult(HealthCheckResult.Unhealthy(pending > 0
                ? $"Waiting for {pending} pending database migrations to be applied."
                : "The database migration history has not been verified."));
        }

        return Task.FromResult(telemetry.IsReady
            ? HealthCheckResult.Healthy("The durable queue has been queried successfully.")
            : HealthCheckResult.Unhealthy("The durable queue has not completed its first query."));
    }
}
