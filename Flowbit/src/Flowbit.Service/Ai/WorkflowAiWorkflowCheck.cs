using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Authoring;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Ai;

/// <summary>The outcome of checking a workflow file: its errors, and the restored workflow JSON when it passed.</summary>
public sealed record WorkflowAiCheckResult(IReadOnlyList<WorkflowAiError> Errors, string? RestoredJson)
{
    public bool Valid => Errors.Count == 0;
}

/// <summary>The outcome of checking items: a refusal when the call itself cannot be checked, otherwise the errors.</summary>
public sealed record WorkflowAiItemCheckResult(IReadOnlyList<WorkflowAiError> Errors, string? Refusal = null);

/// <summary>
/// The check behind validate_workflow, validate_item and the host's end-of-turn gate. Every structural problem is reported;
/// Flowbit's definition validation stops at its first error. Messages never contain credentials or the provider key.
/// </summary>
public sealed class WorkflowAiWorkflowCheck(IWorkflowDefinitionValidator validator, WorkflowAiRedaction redaction, string apiKey, WorkflowModel? baseline)
{
    public static readonly IReadOnlyList<string> NotChecked =
    [
        "Business correctness against the requirements",
        "Definition rules after the first one that failed: Flowbit reports one definition error at a time",
        "Shared-variable catalog bindings, save and publication readiness",
        "Script behavior, expression evaluation and REST or message calls at runtime"
    ];

    public static readonly IReadOnlyList<string> ItemNotChecked =
    [
        "References to other nodes, flows, lanes and variables, graph topology, routing, defaults and configuration rules: check the saved file with validate_workflow.",
        "Business correctness against the requirements"
    ];

    public static readonly IReadOnlyList<string> ItemKinds = ["node", "variable", "sequenceFlow", "lane"];

    public WorkflowAiCheckResult Check(string text)
    {
        using var activity = WorkflowAiTelemetry.Start("workflow.check");
        var result = CheckCore(text);
        activity?.SetTag("valid", result.Valid);
        activity?.SetTag("errors", result.Errors.Count);
        return result;
    }

    private WorkflowAiCheckResult CheckCore(string text)
    {
        if (!WorkflowAiDiagnostics.TryParse(text, out var root, out var malformed))
            return new([malformed! with { Path = "/", Message = Scrub(malformed.Message) }], null);
        // A structurally invalid workflow stops here, so rule errors appear once the structure is right.
        var errors = WorkflowAuthoringJson.Inspect(root).Select(issue => Located(root, issue.Code, issue.Pointer, issue.Message)).ToList();
        if (errors.Count > 0) return new(Sorted(errors), null);
        errors.AddRange(DuplicateIds(root));
        if (ProviderKeyPointer(root, "") is { } keyPointer)
            errors.Add(Located(root, "provider-key", keyPointer, "The workflow must not contain the AI provider key."));
        if (errors.Count > 0) return new(Sorted(errors), null);

        JsonElement restored;
        try { restored = redaction.Restore(root); }
        catch (WorkflowAiPlaceholderException error)
        {
            return new([Located(root, "credential-placeholder", error.Pointer, error.Message)], null);
        }
        WorkflowModel authored, normalized;
        try
        {
            authored = WorkflowAuthoringJson.Parse(restored.GetRawText());
            normalized = WorkflowAuthoringJson.Parse(restored.GetRawText());
        }
        catch (JsonException error)
        {
            var pointer = WorkflowAiDiagnostics.PointerFromJsonPath(error.Path);
            return new([Located(root, "json-shape", pointer, WorkflowAiDiagnostics.Reason(error))], null);
        }
        try
        {
            validator.ValidateAuthored(normalized);
            WorkflowModelMigrator.Normalize(normalized);
            validator.ValidateNormalized(normalized);
        }
        catch (Exception error) when (error is WorkflowDomainException or FormatException or ArgumentException or JsonException)
        {
            var pointer = WorkflowAiDiagnostics.DefinitionPointer(root, error.Message);
            errors.Add(Located(root, "definition-rule", pointer, error.Message));
        }
        foreach (var node in WorkflowAiAuthoringService.UnreachableNodes(authored, baseline))
        {
            var pointer = "/flowNodes/" + authored.FlowNodes.IndexOf(node);
            errors.Add(Located(root, "unreachable-node", pointer,
                $"Node {node.Id} cannot be reached from any start event by following sequence flows and boundary attachments."));
        }
        return errors.Count == 0 ? new([], restored.GetRawText()) : new(Sorted(errors), null);
    }

