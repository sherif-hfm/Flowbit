using Flowbit.Service.Ai;
using Flowbit.Service.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flowbit.Infrastructure.Ai;

public static class WorkflowAiServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowAiAuthoring(this IServiceCollection services, WorkflowAiOptions? options = null)
    {
        services.AddSingleton(options ?? new WorkflowAiOptions());
        services.AddSingleton<WorkflowAiConcurrencyGate>();
        services.TryAddScoped<WorkflowDefinitionReadinessChecker>();
        services.AddHttpClient<OpenCodeGoProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RedactLoggedHeaders(_ => true)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<IAiWorkflowProvider>(provider => provider.GetRequiredService<OpenCodeGoProvider>());
        services.AddScoped<IWorkflowAiAuthoringService, WorkflowAiAuthoringService>();
        return services;
    }
}

