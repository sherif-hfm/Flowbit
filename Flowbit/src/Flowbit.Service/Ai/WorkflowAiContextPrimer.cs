using System.Text;
using System.Text.Json;
using Flowbit.Service.Authoring;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Ai;

/// <summary>Small contracts derived from the verified package, not a second hand-maintained schema.</summary>
internal static class WorkflowAiContextPrimer
{
    public static string Build(IAuthoringKnowledge knowledge, AiTurnRequestDto request)
    {
        var result = new StringBuilder("\nCanonical quick reference: use these contracts immediately; read only missing details. " +
            "Include a short checklist with edits; do not spend a separate call planning or validating before finish.\n");
        if (knowledge.Resources.TryGetValue("references/workflow.schema.json", out var schema))
        {
            using var document = JsonDocument.Parse(schema);
            if (document.RootElement.TryGetProperty("$defs", out var definitions))
                foreach (var definition in definitions.EnumerateObject())
                {
                    if (!definition.Value.TryGetProperty("properties", out var properties)) continue;
                    var contract = properties.EnumerateObject().Where(p => p.Name is not ("x" or "y" or "w" or "h"))
                        .ToDictionary(p => p.Name, p => Shape(p.Value));
                    var line = definition.Name + ": " + JsonSerializer.Serialize(contract) + "\n";
                    if (result.Length + line.Length <= 10_000) result.Append(line);
                }
        }
        if (knowledge.Resources.TryGetValue("references/docs/node-reference.md", out var guide))
        {
            var input = request.Message + " " + string.Join(" ", request.Sources.Select(page => page.Text));
            var headings = new List<string> { "## Sequence flows", "## Variables and inputs" };
            if (Contains(input, "multi-instance", "multiInstance", "quorum", "vote")) headings.Add("#### Multi-instance properties");
            if (Contains(input, "review", "approval", "task")) headings.Add("### userTask");
            if (Contains(input, "parallel", "join", "branch")) headings.Add("### parallelGateway");
            if (Contains(input, "exclusive", "condition", "amount", "decision")) headings.Add("### exclusiveGateway");
            if (Contains(input, "script", "javascript")) headings.Add("### scriptTask");
            if (Contains(input, "timer", "delay", "PT1H")) headings.Add("## Timer configuration");
            foreach (var heading in headings)
            {
                var start = guide.IndexOf(heading + "\n", StringComparison.Ordinal);
                if (start < 0) start = guide.IndexOf(heading + "\r\n", StringComparison.Ordinal);
                if (start < 0) continue;
                var end = guide.IndexOf("\n#", start + heading.Length, StringComparison.Ordinal);
                var section = guide.Substring(start, (end < 0 ? guide.Length : end) - start);
                // Full lines only. Mark omissions explicitly; complete source stays available through bounded reads.
                result.AppendLine("Excerpt from references/docs/node-reference.md: " + heading);
                foreach (var line in section.Split('\n').Skip(1))
                    if (result.Length + line.Length <= 20_000) result.AppendLine(line.TrimEnd('\r'));
                    else { result.AppendLine("[Remaining section available through a focused reference read.]"); break; }
            }
        }
        return result.ToString();
    }

    private static bool Contains(string value, params string[] words) => words.Any(word => value.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string Shape(JsonElement rule)
    {
        if (rule.TryGetProperty("$ref", out var reference)) return reference.GetString()!.Split('/').Last();
        if (rule.TryGetProperty("enum", out var values)) return string.Join("|", values.EnumerateArray().Select(value => value.ToString()));
        if (rule.TryGetProperty("anyOf", out var alternatives)) return string.Join("|", alternatives.EnumerateArray().Select(Shape));
        if (!rule.TryGetProperty("type", out var type)) return "any";
        return type.ValueKind == JsonValueKind.String && type.GetString() == "array" && rule.TryGetProperty("items", out var items)
            ? Shape(items) + "[]" : type.ToString();
    }
}
