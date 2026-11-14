using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Http;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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

    // A host with the release switch open whose REST invoker is the controlled receiver.
    // A test may change further services after that, such as restoring the real invoker.
    private WebApplicationFactory<Program> OpenHost(Action<IServiceCollection>? configure = null) =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var open = new AutomaticMultiInstanceOptions { AllowInDevelopment = true };
            open.ResolveForEnvironment(isDevelopment: true);
            services.RemoveAll<AutomaticMultiInstanceOptions>();
            services.AddSingleton(open);
            services.RemoveAll<IServiceTaskInvoker>();
            services.AddSingleton<IServiceTaskInvoker>(receiver);
            configure?.Invoke(services);
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

    // A conditional boundary on the body that an instance write of stop = true wakes.
    private static void AddStopBoundary(WorkflowModel definition, bool cancelActivity, bool stopAtStart = false)
    {
        definition.Variables.Add(new() { Id = 90, Name = "stop", DataType = WorkflowVariableTypes.Boolean, DefaultValue = Json(stopAtStart) });
        definition.FlowNodes.Add(new()
        {
            Id = 6, Name = "Stop requested", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = 2,
            CancelActivity = cancelActivity, Conditional = new ConditionalDefinitionModel { Condition = "stop == true" }
        });
        definition.FlowNodes.Add(new() { Id = 7, Name = "Stopped", Type = BpmnFlowNodeTypes.EndEvent, X = 500, Y = 300 });
        definition.SequenceFlows.Add(new() { Id = 601, SourceRef = 6, TargetRef = 7 });
    }

    [Fact]
    public async Task An_interrupting_conditional_boundary_during_the_call_cancels_the_body_once_and_fences_the_late_response()
    {
        await using var host = OpenHost();
        receiver.Respond = request => Created("notice-" + Supplier(request));
        var definition = Workflow(Key("condition-interrupt"), NotifyBody(), Suppliers("held", "queued"));
        AddStopBoundary(definition, cancelActivity: true);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJobs = await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem);
        Assert.Equal(2, itemJobs.Count);
        // An administrative write makes the condition true while the first request is in
        // flight; the item holds no lock, so the writer captures the boundary at once.
        receiver.BeforeNext = () => SetInstanceVariableAsync(host, instanceId, "stop", true).GetAwaiter().GetResult();
        await ProcessAsync(host, itemJobs[0]);

        Assert.Equal(1, receiver.Count);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        var tokens = await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync();
        Assert.Single(tokens, nodeId => nodeId == 7);
        Assert.DoesNotContain(3, tokens);
        Assert.Equal(1, await db.InstanceHistory.CountAsync(row => row.InstanceId == instanceId
            && row.Note == InstanceHistoryNotes.ConditionalTriggered));
        var subscription = await db.ConditionalBoundarySubscriptions.SingleAsync(row => row.InstanceId == instanceId);
        Assert.Equal(ConditionalBoundarySubscriptionStatuses.Completed, subscription.Status);
        Assert.Equal(1, subscription.Occurrence);
        // The late response saved nothing and published nothing; no item job is left to run.
        var bodyRow = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId);
        Assert.Equal(MultiInstanceRecordStatuses.Cancelled, bodyRow.Status);
        var items = await db.MultiInstanceActivityItems.Where(row => row.MultiInstanceExecutionId == bodyRow.Id).ToListAsync();
        Assert.All(items, item => Assert.Equal("cancelled", item.Status));
        Assert.All(items, item => Assert.Null(item.CandidateJson));
        Assert.Equal("[]", (await LatestAsync(instanceId, "notificationResults")).GetRawText());
        Assert.All(await db.WorkflowJobs.Where(row => row.InstanceId == instanceId).Select(row => row.Status).ToListAsync(),
            status => Assert.Contains(status, WorkflowJobStatuses.Terminal));
    }

    [Fact]
    public async Task A_non_interrupting_conditional_boundary_runs_its_branch_while_the_body_keeps_running_and_publishes_everything()
    {
        await using var host = OpenHost();
        receiver.Respond = request => Created("notice-" + Supplier(request));
        var definition = Workflow(Key("condition-branch"), NotifyBody(), Suppliers("first", "second"));
        AddStopBoundary(definition, cancelActivity: false);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJobs = await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem);
        Assert.Equal(2, itemJobs.Count);
        receiver.BeforeNext = () => SetInstanceVariableAsync(host, instanceId, "stop", true).GetAwaiter().GetResult();
        await ProcessAsync(host, itemJobs[0]);

        await using (var db = fixture.CreateDbContext())
        {
            // The branch ran to its end on a sibling token; the body kept the item that
            // was in flight and still owns its second item.
            Assert.Single(await db.ExecutionTokens.Where(row => row.InstanceId == instanceId && row.NodeId == 7
                && row.Status == ExecutionTokenRecordStatuses.Completed).ToListAsync());
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
            var bodyRow = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId);
            Assert.Equal(MultiInstanceRecordStatuses.Active, bodyRow.Status);
            Assert.Equal("completed", (await db.MultiInstanceActivityItems.SingleAsync(row =>
                row.MultiInstanceExecutionId == bodyRow.Id && row.ItemIndex == 0)).Status);
            Assert.Equal(new[] { itemJobs[1] }, await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem));
        }
        await DrainAsync(host, instanceId);

        Assert.Equal(new[] { "first", "second" }, receiver.Requests.Select(Supplier));
        var results = await LatestAsync(instanceId, "notificationResults");
        Assert.Equal(new[] { "completed", "completed" }, results.EnumerateArray().Select(row => row.GetProperty("status").GetString()));
        Assert.Equal(new[] { "notice-first", "notice-second" },
            results.EnumerateArray().Select(row => row.GetProperty("variables").GetProperty("notificationId").GetString()));
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        var reached = await after.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync();
        Assert.Contains(3, reached);
        Assert.Single(reached, nodeId => nodeId == 7);
        Assert.Equal(1, await after.InstanceHistory.CountAsync(row => row.InstanceId == instanceId
            && row.Note == InstanceHistoryNotes.ConditionalTriggered));
    }

    [Fact]
    public async Task An_interrupting_condition_already_true_at_entry_wins_before_any_body_or_request_exists()
    {
        await using var host = OpenHost();
        var definition = Workflow(Key("condition-entry"), NotifyBody(), Suppliers("never"));
        AddStopBoundary(definition, cancelActivity: true, stopAtStart: true);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await DrainAsync(host, instanceId);

        Assert.Equal(0, receiver.Count);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(new[] { 7 }, await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync());
        Assert.False(await db.MultiInstanceExecutions.AnyAsync(row => row.InstanceId == instanceId));
        Assert.False(await db.WorkflowJobs.AnyAsync(row => row.InstanceId == instanceId));
        Assert.Equal("[]", (await LatestAsync(instanceId, "notificationResults")).GetRawText());
    }

    [Fact]
    public async Task A_shared_typed_collection_is_frozen_at_initialization_and_its_items_run_and_aggregate_in_order()
    {
        await using var host = OpenHost();
        var sharedKey = $"tests.service.suppliers.{Guid.NewGuid():N}";
        using (var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
                   new CreateSharedVariableRequest(sharedKey, "string", IsArray: true, Nullable: false, HasValue: true,
                       Json(new[] { "shared-a", "shared-b", "shared-c" }))))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        receiver.Respond = request => Created("notice-" + Supplier(request));
        var body = NotifyBody();
        body.MultiInstance!.CollectionVariable = "sharedSuppliers";
        // The collection's alias in a template reads the body's frozen copy as well.
        body.Service!.Body = "{\"supplier\":${mi.item},\"index\":${mi.index},\"total\":${mi.total},\"all\":${sharedSuppliers}}";
        var variables = Suppliers();
        variables.Add(new() { Id = 8, Name = "sharedSuppliers", DataType = "string", IsArray = true, Scope = "shared", SharedKey = sharedKey, Access = "read" });
        var published = await PublishAsync(host, Workflow(Key("shared-collection"), body, variables));
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        // Another writer replaces the catalog value after the body froze it.
        await SetSharedJsonAsync(host, sharedKey, Json(new[] { "late" }));
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJobs = await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem);
        Assert.Equal(3, itemJobs.Count);
        foreach (var jobId in itemJobs.AsEnumerable().Reverse()) await ProcessAsync(host, jobId);
        await DrainAsync(host, instanceId);

        Assert.Equal(new[] { "shared-a", "shared-b", "shared-c" }, receiver.Requests.Select(Supplier).Order());
        foreach (var request in receiver.Requests)
        {
            using var sent = JsonDocument.Parse(request.Body!);
            Assert.Equal(3, sent.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(new[] { "shared-a", "shared-b", "shared-c" },
                sent.RootElement.GetProperty("all").EnumerateArray().Select(value => value.GetString()));
        }
        var results = await LatestAsync(instanceId, "notificationResults");
        Assert.Equal(new[] { "shared-a", "shared-b", "shared-c" }, results.EnumerateArray().Select(row => row.GetProperty("item").GetString()));
        Assert.Equal(new[] { "notice-shared-a", "notice-shared-b", "notice-shared-c" },
            results.EnumerateArray().Select(row => row.GetProperty("variables").GetProperty("notificationId").GetString()));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        // The shared value stays the other writer's, and is never copied into the instance.
        Assert.False(await db.InstanceVariables.AnyAsync(row => row.InstanceId == instanceId && row.VariableName == "sharedSuppliers"));
        using var current = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{sharedKey}/value");
        using var document = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "late" }, document.RootElement.GetProperty("value").EnumerateArray().Select(entry => entry.GetString()));
    }

    [Fact]
    public async Task Two_item_incidents_stay_open_together_and_each_retry_resends_and_publishes_only_its_own_item()
    {
        await using var host = OpenHost();
        var refusing = new ConcurrentDictionary<string, bool> { ["flaky-1"] = true, ["flaky-2"] = true };
        receiver.Respond = request => refusing.TryGetValue(Supplier(request), out var refuse) && refuse
            ? new ServiceTaskResult(true, 503, null, null)
            : Created("notice-" + Supplier(request));
        var body = NotifyBody();
        body.Job = new JobPolicyModel { RetryDelays = ["PT10S"] };
        var published = await PublishAsync(host, Workflow(Key("incidents"), body, Suppliers("steady", "flaky-1", "flaky-2")));
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJobs = await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem);
        Assert.Equal(3, itemJobs.Count);
        foreach (var jobId in itemJobs) await ProcessAsync(host, jobId);
        // Each item's key is its own job id, so the requests name each item's job.
        var jobOf = new[] { "steady", "flaky-1", "flaky-2" }.ToDictionary(supplier => supplier, supplier =>
            long.Parse(Assert.Single(receiver.Requests.Where(request => Supplier(request) == supplier)
                .Select(request => Header(request, "Idempotency-Key")).Distinct())["flowbit-supplier-".Length..]));
        Assert.Equal(itemJobs.Order(), jobOf.Values.Order());
        Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(jobOf["flaky-1"]));
        Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(jobOf["flaky-2"]));
        await ProcessAsync(host, jobOf["flaky-1"]);
        await ProcessAsync(host, jobOf["flaky-2"]);

        long firstIncident, secondIncident;
        await using (var db = fixture.CreateDbContext())
        {
            var open = await db.WorkflowIncidents.Where(row => row.InstanceId == instanceId
                && row.Status == WorkflowIncidentStatuses.Open).OrderBy(row => row.Id).ToListAsync();
            Assert.Equal(new long?[] { jobOf["flaky-1"], jobOf["flaky-2"] }, open.Select(row => row.JobId));
            Assert.All(open, incident => Assert.Equal("job_execution_failed", incident.Type));
            (firstIncident, secondIncident) = (open[0].Id, open[1].Id);
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
            Assert.Equal(MultiInstanceRecordStatuses.Active,
                (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId)).Status);
        }
        Assert.Equal("[]", (await LatestAsync(instanceId, "notificationResults")).GetRawText());

        refusing["flaky-1"] = false;
        await RetryIncidentAsync(host, firstIncident);
        await DrainAsync(host, instanceId);

        // Only the retried item was sent again and published; the body keeps waiting.
        Assert.Equal(1, receiver.Requests.Count(request => Supplier(request) == "steady"));
        Assert.Equal(3, receiver.Requests.Count(request => Supplier(request) == "flaky-1"));
        Assert.Equal(2, receiver.Requests.Count(request => Supplier(request) == "flaky-2"));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.NotEqual(WorkflowIncidentStatuses.Open, (await db.WorkflowIncidents.SingleAsync(row => row.Id == firstIncident)).Status);
            Assert.Equal(WorkflowIncidentStatuses.Open, (await db.WorkflowIncidents.SingleAsync(row => row.Id == secondIncident)).Status);
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
            var bodyRow = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId);
            Assert.Equal(MultiInstanceRecordStatuses.Active, bodyRow.Status);
            var statuses = await db.MultiInstanceActivityItems.Where(row => row.MultiInstanceExecutionId == bodyRow.Id)
                .OrderBy(row => row.ItemIndex).Select(row => row.Status).ToListAsync();
            Assert.Equal("completed", statuses[0]);
            Assert.Equal("completed", statuses[1]);
            Assert.NotEqual("completed", statuses[2]);
        }
        Assert.Equal("[]", (await LatestAsync(instanceId, "notificationResults")).GetRawText());

        refusing["flaky-2"] = false;
        await RetryIncidentAsync(host, secondIncident);
        await DrainAsync(host, instanceId);

        Assert.Equal(1, receiver.Requests.Count(request => Supplier(request) == "steady"));
        Assert.Equal(3, receiver.Requests.Count(request => Supplier(request) == "flaky-1"));
        Assert.Equal(3, receiver.Requests.Count(request => Supplier(request) == "flaky-2"));
        foreach (var (supplier, jobId) in jobOf)
            Assert.Equal(new[] { "flowbit-supplier-" + jobId }, receiver.Requests.Where(request => Supplier(request) == supplier)
                .Select(request => Header(request, "Idempotency-Key")).Distinct());
        var results = await LatestAsync(instanceId, "notificationResults");
        Assert.Equal(new[] { "steady", "flaky-1", "flaky-2" }, results.EnumerateArray().Select(row => row.GetProperty("item").GetString()));
        Assert.All(results.EnumerateArray(), row => Assert.Equal("completed", row.GetProperty("status").GetString()));
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.False(await after.WorkflowIncidents.AnyAsync(row => row.InstanceId == instanceId && row.Status == WorkflowIncidentStatuses.Open));
    }

    [Fact]
    public async Task A_response_over_the_body_limit_fails_the_item_in_the_real_invoker_and_its_2xx_status_selects_the_boundary()
    {
        // Over the 256-byte limit this host sets, and holding text no item record may repeat.
        var marker = "response-marker-" + Guid.NewGuid().ToString("N");
        await using var remote = await LoopbackReceiver.StartAsync(200,
            JsonSerializer.Serialize(new { id = "n-1", note = marker, pad = new string('x', 1024) }));
        await using var host = OpenHost(services =>
        {
            // The deployment's own invoker and HTTP client, with a small response limit.
            services.RemoveAll<IServiceTaskInvoker>();
            services.AddHttpClient<IServiceTaskInvoker, HttpServiceTaskInvoker>(client => client.Timeout = Timeout.InfiniteTimeSpan);
            services.RemoveAll<ServiceTaskOptions>();
            services.AddSingleton(new ServiceTaskOptions { MaxResponseBodyBytes = 256 });
        });
        var definition = Workflow(Key("oversized"), NotifyBody(url: remote.Url("send")), Suppliers("only"));
        AddBoundary(definition, 4, "successFailure", new() { HttpStatusRanges = [new() { From = 200, To = 299 }] });
        AddBoundary(definition, 6, "anyFailure", null);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await DrainAsync(host, instanceId);

        Assert.Equal(0, receiver.Count);
        await using var db = fixture.CreateDbContext();
        var itemJob = await db.WorkflowJobs.SingleAsync(row => row.InstanceId == instanceId && row.Kind == WorkflowJobKinds.MultiInstanceItem);
        Assert.Equal(new[] { "flowbit-supplier-" + itemJob.Id }, remote.IdempotencyKeys);
        // The status arrived before the body went over the limit, so the 2xx range wins.
        Assert.Equal(WorkflowEngineService.AutomaticServiceFailureCode, (await LatestAsync(instanceId, "successFailure")).GetString());
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "anyFailure")).ValueKind);
        var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == instanceId);
        Assert.Equal(WorkflowEngineService.AutomaticServiceFailureCode, item.ErrorCode);
        Assert.Equal("The REST call failed with HTTP status 200.", item.ErrorDescription);
        Assert.Null(item.CandidateJson);
        var visit = await db.NodeExecutions.SingleAsync(row => row.AutomaticMultiInstanceItemId == item.Id);
        Assert.Equal(item.ErrorDescription, visit.ErrorDescription);
        var failedRow = (await LatestAsync(instanceId, "notificationResults")).EnumerateArray().Single();
        Assert.Equal("failed", failedRow.GetProperty("status").GetString());
        Assert.Equal("{}", failedRow.GetProperty("variables").GetRawText());
        // Nothing the response carried reached the instance.
        Assert.DoesNotContain((await db.InstanceVariables.Where(variable => variable.InstanceId == instanceId).ToListAsync())
            .Select(variable => variable.ValueJson.RootElement.GetRawText()), text => text.Contains(marker));
        Assert.Contains(await db.ExecutionTokens.Where(token => token.InstanceId == instanceId).Select(token => token.NodeId).ToListAsync(),
            nodeId => nodeId == 5);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(instance => instance.Id == instanceId)).Status);
        Assert.False(await db.WorkflowIncidents.AnyAsync(incident => incident.InstanceId == instanceId));
    }

    [Fact]
    public async Task A_crash_after_the_call_and_before_the_candidate_is_saved_resends_with_the_same_job_id_and_publishes_once()
    {
        await using var host = OpenHost();
        var answered = 0;
        receiver.Respond = _ => Created("notice-" + Interlocked.Increment(ref answered));
        var published = await PublishAsync(host, Workflow(Key("crash"), NotifyBody(), Suppliers("only")));
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();

        // The Worker stops while the request is in flight: the receiver has it, and the
        // call ends in the Worker's cancellation before any response reaches the item.
        using var crash = new CancellationTokenSource();
        var held = 0;
        receiver.WhileHeld = (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref held) == 1)
            {
                crash.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        };
        var lease = await LeaseAsync(host, itemJob);
        await using (var scope = host.Services.CreateAsyncScope())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, crash.Token));

        Assert.Equal(1, receiver.Count);
        Assert.Equal(0, Volatile.Read(ref answered));
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.AsNoTracking().SingleAsync(row => row.Id == itemJob);
            Assert.Equal(WorkflowJobStatuses.Running, job.Status);
            var item = await db.MultiInstanceActivityItems.AsNoTracking().SingleAsync(row => row.Id == job.AutomaticMultiInstanceItemId);
            Assert.NotNull(item.StagedInputJson);
            Assert.Null(item.CandidateJson);
            // Nobody renews the stopped Worker's lease, so it runs out.
            await db.WorkflowJobs.Where(row => row.Id == itemJob).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        // Another Worker recovers the expired lease and runs the item again.
        await ProcessAsync(host, itemJob);
        await DrainAsync(host, instanceId);

        var requests = receiver.Requests;
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal("flowbit-supplier-" + itemJob, Header(request, "Idempotency-Key")));
        // Rendered again from the item's saved context: the same request both times.
        Assert.Equal(requests[0].Url, requests[1].Url);
        Assert.Equal(requests[0].Body, requests[1].Body);
        Assert.Equal(1, Volatile.Read(ref answered));
        var results = await LatestAsync(instanceId, "notificationResults");
        var only = Assert.Single(results.EnumerateArray());
        Assert.Equal("completed", only.GetProperty("status").GetString());
        Assert.Equal("notice-1", only.GetProperty("variables").GetProperty("notificationId").GetString());
        await using var after = fixture.CreateDbContext();
        var recovered = await after.WorkflowJobs.SingleAsync(row => row.Id == itemJob);
        Assert.Equal(WorkflowJobStatuses.Completed, recovered.Status);
        Assert.Equal(2, recovered.AttemptCount);
        var attempts = await after.WorkflowJobAttempts.Where(row => row.JobId == itemJob).OrderBy(row => row.AttemptNumber).ToListAsync();
        Assert.Equal(2, attempts.Count);
        Assert.Equal(("leaseLost", "leaseExpired"), (attempts[0].Status, attempts[0].FailureCode));
        var settled = await after.MultiInstanceActivityItems.SingleAsync(row => row.Id == recovered.AutomaticMultiInstanceItemId);
        Assert.Equal("completed", settled.Status);
        Assert.Single(await after.NodeExecutions.Where(row => row.AutomaticMultiInstanceItemId == settled.Id).ToListAsync());
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.False(await after.WorkflowIncidents.AnyAsync(row => row.InstanceId == instanceId));
    }

    [Fact]
    public async Task Two_Workers_under_queue_pressure_invoke_each_item_once_and_keep_each_bodys_admission()
    {
        await using var first = OpenHost();
        await using var second = OpenHost();
        receiver.Respond = request => Created("notice-" + Supplier(request));
        // The first two requests wait for each other. Each Worker below runs one job at a
        // time, so only two Workers each holding a request can meet.
        var rendezvous = new Rendezvous(2, TimeSpan.FromSeconds(30));
        receiver.WhileHeld = (_, _) => rendezvous.ArriveAsync();
        var parallel = await PublishAsync(first, Workflow(Key("workers-parallel"), NotifyBody(),
            Suppliers("a-0", "a-1", "a-2", "a-3", "a-4", "a-5")));
        var sequential = await PublishAsync(first, Workflow(Key("workers-sequential"), NotifyBody("sequential"),
            Suppliers("b-0", "b-1", "b-2")));
        long[] instances = [await StartAsync(first, parallel.Id), await StartAsync(first, sequential.Id)];

        var processed = new ConcurrentQueue<(string Worker, string Kind)>();
        using var failed = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await Task.WhenAll(Task.Run(() => WorkAsync(first, "worker-1")), Task.Run(() => WorkAsync(second, "worker-2")));

        Assert.False(rendezvous.TimedOut, "The two Workers never had a request in flight at the same time.");
        Assert.All(new[] { "worker-1", "worker-2" }, worker =>
            Assert.Contains(processed, entry => entry.Worker == worker && entry.Kind == WorkflowJobKinds.MultiInstanceItem));
        var requests = receiver.Requests;
        Assert.Equal(new[] { "a-0", "a-1", "a-2", "a-3", "a-4", "a-5", "b-0", "b-1", "b-2" }, requests.Select(Supplier).Order());
        await using var db = fixture.CreateDbContext();
        var itemJobs = await db.WorkflowJobs.AsNoTracking().Where(job => job.InstanceId != null
            && instances.Contains(job.InstanceId.Value) && job.Kind == WorkflowJobKinds.MultiInstanceItem).ToListAsync();
        Assert.Equal(9, itemJobs.Count);
        // Each item was leased once and sent once, under its own job id.
        Assert.All(itemJobs, job => Assert.Equal((WorkflowJobStatuses.Completed, 1), (job.Status, job.AttemptCount)));
        Assert.Equal(itemJobs.Select(job => "flowbit-supplier-" + job.Id).Order(),
            requests.Select(request => Header(request, "Idempotency-Key")).Order());
        Assert.Equal(new[] { "a-0", "a-1", "a-2", "a-3", "a-4", "a-5" }, (await LatestAsync(instances[0], "notificationResults"))
            .EnumerateArray().Select(row => row.GetProperty("item").GetString()));
        Assert.Equal(new[] { "b-0", "b-1", "b-2" }, (await LatestAsync(instances[1], "notificationResults"))
            .EnumerateArray().Select(row => row.GetProperty("item").GetString()));
        foreach (var instanceId in instances)
            Assert.All((await LatestAsync(instanceId, "notificationResults")).EnumerateArray(),
                row => Assert.Equal("completed", row.GetProperty("status").GetString()));
        var items = await db.MultiInstanceActivityItems.AsNoTracking().Where(row => instances.Contains(row.InstanceId)).ToListAsync();
        // The parallel body never had more than its four items active at once.
        var parallelItems = items.Where(row => row.InstanceId == instances[0]).ToList();
        Assert.Equal(4, parallelItems.Max(item => parallelItems.Count(other =>
            other.StartedAt <= item.StartedAt && item.StartedAt < other.CompletedAt)));
        // The sequential body admitted each item only after the previous one completed.
        var sequentialItems = items.Where(row => row.InstanceId == instances[1]).OrderBy(row => row.ItemIndex).ToList();
        for (var index = 1; index < sequentialItems.Count; index++)
            Assert.True(sequentialItems[index].StartedAt >= sequentialItems[index - 1].CompletedAt,
                $"Sequential item {index} started before item {index - 1} completed.");
        Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.InstanceId != null && instances.Contains(row.InstanceId.Value)));
        Assert.Equal(2, await db.WorkflowInstances.CountAsync(row => instances.Contains(row.Id)
            && row.Status == WorkflowInstanceStatuses.Completed));

        // One Worker: lease one job, run it, repeat, until both instances finish. It runs
        // only this test's jobs and fails on any other, as ProcessAsync does.
        async Task WorkAsync(WebApplicationFactory<Program> worker, string name)
        {
            try
            {
                while (!failed.IsCancellationRequested && !await BothFinishedAsync())
                {
                    if (deadline.IsCancellationRequested)
                        throw new TimeoutException($"{name} still had work after two minutes.");
                    IReadOnlyList<WorkflowJobLeaseRecord> leases;
                    await using (var scope = worker.Services.CreateAsyncScope())
                        leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                            new WorkflowJobLeaseRequest($"{name}:{Guid.NewGuid():N}", MaxCount: 1, MaxActivityCount: 1,
                                MaxPerInstance: 4, LeaseDuration: TimeSpan.FromMinutes(1)), CancellationToken.None);
                    if (leases.Count == 0)
                    {
                        // New work is due by this machine's clock, which can run ahead of the database's.
                        await Task.Delay(20);
                        continue;
                    }
                    var lease = Assert.Single(leases);
                    Assert.True(lease.Job.InstanceId is long owner && instances.Contains(owner),
                        $"{name} leased #{lease.Job.Id} ({lease.Job.Kind}) of another test.");
                    processed.Enqueue((name, lease.Job.Kind));
                    await using var run = worker.Services.CreateAsyncScope();
                    await run.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
                }
            }
            catch
            {
                await failed.CancelAsync();
                throw;
            }
        }

        // Both instances completed. An open incident stops the Workers at once.
        async Task<bool> BothFinishedAsync()
        {
            await using var check = fixture.CreateDbContext();
            var incident = await check.WorkflowIncidents.AsNoTracking().FirstOrDefaultAsync(row => row.InstanceId != null
                && instances.Contains(row.InstanceId.Value) && row.Status == WorkflowIncidentStatuses.Open);
            if (incident is not null)
                throw new InvalidOperationException($"Incident {incident.Type} on job #{incident.JobId}: {incident.Details}");
            return await check.WorkflowInstances.CountAsync(row => instances.Contains(row.Id)
                && row.Status == WorkflowInstanceStatuses.Completed) == instances.Length;
        }
    }

    private async Task SetInstanceVariableAsync(WebApplicationFactory<Program> host, long instanceId, string name, object value)
    {
        using var response = await SendAsync(host, HttpMethod.Patch, $"/api/instances/{instanceId}/variables",
            new UpdateInstanceVariablesRequest([new InstanceVariableWriteDto(name, Json(value))],
                "service body runtime test", $"service-mi-update-{Guid.NewGuid():N}"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task SetSharedJsonAsync(WebApplicationFactory<Program> host, string key, JsonElement value)
    {
        using var metadata = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{key}");
        var revision = (await metadata.Content.ReadFromJsonAsync<SharedVariableMetadataDto>())!.Revision;
        using var updated = await SendAsync(host, HttpMethod.Put, $"/api/shared-variables/{key}/value",
            new UpdateSharedVariableValueRequest(value, revision));
        Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync());
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
        var lease = await LeaseAsync(host, jobId);
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
    }

    // Leases one job without running it, after running any due control job of the same
    // instance that a Worker would lease first.
    private async Task<WorkflowJobLeaseRecord> LeaseAsync(WebApplicationFactory<Program> host, long jobId)
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
            if (lease.Job.Id == jobId) return lease;
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
        }
        Assert.Fail($"Job #{jobId} was not leased.");
        return null!;
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
        private Func<ServiceTaskRequest, CancellationToken, Task>? whileHeld;

        public Func<ServiceTaskRequest, ServiceTaskResult> Respond { get; set; } = _ => new(true, 200, "{}", null);

        public Action? BeforeNext
        {
            set => Volatile.Write(ref beforeNext, value);
        }

        /// <summary>
        /// Runs for every request after it is recorded and before it is answered, with the
        /// caller's cancellation token: the request has reached the receiver and its
        /// response has not yet been sent. It can hold the request or throw as the caller's
        /// transport would.
        /// </summary>
        public Func<ServiceTaskRequest, CancellationToken, Task>? WhileHeld
        {
            set => Volatile.Write(ref whileHeld, value);
        }

        public int Count
        {
            get { lock (gate) return requests.Count; }
        }

        public IReadOnlyList<ServiceTaskRequest> Requests
        {
            get { lock (gate) return requests.ToArray(); }
        }

        public async Task<ServiceTaskResult> InvokeAsync(ServiceTaskRequest request, CancellationToken cancellationToken)
        {
            lock (gate) requests.Add(request);
            Interlocked.Exchange(ref beforeNext, null)?.Invoke();
            if (Volatile.Read(ref whileHeld) is { } held) await held(request, cancellationToken);
            return Respond(request);
        }
    }

    /// <summary>
    /// Holds the first <c>parties</c> requests until all of them have arrived. A Worker
    /// loop here runs one job at a time, so two requests held together prove that two
    /// Workers had a request in flight at once. A rendezvous that never completes lets the
    /// held requests go after the timeout and records it, so the test fails on that
    /// rather than waiting forever.
    /// </summary>
    private sealed class Rendezvous(int parties, TimeSpan timeout)
    {
        private readonly TaskCompletionSource met = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrived;
        private int timedOut;

        public bool TimedOut => Volatile.Read(ref timedOut) == 1;

        public async Task ArriveAsync()
        {
            var position = Interlocked.Increment(ref arrived);
            if (position > parties) return;
            if (position == parties) met.TrySetResult();
            try
            {
                await met.Task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                Interlocked.Exchange(ref timedOut, 1);
            }
        }
    }

    /// <summary>
    /// A real HTTP endpoint on loopback with an ephemeral port: it records each request's
    /// idempotency key and answers every request with one fixed status and body.
    /// </summary>
    private sealed class LoopbackReceiver : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly ConcurrentQueue<string> keys = new();

        private LoopbackReceiver(WebApplication app) => this.app = app;

        public IReadOnlyList<string> IdempotencyKeys => keys.ToArray();

        public string Url(string path) => $"{app.Urls.Single()}/{path}";

        public static async Task<LoopbackReceiver> StartAsync(int status, string body)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var receiver = new LoopbackReceiver(builder.Build());
            receiver.app.Run(async context =>
            {
                using (var drain = new MemoryStream()) await context.Request.Body.CopyToAsync(drain);
                receiver.keys.Enqueue(context.Request.Headers["Idempotency-Key"].ToString());
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(body);
            });
            await receiver.app.StartAsync();
            return receiver;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
