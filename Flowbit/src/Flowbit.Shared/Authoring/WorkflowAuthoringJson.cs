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
        RejectDuplicates(document.RootElement, "$");
        ValidateShape(document.RootElement, typeof(WorkflowModel), "$", false);
        return JsonSerializer.Deserialize<WorkflowModel>(document.RootElement, Options)
            ?? throw new JsonException("A workflow object is required.");
    }

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

    private static void ValidateShape(JsonElement value, Type type, string path, bool nullable)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (nullable || type == typeof(JsonElement)) return;
            throw new JsonException($"{path} cannot be null.");
        }
        if (type == typeof(JsonElement)) return;
        if (type == typeof(string)) { Require(value.ValueKind == JsonValueKind.String, path, "string"); return; }
        if (type == typeof(bool)) { Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, path, "boolean"); return; }
        if (type == typeof(int)) { Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _), path, "32-bit integer"); return; }
        if (type == typeof(long)) { Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _), path, "integer"); return; }
        if (type == typeof(double) || type == typeof(decimal)) { Require(value.ValueKind == JsonValueKind.Number, path, "number"); return; }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            Require(value.ValueKind == JsonValueKind.Array, path, "array");
            var index = 0;
            foreach (var item in value.EnumerateArray()) ValidateShape(item, type.GetGenericArguments()[0], $"{path}[{index++}]", false);
            return;
        }
        Require(value.ValueKind == JsonValueKind.Object, path, "object");
        var properties = Properties(type).ToDictionary(item => item.Name, item => item.Property, StringComparer.Ordinal);
        foreach (var required in Required(type))
            if (!value.TryGetProperty(required, out _)) throw new JsonException($"{path}.{required} is required.");
        foreach (var item in value.EnumerateObject())
        {
            if (!properties.TryGetValue(item.Name, out var property)) throw new JsonException($"Unknown authoring property {path}.{item.Name}.");
            ValidateShape(item.Value, property.PropertyType, path + "." + item.Name, AllowsNull(property));
            if (item.Value.ValueKind == JsonValueKind.String && AllowedValues(type, item.Name) is { } values
                && !values.Contains(item.Value.GetString(), StringComparer.Ordinal))
                throw new JsonException($"Unsupported value at {path}.{item.Name}; use one of {string.Join(", ", values)}.");
        }
    }

    private static void Require(bool condition, string path, string expected)
    {
        if (!condition) throw new JsonException($"{path} must be a {expected}.");
    }

    private static void RejectDuplicates(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException($"Duplicate JSON property {path}.{property.Name}.");
                RejectDuplicates(property.Value, path + "." + property.Name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item, $"{path}[{index++}]");
        }
    }
}
