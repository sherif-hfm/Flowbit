using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Http;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Infrastructure.Webhooks;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;

namespace Flowbit.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Flowbit")
            ?? configuration.GetConnectionString("WorkflowEngine")
            ?? throw new InvalidOperationException(
                "Connection string 'Flowbit' is missing (legacy key 'WorkflowEngine' is also supported).");

        // The container builds each pool, so it also disposes it when its host stops. A
        // pool registered as a ready-made instance is never disposed: its idle connections
        // stayed open until Npgsql pruned them, and short-lived hosts, such as tests', used
        // up the server's connection limit.
        services.AddSingleton(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.EnableDynamicJson();
            return dataSourceBuilder.Build();
        });
        services.AddSingleton(_ => new RetentionDataSource(connectionString));
        services.AddScoped<IRetentionRepository, RetentionRepository>();
        services.TryAddSingleton(new ServiceTaskOptions());
        services.TryAddSingleton(new MessageDeliveryOptions());
        services.TryAddSingleton(new ScriptOptions());
        services.AddDbContext<AppDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>(), FlowbitDatabase.ConfigureProvider));
        services.AddMemoryCache();
        services.AddScoped<ILocalIdentityRepository, LocalIdentityRepository>();
        services.AddSingleton<ILocalPasswordHasher, LocalPasswordHasher>();
        services.AddScoped<IWorkflowDefinitionRepository, WorkflowDefinitionRepository>();
        // One scoped concrete repository instance serves both the full runtime
        // port and the narrow instance-query port, preserving a shared DbContext
        // and per-scope bookkeeping across the engine and query services.
        services.AddScoped<WorkflowRuntimeRepository>();
        services.AddScoped<IWorkflowRuntimeRepository>(static provider =>
            provider.GetRequiredService<WorkflowRuntimeRepository>());
        services.AddScoped<IWorkflowInstanceQueryRepository>(static provider =>
            provider.GetRequiredService<WorkflowRuntimeRepository>());
        services.AddScoped<ISharedVariableRepository, SharedVariableRepository>();
        services.AddScoped<ISharedVariableClientRepository, SharedVariableClientRepository>();
        services.AddScoped<IAdministrativeActionCandidateRepository, AdministrativeActionCandidateRepository>();
        services.AddScoped<IInstanceVersionChangeCandidateRepository, InstanceVersionChangeCandidateRepository>();
        services.AddScoped<IInstanceVariableUpdateCandidateRepository, InstanceVariableUpdateCandidateRepository>();
        services.AddScoped<INodeExecutionQueryRepository, NodeExecutionQueryRepository>();
        services.AddScoped<IInstanceDiagramHistoryQueryRepository, InstanceDiagramHistoryQueryRepository>();
        services.AddScoped<IWorkflowSettingsRepository, WorkflowSettingsRepository>();
        services.AddScoped<IEngineSettingsRepository, EngineSettingsRepository>();
        services.AddScoped<IBusinessCalendarRepository, BusinessCalendarRepository>();
        services.AddScoped<IUserDelegationRepository, UserDelegationRepository>();
        services.AddScoped<IAdministrativeActionBatchRepository, AdministrativeActionBatchRepository>();
        services.AddScoped<IInstanceVersionChangeBatchRepository, InstanceVersionChangeBatchRepository>();
        services.AddScoped<IInstanceVariableUpdateRepository, InstanceVariableUpdateRepository>();
        services.AddScoped<IInstanceVariableUpdateBatchRepository, InstanceVariableUpdateBatchRepository>();
        services.AddScoped<IWorkflowJobRepository, WorkflowJobRepository>();
        services.AddScoped<IAutomaticMultiInstanceRepository, AutomaticMultiInstanceRepository>();
        services.AddScoped<IAutomaticMultiInstanceQueryRepository, AutomaticMultiInstanceQueryRepository>();
        services.AddScoped<ICallActivityRepository, CallActivityRepository>();
        services.AddScoped<ITimerSubscriptionRepository, TimerSubscriptionRepository>();
        services.AddScoped<IConditionalBoundarySubscriptionRepository, ConditionalBoundarySubscriptionRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<DatabaseInitializer>();

        // Hosts register their validated WebhookOptions first; this default serves tools and tests.
        services.TryAddSingleton(configuration.GetSection(WebhookOptions.SectionName).Get<WebhookOptions>()
            ?? new WebhookOptions());
        services.AddSingleton(provider => new WebhookDataSource(
            connectionString,
            provider.GetRequiredService<WebhookOptions>().ConnectionPoolSize));
        services.AddSingleton(provider => new WebhookDestinationPolicy(
            provider.GetRequiredService<WebhookOptions>().Network));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IWebhookSecretProtector, DataProtectionWebhookSecretProtector>();
        services.AddSingleton<IWebhookTransport, WebhookHttpTransport>();
        services.AddScoped<IWebhookRepository, WebhookRepository>();
        services.AddScoped<IWebhookDispatchRepository, WebhookDispatchRepository>();
        services.AddHttpClient<IServiceTaskInvoker, HttpServiceTaskInvoker>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IScriptEvaluator, JintScriptEvaluator>();

        return services;
    }
}
