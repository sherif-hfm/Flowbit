using Microsoft.Extensions.Logging;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed class WorkflowDefinitionService(
    IWorkflowDefinitionRepository definitions,
    IWorkflowDefinitionValidator validator,
    ILogger<WorkflowDefinitionService> logger,
    DurableProcessingOptions? durableProcessingOptions = null,
    IConditionalEventDefinitionAnalyzer? conditionalEventAnalyzer = null,
    IConditionalEventDependencyPlanCache? conditionalEventPlanCache = null,
    ISharedVariableRepository? sharedVariables = null,
    ISharedVariableAccessPlanCache? sharedVariableAccessPlanCache = null)
    : IWorkflowDefinitionService
{
    private readonly WorkflowDefinitionReadinessChecker readiness =
        new(sharedVariables, conditionalEventAnalyzer, durableProcessingOptions);
    private readonly IConditionalEventDefinitionAnalyzer conditionalAnalyzer =
        conditionalEventAnalyzer ?? new ConditionalEventDefinitionAnalyzer();

    public async Task<IReadOnlyList<WorkflowSummaryDto>> ListLatestAsync(CancellationToken cancellationToken)
    {
        var records = await definitions.ListLatestAsync(cancellationToken);
        return records.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<WorkflowSummaryDto>> ListVersionsAsync(string workflowKey, CancellationToken cancellationToken)
    {
        var records = await definitions.ListVersionsByKeyAsync(workflowKey, cancellationToken);
        return records.Select(ToSummary).ToList();
    }

    public async Task<WorkflowDetailDto?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var record = await definitions.GetAsync(id, cancellationToken);
        return record is null ? null : ToDetail(record);
    }

    public async Task<WorkflowDetailDto> CreateAsync(
        WorkflowModel definition,
        bool publish,
        CancellationToken cancellationToken)
    {
        validator.ValidateAuthored(definition);
        WorkflowModelMigrator.Normalize(definition);
        validator.ValidateNormalized(definition);
        await readiness.ValidateSharedCatalogBindingsAsync(definition, cancellationToken);
        readiness.ValidateSharedServiceTaskDurability(definition);
        readiness.ValidateSharedTransactionLockOrder(definition);
        readiness.EnsureDurablePublicationAllowed(definition, publish);
        var name = definition.Name.Trim();
        var created = await definitions.AddAsync(name, definition, publish, cancellationToken);
        _ = conditionalEventPlanCache?.GetOrAdd(created.Id, created.Definition);
        _ = sharedVariableAccessPlanCache?.GetOrAdd(created.Id, created.Definition);
        logger.LogInformation("Created workflow definition {WorkflowId} '{Name}' v{Version} (published={Published}, default={Default}).", created.Id, name, created.Version, publish, created.IsDefault);
        return ToDetail(created);
    }

    public async Task<WorkflowDetailDto?> CreateNewVersionAsync(
        long sourceWorkflowId,
        WorkflowModel definition,
        bool publish,
        CancellationToken cancellationToken)
    {
        var source = await definitions.GetAsync(sourceWorkflowId, cancellationToken);
        if (source is null)
        {
            logger.LogInformation("Create new version from workflow {WorkflowId}: source not found.", sourceWorkflowId);
            return null;
        }

        definition.Id = source.WorkflowKey;
        validator.ValidateAuthored(definition);
        WorkflowModelMigrator.Normalize(definition);
        validator.ValidateNormalized(definition);
        await readiness.ValidateSharedCatalogBindingsAsync(definition, cancellationToken);
        readiness.ValidateSharedServiceTaskDurability(definition);
        readiness.ValidateSharedTransactionLockOrder(definition);
        readiness.EnsureDurablePublicationAllowed(definition, publish);
        var name = string.IsNullOrWhiteSpace(definition.Name) ? source.Name : definition.Name.Trim();
        var created = await definitions.AddAsync(name, definition, publish, cancellationToken);
        _ = conditionalEventPlanCache?.GetOrAdd(created.Id, created.Definition);
        _ = sharedVariableAccessPlanCache?.GetOrAdd(created.Id, created.Definition);
        logger.LogInformation("Created new workflow version {WorkflowId} '{Name}' v{Version} from source {SourceWorkflowId} (published={Published}, default={Default}).", created.Id, name, created.Version, sourceWorkflowId, publish, created.IsDefault);
        return ToDetail(created);
    }

    public async Task<bool> PublishAsync(long id, CancellationToken cancellationToken)
    {
        var definition = await definitions.GetAsync(id, cancellationToken);
        if (definition is null)
        {
            logger.LogInformation("Publish workflow {WorkflowId}: definition not found.", id);
            return false;
        }
        _ = conditionalAnalyzer.Analyze(definition.Definition);
        await readiness.ValidateSharedCatalogBindingsAsync(
            definition.Definition,
            cancellationToken);
        readiness.ValidateSharedServiceTaskDurability(definition.Definition);
        readiness.ValidateSharedTransactionLockOrder(definition.Definition);
        readiness.EnsureDurablePublicationAllowed(definition.Definition, publish: true);
        var published = await definitions.SetPublishedAsync(id, true, cancellationToken);
        if (published)
        {
            logger.LogInformation("Workflow definition {WorkflowId} published.", id);
        }
        else
        {
            logger.LogInformation("Publish workflow {WorkflowId}: definition not found.", id);
        }
        return published;
    }

    public async Task<bool> UnpublishAsync(long id, CancellationToken cancellationToken)
    {
        var unpublished = await definitions.SetPublishedAsync(id, false, cancellationToken);
        if (unpublished)
        {
            logger.LogInformation("Workflow definition {WorkflowId} unpublished.", id);
        }
        else
        {
            logger.LogInformation("Unpublish workflow {WorkflowId}: definition not found.", id);
        }
        return unpublished;
    }

    public async Task<bool> SetDefaultAsync(long id, CancellationToken cancellationToken)
    {
        var definition = await definitions.GetAsync(id, cancellationToken);
        if (definition is null)
        {
            logger.LogInformation("Set default workflow {WorkflowId}: definition not found.", id);
            return false;
        }
        _ = conditionalAnalyzer.Analyze(definition.Definition);
        await readiness.ValidateSharedCatalogBindingsAsync(
            definition.Definition,
            cancellationToken);
        readiness.ValidateSharedServiceTaskDurability(definition.Definition);
        readiness.ValidateSharedTransactionLockOrder(definition.Definition);
        readiness.EnsureDurablePublicationAllowed(definition.Definition, publish: true);
        var set = await definitions.SetDefaultAsync(id, true, cancellationToken);
        if (set)
        {
            logger.LogInformation("Workflow definition {WorkflowId} set as default.", id);
        }
        else
        {
            logger.LogInformation("Set default workflow {WorkflowId}: definition not found.", id);
        }
        return set;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var deleted = await definitions.DeleteAsync(id, cancellationToken);
        if (deleted)
        {
            conditionalEventPlanCache?.Remove(id);
            sharedVariableAccessPlanCache?.Remove(id);
            logger.LogInformation("Workflow definition {WorkflowId} deleted.", id);
        }
        else
        {
            logger.LogInformation("Delete workflow {WorkflowId}: definition not found.", id);
        }
        return deleted;
    }

    internal static WorkflowSummaryDto ToSummary(WorkflowDefinitionRecord record) =>
        new(record.Id, record.Name, record.WorkflowKey, record.Version, record.IsPublished, record.IsDefault, record.CreatedAt);

    internal static WorkflowDetailDto ToDetail(WorkflowDefinitionRecord record) =>
        new(record.Id, record.Name, record.WorkflowKey, record.Version, record.IsPublished, record.IsDefault, record.CreatedAt, record.Definition);
}
