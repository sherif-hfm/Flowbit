using System.Reflection;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Flowbit.Shared.Models;

namespace Flowbit.Shared.Authoring;

/// <summary>The strict canonical authoring contract; ordinary legacy imports keep their existing decoder.</summary>
public static class WorkflowAuthoringJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64,
        WriteIndented = true
    };
    private static readonly ConcurrentDictionary<PropertyInfo, bool> NullableProperties = new();

    public static WorkflowModel Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        // Stop at the first structural problem; Inspect reports all of them.
        static bool Throw(WorkflowAuthoringIssue issue, string legacyMessage) => throw new JsonException(legacyMessage);
        RejectDuplicates(document.RootElement, "$", "", Throw);
        ValidateShape(document.RootElement, typeof(WorkflowModel), "$", "", false, Throw);
        return JsonSerializer.Deserialize<WorkflowModel>(document.RootElement, Options)
            ?? throw new JsonException("A workflow object is required.");
    }

    /// <summary>
    /// Every structural problem of a workflow: duplicate keys, unknown or missing properties, wrong types and unsupported values,
    /// each at its RFC 6901 pointer. Parse stops at the first of them. Deserialization can still reject values these checks allow.
    /// </summary>
    public static IReadOnlyList<WorkflowAuthoringIssue> Inspect(JsonElement workflow) => InspectItem(workflow, typeof(WorkflowModel));

    /// <summary>The structural problems of one canonical item, such as a FlowNodeModel or a LaneModel.</summary>
    public static IReadOnlyList<WorkflowAuthoringIssue> InspectItem(JsonElement item, Type type)
    {
        var issues = new List<WorkflowAuthoringIssue>();
        bool Collect(WorkflowAuthoringIssue issue, string legacyMessage) { issues.Add(issue); return true; }
        RejectDuplicates(item, "$", "", Collect);
        ValidateShape(item, type, "$", "", false, Collect);
        return issues;
    }

    /// <summary>A model type's canonical property names, in ordinal order.</summary>
    public static IReadOnlyList<string> PropertyNames(Type type) => Properties(type).Select(item => item.Name).ToArray();

    public static string CreateSchema()
    {
        var definitions = new JsonObject();
        var pending = new Queue<Type>();
        var seen = new HashSet<Type>();
        pending.Enqueue(typeof(WorkflowModel));
        while (pending.TryDequeue(out var type))
        {
            if (!seen.Add(type)) continue;
            var properties = new JsonObject();
            foreach (var (name, property) in Properties(type))
            {
                properties[name] = PropertySchema(property.PropertyType, AllowsNull(property), pending);
                if (AllowedValues(type, name) is { } values)
                {
                    var schema = (JsonObject)properties[name]!;
                    var scalar = schema["anyOf"]?[0]?.AsObject() ?? schema;
                    scalar["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
                }
            }
            var required = Required(type);
            definitions[type.Name] = new JsonObject
            {
                ["type"] = "object", ["properties"] = properties,
                ["additionalProperties"] = false,
                ["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
            };
        }
        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Flowbit canonical workflow authoring contract",
            ["description"] = "Structural schema generated from Flowbit models. Semantic and deployment validation remain required.",
            ["$ref"] = "#/$defs/WorkflowModel", ["$defs"] = definitions
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    public static IReadOnlyList<string> NodeTypes { get; } = StringConstants(typeof(BpmnFlowNodeTypes));

    private static string[] StringConstants(Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .Order(StringComparer.Ordinal).ToArray();

    private static readonly IReadOnlyDictionary<(Type Type, string Name), string[]> Enums =
        new Dictionary<(Type, string), Type>
        {
            [(typeof(FlowNodeModel), "type")] = typeof(BpmnFlowNodeTypes),
            [(typeof(FlowNodeModel), "claimMode")] = typeof(ClaimModes),
            [(typeof(FlowNodeModel), "assignmentMode")] = typeof(AssignmentModes),
            [(typeof(FlowNodeModel), "scriptFormat")] = typeof(ScriptFormats),
            [(typeof(JobPolicyModel), "failureHandling")] = typeof(JobFailureHandling),
            [(typeof(ConditionalDefinitionModel), "deliveryMode")] = typeof(ConditionalEventDeliveryModes),
            [(typeof(ServiceTaskModel), "type")] = typeof(ServiceConnectorTypes),
            [(typeof(BusinessKeyModel), "uniqueness")] = typeof(BusinessKeyUniqueness),
            [(typeof(MultiInstanceModel), "mode")] = typeof(MultiInstanceModes),
            [(typeof(MultiInstanceModel), "source")] = typeof(MultiInstanceSources),
            [(typeof(MultiInstanceModel), "completionEvaluation")] = typeof(MultiInstanceCompletionEvaluations),
            [(typeof(VariableModel), "dataType")] = typeof(WorkflowVariableTypes),
            [(typeof(VariableModel), "scope")] = typeof(VariableScopes),
            [(typeof(VariableModel), "access")] = typeof(SharedVariableAccessModes),
            [(typeof(ServiceOutputMappingModel), "dataType")] = typeof(WorkflowVariableTypes),
            [(typeof(MessageOutputMappingModel), "dataType")] = typeof(WorkflowVariableTypes)
        }.ToDictionary(item => item.Key, item => StringConstants(item.Value));

    private static string[]? AllowedValues(Type type, string property) => Enums.GetValueOrDefault((type, property));

    private static IEnumerable<(string Name, PropertyInfo Property)> Properties(Type type) => type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => !property.Name.StartsWith("Legacy", StringComparison.Ordinal))
        .Where(property => !(type == typeof(MessageCatchModel) && property.Name == nameof(MessageCatchModel.IdempotencyVariable)))
        .Select(property => (Name: property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name, Property: property))
        .Where(item => item.Name is not null && item.Property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
        .Select(item => (item.Name!, item.Property))
        .OrderBy(item => item.Item1, StringComparer.Ordinal);

    private static string[] Required(Type type) => type == typeof(WorkflowModel)
        ? ["id", "name", "flowNodes", "sequenceFlows"]
        : type == typeof(FlowNodeModel) ? ["id", "name", "type"]
        : type == typeof(SequenceFlowModel) ? ["id", "sourceRef", "targetRef"]
        : type == typeof(LaneModel) ? ["id", "name"]
        : type == typeof(VariableModel) ? ["id", "name", "dataType"] : [];

    private static bool AllowsNull(PropertyInfo property) => NullableProperties.GetOrAdd(property, item =>
        Nullable.GetUnderlyingType(item.PropertyType) is not null
        || (!item.PropertyType.IsValueType && new NullabilityInfoContext().Create(item).ReadState == NullabilityState.Nullable));

    private static JsonObject PropertySchema(Type type, bool nullable, Queue<Type> pending)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        JsonObject schema;
        if (type == typeof(JsonElement)) schema = new JsonObject();
        else if (type == typeof(string)) schema = new JsonObject { ["type"] = "string" };
        else if (type == typeof(bool)) schema = new JsonObject { ["type"] = "boolean" };
        else if (type == typeof(int) || type == typeof(long)) schema = new JsonObject { ["type"] = "integer" };
        else if (type == typeof(double) || type == typeof(decimal)) schema = new JsonObject { ["type"] = "number" };
        else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            schema = new JsonObject { ["type"] = "array", ["items"] = PropertySchema(type.GetGenericArguments()[0], false, pending) };
        else
        {
            pending.Enqueue(type);
            schema = new JsonObject { ["$ref"] = "#/$defs/" + type.Name };
        }
        return nullable && type != typeof(JsonElement)
            ? new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) }
            : schema;
    }

    // Each problem carries Parse's original message, which other callers and the API already report, and an issue for Inspect.
    private delegate bool IssueSink(WorkflowAuthoringIssue issue, string legacyMessage);

    private static void ValidateShape(JsonElement value, Type type, string path, string pointer, bool nullable, IssueSink report)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (nullable || type == typeof(JsonElement)) return;
            report(new("wrong-type", pointer, $"Expected {Expected(type)}; found null."), $"{path} cannot be null.");
            return;
        }
        if (type == typeof(JsonElement)) return;
        if (type == typeof(string)) { Require(value.ValueKind == JsonValueKind.String, value, path, pointer, "string", report); return; }
        if (type == typeof(bool)) { Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, value, path, pointer, "boolean", report); return; }
        if (type == typeof(int)) { Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _), value, path, pointer, "32-bit integer", report); return; }
        if (type == typeof(long)) { Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _), value, path, pointer, "integer", report); return; }
        if (type == typeof(double) || type == typeof(decimal)) { Require(value.ValueKind == JsonValueKind.Number, value, path, pointer, "number", report); return; }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            if (!Require(value.ValueKind == JsonValueKind.Array, value, path, pointer, "array", report)) return;
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateShape(item, type.GetGenericArguments()[0], $"{path}[{index}]", $"{pointer}/{index}", false, report);
                index++;
            }
            return;
        }
        if (!Require(value.ValueKind == JsonValueKind.Object, value, path, pointer, "object", report)) return;
        var properties = Properties(type).ToDictionary(item => item.Name, item => item.Property, StringComparer.Ordinal);
        var missing = Required(type).Where(required => !value.TryGetProperty(required, out _)).ToArray();
        if (missing.Length > 0)
            report(new("missing-property", pointer,
                    $"Missing required propert{(missing.Length == 1 ? "y" : "ies")} {string.Join(", ", missing.Select(name => "\"" + name + "\""))}."),
                $"{path}.{missing[0]} is required.");
        foreach (var item in value.EnumerateObject())
        {
            var itemPointer = pointer + "/" + Escape(item.Name);
            if (!properties.TryGetValue(item.Name, out var property))
            {
                report(new("unknown-property", itemPointer, $"Unknown property \"{item.Name}\"; allowed properties: {string.Join(", ", properties.Keys.Order(StringComparer.Ordinal))}."),
                    $"Unknown authoring property {path}.{item.Name}.");
                continue;
            }
            ValidateShape(item.Value, property.PropertyType, path + "." + item.Name, itemPointer, AllowsNull(property), report);
            if (item.Value.ValueKind == JsonValueKind.String && AllowedValues(type, item.Name) is { } values
                && !values.Contains(item.Value.GetString(), StringComparer.Ordinal))
                report(new("unsupported-value", itemPointer,
                        $"Value {JsonSerializer.Serialize(item.Value.GetString())} is not allowed; use one of: {string.Join(", ", values)}{(AllowsNull(property) ? " or null" : "")}."),
                    $"Unsupported value at {path}.{item.Name}; use one of {string.Join(", ", values)}.");
        }
    }

    private static bool Require(bool condition, JsonElement value, string path, string pointer, string expected, IssueSink report)
    {
        if (condition) return true;
        report(new("wrong-type", pointer, $"Expected {Article(expected)} {expected}; found {Describe(value)}."), $"{path} must be a {expected}.");
        return false;
    }

    private static void RejectDuplicates(JsonElement value, string path, string pointer, IssueSink report)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    report(new("duplicate-property", pointer + "/" + Escape(property.Name), $"Property \"{property.Name}\" appears more than once in this object."),
                        $"Duplicate JSON property {path}.{property.Name}.");
                RejectDuplicates(property.Value, path + "." + property.Name, pointer + "/" + Escape(property.Name), report);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicates(item, $"{path}[{index}]", $"{pointer}/{index}", report);
                index++;
            }
        }
    }

    private static string Expected(Type type) =>
        type == typeof(string) ? "a string" : type == typeof(bool) ? "a boolean" : type == typeof(int) ? "a 32-bit integer"
        : type == typeof(long) ? "an integer" : type == typeof(double) || type == typeof(decimal) ? "a number"
        : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? "an array" : "an object";

    private static string Article(string noun) => noun.StartsWith("integer", StringComparison.Ordinal)
        || noun.StartsWith("array", StringComparison.Ordinal) || noun.StartsWith("object", StringComparison.Ordinal) ? "an" : "a";

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "the string " + Clip(value.GetRawText()),
        JsonValueKind.Number => "the number " + Clip(value.GetRawText()),
        JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => "null"
    };

    private static string Clip(string text) => text.Length <= 80 ? text : text[..77] + "...";

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}

/// <summary>One structural problem: a stable code, the RFC 6901 pointer of the offending element, and what is wrong.</summary>
public sealed record WorkflowAuthoringIssue(string Code, string Pointer, string Message);
