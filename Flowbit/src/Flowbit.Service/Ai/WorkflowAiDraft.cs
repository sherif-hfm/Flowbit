using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Shared.Authoring;

namespace Flowbit.Service.Ai;

/// <summary>Applies complete, atomic operation batches to a private redacted authoring draft.</summary>
public static class WorkflowAiDraft
{
    private const int MaxDepth = 64;
    private static readonly HashSet<string> OperationFields = new(StringComparer.Ordinal)
        { "op", "target", "id", "owner", "ownerId", "path", "value" };

    public static JsonElement Apply(JsonElement candidate, JsonElement operations, WorkflowAiRedaction redaction,
        string lockedWorkflowId, int maxBytes, int maxOperations)
    {
        ArgumentNullException.ThrowIfNull(redaction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOperations);
        CheckJson(candidate, maxBytes);
        CheckJson(operations, maxBytes);
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() > maxOperations)
            throw new JsonException("An operation batch must be an array within the configured operation limit.");

        ValidateCandidate(redaction.Restore(candidate), lockedWorkflowId, maxBytes);
        var draft = JsonNode.Parse(candidate.GetRawText(), documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth })!.AsObject();
        foreach (var operation in operations.EnumerateArray())
        {
            ApplyOperation(draft, operation);
            CheckSize(draft.ToJsonString(), maxBytes);
        }

        var result = JsonSerializer.SerializeToElement(draft);
        // Restore only a validation copy. Provider-facing state always retains its placeholders.
        ValidateCandidate(redaction.Restore(result), lockedWorkflowId, maxBytes);
        return result;
    }

    /// <summary>Checks restored checkpoint structure and identities, without graph validation or normalization.</summary>
    public static void ValidateCandidate(JsonElement candidate, string lockedWorkflowId, int maxBytes)
    {
        CheckJson(candidate, maxBytes);
        var model = WorkflowAuthoringJson.Parse(candidate.GetRawText());
        if (!string.Equals(model.Id, lockedWorkflowId, StringComparison.Ordinal))
            throw new JsonException("Authoring operations must preserve the workflow id.");
        Unique(model.Lanes.Select(item => item.Id), "lane");
        Unique(model.FlowNodes.Select(item => item.Id), "node");
        Unique(model.SequenceFlows.Select(item => item.Id), "flow");
        Unique(model.Variables.Select(item => item.Id), "workflow variable");
        foreach (var node in model.FlowNodes) Unique(node.Variables.Select(item => item.Id), "node variable");
        foreach (var flow in model.SequenceFlows) Unique(flow.Variables.Select(item => item.Id), "flow variable");
    }

    private static void ApplyOperation(JsonObject draft, JsonElement operation)
    {
        if (operation.ValueKind != JsonValueKind.Object) throw new JsonException("Each operation must be an object.");
        foreach (var property in operation.EnumerateObject())
            if (!OperationFields.Contains(property.Name)) throw new JsonException("Unknown authoring operation property.");
        var op = String(operation, "op");
        var target = String(operation, "target");
        if (op is not ("create" or "set" or "remove" or "delete")) throw new JsonException("Unsupported authoring operation.");
        if (target is not ("workflow" or "lane" or "node" or "flow" or "variable")) throw new JsonException("Unsupported operation target.");
        var hasPath = operation.TryGetProperty("path", out _);
        var hasValue = operation.TryGetProperty("value", out var value);
        if (hasPath != (op is "set" or "remove") || hasValue != (op is "create" or "set"))
            throw new JsonException("The operation has missing or inapplicable path/value properties.");
        if (target != "variable" && (operation.TryGetProperty("owner", out _) || operation.TryGetProperty("ownerId", out _)))
            throw new JsonException("Only variable targets accept owner properties.");

        JsonObject entity;
        if (target == "workflow")
        {
            if (operation.TryGetProperty("id", out _) || op is "create" or "delete")
                throw new JsonException("The workflow target permits property edits only, without an entity id.");
            entity = draft;
        }
        else
        {
            var id = Integer(operation, "id");
            var collection = Collection(draft, target, operation, createCollection: op == "create");
            var index = Find(collection, id);
            if (op == "create")
            {
                if (index >= 0) throw new JsonException("The created entity id already exists.");
                if (value.ValueKind != JsonValueKind.Object || Integer(value, "id") != id)
                    throw new JsonException("A created entity must be an object with the specified id.");
                foreach (var property in value.EnumerateObject())
                    if (LayoutField(target, property.Name)) throw new JsonException("Diagram coordinates are managed by Flowbit.");
                collection.Add(JsonNode.Parse(value.GetRawText()));
                return;
            }
            if (index < 0) throw new JsonException("The target entity does not exist.");
            if (op == "delete")
            {
                collection.RemoveAt(index);
                return;
            }
            entity = collection[index]!.AsObject();
        }

        var path = Pointer(String(operation, "path"));
        if (path[0] == "id") throw new JsonException("Entity identities cannot be edited or removed.");
        if ((target == "workflow" && path[0] is "lanes" or "flowNodes" or "sequenceFlows" or "variables")
            || (target is "node" or "flow" && path[0] == "variables"))
            throw new JsonException("Use typed entity operations to edit entity collections.");
        if (LayoutField(target, path[0])) throw new JsonException("Diagram coordinates are managed by Flowbit.");
        SetOrRemove(entity, path, op == "remove", hasValue ? value : default);
    }

    private static JsonArray Collection(JsonObject draft, string target, JsonElement operation, bool createCollection)
    {
        var parent = draft;
        var name = target switch { "lane" => "lanes", "node" => "flowNodes", "flow" => "sequenceFlows", _ => "variables" };
        if (target == "variable")
        {
            var owner = String(operation, "owner");
            if (owner == "workflow")
            {
                if (operation.TryGetProperty("ownerId", out _)) throw new JsonException("Workflow variables do not take an owner id.");
            }
            else if (owner is "node" or "flow")
            {
                var ownerId = Integer(operation, "ownerId");
                var owners = draft[owner == "node" ? "flowNodes" : "sequenceFlows"] as JsonArray
                    ?? throw new JsonException("The variable owner collection is missing.");
                var ownerIndex = Find(owners, ownerId);
                if (ownerIndex < 0) throw new JsonException("The variable owner does not exist.");
                parent = owners[ownerIndex]!.AsObject();
            }
            else throw new JsonException("Variable owner must be workflow, node, or flow.");
        }
        if (!parent.ContainsKey(name) && createCollection) parent[name] = new JsonArray();
        return parent[name] as JsonArray ?? throw new JsonException("The target entity collection is missing or invalid.");
    }

    private static int Find(JsonArray collection, int id)
    {
        var found = -1;
        for (var index = 0; index < collection.Count; index++)
        {
            if (collection[index] is not JsonObject entity || entity["id"] is not JsonValue value || !value.TryGetValue<int>(out var itemId))
                throw new JsonException("Entity collections require objects with integer ids.");
            if (itemId != id) continue;
            if (found >= 0) throw new JsonException("The target entity id is ambiguous.");
            found = index;
        }
        return found;
    }

    private static void SetOrRemove(JsonObject entity, string[] path, bool remove, JsonElement value)
    {
        JsonNode parent = entity;
        foreach (var segment in path[..^1])
        {
            parent = parent switch
            {
                JsonObject obj when obj.TryGetPropertyValue(segment, out var child) && child is not null => child,
                JsonArray array => array[Index(segment, array.Count)] ?? throw new JsonException("Cannot traverse a null array item."),
                _ => throw new JsonException("The property path parent does not exist or is not a container.")
            };
        }
        var last = path[^1];
        var replacement = remove ? null : JsonNode.Parse(value.GetRawText());
        if (parent is JsonObject objParent)
        {
            if (remove)
            {
                if (!objParent.Remove(last)) throw new JsonException("The removed property does not exist.");
            }
            else objParent[last] = replacement;
        }
        else if (parent is JsonArray arrayParent)
        {
            if (!remove && last == "-") arrayParent.Add(replacement);
            else
            {
                var index = Index(last, arrayParent.Count);
                if (remove) arrayParent.RemoveAt(index);
                else arrayParent[index] = replacement;
            }
        }
        else throw new JsonException("The property path parent is not a container.");
    }

    private static string[] Pointer(string path)
    {
        if (path.Length is 0 or > 4096 || path[0] != '/') throw new JsonException("A bounded relative JSON pointer beginning with / is required.");
        var segments = path[1..].Split('/');
        if (segments.Length > MaxDepth) throw new JsonException("The property path exceeds the depth limit.");
        for (var segment = 0; segment < segments.Length; segment++)
        {
            var encoded = segments[segment];
            for (var index = 0; index < encoded.Length; index++)
                if (encoded[index] == '~' && (++index == encoded.Length || encoded[index] is not ('0' or '1')))
                    throw new JsonException("Invalid JSON pointer escape.");
            segments[segment] = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        }
        return segments;
    }

    private static int Index(string value, int count)
    {
        if (value.Length == 0 || (value.Length > 1 && value[0] == '0') || !value.All(character => character is >= '0' and <= '9')
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= count)
            throw new JsonException("An existing, non-negative array index is required; use - to append.");
        return index;
    }

    private static bool LayoutField(string target, string property) =>
        (target == "node" && property is "x" or "y") || (target == "lane" && property is "x" or "y" or "w" or "h");

    private static string String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()! : throw new JsonException("Expected an operation string for " + name + ".");

    private static int Integer(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)
            ? number : throw new JsonException("Expected a 32-bit integer for " + name + ".");

    private static void Unique(IEnumerable<int> values, string kind)
    {
        var seen = new HashSet<int>();
        foreach (var value in values)
            if (!seen.Add(value)) throw new JsonException("Duplicate " + kind + " id in the authoring draft.");
    }

    private static void CheckJson(JsonElement value, int maxBytes)
    {
        if (value.ValueKind == JsonValueKind.Undefined) throw new JsonException("Authoring JSON is required.");
        CheckSize(value.GetRawText(), maxBytes);
        CheckMembers(value, 0);
    }

    private static void CheckSize(string text, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (Encoding.UTF8.GetByteCount(text) > maxBytes) throw new JsonException("The authoring draft or operation batch exceeds the size limit.");
    }

    private static void CheckMembers(JsonElement value, int depth)
    {
        if (depth > MaxDepth) throw new JsonException("Authoring JSON exceeds the depth limit.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property in authoring data.");
                CheckMembers(property.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckMembers(item, depth + 1);
    }
}
