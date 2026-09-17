using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

/// <summary>
/// Focused query service for the instance list and advanced instance search
/// routes. Parses query input, resolves the dynamic instance-list
/// authorization, delegates membership/count/ordering/paging to the SQL-backed
/// repository port, enriches the selected page, and builds summary DTOs.
/// </summary>
public sealed class WorkflowInstanceQueryService(
    IWorkflowInstanceQueryRepository runtime,
    IWorkflowDefinitionRepository definitions,
    IWorkflowJobRepository jobs,
    IEngineSettingsRepository engineSettings,
    IWorkflowVariableStore? workflowVariables = null) : IWorkflowInstanceQueryService
{
    internal const string InstanceListRequiredRoleSettingKey =
        "WorkflowInstances.RequiredRole";
    internal const string DefaultInstanceListRequiredRole = "admin";

    public Task<PagedResult<InstanceSummaryDto>> ListInstancesAsync(
        ActorContext actor,
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<string>? variables,
        IReadOnlyList<string>? sort,
        string? cursor,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        ListInstancesCoreAsync(
            actor, status, instanceId, workflowId, workflowKey, businessKey,
            nodeId, nodeExternalId,
            VariableFilterParser.FromLegacy(WorkflowQueryInputParser.ParseVariableFilters(variables)),
            ParseInstanceSort(sort), cursor, includeVariables, page, pageSize,
            cancellationToken);

    public Task<PagedResult<InstanceSummaryDto>> SearchInstancesAsync(
        ActorContext actor,
        InstanceSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ListInstancesCoreAsync(
            actor,
            request.Status,
            request.InstanceId,
            request.WorkflowId,
            request.WorkflowKey,
            request.BusinessKey,
            request.NodeId,
            request.NodeExternalId,
            VariableFilterParser.Parse(request.VariableFilter),
            ParseInstanceSort(WorkflowQueryInputParser.ToLegacySort(request.Sort)),
            request.Cursor,
            request.IncludeVariables ?? false,
            Math.Max(1, request.Page ?? 1),
            Math.Clamp(request.PageSize ?? 50, 1, 200),
            cancellationToken);
    }

    private async Task<PagedResult<InstanceSummaryDto>> ListInstancesCoreAsync(
        ActorContext actor,
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        VariableFilterExpression? variableFilter,
        IReadOnlyList<InstanceSortCriterion> sortCriteria,
        string? cursor,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalizedSort = WorkflowInstanceCursor.NormalizeSort(sortCriteria);
        if (!string.IsNullOrWhiteSpace(cursor)
            && !WorkflowInstanceCursor.TryDecode(
                cursor,
                normalizedSort,
                out _))
        {
            throw new WorkflowDomainException(
                "The instance cursor is invalid, expired, or belongs to a different sort order.");
        }
        if (string.IsNullOrWhiteSpace(cursor) && page > 1)
        {
            throw new WorkflowDomainException(
                "Instance pages after the first require the opaque cursor returned by the preceding page.");
        }

        var authorization = await ResolveInstanceListAuthorizationAsync(
            actor,
            cancellationToken);
        var paged = await runtime.ListInstancesAsync(
            status,
            instanceId,
            workflowId,
            workflowKey,
            businessKey,
            nodeId,
            nodeExternalId,
            variableFilter,
            normalizedSort,
            authorization,
            cursor,
            includeVariables,
            page,
            pageSize,
            cancellationToken);
        var jobSummaries = await jobs.GetInstanceJobSummariesAsync(
            paged.Items.Select(item => item.Id).ToArray(),
            cancellationToken);
        var sharedMetadataByWorkflowId = new Dictionary<long, IReadOnlyList<SharedVariableBindingMetadataDto>>();
        if (includeVariables && workflowVariables is not null)
        {
            var workflowRecords = await definitions.GetManyAsync(
                paged.Items.Select(item => item.WorkflowId).Distinct().ToArray(),
                cancellationToken);
            foreach (var pair in workflowRecords)
            {
                sharedMetadataByWorkflowId[pair.Key] = await workflowVariables.DescribeBindingsAsync(
                    pair.Value.Definition,
                    cancellationToken);
            }
        }
        var items = paged.Items.Select(row =>
        {
            var summary = ToSummary(row);
            if (jobSummaries.TryGetValue(row.Id, out var jobsForInstance))
            {
                summary = summary with
                {
                    Jobs = new InstanceJobSummaryDto(
                        jobsForInstance.OpenCount,
                        jobsForInstance.QueuedCount,
                        jobsForInstance.RunningCount,
                        jobsForInstance.IncidentCount,
                        jobsForInstance.NearestDueAt)
                };
            }
            if (sharedMetadataByWorkflowId.TryGetValue(
                    row.WorkflowId,
                    out var sharedMetadata))
            {
                summary = summary with { SharedVariables = sharedMetadata };
            }
            return summary;
        }).ToArray();
        return new PagedResult<InstanceSummaryDto>(
            items,
            paged.Page,
            paged.PageSize,
            paged.TotalCount)
        {
            NextCursor = paged.NextCursor
        };
    }

    private async Task<InstanceListAuthorization> ResolveInstanceListAuthorizationAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var setting = await engineSettings.GetByKeyAsync(
            InstanceListRequiredRoleSettingKey,
            cancellationToken);
        var configuredGlobalRoles = string.IsNullOrWhiteSpace(setting?.Value)
            ? [DefaultInstanceListRequiredRole]
            : setting.Value
                .Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Where(static role => role.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        if (configuredGlobalRoles.Length == 0)
        {
            configuredGlobalRoles = [DefaultInstanceListRequiredRole];
        }

        var lowerCallerRoles = actor.Roles
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Select(static role => role.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var isGlobalReader = configuredGlobalRoles
            .Select(static role => role.ToLowerInvariant())
            .Intersect(lowerCallerRoles, StringComparer.Ordinal)
            .Any();
        return new InstanceListAuthorization(isGlobalReader, lowerCallerRoles);
    }

    private static IReadOnlyList<InstanceSortCriterion> ParseInstanceSort(IReadOnlyList<string>? sort)
    {
        if (sort is null || sort.Count == 0)
        {
            return [new InstanceSortCriterion(InstanceSortField.UpdatedAt, SortDirection.Descending)];
        }

        return WorkflowQueryInputParser.ParseSort(
            sort,
            field => field.ToLowerInvariant() switch
            {
                "id" => InstanceSortField.Id,
                "createdat" => InstanceSortField.CreatedAt,
                "updatedat" => InstanceSortField.UpdatedAt,
                _ => throw new WorkflowDomainException(
                    $"Unknown instance sort field '{field}'. Allowed fields: id, createdAt, updatedAt.")
            },
            static (field, direction) => new InstanceSortCriterion(field, direction));
    }

    private static InstanceSummaryDto ToSummary(InstanceListItem row) =>
        new(
            row.Id,
            row.WorkflowId,
            row.WorkflowName,
            row.WorkflowVersion,
            row.CurrentNodeId,
            row.CurrentNodeName,
            row.CurrentNodeExternalId,
            row.Status,
            row.BusinessKey,
            row.BusinessKeyUniqueness,
            row.StartedBy,
            row.CreatedAt,
            row.UpdatedAt,
            row.UserTasks is null ? null : RuntimeProjectionMapper.ToUserTaskWorkSummary(row.UserTasks),
            row.Variables,
            RuntimeProjectionMapper.ToFault(row.Status, row.FaultCode, row.FaultDescription, row.CurrentNodeName))
        {
            ExecutionPositions = (row.ExecutionPositions ?? [])
                .Select(position => new ExecutionPositionDto(
                    position.TokenId,
                    position.NodeId,
                    position.NodeName,
                    position.NodeExternalId,
                    position.NodeType,
                    position.Status,
                    position.ArrivedViaFlowId,
                    position.TerminationReason,
                    position.UserTaskId,
                    position.MultiInstanceExecutionId,
                    position.ActivationId,
                    position.WaitState,
                    position.WaitingJobId,
                    position.WaitingTimerSubscriptionId))
                .ToArray(),
            Completion = row.Completion is null
                ? null
                : new CompletionInfoDto(
                    row.Completion.Kind,
                    row.Completion.TokenId,
                    row.Completion.NodeId,
                    row.Completion.NodeName,
                    row.Completion.NodeExternalId,
                    row.Completion.CompletedAt)
        };
}
