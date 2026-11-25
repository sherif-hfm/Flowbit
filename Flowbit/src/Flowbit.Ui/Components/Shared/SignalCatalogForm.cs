using System.Globalization;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Ui.Components.Shared;

/// <summary>One row of the Signal catalog page's field table, as typed.</summary>
internal sealed class SignalCatalogFieldRow
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = WorkflowVariableTypes.String;
    public bool IsArray { get; set; }
    public bool Required { get; set; }
    public string? Description { get; set; }

    /// <summary>The field as a request sends it: a trimmed name and a trimmed description, blank meaning none.</summary>
    public SignalCatalogFieldDto ToDto() => new(
        Name.Trim(),
        DataType,
        IsArray,
        Required,
        SignalCatalogEntryRules.NormalizeDescription(Description));
}

/// <summary>
/// The Signal catalog page's form: an entry being created or edited, as typed. It reads itself
/// into the API's requests and checks them with <see cref="SignalCatalogEntryRules"/> before any
/// request, and gives a fingerprint that tells whether a review still describes it.
/// </summary>
internal sealed class SignalCatalogForm
{
    private SignalCatalogForm(string? editingName, DateTimeOffset? expectedUpdatedAt)
    {
        EditingName = editingName;
        ExpectedUpdatedAt = expectedUpdatedAt;
    }

    /// <summary>The name of the entry being edited, or null for a new entry.</summary>
    public string? EditingName { get; }

    /// <summary>The edited entry's <c>updatedAt</c> as last read.</summary>
    public DateTimeOffset? ExpectedUpdatedAt { get; }

    public bool IsEditing => EditingName is not null;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<SignalCatalogFieldRow> Fields { get; } = [];

    /// <summary>The throw roles as one comma-separated text.</summary>
    public string RolesText { get; set; } = string.Empty;

    public bool AllowClients { get; set; } = true;
    public bool AllowWorkflows { get; set; } = true;

    public static SignalCatalogForm Create(string? name = null) => new(null, null) { Name = name ?? string.Empty };

    public static SignalCatalogForm Edit(SignalCatalogEntryDto entry)
    {
        var form = new SignalCatalogForm(entry.Name, entry.UpdatedAt)
        {
            Name = entry.Name,
            Description = entry.Description,
            RolesText = SignalCatalogFormatting.RolesText(entry.ThrowRoles),
            AllowClients = entry.AllowClients,
            AllowWorkflows = entry.AllowWorkflows
        };
        form.Fields.AddRange(entry.Fields.Select(field => new SignalCatalogFieldRow
        {
            Name = field.Name,
            DataType = field.DataType,
            IsArray = field.IsArray,
            Required = field.Required,
            Description = field.Description
        }));
        return form;
    }

    /// <summary>The name the requests use: the edited entry's, or the trimmed typed one.</summary>
    public string EntryName => EditingName ?? Name.Trim();

    /// <summary>The throw roles as a request sends them (<see cref="SignalCatalogFormatting.ParseRoles"/>).</summary>
    public IReadOnlyList<string> Roles => SignalCatalogFormatting.ParseRoles(RolesText);

    public IReadOnlyList<SignalCatalogFieldDto> FieldDtos => Fields.Select(row => row.ToDto()).ToList();

    public SignalCatalogFieldRow AddField()
    {
        var row = new SignalCatalogFieldRow();
        Fields.Add(row);
        return row;
    }

    /// <summary>The first problem of the form, under the API's rules, or null.</summary>
    public string? Problem() =>
        SignalCatalogEntryRules.Problem(EntryName, !IsEditing, Description, FieldDtos, Roles);

    /// <summary>The form as a candidate entry: what a creation sends, and what the preview checks.</summary>
    public CreateSignalCatalogEntryRequest ToCandidate() => new(
        EntryName,
        SignalCatalogEntryRules.NormalizeDescription(Description),
        FieldDtos,
        Roles,
        AllowClients,
        AllowWorkflows);

    public UpdateSignalCatalogEntryRequest ToUpdate()
    {
        var candidate = ToCandidate();
        return new UpdateSignalCatalogEntryRequest(
            ExpectedUpdatedAt ?? throw new InvalidOperationException("A new entry has no version."),
            candidate.Description,
            candidate.Fields,
            candidate.ThrowRoles,
            candidate.AllowClients,
            candidate.AllowWorkflows);
    }

    /// <summary>
    /// The candidate as text. A review describes the form only while the fingerprint it was made
    /// with equals the form's.
    /// </summary>
    public string Fingerprint() => JsonSerializer.Serialize(ToCandidate());
}

