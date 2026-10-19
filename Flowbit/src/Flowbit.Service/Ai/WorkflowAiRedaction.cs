using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flowbit.Service.Ai;

/// <summary>Redacts literal credentials from workflow copies while retaining reversible, location-bound placeholders.</summary>
public sealed class WorkflowAiRedaction
{
    private readonly Dictionary<string, (string Path, JsonNode? Value)> originals = new(StringComparer.Ordinal);
    private readonly string nonce;

    /// <summary>
    /// Placeholders combine <paramref name="nonce"/> with a hash of the field's identity path. A conversation that keeps its nonce
    /// and redacts each turn's editor workflow again therefore gets the same placeholder for the same credential field.
    /// </summary>
    public WorkflowAiRedaction(string? nonce = null)
    {
        this.nonce = nonce ?? Guid.NewGuid().ToString("N");
        if (this.nonce.Length != 32 || !this.nonce.All(char.IsAsciiHexDigitLower))
            throw new ArgumentException("A redaction nonce is 32 lower-case hexadecimal characters.", nameof(nonce));
    }

    public string Nonce => nonce;

    public JsonElement Redact(JsonElement source)
    {
        var node = JsonNode.Parse(source.GetRawText())!;
        Visit(node, "", "", restore: false);
        return JsonSerializer.SerializeToElement(node);
    }

    public JsonElement Restore(JsonElement source)
    {
        var node = JsonNode.Parse(source.GetRawText())!;
        Visit(node, "", "", restore: true);
        return JsonSerializer.SerializeToElement(node);
    }

    public string Sanitize(string text)
    {
        var secrets = originals.Values.SelectMany(original => SecretStrings(original.Value))
            .SelectMany(secret => new[] { secret, JsonEncodedText.Encode(secret).ToString() })
            .Distinct(StringComparer.Ordinal).OrderByDescending(secret => secret.Length).ToArray();
        if (secrets.Length == 0) return text;
        var result = new System.Text.StringBuilder();
        var offset = 0;
        while (offset < text.Length)
        {
            var next = originals.Keys.Select(placeholder => new { Placeholder = placeholder, Index = text.IndexOf(placeholder, offset, StringComparison.Ordinal) })
                .Where(match => match.Index >= 0).OrderBy(match => match.Index).FirstOrDefault();
            var end = next?.Index ?? text.Length;
            var segment = text[offset..end];
            foreach (var secret in secrets) segment = segment.Replace(secret, "[credential removed]", StringComparison.Ordinal);
            result.Append(segment);
            if (next is null) break;
            // A literal credential may be as short as "config". Never rewrite its reversible placeholder.
            result.Append(next.Placeholder);
            offset = end + next.Placeholder.Length;
        }
        return result.ToString();
    }

    private static IEnumerable<string> SecretStrings(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text))
            yield return text;
        else if (value is JsonValue nonString && !nonString.TryGetValue<string>(out _))
            yield return nonString.ToJsonString();
        else if (value is JsonArray array)
            foreach (var item in array.SelectMany(SecretStrings)) yield return item;
        else if (value is JsonObject obj)
            foreach (var item in obj.SelectMany(property => SecretStrings(property.Value))) yield return item;
    }

    // path binds a placeholder to its field by entity identity; pointer is the field's JSON pointer, for error reports.
    private void Visit(JsonNode node, string path, string pointer, bool restore)
    {
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is not { } child) continue;
                var childPointer = pointer + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (restore && child is JsonValue value && value.TryGetValue<string>(out var text)
                    && text.Contains("FLOWBIT_REDACTED_", StringComparison.Ordinal))
                    throw new WorkflowAiPlaceholderException(childPointer, "A protected credential placeholder moved into an array value.");
                // Only canonical entity collections have stable identity. Opaque JSON may repeat arbitrary id values.
                var identity = IsEntityCollection(path) && child is JsonObject item && item["id"] is { } id
                    ? "id=" + id.ToJsonString() : index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Visit(child, path + "/" + identity, childPointer, restore);
            }
            return;
        }
        if (node is not JsonObject obj) return;
        var namedSecret = obj["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name) && IsSecretName(name);
        foreach (var pair in obj.ToArray())
        {
            var key = pair.Key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
            var childPath = path + "/" + key;
            var childPointer = pointer + "/" + key;
            var secretField = IsSecretName(pair.Key) || (pair.Key == "value" && path.Contains("/headers/", StringComparison.Ordinal))
                || (pair.Key == "defaultValue" && namedSecret);
            // JSON-valued defaults may be numbers or booleans as well as strings or objects.
            // Keep every non-string secret default protected and restore its exact JSON type.
            if (!restore && secretField && pair.Value is not null
                && (pair.Value is not JsonValue defaultValue || !defaultValue.TryGetValue<string>(out _)))
            {
                obj[pair.Key] = Protect(childPath, pair.Value);
                continue;
            }
            if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
            {
                if (restore && originals.TryGetValue(text, out var original))
                {
                    if (!string.Equals(original.Path, childPath, StringComparison.Ordinal))
                        throw new WorkflowAiPlaceholderException(childPointer, "A protected credential placeholder moved to another field.");
                    obj[pair.Key] = original.Value?.DeepClone();
                }
                else if (restore && text.Contains("FLOWBIT_REDACTED_", StringComparison.Ordinal))
                    throw new WorkflowAiPlaceholderException(childPointer, "An unknown or modified protected credential placeholder was returned.");
                else if (!restore && secretField && !IsTrustedReference(text) && !string.IsNullOrEmpty(text))
                    obj[pair.Key] = Protect(childPath, pair.Value);
            }
            else if (pair.Value is { } nested) Visit(nested, childPath, childPointer, restore);
        }
    }

    private string Protect(string path, JsonNode value)
    {
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];
        var placeholder = "${config.FLOWBIT_REDACTED_" + nonce + "_" + name + "}";
        // Entity ids are unique in a valid workflow; a duplicate identity path still gets its own placeholder.
        for (var suffix = 2; originals.TryGetValue(placeholder, out var taken) && taken.Path != path; suffix++)
            placeholder = "${config.FLOWBIT_REDACTED_" + nonce + "_" + name + "_" + suffix + "}";
        originals[placeholder] = (path, value.DeepClone());
        return placeholder;
    }

    private static bool IsEntityCollection(string path) => path is "/lanes" or "/flowNodes" or "/sequenceFlows" or "/variables"
        || System.Text.RegularExpressions.Regex.IsMatch(path, @"^/(?:flowNodes|sequenceFlows)/id=-?\d+/variables$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsTrustedReference(string value) =>
        (value.StartsWith("${config.", StringComparison.Ordinal) || value.StartsWith("${setting.", StringComparison.Ordinal))
        && value.EndsWith('}') && value.IndexOf('}') == value.Length - 1;

    private static bool IsSecretName(string value)
    {
        var lower = value.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return lower.Contains("secret", StringComparison.Ordinal) || lower.Contains("password", StringComparison.Ordinal)
            || lower.Contains("apikey", StringComparison.Ordinal) || lower.Contains("accesstoken", StringComparison.Ordinal)
            || lower.Contains("refreshtoken", StringComparison.Ordinal) || lower is "authorization" or "headervalue";
    }
}

/// <summary>A credential placeholder that was moved, changed or invented, at the JSON pointer where it was found.</summary>
public sealed class WorkflowAiPlaceholderException(string pointer, string message) : JsonException(message)
{
    public string Pointer { get; } = pointer;
}
