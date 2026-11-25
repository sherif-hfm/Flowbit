using System.Globalization;
using Flowbit.Shared.Dtos;

namespace Flowbit.Shared.Models;

/// <summary>
/// The rules of a signal catalog entry (EVENT-04F): its limits, how its description, fields,
/// and throw roles are normalized, and the first problem of a candidate entry. The API and
/// the Signal catalog page both use them, so the page can check a form before any request.
/// </summary>
public static class SignalCatalogEntryRules
{
    public const int MaxDescriptionLength = 1000;
    public const int MaxFields = SignalEventRules.MaxPayloadEntries;
    public const int MaxFieldDescriptionLength = 300;
    public const int MaxThrowRoles = 100;
    public const int MaxThrowRoleLength = 300;

    /// <summary>The most fields a message lists before it counts the others.</summary>
    public const int MaxListedFields = 20;

    /// <summary>The data types a field can have, in the order the messages list them.</summary>
    public static IReadOnlyList<string> DataTypes { get; } =
    [
        WorkflowVariableTypes.String,
        WorkflowVariableTypes.Number,
        WorkflowVariableTypes.Boolean,
        WorkflowVariableTypes.Date,
        WorkflowVariableTypes.DateTime,
        WorkflowVariableTypes.Json
    ];

    /// <summary>A trimmed description, or null when it is blank.</summary>
    public static string? NormalizeDescription(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The trimmed, case-insensitively deduplicated throw roles, in their first order. Blank and
    /// overlong roles are kept for <see cref="Problem"/> to report.
    /// </summary>
    public static IReadOnlyList<string> NormalizeRoles(IEnumerable<string?>? roles)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles ?? [])
        {
            var trimmed = role?.Trim() ?? string.Empty;
            if (trimmed.Length == 0 || seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    /// <summary>The fields with trimmed descriptions, a blank description becoming null.</summary>
    public static IReadOnlyList<SignalCatalogFieldDto> NormalizeFields(IEnumerable<SignalCatalogFieldDto?>? fields) =>
        (fields ?? [])
            .Select(field => field is null
                ? null!
                : field with { Description = NormalizeDescription(field.Description) })
            .ToList();

    /// <summary>
    /// The first problem of an entry's content, or null. The name is checked only when it is
    /// given, because an update keeps the entry's name.
    /// </summary>
    public static string? Problem(
        string? name,
        bool checkName,
        string? description,
        IReadOnlyList<SignalCatalogFieldDto?>? fields,
        IReadOnlyList<string?>? throwRoles)
    {
        if (checkName && SignalEventRules.ThrowNameProblem(name) is { } nameProblem)
        {
            return nameProblem;
        }

        if (ScalarCount(NormalizeDescription(description)) > MaxDescriptionLength)
        {
            return $"description can have at most {MaxDescriptionLength} characters.";
        }

        var fieldList = fields ?? [];
        if (fieldList.Count > MaxFields)
        {
            return $"fields can have at most {MaxFields} entries.";
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < fieldList.Count; index++)
        {
            var field = fieldList[index];
            if (field is null)
            {
                return $"Field {index + 1} cannot be null.";
            }

            if (!SignalEventRules.IsValidPayloadName(field.Name))
            {
                return $"Field {index + 1} name must be {SignalEventRules.PayloadNameRuleDescription}.";
            }

            if (!names.Add(field.Name))
            {
                return $"Field '{field.Name}' appears more than once.";
            }

            if (!DataTypes.Contains(field.DataType ?? string.Empty, StringComparer.Ordinal))
            {
                return $"Field '{field.Name}' dataType must be string, number, boolean, date, datetime, or json.";
            }

            if (ScalarCount(NormalizeDescription(field.Description)) > MaxFieldDescriptionLength)
            {
                return $"Field '{field.Name}' description can have at most {MaxFieldDescriptionLength} characters.";
            }
        }

        var roles = NormalizeRoles(throwRoles);
        if (roles.Count > MaxThrowRoles)
        {
            return $"throwRoles can have at most {MaxThrowRoles} roles.";
        }

        if (roles.Any(role => role.Length == 0))
        {
            return "A throw role cannot be blank.";
        }

        return roles.Any(role => ScalarCount(role) > MaxThrowRoleLength)
            ? $"A throw role can have at most {MaxThrowRoleLength} characters."
            : null;
    }

    /// <summary>
    /// A type in the words the messages use: <c>a string</c>, <c>an array of strings</c>, and so on.
    /// </summary>
    public static string TypeWords(string? dataType, bool isArray) => (dataType, isArray) switch
    {
        (WorkflowVariableTypes.String, false) => "a string",
        (WorkflowVariableTypes.String, true) => "an array of strings",
        (WorkflowVariableTypes.Number, false) => "a number",
        (WorkflowVariableTypes.Number, true) => "an array of numbers",
        (WorkflowVariableTypes.Boolean, false) => "true or false",
        (WorkflowVariableTypes.Boolean, true) => "an array of true or false values",
        (WorkflowVariableTypes.Date, false) => "a date (YYYY-MM-DD)",
        (WorkflowVariableTypes.Date, true) => "an array of dates (YYYY-MM-DD)",
        (WorkflowVariableTypes.DateTime, false) => "a date and time (ISO-8601)",
        (WorkflowVariableTypes.DateTime, true) => "an array of dates and times (ISO-8601)",
        (WorkflowVariableTypes.Json, false) => "any JSON value",
        (WorkflowVariableTypes.Json, true) => "an array of JSON values",
        _ => isArray ? $"an array of {dataType}" : dataType ?? "unknown"
    };

    /// <summary>A type as a declaration writes it: <c>string</c> or <c>string[]</c>.</summary>
    public static string TypeName(string? dataType, bool isArray) =>
        (dataType ?? "unknown") + (isArray ? "[]" : string.Empty);

    /// <summary>
    /// The fields as a message lists them, such as <c>reason (string), skus (string[])</c>, at most
    /// <see cref="MaxListedFields"/> and then the number of the others; <c>none</c> when empty.
    /// </summary>
    public static string FieldList(IReadOnlyList<SignalCatalogFieldDto> fields)
    {
        if (fields.Count == 0)
        {
            return "none";
        }

        var listed = string.Join(
            ", ",
            fields.Take(MaxListedFields).Select(field => $"{field.Name} ({TypeName(field.DataType, field.IsArray)})"));
        var others = fields.Count - MaxListedFields;
        return others > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{listed}, and {others} more")
            : listed;
    }

    private static int ScalarCount(string? value) => value?.EnumerateRunes().Count() ?? 0;
}

/// <summary>The values of the engine setting <c>Signals.CatalogMode</c>.</summary>
public static class SignalCatalogModes
{
    public const string SettingKey = "Signals.CatalogMode";

    /// <summary>Names without an entry keep working; the default.</summary>
    public const string Optional = "optional";

    /// <summary>Publishing and throwing through the API need an entry.</summary>
    public const string Required = "required";

    /// <summary>
    /// <c>required</c> when the trimmed value is <c>required</c> in any letter case, and
    /// <c>optional</c> for anything else, a missing or blank value included.
    /// </summary>
    public static string ParseMode(string? value) =>
        string.Equals(value?.Trim(), Required, StringComparison.OrdinalIgnoreCase) ? Required : Optional;
}

/// <summary>The statuses of a signal catalog entry.</summary>
public static class SignalCatalogStatuses
{
    public const string Active = "active";
    public const string Archived = "archived";
}

/// <summary>How a definition uses a signal name, as the references table records it.</summary>
public static class SignalNameUsages
{
    public const string Catch = "catch";
    public const string Boundary = "boundary";
    public const string Start = "start";
    public const string Throw = "throw";
    public const string End = "end";

    /// <summary>The usage of a signal event type, or null for any other type.</summary>
    public static string? Of(string? type) =>
        type is null ? null
        : BpmnFlowNodeTypes.IsSignalCatch(type) ? Catch
        : BpmnFlowNodeTypes.IsSignalBoundary(type) ? Boundary
        : BpmnFlowNodeTypes.IsSignalStart(type) ? Start
        : BpmnFlowNodeTypes.IsSignalThrow(type) ? Throw
        : BpmnFlowNodeTypes.IsSignalEnd(type) ? End
        : null;
}
