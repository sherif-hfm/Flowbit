using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class FlowInfoApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ConfirmAction_RoutesByActorRoleAndFeedsJavaScriptFromPersistedSummary()
    {
        var workflowId = await CreateWorkflowAsync(CreateFlowInfoModel());
        var started = await StartAsync(workflowId);
        var task = await GetSingleActiveTaskAsync(started.Id, "alice", "User", "Manager");

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201",
            new TakeFlowRequest(null),
            "alice",
            ["User", "Manager"]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var acknowledgement = await ReadAsync<UserTaskActionAckDto>(response);
        Assert.Equal("completed", acknowledgement.InstanceStatus);
        Assert.Equal(5, acknowledgement.CurrentNodeId);

        using var detailResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{started.Id}",
            user: "alice",
            roles: ["User", "Manager"]);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(detailResponse);
        Assert.Equal("alice", detail.Variables.Last(variable =>
            variable.VariableName == "confirmedBy").Value.GetString());
        var capturedRoles = detail.Variables.Last(variable =>
                variable.VariableName == "confirmedRoles")
            .Value.EnumerateArray()
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .Cast<string>()
            .ToList();
        Assert.Contains(capturedRoles, role =>
            role.Equals("Manager", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capturedRoles, role =>
            role.Equals("User", StringComparison.OrdinalIgnoreCase));

        await using var db = fixture.CreateDbContext();
        var confirm = await db.SequenceFlowOccurrences.SingleAsync(row =>
            row.InstanceId == started.Id && row.SequenceFlowId == 201);
        Assert.True(confirm.IsAction);
        Assert.True(confirm.IsTraversal);
        Assert.Equal("userTaskAction", confirm.Kind);
        Assert.Equal("alice", confirm.User);
        Assert.Equal(task.Id, confirm.UserTaskId);
        Assert.Contains(confirm.UserRoles, role =>
            role.Equals("Manager", StringComparison.OrdinalIgnoreCase));

        var confirmSummary = await db.SequenceFlowSummaries.SingleAsync(row =>
            row.InstanceId == started.Id && row.SequenceFlowId == 201);
        Assert.Equal(1, confirmSummary.ActionCount);
        Assert.Equal(1, confirmSummary.TraversalCount);
        Assert.Equal("alice", confirmSummary.LastActionUser);
        Assert.Equal("userTaskAction", confirmSummary.LastActionKind);
        Assert.Equal("alice", confirmSummary.LastTraversalUser);
        Assert.Equal("userTaskAction", confirmSummary.LastTraversalKind);
        Assert.NotNull(confirmSummary.LastActionOccurredAt);
        Assert.NotNull(confirmSummary.LastTraversalOccurredAt);
        Assert.Contains(confirmSummary.LastActionUserRoles, role =>
            role.Equals("Manager", StringComparison.OrdinalIgnoreCase));

        var gateway = await db.SequenceFlowOccurrences.SingleAsync(row =>
            row.InstanceId == started.Id && row.SequenceFlowId == 301);
        Assert.False(gateway.IsAction);
        Assert.True(gateway.IsTraversal);
        Assert.Equal("gateway", gateway.Kind);
        Assert.Equal("alice", gateway.User);

        var gatewaySummary = await db.SequenceFlowSummaries.SingleAsync(row =>
            row.InstanceId == started.Id && row.SequenceFlowId == 301);
        Assert.Equal(0, gatewaySummary.ActionCount);
        Assert.Equal(1, gatewaySummary.TraversalCount);
        Assert.Null(gatewaySummary.LastActionUser);
        Assert.Equal("alice", gatewaySummary.LastTraversalUser);
        Assert.Equal("gateway", gatewaySummary.LastTraversalKind);
    }

    [Fact]
    public async Task WorkflowWithoutFlowInfo_DoesNotWriteOccurrenceOrSummaryRows()
    {
        var workflowId = await CreateWorkflowAsync(CreatePlainModel());
        var started = await StartAsync(workflowId);
        var task = await GetSingleActiveTaskAsync(started.Id, "worker");

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201",
            new TakeFlowRequest(null),
            "worker");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("completed", (await ReadAsync<UserTaskActionAckDto>(response)).InstanceStatus);

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.SequenceFlowOccurrences.AnyAsync(row => row.InstanceId == started.Id));
        Assert.False(await db.SequenceFlowSummaries.AnyAsync(row => row.InstanceId == started.Id));
    }

    [Theory]
    [InlineData("execution.getFlowInfo(201)")]
    [InlineData("execution['getFlowInfo'](201)")]
    [InlineData("alias['getFlowInfo'](201)")]
    public async Task JavaScriptFlowInfoCapability_TracksEvidenceForSupportedAccessPatterns(string access)
    {
        var workflowId = await CreateWorkflowAsync(CreateJavaScriptOnlyFlowInfoModel(access));
        var started = await StartAsync(workflowId);
        var task = await GetSingleActiveTaskAsync(started.Id, "reviewer", "User");

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201",
            new TakeFlowRequest(null),
            "reviewer",
            ["User"]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("completed", (await ReadAsync<UserTaskActionAckDto>(response)).InstanceStatus);

        using var detailResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{started.Id}",
            user: "reviewer",
            roles: ["User"]);
        var detail = await ReadAsync<InstanceDetailDto>(detailResponse);
        Assert.Equal("reviewer", detail.Variables.Last(variable =>
            variable.VariableName == "observedUser").Value.GetString());

        await using var db = fixture.CreateDbContext();
        Assert.True(await db.SequenceFlowOccurrences.AnyAsync(row =>
            row.InstanceId == started.Id && row.SequenceFlowId == 201 && row.IsAction));
    }

    [Fact]
    public async Task MultiInstanceSelectionAndAggregateTraversal_UpdateDistinctViewsBeforeDownstreamScript()
    {
        var workflowId = await CreateWorkflowAsync(CreateMultiInstanceFlowInfoModel());
        var started = await StartAsync(workflowId);
        var task = await GetSingleActiveTaskAsync(started.Id, "voter", "User");

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201",
            new TakeFlowRequest(null),
            "voter",
            ["User"]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var acknowledgement = await ReadAsync<UserTaskActionAckDto>(response);
        Assert.Equal("completed", acknowledgement.InstanceStatus);
        Assert.Equal(4, acknowledgement.CurrentNodeId);

        using var detailResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{started.Id}",
            user: "voter",
            roles: ["User"]);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(detailResponse);
        Assert.Equal(1, detail.Variables.Last(variable =>
            variable.VariableName == "observedActionCount").Value.GetInt32());
        Assert.Equal(1, detail.Variables.Last(variable =>
            variable.VariableName == "observedTraversalCount").Value.GetInt32());

        await using var db = fixture.CreateDbContext();
        var occurrences = await db.SequenceFlowOccurrences
            .Where(row => row.InstanceId == started.Id && row.SequenceFlowId == 201)
            .OrderBy(row => row.Id)
            .ToListAsync();
        Assert.Collection(
            occurrences,
            action =>
            {
                Assert.True(action.IsAction);
                Assert.False(action.IsTraversal);
                Assert.Equal("multiInstanceItem", action.Kind);
                Assert.Equal("voter", action.User);
                Assert.Equal(task.Id, action.UserTaskId);
                Assert.NotNull(action.MultiInstanceExecutionId);
                Assert.Equal(0, action.ItemIndex);
            },
            traversal =>
            {
                Assert.False(traversal.IsAction);
                Assert.True(traversal.IsTraversal);
                Assert.Equal("multiInstanceOutcome", traversal.Kind);
                Assert.Equal("voter", traversal.User);
                Assert.Equal(task.Id, traversal.UserTaskId);
                Assert.NotNull(traversal.MultiInstanceExecutionId);
                Assert.Equal(0, traversal.ItemIndex);
            });

        var summary = await db.SequenceFlowSummaries.SingleAsync(row =>
            row.InstanceId == started.Id && row.SequenceFlowId == 201);
        Assert.Equal(1, summary.ActionCount);
        Assert.Equal(1, summary.TraversalCount);
        Assert.Equal("voter", summary.LastActionUser);
        Assert.Equal("multiInstanceItem", summary.LastActionKind);
        Assert.Equal("voter", summary.LastTraversalUser);
        Assert.Equal("multiInstanceOutcome", summary.LastTraversalKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncAfterContinuation_RecordsDeferredTraversalOnce(bool multiInstance)
    {
        var workflowId = await CreateWorkflowAsync(CreateAsyncAfterFlowInfoModel(multiInstance));
        var started = await StartAsync(workflowId);
        var task = await GetSingleActiveTaskAsync(started.Id, "reviewer", "User");

        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201",
            new TakeFlowRequest(null),
            "reviewer",
            ["User"]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("running", (await ReadAsync<UserTaskActionAckDto>(response)).InstanceStatus);

        long jobId;
        long? jobMultiInstanceExecutionId;
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.SingleAsync(row => row.InstanceId == started.Id);
            Assert.Equal("queued", job.Status);
            Assert.Equal(task.Id, job.UserTaskId);
            jobId = job.Id;
            jobMultiInstanceExecutionId = job.MultiInstanceExecutionId;
            Assert.Equal(multiInstance, jobMultiInstanceExecutionId is not null);

            // The action is recorded on completion; the traversal waits for the job.
            Assert.False(await db.SequenceFlowOccurrences.AnyAsync(row =>
                row.InstanceId == started.Id && row.SequenceFlowId == 201 && row.IsTraversal));
        }

        await ProcessJobAsync(jobId);

        var expectedKind = multiInstance ? "multiInstanceAsyncAfter" : "userTaskAsyncAfter";
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal("completed", (await db.WorkflowInstances.SingleAsync(row =>
                row.Id == started.Id)).Status);
            Assert.Equal("completed", (await db.WorkflowJobs.SingleAsync(row =>
                row.Id == jobId)).Status);

            var traversal = await db.SequenceFlowOccurrences.SingleAsync(row =>
                row.InstanceId == started.Id && row.SequenceFlowId == 201 && row.IsTraversal);
            Assert.False(traversal.IsAction);
            Assert.Equal(expectedKind, traversal.Kind);
            Assert.Equal("reviewer", traversal.User);
            Assert.Equal(task.Id, traversal.UserTaskId);
            Assert.Equal(jobMultiInstanceExecutionId, traversal.MultiInstanceExecutionId);

            var summary = await db.SequenceFlowSummaries.SingleAsync(row =>
                row.InstanceId == started.Id && row.SequenceFlowId == 201);
            Assert.Equal(1, summary.ActionCount);
            Assert.Equal(1, summary.TraversalCount);
            Assert.Equal(expectedKind, summary.LastTraversalKind);
        }

        // The gateway after the continuation saw one action and one traversal.
        using var detailResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{started.Id}",
            user: "reviewer",
            roles: ["User"]);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(detailResponse);
        Assert.Equal("completed", detail.Status);
        Assert.Equal(4, detail.CurrentNodeId);
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            "starter");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal("running", detail.Status);
        Assert.Equal(2, detail.CurrentNodeId);
        return detail;
    }

    private async Task<UserTaskDto> GetSingleActiveTaskAsync(
        long instanceId,
        string user,
        params string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/user-tasks?status=active&page=1&pageSize=20",
            user: user,
            roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single((await ReadAsync<PagedResult<UserTaskDto>>(response)).Items);
    }

    private async Task ProcessJobAsync(long jobId)
    {
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.SingleAsync(entity => entity.Id == jobId);
            job.Priority = 10_000;
            // Due a minute ago, so the job is due even when the database clock runs behind this machine's.
            job.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"flow-info-async-after-{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 1,
                MaxPerInstance: 1,
                LeaseDuration: TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        await scope.ServiceProvider
            .GetRequiredService<IWorkflowJobProcessor>()
            .ProcessAsync(lease, CancellationToken.None);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string[]? roles = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, user, roles ?? []);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateFlowInfoModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "flow-info-api-" + suffix,
            Name = "FlowInfo API " + suffix,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "confirmedBy",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement(string.Empty)
                },
                new VariableModel
                {
                    Id = 2,
                    Name = "confirmedRoles",
                    DataType = WorkflowVariableTypes.String,
                    IsArray = true,
                    DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<string>())
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Confirm",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["User", "Manager"]
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Route by confirmer role",
                    Type = BpmnFlowNodeTypes.ExclusiveGateway
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Capture confirmer",
                    Type = BpmnFlowNodeTypes.ScriptTask,
                    ScriptFormat = ScriptFormats.JavaScript,
                    UsesFlowInfo = true,
                    Script = "const info = execution.getFlowInfo(201); " +
                             "execution.setVariable('confirmedBy', info.actions.last.user); " +
                             "execution.setVariable('confirmedRoles', info.actions.last.userRoles);"
                },
                new FlowNodeModel { Id = 5, Name = "Manager confirmed", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 6, Name = "User confirmed", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Confirm",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["User", "Manager"]
                },
                new SequenceFlowModel
                {
                    Id = 301,
                    Name = "Manager",
                    SourceRef = 3,
                    TargetRef = 4,
                    Condition = "Contains(FlowInfo(201, 'actions.last.userRoles'), 'Manager')",
                    ConditionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 302,
                    Name = "User",
                    SourceRef = 3,
                    TargetRef = 6,
                    IsDefault = true
                },
                new SequenceFlowModel { Id = 401, Name = "Finish", SourceRef = 4, TargetRef = 5 }
            ]
        };
    }

    private static WorkflowModel CreatePlainModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "flow-info-opt-out-" + suffix,
            Name = "FlowInfo opt out " + suffix,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "note",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement(string.Empty)
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Work", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Quoted documentation",
                    Type = BpmnFlowNodeTypes.ScriptTask,
                    ScriptFormat = ScriptFormats.JavaScript,
                    UsesFlowInfo = false,
                    Script = "execution.setVariable('note', 'execution.getFlowInfo(201)');"
                },
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Finish", SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, Name = "Done", SourceRef = 3, TargetRef = 4 }
            ]
        };
    }

    private static WorkflowModel CreateJavaScriptOnlyFlowInfoModel(string access)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "flow-info-js-only-" + suffix,
            Name = "FlowInfo JavaScript only " + suffix,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "observedUser",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement(string.Empty)
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["User"]
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Read evidence",
                    Type = BpmnFlowNodeTypes.ScriptTask,
                    ScriptFormat = ScriptFormats.JavaScript,
                    UsesFlowInfo = true,
                    Script = "const alias = execution; const info = " + access + "; " +
                             "execution.setVariable('observedUser', info.actions.last.user);"
                },
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Complete review",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["User"]
                },
                new SequenceFlowModel { Id = 301, Name = "Done", SourceRef = 3, TargetRef = 4 }
            ]
        };
    }

    private static WorkflowModel CreateAsyncAfterFlowInfoModel(bool multiInstance)
    {
        var suffix = Guid.NewGuid().ToString("N");
        // A gateway, unlike a script task, routes inline after the continuation,
        // so the end event reached shows the counts the downstream logic saw.
        var model = new WorkflowModel
        {
            Id = "flow-info-async-after-" + suffix,
            Name = "FlowInfo async after " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["User"],
                    AsyncAfter = true
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Check evidence",
                    Type = BpmnFlowNodeTypes.ExclusiveGateway
                },
                new FlowNodeModel { Id = 4, Name = "Counted once", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 5, Name = "Miscounted", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Approve",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["User"]
                },
                new SequenceFlowModel
                {
                    Id = 301,
                    Name = "Counted once",
                    SourceRef = 3,
                    TargetRef = 4,
                    Condition = "FlowInfo(201, 'actions.count') == 1 and " +
                                "FlowInfo(201, 'traversals.count') == 1",
                    ConditionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 302,
                    Name = "Miscounted",
                    SourceRef = 3,
                    TargetRef = 5,
                    IsDefault = true
                }
            ]
        };
        if (!multiInstance)
        {
            return model;
        }

        model.Variables.Add(new VariableModel
        {
            Id = 1,
            Name = "voteResult",
            DataType = WorkflowVariableTypes.Json,
            DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
        });
        model.FlowNodes.Single(node => node.Id == 2).MultiInstance = new MultiInstanceModel
        {
            Mode = MultiInstanceModes.Parallel,
            Source = MultiInstanceSources.Cardinality,
            CardinalityExpression = "1",
            OnePerActor = false,
            CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
            ResultVariable = "voteResult"
        };
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 6,
            Name = "No outcome",
            Type = BpmnFlowNodeTypes.ErrorEndEvent,
            ErrorCode = "NO_OUTCOME"
        });
        var approve = model.SequenceFlows.Single(flow => flow.Id == 201);
        approve.CompletionCondition = "CountFlow(201) == 1";
        approve.CompletionPriority = 10;
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 205,
            Name = "No outcome",
            SourceRef = 2,
            TargetRef = 6,
            IsDefault = true,
            IsSelectable = false
        });
        return model;
    }

    private static WorkflowModel CreateMultiInstanceFlowInfoModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "flow-info-mi-" + suffix,
            Name = "FlowInfo multi-instance " + suffix,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "voteResult",
                    DataType = WorkflowVariableTypes.Json,
                    DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
                },
                new VariableModel
                {
                    Id = 2,
                    Name = "observedActionCount",
                    DataType = WorkflowVariableTypes.Number,
                    DefaultValue = JsonSerializer.SerializeToElement(0)
                },
                new VariableModel
                {
                    Id = 3,
                    Name = "observedTraversalCount",
                    DataType = WorkflowVariableTypes.Number,
                    DefaultValue = JsonSerializer.SerializeToElement(0)
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Vote",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["User"],
                    MultiInstance = new MultiInstanceModel
                    {
                        Mode = MultiInstanceModes.Parallel,
                        Source = MultiInstanceSources.Cardinality,
                        CardinalityExpression = "1",
                        OnePerActor = false,
                        CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
                        ResultVariable = "voteResult"
                    }
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Observe aggregate",
                    Type = BpmnFlowNodeTypes.ScriptTask,
                    ScriptFormat = ScriptFormats.NCalc,
                    Assignments =
                    [
                        new AssignmentModel
                        {
                            Variable = "observedActionCount",
                            Expression = "FlowInfo(201, 'actions.count')"
                        },
                        new AssignmentModel
                        {
                            Variable = "observedTraversalCount",
                            Expression = "FlowInfo(201, 'traversals.count')"
                        }
                    ]
                },
                new FlowNodeModel { Id = 4, Name = "Approved", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "No outcome",
                    Type = BpmnFlowNodeTypes.ErrorEndEvent,
                    ErrorCode = "NO_OUTCOME"
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Approve",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["User"],
                    CompletionCondition =
                        "CountFlow(201) == 1 and " +
                        "FlowInfo(201, 'actions.count') == 1 and " +
                        "FlowInfo(201, 'traversals.count') == 0",
                    CompletionPriority = 10
                },
                new SequenceFlowModel
                {
                    Id = 205,
                    Name = "No outcome",
                    SourceRef = 2,
                    TargetRef = 5,
                    IsDefault = true,
                    IsSelectable = false
                },
                new SequenceFlowModel { Id = 301, Name = "Finish", SourceRef = 3, TargetRef = 4 }
            ]
        };
    }
}
