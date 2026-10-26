using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Ui.Components.Shared;

/// <summary>
/// Shared wording for execution positions in the position list and the
/// instance diagram. Formatting only: no state or page logic.
/// </summary>
internal static class ExecutionPositionText
{
    internal static string NodeTypeLabel(string type) => type switch
    {
        BpmnFlowNodeTypes.StartEvent => "Start event",
        BpmnFlowNodeTypes.TimerStartEvent => "Timer start",
        BpmnFlowNodeTypes.MessageStartEvent => "Message start",
        BpmnFlowNodeTypes.UserTask => "User task",
        BpmnFlowNodeTypes.Task => "Automatic task",
        BpmnFlowNodeTypes.ServiceTask => "Service task",
        BpmnFlowNodeTypes.ScriptTask => "Script task",
        BpmnFlowNodeTypes.IntermediateMessageCatchEvent => "Message wait",
        BpmnFlowNodeTypes.IntermediateTimerCatchEvent => "Timer wait",
        BpmnFlowNodeTypes.IntermediateConditionalCatchEvent => "Condition wait",
        BpmnFlowNodeTypes.ExclusiveGateway => "Exclusive gateway",
        BpmnFlowNodeTypes.ParallelGateway => "Parallel gateway",
        BpmnFlowNodeTypes.InclusiveGateway => "Inclusive gateway",
        BpmnFlowNodeTypes.ComplexGateway => "Complex gateway",
        BpmnFlowNodeTypes.ScopedInterruptEvent => "Scoped interrupt",
        BpmnFlowNodeTypes.ErrorBoundaryEvent => "Error boundary",
        BpmnFlowNodeTypes.TimerBoundaryEvent => "Timer boundary",
        BpmnFlowNodeTypes.ConditionalBoundaryEvent => "Conditional boundary",
        BpmnFlowNodeTypes.TerminateEndEvent => "Terminate end",
        BpmnFlowNodeTypes.ErrorEndEvent => "Error end",
        BpmnFlowNodeTypes.EndEvent => "End event",
        _ => type
    };

    internal static string TerminationReasonLabel(string reason) => reason switch
    {
        "gatewayScopeCancelled" => "Cancelled by a scoped interrupt",
        "gatewayJoinMerged" => "Merged at a gateway join",
        "terminateEnd" => "Cancelled by a terminate end event",
        "errorEnd" => "Cancelled when the instance faulted",
        "instanceCancelled" => "Cancelled with the instance",
        _ => reason
    };

    /// <summary>True while the token waits for a durable background job rather than for people or an event.</summary>
    internal static bool IsBackgroundWait(ExecutionPositionDto position) =>
        position.WaitState is "asyncBefore" or "asyncAfter" or "conditionalWake";

    /// <summary>What an active position is waiting for.</summary>
    internal static string DescribeWait(ExecutionPositionDto position)
    {
        if (IsBackgroundWait(position)) return "Queued for background processing";
        if (position.MultiInstanceExecutionId is not null) return "Waiting for multi-instance work";
        var type = position.NodeType;
        if (BpmnFlowNodeTypes.IsUserTask(type)) return "Waiting for user work";
        if (BpmnFlowNodeTypes.IsMessageCatch(type)) return "Waiting for a message";
        if (BpmnFlowNodeTypes.IsTimerCatch(type)) return "Waiting for a timer";
        if (BpmnFlowNodeTypes.IsConditionalCatch(type)) return "Waiting for a condition";
        if (BpmnFlowNodeTypes.IsGateway(type)) return "Waiting at the gateway";
        return "Active";
    }

    /// <summary>An active position's wait, or how a finished position ended.</summary>
    internal static string DescribePosition(ExecutionPositionDto position)
    {
        if (Is(position.TokenStatus, "active")) return DescribeWait(position);
        if (Is(position.TokenStatus, "faulted")) return "Faulted here";
        if (Is(position.TokenStatus, "completed"))
        {
            if (Is(position.TerminationReason, "terminateEnd")) return "Terminated here";
            // Tokens finished before termination reasons were recorded have none.
            if (position.TerminationReason is null || Is(position.TerminationReason, "normalEnd")) return "Ended here";
        }
        if (!string.IsNullOrWhiteSpace(position.TerminationReason))
            return TerminationReasonLabel(position.TerminationReason);
        if (Is(position.TokenStatus, "cancelled")) return "Cancelled here";
        return string.IsNullOrWhiteSpace(position.TokenStatus)
            ? "Unknown"
            : char.ToUpperInvariant(position.TokenStatus[0]) + position.TokenStatus[1..];
    }

    internal static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
