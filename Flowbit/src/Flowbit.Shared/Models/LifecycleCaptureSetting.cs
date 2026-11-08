namespace Flowbit.Shared.Models;

/// <summary>The canonical engine setting controlling database lifecycle-event capture.</summary>
public static class LifecycleCaptureSetting
{
    public const string Namespace = "Webhooks";
    public const string Key = "CaptureEnabled";
    public const string LogicalKey = Namespace + "." + Key;

    public static bool IsReserved(string? settingNamespace, string? key) =>
        IsReserved(string.IsNullOrWhiteSpace(settingNamespace)
            ? key?.Trim()
            : $"{settingNamespace.Trim()}.{key?.Trim()}");

    public static bool IsReserved(string? logicalKey) =>
        string.Equals(logicalKey?.Trim(), LogicalKey, StringComparison.OrdinalIgnoreCase);

    public static bool IsCanonical(string? settingNamespace, string? key) =>
        string.Equals(settingNamespace, Namespace, StringComparison.Ordinal)
        && string.Equals(key, Key, StringComparison.Ordinal);

    public static bool TryNormalizeValue(string? value, out string normalized)
    {
        var trimmed = value?.Trim();
        // Boolean.TryParse also ignores NUL padding, which is outside this setting's accepted input format.
        if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "true";
            return true;
        }

        if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "false";
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}
