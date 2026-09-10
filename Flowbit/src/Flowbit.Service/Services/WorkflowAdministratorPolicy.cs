using Flowbit.Service.Abstractions;

namespace Flowbit.Service.Services;

/// <summary>Dynamic role policy shared by workflow HTTP authorization and administrative execution.</summary>
public static class WorkflowAdministratorPolicy
{
    public const string RequiredRoleSettingKey = "Workflow.RequiredRole";
    public const string DefaultRequiredRole = "admin";

    public static IReadOnlyList<string> RequiredRoles(string? configuredRoles)
    {
        var roles = configuredRoles?.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return roles.Length == 0 ? [DefaultRequiredRole] : roles;
    }

    public static bool IsAuthorized(IReadOnlyCollection<string> actorRoles, string? configuredRoles)
    {
        var roles = actorRoles.Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RequiredRoles(configuredRoles).Any(roles.Contains);
    }

    public static async Task RequireAsync(
        ActorContext actor,
        IEngineSettingsRepository settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor.User))
        {
            throw new WorkflowUnauthorizedException("An authenticated administrative operator is required.");
        }

        // Read the current setting for every entry, including durable jobs using stored actor roles.
        var setting = await settings.GetByKeyAsync(RequiredRoleSettingKey, cancellationToken);
        if (!IsAuthorized(actor.Roles, setting?.Value))
        {
            throw new WorkflowForbiddenException("The workflow administrator role is required.");
        }
    }
}
