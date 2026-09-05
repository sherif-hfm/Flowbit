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
public sealed class RolePolicyVersionSwitchApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompatibleSwitchEnablesRoleManagementAndPreservesManuallyChangedPolicy(bool multiInstance)
    {
        var sourceModel = Model(multiInstance);
        var source = await CreateAsync(sourceModel);
        using var start = await SendAsync(HttpMethod.Post, "/api/instances?detail=full",
            new StartInstanceRequest(source.Id, null, null, null));
        await ExpectAsync(start, HttpStatusCode.Created);
        var instance = (await start.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
        var originalTasks = await TasksAsync(instance.Id);
        var activeTask = Assert.Single(originalTasks, task => task.Status == UserTaskStatuses.Active);
        var originalPolicyId = Assert.IsType<long>(activeTask.RolePolicyId);
        var executionId = activeTask.MultiInstanceExecutionId;
        var roleUrl = multiInstance
            ? $"/api/multi-instance-executions/{executionId}/roles"
            : $"/api/user-tasks/{activeTask.Id}/roles";

        // No management authority exists in the original immutable version.
        using (var denied = await SendAsync(HttpMethod.Get, roleUrl, roles: ["RoleManager"]))
            await ExpectAsync(denied, HttpStatusCode.Forbidden);

        var enabledModel = Clone(sourceModel);
        enabledModel.TaskRoleManagementRoles = ["RoleManager"];
        var enabled = await CreateAsync(enabledModel);
        await SwitchAsync(instance.Id, enabled.Id);
        var enabledPolicy = await PolicyAsync(roleUrl);
        Assert.Equal(originalPolicyId, enabledPolicy.RolePolicyId);
        Assert.Equal(["Finance"], enabledPolicy.Roles);
        Assert.All(enabledPolicy.Flows, flow => Assert.Equal(["Approver"], flow.Roles));

        using var change = await SendAsync(HttpMethod.Post, roleUrl,
            new ChangeUserTaskRolesRequest(enabledPolicy.RolePolicyId, ["Legal"],
                enabledPolicy.Flows.Select(flow => new ChangeUserTaskFlowRolesRequest(flow.FlowId, ["LegalAction"])).ToArray(),
                "Retain adjusted permissions across the next version switch"),
            roles: ["RoleManager"]);
        await ExpectAsync(change, HttpStatusCode.OK);
        var replacement = (await change.Content.ReadFromJsonAsync<UserTaskRolesChangeAckDto>(JsonOptions))!;
        Assert.True(replacement.Changed);
        Assert.NotEqual(originalPolicyId, replacement.Policy.RolePolicyId);

        var targetModel = Clone(enabledModel);
        targetModel.Name += " (renamed)";
        targetModel.FlowNodes[1].Name = "Renamed waiting approval";
        var target = await CreateAsync(targetModel);
        await SwitchAsync(instance.Id, target.Id);

        var switched = await PolicyAsync(roleUrl);
        Assert.Equal(replacement.Policy.RolePolicyId, switched.RolePolicyId);
        Assert.Equal(["Legal"], switched.Roles);
        Assert.All(switched.Flows, flow => Assert.Equal(["LegalAction"], flow.Roles));
        var currentTasks = await TasksAsync(instance.Id);
        Assert.Equal(multiInstance ? 3 : 1, currentTasks.Length);
        Assert.All(currentTasks, task =>
        {
            Assert.Equal(replacement.Policy.RolePolicyId, task.RolePolicyId);
            Assert.Equal(["Legal"], task.Roles);
        });
        if (multiInstance)
            Assert.Equal(2, currentTasks.Count(task => task.Status == UserTaskStatuses.Pending));

        await using (var db = fixture.CreateDbContext())
        {
            var policies = await db.UserTaskRolePolicies.AsNoTracking()
                .Where(policy => policy.InstanceId == instance.Id).OrderBy(policy => policy.Id).ToArrayAsync();
            Assert.Equal(2, policies.Length); // Neither version switch creates or rewrites a policy.
            Assert.Equal(source.Id, policies[0].WorkflowDefinitionId);
            Assert.Equal(["Finance"], policies[0].Roles);
            Assert.Equal(enabled.Id, policies[1].WorkflowDefinitionId);
            Assert.Equal(["Legal"], policies[1].Roles);
            if (multiInstance)
            {
                var execution = await db.MultiInstanceExecutions.AsNoTracking()
                    .SingleAsync(row => row.Id == executionId);
                Assert.Equal(replacement.Policy.RolePolicyId, execution.RolePolicyId);
            }
            var visits = await db.NodeExecutions.AsNoTracking()
                .Where(visit => visit.InstanceId == instance.Id && visit.NodeId == 2).ToArrayAsync();
            Assert.NotEmpty(visits);
            Assert.All(visits, visit => Assert.Equal("Finance", visit.NodeRolesJson!.RootElement[0].GetString()));
        }

        Assert.Empty((await InboxAsync(instance.Id, ["Finance", "Approver"])).Items);
        Assert.True(Assert.Single((await InboxAsync(instance.Id, ["Legal", "LegalAction"])).Items).CanAct);
        using (var available = await SendAsync(HttpMethod.Get, $"/api/user-tasks/{activeTask.Id}/flows",
                   roles: ["Legal", "LegalAction"]))
        {
            await ExpectAsync(available, HttpStatusCode.OK);
            var flows = (await available.Content.ReadFromJsonAsync<SequenceFlowModel[]>(JsonOptions))!;
            Assert.NotEmpty(flows);
            Assert.All(flows, flow =>
            {
                Assert.Equal(["LegalAction"], flow.Roles);
                Assert.Null(flow.RolesVariable);
            });
        }

        // Exercise the saved action policy after switching and close the test work.
        var actionUrl = multiInstance
            ? $"/api/multi-instance-executions/{executionId}/flows/202"
            : $"/api/user-tasks/{activeTask.Id}/flows/201";
        using var complete = await SendAsync(HttpMethod.Post, actionUrl, new TakeFlowRequest(null),
            roles: ["Legal", "LegalAction"]);
        await ExpectAsync(complete, HttpStatusCode.OK);
    }

    private async Task SwitchAsync(long instanceId, long targetId)
    {
        using var previewResponse = await SendAsync(HttpMethod.Post,
            $"/api/instances/{instanceId}/version-change/preview", new PreviewInstanceVersionChangeRequest(targetId));
        await ExpectAsync(previewResponse, HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<InstanceVersionChangePreviewDto>(JsonOptions))!;
        Assert.True(preview.Compatible, JsonSerializer.Serialize(preview.Blockers));
        using var change = await SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/version-change",
            new ChangeInstanceVersionRequest(targetId, preview.ExpectedSourceWorkflowId, preview.ExpectedUpdatedAt,
                "Verify waiting-task policy compatibility"));
        await ExpectAsync(change, HttpStatusCode.OK);
        var result = (await change.Content.ReadFromJsonAsync<ChangeInstanceVersionResultDto>(JsonOptions))!;
        Assert.Equal(targetId, result.Instance.Workflow.Id);
    }

    private async Task<WorkflowDetailDto> CreateAsync(WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        await ExpectAsync(response, HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;
    }

    private async Task<UserTaskRolePolicyDto> PolicyAsync(string url)
    {
        using var response = await SendAsync(HttpMethod.Get, url, roles: ["RoleManager"]);
        await ExpectAsync(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<UserTaskRolePolicyDto>(JsonOptions))!;
    }

    private async Task<UserTaskEntity[]> TasksAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.AsNoTracking().Where(task => task.InstanceId == instanceId).OrderBy(task => task.Id).ToArrayAsync();
    }

    private async Task<PagedResult<InboxItemDto>> InboxAsync(long instanceId, string[] roles)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/inbox?instanceId={instanceId}", roles: roles);
        await ExpectAsync(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PagedResult<InboxItemDto>>(JsonOptions))!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string[]? roles = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, "role-policy-version-switch", roles ?? ["admin"]);
        return fixture.Client.SendAsync(request);
    }

    private static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode expected) =>
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static WorkflowModel Clone(WorkflowModel model) =>
        JsonSerializer.Deserialize<WorkflowModel>(JsonSerializer.Serialize(model))!;

    private static WorkflowModel Model(bool multiInstance)
    {
        var model = new WorkflowModel
        {
            Id = $"role-policy-version-{Guid.NewGuid():N}", Name = "Role policy version compatibility", InitialEventId = 1,
            Variables =
            [
                new VariableModel { Id = 1, Name = "taskRoles", DataType = "string", IsArray = true,
                    DefaultValue = JsonSerializer.SerializeToElement(new[] { "Finance" }) },
                new VariableModel { Id = 2, Name = "actionRoles", DataType = "string", IsArray = true,
                    DefaultValue = JsonSerializer.SerializeToElement(new[] { "Approver" }) }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = "startEvent" },
                new FlowNodeModel { Id = 2, Name = "Review", Type = "userTask", RolesVariable = "taskRoles" },
                new FlowNodeModel { Id = 3, Name = "Done", Type = "endEvent" }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Approve", RolesVariable = "actionRoles" }
            ]
        };
        if (!multiInstance) return model;
        model.Variables.Add(new VariableModel { Id = 3, Name = "results", DataType = "json",
            DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) });
        model.FlowNodes[1].MultiInstance = new MultiInstanceModel
        {
            Mode = "sequential", Source = "cardinality", CardinalityExpression = "3", ResultVariable = "results"
        };
        model.SequenceFlows[1].CompletionCondition = "CountFlow(201) >= 3";
        model.SequenceFlows[1].CompletionPriority = 1;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 3, Name = "Stop", RolesVariable = "actionRoles", CancelRemainingInstances = true });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 203, SourceRef = 2, TargetRef = 3, IsDefault = true, IsSelectable = false });
        return model;
    }
}
