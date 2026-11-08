using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public static class EngineSettingWriteRules
{
    public static string NormalizeActor(string actor)
    {
        var normalized = actor?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > UserTaskConstraints.MaxActorNameLength)
            throw new WorkflowUnauthorizedException("An authenticated actor of at most 300 characters is required.");
        return normalized;
    }

    public static string NormalizeValue(string? settingNamespace, string key, string value)
    {
        if (!LifecycleCaptureSetting.IsReserved(settingNamespace, key)) return value;
        if (!LifecycleCaptureSetting.IsCanonical(settingNamespace, key))
            throw new WorkflowDomainException($"Use namespace '{LifecycleCaptureSetting.Namespace}' and key '{LifecycleCaptureSetting.Key}' exactly for lifecycle capture.");
        if (!LifecycleCaptureSetting.TryNormalizeValue(value, out var normalized))
            throw new WorkflowDomainException($"{LifecycleCaptureSetting.LogicalKey} must be 'true' or 'false'.");
        return normalized;
    }

    public static void RejectCaptureDeletion(string? settingNamespace, string key)
    {
        if (LifecycleCaptureSetting.IsReserved(settingNamespace, key))
            throw new WorkflowDomainException($"{LifecycleCaptureSetting.LogicalKey} cannot be deleted. Set its value to 'false' to disable capture.");
    }
}
