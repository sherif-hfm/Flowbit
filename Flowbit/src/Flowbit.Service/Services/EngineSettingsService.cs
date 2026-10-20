using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Ai;
using Flowbit.Service.Models;

namespace Flowbit.Service.Services;

public sealed class EngineSettingsService(
    IEngineSettingsRepository repository,
    ILogger<EngineSettingsService> logger) : IEngineSettingsService
{
    public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
        repository.GetByKeyAsync(key, cancellationToken);

    public async Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken) =>
        WithoutAiModels(await repository.SearchAsync(pattern, cancellationToken));

    public async Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        RejectAiModels(AiModelSettingRules.IsReservedEngineSetting(key));
        var record = await repository.SetAsync(key, value, cancellationToken);
        logger.LogInformation("Engine setting '{Key}' updated.", key);
        return record;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        RejectAiModels(AiModelSettingRules.IsReservedEngineSetting(key));
        var deleted = await repository.DeleteAsync(key, cancellationToken);
        if (deleted)
        {
            logger.LogInformation("Engine setting '{Key}' deleted.", key);
        }
        else
        {
            logger.LogInformation("Delete engine setting '{Key}': not found.", key);
        }
        return deleted;
    }

    public async Task<IReadOnlyList<EngineSettingRecord>> ListAsync(CancellationToken cancellationToken) =>
        WithoutAiModels(await repository.ListAsync(cancellationToken));

    public async Task<EngineSettingRecord> CreateAsync(
        string? settingNamespace,
        string key,
        string value,
        string? description,
        CancellationToken cancellationToken)
    {
        var normalizedNamespace = SettingManagementValidation.NormalizeNamespace(settingNamespace);
        var normalizedKey = SettingManagementValidation.NormalizeIdentifier(key, "key");
        var normalizedValue = SettingManagementValidation.NormalizeEngineValue(value);
        var normalizedDescription = SettingManagementValidation.NormalizeDescription(description);
        RejectAiModels(AiModelSettingRules.IsReservedEngineSetting(normalizedNamespace, normalizedKey));

        var record = await repository.CreateAsync(
            normalizedNamespace,
            normalizedKey,
            normalizedValue,
            normalizedDescription,
            cancellationToken);
        logger.LogInformation(
            "Engine setting '{Namespace}.{Key}' created with id {SettingId}.",
            normalizedNamespace,
            normalizedKey,
            record.Id);
        return record;
    }

    public async Task<EngineSettingRecord?> UpdateAsync(
        long id,
        string value,
        string? description,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken)
    {
        SettingManagementValidation.ValidateMutation(id, expectedUpdatedAt);
        var normalizedValue = SettingManagementValidation.NormalizeEngineValue(value);
        var normalizedDescription = SettingManagementValidation.NormalizeDescription(description);
        await RejectAiModelsAsync(id, cancellationToken);

        var record = await repository.UpdateAsync(
            id,
            normalizedValue,
            normalizedDescription,
            expectedUpdatedAt,
            cancellationToken);
        if (record is not null)
        {
            logger.LogInformation("Engine setting {SettingId} updated.", id);
        }

        return record;
    }

    public async Task<bool> DeleteByIdAsync(
        long id,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken)
    {
        SettingManagementValidation.ValidateMutation(id, expectedUpdatedAt);
        await RejectAiModelsAsync(id, cancellationToken);
        var deleted = await repository.DeleteByIdAsync(id, expectedUpdatedAt, cancellationToken);
        if (deleted)
        {
            logger.LogInformation("Engine setting {SettingId} deleted.", id);
        }

        return deleted;
    }

    // The AI models row holds provider keys and is managed only through IAiModelSettingsService, which uses the repository
    // directly. Namespace and key never change after creation and ids are never reused, so checking a row by id cannot race.
    private static IReadOnlyList<EngineSettingRecord> WithoutAiModels(IReadOnlyList<EngineSettingRecord> settings) =>
        settings.Where(setting => !AiModelSettingRules.IsReservedEngineSetting(setting.Namespace, setting.Key)).ToList();

    private async Task RejectAiModelsAsync(long id, CancellationToken cancellationToken)
    {
        var existing = await repository.GetByIdAsync(id, cancellationToken);
        RejectAiModels(existing is not null && AiModelSettingRules.IsReservedEngineSetting(existing.Namespace, existing.Key));
    }

    private static void RejectAiModels(bool reserved)
    {
        if (reserved)
        {
            throw new WorkflowDomainException(
                $"Engine setting {AiModelSettingRules.LogicalKey} is managed on the AI models page.");
        }
    }
}