/// <summary>Pure presentation rules of the Signal catalog page.</summary>
internal static class SignalCatalogFormatting
{
    internal const string ClientsWarning =
        "API clients with signals.throw can still throw this signal; their roles are not checked.";

    internal const string RequiredRoleWarning =
        "Users need a role from Signals.RequiredRole as well as one of these roles. None of these roles is in Signals.RequiredRole, so only users who hold both can throw this signal; add the role there to let its holders throw it.";

    internal const string AllFitText = "The live workflow versions that use this name all fit.";

    internal const string SettingsAdministratorMessage =
        "This identity does not have the configured settings administrator role.";

    /// <summary>The data types of the type list, with the words the list shows.</summary>
    internal static IReadOnlyList<(string Value, string Label)> DataTypeOptions { get; } =
    [
        (WorkflowVariableTypes.String, "string"),
        (WorkflowVariableTypes.Number, "number"),
        (WorkflowVariableTypes.Boolean, "boolean"),
        (WorkflowVariableTypes.Date, "date"),
        (WorkflowVariableTypes.DateTime, "datetime"),
        (WorkflowVariableTypes.Json, "json")
    ];

    /// <summary>
    /// The roles of the comma-separated text: each segment trimmed, empty segments left out, and
    /// repeats in another letter case removed.
    /// </summary>
    internal static IReadOnlyList<string> ParseRoles(string? text) =>
        SignalCatalogEntryRules.NormalizeRoles(
            (text ?? string.Empty)
                .Split(',')
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0));

    internal static string RolesText(IReadOnlyList<string> roles) => string.Join(", ", roles);

    /// <summary>The warnings of the form, in the page's order.</summary>
    internal static IReadOnlyList<string> Warnings(
        IReadOnlyList<string> roles,
        bool allowClients,
        IReadOnlyList<string>? signalRoles)
    {
        var warnings = new List<string>();
        if (roles.Count > 0 && allowClients)
        {
            warnings.Add(ClientsWarning);
        }

        // Only when the roles of Signals.RequiredRole are known, from the list's reply.
        if (roles.Count > 0
            && signalRoles is not null
            && !roles.Any(role => signalRoles.Contains(role, StringComparer.OrdinalIgnoreCase)))
        {
            warnings.Add(RequiredRoleWarning);
        }

        return warnings;
    }

    /// <summary>An entry's throw roles in the list, or who may throw it when it lists none.</summary>
    internal static string RolesLabel(IReadOnlyList<string> roles) =>
        roles.Count == 0 ? "Any signal user" : string.Join(", ", roles);

    internal static string FieldCountText(int count) => count switch
    {
        0 => "No fields",
        1 => "1 field",
        _ => string.Create(CultureInfo.InvariantCulture, $"{count:N0} fields")
    };

    internal static string AllowedText(bool allowed) => allowed ? "Allowed" : "Not allowed";

    internal static string StatusLabel(string status) => status switch
    {
        SignalCatalogStatuses.Active => "Active",
        SignalCatalogStatuses.Archived => "Archived",
        _ => status
    };

    /// <summary>The mode in effect, in words.</summary>
    internal static string ModeText(string? mode) =>
        SignalCatalogModes.ParseMode(mode) == SignalCatalogModes.Required
            ? "Required: publishing a workflow and throwing through the API need an entry for every signal name."
            : "Optional: names without an entry keep working.";

    /// <summary>The five things that block archiving, with their counts, in the usage panel's order.</summary>
    internal static IReadOnlyList<(string Label, long Count)> UsageCounts(SignalCatalogUsageDto usage) =>
    [
        ("Published workflow versions that use it", usage.PublishedVersionCount),
        ("Workflow versions with a running instance that use it", usage.RunningVersionCount),
        ("Open signal waits for it", usage.ActiveWaitCount),
        ("Workflow families that start on it", usage.StartingFamilyCount),
        ("Broadcasts still delivering", usage.DeliveringBroadcastCount)
    ];

    /// <summary>Whether nothing blocks archiving.</summary>
    internal static bool CanArchive(SignalCatalogUsageDto usage) =>
        UsageCounts(usage).All(item => item.Count == 0);

    /// <summary>The version a problem is about, as the API's conflict messages name it.</summary>
    internal static string VersionLabel(SignalCatalogProblemDto problem) =>
        string.Create(CultureInfo.InvariantCulture, $"{problem.WorkflowKey} v{problem.Version} (#{problem.WorkflowDefinitionId})");

    internal static string VersionHref(long workflowDefinitionId) =>
        string.Create(CultureInfo.InvariantCulture, $"workflows/{workflowDefinitionId}/edit");

    internal static string VersionCountText(int count) =>
        count == 1 ? "1 version" : string.Create(CultureInfo.InvariantCulture, $"{count:N0} versions");
}
