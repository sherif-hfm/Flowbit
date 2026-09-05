using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class RolePolicyActivationTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PendingNormalRoleOverrideSurvivesDurableWorkerActivation()
    {
        var model = Model();
        model.FlowNodes[1].AsyncBefore = true;
        var instance = await StartAsync(model);
        var pending = Assert.Single(await TasksAsync(instance.Id));
        Assert.Equal(UserTaskStatuses.Pending, pending.Status);
        var changed = await ChangeAsync($"/api/user-tasks/{pending.Id}/roles", pending.RolePolicyId!.Value);
        long jobId;
        string queueClass;
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.AsNoTracking().SingleAsync(row =>
                row.InstanceId == instance.Id && row.Kind == WorkflowJobKinds.AsyncBefore);
            jobId = job.Id;
            queueClass = job.QueueClass;
            await db.WorkflowJobs.Where(row => row.Id == jobId).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Priority, 1_000_000).SetProperty(row => row.DueAt, DateTimeOffset.UtcNow));
        }
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var lease = Assert.Single(await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>()
                .LeaseRunnableAsync(new WorkflowJobLeaseRequest($"role-activation:{Guid.NewGuid():N}",
                    1, queueClass == WorkflowJobClasses.Activity ? 1 : 0, 4, TimeSpan.FromMinutes(1)), CancellationToken.None));
            Assert.Equal(jobId, lease.Job.Id);
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
        }
        var active = Assert.Single(await TasksAsync(instance.Id));
        Assert.Equal(UserTaskStatuses.Active, active.Status);
        Assert.Equal(pending.Id, active.Id);
        Assert.Equal(changed.Policy.RolePolicyId, active.RolePolicyId);
        Assert.Equal(new[] { "Legal" }, active.Roles);
        using var denied = await ActAsync(active.Id, "Worker", "Reviewer");
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        using var completed = await ActAsync(active.Id, "Legal", "LegalAction");
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    [Fact]
    public async Task SequentialChildrenKeepManualPolicyAcrossEveryNextChildActivation()
    {
        var model = Model();
        model.Variables.Add(new VariableModel { Id = 3, Name = "results", DataType = "json",
            DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) });
        model.FlowNodes[1].MultiInstance = new MultiInstanceModel
        {
            Mode = "sequential", Source = "cardinality", CardinalityExpression = "3",
            ResultVariable = "results", CompletionEvaluation = "afterAll"
        };
        model.SequenceFlows[1].CompletionCondition = "CountFlow(201) == 3";
        model.SequenceFlows[1].CompletionPriority = 1;
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 202, SourceRef = 2, TargetRef = 3, IsDefault = true, IsSelectable = false
        });
        var instance = await StartAsync(model);
        var tasks = await TasksAsync(instance.Id);
        var changed = await ChangeAsync($"/api/multi-instance-executions/{tasks[0].MultiInstanceExecutionId}/roles",
            tasks[0].RolePolicyId!.Value);
        for (var index = 0; index < 3; index++)
        {
            tasks = await TasksAsync(instance.Id);
            var active = Assert.Single(tasks, task => task.Status == UserTaskStatuses.Active);
            Assert.Equal(index, active.ItemIndex);
            Assert.Equal(changed.Policy.RolePolicyId, active.RolePolicyId);
            Assert.Equal(new[] { "Legal" }, active.Roles);
            using var denied = await ActAsync(active.Id, "Worker", "Reviewer");
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            using var completed = await ActAsync(active.Id, "Legal", "LegalAction");
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }
        Assert.All(await TasksAsync(instance.Id), task => Assert.Equal(UserTaskStatuses.Completed, task.Status));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("completed", await verify.WorkflowInstances.Where(row => row.Id == instance.Id)
            .Select(row => row.Status).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoleChangesSerializeWithCompletionAndCancellation(bool cancel)
    {
        var instance = await StartAsync(Model());
        var task = Assert.Single(await TasksAsync(instance.Id));
        var changeRequest = new ChangeUserTaskRolesRequest(task.RolePolicyId!.Value, ["Legal"],
            [new(201, ["LegalAction"])], "Concurrent transition");
        var responses = await Task.WhenAll(
            SendAsync(HttpMethod.Post, $"/api/user-tasks/{task.Id}/roles", changeRequest),
            cancel ? SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/cancel")
                : ActAsync(task.Id, "Worker", "Reviewer"));
        try
        {
            if (cancel)
            {
                Assert.Equal(HttpStatusCode.NoContent, responses[1].StatusCode);
                Assert.Contains(responses[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
                Assert.Equal(UserTaskStatuses.Cancelled, Assert.Single(await TasksAsync(instance.Id)).Status);
            }
            else
            {
                Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
                Assert.Contains(responses[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
                Assert.Contains(responses[1].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest });
                if (responses[1].StatusCode == HttpStatusCode.OK)
                    Assert.Equal(UserTaskStatuses.Completed, Assert.Single(await TasksAsync(instance.Id)).Status);
                else
                    Assert.Equal(new[] { "Legal" }, Assert.Single(await TasksAsync(instance.Id)).Roles);
            }
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    private async Task<UserTaskRolesChangeAckDto> ChangeAsync(string url, long policyId)
    {
        using var response = await SendAsync(HttpMethod.Post, url,
            new ChangeUserTaskRolesRequest(policyId, ["Legal"], [new(201, ["LegalAction"])], "Activation regression"));
        await ExpectAsync(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<UserTaskRolesChangeAckDto>(JsonOptions))!;
    }

    private Task<HttpResponseMessage> ActAsync(long taskId, params string[] roles) =>
        SendAsync(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/201", new TakeFlowRequest(null), roles);

    private async Task<UserTaskEntity[]> TasksAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.AsNoTracking().Where(task => task.InstanceId == instanceId)
            .OrderBy(task => task.Id).ToArrayAsync();
    }

    private async Task<InstanceDetailDto> StartAsync(WorkflowModel model)
    {
        using var created = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        await ExpectAsync(created, HttpStatusCode.Created);
        var workflow = (await created.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;
        using var started = await SendAsync(HttpMethod.Post, "/api/instances?detail=full", new StartInstanceRequest(workflow.Id, null, null, null));
        await ExpectAsync(started, HttpStatusCode.Created);
        return (await started.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string[]? roles = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, "test-actor", roles ?? ["RoleManager"]);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode expected) =>
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static WorkflowModel Model() => new()
    {
        Id = $"role-activation-{Guid.NewGuid():N}", Name = "Role activation", InitialEventId = 1,
        TaskRoleManagementRoles = ["RoleManager"],
        Variables =
        [
            new() { Id = 1, Name = "taskRoles", DataType = "string", IsArray = true, DefaultValue = JsonSerializer.SerializeToElement(new[] { "Worker" }) },
            new() { Id = 2, Name = "actionRoles", DataType = "string", IsArray = true, DefaultValue = JsonSerializer.SerializeToElement(new[] { "Reviewer" }) }
        ],
        FlowNodes =
        [
            new() { Id = 1, Name = "Start", Type = "startEvent" },
            new() { Id = 2, Name = "Review", Type = "userTask", RolesVariable = "taskRoles" },
            new() { Id = 3, Name = "Done", Type = "endEvent" }
        ],
        SequenceFlows =
        [
            new() { Id = 101, SourceRef = 1, TargetRef = 2 },
            new() { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Complete", RolesVariable = "actionRoles" }
        ]
    };
}
