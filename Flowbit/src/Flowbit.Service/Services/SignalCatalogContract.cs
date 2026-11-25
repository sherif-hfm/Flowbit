using System.Globalization;
using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// The checks against the signal catalog (EVENT-04F), as pure functions over an entry and a
/// payload or a definition. A field accepts exactly the values a typed mapping of its type
/// accepts, because both use <see cref="TypedOutputValueValidator"/>. Messages name the signal,
/// the field or the node, and the types, never a value.
/// </summary>
public static class SignalCatalogContract
{
    /// <summary>
    /// The first problem of a payload thrown through the API, or null. Fields are checked in
    /// contract order, then the payload's other members in payload order. An absent payload is
    /// an empty object. No field may hold JSON null: a receiver's mapping refuses null unless its
    /// target variable is nullable, so a null that one receiver accepts would fail another.
    /// </summary>
    public static string? PayloadProblem(SignalCatalogEntryRecord entry, JsonElement? payload)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var body = payload is { ValueKind: JsonValueKind.Object } value ? value : (JsonElement?)null;
        foreach (var field in entry.Fields)
        {
            if (body is not { } present
                || !present.TryGetProperty(field.Name, out var member)
                || member.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (field.Required)
                {
                    return $"Signal '{entry.Name}' payload is missing the required field '{field.Name}'.";
                }

                if (body is { } sent
                    && sent.TryGetProperty(field.Name, out var nullMember)
                    && nullMember.ValueKind == JsonValueKind.Null)
                {
                    return $"Signal '{entry.Name}' payload field '{field.Name}' is null; "
                        + "leave out an optional field instead of sending null.";
                }

                continue;
            }

            if (!TypedOutputValueValidator.IsValid(member, field.DataType, field.IsArray))
            {
                return $"Signal '{entry.Name}' payload field '{field.Name}' must be "
                    + $"{SignalCatalogEntryRules.TypeWords(field.DataType, field.IsArray)}.";
            }
        }

        if (body is { } members)
        {
            foreach (var property in members.EnumerateObject())
            {
                if (FindField(entry, property.Name) is null)
                {
                    return $"Signal '{entry.Name}' payload member '{property.Name}' is not a field of its catalog entry. "
                        + $"Its fields: {SignalCatalogEntryRules.FieldList(entry.Fields)}.";
                }
            }
        }

