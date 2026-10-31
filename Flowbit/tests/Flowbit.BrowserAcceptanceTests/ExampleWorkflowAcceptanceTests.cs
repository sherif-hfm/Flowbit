using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    [InlineData("status-409", 3, 8, "completed", 409)]
    [InlineData("status-422", 4, 9, "completed", 422)]
    [InlineData("status-503", 5, 10, "faulted", 503)]
    [InlineData("lost-503", 5, 10, "faulted", 503)]
    [InlineData("hang", 6, 11, "faulted", 0)]
    public async Task HttpStatusRoutingSelectsExactThenRangeThenCatchAll(
        string orderId, int boundary, int end, string status, int expectedStatus)
    {
        await using var scenario = await Stack.CreateScenario($"examples-status-routing-{orderId}");
        await scenario.RunAsync("HTTP status routing without a Worker job", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-operator", OperatorRoles);
            var workflow = await client.Workflows.CreateAndPublishAsync(Example("service-tasks/04-rest-http-status-routing.json"));
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id, Variables(("orderId", orderId)));

            var detail = await client.Workflows.GetInstanceAsync(instance.Id);
            Assert.Equal(status, detail.Status);
            Assert.Equal(expectedStatus, Value(detail, "serviceStatus").GetInt32());
            Assert.Contains("REST call failed", Value(detail, "integrationFailure").GetString());
            var visits = await client.ExecutionsAsync(instance.Id);
            Assert.Contains(visits, visit => visit.NodeId == boundary);
            Assert.Contains(visits, visit => visit.NodeId == end);
            Assert.DoesNotContain(visits, visit => visit.NodeId == 7);
            Assert.Empty(visits.Where(visit => visit.NodeId is >= 3 and <= 6 && visit.NodeId != boundary));
        });
    }

    [Theory]
    [InlineData("status-409-boundary-first", 1, 3)]
    [InlineData("status-422-boundary-first", 1, 4)]
    [InlineData("sequence-503-409-later-attempt", 2, 3)]
    public async Task DurableStatusRoutingUsesTheCurrentAttempt(string stockScenario, int attempts, int boundary)
    {
        await using var scenario = await Stack.CreateScenario($"examples-durable-status-{stockScenario}");
        await scenario.RunAsync("durable status routing through a real Worker", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-operator", OperatorRoles);
            await Stack.StartWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(Example("durable-jobs/04-rest-status-routing-and-retries.json"));
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id, Variables(("scenario", stockScenario)));

            await UntilAsync(() => client.Workflows.GetInstanceAsync(instance.Id), detail => detail.Status == "completed", "the boundary route to finish");

            var job = Assert.Single(await client.JobsAsync(instance.Id), job => job.NodeId == 2 && job.Kind == "asyncBefore");
            Assert.Equal(attempts, (await client.AttemptsAsync(job.Id)).Count);
            Assert.Empty(await client.IncidentsAsync(instance.Id));
            var visits = await client.ExecutionsAsync(instance.Id);
            Assert.Contains(visits, visit => visit.NodeId == boundary);
            Assert.DoesNotContain(visits, visit => visit.NodeId == 5);
        });
    }

    [Fact]
    public async Task DurableUnmatchedStatusRetriesThenOpensARecoverableIncident()
    {
        await using var scenario = await Stack.CreateScenario("examples-durable-status-unmatched");
        await scenario.RunAsync("an unmatched status exhausts retries", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-operator", OperatorRoles);
            await Stack.StartWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(Example("durable-jobs/04-rest-status-routing-and-retries.json"));
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id, Variables(("scenario", "sequence-503-503-503-409-recovered")));

            var jobs = await UntilAsync(() => client.JobsAsync(instance.Id),
                rows => rows.Any(job => job.NodeId == 2 && job.Status == "incident"),
                "retries to run out");
            var job = Assert.Single(jobs, job => job.NodeId == 2 && job.Kind == "asyncBefore");
            Assert.Equal(3, (await client.AttemptsAsync(job.Id)).Count);
            var incident = Assert.Single(await client.IncidentsAsync(instance.Id));
            Assert.DoesNotContain(await client.ExecutionsAsync(instance.Id), visit => visit.NodeId is 3 or 4);

            // A manual retry calls again, and the new response selects the boundary.
            await client.PostAsync<RetryIncidentResultDto>($"/api/incidents/{incident.Id}/retry");
            await UntilAsync(() => client.Workflows.GetInstanceAsync(instance.Id), detail => detail.Status == "completed", "the recovered route to finish");
            Assert.Contains(await client.ExecutionsAsync(instance.Id), visit => visit.NodeId == 3);
        });
    }

    // A copy of the durable example, changed for one scenario and named uniquely.
    private string DurableStatusExample(BrowserScenario scenario, Action<JsonNode> change)
    {
        var definition = JsonNode.Parse(File.ReadAllText(Example("durable-jobs/04-rest-status-routing-and-retries.json")))!;
        change(definition);
        definition["id"] = $"example-durable-status-{Guid.NewGuid():N}";
        var path = Path.Combine(scenario.ArtifactDirectory, "durable-status-example.json");
        File.WriteAllText(path, definition.ToJsonString());
        return path;
    }

    private static JsonNode FlowNode(JsonNode definition, int id) =>
        definition["flowNodes"]!.AsArray().Single(node => node!["id"]!.GetValue<int>() == id)!;

    private static string UniqueStock(string scenario) => $"{scenario}-x{Guid.NewGuid().ToString("N")[..6]}";

    [Theory]
    [InlineData("status-409", 3, 3)]
    [InlineData("sequence-503-409", 3, 3)]
    [InlineData("sequence-409-503", 3, 0)]
    [InlineData("sequence-409-200", 2, 5)]
    public async Task DurableRetryFirstRetriesEveryFailureThenUsesTheFinalAttempt(string stockScenario, int attempts, int expectedNode)
    {
        await using var scenario = await Stack.CreateScenario($"examples-retry-first-{stockScenario}");
        await scenario.RunAsync("retryFirst through a real Worker", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-operator", OperatorRoles);
            await Stack.StartWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(DurableStatusExample(scenario, definition =>
            {
                var job = FlowNode(definition, 2)["job"]!;
                job["failureHandling"] = "retryFirst";
                job["retryDelays"] = new JsonArray("PT1S", "PT1S");
            }));
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id, Variables(("scenario", UniqueStock(stockScenario))));

            if (expectedNode == 0)
            {
                await UntilAsync(() => client.IncidentsAsync(instance.Id), rows => rows.Count > 0, "an incident for the unmatched final status");
            }
            else
            {
                await UntilAsync(() => client.Workflows.GetInstanceAsync(instance.Id), detail => detail.Status == "completed", "the final attempt's route");
            }

            var job = Assert.Single(await client.JobsAsync(instance.Id), item => item.NodeId == 2 && item.Kind == "asyncBefore");
            Assert.Equal(attempts, (await client.AttemptsAsync(job.Id)).Count);
            var visits = await client.ExecutionsAsync(instance.Id);
            Assert.Equal(expectedNode == 0, (await client.IncidentsAsync(instance.Id)).Count > 0);
            if (expectedNode == 0)
            {
                Assert.DoesNotContain(visits, visit => visit.NodeId is 3 or 4 or 5);
            }
            else
            {
                Assert.Contains(visits, visit => visit.NodeId == expectedNode);
            }
        });
    }

    // Declares a status variable, and a conditional boundary that a stored status would trigger.
    private static void WatchStatus(JsonNode definition)
    {
        definition["variables"]!.AsArray().Add(JsonNode.Parse("""{ "id": 2, "name": "httpStatus", "dataType": "number", "isArray": false, "required": false, "defaultValue": 0 }"""));
        FlowNode(definition, 2)["service"]!["statusVariable"] = "httpStatus";
        definition["flowNodes"]!.AsArray().Add(JsonNode.Parse("""{ "id": 8, "name": "Status Watch", "type": "conditionalBoundaryEvent", "laneId": 1, "x": 320, "y": 190, "roles": [], "variables": [], "attachedToRef": 2, "cancelActivity": true, "conditional": { "condition": "httpStatus > 0" } }"""));
        definition["flowNodes"]!.AsArray().Add(JsonNode.Parse("""{ "id": 9, "name": "Status Seen", "type": "endEvent", "laneId": 1, "x": 820, "y": 400, "roles": [], "variables": [] }"""));
        definition["sequenceFlows"]!.AsArray().Add(JsonNode.Parse("""{ "id": 801, "name": "Seen", "sourceRef": 8, "targetRef": 9, "roles": [], "variables": [] }"""));
    }

    [Fact]
    public async Task DurableMatchingBoundaryBeatsAConditionOnTheFailedCallsStatus()
    {
        await using var scenario = await Stack.CreateScenario("examples-status-condition-precedence");
        await scenario.RunAsync("error boundary precedence through a real Worker", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-operator", OperatorRoles);
            await Stack.StartWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(DurableStatusExample(scenario, WatchStatus));
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id, Variables(("scenario", UniqueStock("status-409"))));

            var detail = await UntilAsync(() => client.Workflows.GetInstanceAsync(instance.Id), item => item.Status == "completed", "the error route");

            Assert.Equal(409, Value(detail, "httpStatus").GetInt32());
            var visits = await client.ExecutionsAsync(instance.Id);
            Assert.Contains(visits, visit => visit.NodeId == 3);
            Assert.DoesNotContain(visits, visit => visit.NodeId is 8 or 9);
            Assert.Empty(await client.IncidentsAsync(instance.Id));
        });
    }

    [Fact]
    public async Task DurableUnmatchedStatusOpensAnIncidentInsteadOfTakingACondition()
    {
        await using var scenario = await Stack.CreateScenario("examples-status-condition-unmatched");
        await scenario.RunAsync("an unmatched status with a condition on it", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-operator", OperatorRoles);
            await Stack.StartWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(DurableStatusExample(scenario, definition =>
            {
                WatchStatus(definition);
                FlowNode(definition, 2)["job"]!["retryDelays"] = new JsonArray();
            }));
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id, Variables(("scenario", UniqueStock("status-503"))));

            await UntilAsync(() => client.IncidentsAsync(instance.Id), rows => rows.Count > 0, "the incident");

            var detail = await client.Workflows.GetInstanceAsync(instance.Id);
            Assert.Equal("running", detail.Status);
            Assert.Equal(0, Value(detail, "httpStatus").GetInt32());
            Assert.DoesNotContain(await client.ExecutionsAsync(instance.Id), visit => visit.NodeId is 8 or 9);
        });
    }

    [Fact]
    public async Task DurableLateMatchingResultAfterAnInterruptingTimerIsDiscarded()
    {
        await using var scenario = await Stack.CreateScenario("examples-status-late-result");
        await scenario.RunAsync("a real timer wins while the call is open", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-operator", OperatorRoles);
            await Stack.StartWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(DurableStatusExample(scenario, definition =>
            {
                FlowNode(definition, 2)["service"]!["timeoutSeconds"] = 10;
                definition["flowNodes"]!.AsArray().Add(JsonNode.Parse("""{ "id": 8, "name": "Deadline", "type": "timerBoundaryEvent", "laneId": 1, "x": 320, "y": 190, "roles": [], "variables": [], "attachedToRef": 2, "cancelActivity": true, "timer": { "timeDuration": "PT1S" } }"""));
                definition["flowNodes"]!.AsArray().Add(JsonNode.Parse("""{ "id": 9, "name": "Timed Out", "type": "endEvent", "laneId": 1, "x": 820, "y": 400, "roles": [], "variables": [] }"""));
                definition["sequenceFlows"]!.AsArray().Add(JsonNode.Parse("""{ "id": 801, "name": "Too late", "sourceRef": 8, "targetRef": 9, "roles": [], "variables": [] }"""));
            }));
            var stock = UniqueStock("late-409");
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id, Variables(("scenario", stock)));

            await UntilAsync(() => client.Workflows.GetInstanceAsync(instance.Id), detail => detail.Status == "completed", "the timer route");
            // The service answers after four seconds; give that answer time to arrive.
            await Task.Delay(TimeSpan.FromSeconds(6));

            Assert.Equal(1, fixture.Service.StockCalls(stock));
            var visits = await client.ExecutionsAsync(instance.Id);
            Assert.Contains(visits, visit => visit.NodeId == 9);
            Assert.DoesNotContain(visits, visit => visit.NodeId is 3 or 4 or 5);
            Assert.Empty(await client.IncidentsAsync(instance.Id));
            Assert.Equal("completed", (await client.Workflows.GetInstanceAsync(instance.Id)).Status);
        });
    }

    [Fact]
    public async Task DurableWorkerStoppedMidCallRoutesOnceAfterItRestarts()
    {
        await using var scenario = await Stack.CreateScenario("examples-status-worker-restart");
        await scenario.RunAsync("a real Worker restart with a call open", async () =>
        {
            await using var client = await CreateAsync(scenario, "example-operator", OperatorRoles);
            await Stack.StartWorkerAsync();
            var workflow = await client.Workflows.CreateAndPublishAsync(DurableStatusExample(scenario, definition =>
                FlowNode(definition, 2)["service"]!["timeoutSeconds"] = 90));
            var stock = UniqueStock("stall-409");
            var instance = await client.Workflows.StartInstanceAsync(workflow.Id, Variables(("scenario", stock)));

            await UntilAsync(() => Task.FromResult(fixture.Service.StockCalls(stock)), calls => calls == 1, "the first call to start");
            await Stack.StopWorkerAsync();
            await Stack.StartWorkerAsync();
            await UntilAsync(
                () => client.Workflows.GetInstanceAsync(instance.Id),
                detail => detail.Status == "completed",
                "the expired lease to be retried and routed",
                TimeSpan.FromSeconds(150));

            Assert.Equal(2, fixture.Service.StockCalls(stock));
            Assert.Single(await client.ExecutionsAsync(instance.Id), visit => visit.NodeId == 3);
            Assert.Empty(await client.IncidentsAsync(instance.Id));
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
