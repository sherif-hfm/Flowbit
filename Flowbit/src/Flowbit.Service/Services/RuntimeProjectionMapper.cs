using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

/// <summary>
/// Pure runtime projection helpers shared by engine responses and the focused
/// instance query service.
/// </summary>
internal static class RuntimeProjectionMapper
{
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
}
