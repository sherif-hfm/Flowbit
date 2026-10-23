using Flowbit.Infrastructure.DependencyInjection;
using Flowbit.Service.Abstractions;
using Flowbit.Service.DependencyInjection;
using Flowbit.Service.Services;
using Flowbit.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

var workerOptions = builder.Configuration
    .GetSection(WorkerOptions.SectionName)
    .Get<WorkerOptions>() ?? new WorkerOptions();
workerOptions.Validate();
var connectionPool = WorkerConnectionPool.Apply(
    builder.Configuration,
    workerOptions.MaxConcurrency);
builder.WebHost.UseUrls(workerOptions.HealthListenUrl);
builder.Services.AddSingleton(workerOptions);
builder.Services.AddSingleton(TimeProvider.System);
// Running jobs may finish during the drain window; jobs still running then
// get a bounded grace period to unwind and release their leases.
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromSeconds(
        workerOptions.ShutdownDrainSeconds + 10));

var contextOptions = builder.Configuration
    .GetSection(WorkflowContextOptions.SectionName)
    .Get<WorkflowContextOptions>() ?? new WorkflowContextOptions();
builder.Services.AddSingleton(contextOptions);
builder.Services.AddSingleton(provider => provider.GetRequiredService<IConfiguration>()
    .GetSection(WorkflowAuditOptions.SectionName)
    .Get<WorkflowAuditOptions>() ?? new WorkflowAuditOptions());

var serviceTaskOptions = builder.Configuration
    .GetSection(ServiceTaskOptions.SectionName)
    .Get<ServiceTaskOptions>() ?? new ServiceTaskOptions();
if (serviceTaskOptions.MaxTimeoutSeconds <= 0 || serviceTaskOptions.MaxResponseBodyBytes <= 0)
{
    throw new InvalidOperationException("WorkflowServiceTasks limits must be positive.");
}
builder.Services.AddSingleton(serviceTaskOptions);

var scriptOptions = builder.Configuration
    .GetSection(ScriptOptions.SectionName)
    .Get<ScriptOptions>() ?? new ScriptOptions();
scriptOptions.Validate();
builder.Services.AddSingleton(scriptOptions);
builder.Services.AddSingleton(new MessageDeliveryOptions());

builder.Services
    .AddServiceLayer()
    .AddInfrastructure(builder.Configuration);

// Durable processing waits until every migration in this build is applied.
builder.Services.AddSingleton(provider => new DatabaseSchemaGate(
    cancellationToken => DatabaseSchemaGate.ReadMigrationStatusAsync(
        provider.GetRequiredService<IServiceScopeFactory>(),
        cancellationToken),
    provider.GetRequiredService<TimeProvider>(),
    provider.GetRequiredService<ILogger<DatabaseSchemaGate>>()));
builder.Services.AddHostedService(provider =>
    provider.GetRequiredService<DatabaseSchemaGate>());
builder.Services.AddSingleton<PostgresJobWakeupSignal>();
builder.Services.AddSingleton<IJobWakeupSignal>(provider =>
    provider.GetRequiredService<PostgresJobWakeupSignal>());
builder.Services.AddHostedService(provider =>
    provider.GetRequiredService<PostgresJobWakeupSignal>());
builder.Services.AddHostedService<JobDispatcher>();
builder.Services.AddHostedService<QueueTelemetryService>();
builder.Services.AddHostedService<RetentionCleanupService>();
builder.Services.AddHostedService<TimerStartReconciliationService>();
builder.Services.AddSingleton<WorkerTelemetry>();
builder.Services.AddHealthChecks()
    .AddCheck(
        "self",
        static () => HealthCheckResult.Healthy(),
        tags: ["live"])
    .AddCheck<WorkerReadinessHealthCheck>("durable-queue", tags: ["ready"]);

var app = builder.Build();
if (connectionPool.IsConstrained)
{
    app.Logger.LogWarning(
        "The configured connection pool maximum ({MaxPoolSize}) is below FlowbitWorker:MaxConcurrency + {Reserved} ({Recommended}). Lease checks may time out under load; raise Maximum Pool Size or lower MaxConcurrency.",
        connectionPool.MaxPoolSize,
        WorkerConnectionPool.ReservedConnections,
        connectionPool.RecommendedMaxPoolSize);
}
app.MapWorkerOperationalEndpoints();

await app.RunAsync();