    /// <summary>Checks one item, or a JSON array of items of one kind, against its canonical model (validate_item).</summary>
    public static WorkflowAiItemCheckResult CheckItems(string requestedKind, string content)
    {
        var kind = ItemKinds.FirstOrDefault(item => string.Equals(item, requestedKind.Trim(), StringComparison.OrdinalIgnoreCase));
        if (kind is null) return new([], $"Unknown kind \"{requestedKind}\". Use one of: {string.Join(", ", ItemKinds)}.");
        if (!WorkflowAiDiagnostics.TryParse(content, out var value, out var malformed)) return new([malformed! with { Path = "/" }]);
        var array = value.ValueKind == JsonValueKind.Array;
        var items = array ? value.EnumerateArray().ToArray() : [value];
        if (items.Length == 0) return new([], "The array is empty; give at least one item.");
        var type = kind switch
        {
            "node" => typeof(FlowNodeModel),
            "variable" => typeof(VariableModel),
            "sequenceFlow" => typeof(SequenceFlowModel),
            _ => typeof(LaneModel)
        };
        var errors = new List<WorkflowAiError>();
        for (var i = 0; i < items.Length; i++)
        {
            var prefix = array ? "/" + i : ""; var label = array ? $"item {i}: " : "";
            errors.AddRange(WorkflowAuthoringJson.InspectItem(items[i], type).Select(issue => new WorkflowAiError(issue.Code,
                Slash(prefix + issue.Pointer), label + WorkflowAiDiagnostics.Locate(items[i], issue.Pointer, kind), issue.Message)));
        }
        return new(Sorted(errors));
    }

    private IEnumerable<WorkflowAiError> DuplicateIds(JsonElement root)
    {
        IEnumerable<(string Pointer, string Kind)> Collections()
        {
            yield return ("/lanes", "lane");
            yield return ("/flowNodes", "node");
            yield return ("/sequenceFlows", "flow");
            yield return ("/variables", "process variable");
            foreach (var owner in new[] { "flowNodes", "sequenceFlows" })
                if (root.TryGetProperty(owner, out var items) && items.ValueKind == JsonValueKind.Array)
                    for (var i = 0; i < items.GetArrayLength(); i++) yield return ($"/{owner}/{i}/variables", "input");
        }
        foreach (var (pointer, kind) in Collections())
        {
            if (!WorkflowAiDiagnostics.At(root, pointer, out var items) || items.ValueKind != JsonValueKind.Array) continue;
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id))
                {
                    var key = id.GetRawText();
                    if (seen.TryGetValue(key, out var first))
                        yield return Located(root, "duplicate-id", $"{pointer}/{index}/id",
                            $"Id {key} is already used by the {kind} at {pointer}/{first}; every {kind} needs its own id.");
                    else seen[key] = index;
                }
                index++;
            }
        }
    }

    // The pointer of the first property name or string value that contains the provider key.
    private string? ProviderKeyPointer(JsonElement element, string pointer)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString()!.Contains(apiKey, StringComparison.Ordinal) ? pointer : null;
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                var child = pointer + "/" + WorkflowAiDiagnostics.Escape(property.Name);
                if (property.Name.Contains(apiKey, StringComparison.Ordinal)) return child;
                if (ProviderKeyPointer(property.Value, child) is { } found) return found;
            }
        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                if (ProviderKeyPointer(item, pointer + "/" + index++) is { } found) return found;
        }
        return null;
    }

    private string Scrub(string message)
    {
        var text = redaction.Sanitize(message)
            .Replace(apiKey, "[provider key removed]", StringComparison.Ordinal)
            .Replace(JsonEncodedText.Encode(apiKey).ToString(), "[provider key removed]", StringComparison.Ordinal);
        return text.Length <= 1500 ? text : text[..1500];
    }

    // Element names and messages can quote workflow text, so both are scrubbed.
    private WorkflowAiError Located(JsonElement root, string code, string pointer, string message) =>
        new(code, Slash(pointer), Scrub(WorkflowAiDiagnostics.Locate(root, pointer)), Scrub(message));

    private static string Slash(string pointer) => pointer.Length == 0 ? "/" : pointer;

    private static IReadOnlyList<WorkflowAiError> Sorted(IEnumerable<WorkflowAiError> errors) => errors.Distinct()
        .OrderBy(error => error.Path, StringComparer.Ordinal).ThenBy(error => error.Code, StringComparer.Ordinal)
        .ThenBy(error => error.Message, StringComparer.Ordinal).ToArray();
}
