using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class WorkflowReactivationRepositoryTests(PostgresApiFixture fixture)
{
    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("faulted")]
    public async Task FinishClockSurvivesTerminalTouchesAndRestartsAfterReactivation(string status)
    {
        var terminal = await CreateTerminalVisitedInstanceAsync();
        await using var db = fixture.CreateDbContext();
        var repository = new WorkflowRuntimeRepository(db);
        var instance = await db.WorkflowInstances.SingleAsync(row => row.Id == terminal.InstanceId);
        var oldFinish = DateTimeOffset.UtcNow.AddDays(-30);
        instance.Status = status;
        instance.FinishedAt = oldFinish;
        await db.SaveChangesAsync();
        await repository.SetInstanceStatusAsync(instance.Id, status, CancellationToken.None);
        Assert.Equal(oldFinish, instance.FinishedAt);
        await repository.SetInstanceStatusAsync(instance.Id, "running", CancellationToken.None);
        Assert.Null(instance.FinishedAt);
        await repository.SetInstanceStatusAsync(instance.Id, status, CancellationToken.None);
        Assert.True(instance.FinishedAt > oldFinish);
        await db.SaveChangesAsync();
        instance.HistoryPrunedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            repository.SetInstanceStatusAsync(instance.Id, "running", CancellationToken.None));
        Assert.Equal(status, instance.Status);
        Assert.NotNull(instance.FinishedAt);
    }

    [Fact]
    public async Task TargetVisitsReturnLatestEligibleTopLevelVisitPerUserTaskNode()
    {
        var terminal = await CreateTerminalVisitedInstanceAsync();

        await using var context = fixture.CreateDbContext();
        var repository = new WorkflowRuntimeRepository(context);
        var visits = await repository.ListReactivationTargetVisitsAsync(
            terminal.InstanceId,
            terminal.WorkflowDefinitionId,
            CancellationToken.None);

        Assert.Equal(2, visits.Count);
        var latestReview = visits.Single(visit => visit.NodeId == 10);
        Assert.Equal(terminal.LatestReviewTaskId, latestReview.UserTaskId);
        Assert.Equal(NodeExecutionStatuses.Cancelled, latestReview.Status);
        Assert.Equal(NodeExecutionKinds.Node, latestReview.ExecutionKind);
        Assert.Null(latestReview.MultiInstanceExecutionId);
        Assert.Null(latestReview.EntryGatewayBranchId);
        Assert.NotNull(latestReview.CompletedAt);

        var approval = visits.Single(visit => visit.NodeId == 20);
        Assert.Equal(terminal.ApprovalTaskId, approval.UserTaskId);
        Assert.Equal(NodeExecutionStatuses.Completed, approval.Status);
        Assert.True(
            (latestReview.CompletedAt ?? latestReview.UpdatedAt)
            >= (approval.CompletedAt ?? approval.UpdatedAt));
    }

    [Fact]
    public async Task RuntimeStateReportsCleanTerminalStateAndRetainedComplexMarkers()
    {
        var terminal = await CreateTerminalVisitedInstanceAsync();

        await using (var cleanContext = fixture.CreateDbContext())
        {
            var clean = await new WorkflowRuntimeRepository(cleanContext)
                .GetReactivationRuntimeStateAsync(
                    terminal.InstanceId,
                    CancellationToken.None);

            Assert.True(clean.IsClean);
            Assert.Equal(0, clean.ActiveExecutionTokenCount);
            Assert.Equal(0, clean.OpenUserTaskCount);
            Assert.Equal(0, clean.OpenNodeExecutionCount);
            Assert.Equal(0, clean.NonTerminalWorkflowJobCount);
            Assert.Empty(clean.NonResetComplexGatewayStateIds);
            Assert.Empty(clean.RetainedComplexLineageTokenIds);
        }

        long complexStateId;
        await using (var dirtyContext = fixture.CreateDbContext())
        {
            var token = await dirtyContext.ExecutionTokens
                .SingleAsync(candidate => candidate.Id == terminal.TokenId);
            token.AutomaticActivationStateIds = [987654321];
            var now = DateTimeOffset.UtcNow;
            dirtyContext.NodeExecutions.Add(new NodeExecutionEntity
            {
                InstanceId = terminal.InstanceId,
                WorkflowDefinitionId = terminal.WorkflowDefinitionId,
                ExecutionTokenId = terminal.TokenId,
                NodeId = 91,
                NodeName = "Orphan open execution",
                NodeType = BpmnFlowNodeTypes.Task,
                ExecutionKind = NodeExecutionKinds.Node,
                Status = NodeExecutionStatuses.Active,
                CreatedAt = now,
                StartedAt = now,
                UpdatedAt = now
            });
            var state = new ComplexGatewayStateEntity
            {
                InstanceId = terminal.InstanceId,
                GatewayNodeId = 90,
                Phase = ComplexGatewayStatePhases.WaitingForReset,
                Cycle = 2,
                ContributingFlowIds = [901],
                RemainingFlowIds = [902]
            };
            dirtyContext.ComplexGatewayStates.Add(state);
            await dirtyContext.SaveChangesAsync();
            complexStateId = state.Id;
        }

        await using var verify = fixture.CreateDbContext();
        var dirty = await new WorkflowRuntimeRepository(verify)
            .GetReactivationRuntimeStateAsync(
                terminal.InstanceId,
                CancellationToken.None);
        Assert.False(dirty.IsClean);
        Assert.Equal(1, dirty.OpenNodeExecutionCount);
        Assert.Equal([complexStateId], dirty.NonResetComplexGatewayStateIds);
        Assert.Equal([terminal.TokenId], dirty.RetainedComplexLineageTokenIds);
    }

    [Fact]
    public async Task RuntimeStateCountsUnresolvedPerInstanceBatchItems()
    {
        var terminal = await CreateTerminalVisitedInstanceAsync();
        var targetWorkflow = await CreateWorkflowDefinitionAsync(
            "reactivation-batch-target");
        await using (var setup = fixture.CreateDbContext())
        {
            var workflow = await setup.WorkflowDefinitions.SingleAsync(candidate =>
                candidate.Id == terminal.WorkflowDefinitionId);
            var token = await setup.ExecutionTokens.SingleAsync(candidate =>
                candidate.Id == terminal.TokenId);
            var task = await setup.UserTasks.SingleAsync(candidate =>
                candidate.Id == terminal.LatestReviewTaskId);
            var now = DateTimeOffset.UtcNow;
            var administrativeBatch = new AdministrativeActionBatchEntity
            {
                WorkflowKey = workflow.WorkflowKey,
                WorkflowDefinitionId = workflow.Id,
                SourceNodeId = task.NodeId,
                ActionKind = AdministrativeActionKinds.DirectFlow,
                FlowId = 1,
                ActionSnapshotJson = JsonDocument.Parse("{}"),
                CommonVariablesJson = JsonDocument.Parse("{}"),
                SelectionJson = JsonDocument.Parse("{}"),
                Status = AdministrativeActionBatchStatuses.Preparing,
                PreparedBy = "reactivation-test",
                PreparedByRolesJson = JsonDocument.Parse("[]"),
                TotalItemCount = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            var variableBatch = new InstanceVariableUpdateBatchEntity
            {
                WorkflowKey = workflow.WorkflowKey,
                VariablesJson = JsonDocument.Parse("[{}]"),
                SelectionJson = JsonDocument.Parse("{}"),
                Status = InstanceVariableUpdateBatchStatuses.Ready,
                PreparedBy = "reactivation-test",
                PreparedByRolesJson = JsonDocument.Parse("[]"),
                TotalItemCount = 1,
                EligibleItemCount = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            var versionBatch = new WorkflowInstanceVersionChangeBatchEntity
            {
                WorkflowKey = workflow.WorkflowKey,
                SourceWorkflowDefinitionId = workflow.Id,
                TargetWorkflowDefinitionId = targetWorkflow.Id,
                Reason = "Verify unresolved reactivation batch work.",
                SelectionJson = JsonDocument.Parse("{}"),
                Status = InstanceVersionChangeBatchStatuses.Queued,
                PreparedBy = "reactivation-test",
                PreparedByRolesJson = JsonDocument.Parse("[]"),
                TotalItemCount = 1,
                QueuedItemCount = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            setup.AddRange(administrativeBatch, variableBatch, versionBatch);
            await setup.SaveChangesAsync();

            setup.AdministrativeActionBatchItems.Add(
                new AdministrativeActionBatchItemEntity
                {
                    BatchId = administrativeBatch.Id,
                    InstanceId = terminal.InstanceId,
                    PositionKind = AdministrativeActionPositionKinds.UserTask,
                    UserTaskId = task.Id,
                    TokenId = token.Id,
                    TokenActivationId = token.ActivationId,
                    WorkflowDefinitionId = workflow.Id,
                    FlowId = administrativeBatch.FlowId,
                    SourceNodeId = task.NodeId,
                    CapturedPositionUpdatedAt = task.UpdatedAt,
                    AffectedTaskCount = 1,
                    Status = AdministrativeActionBatchItemStatuses.Preparing,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            setup.InstanceVariableUpdateBatchItems.Add(
                new InstanceVariableUpdateBatchItemEntity
                {
                    BatchId = variableBatch.Id,
                    InstanceId = terminal.InstanceId,
                    CapturedWorkflowDefinitionId = workflow.Id,
                    CapturedInstanceUpdatedAt = now,
                    Status = InstanceVariableUpdateBatchItemStatuses.Eligible,
                    CreatedAt = now,
                    UpdatedAt = now,
                    PreparedAt = now
                });
            setup.WorkflowInstanceVersionChangeBatchItems.Add(
                new WorkflowInstanceVersionChangeBatchItemEntity
                {
                    BatchId = versionBatch.Id,
                    InstanceId = terminal.InstanceId,
                    CapturedSourceWorkflowDefinitionId = workflow.Id,
                    CapturedInstanceUpdatedAt = now,
                    Status = InstanceVersionChangeBatchItemStatuses.Queued,
                    CreatedAt = now,
                    UpdatedAt = now,
                    PreparedAt = now
                });
            await setup.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var state = await new WorkflowRuntimeRepository(verify)
            .GetReactivationRuntimeStateAsync(
                terminal.InstanceId,
                CancellationToken.None);
        Assert.False(state.IsClean);
        Assert.Equal(3, state.NonTerminalWorkflowJobCount);
    }

    [Fact]
    public async Task ReacquireBusinessKeyRestoresUnownedClaimAndRejectsAnotherActiveOwner()
    {
        var workflow = await CreateWorkflowDefinitionAsync("reactivation-business-key");
        var businessKey = $"order-{Guid.NewGuid():N}";
        var firstInstanceId = await CreateBusinessKeyInstanceAsync(
            workflow,
            businessKey,
            complete: true);

        await using (var assessmentContext = fixture.CreateDbContext())
        {
            var assessment = await new WorkflowRuntimeRepository(assessmentContext)
                .AssessBusinessKeyReacquisitionAsync(
                    firstInstanceId,
                    CancellationToken.None);
            Assert.True(assessment.Acquired);
            Assert.Null(assessment.ConflictingInstanceId);
            Assert.False(assessment.ClaimMissing);
        }

        await using (var reacquireContext = fixture.CreateDbContext())
        await using (var transaction = await reacquireContext.Database.BeginTransactionAsync())
        {
            var result = await new WorkflowRuntimeRepository(reacquireContext)
                .ReacquireBusinessKeyAsync(firstInstanceId, CancellationToken.None);

            Assert.True(result.Acquired);
            Assert.Null(result.ConflictingInstanceId);
            await reacquireContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using (var releaseContext = fixture.CreateDbContext())
        await using (var transaction = await releaseContext.Database.BeginTransactionAsync())
        {
            var repository = new WorkflowRuntimeRepository(releaseContext);
            _ = await repository.GetInstanceForUpdateAsync(
                firstInstanceId,
                lockActiveUserTask: false,
                CancellationToken.None);
            await repository.SetInstanceStatusAsync(
                firstInstanceId,
                WorkflowInstanceStatuses.Completed,
                CancellationToken.None);
            await releaseContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var secondInstanceId = await CreateBusinessKeyInstanceAsync(
            workflow,
            businessKey,
            complete: false);

        await using (var assessmentContext = fixture.CreateDbContext())
        {
            var assessment = await new WorkflowRuntimeRepository(assessmentContext)
                .AssessBusinessKeyReacquisitionAsync(
                    firstInstanceId,
                    CancellationToken.None);
            Assert.False(assessment.Acquired);
            Assert.Equal(secondInstanceId, assessment.ConflictingInstanceId);
            Assert.False(assessment.ClaimMissing);
        }

        await using (var conflictContext = fixture.CreateDbContext())
        await using (var transaction = await conflictContext.Database.BeginTransactionAsync())
        {
            var result = await new WorkflowRuntimeRepository(conflictContext)
                .ReacquireBusinessKeyAsync(firstInstanceId, CancellationToken.None);

            Assert.False(result.Acquired);
            Assert.Equal(secondInstanceId, result.ConflictingInstanceId);
            await transaction.RollbackAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var claim = await verify.WorkflowBusinessKeyClaims.AsNoTracking()
            .SingleAsync(candidate =>
                candidate.WorkflowKey == workflow.WorkflowKey
                && candidate.BusinessKey == businessKey);
        Assert.Equal(secondInstanceId, claim.ActiveInstanceId);
        Assert.Equal(secondInstanceId, claim.LastInstanceId);
    }

    [Fact]
    public async Task CompletedRepresentativeUsesLatestCycleBeforeTerminateTieBreaker()
    {
        var workflow = await CreateWorkflowDefinitionAsync("reactivation-terminal-token");
        long instanceId;
        long latestNormalTokenId;
        await using (var setup = fixture.CreateDbContext())
        {
            var instance = new WorkflowInstanceEntity
            {
                WorkflowDefinitionId = workflow.Id,
                WorkflowKey = workflow.WorkflowKey,
                Status = WorkflowInstanceStatuses.Completed
            };
            setup.WorkflowInstances.Add(instance);
            await setup.SaveChangesAsync();
            instanceId = instance.Id;

            var terminateToken = new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                NodeId = 90,
                NodeName = "Terminate",
                NodeType = BpmnFlowNodeTypes.TerminateEndEvent,
                Status = ExecutionTokenStatuses.Completed,
                TerminationReason = ExecutionTokenTerminationReasons.TerminateEnd,
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            var laterCancelledSibling = new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                NodeId = 80,
                NodeName = "Cancelled sibling",
                NodeType = BpmnFlowNodeTypes.UserTask,
                Status = ExecutionTokenStatuses.Cancelled,
                TerminationReason = ExecutionTokenTerminationReasons.InstanceCancelled,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var latestNormalCompletion = new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                NodeId = 100,
                NodeName = "Later normal completion",
                NodeType = BpmnFlowNodeTypes.EndEvent,
                Status = ExecutionTokenStatuses.Completed,
                TerminationReason = ExecutionTokenTerminationReasons.NormalEnd,
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1)
            };
            setup.ExecutionTokens.AddRange(
                terminateToken,
                laterCancelledSibling,
                latestNormalCompletion);
            await setup.SaveChangesAsync();
            latestNormalTokenId = latestNormalCompletion.Id;
        }

        await using (var readContext = fixture.CreateDbContext())
        {
            var read = await new WorkflowRuntimeRepository(readContext)
                .GetInstanceAsync(instanceId, CancellationToken.None);

            Assert.NotNull(read);
            Assert.Equal(latestNormalTokenId, read.ActiveTokenId);
        }

        await using var context = fixture.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var locked = await new WorkflowRuntimeRepository(context)
            .GetInstanceForUpdateAsync(
                instanceId,
                lockActiveUserTask: false,
                CancellationToken.None);

        Assert.NotNull(locked);
        Assert.Equal(latestNormalTokenId, locked.ActiveTokenId);
        await transaction.RollbackAsync();
    }

    private async Task<TerminalInstance> CreateTerminalVisitedInstanceAsync()
    {
        var workflow = await CreateWorkflowDefinitionAsync("reactivation-visits");
        await using var context = fixture.CreateDbContext();
        var repository = new WorkflowRuntimeRepository(context);
        var instance = await repository.AddInstanceAsync(
            workflow.Id,
            workflow.WorkflowKey,
            null,
            null,
            null,
            Snapshot(1, "Start", BpmnFlowNodeTypes.StartEvent),
            "starter",
            ["user"],
            CancellationToken.None);

        await repository.UpdateExecutionTokenAsync(
            instance.ActiveTokenId,
            Snapshot(10, "Review", BpmnFlowNodeTypes.UserTask),
            ExecutionTokenStatuses.Active,
            null,
            101,
            null,
            null,
            new NodeExecutionActorRecord("starter", ["user"]),
            null,
            CancellationToken.None);
        var firstReview = await repository.GetActiveUserTaskAsync(
            instance.Id,
            forUpdate: false,
            CancellationToken.None)
            ?? throw new InvalidOperationException("The first review task was not created.");
        await repository.CompleteUserTaskAsync(
            firstReview.Id,
            102,
            "reviewer",
            ["reviewer"],
            [],
            CancellationToken.None);

        await repository.UpdateExecutionTokenAsync(
            instance.ActiveTokenId,
            Snapshot(20, "Approval", BpmnFlowNodeTypes.UserTask),
            ExecutionTokenStatuses.Active,
            null,
            102,
            null,
            null,
            new NodeExecutionActorRecord("reviewer", ["reviewer"]),
            null,
            CancellationToken.None);
        var approval = await repository.GetActiveUserTaskAsync(
            instance.Id,
            forUpdate: false,
            CancellationToken.None)
            ?? throw new InvalidOperationException("The approval task was not created.");
        await repository.CompleteUserTaskAsync(
            approval.Id,
            103,
            "approver",
            ["approver"],
            [],
            CancellationToken.None);

        await repository.UpdateExecutionTokenAsync(
            instance.ActiveTokenId,
            Snapshot(10, "Review", BpmnFlowNodeTypes.UserTask),
            ExecutionTokenStatuses.Active,
            null,
            103,
            null,
            null,
            new NodeExecutionActorRecord("approver", ["approver"]),
            null,
            CancellationToken.None);
        var latestReview = await repository.GetActiveUserTaskAsync(
            instance.Id,
            forUpdate: false,
            CancellationToken.None)
            ?? throw new InvalidOperationException("The repeated review task was not created.");

        await repository.SetExecutionTokenStatusAsync(
            instance.ActiveTokenId,
            ExecutionTokenStatuses.Cancelled,
            ExecutionTokenTerminationReasons.InstanceCancelled,
            new NodeExecutionCompletionRecord(
                NodeExecutionRecordStatuses.Cancelled,
                NodeExecutionCompletionReasons.InstanceCancelled,
                null,
                null,
                null,
                new NodeExecutionActorRecord("administrator", ["admin"])),
            CancellationToken.None);
        await repository.SetInstanceStatusAsync(
            instance.Id,
            WorkflowInstanceStatuses.Cancelled,
            CancellationToken.None);
        await context.SaveChangesAsync();

        return new TerminalInstance(
            instance.Id,
            workflow.Id,
            instance.ActiveTokenId,
            approval.Id,
            latestReview.Id);
    }

    private async Task<WorkflowDefinitionEntity> CreateWorkflowDefinitionAsync(
        string prefix)
    {
        var workflowKey = $"{prefix}-{Guid.NewGuid():N}";
        await using var context = fixture.CreateDbContext();
        var definition = new WorkflowDefinitionEntity
        {
            Name = workflowKey,
            WorkflowKey = workflowKey,
            Version = 1,
            IsPublished = true,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = workflowKey
            }
        };
        context.WorkflowDefinitions.Add(definition);
        await context.SaveChangesAsync();
        return definition;
    }

    private async Task<long> CreateBusinessKeyInstanceAsync(
        WorkflowDefinitionEntity workflow,
        string businessKey,
        bool complete)
    {
        await using var context = fixture.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var repository = new WorkflowRuntimeRepository(context);
        var reservation = await repository.ReserveBusinessKeyAsync(
            workflow.WorkflowKey,
            businessKey,
            BusinessKeyUniqueness.Active,
            CancellationToken.None);
        Assert.True(reservation.Reserved);
        var instance = await repository.AddInstanceAsync(
            workflow.Id,
            workflow.WorkflowKey,
            null,
            businessKey,
            BusinessKeyUniqueness.Active,
            Snapshot(1, "Start", BpmnFlowNodeTypes.StartEvent),
            "starter",
            ["user"],
            CancellationToken.None);
        await repository.BindBusinessKeyAsync(
            workflow.WorkflowKey,
            businessKey,
            instance.Id,
            CancellationToken.None);

        if (complete)
        {
            await repository.SetExecutionTokenStatusAsync(
                instance.ActiveTokenId,
                ExecutionTokenStatuses.Completed,
                ExecutionTokenTerminationReasons.NormalEnd,
                new NodeExecutionCompletionRecord(
                    NodeExecutionRecordStatuses.Completed,
                    NodeExecutionCompletionReasons.NormalEnd,
                    null,
                    null,
                    null,
                    new NodeExecutionActorRecord("system", [])),
                CancellationToken.None);
            await repository.SetInstanceStatusAsync(
                instance.Id,
                WorkflowInstanceStatuses.Completed,
                CancellationToken.None);
        }

        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return instance.Id;
    }

    private static CurrentNodeSnapshot Snapshot(int id, string name, string type) =>
        new(id, name, null, type, [], false, false, null);

    private sealed record TerminalInstance(
        long InstanceId,
        long WorkflowDefinitionId,
        long TokenId,
        long ApprovalTaskId,
        long LatestReviewTaskId);
}
