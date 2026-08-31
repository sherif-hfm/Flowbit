using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceReactivationTopologyApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SynchronizingMergeWithoutFreshSplit_IsRejectedWithoutMutation()
    {
        var workflow = await CreateWorkflowAsync(CreateUnsafeSynchronizingMergeWorkflow());
        var started = await StartAsync(workflow.Id);
        var task = await GetSingleActiveTaskAsync(started.Id);

        using (var response = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/flows/20",
                   new TakeFlowRequest(null),
                   user: "reviewer",
                   roles: ["Reviewer"]))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await CancelAsync(started.Id);

        var preview = await PreviewAsync(started.Id);
        Assert.False(preview.CanReactivate);
        Assert.Empty(preview.Targets);
        var topologyBlocker = Assert.Single(
            preview.Blockers,
            blocker => blocker.Code == "target_unsafe_topology");
        Assert.Equal(2, topologyBlocker.NodeId);
        Assert.Contains("synchronizing merge #3", topologyBlocker.Message);
        Assert.Contains(preview.Blockers, blocker => blocker.Code == "no_eligible_targets");
        Assert.DoesNotContain(
            preview.Blockers,
            blocker => blocker.Code == "missing_execution_evidence");

        await AssertCommitRejectedWithoutMutationAsync(started.Id, preview, targetNodeId: 2);
    }

    [Fact]
    public async Task MultiInstanceItemEvidence_DoesNotMakeParentTaskEligible()
    {
        var workflow = await CreateWorkflowAsync(CreateMultiInstanceWorkflow());
        var started = await StartAsync(workflow.Id);

        await using (var db = fixture.CreateDbContext())
        {
            var executions = await db.NodeExecutions.AsNoTracking()
                .Where(execution =>
                    execution.InstanceId == started.Id
                    && execution.NodeId == 2)
                .OrderBy(execution => execution.Id)
                .ToListAsync();

            Assert.Equal(2, executions.Count);
            Assert.All(executions, execution =>
            {
                Assert.Equal(NodeExecutionKinds.UserTaskItem, execution.ExecutionKind);
                Assert.NotNull(execution.MultiInstanceExecutionId);
                Assert.Equal(NodeExecutionStatuses.Active, execution.Status);
            });
            Assert.DoesNotContain(
                executions,
                execution => execution.ExecutionKind == NodeExecutionKinds.Node);
        }

        await CancelAsync(started.Id);

        await using (var db = fixture.CreateDbContext())
        {
            var executions = await db.NodeExecutions.AsNoTracking()
                .Where(execution =>
                    execution.InstanceId == started.Id
                    && execution.NodeId == 2)
                .ToListAsync();
            Assert.All(executions, execution =>
                Assert.Equal(NodeExecutionStatuses.Cancelled, execution.Status));
        }

        var preview = await PreviewAsync(started.Id);
        Assert.False(preview.CanReactivate);
        Assert.Empty(preview.Targets);
        Assert.Contains(
            preview.Blockers,
            blocker => blocker.Code == "missing_execution_evidence");
        Assert.Contains(preview.Blockers, blocker => blocker.Code == "no_eligible_targets");

        await AssertCommitRejectedWithoutMutationAsync(started.Id, preview, targetNodeId: 2);
    }

    private async Task AssertCommitRejectedWithoutMutationAsync(
        long instanceId,
        InstanceReactivationPreviewDto preview,
        int targetNodeId)
    {
        var before = await ReadMutationSnapshotAsync(instanceId);

        using (var response = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{instanceId}/reactivation",
                   new ReactivateInstanceRequest(
                       targetNodeId,
                       preview.WorkflowId,
                       preview.ExpectedUpdatedAt,
                       "verify unsafe target rejection")))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        var after = await ReadMutationSnapshotAsync(instanceId);
        Assert.Equal(before, after);

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.InstanceHistory.AsNoTracking().AnyAsync(item =>
            item.InstanceId == instanceId
            && item.Note == "instanceReactivated"));
    }

    private async Task<MutationSnapshot> ReadMutationSnapshotAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        var instance = await db.WorkflowInstances.AsNoTracking()
            .SingleAsync(item => item.Id == instanceId);
        return new MutationSnapshot(
            instance.Status,
            instance.UpdatedAt,
            await db.ExecutionTokens.CountAsync(item => item.InstanceId == instanceId),
            await db.UserTasks.CountAsync(item => item.InstanceId == instanceId),
            await db.NodeExecutions.CountAsync(item => item.InstanceId == instanceId),
            await db.InstanceHistory.CountAsync(item => item.InstanceId == instanceId));
    }

    private async Task<WorkflowDetailDto> CreateWorkflowAsync(WorkflowModel definition)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(definition, true));
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            user: "starter");
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());
        var started = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal("running", started.Status, ignoreCase: true);
        Assert.Equal(2, started.CurrentNodeId);
        return started;
    }

    private async Task CancelAsync(long instanceId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instanceId}/cancel");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<InstanceReactivationPreviewDto> PreviewAsync(long instanceId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/reactivation");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceReactivationPreviewDto>(response);
    }

    private async Task<UserTaskDto> GetSingleActiveTaskAsync(long instanceId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/user-tasks?status=active&page=1&pageSize=100",
            user: "reviewer",
            roles: ["Reviewer"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single((await ReadAsync<PagedResult<UserTaskDto>>(response)).Items);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string[]? roles = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles ?? ["admin"]);
        return fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException(
            $"Response did not contain {typeof(T).Name}.");

    private static WorkflowModel CreateUnsafeSynchronizingMergeWorkflow()
    {
        var key = $"reactivation-unsafe-merge-{Guid.NewGuid():N}";
        return new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = 1,
            CancelRoles = ["admin"],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Choose branch",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Reviewer"]
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Unsafe merge",
                    Type = BpmnFlowNodeTypes.ParallelGateway
                },
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 5, Name = "First path", Type = BpmnFlowNodeTypes.Task },
                new FlowNodeModel { Id = 6, Name = "Second path", Type = BpmnFlowNodeTypes.Task }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 20,
                    Name = "Take first path",
                    SourceRef = 2,
                    TargetRef = 5,
                    Roles = ["Reviewer"]
                },
                new SequenceFlowModel
                {
                    Id = 21,
                    Name = "Take second path",
                    SourceRef = 2,
                    TargetRef = 6,
                    Roles = ["Reviewer"]
                },
                new SequenceFlowModel { Id = 50, SourceRef = 5, TargetRef = 3 },
                new SequenceFlowModel { Id = 60, SourceRef = 6, TargetRef = 3 },
                new SequenceFlowModel { Id = 30, SourceRef = 3, TargetRef = 4 }
            ]
        };
    }

    private static WorkflowModel CreateMultiInstanceWorkflow()
    {
        var key = $"reactivation-multi-instance-{Guid.NewGuid():N}";
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
                    Name = "approvalResults",
                    DataType = WorkflowVariableTypes.Json,
                    DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Parallel approvals",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Reviewer"],
                    MultiInstance = new MultiInstanceModel
                    {
                        Mode = MultiInstanceModes.Parallel,
                        Source = MultiInstanceSources.Cardinality,
                        CardinalityExpression = "2",
                        CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
                        ResultVariable = "approvalResults"
                    }
                },
                new FlowNodeModel { Id = 3, Name = "Approved", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 4, Name = "Fallback", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 20,
                    Name = "Approve",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Reviewer"],
                    CompletionCondition = "CountFlow(20) == 2",
                    CompletionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 21,
                    Name = "No outcome",
                    SourceRef = 2,
                    TargetRef = 4,
                    IsDefault = true,
                    IsSelectable = false
                }
            ]
        };
    }

    private sealed record MutationSnapshot(
        string Status,
        DateTimeOffset UpdatedAt,
        int TokenCount,
        int TaskCount,
        int ExecutionCount,
        int HistoryCount);
}
