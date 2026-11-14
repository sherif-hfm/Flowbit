using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Scripting;
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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// TASK-05C end to end against PostgreSQL: definitions are published over HTTP through
/// a host with the Development release switch open, instances start through the
/// engine, and every entry, dispatch, item, and completion job runs through the
/// registered job processor, as Workers run them.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class AutomaticMultiInstanceScriptRuntimeTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private readonly List<long> startedInstances = [];
    private readonly CountingScriptEvaluator evaluator = new();

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
            services.RemoveAll<IScriptEvaluator>();
            services.AddSingleton<IScriptEvaluator>(evaluator);
        }));

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    private static string Key(string name) => $"script-mi-{name}-{Guid.NewGuid():N}";

    private static WorkflowModel Workflow(string key, FlowNodeModel body, IEnumerable<VariableModel> variables,
        bool boundary = false)
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
        if (boundary)
        {
            definition.Variables.Add(new() { Id = 90, Name = "failure", DataType = "string", Nullable = true });
            definition.FlowNodes.Add(new() { Id = 4, Name = "Caught", Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, AttachedToRef = 2, ErrorVariable = "failure" });
            definition.FlowNodes.Add(new() { Id = 5, Name = "Handled", Type = BpmnFlowNodeTypes.EndEvent, X = 500, Y = 200 });
            definition.SequenceFlows.Add(new() { Id = 401, SourceRef = 4, TargetRef = 5 });
        }
        return definition;
    }

    private static MultiInstanceModel Multi(string mode, string collection, string result,
        params MultiInstanceOutputVariableModel[] outputs) => new()
    {
        Mode = mode, Source = "collection", CollectionVariable = collection, CompletionEvaluation = "afterAll",
        MaxConcurrency = mode == "sequential" ? 1 : 4, OutputVariables = outputs.ToList(), ResultVariable = result
    };

    private static MultiInstanceOutputVariableModel Local(string name, string type = "number") => new()
    {
        Name = name, DataType = type, IsArray = false, Nullable = false
    };

    [Fact]
    public async Task NCalc_sequential_body_runs_one_item_at_a_time_and_publishes_ordered_local_results()
    {
        await using var host = OpenHost();
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Scale each amount", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
            ScriptFormat = ScriptFormats.NCalc, X = 200, Y = 20,
            MultiInstance = Multi("sequential", "amounts", "calculationResults", Local("scaledAmount"), Local("adjustedAmount")),
            Assignments =
            [
                new() { Variable = "scaledAmount", Expression = "[mi.item] * multiplier" },
                new() { Variable = "adjustedAmount", Expression = "scaledAmount + [mi.index]" }
            ]
        };
        var published = await PublishAsync(host, Workflow(Key("ncalc"), body,
        [
            new() { Id = 1, Name = "amounts", DataType = "number", IsArray = true, DefaultValue = Json(new[] { 10, 20 }) },
            new() { Id = 2, Name = "multiplier", DataType = "number", DefaultValue = Json(2) },
            new() { Id = 3, Name = "calculationResults", DataType = "json", DefaultValue = Json(Array.Empty<object>()) }
        ]));
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        // Sequential: only the first item is admitted until it commits.
        Assert.Equal(1, await CountJobsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem));
        await DrainAsync(host, instanceId);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        var results = await LatestAsync(instanceId, "calculationResults");
        Assert.Equal(2, results.GetArrayLength());
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(
                """{"kind":"item","index":0,"item":10,"status":"completed","variables":{"scaledAmount":20,"adjustedAmount":20}}"""),
            System.Text.Json.Nodes.JsonNode.Parse(results[0].GetRawText())), results[0].GetRawText());
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(
                """{"kind":"item","index":1,"item":20,"status":"completed","variables":{"scaledAmount":40,"adjustedAmount":41}}"""),
            System.Text.Json.Nodes.JsonNode.Parse(results[1].GetRawText())), results[1].GetRawText());
        // Locals never become instance variables.
        Assert.False(await db.InstanceVariables.AnyAsync(row => row.InstanceId == instanceId && row.VariableName == "scaledAmount"));
        var visits = await db.NodeExecutions.Where(row => row.InstanceId == instanceId && row.NodeId == 2).ToListAsync();
        Assert.Equal(2, visits.Count);
        Assert.All(visits, visit => Assert.Equal("completed", visit.Status));
        Assert.Equal(1, await db.InstanceHistory.CountAsync(row => row.InstanceId == instanceId
            && row.Note == InstanceHistoryNotes.AutomaticMultiInstanceStarted));
    }

    [Fact]
    public async Task JavaScript_parallel_body_over_json_items_orders_results_whatever_order_items_finish()
    {
        await using var host = OpenHost();
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Calculate order lines", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
            ScriptFormat = ScriptFormats.JavaScript, X = 200, Y = 20,
            MultiInstance = Multi("parallel", "orderLines", "lineResults", Local("lineAmount"), Local("lineSummary", "json")),
            Script = "const item = execution.getVariable('mi.item');\nconst index = execution.getVariable('mi.index');\n" +
                "const multiplier = execution.getVariable('multiplier');\n" +
                "execution.setVariable('lineAmount', item.quantity * item.unitPrice * multiplier);\n" +
                "execution.setVariable('lineSummary', { index: index, sku: item.sku, amount: execution.getVariable('lineAmount') });"
        };
        var published = await PublishAsync(host, Workflow(Key("javascript"), body,
        [
            new() { Id = 1, Name = "orderLines", DataType = "json", IsArray = true, DefaultValue = Json(new object[]
            {
                new { sku = "A", quantity = 2, unitPrice = 5 },
                new { sku = "B", quantity = 1, unitPrice = 7 },
                new { sku = "C", quantity = 3, unitPrice = 1 }
            }) },
            new() { Id = 2, Name = "multiplier", DataType = "number", DefaultValue = Json(3) },
            new() { Id = 3, Name = "lineResults", DataType = "json", DefaultValue = Json(Array.Empty<object>()) }
        ]));
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJobs = await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem);
        Assert.Equal(3, itemJobs.Count);
        foreach (var jobId in itemJobs.AsEnumerable().Reverse()) await ProcessAsync(host, jobId);
        await DrainAsync(host, instanceId);

        var results = await LatestAsync(instanceId, "lineResults");
        Assert.Equal(new[] { 30.0, 21.0, 9.0 }, results.EnumerateArray()
            .Select(row => row.GetProperty("variables").GetProperty("lineAmount").GetDouble()));
        Assert.Equal(new[] { "A", "B", "C" }, results.EnumerateArray()
            .Select(row => row.GetProperty("variables").GetProperty("lineSummary").GetProperty("sku").GetString()));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
    }

    [Fact]
    public async Task Caught_script_failure_stops_the_body_publishes_a_partial_result_and_takes_the_boundary()
    {
        await using var host = OpenHost();
        var body = ThrowingBody("if (execution.getVariable('mi.index') === 1) throw new Error('bad item');\nexecution.setVariable('value', execution.getVariable('mi.item'));");
        var published = await PublishAsync(host, Workflow(Key("caught"), body, Items(3), boundary: true));
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJobs = await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem);
        await ProcessAsync(host, itemJobs[0]);
        await ProcessAsync(host, itemJobs[1]);
        // The failure cancelled the unfinished sibling's job.
        Assert.Equal(WorkflowJobStatuses.Cancelled, await JobStatusAsync(itemJobs[2]));
        await DrainAsync(host, instanceId);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(WorkflowEngineService.AutomaticScriptFailureCode, (await LatestAsync(instanceId, "failure")).GetString());
        var results = await LatestAsync(instanceId, "results");
        Assert.Equal(new[] { "completed", "failed", "cancelled" }, results.EnumerateArray().Select(row => row.GetProperty("status").GetString()));
        Assert.Equal(1, results[0].GetProperty("variables").GetProperty("value").GetDouble());
        Assert.Equal("{}", results[1].GetProperty("variables").GetRawText());
        Assert.Contains(await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync(),
            nodeId => nodeId == 5);
    }

    [Fact]
    public async Task Uncaught_failure_retries_the_same_item_then_waits_in_an_incident_without_publishing()
    {
        await using var host = OpenHost();
        var body = ThrowingBody("throw new Error('always refused');");
        body.Job = new JobPolicyModel { RetryDelays = ["PT10S"] };
        var published = await PublishAsync(host, Workflow(Key("uncaught"), body, Items(2)));
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var first = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem))[0];
        var before = evaluator.Count;
        await ProcessAsync(host, first);
        Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(first));
        await ProcessAsync(host, first);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(2, evaluator.Count - before);
        var incident = await db.WorkflowIncidents.SingleAsync(row => row.JobId == first && row.Status == WorkflowIncidentStatuses.Open);
        Assert.Equal("job_execution_failed", incident.Type);
        Assert.Contains("always refused", incident.Details);
        var job = await db.WorkflowJobs.SingleAsync(row => row.Id == first);
        var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.Id == job.AutomaticMultiInstanceItemId);
        // The item keeps its slot, its staged input, and its active visit while recovery is possible.
        Assert.Equal("active", item.Status);
        Assert.NotNull(item.StagedInputJson);
        Assert.Null(item.CandidateJson);
        Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal("[]", (await LatestAsync(instanceId, "results")).GetRawText());
    }

    [Fact]
    public async Task Shared_output_conflict_keeps_the_saved_candidate_and_retry_publishes_it_without_evaluating_again()
    {
        await using var host = OpenHost();
        var sharedKey = $"tests.script.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, sharedKey, 5);
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Increment", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
            ScriptFormat = ScriptFormats.JavaScript, X = 200, Y = 20,
            MultiInstance = Multi("sequential", "items", "results"),
            Script = "execution.setVariable('counter', execution.getVariable('counter') + 1);"
        };
        var variables = Items(1);
        variables.Add(new() { Id = 7, Name = "counter", DataType = "number", Scope = "shared", SharedKey = sharedKey, Access = "readWrite" });
        var published = await PublishAsync(host, Workflow(Key("conflict"), body, variables));
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();
        // Another writer changes the value after the item staged and before it publishes.
        evaluator.BeforeNext = () => SetSharedAsync(host, sharedKey, 100).GetAwaiter().GetResult();
        var before = evaluator.Count;
        await ProcessAsync(host, itemJob);

        Assert.Equal(1, evaluator.Count - before);
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
        await ProcessAsync(host, itemJob);
        await DrainAsync(host, instanceId);

        // The saved read-modify-write result replaced the newer value; nothing was evaluated again.
        Assert.Equal(1, evaluator.Count - before);
        Assert.Equal(6, await SharedValueAsync(host, sharedKey));
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
    }

    [Fact]
    public async Task Interrupting_timer_during_evaluation_cancels_the_body_and_fences_the_late_result()
    {
        await using var host = OpenHost();
        var body = ThrowingBody("execution.setVariable('value', execution.getVariable('mi.item'));");
        var definition = Workflow(Key("timer"), body, Items(2));
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
        // The timer fires after the item staged and while its script runs, outside any lock.
        evaluator.BeforeNext = () => Task.Run(() => ProcessAsync(host, timerJob)).GetAwaiter().GetResult();
        var before = evaluator.Count;
        await ProcessAsync(host, itemJob);

        Assert.Equal(1, evaluator.Count - before);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Contains(await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync(),
            nodeId => nodeId == 7);
        var bodyRow = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId);
        Assert.Equal(MultiInstanceRecordStatuses.Cancelled, bodyRow.Status);
        // The late evaluation saved and published nothing, and no aggregate was written.
        var items = await db.MultiInstanceActivityItems.Where(row => row.MultiInstanceExecutionId == bodyRow.Id).ToListAsync();
        Assert.All(items, item => Assert.Equal("cancelled", item.Status));
        Assert.All(items, item => Assert.Null(item.CandidateJson));
        Assert.Equal("[]", (await LatestAsync(instanceId, "results")).GetRawText());
        Assert.Empty(await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem));
    }

    [Fact]
    public async Task Async_after_runs_once_after_the_aggregate_and_an_empty_collection_evaluates_nothing()
    {
        await using var host = OpenHost();
        var body = ThrowingBody("execution.setVariable('value', execution.getVariable('mi.item'));");
        body.AsyncAfter = true;
        var published = await PublishAsync(host, Workflow(Key("async-after"), body, Items(2)));
        var instanceId = await StartAsync(host, published.Id);
        await DrainAsync(host, instanceId);

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
            Assert.Equal(1, await db.WorkflowJobs.CountAsync(job => job.InstanceId == instanceId && job.Kind == WorkflowJobKinds.AsyncAfter));
            Assert.Equal(2, (await LatestAsync(instanceId, "results")).GetArrayLength());
        }

        var empty = await PublishAsync(host, Workflow(Key("empty"), ThrowingBody("throw new Error('never runs');"), Items(0)));
        var before = evaluator.Count;
        var emptyId = await StartAsync(host, empty.Id);
        await DrainAsync(host, emptyId);

        Assert.Equal(before, evaluator.Count);
        Assert.Equal("[]", (await LatestAsync(emptyId, "results")).GetRawText());
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == emptyId)).Status);
        Assert.False(await after.MultiInstanceActivityItems.AnyAsync(row => row.InstanceId == emptyId));
    }

    [Fact]
    public async Task Sequential_items_read_shared_values_committed_by_earlier_items_but_not_their_locals()
    {
        await using var host = OpenHost();
        var sharedKey = $"tests.script.sequence.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, sharedKey, 0);
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Accumulate", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
            ScriptFormat = ScriptFormats.NCalc, X = 200, Y = 20,
            MultiInstance = Multi("sequential", "items", "results", Local("seen")),
            Assignments =
            [
                new() { Variable = "seen", Expression = "total" },
                new() { Variable = "total", Expression = "total + [mi.item]" }
            ]
        };
        var variables = Items(3);
        variables.Add(new() { Id = 7, Name = "total", DataType = "number", Scope = "shared", SharedKey = sharedKey, Access = "readWrite" });
        var published = await PublishAsync(host, Workflow(Key("sequence"), body, variables));
        var instanceId = await StartAsync(host, published.Id);
        await DrainAsync(host, instanceId);

        Assert.Equal(6, await SharedValueAsync(host, sharedKey));
        Assert.Equal(new[] { 0.0, 1.0, 3.0 }, (await LatestAsync(instanceId, "results")).EnumerateArray()
            .Select(row => row.GetProperty("variables").GetProperty("seen").GetDouble()));
    }

    [Fact]
    public async Task JavaScript_body_stages_while_a_binding_has_never_been_set_and_can_write_it_first()
    {
        await using var host = OpenHost();
        var unsetKey = $"tests.script.unset.{Guid.NewGuid():N}";
        using (var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
                   new CreateSharedVariableRequest(unsetKey, "number", IsArray: false, Nullable: false, HasValue: false, null)))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = ThrowingBody("execution.setVariable('value', execution.hasVariable('firstWrite') ? 1 : 0);\nexecution.setVariable('firstWrite', 42);");
        var variables = Items(1);
        variables.Add(new() { Id = 7, Name = "firstWrite", DataType = "number", Scope = "shared", SharedKey = unsetKey, Access = "readWrite" });
        var published = await PublishAsync(host, Workflow(Key("unset"), body, variables));
        var instanceId = await StartAsync(host, published.Id);
        await DrainAsync(host, instanceId);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(0, (await LatestAsync(instanceId, "results"))[0].GetProperty("variables").GetProperty("value").GetDouble());
        Assert.Equal(42, await SharedValueAsync(host, unsetKey));
    }

    [Fact]
    public async Task Retry_first_retries_the_item_before_taking_the_boundary()
    {
        await using var host = OpenHost();
        var body = ThrowingBody("throw new Error('always refused');");
        body.Job = new JobPolicyModel { FailureHandling = JobFailureHandling.RetryFirst, RetryDelays = ["PT10S"] };
        var published = await PublishAsync(host, Workflow(Key("retry-first"), body, Items(1), boundary: true));
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();
        var before = evaluator.Count;

        await ProcessAsync(host, itemJob);
        Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(itemJob));
        await ProcessAsync(host, itemJob);
        await DrainAsync(host, instanceId);

        Assert.Equal(2, evaluator.Count - before);
        Assert.Equal(WorkflowEngineService.AutomaticScriptFailureCode, (await LatestAsync(instanceId, "failure")).GetString());
        Assert.Equal("failed", (await LatestAsync(instanceId, "results"))[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Output_contract_failure_takes_the_boundary_with_the_result_invalid_code()
    {
        await using var host = OpenHost();
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Checked", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
            ScriptFormat = ScriptFormats.NCalc, X = 200, Y = 20,
            MultiInstance = Multi("parallel", "items", "results",
                new MultiInstanceOutputVariableModel { Name = "value", DataType = "number", Validation = "value > 100" }),
            Assignments = [new() { Variable = "value", Expression = "[mi.item]" }]
        };
        var published = await PublishAsync(host, Workflow(Key("contract"), body, Items(1), boundary: true));
        var instanceId = await StartAsync(host, published.Id);
        await DrainAsync(host, instanceId);

        Assert.Equal(AutomaticMultiInstanceValues.ResultInvalid, (await LatestAsync(instanceId, "failure")).GetString());
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
    }

    [Fact]
    public async Task Local_validation_failing_only_at_publication_is_the_saved_candidates_output_failure()
    {
        await using var host = OpenHost();
        var limitKey = $"tests.script.limit.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, limitKey, 10);
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Limited", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
            ScriptFormat = ScriptFormats.JavaScript, X = 200, Y = 20,
            MultiInstance = Multi("parallel", "items", "results",
                new MultiInstanceOutputVariableModel { Name = "value", DataType = "number", Validation = "value <= limit" }),
            Script = "execution.setVariable('value', 5);"
        };
        var variables = Items(1);
        variables.Add(new() { Id = 7, Name = "limit", DataType = "number", Scope = "shared", SharedKey = limitKey, Access = "read" });
        var published = await PublishAsync(host, Workflow(Key("publication-validation"), body, variables, boundary: true));
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();
        // The limit drops after the item staged with 10, so 5 passes at evaluation and fails at publication.
        evaluator.BeforeNext = () => SetSharedAsync(host, limitKey, 1).GetAwaiter().GetResult();
        var before = evaluator.Count;
        await ProcessAsync(host, itemJob);
        await DrainAsync(host, instanceId);

        Assert.Equal(1, evaluator.Count - before);
        Assert.Equal(AutomaticMultiInstanceValues.ResultInvalid, (await LatestAsync(instanceId, "failure")).GetString());
        Assert.Equal("failed", (await LatestAsync(instanceId, "results"))[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task JavaScript_freezes_every_instance_variable_so_an_oversized_one_fails_entry_where_NCalc_does_not()
    {
        await using var host = OpenHost();
        // A json value of many strings, so the input is large but each element is short.
        var blob = Enumerable.Range(0, AutomaticMultiInstanceRules.MaxSnapshotBytes / 1000 + 50)
            .Select(_ => new string('x', 1000)).ToArray();
        Dictionary<string, JsonElement> Values() => new() { ["blob"] = Json(blob) };
        WorkflowModel WithBlob(string key, FlowNodeModel body)
        {
            var definition = Workflow(key, body, Items(1), boundary: true);
            definition.FlowNodes[0].Variables = [new() { Id = 50, Name = "blob", DataType = "json" }];
            return definition;
        }

        var script = await PublishAsync(host, WithBlob(Key("blob-js"), ThrowingBody("execution.setVariable('value', 1);")));
        var scriptId = await StartAsync(host, script.Id, Values());
        await DrainAsync(host, scriptId);
        Assert.Equal(AutomaticMultiInstanceValues.SourceInvalid, (await LatestAsync(scriptId, "failure")).GetString());

        var ncalc = new FlowNodeModel
        {
            Id = 2, Name = "Body", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true, ScriptFormat = ScriptFormats.NCalc,
            X = 200, Y = 20, MultiInstance = Multi("parallel", "items", "results", Local("value")),
            Assignments = [new() { Variable = "value", Expression = "[mi.item]" }]
        };
        var ncalcDefinition = await PublishAsync(host, WithBlob(Key("blob-ncalc"), ncalc));
        var ncalcId = await StartAsync(host, ncalcDefinition.Id, Values());
        await DrainAsync(host, ncalcId);
        Assert.Equal(1, (await LatestAsync(ncalcId, "results"))[0].GetProperty("variables").GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Unset_shared_collection_is_an_entry_failure_that_takes_the_catch_all_boundary()
    {
        await using var host = OpenHost();
        var sharedKey = $"tests.script.items.{Guid.NewGuid():N}";
        using (var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
                   new CreateSharedVariableRequest(sharedKey, "number", IsArray: true, Nullable: false, HasValue: false, null)))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = ThrowingBody("execution.setVariable('value', 1);");
        body.MultiInstance!.CollectionVariable = "sharedItems";
        var variables = Items(0);
        variables.Add(new() { Id = 8, Name = "sharedItems", DataType = "number", IsArray = true, Scope = "shared", SharedKey = sharedKey, Access = "read" });
        var published = await PublishAsync(host, Workflow(Key("entry"), body, variables, boundary: true));
        var instanceId = await StartAsync(host, published.Id);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(AutomaticMultiInstanceValues.SourceInvalid, (await LatestAsync(instanceId, "failure")).GetString());
        Assert.False(await db.MultiInstanceExecutions.AnyAsync(row => row.InstanceId == instanceId));
    }

    [Fact]
    public async Task A_host_outside_development_refuses_call_and_script_bodies_at_publication()
    {
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Body", Type = BpmnFlowNodeTypes.CallActivity, X = 200, Y = 20,
            MultiInstance = Multi("parallel", "items", "results"),
            CallActivity = new() { WorkflowKey = "unused-child" }
        };
        // The ordinary test host is not Development: it refuses a call body, whatever its
        // target, and script bodies too.
        using (var response = await SendAsync(null, HttpMethod.Post, "/api/workflows",
                   new CreateWorkflowRequest(Workflow(Key("call"), body, Items(1)), true)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("automatic multiInstance is not enabled", await response.Content.ReadAsStringAsync());
        }

        var script = ThrowingBody("execution.setVariable('value', 1);");
        using var closed = await SendAsync(null, HttpMethod.Post, "/api/workflows",
            new CreateWorkflowRequest(Workflow(Key("closed"), script, Items(1)), true));
        Assert.Equal(HttpStatusCode.BadRequest, closed.StatusCode);
        Assert.Contains("automatic multiInstance is not enabled", await closed.Content.ReadAsStringAsync());
    }

    // Review P1. An entry failure used to resume ordinary pass-through, which locks the
    // node's ordinary plan: for JavaScript, every readWrite binding. Holding one of those
    // rows now does not block it, because the route takes only the Error-phase locks.
    [Fact]
    public async Task Entry_failure_takes_the_boundary_under_error_phase_locks_only()
    {
        await using var host = OpenHost();
        var writable = $"tests.script.writable.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, writable, 1);
        var instanceId = await StartUnsetSharedCollectionAsync(host, writable, failureValidation: null);

        await using var holder = fixture.CreateDbContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM flowbit.shared_variables WHERE "Key" = {writable} FOR UPDATE""");
        var entry = ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        var finished = await Task.WhenAny(entry, Task.Delay(TimeSpan.FromSeconds(20)));
        await held.RollbackAsync();
        Assert.Same(entry, finished);
        await entry;

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(AutomaticMultiInstanceValues.SourceInvalid, (await LatestAsync(instanceId, "failure")).GetString());
    }

    // Review P1. The entry route now checks the error target's rule as a body's error
    // route does. A refused code is never written: the job waits in an incident.
    [Fact]
    public async Task Entry_failure_never_writes_an_error_code_its_target_refuses()
    {
        await using var host = OpenHost();
        var writable = $"tests.script.writable.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, writable, 1);
        var instanceId = await StartUnsetSharedCollectionAsync(host, writable, failureValidation: "Len(failure) < 5");
        var entry = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.AsyncBefore)).Single();

        await ProcessAsync(host, entry);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "failure")).ValueKind);
        var incident = await db.WorkflowIncidents.SingleAsync(row => row.JobId == entry && row.Status == WorkflowIncidentStatuses.Open);
        Assert.Equal(WorkflowIncidentTypes.JobConflict, incident.Type);
        var token = await db.ExecutionTokens.SingleAsync(row => row.InstanceId == instanceId && row.Status == "active");
        Assert.Equal(2, token.NodeId);
        Assert.Equal(entry, token.WaitingJobId);
    }

    // Review P2. Division by zero in an item's NCalc assignment is the item's script
    // failure, so the catch-all boundary takes it instead of an unhandled exception.
    [Fact]
    public async Task NCalc_division_by_zero_in_an_item_takes_the_boundary()
    {
        await using var host = OpenHost();
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Body", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true, ScriptFormat = ScriptFormats.NCalc,
            X = 200, Y = 20, MultiInstance = Multi("sequential", "items", "results", Local("value")),
            Assignments = [new() { Variable = "value", Expression = "[mi.item] % 0" }]
        };
        var published = await PublishAsync(host, Workflow(Key("modulo"), body, Items(2), boundary: true));
        var instanceId = await StartAsync(host, published.Id);

        await DrainAsync(host, instanceId);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(WorkflowEngineService.AutomaticScriptFailureCode, (await LatestAsync(instanceId, "failure")).GetString());
        Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.InstanceId == instanceId));
    }

    // Review P3. 4,000 copies of 1E+300 are 28 KB of compact JSON but about 1.2 MB as
    // PostgreSQL prints the stored value. The candidate used to pass the code's compact
    // check and then break the item row's check constraint, so Worker recovery evaluated
    // it again. It is now refused once, before saving, like any oversized result.
    [Fact]
    public async Task A_result_too_large_as_stored_is_refused_once_without_reaching_the_database_check()
    {
        await using var host = OpenHost();
        var body = ThrowingBody("const values = [];\nfor (let i = 0; i < 4000; i++) values.push(1e300);\nexecution.setVariable('big', values);");
        body.MultiInstance!.OutputVariables = [new() { Name = "big", DataType = "json", Nullable = true }];
        var published = await PublishAsync(host, Workflow(Key("stored-size"), body, Items(1), boundary: true));
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var before = evaluator.Count;

        await DrainAsync(host, instanceId);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, evaluator.Count - before);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(AutomaticMultiInstanceValues.ResultTooLarge, (await LatestAsync(instanceId, "failure")).GetString());
        Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.InstanceId == instanceId));
    }

    // Review P4. The snapshot holds the collection once. A JavaScript body used to freeze
    // it again among its instance inputs, so a 600 KB collection broke the 1 MiB limit.
    [Fact]
    public async Task A_JavaScript_body_over_a_600_KB_collection_freezes_it_once_and_runs()
    {
        await using var host = OpenHost();
        var body = ThrowingBody("execution.setVariable('value', execution.getVariable('mi.item').length);");
        var definition = Workflow(Key("large-collection"), body,
        [
            new() { Id = 1, Name = "items", DataType = "string", IsArray = true, DefaultValue = Json(Array.Empty<string>()) },
            new() { Id = 2, Name = "results", DataType = "json", DefaultValue = Json(Array.Empty<object>()) }
        ]);
        // An ordinary script fills the collection in the start transaction.
        definition.FlowNodes.Add(new()
        {
            Id = 6, Name = "Fill", Type = BpmnFlowNodeTypes.ScriptTask, ScriptFormat = ScriptFormats.JavaScript, X = 120, Y = 20,
            Script = "const items = [];\nfor (let i = 0; i < 6; i++) items.push('x'.repeat(100000));\nexecution.setVariable('items', items);"
        });
        definition.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 6;
        definition.SequenceFlows.Add(new() { Id = 601, SourceRef = 6, TargetRef = 2 });
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);

        await DrainAsync(host, instanceId);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        var results = await LatestAsync(instanceId, "results");
        Assert.Equal(6, results.GetArrayLength());
        Assert.All(results.EnumerateArray(), row => Assert.Equal(100000, row.GetProperty("variables").GetProperty("value").GetInt32()));
    }

    // Review P5. Publication alone used to need the switch: a definition published on an
    // open host then started and ran anywhere. Start, entry, and item jobs now refuse it
    // on a host outside Development, and the work resumes on an open host.
    [Fact]
    public async Task A_host_outside_Development_neither_starts_nor_runs_a_published_script_body()
    {
        await using var host = OpenHost();
        var body = ThrowingBody("execution.setVariable('value', execution.getVariable('mi.item'));");
        body.Job = new JobPolicyModel { RetryDelays = ["PT1S", "PT1S"] };
        var published = await PublishAsync(host, Workflow(Key("closed-run"), body, Items(2)));

        var refused = await Assert.ThrowsAsync<WorkflowDomainException>(() => StartAsync(fixture.Factory, published.Id));
        Assert.Contains("runs only on a Development host with the release switch open", refused.Message);

        var instanceId = await StartAsync(host, published.Id);
        var entry = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.AsyncBefore)).Single();
        await ProcessAsync(fixture.Factory, entry);
        Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(entry));
        await using (var db = fixture.CreateDbContext())
            Assert.False(await db.MultiInstanceExecutions.AnyAsync(row => row.InstanceId == instanceId));

        await ProcessAsync(host, entry);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var item = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem))[0];
        var before = evaluator.Count;
        await ProcessAsync(fixture.Factory, item);
        Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(item));
        Assert.Equal(before, evaluator.Count);

        await DrainAsync(host, instanceId);
        await using var final = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await final.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(2, (await LatestAsync(instanceId, "results")).GetArrayLength());
    }

    // An interrupting conditional boundary on the body, woken by an administrative write
    // (which goes through the conditional coordinator) while the second item's script
    // runs. The body is cancelled at once; the first item's committed shared write stays,
    // and the held item's late result is fenced.
    [Fact]
    public async Task Interrupting_conditional_boundary_woken_during_evaluation_cancels_the_body_and_fences_the_late_result()
    {
        await using var host = OpenHost();
        var sharedKey = $"tests.script.condition.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, sharedKey, 0);
        var variables = Items(2);
        variables.Add(Shared(7, "counter", sharedKey, SharedVariableAccessModes.ReadWrite));
        var definition = Workflow(Key("condition-interrupt"), IncrementingBody("sequential"), variables);
        AddStopBoundary(definition, cancelActivity: true);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceItem);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var second = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();

        Task? wake = null;
        evaluator.BeforeNext = () =>
        {
            wake = Task.Run(() => PatchVariableAsync(host, instanceId, "stop", true));
            wake.GetAwaiter().GetResult();
        };
        var before = evaluator.Count;
        await ProcessAsync(host, second);
        await wake!;

        Assert.Equal(1, evaluator.Count - before);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        var nodes = await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync();
        Assert.Contains(7, nodes);
        Assert.DoesNotContain(3, nodes);
        var bodyRow = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId);
        Assert.Equal(MultiInstanceRecordStatuses.Cancelled, bodyRow.Status);
        var items = await db.MultiInstanceActivityItems.Where(row => row.MultiInstanceExecutionId == bodyRow.Id)
            .OrderBy(row => row.ItemIndex).ToListAsync();
        Assert.Equal(new[] { "completed", "cancelled" }, items.Select(item => item.Status));
        Assert.Null(items[1].CandidateJson);
        // No partial aggregate, and the late increment never reached the catalog.
        Assert.Equal("[]", (await LatestAsync(instanceId, "results")).GetRawText());
        Assert.Equal(1, await SharedValueAsync(host, sharedKey));
        var subscription = await db.ConditionalBoundarySubscriptions.SingleAsync(row => row.InstanceId == instanceId);
        Assert.Equal(1, subscription.Occurrence);
        Assert.Equal(1, await db.InstanceHistory.CountAsync(row => row.InstanceId == instanceId
            && row.Note == InstanceHistoryNotes.ConditionalTriggered));
        Assert.Empty(await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem));
        Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.InstanceId == instanceId));
    }

    // A non-interrupting conditional boundary spawns its branch on every rising edge while
    // the body keeps running: the held item publishes normally, and the body completes.
    [Fact]
    public async Task Non_interrupting_conditional_boundary_branches_on_each_rising_edge_while_the_body_keeps_running()
    {
        await using var host = OpenHost();
        var sharedKey = $"tests.script.condition.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, sharedKey, 0);
        var variables = Items(2);
        variables.Add(Shared(7, "counter", sharedKey, SharedVariableAccessModes.ReadWrite));
        var definition = Workflow(Key("condition-branch"), IncrementingBody("sequential"), variables);
        AddStopBoundary(definition, cancelActivity: false);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var first = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();

        Task? wake = null;
        evaluator.BeforeNext = () =>
        {
            wake = Task.Run(() => PatchVariableAsync(host, instanceId, "stop", true));
            wake.GetAwaiter().GetResult();
        };
        await ProcessAsync(host, first);
        await wake!;

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.ExecutionTokens.CountAsync(row => row.InstanceId == instanceId && row.NodeId == 7));
            var bodyRow = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId);
            Assert.Equal(MultiInstanceRecordStatuses.Active, bodyRow.Status);
            Assert.Equal("completed", (await db.MultiInstanceActivityItems
                .SingleAsync(row => row.MultiInstanceExecutionId == bodyRow.Id && row.ItemIndex == 0)).Status);
        }
        Assert.Equal(1, await SharedValueAsync(host, sharedKey));

        // It rearms once the condition is false; the next rising edge branches again.
        await PatchVariableAsync(host, instanceId, "stop", false);
        await PatchVariableAsync(host, instanceId, "stop", true);
        await DrainAsync(host, instanceId);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(2, await after.ExecutionTokens.CountAsync(row => row.InstanceId == instanceId && row.NodeId == 7));
        Assert.Equal(1, await after.ExecutionTokens.CountAsync(row => row.InstanceId == instanceId && row.NodeId == 3));
        Assert.Equal(2, (await after.ConditionalBoundarySubscriptions.SingleAsync(row => row.InstanceId == instanceId)).Occurrence);
        Assert.Equal(new[] { "completed", "completed" }, (await LatestAsync(instanceId, "results")).EnumerateArray()
            .Select(row => row.GetProperty("status").GetString()));
        Assert.Equal(2, await SharedValueAsync(host, sharedKey));
    }

    [Fact]
    public async Task Non_interrupting_timer_fired_during_evaluation_leaves_the_item_running_and_the_body_completes()
    {
        await using var host = OpenHost();
        var body = ThrowingBody("execution.setVariable('value', execution.getVariable('mi.item'));");
        var definition = Workflow(Key("timer-reminder"), body, Items(2));
        definition.FlowNodes.Add(new() { Id = 6, Name = "Reminder", Type = BpmnFlowNodeTypes.TimerBoundaryEvent, AttachedToRef = 2,
            CancelActivity = false, Timer = new TimerDefinitionModel { TimeDuration = "PT1H" } });
        definition.FlowNodes.Add(new() { Id = 7, Name = "Reminded", Type = BpmnFlowNodeTypes.EndEvent, X = 500, Y = 300 });
        definition.SequenceFlows.Add(new() { Id = 601, SourceRef = 6, TargetRef = 7 });
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var timerJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.TimerBoundary)).Single();
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem))[0];

        // The reminder fires after the item staged and while its script runs.
        evaluator.BeforeNext = () => Task.Run(() => ProcessAsync(host, timerJob)).GetAwaiter().GetResult();
        await ProcessAsync(host, itemJob);

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Contains(7, await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync());
            var bodyRow = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId);
            Assert.Equal(MultiInstanceRecordStatuses.Active, bodyRow.Status);
            var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.MultiInstanceExecutionId == bodyRow.Id && row.ItemIndex == 0);
            Assert.Equal("completed", item.Status);
        }
        await DrainAsync(host, instanceId);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Contains(3, await after.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync());
        var results = await LatestAsync(instanceId, "results");
        Assert.Equal(new[] { "completed", "completed" }, results.EnumerateArray().Select(row => row.GetProperty("status").GetString()));
        Assert.Equal(new[] { 1.0, 2.0 }, results.EnumerateArray().Select(row => row.GetProperty("variables").GetProperty("value").GetDouble()));
    }

    // A shared typed collection is copied at initialization. A catalog change after that
    // reaches no item: neither mi.item and mi.total nor a JavaScript read of the alias.
    [Theory]
    [InlineData(ScriptFormats.NCalc)]
    [InlineData(ScriptFormats.JavaScript)]
    public async Task A_shared_collection_is_frozen_at_initialization_so_later_catalog_changes_reach_no_item(string format)
    {
        await using var host = OpenHost();
        var sharedKey = $"tests.script.collection.{Guid.NewGuid():N}";
        using (var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
                   new CreateSharedVariableRequest(sharedKey, "number", IsArray: true, Nullable: false, HasValue: true, Json(new[] { 1, 2, 3 }))))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Body", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true, ScriptFormat = format, X = 200, Y = 20,
            MultiInstance = Multi("sequential", "sharedItems", "results", Local("value"), Local("seen"))
        };
        if (format == ScriptFormats.JavaScript)
            body.Script = "execution.setVariable('value', execution.getVariable('mi.item') * 10 + execution.getVariable('mi.total'));\n" +
                "execution.setVariable('seen', execution.getVariable('sharedItems').length);";
        else
            body.Assignments =
            [
                new() { Variable = "value", Expression = "[mi.item] * 10 + [mi.total]" },
                new() { Variable = "seen", Expression = "[mi.total]" }
            ];
        var published = await PublishAsync(host, Workflow(Key("shared-collection"), body,
        [
            Shared(1, "sharedItems", sharedKey, SharedVariableAccessModes.Read, isArray: true),
            new() { Id = 2, Name = "results", DataType = "json", DefaultValue = Json(Array.Empty<object>()) }
        ]));
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceItem);

        // Later items stage after this change.
        await SetSharedValueAsync(host, sharedKey, Json(new[] { 100, 200 }));
        await DrainAsync(host, instanceId);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        var results = (await LatestAsync(instanceId, "results")).EnumerateArray().ToArray();
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, results.Select(row => row.GetProperty("item").GetDouble()));
        Assert.Equal(new[] { 13.0, 23.0, 33.0 }, results.Select(row => row.GetProperty("variables").GetProperty("value").GetDouble()));
        Assert.Equal(new[] { 3.0, 3.0, 3.0 }, results.Select(row => row.GetProperty("variables").GetProperty("seen").GetDouble()));
        Assert.Equal("[100,200]", (await SharedJsonAsync(host, sharedKey)).GetRawText().Replace(" ", string.Empty));
    }

    // The NCalc case is Sequential_items_read_shared_values_committed_by_earlier_items_but_not_their_locals.
    [Fact]
    public async Task JavaScript_sequential_items_read_earlier_shared_commits_but_never_earlier_locals()
    {
        await using var host = OpenHost();
        var sharedKey = $"tests.script.sequence.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, sharedKey, 0);
        var body = ThrowingBody("execution.setVariable('sawLocal', execution.hasVariable('seen'));\n" +
            "execution.setVariable('seen', execution.getVariable('total'));\n" +
            "execution.setVariable('total', execution.getVariable('total') + execution.getVariable('mi.item'));");
        body.MultiInstance = Multi("sequential", "items", "results", Local("seen"), Local("sawLocal", "boolean"));
        var variables = Items(3);
        variables.Add(Shared(7, "total", sharedKey, SharedVariableAccessModes.ReadWrite));
        var published = await PublishAsync(host, Workflow(Key("js-sequence"), body, variables));
        var instanceId = await StartAsync(host, published.Id);
        await DrainAsync(host, instanceId);

        Assert.Equal(6, await SharedValueAsync(host, sharedKey));
        var results = (await LatestAsync(instanceId, "results")).EnumerateArray().ToArray();
        Assert.Equal(new[] { 0.0, 1.0, 3.0 }, results.Select(row => row.GetProperty("variables").GetProperty("seen").GetDouble()));
        Assert.All(results, row => Assert.False(row.GetProperty("variables").GetProperty("sawLocal").GetBoolean()));
    }

    // Every shared value an item stages counts toward its 1 MiB input. A JavaScript stage
    // reads every binding; an NCalc stage reads the one its assignment names. The refusal
    // is the same on every attempt, so it is never retried, never evaluated, and never left
    // in an incident: the body stops and takes its catch-all with the source code.
    [Theory]
    [InlineData(ScriptFormats.NCalc)]
    [InlineData(ScriptFormats.JavaScript)]
    public async Task Staged_input_over_the_item_limit_is_refused_once_without_evaluation(string format)
    {
        await using var host = OpenHost();
        var bigKey = $"tests.script.big.{Guid.NewGuid():N}";
        using (var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
                   new CreateSharedVariableRequest(bigKey, "string", IsArray: false, Nullable: false, HasValue: true,
                       Json(new string('x', AutomaticMultiInstanceRules.MaxSnapshotBytes + 1000)))))
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var body = format == ScriptFormats.JavaScript
            ? ThrowingBody("execution.setVariable('value', execution.getVariable('mi.item'));")
            : new FlowNodeModel
            {
                Id = 2, Name = "Body", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true, ScriptFormat = ScriptFormats.NCalc,
                X = 200, Y = 20, MultiInstance = Multi("parallel", "items", "results", Local("value")),
                Assignments = [new() { Variable = "value", Expression = "[mi.item] + Len(big)" }]
            };
        // Retries are available, and a refusal must not use them.
        body.Job = new JobPolicyModel { RetryDelays = ["PT10S"] };
        var variables = Items(1);
        variables.Add(Shared(7, "big", bigKey, SharedVariableAccessModes.Read, type: "string"));
        var published = await PublishAsync(host, Workflow(Key("big-stage"), body, variables, boundary: true));
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();
        var before = evaluator.Count;

        await ProcessAsync(host, itemJob);

        Assert.Equal(before, evaluator.Count);
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.SingleAsync(row => row.Id == itemJob);
            Assert.Equal(WorkflowJobStatuses.Completed, job.Status);
            Assert.Equal(1, job.AttemptCount);
            var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.Id == job.AutomaticMultiInstanceItemId);
            Assert.Equal("failed", item.Status);
            Assert.Equal(AutomaticMultiInstanceValues.SourceInvalid, item.ErrorCode);
            Assert.Null(item.StagedInputJson);
            Assert.Null(item.CandidateJson);
        }
        await DrainAsync(host, instanceId);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Contains(5, await after.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync());
        Assert.Equal(AutomaticMultiInstanceValues.SourceInvalid, (await LatestAsync(instanceId, "failure")).GetString());
        // A refused result publishes nothing.
        Assert.Equal("[]", (await LatestAsync(instanceId, "results")).GetRawText());
        Assert.False(await after.WorkflowIncidents.AnyAsync(row => row.InstanceId == instanceId));
    }

    // The 1 MiB limit is on the stored snapshot row: the snapshot as PostgreSQL prints its
    // jsonb, plus the causal actor stored beside it (which the snapshot also holds). A run
    // with a small input measures what the row holds besides the blob; a blob that leaves
    // the row 64 bytes under the limit then runs, and one 64 bytes over is refused at entry.
    // The refusal far over the limit is
    // JavaScript_freezes_every_instance_variable_so_an_oversized_one_fails_entry_where_NCalc_does_not.
    [Fact]
    public async Task A_body_snapshot_just_under_the_limit_runs_and_one_just_over_is_refused_at_entry()
    {
        await using var host = OpenHost();
        var definition = Workflow(Key("near-limit"), ThrowingBody("execution.setVariable('value', execution.getVariable('mi.item'));"),
            Items(1), boundary: true);
        definition.FlowNodes[0].Variables = [new() { Id = 50, Name = "blob", DataType = "json" }];
        var published = await PublishAsync(host, definition);

        var small = Blob(10_000);
        var calibrationId = await StartAsync(host, published.Id, new() { ["blob"] = small });
        await ProcessNextAsync(host, calibrationId, WorkflowJobKinds.AsyncBefore);
        var (calibration, calibrationBytes) = await SnapshotAsync(host, calibrationId);
        var frozenBlob = calibration.GetProperty("instanceInputs").GetProperty("blob");
        Assert.Equal(JsonbText.ByteCount(small), JsonbText.ByteCount(frozenBlob));
        // The causal actor counts again beside the snapshot that already holds it.
        Assert.True(calibrationBytes > JsonbText.ByteCount(calibration),
            $"Row {calibrationBytes} bytes, snapshot {JsonbText.ByteCount(calibration)} bytes.");
        var overhead = calibrationBytes - JsonbText.ByteCount(frozenBlob);
        // Its due dispatch would otherwise be leased before the next instance's entry.
        await DrainAsync(host, calibrationId);

        var underId = await StartAsync(host, published.Id,
            new() { ["blob"] = Blob(AutomaticMultiInstanceRules.MaxSnapshotBytes - overhead - 64) });
        await ProcessNextAsync(host, underId, WorkflowJobKinds.AsyncBefore);
        var (_, under) = await SnapshotAsync(host, underId);
        Assert.InRange(under, AutomaticMultiInstanceRules.MaxSnapshotBytes - 128, AutomaticMultiInstanceRules.MaxSnapshotBytes);
        await DrainAsync(host, underId);
        await using (var db = fixture.CreateDbContext())
            Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == underId)).Status);
        Assert.Equal(1, (await LatestAsync(underId, "results"))[0].GetProperty("variables").GetProperty("value").GetDouble());
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(underId, "failure")).ValueKind);

        var overId = await StartAsync(host, published.Id,
            new() { ["blob"] = Blob(AutomaticMultiInstanceRules.MaxSnapshotBytes - overhead + 64) });
        await DrainAsync(host, overId);
        Assert.Equal(AutomaticMultiInstanceValues.SourceInvalid, (await LatestAsync(overId, "failure")).GetString());
        await using var after = fixture.CreateDbContext();
        Assert.False(await after.MultiInstanceExecutions.AnyAsync(row => row.InstanceId == overId));
    }

    // FlowInfo is frozen once, on the body, when it initializes. Every item reads that
    // evidence: the review action before the body, but not the reminder branch that a
    // non-interrupting timer traverses after initialization. Items add no traversals.
    [Theory]
    [InlineData(ScriptFormats.NCalc)]
    [InlineData(ScriptFormats.JavaScript)]
    public async Task Items_read_the_FlowInfo_the_body_froze_at_initialization(string format)
    {
        await using var host = OpenHost();
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Body", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true, ScriptFormat = format, X = 200, Y = 20,
            MultiInstance = Multi("sequential", "items", "results", Local("approvals"), Local("approver", "string"), Local("reminders"))
        };
        if (format == ScriptFormats.JavaScript)
        {
            body.UsesFlowInfo = true;
            body.Script = "const approval = execution.getFlowInfo(1001);\n" +
                "execution.setVariable('approvals', approval.actions.count);\n" +
                "execution.setVariable('approver', approval.actions.last.user);\n" +
                "execution.setVariable('reminders', execution.getFlowInfo(601).traversals.count);";
        }
        else
        {
            body.Assignments =
            [
                new() { Variable = "approvals", Expression = "FlowInfo(1001, 'actions.count')" },
                new() { Variable = "approver", Expression = "FlowInfo(1001, 'actions.last.user')" },
                new() { Variable = "reminders", Expression = "FlowInfo(601, 'traversals.count')" }
            ];
        }
        var definition = Workflow(Key("flowinfo"), body, Items(2));
        AddReview(definition);
        definition.FlowNodes.Add(new() { Id = 6, Name = "Reminder", Type = BpmnFlowNodeTypes.TimerBoundaryEvent, AttachedToRef = 2,
            CancelActivity = false, Timer = new TimerDefinitionModel { TimeDuration = "PT1H" } });
        definition.FlowNodes.Add(new() { Id = 7, Name = "Reminded", Type = BpmnFlowNodeTypes.EndEvent, X = 500, Y = 300 });
        definition.SequenceFlows.Add(new() { Id = 601, SourceRef = 6, TargetRef = 7 });
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await TakeReviewAsync(host, instanceId);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceItem);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.TimerBoundary);
        Assert.Equal(1, await TraversalsAsync(instanceId, 601));
        await DrainAsync(host, instanceId);

        var results = (await LatestAsync(instanceId, "results")).EnumerateArray().ToArray();
        Assert.Equal(2, results.Length);
        Assert.All(results, row =>
        {
            var locals = row.GetProperty("variables");
            Assert.Equal(1, locals.GetProperty("approvals").GetDouble());
            Assert.Equal("script-admin", locals.GetProperty("approver").GetString());
            Assert.Equal(0, locals.GetProperty("reminders").GetDouble());
        });
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        // The body leaves once, whatever its item count.
        Assert.Equal(1, await TraversalsAsync(instanceId, 201));
        Assert.Equal(1, (await db.SequenceFlowSummaries.SingleAsync(row => row.InstanceId == instanceId && row.SequenceFlowId == 1001)).ActionCount);
    }

    // Archiving is refused while a published definition or a running instance binds a key,
    // so the key goes away the way an operator retires it: a new default without it, the
    // old version unpublished, then the archive. A reactivated instance then reaches the body.
    [Fact]
    public async Task An_archived_binding_stops_the_item_in_an_operational_incident_until_the_key_returns()
    {
        await using var host = OpenHost();
        var sharedKey = $"tests.script.archived.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, sharedKey, 0);
        var key = Key("archived");
        var variables = Items(1);
        variables.Add(Shared(7, "counter", sharedKey, SharedVariableAccessModes.ReadWrite));
        var definition = Workflow(key, IncrementingBody("parallel"), variables, boundary: true);
        AddReview(definition);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await TakeReviewAsync(host, instanceId);
        await DrainAsync(host, instanceId);
        Assert.Equal(1, await SharedValueAsync(host, sharedKey));

        var replacement = await PublishAsync(host, new WorkflowModel
        {
            Id = key, Name = key, InitialEventId = 1, Variables = [],
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent, X = 40, Y = 40 },
                new() { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent, X = 500, Y = 40 }
            ],
            SequenceFlows = [new() { Id = 101, SourceRef = 1, TargetRef = 3 }]
        });
        using (var madeDefault = await SendAsync(host, HttpMethod.Post, $"/api/workflows/{replacement.Id}/set-default"))
            Assert.True(madeDefault.StatusCode == HttpStatusCode.NoContent, await madeDefault.Content.ReadAsStringAsync());
        using (var unpublished = await SendAsync(host, HttpMethod.Post, $"/api/workflows/{published.Id}/unpublish"))
            Assert.True(unpublished.StatusCode == HttpStatusCode.NoContent, await unpublished.Content.ReadAsStringAsync());
        await ChangeSharedStatusAsync(host, sharedKey, "archive");
        await ReactivateAtReviewAsync(host, instanceId);
        await TakeReviewAsync(host, instanceId);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();
        var before = evaluator.Count;

        await ProcessAsync(host, itemJob);

        // An operational conflict, not the authored failure: nothing ran, the boundary was
        // not taken, and the body waits with its item's slot.
        Assert.Equal(before, evaluator.Count);
        long incidentId;
        await using (var db = fixture.CreateDbContext())
        {
            var incident = await db.WorkflowIncidents.SingleAsync(row => row.JobId == itemJob && row.Status == WorkflowIncidentStatuses.Open);
            Assert.Equal(WorkflowIncidentTypes.JobConflict, incident.Type);
            incidentId = incident.Id;
            var job = await db.WorkflowJobs.SingleAsync(row => row.Id == itemJob);
            var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.Id == job.AutomaticMultiInstanceItemId);
            Assert.Equal("active", item.Status);
            Assert.Null(item.StagedInputJson);
            Assert.Equal(MultiInstanceRecordStatuses.Active,
                (await db.MultiInstanceExecutions.SingleAsync(row => row.Id == item.MultiInstanceExecutionId)).Status);
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        }
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "failure")).ValueKind);

        await ChangeSharedStatusAsync(host, sharedKey, "reactivate");
        await RetryIncidentAsync(host, incidentId);
        await DrainAsync(host, instanceId);

        Assert.Equal(1, evaluator.Count - before);
        Assert.Equal(2, await SharedValueAsync(host, sharedKey));
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "failure")).ValueKind);
    }

    // A statically named shared dependency fails closed while it has no value, unlike a
    // binding a JavaScript stage reads only conservatively
    // (JavaScript_body_stages_while_a_binding_has_never_been_set_and_can_write_it_first).
    [Fact]
    public async Task An_NCalc_item_reading_a_never_set_shared_alias_waits_in_an_operational_incident_not_the_boundary()
    {
        await using var host = OpenHost();
        var baseKey = $"tests.script.base.{Guid.NewGuid():N}";
        using (var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
                   new CreateSharedVariableRequest(baseKey, "number", IsArray: false, Nullable: false, HasValue: false, null)))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Body", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true, ScriptFormat = ScriptFormats.NCalc,
            X = 200, Y = 20, MultiInstance = Multi("parallel", "items", "results", Local("value")),
            Assignments = [new() { Variable = "value", Expression = "base + [mi.item]" }]
        };
        var variables = Items(1);
        variables.Add(Shared(7, "base", baseKey, SharedVariableAccessModes.Read));
        var published = await PublishAsync(host, Workflow(Key("never-set"), body, variables, boundary: true));
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJob = (await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem)).Single();

        await ProcessAsync(host, itemJob);

        long incidentId;
        await using (var db = fixture.CreateDbContext())
        {
            var incident = await db.WorkflowIncidents.SingleAsync(row => row.JobId == itemJob && row.Status == WorkflowIncidentStatuses.Open);
            Assert.Equal(WorkflowIncidentTypes.JobConflict, incident.Type);
            incidentId = incident.Id;
            Assert.Equal(MultiInstanceRecordStatuses.Active,
                (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId)).Status);
        }
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "failure")).ValueKind);

        await SetSharedAsync(host, baseKey, 10);
        await RetryIncidentAsync(host, incidentId);
        await DrainAsync(host, instanceId);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(11, (await LatestAsync(instanceId, "results"))[0].GetProperty("variables").GetProperty("value").GetDouble());
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(instanceId, "failure")).ValueKind);
    }

    // Two of three items fail on their first attempt with no retry left and no boundary, so
    // each opens its own incident. The third item is never admitted while either incident is
    // open: retrying one runs only that item, and admission resumes after the other.
    [Fact]
    public async Task Several_item_incidents_keep_admission_paused_until_each_is_retried_on_its_own()
    {
        await using var host = OpenHost();
        var body = ThrowingBody("if (execution.getVariable('mi.index') < 2 && execution.getVariable('sys.jobAttempt') === 1)\n" +
            "  throw new Error('first attempt of item ' + execution.getVariable('mi.index'));\n" +
            "execution.setVariable('value', execution.getVariable('mi.item'));");
        body.MultiInstance!.MaxConcurrency = 2;
        body.Job = new JobPolicyModel { RetryDelays = [] };
        var published = await PublishAsync(host, Workflow(Key("incidents"), body, Items(3)));
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var admitted = await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem);
        Assert.Equal(2, admitted.Count);
        var before = evaluator.Count;
        await ProcessAsync(host, admitted[0]);
        await ProcessAsync(host, admitted[1]);

        long firstIncident, secondIncident;
        await using (var db = fixture.CreateDbContext())
        {
            var incidents = await db.WorkflowIncidents.Where(row => row.InstanceId == instanceId && row.Status == WorkflowIncidentStatuses.Open)
                .OrderBy(row => row.JobId).ToListAsync();
            Assert.Equal(admitted, incidents.Select(incident => incident.JobId!.Value));
            Assert.All(incidents, incident => Assert.Equal("job_execution_failed", incident.Type));
            (firstIncident, secondIncident) = (incidents[0].Id, incidents[1].Id);
        }

        await RetryIncidentAsync(host, firstIncident);
        await DrainAsync(host, instanceId);

        Assert.Equal(3, evaluator.Count - before);
        await using (var db = fixture.CreateDbContext())
        {
            var items = await db.MultiInstanceActivityItems.Where(row => row.InstanceId == instanceId).OrderBy(row => row.ItemIndex).ToListAsync();
            Assert.Equal(new[] { "completed", "active", "pending" }, items.Select(item => item.Status));
            Assert.Equal(2, await CountJobsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem));
            Assert.True(await db.WorkflowIncidents.AnyAsync(row => row.Id == secondIncident && row.Status == WorkflowIncidentStatuses.Open));
            Assert.Equal(MultiInstanceRecordStatuses.Active, (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId)).Status);
        }
        Assert.Equal("[]", (await LatestAsync(instanceId, "results")).GetRawText());

        await RetryIncidentAsync(host, secondIncident);
        await DrainAsync(host, instanceId);

        Assert.Equal(5, evaluator.Count - before);
        var results = await LatestAsync(instanceId, "results");
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, results.EnumerateArray().Select(row => row.GetProperty("variables").GetProperty("value").GetDouble()));
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
    }

    // Shared catalog changes are not conditional dependencies, so an item's shared write
    // creates no wake work. To make that observable, stop is made true by a repository write
    // that deliberately bypasses the variable-mutation coordinator: any re-evaluation of the
    // boundary would now latch a wake. The real administrative writes at the end show that
    // the subscription is live.
    [Fact]
    public async Task An_item_publishing_a_shared_write_creates_no_conditional_wake_work()
    {
        await using var host = OpenHost();
        var sharedKey = $"tests.script.wake.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, sharedKey, 0);
        var variables = Items(2);
        variables.Add(Shared(7, "counter", sharedKey, SharedVariableAccessModes.ReadWrite));
        var definition = Workflow(Key("no-wake"), IncrementingBody("sequential"), variables);
        AddStopBoundary(definition, cancelActivity: false, ConditionalEventDeliveryModes.DurableAsync);
        var published = await PublishAsync(host, definition);
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        await WriteBypassingConditionsAsync(host, instanceId, "stop", true);

        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceItem);

        Assert.Equal(1, await SharedValueAsync(host, sharedKey));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.WorkflowJobs.AnyAsync(row => row.InstanceId == instanceId && row.Kind == WorkflowJobKinds.ConditionalWake));
            var subscription = await db.ConditionalBoundarySubscriptions.SingleAsync(row => row.InstanceId == instanceId);
            Assert.Equal(0, subscription.Occurrence);
            Assert.False(subscription.IsConditionTrue);
            Assert.False(await db.InstanceHistory.AnyAsync(row => row.InstanceId == instanceId
                && (row.Note == InstanceHistoryNotes.ConditionalLatched || row.Note == InstanceHistoryNotes.ConditionalTriggered)));
        }

        await PatchVariableAsync(host, instanceId, "stop", false);
        await PatchVariableAsync(host, instanceId, "stop", true);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.WorkflowJobs.CountAsync(row => row.InstanceId == instanceId && row.Kind == WorkflowJobKinds.ConditionalWake));
            Assert.Equal(1, (await db.ConditionalBoundarySubscriptions.SingleAsync(row => row.InstanceId == instanceId)).Occurrence);
        }
        await DrainAsync(host, instanceId);

        Assert.Equal(2, await SharedValueAsync(host, sharedKey));
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Contains(7, await after.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync());
    }

    // With a limit of two consecutive automatic activations, the body is the first, the
    // plain async task after it the second, and the next one blocks. Its three items and an
    // item's retry neither add to the count nor reset it.
    [Fact]
    public async Task A_body_counts_once_under_the_automatic_loop_guard_whatever_its_items_and_attempts()
    {
        EngineSettingRecord? previous;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IEngineSettingsRepository>();
            previous = await settings.GetByKeyAsync(WorkflowAutomaticActivationGuard.SettingKey, CancellationToken.None);
            await settings.SetAsync(WorkflowAutomaticActivationGuard.SettingKey, "2", "script-mi-test", CancellationToken.None);
        }
        try
        {
            await using var host = OpenHost();
            var body = ThrowingBody("if (execution.getVariable('mi.index') === 1 && execution.getVariable('sys.jobAttempt') === 1)\n" +
                "  throw new Error('retry me');\n" +
                "execution.setVariable('value', execution.getVariable('mi.item'));");
            body.Job = new JobPolicyModel { RetryDelays = ["PT1S"] };
            var definition = Workflow(Key("loop-guard"), body, Items(3));
            definition.SequenceFlows.Single(flow => flow.Id == 201).TargetRef = 8;
            definition.FlowNodes.Add(new() { Id = 8, Name = "After body", Type = BpmnFlowNodeTypes.Task, AsyncBefore = true, X = 300, Y = 120 });
            definition.FlowNodes.Add(new() { Id = 9, Name = "One too many", Type = BpmnFlowNodeTypes.Task, AsyncBefore = true, X = 400, Y = 120 });
            definition.SequenceFlows.Add(new() { Id = 801, SourceRef = 8, TargetRef = 9 });
            definition.SequenceFlows.Add(new() { Id = 901, SourceRef = 9, TargetRef = 3 });
            var published = await PublishAsync(host, definition);
            var instanceId = await StartAsync(host, published.Id);
            await DrainAsync(host, instanceId);

            await using var db = fixture.CreateDbContext();
            var jobs = await db.WorkflowJobs.Where(row => row.InstanceId == instanceId).ToListAsync();
            Assert.Equal(1, jobs.Single(job => job.Kind == WorkflowJobKinds.AsyncBefore && job.NodeId == 2).AutomaticActivationCount);
            var afterBody = jobs.Single(job => job.Kind == WorkflowJobKinds.AsyncBefore && job.NodeId == 8);
            Assert.Equal(WorkflowJobStatuses.Completed, afterBody.Status);
            Assert.Equal(2, afterBody.AutomaticActivationCount);
            var blocked = jobs.Single(job => job.Kind == WorkflowJobKinds.AsyncBefore && job.NodeId == 9);
            Assert.Equal(WorkflowJobStatuses.Incident, blocked.Status);
            Assert.Equal(3, blocked.AutomaticActivationCount);
            Assert.Equal(0, blocked.AttemptCount);
            var incident = await db.WorkflowIncidents.SingleAsync(row => row.JobId == blocked.Id);
            Assert.Equal(WorkflowIncidentTypes.AutomaticLoopLimit, incident.Type);
            Assert.Contains("\"observedCount\":3", incident.Details, StringComparison.Ordinal);
            Assert.Contains("\"configuredLimit\":2", incident.Details, StringComparison.Ordinal);
            var items = jobs.Where(job => job.Kind == WorkflowJobKinds.MultiInstanceItem).OrderBy(job => job.Id).ToList();
            Assert.Equal(new[] { 1, 2, 1 }, items.Select(job => job.AttemptCount));
            Assert.Equal(3, (await LatestAsync(instanceId, "results")).GetArrayLength());
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<IEngineSettingsRepository>();
            if (previous is null)
                await settings.DeleteAsync(WorkflowAutomaticActivationGuard.SettingKey, "script-mi-test", CancellationToken.None);
            else
                await settings.SetAsync(WorkflowAutomaticActivationGuard.SettingKey, previous.Value, "script-mi-test", CancellationToken.None);
        }
    }

    // The second of three NCalc items divides by zero on every attempt. boundaryFirst takes
    // the boundary at its first failure and cancels the unfinished third item; retryFirst
    // retries it first, so the third item completes before the boundary takes the failure.
    [Theory]
    [InlineData(JobFailureHandling.BoundaryFirst)]
    [InlineData(JobFailureHandling.RetryFirst)]
    public async Task NCalc_failure_rows_follow_the_failure_handling_order(string failureHandling)
    {
        await using var host = OpenHost();
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Body", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true, ScriptFormat = ScriptFormats.NCalc,
            X = 200, Y = 20, MultiInstance = Multi("parallel", "items", "results", Local("value")),
            Assignments = [new() { Variable = "value", Expression = "[mi.item] * 10 / ([mi.item] - 2)" }],
            Job = new JobPolicyModel { FailureHandling = failureHandling, RetryDelays = ["PT10S"] }
        };
        var published = await PublishAsync(host, Workflow(Key("ncalc-rows"), body, Items(3), boundary: true));
        var instanceId = await StartAsync(host, published.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        var itemJobs = await OpenJobIdsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem);
        Assert.Equal(3, itemJobs.Count);
        await ProcessAsync(host, itemJobs[0]);
        await ProcessAsync(host, itemJobs[1]);
        var retryFirst = failureHandling == JobFailureHandling.RetryFirst;
        if (retryFirst)
        {
            Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(itemJobs[1]));
            await ProcessAsync(host, itemJobs[2]);
            await ProcessAsync(host, itemJobs[1]);
        }
        else
        {
            Assert.Equal(WorkflowJobStatuses.Cancelled, await JobStatusAsync(itemJobs[2]));
        }
        await DrainAsync(host, instanceId);

        var results = (await LatestAsync(instanceId, "results")).EnumerateArray().ToArray();
        Assert.Equal(retryFirst ? new[] { "completed", "failed", "completed" } : new[] { "completed", "failed", "cancelled" },
            results.Select(row => row.GetProperty("status").GetString()));
        Assert.Equal(-10, results[0].GetProperty("variables").GetProperty("value").GetDouble());
        Assert.Equal("{}", results[1].GetProperty("variables").GetRawText());
        if (retryFirst) Assert.Equal(30, results[2].GetProperty("variables").GetProperty("value").GetDouble());
        else Assert.Equal("{}", results[2].GetProperty("variables").GetRawText());
        Assert.Equal(WorkflowEngineService.AutomaticScriptFailureCode, (await LatestAsync(instanceId, "failure")).GetString());
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(retryFirst ? 2 : 1, (await db.WorkflowJobs.SingleAsync(row => row.Id == itemJobs[1])).AttemptCount);
        var failed = await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == instanceId && row.ItemIndex == 1);
        Assert.Equal("failed", failed.Status);
        Assert.Equal(WorkflowEngineService.AutomaticScriptFailureCode, failed.ErrorCode);
        Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.InstanceId == instanceId));
    }

    // A JavaScript stage locks every shared binding of the definition, read-only ones
    // included, FOR UPDATE in ordinal key order, in a short committed transaction; nothing is
    // held while the script runs. Host A's item is held inside its script by the evaluator
    // gate. While it waits, host B's item (another instance binding the same keys) is seen
    // blocked on a held row lock of the read-only, higher key while holding the lower key,
    // and then stages, evaluates, and publishes. Every interleaving is forced, none timed.
    [Fact]
    public async Task A_JavaScript_stage_locks_every_binding_in_key_order_and_holds_none_while_the_script_runs()
    {
        await using var hostA = OpenHost();
        await using var hostB = OpenHost();
        var suffix = Guid.NewGuid().ToString("N");
        // Alias order is the reverse of key order: zeta binds the lower key.
        var lowKey = $"tests.script.lock.a.{suffix}";
        var highKey = $"tests.script.lock.b.{suffix}";
        await CreateSharedAsync(hostA, lowKey, 0);
        await CreateSharedAsync(hostA, highKey, 7);
        WorkflowModel Definition(string name, string script)
        {
            var variables = Items(1);
            variables.Add(Shared(7, "zeta", lowKey, SharedVariableAccessModes.ReadWrite));
            variables.Add(Shared(8, "alpha", highKey, SharedVariableAccessModes.Read));
            return Workflow(Key(name), ThrowingBody(script), variables);
        }
        var reader = await PublishAsync(hostA, Definition("lock-reader",
            "execution.setVariable('value', execution.getVariable('alpha') + execution.getVariable('mi.item'));"));
        var writer = await PublishAsync(hostA, Definition("lock-writer",
            "execution.setVariable('zeta', execution.getVariable('zeta') + 1);\nexecution.setVariable('value', execution.getVariable('zeta'));"));
        var readerId = await StartAsync(hostA, reader.Id);
        var writerId = await StartAsync(hostB, writer.Id);
        foreach (var (host, instanceId) in new[] { (hostA, readerId), (hostB, writerId) })
        {
            await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
            await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        }
        var readerItem = (await OpenJobIdsAsync(readerId, WorkflowJobKinds.MultiInstanceItem)).Single();
        var writerItem = (await OpenJobIdsAsync(writerId, WorkflowJobKinds.MultiInstanceItem)).Single();
        var before = evaluator.Count;

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        evaluator.BeforeNext = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(120));
        };
        var readerRun = Task.Run(() => ProcessAsync(hostA, readerItem));
        Task? writerRun = null;
        try
        {
            Assert.True(await EventuallyAsync(() => Task.FromResult(entered.IsSet || readerRun.IsCompleted)) && entered.IsSet,
                "Host A's item never reached its script.");
            // Host A's stage committed before its script started: it holds no shared lock now.
            Assert.False(await IsRowLockedAsync(lowKey));
            Assert.False(await IsRowLockedAsync(highKey));

            await using (var holder = fixture.CreateDbContext())
            await using (var held = await holder.Database.BeginTransactionAsync())
            {
                await holder.Database.ExecuteSqlInterpolatedAsync(
                    $"""SELECT 1 FROM flowbit.shared_variables WHERE "Key" = {highKey} FOR UPDATE""");
                var holderPid = await holder.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
                writerRun = Task.Run(() => ProcessAsync(hostB, writerItem));
                Assert.True(await EventuallyAsync(async () => writerRun.IsCompleted || await IsBlockedByAsync(holderPid)),
                    "Host B's stage was never seen waiting for the held read-only binding.");
                Assert.False(writerRun.IsCompleted, "Host B's stage did not lock the read-only binding.");
                // It waits for the higher key while already holding the lower one.
                Assert.True(await IsRowLockedAsync(lowKey), "Host B's stage did not lock the lower key first.");
                await held.RollbackAsync();
            }

            // Host B's item finishes while host A's script is still running.
            await writerRun.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.False(readerRun.IsCompleted);
            Assert.Equal(1, await SharedValueAsync(hostB, lowKey));
        }
        finally
        {
            release.Set();
        }
        await readerRun.WaitAsync(TimeSpan.FromSeconds(60));
        await DrainAsync(hostA, readerId);
        await DrainAsync(hostB, writerId);

        Assert.Equal(2, evaluator.Count - before);
        // The reader wrote no shared alias, so its candidate holds no fence and the writer's
        // commit after its stage is no conflict.
        Assert.Equal(8, (await LatestAsync(readerId, "results"))[0].GetProperty("variables").GetProperty("value").GetDouble());
        Assert.Equal(1, (await LatestAsync(writerId, "results"))[0].GetProperty("variables").GetProperty("value").GetDouble());
        Assert.Equal(1, await SharedValueAsync(hostA, lowKey));
        await using var db = fixture.CreateDbContext();
        foreach (var instanceId in new[] { readerId, writerId })
        {
            Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
            Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.InstanceId == instanceId));
        }
    }

    // A JavaScript body over an unset shared collection, with a readWrite binding its
    // ordinary node plan would lock. Entry fails because the source has no value.
    private async Task<long> StartUnsetSharedCollectionAsync(WebApplicationFactory<Program> host, string writableKey,
        string? failureValidation)
    {
        var sharedKey = $"tests.script.items.{Guid.NewGuid():N}";
        using (var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
                   new CreateSharedVariableRequest(sharedKey, "number", IsArray: true, Nullable: false, HasValue: false, null)))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = ThrowingBody("execution.setVariable('value', 1);");
        body.MultiInstance!.CollectionVariable = "sharedItems";
        var variables = Items(0);
        variables.Add(new() { Id = 8, Name = "sharedItems", DataType = "number", IsArray = true, Scope = "shared", SharedKey = sharedKey, Access = "read" });
        variables.Add(new() { Id = 9, Name = "counter", DataType = "number", Scope = "shared", SharedKey = writableKey, Access = "readWrite" });
        var definition = Workflow(Key("entry-route"), body, variables, boundary: true);
        definition.Variables.Single(variable => variable.Name == "failure").Validation = failureValidation;
        var published = await PublishAsync(host, definition);
        return await StartAsync(host, published.Id);
    }

    private static FlowNodeModel ThrowingBody(string script) => new()
    {
        Id = 2, Name = "Body", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
        ScriptFormat = ScriptFormats.JavaScript, X = 200, Y = 20,
        MultiInstance = Multi("parallel", "items", "results", Local("value")),
        Script = script
    };

    private static List<VariableModel> Items(int count) =>
    [
        new() { Id = 1, Name = "items", DataType = "number", IsArray = true, DefaultValue = Json(Enumerable.Range(1, count).ToArray()) },
        new() { Id = 2, Name = "results", DataType = "json", DefaultValue = Json(Array.Empty<object>()) }
    ];

    // Each item adds one to the shared counter and keeps its item as a local.
    private static FlowNodeModel IncrementingBody(string mode)
    {
        var body = ThrowingBody("execution.setVariable('counter', execution.getVariable('counter') + 1);\n" +
            "execution.setVariable('value', execution.getVariable('mi.item'));");
        body.MultiInstance = Multi(mode, "items", "results", Local("value"));
        return body;
    }

    private static VariableModel Shared(int id, string name, string key, string access, string type = "number",
        bool isArray = false) => new()
    {
        Id = id, Name = name, DataType = type, IsArray = isArray, Scope = VariableScopes.Shared, SharedKey = key, Access = access
    };

    // A conditional boundary on the body over the instance variable stop, ending at node 7.
    private static void AddStopBoundary(WorkflowModel definition, bool cancelActivity,
        string deliveryMode = ConditionalEventDeliveryModes.Atomic)
    {
        definition.Variables.Add(new() { Id = 95, Name = "stop", DataType = "boolean", DefaultValue = Json(false) });
        definition.FlowNodes.Add(new()
        {
            Id = 6, Name = "Stop requested", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = 2,
            CancelActivity = cancelActivity,
            Conditional = new ConditionalDefinitionModel { Condition = "stop == true", DeliveryMode = deliveryMode }
        });
        definition.FlowNodes.Add(new() { Id = 7, Name = "Stopped", Type = BpmnFlowNodeTypes.EndEvent, X = 500, Y = 300 });
        definition.SequenceFlows.Add(new() { Id = 601, SourceRef = 6, TargetRef = 7 });
    }

    // A review task (node 10) before the body; its Approve flow (1001) enters the body.
    private static void AddReview(WorkflowModel definition)
    {
        definition.FlowNodes.Add(new() { Id = 10, Name = "Review", Type = BpmnFlowNodeTypes.UserTask, X = 120, Y = 20 });
        definition.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 10;
        definition.SequenceFlows.Add(new() { Id = 1001, Name = "Approve", SourceRef = 10, TargetRef = 2 });
    }

    // A json array of short strings that PostgreSQL prints in exactly `bytes` bytes:
    // n strings of total length L print as L + 4n ("[", "]", quotes, and ", ").
    private static JsonElement Blob(long bytes)
    {
        var count = (int)((bytes + 1000) / 1004);
        var last = bytes - 4L * count - 1000L * (count - 1);
        Assert.InRange(last, 0, 1003);
        var parts = Enumerable.Range(0, count).Select(index => new string('x', index == count - 1 ? (int)last : 1000)).ToArray();
        var blob = Json(parts);
        Assert.Equal(bytes, JsonbText.ByteCount(blob));
        return blob;
    }

    private async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program>? host, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        ApiTestAuth.Authorize(request, "script-admin", "admin");
        return await (host?.CreateClient() ?? fixture.Client).SendAsync(request);
    }

    private async Task<WorkflowDetailDto> PublishAsync(WebApplicationFactory<Program> host, WorkflowModel definition)
    {
        using var response = await SendAsync(host, HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(definition, true));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonSerializer.Deserialize<WorkflowDetailDto>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<long> StartAsync(WebApplicationFactory<Program> host, long workflowId,
        Dictionary<string, JsonElement>? values = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>().StartInstanceSlimAsync(
            workflowId, null, new ActorContext("starter", ["User"], new Dictionary<string, string>()), null, values,
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

    private Task SetSharedAsync(WebApplicationFactory<Program> host, string key, int value) =>
        SetSharedValueAsync(host, key, Json(value));

    private async Task SetSharedValueAsync(WebApplicationFactory<Program> host, string key, JsonElement value)
    {
        using var metadata = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{key}");
        var revision = (await metadata.Content.ReadFromJsonAsync<SharedVariableMetadataDto>())!.Revision;
        using var updated = await SendAsync(host, HttpMethod.Put, $"/api/shared-variables/{key}/value",
            new UpdateSharedVariableValueRequest(value, revision));
        Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync());
    }

    private async Task<double> SharedValueAsync(WebApplicationFactory<Program> host, string key) =>
        (await SharedJsonAsync(host, key)).GetDouble();

    private async Task<JsonElement> SharedJsonAsync(WebApplicationFactory<Program> host, string key)
    {
        using var response = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{key}/value");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("value").Clone();
    }

    // Archives or reactivates a catalog key at its current revision.
    private async Task ChangeSharedStatusAsync(WebApplicationFactory<Program> host, string key, string action)
    {
        using var metadata = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{key}");
        var revision = (await metadata.Content.ReadFromJsonAsync<SharedVariableMetadataDto>())!.Revision;
        using var changed = await SendAsync(host, HttpMethod.Post, $"/api/shared-variables/{key}/{action}",
            action == "archive" ? new ArchiveSharedVariableRequest(revision) : new ReactivateSharedVariableRequest(revision));
        Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
    }

    // An administrative write, which goes through the conditional coordinator.
    private async Task PatchVariableAsync(WebApplicationFactory<Program> host, long instanceId, string name, object value)
    {
        using var response = await SendAsync(host, HttpMethod.Patch, $"/api/instances/{instanceId}/variables",
            new UpdateInstanceVariablesRequest([new InstanceVariableWriteDto(name, Json(value))], "script body test",
                $"script-mi-{Guid.NewGuid():N}"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    // A direct repository write. It bypasses the variable-mutation coordinator, so no
    // conditional subscription learns of it; only the no-wake test uses it, on purpose.
    private static async Task WriteBypassingConditionsAsync(WebApplicationFactory<Program> host, long instanceId,
        string name, object value)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowRuntimeRepository>().AddVariableAsync(
            instanceId, name, null, "script-mi-test", Json(value), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
    }

    private async Task TakeReviewAsync(WebApplicationFactory<Program> host, long instanceId)
    {
        long taskId;
        await using (var db = fixture.CreateDbContext())
            taskId = await db.UserTasks.Where(task => task.InstanceId == instanceId && task.Status == "active")
                .Select(task => task.Id).SingleAsync();
        using var response = await SendAsync(host, HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/1001", new TakeFlowRequest(null));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task ReactivateAtReviewAsync(WebApplicationFactory<Program> host, long instanceId)
    {
        InstanceReactivationPreviewDto preview;
        using (var previewed = await SendAsync(host, HttpMethod.Get, $"/api/instances/{instanceId}/reactivation"))
        {
            Assert.True(previewed.IsSuccessStatusCode, await previewed.Content.ReadAsStringAsync());
            preview = (await previewed.Content.ReadFromJsonAsync<InstanceReactivationPreviewDto>())!;
        }
        using var reactivated = await SendAsync(host, HttpMethod.Post, $"/api/instances/{instanceId}/reactivation",
            new ReactivateInstanceRequest(10, preview.WorkflowId, preview.ExpectedUpdatedAt, "Review again"));
        Assert.True(reactivated.StatusCode == HttpStatusCode.OK, await reactivated.Content.ReadAsStringAsync());
    }

    // The input snapshot of the instance's only body, and the size its stored row counts.
    private async Task<(JsonElement Snapshot, int SizeBytes)> SnapshotAsync(WebApplicationFactory<Program> host, long instanceId)
    {
        long bodyId;
        int sizeBytes;
        await using (var db = fixture.CreateDbContext())
        {
            var row = await db.MultiInstanceExecutions.Where(row => row.InstanceId == instanceId)
                .Select(row => new { row.Id, row.InputSnapshot!.SizeBytes }).SingleAsync();
            (bodyId, sizeBytes) = (row.Id, row.SizeBytes);
        }
        await using var scope = host.Services.CreateAsyncScope();
        var snapshot = (await scope.ServiceProvider.GetRequiredService<IAutomaticMultiInstanceRepository>()
            .GetInputSnapshotAsync(bodyId, CancellationToken.None))!.Value.Clone();
        return (snapshot, sizeBytes);
    }

    private async Task<long> TraversalsAsync(long instanceId, int flowId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SequenceFlowSummaries.Where(row => row.InstanceId == instanceId && row.SequenceFlowId == flowId)
            .Select(row => row.TraversalCount).SingleOrDefaultAsync();
    }

    // True when another transaction holds the row lock: FOR UPDATE NOWAIT fails at once.
    private async Task<bool> IsRowLockedAsync(string key)
    {
        await using var probe = fixture.CreateDbContext();
        await using var transaction = await probe.Database.BeginTransactionAsync();
        try
        {
            await probe.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM flowbit.shared_variables WHERE "Key" = {key} FOR UPDATE NOWAIT""");
            return false;
        }
        catch (Exception error) when ((error as PostgresException ?? error.InnerException as PostgresException)?.SqlState
                                      == PostgresErrorCodes.LockNotAvailable)
        {
            return true;
        }
    }

    private async Task<bool> IsBlockedByAsync(int holderPid)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE {holderPid} = ANY(pg_blocking_pids(pid))").SingleAsync() > 0;
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        return await condition();
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
                new WorkflowJobLeaseRequest($"script-mi:{Guid.NewGuid():N}", MaxCount: 1, MaxActivityCount: 1,
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

    /// <summary>The real Jint evaluator, counting evaluations and running one hook before the next.</summary>
    private sealed class CountingScriptEvaluator : IScriptEvaluator
    {
        private readonly JintScriptEvaluator inner = new(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance);
        private int count;
        private Action? beforeNext;

        public int Count => Volatile.Read(ref count);

        public Action? BeforeNext
        {
            set => Volatile.Write(ref beforeNext, value);
        }

        public ScriptResult Evaluate(string script, IScriptContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            Interlocked.Exchange(ref beforeNext, null)?.Invoke();
            return inner.Evaluate(script, context, cancellationToken);
        }

        public bool IsValid(string script, out string? error) => inner.IsValid(script, out error);
    }
}
