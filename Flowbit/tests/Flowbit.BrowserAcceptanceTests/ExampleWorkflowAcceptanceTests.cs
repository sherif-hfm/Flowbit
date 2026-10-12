using System.Net;
using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.Shared.Dtos;
using Xunit;
using static Flowbit.BrowserAcceptanceTests.ExampleApiClient;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>Exercises the checked-in catalog through real API and Worker processes.</summary>
[Collection(ExampleAcceptanceCollection.Name)]
public sealed partial class ExampleWorkflowAcceptanceTests(ExampleAcceptanceFixture fixture)
{
    private BrowserStackFixture Stack => fixture.Stack;
    private static readonly string[] OperatorRoles =
        ["admin", "ExampleRequester", "ExampleOperator", "ExampleReviewer", "Supervisor", "Reviewer", "Coordinator", "ReviewCoordinator", "RoleManager"];
    private string Example(string relativePath) => Path.Combine(Stack.RepositoryRoot, "examples", relativePath);

    [Theory]
    [InlineData("fail-once")]
    [InlineData("recover")]
    public async Task DurableRetriesAndIncidentRecovery(string mode)
    {
        await using var scenario = await Stack.CreateScenario($"examples-retries-{mode}");
        await scenario.RunAsync("durable retry and incident HTTP contract", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-operator", OperatorRoles);
            await Stack.StartWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(Example("durable-jobs/02-retries-and-incident-recovery.json"));
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id, Variables(("scenario", mode)));
            var jobs = await UntilAsync(() => client.JobsAsync(instance.Id),
                rows => rows.Any(job => job.NodeId == 2 && job.Status == (mode == "recover" ? "incident" : "completed")),
                "REST job retries to settle");
            var job = Assert.Single(jobs, job => job.NodeId == 2 && job.Kind == "asyncBefore");
            var attempts = await client.AttemptsAsync(job.Id);
            Assert.Equal(mode == "recover" ? 3 : 2, attempts.Count);
            Assert.Equal(Enumerable.Range(1, attempts.Count), attempts.OrderBy(attempt => attempt.AttemptNumber).Select(attempt => attempt.AttemptNumber));
            var calls = fixture.Service.Requests.Where(request => request.JobId == job.Id).ToArray();
            Assert.Equal(attempts.Count, calls.Length);
            Assert.Equal(Enumerable.Range(1, calls.Length), calls.Select(call => call.Attempt));
            Assert.Equal(503, calls[0].StatusCode);
            if (mode == "recover")
            {
                Assert.All(attempts, attempt => Assert.Equal("failed", attempt.Status));
                Assert.All(calls, call => Assert.Equal(503, call.StatusCode));
                var incident = Assert.Single(await client.IncidentsAsync(instance.Id));
                Assert.Equal("open", incident.Status);
                Assert.Equal(job.Id, incident.JobId);
                // Stop our Worker to observe the queueing contract without a race.
                await Stack.StopWorkerAsync();
                fixture.Service.Recover(job.Id);
                var retry = await client.PostAsync<RetryIncidentResultDto>($"/api/incidents/{incident.Id}/retry");
                Assert.Equal("resolved", retry.IncidentStatus);
                Assert.Equal("queued", retry.JobStatus);
                Assert.Equal(job.Id, retry.JobId);
                Assert.DoesNotContain((await client.Workflows.GetInstanceAsync(instance.Id)).ExecutionPositions, position => position.NodeId == 3 && position.TokenStatus == "active");
                await Stack.StartWorkerAsync();
                await UntilAsync(() => client.JobsAsync(instance.Id), rows => rows.Any(item => item.Id == job.Id && item.Status == "completed"), "manually queued job to finish");
                Assert.Equal(4, (await client.AttemptsAsync(job.Id)).Count);
                Assert.Equal(200, fixture.Service.Requests.Last(request => request.JobId == job.Id).StatusCode);
                Assert.Equal("resolved", Assert.Single(await client.IncidentsAsync(instance.Id)).Status);
            }
            else
            {
                Assert.Equal(200, calls[^1].StatusCode);
                Assert.Empty(await client.IncidentsAsync(instance.Id));
            }
            var detail = await client.Workflows.GetInstanceAsync(instance.Id);
            Assert.True(Value(detail, "recovered").GetBoolean());
            Assert.Equal(200, Value(detail, "httpStatus").GetInt32());
            await ActAsync(client, instance.Id, 3, 301);
            Assert.Equal("completed", (await client.Workflows.GetInstanceAsync(instance.Id)).Status);
        });
    }

    [Fact]
    public async Task ErrorBoundaryOrderingUsesOneThenThreeAttemptsWithoutIncidents()
    {
        await using var scenario = await Stack.CreateScenario("examples-boundary-precedence");
        await scenario.RunAsync("boundaryFirst versus retryFirst", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-operator", OperatorRoles);
            await Stack.StartWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(Example("durable-jobs/03-error-boundary-precedence.json"));
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id);
            var detail = await UntilAsync(() => client.Workflows.GetInstanceAsync(instance.Id), AtNode(6), "both caught failures and inspection task");
            Assert.NotEmpty(Value(detail, "boundaryFirstError").GetString()!);
            Assert.NotEmpty(Value(detail, "retryFirstError").GetString()!);
            var jobs = await client.JobsAsync(instance.Id);
            foreach (var (node, expected) in new[] { (2, 1), (4, 3) })
            {
                var job = Assert.Single(jobs, job => job.NodeId == node && job.Kind == "asyncBefore");
                Assert.Equal("completed", job.Status);
                Assert.Equal(expected, (await client.AttemptsAsync(job.Id)).Count);
            }
            Assert.Empty(await client.IncidentsAsync(instance.Id));
            var visits = await client.ExecutionsAsync(instance.Id);
            Assert.All(visits.Where(visit => visit.NodeId is 2 or 4), visit => Assert.Equal("faulted", visit.Status));
            Assert.Contains(visits, visit => visit.NodeId == 3);
            Assert.Contains(visits, visit => visit.NodeId == 5);
            Assert.DoesNotContain(visits, visit => visit.NodeId == 8);
            await ActAsync(client, instance.Id, 6, 601);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MessageAndTimeoutAreExclusiveAndRejectedDeliveriesDoNotWrite(bool expire)
    {
        await using var scenario = await Stack.CreateScenario($"examples-message-timeout-{expire}");
        await scenario.RunAsync("message versus real timer", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-operator", OperatorRoles);
            await Stack.StopWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(Example("messages/03-message-catch-with-timeout.json"));
            var caseKey = $"CASE-{Guid.NewGuid():N}";
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id, Variables(("caseKey", caseKey)));
            var url = $"/api/instances/{instance.Id}/message?catchEvent=INTERMEDIATE_MESSAGE_CATCH_EVENT_2";
            var headers = MessageHeaders(caseKey, $"delivery-{instance.Id}");
            var body = new { receipt = new { accepted = true, code = "RCPT-ACCEPTED" } };
            Assert.True(AtNode(2)(await client.Workflows.GetInstanceAsync(instance.Id)));
            if (expire)
            {
                await Stack.StartWorkerAsync();
                await UntilAsync(() => client.Workflows.GetInstanceAsync(instance.Id), AtNode(5), "thirty-second timeout", TimeSpan.FromSeconds(60));
                var before = await client.Workflows.GetInstanceAsync(instance.Id);
                await client.ExpectStatusAsync(HttpStatusCode.BadRequest, HttpMethod.Post, url, body, headers);
                var after = await client.Workflows.GetInstanceAsync(instance.Id);
                Assert.Equal(VariableSnapshot(before), VariableSnapshot(after));
                Assert.False(Value(after, "acknowledged").GetBoolean());
                Assert.Equal("PENDING", Value(after, "receiptCode").GetString());
                Assert.DoesNotContain(await client.ExecutionsAsync(instance.Id), visit => visit.NodeId == 3);
                var wait = Assert.Single(await client.ExecutionsAsync(instance.Id), visit => visit.NodeId == 2);
                Assert.Equal("cancelled", wait.Status);
                await ActAsync(client, instance.Id, 5, 501);
            }
            else
            {
                await client.SendAsync<MessageDeliveryAckDto>(HttpMethod.Post, url, body, headers);
                var before = await client.Workflows.GetInstanceAsync(instance.Id);
                Assert.True(AtNode(3)(before));
                Assert.True(Value(before, "acknowledged").GetBoolean());
                await client.ExpectStatusAsync(HttpStatusCode.Conflict, HttpMethod.Post, url,
                    new { receipt = new { accepted = false, code = "MUST-NOT-WRITE" } }, headers);
                Assert.Equal(VariableSnapshot(before), VariableSnapshot(await client.Workflows.GetInstanceAsync(instance.Id)));
                Assert.Equal("cancelled", Assert.Single(await client.JobsAsync(instance.Id), job => job.Kind == "timerBoundary").Status);
                await Stack.StartWorkerAsync();
                await ActAsync(client, instance.Id, 3, 301);
            }
            Assert.Equal("completed", (await client.Workflows.GetInstanceAsync(instance.Id)).Status);
        });
    }

    [Fact]
    public async Task InnerScopeCancellationPreservesOuterWorkAndCancelsEveryInnerWait()
    {
        await using var scenario = await Stack.CreateScenario("examples-nested-scope-cancellation");
        await scenario.RunAsync("nested scope cancellation", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-supervisor", OperatorRoles);
            await Stack.StopWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(Example("gateways/07-nested-scope-cancellation.json"));
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id);
            var before = await client.ExecutionsAsync(instance.Id);
            Assert.Equal(1, before.Count(visit => visit.NodeId == 5 && visit.Status == "active"));
            Assert.Equal(2, before.Count(visit => visit.NodeId == 5 && visit.Status == "pending"));
            Assert.Contains(before, visit => visit.NodeId == 6 && visit.Status == "active");
            Assert.Contains(before, visit => visit.NodeId == 7 && visit.Status == "active");
            var timer = Assert.Single(await client.JobsAsync(instance.Id), job => job.NodeId == 7 && job.Kind == "timer");
            await ActAsync(client, instance.Id, 8, 801);
            var detail = await client.Workflows.GetInstanceAsync(instance.Id);
            Assert.True(AtNode(3)(detail));
            Assert.True(AtNode(10)(detail));
            Assert.Equal("running", detail.Status);
            var cancelled = await client.ExecutionsAsync(instance.Id);
            Assert.All(cancelled.Where(visit => visit.NodeId is 5 or 6 or 7), visit => Assert.Equal("cancelled", visit.Status));
            Assert.Equal(3, cancelled.Count(visit => visit.NodeId == 5));
            Assert.Equal(new[] { 3, 10 }, (await client.ManagedTasksAsync(instance.Id)).Select(task => task.NodeId).Order());
            Assert.Equal("cancelled", (await client.JobsAsync(instance.Id)).Single(job => job.Id == timer.Id).Status);
            var beforeLate = VariableSnapshot(detail);
            await client.ExpectStatusAsync(HttpStatusCode.BadRequest, HttpMethod.Post,
                $"/api/instances/{instance.Id}/message?catchEvent=INTERMEDIATE_MESSAGE_CATCH_EVENT_6", new { receipt = new { accepted = true, code = "LATE" } },
                MessageHeaders("CASE-INNER", $"late-{instance.Id}"));
            Assert.Equal(beforeLate, VariableSnapshot(await client.Workflows.GetInstanceAsync(instance.Id)));
            await Stack.StartWorkerAsync();
            // Observe beyond the cancelled job's real due date; no timer/time manipulation.
            await ObservePastDueAsync(timer.DueAt);
            var afterWorker = await client.Workflows.GetInstanceAsync(instance.Id);
            Assert.True(AtNode(3)(afterWorker));
            Assert.True(AtNode(10)(afterWorker));
            Assert.Equal(new[] { 3, 10 }, (await client.ManagedTasksAsync(instance.Id)).Select(task => task.NodeId).Order());
            Assert.Equal(cancelled.Count, (await client.ExecutionsAsync(instance.Id)).Count);
            Assert.Empty(await client.AttemptsAsync(timer.Id));
            await ActAsync(client, instance.Id, 10, 1001);
            Assert.Equal("running", (await client.Workflows.GetInstanceAsync(instance.Id)).Status);
            await ActAsync(client, instance.Id, 3, 301);
            Assert.Equal("completed", (await client.Workflows.GetInstanceAsync(instance.Id)).Status);
        });
    }

    [Fact]
    public async Task MultiInstanceRoleReplacementPreservesHistoryOwnershipAndPendingActivation()
    {
        await using var scenario = await Stack.CreateScenario("examples-mi-role-management");
        await scenario.RunAsync("active and pending role policy replacement", async () =>
        {
            await using var coordinator = await CreateAsync(scenario, "example-coordinator", OperatorRoles);
            await using var analyst = await CreateAsync(scenario, "analyst", "Reviewer", "Approver");
            await using var manager = await CreateAsync(scenario, "manager", "SeniorReviewer", "SeniorApprover");
            await using var director = await CreateAsync(scenario, "director", "SeniorReviewer", "SeniorApprover");
            var workflow = await coordinator.Workflows.CreateAndPublishAsync(Example("multi-instance/07-role-management-active-and-pending.json"));
            var instance = await coordinator.Workflows.StartInstanceAsync(workflow.Id);
            var initial = (await coordinator.ManagedTasksAsync(instance.Id)).OrderBy(item => item.ItemIndex).ToArray();
            Assert.Equal(new[] { "analyst", "manager", "director" }, initial.Select(item => item.Owner));
            var executionId = initial[0].MultiInstanceExecutionId!.Value;
            var url = $"/api/multi-instance-executions/{executionId}/roles";
            var policy = await coordinator.GetAsync<UserTaskRolePolicyDto>(url);
            Assert.Equal(new[] { "Reviewer" }, policy.Roles);
            Assert.Equal(new[] { "Approver" }, Assert.Single(policy.Flows).Roles);
            await analyst.Workflows.TakeUserTaskFlowAsync(initial[0].UserTaskId, 201);
            var completedBefore = Assert.Single(await coordinator.ExecutionsAsync(instance.Id), visit => visit.UserTaskId == initial[0].UserTaskId);
            Assert.Equal("completed", completedBefore.Status);
            var afterFirst = await coordinator.Workflows.GetInstanceAsync(instance.Id);
            var completionHistory = afterFirst.History.Where(row => row.UserTaskId == initial[0].UserTaskId).ToArray();
            var change = new ChangeUserTaskRolesRequest(policy.RolePolicyId, ["SeniorReviewer"],
                [new(201, ["SeniorApprover"])], "Escalate unfinished reviews");
            var changed = await coordinator.PostAsync<UserTaskRolesChangeAckDto>(url, change);
            Assert.True(changed.Changed);
            Assert.NotEqual(policy.RolePolicyId, changed.Policy.RolePolicyId);
            Assert.Equal(1, changed.Policy.ActiveTaskCount);
            Assert.Equal(1, changed.Policy.PendingTaskCount);
            var remaining = (await coordinator.ManagedTasksAsync(instance.Id)).OrderBy(item => item.ItemIndex).ToArray();
            Assert.Equal(new[] { "manager", "director" }, remaining.Select(item => item.Owner));
            Assert.Equal(new[] { "active", "pending" }, remaining.Select(item => item.Status));
            Assert.All(remaining, item => Assert.Equal(new[] { "SeniorReviewer" }, item.NodeRoles));
            Assert.False((await coordinator.PostAsync<UserTaskRolesChangeAckDto>(url, change)).Changed);
            await coordinator.ExpectStatusAsync(HttpStatusCode.Conflict, HttpMethod.Post, url,
                change with { Roles = ["DifferentReviewer"] });
            var afterChange = await coordinator.Workflows.GetInstanceAsync(instance.Id);
            Assert.Single(afterChange.History, row => row.Note == "taskRolesChanged");
            Assert.Equal(JsonSerializer.Serialize(completionHistory), JsonSerializer.Serialize(afterChange.History.Where(row => row.UserTaskId == initial[0].UserTaskId)));
            var completedAfter = Assert.Single(await coordinator.ExecutionsAsync(instance.Id), visit => visit.Id == completedBefore.Id);
            Assert.Equal(completedBefore.CompletedAt, completedAfter.CompletedAt);
            Assert.Equal(completedBefore.CompletedBy, completedAfter.CompletedBy);
            Assert.Equal(completedBefore.Owner, completedAfter.Owner);
            await manager.Workflows.TakeUserTaskFlowAsync(initial[1].UserTaskId, 201);
            var last = await director.Workflows.GetUserTaskAsync(initial[2].UserTaskId);
            Assert.Equal("active", last.Status);
            Assert.Equal("director", last.Assignee);
            Assert.Equal(new[] { "SeniorReviewer" }, last.Roles);
            Assert.Equal(201, Assert.Single(await director.Workflows.GetUserTaskFlowsAsync(last.Id)).Id);
            await director.Workflows.TakeUserTaskFlowAsync(last.Id, 201);
            var finished = await coordinator.Workflows.GetInstanceAsync(instance.Id);
            Assert.Equal("completed", finished.Status);
            Assert.Equal(3, Value(finished, "reviewResults").GetArrayLength());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalEndAndTerminateDifferWithLiveSiblings(bool terminate)
    {
        await using var scenario = await Stack.CreateScenario($"examples-live-siblings-{terminate}");
        await scenario.RunAsync("normal versus terminate live siblings", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-supervisor", OperatorRoles);
            await Stack.StopWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(Example("gateways/08-terminate-live-siblings.json"));
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id);
            var before = await client.Workflows.GetInstanceAsync(instance.Id);
            Assert.True(AtNode(3)(before));
            Assert.True(AtNode(4)(before));
            Assert.True(AtNode(5)(before));
            var timer = Assert.Single(await client.JobsAsync(instance.Id), job => job.Kind == "timer");
            await ActAsync(client, instance.Id, 5, terminate ? 502 : 501);
            var after = await client.Workflows.GetInstanceAsync(instance.Id);
            await Stack.StartWorkerAsync();
            if (terminate)
            {
                Assert.Equal("completed", after.Status);
                Assert.Equal("terminate", after.Completion?.Kind);
                Assert.All((await client.ExecutionsAsync(instance.Id)).Where(visit => visit.NodeId is 3 or 4), visit => Assert.Equal("cancelled", visit.Status));
                Assert.Equal("cancelled", (await client.JobsAsync(instance.Id)).Single(job => job.Id == timer.Id).Status);
                var count = (await client.ExecutionsAsync(instance.Id)).Count;
                await ObservePastDueAsync(timer.DueAt);
                Assert.Empty(await client.AttemptsAsync(timer.Id));
                Assert.Equal(count, (await client.ExecutionsAsync(instance.Id)).Count);
            }
            else
            {
                Assert.Equal("running", after.Status);
                Assert.True(AtNode(3)(after));
                Assert.True(AtNode(4)(after));
                await UntilAsync(() => client.JobsAsync(instance.Id), jobs => jobs.Any(job => job.Id == timer.Id && job.Status == "completed"), "live sibling timer");
                Assert.Equal("running", (await client.Workflows.GetInstanceAsync(instance.Id)).Status);
                await ActAsync(client, instance.Id, 3, 301);
                Assert.Equal("completed", (await client.Workflows.GetInstanceAsync(instance.Id)).Status);
            }
        });
    }

    [Fact]
    public async Task ExistingApprovalTimerReturnsToFirstTask()
    {
        await using var scenario = await Stack.CreateScenario("examples-existing-two-step-timer");
        await scenario.RunAsync("existing one-minute approval timer", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-operator", OperatorRoles);
            await Stack.StartWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(Example("basics/02-two-step-approval.json"));
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id);
            await ActAsync(client, instance.Id, 2, 102);
            var secondTask = Assert.Single(await client.Workflows.GetInboxAsync(instance.Id));
            await UntilAsync(() => client.Workflows.GetInstanceAsync(instance.Id), AtNode(2), "the authored one-minute timer to return to approval1", TimeSpan.FromSeconds(90));
            Assert.Equal("cancelled", Assert.Single(await client.ExecutionsAsync(instance.Id), visit => visit.UserTaskId == secondTask.UserTaskId).Status);
            var returned = await client.Workflows.GetInstanceAsync(instance.Id);
            Assert.Contains(returned.History, row => row.SequenceFlowId == 105);
            await ActAsync(client, instance.Id, 2, 102);
            await ActAsync(client, instance.Id, 3, 103);
            Assert.Equal("completed", (await client.Workflows.GetInstanceAsync(instance.Id)).Status);
        });
    }

    [Fact]
    public async Task ExistingQuorumCancelsThirdReviewerBeforeFinalization()
    {
        await using var scenario = await Stack.CreateScenario("examples-existing-quorum-cancellation");
        await scenario.RunAsync("existing quorum cancellation timing", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-reviewer", OperatorRoles);
            var workflow = await client.Workflows.CreateAndPublishAsync(Example("gateways/04-complex-two-of-three-merge.json"));
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id);
            var third = Assert.Single(await client.Workflows.GetInboxAsync(instance.Id), task => task.CurrentNodeId == 5);
            await ActAsync(client, instance.Id, 3, 301);
            Assert.Contains(await client.Workflows.GetInboxAsync(instance.Id), task => task.UserTaskId == third.UserTaskId);
            await ActAsync(client, instance.Id, 4, 401);
            var detail = await client.Workflows.GetInstanceAsync(instance.Id);
            Assert.Equal("running", detail.Status);
            Assert.True(AtNode(7)(detail));
            var visits = await client.ExecutionsAsync(instance.Id);
            Assert.Equal("cancelled", Assert.Single(visits, visit => visit.UserTaskId == third.UserTaskId).Status);
            Assert.DoesNotContain(visits, visit => visit.NodeId == 8);
            await ActAsync(client, instance.Id, 7, 701);
            Assert.Equal("completed", (await client.Workflows.GetInstanceAsync(instance.Id)).Status);
        });
    }

    private static Func<InstanceDetailDto, bool> AtNode(int nodeId) => detail =>
        detail.ExecutionPositions.Any(position => position.NodeId == nodeId && position.TokenStatus == "active");

    private static JsonElement Value(InstanceDetailDto detail, string name) =>
        detail.Variables.Where(variable => variable.VariableName == name).OrderByDescending(variable => variable.Id).First().Value;

    private static string VariableSnapshot(InstanceDetailDto detail) =>
        JsonSerializer.Serialize(detail.Variables.OrderBy(variable => variable.Id));

    private static async Task ActAsync(ExampleApiClient client, long instanceId, int nodeId, int flowId)
    {
        var inbox = await client.Workflows.GetInboxAsync(instanceId);
        var task = Assert.Single(inbox, item => item.CurrentNodeId == nodeId);
        await client.Workflows.TakeUserTaskFlowAsync(task.UserTaskId, flowId);
    }

    private static async Task ObservePastDueAsync(DateTimeOffset dueAt)
    {
        var remaining = dueAt.AddSeconds(2) - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, ScenarioCancellation.Token);
    }
}
