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
    ISharedVariableAccessPlanCache? sharedVariableAccessPlanCache = null,
    IBusinessCalendarRepository? businessCalendars = null,
    ICallActivityRepository? callActivities = null,
    AutomaticMultiInstanceOptions? automaticMultiInstanceOptions = null)
    : IWorkflowDefinitionService
{
    private readonly WorkflowDefinitionReadinessChecker readiness =
        new(sharedVariables, conditionalEventAnalyzer, durableProcessingOptions, businessCalendars, definitions,
            automaticMultiInstanceOptions);
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
        await readiness.ValidateBusinessCalendarReferencesAsync(definition, cancellationToken);
        readiness.ValidateSharedServiceTaskDurability(definition);
        readiness.ValidateSharedTransactionLockOrder(definition);
        readiness.EnsureDurablePublicationAllowed(definition, publish);
        if (publish)
        {
            await readiness.ValidateCallTargetsAsync(definition, cancellationToken);
        }
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
        await readiness.ValidateBusinessCalendarReferencesAsync(definition, cancellationToken);
        readiness.ValidateSharedServiceTaskDurability(definition);
        readiness.ValidateSharedTransactionLockOrder(definition);
        readiness.EnsureDurablePublicationAllowed(definition, publish);
        if (publish)
        {
            await readiness.ValidateCallTargetsAsync(definition, cancellationToken);
        }
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
        await readiness.ValidateBusinessCalendarReferencesAsync(
            definition.Definition,
            cancellationToken);
        readiness.ValidateSharedServiceTaskDurability(definition.Definition);
        readiness.ValidateSharedTransactionLockOrder(definition.Definition);
        readiness.EnsureDurablePublicationAllowed(definition.Definition, publish: true);
        await readiness.ValidateCallTargetsAsync(definition.Definition, cancellationToken);
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
        await readiness.ValidateBusinessCalendarReferencesAsync(
            definition.Definition,
            cancellationToken);
        readiness.ValidateSharedServiceTaskDurability(definition.Definition);
        readiness.ValidateSharedTransactionLockOrder(definition.Definition);
        readiness.EnsureDurablePublicationAllowed(definition.Definition, publish: true);
        await readiness.ValidateCallTargetsAsync(definition.Definition, cancellationToken);
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

    public async Task<CallersPreviewDto?> GetCallersAsync(
        string workflowKey,
        long? candidateId,
        long? deletingId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (candidateId is not null && deletingId is not null)
        {
            throw new WorkflowDomainException("candidateId and deletingId cannot be used together.");
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var current = await definitions.GetDefaultByWorkflowKeyAsync(workflowKey, cancellationToken);
        var mode = CallersPreviewModes.Current;
        var target = current;
        bool? deletionAllowed = null;
        IReadOnlyList<string> deletionBlockers = [];
        if (candidateId is long candidate)
        {
            var version = await definitions.GetAsync(candidate, cancellationToken);
            if (version is null || !string.Equals(version.WorkflowKey, workflowKey, StringComparison.Ordinal))
            {
                return null;
            }

            if (!version.IsPublished)
            {
                throw new WorkflowDomainException($"Workflow version #{candidate} is not published.");
            }

            mode = CallersPreviewModes.Candidate;
            target = version;
        }
        else if (deletingId is long deleting)
        {
            var version = await definitions.GetAsync(deleting, cancellationToken);
            if (version is null || !string.Equals(version.WorkflowKey, workflowKey, StringComparison.Ordinal))
            {
                return null;
            }

            // The version's own flag, not the published default: a family's first version is
            // its default even as a draft, and deleting one can promote a published successor.
            if (!version.IsDefault)
            {
                throw new WorkflowDomainException($"Workflow version #{deleting} is not the default of '{workflowKey}'.");
            }

            var assessment = await definitions.AssessDeletionAsync(deleting, cancellationToken);
            if (assessment is null)
            {
                return null;
            }

            mode = CallersPreviewModes.Deletion;
            deletionAllowed = assessment.Allowed;
            deletionBlockers = assessment.Blockers;

            // A blocked deletion leaves the default as it is, so the callers keep their
            // current target; an allowed one moves them to the actual successor.
            if (assessment.Allowed)
            {
                target = assessment.SuccessorId is long successor
                    ? await definitions.GetAsync(successor, cancellationToken)
                    : null;
            }
        }

        var callers = callActivities is null
            ? new PagedResult<WorkflowCallerRecord>([], page, pageSize, 0)
            : await callActivities.ListPublishedCallersAsync(workflowKey, page, pageSize, cancellationToken);
        var callerDefinitions = await definitions.GetManyAsync(
            callers.Items.Select(caller => caller.WorkflowDefinitionId).Distinct().ToArray(),
            cancellationToken);
        return new CallersPreviewDto(
            mode,
            target?.Id,
            target?.Version,
            target is null,
            deletionAllowed,
            deletionBlockers,
            new PagedResult<WorkflowCallerDto>(
                callers.Items
                    .Select(caller => ToCaller(
                        caller,
                        callerDefinitions.GetValueOrDefault(caller.WorkflowDefinitionId),
                        target?.Definition))
                    .ToList(),
                callers.Page,
                callers.PageSize,
                callers.TotalCount));
    }

    private WorkflowCallerDto ToCaller(
        WorkflowCallerRecord caller,
        WorkflowDefinitionRecord? definition,
        WorkflowModel? target)
    {
        var node = definition?.Definition.FlowNodes.FirstOrDefault(candidate => candidate.Id == caller.NodeId);
        var problems = node is null
            ? []
            : readiness.FindCallTargetProblems(definition!.Definition, node, target);
        return new WorkflowCallerDto(
            caller.WorkflowDefinitionId,
            caller.WorkflowKey,
            caller.Name,
            caller.Version,
            caller.IsDefault,
            caller.NodeId,
            node?.Name,
            problems.Count == 0,
            problems.Select(problem => problem.Code).Distinct().ToArray(),
            problems.Select(problem => problem.Message).ToArray());
    }

    internal static WorkflowSummaryDto ToSummary(WorkflowDefinitionRecord record) =>
        new(record.Id, record.Name, record.WorkflowKey, record.Version, record.IsPublished, record.IsDefault, record.CreatedAt);

    internal static WorkflowDetailDto ToDetail(WorkflowDefinitionRecord record) =>
        new(record.Id, record.Name, record.WorkflowKey, record.Version, record.IsPublished, record.IsDefault, record.CreatedAt, record.Definition);
}
