using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Ui.Components.Shared.InstanceDetails;

/// <summary>
/// How the call panel words an invocation. Formatting only: it reads the supplied DTO.
/// </summary>
internal static class CallActivityFormatting
{
    /// <summary>The short state shown on the badge.</summary>
    internal static string StateLabel(string state, bool capacityReserved) => state switch
    {
        "queued" when !capacityReserved => "Blocked",
        "queued" => "Queued",
        "running" => "Running",
        "resultReady" => "Result ready",
        "completed" => "Completed",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        _ => state
    };

    /// <summary>The status that gives the badge its colour.</summary>
    internal static string BadgeStatus(string state, bool capacityReserved) =>
        state == "queued" && !capacityReserved ? "warning" : state;

    /// <summary>What the state means for this visit and what, if anything, a reader can do.</summary>
    internal static string Explain(CallActivityExecutionDto call)
    {
        var blocked = call.OpenIncidentCount > 0;
        switch (call.State)
        {
            case "queued" when !call.CapacityReserved:
                return blocked
                    ? "Blocked before admission. It holds no capacity and has no child. Fix the cause, then retry its incident."
                    : "Blocked before admission. It holds no capacity and has no child yet.";
            case "queued":
                return blocked
                    ? "Admitted, but the child could not be started. Fix the cause, then retry its incident."
                    : "Admitted and waiting for a Worker to start the child.";
            case "running":
                return blocked
                    ? $"The child is blocked by {IncidentCount(call.OpenIncidentCount)}. This is not a fault: the call keeps waiting until it is resolved."
                    : "The child is running.";
            case "resultReady":
                return blocked
                    ? "The child has finished, but its result could not be delivered. Fix the cause, then retry the incident. The child does not run again."
                    : "The child has finished and a Worker is delivering its result.";
            case "completed":
                return "The child completed and its results were delivered.";
            case "failed":
                return call.FailureCode switch
                {
                    CallFailureCodes.ChildFaulted => string.IsNullOrWhiteSpace(call.ChildFaultCode)
                        ? "The child faulted."
                        : $"The child faulted with code {call.ChildFaultCode}.",
                    CallFailureCodes.ChildCancelled => "The child was cancelled on its own.",
                    _ => "The call failed, and this workflow took its error path."
                };
            case "cancelled":
                if (call.ChildInstanceId is null) return "Cancelled before a child was created.";
                if (Is(call.ChildStatus, "running"))
                {
                    return "Cancelled. A background job is still stopping the child, which accepts no new actions.";
                }

                return string.IsNullOrWhiteSpace(call.ChildStatus)
                    ? "Cancelled."
                    : $"Cancelled. The child ended as {call.ChildStatus}.";
            default:
                return string.Empty;
        }
    }

    /// <summary>The child's version, once a visit has chosen one.</summary>
    internal static string VersionText(CallActivityExecutionDto call) =>
        call.ChildDefinitionVersion is int version
            ? $"Version {version}"
            : call.State == "queued"
                ? "Version chosen when the child starts"
                : "No version chosen";

    /// <summary>The message for a child whose parent's call has been cancelled, or null.</summary>
    internal static string? ParentCancellationNotice(ParentCallSummaryDto? parentCall, string? instanceStatus) =>
        parentCall is { State: "cancelled" } && Is(instanceStatus, "running")
            ? "The parent's call was cancelled, so this instance is being cancelled. It accepts no new actions, and a background job will finish stopping it."
            : null;

    private static string IncidentCount(long count) =>
        count == 1 ? "1 open incident" : $"{count:N0} open incidents";

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
