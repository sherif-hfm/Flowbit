using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class RuntimeRolePolicyTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task InstanceRoleSourcesFreezeAndNewVisitResolvesLatestValues()
    {
        var model = Model();
        model.SequenceFlows[1].TargetRef = 2;
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 202, SourceRef = 2, TargetRef = 3, Name = "Finish", RolesVariable = "actionRoles"
        });
        var instance = await StartAsync(await CreateAsync(model));
        var original = Assert.Single(await TasksAsync(instance.Id));
        Assert.Equal(new[] { "Finance", "Manager" }, original.Roles);
        var originalPolicyId = Assert.IsType<long>(original.RolePolicyId);

        using (var update = await SendAsync(HttpMethod.Patch, $"/api/instances/{instance.Id}/variables",
                   new UpdateInstanceVariablesRequest(
                   [new("taskRoles", JsonSerializer.SerializeToElement(new[] { "Legal" })),
                    new("actionRoles", JsonSerializer.SerializeToElement(new[] { "Legal" }))],
                       "Change sources while work waits", null)))
        {
            await ExpectAsync(update, HttpStatusCode.OK);
        }

        Assert.Single((await InboxAsync(instance.Id, "financer", "Finance")).Items);
        Assert.Empty((await InboxAsync(instance.Id, "lawyer", "Legal")).Items);
        using (var available = await SendAsync(HttpMethod.Get, $"/api/user-tasks/{original.Id}/flows", null,
                   "financer", "Finance", "Approver"))
        {
            await ExpectAsync(available, HttpStatusCode.OK);
            var flows = (await available.Content.ReadFromJsonAsync<SequenceFlowModel[]>(JsonOptions))!;
            Assert.Equal(2, flows.Length);
            Assert.All(flows, flow => Assert.Equal(new[] { "Approver" }, flow.Roles));
            Assert.All(flows, flow => Assert.Null(flow.RolesVariable));
        }
        using (var forbidden = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{original.Id}/flows/201",
                   new TakeFlowRequest(null), "financer", "Finance"))
            await ExpectAsync(forbidden, HttpStatusCode.BadRequest);

        using (var next = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{original.Id}/flows/201",
                   new TakeFlowRequest(null), "financer", "Finance", "Approver"))
            await ExpectAsync(next, HttpStatusCode.OK);

        var rows = await TasksAsync(instance.Id);
        var completed = Assert.Single(rows, task => task.Status == UserTaskStatuses.Completed);
        var current = Assert.Single(rows, task => task.Status == UserTaskStatuses.Active);
        Assert.Equal(originalPolicyId, completed.RolePolicyId);
        Assert.NotEqual(originalPolicyId, current.RolePolicyId);
        Assert.Equal(new[] { "Legal" }, current.Roles);
        Assert.Empty((await InboxAsync(instance.Id, "financer", "Finance", "Approver")).Items);
        Assert.True(Assert.Single((await InboxAsync(instance.Id, "lawyer", "Legal")).Items).CanAct);
    }

    [Fact]
    public async Task SharedRoleSourceFreezesWithoutPreventingFutureInstancesFromSeeingChanges()
    {
        var key = $"tests.roles.{Guid.NewGuid():N}";
        using var create = await SendAsync(HttpMethod.Post, "/api/shared-variables",
            new CreateSharedVariableRequest(key, WorkflowVariableTypes.String, true, false, true,
                JsonSerializer.SerializeToElement(new[] { "Finance" })));
        await ExpectAsync(create, HttpStatusCode.Created);
        var shared = (await create.Content.ReadFromJsonAsync<SharedVariableDto>(JsonOptions))!;
        var model = Model();
        var variable = model.Variables[0];
        variable.Scope = VariableScopes.Shared;
        variable.SharedKey = key;
        variable.Access = SharedVariableAccessModes.Read;
        variable.DefaultValue = null;
        variable.Nullable = false;
        var workflowId = await CreateAsync(model);
        var original = await StartAsync(workflowId);
        using (var update = await SendAsync(HttpMethod.Put, $"/api/shared-variables/{key}/value",
                   new UpdateSharedVariableRequest(JsonSerializer.SerializeToElement(new[] { "Legal" }), shared.Revision)))
            await ExpectAsync(update, HttpStatusCode.OK);
        var later = await StartAsync(workflowId);
        Assert.Equal(new[] { "Finance" }, Assert.Single(await TasksAsync(original.Id)).Roles);
        Assert.Equal(new[] { "Legal" }, Assert.Single(await TasksAsync(later.Id)).Roles);
        Assert.Single((await InboxAsync(original.Id, "financer", "Finance")).Items);
        Assert.Empty((await InboxAsync(original.Id, "lawyer", "Legal")).Items);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[\"   \"]")]
    public async Task InvalidRoleSourceRollsBackTaskCreation(string valueJson)
    {
        var model = Model();
        model.Variables[0].DefaultValue = JsonSerializer.Deserialize<JsonElement>(valueJson);
        model.Variables[0].Nullable = true;
        var workflowId = await CreateAsync(model);
        using var start = await SendAsync(HttpMethod.Post, "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null));
        await ExpectAsync(start, HttpStatusCode.BadRequest);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowInstances.AnyAsync(instance => instance.WorkflowDefinitionId == workflowId));
    }

    [Fact]
    public async Task MultiInstancePolicyIsSharedByChildrenAndParentInterruptUsesSavedRoles()
    {
        var model = Model();
        model.Variables.Add(new VariableModel { Id = 3, Name = "results", DataType = WorkflowVariableTypes.Json,
            DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) });
        var node = model.FlowNodes[1];
        node.MultiInstance = new MultiInstanceModel
        {
            Mode = MultiInstanceModes.Sequential, Source = MultiInstanceSources.Cardinality,
            CardinalityExpression = "3", ResultVariable = "results"
        };
        model.SequenceFlows[1].CompletionPriority = 1;
        model.SequenceFlows[1].CompletionCondition = "CountFlow(201) >= 3";
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 202, SourceRef = 2, TargetRef = 3, Name = "Interrupt",
            CancelRemainingInstances = true, RolesVariable = "actionRoles"
        });
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 203, SourceRef = 2, TargetRef = 3, Name = "Fallback",
            IsDefault = true, IsSelectable = false
        });
        var instance = await StartAsync(await CreateAsync(model));
        var tasks = await TasksAsync(instance.Id);
        Assert.Equal(3, tasks.Length);
        var policyId = Assert.IsType<long>(tasks[0].RolePolicyId);
        Assert.All(tasks, task => Assert.Equal(policyId, task.RolePolicyId));
        Assert.Equal(2, tasks.Count(task => task.Status == UserTaskStatuses.Pending));
        var executionId = tasks[0].MultiInstanceExecutionId!.Value;
        using (var policies = await SendAsync(HttpMethod.Get, $"/api/multi-instance-executions/{executionId}/roles"))
        {
            await ExpectAsync(policies, HttpStatusCode.OK);
            var policy = (await policies.Content.ReadFromJsonAsync<UserTaskRolePolicyDto>(JsonOptions))!;
            Assert.Equal(policyId, policy.RolePolicyId);
            Assert.Equal(2, policy.Flows.Count);
        }
        using (var change = await SendAsync(HttpMethod.Post, $"/api/multi-instance-executions/{executionId}/roles",
                   new ChangeUserTaskRolesRequest(policyId, ["Legal"],
                       [new(201, ["Legal"]), new(202, ["Legal"])], "Transfer approval")))
            await ExpectAsync(change, HttpStatusCode.OK);

        using (var denied = await SendAsync(HttpMethod.Post,
                   $"/api/multi-instance-executions/{executionId}/flows/202", new TakeFlowRequest(null),
                   "old-reviewer", "Finance", "Approver"))
            await ExpectAsync(denied, HttpStatusCode.BadRequest);
        using (var flows = await SendAsync(HttpMethod.Get,
                   $"/api/multi-instance-executions/{executionId}/flows", null, "lawyer", "Legal"))
        {
            await ExpectAsync(flows, HttpStatusCode.OK);
            var flow = Assert.Single((await flows.Content.ReadFromJsonAsync<SequenceFlowModel[]>(JsonOptions))!);
            Assert.Equal(new[] { "Legal" }, flow.Roles);
        }
        using (var interrupt = await SendAsync(HttpMethod.Post,
                   $"/api/multi-instance-executions/{executionId}/flows/202", new TakeFlowRequest(null),
                   "lawyer", "Legal"))
            await ExpectAsync(interrupt, HttpStatusCode.OK);
        Assert.All(await TasksAsync(instance.Id), task => Assert.Equal(UserTaskStatuses.Cancelled, task.Status));
    }

    private async Task<UserTaskEntity[]> TasksAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.AsNoTracking().Where(task => task.InstanceId == instanceId).OrderBy(task => task.Id).ToArrayAsync();
    }

    private async Task<PagedResult<InboxItemDto>> InboxAsync(long instanceId, string user, params string[] roles)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/inbox?instanceId={instanceId}", null, user, roles);
        await ExpectAsync(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PagedResult<InboxItemDto>>(JsonOptions))!;
    }

    private async Task<long> CreateAsync(WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        await ExpectAsync(response, HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!.Id;
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null));
        await ExpectAsync(response, HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null,
        string user = "role-manager", params string[] roles)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, user, roles.Length == 0 ? ["RoleManager", "admin"] : roles);
        return fixture.Client.SendAsync(request);
    }

    private static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode expected) =>
        Assert.True(response.StatusCode == expected, $"Expected {expected}; received {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static WorkflowModel Model() => new()
    {
        Id = $"tests-role-runtime-{Guid.NewGuid():N}", Name = "Saved role policy", InitialEventId = 1,
        TaskRoleManagementRoles = ["RoleManager"],
        Variables =
        [
            new VariableModel { Id = 1, Name = "taskRoles", DataType = WorkflowVariableTypes.String, IsArray = true,
                DefaultValue = JsonSerializer.SerializeToElement(new[] { " Finance ", "Manager", "finance" }) },
            new VariableModel { Id = 2, Name = "actionRoles", DataType = WorkflowVariableTypes.String, IsArray = true,
                DefaultValue = JsonSerializer.SerializeToElement(new[] { "Approver" }) }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask, RolesVariable = "taskRoles" },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Complete", RolesVariable = "actionRoles" }
        ]
    };
}
