using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flowbit.Service.Ai;

/// <summary>One error reported to the authoring agent: its code, JSON pointer, the element it is in, and what is wrong.</summary>
public sealed record WorkflowAiError(string Code, string Path, string Where, string Message)
{
    /// <summary>The report line used by the host nudge.</summary>
    public string Describe() => $"{Code} at {(Path.Length == 0 ? "/" : Path)}{(Where.Length == 0 ? "" : $" ({Where})")}: {Message}";
}

/// <summary>
/// Locations and texts for errors the agent receives, ported from Flowbit AI Lab v3 (Validation/FlowbitResults.cs): every error
/// names the element it is in, such as node 60 (inclusiveGateway "Setup split") or input "comment" of node 12 (...).
/// </summary>
internal static partial class WorkflowAiDiagnostics
{
    private static readonly JsonWriterOptions ShowOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Parses one JSON document, or explains where and why the text is not one.</summary>
    internal static bool TryParse(string text, out JsonElement value, out WorkflowAiError? error)
    {
        value = default;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
                { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 });
            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException exception)
        {
            var line = (int)(exception.LineNumber ?? 0); var position = (int)(exception.BytePositionInLine ?? 0);
            error = new("malformed-json", "", "", "The content is not one valid JSON document (no Markdown fences, comments, trailing commas or prose). "
                + $"Parse failed at line {line + 1}, byte {position + 1}: {Reason(exception)}{Near(text, line, position)}");
            return false;
        }
    }

    /// <summary>The parser's own explanation without its zero-based position suffix.</summary>
    internal static string Reason(JsonException exception)
    {
        var message = exception.Message;
        foreach (var marker in new[] { " Path:", " LineNumber:", " | LineNumber:" })
            if (message.IndexOf(marker, StringComparison.Ordinal) is var cut and > 0) message = message[..cut];
        message = message.Trim().TrimEnd('|').Trim();
        return message.EndsWith('.') ? message : message + ".";
    }

    // Up to 60 characters either side of the failing position on its line; for a blank line (the content ended early),
    // the end of the last non-blank line before it.
    private static string Near(string text, int line, int position)
    {
        var lines = text.Split('\n');
        if (line < 0 || line >= lines.Length) return "";
        var content = lines[line].TrimEnd('\r');
        if (string.IsNullOrWhiteSpace(content))
        {
            var last = Array.FindLastIndex(lines, line, l => !string.IsNullOrWhiteSpace(l));
            if (last < 0) return "";
            var previous = lines[last].TrimEnd('\r');
            return $" The content ends after line {last + 1}: \"{(previous.Length > 120 ? "..." + previous[^117..] : previous)}\"";
        }
        var bytes = Encoding.UTF8.GetBytes(content);
        var index = Encoding.UTF8.GetString(bytes, 0, Math.Clamp(position, 0, bytes.Length)).Length;
        index = Math.Min(index, content.Length);
        var start = Math.Max(0, index - 60); var end = Math.Min(content.Length, index + 60);
        return $" Text near the error: \"{(start > 0 ? "..." : "")}{content[start..end]}{(end < content.Length ? "..." : "")}\"";
    }

    /// <summary>
    /// Names the element a JSON pointer is in. <paramref name="kind"/> is workflow for a whole workflow, otherwise node,
    /// sequenceFlow, variable or lane for one item.
    /// </summary>
    internal static string Locate(JsonElement root, string pointer, string kind = "workflow")
    {
        var segments = Segments(pointer);
        if (kind != "workflow") return Element(root, kind, segments, 0);
        if (segments.Length >= 2 && Child(root, segments[0], segments[1]) is { } item)
            switch (segments[0])
            {
                case "flowNodes": return Element(item, "node", segments, 2);
                case "sequenceFlows": return Element(item, "sequenceFlow", segments, 2);
                case "variables": return Element(item, "process variable", segments, 2);
                case "lanes": return Element(item, "lane", segments, 2);
            }
        return "workflow";
    }

    private static string Element(JsonElement item, string kind, string[] segments, int next)
    {
        var self = Name(item, kind);
        if (kind is "node" or "sequenceFlow" && segments.Length >= next + 2 && segments[next] == "variables" && Child(item, "variables", segments[next + 1]) is { } input)
            return $"{(kind == "node" ? "input" : "action input")} {Quoted(input, "name")} of {self}";
        return self;
    }

    /// <summary>A short name for a node, flow, variable or lane.</summary>
    internal static string Name(JsonElement item, string kind)
    {
        if (item.ValueKind != JsonValueKind.Object) return $"{(kind == "sequenceFlow" ? "flow" : kind)} {Show(item)}";
        var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString()!.Length > 0
            ? $" \"{Clip(n.GetString()!, 60)}\"" : "";
        return kind switch
        {
            "node" => $"node {Raw(item, "id")} ({Text(item, "type") ?? "no type"}{name})",
            "sequenceFlow" => $"flow {Raw(item, "id")} ({Raw(item, "sourceRef")} -> {Raw(item, "targetRef")}{name})",
            "lane" => $"lane {Raw(item, "id")}{name}",
            _ => $"{kind} {Quoted(item, "name")}"
        };
    }

    // Flowbit's definition messages name their element as "<noun> #<id>": "Sequence flow #101 …", "User task #7 …",
    // "Gateway merge #9 …". Variables are named as "Variable 'x' on flow node #5", "on sequence flow #101" or "on process variables".
    [GeneratedRegex(@"\bVariable '(?<name>[^']*)' on (?:(?<owner>flow node|sequence flow) #(?<id>-?\d+)|process variables)", RegexOptions.CultureInvariant)]
    private static partial Regex VariableReference();

    [GeneratedRegex(@"(?<word>[A-Za-z][\w-]*) #(?<id>-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex EntityReference();

    /// <summary>The JSON pointer of the element a Flowbit definition message is about, or the workflow root.</summary>
    internal static string DefinitionPointer(JsonElement root, string message)
    {
        var variable = VariableReference().Match(message);
        if (variable.Success)
        {
            var owner = variable.Groups["owner"].Success
                ? Find(root, variable.Groups["owner"].Value == "flow node" ? "flowNodes" : "sequenceFlows", variable.Groups["id"].Value)
                : "";
            if (owner is not null && At(root, owner, out var holder) && holder.ValueKind == JsonValueKind.Object
                && holder.TryGetProperty("variables", out var variables) && variables.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in variables.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && Text(item, "name") == variable.Groups["name"].Value)
                        return $"{owner}/variables/{index}";
                    index++;
                }
            }
            if (owner is { Length: > 0 }) return owner;
        }
        foreach (Match match in EntityReference().Matches(message))
        {
            var word = match.Groups["word"].Value.ToLowerInvariant();
            // "Attribute #2 on Flow node #5" numbers the attribute; its owner follows.
            if (word == "attribute") continue;
            if (word == "id") word = message[..match.Index].TrimEnd().Split(' ').LastOrDefault()?.ToLowerInvariant() ?? "";
            var collection = word == "flow" ? "sequenceFlows" : word == "lane" ? "lanes" : "flowNodes";
            if (Find(root, collection, match.Groups["id"].Value) is { } pointer) return pointer;
        }
        return "";
    }

    /// <summary>Converts a System.Text.Json path such as $.flowNodes[3].name into a JSON pointer.</summary>
    internal static string PointerFromJsonPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('$')) return "";
        var pointer = new StringBuilder();
        var i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                var end = i + 1;
                while (end < path.Length && path[end] is not ('.' or '[')) end++;
                pointer.Append('/').Append(Escape(path[(i + 1)..end]));
                i = end;
            }
            else if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '\'')
            {
                var end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                if (end < 0) break;
                pointer.Append('/').Append(Escape(path[(i + 2)..end]));
                i = end + 2;
            }
            else if (path[i] == '[')
            {
                var end = path.IndexOf(']', i + 1);
                if (end < 0) break;
                pointer.Append('/').Append(path[(i + 1)..end]);
                i = end + 1;
            }
            else break;
        }
        return pointer.ToString();
    }

    internal static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    internal static string[] Segments(string pointer) => pointer.Length == 0
        ? [] : pointer[1..].Split('/').Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();

    /// <summary>Resolves a JSON pointer; false when any segment is missing.</summary>
    internal static bool At(JsonElement root, string pointer, out JsonElement value)
    {
        value = root;
        foreach (var segment in Segments(pointer))
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(segment, out var next)) value = next;
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i)
                && i < value.GetArrayLength()) value = value[i];
            else return false;
        }
        return true;
    }

    /// <summary>Compact JSON text of a value, at most 80 characters.</summary>
    internal static string Show(JsonElement value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, ShowOptions)) value.WriteTo(writer);
        return Clip(Encoding.UTF8.GetString(buffer.WrittenSpan), 80);
    }

    /// <summary>The JSON text of a property, or "(none)" when it is absent.</summary>
    internal static string Raw(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) ? Show(value) : "(none)";

    internal static string? Text(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // The pointer of the first item in a collection whose id equals the given number.
    private static string? Find(JsonElement root, string collection, string id)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(collection, out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number
                && value.GetRawText() == id) return $"/{collection}/{index}";
            index++;
        }
        return null;
    }

    private static string Quoted(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? $"\"{Clip(value.GetString()!, 60)}\"" : "(no name)";

    private static JsonElement? Child(JsonElement owner, string key, string index) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array
        && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < array.GetArrayLength() ? array[i] : null;

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 3)] + "...";
}
