using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceReactivationApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PrunedHistoryBlocksFreshAndPreviouslyPreviewedReactivation()
    {
        var workflow = await CreateWorkflowAsync(CreateSimpleWorkflow());
        var started = await StartAsync(workflow.Id);
        using (var cancellation = await SendAsync(HttpMethod.Post, $"/api/instances/{started.Id}/cancel"))
            Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);

        var before = await PreviewAsync(started.Id);
        Assert.True(before.CanReactivate);
        DateTimeOffset prunedAt;
        await using (var db = fixture.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var instance = await db.WorkflowInstances.SingleAsync(row => row.Id == started.Id);
            Assert.NotNull(instance.FinishedAt);
            await db.InstanceHistory.Where(row => row.InstanceId == started.Id).ExecuteDeleteAsync();
            instance.HistoryPrunedAt = instance.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            prunedAt = (await db.WorkflowInstances.AsNoTracking().SingleAsync(row => row.Id == started.Id))
                .HistoryPrunedAt!.Value;
        }

        var after = await PreviewAsync(started.Id);
        Assert.False(after.CanReactivate);
        Assert.Empty(after.Targets);
        Assert.Contains(after.Blockers, issue => issue.Code == "history_pruned");
        foreach (var preview in new[] { before, after })
        {
            using var response = await SendAsync(HttpMethod.Post, $"/api/instances/{started.Id}/reactivation",
                new ReactivateInstanceRequest(2, preview.WorkflowId, preview.ExpectedUpdatedAt, "Try to reopen"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("retention", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        using var detailResponse = await SendAsync(HttpMethod.Get, $"/api/instances/{started.Id}");
        var detail = await ReadAsync<InstanceDetailDto>(detailResponse);
        Assert.Equal(prunedAt, detail.HistoryPrunedAt);
        Assert.NotNull(detail.FinishedAt);
        Assert.Equal("cancelled", detail.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.UserTasks.AnyAsync(row => row.InstanceId == started.Id && row.Status == "active"));
    }

    [Fact]
    public async Task CompletedInstance_ReactivationPreservesHistoryVariablesAndFlowEvidenceAndCreatesFreshWork()
    {
        var workflow = await CreateWorkflowAsync(CreateEvidenceWorkflow());
        var started = await StartAsync(workflow.Id);
        var originalTask = await GetSingleActiveTaskAsync(started.Id);

        using (var completion = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{originalTask.Id}/flows/20",
                   new TakeFlowRequest(null),
                   user: "finisher",
                   roles: ["Reviewer"]))
        {
            Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
            Assert.Equal(
                "completed",
                (await ReadAsync<UserTaskActionAckDto>(completion)).InstanceStatus,
                ignoreCase: true);
        }

        InstanceReactivationPreviewDto preview;
        using (var previewResponse = await SendAsync(
                   HttpMethod.Get,
                   $"/api/instances/{started.Id}/reactivation"))
        {
            Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
            preview = await ReadAsync<InstanceReactivationPreviewDto>(previewResponse);
        }
        Assert.True(preview.CanReactivate);
        Assert.Equal("completed", preview.Status, ignoreCase: true);
        Assert.Equal(workflow.Id, preview.WorkflowId);
        var target = Assert.Single(preview.Targets);
        Assert.Equal(2, target.NodeId);
        Assert.Equal("Review retained case", target.NodeName);
        Assert.Equal("retained-review", target.NodeExternalId);
        Assert.Contains(preview.Warnings, issue => issue.Code == "retained_state");
        Assert.Contains(preview.Warnings, issue => issue.Code == "repeated_side_effects");

        RuntimeSnapshot before;
        await using (var db = fixture.CreateDbContext())
        {
            before = await SnapshotAsync(db, started.Id);
            Assert.Single(before.UserTasks);
            Assert.All(before.Tokens.Values, token =>
                Assert.Equal(ExecutionTokenStatuses.Completed, token.Status));
            Assert.Equal(UserTaskStatuses.Completed, before.UserTasks[originalTask.Id].Status);
            Assert.NotEmpty(before.VariableIds);
            Assert.NotEmpty(before.FlowOccurrences);
            Assert.NotEmpty(before.FlowSummaries);
        }

        InstanceDetailDto reactivated;
        using (var response = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{started.Id}/reactivation",
                   new ReactivateInstanceRequest(
                       target.NodeId,
                       preview.WorkflowId,
                       preview.ExpectedUpdatedAt,
                       "  restore after verified completion  "),
                   user: "reactivation-admin",
                   roles: ["admin", "Auditor"],
                   suppressDefaultAdmin: true))
        {
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                await response.Content.ReadAsStringAsync());
            reactivated = await ReadAsync<InstanceDetailDto>(response);
        }

        Assert.Equal("running", reactivated.Status, ignoreCase: true);
        Assert.Null(reactivated.FinishedAt);
        Assert.Null(reactivated.HistoryPrunedAt);
        Assert.Equal(2, reactivated.CurrentNodeId);
        Assert.Equal("Review retained case", reactivated.CurrentNodeName);
        Assert.Null(reactivated.Completion);
        var activePosition = Assert.Single(
            reactivated.ExecutionPositions,
            position => position.TokenStatus == ExecutionTokenStatuses.Active);
        Assert.Equal(2, activePosition.NodeId);
        Assert.Equal(ExecutionTokenStatuses.Active, activePosition.TokenStatus);
        var reactivationAudit = Assert.Single(reactivated.History, item =>
            item.Note == "instanceReactivated");
        Assert.Equal("reactivation-admin", reactivationAudit.PerformedBy);
        Assert.Equal("restore after verified completion", reactivationAudit.Reason);
        Assert.Equal(4, reactivationAudit.FromNodeId);
        Assert.Equal(2, reactivationAudit.ToNodeId);
        Assert.Null(reactivationAudit.SequenceFlowId);
        Assert.Equal(
            "completed",
            reactivationAudit.Payload!["sourceStatus"].GetString(),
            ignoreCase: true);
        Assert.Equal(2, reactivationAudit.Payload["targetNodeId"].GetInt32());
        Assert.Equal(workflow.Version, reactivationAudit.Payload["workflowVersion"].GetInt32());
        Assert.Equal("reactivation-admin", reactivationAudit.Payload["operator"].GetString());
        Assert.Contains(
            reactivationAudit.Payload["operatorRoles"].EnumerateArray()
                .Select(value => value.GetString()),
            role => string.Equals(role, "Auditor", StringComparison.OrdinalIgnoreCase));

        await using (var db = fixture.CreateDbContext())
        {
            var after = await SnapshotAsync(db, started.Id);

            Assert.Equal(before.VariableIds, after.VariableIds);
            Assert.Equal(before.CurrentVariables, after.CurrentVariables);
            Assert.Equal(before.FlowOccurrences, after.FlowOccurrences);
            Assert.Equal(before.FlowSummaries, after.FlowSummaries);
            Assert.Equal(before.HistoryIds.Count + 1, after.HistoryIds.Count);

            foreach (var (id, oldToken) in before.Tokens)
            {
                Assert.Equal(oldToken, after.Tokens[id]);
            }
            foreach (var (id, oldTask) in before.UserTasks)
            {
                Assert.Equal(oldTask, after.UserTasks[id]);
            }
            foreach (var (id, oldExecution) in before.NodeExecutions)
            {
                Assert.Equal(oldExecution, after.NodeExecutions[id]);
            }

            var newToken = Assert.Single(
                after.Tokens,
                pair => !before.Tokens.ContainsKey(pair.Key));
            Assert.Equal(ExecutionTokenStatuses.Active, newToken.Value.Status);
            Assert.Equal(2, newToken.Value.NodeId);
            Assert.Null(newToken.Value.GatewayBranchId);
            Assert.DoesNotContain(
                before.Tokens.Values.Select(token => token.ActivationId),
                activationId => activationId == newToken.Value.ActivationId);

            var newTask = Assert.Single(
                after.UserTasks,
                pair => !before.UserTasks.ContainsKey(pair.Key));
            Assert.Equal(UserTaskStatuses.Active, newTask.Value.Status);
            Assert.Equal(newToken.Key, newTask.Value.TokenId);
            Assert.Equal(2, newTask.Value.NodeId);

            var newExecution = Assert.Single(
                after.NodeExecutions,
                pair => !before.NodeExecutions.ContainsKey(pair.Key));
            Assert.Equal(NodeExecutionStatuses.Active, newExecution.Value.Status);
            Assert.Equal(NodeExecutionKinds.Node, newExecution.Value.ExecutionKind);
            Assert.Equal(2, newExecution.Value.NodeId);
            Assert.Equal(newToken.Key, newExecution.Value.TokenId);
            Assert.Equal(newTask.Key, newExecution.Value.UserTaskId);
            Assert.Equal(workflow.Id, newExecution.Value.WorkflowDefinitionId);
            Assert.Null(newExecution.Value.EntryGatewayBranchId);
            Assert.Null(newExecution.Value.MultiInstanceExecutionId);

            Assert.Single(after.Tokens.Values, token =>
                token.Status == ExecutionTokenStatuses.Active);
            Assert.Single(after.UserTasks.Values, task =>
                task.Status == UserTaskStatuses.Active);
            Assert.Single(after.NodeExecutions.Values, execution =>
                execution.Status == NodeExecutionStatuses.Active);
        }

        var freshTask = await GetSingleActiveTaskAsync(started.Id);
        Assert.NotEqual(originalTask.Id, freshTask.Id);
        using var repeatCompletion = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{freshTask.Id}/flows/20",
            new TakeFlowRequest(null),
            user: "second-finisher",
            roles: ["Reviewer"]);
        Assert.Equal(HttpStatusCode.OK, repeatCompletion.StatusCode);
        Assert.Equal(
            "completed",
            (await ReadAsync<UserTaskActionAckDto>(repeatCompletion)).InstanceStatus,
            ignoreCase: true);
    }

    [Fact]
    public async Task CancelledInstance_ReactivationLeavesCancelledRowsAndCreatesOneFreshActivation()
    {
        var workflow = await CreateWorkflowAsync(CreateSimpleWorkflow());
        var started = await StartAsync(workflow.Id);
        var originalTask = await GetSingleActiveTaskAsync(started.Id);

        using (var cancellation = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{started.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);
        }

        var preview = await PreviewAsync(started.Id);
        Assert.True(preview.CanReactivate);
        Assert.Equal("cancelled", preview.Status, ignoreCase: true);
        Assert.Equal(2, Assert.Single(preview.Targets).NodeId);

        RuntimeSnapshot before;
        await using (var db = fixture.CreateDbContext())
        {
            before = await SnapshotAsync(db, started.Id);
            Assert.Equal(UserTaskStatuses.Cancelled, before.UserTasks[originalTask.Id].Status);
            Assert.All(before.Tokens.Values, token =>
                Assert.Equal(ExecutionTokenStatuses.Cancelled, token.Status));
        }

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{started.Id}/reactivation",
            new ReactivateInstanceRequest(
                2,
                preview.WorkflowId,
                preview.ExpectedUpdatedAt,
                "customer supplied the missing documents"),
            user: "case-admin");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reactivated = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal("running", reactivated.Status, ignoreCase: true);
        Assert.Equal(2, reactivated.CurrentNodeId);

        await using var afterDb = fixture.CreateDbContext();
        var after = await SnapshotAsync(afterDb, started.Id);
        foreach (var (id, token) in before.Tokens)
        {
            Assert.Equal(token, after.Tokens[id]);
        }
        foreach (var (id, task) in before.UserTasks)
        {
            Assert.Equal(task, after.UserTasks[id]);
        }
        foreach (var (id, execution) in before.NodeExecutions)
        {
            Assert.Equal(execution, after.NodeExecutions[id]);
        }
        Assert.Single(after.Tokens.Values, token => token.Status == ExecutionTokenStatuses.Active);
        Assert.Single(after.UserTasks.Values, task => task.Status == UserTaskStatuses.Active);
        Assert.Single(after.NodeExecutions.Values, execution => execution.Status == NodeExecutionStatuses.Active);
        var audit = await afterDb.InstanceHistory.SingleAsync(item =>
            item.InstanceId == started.Id && item.Note == "instanceReactivated");
        Assert.Equal("cancelled", audit.Payload!.RootElement.GetProperty("sourceStatus").GetString());
        Assert.Equal("customer supplied the missing documents", audit.Reason);
    }

    [Fact]
    public async Task TerminateEndCompletion_ReactivatesAtItsPriorTopLevelTaskWithFreshActivation()
    {
        var workflow = await CreateWorkflowAsync(
            CreateSimpleWorkflow(BpmnFlowNodeTypes.TerminateEndEvent));
        var started = await StartAsync(workflow.Id);
        var originalTask = await GetSingleActiveTaskAsync(started.Id);

        using (var terminate = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{originalTask.Id}/flows/20",
                   new TakeFlowRequest(null),
                   user: "terminator",
                   roles: ["Reviewer"]))
        {
            Assert.Equal(HttpStatusCode.OK, terminate.StatusCode);
            var acknowledgement = await ReadAsync<UserTaskActionAckDto>(terminate);
            Assert.Equal("completed", acknowledgement.InstanceStatus, ignoreCase: true);
            Assert.Equal(
                WorkflowCompletionKinds.Terminate,
                Assert.IsType<CompletionInfoDto>(acknowledgement.Completion).Kind);
        }

        long sourceTokenId;
        Guid sourceActivationId;
        await using (var before = fixture.CreateDbContext())
        {
            var sourceToken = await before.ExecutionTokens.SingleAsync(item =>
                item.InstanceId == started.Id);
            sourceTokenId = sourceToken.Id;
            sourceActivationId = sourceToken.ActivationId;
            Assert.Equal(ExecutionTokenStatuses.Completed, sourceToken.Status);
            Assert.Equal("terminateEnd", sourceToken.TerminationReason);
        }

        var preview = await PreviewAsync(started.Id);
        Assert.True(preview.CanReactivate);
        Assert.Equal(2, Assert.Single(preview.Targets).NodeId);
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{started.Id}/reactivation",
            new ReactivateInstanceRequest(
                2,
                workflow.Id,
                preview.ExpectedUpdatedAt,
                "reopen after terminate-end completion"),
            user: "terminate-admin");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reactivated = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal("running", reactivated.Status, ignoreCase: true);
        Assert.Equal(2, reactivated.CurrentNodeId);
        var audit = Assert.Single(reactivated.History, item =>
            item.Note == "instanceReactivated");
        Assert.Equal(
            sourceTokenId,
            audit.Payload!["sourceTokenId"].GetInt64());
        Assert.Equal("completed", audit.Payload["sourceStatus"].GetString());

        await using var after = fixture.CreateDbContext();
        var oldToken = await after.ExecutionTokens.SingleAsync(item =>
            item.Id == sourceTokenId);
        Assert.Equal(ExecutionTokenStatuses.Completed, oldToken.Status);
        Assert.Equal("terminateEnd", oldToken.TerminationReason);
        Assert.Equal(sourceActivationId, oldToken.ActivationId);
        var freshToken = await after.ExecutionTokens.SingleAsync(item =>
            item.InstanceId == started.Id
            && item.Status == ExecutionTokenStatuses.Active);
        Assert.NotEqual(sourceTokenId, freshToken.Id);
        Assert.NotEqual(sourceActivationId, freshToken.ActivationId);
        Assert.Equal(2, freshToken.NodeId);
        Assert.Single(await after.UserTasks.Where(item =>
            item.InstanceId == started.Id
            && item.Status == UserTaskStatuses.Active).ToListAsync());
        Assert.Single(await after.NodeExecutions.Where(item =>
            item.InstanceId == started.Id
            && item.Status == NodeExecutionStatuses.Active).ToListAsync());
    }

    [Fact]
    public async Task PreviewAndCommit_RejectRunningFaultedStaleAndInvalidReasonRequests()
    {
        var runningWorkflow = await CreateWorkflowAsync(CreateSimpleWorkflow());
        var running = await StartAsync(runningWorkflow.Id);
        var runningPreview = await PreviewAsync(running.Id);
        Assert.False(runningPreview.CanReactivate);
        Assert.Contains(runningPreview.Blockers, issue =>
            issue.Code == "status_not_reactivatable");
        using (var runningCommit = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{running.Id}/reactivation",
                   new ReactivateInstanceRequest(
                       2,
                       runningWorkflow.Id,
                       running.UpdatedAt,
                       "running is not terminal")))
        {
            Assert.Equal(HttpStatusCode.Conflict, runningCommit.StatusCode);
        }

        var faultWorkflow = await CreateWorkflowAsync(
            CreateSimpleWorkflow(BpmnFlowNodeTypes.ErrorEndEvent));
        var faultStarted = await StartAsync(faultWorkflow.Id);
        var faultTask = await GetSingleActiveTaskAsync(faultStarted.Id);
        using (var fault = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{faultTask.Id}/flows/20",
                   new TakeFlowRequest(null),
                   roles: ["Reviewer"]))
        {
            Assert.True(
                fault.StatusCode == HttpStatusCode.OK,
                await fault.Content.ReadAsStringAsync());
            Assert.Equal(
                "faulted",
                (await ReadAsync<UserTaskActionAckDto>(fault)).InstanceStatus,
                ignoreCase: true);
        }
        var faultPreview = await PreviewAsync(faultStarted.Id);
        Assert.False(faultPreview.CanReactivate);
        Assert.Contains(faultPreview.Blockers, issue =>
            issue.Code == "status_not_reactivatable"
            && issue.Message.Contains("Faulted", StringComparison.OrdinalIgnoreCase));
        using (var faultCommit = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{faultStarted.Id}/reactivation",
                   new ReactivateInstanceRequest(
                       2,
                       faultWorkflow.Id,
                       faultPreview.ExpectedUpdatedAt,
                       "faulted is unsupported")))
        {
            Assert.Equal(HttpStatusCode.Conflict, faultCommit.StatusCode);
        }

        var completedWorkflow = await CreateWorkflowAsync(CreateSimpleWorkflow());
        var completed = await CompleteAsync(completedWorkflow.Id);
        var completedPreview = await PreviewAsync(completed.Id);
        Assert.True(completedPreview.CanReactivate);

        using (var stale = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{completed.Id}/reactivation",
                   new ReactivateInstanceRequest(
                       2,
                       completedPreview.WorkflowId,
                       completedPreview.ExpectedUpdatedAt.AddTicks(-1),
                       "stale operator preview")))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        }
        using (var wrongWorkflow = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{completed.Id}/reactivation",
                   new ReactivateInstanceRequest(
                       2,
                       completedPreview.WorkflowId + 1,
                       completedPreview.ExpectedUpdatedAt,
                       "wrong workflow fence")))
        {
            Assert.Equal(HttpStatusCode.Conflict, wrongWorkflow.StatusCode);
        }
        using (var blankReason = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{completed.Id}/reactivation",
                   new ReactivateInstanceRequest(
                       2,
                       completedPreview.WorkflowId,
                       completedPreview.ExpectedUpdatedAt,
                       "   ")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, blankReason.StatusCode);
        }
        using (var longReason = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{completed.Id}/reactivation",
                   new ReactivateInstanceRequest(
                       2,
                       completedPreview.WorkflowId,
                       completedPreview.ExpectedUpdatedAt,
                       string.Concat(Enumerable.Repeat("😀", 1001)))))
        {
            Assert.Equal(HttpStatusCode.BadRequest, longReason.StatusCode);
        }

        await using var db = fixture.CreateDbContext();
        var unchanged = await db.WorkflowInstances.SingleAsync(item => item.Id == completed.Id);
        Assert.Equal("completed", unchanged.Status, ignoreCase: true);
        Assert.False(await db.InstanceHistory.AnyAsync(item =>
            item.InstanceId == completed.Id && item.Note == "instanceReactivated"));
    }

    [Fact]
    public async Task ConditionalBoundaryThatIsCurrentlyTrue_MakesHistoricalTaskIneligible()
    {
        var workflow = await CreateWorkflowAsync(CreateTrueConditionalBoundaryWorkflow());
        var terminal = await StartAsync(workflow.Id, expectedStatus: "completed", expectedNodeId: 5);

        var preview = await PreviewAsync(terminal.Id);

        Assert.False(preview.CanReactivate);
        Assert.Empty(preview.Targets);
        var blocker = Assert.Single(preview.Blockers, issue =>
            issue.Code == "conditional_boundary_true");
        Assert.Equal(2, blocker.NodeId);
        Assert.Equal(4, blocker.StateId);
        Assert.Contains(preview.Blockers, issue => issue.Code == "no_eligible_targets");

        using var commit = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{terminal.Id}/reactivation",
            new ReactivateInstanceRequest(
                2,
                workflow.Id,
                preview.ExpectedUpdatedAt,
                "boundary should prevent a transient reactivation"));
        Assert.Equal(HttpStatusCode.Conflict, commit.StatusCode);

        await using var db = fixture.CreateDbContext();
        Assert.Equal("completed", (await db.WorkflowInstances.SingleAsync(item =>
            item.Id == terminal.Id)).Status, ignoreCase: true);
        Assert.False(await db.ExecutionTokens.AnyAsync(token =>
            token.InstanceId == terminal.Id
            && token.Status == ExecutionTokenStatuses.Active));
        Assert.False(await db.InstanceHistory.AnyAsync(item =>
            item.InstanceId == terminal.Id && item.Note == "instanceReactivated"));
    }

    [Fact]
    public async Task BranchScopedHistoricalTasks_AreNotEligibleTargets()
    {
        var workflow = await CreateWorkflowAsync(CreateBranchScopedTerminateWorkflow());
        var started = await StartAsync(workflow.Id, expectedNodeId: 3);
        var tasks = await ListActiveTasksAsync(started.Id);
        var terminatingTask = Assert.Single(tasks, task => task.NodeId == 3);
        Assert.Single(tasks, task => task.NodeId == 4);

        using (var terminate = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{terminatingTask.Id}/flows/30",
                   new TakeFlowRequest(null)))
        {
            Assert.Equal(HttpStatusCode.OK, terminate.StatusCode);
            Assert.Equal(
                "completed",
                (await ReadAsync<UserTaskActionAckDto>(terminate)).InstanceStatus,
                ignoreCase: true);
        }

        var preview = await PreviewAsync(started.Id);
        Assert.False(preview.CanReactivate);
        Assert.Empty(preview.Targets);
        Assert.Contains(preview.Blockers, issue => issue.Code == "missing_execution_evidence");
        Assert.Contains(preview.Blockers, issue => issue.Code == "no_eligible_targets");
    }

    [Fact]
    public async Task RetainedComplexLineageMarker_BlocksPreviewAndCommitWithoutMutation()
    {
        var workflow = await CreateWorkflowAsync(CreateSimpleWorkflow());
        var completed = await CompleteAsync(workflow.Id);
        long sourceTokenId;
        long tokenCount;
        long taskCount;
        long executionCount;
        await using (var dirty = fixture.CreateDbContext())
        {
            var token = await dirty.ExecutionTokens.SingleAsync(item =>
                item.InstanceId == completed.Id);
            sourceTokenId = token.Id;
            token.ComplexDrainStateIds = [987654321];
            await dirty.SaveChangesAsync();
            tokenCount = await dirty.ExecutionTokens.LongCountAsync(item =>
                item.InstanceId == completed.Id);
            taskCount = await dirty.UserTasks.LongCountAsync(item =>
                item.InstanceId == completed.Id);
            executionCount = await dirty.NodeExecutions.LongCountAsync(item =>
                item.InstanceId == completed.Id);
        }

        var preview = await PreviewAsync(completed.Id);
        Assert.False(preview.CanReactivate);
        var blocker = Assert.Single(preview.Blockers, issue =>
            issue.Code == "complex_lineage_retained");
        Assert.Equal(sourceTokenId, blocker.StateId);
        Assert.Single(preview.Targets, target => target.NodeId == 2);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{completed.Id}/reactivation",
            new ReactivateInstanceRequest(
                2,
                workflow.Id,
                preview.ExpectedUpdatedAt,
                "must reject retained Complex lineage"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var after = fixture.CreateDbContext();
        Assert.Equal("completed", (await after.WorkflowInstances.SingleAsync(item =>
            item.Id == completed.Id)).Status, ignoreCase: true);
        Assert.Equal(tokenCount, await after.ExecutionTokens.LongCountAsync(item =>
            item.InstanceId == completed.Id));
        Assert.Equal(taskCount, await after.UserTasks.LongCountAsync(item =>
            item.InstanceId == completed.Id));
        Assert.Equal(executionCount, await after.NodeExecutions.LongCountAsync(item =>
            item.InstanceId == completed.Id));
        Assert.False(await after.InstanceHistory.AnyAsync(item =>
            item.InstanceId == completed.Id && item.Note == "instanceReactivated"));
    }

    [Fact]
    public async Task UnknownAndAsyncBeforeTargets_AreRejectedWithoutCreatingRows()
    {
        var workflow = await CreateWorkflowAsync(CreateSimpleWorkflow());
        var completed = await CompleteAsync(workflow.Id);
        var preview = await PreviewAsync(completed.Id);
        Assert.True(preview.CanReactivate);

        long beforeTokens;
        long beforeTasks;
        long beforeExecutions;
        await using (var before = fixture.CreateDbContext())
        {
            beforeTokens = await before.ExecutionTokens.CountAsync(item => item.InstanceId == completed.Id);
            beforeTasks = await before.UserTasks.CountAsync(item => item.InstanceId == completed.Id);
            beforeExecutions = await before.NodeExecutions.CountAsync(item => item.InstanceId == completed.Id);
        }

        using (var unknown = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{completed.Id}/reactivation",
                   new ReactivateInstanceRequest(
                       999,
                       workflow.Id,
                       preview.ExpectedUpdatedAt,
                       "unvisited node")))
        {
            Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        }

        await using (var mutateDefinition = fixture.CreateDbContext())
        {
            var definition = await mutateDefinition.WorkflowDefinitions.SingleAsync(item =>
                item.Id == workflow.Id);
            definition.Definition.FlowNodes.Single(node => node.Id == 2).AsyncBefore = true;
            mutateDefinition.Entry(definition)
                .Property(item => item.Definition)
                .IsModified = true;
            await mutateDefinition.SaveChangesAsync();
        }
        fixture.Factory.Services.GetRequiredService<IMemoryCache>()
            .Remove($"wf:def:{workflow.Id}");

        var asyncPreview = await PreviewAsync(completed.Id);
        Assert.False(asyncPreview.CanReactivate);
        Assert.Empty(asyncPreview.Targets);
        Assert.Contains(asyncPreview.Blockers, issue => issue.Code == "target_async_before");
        using (var asyncTarget = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{completed.Id}/reactivation",
                   new ReactivateInstanceRequest(
                       2,
                       workflow.Id,
                       asyncPreview.ExpectedUpdatedAt,
                       "durable task is not a stable re-entry point")))
        {
            Assert.Equal(HttpStatusCode.Conflict, asyncTarget.StatusCode);
        }

        await using var after = fixture.CreateDbContext();
        Assert.Equal(beforeTokens, await after.ExecutionTokens.CountAsync(item => item.InstanceId == completed.Id));
        Assert.Equal(beforeTasks, await after.UserTasks.CountAsync(item => item.InstanceId == completed.Id));
        Assert.Equal(beforeExecutions, await after.NodeExecutions.CountAsync(item => item.InstanceId == completed.Id));
        Assert.False(await after.InstanceHistory.AnyAsync(item =>
            item.InstanceId == completed.Id && item.Note == "instanceReactivated"));
    }

    [Fact]
    public async Task ActiveBusinessKeyConflict_IsVisibleInPreviewAndKeepsTheNewOwner()
    {
        var model = CreateBusinessKeyWorkflow(BusinessKeyUniqueness.Active);
        var workflow = await CreateWorkflowAsync(model);
        const string businessKey = "CASE-REACTIVATION-CONFLICT";

        var original = await StartAsync(
            workflow.Id,
            variables: new Dictionary<string, JsonElement>
            {
                ["caseId"] = JsonSerializer.SerializeToElement(businessKey)
            });
        var originalTask = await GetSingleActiveTaskAsync(original.Id);
        await CompleteTaskAsync(originalTask.Id, user: "first-owner");
        var replacement = await StartAsync(
            workflow.Id,
            variables: new Dictionary<string, JsonElement>
            {
                ["caseId"] = JsonSerializer.SerializeToElement(businessKey)
            });
        Assert.NotEqual(original.Id, replacement.Id);

        var preview = await PreviewAsync(original.Id);
        Assert.False(preview.CanReactivate);
        Assert.Contains(preview.Blockers, blocker =>
            blocker.Code == "business_key_conflict"
            && blocker.Message.Contains(
                $"instance #{replacement.Id}",
                StringComparison.Ordinal));

        using var conflict = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{original.Id}/reactivation",
            new ReactivateInstanceRequest(
                2,
                workflow.Id,
                preview.ExpectedUpdatedAt,
                "attempt to retake an active business key"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        await using var db = fixture.CreateDbContext();
        Assert.Equal("completed", (await db.WorkflowInstances.SingleAsync(item =>
            item.Id == original.Id)).Status, ignoreCase: true);
        var claim = await db.WorkflowBusinessKeyClaims.SingleAsync(item =>
            item.WorkflowKey == model.Id && item.BusinessKey == businessKey);
        Assert.Equal(replacement.Id, claim.ActiveInstanceId);
        Assert.False(await db.InstanceHistory.AnyAsync(item =>
            item.InstanceId == original.Id && item.Note == "instanceReactivated"));
        Assert.Single(await db.ExecutionTokens.Where(item =>
            item.InstanceId == original.Id).ToListAsync());
        Assert.Single(await db.UserTasks.Where(item =>
            item.InstanceId == original.Id).ToListAsync());
    }

    [Theory]
    [InlineData(BusinessKeyUniqueness.Active)]
    [InlineData(BusinessKeyUniqueness.All)]
    public async Task Reactivation_ReacquiresItsReleasedOrPermanentBusinessKey(string uniqueness)
    {
        var model = CreateBusinessKeyWorkflow(uniqueness);
        var workflow = await CreateWorkflowAsync(model);
        var businessKey = $"CASE-{uniqueness}-{Guid.NewGuid():N}";
        var completed = await StartAsync(
            workflow.Id,
            variables: new Dictionary<string, JsonElement>
            {
                ["caseId"] = JsonSerializer.SerializeToElement(businessKey)
            });
        await CompleteTaskAsync((await GetSingleActiveTaskAsync(completed.Id)).Id);
        var preview = await PreviewAsync(completed.Id);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{completed.Id}/reactivation",
            new ReactivateInstanceRequest(
                2,
                workflow.Id,
                preview.ExpectedUpdatedAt,
                "resume the same business case"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var db = fixture.CreateDbContext();
        var claim = await db.WorkflowBusinessKeyClaims.SingleAsync(item =>
            item.WorkflowKey == model.Id && item.BusinessKey == businessKey);
        Assert.Equal(completed.Id, claim.ActiveInstanceId);
        Assert.Equal(completed.Id, claim.LastInstanceId);
        Assert.Equal(uniqueness == BusinessKeyUniqueness.All, claim.IsPermanent);
    }

    [Fact]
    public async Task ConcurrentRequestsFromOnePreview_CommitExactlyOnce()
    {
        var workflow = await CreateWorkflowAsync(CreateSimpleWorkflow());
        var completed = await CompleteAsync(workflow.Id);
        var preview = await PreviewAsync(completed.Id);
        var request = new ReactivateInstanceRequest(
            2,
            workflow.Id,
            preview.ExpectedUpdatedAt,
            "single winner under the instance lock");

        var responses = await Task.WhenAll(
            SendAsync(
                HttpMethod.Post,
                $"/api/instances/{completed.Id}/reactivation",
                request,
                user: "operator-one"),
            SendAsync(
                HttpMethod.Post,
                $"/api/instances/{completed.Id}/reactivation",
                request,
                user: "operator-two"));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var db = fixture.CreateDbContext();
        Assert.Equal("running", (await db.WorkflowInstances.SingleAsync(item =>
            item.Id == completed.Id)).Status, ignoreCase: true);
        Assert.Single(await db.ExecutionTokens.Where(item =>
            item.InstanceId == completed.Id
            && item.Status == ExecutionTokenStatuses.Active).ToListAsync());
        Assert.Single(await db.UserTasks.Where(item =>
            item.InstanceId == completed.Id
            && item.Status == UserTaskStatuses.Active).ToListAsync());
        Assert.Single(await db.NodeExecutions.Where(item =>
            item.InstanceId == completed.Id
            && item.Status == NodeExecutionStatuses.Active).ToListAsync());
        Assert.Single(await db.InstanceHistory.Where(item =>
            item.InstanceId == completed.Id
            && item.Note == "instanceReactivated").ToListAsync());
    }

    [Fact]
    public async Task Endpoints_RequireAuthenticationAndTheDynamicWorkflowAdministratorRole()
    {
        await SetWorkflowRequiredRoleAsync(string.Empty);
        try
        {
            var workflow = await CreateWorkflowAsync(CreateSimpleWorkflow());
            var completed = await CompleteAsync(workflow.Id);

            using (var anonymousPreview = await SendAnonymousAsync(
                       HttpMethod.Get,
                       $"/api/instances/{completed.Id}/reactivation"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymousPreview.StatusCode);
            }
            using (var anonymousCommit = await SendAnonymousAsync(
                       HttpMethod.Post,
                       $"/api/instances/{completed.Id}/reactivation",
                       new ReactivateInstanceRequest(
                           2,
                           workflow.Id,
                           completed.UpdatedAt,
                           "anonymous")))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymousCommit.StatusCode);
            }
            using (var forbiddenPreview = await SendAsync(
                       HttpMethod.Get,
                       $"/api/instances/{completed.Id}/reactivation",
                       user: "worker",
                       roles: ["Worker"],
                       suppressDefaultAdmin: true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, forbiddenPreview.StatusCode);
            }

            await SetWorkflowRequiredRoleAsync(" ReactivationAdmin, ReleaseManager ");

            using (var formerAdmin = await SendAsync(
                       HttpMethod.Get,
                       $"/api/instances/{completed.Id}/reactivation",
                       user: "old-admin",
                       roles: ["admin"],
                       suppressDefaultAdmin: true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, formerAdmin.StatusCode);
            }
            InstanceReactivationPreviewDto configuredPreview;
            using (var configured = await SendAsync(
                       HttpMethod.Get,
                       $"/api/instances/{completed.Id}/reactivation",
                       user: "release-manager",
                       roles: ["reAcTiVaTiOnAdMiN"],
                       suppressDefaultAdmin: true))
            {
                Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
                configuredPreview = await ReadAsync<InstanceReactivationPreviewDto>(configured);
            }
            using var configuredCommit = await SendAsync(
                HttpMethod.Post,
                $"/api/instances/{completed.Id}/reactivation",
                new ReactivateInstanceRequest(
                    2,
                    workflow.Id,
                    configuredPreview.ExpectedUpdatedAt,
                    "authorized dynamic administrator"),
                user: "release-manager",
                roles: ["ReleaseManager"],
                suppressDefaultAdmin: true);
            Assert.Equal(HttpStatusCode.OK, configuredCommit.StatusCode);
        }
        finally
        {
            await SetWorkflowRequiredRoleAsync("admin");
        }
    }

    private async Task<WorkflowDetailDto> CreateWorkflowAsync(WorkflowModel definition)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(definition, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private async Task<InstanceDetailDto> StartAsync(
        long workflowId,
        string expectedStatus = "running",
        int expectedNodeId = 2,
        Dictionary<string, JsonElement>? variables = null)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, variables),
            user: "starter");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal(expectedStatus, detail.Status, ignoreCase: true);
        Assert.Equal(expectedNodeId, detail.CurrentNodeId);
        return detail;
    }

    private async Task<InstanceDetailDto> CompleteAsync(long workflowId)
    {
        var started = await StartAsync(workflowId);
        await CompleteTaskAsync((await GetSingleActiveTaskAsync(started.Id)).Id);
        using var detail = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{started.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var completed = await ReadAsync<InstanceDetailDto>(detail);
        Assert.Equal("completed", completed.Status, ignoreCase: true);
        return completed;
    }

    private async Task CompleteTaskAsync(long taskId, string user = "finisher")
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{taskId}/flows/20",
            new TakeFlowRequest(null),
            user: user,
            roles: ["Reviewer"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "completed",
            (await ReadAsync<UserTaskActionAckDto>(response)).InstanceStatus,
            ignoreCase: true);
    }

    private async Task<InstanceReactivationPreviewDto> PreviewAsync(long instanceId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/reactivation");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceReactivationPreviewDto>(response);
    }

    private async Task<UserTaskDto> GetSingleActiveTaskAsync(long instanceId) =>
        Assert.Single(await ListActiveTasksAsync(instanceId));

    private async Task<IReadOnlyList<UserTaskDto>> ListActiveTasksAsync(long instanceId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/user-tasks?status=active&page=1&pageSize=100",
            user: "reviewer",
            roles: ["Reviewer"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<PagedResult<UserTaskDto>>(response)).Items;
    }

    private Task<HttpResponseMessage> SendAnonymousAsync(
        HttpMethod method,
        string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        return fixture.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string[]? roles = null,
        bool suppressDefaultAdmin = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles ?? ["admin"]);
        if (suppressDefaultAdmin)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        }
        return fixture.Client.SendAsync(request);
    }

    private async Task SetWorkflowRequiredRoleAsync(string value)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleOrDefaultAsync(item =>
            item.Namespace == "Workflow" && item.Key == "RequiredRole");
        if (setting is null)
        {
            db.EngineSettings.Add(new EngineSettingEntity
            {
                Namespace = "Workflow",
                Key = "RequiredRole",
                Value = value
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException(
            $"Response did not contain {typeof(T).Name}.");

    private static async Task<RuntimeSnapshot> SnapshotAsync(
        Flowbit.Infrastructure.Data.AppDbContext db,
        long instanceId)
    {
        var tokens = (await db.ExecutionTokens.AsNoTracking()
                .Where(item => item.InstanceId == instanceId)
                .OrderBy(item => item.Id)
                .ToListAsync())
            .ToDictionary(
                item => item.Id,
                item => new TokenSnapshot(
                    item.NodeId,
                    item.Status,
                    item.ActivationId,
                    item.GatewayBranchId,
                    item.TerminationReason));
        var tasks = (await db.UserTasks.AsNoTracking()
                .Where(item => item.InstanceId == instanceId)
                .OrderBy(item => item.Id)
                .ToListAsync())
            .ToDictionary(
                item => item.Id,
                item => new TaskSnapshot(
                    item.TokenId,
                    item.NodeId,
                    item.Status,
                    item.CompletedAt,
                    item.CompletedBy));
        var executions = (await db.NodeExecutions.AsNoTracking()
                .Where(item => item.InstanceId == instanceId)
                .OrderBy(item => item.Id)
                .ToListAsync())
            .ToDictionary(
                item => item.Id,
                item => new NodeExecutionSnapshot(
                    item.WorkflowDefinitionId,
                    item.ExecutionTokenId,
                    item.UserTaskId,
                    item.MultiInstanceExecutionId,
                    item.NodeId,
                    item.ExecutionKind,
                    item.Status,
                    item.CompletionReason,
                    item.EntryGatewayBranchId,
                    item.CompletedAt));
        var variableIds = await db.InstanceVariables.AsNoTracking()
            .Where(item => item.InstanceId == instanceId)
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .ToListAsync();
        var currentVariables = (await db.InstanceVariableCurrentValues.AsNoTracking()
                .Where(item => item.InstanceId == instanceId)
                .OrderBy(item => item.VariableName)
                .ToListAsync())
            .Select(item => $"{item.VariableName}:{item.ValueJson.RootElement.GetRawText()}:{item.SourceVariableId}")
            .ToArray();
        var historyIds = await db.InstanceHistory.AsNoTracking()
            .Where(item => item.InstanceId == instanceId)
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .ToListAsync();
        var occurrences = (await db.SequenceFlowOccurrences.AsNoTracking()
                .Where(item => item.InstanceId == instanceId)
                .OrderBy(item => item.Id)
                .ToListAsync())
            .Select(item => new FlowOccurrenceSnapshot(
                item.Id,
                item.SequenceFlowId,
                item.TokenId,
                item.UserTaskId,
                item.Kind,
                item.IsAction,
                item.IsTraversal,
                item.User,
                item.OccurredAt))
            .ToArray();
        var summaries = (await db.SequenceFlowSummaries.AsNoTracking()
                .Where(item => item.InstanceId == instanceId)
                .OrderBy(item => item.SequenceFlowId)
                .ToListAsync())
            .Select(item => new FlowSummarySnapshot(
                item.SequenceFlowId,
                item.ActionCount,
                item.TraversalCount,
                item.LastActionUser,
                item.LastTraversalUser,
                item.LastActionOccurredAt,
                item.LastTraversalOccurredAt))
            .ToArray();
        return new RuntimeSnapshot(
            tokens,
            tasks,
            executions,
            variableIds,
            currentVariables,
            historyIds,
            occurrences,
            summaries);
    }

    private static WorkflowModel CreateSimpleWorkflow(
        string terminalType = BpmnFlowNodeTypes.EndEvent)
    {
        var key = $"instance-reactivation-{Guid.NewGuid():N}";
        return new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = 1,
            CancelRoles = ["admin"],
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    ExternalId = "start"
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    ExternalId = "review",
                    Roles = ["Reviewer"]
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Terminal",
                    Type = terminalType,
                    ExternalId = "terminal",
                    ErrorCode = terminalType == BpmnFlowNodeTypes.ErrorEndEvent
                        ? "REACTIVATION_TEST_FAULT"
                        : null,
                    ErrorDescription = terminalType == BpmnFlowNodeTypes.ErrorEndEvent
                        ? "Faulted instances remain terminal."
                        : null
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 20,
                    Name = "Finish review",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Reviewer"]
                }
            ]
        };
    }

    private static WorkflowModel CreateEvidenceWorkflow()
    {
        var key = $"instance-reactivation-evidence-{Guid.NewGuid():N}";
        return new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = 1,
            CancelRoles = ["admin"],
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "memo",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement("retain me")
                },
                new VariableModel
                {
                    Id = 2,
                    Name = "observedActions",
                    DataType = WorkflowVariableTypes.Number,
                    DefaultValue = JsonSerializer.SerializeToElement(0)
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review retained case",
                    Type = BpmnFlowNodeTypes.UserTask,
                    ExternalId = "retained-review",
                    Roles = ["Reviewer"]
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Observe retained FlowInfo",
                    Type = BpmnFlowNodeTypes.ScriptTask,
                    ScriptFormat = ScriptFormats.JavaScript,
                    UsesFlowInfo = true,
                    Script = "const info = execution.getFlowInfo(20); "
                             + "execution.setVariable('observedActions', info.actions.count);"
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Completed",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 20,
                    Name = "Approve retained case",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Reviewer"]
                },
                new SequenceFlowModel { Id = 30, SourceRef = 3, TargetRef = 4 }
            ]
        };
    }

    private static WorkflowModel CreateTrueConditionalBoundaryWorkflow()
    {
        var key = $"instance-reactivation-boundary-{Guid.NewGuid():N}";
        return new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "approved",
                    DataType = WorkflowVariableTypes.Boolean,
                    Required = true,
                    DefaultValue = JsonSerializer.SerializeToElement(true)
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 3, Name = "Normal end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Approved boundary",
                    Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = true,
                    Conditional = new ConditionalDefinitionModel
                    {
                        Condition = "approved == true",
                        DeliveryMode = ConditionalEventDeliveryModes.Atomic
                    }
                },
                new FlowNodeModel { Id = 5, Name = "Boundary end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 20, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 40, SourceRef = 4, TargetRef = 5 }
            ]
        };
    }

    private static WorkflowModel CreateBranchScopedTerminateWorkflow()
    {
        var key = $"instance-reactivation-branch-{Guid.NewGuid():N}";
        return new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 3, Name = "Terminate branch", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 4, Name = "Sibling branch", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 5, Name = "Terminate", Type = BpmnFlowNodeTypes.TerminateEndEvent },
                new FlowNodeModel { Id = 6, Name = "Sibling end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 21, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 22, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 30, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 40, SourceRef = 4, TargetRef = 6 }
            ]
        };
    }

    private static WorkflowModel CreateBusinessKeyWorkflow(string uniqueness)
    {
        var model = CreateSimpleWorkflow();
        var start = model.FlowNodes.Single(node => node.Id == 1);
        start.Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "caseId",
                DataType = WorkflowVariableTypes.String,
                Required = true
            }
        ];
        start.BusinessKey = new BusinessKeyModel
        {
            Variable = "caseId",
            Uniqueness = uniqueness
        };
        return model;
    }

    private sealed record RuntimeSnapshot(
        IReadOnlyDictionary<long, TokenSnapshot> Tokens,
        IReadOnlyDictionary<long, TaskSnapshot> UserTasks,
        IReadOnlyDictionary<long, NodeExecutionSnapshot> NodeExecutions,
        IReadOnlyList<long> VariableIds,
        IReadOnlyList<string> CurrentVariables,
        IReadOnlyList<long> HistoryIds,
        IReadOnlyList<FlowOccurrenceSnapshot> FlowOccurrences,
        IReadOnlyList<FlowSummarySnapshot> FlowSummaries);

    private sealed record TokenSnapshot(
        int NodeId,
        string Status,
        Guid ActivationId,
        long? GatewayBranchId,
        string? TerminationReason);

    private sealed record TaskSnapshot(
        long TokenId,
        int NodeId,
        string Status,
        DateTimeOffset? CompletedAt,
        string? CompletedBy);

    private sealed record NodeExecutionSnapshot(
        long WorkflowDefinitionId,
        long TokenId,
        long? UserTaskId,
        long? MultiInstanceExecutionId,
        int NodeId,
        string ExecutionKind,
        string Status,
        string? CompletionReason,
        long? EntryGatewayBranchId,
        DateTimeOffset? CompletedAt);

    private sealed record FlowOccurrenceSnapshot(
        long Id,
        int SequenceFlowId,
        long? TokenId,
        long? UserTaskId,
        string Kind,
        bool IsAction,
        bool IsTraversal,
        string? User,
        DateTimeOffset OccurredAt);

    private sealed record FlowSummarySnapshot(
        int SequenceFlowId,
        long ActionCount,
        long TraversalCount,
        string? LastActionUser,
        string? LastTraversalUser,
        DateTimeOffset? LastActionOccurredAt,
        DateTimeOffset? LastTraversalOccurredAt);
}
