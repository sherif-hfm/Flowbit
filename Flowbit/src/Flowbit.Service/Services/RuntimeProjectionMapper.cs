using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Pure runtime projection helpers shared by engine responses, the focused
/// instance query service, and the instance projection service.
/// </summary>
internal static class RuntimeProjectionMapper
{
    internal const string RedactedSecret = "[redacted]";

    internal static FaultInfoDto? ToFault(
        string status,
        string? code,
        string? description,
        string nodeName) =>
        status == WorkflowInstanceStatuses.Faulted
            ? new FaultInfoDto(code, string.IsNullOrWhiteSpace(description) ? nodeName : description)
            : null;

    internal static UserTaskWorkSummaryDto ToUserTaskWorkSummary(
        UserTaskWorkSummaryRecord summary) =>
        new(
            summary.IsMultiInstance,
            summary.ActiveCount,
            summary.PendingCount,
            summary.ClaimedCount,
            summary.AssignedCount,
            summary.SoleClaimedBy,
            summary.SoleAssignee)
        {
            NormalTaskCount = summary.NormalTaskCount,
            MultiInstanceTaskCount = summary.MultiInstanceTaskCount,
            SoleUserTaskId = summary.SoleUserTaskId
        };

    internal static MultiInstanceProgressDto ToProgress(MultiInstanceProgressRecord record)
    {
        var execution = record.Execution;
        return new MultiInstanceProgressDto(
            execution.Id,
            execution.Mode,
            execution.Status,
            execution.TotalCount,
            execution.CompletedCount,
            record.ActiveCount,
            record.PendingCount,
            record.CancelledCount,
            execution.WinningFlowId,
            execution.CompletionReason,
            record.FlowCounts.OrderBy(pair => pair.Key)
                .Select(pair => new MultiInstanceFlowCountDto(
                    pair.Key,
                    pair.Value,
                    execution.TotalCount == 0 ? 0d : pair.Value * 100d / execution.TotalCount))
                .ToList());
    }

    /// <summary>
    /// Clones the immutable cached definition and redacts message
    /// client secrets/header values and the task-distribution client secret
    /// before the definition is embedded in a response DTO.
    /// </summary>
    internal static WorkflowDetailDto ToRuntimeWorkflowDetail(WorkflowDefinitionRecord workflow)
    {
        var definition = JsonSerializer.Deserialize<WorkflowModel>(
            JsonSerializer.Serialize(workflow.Definition))
            ?? throw new InvalidOperationException("Unable to clone the workflow definition.");
        foreach (var node in definition.FlowNodes)
        {
            if (node.Message is null)
            {
                continue;
            }

            node.Message.ClientSecret = RedactedSecret;
            node.Message.HeaderValue = RedactedSecret;
        }
        if (definition.TaskDistribution is not null)
        {
            definition.TaskDistribution.ClientSecret = RedactedSecret;
        }

        return new WorkflowDetailDto(
            workflow.Id,
            workflow.Name,
            workflow.WorkflowKey,
            workflow.Version,
            workflow.IsPublished,
            workflow.IsDefault,
            workflow.CreatedAt,
            definition);
    }

    internal static InstanceVersionChangePreviewDto ToVersionChangePreview(
        WorkflowInstanceRecord instance,
        WorkflowDefinitionRecord source,
        WorkflowDefinitionRecord target,
        WorkflowVersionCompatibilityResult result) =>
        new(
            instance.Id,
            ToVersionSummary(source),
            ToVersionSummary(target),
            VersionChangeDirection(source, target),
            result.IsCompatible,
            result.Blockers.Select(ToVersionChangeIssue).ToList(),
            result.Warnings.Select(ToVersionChangeIssue).ToList(),
            instance.WorkflowDefinitionId,
            instance.UpdatedAt);

    internal static InstanceVersionChangeIssueDto ToVersionChangeIssue(
        WorkflowVersionCompatibilityIssue issue) =>
        new(
            Code: issue.Code,
            Message: issue.Message,
            StateType: VersionChangeStateType(issue),
            StateId: issue.RuntimeId,
            NodeId: issue.NodeId,
            FlowId: issue.FlowId,
            VariableName: issue.VariableName);

    internal static string? VersionChangeStateType(
        WorkflowVersionCompatibilityIssue issue)
    {
        if (issue.RuntimeId is null)
        {
            return null;
        }

        return issue.Code switch
        {
            WorkflowVersionCompatibilityCodes.InstanceNotRunning
                or WorkflowVersionCompatibilityCodes.SourceDefinitionMismatch => "instance",
            WorkflowVersionCompatibilityCodes.MultiInstanceContractChanged =>
                "multiInstanceExecution",
            WorkflowVersionCompatibilityCodes.OpenJobNodeMissing
                or WorkflowVersionCompatibilityCodes.OpenJobContractChanged => "job",
            WorkflowVersionCompatibilityCodes.OpenTimerNodeMissing
                or WorkflowVersionCompatibilityCodes.OpenTimerContractChanged => "timerSubscription",
            _ => "runtimeState"
        };
    }

    internal static InstanceVersionChangeAuditDto ToVersionChangeAudit(
        WorkflowInstanceVersionChangeRecord record,
        WorkflowDefinitionRecord source,
        WorkflowDefinitionRecord target) =>
        new(
            record.Id,
            record.InstanceId,
            ToVersionSummary(source),
            ToVersionSummary(target),
            VersionChangeDirection(source, target),
            record.ChangedBy,
            record.ChangedByRoles,
            record.Reason,
            record.ChangedAt,
            record.BatchId,
            record.BatchItemId);

    internal static WorkflowSummaryDto ToVersionSummary(
        WorkflowDefinitionRecord workflow) =>
        new(
            workflow.Id,
            workflow.Name,
            workflow.WorkflowKey,
            workflow.Version,
            workflow.IsPublished,
            workflow.IsDefault,
            workflow.CreatedAt);

    internal static string VersionChangeDirection(
        WorkflowDefinitionRecord source,
        WorkflowDefinitionRecord target) =>
        target.Version > source.Version
            ? InstanceVersionChangeDirections.Upgrade
            : InstanceVersionChangeDirections.Downgrade;
}
