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
public sealed class InstanceAdministrativeActionApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Sample_AdminCanApproveReturnAndFinishWithoutGainingPersonalTaskAccess()
    {
        var model = JsonSerializer.Deserialize<WorkflowModel>(
            ExampleWorkflowData.Read("examples/basics/10-admin-action.json"), JsonOptions)!;
        model.Id = $"direct-admin-example-{Guid.NewGuid():N}";
        var instance = await StartAsync(model);
        var first = Assert.Single((await PositionsAsync(instance.Id)).Items);
        Assert.Equal(2, first.Position.NodeId);
        Assert.Equal(new[] { 102, 106 }, first.Actions.Select(x => x.FlowId).Order().ToArray());
        Assert.Empty((await InboxAsync(instance.Id, "administrator", ["admin"])).Items);
        Assert.Single((await InboxAsync(instance.Id, "worker", ["User"])).Items);
        await AssertOrdinaryDeniedAsync(instance.Id, first.Position.UserTaskId!.Value, 102);

        var approved = await ExecuteAsync(instance.Id, Request(first, 102));
        Assert.Equal(3, Assert.Single((await PositionsAsync(instance.Id)).Items).Position.NodeId);
        Assert.Empty((await InboxAsync(instance.Id, "worker", ["User"])).Items);
        Assert.Single((await InboxAsync(instance.Id, "manager", ["Manager"])).Items);
        Assert.Empty((await InboxAsync(instance.Id, "administrator", ["admin"])).Items);
        await AssertAuditAsync(approved, 1);

        var second = Assert.Single((await PositionsAsync(instance.Id)).Items);
        Assert.Equal(new[] { 103, 104, 107 }, second.Actions.Select(x => x.FlowId).Order().ToArray());
        await AssertOrdinaryDeniedAsync(instance.Id, second.Position.UserTaskId!.Value, 104);
        await ExecuteAsync(instance.Id, Request(second, 104));
        var returned = Assert.Single((await PositionsAsync(instance.Id)).Items);
        Assert.Equal(2, returned.Position.NodeId);
        Assert.NotEqual(first.Position.PositionId, returned.Position.PositionId);
        var completed = await ExecuteAsync(instance.Id, Request(returned, 106));
        Assert.Equal("completed", completed.Instance.Status);
        Assert.Empty((await PositionsAsync(instance.Id)).Items);

        await using var db = fixture.CreateDbContext();
        var persisted = await db.WorkflowDefinitions.SingleAsync(x => x.Id == instance.Workflow.Id);
        Assert.Equal(new[] { "User" }, persisted.Definition.FlowNodes.Single(x => x.Id == 2).Roles);
        Assert.Equal(new[] { "Manager" }, persisted.Definition.FlowNodes.Single(x => x.Id == 3).Roles);
        Assert.False(await db.WorkflowJobs.AnyAsync(x => x.WorkflowDefinitionId == instance.Workflow.Id));
    }

    [Fact]
    public async Task NewEndpointsRejectAnonymousAndUnrelatedRolesIncludingSpoofedAuthority()
    {
        var instance = await StartAsync(OrdinaryModel());
        var position = Assert.Single((await PositionsAsync(instance.Id)).Items);
        var request = Request(position, 201);
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var anonymous = await SendAsync(method, Path(instance.Id), method == HttpMethod.Post ? request : null,
                user: null);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var plain = await SendAsync(method, Path(instance.Id), method == HttpMethod.Post ? request : null,
                roles: ["Worker"]);
            Assert.Equal(HttpStatusCode.Forbidden, plain.StatusCode);
        }

        var forged = JsonSerializer.SerializeToNode(request, JsonOptions)!.AsObject();
        forged["actor"] = JsonSerializer.SerializeToNode(new { user = "admin", roles = new[] { "admin" } });
        forged["administrativeOverride"] = true;
        forged["batchId"] = 1;
        using var denied = await SendAsync(HttpMethod.Post, Path(instance.Id), forged, roles: ["Worker"]);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await AssertNoAuditAsync(instance);
    }

    [Fact]
    public async Task DirectActionRechecksConfiguredRoleAfterDiscoveryAndDoesNotImplicitlyRetainAdmin()
    {
        var instance = await StartAsync(OrdinaryModel());
        var request = Request(Assert.Single((await PositionsAsync(instance.Id)).Items), 201);
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleAsync(x => x.Namespace == "Workflow" && x.Key == "RequiredRole");
        var original = setting.Value;
        try
        {
            setting.Value = " OperationsLead , Recovery ";
            await db.SaveChangesAsync();
            using var oldAdmin = await SendAsync(HttpMethod.Post, Path(instance.Id), request);
            Assert.Equal(HttpStatusCode.Forbidden, oldAdmin.StatusCode);
            using var custom = await SendAsync(HttpMethod.Get, Path(instance.Id), roles: ["operationslead"]);
            Assert.Equal(HttpStatusCode.OK, custom.StatusCode);
            using var execute = await SendAsync(HttpMethod.Post, Path(instance.Id), request, roles: ["RECOVERY"]);
            Assert.Equal(HttpStatusCode.OK, execute.StatusCode);
        }
        finally
        {
            setting.Value = original;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task CrossInstanceAndStaleSelectionsDoNotCreateAuditOrAdvance()
    {
        var model = OrdinaryModel();
        var first = await StartAsync(model);
        var second = await StartExistingAsync(first.Workflow.Id);
        var selection = Assert.Single((await PositionsAsync(first.Id)).Items);
        var request = Request(selection, 201);
        using (var cross = await SendAsync(HttpMethod.Post, Path(second.Id), request))
            Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        using (var missing = await SendAsync(HttpMethod.Post, Path(first.Id), request with { PositionId = long.MaxValue }))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        foreach (var stale in new[]
                 {
                     request with { ExpectedTokenActivationId = Guid.NewGuid() },
                     request with { ExpectedPositionUpdatedAt = request.ExpectedPositionUpdatedAt.AddSeconds(-1) },
                     request with { ExpectedWorkflowDefinitionId = first.Workflow.Id + 100000 },
                     request with { ExpectedAffectedTaskCount = 2 }
                 })
        {
            using var result = await SendAsync(HttpMethod.Post, Path(first.Id), stale);
            Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        }
        await AssertNoAuditAsync(first);
        Assert.Equal(selection.Position.PositionId, Assert.Single((await PositionsAsync(first.Id)).Items).Position.PositionId);
    }

    [Fact]
    public async Task ConcurrentDuplicateRequestsCommitOnlyOneTransitionAndOneAudit()
    {
        var instance = await StartAsync(OrdinaryModel());
        var request = Request(Assert.Single((await PositionsAsync(instance.Id)).Items), 201);
        var responses = await Task.WhenAll(
            SendAsync(HttpMethod.Post, Path(instance.Id), request),
            SendAsync(HttpMethod.Post, Path(instance.Id), request));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            await using var db = fixture.CreateDbContext();
            Assert.Equal(1, await db.AdministrativeActionBatches.CountAsync(x => x.WorkflowDefinitionId == instance.Workflow.Id));
            Assert.Equal(1, await db.SequenceFlowOccurrences.CountAsync(x => x.InstanceId == instance.Id && x.SequenceFlowId == 201 && x.IsTraversal));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task HiddenAssignedClaimedTaskAndFalseConditionRequireExplicitOverride()
    {
        var model = OrdinaryModel();
        var task = model.FlowNodes.Single(x => x.Id == 2);
        task.InboxVisibilityCondition = "false";
        task.RequiresClaim = true;
        task.AssigneeExpression = "'assigned-worker'";
        model.SequenceFlows.Single(x => x.Id == 201).Condition = "false";
        var instance = await StartAsync(model);
        var position = Assert.Single((await PositionsAsync(instance.Id)).Items);
        await using (var db = fixture.CreateDbContext())
        {
            var stored = await db.UserTasks.SingleAsync(x => x.Id == position.Position.UserTaskId);
            stored.ClaimedBy = "assigned-worker";
            await db.SaveChangesAsync();
        }
        position = Assert.Single((await PositionsAsync(instance.Id)).Items);
        Assert.Empty((await InboxAsync(instance.Id, "assigned-worker", ["Worker"])).Items);
        await AssertOrdinaryDeniedAsync(instance.Id, position.Position.UserTaskId!.Value, 201);
        var result = await ExecuteAsync(instance.Id, Request(position, 201));
        Assert.Equal("completed", result.Instance.Status);
        await AssertAuditAsync(result, 1);
    }

    [Theory]
    [InlineData(MultiInstanceModes.Parallel, AdministrativeActionMultiInstanceModes.ForceParent)]
    [InlineData(MultiInstanceModes.Parallel, AdministrativeActionMultiInstanceModes.CompleteAllChildren)]
    [InlineData(MultiInstanceModes.Sequential, AdministrativeActionMultiInstanceModes.ForceParent)]
    [InlineData(MultiInstanceModes.Sequential, AdministrativeActionMultiInstanceModes.CompleteAllChildren)]
    public async Task MultiInstanceParentUsesExplicitModeAndKeepsNormalChildAndInterruptPermissions(string executionMode, string actionMode)
    {
        var model = MultiInstanceModel(executionMode);
        var instance = await StartAsync(model);
        var position = Assert.Single((await PositionsAsync(instance.Id)).Items);
        Assert.Equal(AdministrativeActionPositionKinds.MultiInstanceExecution, position.Position.PositionKind);
        Assert.Equal(3, position.Position.AffectedTaskCount);
        Assert.DoesNotContain(position.Actions, x => x.FlowId == 202);
        Assert.Empty((await InboxAsync(instance.Id, "administrator", ["admin"])).Items);
        using (var normalInterrupt = await SendAsync(HttpMethod.Post,
                   $"/api/multi-instance-executions/{position.Position.PositionId}/flows/203", new TakeFlowRequest(null)))
            Assert.Contains(normalInterrupt.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.Forbidden });
        using (var noMode = await SendAsync(HttpMethod.Post, Path(instance.Id), Request(position, 201)))
            Assert.Equal(HttpStatusCode.BadRequest, noMode.StatusCode);
        using (var fallback = await SendAsync(HttpMethod.Post, Path(instance.Id), Request(position, 202) with { MultiInstanceMode = actionMode }))
            Assert.Equal(HttpStatusCode.BadRequest, fallback.StatusCode);
        var result = await ExecuteAsync(instance.Id, Request(position, 201) with { MultiInstanceMode = actionMode });
        Assert.Equal("completed", result.Instance.Status);
        await AssertAuditAsync(result, 3);
        await using var db = fixture.CreateDbContext();
        var children = await db.UserTasks.Where(x => x.InstanceId == instance.Id).ToListAsync();
        Assert.Equal(3, children.Count);
        Assert.All(children, child =>
        {
            Assert.Equal(actionMode == AdministrativeActionMultiInstanceModes.ForceParent ? "cancelled" : "completed", child.Status);
            Assert.Equal(result.AdministrativeActionBatchId, child.AdministrativeActionBatchId);
            Assert.Equal(actionMode == AdministrativeActionMultiInstanceModes.ForceParent ? (int?)null : 201, child.SelectedFlowId);
        });
        Assert.Equal(1, await db.SequenceFlowOccurrences.CountAsync(x => x.InstanceId == instance.Id && x.SequenceFlowId == 201 && x.IsTraversal));
    }

    [Fact]
    public async Task RequiredTypedInputsFailWithoutLeavingAuditThenAcceptValidValue()
    {
        var model = OrdinaryModel();
        model.SequenceFlows.Single(x => x.Id == 201).Variables =
        [new VariableModel { Id = 10, Name = "amount", DataType = WorkflowVariableTypes.Number, Required = true }];
        var instance = await StartAsync(model);
        var request = Request(Assert.Single((await PositionsAsync(instance.Id)).Items), 201);
        foreach (var invalid in new[] { request, request with { Variables = new() { ["amount"] = JsonSerializer.SerializeToElement("invalid") } } })
        {
            using var response = await SendAsync(HttpMethod.Post, Path(instance.Id), invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertNoAuditAsync(instance);
        }
        var result = await ExecuteAsync(instance.Id, request with { Variables = new() { ["amount"] = JsonSerializer.SerializeToElement(12) } });
        Assert.Equal("completed", result.Instance.Status);
    }

    [Fact]
    public async Task DownstreamFailureRollsBackTaskAndEntireSynchronousAudit()
    {
        var model = OrdinaryModel();
        model.FlowNodes.Single(x => x.Id == 3).Type = BpmnFlowNodeTypes.ScriptTask;
        model.FlowNodes.Single(x => x.Id == 3).ScriptFormat = ScriptFormats.JavaScript;
        model.FlowNodes.Single(x => x.Id == 3).Script = "throw new Error('direct admin rollback probe');";
        model.FlowNodes.Add(new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 });
        var instance = await StartAsync(model);
        var position = Assert.Single((await PositionsAsync(instance.Id)).Items);
        using var response = await SendAsync(HttpMethod.Post, Path(instance.Id), Request(position, 201));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoAuditAsync(instance);
        Assert.Equal(position.Position.PositionId, Assert.Single((await PositionsAsync(instance.Id)).Items).Position.PositionId);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.SequenceFlowOccurrences.AnyAsync(x => x.InstanceId == instance.Id && x.SequenceFlowId == 201));
    }

    [Fact]
    public async Task ParallelPositionsArePagedAndOnlySelectedBranchMoves()
    {
        var model = OrdinaryModel();
        model.FlowNodes.Single(x => x.Id == 1).Type = BpmnFlowNodeTypes.StartEvent;
        model.FlowNodes.Add(new FlowNodeModel { Id = 4, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway });
        model.FlowNodes.Add(new FlowNodeModel { Id = 5, Name = "Other review", Type = BpmnFlowNodeTypes.UserTask, Roles = ["Other"] });
        model.SequenceFlows.Single(x => x.Id == 101).TargetRef = 4;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 2 });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 402, SourceRef = 4, TargetRef = 5 });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 3 });
        var instance = await StartAsync(model);
        var first = await PositionsAsync(instance.Id, 1, 1);
        var second = await PositionsAsync(instance.Id, 2, 1);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(2, second.TotalCount);
        var chosen = Assert.Single(first.Items);
        var untouched = Assert.Single(second.Items);
        Assert.NotEqual(chosen.Position.PositionId, untouched.Position.PositionId);
        await ExecuteAsync(instance.Id, Request(chosen, Assert.Single(chosen.Actions).FlowId));
        var remaining = Assert.Single((await PositionsAsync(instance.Id)).Items);
        Assert.Equal(untouched.Position.PositionId, remaining.Position.PositionId);
        Assert.Equal(untouched.Position.PositionUpdatedAt, remaining.Position.PositionUpdatedAt);
    }

    [Fact]
    public async Task AsyncAfterCommitsAuditWithoutAdministrativeJobsAndRetainsContinuation()
    {
        var model = OrdinaryModel();
        model.FlowNodes.Single(x => x.Id == 2).AsyncAfter = true;
        var instance = await StartAsync(model);
        var result = await ExecuteAsync(instance.Id, Request(Assert.Single((await PositionsAsync(instance.Id)).Items), 201));
        await AssertAuditAsync(result, 1);
        await using var db = fixture.CreateDbContext();
        var job = Assert.Single(await db.WorkflowJobs.Where(x => x.InstanceId == instance.Id).ToListAsync());
        Assert.DoesNotContain("administrativeBatch", job.Kind, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("queued", job.Status);
        Assert.Equal("completed", (await db.UserTasks.SingleAsync(x => x.InstanceId == instance.Id)).Status);
        // Consume our continuation so subsequent queue tests have no stray job,
        // and prove that revoking admin authority does not undo a committed selection.
        var setting = await db.EngineSettings.SingleAsync(x => x.Namespace == "Workflow" && x.Key == "RequiredRole");
        var original = setting.Value;
        try
        {
            setting.Value = "Recovery";
            job.Priority = 10_000;
            job.DueAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
            var leases = await repository.LeaseRunnableAsync(new WorkflowJobLeaseRequest(
                $"direct-admin-continuation-{Guid.NewGuid():N}", 1, 1, 1, TimeSpan.FromMinutes(2)), CancellationToken.None);
            var lease = Assert.Single(leases);
            Assert.Equal(job.Id, lease.Job.Id);
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
        }
        finally
        {
            setting.Value = original;
            await db.SaveChangesAsync();
        }
        await db.Entry(job).ReloadAsync();
        Assert.Equal("completed", job.Status);
        Assert.Equal("completed", (await db.WorkflowInstances.SingleAsync(x => x.Id == instance.Id)).Status);
        Assert.Equal(1, await db.SequenceFlowOccurrences.CountAsync(x => x.InstanceId == instance.Id && x.SequenceFlowId == 201 && x.IsTraversal));
    }

    private static WorkflowModel OrdinaryModel() => new()
    {
        Id = $"direct-admin-{Guid.NewGuid():N}", Name = "Direct admin regression", InitialEventId = 1,
        FlowNodes =
        [
            new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new() { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask, Roles = ["Worker"] },
            new() { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new() { Id = 101, SourceRef = 1, TargetRef = 2 },
            new() { Id = 201, Name = "Approve", SourceRef = 2, TargetRef = 3, Roles = ["Worker"] }
        ]
    };

    private static WorkflowModel MultiInstanceModel(string executionMode)
    {
        var model = OrdinaryModel();
        model.Variables = [new VariableModel { Id = 1, Name = "results", DataType = WorkflowVariableTypes.Json, DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) }];
        model.FlowNodes.Single(x => x.Id == 2).MultiInstance = new MultiInstanceModel
        {
            Mode = executionMode, Source = MultiInstanceSources.Cardinality, CardinalityExpression = "3",
            CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll, ResultVariable = "results"
        };
        model.SequenceFlows.Single(x => x.Id == 201).CompletionCondition = "CountFlow(201) >= 3";
        model.SequenceFlows.Single(x => x.Id == 201).CompletionPriority = 1;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 202, Name = "Fallback", SourceRef = 2, TargetRef = 3, IsDefault = true, IsSelectable = false });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 203, Name = "Interrupt", SourceRef = 2, TargetRef = 3, Roles = ["Worker"], CancelRemainingInstances = true });
        return model;
    }

    private async Task<InstanceDetailDto> StartAsync(WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await StartExistingAsync((await ReadAsync<WorkflowDetailDto>(response)).Id);
    }

    private async Task<InstanceDetailDto> StartExistingAsync(long workflowId)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/instances?detail=full", new StartInstanceRequest(workflowId, null, null, null));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<PagedResult<InstanceAdministrativeActionPositionDto>> PositionsAsync(long instanceId, int page = 1, int pageSize = 50)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Path(instanceId)}?page={page}&pageSize={pageSize}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadAsync<PagedResult<InstanceAdministrativeActionPositionDto>>(response);
    }

    private async Task<AdministrativeActionResultDto> ExecuteAsync(long instanceId, ExecuteInstanceAdministrativeActionRequest request)
    {
        using var response = await SendAsync(HttpMethod.Post, Path(instanceId), request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadAsync<AdministrativeActionResultDto>(response);
    }

    private static ExecuteInstanceAdministrativeActionRequest Request(InstanceAdministrativeActionPositionDto selected, int flowId) => new()
    {
        ExpectedWorkflowDefinitionId = selected.Position.WorkflowDefinitionId,
        SourceNodeId = selected.Position.NodeId, FlowId = flowId,
        PositionKind = selected.Position.PositionKind, PositionId = selected.Position.PositionId,
        ExpectedTokenId = selected.Position.TokenId, ExpectedTokenActivationId = selected.Position.TokenActivationId,
        ExpectedPositionUpdatedAt = selected.Position.PositionUpdatedAt,
        ExpectedAffectedTaskCount = selected.Position.AffectedTaskCount,
        Reason = "Direct administrative regression"
    };

    private async Task<PagedResult<InboxItemDto>> InboxAsync(long instanceId, string user, string[] roles)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/inbox?instanceId={instanceId}&page=1&pageSize=10", user: user, roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<InboxItemDto>>(response);
    }

    private async Task AssertOrdinaryDeniedAsync(long instanceId, long taskId, int flowId)
    {
        foreach (var path in new[] { $"/api/user-tasks/{taskId}/flows/{flowId}", $"/api/instances/{instanceId}/flows/{flowId}" })
        {
            using var response = await SendAsync(HttpMethod.Post, path, new { variables = new { }, administrativeOverride = true, actorRoles = new[] { "Worker", "User", "Manager" }, batchId = 1 });
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.Forbidden });
        }
    }

    private async Task AssertAuditAsync(AdministrativeActionResultDto result, int count)
    {
        await using var db = fixture.CreateDbContext();
        var audit = await db.AdministrativeActionBatches.SingleAsync(x => x.Id == result.AdministrativeActionBatchId);
        Assert.Equal("completed", audit.Status);
        Assert.Equal(1, audit.TotalItemCount);
        Assert.Equal(1, audit.SucceededItemCount);
        Assert.Equal(0, audit.EligibleItemCount);
        Assert.Equal(0, audit.QueuedItemCount);
        Assert.Equal(count, audit.TotalAffectedTaskCount);
        Assert.Equal("administrator", audit.PreparedBy);
        Assert.Equal("administrator", audit.ConfirmedBy);
        Assert.Equal("Direct administrative regression", audit.Reason);
        Assert.Equal(new[] { "admin" }, audit.ConfirmedByRolesJson!.RootElement.EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.NotNull(audit.CompletedAt);
        Assert.Null(audit.PreparationJobId);
        Assert.Null(audit.ExecutionJobId);
        var item = await db.AdministrativeActionBatchItems.SingleAsync(x => x.BatchId == audit.Id);
        Assert.Equal("succeeded", item.Status);
        Assert.Equal(count, item.AffectedTaskCount);
        Assert.NotNull(item.ResultJson);
        Assert.False(await db.WorkflowJobs.AnyAsync(x => x.WorkflowDefinitionId == result.Instance.Workflow.Id &&
            (x.Kind == "administrativeBatchPrepare" || x.Kind == "administrativeBatchExecute")));
    }

    private async Task AssertNoAuditAsync(InstanceDetailDto instance)
    {
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AdministrativeActionBatches.AnyAsync(x => x.WorkflowDefinitionId == instance.Workflow.Id));
        Assert.Equal("running", (await db.WorkflowInstances.SingleAsync(x => x.Id == instance.Id)).Status);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? user = "administrator", string[]? roles = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        if (user is not null)
        {
            ApiTestAuth.Authorize(request, user, roles ?? ["admin"]);
            request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        }
        return await fixture.Client.SendAsync(request);
    }

    private static string Path(long instanceId) => $"/api/instances/{instanceId}/administrative-actions";
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions) ?? throw new InvalidOperationException("Empty response.");
}
