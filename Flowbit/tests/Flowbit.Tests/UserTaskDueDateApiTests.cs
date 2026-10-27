using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class UserTaskDueDateApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task WorkItemsSaveTheirDueDateAndPriorityAndEveryTaskResponseReturnsThem()
    {
        var suffix = Guid.NewGuid().ToString("N");
        // Whole seconds, because the value is sent as text without fractions.
        var inThreeHours = DateTimeOffset.UtcNow.AddHours(3);
        var contractEnd = new DateTimeOffset(
                inThreeHours.Ticks - inThreeHours.Ticks % TimeSpan.TicksPerSecond,
                TimeSpan.Zero)
            .ToOffset(TimeSpan.FromHours(2));
        var workflowId = await CreateWorkflowAsync(ReviewModel(suffix));

        var instanceId = await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["amount"] = JsonSerializer.SerializeToElement(20000),
            ["contractEnd"] = JsonSerializer.SerializeToElement(
                contractEnd.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture))
        });

        var inboxItem = Single((await GetJsonAsync(
            $"/api/instances/inbox?instanceId={instanceId}", "reviewer", "Reviewer"))["items"]!);
        var taskId = inboxItem["userTaskId"]!.GetValue<long>();
        var detail = await GetJsonAsync($"/api/user-tasks/{taskId}", "reviewer", "Reviewer");
        var createdAt = detail["createdAt"]!.GetValue<DateTimeOffset>();
        var dueAt = detail["dueAt"]!.GetValue<DateTimeOffset>();

        // A duration counts exactly from the work item's creation time.
        Assert.Equal(createdAt.AddHours(4), dueAt);
        Assert.Equal(80, detail["priority"]!.GetValue<int>());
        Assert.Equal(UserTaskDueStatuses.DueSoon, detail["dueStatus"]!.GetValue<string>());
        Assert.Equal(dueAt, inboxItem["dueAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(80, inboxItem["priority"]!.GetValue<int>());
        Assert.Equal(UserTaskDueStatuses.DueSoon, inboxItem["dueStatus"]!.GetValue<string>());

        var managed = Single((await GetJsonAsync(
            $"/api/user-tasks/manage?instanceId={instanceId}", "manager", "Manager"))["items"]!);
        Assert.Equal(dueAt, managed["dueAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(80, managed["priority"]!.GetValue<int>());
        Assert.Equal(UserTaskDueStatuses.DueSoon, managed["dueStatus"]!.GetValue<string>());

        using (var take = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{taskId}/flows/201",
                   new TakeFlowRequest(null),
                   "reviewer",
                   "Reviewer"))
        {
            Assert.Equal(HttpStatusCode.OK, take.StatusCode);
        }

        // The next task's due date comes from a datetime variable with an offset.
        var countersign = Single((await GetJsonAsync(
            $"/api/instances/inbox?instanceId={instanceId}", "signatory", "Signatory"))["items"]!);
        Assert.Equal(
            contractEnd.ToUniversalTime(),
            countersign["dueAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(60, countersign["priority"]!.GetValue<int>());

        // Completed work keeps its saved values but has no due status.
        var history = await GetJsonAsync($"/api/instances/{instanceId}/user-tasks", "reviewer", "Reviewer");
        var completed = history["items"]!.AsArray().Single(item => item!["id"]!.GetValue<long>() == taskId)!;
        Assert.Equal("completed", completed["status"]!.GetValue<string>());
        Assert.Equal(dueAt, completed["dueAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(80, completed["priority"]!.GetValue<int>());
        Assert.False(completed.AsObject().ContainsKey("dueStatus"));
    }

    [Fact]
    public async Task ClaimUnclaimAndDistributionResponsesReturnTheSavedValues()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var clientId = $"due-distributor-{suffix}";
        var clientSecret = $"due-secret-{suffix}";
        var model = ReviewModel(suffix);
        model.FlowNodes.Single(node => node.Id == 2).RequiresClaim = true;
        model.TaskDistribution = new TaskDistributionModel { ClientId = clientId, ClientSecret = clientSecret };
        var workflowId = await CreateWorkflowAsync(model);
        var instanceId = await StartAsync(workflowId, new Dictionary<string, JsonElement>
        {
            ["amount"] = JsonSerializer.SerializeToElement(20000),
            ["contractEnd"] = JsonSerializer.SerializeToElement("2030-01-31T17:00:00Z")
        });
        var taskId = Single((await GetJsonAsync(
            $"/api/instances/inbox?instanceId={instanceId}", "reviewer", "Reviewer"))["items"]!)["userTaskId"]!.GetValue<long>();
        var dueAt = (await GetJsonAsync($"/api/user-tasks/{taskId}", "reviewer", "Reviewer"))["dueAt"]!
            .GetValue<DateTimeOffset>();

        foreach (var action in new[] { "claim", "unclaim" })
        {
            using var response = await SendAsync(
                HttpMethod.Post, $"/api/user-tasks/{taskId}/{action}", null, "reviewer", "Reviewer");
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            AssertDueValues(await ReadJsonAsync(response), dueAt, 80, UserTaskDueStatuses.DueSoon);
        }

        using (var listed = await SendDistributorAsync(
                   HttpMethod.Get,
                   $"/api/task-distribution/workflows/{model.Id}/tasks?taskId={taskId}",
                   clientId,
                   clientSecret))
        {
            Assert.True(listed.StatusCode == HttpStatusCode.OK, await listed.Content.ReadAsStringAsync());
            AssertDueValues(Single((await ReadJsonAsync(listed))["items"]!), dueAt, 80, UserTaskDueStatuses.DueSoon);
        }

        using (var searched = await SendDistributorAsync(
                   HttpMethod.Post,
                   $"/api/task-distribution/workflows/{model.Id}/tasks/search",
                   clientId,
                   clientSecret,
                   new DistributableUserTaskSearchRequest { TaskId = taskId }))
        {
            Assert.True(searched.StatusCode == HttpStatusCode.OK, await searched.Content.ReadAsStringAsync());
            AssertDueValues(Single((await ReadJsonAsync(searched))["items"]!), dueAt, 80, UserTaskDueStatuses.DueSoon);
        }
    }

    [Fact]
    public async Task VersionChangeKeepsSavedValuesAndLaterWorkItemsUseTheTargetVersion()
    {
        var model = ReviewModel(Guid.NewGuid().ToString("N"));
        var review = model.FlowNodes.Single(node => node.Id == 2);
        review.DueDateExpression = "P2D";
        review.PriorityExpression = "40";
        var sourceId = await CreateWorkflowAsync(model);
        var instanceId = await StartAsync(sourceId, StartValues());
        var taskId = Single((await GetJsonAsync(
            $"/api/instances/inbox?instanceId={instanceId}", "reviewer", "Reviewer"))["items"]!)["userTaskId"]!.GetValue<long>();
        var before = await GetJsonAsync($"/api/user-tasks/{taskId}", "reviewer", "Reviewer");

        // The target changes only due dates and priorities, so the switch is allowed.
        review.DueDateExpression = "P5D";
        review.PriorityExpression = "90";
        var countersign = model.FlowNodes.Single(node => node.Id == 3);
        countersign.DueDateExpression = "PT1H";
        countersign.PriorityExpression = "10";
        var targetId = await CreateWorkflowVersionAsync(sourceId, model);
        await ChangeVersionAsync(instanceId, targetId);

        // The open work item keeps the values saved under the source version.
        var after = await GetJsonAsync($"/api/user-tasks/{taskId}", "reviewer", "Reviewer");
        Assert.Equal(before["dueAt"]!.GetValue<DateTimeOffset>(), after["dueAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(40, after["priority"]!.GetValue<int>());

        // Work items created after the switch use the target version's values.
        using (var take = await SendAsync(
                   HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/201", new TakeFlowRequest(null), "reviewer", "Reviewer"))
        {
            Assert.True(take.StatusCode == HttpStatusCode.OK, await take.Content.ReadAsStringAsync());
        }
        var next = Single((await GetJsonAsync(
            $"/api/instances/inbox?instanceId={instanceId}", "signatory", "Signatory"))["items"]!);
        Assert.Equal(
            next["taskCreatedAt"]!.GetValue<DateTimeOffset>().AddHours(1),
            next["dueAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(10, next["priority"]!.GetValue<int>());
    }

    [Fact]
    public async Task DueSoonThresholdIsReadOnlyWhenAResponseOrFilterNeedsIt()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var unscheduledModel = FilterModel($"due-queries-none-{suffix}");
        var unscheduledTask = unscheduledModel.FlowNodes.Single(node => node.Id == 2);
        unscheduledTask.DueDateExpression = null;
        unscheduledTask.PriorityExpression = null;
        unscheduledModel.TaskAssignmentRoles = ["Manager"];
        var scheduledModel = FilterModel($"due-queries-set-{suffix}");
        scheduledModel.TaskAssignmentRoles = ["Manager"];
        await StartAsync(await CreateWorkflowAsync(unscheduledModel), FilterValues(null, null));
        await StartAsync(await CreateWorkflowAsync(scheduledModel), FilterValues("PT2H", 60));
        var unscheduledInbox = $"/api/instances/inbox?workflowKey={unscheduledModel.Id}";
        var scheduledInbox = $"/api/instances/inbox?workflowKey={scheduledModel.Id}";
        var unscheduledTaskId = Single((await GetJsonAsync(unscheduledInbox, "worker", "Worker"))["items"]!)["userTaskId"]!
            .GetValue<long>();
        var scheduledTaskId = Single((await GetJsonAsync(scheduledInbox, "worker", "Worker"))["items"]!)["userTaskId"]!
            .GetValue<long>();

        // An inbox page without due dates never reads the setting; one with
        // due dates reads it once, which is its only extra query.
        var unscheduled = await MeasureAsync(() => GetJsonAsync(unscheduledInbox, "worker", "Worker"));
        var scheduled = await MeasureAsync(() => GetJsonAsync(scheduledInbox, "worker", "Worker"));
        Assert.Equal(0, unscheduled.ThresholdReads);
        Assert.Equal(1, scheduled.ThresholdReads);
        Assert.Equal(unscheduled.Commands + 1, scheduled.Commands);

        // A dueSoon filter reads it once and the response reuses that value; an
        // overdue or none filter does not need it unless a returned task has a due time.
        Assert.Equal(1, (await MeasureAsync(() => GetJsonAsync(scheduledInbox + "&dueStatus=dueSoon", "worker", "Worker"))).ThresholdReads);
        Assert.Equal(0, (await MeasureAsync(() => GetJsonAsync(scheduledInbox + "&dueStatus=overdue", "worker", "Worker"))).ThresholdReads);
        Assert.Equal(0, (await MeasureAsync(() => GetJsonAsync(unscheduledInbox + "&dueStatus=none", "worker", "Worker"))).ThresholdReads);

        // Task detail and task management add the read only for a task with a due time.
        var unscheduledDetail = await MeasureAsync(() => GetJsonAsync($"/api/user-tasks/{unscheduledTaskId}", "worker", "Worker"));
        var scheduledDetail = await MeasureAsync(() => GetJsonAsync($"/api/user-tasks/{scheduledTaskId}", "worker", "Worker"));
        Assert.Equal(unscheduledDetail.ThresholdReads + 1, scheduledDetail.ThresholdReads);
        Assert.Equal(unscheduledDetail.Commands + 1, scheduledDetail.Commands);
        var unscheduledManaged = await MeasureAsync(() => GetJsonAsync(
            $"/api/user-tasks/manage?workflowKey={unscheduledModel.Id}", "manager", "Manager"));
        var scheduledManaged = await MeasureAsync(() => GetJsonAsync(
            $"/api/user-tasks/manage?workflowKey={scheduledModel.Id}", "manager", "Manager"));
        Assert.Equal(unscheduledManaged.ThresholdReads + 1, scheduledManaged.ThresholdReads);
        Assert.Equal(unscheduledManaged.Commands + 1, scheduledManaged.Commands);
    }

    [Fact]
    public async Task ResultsThatCannotBeUsedCreateTheWorkItemWithoutValues()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = ReviewModel(suffix);
        var review = model.FlowNodes.Single(node => node.Id == 2);
        review.DueDateExpression = "deadline";
        review.PriorityExpression = "'high'";
        model.Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "deadline",
                DataType = WorkflowVariableTypes.DateTime,
                Nullable = true
            }
        ];
        var workflowId = await CreateWorkflowAsync(model);

        var instanceId = await StartAsync(workflowId, StartValues());

        var item = Single((await GetJsonAsync(
            $"/api/instances/inbox?instanceId={instanceId}", "reviewer", "Reviewer"))["items"]!).AsObject();
        Assert.False(item.ContainsKey("dueAt"));
        Assert.False(item.ContainsKey("priority"));
        Assert.False(item.ContainsKey("dueStatus"));
    }

    [Theory]
    [InlineData(MultiInstanceModes.Parallel)]
    [InlineData(MultiInstanceModes.Sequential)]
    public async Task MultiInstanceChildrenGetTheValuesOfTheirExecution(string mode)
    {
        var model = MultiInstanceModel(Guid.NewGuid().ToString("N"));
        model.FlowNodes.Single(node => node.Id == 2).MultiInstance!.Mode = mode;
        var workflowId = await CreateWorkflowAsync(model);
        var instanceId = await StartAsync(workflowId, null, "starter", "Requester");
        var executionId = await EnterVotingAsync(instanceId, ["alice", "bob", "carol"]);

        var rows = await ReadChildrenAsync(executionId);

        Assert.Equal(3, rows.Count);
        Assert.Equal(mode == MultiInstanceModes.Parallel ? 3 : 1, rows.Count(row => row.Status == "active"));
        Assert.All(rows, row =>
        {
            Assert.Equal(rows[0].CreatedAt.AddHours(6), row.DueAt);
            Assert.Equal(70, row.Priority);
        });
    }

    [Fact]
    public async Task InboxSortsAndFiltersByDueDateAndPriority()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"due-filters-{suffix}";
        var workflowId = await CreateWorkflowAsync(FilterModel(workflowKey));
        var overdue = await StartAsync(workflowId, FilterValues(
            DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture), 90));
        var dueSoon = await StartAsync(workflowId, FilterValues("PT2H", 60));
        var upcoming = await StartAsync(workflowId, FilterValues("P3D", 20));
        var unscheduled = await StartAsync(workflowId, FilterValues(null, null));
        var path = $"/api/instances/inbox?workflowKey={workflowKey}";

        var all = (await GetJsonAsync(path, "worker", "Worker"))["items"]!.AsArray();
        Assert.Equal(UserTaskDueStatuses.Overdue, StatusOf(all, overdue));
        Assert.Equal(UserTaskDueStatuses.DueSoon, StatusOf(all, dueSoon));
        Assert.Equal(UserTaskDueStatuses.Upcoming, StatusOf(all, upcoming));
        Assert.Null(StatusOf(all, unscheduled));

        Assert.Equal([overdue, dueSoon, upcoming, unscheduled], await InstanceIdsAsync(path + "&sort=dueAt:asc"));
        Assert.Equal([upcoming, dueSoon, overdue, unscheduled], await InstanceIdsAsync(path + "&sort=dueAt:desc"));
        Assert.Equal([overdue, dueSoon, upcoming, unscheduled], await InstanceIdsAsync(path + "&sort=priority:desc"));
        Assert.Equal([upcoming, dueSoon, overdue, unscheduled], await InstanceIdsAsync(path + "&sort=priority:asc"));

        Assert.Equal([overdue], await InstanceIdsAsync(path + "&dueStatus=overdue"));
        Assert.Equal([dueSoon], await InstanceIdsAsync(path + "&dueStatus=DUESOON"));
        Assert.Equal([upcoming], await InstanceIdsAsync(path + "&dueStatus=upcoming"));
        Assert.Equal([unscheduled], await InstanceIdsAsync(path + "&dueStatus=none"));
        Assert.Equal(
            [overdue, unscheduled],
            await InstanceIdsAsync(path + "&dueStatus=overdue&dueStatus=none&sort=dueAt:asc"));
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).ToString("O", CultureInfo.InvariantCulture));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture));
        Assert.Equal([dueSoon], await InstanceIdsAsync(path + $"&dueFrom={from}&dueTo={to}"));
        Assert.Equal([overdue, dueSoon], await InstanceIdsAsync(path + "&minPriority=50&sort=priority:desc"));
        Assert.Equal([upcoming], await InstanceIdsAsync(path + "&maxPriority=50"));
        Assert.Equal([dueSoon], await InstanceIdsAsync(path + "&minPriority=50&maxPriority=70"));

        using (var search = await SendAsync(
                   HttpMethod.Post,
                   "/api/instances/inbox/search",
                   new InboxSearchRequest
                   {
                       WorkflowKey = workflowKey,
                       DueStatuses = ["overdue", "none"],
                       MaxPriority = 95,
                       Sort = [new SearchSortDto("priority", "desc")]
                   },
                   "worker",
                   "Worker"))
        {
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            var body = (await search.Content.ReadFromJsonAsync<PagedResult<InboxItemDto>>(JsonOptions))!;
            Assert.Equal([overdue], body.Items.Select(item => item.InstanceId));
            Assert.Equal(1, body.TotalCount);
        }

        foreach (var invalid in new[]
                 {
                     "&dueStatus=late",
                     $"&dueFrom={to}&dueTo={to}",
                     "&minPriority=5&maxPriority=1",
                     "&sort=due:asc"
                 })
        {
            using var response = await SendAsync(HttpMethod.Get, path + invalid, null, "worker", "Worker");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task DueSoonThresholdComesFromTheEngineSetting()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"due-threshold-{suffix}";
        var workflowId = await CreateWorkflowAsync(FilterModel(workflowKey));
        var upcoming = await StartAsync(workflowId, FilterValues("P3D", 20));
        var path = $"/api/instances/inbox?workflowKey={workflowKey}";

        Assert.Equal([upcoming], await InstanceIdsAsync(path + "&dueStatus=upcoming"));
        await SetThresholdAsync("P5D");
        try
        {
            var item = Single((await GetJsonAsync(path, "worker", "Worker"))["items"]!);
            Assert.Equal(UserTaskDueStatuses.DueSoon, item["dueStatus"]!.GetValue<string>());
            Assert.Equal([upcoming], await InstanceIdsAsync(path + "&dueStatus=dueSoon"));
            Assert.Empty(await InstanceIdsAsync(path + "&dueStatus=upcoming"));

            await SetThresholdAsync("not a duration");
            item = Single((await GetJsonAsync(path, "worker", "Worker"))["items"]!);
            Assert.Equal(UserTaskDueStatuses.Upcoming, item["dueStatus"]!.GetValue<string>());
        }
        finally
        {
            await SetThresholdAsync("P1D");
        }
    }

    [Fact]
    public async Task FiltersKeepOrDropAWholeOnePerActorExecution()
    {
        var model = MultiInstanceModel(Guid.NewGuid().ToString("N"));
        var task = model.FlowNodes.Single(node => node.Id == 2);
        task.MultiInstance = new MultiInstanceModel
        {
            Mode = MultiInstanceModes.Parallel,
            Source = MultiInstanceSources.Cardinality,
            CardinalityExpression = "3",
            OnePerActor = true,
            ResultVariable = "voteResults"
        };
        model.SequenceFlows.Single(flow => flow.Id == 204).Variables = [];
        var workflowId = await CreateWorkflowAsync(model);
        var instanceId = await StartAsync(workflowId, null, "starter", "Requester");
        await EnterVotingAsync(instanceId, null);
        var path = $"/api/instances/inbox?instanceId={instanceId}";

        var representative = Single((await GetJsonAsync(path + "&dueStatus=dueSoon&minPriority=70", "alice", "User"))["items"]!);
        Assert.Equal(70, representative["priority"]!.GetValue<int>());
        Assert.Empty((await GetJsonAsync(path + "&dueStatus=overdue", "alice", "User"))["items"]!.AsArray());
        Assert.Empty((await GetJsonAsync(path + "&maxPriority=69", "alice", "User"))["items"]!.AsArray());
    }

    [Fact]
    public async Task PendingAsyncBeforeTaskCountsItsDurationFromEntryAndKeepsItOnActivation()
    {
        var model = ReviewModel(Guid.NewGuid().ToString("N"));
        var review = model.FlowNodes.Single(node => node.Id == 2);
        review.AsyncBefore = true;
        review.DueDateExpression = "PT6H";
        review.PriorityExpression = "70";
        model.TaskRoleManagementRoles = ["RoleManager"];
        model.CancelRoles = ["admin"];
        var workflowId = await CreateWorkflowAsync(model);
        var instanceId = await StartAsync(workflowId, StartValues());
        try
        {
            // The pending work item is created on entry, so the duration counts from then.
            var pending = Assert.Single(await ReadTasksAsync(instanceId));
            Assert.Equal(UserTaskStatuses.Pending, pending.Status);
            Assert.Equal(pending.CreatedAt.AddHours(6), pending.DueAt);
            Assert.Equal(70, pending.Priority);
            var managed = Single((await GetJsonAsync(
                $"/api/user-tasks/manage?instanceId={instanceId}&status=pending", "role-manager", "RoleManager"))["items"]!);
            Assert.Equal("pending", managed["status"]!.GetValue<string>());
            Assert.Equal(pending.DueAt, managed["dueAt"]!.GetValue<DateTimeOffset>());
            Assert.Equal(UserTaskDueStatuses.DueSoon, managed["dueStatus"]!.GetValue<string>());

            await RunAsyncBeforeJobAsync(instanceId);

            // Activation keeps the saved values instead of resolving them again.
            var active = Assert.Single(await ReadTasksAsync(instanceId));
            Assert.Equal(pending.Id, active.Id);
            Assert.Equal(UserTaskStatuses.Active, active.Status);
            Assert.Equal(pending.DueAt, active.DueAt);
            Assert.Equal(70, active.Priority);
            var inboxItem = Single((await GetJsonAsync(
                $"/api/instances/inbox?instanceId={instanceId}", "reviewer", "Reviewer"))["items"]!);
            Assert.Equal(pending.DueAt, inboxItem["dueAt"]!.GetValue<DateTimeOffset>());
        }
        finally
        {
            // Never leave a runnable job in the shared fixture's queue.
            using var cancel = await SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/cancel", null, "operator", "admin");
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }
    }

    [Fact]
    public async Task ReactivationEvaluatesTheDueDateAgain()
    {
        var model = ReviewModel(Guid.NewGuid().ToString("N"));
        model.FlowNodes.Single(node => node.Id == 2).DueDateExpression = "P2D";
        model.CancelRoles = ["admin"];
        var workflowId = await CreateWorkflowAsync(model);
        var instanceId = await StartAsync(workflowId, StartValues());
        var first = await GetJsonAsync(
            $"/api/user-tasks/{Single((await GetJsonAsync($"/api/instances/inbox?instanceId={instanceId}", "reviewer", "Reviewer"))["items"]!)["userTaskId"]!.GetValue<long>()}",
            "reviewer",
            "Reviewer");
        using (var cancel = await SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/cancel", null, "operator", "admin"))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }
        var preview = await GetJsonAsync($"/api/instances/{instanceId}/reactivation", "operator", "admin");
        Assert.True(preview["canReactivate"]!.GetValue<bool>(), preview.ToJsonString());

        using (var reactivate = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{instanceId}/reactivation",
                   new ReactivateInstanceRequest(
                       2,
                       workflowId,
                       preview["expectedUpdatedAt"]!.GetValue<DateTimeOffset>(),
                       "Reopen the review"),
                   "operator",
                   "admin"))
        {
            Assert.Equal(HttpStatusCode.OK, reactivate.StatusCode);
        }

        var reopened = Single((await GetJsonAsync(
            $"/api/instances/inbox?instanceId={instanceId}", "reviewer", "Reviewer"))["items"]!);
        Assert.NotEqual(first["id"]!.GetValue<long>(), reopened["userTaskId"]!.GetValue<long>());
        Assert.Equal(
            reopened["taskCreatedAt"]!.GetValue<DateTimeOffset>().AddDays(2),
            reopened["dueAt"]!.GetValue<DateTimeOffset>());
        Assert.True(reopened["dueAt"]!.GetValue<DateTimeOffset>() > first["dueAt"]!.GetValue<DateTimeOffset>());
    }

    private static WorkflowModel ReviewModel(string suffix) => new()
    {
        Id = $"due-date-api-{suffix}",
        Name = $"Due date API {suffix}",
        InitialEventId = 1,
        TaskAssignmentRoles = ["Manager"],
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Roles = ["Requester"],
                Variables =
                [
                    new VariableModel { Id = 1, Name = "amount", DataType = WorkflowVariableTypes.Number, Required = true },
                    new VariableModel { Id = 2, Name = "contractEnd", DataType = WorkflowVariableTypes.DateTime, Required = true }
                ]
            },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Review purchase",
                Type = BpmnFlowNodeTypes.UserTask,
                Roles = ["Reviewer"],
                DueDateExpression = "if(amount >= 10000, 'PT4H', 'P2D')",
                PriorityExpression = "if(amount >= 10000, 80, 40)"
            },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Countersign contract",
                Type = BpmnFlowNodeTypes.UserTask,
                Roles = ["Signatory"],
                DueDateExpression = "contractEnd",
                PriorityExpression = "60"
            },
            new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Approve", SourceRef = 2, TargetRef = 3, Roles = ["Reviewer"] },
            new SequenceFlowModel { Id = 301, Name = "Sign", SourceRef = 3, TargetRef = 4, Roles = ["Signatory"] }
        ]
    };

    private static WorkflowModel MultiInstanceModel(string suffix)
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        model.Id = $"due-date-mi-{suffix}";
        model.Name = $"Due date MI {suffix}";
        var task = model.FlowNodes.Single(node => node.Id == 2);
        task.DueDateExpression = "PT6H";
        task.PriorityExpression = "70";
        return model;
    }

    private static WorkflowModel FilterModel(string workflowKey) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Variables =
                [
                    new VariableModel { Id = 1, Name = "dueText", DataType = WorkflowVariableTypes.String },
                    new VariableModel { Id = 2, Name = "level", DataType = WorkflowVariableTypes.Number }
                ]
            },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Work",
                Type = BpmnFlowNodeTypes.UserTask,
                Roles = ["Worker"],
                DueDateExpression = "dueText",
                PriorityExpression = "level"
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Finish", SourceRef = 2, TargetRef = 3 }
        ]
    };

    private static Dictionary<string, JsonElement> StartValues() => new()
    {
        ["amount"] = JsonSerializer.SerializeToElement(500),
        ["contractEnd"] = JsonSerializer.SerializeToElement("2030-01-31T17:00:00Z")
    };

    private static Dictionary<string, JsonElement> FilterValues(string? dueText, int? level)
    {
        var values = new Dictionary<string, JsonElement>();
        if (dueText is not null)
        {
            values["dueText"] = JsonSerializer.SerializeToElement(dueText);
        }
        if (level is int number)
        {
            values["level"] = JsonSerializer.SerializeToElement(number);
        }
        return values;
    }

    private static string? StatusOf(JsonArray items, long instanceId) =>
        items.Single(item => item!["instanceId"]!.GetValue<long>() == instanceId)!["dueStatus"]?.GetValue<string>();

    private async Task<long[]> InstanceIdsAsync(string path) =>
        (await GetJsonAsync(path, "worker", "Worker"))["items"]!.AsArray()
        .Select(item => item!["instanceId"]!.GetValue<long>())
        .ToArray();

    private async Task<long> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response))["id"]!.GetValue<long>();
    }

    private async Task<long> StartAsync(
        long workflowId,
        Dictionary<string, JsonElement>? values,
        string user = "starter",
        params string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(workflowId, null, null, values),
            user,
            roles.Length == 0 ? ["Requester"] : roles);
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response))["id"]!.GetValue<long>();
    }

    private async Task<long> EnterVotingAsync(long instanceId, string[]? voters)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instanceId}/flows/204",
            new TakeFlowRequest(voters is null
                ? null
                : new Dictionary<string, JsonElement> { ["voters"] = JsonSerializer.SerializeToElement(voters) }),
            "manager",
            "Manager");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response))["multiInstance"]!["executionId"]!.GetValue<long>();
    }

    private async Task<List<ChildRow>> ReadChildrenAsync(long executionId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT "Status", "CreatedAt", "DueAt", "Priority"
            FROM flowbit.user_tasks
            WHERE "MultiInstanceExecutionId" = @executionId
            ORDER BY "ItemIndex"
            """,
            connection);
        command.Parameters.AddWithValue("executionId", executionId);
        var rows = new List<ChildRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ChildRow(
                reader.GetString(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3)));
        }
        return rows;
    }

    private static void AssertDueValues(JsonNode item, DateTimeOffset dueAt, int priority, string dueStatus)
    {
        Assert.Equal(dueAt, item["dueAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(priority, item["priority"]!.GetValue<int>());
        Assert.Equal(dueStatus, item["dueStatus"]!.GetValue<string>());
    }

    private async Task<long> CreateWorkflowVersionAsync(long sourceWorkflowId, WorkflowModel model)
    {
        using var response = await SendAsync(
            HttpMethod.Put, $"/api/workflows/{sourceWorkflowId}", new UpdateWorkflowRequest(model, true));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response))["id"]!.GetValue<long>();
    }

    private async Task ChangeVersionAsync(long instanceId, long targetWorkflowId)
    {
        using var previewResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instanceId}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(targetWorkflowId),
            "operator",
            "admin");
        Assert.True(previewResponse.StatusCode == HttpStatusCode.OK, await previewResponse.Content.ReadAsStringAsync());
        var preview = (await previewResponse.Content.ReadFromJsonAsync<InstanceVersionChangePreviewDto>(JsonOptions))!;
        Assert.True(preview.Compatible, JsonSerializer.Serialize(preview.Blockers));

        using var change = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instanceId}/version-change",
            new ChangeInstanceVersionRequest(
                targetWorkflowId,
                preview.ExpectedSourceWorkflowId,
                preview.ExpectedUpdatedAt,
                "Change only due dates and priorities"),
            "operator",
            "admin");
        Assert.True(change.StatusCode == HttpStatusCode.OK, await change.Content.ReadAsStringAsync());
    }

    // Counts the database reads of one request after a warm-up call, so cached
    // definitions do not differ between the requests being compared.
    private async Task<(int Commands, int ThresholdReads)> MeasureAsync(Func<Task> request)
    {
        await request();
        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        await request();
        var commands = fixture.CommandCounter.ReaderCommands;
        var thresholdReads = fixture.CommandCounter.ReaderCommandTexts
            .Count(text => text.Contains("engine_settings", StringComparison.Ordinal));
        fixture.CommandCounter.Reset();
        return (commands, thresholdReads);
    }

    private async Task<HttpResponseMessage> SendDistributorAsync(
        HttpMethod method,
        string path,
        string clientId,
        string clientSecret,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        request.Headers.TryAddWithoutValidation("X-Client-Id", clientId);
        request.Headers.TryAddWithoutValidation("X-Client-Secret", clientSecret);
        return await fixture.Client.SendAsync(request);
    }

    private async Task<UserTaskEntity[]> ReadTasksAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.AsNoTracking().Where(task => task.InstanceId == instanceId)
            .OrderBy(task => task.Id).ToArrayAsync();
    }

    // Runs the instance's asyncBefore job now, as the Worker would.
    private async Task RunAsyncBeforeJobAsync(long instanceId)
    {
        long jobId;
        string queueClass;
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.AsNoTracking().SingleAsync(row =>
                row.InstanceId == instanceId && row.Kind == WorkflowJobKinds.AsyncBefore);
            jobId = job.Id;
            queueClass = job.QueueClass;
            await db.WorkflowJobs.Where(row => row.Id == jobId).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Priority, 1_000_000).SetProperty(row => row.DueAt, DateTimeOffset.UtcNow));
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var lease = Assert.Single(await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>()
            .LeaseRunnableAsync(new WorkflowJobLeaseRequest($"due-date-activation:{Guid.NewGuid():N}",
                1, queueClass == WorkflowJobClasses.Activity ? 1 : 0, 4, TimeSpan.FromMinutes(1)), CancellationToken.None));
        Assert.Equal(jobId, lease.Job.Id);
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
    }

    private async Task SetThresholdAsync(string value)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE flowbit.engine_settings
            SET "Value" = @value
            WHERE "Namespace" = 'UserTasks' AND "Key" = 'DueSoonThreshold'
            """,
            connection);
        command.Parameters.AddWithValue("value", value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<JsonNode> GetJsonAsync(string path, string user, params string[] roles)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, user, roles);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadJsonAsync(response);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        params string[] roles)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles);
        return await fixture.Client.SendAsync(request);
    }

    private static JsonNode Single(JsonNode array) => Assert.Single(array.AsArray())!;

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())
        ?? throw new InvalidOperationException("Response body was empty.");

    private sealed record ChildRow(string Status, DateTimeOffset CreatedAt, DateTimeOffset? DueAt, int? Priority);
}
