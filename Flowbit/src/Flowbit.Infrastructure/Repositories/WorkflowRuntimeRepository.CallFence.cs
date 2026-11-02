using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Flowbit.Infrastructure.Repositories;

public sealed partial class WorkflowRuntimeRepository
{
    public async Task<InstanceCallIdentityRecord?> GetCallIdentityAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var tracked = dbContext.WorkflowInstances.Local.SingleOrDefault(entity => entity.Id == instanceId);
        if (tracked is not null)
        {
            return new InstanceCallIdentityRecord(tracked.CallRootInstanceId, tracked.CallDepth);
        }

        var row = await dbContext.WorkflowInstances.AsNoTracking()
            .Where(entity => entity.Id == instanceId)
            .Select(entity => new { entity.CallRootInstanceId, entity.CallDepth })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new InstanceCallIdentityRecord(row.CallRootInstanceId, row.CallDepth);
    }

    public async Task<bool> FaultExecutionTokenAtNodeAsync(
        long tokenId,
        Guid activationId,
        string faultCode,
        string? faultDescription,
        string terminationReason,
        string completionReason,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken)
    {
        var token = dbContext.ExecutionTokens.Local.SingleOrDefault(entity => entity.Id == tokenId)
            ?? await dbContext.ExecutionTokens.SingleOrDefaultAsync(entity => entity.Id == tokenId, cancellationToken);
        if (token is null
            || token.Status != ExecutionTokenStatuses.Active
            || token.ActivationId != activationId)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        await CompleteCurrentNodeExecutionAsync(
            token,
            new NodeExecutionCompletionRecord(
                NodeExecutionRecordStatuses.Faulted,
                completionReason,
                null,
                null,
                token.GatewayBranchId,
                actor,
                faultCode,
                faultDescription),
            now,
            cancellationToken);
        token.Status = ExecutionTokenStatuses.Faulted;
        token.FaultCode = faultCode;
        token.FaultDescription = faultDescription;
        token.TerminationReason = terminationReason;
        token.ComplexGatewayStateId = null;
        token.ComplexGatewayCycle = null;
        token.WaitState = null;
        token.WaitingJobId = null;
        token.WaitingTimerSubscriptionId = null;
        token.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task EnsureCallFenceAsync(
        WorkflowInstanceEntity instance,
        CancellationToken cancellationToken)
    {
        if (await FindCancelledCallAboveAsync(
                instance.Id,
                instance.CallRootInstanceId!.Value,
                instance.CallDepth,
                cancellationToken) is long invocationId)
        {
            throw new WorkflowCallFenceException(instance.Id, invocationId);
        }
    }

    // Reads the complete incoming chain in one statement and takes no row locks. The bound
    // is the fixed supported depth plus one, so a chain longer than any legal depth fails
    // the count check rather than being truncated into something valid.
    public async Task<long?> FindCancelledCallAboveAsync(
        long instanceId,
        long rootInstanceId,
        int depth,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database
            .SqlQuery<CallChainRow>($"""
                WITH RECURSIVE chain AS (
                    SELECT
                        call."Id" AS "InvocationId",
                        call."ParentInstanceId",
                        call."ChildInstanceId",
                        call."RootInstanceId",
                        call."ChildDepth",
                        call."State",
                        1 AS "Hop",
                        ARRAY[call."ChildInstanceId"] AS "Visited",
                        FALSE AS "Cycle"
                    FROM flowbit.call_activity_executions AS call
                    WHERE call."ChildInstanceId" = {instanceId}
                    UNION ALL
                    SELECT
                        parent."Id",
                        parent."ParentInstanceId",
                        parent."ChildInstanceId",
                        parent."RootInstanceId",
                        parent."ChildDepth",
                        parent."State",
                        chain."Hop" + 1,
                        chain."Visited" || parent."ChildInstanceId",
                        parent."ChildInstanceId" = ANY(chain."Visited")
                    FROM chain
                    JOIN flowbit.call_activity_executions AS parent
                        ON parent."ChildInstanceId" = chain."ParentInstanceId"
                    WHERE NOT chain."Cycle"
                      AND chain."Hop" <= {CallActivityConstraints.MaxSupportedDepth}
                )
                SELECT
                    chain."InvocationId",
                    chain."ParentInstanceId",
                    chain."ChildInstanceId",
                    chain."RootInstanceId",
                    chain."ChildDepth",
                    chain."State",
                    chain."Cycle",
                    chain."Hop",
                    owner."Id" IS NOT NULL AS "ParentExists",
                    owner."CallRootInstanceId" AS "ParentRootInstanceId",
                    owner."CallDepth" AS "ParentDepth"
                FROM chain
                LEFT JOIN flowbit.workflow_instances AS owner
                    ON owner."Id" = chain."ParentInstanceId"
                ORDER BY chain."Hop"
                """)
            .ToListAsync(cancellationToken);
        return CallChainFence.FindCancelledInvocation(
            instanceId,
            rootInstanceId,
            depth,
            rows.Select(row => new CallChainLinkRecord(
                    row.InvocationId,
                    row.ParentInstanceId,
                    row.ChildInstanceId,
                    row.RootInstanceId,
                    row.ChildDepth,
                    row.State,
                    row.Cycle,
                    row.ParentExists,
                    row.ParentRootInstanceId,
                    row.ParentDepth))
                .ToList());
    }

    private sealed class CallChainRow
    {
        public long InvocationId { get; set; }
        public long ParentInstanceId { get; set; }
        public long? ChildInstanceId { get; set; }
        public long RootInstanceId { get; set; }
        public int ChildDepth { get; set; }
        public string State { get; set; } = string.Empty;
        public bool Cycle { get; set; }
        public int Hop { get; set; }
        public bool ParentExists { get; set; }
        public long? ParentRootInstanceId { get; set; }
        public int? ParentDepth { get; set; }
    }
}
