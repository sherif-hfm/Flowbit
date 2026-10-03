using Flowbit.Service.Ai;
using Flowbit.Service.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Flowbit.Infrastructure.Ai;

public static class WorkflowAiServiceCollectionExtensions
{
    public static WorkflowAiOptions ReadWorkflowAiOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(WorkflowAiOptions.SectionName);
        var options = new WorkflowAiOptions();
        // The configuration binder appends to initialized lists. An explicit model list must
        // replace fallback models so discovery and its first/default model reflect configuration.
        if (section.GetChildren().Any(child => child.Key.Equals(nameof(WorkflowAiOptions.OpenCodeModels), StringComparison.OrdinalIgnoreCase)))
            options.OpenCodeModels.Clear();
        section.Bind(options);
        return options;
    }

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

