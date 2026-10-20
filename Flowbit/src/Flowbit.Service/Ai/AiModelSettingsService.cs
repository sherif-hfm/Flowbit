using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.Logging;
using StoredAiModel = Flowbit.Service.Ai.AiModelSettingRules.StoredAiModel;

namespace Flowbit.Service.Ai;

/// <summary>
/// Keeps the editor assistant's AI models as one JSON array in engine setting AiAuthoring.Models. It works on the repository
/// directly because the engine-settings service refuses that row. Each model has its own revision. A write reads the row, applies
/// one change and saves the whole array with the row's optimistic check, applying the change again to the latest list when
/// another write landed first. Keys never leave this class except in <see cref="AiModelConnection"/>.
/// </summary>
public sealed class AiModelSettingsService(
    IEngineSettingsRepository repository,
    TimeProvider timeProvider,
    ILogger<AiModelSettingsService> logger) : IAiModelSettingsService, IAiModelStore
{
    private const int MaxWriteAttempts = 3;

    public async Task<IReadOnlyList<AiModelConnection>> GetConnectionsAsync(CancellationToken cancellationToken)
    {
        var row = await repository.GetByKeyAsync(AiModelSettingRules.LogicalKey, cancellationToken);
        if (!AiModelSettingRules.TryParse(row?.Value, out var models))
            throw new WorkflowAiException("provider_configuration",
                "The configured AI models are invalid. Ask a settings administrator to review the AI models page.", 503);
        return models.Select(model => new AiModelConnection(model.Id, model.Name, model.Endpoint, model.Model,
            model.ReasoningEffort, model.ApiKey)).ToArray();
    }

    public async Task<IReadOnlyList<AiModelSettingDto>> ListAsync(CancellationToken cancellationToken)
    {
        var (_, models) = await ReadAsync(cancellationToken);
        return models.Select(ToDto).ToArray();
    }

    public async Task<AiModelSettingDto> CreateAsync(CreateAiModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definition = AiModelSettingRules.NormalizeDefinition(request.Name, request.Endpoint, request.Model, request.ReasoningEffort);
        var apiKey = AiModelSettingRules.NormalizeApiKey(request.ApiKey, required: true)!;
        var id = Guid.NewGuid().ToString("N");
        var created = await WriteAsync(models =>
        {
            if (models.Count >= AiModelSettingRules.MaxModels)
                throw new WorkflowDomainException($"At most {AiModelSettingRules.MaxModels} AI models can be configured.");
            EnsureUniqueName(models, definition.Name, exceptId: null);
            var now = timeProvider.GetUtcNow();
            var model = new StoredAiModel
            {
                Id = id, Name = definition.Name, Endpoint = definition.Endpoint, Model = definition.Model,
                ReasoningEffort = definition.ReasoningEffort, ApiKey = apiKey, Revision = 1, CreatedAt = now, UpdatedAt = now
            };
            models.Add(model);
            return model;
        }, cancellationToken);
        logger.LogInformation("AI model {AiModelId} created.", id);
        return ToDto(created!);
    }

    public async Task<AiModelSettingDto?> UpdateAsync(string id, UpdateAiModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definition = AiModelSettingRules.NormalizeDefinition(request.Name, request.Endpoint, request.Model, request.ReasoningEffort);
        var apiKey = AiModelSettingRules.NormalizeApiKey(request.ApiKey, required: false);
        ValidateRevision(request.ExpectedRevision);
        var updated = await WriteAsync(models =>
        {
            var model = models.FirstOrDefault(item => item.Id == id);
            if (model is null) return null;
            if (model.Revision != request.ExpectedRevision) throw Stale();
            EnsureUniqueName(models, definition.Name, exceptId: id);
            model.Name = definition.Name;
            model.Endpoint = definition.Endpoint;
            model.Model = definition.Model;
            model.ReasoningEffort = definition.ReasoningEffort;
            if (apiKey is not null) model.ApiKey = apiKey;
            model.Revision++;
            model.UpdatedAt = timeProvider.GetUtcNow();
            return model;
        }, cancellationToken);
        if (updated is null) return null;
        logger.LogInformation("AI model {AiModelId} updated to revision {Revision}.", id, updated.Revision);
        return ToDto(updated);
    }

    public async Task<bool> DeleteAsync(string id, long expectedRevision, CancellationToken cancellationToken)
    {
        ValidateRevision(expectedRevision);
        var removed = await WriteAsync(models =>
        {
            var index = models.FindIndex(item => item.Id == id);
            if (index < 0) return null;
            var model = models[index];
            if (model.Revision != expectedRevision) throw Stale();
            models.RemoveAt(index);
            return model;
        }, cancellationToken);
        if (removed is null) return false;
        logger.LogInformation("AI model {AiModelId} deleted.", id);
        return true;
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the latest list and saves it. A null result means there was nothing to change. Errors
    /// thrown by the change, such as a stale model revision, are final; a conflict on the row itself is retried.
    /// </summary>
    private async Task<StoredAiModel?> WriteAsync(Func<List<StoredAiModel>, StoredAiModel?> change, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (row, models) = await ReadAsync(cancellationToken);
            var result = change(models);
            if (result is null) return null;
            var value = AiModelSettingRules.Serialize(models);
            try
            {
                if (row is null)
                {
                    await repository.CreateAsync(AiModelSettingRules.SettingNamespace, AiModelSettingRules.SettingKey, value,
                        AiModelSettingRules.SettingDescription, cancellationToken);
                    return result;
                }
                if (await repository.UpdateAsync(row.Id, value, AiModelSettingRules.SettingDescription, row.UpdatedAt, cancellationToken) is not null)
                    return result;
                // The row was deleted after it was read; apply the change to the list as it is now.
            }
            catch (WorkflowConflictException) when (attempt < MaxWriteAttempts)
            {
                // Another write changed the row after it was read, or created it first.
            }
            catch (WorkflowConflictException)
            {
                throw new WorkflowConflictException("The AI models were changed by other requests; reload and try again.");
            }
            if (attempt >= MaxWriteAttempts)
                throw new WorkflowConflictException("The AI models were changed by other requests; reload and try again.");
        }
    }

    private async Task<(EngineSettingRecord? Row, List<StoredAiModel> Models)> ReadAsync(CancellationToken cancellationToken)
    {
        var row = await repository.GetByKeyAsync(AiModelSettingRules.LogicalKey, cancellationToken);
        if (!AiModelSettingRules.TryParse(row?.Value, out var models))
            throw new WorkflowConflictException(
                $"The stored AI models (engine setting {AiModelSettingRules.LogicalKey}) are invalid. Repair or remove that row in the database.");
        return (row, models);
    }

    private static void EnsureUniqueName(IEnumerable<StoredAiModel> models, string name, string? exceptId)
    {
        if (models.Any(model => model.Id != exceptId && string.Equals(model.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new WorkflowConflictException($"An AI model named '{name}' already exists.");
    }

    private static void ValidateRevision(long expectedRevision)
    {
        if (expectedRevision < 1) throw new WorkflowDomainException("expectedRevision is required.");
    }

    private static WorkflowConflictException Stale() =>
        new("The AI model was changed by another request; reload and try again.");

    private static AiModelSettingDto ToDto(StoredAiModel model) => new(model.Id, model.Name, model.Endpoint, model.Model,
        model.ReasoningEffort, model.Revision, model.CreatedAt, model.UpdatedAt);
}
