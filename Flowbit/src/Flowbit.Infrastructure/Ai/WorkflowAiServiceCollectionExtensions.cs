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
        // Settings of the replaced engine. An engine selection or an enabled review would silently change behaviour if ignored,
        // so they stop AI requests; the other tuning settings only produce a startup warning.
        var removed = section.GetChildren().Select(child => child.Key)
            .Where(key => WorkflowAiOptions.RemovedSettings.Contains(key, StringComparer.OrdinalIgnoreCase)).ToArray();
        options.UnsupportedSettings = removed.Where(key => key.Equals("ExecutionVariant", StringComparison.OrdinalIgnoreCase)
            || key.Equals("RequirementsReviewEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(section[key], out var enabled) && enabled).ToArray();
        options.IgnoredSettings = removed.Except(options.UnsupportedSettings, StringComparer.OrdinalIgnoreCase).ToArray();
        return options;
    }

    public static IServiceCollection AddWorkflowAiAuthoring(this IServiceCollection services, WorkflowAiOptions? options = null)
    {
        services.AddSingleton(options ?? new WorkflowAiOptions());
        services.AddSingleton<WorkflowAiConcurrencyGate>();
        services.TryAddScoped<WorkflowDefinitionReadinessChecker>();
        services.AddHttpClient(OpenCodeZenChatClientFactory.HttpClientName)
            .RedactLoggedHeaders(_ => true)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IWorkflowAiChatClientFactory>(provider => new OpenCodeZenChatClientFactory(provider.GetRequiredService<WorkflowAiOptions>(),
            () => provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(OpenCodeZenChatClientFactory.HttpClientName)));
        services.AddSingleton<WorkflowAiDocs>();
        services.AddScoped<IWorkflowAiAuthoringService, WorkflowAiAuthoringService>();
        return services;
    }
}
