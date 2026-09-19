using System.Text.Json;

namespace Flowbit.Ui.Components.Shared.InstanceDetails;

/// <summary>
/// Pure display formatting shared by the instance-detail display components.
/// Formatting only: no mutation, actor evaluation, or page-state logic.
/// </summary>
internal static class InstanceDetailFormatting
{
    internal static string FormatJson(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();
}