        return null;
    }

    /// <summary>The distinct valid signal names a definition's signal events use, in ordinal order.</summary>
    public static IReadOnlyList<string> SignalNames(WorkflowModel definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return SignalEvents(definition)
            .Select(node => node.Signal!.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The signal events of a definition with a valid name, in node order.</summary>
    public static IEnumerable<FlowNodeModel> SignalEvents(WorkflowModel definition) =>
        (definition.FlowNodes ?? [])
            .Where(node => node is not null
                && BpmnFlowNodeTypes.IsSignalEvent(node.Type)
                && SignalEventRules.IsValidName(node.Signal?.Name))
            .OrderBy(node => node.Id);

    /// <summary>
    /// Every problem of a definition against the catalog, in node order and, within a node, in
    /// the order of the plan's table. <paramref name="onlyName"/> limits the check to the
    /// signal events that use that name, as a catalog write does.
    /// </summary>
    public static IReadOnlyList<SignalCatalogDefinitionProblem> FindDefinitionProblems(
        WorkflowModel definition,
        IReadOnlyDictionary<string, SignalCatalogEntryRecord> entries,
        string mode,
        SignalCatalogStage stage,
        string? onlyName = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(entries);
        var problems = new List<SignalCatalogDefinitionProblem>();
        var publicationRules = stage is SignalCatalogStage.Publication or SignalCatalogStage.Reactivation;
        IReadOnlyDictionary<string, IReadOnlyList<CallSourceDeclaration>>? declarations = null;
        foreach (var node in SignalEvents(definition))
        {
            var name = node.Signal!.Name;
            if (onlyName is not null && !string.Equals(name, onlyName, StringComparison.Ordinal))
            {
                continue;
            }

            var label = SignalEventRules.Label(node);
            void Add(bool save, string message) =>
                problems.Add(new SignalCatalogDefinitionProblem(node.Id, name, save, message));

            if (!entries.TryGetValue(name, out var entry))
            {
                if (stage == SignalCatalogStage.Publication
                    && string.Equals(mode, SignalCatalogModes.Required, StringComparison.Ordinal))
                {
                    Add(false, $"{label} uses signal '{name}', which has no entry in the signal catalog; "
                        + "this deployment requires one before publication.");
                }

                continue;
            }

            if (!entry.IsActive)
            {
                Add(true, $"{label} uses signal '{name}', whose catalog entry is archived.");
                continue;
            }

            if (BpmnFlowNodeTypes.ThrowsSignal(node.Type))
            {
                declarations ??= CallActivityRules.SourceDeclarations(definition);
                AddThrowProblems(node, label, entry, declarations, publicationRules, Add);
            }
            else
            {
                AddReceiverProblems(node, label, entry, publicationRules, Add);
            }
        }

        return problems;
    }

    /// <summary>
    /// Whether a mapping of <paramref name="mappingType"/> can read a field of
    /// <paramref name="fieldType"/>: the same type and array flag; a json mapping reads any
    /// field, and a json array mapping any array field; a string mapping reads a date or a
    /// datetime with the same array flag.
    /// </summary>
    public static bool ReceiverAccepts(string fieldType, bool fieldIsArray, string? mappingType, bool mappingIsArray)
    {
        if (string.Equals(mappingType, WorkflowVariableTypes.Json, StringComparison.Ordinal))
        {
            return !mappingIsArray || fieldIsArray;
        }

        if (fieldIsArray != mappingIsArray)
        {
            return false;
        }

        return string.Equals(fieldType, mappingType, StringComparison.Ordinal)
            || (string.Equals(mappingType, WorkflowVariableTypes.String, StringComparison.Ordinal)
                && fieldType is WorkflowVariableTypes.Date or WorkflowVariableTypes.DateTime);
    }

    /// <summary>
    /// Whether a value of <paramref name="valueType"/> can fill a field of
    /// <paramref name="fieldType"/>: the same type and array flag; any value fills a json field;
    /// a date or a datetime fills a string field with the same array flag.
    /// </summary>
    public static bool ThrowAccepts(string fieldType, bool fieldIsArray, string? valueType, bool valueIsArray)
    {
        if (string.Equals(fieldType, WorkflowVariableTypes.Json, StringComparison.Ordinal))
        {
            return true;
        }

        if (fieldIsArray != valueIsArray)
        {
            return false;
        }

        return string.Equals(fieldType, valueType, StringComparison.Ordinal)
            || (string.Equals(fieldType, WorkflowVariableTypes.String, StringComparison.Ordinal)
                && valueType is WorkflowVariableTypes.Date or WorkflowVariableTypes.DateTime);
    }

    private static void AddThrowProblems(
        FlowNodeModel node,
        string label,
        SignalCatalogEntryRecord entry,
        IReadOnlyDictionary<string, IReadOnlyList<CallSourceDeclaration>> declarations,
        bool publicationRules,
        Action<bool, string> add)
    {
        var name = entry.Name;
        var payload = (node.Signal?.Payload ?? [])
            .Where(item => item is not null && SignalEventRules.IsValidPayloadName(item.Name))
            .Select(item => item!)
            .ToArray();

        foreach (var item in payload)
        {
            if (FindField(entry, item.Name!) is null)
            {
                add(true, $"{label} payload sends '{item.Name}', which is not a field of the catalog entry of '{name}'. "
                    + $"Its fields: {SignalCatalogEntryRules.FieldList(entry.Fields)}.");
            }
        }

        foreach (var item in payload)
        {
            if (FindField(entry, item.Name!) is not { } field)
            {
                continue;
            }

            var fieldType = SignalCatalogEntryRules.TypeName(field.DataType, field.IsArray);
            if (!string.IsNullOrEmpty(item.Variable))
            {
                if (!declarations.TryGetValue(item.Variable, out var declared))
                {
                    continue;
                }

                var mismatch = declared.FirstOrDefault(declaration =>
                    !ThrowAccepts(field.DataType, field.IsArray, declaration.DataType, declaration.IsArray));
                if (mismatch is not null)
                {
                    add(true, $"{label} payload entry '{item.Name}' reads '{item.Variable}', declared as "
                        + $"{SignalCatalogEntryRules.TypeName(mismatch.DataType, mismatch.IsArray)}, but the catalog entry of "
                        + $"'{name}' defines it as {fieldType}.");
                }
            }
            else if (SourceType(item.Source) is { } sourceType
                     && !ThrowAccepts(field.DataType, field.IsArray, sourceType, false))
            {
                add(true, $"{label} payload entry '{item.Name}' sends source '{item.Source}', which is {sourceType}, "
                    + $"but the catalog entry of '{name}' defines it as {fieldType}.");
            }
        }

        if (!publicationRules)
        {
            return;
        }

        if (!entry.AllowWorkflows)
        {
            add(false, $"{label} throws '{name}', which its catalog entry does not let workflows throw.");
        }

        foreach (var field in entry.Fields.Where(field => field.Required))
        {
            if (!payload.Any(item => string.Equals(item.Name, field.Name, StringComparison.Ordinal)))
            {
                add(false, $"{label} payload does not send '{field.Name}', which the catalog entry of '{name}' requires.");
            }
        }

        foreach (var item in payload)
        {
            if (FindField(entry, item.Name!) is { Required: true }
                && !item.Required
                && !string.Equals(item.Source, SignalPayloadSources.InstanceId, StringComparison.Ordinal))
            {
                add(false, $"{label} payload entry '{item.Name}' must set required, because the catalog entry of "
                    + $"'{name}' requires it.");
            }
        }

        // A field never holds null, but the throw sends a stored null of an optional entry as it
        // is. Only required stops a null at the throw, before any delivery reads it.
        foreach (var item in payload)
        {
            if (!item.Required
                && !string.IsNullOrEmpty(item.Variable)
                && FindField(entry, item.Name!) is { Required: false }
                && declarations.TryGetValue(item.Variable, out var declared)
                && declared.Any(declaration => declaration.MayBeNull))
            {
                add(false, $"{label} payload entry '{item.Name}' must set required, because variable "
                    + $"'{item.Variable}' can be null and the catalog entry of '{name}' accepts no null field.");
            }
        }
    }

    private static void AddReceiverProblems(
        FlowNodeModel node,
        string label,
        SignalCatalogEntryRecord entry,
        bool publicationRules,
        Action<bool, string> add)
    {
        var name = entry.Name;
        var mappings = (node.Signal?.OutputMappings ?? [])
            .Where(mapping => mapping is not null && !string.IsNullOrWhiteSpace(mapping.Path))
            .ToArray();
        var reading = new List<(MessageOutputMappingModel Mapping, SignalCatalogFieldDto Field)>();
        foreach (var mapping in mappings)
        {
            var segments = mapping.Path.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var first = segments[0];
            if (FindField(entry, first) is not { } field)
            {
                var what = segments.Length == 1
                    ? $"reads '{mapping.Path}', which is not a field"
                    : $"reads '{mapping.Path}', whose first part '{first}' is not a field";
                add(true, $"{label} mapping to '{mapping.Variable}' {what} of the catalog entry of '{name}'. "
                    + $"Its fields: {SignalCatalogEntryRules.FieldList(entry.Fields)}.");
                continue;
            }

            reading.Add((mapping, field));
            var mappingType = SignalCatalogEntryRules.TypeName(mapping.DataType, mapping.IsArray == true);
            if (segments.Length == 1)
            {
                if (!ReceiverAccepts(field.DataType, field.IsArray, mapping.DataType, mapping.IsArray == true))
                {
                    add(true, $"{label} mapping to '{mapping.Variable}' is {mappingType}, but the catalog entry of "
                        + $"'{name}' defines '{field.Name}' as {SignalCatalogEntryRules.TypeName(field.DataType, field.IsArray)}.");
                }

                continue;
            }

            var json = string.Equals(field.DataType, WorkflowVariableTypes.Json, StringComparison.Ordinal);
            if (field.IsArray && segments.Length == 2 && IsIndex(segments[1]))
            {
                if (!ReceiverAccepts(field.DataType, false, mapping.DataType, mapping.IsArray == true))
                {
                    add(true, $"{label} mapping to '{mapping.Variable}' is {mappingType}, but the catalog entry of "
                        + $"'{name}' defines the items of '{field.Name}' as {SignalCatalogEntryRules.TypeName(field.DataType, false)}.");
                }

                continue;
            }

            if (!json)
            {
                add(true, $"{label} mapping to '{mapping.Variable}' reads '{mapping.Path}' below field '{field.Name}', which is "
                    + $"{SignalCatalogEntryRules.TypeWords(field.DataType, field.IsArray)}; only a json field or an array element has parts.");
            }
        }

        if (!publicationRules)
        {
            return;
        }

        foreach (var (mapping, field) in reading)
        {
            if (mapping.Required
                && !field.Required
                && mapping.DefaultValue is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
            {
                add(false, $"{label} mapping to '{mapping.Variable}' is required without a defaultValue, but "
                    + $"'{field.Name}' is optional in the catalog entry of '{name}'.");
            }
        }
    }

    private static SignalCatalogFieldDto? FindField(SignalCatalogEntryRecord entry, string name) =>
        entry.Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    private static string? SourceType(string? source) => source switch
    {
        SignalPayloadSources.InstanceId => WorkflowVariableTypes.Number,
        SignalPayloadSources.BusinessKey => WorkflowVariableTypes.String,
        _ => null
    };

    private static bool IsIndex(string segment) =>
        int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0;
}
