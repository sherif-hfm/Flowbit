using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>One incoming call in an instance's ancestry, nearest first, read without locks.</summary>
public sealed record CallChainLinkRecord(
    long InvocationId,
    long ParentInstanceId,
    long? ChildInstanceId,
    long RootInstanceId,
    int ChildDepth,
    string State,
    bool Cycle,
    bool ParentExists,
    long? ParentRootInstanceId,
    int? ParentDepth);

/// <summary>
/// Decides whether a call descendant may transition. It follows the depth stored on the
/// instance, never the configured admission limit, so lowering that limit cannot shorten
/// an existing descendant's fence.
/// </summary>
public static class CallChainFence
{
    /// <summary>
    /// Returns the nearest cancelled invocation above the instance, or null when the chain
    /// is intact and none is cancelled. An inconsistent chain throws, because the fence
    /// cannot decide; only a valid chain with a cancelled call counts as fenced.
    /// </summary>
    public static long? FindCancelledInvocation(
        long instanceId,
        long rootInstanceId,
        int depth,
        IReadOnlyList<CallChainLinkRecord> links)
    {
        if (depth is < 1 or > CallActivityConstraints.MaxSupportedDepth)
        {
            throw new WorkflowCallChainException(instanceId, $"depth {depth} is outside the supported range.");
        }

        if (links.Count != depth)
        {
            throw new WorkflowCallChainException(
                instanceId,
                $"depth {depth} needs {depth} incoming calls, but {links.Count} were found.");
        }

        var expectedChild = instanceId;
        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];
            var expectedDepth = depth - index;
            if (link.Cycle)
            {
                throw new WorkflowCallChainException(instanceId, $"invocation #{link.InvocationId} repeats an ancestor.");
            }

            if (link.ChildInstanceId != expectedChild)
            {
                throw new WorkflowCallChainException(
                    instanceId,
                    $"invocation #{link.InvocationId} does not link child #{expectedChild}.");
            }

            if (link.ChildDepth != expectedDepth)
            {
                throw new WorkflowCallChainException(
                    instanceId,
                    $"invocation #{link.InvocationId} has depth {link.ChildDepth}, expected {expectedDepth}.");
            }

            if (link.RootInstanceId != rootInstanceId)
            {
                throw new WorkflowCallChainException(
                    instanceId,
                    $"invocation #{link.InvocationId} names root #{link.RootInstanceId}, expected #{rootInstanceId}.");
            }

            var parentIsRoot = expectedDepth == 1;
            if (!link.ParentExists
                || link.ParentDepth != expectedDepth - 1
                || link.ParentRootInstanceId != (parentIsRoot ? null : rootInstanceId)
                || (parentIsRoot && link.ParentInstanceId != rootInstanceId))
            {
                throw new WorkflowCallChainException(
                    instanceId,
                    $"invocation #{link.InvocationId} parent #{link.ParentInstanceId} does not match the recorded root and depth.");
            }

            expectedChild = link.ParentInstanceId;
        }

        return links.FirstOrDefault(link => link.State == CallInvocationStates.Cancelled)?.InvocationId;
    }
}
