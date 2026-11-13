using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// TASK-05B end to end against PostgreSQL: REST service bodies are published over HTTP
/// through a host with the Development release switch open, and every entry, dispatch,
/// item, and completion job runs through the registered job processor, as Workers run
/// them. A controlled in-process receiver stands in for the remote endpoint and records
/// every request, so the tests can count invocations and read the rendered requests.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class AutomaticMultiInstanceServiceRuntimeTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private readonly List<long> startedInstances = [];
    private readonly ControlledReceiver receiver = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Other suites lease specific jobs from this shared database.
        await using var db = fixture.CreateDbContext();
        var distant = DateTimeOffset.UtcNow.AddYears(10);
        await db.WorkflowJobs
            .Where(job => job.InstanceId != null && startedInstances.Contains(job.InstanceId.Value)
                && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry
                    || job.Status == WorkflowJobStatuses.Running || job.Status == WorkflowJobStatuses.ResultReady))
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.DueAt, distant)
                .SetProperty(job => job.LeaseExpiresAt, job => job.LeaseExpiresAt == null ? null : distant));
    }

    private WebApplicationFactory<Program> OpenHost() =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var open = new AutomaticMultiInstanceOptions { AllowInDevelopment = true };
            open.ResolveForEnvironment(isDevelopment: true);
            services.RemoveAll<AutomaticMultiInstanceOptions>();
            services.AddSingleton(open);
            services.RemoveAll<IServiceTaskInvoker>();
            services.AddSingleton<IServiceTaskInvoker>(receiver);
        }));

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    private static string Key(string name) => $"service-mi-{name}-{Guid.NewGuid():N}";

    private static WorkflowModel Workflow(string key, FlowNodeModel body, IEnumerable<VariableModel> variables)
    {
        var definition = new WorkflowModel
        {
            Id = key, Name = key, InitialEventId = 1, Variables = variables.ToList(),
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent, X = 40, Y = 40 },
                body,
                new() { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent, X = 500, Y = 40 }
            ],
            SequenceFlows = [new() { Id = 101, SourceRef = 1, TargetRef = 2 }, new() { Id = 201, SourceRef = 2, TargetRef = 3 }]
        };
        return definition;
    }

    // An error boundary on the body: null match is the catch-all.
    private static void AddBoundary(WorkflowModel definition, int id, string errorVariable, ErrorBoundaryMatchModel? match)
    {
        definition.Variables.Add(new() { Id = 80 + id, Name = errorVariable, DataType = "string", Nullable = true });
        definition.FlowNodes.Add(new()
        {
            Id = id, Name = "Caught " + id, Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, AttachedToRef = 2,
            ErrorVariable = errorVariable, ErrorMatch = match
        });
        definition.FlowNodes.Add(new() { Id = id + 1, Name = "Handled " + id, Type = BpmnFlowNodeTypes.EndEvent, X = 500, Y = 100 + id * 20 });
        definition.SequenceFlows.Add(new() { Id = id * 100 + 1, SourceRef = id, TargetRef = id + 1 });
    }

    private static MultiInstanceOutputVariableModel Local(string name, string type = "string") => new()
    {
        Name = name, DataType = type, IsArray = false, Nullable = false
    };

    // The plan's example: one POST per supplier with a stable idempotency header, the
    // whole item in the body, a local notification id, and a local response status.
    private static FlowNodeModel NotifyBody(string mode = "parallel", string? url = null) => new()
    {
        Id = 2, Name = "Notify suppliers", Type = BpmnFlowNodeTypes.ServiceTask, AsyncBefore = true, X = 200, Y = 20,
        MultiInstance = new()
        {
            Mode = mode, Source = "collection", CollectionVariable = "supplierIds", CompletionEvaluation = "afterAll",
            MaxConcurrency = mode == "sequential" ? 1 : 4, ResultVariable = "notificationResults",
            OutputVariables = [Local("notificationId"), Local("responseStatus", "number")]
        },
        Service = new()
        {
            Type = "rest", Method = "POST", Url = url ?? "https://notifications.example.test/send", TimeoutSeconds = 30,
            Headers =
            [
                new() { Name = "Content-Type", Value = "application/json" },
                new() { Name = "Idempotency-Key", Value = "flowbit-supplier-${sys.jobId}" }
            ],
            Body = "{\"supplier\":${mi.item},\"index\":${mi.index},\"total\":${mi.total},\"region\":\"${region}\"}",
            StatusVariable = "responseStatus",
            OutputMappings = [new() { Variable = "notificationId", Path = "id", DataType = "string", IsArray = false, Required = true }]
        }
    };

    private static List<VariableModel> Suppliers(params string[] ids) =>
    [
        new() { Id = 1, Name = "supplierIds", DataType = "string", IsArray = true, DefaultValue = Json(ids) },
        new() { Id = 2, Name = "notificationResults", DataType = "json", DefaultValue = Json(Array.Empty<object>()) },
        new() { Id = 3, Name = "region", DataType = "string", DefaultValue = Json("eu") }
    ];

    private static ServiceTaskResult Created(string id) => new(true, 201, JsonSerializer.Serialize(new { id }), null);

    private static string Supplier(ServiceTaskRequest request) =>
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("supplier").GetString()!;

    private static string Header(ServiceTaskRequest request, string name) =>
        request.Headers.Single(header => header.Name == name).Value;

    [Fact]
    public async Task Parallel_body_sends_one_request_per_item_and_orders_results_whatever_order_responses_arrive()
    {
        await using var host = OpenHost();
        receiver.Respond = request => Created("notice-" + Supplier(request));
        var published = await PublishAsync(host, Workflow(Key("parallel"), NotifyBody(),
            Suppliers("supplier-a", "supplier-b", "supplier-c")));
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        // An instance edit after initialization never reaches the frozen body.
        await using (var db = fixture.CreateDbContext())
        {
            await new WorkflowRuntimeRepository(db).AddVariableAsync(instanceId, "region", null, "tester", Json("us"), default);
            await db.SaveChangesAsync();
        }
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJobs = await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem);
        Assert.Equal(3, itemJobs.Count);
        foreach (var jobId in itemJobs.AsEnumerable().Reverse()) await ProcessAsync(host, jobId);
        await DrainAsync(host, instanceId);

        Assert.Equal(3, receiver.Count);
        foreach (var request in receiver.Requests)
        {
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal("eu", body.RootElement.GetProperty("region").GetString());
            Assert.Equal(3, body.RootElement.GetProperty("total").GetInt32());
            Assert.Equal("POST", request.Method);
        }
        // Each item's idempotency key is its own stable job id.
        Assert.Equal(itemJobs.Select(id => "flowbit-supplier-" + id).Order(),
            receiver.Requests.Select(request => Header(request, "Idempotency-Key")).Order());
        var results = await LatestAsync(instanceId, "notificationResults");
        Assert.Equal(new[] { "supplier-a", "supplier-b", "supplier-c" }, results.EnumerateArray().Select(row => row.GetProperty("item").GetString()));
        Assert.Equal(new[] { "notice-supplier-a", "notice-supplier-b", "notice-supplier-c" },
            results.EnumerateArray().Select(row => row.GetProperty("variables").GetProperty("notificationId").GetString()));
        Assert.All(results.EnumerateArray(), row => Assert.Equal(201, row.GetProperty("variables").GetProperty("responseStatus").GetInt32()));
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        // Locals and the status never become instance variables.
        Assert.False(await after.InstanceVariables.AnyAsync(row => row.InstanceId == instanceId
            && (row.VariableName == "notificationId" || row.VariableName == "responseStatus")));
        Assert.Equal(3, await after.NodeExecutions.CountAsync(row => row.InstanceId == instanceId && row.NodeId == 2 && row.Status == "completed"));
    }

    [Fact]
    public async Task Sequential_body_sends_the_next_request_only_after_the_previous_item_commits_and_an_empty_collection_sends_none()
    {
        await using var host = OpenHost();
        receiver.Respond = request => Created("notice-" + Supplier(request));
        var published = await PublishAsync(host, Workflow(Key("sequential"), NotifyBody("sequential"), Suppliers("first", "second")));
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        Assert.Equal(1, await CountJobsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem));
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceItem);
        Assert.Equal(1, receiver.Count);
        Assert.Equal("first", Supplier(receiver.Requests[0]));
        await DrainAsync(host, instanceId);
        Assert.Equal(new[] { "first", "second" }, receiver.Requests.Select(Supplier));

        var empty = await PublishAsync(host, Workflow(Key("empty"), NotifyBody(), Suppliers()));
        var emptyId = await StartAsync(host, empty.Id);
        await DrainAsync(host, emptyId);
        Assert.Equal(2, receiver.Count);
        Assert.Equal("[]", (await LatestAsync(emptyId, "notificationResults")).GetRawText());
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == emptyId)).Status);
    }

    [Fact]
    public async Task A_status_matched_boundary_catches_a_failed_item_and_its_error_variable_holds_the_code()
    {
        await using var host = OpenHost();
        receiver.Respond = request => Supplier(request) == "missing"
            ? new ServiceTaskResult(true, 404, "{\"error\":\"unknown supplier\"}", null)
            : Created("notice-" + Supplier(request));
        var definition = Workflow(Key("status-boundary"), NotifyBody(), Suppliers("known", "missing", "later"));
        AddBoundary(definition, 4, "notFound", new() { HttpStatusCodes = [404] });
        AddBoundary(definition, 6, "otherFailure", null);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJobs = await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem);
        await ProcessAsync(host, itemJobs[0]);
        await ProcessAsync(host, itemJobs[1]);
        Assert.Equal(WorkflowJobStatuses.Cancelled, await JobStatusAsync(itemJobs[2]));
        await DrainAsync(host, instanceId);

        // The exact 404 boundary won over the catch-all; it got the code, not the status.
        Assert.Equal(WorkflowEngineService.AutomaticServiceFailureCode, (await LatestAsync(instanceId, "notFound")).GetString());
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "otherFailure")).ValueKind);
        var results = await LatestAsync(instanceId, "notificationResults");
        Assert.Equal(new[] { "completed", "failed", "cancelled" }, results.EnumerateArray().Select(row => row.GetProperty("status").GetString()));
        Assert.Equal("notice-known", results[0].GetProperty("variables").GetProperty("notificationId").GetString());
        Assert.Equal("{}", results[1].GetProperty("variables").GetRawText());
        Assert.Equal(2, receiver.Count);
        await using var db = fixture.CreateDbContext();
        // The failed item and its node visit say which status fired the boundary.
        var failed = await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == instanceId && row.ItemIndex == 1);
        Assert.Equal(WorkflowEngineService.AutomaticServiceFailureCode, failed.ErrorCode);
        Assert.Equal("The REST call failed with HTTP status 404.", failed.ErrorDescription);
        Assert.Equal(failed.ErrorDescription, (await db.NodeExecutions.SingleAsync(row =>
            row.AutomaticMultiInstanceItemId == failed.Id)).ErrorDescription);
        Assert.Contains(await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync(),
            nodeId => nodeId == 5);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
    }

    [Fact]
    public async Task An_uncaught_failure_retries_the_same_item_with_one_job_id_then_waits_and_its_retry_resends_only_that_item()
    {
        await using var host = OpenHost();
        var refuse = true;
        receiver.Respond = request => Supplier(request) == "flaky" && Volatile.Read(ref refuse)
            ? new ServiceTaskResult(true, 503, null, null)
            : Created("notice-" + Supplier(request));
        var body = NotifyBody();
        body.Job = new JobPolicyModel { RetryDelays = ["PT10S"] };
        var published = await PublishAsync(host, Workflow(Key("uncaught"), body, Suppliers("steady", "flaky")));
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJobs = await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem);
        await ProcessAsync(host, itemJobs[0]);
        await ProcessAsync(host, itemJobs[1]);
        Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(itemJobs[1]));
        await ProcessAsync(host, itemJobs[1]);

        long incidentId;
        await using (var db = fixture.CreateDbContext())
        {
            var incident = await db.WorkflowIncidents.SingleAsync(row => row.JobId == itemJobs[1] && row.Status == WorkflowIncidentStatuses.Open);
            Assert.Equal("job_execution_failed", incident.Type);
            Assert.Contains("HTTP status 503", incident.Details);
            incidentId = incident.Id;
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        }
        Assert.Equal("[]", (await LatestAsync(instanceId, "notificationResults")).GetRawText());
        var flaky = receiver.Requests.Where(request => Supplier(request) == "flaky").ToArray();
        Assert.Equal(2, flaky.Length);
        Assert.Single(flaky.Select(request => Header(request, "Idempotency-Key")).Distinct());

        Volatile.Write(ref refuse, false);
        await RetryIncidentAsync(host, incidentId);
        await DrainAsync(host, instanceId);

        // Only the failed item was sent again; the committed sibling was not.
        Assert.Equal(1, receiver.Requests.Count(request => Supplier(request) == "steady"));
        Assert.Equal(3, receiver.Requests.Count(request => Supplier(request) == "flaky"));
        Assert.Single(receiver.Requests.Where(request => Supplier(request) == "flaky")
            .Select(request => Header(request, "Idempotency-Key")).Distinct());
        Assert.Equal(new[] { "completed", "completed" }, (await LatestAsync(instanceId, "notificationResults")).EnumerateArray()
            .Select(row => row.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task A_2xx_whose_mapping_fails_takes_a_2xx_range_boundary_with_the_result_invalid_code()
    {
        await using var host = OpenHost();
        receiver.Respond = _ => new ServiceTaskResult(true, 200, "{\"unexpected\":true}", null);
        var definition = Workflow(Key("mapping"), NotifyBody(), Suppliers("only"));
        AddBoundary(definition, 4, "mappingFailure", new() { HttpStatusRanges = [new() { From = 200, To = 299 }] });
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await DrainAsync(host, instanceId);

        Assert.Equal(AutomaticMultiInstanceValues.ResultInvalid, (await LatestAsync(instanceId, "mappingFailure")).GetString());
        Assert.Equal("failed", (await LatestAsync(instanceId, "notificationResults"))[0].GetProperty("status").GetString());
        Assert.Equal(1, receiver.Count);
        await using var db = fixture.CreateDbContext();
        Assert.Equal("The REST call returned HTTP status 200, but the response did not satisfy the output mappings.",
            (await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == instanceId)).ErrorDescription);
    }

    [Fact]
    public async Task A_failure_without_a_response_reaches_only_the_catch_all_and_says_no_response_arrived()
    {
        await using var host = OpenHost();
        receiver.Respond = _ => new ServiceTaskResult(false, 0, null, "Connection refused (receiver.example.test:443)");
        var definition = Workflow(Key("no-response"), NotifyBody(), Suppliers("down"));
        AddBoundary(definition, 4, "serverFailure", new() { HttpStatusRanges = [new() { From = 500, To = 599 }] });
        AddBoundary(definition, 6, "anyFailure", null);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await DrainAsync(host, instanceId);

        Assert.Equal(WorkflowEngineService.AutomaticServiceFailureCode, (await LatestAsync(instanceId, "anyFailure")).GetString());
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "serverFailure")).ValueKind);
        await using var db = fixture.CreateDbContext();
        var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == instanceId);
        // The transport error text, which names the receiver, stays out of the item.
        Assert.Equal("The REST call got no HTTP response: it timed out, failed in transport, or its request could not be rendered.",
            item.ErrorDescription);
        Assert.Contains(await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync(),
            nodeId => nodeId == 7);
    }

    [Fact]
    public async Task Retry_first_retries_a_status_matched_failure_with_one_job_id_before_its_boundary_takes_it()
    {
        await using var host = OpenHost();
        receiver.Respond = _ => new ServiceTaskResult(true, 404, "{}", null);
        var body = NotifyBody();
        body.Job = new JobPolicyModel { FailureHandling = JobFailureHandling.RetryFirst, RetryDelays = ["PT10S"] };
        var definition = Workflow(Key("retry-first"), body, Suppliers("missing"));
        AddBoundary(definition, 4, "notFound", new() { HttpStatusCodes = [404] });
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();

        await ProcessAsync(host, itemJob);
        Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(itemJob));
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "notFound")).ValueKind);
        await ProcessAsync(host, itemJob);
        await DrainAsync(host, instanceId);

        Assert.Equal(2, receiver.Count);
        Assert.Single(receiver.Requests.Select(request => Header(request, "Idempotency-Key")).Distinct());
        Assert.Equal(WorkflowEngineService.AutomaticServiceFailureCode, (await LatestAsync(instanceId, "notFound")).GetString());
        await using var db = fixture.CreateDbContext();
        Assert.Contains(await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync(),
            nodeId => nodeId == 5);
    }

    [Fact]
    public async Task A_result_too_large_to_save_is_refused_once_and_only_the_catch_all_takes_it()
    {
        await using var host = OpenHost();
        // Over the 1 MiB an item's staged input and saved result share.
        var huge = new string('x', AutomaticMultiInstanceRules.MaxSnapshotBytes + 1024);
        receiver.Respond = _ => new ServiceTaskResult(true, 201, JsonSerializer.Serialize(new { id = huge }), null);
        var definition = Workflow(Key("too-large"), NotifyBody(), Suppliers("only"));
        AddBoundary(definition, 4, "createdFailure", new() { HttpStatusCodes = [201] });
        AddBoundary(definition, 6, "anyFailure", null);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await DrainAsync(host, instanceId);

        Assert.Equal(1, receiver.Count);
        Assert.Equal(AutomaticMultiInstanceValues.ResultTooLarge, (await LatestAsync(instanceId, "anyFailure")).GetString());
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "createdFailure")).ValueKind);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.InstanceId == instanceId));
        Assert.Null((await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == instanceId)).CandidateJson);
    }

    [Fact]
    public async Task Cancelling_the_instance_during_a_call_fences_the_late_response()
    {
        await using var host = OpenHost();
        receiver.Respond = request => Created("notice-" + Supplier(request));
        var published = await PublishAsync(host, Workflow(Key("cancel"), NotifyBody(), Suppliers("held", "queued")));
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem))[0];
        receiver.BeforeNext = () =>
        {
            using var cancelled = SendAsync(host, HttpMethod.Post, $"/api/instances/{instanceId}/cancel").GetAwaiter().GetResult();
            Assert.True(cancelled.IsSuccessStatusCode, cancelled.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        };
        await ProcessAsync(host, itemJob);

        Assert.Equal(1, receiver.Count);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        var bodyRow = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId);
        Assert.Equal(MultiInstanceRecordStatuses.Cancelled, bodyRow.Status);
        var items = await db.MultiInstanceActivityItems.Where(row => row.MultiInstanceExecutionId == bodyRow.Id).ToListAsync();
        Assert.All(items, item => Assert.Equal("cancelled", item.Status));
        Assert.All(items, item => Assert.Null(item.CandidateJson));
        Assert.Equal("[]", (await LatestAsync(instanceId, "notificationResults")).GetRawText());
        Assert.Empty(await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem));
    }

    [Fact]
    public async Task A_non_interrupting_timer_runs_its_branch_while_the_body_keeps_running()
    {
        await using var host = OpenHost();
        receiver.Respond = request => Created("notice-" + Supplier(request));
        var definition = Workflow(Key("reminder"), NotifyBody(), Suppliers("first", "second"));
        definition.FlowNodes.Add(new() { Id = 6, Name = "Reminder", Type = BpmnFlowNodeTypes.TimerBoundaryEvent, AttachedToRef = 2,
            CancelActivity = false, Timer = new TimerDefinitionModel { TimeDuration = "PT1H" } });
        definition.FlowNodes.Add(new() { Id = 7, Name = "Reminded", Type = BpmnFlowNodeTypes.EndEvent, X = 500, Y = 300 });
        definition.SequenceFlows.Add(new() { Id = 601, SourceRef = 6, TargetRef = 7 });
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.TimerBoundary);
        await using (var db = fixture.CreateDbContext())
        {
            // The reminder ran its own branch; the body and its items are untouched.
            Assert.Contains(await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync(),
                nodeId => nodeId == 7);
            Assert.Equal(MultiInstanceRecordStatuses.Active,
                (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId)).Status);
            Assert.Equal(2, (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Count);
        }
        await DrainAsync(host, instanceId);

        Assert.Equal(2, receiver.Count);
        Assert.Equal(new[] { "completed", "completed" }, (await LatestAsync(instanceId, "notificationResults")).EnumerateArray()
            .Select(row => row.GetProperty("status").GetString()));
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Contains(await after.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync(),
            nodeId => nodeId == 3);
    }

    [Fact]
    public async Task Json_object_items_are_sent_whole_and_kept_whole_in_the_aggregate()
    {
        await using var host = OpenHost();
        receiver.Respond = request =>
        {
            using var sent = JsonDocument.Parse(request.Body!);
            return Created("notice-" + sent.RootElement.GetProperty("order").GetProperty("sku").GetString());
        };
        var body = NotifyBody();
        body.MultiInstance!.CollectionVariable = "orders";
        body.Service!.Body = "{\"order\":${mi.item},\"index\":${mi.index}}";
        var variables = Suppliers();
        variables.Add(new() { Id = 8, Name = "orders", DataType = "json", IsArray = true, DefaultValue = Json(new object[]
        {
            new { sku = "A", quantity = 2 },
            new { sku = "B", quantity = 1, note = "say \"hi\"" }
        }) });
        var published = await PublishAsync(host, Workflow(Key("objects"), body, variables));
        var instanceId = await StartAsync(host, published.Id);
        await DrainAsync(host, instanceId);

        Assert.Equal(2, receiver.Count);
        var sent = receiver.Requests.Select(request => JsonDocument.Parse(request.Body!).RootElement.GetProperty("order"))
            .OrderBy(order => order.GetProperty("sku").GetString()).ToArray();
        Assert.Equal(2, sent[0].GetProperty("quantity").GetInt32());
        Assert.Equal("say \"hi\"", sent[1].GetProperty("note").GetString());
        var results = await LatestAsync(instanceId, "notificationResults");
        Assert.Equal(JsonValueKind.Object, results[1].GetProperty("item").ValueKind);
        Assert.Equal("B", results[1].GetProperty("item").GetProperty("sku").GetString());
        Assert.Equal("notice-B", results[1].GetProperty("variables").GetProperty("notificationId").GetString());
    }

    [Fact]
    public async Task An_optional_shared_mapping_the_response_left_empty_is_not_checked_again_at_publication()
    {
        await using var host = OpenHost();
        var refKey = $"tests.service.ref.{Guid.NewGuid():N}";
        using (var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
                   new CreateSharedVariableRequest(refKey, "string", IsArray: false, Nullable: false, HasValue: true, Json("R-1"))))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        // The response carries no "ref", so this item never writes lastRef.
        receiver.Respond = _ => Created("n-1");
        var body = NotifyBody();
        body.Service!.OutputMappings.Add(new()
        {
            Variable = "lastRef", Path = "ref", DataType = "string", IsArray = false, Required = false,
            Validation = "StartsWith(lastRef, 'R-')"
        });
        var variables = Suppliers("only");
        variables.Add(new() { Id = 7, Name = "lastRef", DataType = "string", Scope = "shared", SharedKey = refKey, Access = "readWrite" });
        var definition = Workflow(Key("unwritten"), body, variables);
        AddBoundary(definition, 6, "anyFailure", null);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();
        // Another writer replaces the value with one the rule would refuse.
        receiver.BeforeNext = () => SetSharedStringAsync(host, refKey, "X").GetAwaiter().GetResult();
        await ProcessAsync(host, itemJob);
        await DrainAsync(host, instanceId);

        Assert.Equal(1, receiver.Count);
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "anyFailure")).ValueKind);
        Assert.Equal("completed", (await LatestAsync(instanceId, "notificationResults"))[0].GetProperty("status").GetString());
        using var value = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{refKey}/value");
        using var document = JsonDocument.Parse(await value.Content.ReadAsStringAsync());
        Assert.Equal("X", document.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task An_optional_local_mapping_the_response_left_empty_keeps_its_default_without_a_publication_check()
    {
        await using var host = OpenHost();
        var limitKey = $"tests.service.reflimit.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, limitKey, 2);
        // The response carries no "ref", so optionalRef keeps its local default.
        receiver.Respond = _ => Created("n-1");
        var body = NotifyBody();
        body.MultiInstance!.OutputVariables!.Add(new()
        {
            Name = "optionalRef", DataType = "string", IsArray = false, Nullable = false, DefaultValue = Json("R-1")
        });
        body.Service!.OutputMappings.Add(new()
        {
            Variable = "optionalRef", Path = "ref", DataType = "string", IsArray = false, Required = false,
            Validation = "Len(optionalRef) > limit"
        });
        var variables = Suppliers("only");
        variables.Add(new() { Id = 7, Name = "limit", DataType = "number", Scope = "shared", SharedKey = limitKey, Access = "read" });
        var definition = Workflow(Key("unmapped-local"), body, variables);
        AddBoundary(definition, 6, "anyFailure", null);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();
        // The rule passes against the staged limit, and would fail against this one.
        receiver.BeforeNext = () => SetSharedAsync(host, limitKey, 5).GetAwaiter().GetResult();
        await ProcessAsync(host, itemJob);
        await DrainAsync(host, instanceId);

        Assert.Equal(1, receiver.Count);
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "anyFailure")).ValueKind);
        var row = (await LatestAsync(instanceId, "notificationResults"))[0];
        Assert.Equal("completed", row.GetProperty("status").GetString());
        Assert.Equal("R-1", row.GetProperty("variables").GetProperty("optionalRef").GetString());
    }

    [Fact]
    public async Task A_numeric_mapping_default_that_is_not_a_finite_number_takes_the_boundary_without_another_request()
    {
        await using var host = OpenHost();
        receiver.Respond = _ => Created("n-1");
        var body = NotifyBody();
        body.MultiInstance!.OutputVariables!.Add(Local("quantity", "number"));
        // The response carries no "quantity", so the default renders the item's text.
        body.Service!.OutputMappings.Add(new()
        {
            Variable = "quantity", Path = "quantity", DataType = "number", IsArray = false, Required = false,
            DefaultValue = Json("${mi.item}")
        });
        var definition = Workflow(Key("non-finite"), body, Suppliers("1e9999"));
        AddBoundary(definition, 6, "anyFailure", null);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await DrainAsync(host, instanceId);

        Assert.Equal(1, receiver.Count);
        Assert.Equal(AutomaticMultiInstanceValues.ResultInvalid, (await LatestAsync(instanceId, "anyFailure")).GetString());
        Assert.Equal("failed", (await LatestAsync(instanceId, "notificationResults"))[0].GetProperty("status").GetString());
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.InstanceId == instanceId));
    }

    [Fact]
    public async Task A_host_outside_Development_neither_starts_nor_runs_a_published_service_body()
    {
        await using var host = OpenHost();
        receiver.Respond = request => Created("notice-" + Supplier(request));
        var body = NotifyBody();
        body.Job = new JobPolicyModel { RetryDelays = ["PT1S", "PT1S"] };
        var published = await PublishAsync(host, Workflow(Key("closed-run"), body, Suppliers("first")));

        var refused = await Assert.ThrowsAsync<WorkflowDomainException>(() => StartAsync(fixture.Factory, published.Id));
        Assert.Contains("runs only on a Development host with the release switch open", refused.Message);

        var instanceId = await StartAsync(host, published.Id);
        var entry = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.AsyncBefore)).Single();
        await ProcessAsync(fixture.Factory, entry);
        Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(entry));
        await ProcessAsync(host, entry);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var item = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();
        await ProcessAsync(fixture.Factory, item);
        Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(item));
        Assert.Equal(0, receiver.Count);

        await DrainAsync(host, instanceId);
        Assert.Equal(1, receiver.Count);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
    }

    [Fact]
    public async Task Shared_output_conflict_keeps_the_saved_candidate_and_retry_publishes_it_without_another_request()
    {
        await using var host = OpenHost();
        var sharedKey = $"tests.service.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, sharedKey, 5);
        receiver.Respond = _ => new ServiceTaskResult(true, 200, "{\"id\":\"n-1\",\"count\":42}", null);
        var body = NotifyBody("sequential");
        body.Service!.OutputMappings.Add(new() { Variable = "lastCount", Path = "count", DataType = "number", IsArray = false, Required = true });
        var variables = Suppliers("only");
        variables.Add(new() { Id = 7, Name = "lastCount", DataType = "number", Scope = "shared", SharedKey = sharedKey, Access = "readWrite" });
        var published = await PublishAsync(host, Workflow(Key("conflict"), body, variables));
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();
        // Another writer changes the value after the item staged and before it publishes.
        receiver.BeforeNext = () => SetSharedAsync(host, sharedKey, 100).GetAwaiter().GetResult();
        await ProcessAsync(host, itemJob);

        Assert.Equal(1, receiver.Count);
        Assert.Equal(100, await SharedValueAsync(host, sharedKey));
        long incidentId;
        await using (var db = fixture.CreateDbContext())
        {
            var incident = await db.WorkflowIncidents.SingleAsync(row => row.JobId == itemJob && row.Status == WorkflowIncidentStatuses.Open);
            Assert.Equal("output_version_conflict", incident.Type);
            incidentId = incident.Id;
            var job = await db.WorkflowJobs.SingleAsync(row => row.Id == itemJob);
            Assert.NotNull((await db.MultiInstanceActivityItems.SingleAsync(row => row.Id == job.AutomaticMultiInstanceItemId)).CandidateJson);
        }

        await RetryIncidentAsync(host, incidentId);
        await DrainAsync(host, instanceId);

        // The saved result replaced the newer value; the receiver saw no new request.
        Assert.Equal(1, receiver.Count);
        Assert.Equal(42, await SharedValueAsync(host, sharedKey));
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        // A shared write is never copied into the aggregate's locals.
        Assert.False((await LatestAsync(instanceId, "notificationResults"))[0].GetProperty("variables").TryGetProperty("lastCount", out _));
    }

    [Fact]
    public async Task Validation_failing_only_at_publication_reaches_the_catch_all_and_never_resends()
    {
        await using var host = OpenHost();
        var limitKey = $"tests.service.limit.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, limitKey, 10);
        receiver.Respond = _ => new ServiceTaskResult(true, 201, "{\"id\":\"n-1\",\"score\":5}", null);
        var body = NotifyBody();
        body.MultiInstance!.OutputVariables!.Add(Local("score", "number"));
        body.Service!.OutputMappings.Add(new() { Variable = "score", Path = "score", DataType = "number", IsArray = false, Required = true, Validation = "score <= limit" });
        var variables = Suppliers("only");
        variables.Add(new() { Id = 7, Name = "limit", DataType = "number", Scope = "shared", SharedKey = limitKey, Access = "read" });
        var definition = Workflow(Key("publication"), body, variables);
        // The response's status would select this boundary; the saved candidate has none.
        AddBoundary(definition, 4, "createdFailure", new() { HttpStatusCodes = [201] });
        AddBoundary(definition, 6, "anyFailure", null);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();
        // The limit drops after the item staged with 10: 5 passes when mapped, fails when published.
        receiver.BeforeNext = () => SetSharedAsync(host, limitKey, 1).GetAwaiter().GetResult();
        await ProcessAsync(host, itemJob);
        await DrainAsync(host, instanceId);

        Assert.Equal(1, receiver.Count);
        Assert.Equal(AutomaticMultiInstanceValues.ResultInvalid, (await LatestAsync(instanceId, "anyFailure")).GetString());
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "createdFailure")).ValueKind);
        await using var db = fixture.CreateDbContext();
        Assert.Contains(await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync(),
            nodeId => nodeId == 7);
    }

    [Fact]
    public async Task Interrupting_timer_during_the_call_cancels_the_body_and_fences_the_late_response()
    {
        await using var host = OpenHost();
        receiver.Respond = request => Created("notice-" + Supplier(request));
        var definition = Workflow(Key("timer"), NotifyBody(), Suppliers("slow", "queued"));
        definition.FlowNodes.Add(new() { Id = 6, Name = "Too slow", Type = BpmnFlowNodeTypes.TimerBoundaryEvent, AttachedToRef = 2,
            Timer = new TimerDefinitionModel { TimeDuration = "PT1H" } });
        definition.FlowNodes.Add(new() { Id = 7, Name = "Timed out", Type = BpmnFlowNodeTypes.EndEvent, X = 500, Y = 300 });
        definition.SequenceFlows.Add(new() { Id = 601, SourceRef = 6, TargetRef = 7 });
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var timerJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.TimerBoundary)).Single();
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem))[0];
        // The timer fires after the item staged and while its request is in flight.
        receiver.BeforeNext = () => Task.Run(() => ProcessAsync(host, timerJob)).GetAwaiter().GetResult();
        await ProcessAsync(host, itemJob);

        Assert.Equal(1, receiver.Count);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Contains(await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync(),
            nodeId => nodeId == 7);
        var bodyRow = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId);
        Assert.Equal(MultiInstanceRecordStatuses.Cancelled, bodyRow.Status);
        var items = await db.MultiInstanceActivityItems.Where(row => row.MultiInstanceExecutionId == bodyRow.Id).ToListAsync();
        Assert.All(items, item => Assert.Equal("cancelled", item.Status));
        Assert.All(items, item => Assert.Null(item.CandidateJson));
        Assert.Equal("[]", (await LatestAsync(instanceId, "notificationResults")).GetRawText());
        Assert.Empty(await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem));
    }

    [Fact]
    public async Task An_entry_failure_has_no_status_so_a_status_matched_boundary_never_takes_it()
    {
        await using var host = OpenHost();
        var sharedKey = $"tests.service.items.{Guid.NewGuid():N}";
        using (var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
                   new CreateSharedVariableRequest(sharedKey, "string", IsArray: true, Nullable: false, HasValue: false, null)))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = NotifyBody();
        body.MultiInstance!.CollectionVariable = "sharedSuppliers";
        body.Job = new JobPolicyModel { RetryDelays = ["PT1S"] };
        var variables = Suppliers();
        variables.Add(new() { Id = 8, Name = "sharedSuppliers", DataType = "string", IsArray = true, Scope = "shared", SharedKey = sharedKey, Access = "read" });
        var definition = Workflow(Key("entry"), body, variables);
        AddBoundary(definition, 4, "serverFailure", new() { HttpStatusRanges = [new() { From = 500, To = 599 }] });
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        var entry = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.AsyncBefore)).Single();

        await ProcessAsync(host, entry);
        Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(entry));
        await ProcessAsync(host, entry);

        await using var db = fixture.CreateDbContext();
        var incident = await db.WorkflowIncidents.SingleAsync(row => row.JobId == entry && row.Status == WorkflowIncidentStatuses.Open);
        Assert.Equal("job_execution_failed", incident.Type);
        Assert.Contains(AutomaticMultiInstanceValues.SourceInvalid, incident.Details);
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "serverFailure")).ValueKind);
        Assert.False(await db.MultiInstanceExecutions.AnyAsync(row => row.InstanceId == instanceId));
        Assert.Equal(0, receiver.Count);
    }

    [Fact]
    public async Task A_template_reading_a_shared_alias_with_no_value_waits_in_an_operational_incident_not_the_boundary()
    {
        await using var host = OpenHost();
        var baseKey = $"tests.service.base.{Guid.NewGuid():N}";
        using (var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
                   new CreateSharedVariableRequest(baseKey, "string", IsArray: false, Nullable: false, HasValue: false, null)))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var variables = Suppliers("only");
        variables.Add(new() { Id = 8, Name = "apiBase", DataType = "string", Scope = "shared", SharedKey = baseKey, Access = "read" });
        var definition = Workflow(Key("unset-shared"), NotifyBody(url: "${apiBase}/send"), variables);
        AddBoundary(definition, 4, "anyFailure", null);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();
        await ProcessAsync(host, itemJob);

        Assert.Equal(0, receiver.Count);
        await using var db = fixture.CreateDbContext();
        var incident = await db.WorkflowIncidents.SingleAsync(row => row.JobId == itemJob && row.Status == WorkflowIncidentStatuses.Open);
        Assert.Equal(WorkflowIncidentTypes.JobConflict, incident.Type);
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "anyFailure")).ValueKind);
        Assert.Equal(MultiInstanceRecordStatuses.Active,
            (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId)).Status);
    }

    [Fact]
    public async Task A_setting_rotated_between_attempts_keeps_the_staged_value_for_that_item_and_reaches_the_next_item()
    {
        await using var host = OpenHost();
        var name = $"svcmiToken{Guid.NewGuid():N}";
        using var createdSetting = await SendAsync(host, HttpMethod.Post, "/api/workflow-settings",
            new CreateWorkflowSettingRequest(null, name, Json("old-token")));
        Assert.Equal(HttpStatusCode.Created, createdSetting.StatusCode);
        var setting = (await createdSetting.Content.ReadFromJsonAsync<WorkflowSettingDto>())!;
        var attempts = 0;
        receiver.Respond = request => Supplier(request) == "first" && Interlocked.Increment(ref attempts) == 1
            ? new ServiceTaskResult(true, 503, null, null)
            : Created("notice-" + Supplier(request));
        var body = NotifyBody("sequential");
        body.Job = new JobPolicyModel { RetryDelays = ["PT1S"] };
        body.Service!.Headers.Add(new() { Name = "Authorization", Value = "Bearer ${setting." + name + "}" });
        var published = await PublishAsync(host, Workflow(Key("rotation"), body, Suppliers("first", "second")));
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var first = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();
        await ProcessAsync(host, first);
        Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(first));
        using (var rotated = await SendAsync(host, HttpMethod.Put, $"/api/workflow-settings/{setting.Id}",
                   new UpdateWorkflowSettingRequest(Json("new-token"), null, setting.UpdatedAt)))
            Assert.True(rotated.IsSuccessStatusCode, await rotated.Content.ReadAsStringAsync());
        await DrainAsync(host, instanceId);

        Assert.Equal(new[] { "Bearer old-token", "Bearer old-token", "Bearer new-token" },
            receiver.Requests.Select(request => Header(request, "Authorization")));
        Assert.Equal(new[] { "first", "first", "second" }, receiver.Requests.Select(Supplier));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        // A staged credential stays in the settled item for no longer than the body.
        Assert.All(await db.MultiInstanceActivityItems.Where(row => row.InstanceId == instanceId).ToListAsync(),
            item => Assert.Null(item.StagedInputJson));
    }

    private async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program>? host, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        ApiTestAuth.Authorize(request, "service-admin", "admin");
        return await (host?.CreateClient() ?? fixture.Client).SendAsync(request);
    }

    private async Task<WorkflowDetailDto> PublishAsync(WebApplicationFactory<Program> host, WorkflowModel definition)
    {
        using var response = await SendAsync(host, HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(definition, true));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonSerializer.Deserialize<WorkflowDetailDto>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<long> StartAsync(WebApplicationFactory<Program> host, long workflowId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>().StartInstanceSlimAsync(
            workflowId, null, new ActorContext("starter", ["User"], new Dictionary<string, string>()), null, null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        startedInstances.Add(started.Id);
        return started.Id;
    }

    private async Task CreateSharedAsync(WebApplicationFactory<Program> host, string key, int value)
    {
        using var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
            new CreateSharedVariableRequest(key, "number", IsArray: false, Nullable: false, HasValue: true, Json(value)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    private async Task SetSharedAsync(WebApplicationFactory<Program> host, string key, int value)
    {
        using var metadata = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{key}");
        var revision = (await metadata.Content.ReadFromJsonAsync<SharedVariableMetadataDto>())!.Revision;
        using var updated = await SendAsync(host, HttpMethod.Put, $"/api/shared-variables/{key}/value",
            new UpdateSharedVariableValueRequest(Json(value), revision));
        Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync());
    }

    private async Task SetSharedStringAsync(WebApplicationFactory<Program> host, string key, string value)
    {
        using var metadata = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{key}");
        var revision = (await metadata.Content.ReadFromJsonAsync<SharedVariableMetadataDto>())!.Revision;
        using var updated = await SendAsync(host, HttpMethod.Put, $"/api/shared-variables/{key}/value",
            new UpdateSharedVariableValueRequest(Json(value), revision));
        Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync());
    }

    private async Task<double> SharedValueAsync(WebApplicationFactory<Program> host, string key)
    {
        using var response = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{key}/value");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("value").GetDouble();
    }

    private async Task RetryIncidentAsync(WebApplicationFactory<Program> host, long incidentId)
    {
        using var response = await SendAsync(host, HttpMethod.Post, $"/api/incidents/{incidentId}/retry");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<List<long>> OpenJobIdsAsync(long instanceId, string kind)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.AsNoTracking()
            .Where(job => job.InstanceId == instanceId && job.Kind == kind
                && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry))
            .OrderBy(job => job.Id).Select(job => job.Id).ToListAsync();
    }

    private async Task<int> CountJobsAsync(long instanceId, string kind)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.CountAsync(job => job.InstanceId == instanceId && job.Kind == kind);
    }

    private async Task<string> JobStatusAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.Where(job => job.Id == jobId).Select(job => job.Status).SingleAsync();
    }

    private async Task ProcessNextAsync(WebApplicationFactory<Program> host, long instanceId, string kind) =>
        await ProcessAsync(host, (await OpenJobIdsAsync(instanceId, kind)).First());

    // Leases and runs one job. Workers lease due control work first, so a dispatch or
    // completion wake of the same instance runs first, as a Worker would run it.
    private async Task ProcessAsync(WebApplicationFactory<Program> host, long jobId)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await using (var promote = fixture.CreateDbContext())
            {
                // Due a minute ago, so the job is due even when the database clock runs behind this machine's.
                await promote.WorkflowJobs.Where(job => job.Id == jobId).ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, 1_000_000)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            }
            await using var scope = host.Services.CreateAsyncScope();
            var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                new WorkflowJobLeaseRequest($"service-mi:{Guid.NewGuid():N}", MaxCount: 1, MaxActivityCount: 1,
                    MaxPerInstance: 4, LeaseDuration: TimeSpan.FromMinutes(1)), CancellationToken.None);
            var lease = Assert.Single(leases);
            await using (var db = fixture.CreateDbContext())
            {
                var target = await db.WorkflowJobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
                Assert.True(lease.Job.Id == jobId
                    || lease.Job.InstanceId == target.InstanceId && lease.Job.QueueClass == WorkflowJobClasses.Control,
                    $"Leased #{lease.Job.Id} ({lease.Job.Kind}) instead of #{jobId}.");
            }
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
            if (lease.Job.Id == jobId) return;
        }
        Assert.Fail($"Job #{jobId} was not leased.");
    }

    // Runs runnable jobs of the instance until none remains, the way Workers eventually would.
    private async Task DrainAsync(WebApplicationFactory<Program> host, long instanceId, int maxJobs = 64)
    {
        for (var processed = 0; processed < maxJobs; processed++)
        {
            await using var db = fixture.CreateDbContext();
            var next = await db.WorkflowJobs.AsNoTracking()
                .Where(job => job.InstanceId == instanceId
                    && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry))
                .OrderBy(job => job.Id).Select(job => (long?)job.Id).FirstOrDefaultAsync();
            if (next is not long jobId) return;
            await ProcessAsync(host, jobId);
        }
        throw new InvalidOperationException($"Instance #{instanceId} still had runnable jobs after {maxJobs}.");
    }

    private async Task<JsonElement> LatestAsync(long instanceId, string name)
    {
        await using var db = fixture.CreateDbContext();
        return (await db.InstanceVariables.Where(row => row.InstanceId == instanceId && row.VariableName == name)
            .OrderByDescending(row => row.Id).FirstAsync()).ValueJson.RootElement.Clone();
    }

    /// <summary>
    /// The remote endpoint: records every request in arrival order, answers through
    /// <see cref="Respond"/>, and runs one hook before answering the next request, while
    /// the item holds no lock.
    /// </summary>
    private sealed class ControlledReceiver : IServiceTaskInvoker
    {
        private readonly object gate = new();
        private readonly List<ServiceTaskRequest> requests = [];
        private Action? beforeNext;

        public Func<ServiceTaskRequest, ServiceTaskResult> Respond { get; set; } = _ => new(true, 200, "{}", null);

        public Action? BeforeNext
        {
            set => Volatile.Write(ref beforeNext, value);
        }

        public int Count
        {
            get { lock (gate) return requests.Count; }
        }

        public IReadOnlyList<ServiceTaskRequest> Requests
        {
            get { lock (gate) return requests.ToArray(); }
        }

        public Task<ServiceTaskResult> InvokeAsync(ServiceTaskRequest request, CancellationToken cancellationToken)
        {
            lock (gate) requests.Add(request);
            Interlocked.Exchange(ref beforeNext, null)?.Invoke();
            return Task.FromResult(Respond(request));
        }
    }
}
