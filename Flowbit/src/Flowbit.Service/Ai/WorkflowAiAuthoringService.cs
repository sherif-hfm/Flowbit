using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Authoring;
using Flowbit.Service.Services;
using Flowbit.Shared.Authoring;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Ai;

/// <summary>Transient authoring only: no definition writes, code execution, or credential persistence.</summary>
public sealed partial class WorkflowAiAuthoringService(
    IWorkflowAiChatClientFactory clients,
    IAuthoringKnowledge knowledge,
    WorkflowAiDocs docs,
    IWorkflowDefinitionValidator validator,
    WorkflowDefinitionReadinessChecker readiness,
    WorkflowAiOptions options,
    WorkflowAiConcurrencyGate concurrency,
    ISharedVariableRepository? sharedVariables = null) : IWorkflowAiAuthoringService
{
    private static readonly JsonSerializerOptions CatalogOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public Task<IReadOnlyList<AiProviderDto>> GetProvidersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AiProviderDto> result = options.Enabled ? [clients.Descriptor] : [];
        return Task.FromResult(result);
    }

    public Task<AiTurnResultDto> TurnAsync(AiTurnRequestDto request, string apiKey, CancellationToken cancellationToken)
        => RunAsync(request, apiKey, null, cancellationToken);

    public async Task<AiTurnResultDto> RunAsync(AiTurnRequestDto request, string apiKey,
        Func<AiRunEventDto, CancellationToken, Task>? emit, CancellationToken cancellationToken)
    {
        if (!options.Enabled) throw new WorkflowAiException("ai_disabled", "AI authoring is disabled.", 503);
        options.Validate();
        ValidateRequest(request, apiKey);
        if (request.ProviderId != clients.Descriptor.Id) throw new WorkflowAiException("unsupported_provider", "Select an enabled AI provider.");
        if (!clients.Descriptor.Models.Any(model => model.Id == request.ModelId)) throw new WorkflowAiException("unsupported_model", "Select an enabled model.");
        if (!await concurrency.Semaphore.WaitAsync(0, cancellationToken))
            throw new WorkflowAiException("authoring_busy", "AI authoring is busy. Try again when another request finishes.", 429);
        try
        {
            var catalog = await LoadSelectedCatalogAsync(request.SharedVariableKeys, cancellationToken);
            // Value-free contracts of the selected catalog entries, given to the model with the message.
            var shared = catalog.Count == 0 ? null : JsonSerializer.Serialize(catalog.Select(item => new
                { item.Key, item.DataType, item.IsArray, item.Nullable, item.Validation, item.Description }), CatalogOptions);
            var conversation = new WorkflowAiConversation(clients, docs, validator, options, knowledge.ContractHash, ValidateModelAsync);
            return await conversation.RunAsync(request, apiKey, shared, emit, cancellationToken);
        }
        finally
        {
            concurrency.Semaphore.Release();
        }
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
        if (!Guid.TryParse(request.ConversationId, out _) || string.IsNullOrWhiteSpace(request.SnapshotId) || request.SnapshotId.Length > 256
            || request.Message is null || request.Sources is null || request.SharedVariableKeys is null)
            throw new WorkflowAiException("invalid_request", "A conversation id, message, and editor snapshot are required.");
        if (request.Continue ? request.Message.Length > 0 : string.IsNullOrWhiteSpace(request.Message))
            throw new WorkflowAiException("invalid_request", request.Continue
                ? "Continue resumes the stopped run without a new message. Send the message separately."
                : "Enter a message for the assistant.");
        if (request.Sources.Count > 200
            || request.Sources.Any(source => source is null || source.PageNumber < 1 || string.IsNullOrWhiteSpace(source.SourceName) || source.SourceName.Length > 300 || source.Text is null))
            throw new WorkflowAiException("invalid_context", "The document context is invalid or too large.");
        long inputLength = request.Message.Length;
        inputLength += request.Sources.Sum(source => (long)source.Text.Length + source.SourceName.Length);
        if (inputLength > options.MaxInputCharacters)
            throw new WorkflowAiException("input_too_large", "The message and source documents exceed the authoring input limit.", 413);
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

    private static AiValidationResultDto Invalid(string error) => new(false, [error], []);
    private static bool IsDefinitionError(Exception error) => error is JsonException or WorkflowDomainException or FormatException or ArgumentException;
    private static string SafeDiagnostic(Exception error) => error is JsonException
        ? "Invalid workflow JSON: " + error.Message.Split(" Path:", StringSplitOptions.None)[0][..Math.Min(error.Message.Split(" Path:", StringSplitOptions.None)[0].Length, 1500)]
        : error.Message[..Math.Min(error.Message.Length, 1500)];

    private sealed record SelectedSharedVariableMetadata(string Key, string DataType, bool IsArray, bool Nullable,
        string? Validation, string? Description, string Status);
}
