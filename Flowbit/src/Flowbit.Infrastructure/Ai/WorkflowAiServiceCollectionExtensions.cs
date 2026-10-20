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
        section.Bind(options);
        var configured = section.GetChildren().Select(child => child.Key).ToArray();
        // Settings of the replaced engine. An engine selection or an enabled review would silently change behaviour if ignored,
        // so they stop AI requests; the other tuning settings only produce a startup warning.
        var removed = configured.Where(key => WorkflowAiOptions.RemovedSettings.Contains(key, StringComparer.OrdinalIgnoreCase)).ToArray();
        options.UnsupportedSettings = removed.Where(key => key.Equals("ExecutionVariant", StringComparison.OrdinalIgnoreCase)
            || key.Equals("RequirementsReviewEnabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(section[key], out var enabled) && enabled).ToArray();
        options.IgnoredSettings = removed.Except(options.UnsupportedSettings, StringComparer.OrdinalIgnoreCase).ToArray();
        // Models, endpoints and keys are configured on the AI models page now; the old provider settings only produce a warning.
        options.ConfiguredRelocatedSettings = configured
            .Where(key => WorkflowAiOptions.RelocatedSettings.Contains(key, StringComparer.OrdinalIgnoreCase)).ToArray();
        return options;
    }

    public static IServiceCollection AddWorkflowAiAuthoring(this IServiceCollection services, WorkflowAiOptions? options = null)
    {
        services.AddSingleton(options ?? new WorkflowAiOptions());
        services.AddSingleton<WorkflowAiConcurrencyGate>();
        services.TryAddScoped<WorkflowDefinitionReadinessChecker>();
        services.AddHttpClient(OpenAiCompatibleChatClientFactory.HttpClientName)
            .RedactLoggedHeaders(_ => true)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IWorkflowAiChatClientFactory>(provider => new OpenAiCompatibleChatClientFactory(
            () => provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(OpenAiCompatibleChatClientFactory.HttpClientName)));
        services.AddSingleton<WorkflowAiDocs>();
        services.AddScoped<IWorkflowAiAuthoringService, WorkflowAiAuthoringService>();
        return services;
    }
}
