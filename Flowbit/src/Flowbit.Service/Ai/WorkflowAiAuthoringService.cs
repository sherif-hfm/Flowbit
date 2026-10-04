using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Authoring;
using Flowbit.Service.Services;
using Flowbit.Shared.Authoring;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Ai;

/// <summary>Transient authoring only: no definition writes, code execution, provider tools, or credential persistence.</summary>
public sealed partial class WorkflowAiAuthoringService(
    IEnumerable<IAiWorkflowProvider> providers,
    IAuthoringKnowledge knowledge,
    IWorkflowDefinitionValidator validator,
    WorkflowDefinitionReadinessChecker readiness,
    WorkflowAiOptions options,
    WorkflowAiConcurrencyGate concurrency,
    ISharedVariableRepository? sharedVariables = null) : IWorkflowAiAuthoringService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 128
    };

    public Task<IReadOnlyList<AiProviderDto>> GetProvidersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AiProviderDto> result = options.Enabled ? providers.Select(provider => provider.Descriptor).ToArray() : [];
        return Task.FromResult(result);
    }

    public async Task<AiValidationResultDto> ValidateAsync(JsonElement definition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(definition.GetRawText()) > options.MaxWorkflowCharacters)
            throw new WorkflowAiException("workflow_too_large", "The workflow exceeds the authoring size limit.", 413);
        try
        {
            var model = WorkflowAuthoringJson.Parse(definition.GetRawText());
            return await ValidateModelAsync(model, cancellationToken);
        }
        catch (Exception error) when (IsDefinitionError(error))
        {
            return Invalid(SafeDiagnostic(error));
        }
    }

    private async Task<AiValidationResultDto> ValidateModelAsync(WorkflowModel model, CancellationToken cancellationToken)
    {
        using var activity = WorkflowAiTelemetry.Start("draft.validate");
        activity?.SetTag("outcome", "invalid");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            validator.ValidateAuthored(model);
            cancellationToken.ThrowIfCancellationRequested();
            WorkflowModelMigrator.Normalize(model);
            validator.ValidateNormalized(model);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (IsDefinitionError(error)) { return Invalid(SafeDiagnostic(error)); }
        var saveBlockers = new List<string>();
        var publicationBlockers = new List<string>();
        try { await readiness.ValidateSharedCatalogBindingsAsync(model, cancellationToken); }
        catch (WorkflowDomainException error) { saveBlockers.Add(SafeDiagnostic(error)); }
        try { readiness.ValidateSharedServiceTaskDurability(model); }
        catch (WorkflowDomainException error) { saveBlockers.Add(SafeDiagnostic(error)); }
        try { readiness.ValidateSharedTransactionLockOrder(model); }
        catch (WorkflowDomainException error) { saveBlockers.Add(SafeDiagnostic(error)); }
        try { readiness.EnsureDurablePublicationAllowed(model, publish: true); }
        catch (WorkflowDomainException error) { publicationBlockers.Add(SafeDiagnostic(error)); }
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        if (model.FlowNodes.Any(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)))
            warnings.Add("Review service endpoints, credentials, response mappings, and network access before running this workflow.");
        if (model.FlowNodes.Any(node => node.Type == "scriptTask"))
            warnings.Add("Review generated scripts before publication; authoring validation does not execute them.");
        activity?.SetTag("outcome", "valid");
        return new(true, [], warnings)
        {
            CanSave = saveBlockers.Count == 0,
            CanPublish = saveBlockers.Count == 0 && publicationBlockers.Count == 0,
            SaveBlockers = saveBlockers,
            PublicationBlockers = publicationBlockers
        };
    }

    private void ValidateRequest(AiTurnRequestDto request, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192 || apiKey.Any(char.IsControl))
            throw new WorkflowAiException("invalid_key", "Enter a valid provider API key.");
        if (!Guid.TryParse(request.ConversationId, out _) || string.IsNullOrWhiteSpace(request.Message)
            || string.IsNullOrWhiteSpace(request.SnapshotId) || request.SnapshotId.Length > 256 || request.History is null || request.Sources is null || request.SharedVariableKeys is null)
            throw new WorkflowAiException("invalid_request", "A conversation id, requirement message, and editor snapshot are required.");
        if (request.History.Count > options.MaxHistoryMessages || request.Sources.Count > 200
            || request.History.Any(item => item is null || (item.Role != "user" && item.Role != "assistant") || item.Content is null)
            || request.Sources.Any(source => source is null || source.PageNumber < 1 || string.IsNullOrWhiteSpace(source.SourceName) || source.SourceName.Length > 300 || source.Text is null))
            throw new WorkflowAiException("invalid_context", "The authoring conversation or document context is invalid or too large.");
        long inputLength = request.Message.Length;
        inputLength += request.History.Sum(item => (long)item.Content.Length);
        inputLength += request.Sources.Sum(source => (long)source.Text.Length + source.SourceName.Length);
        if (inputLength > options.MaxInputCharacters)
            throw new WorkflowAiException("input_too_large", "The conversation and source documents exceed the authoring input limit.", 413);
        if (request.SharedVariableKeys.Count > 50 || request.SharedVariableKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length > 300)
            || request.SharedVariableKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.SharedVariableKeys.Count)
            throw new WorkflowAiException("invalid_catalog_selection", "Select up to 50 distinct shared-variable keys.");
        if (request.CurrentWorkflow is { } current && Encoding.UTF8.GetByteCount(current.GetRawText()) > options.MaxWorkflowCharacters)
            throw new WorkflowAiException("workflow_too_large", "The editor workflow exceeds the authoring size limit.", 413);
    }

    private async Task<IReadOnlyList<SelectedSharedVariableMetadata>> LoadSelectedCatalogAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0) return [];
        if (sharedVariables is null)
            throw new WorkflowAiException("catalog_unavailable", "Shared-variable storage is not configured.", 503);
        var records = await sharedVariables.GetManyByKeyAsync(keys, includeArchived: true, cancellationToken);
        var result = new List<SelectedSharedVariableMetadata>();
        foreach (var key in keys)
        {
            if (!records.TryGetValue(key, out var record) || record.Key != key || record.Status != SharedVariableStatuses.Active)
                throw new WorkflowAiException("catalog_selection_changed", "A selected shared variable is unavailable or archived. Refresh the catalog selection.");
            result.Add(new(record.Key, record.DataType, record.IsArray, record.Nullable, record.Validation, record.Description, record.Status));
        }
        return result;
    }

    private static JsonDocument ParseProviderCommand(string response, string apiKey)
    {
        // Inspect decoded keys before value scrubbing. Renaming a JSON member or pointer would change
        // the requested operation, and raw-text replacement misses Unicode-escaped provider keys.
        var document = ParseCommand(response);
        try
        {
            CheckMembers(document.RootElement);
            if (document.RootElement.TryGetProperty("batchId", out var batchId) && batchId.ValueKind == JsonValueKind.String)
                RejectEcho(batchId.GetString()!);
            if (document.RootElement.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
                foreach (var operation in operations.EnumerateArray())
                    if (operation.ValueKind == JsonValueKind.Object && operation.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
                    {
                        RejectEcho(path.GetString()!);
                        foreach (var segment in path.GetString()!.Split('/'))
                            RejectEcho(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
                    }
            return document;
        }
        catch { document.Dispose(); throw; }

        void CheckMembers(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                {
                    RejectEcho(property.Name);
                    CheckMembers(property.Value);
                }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) CheckMembers(child);
        }

        void RejectEcho(string text)
        {
            if (text.Contains(apiKey, StringComparison.Ordinal))
                throw new JsonException("Provider credentials cannot appear in JSON property names, edit paths, or batch identifiers.");
        }
    }

    private static Envelope ParseEnvelope(string response, IReadOnlyList<AiSourcePageDto> sources, string apiKey)
    {
        var text = response.Trim();
        if (text.StartsWith("```json", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal)) text = text[7..^3].Trim();
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 128 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Authoring response must be a JSON object.");
        var allowed = new HashSet<string>(["kind", "message", "questions", "definition", "assumptions", "dependencies", "changeSummary", "sourceReferences"], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw new JsonException("Unknown or duplicate response property: " + property.Name);
        // Scrub decoded strings too: an upstream response can spell a key using JSON Unicode escapes.
        var scrubbed = JsonNode.Parse(root.GetRawText())!;
        SanitizeValues(scrubbed, text => RemoveApiKey(text, apiKey));
        root = JsonSerializer.SerializeToElement(scrubbed);
        var kind = RequiredString(root, "kind");
        var message = RequiredString(root, "message");
        var references = new List<AiSourceReferenceDto>();
        if (root.TryGetProperty("sourceReferences", out var refs))
        {
            if (refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() > 200) throw new JsonException("Invalid source references.");
            foreach (var reference in refs.EnumerateArray())
            {
                if (reference.ValueKind != JsonValueKind.Object) throw new JsonException("Source reference must be an object.");
                var referenceNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in reference.EnumerateObject())
                    if (property.Name is not ("sourceName" or "pageNumber" or "requirement") || !referenceNames.Add(property.Name))
                        throw new JsonException("Unknown or duplicate source reference property.");
                var source = RequiredString(reference, "sourceName");
                if (!reference.TryGetProperty("pageNumber", out var page) || page.ValueKind != JsonValueKind.Number || !page.TryGetInt32(out var number)
                    || !sources.Any(item => item.SourceName == source && item.PageNumber == number))
                    throw new JsonException("Source reference must identify an uploaded source page.");
                references.Add(new(source, number, RequiredString(reference, "requirement")));
            }
        }
        JsonElement? definition = root.TryGetProperty("definition", out var content) && content.ValueKind != JsonValueKind.Null ? content.Clone() : null;
        return new(kind, message, StringArray(root, "questions"), definition, StringArray(root, "assumptions"),
            StringArray(root, "dependencies"), StringArray(root, "changeSummary"), references);
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 10_000 } text
            ? text : throw new JsonException("Expected a bounded string for " + name + ".");

    private static IReadOnlyList<string> StringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array)) return [];
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 100) throw new JsonException("Expected a bounded string array for " + name + ".");
        return array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: <= 10_000 } text
            ? text : throw new JsonException("Expected a string in " + name + ".")).ToArray();
    }

    private AiTurnResultDto Result(Envelope envelope, JsonElement? definition, AiValidationResultDto validation, AiTurnRequestDto request) =>
        new(envelope.Kind, envelope.Message, envelope.Questions, definition, envelope.Assumptions, envelope.Dependencies,
            envelope.ChangeSummary, validation, request.SnapshotId, knowledge.ContractHash) { SourceReferences = envelope.SourceReferences };

    private static AiValidationResultDto Invalid(string error) => new(false, [error], []);
    private static AiValidationResultDto ScrubDiagnostics(AiValidationResultDto result, WorkflowAiRedaction redaction, string apiKey) => result with
    {
        Errors = result.Errors.Select(error => redaction.Sanitize(RemoveApiKey(error, apiKey))).ToArray(),
        Warnings = result.Warnings.Select(error => redaction.Sanitize(RemoveApiKey(error, apiKey))).ToArray(),
        SaveBlockers = result.SaveBlockers.Select(error => redaction.Sanitize(RemoveApiKey(error, apiKey))).ToArray(),
        PublicationBlockers = result.PublicationBlockers.Select(error => redaction.Sanitize(RemoveApiKey(error, apiKey))).ToArray()
    };
    private static bool IsDefinitionError(Exception error) => error is JsonException or WorkflowDomainException or FormatException or ArgumentException;
    private static string SafeDiagnostic(Exception error) => error is JsonException
        ? "Invalid workflow JSON: " + error.Message.Split(" Path:", StringSplitOptions.None)[0][..Math.Min(error.Message.Split(" Path:", StringSplitOptions.None)[0].Length, 1500)]
        : error.Message[..Math.Min(error.Message.Length, 1500)];
    private static string RemoveApiKey(string content, string apiKey) => content
        .Replace(apiKey, "[provider key removed]", StringComparison.Ordinal)
        .Replace(JsonEncodedText.Encode(apiKey).ToString(), "[provider key removed]", StringComparison.Ordinal);

    private static void SanitizeValues(JsonNode node, Func<string, string> sanitize)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
                if (property.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) obj[property.Key] = sanitize(text);
                else if (property.Value is { } child) SanitizeValues(child, sanitize);
        }
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++)
                if (array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) array[i] = sanitize(text);
                else if (array[i] is { } child) SanitizeValues(child, sanitize);
    }
    private sealed record Envelope(string Kind, string Message, IReadOnlyList<string> Questions, JsonElement? Definition,
        IReadOnlyList<string> Assumptions, IReadOnlyList<string> Dependencies, IReadOnlyList<string> ChangeSummary, IReadOnlyList<AiSourceReferenceDto> SourceReferences);
    private sealed record SelectedSharedVariableMetadata(string Key, string DataType, bool IsArray, bool Nullable,
        string? Validation, string? Description, string Status);
}
