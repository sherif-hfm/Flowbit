using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class TaskRoleManagementApiTests(PostgresApiFixture fixture)
{
    private const string RoleManager = "RoleManager";
    private const string AssignmentManager = "AssignmentManager";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RoleAuthorityIsSeparateAndChangesAreAuditedIdempotentAndImmediatelyEffective()
    {
        var instance = await StartAsync(CreateModel());
        var task = Assert.Single((await ManagedAsync(instance.Id)).Items);
        Assert.True(task.CanManageRoles);
        Assert.False(task.CanManageAssignment);
        var assignmentView = Assert.Single((await ManagedAsync(instance.Id, roles: [AssignmentManager])).Items);
        Assert.True(assignmentView.CanManageAssignment);
        Assert.False(assignmentView.CanManageRoles);
        var url = $"/api/user-tasks/{task.UserTaskId}/roles";
        using var denied = await SendAsync(HttpMethod.Get, url, roles: [AssignmentManager]);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var assignDenied = await SendAsync(HttpMethod.Post,
            $"/api/user-tasks/{task.UserTaskId}/assign", new AssignUserTaskRequest("alice", task.UpdatedAt, null));
        Assert.Equal(HttpStatusCode.Forbidden, assignDenied.StatusCode);

        var original = await PolicyAsync(url);
        Assert.Equal(["Worker"], original.Roles);
        Assert.Equal(["Reviewer"], Assert.Single(original.Flows).Roles);
        var change = new ChangeUserTaskRolesRequest(original.RolePolicyId, [" Legal ", "legal"],
            [new(201, ["LegalAction"])], "Coverage changed");
        var changed = await ChangeAsync(url, change);
        Assert.True(changed.Changed);
        Assert.NotEqual(original.RolePolicyId, changed.Policy.RolePolicyId);
        Assert.Equal(["Legal"], changed.Policy.Roles);
        Assert.Empty((await InboxAsync(instance.Id, "alice", "Worker", "Reviewer")).Items);
        Assert.Single((await InboxAsync(instance.Id, "alice", "Legal", "LegalAction")).Items);

        var noop = await ChangeAsync(url, change with { Roles = ["LEGAL"] });
        Assert.False(noop.Changed);
        Assert.Equal(changed.Policy.RolePolicyId, noop.Policy.RolePolicyId);
        using var stale = await SendAsync(HttpMethod.Post, url, change with { Roles = ["Other"] });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var missingAction = await SendAsync(HttpMethod.Post, url,
            change with { ExpectedRolePolicyId = changed.Policy.RolePolicyId, Flows = [] });
        Assert.Equal(HttpStatusCode.BadRequest, missingAction.StatusCode);
        using var nullRoles = await SendAsync(HttpMethod.Post, url,
            new { expectedRolePolicyId = changed.Policy.RolePolicyId, roles = (string[]?)null, flows = new[] { new { flowId = 201, roles = Array.Empty<string>() } } });
        Assert.Equal(HttpStatusCode.BadRequest, nullRoles.StatusCode);

        using var history = fixture.DataSource.CreateCommand(
            "SELECT COUNT(*) FROM flowbit.instance_history WHERE \"InstanceId\" = $1 AND \"Note\" = 'taskRolesChanged' AND \"Payload\" ->> 'reason' = 'Coverage changed'");
        history.Parameters.AddWithValue(instance.Id);
        Assert.Equal(1L, await history.ExecuteScalarAsync());

        using var completion = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{task.UserTaskId}/flows/201",
            new TakeFlowRequest(null), "alice", ["Legal", "LegalAction"]);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        using var closed = await SendAsync(HttpMethod.Post, url, change with { ExpectedRolePolicyId = changed.Policy.RolePolicyId });
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRoleChangesWithTheSamePolicyHaveOneWinner()
    {
        var instance = await StartAsync(CreateModel());
        var task = Assert.Single((await ManagedAsync(instance.Id)).Items);
        var url = $"/api/user-tasks/{task.UserTaskId}/roles";
        var original = await PolicyAsync(url);
        var responses = await Task.WhenAll(
            SendAsync(HttpMethod.Post, url, new ChangeUserTaskRolesRequest(original.RolePolicyId, ["Legal"], [new(201, [])], null)),
            SendAsync(HttpMethod.Post, url, new ChangeUserTaskRolesRequest(original.RolePolicyId, ["Finance"], [new(201, [])], null)));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            var current = await PolicyAsync(url);
            Assert.NotEqual(original.RolePolicyId, current.RolePolicyId);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task RoleChangePreservesClaimsAndDoesNotConflictWithAnAssignmentOnlyChange()
    {
        var model = CreateModel();
        model.FlowNodes.Single(node => node.Id == 2).RequiresClaim = true;
        var instance = await StartAsync(model);
        var task = Assert.Single((await ManagedAsync(instance.Id)).Items);
        var url = $"/api/user-tasks/{task.UserTaskId}/roles";
        var original = await PolicyAsync(url);
        using var claim = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{task.UserTaskId}/claim", user: "alice", roles: ["Worker", "Reviewer"]);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        var change = new ChangeUserTaskRolesRequest(original.RolePolicyId, ["Legal"], [new(201, ["LegalAction"])], null);
        var changed = await ChangeAsync(url, change);
        var claimed = Assert.Single((await ManagedAsync(instance.Id)).Items);
        Assert.Equal("alice", claimed.Owner);
        Assert.Equal(UserTaskOwnershipKinds.Claimed, claimed.Ownership);
        Assert.True(claimed.RequiresClaim);

        using var assign = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{task.UserTaskId}/assign",
            new AssignUserTaskRequest("bob", claimed.UpdatedAt, null), roles: [AssignmentManager]);
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        var afterAssignment = await ChangeAsync(url,
            new ChangeUserTaskRolesRequest(changed.Policy.RolePolicyId, [], [new(201, [])], null));
        Assert.True(afterAssignment.Changed);
        var assigned = Assert.Single((await ManagedAsync(instance.Id)).Items);
        Assert.Equal("bob", assigned.Owner);
        Assert.Equal(UserTaskOwnershipKinds.Assigned, assigned.Ownership);
        Assert.False(assigned.RequiresClaim);
    }

    [Fact]
    public async Task PendingNormalTasksAreOptInAndOnlyRoleManagersCanManageThem()
    {
        var model = CreateModel();
        model.FlowNodes.Single(node => node.Id == 2).AsyncBefore = true;
        var instance = await StartAsync(model);
        try
        {
            Assert.Empty((await ManagedAsync(instance.Id)).Items);
            Assert.Empty((await ManagedAsync(instance.Id, "open", [AssignmentManager])).Items);
            var pending = Assert.Single((await ManagedAsync(instance.Id, "pending")).Items);
            Assert.Equal("pending", pending.Status);
            Assert.True(pending.CanManageRoles);
            Assert.False(pending.CanManageAssignment);
            var url = $"/api/user-tasks/{pending.UserTaskId}/roles";
            var original = await PolicyAsync(url);
            var changed = await ChangeAsync(url, new ChangeUserTaskRolesRequest(original.RolePolicyId, ["Legal"], [new(201, [])], null));
            Assert.True(changed.Changed);
            Assert.Equal(["Legal"], Assert.Single((await ManagedAsync(instance.Id, "open")).Items).NodeRoles);
            using var badStatus = await SendAsync(HttpMethod.Get, $"/api/user-tasks/manage?instanceId={instance.Id}&status=completed");
            Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
            using var searched = await SendAsync(HttpMethod.Post, "/api/user-tasks/manage/search",
                new ManageableUserTaskSearchRequest { InstanceId = instance.Id, Status = "open", PageSize = 1 });
            Assert.Equal(HttpStatusCode.OK, searched.StatusCode);
            var page = await ReadAsync<PagedResult<ManagedUserTaskDto>>(searched);
            Assert.Equal(1, page.TotalCount);
            Assert.Single(page.Items);
        }
        finally
        {
            // Control jobs outrank activity jobs even when the latter have higher
            // priority. Do not leave this intentionally unactivated wait in the
            // shared fixture's runnable queue for later processor tests.
            using var cancelled = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/cancel");
            Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        }
    }

    [Fact]
    public async Task MultiInstanceChangeUpdatesAllUnfinishedItemsAndPreservesCompletedPolicy()
    {
        var model = CreateModel();
        model.Variables = [new VariableModel { Id = 1, Name = "results", DataType = "json", DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) }];
        var node = model.FlowNodes.Single(node => node.Id == 2);
        node.MultiInstance = new MultiInstanceModel
        {
            Mode = MultiInstanceModes.Sequential, Source = MultiInstanceSources.Cardinality,
            CardinalityExpression = "3", CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
            ResultVariable = "results"
        };
        model.SequenceFlows.Single(flow => flow.Id == 201).CompletionCondition = "CountFlow(201) == 3";
        model.SequenceFlows.Single(flow => flow.Id == 201).CompletionPriority = 1;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 3, Name = "Fallback", IsDefault = true, IsSelectable = false });
        var instance = await StartAsync(model);
        var initialTasks = (await ManagedAsync(instance.Id, "open")).Items.OrderBy(task => task.ItemIndex).ToList();
        Assert.Equal(3, initialTasks.Count);
        Assert.Equal(2, initialTasks.Count(task => task.Status == "pending"));
        var executionId = initialTasks[0].MultiInstanceExecutionId!.Value;
        var url = $"/api/multi-instance-executions/{executionId}/roles";
        var original = await PolicyAsync(url);
        Assert.Single(original.Flows);
        using var wrongScope = await SendAsync(HttpMethod.Get, $"/api/user-tasks/{initialTasks[1].UserTaskId}/roles");
        Assert.Equal(HttpStatusCode.Conflict, wrongScope.StatusCode);
        using var firstCompletion = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{initialTasks[0].UserTaskId}/flows/201",
            new TakeFlowRequest(null), "alice", ["Worker", "Reviewer"]);
        Assert.Equal(HttpStatusCode.OK, firstCompletion.StatusCode);

        var changed = await ChangeAsync(url,
            new ChangeUserTaskRolesRequest(original.RolePolicyId, ["Legal"], [new(201, ["LegalAction"])], "Reroute reviewers"));
        Assert.Equal(1, changed.Policy.ActiveTaskCount);
        Assert.Equal(1, changed.Policy.PendingTaskCount);
        Assert.All((await ManagedAsync(instance.Id, "open")).Items, task => Assert.Equal(["Legal"], task.NodeRoles));
        using var policies = fixture.DataSource.CreateCommand(
            "SELECT \"Id\", \"RolePolicyId\" FROM flowbit.user_tasks WHERE \"MultiInstanceExecutionId\" = $1 ORDER BY \"ItemIndex\"");
        policies.Parameters.AddWithValue(executionId);
        await using var reader = await policies.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(original.RolePolicyId, reader.GetInt64(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(changed.Policy.RolePolicyId, reader.GetInt64(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(changed.Policy.RolePolicyId, reader.GetInt64(1));
    }

    private async Task<UserTaskRolePolicyDto> PolicyAsync(string url)
    {
        using var response = await SendAsync(HttpMethod.Get, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<UserTaskRolePolicyDto>(response);
    }

    private async Task<UserTaskRolesChangeAckDto> ChangeAsync(string url, ChangeUserTaskRolesRequest request)
    {
        using var response = await SendAsync(HttpMethod.Post, url, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<UserTaskRolesChangeAckDto>(response);
    }

    private async Task<PagedResult<ManagedUserTaskDto>> ManagedAsync(long instanceId, string? status = null, string[]? roles = null)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"/api/user-tasks/manage?instanceId={instanceId}&pageSize=200{(status is null ? "" : $"&status={status}")}", roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<ManagedUserTaskDto>>(response);
    }

    private async Task<PagedResult<InboxItemDto>> InboxAsync(long instanceId, string user, params string[] roles)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/inbox?instanceId={instanceId}", user: user, roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<InboxItemDto>>(response);
    }

    private async Task<InstanceDetailDto> StartAsync(WorkflowModel model)
    {
        using var created = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var workflowId = (await ReadAsync<WorkflowDetailDto>(created)).Id;
        using var started = await SendAsync(HttpMethod.Post, "/api/instances?detail=full", new StartInstanceRequest(workflowId, null, null, null));
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        return await ReadAsync<InstanceDetailDto>(started);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null,
        string user = "manager", string[]? roles = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, user, roles ?? [RoleManager]);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions) ?? throw new InvalidOperationException("Empty response.");

    private static WorkflowModel CreateModel() => new()
    {
        Id = $"test-role-management-{Guid.NewGuid():N}", Name = "Task role management", InitialEventId = 1,
        TaskRoleManagementRoles = [RoleManager], TaskAssignmentRoles = [AssignmentManager],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask, Roles = ["Worker"] },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Complete", Roles = ["Reviewer"] }
        ]
    };
}
