using System.Text.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Components.Shared.AdministrativeBatches;

/// <summary>
/// Pure presentation formatting shared by the administrative batch display
/// components and the remaining `/administrative-actions` page regions.
/// Fallbacks and comparison casing are copied verbatim from the page they were
/// extracted from.
/// </summary>
internal static class AdministrativeBatchFormatting
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static string ActionTitle(AdministrativeActionSummaryDto action) => string.IsNullOrWhiteSpace(action.Name)
        ? action.ActionKind == AdministrativeActionKinds.TimerBoundary ? action.BoundaryNodeName ?? $"Timer boundary #{action.BoundaryNodeId}" : $"Flow #{action.FlowId}"
        : action.Name;

    internal static string ActionLabel(AdministrativeActionSummaryDto action) =>
        $"{ActionTitle(action)} · {ActionKindLabel(action.ActionKind)} · {action.SourceNodeName} → {action.TargetNodeName} · flow #{action.FlowId}";

    internal static string ActionKindLabel(string value) =>
        value == AdministrativeActionKinds.TimerBoundary ? "Timer boundary" : "Task action";

    internal static string PositionKindLabel(string value) =>
        value == AdministrativeActionPositionKinds.MultiInstanceExecution ? "MI execution" : "User task";

    internal static string MultiInstanceModeLabel(string value) =>
        value == AdministrativeActionMultiInstanceModes.CompleteAllChildren ? "Complete all unfinished children" : "Force parent";

    internal static string RoleSnapshot(IReadOnlyList<string>? roles) =>
        roles is null || roles.Count == 0 ? "no roles" : string.Join(", ", roles);

    internal static string FormatJson(JsonElement? value) =>
        value is null ? string.Empty : JsonSerializer.Serialize(value.Value, JsonOptions);
}
