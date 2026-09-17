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
using Flowbit.Service.Abstractions;

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

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        services.AddSingleton(dataSource);
        services.AddSingleton(new RetentionDataSource(connectionString));
        services.AddScoped<IRetentionRepository, RetentionRepository>();
        services.TryAddSingleton(new ServiceTaskOptions());
        services.TryAddSingleton(new MessageDeliveryOptions());
        services.TryAddSingleton(new ScriptOptions());
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider));
        services.AddMemoryCache();
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
        services.AddScoped<IWorkflowSettingsRepository, WorkflowSettingsRepository>();
        services.AddScoped<IEngineSettingsRepository, EngineSettingsRepository>();
        services.AddScoped<IUserDelegationRepository, UserDelegationRepository>();
        services.AddScoped<IAdministrativeActionBatchRepository, AdministrativeActionBatchRepository>();
        services.AddScoped<IInstanceVersionChangeBatchRepository, InstanceVersionChangeBatchRepository>();
        services.AddScoped<IInstanceVariableUpdateRepository, InstanceVariableUpdateRepository>();
        services.AddScoped<IInstanceVariableUpdateBatchRepository, InstanceVariableUpdateBatchRepository>();
        services.AddScoped<IWorkflowJobRepository, WorkflowJobRepository>();
        services.AddScoped<ITimerSubscriptionRepository, TimerSubscriptionRepository>();
        services.AddScoped<IConditionalBoundarySubscriptionRepository, ConditionalBoundarySubscriptionRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<DatabaseInitializer>();
        services.AddHttpClient<IServiceTaskInvoker, HttpServiceTaskInvoker>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IScriptEvaluator, JintScriptEvaluator>();

        return services;
    }
}
