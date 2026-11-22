using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using Npgsql;
using Xunit;
using static Flowbit.Tests.CallActivityHarness;

namespace Flowbit.Tests;

/// <summary>
/// TASK-05D end to end against PostgreSQL: call bodies are published over HTTP through a
/// test host, and every entry, dispatch, callStart,
/// callResume, and completion job runs through the registered job processor, as Workers
/// run them. Children are real instances of real published workflows.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class AutomaticMultiInstanceCallRuntimeTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private const int Body = 2;
    private readonly List<long> roots = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Other suites lease specific jobs from this shared database.
        await using var db = fixture.CreateDbContext();
        var instanceIds = await db.WorkflowInstances.AsNoTracking()
            .Where(instance => roots.Contains(instance.Id)
                || instance.CallRootInstanceId != null && roots.Contains(instance.CallRootInstanceId.Value))
            .Select(instance => instance.Id).ToListAsync();
        var distant = DateTimeOffset.UtcNow.AddYears(10);
        await db.WorkflowJobs
            .Where(job => job.InstanceId != null && instanceIds.Contains(job.InstanceId.Value)
                && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry
                    || job.Status == WorkflowJobStatuses.Running || job.Status == WorkflowJobStatuses.ResultReady))
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.DueAt, distant)
                .SetProperty(job => job.LeaseExpiresAt, job => job.LeaseExpiresAt == null ? null : distant));
    }

    private WebApplicationFactory<Program> OpenHost(Action<CallActivityOptions>? limits = null,
        Action<IServiceCollection>? configure = null) =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            if (limits is not null)
            {
                var callOptions = new CallActivityOptions();
                limits(callOptions);
                services.RemoveAll<CallActivityOptions>();
                services.AddSingleton(callOptions);
            }
            configure?.Invoke(services);
        }));

    // An open host whose unit of work holds the next DiscardChanges after the gate is armed.
    // Every adapter step that runs in two transactions discards between them, so a job
    // processed here stops after its first transaction, with no lock held, until released.
    private WebApplicationFactory<Program> OpenGatedHost(DiscardGate gate) => OpenHost(configure: services =>
    {
        services.RemoveAll<IUnitOfWork>();
        services.AddScoped<IUnitOfWork>(provider => new GatedUnitOfWork(
            new Flowbit.Infrastructure.Repositories.UnitOfWork(
                provider.GetRequiredService<Flowbit.Infrastructure.Data.AppDbContext>()),
            gate));
    });

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    private static string NewKey(string name) => $"call-mi-{name}-{Guid.NewGuid():N}";

    private static readonly IReadOnlyList<VariableModel> ChildInputs =
        [Input("order", WorkflowVariableTypes.Json), Input("orderIndex")];

    private static MultiInstanceOutputVariableModel Local(string name, string type = "number", bool nullable = false) => new()
    {
        Name = name, DataType = type, IsArray = false, Nullable = nullable
    };

    /// <summary>
    /// The plan's example: one child per order, the item and its index as inputs, and the
    /// child's index and order copied back to item-local outputs.
    /// </summary>
    private static WorkflowModel Parent(string key, string childKey, string mode, object[] orders,
        bool boundary = false, int? maxConcurrency = null)
    {
        var model = new WorkflowModel
        {
            Id = key, Name = key, InitialEventId = Start,
            Variables =
            [
                new() { Id = 1, Name = "orders", DataType = "json", IsArray = true, DefaultValue = Json(orders) },
                new() { Id = 2, Name = "orderResults", DataType = "json", DefaultValue = Json(Array.Empty<object>()) }
            ],
            FlowNodes =
            [
                new() { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new()
                {
                    Id = Body, Name = "Check each order", Type = BpmnFlowNodeTypes.CallActivity,
                    MultiInstance = new()
                    {
                        Mode = mode, Source = "collection", CollectionVariable = "orders", CompletionEvaluation = "afterAll",
                        MaxConcurrency = maxConcurrency ?? (mode == "sequential" ? 1 : 4), ResultVariable = "orderResults",
                        OutputVariables = [Local("itemIndex"), Local("echo", "json")]
                    },
                    CallActivity = new()
                    {
                        WorkflowKey = childKey,
                        InputMappings =
                        [
                            Mapping("mi.item", "order", WorkflowVariableTypes.Json),
                            Mapping("mi.index", "orderIndex")
                        ],
                        OutputMappings =
                        [
                            Mapping("orderIndex", "itemIndex"),
                            Mapping("order", "echo", WorkflowVariableTypes.Json)
                        ]
                    }
                },
                new() { Id = End, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new() { Id = 101, SourceRef = Start, TargetRef = Body }, new() { Id = 201, SourceRef = Body, TargetRef = End }]
        };
        if (boundary)
        {
            model.Variables.Add(new() { Id = 3, Name = "failure", DataType = "string", Nullable = true });
            model.FlowNodes.Add(new()
            {
                Id = Boundary, Name = "Caught", Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, AttachedToRef = Body,
                ErrorVariable = "failure"
            });
            model.FlowNodes.Add(new() { Id = BoundaryEnd, Name = "Handled", Type = BpmnFlowNodeTypes.EndEvent });
            model.SequenceFlows.Add(new() { Id = 401, SourceRef = Boundary, TargetRef = BoundaryEnd });
        }
        return model;
    }

    private static object[] Orders(params string[] ids) => ids.Select(id => (object)new { id }).ToArray();

    [Fact]
    public async Task Parallel_body_starts_one_child_per_item_and_publishes_ordered_local_results()
    {
        await using var host = OpenHost();
        var childKey = NewKey("child");
        var child = await PublishAsync(host, ImmediateChild(childKey, ChildInputs));
        var parent = await PublishAsync(host, Parent(NewKey("parallel"), childKey, "parallel", Orders("a", "b", "c")));
        var root = await StartAsync(host, parent.Id);

        // The token waits on the body's own entry job; no body or child exists yet.
        await using (var db = fixture.CreateDbContext())
        {
            var token = await db.ExecutionTokens.SingleAsync(row => row.InstanceId == root && row.NodeId == Body);
            Assert.Equal(WorkflowJobKinds.MultiInstanceStart, token.WaitState);
            Assert.False(await db.MultiInstanceExecutions.AnyAsync(row => row.InstanceId == root));
            Assert.Null(token.CurrentNodeExecutionId);
        }

        await ProcessNextAsync(host, root, WorkflowJobKinds.MultiInstanceStart);
        await using (var db = fixture.CreateDbContext())
        {
            var body = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root);
            Assert.Equal(child.Id, body.PinnedChildDefinitionId);
            Assert.Equal(Start, body.PinnedChildStartEventId);
            Assert.Equal(AutomaticMultiInstancePinStates.Pinned, body.PinState);
            Assert.Equal(3, body.TotalCount);
        }

        await ProcessNextAsync(host, root, WorkflowJobKinds.MultiInstanceDispatch);
        var starts = await OpenJobIdsAsync(root, WorkflowJobKinds.CallStart);
        Assert.Equal(3, starts.Count);
        // Admission reserves one invocation per item and reads no input yet.
        await using (var db = fixture.CreateDbContext())
        {
            var invocations = await db.CallActivityExecutions.Where(row => row.ParentInstanceId == root).ToListAsync();
            Assert.Equal(3, invocations.Count);
            Assert.All(invocations, invocation =>
            {
                Assert.Equal(CallInvocationStates.Queued, invocation.State);
                Assert.True(invocation.CapacityReserved);
                Assert.NotNull(invocation.AutomaticMultiInstanceItemId);
                Assert.Null(invocation.InputValuesJson);
            });
        }

        // Children finish at once and in reverse order; the aggregate stays in item order.
        foreach (var jobId in starts.AsEnumerable().Reverse()) await ProcessAsync(host, jobId);
        await DrainAsync(host, root);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        var children = await after.WorkflowInstances.Where(row => row.CallRootInstanceId == root).ToListAsync();
        Assert.Equal(3, children.Count);
        Assert.All(children, row => Assert.Equal(child.Id, row.WorkflowDefinitionId));
        var settled = await after.CallActivityExecutions.Where(row => row.ParentInstanceId == root).ToListAsync();
        Assert.All(settled, row => Assert.Equal(CallInvocationStates.Completed, row.State));
        Assert.Equal(3, settled.Select(row => row.ChildInstanceId).Distinct().Count());
        var results = await LatestAsync(root, "orderResults");
        Assert.Equal(new[] { "a", "b", "c" }, results.EnumerateArray().Select(row => row.GetProperty("item").GetProperty("id").GetString()));
        Assert.Equal(new[] { 0, 1, 2 }, results.EnumerateArray().Select(row => row.GetProperty("variables").GetProperty("itemIndex").GetInt32()));
        Assert.Equal(new[] { "a", "b", "c" },
            results.EnumerateArray().Select(row => row.GetProperty("variables").GetProperty("echo").GetProperty("id").GetString()));
        // Locals never become instance variables, and the pin is released at settlement.
        Assert.False(await after.InstanceVariables.AnyAsync(row => row.InstanceId == root
            && (row.VariableName == "itemIndex" || row.VariableName == "echo")));
        var settledBody = await after.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root);
        Assert.Equal(MultiInstanceRecordStatuses.Completed, settledBody.Status);
        Assert.Equal(AutomaticMultiInstancePinStates.Released, settledBody.PinState);
        Assert.Equal(3, await after.NodeExecutions.CountAsync(row => row.InstanceId == root && row.NodeId == Body
            && row.ExecutionKind == NodeExecutionRecordKinds.AutomaticActivityItem && row.Status == "completed"));
        // The children ran as the fixed system actor.
        Assert.All(children, row => Assert.Equal("callActivity", row.StartedBy));
    }

    [Fact]
    public async Task Sequential_body_starts_the_next_child_after_the_previous_result_commits_and_keeps_its_pin()
    {
        await using var host = OpenHost();
        var childKey = NewKey("waiting");
        var v1 = await PublishAsync(host, WaitingChild(childKey, ChildInputs));
        var parent = await PublishAsync(host, Parent(NewKey("sequential"), childKey, "sequential", Orders("first", "second")));
        var root = await StartAsync(host, parent.Id);
        await ProcessNextAsync(host, root, WorkflowJobKinds.MultiInstanceStart);
        await ProcessNextAsync(host, root, WorkflowJobKinds.MultiInstanceDispatch);
        await ProcessNextAsync(host, root, WorkflowJobKinds.CallStart);
        var first = Assert.Single(await ChildrenAsync(root));

        // A new default for the family changes nothing for this body: its pin stays v1.
        var v2 = await PublishNewDefaultAsync(host, v1.Id, WaitingChild(childKey, ChildInputs));
        Assert.NotEqual(v1.Id, v2.Id);
        // While the body is open, its pinned version cannot be unpublished or deleted.
        using (var unpublish = await SendAsync(host, HttpMethod.Post, $"/api/workflows/{v1.Id}/unpublish"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unpublish.StatusCode);
            Assert.Contains("pinned by open automatic multi-instance call work", await unpublish.Content.ReadAsStringAsync());
        }
        using (var delete = await SendAsync(host, HttpMethod.Delete, $"/api/workflows/{v1.Id}"))
            Assert.False(delete.IsSuccessStatusCode);
        // While the first child waits for a person, nothing else is admitted.
        await DrainAsync(host, root);
        Assert.Single(await ChildrenAsync(root));

        await TakeChildActionAsync(host, first);
        await DrainAsync(host, root);
        var both = await ChildrenAsync(root);
        Assert.Equal(2, both.Count);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.All(await db.WorkflowInstances.Where(row => both.Contains(row.Id)).ToListAsync(),
                row => Assert.Equal(v1.Id, row.WorkflowDefinitionId));
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        }

        await TakeChildActionAsync(host, both.Single(id => id != first));
        await DrainAsync(host, root);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(new[] { "first", "second" },
            (await LatestAsync(root, "orderResults")).EnumerateArray().Select(row => row.GetProperty("item").GetProperty("id").GetString()));
    }

    [Fact]
    public async Task An_empty_body_completes_without_a_pin_even_when_its_target_is_missing()
    {
        await using var host = OpenHost();
        var childKey = NewKey("missing");
        await PublishAsync(host, ImmediateChild(childKey, ChildInputs));
        var parent = await PublishAsync(host, Parent(NewKey("empty"), childKey, "parallel", []));
        var harness = new CallActivityHarness(fixture);
        await harness.RemoveDefaultAsync(childKey);
        var root = await StartAsync(host, parent.Id);

        await ProcessNextAsync(host, root, WorkflowJobKinds.MultiInstanceStart);
        await DrainAsync(host, root);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        var body = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root);
        Assert.Null(body.PinnedChildDefinitionId);
        Assert.Null(body.PinState);
        Assert.Equal("[]", (await LatestAsync(root, "orderResults")).GetRawText());
        Assert.False(await db.CallActivityExecutions.AnyAsync(row => row.ParentInstanceId == root));
    }

    [Fact]
    public async Task A_nonempty_body_without_a_target_fails_at_entry_before_any_item_exists()
    {
        await using var host = OpenHost();
        var childKey = NewKey("gone");
        await PublishAsync(host, ImmediateChild(childKey, ChildInputs));
        var caught = await PublishAsync(host, Parent(NewKey("caught"), childKey, "parallel", Orders("a"), boundary: true));
        var uncaught = await PublishAsync(host, Parent(NewKey("uncaught"), childKey, "parallel", Orders("a")));
        await new CallActivityHarness(fixture).RemoveDefaultAsync(childKey);

        // With a catch-all boundary the entry takes it at once.
        var caughtRoot = await StartAsync(host, caught.Id);
        await ProcessNextAsync(host, caughtRoot, WorkflowJobKinds.MultiInstanceStart);
        // Without one, the entry job retries; no body, item, or invocation exists.
        var uncaughtRoot = await StartAsync(host, uncaught.Id);
        var entryJob = (await OpenJobIdsAsync(uncaughtRoot, WorkflowJobKinds.MultiInstanceStart)).Single();
        await ProcessAsync(host, entryJob);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == caughtRoot)).Status);
        Assert.Equal(CallFailureCodes.TargetUnavailable, (await LatestAsync(caughtRoot, "failure")).GetString());
        var retry = await db.WorkflowJobs.SingleAsync(row => row.Id == entryJob);
        Assert.Equal(WorkflowJobStatuses.Retry, retry.Status);
        Assert.Equal(CallFailureCodes.TargetUnavailable, retry.LastFailureCode);
        foreach (var root in new[] { caughtRoot, uncaughtRoot })
        {
            Assert.False(await db.MultiInstanceExecutions.AnyAsync(row => row.InstanceId == root));
            Assert.False(await db.CallActivityExecutions.AnyAsync(row => row.ParentInstanceId == root));
        }
    }

    [Fact]
    public async Task A_faulted_child_takes_the_catch_all_with_a_partial_result_and_without_one_faults_the_parent()
    {
        await using var host = OpenHost();
        var childKey = NewKey("faulting");
        await PublishAsync(host, FaultingChildWithInputs(childKey));
        var caught = await PublishAsync(host, FaultingParent(NewKey("caught"), childKey, boundary: true));
        var uncaught = await PublishAsync(host, FaultingParent(NewKey("uncaught"), childKey, boundary: false));

        var caughtRoot = await StartAsync(host, caught.Id);
        await ProcessNextAsync(host, caughtRoot, WorkflowJobKinds.MultiInstanceStart);
        await ProcessNextAsync(host, caughtRoot, WorkflowJobKinds.MultiInstanceDispatch);
        // Both children fault as they start; the first result stops the body.
        foreach (var start in await OpenJobIdsAsync(caughtRoot, WorkflowJobKinds.CallStart)) await ProcessAsync(host, start);
        await DrainAsync(host, caughtRoot);

        var uncaughtRoot = await StartAsync(host, uncaught.Id);
        await DrainAsync(host, uncaughtRoot);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == caughtRoot)).Status);
        Assert.Equal(CallFailureCodes.ChildFaulted, (await LatestAsync(caughtRoot, "failure")).GetString());
        var partial = await LatestAsync(caughtRoot, "orderResults");
        Assert.Equal(2, partial.GetArrayLength());
        Assert.Contains(partial.EnumerateArray(), row => row.GetProperty("status").GetString() == "failed");
        Assert.All(partial.EnumerateArray(), row => Assert.NotEqual("completed", row.GetProperty("status").GetString()));
        // The failed item's invocation settles as failed, keeping the child's fault code.
        var failed = await db.CallActivityExecutions.SingleAsync(row => row.ParentInstanceId == caughtRoot
            && row.State == CallInvocationStates.Failed);
        Assert.Equal(CallFailureCodes.ChildFaulted, failed.FailureCode);
        Assert.Equal("child.rejected", failed.ChildFaultCode);
        Assert.Equal(WorkflowInstanceStatuses.Faulted, (await db.WorkflowInstances.SingleAsync(row => row.Id == uncaughtRoot)).Status);
        Assert.Equal(CallFailureCodes.ChildFaulted,
            (await db.ExecutionTokens.SingleAsync(row => row.InstanceId == uncaughtRoot && row.Status == "faulted")).FaultCode);
    }

    [Fact]
    public async Task The_root_open_call_limit_holds_items_pending_and_admits_them_as_children_finish()
    {
        await using var host = OpenHost(limits => limits.MaxOpenInvocationsPerRoot = 1);
        var childKey = NewKey("limited");
        await PublishAsync(host, WaitingChild(childKey, ChildInputs));
        var parent = await PublishAsync(host, Parent(NewKey("limited-parent"), childKey, "parallel", Orders("a", "b"), maxConcurrency: 2));
        var root = await StartAsync(host, parent.Id);
        await ProcessNextAsync(host, root, WorkflowJobKinds.MultiInstanceStart);
        await ProcessNextAsync(host, root, WorkflowJobKinds.MultiInstanceDispatch);

        // The window allows two, the root one: the second item stays pending with no invocation.
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.CallActivityExecutions.CountAsync(row => row.ParentInstanceId == root));
            Assert.Equal(1, await db.MultiInstanceActivityItems.CountAsync(row => row.InstanceId == root && row.Status == "pending"));
        }
        await DrainAsync(host, root);
        var first = Assert.Single(await ChildrenAsync(root));
        await TakeChildActionAsync(host, first);
        await DrainAsync(host, root);
        var both = await ChildrenAsync(root);
        Assert.Equal(2, both.Count);
        await TakeChildActionAsync(host, both.Single(id => id != first));
        await DrainAsync(host, root);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.False(await after.WorkflowIncidents.AnyAsync(row => row.InstanceId == root));
    }

    [Fact]
    public async Task A_full_root_with_nothing_in_flight_opens_a_call_capacity_incident_only_at_the_longest_backoff()
    {
        await using var host = OpenHost(limits => limits.MaxOpenInvocationsPerRoot = 1);
        var childKey = NewKey("blocking");
        await PublishAsync(host, WaitingChild(childKey, ChildInputs));
        // An ordinary call on a parallel branch holds the root's only open call.
        var parent = await PublishAsync(host, BranchedParent(NewKey("branched"), childKey));
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root, skipKind: WorkflowJobKinds.MultiInstanceDispatch);
        var ordinaryChild = Assert.Single(await ChildrenAsync(root));

        var dispatch = (await OpenJobIdsAsync(root, WorkflowJobKinds.MultiInstanceDispatch)).Single();
        // A full root parks the dispatch with a growing delay and no incident, until the
        // longest delay with nothing of its own in flight. A Worker may already have leased
        // the wake once, so count by its lease generation rather than by these calls.
        for (var attempt = 0; ; attempt++)
        {
            Assert.True(attempt < 8, "The dispatch never opened its capacity incident.");
            await ProcessAsync(host, dispatch);
            await using var db = fixture.CreateDbContext();
            var job = await db.WorkflowJobs.SingleAsync(row => row.Id == dispatch);
            var incident = await db.WorkflowIncidents.SingleOrDefaultAsync(row => row.JobId == dispatch && row.Status == "open");
            Assert.False(await db.CallActivityExecutions.AnyAsync(row => row.ParentInstanceId == root
                && row.AutomaticMultiInstanceItemId != null));
            Assert.Equal("callCapacity", (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root)).AdmissionBlockedReason);
            if (job.LeaseGeneration < 5)
            {
                Assert.Null(incident);
                Assert.Equal(WorkflowJobStatuses.ResultReady, job.Status);
                continue;
            }
            Assert.Equal(CallFailureCodes.CapacityExceeded, incident!.Type);
            break;
        }

        // The ordinary call finishes and frees the root; retrying admits the item.
        await TakeChildActionAsync(host, ordinaryChild);
        await DrainAsync(host, root);
        await RetryIncidentAsync(host, dispatch);
        await DrainAsync(host, root);
        var itemChild = Assert.Single(await ChildrenAsync(root), id => id != ordinaryChild);
        await TakeChildActionAsync(host, itemChild);
        await DrainAsync(host, root);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
    }

    [Fact]
    public async Task Cancelling_the_parent_settles_unfinished_invocations_and_cancels_children_while_a_late_result_writes_nothing()
    {
        await using var host = OpenHost();
        var childKey = NewKey("cancel");
        await PublishAsync(host, WaitingChild(childKey, ChildInputs));
        var parent = await PublishAsync(host, Parent(NewKey("cancel-parent"), childKey, "parallel", Orders("a", "b")));
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root);
        var children = await ChildrenAsync(root);
        Assert.Equal(2, children.Count);

        using (var cancelled = await SendAsync(host, HttpMethod.Post, $"/api/instances/{root}/cancel", new { }))
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        await using (var db = fixture.CreateDbContext())
        {
            Assert.All(await db.CallActivityExecutions.Where(row => row.ParentInstanceId == root).ToListAsync(),
                row => Assert.Equal(CallInvocationStates.Cancelled, row.State));
            Assert.Equal(2, await db.WorkflowJobs.CountAsync(row => row.InstanceId == root && row.Kind == WorkflowJobKinds.CallCancel));
        }
        await DrainAsync(host, root);

        await using var after = fixture.CreateDbContext();
        Assert.All(await after.WorkflowInstances.Where(row => children.Contains(row.Id)).ToListAsync(),
            row => Assert.Equal(WorkflowInstanceStatuses.Cancelled, row.Status));
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal("[]", (await LatestAsync(root, "orderResults")).GetRawText());
        Assert.Equal(MultiInstanceRecordStatuses.Cancelled,
            (await after.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root)).Status);
    }

    [Fact]
    public async Task Call_bodies_publish_on_every_host_and_validate_item_sources()
    {
        await using var open = OpenHost();
        var childKey = NewKey("publish");
        await PublishAsync(open, ImmediateChild(childKey, ChildInputs));
        var model = Parent(NewKey("plain-publish"), childKey, "parallel", Orders("a"));

        // The ordinary test host, with no overrides, publishes a call body.
        using (var published = await SendAsync(null, HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true)))
        {
            Assert.True(published.StatusCode == HttpStatusCode.Created, await published.Content.ReadAsStringAsync());
        }

        // An item source must be spelled exactly and typed like the item.
        var misspelled = Parent(NewKey("misspelled"), childKey, "parallel", Orders("a"));
        misspelled.FlowNodes.Single(node => node.Id == Body).CallActivity!.InputMappings[1].SourceVariable = "mi.Index";
        using (var bad = await SendAsync(open, HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(misspelled, true)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("'mi.Index' uses a reserved context prefix.", await bad.Content.ReadAsStringAsync());
        }
        var mistyped = Parent(NewKey("mistyped"), childKey, "parallel", Orders("a"));
        mistyped.FlowNodes.Single(node => node.Id == Body).CallActivity!.InputMappings[0].DataType = WorkflowVariableTypes.String;
        using (var bad = await SendAsync(open, HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(mistyped, true)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("input source 'mi.item' is not declared as string.", await bad.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Each_item_stages_shared_inputs_when_it_first_starts_and_a_shared_output_conflict_retries_publication_only()
    {
        await using var host = OpenHost();
        var limitKey = $"tests.call.limit.{Guid.NewGuid():N}";
        var latestKey = $"tests.call.latest.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, limitKey, 1);
        await CreateSharedAsync(host, latestKey, -1);
        var childKey = NewKey("shared-child");
        await PublishAsync(host, WaitingChild(childKey, [.. ChildInputs, Input("limit")]));
        var model = Parent(NewKey("shared"), childKey, "sequential", Orders("first", "second"));
        model.Variables.Add(new() { Id = 11, Name = "limit", DataType = "number", Scope = "shared", SharedKey = limitKey, Access = "read" });
        model.Variables.Add(new() { Id = 12, Name = "latest", DataType = "number", Scope = "shared", SharedKey = latestKey, Access = "readWrite" });
        var call = model.FlowNodes.Single(node => node.Id == Body).CallActivity!;
        call.InputMappings.Add(Mapping("limit", "limit"));
        call.OutputMappings.Add(Mapping("orderIndex", "latest"));
        var parent = await PublishAsync(host, model);
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root);
        var first = Assert.Single(await ChildrenAsync(root));

        // The second item has not staged yet, so it reads the newer value; the first keeps its own.
        await SetSharedAsync(host, limitKey, 2);
        // Another writer changes the shared output after the first item fenced it.
        await SetSharedAsync(host, latestKey, 99);
        await TakeChildActionAsync(host, first);
        await DrainAsync(host, root);

        long resumeJob;
        await using (var db = fixture.CreateDbContext())
        {
            var incident = await db.WorkflowIncidents.SingleAsync(row => row.Status == "open"
                && row.Job!.Kind == WorkflowJobKinds.CallResume && row.Job.InstanceId == first);
            Assert.Equal("output_version_conflict", incident.Type);
            resumeJob = incident.JobId!.Value;
            // The candidate is saved, the child stays completed, and nothing else is admitted.
            Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == first)).Status);
            Assert.NotNull((await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == root && row.ItemIndex == 0)).CandidateJson);
        }
        Assert.Single(await ChildrenAsync(root));
        Assert.Equal(99, await SharedValueAsync(host, latestKey));

        // An authorized retry publishes the saved result over the newer value, once.
        await RetryIncidentAsync(host, resumeJob);
        await DrainAsync(host, root);
        Assert.Equal(0, await SharedValueAsync(host, latestKey));
        var both = await ChildrenAsync(root);
        Assert.Equal(2, both.Count);
        Assert.Equal("1", await StartValueAsync(first, "limit"));
        Assert.Equal("2", await StartValueAsync(both.Single(id => id != first), "limit"));

        await TakeChildActionAsync(host, both.Single(id => id != first));
        await DrainAsync(host, root);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(1, await SharedValueAsync(host, latestKey));
        // Shared writes never reach the instance or the local result envelope.
        Assert.False(await after.InstanceVariables.AnyAsync(row => row.InstanceId == root && row.VariableName == "latest"));
        Assert.All((await LatestAsync(root, "orderResults")).EnumerateArray(),
            row => Assert.False(row.GetProperty("variables").TryGetProperty("latest", out _)));
    }

    [Fact]
    public async Task An_invalid_local_result_waits_in_a_callResume_incident_and_never_starts_the_child_again()
    {
        await using var host = OpenHost();
        var childKey = NewKey("invalid-child");
        await PublishAsync(host, ImmediateChild(childKey, ChildInputs));
        var model = Parent(NewKey("invalid"), childKey, "parallel", Orders("a", "b"));
        model.FlowNodes.Single(node => node.Id == Body).MultiInstance!.OutputVariables![0].Validation = "itemIndex < 1";
        var parent = await PublishAsync(host, model);
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        var incident = await db.WorkflowIncidents.SingleAsync(row => row.Status == "open" && row.Job!.Kind == WorkflowJobKinds.CallResume
            && row.Job.CallInvocation!.ParentInstanceId == root);
        Assert.Equal(CallFailureCodes.OutputInvalid, incident.Type);
        var items = await db.MultiInstanceActivityItems.Where(row => row.InstanceId == root).OrderBy(row => row.ItemIndex).ToListAsync();
        Assert.Equal(new[] { "completed", "active" }, items.Select(item => item.Status));
        Assert.Equal(2, (await ChildrenAsync(root)).Count);
        Assert.All(await db.WorkflowInstances.Where(row => row.CallRootInstanceId == root).ToListAsync(),
            row => Assert.Equal(WorkflowInstanceStatuses.Completed, row.Status));
        // The failing item's invocation stays running with its reason, and its incident blocks admission.
        var invocation = await db.CallActivityExecutions.SingleAsync(row => row.AutomaticMultiInstanceItemId == items[1].Id);
        Assert.Equal(CallInvocationStates.Running, invocation.State);
        Assert.Equal(CallFailureCodes.OutputInvalid, invocation.FailureCode);
        await using var scope = host.Services.CreateAsyncScope();
        var progress = Assert.Single(await scope.ServiceProvider.GetRequiredService<IAutomaticMultiInstanceRepository>()
            .GetProgressAsync(root, CancellationToken.None));
        Assert.Equal("itemIncident", progress.Execution.AdmissionBlockedReason);

        // The item API finds the incident on the child-owned callResume through the item's
        // call, and names the call and the child.
        var listed = (await ItemsAsync(host, root)).Items;
        Assert.Equal(incident.Id, listed[1].IncidentId);
        Assert.Equal(incident.JobId, listed[1].JobId);
        Assert.Equal(invocation.Id, listed[1].CallInvocationId);
        Assert.Equal(invocation.ChildInstanceId, listed[1].ChildInstanceId);
        Assert.Null(listed[0].IncidentId);
        Assert.NotNull(listed[0].ChildInstanceId);
    }

    [Fact]
    public async Task An_interrupting_timer_cancels_waiting_children_and_routes_the_boundary_once()
    {
        await using var host = OpenHost();
        var childKey = NewKey("timer-child");
        await PublishAsync(host, WaitingChild(childKey, ChildInputs));
        var model = Parent(NewKey("timer"), childKey, "parallel", Orders("a", "b"));
        model.FlowNodes.Add(new() { Id = 9, Name = "Too slow", Type = BpmnFlowNodeTypes.TimerBoundaryEvent, AttachedToRef = Body,
            Timer = new TimerDefinitionModel { TimeDuration = "PT1H" } });
        model.FlowNodes.Add(new() { Id = 10, Name = "Timed out", Type = BpmnFlowNodeTypes.EndEvent });
        model.SequenceFlows.Add(new() { Id = 901, SourceRef = 9, TargetRef = 10 });
        var parent = await PublishAsync(host, model);
        var root = await StartAsync(host, parent.Id);
        // The timer starts when the body initializes, not while its entry is queued.
        await using (var db = fixture.CreateDbContext())
            Assert.False(await db.WorkflowJobs.AnyAsync(row => row.InstanceId == root && row.Kind == WorkflowJobKinds.TimerBoundary));
        await DrainAsync(host, root, skipKind: WorkflowJobKinds.TimerBoundary);
        var children = await ChildrenAsync(root);
        Assert.Equal(2, children.Count);

        await ProcessNextAsync(host, root, WorkflowJobKinds.TimerBoundary);
        await using (var db = fixture.CreateDbContext())
        {
            // Descendants are fenced at once; their physical cancellation follows by message.
            Assert.All(await db.CallActivityExecutions.Where(row => row.ParentInstanceId == root).ToListAsync(),
                row => Assert.Equal(CallInvocationStates.Cancelled, row.State));
            Assert.Equal(MultiInstanceRecordStatuses.Cancelled,
                (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root)).Status);
        }
        await DrainAsync(host, root);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Contains(10, await after.ExecutionTokens.Where(row => row.InstanceId == root).Select(row => row.NodeId).ToListAsync());
        Assert.All(await after.WorkflowInstances.Where(row => children.Contains(row.Id)).ToListAsync(),
            row => Assert.Equal(WorkflowInstanceStatuses.Cancelled, row.Status));
        // No error-style partial aggregate is published.
        Assert.Equal("[]", (await LatestAsync(root, "orderResults")).GetRawText());
    }

    [Fact]
    public async Task The_depth_limit_fails_the_body_at_entry_before_any_item()
    {
        await using var host = OpenHost(limits => limits.MaxDepth = 1);
        var leafKey = NewKey("leaf");
        await PublishAsync(host, ImmediateChild(leafKey, ChildInputs));
        // The body's workflow is itself a child at depth 1, so its children would be at depth 2.
        var middleKey = NewKey("middle");
        await PublishAsync(host, Parent(middleKey, leafKey, "parallel", Orders("a"), boundary: true));
        var rootModel = CallActivityHarness.Parent(NewKey("root"), middleKey);
        var rootDefinition = await PublishAsync(host, rootModel);
        var root = await StartAsync(host, rootDefinition.Id);
        await DrainAsync(host, root);

        await using var db = fixture.CreateDbContext();
        var middle = await db.WorkflowInstances.SingleAsync(row => row.CallRootInstanceId == root);
        Assert.Equal(WorkflowInstanceStatuses.Completed, middle.Status);
        Assert.Equal(CallFailureCodes.DepthExceeded, (await LatestAsync(middle.Id, "failure")).GetString());
        Assert.False(await db.MultiInstanceExecutions.AnyAsync(row => row.InstanceId == middle.Id));
        Assert.False(await db.CallActivityExecutions.AnyAsync(row => row.ParentInstanceId == middle.Id));
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
    }

    [Fact]
    public async Task An_input_rejected_at_staging_takes_the_catch_all_or_blocks_its_item_and_saves_nothing()
    {
        await using var host = OpenHost();
        var childKey = NewKey("note-child");
        await PublishAsync(host, ImmediateChild(childKey, [.. ChildInputs, Input("note", WorkflowVariableTypes.String)]));
        WorkflowModel WithNullNote(string key, bool boundary)
        {
            var model = Parent(key, childKey, "parallel", Orders("a"), boundary);
            // A nullable instance variable that is still null fails its required mapping.
            model.Variables.Add(new() { Id = 13, Name = "note", DataType = "string", Nullable = true });
            model.FlowNodes.Single(node => node.Id == Body).CallActivity!.InputMappings.Add(Mapping("note", "note", WorkflowVariableTypes.String));
            return model;
        }
        var caught = await PublishAsync(host, WithNullNote(NewKey("caught-input"), boundary: true));
        var uncaught = await PublishAsync(host, WithNullNote(NewKey("blocked-input"), boundary: false));

        var caughtRoot = await StartAsync(host, caught.Id);
        await DrainAsync(host, caughtRoot);
        var blockedRoot = await StartAsync(host, uncaught.Id);
        await DrainAsync(host, blockedRoot);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == caughtRoot)).Status);
        Assert.Equal(CallFailureCodes.InputInvalid, (await LatestAsync(caughtRoot, "failure")).GetString());
        var settled = await db.CallActivityExecutions.SingleAsync(row => row.ParentInstanceId == caughtRoot);
        Assert.Equal(CallInvocationStates.Failed, settled.State);
        Assert.Null(settled.ChildInstanceId);

        // Without a boundary the item waits in its callStart incident, with no stage saved.
        Assert.Empty(await ChildrenAsync(blockedRoot));
        var start = await db.WorkflowJobs.SingleAsync(row => row.InstanceId == blockedRoot && row.Kind == WorkflowJobKinds.CallStart);
        Assert.Equal(WorkflowJobStatuses.Incident, start.Status);
        Assert.Equal(CallFailureCodes.InputInvalid,
            (await db.WorkflowIncidents.SingleAsync(row => row.JobId == start.Id && row.Status == "open")).Type);
        var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == blockedRoot);
        Assert.Equal("active", item.Status);
        Assert.Null(item.StagedInputJson);
        var queued = await db.CallActivityExecutions.SingleAsync(row => row.ParentInstanceId == blockedRoot);
        Assert.Equal(CallInvocationStates.Queued, queued.State);
        Assert.Null(queued.InputValuesJson);
        Assert.Equal(CallFailureCodes.InputInvalid, queued.FailureCode);
    }

    [Fact]
    public async Task A_partial_admission_at_the_root_limit_keeps_the_dispatch_waiting_until_other_calls_free_the_root()
    {
        await using var host = OpenHost(limits => limits.MaxOpenInvocationsPerRoot = 2);
        var childKey = NewKey("partial");
        await PublishAsync(host, WaitingChild(childKey, ChildInputs));
        // An ordinary call on a parallel branch holds one of the root's two open calls.
        var parent = await PublishAsync(host, BranchedParent(NewKey("partial-parent"), childKey, Orders("a", "b")));
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root, skipKind: WorkflowJobKinds.MultiInstanceDispatch);
        var ordinaryChild = Assert.Single(await ChildrenAsync(root));
        var dispatch = (await OpenJobIdsAsync(root, WorkflowJobKinds.MultiInstanceDispatch)).Single();
        await ProcessAsync(host, dispatch);

        // The window allows two items and the root one more call: one item starts, and the
        // dispatch waits instead of completing.
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.CallActivityExecutions.CountAsync(row => row.ParentInstanceId == root
                && row.AutomaticMultiInstanceItemId != null));
            Assert.Equal(1, await db.MultiInstanceActivityItems.CountAsync(row => row.InstanceId == root && row.Status == "pending"));
            Assert.Equal("callCapacity", (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root)).AdmissionBlockedReason);
            Assert.Equal(WorkflowJobStatuses.ResultReady, (await db.WorkflowJobs.SingleAsync(row => row.Id == dispatch)).Status);
        }
        await DrainAsync(host, root);
        var firstItemChild = Assert.Single(await ChildrenAsync(root), id => id != ordinaryChild);

        // The ordinary call finishes while the first item's child still waits. The waiting
        // dispatch admits the second item; nothing of the body's own had to settle.
        await TakeChildActionAsync(host, ordinaryChild);
        await DrainAsync(host, root);
        await ProcessAsync(host, dispatch);
        await DrainAsync(host, root);
        var itemChildren = (await ChildrenAsync(root)).Where(id => id != ordinaryChild).ToList();
        Assert.Equal(2, itemChildren.Count);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Null((await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root)).AdmissionBlockedReason);
            Assert.Equal(WorkflowJobStatuses.Completed, (await db.WorkflowJobs.SingleAsync(row => row.Id == dispatch)).Status);
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == firstItemChild)).Status);
        }

        foreach (var child in itemChildren) await TakeChildActionAsync(host, child);
        await DrainAsync(host, root);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.False(await after.WorkflowIncidents.AnyAsync(row => row.InstanceId == root));
    }

    [Fact]
    public async Task A_sequential_body_waits_through_a_briefly_full_root_without_an_incident()
    {
        await using var host = OpenHost(limits => limits.MaxOpenInvocationsPerRoot = 1);
        var childKey = NewKey("brief");
        await PublishAsync(host, WaitingChild(childKey, ChildInputs));
        var parent = await PublishAsync(host, BranchedParent(NewKey("brief-parent"), childKey, Orders("a", "b"), "sequential"));
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root, skipKind: WorkflowJobKinds.MultiInstanceDispatch);
        var ordinaryChild = Assert.Single(await ChildrenAsync(root));
        var dispatch = (await OpenJobIdsAsync(root, WorkflowJobKinds.MultiInstanceDispatch)).Single();

        // Nothing of the body's own is in flight, but the backoff has not reached its longest
        // delay, so a full root only parks the dispatch.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await ProcessAsync(host, dispatch);
            await using var db = fixture.CreateDbContext();
            Assert.Equal(WorkflowJobStatuses.ResultReady, (await db.WorkflowJobs.SingleAsync(row => row.Id == dispatch)).Status);
            Assert.Equal("callCapacity", (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root)).AdmissionBlockedReason);
            Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.InstanceId == root));
        }

        await TakeChildActionAsync(host, ordinaryChild);
        await DrainAsync(host, root);
        await ProcessAsync(host, dispatch);
        // One item at a time: the second child starts only after the first result commits.
        for (var item = 0; item < 2; item++)
        {
            await DrainAsync(host, root);
            long waiting;
            await using (var db = fixture.CreateDbContext())
                waiting = await db.WorkflowInstances.Where(row => row.CallRootInstanceId == root && row.Id != ordinaryChild
                    && row.Status == WorkflowInstanceStatuses.Running).Select(row => row.Id).SingleAsync();
            await TakeChildActionAsync(host, waiting);
        }
        await DrainAsync(host, root);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(3, (await ChildrenAsync(root)).Count);
        Assert.False(await after.WorkflowIncidents.AnyAsync(row => row.InstanceId == root));
    }

    [Fact]
    public async Task A_start_failure_after_the_stage_is_saved_waits_in_its_incident_and_its_retry_reuses_the_stage()
    {
        await using var host = OpenHost();
        var limitKey = $"tests.call.stage.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, limitKey, 1);
        var childKey = NewKey("keyed");
        var child = WaitingChild(childKey, [.. ChildInputs, Input("groupKey", WorkflowVariableTypes.String), Input("limit")]);
        child.FlowNodes.Single(node => node.Id == Start).BusinessKey = new BusinessKeyModel
        {
            Variable = "groupKey",
            Uniqueness = BusinessKeyUniqueness.Active
        };
        await PublishAsync(host, child);
        // Both items map the same business key, so the second child cannot start while the
        // first is active.
        var model = Parent(NewKey("keyed-parent"), childKey, "parallel", Orders("a", "b"));
        model.Variables.Add(new() { Id = 11, Name = "group", DataType = "string", DefaultValue = Json("same") });
        model.Variables.Add(new() { Id = 12, Name = "limit", DataType = "number", Scope = "shared", SharedKey = limitKey, Access = "read" });
        var call = model.FlowNodes.Single(node => node.Id == Body).CallActivity!;
        call.InputMappings.Add(Mapping("group", "groupKey", WorkflowVariableTypes.String));
        call.InputMappings.Add(Mapping("limit", "limit"));
        var parent = await PublishAsync(host, model);
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root);
        var first = Assert.Single(await ChildrenAsync(root));

        long start;
        await using (var db = fixture.CreateDbContext())
        {
            var blocked = await db.CallActivityExecutions.SingleAsync(row => row.ParentInstanceId == root
                && row.ChildInstanceId == null);
            Assert.Equal(CallInvocationStates.Queued, blocked.State);
            Assert.Equal(CallFailureCodes.BusinessKeyConflict, blocked.FailureCode);
            Assert.NotNull(blocked.InputValuesJson);
            var incident = await db.WorkflowIncidents.SingleAsync(row => row.Status == "open"
                && row.Job!.Kind == WorkflowJobKinds.CallStart && row.Job.CallInvocationId == blocked.Id);
            Assert.Equal(CallFailureCodes.BusinessKeyConflict, incident.Type);
            start = incident.JobId!.Value;
            var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.Id == blocked.AutomaticMultiInstanceItemId);
            Assert.Equal("active", item.Status);
            Assert.NotNull(item.StagedInputJson);

            // The item API shows the callStart incident; the blocked item has a call but no child.
            var listed = Assert.Single((await ItemsAsync(host, root)).Items, row => row.Id == item.Id);
            Assert.Equal(incident.Id, listed.IncidentId);
            Assert.Equal(start, listed.JobId);
            Assert.Equal(blocked.Id, listed.CallInvocationId);
            Assert.Null(listed.ChildInstanceId);

            // Before its child exists, the call already shows the version its body pinned.
            var pinned = (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root)).PinnedChildDefinitionId!.Value;
            var pinnedVersion = (await db.WorkflowDefinitions.SingleAsync(row => row.Id == pinned)).Version;
            var listedCall = Assert.Single((await CallsAsync(host, root)).Items, row => row.InvocationId == blocked.Id);
            Assert.Null(listedCall.ChildDefinitionVersion);
            Assert.Equal(pinned, listedCall.PinnedChildDefinitionId);
            Assert.Equal(pinnedVersion, listedCall.PinnedChildDefinitionVersion);
            Assert.Equal(pinnedVersion,
                Assert.Single((await InstanceDetailAsync(host, root)).AutomaticMultiInstances).PinnedChildDefinitionVersion);
        }

        // The shared input changes after the stage. The first child finishes, which releases
        // the key, and the retry starts the second child from the saved stage.
        await SetSharedAsync(host, limitKey, 2);
        await TakeChildActionAsync(host, first);
        await DrainAsync(host, root);
        await RetryIncidentAsync(host, start);
        await DrainAsync(host, root);
        var second = Assert.Single(await ChildrenAsync(root), id => id != first);
        Assert.Equal("1", await StartValueAsync(second, "limit"));

        await TakeChildActionAsync(host, second);
        await DrainAsync(host, root);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(2, (await LatestAsync(root, "orderResults")).GetArrayLength());
    }

    [Fact]
    public async Task A_failed_entry_retries_into_its_call_incident_and_initializes_once_the_target_returns()
    {
        await using var host = OpenHost();
        var childKey = NewKey("returns");
        var child = await PublishAsync(host, ImmediateChild(childKey, ChildInputs));
        var parent = await PublishAsync(host, Parent(NewKey("returns-parent"), childKey, "parallel", Orders("a")));
        var harness = new CallActivityHarness(fixture);
        await harness.RemoveDefaultAsync(childKey);
        var root = await StartAsync(host, parent.Id);
        var entry = (await OpenJobIdsAsync(root, WorkflowJobKinds.MultiInstanceStart)).Single();

        for (var attempt = 0; ; attempt++)
        {
            Assert.True(attempt < 6, "The entry never opened its incident.");
            await ProcessAsync(host, entry);
            await using var db = fixture.CreateDbContext();
            if ((await db.WorkflowJobs.SingleAsync(row => row.Id == entry)).Status == WorkflowJobStatuses.Incident) break;
        }
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(CallFailureCodes.TargetUnavailable,
                (await db.WorkflowIncidents.SingleAsync(row => row.JobId == entry && row.Status == "open")).Type);
            Assert.False(await db.MultiInstanceExecutions.AnyAsync(row => row.InstanceId == root));
        }

        // A retry repeats initialization from the start and resolves the target again.
        await harness.RestoreDefaultAsync(child.Id);
        await RetryIncidentAsync(host, entry);
        await DrainAsync(host, root);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(child.Id, (await after.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root)).PinnedChildDefinitionId);
        Assert.Single(await ChildrenAsync(root));
    }

    [Fact]
    public async Task A_child_cancelled_on_its_own_takes_the_catch_all_or_faults_the_parent()
    {
        await using var host = OpenHost();
        var childKey = NewKey("cancelled-child");
        await PublishAsync(host, WaitingChild(childKey, ChildInputs));
        var caught = await PublishAsync(host, Parent(NewKey("cancelled-caught"), childKey, "parallel", Orders("a", "b"), boundary: true));
        var uncaught = await PublishAsync(host, Parent(NewKey("cancelled-uncaught"), childKey, "parallel", Orders("a", "b")));

        var caughtRoot = await StartAsync(host, caught.Id);
        var uncaughtRoot = await StartAsync(host, uncaught.Id);
        foreach (var root in new[] { caughtRoot, uncaughtRoot })
        {
            await DrainAsync(host, root);
            var children = await ChildrenAsync(root);
            Assert.Equal(2, children.Count);
            using (var cancelled = await SendAsync(host, HttpMethod.Post, $"/api/instances/{children[0]}/cancel", new { }))
                Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
            await DrainAsync(host, root);
        }

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == caughtRoot)).Status);
        Assert.Equal(CallFailureCodes.ChildCancelled, (await LatestAsync(caughtRoot, "failure")).GetString());
        var partial = await LatestAsync(caughtRoot, "orderResults");
        Assert.Equal(2, partial.GetArrayLength());
        Assert.Contains(partial.EnumerateArray(), row => row.GetProperty("status").GetString() == "failed");
        Assert.Contains(partial.EnumerateArray(), row => row.GetProperty("status").GetString() == "cancelled");
        Assert.Equal(WorkflowInstanceStatuses.Faulted, (await db.WorkflowInstances.SingleAsync(row => row.Id == uncaughtRoot)).Status);
        Assert.Equal(CallFailureCodes.ChildCancelled,
            (await db.ExecutionTokens.SingleAsync(row => row.InstanceId == uncaughtRoot && row.Status == "faulted")).FaultCode);
        // The sibling's child is cancelled by message; neither body starts a replacement.
        foreach (var root in new[] { caughtRoot, uncaughtRoot })
        {
            var children = await ChildrenAsync(root);
            Assert.Equal(2, children.Count);
            Assert.All(await db.WorkflowInstances.Where(row => children.Contains(row.Id)).ToListAsync(),
                row => Assert.Equal(WorkflowInstanceStatuses.Cancelled, row.Status));
        }
    }

    [Fact]
    public async Task A_second_token_at_the_node_waits_until_the_open_body_settles()
    {
        await using var host = OpenHost();
        var childKey = NewKey("second-token");
        await PublishAsync(host, WaitingChild(childKey, ChildInputs));
        // Two parallel branches each reach the call body.
        var model = Parent(NewKey("second-token-parent"), childKey, "parallel", Orders("a"));
        model.SequenceFlows.Clear();
        model.FlowNodes.Add(new() { Id = 6, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway });
        model.FlowNodes.Add(new() { Id = 7, Name = "First branch", Type = BpmnFlowNodeTypes.Task });
        model.FlowNodes.Add(new() { Id = 8, Name = "Second branch", Type = BpmnFlowNodeTypes.Task });
        model.SequenceFlows.AddRange(
        [
            new() { Id = 101, SourceRef = Start, TargetRef = 6 },
            new() { Id = 601, SourceRef = 6, TargetRef = 7 },
            new() { Id = 602, SourceRef = 6, TargetRef = 8 },
            new() { Id = 701, SourceRef = 7, TargetRef = Body },
            new() { Id = 801, SourceRef = 8, TargetRef = Body },
            new() { Id = 201, SourceRef = Body, TargetRef = End }
        ]);
        var parent = await PublishAsync(host, model);
        var root = await StartAsync(host, parent.Id);
        var entries = await OpenJobIdsAsync(root, WorkflowJobKinds.MultiInstanceStart);
        Assert.Equal(2, entries.Count);
        await DrainAsync(host, root);

        // One body runs; the other token's entry waits for the node's slot.
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.MultiInstanceExecutions.CountAsync(row => row.InstanceId == root));
            Assert.Single(await db.WorkflowJobs.Where(row => entries.Contains(row.Id)
                && row.Status == WorkflowJobStatuses.ResultReady).ToListAsync());
        }
        var first = Assert.Single(await ChildrenAsync(root));
        await TakeChildActionAsync(host, first);
        await DrainAsync(host, root);

        var waiting = (await OpenJobIdsAsync(root, WorkflowJobKinds.MultiInstanceStart)).Single();
        await ProcessAsync(host, waiting);
        await DrainAsync(host, root);
        var second = Assert.Single(await ChildrenAsync(root), id => id != first);
        await TakeChildActionAsync(host, second);
        await DrainAsync(host, root);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(2, await after.MultiInstanceExecutions.CountAsync(row => row.InstanceId == root
            && row.Status == MultiInstanceRecordStatuses.Completed));
    }

    [Fact]
    public async Task Local_output_defaults_read_the_system_context_their_item_staged()
    {
        await using var host = OpenHost();
        var childKey = NewKey("context-child");
        await PublishAsync(host, ImmediateChild(childKey, ChildInputs));
        var model = Parent(NewKey("context"), childKey, "parallel", Orders("a"));
        var multi = model.FlowNodes.Single(node => node.Id == Body).MultiInstance!;
        multi.OutputVariables = [.. multi.OutputVariables!, new MultiInstanceOutputVariableModel
        {
            Name = "origin", DataType = WorkflowVariableTypes.String, IsArray = false, Nullable = false,
            DefaultValue = Json("${sys.instanceId}/${sys.nodeId}/${sys.user}"),
            Validation = "Len(origin) > 4"
        }];
        var parent = await PublishAsync(host, model);
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        var row = Assert.Single((await LatestAsync(root, "orderResults")).EnumerateArray());
        // A call item acts as the fixed callActivity actor.
        Assert.Equal($"{root}/{Body}/callActivity", row.GetProperty("variables").GetProperty("origin").GetString());
    }

    [Fact]
    public async Task Non_interrupting_timer_and_condition_run_their_handlers_beside_a_body_that_still_publishes()
    {
        await using var host = OpenHost();
        var childKey = NewKey("reminded-child");
        await PublishAsync(host, WaitingChild(childKey, ChildInputs));
        var model = Parent(NewKey("reminded"), childKey, "parallel", Orders("a", "b"));
        model.Variables.Add(new() { Id = 20, Name = "flag", DataType = "boolean", DefaultValue = Json(false) });
        // A reminder whose handler waits for a person, so it outlives the body, and a
        // condition whose handler ends at once.
        model.FlowNodes.Add(new()
        {
            Id = 9, Name = "Reminder", Type = BpmnFlowNodeTypes.TimerBoundaryEvent, AttachedToRef = Body,
            CancelActivity = false, Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
        });
        model.FlowNodes.Add(new() { Id = 10, Name = "Follow up", Type = BpmnFlowNodeTypes.UserTask });
        model.FlowNodes.Add(new() { Id = 11, Name = "Followed up", Type = BpmnFlowNodeTypes.EndEvent });
        model.FlowNodes.Add(new()
        {
            Id = 12, Name = "Flagged", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = Body,
            CancelActivity = false, Conditional = new ConditionalDefinitionModel { Condition = "flag == true" }
        });
        model.FlowNodes.Add(new() { Id = 13, Name = "Noted", Type = BpmnFlowNodeTypes.EndEvent });
        model.SequenceFlows.Add(new() { Id = 901, SourceRef = 9, TargetRef = 10 });
        model.SequenceFlows.Add(new() { Id = 1001, Name = "Done", SourceRef = 10, TargetRef = 11 });
        model.SequenceFlows.Add(new() { Id = 1201, SourceRef = 12, TargetRef = 13 });
        var parent = await PublishAsync(host, model);
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root, skipKind: WorkflowJobKinds.TimerBoundary);
        var children = await ChildrenAsync(root);
        Assert.Equal(2, children.Count);

        // The reminder fires while both children wait, and the condition becomes true
        // through the engine's own variable update.
        await ProcessNextAsync(host, root, WorkflowJobKinds.TimerBoundary);
        await PatchVariableAsync(host, root, "flag", true);
        await using (var db = fixture.CreateDbContext())
        {
            var tokens = await db.ExecutionTokens.Where(row => row.InstanceId == root).ToListAsync();
            Assert.Equal("active", Assert.Single(tokens, row => row.NodeId == 10).Status);
            Assert.Equal("completed", Assert.Single(tokens, row => row.NodeId == 13).Status);
            Assert.Equal("active", Assert.Single(tokens, row => row.NodeId == Body).Status);
            Assert.Equal(MultiInstanceRecordStatuses.Active,
                (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root)).Status);
            Assert.All(await db.CallActivityExecutions.Where(row => row.ParentInstanceId == root).ToListAsync(),
                row => Assert.Equal(CallInvocationStates.Running, row.State));
            Assert.All(await db.WorkflowInstances.Where(row => children.Contains(row.Id)).ToListAsync(),
                row => Assert.Equal(WorkflowInstanceStatuses.Running, row.Status));
        }

        // The body still finishes normally and publishes every item.
        foreach (var child in children) await TakeChildActionAsync(host, child);
        await DrainAsync(host, root);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
            Assert.Equal(MultiInstanceRecordStatuses.Completed,
                (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root)).Status);
            Assert.Equal("completed", (await db.ExecutionTokens.SingleAsync(row => row.InstanceId == root && row.NodeId == End)).Status);
            // Normal completion closes the host's subscriptions but keeps the handler it spawned.
            Assert.Equal("active", (await db.ExecutionTokens.SingleAsync(row => row.InstanceId == root && row.NodeId == 10)).Status);
            Assert.False(await db.ConditionalBoundarySubscriptions.AnyAsync(row => row.InstanceId == root
                && row.Status == ConditionalBoundarySubscriptionStatuses.Active));
            Assert.False(await db.TimerSubscriptions.AnyAsync(row => row.InstanceId == root
                && row.Status == TimerSubscriptionStatuses.Active));
        }
        var results = await LatestAsync(root, "orderResults");
        Assert.Equal(new[] { "a", "b" }, results.EnumerateArray().Select(row => row.GetProperty("item").GetProperty("id").GetString()));
        Assert.All(results.EnumerateArray(), row => Assert.Equal("completed", row.GetProperty("status").GetString()));

        await CompleteRootTaskAsync(host, root, 10, 1001);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.All(await after.WorkflowInstances.Where(row => children.Contains(row.Id)).ToListAsync(),
            row => Assert.Equal(WorkflowInstanceStatuses.Completed, row.Status));
    }

    [Fact]
    public async Task An_interrupting_condition_cancels_a_queued_entry_before_any_body_exists()
    {
        await using var host = OpenHost();
        var childKey = NewKey("never-child");
        await PublishAsync(host, ImmediateChild(childKey, ChildInputs));
        var model = Parent(NewKey("stopped"), childKey, "parallel", Orders("a", "b"));
        model.Variables.Add(new() { Id = 20, Name = "stop", DataType = "boolean", DefaultValue = Json(false) });
        // cancelActivity is omitted, so the boundary interrupts.
        model.FlowNodes.Add(new()
        {
            Id = 12, Name = "Stopped", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = Body,
            Conditional = new ConditionalDefinitionModel { Condition = "stop == true" }
        });
        model.FlowNodes.Add(new() { Id = 13, Name = "Stopped early", Type = BpmnFlowNodeTypes.EndEvent });
        model.SequenceFlows.Add(new() { Id = 1201, SourceRef = 12, TargetRef = 13 });
        var parent = await PublishAsync(host, model);
        var root = await StartAsync(host, parent.Id);
        var entry = (await OpenJobIdsAsync(root, WorkflowJobKinds.MultiInstanceStart)).Single();

        // The condition subscribes when the token enters, before its entry job runs.
        await PatchVariableAsync(host, root, "stop", true);
        await DrainAsync(host, root);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal("completed", (await db.ExecutionTokens.SingleAsync(row => row.InstanceId == root && row.NodeId == 13)).Status);
        Assert.False(await db.ExecutionTokens.AnyAsync(row => row.InstanceId == root && row.NodeId == End));
        Assert.Equal(WorkflowJobStatuses.Cancelled, (await db.WorkflowJobs.SingleAsync(row => row.Id == entry)).Status);
        // No body, item, invocation, child, or aggregate write ever existed.
        Assert.False(await db.MultiInstanceExecutions.AnyAsync(row => row.InstanceId == root));
        Assert.False(await db.CallActivityExecutions.AnyAsync(row => row.ParentInstanceId == root));
        Assert.Empty(await ChildrenAsync(root));
        Assert.Equal(1, await db.InstanceVariables.CountAsync(row => row.InstanceId == root && row.VariableName == "orderResults"));
    }

    [Fact]
    public async Task A_stale_callStart_lease_that_already_staged_its_item_cannot_start_a_second_child()
    {
        var gate = new DiscardGate();
        await using var first = OpenGatedHost(gate);
        await using var second = OpenHost();
        var childKey = NewKey("dup-start-child");
        await PublishAsync(second, WaitingChild(childKey, ChildInputs));
        var parent = await PublishAsync(second, Parent(NewKey("dup-start"), childKey, "parallel", Orders("a", "b")));
        var root = await StartAsync(second, parent.Id);
        await ProcessNextAsync(second, root, WorkflowJobKinds.MultiInstanceStart);
        await ProcessNextAsync(second, root, WorkflowJobKinds.MultiInstanceDispatch);
        var start = (await OpenJobIdsAsync(root, WorkflowJobKinds.CallStart)).First();

        // The first Worker stages the item, commits, and stops before its start transaction.
        var stale = await LeaseExactAsync(second, start);
        var held = await ProcessUntilGateAsync(first, gate, stale);
        string staged;
        await using (var db = fixture.CreateDbContext())
        {
            var invocation = await db.CallActivityExecutions.SingleAsync(row => db.WorkflowJobs
                .Any(job => job.Id == start && job.CallInvocationId == row.Id));
            Assert.NotNull(invocation.InputValuesJson);
            Assert.Null(invocation.ChildInstanceId);
            staged = (await db.MultiInstanceActivityItems.SingleAsync(row => row.Id == invocation.AutomaticMultiInstanceItemId))
                .StagedInputJson!.RootElement.GetRawText();
        }

        // Its lease expires, and a second Worker takes the job over and starts the child.
        var current = await LeaseExactAsync(second, start);
        Assert.True(current.LeaseGeneration > stale.LeaseGeneration);
        await ProcessLeaseAsync(second, current);
        // The first Worker resumes with its stale fence, and later replays it once more.
        gate.Release();
        await held.WaitAsync(RaceTimeout);
        await ProcessLeaseAsync(first, stale);

        await using (var db = fixture.CreateDbContext())
        {
            var invocation = await db.CallActivityExecutions.SingleAsync(row => db.WorkflowJobs
                .Any(job => job.Id == start && job.CallInvocationId == row.Id));
            Assert.Equal(CallInvocationStates.Running, invocation.State);
            Assert.Equal(new[] { invocation.ChildInstanceId!.Value }, await ChildrenAsync(root));
            // The stage the stale Worker saved is the one the child started from.
            Assert.Equal(staged, (await db.MultiInstanceActivityItems.SingleAsync(row => row.Id == invocation.AutomaticMultiInstanceItemId))
                .StagedInputJson!.RootElement.GetRawText());
            var job = await db.WorkflowJobs.SingleAsync(row => row.Id == start);
            Assert.Equal(WorkflowJobStatuses.Completed, job.Status);
            Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.JobId == start));
        }

        // The other item starts normally: one child per item, and the body completes once.
        await DrainAsync(second, root);
        var children = await ChildrenAsync(root);
        Assert.Equal(2, children.Count);
        foreach (var child in children) await TakeChildActionAsync(second, child);
        await DrainAsync(second, root);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(2, await after.CallActivityExecutions.CountAsync(row => row.ParentInstanceId == root
            && row.State == CallInvocationStates.Completed));
        Assert.Equal(2, (await LatestAsync(root, "orderResults")).GetArrayLength());
    }

    [Fact]
    public async Task A_stale_callResume_lease_that_saved_its_candidate_cannot_deliver_or_write_the_shared_output_twice()
    {
        var gate = new DiscardGate();
        await using var first = OpenGatedHost(gate);
        await using var second = OpenHost();
        var latestKey = $"tests.call.dup.{Guid.NewGuid():N}";
        await CreateSharedAsync(second, latestKey, -1);
        var childKey = NewKey("dup-resume-child");
        await PublishAsync(second, ImmediateChild(childKey, ChildInputs));
        var model = Parent(NewKey("dup-resume"), childKey, "parallel", Orders("a"));
        model.Variables.Add(new() { Id = 12, Name = "latest", DataType = "number", Scope = "shared", SharedKey = latestKey, Access = "readWrite" });
        model.FlowNodes.Single(node => node.Id == Body).CallActivity!.OutputMappings.Add(Mapping("orderIndex", "latest"));
        var parent = await PublishAsync(second, model);
        var root = await StartAsync(second, parent.Id);
        // The child finishes as it starts; its result waits in the child-owned callResume.
        await DrainAsync(second, root, skipKind: WorkflowJobKinds.CallResume);
        var child = Assert.Single(await ChildrenAsync(root));
        var resume = (await OpenJobIdsAsync(child, WorkflowJobKinds.CallResume)).Single();
        var revisions = await SharedRevisionCountAsync(latestKey);

        // The first Worker saves the item's candidate, commits, and stops before publishing.
        var stale = await LeaseExactAsync(second, resume);
        var held = await ProcessUntilGateAsync(first, gate, stale);
        await using (var db = fixture.CreateDbContext())
        {
            var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == root);
            Assert.NotNull(item.CandidateJson);
            Assert.Equal("active", item.Status);
            Assert.Equal(CallInvocationStates.Running, (await db.CallActivityExecutions.SingleAsync(row => row.ParentInstanceId == root)).State);
        }
        Assert.Equal(-1, await SharedValueAsync(second, latestKey));

        // A second Worker takes the expired lease over and publishes the saved candidate.
        var current = await LeaseExactAsync(second, resume);
        Assert.True(current.LeaseGeneration > stale.LeaseGeneration);
        await ProcessLeaseAsync(second, current);
        // The first Worker resumes with its stale fence, then replays it once more.
        gate.Release();
        await held.WaitAsync(RaceTimeout);
        await ProcessLeaseAsync(first, stale);
        await DrainAsync(second, root);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        var invocation = await after.CallActivityExecutions.SingleAsync(row => row.ParentInstanceId == root);
        Assert.Equal(CallInvocationStates.Completed, invocation.State);
        Assert.Equal(WorkflowJobStatuses.Completed, (await after.WorkflowJobs.SingleAsync(row => row.Id == resume)).Status);
        Assert.False(await after.WorkflowIncidents.AnyAsync(row => row.JobId == resume));
        // One delivery: one call-completed record, one shared write, one aggregate row.
        Assert.Equal(1, await after.InstanceHistory.CountAsync(row => row.InstanceId == root
            && row.Note == InstanceHistoryNotes.CallCompleted));
        Assert.Equal(revisions + 1, await SharedRevisionCountAsync(latestKey));
        Assert.Equal(0, await SharedValueAsync(second, latestKey));
        Assert.Equal(1, (await LatestAsync(root, "orderResults")).GetArrayLength());
        Assert.Single(await ChildrenAsync(root));
    }

    [Fact]
    public async Task Simultaneous_last_results_on_two_workers_publish_one_aggregate_and_continue_once()
    {
        var oneGate = new DiscardGate();
        var twoGate = new DiscardGate();
        await using var one = OpenGatedHost(oneGate);
        await using var two = OpenGatedHost(twoGate);
        await using var host = OpenHost();
        var childKey = NewKey("last-child");
        await PublishAsync(host, ImmediateChild(childKey, ChildInputs));
        var parent = await PublishAsync(host, Parent(NewKey("last"), childKey, "parallel", Orders("a", "b")));
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root, skipKind: WorkflowJobKinds.CallResume);
        var children = await ChildrenAsync(root);
        Assert.Equal(2, children.Count);
        var resumes = new List<long>();
        foreach (var child in children) resumes.Add((await OpenJobIdsAsync(child, WorkflowJobKinds.CallResume)).Single());

        // Each Worker saves its item's candidate and stops before publishing, so both items
        // are the body's last result at once.
        var heldOne = await ProcessUntilGateAsync(one, oneGate, await LeaseExactAsync(host, resumes[0]));
        var heldTwo = await ProcessUntilGateAsync(two, twoGate, await LeaseExactAsync(host, resumes[1]));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.All(await db.MultiInstanceActivityItems.Where(row => row.InstanceId == root).ToListAsync(), item =>
            {
                Assert.Equal("active", item.Status);
                Assert.NotNull(item.CandidateJson);
            });
            Assert.False(await db.WorkflowJobs.AnyAsync(row => row.InstanceId == root && row.Kind == WorkflowJobKinds.MultiInstanceComplete));
        }

        // Both publications are released behind a held parent row, so both wait on it together.
        await using (var holder = await HoldInstanceRowAsync(root))
        {
            oneGate.Release();
            twoGate.Release();
            await WaitForBlockedAsync(holder.Pid, 2);
        }
        await Task.WhenAll(heldOne, heldTwo).WaitAsync(RaceTimeout);

        await using (var db = fixture.CreateDbContext())
        {
            Assert.All(await db.MultiInstanceActivityItems.Where(row => row.InstanceId == root).ToListAsync(),
                item => Assert.Equal("completed", item.Status));
            Assert.Equal(1, await db.WorkflowJobs.CountAsync(row => row.InstanceId == root && row.Kind == WorkflowJobKinds.MultiInstanceComplete));
            Assert.All(await db.WorkflowJobs.Where(row => resumes.Contains(row.Id)).ToListAsync(),
                job => Assert.Equal(WorkflowJobStatuses.Completed, job.Status));
        }
        await DrainAsync(host, root);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(2, await after.InstanceHistory.CountAsync(row => row.InstanceId == root
            && row.Note == InstanceHistoryNotes.CallCompleted));
        // One aggregate, published once, and one traversal out of the body.
        var aggregates = (await after.InstanceVariables.Where(row => row.InstanceId == root && row.VariableName == "orderResults")
            .ToListAsync()).Count(row => row.ValueJson.RootElement.GetArrayLength() > 0);
        Assert.Equal(1, aggregates);
        Assert.Equal(new[] { "a", "b" },
            (await LatestAsync(root, "orderResults")).EnumerateArray().Select(row => row.GetProperty("item").GetProperty("id").GetString()));
        Assert.Equal(1, await after.NodeExecutions.CountAsync(row => row.InstanceId == root && row.NodeId == End));
        Assert.False(await after.WorkflowIncidents.AnyAsync(row => row.InstanceId == root || children.Contains(row.InstanceId!.Value)));
    }

    [Fact]
    public async Task An_output_failure_found_before_a_siblings_failure_cancelled_its_item_opens_no_incident()
    {
        var gate = new DiscardGate();
        await using var first = OpenGatedHost(gate);
        await using var host = OpenHost();
        var childKey = NewKey("split-child");
        await PublishAsync(host, SplitChild(childKey));
        // Item 0's child completes but its result fails the local validation; item 1's child
        // faults. Neither failure has a boundary.
        var model = Parent(NewKey("fenced"), childKey, "parallel", Orders("a", "b"));
        model.FlowNodes.Single(node => node.Id == Body).MultiInstance!.OutputVariables![0].Validation = "itemIndex > 5";
        var parent = await PublishAsync(host, model);
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root, skipKind: WorkflowJobKinds.CallResume);
        var children = await ChildrenAsync(root);
        Assert.Equal(2, children.Count);
        long invalidResume, faultedResume, invalidInvocation, invalidChild;
        await using (var db = fixture.CreateDbContext())
        {
            var invocations = await db.CallActivityExecutions.Where(row => row.ParentInstanceId == root)
                .Join(db.MultiInstanceActivityItems, call => call.AutomaticMultiInstanceItemId, item => item.Id,
                    (call, item) => new { call.Id, call.ChildInstanceId, item.ItemIndex })
                .OrderBy(row => row.ItemIndex).ToListAsync();
            invalidInvocation = invocations[0].Id;
            invalidChild = invocations[0].ChildInstanceId!.Value;
            invalidResume = (await OpenJobIdsAsync(invocations[0].ChildInstanceId!.Value, WorkflowJobKinds.CallResume)).Single();
            faultedResume = (await OpenJobIdsAsync(invocations[1].ChildInstanceId!.Value, WorkflowJobKinds.CallResume)).Single();
            Assert.Equal(WorkflowInstanceStatuses.Completed,
                (await db.WorkflowInstances.SingleAsync(row => row.Id == invocations[0].ChildInstanceId)).Status);
            Assert.Equal(WorkflowInstanceStatuses.Faulted,
                (await db.WorkflowInstances.SingleAsync(row => row.Id == invocations[1].ChildInstanceId)).Status);
        }

        // Item 0's delivery finds the invalid result in its first transaction and stops before
        // the transaction that would open its incident.
        var held = await ProcessUntilGateAsync(first, gate, await LeaseExactAsync(host, invalidResume));
        // Meanwhile the sibling's fault stops the body, which cancels item 0 and its call.
        await ProcessAsync(host, faultedResume);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal("cancelled", (await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == root && row.ItemIndex == 0)).Status);
            Assert.Equal(CallInvocationStates.Cancelled, (await db.CallActivityExecutions.SingleAsync(row => row.Id == invalidInvocation)).State);
        }
        gate.Release();
        await held.WaitAsync(RaceTimeout);

        // Item 0's incident transaction checks the item's fence again and finds it cancelled:
        // its delivery ends quietly instead of opening an incident on a cancelled item.
        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.JobId == invalidResume));
            Assert.Equal(WorkflowJobStatuses.Completed, (await db.WorkflowJobs.SingleAsync(row => row.Id == invalidResume)).Status);
            var invocation = await db.CallActivityExecutions.SingleAsync(row => row.Id == invalidInvocation);
            Assert.Equal(CallInvocationStates.Cancelled, invocation.State);
            Assert.Null(invocation.FailureCode);
        }
        await DrainAsync(host, root);

        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Faulted, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(CallFailureCodes.ChildFaulted,
            (await after.ExecutionTokens.SingleAsync(row => row.InstanceId == root && row.Status == "faulted")).FaultCode);
        Assert.False(await after.WorkflowIncidents.AnyAsync(row => row.InstanceId == root || children.Contains(row.InstanceId!.Value)));
        // The completed child stays completed.
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == invalidChild)).Status);
    }

    [Fact]
    public async Task A_child_waiting_in_its_own_incident_keeps_its_slot_while_the_root_limit_holds_the_next_item()
    {
        await using var host = OpenHost(limits => limits.MaxOpenInvocationsPerRoot = 1);
        var childKey = NewKey("remote-child");
        await PublishAsync(host, RemoteChild(childKey));
        var parent = await PublishAsync(host, Parent(NewKey("slot"), childKey, "parallel", Orders("a", "b"), maxConcurrency: 2));
        var root = await StartAsync(host, parent.Id);
        // Item 0's child gets a 503 from its one REST attempt and waits in its own incident.
        await DrainAsync(host, root);
        var child = Assert.Single(await ChildrenAsync(root));
        long childJob, dispatch;
        await using (var db = fixture.CreateDbContext())
        {
            var incident = await db.WorkflowIncidents.SingleAsync(row => row.InstanceId == child && row.Status == "open");
            childJob = incident.JobId!.Value;
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == child)).Status);
            dispatch = (await OpenJobIdsAsync(root, WorkflowJobKinds.MultiInstanceDispatch)).Single();
        }

        // The child's incident is not the body's: its item keeps its slot and its reserved
        // call, the root's only open call, and the next item stays pending. Even past the
        // longest backoff, the dispatch only waits, because an item is in flight.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await ProcessAsync(host, dispatch);
            await using var db = fixture.CreateDbContext();
            Assert.Equal(WorkflowJobStatuses.ResultReady, (await db.WorkflowJobs.SingleAsync(row => row.Id == dispatch)).Status);
            Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.InstanceId == root));
            var items = await db.MultiInstanceActivityItems.Where(row => row.InstanceId == root).OrderBy(row => row.ItemIndex).ToListAsync();
            Assert.Equal(new[] { "active", "pending" }, items.Select(item => item.Status));
            var invocation = await db.CallActivityExecutions.SingleAsync(row => row.ParentInstanceId == root);
            Assert.Equal(CallInvocationStates.Running, invocation.State);
            Assert.True(invocation.CapacityReserved);
            Assert.Equal(child, invocation.ChildInstanceId);
        }
        Assert.True((await LeaseGenerationAsync(dispatch)) >= 5);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var progress = Assert.Single(await scope.ServiceProvider.GetRequiredService<IAutomaticMultiInstanceRepository>()
                .GetProgressAsync(root, CancellationToken.None));
            Assert.Equal("callCapacity", progress.Execution.AdmissionBlockedReason);
            Assert.Equal(1, progress.ActiveCount);
        }

        // An operator retries the child's own incident; the item settles, which
        // frees the root for the next item.
        await RetryIncidentAsync(host, childJob);
        await DrainAsync(host, root);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(2, (await ChildrenAsync(root)).Count);
        Assert.Equal(new[] { 0, 1 }, (await LatestAsync(root, "orderResults")).EnumerateArray()
            .Select(row => row.GetProperty("variables").GetProperty("itemIndex").GetInt32()));
        Assert.False(await after.WorkflowIncidents.AnyAsync(row => row.InstanceId == root));
    }

    [Fact]
    public async Task An_archived_shared_input_at_staging_opens_an_operational_callStart_incident_not_the_boundary()
    {
        await using var host = OpenHost();
        var limitKey = $"tests.call.archived.{Guid.NewGuid():N}";
        await CreateSharedAsync(host, limitKey, 1);
        var childKey = NewKey("archived-child");
        await PublishAsync(host, ImmediateChild(childKey, [.. ChildInputs, Input("limit")]));
        // A review comes before the body, so a finished instance can be reactivated there.
        var parentKey = NewKey("archived");
        var model = Parent(parentKey, childKey, "parallel", Orders("a"), boundary: true);
        model.Variables.Add(new() { Id = 11, Name = "limit", DataType = "number", Scope = "shared", SharedKey = limitKey, Access = "read" });
        model.FlowNodes.Single(node => node.Id == Body).CallActivity!.InputMappings.Add(Mapping("limit", "limit"));
        model.FlowNodes.Add(new() { Id = 6, Name = "Review", Type = BpmnFlowNodeTypes.UserTask });
        model.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 6;
        model.SequenceFlows.Add(new() { Id = 601, Name = "Go", SourceRef = 6, TargetRef = Body });
        var parent = await PublishAsync(host, model);
        var root = await StartAsync(host, parent.Id);
        await CompleteRootTaskAsync(host, root, 6, 601);
        await DrainAsync(host, root);
        await using (var db = fixture.CreateDbContext())
            Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);

        // Archiving is refused while a running instance or a published definition binds the
        // key, so the key is retired the supported way: a new default without the binding,
        // the old version unpublished, then the archive.
        await PublishNewDefaultAsync(host, parent.Id, new WorkflowModel
        {
            Id = parentKey, Name = parentKey, InitialEventId = Start,
            FlowNodes =
            [
                new() { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new() { Id = End, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new() { Id = 101, SourceRef = Start, TargetRef = End }]
        });
        using (var unpublished = await SendAsync(host, HttpMethod.Post, $"/api/workflows/{parent.Id}/unpublish"))
            Assert.True(unpublished.IsSuccessStatusCode, await unpublished.Content.ReadAsStringAsync());
        await SharedLifecycleAsync(host, limitKey, "archive");

        // Reactivating the finished instance at its review runs the body again on the
        // version that binds the archived key.
        InstanceReactivationPreviewDto preview;
        using (var previewed = await SendAsync(host, HttpMethod.Get, $"/api/instances/{root}/reactivation"))
        {
            Assert.True(previewed.IsSuccessStatusCode, await previewed.Content.ReadAsStringAsync());
            preview = (await previewed.Content.ReadFromJsonAsync<InstanceReactivationPreviewDto>())!;
        }
        using (var reactivated = await SendAsync(host, HttpMethod.Post, $"/api/instances/{root}/reactivation",
                   new ReactivateInstanceRequest(6, preview.WorkflowId, preview.ExpectedUpdatedAt, "Review again")))
            Assert.True(reactivated.IsSuccessStatusCode, await reactivated.Content.ReadAsStringAsync());
        await CompleteRootTaskAsync(host, root, 6, 601);
        await DrainAsync(host, root);

        long start;
        await using (var db = fixture.CreateDbContext())
        {
            var body = await db.MultiInstanceExecutions.Where(row => row.InstanceId == root && row.Status == MultiInstanceRecordStatuses.Active).SingleAsync();
            var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.MultiInstanceExecutionId == body.Id);
            var invocation = await db.CallActivityExecutions.SingleAsync(row => row.AutomaticMultiInstanceItemId == item.Id);
            var job = await db.WorkflowJobs.SingleAsync(row => row.CallInvocationId == invocation.Id && row.Kind == WorkflowJobKinds.CallStart);
            start = job.Id;
            // An operational incident on the item's callStart, never the business boundary.
            Assert.Equal(WorkflowJobStatuses.Incident, job.Status);
            var incident = await db.WorkflowIncidents.SingleAsync(row => row.JobId == start && row.Status == "open");
            Assert.Equal(WorkflowIncidentTypes.JobConflict, incident.Type);
            Assert.Contains("shared binding is unavailable", incident.Details);
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
            Assert.Equal(JsonValueKind.Null, (await LatestAsync(root, "failure")).ValueKind);
            Assert.False(await db.ExecutionTokens.AnyAsync(row => row.InstanceId == root && row.NodeId == Boundary));
            // The failed stage saved nothing, so the item stays active with its queued call.
            Assert.Equal("active", item.Status);
            Assert.Null(item.StagedInputJson);
            Assert.Equal(CallInvocationStates.Queued, invocation.State);
            Assert.Null(invocation.InputValuesJson);
            Assert.Null(invocation.ChildInstanceId);
        }
        Assert.Single(await ChildrenAsync(root));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var progress = (await scope.ServiceProvider.GetRequiredService<IAutomaticMultiInstanceRepository>()
                .GetProgressAsync(root, CancellationToken.None)).Single(row => row.Execution.Status == MultiInstanceRecordStatuses.Active);
            Assert.Equal("itemIncident", progress.Execution.AdmissionBlockedReason);
        }

        // Once the key is back, a retry stages the value current at that first success.
        await SharedLifecycleAsync(host, limitKey, "reactivate");
        await SetSharedAsync(host, limitKey, 7);
        await RetryIncidentAsync(host, start);
        await DrainAsync(host, root);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        var children = await ChildrenAsync(root);
        Assert.Equal(2, children.Count);
        Assert.Equal("7", await StartValueAsync(children[1], "limit"));
        Assert.Equal(JsonValueKind.Null, (await LatestAsync(root, "failure")).ValueKind);
    }

    [Fact]
    public async Task A_shared_typed_collection_is_frozen_at_initialization_for_every_item_and_whole_collection_mapping()
    {
        await using var host = OpenHost();
        var ordersKey = $"tests.call.orders.{Guid.NewGuid():N}";
        await CreateSharedJsonAsync(host, ordersKey, Json(Orders("a", "b")));
        var childKey = NewKey("shared-collection-child");
        await PublishAsync(host, WaitingChild(childKey, [.. ChildInputs, Input("all", WorkflowVariableTypes.Json, isArray: true)]));
        var model = Parent(NewKey("shared-collection"), childKey, "sequential", []);
        model.Variables[0] = new() { Id = 1, Name = "orders", DataType = "json", IsArray = true, Scope = "shared", SharedKey = ordersKey, Access = "read" };
        // A mapping that names the collection passes the whole frozen collection.
        model.FlowNodes.Single(node => node.Id == Body).CallActivity!.InputMappings
            .Add(Mapping("orders", "all", WorkflowVariableTypes.Json, isArray: true));
        var parent = await PublishAsync(host, model);
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root);
        var first = Assert.Single(await ChildrenAsync(root));

        // The catalog value changes after initialization; the body keeps its frozen copy.
        await SetSharedJsonAsync(host, ordersKey, Json(Orders("x", "y", "z")));
        await TakeChildActionAsync(host, first);
        await DrainAsync(host, root);
        var second = Assert.Single(await ChildrenAsync(root), id => id != first);
        await TakeChildActionAsync(host, second);
        await DrainAsync(host, root);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(2, (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root)).TotalCount);
        Assert.Equal("""{"id":"b"}""", (await LatestAsync(second, "order")).GetRawText().Replace(" ", ""));
        foreach (var child in new[] { first, second })
        {
            Assert.Equal(new[] { "a", "b" },
                (await LatestAsync(child, "all")).EnumerateArray().Select(row => row.GetProperty("id").GetString()));
        }
        var results = await LatestAsync(root, "orderResults");
        Assert.Equal(new[] { "a", "b" }, results.EnumerateArray().Select(row => row.GetProperty("item").GetProperty("id").GetString()));
        Assert.Equal(new[] { "a", "b" },
            results.EnumerateArray().Select(row => row.GetProperty("variables").GetProperty("echo").GetProperty("id").GetString()));
        // The shared collection never becomes an instance variable of the parent.
        Assert.False(await db.InstanceVariables.AnyAsync(row => row.InstanceId == root && row.VariableName == "orders"));
    }

    [Fact]
    public async Task A_pinned_version_a_newer_keyed_version_makes_unstartable_takes_the_catch_all_or_waits_in_its_incident()
    {
        await using var host = OpenHost();
        var childKey = NewKey("keyed-later");
        // Version 1 has no business key; its group input is optional.
        var v1 = await PublishAsync(host, WaitingChild(childKey, [.. ChildInputs, Input("groupKey", WorkflowVariableTypes.String, required: false)]));
        WorkflowModel Keyed(string key, bool boundary)
        {
            var model = Parent(key, childKey, "sequential", Orders("a", "b"), boundary);
            model.Variables.Add(new() { Id = 11, Name = "group", DataType = "string", DefaultValue = Json("g") });
            model.FlowNodes.Single(node => node.Id == Body).CallActivity!.InputMappings
                .Add(Mapping("group", "groupKey", WorkflowVariableTypes.String));
            return model;
        }
        var caught = await PublishAsync(host, Keyed(NewKey("keyed-caught"), boundary: true));
        var uncaught = await PublishAsync(host, Keyed(NewKey("keyed-uncaught"), boundary: false));
        var caughtRoot = await StartAsync(host, caught.Id);
        var uncaughtRoot = await StartAsync(host, uncaught.Id);
        var firstChildren = new Dictionary<long, long>();
        foreach (var root in new[] { caughtRoot, uncaughtRoot })
        {
            await DrainAsync(host, root);
            firstChildren[root] = Assert.Single(await ChildrenAsync(root));
        }

        // A newer default enables business keys, so the family's unkeyed version 1, which
        // both bodies pinned, can no longer start. No guard refuses that change.
        var v2Model = WaitingChild(childKey, [.. ChildInputs, Input("groupKey", WorkflowVariableTypes.String)]);
        v2Model.FlowNodes.Single(node => node.Id == Start).BusinessKey = new BusinessKeyModel
        {
            Variable = "groupKey",
            Uniqueness = BusinessKeyUniqueness.Active
        };
        await PublishNewDefaultAsync(host, v1.Id, v2Model);
        foreach (var root in new[] { caughtRoot, uncaughtRoot })
        {
            await TakeChildActionAsync(host, firstChildren[root]);
            await DrainAsync(host, root);
        }

        await using (var db = fixture.CreateDbContext())
        {
            // With the catch-all, the second item fails at its start and the body publishes
            // its partial result before the boundary.
            Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == caughtRoot)).Status);
            Assert.Equal(CallFailureCodes.StartFailed, (await LatestAsync(caughtRoot, "failure")).GetString());
            Assert.Equal(new[] { "completed", "failed" },
                (await LatestAsync(caughtRoot, "orderResults")).EnumerateArray().Select(row => row.GetProperty("status").GetString()));
            var failed = await db.CallActivityExecutions.SingleAsync(row => row.ParentInstanceId == caughtRoot
                && row.State == CallInvocationStates.Failed);
            Assert.Equal(CallFailureCodes.StartFailed, failed.FailureCode);
            Assert.Null(failed.ChildInstanceId);
            Assert.Single(await ChildrenAsync(caughtRoot));
            // Both bodies stayed pinned to version 1 throughout.
            Assert.All(await db.MultiInstanceExecutions.Where(row => row.InstanceId == caughtRoot || row.InstanceId == uncaughtRoot).ToListAsync(),
                body => Assert.Equal(v1.Id, body.PinnedChildDefinitionId));
        }

        // Without one, the item waits in its callStart incident, and no retry can succeed.
        long start;
        for (var round = 0; ; round++)
        {
            await using var db = fixture.CreateDbContext();
            var blocked = await db.CallActivityExecutions.SingleAsync(row => row.ParentInstanceId == uncaughtRoot && row.ChildInstanceId == null);
            Assert.Equal(CallInvocationStates.Queued, blocked.State);
            Assert.Equal(CallFailureCodes.StartFailed, blocked.FailureCode);
            Assert.NotNull(blocked.InputValuesJson);
            var job = await db.WorkflowJobs.SingleAsync(row => row.CallInvocationId == blocked.Id && row.Kind == WorkflowJobKinds.CallStart);
            Assert.Equal(WorkflowJobStatuses.Incident, job.Status);
            Assert.Equal(CallFailureCodes.StartFailed,
                (await db.WorkflowIncidents.SingleAsync(row => row.JobId == job.Id && row.Status == "open")).Type);
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == uncaughtRoot)).Status);
            start = job.Id;
            if (round == 1) break;
            await RetryIncidentAsync(host, start);
            await DrainAsync(host, uncaughtRoot);
        }
        Assert.Single(await ChildrenAsync(uncaughtRoot));

        // Cancelling the body is the way out: the blocked call settles without a child.
        using (var cancelled = await SendAsync(host, HttpMethod.Post, $"/api/instances/{uncaughtRoot}/cancel", new { }))
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        await DrainAsync(host, uncaughtRoot);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await after.WorkflowInstances.SingleAsync(row => row.Id == uncaughtRoot)).Status);
        Assert.Equal(CallInvocationStates.Cancelled, (await after.CallActivityExecutions.SingleAsync(row => row.ParentInstanceId == uncaughtRoot
            && row.ChildInstanceId == null)).State);
        Assert.NotEqual(WorkflowJobStatuses.Incident, (await after.WorkflowJobs.SingleAsync(row => row.Id == start)).Status);
    }

    [Fact]
    public async Task An_output_within_the_call_limit_that_the_item_cannot_hold_is_refused_and_takes_the_catch_all()
    {
        await using var host = OpenHost();
        var childKey = NewKey("big-child");
        await PublishAsync(host, WaitingChild(childKey, ChildInputs, actionVariables: [Input("big", WorkflowVariableTypes.Json)]));
        var parent = await PublishAsync(host, BigOutputParent(NewKey("big-item"), childKey, boundary: true));
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root);
        var children = await ChildrenAsync(root);
        Assert.Equal(2, children.Count);

        // 450,000 zeros are about 0.9 MB as compact JSON, inside the call's output limit, but
        // about 1.35 MB as PostgreSQL prints the jsonb, past the item's 1 MiB.
        await TakeChildActionAsync(host, children[0], new TakeFlowRequest(new() { ["big"] = Json(new int[450_000]) }));
        await DrainAsync(host, root);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(AutomaticMultiInstanceValues.ResultTooLarge, (await LatestAsync(root, "failure")).GetString());
        // The body stopped at once and published nothing.
        Assert.Equal("[]", (await LatestAsync(root, "orderResults")).GetRawText());
        var refused = await db.CallActivityExecutions.SingleAsync(row => row.ChildInstanceId == children[0]);
        Assert.Equal(CallInvocationStates.Failed, refused.State);
        Assert.Equal(AutomaticMultiInstanceValues.ResultTooLarge, refused.FailureCode);
        var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.Id == refused.AutomaticMultiInstanceItemId);
        Assert.Equal("failed", item.Status);
        Assert.Null(item.CandidateJson);
        // The finished child stays completed and gets no cancel message; its waiting sibling is cancelled.
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == children[0])).Status);
        Assert.False(await db.WorkflowJobs.AnyAsync(row => row.CallInvocationId == refused.Id && row.Kind == WorkflowJobKinds.CallCancel));
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await db.WorkflowInstances.SingleAsync(row => row.Id == children[1])).Status);
        Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.InstanceId == root));
    }

    [Fact]
    public async Task Item_results_that_fit_alone_but_not_together_refuse_the_last_and_fault_the_parent_without_a_boundary()
    {
        await using var host = OpenHost();
        var childKey = NewKey("half-child");
        await PublishAsync(host, WaitingChild(childKey, ChildInputs, actionVariables: [Input("big", WorkflowVariableTypes.Json)]));
        var parent = await PublishAsync(host, BigOutputParent(NewKey("big-body"), childKey, boundary: false));
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root);
        var children = await ChildrenAsync(root);
        Assert.Equal(2, children.Count);

        // 200,000 zeros are about 0.6 MB as PostgreSQL prints them: one item fits the body's
        // 1 MiB result, two do not.
        foreach (var child in children)
        {
            await TakeChildActionAsync(host, child, new TakeFlowRequest(new() { ["big"] = Json(new int[200_000]) }));
            await DrainAsync(host, root);
        }

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Faulted, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(AutomaticMultiInstanceValues.ResultTooLarge,
            (await db.ExecutionTokens.SingleAsync(row => row.InstanceId == root && row.Status == "faulted")).FaultCode);
        Assert.Equal("[]", (await LatestAsync(root, "orderResults")).GetRawText());
        var invocations = await db.CallActivityExecutions.Where(row => row.ParentInstanceId == root).OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(new[] { CallInvocationStates.Completed, CallInvocationStates.Failed }, invocations.Select(row => row.State));
        Assert.Equal(AutomaticMultiInstanceValues.ResultTooLarge, invocations[1].FailureCode);
        Assert.Equal(new[] { "completed", "failed" }, await db.MultiInstanceActivityItems.Where(row => row.InstanceId == root)
            .OrderBy(row => row.ItemIndex).Select(row => row.Status).ToListAsync());
        Assert.All(await db.WorkflowInstances.Where(row => children.Contains(row.Id)).ToListAsync(),
            row => Assert.Equal(WorkflowInstanceStatuses.Completed, row.Status));
        Assert.False(await db.WorkflowJobs.AnyAsync(row => row.InstanceId == root && row.Kind == WorkflowJobKinds.CallCancel));
        Assert.False(await db.WorkflowIncidents.AnyAsync(row => row.InstanceId == root));
    }

    [Fact]
    public async Task Children_whose_own_calls_the_root_limit_refuses_while_the_body_holds_it_wait_in_incidents()
    {
        await using var host = OpenHost(limits => limits.MaxOpenInvocationsPerRoot = 2);
        var grandKey = NewKey("grandchild");
        await PublishAsync(host, ImmediateChild(grandKey));
        // Each child makes an ordinary call of its own.
        var childKey = NewKey("calling-child");
        await PublishAsync(host, CallActivityHarness.Parent(childKey, grandKey, startInputs: ChildInputs));
        var parent = await PublishAsync(host, Parent(NewKey("starving"), childKey, "parallel", Orders("a", "b"), maxConcurrency: 2));
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root);

        // The body's two items hold the root's two open calls, so each child's own call is
        // refused as a failure, not held back: it waits in an incident.
        var children = await ChildrenAsync(root);
        Assert.Equal(2, children.Count);
        var childStarts = new List<long>();
        await using (var db = fixture.CreateDbContext())
        {
            foreach (var child in children)
            {
                Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == child)).Status);
                var call = await db.CallActivityExecutions.SingleAsync(row => row.ParentInstanceId == child);
                Assert.False(call.CapacityReserved);
                Assert.Equal(CallFailureCodes.CapacityExceeded, call.FailureCode);
                Assert.Null(call.ChildInstanceId);
                var start = await db.WorkflowJobs.SingleAsync(row => row.CallInvocationId == call.Id && row.Kind == WorkflowJobKinds.CallStart);
                Assert.Equal(CallFailureCodes.CapacityExceeded,
                    (await db.WorkflowIncidents.SingleAsync(row => row.JobId == start.Id && row.Status == "open")).Type);
                childStarts.Add(start.Id);
            }
            Assert.All(await db.MultiInstanceActivityItems.Where(row => row.InstanceId == root).ToListAsync(),
                item => Assert.Equal("active", item.Status));
            Assert.Equal(2, await db.CallActivityExecutions.CountAsync(row => row.ParentInstanceId == root && row.CapacityReserved
                && row.State == CallInvocationStates.Running));
        }

        // A retry cannot succeed while the body holds the root.
        await RetryIncidentAsync(host, childStarts[0]);
        await DrainAsync(host, root);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(WorkflowJobStatuses.Incident, (await db.WorkflowJobs.SingleAsync(row => row.Id == childStarts[0])).Status);
            Assert.False(await db.WorkflowInstances.AnyAsync(row => row.CallRootInstanceId == root && row.CallDepth == 2));
        }

        // The documented remedy: a root limit that fits the body's window and the calls each
        // child holds open. Retried there, the children's calls start and the body completes.
        await using var roomy = OpenHost(limits => limits.MaxOpenInvocationsPerRoot = 4);
        foreach (var start in childStarts) await RetryIncidentAsync(roomy, start);
        await DrainAsync(roomy, root);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(2, await after.WorkflowInstances.CountAsync(row => row.CallRootInstanceId == root && row.CallDepth == 2));
        Assert.Equal(2, (await LatestAsync(root, "orderResults")).GetArrayLength());
    }

    [Fact]
    public async Task Retention_of_a_finished_child_and_old_jobs_keeps_the_pending_delivery_and_every_items_identity()
    {
        await using var host = OpenHost();
        var childKey = NewKey("retained-child");
        await PublishAsync(host, WaitingChild(childKey, ChildInputs));
        var parent = await PublishAsync(host, Parent(NewKey("retained"), childKey, "parallel", Orders("a", "b")));
        var root = await StartAsync(host, parent.Id);
        await DrainAsync(host, root);
        var children = await ChildrenAsync(root);
        Assert.Equal(2, children.Count);
        // Item 0 is published; item 1's child has finished, but its result is not delivered yet.
        await TakeChildActionAsync(host, children[0]);
        await DrainAsync(host, root);
        await TakeChildActionAsync(host, children[1]);
        var pending = (await OpenJobIdsAsync(children[1], WorkflowJobKinds.CallResume)).Single();

        // Age only this tree: the finished children and every completed job in it.
        var old = DateTimeOffset.UtcNow.AddDays(-60);
        await using (var db = fixture.CreateDbContext())
        {
            await db.WorkflowInstances.Where(row => children.Contains(row.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.FinishedAt, old));
            var tree = children.Append(root).ToList();
            await db.WorkflowJobs.Where(row => row.InstanceId != null && tree.Contains(row.InstanceId.Value)
                    && row.Status == WorkflowJobStatuses.Completed)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CompletedAt, old));
        }
        var completedJobs = await CompletedJobIdsAsync(children.Append(root).ToList());
        Assert.NotEmpty(completedJobs);

        await RunRetentionAsync([RetentionCategories.WorkflowHistory, RetentionCategories.VariableHistory,
            RetentionCategories.NodeActivity, RetentionCategories.CompletedJobs]);

        await using (var db = fixture.CreateDbContext())
        {
            // Retention reached the tree: the finished children's history and the old jobs are gone.
            Assert.All(await db.WorkflowInstances.Where(row => children.Contains(row.Id)).ToListAsync(),
                row => Assert.NotNull(row.HistoryPrunedAt));
            Assert.False(await db.WorkflowJobs.AnyAsync(row => completedJobs.Contains(row.Id)));
            // The running parent and what the pending delivery needs are untouched.
            Assert.Null((await db.WorkflowInstances.SingleAsync(row => row.Id == root)).HistoryPrunedAt);
            Assert.Equal(WorkflowJobStatuses.Queued, (await db.WorkflowJobs.SingleAsync(row => row.Id == pending)).Status);
            var result = await db.CallActivityResults.SingleAsync(row => row.ChildInstanceId == children[1]);
            Assert.NotNull(result.OutputValuesJson);
            var invocations = await db.CallActivityExecutions.Where(row => row.ParentInstanceId == root).ToListAsync();
            Assert.Equal(2, invocations.Count);
            Assert.All(invocations, row =>
            {
                Assert.NotNull(row.AutomaticMultiInstanceItemId);
                Assert.NotNull(row.MultiInstanceExecutionId);
                Assert.NotNull(row.InputValuesJson);
            });
            var published = await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == root && row.ItemIndex == 0);
            Assert.Equal("completed", published.Status);
            Assert.NotNull(published.ResultJson);
        }

        await DrainAsync(host, root);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        var results = await LatestAsync(root, "orderResults");
        Assert.Equal(new[] { 0, 1 }, results.EnumerateArray().Select(row => row.GetProperty("variables").GetProperty("itemIndex").GetInt32()));
        Assert.Equal(new[] { "a", "b" },
            results.EnumerateArray().Select(row => row.GetProperty("variables").GetProperty("echo").GetProperty("id").GetString()));
        Assert.All(await after.CallActivityExecutions.Where(row => row.ParentInstanceId == root).ToListAsync(),
            row => Assert.Equal(CallInvocationStates.Completed, row.State));
    }

    private async Task<PagedResult<AutomaticMultiInstanceItemSummaryDto>> ItemsAsync(WebApplicationFactory<Program> host, long root)
    {
        long bodyId;
        await using (var db = fixture.CreateDbContext())
            bodyId = (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == root)).Id;
        return await GetAsync<PagedResult<AutomaticMultiInstanceItemSummaryDto>>(host, $"/api/multi-instance-executions/{bodyId}/items");
    }

    private Task<PagedResult<CallActivityExecutionDto>> CallsAsync(WebApplicationFactory<Program> host, long root) =>
        GetAsync<PagedResult<CallActivityExecutionDto>>(host, $"/api/instances/{root}/calls");

    private Task<InstanceDetailDto> InstanceDetailAsync(WebApplicationFactory<Program> host, long root) =>
        GetAsync<InstanceDetailDto>(host, $"/api/instances/{root}");

    private async Task<T> GetAsync<T>(WebApplicationFactory<Program> host, string path)
    {
        using var response = await SendAsync(host, HttpMethod.Get, path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonSerializer.Deserialize<T>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task CreateSharedAsync(WebApplicationFactory<Program> host, string key, int value)
    {
        using var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
            new CreateSharedVariableRequest(key, "number", IsArray: false, Nullable: false, HasValue: true, Json(value)));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
    }

    private async Task SetSharedAsync(WebApplicationFactory<Program> host, string key, int value)
    {
        using var metadata = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{key}");
        var revision = (await metadata.Content.ReadFromJsonAsync<SharedVariableMetadataDto>())!.Revision;
        using var updated = await SendAsync(host, HttpMethod.Put, $"/api/shared-variables/{key}/value",
            new UpdateSharedVariableValueRequest(Json(value), revision));
        Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync());
    }

    private async Task CreateSharedJsonAsync(WebApplicationFactory<Program> host, string key, JsonElement value)
    {
        using var created = await SendAsync(host, HttpMethod.Post, "/api/shared-variables",
            new CreateSharedVariableRequest(key, "json", IsArray: true, Nullable: false, HasValue: true, value));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
    }

    private async Task SetSharedJsonAsync(WebApplicationFactory<Program> host, string key, JsonElement value)
    {
        using var metadata = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{key}");
        var revision = (await metadata.Content.ReadFromJsonAsync<SharedVariableMetadataDto>())!.Revision;
        using var updated = await SendAsync(host, HttpMethod.Put, $"/api/shared-variables/{key}/value",
            new UpdateSharedVariableValueRequest(value, revision));
        Assert.True(updated.IsSuccessStatusCode, await updated.Content.ReadAsStringAsync());
    }

    // Archives or reactivates a catalog key through its own API.
    private async Task SharedLifecycleAsync(WebApplicationFactory<Program> host, string key, string action)
    {
        using var metadata = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{key}");
        var revision = (await metadata.Content.ReadFromJsonAsync<SharedVariableMetadataDto>())!.Revision;
        using var response = await SendAsync(host, HttpMethod.Post, $"/api/shared-variables/{key}/{action}",
            action == "archive" ? new ArchiveSharedVariableRequest(revision) : new ReactivateSharedVariableRequest(revision));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<long> LeaseGenerationAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.Where(row => row.Id == jobId).Select(row => row.LeaseGeneration).SingleAsync();
    }

    private async Task<double> SharedValueAsync(WebApplicationFactory<Program> host, string key)
    {
        using var response = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{key}/value");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("value").GetDouble();
    }

    // An administrative variable update, which wakes conditions through the engine's own
    // conditional coordinator in the writing transaction.
    private async Task PatchVariableAsync(WebApplicationFactory<Program> host, long instanceId, string name, object? value)
    {
        using var response = await SendAsync(host, HttpMethod.Patch, $"/api/instances/{instanceId}/variables",
            new UpdateInstanceVariablesRequest([new InstanceVariableWriteDto(name, Json(value))],
                "call body runtime test", $"call-mi-update-{Guid.NewGuid():N}"));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    // Takes the one action of an instance's active user task at a node.
    private async Task CompleteRootTaskAsync(WebApplicationFactory<Program> host, long instanceId, int nodeId, int flowId)
    {
        long taskId;
        await using (var db = fixture.CreateDbContext())
            taskId = await db.UserTasks.Where(row => row.InstanceId == instanceId && row.NodeId == nodeId && row.Status == "active")
                .Select(row => row.Id).SingleAsync();
        using var response = await SendAsync(host, HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/{flowId}", new TakeFlowRequest(null));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    // The raw JSON a child stored for one of its start inputs.
    private async Task<string> StartValueAsync(long childInstanceId, string name) =>
        (await LatestAsync(childInstanceId, name)).GetRawText();

    // A child whose REST call gets one attempt before an incident. The fixture's service
    // answers this URL 503 the first time and 200 after that, so the first child waits in
    // its own incident until a retry, and every later child succeeds at once.
    private static WorkflowModel RemoteChild(string key) => new()
    {
        Id = key, Name = key, InitialEventId = Start,
        FlowNodes =
        [
            new() { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent, Variables = ChildInputs.ToList() },
            new()
            {
                Id = 6, Name = "Remote", Type = BpmnFlowNodeTypes.ServiceTask, AsyncBefore = true,
                Job = new JobPolicyModel { FailureHandling = JobFailureHandling.RetryFirst, RetryDelays = [] },
                Service = new ServiceTaskModel
                {
                    Url = $"https://tests.local/status-sequence/{Guid.NewGuid():N}/503,200", Method = "GET", TimeoutSeconds = 5
                }
            },
            new() { Id = End, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows = [new() { Id = 101, SourceRef = Start, TargetRef = 6 }, new() { Id = 601, SourceRef = 6, TargetRef = End }]
    };

    private async Task<int> SharedRevisionCountAsync(string key)
    {
        await using var db = fixture.CreateDbContext();
        var id = await db.SharedVariables.Where(row => row.Key == key).Select(row => row.Id).SingleAsync();
        return await db.SharedVariableRevisions.CountAsync(row => row.SharedVariableId == id);
    }

    // Each item copies its child's submitted "big" value into a nullable json local.
    private static WorkflowModel BigOutputParent(string key, string childKey, bool boundary)
    {
        var model = Parent(key, childKey, "parallel", Orders("a", "b"), boundary);
        var body = model.FlowNodes.Single(node => node.Id == Body);
        body.MultiInstance!.OutputVariables = [Local("itemIndex"), Local("echo", "json", nullable: true)];
        body.CallActivity!.OutputMappings = [Mapping("orderIndex", "itemIndex"), Mapping("big", "echo", WorkflowVariableTypes.Json)];
        return model;
    }

    private async Task<List<long>> CompletedJobIdsAsync(List<long> instanceIds)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.Where(row => row.InstanceId != null && instanceIds.Contains(row.InstanceId.Value)
            && row.Status == WorkflowJobStatuses.Completed).Select(row => row.Id).ToListAsync();
    }

    // Runs retention over the shared database with a 30-day policy for these categories,
    // then puts every policy back to keep forever. Only rows a test aged past 30 days are
    // eligible, as in RetentionRepositoryTests.
    private async Task RunRetentionAsync(IReadOnlyList<string> categories)
    {
        await using var source = new Flowbit.Infrastructure.Data.RetentionDataSource(fixture.ConnectionString);
        var repository = new Flowbit.Infrastructure.Repositories.RetentionRepository(source, TimeProvider.System);
        try
        {
            foreach (var category in categories)
            {
                var policy = (await repository.GetAsync(default)).Policies.Single(row => row.Category == category);
                await repository.UpdatePolicyAsync(category, new(30, policy.Revision), "call-mi-retention", default);
            }
            await repository.RequestRunAsync("call-mi-retention", default);
            var workerId = Guid.NewGuid().ToString();
            for (var index = 0; ; index++)
            {
                Assert.True(index < 300, "The retention run did not finish.");
                var tick = await repository.ProcessBatchAsync(new()
                {
                    WorkerId = workerId, BatchSize = 250, MaxRunnableJobs = int.MaxValue, MaxQueueLagSeconds = int.MaxValue
                }, default);
                if (!tick.HasActiveRun) break;
            }
        }
        finally
        {
            await using var db = fixture.CreateDbContext();
            await db.RetentionPolicies.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RetentionDays, (int?)null)
                .SetProperty(row => row.IsInitialized, true).SetProperty(row => row.Revision, row => row.Revision + 1));
            await db.RetentionCoordinators.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CurrentRunJson, (string?)null)
                .SetProperty(row => row.LeaseOwner, (string?)null)
                .SetProperty(row => row.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(row => row.NextScheduledAt, DateTimeOffset.UtcNow.AddDays(1)));
        }
    }

    // A child that ends normally for item 0 and in an error end for every other item.
    private static WorkflowModel SplitChild(string key) => new()
    {
        Id = key, Name = key, InitialEventId = Start,
        FlowNodes =
        [
            new() { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent, Variables = ChildInputs.ToList() },
            new() { Id = 6, Name = "First item?", Type = BpmnFlowNodeTypes.ExclusiveGateway },
            new() { Id = End, Name = "End", Type = BpmnFlowNodeTypes.EndEvent },
            new()
            {
                Id = 7, Name = "Rejected", Type = BpmnFlowNodeTypes.ErrorEndEvent, ErrorCode = "child.rejected",
                ErrorDescription = "The child rejected the request."
            }
        ],
        SequenceFlows =
        [
            new() { Id = 101, SourceRef = Start, TargetRef = 6 },
            new() { Id = 601, SourceRef = 6, TargetRef = End, Condition = "orderIndex == 0", ConditionPriority = 1 },
            new() { Id = 602, SourceRef = 6, TargetRef = 7, IsDefault = true }
        ]
    };

    private static WorkflowModel FaultingChildWithInputs(string key)
    {
        var child = FaultingChild(key);
        child.FlowNodes.Single(node => node.Id == Start).Variables = ChildInputs.ToList();
        return child;
    }

    // A faulting child produces no results, so this parent maps none.
    private static WorkflowModel FaultingParent(string key, string childKey, bool boundary)
    {
        var model = Parent(key, childKey, "parallel", Orders("a", "b"), boundary);
        var body = model.FlowNodes.Single(node => node.Id == Body);
        body.CallActivity!.OutputMappings = [];
        body.MultiInstance!.OutputVariables = [];
        return model;
    }

    // Start, a parallel split into an ordinary call and the call body, a join, and the end.
    private static WorkflowModel BranchedParent(string key, string childKey, object[]? orders = null, string mode = "parallel")
    {
        var model = Parent(key, childKey, mode, orders ?? Orders("a"));
        model.SequenceFlows.Clear();
        model.FlowNodes.Add(new() { Id = 6, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway });
        model.FlowNodes.Add(new()
        {
            Id = 7, Name = "Ordinary call", Type = BpmnFlowNodeTypes.CallActivity,
            CallActivity = new()
            {
                WorkflowKey = childKey,
                InputMappings = [Mapping("seedOrder", "order", WorkflowVariableTypes.Json), Mapping("seed", "orderIndex")]
            }
        });
        model.FlowNodes.Add(new() { Id = 8, Name = "Join", Type = BpmnFlowNodeTypes.ParallelGateway });
        model.Variables.Add(new() { Id = 9, Name = "seed", DataType = "number", DefaultValue = Json(7) });
        model.Variables.Add(new() { Id = 10, Name = "seedOrder", DataType = "json", DefaultValue = Json(new { id = "ordinary" }) });
        model.SequenceFlows.AddRange(
        [
            new() { Id = 101, SourceRef = Start, TargetRef = 6 },
            new() { Id = 601, SourceRef = 6, TargetRef = 7 },
            new() { Id = 602, SourceRef = 6, TargetRef = Body },
            new() { Id = 701, SourceRef = 7, TargetRef = 8 },
            new() { Id = 201, SourceRef = Body, TargetRef = 8 },
            new() { Id = 801, SourceRef = 8, TargetRef = End }
        ]);
        return model;
    }

    private async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program>? host, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        ApiTestAuth.Authorize(request, "call-mi-admin", "admin");
        return await (host?.CreateClient() ?? fixture.Client).SendAsync(request);
    }

    private async Task<WorkflowDetailDto> PublishAsync(WebApplicationFactory<Program> host, WorkflowModel definition)
    {
        using var response = await SendAsync(host, HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(definition, true));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonSerializer.Deserialize<WorkflowDetailDto>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<WorkflowDetailDto> PublishNewDefaultAsync(WebApplicationFactory<Program> host, long previousId, WorkflowModel definition)
    {
        using var response = await SendAsync(host, HttpMethod.Put, $"/api/workflows/{previousId}", new UpdateWorkflowRequest(definition, true));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        var version = JsonSerializer.Deserialize<WorkflowDetailDto>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        using var setDefault = await SendAsync(host, HttpMethod.Post, $"/api/workflows/{version.Id}/set-default");
        Assert.True(setDefault.IsSuccessStatusCode, await setDefault.Content.ReadAsStringAsync());
        return version;
    }

    private async Task<long> StartAsync(WebApplicationFactory<Program> host, long workflowId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>().StartInstanceSlimAsync(
            workflowId, null, new ActorContext("starter", ["User"], new Dictionary<string, string>()), null, null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        roots.Add(started.Id);
        return started.Id;
    }

    private async Task TakeChildActionAsync(WebApplicationFactory<Program> host, long childInstanceId, TakeFlowRequest? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{childInstanceId}/flows/{ChildFlow}")
        {
            Content = body is null ? JsonContent.Create(new { }) : JsonContent.Create(body)
        };
        ApiTestAuth.Authorize(request, "approver", "approver");
        using var response = await host.CreateClient().SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task RetryIncidentAsync(WebApplicationFactory<Program> host, long jobId)
    {
        await using var db = fixture.CreateDbContext();
        var incident = await db.WorkflowIncidents.SingleAsync(row => row.JobId == jobId && row.Status == "open");
        using var response = await SendAsync(host, HttpMethod.Post, $"/api/incidents/{incident.Id}/retry");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<List<long>> ChildrenAsync(long root)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowInstances.Where(row => row.CallRootInstanceId == root).OrderBy(row => row.Id)
            .Select(row => row.Id).ToListAsync();
    }

    private async Task<List<long>> OpenJobIdsAsync(long instanceId, string kind)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.AsNoTracking()
            .Where(job => job.InstanceId == instanceId && job.Kind == kind
                && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry
                    || job.Status == WorkflowJobStatuses.ResultReady))
            .OrderBy(job => job.Id).Select(job => job.Id).ToListAsync();
    }

    private async Task ProcessNextAsync(WebApplicationFactory<Program> host, long instanceId, string kind) =>
        await ProcessAsync(host, (await OpenJobIdsAsync(instanceId, kind)).First());

    // Leases and runs one job. Workers lease due control work first, so another due control
    // job of the same tree may run first, as a Worker would run it. A parked result-ready job
    // is made runnable by ending its lease, as its deadline would.
    private async Task ProcessAsync(WebApplicationFactory<Program> host, long jobId)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            await using (var promote = fixture.CreateDbContext())
            {
                await promote.WorkflowJobs.Where(job => job.Id == jobId).ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, 1_000_000)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1))
                    .SetProperty(job => job.LeaseExpiresAt, job => job.LeaseExpiresAt == null ? null : DateTimeOffset.UtcNow.AddMinutes(-1)));
            }
            await using var scope = host.Services.CreateAsyncScope();
            var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                new WorkflowJobLeaseRequest($"call-mi:{Guid.NewGuid():N}", MaxCount: 1, MaxActivityCount: 1,
                    MaxPerInstance: 4, LeaseDuration: TimeSpan.FromMinutes(1)), CancellationToken.None);
            var lease = Assert.Single(leases);
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
            if (lease.Job.Id == jobId) return;
        }
        Assert.Fail($"Job #{jobId} was not leased.");
    }

    // Runs runnable jobs of the parent and its children until none remains, the way Workers
    // eventually would, optionally leaving every job of one kind unprocessed.
    private async Task DrainAsync(WebApplicationFactory<Program> host, long root, int maxJobs = 96, string? skipKind = null)
    {
        for (var processed = 0; processed < maxJobs; processed++)
        {
            await using var db = fixture.CreateDbContext();
            var tree = await db.WorkflowInstances.AsNoTracking()
                .Where(instance => instance.Id == root || instance.CallRootInstanceId == root)
                .Select(instance => instance.Id).ToListAsync();
            var next = await db.WorkflowJobs.AsNoTracking()
                .Where(job => job.InstanceId != null && tree.Contains(job.InstanceId.Value) && job.Kind != skipKind
                    && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry))
                .OrderBy(job => job.Id).Select(job => (long?)job.Id).FirstOrDefaultAsync();
            if (next is not long jobId) return;
            await ProcessAsync(host, jobId);
        }
        throw new InvalidOperationException($"Root #{root} still had runnable jobs after {maxJobs}.");
    }

    private async Task<JsonElement> LatestAsync(long instanceId, string name)
    {
        await using var db = fixture.CreateDbContext();
        return (await db.InstanceVariables.Where(row => row.InstanceId == instanceId && row.VariableName == name)
            .OrderByDescending(row => row.Id).FirstAsync()).ValueJson.RootElement.Clone();
    }

    private static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(90);

    // Leases exactly this job as a Worker would, taking over an expired lease. Workers lease
    // due control work first, so another due job handed out first runs on the same host.
    private async Task<WorkflowJobLeaseRecord> LeaseExactAsync(WebApplicationFactory<Program> host, long jobId)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            await using (var promote = fixture.CreateDbContext())
            {
                await promote.WorkflowJobs.Where(job => job.Id == jobId).ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, 1_000_000)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1))
                    .SetProperty(job => job.LeaseExpiresAt, job => job.LeaseExpiresAt == null ? null : DateTimeOffset.UtcNow.AddMinutes(-1)));
            }
            await using var scope = host.Services.CreateAsyncScope();
            var lease = Assert.Single(await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                new WorkflowJobLeaseRequest($"call-mi:{Guid.NewGuid():N}", MaxCount: 1, MaxActivityCount: 1,
                    MaxPerInstance: 4, LeaseDuration: TimeSpan.FromMinutes(1)), CancellationToken.None));
            if (lease.Job.Id == jobId) return lease;
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
        }
        Assert.Fail($"Job #{jobId} was not leased.");
        return null!;
    }

    private static async Task ProcessLeaseAsync(WebApplicationFactory<Program> host, WorkflowJobLeaseRecord lease)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
    }

    // Starts a lease on a gated host and returns its processing once it waits at the gate,
    // after its first transaction ended.
    private static async Task<Task> ProcessUntilGateAsync(WebApplicationFactory<Program> host, DiscardGate gate,
        WorkflowJobLeaseRecord lease)
    {
        gate.Arm();
        var processing = Task.Run(() => ProcessLeaseAsync(host, lease));
        if (await Task.WhenAny(gate.Reached.Task, processing).WaitAsync(RaceTimeout) == processing)
        {
            await processing;
            Assert.Fail($"Job #{lease.Job.Id} finished before it reached its gate.");
        }
        return processing;
    }

    /// <summary>
    /// Holds the next <see cref="IUnitOfWork.DiscardChanges"/> after <see cref="Arm"/> until
    /// <see cref="Release"/>. Only the first discard after arming waits, so a job's later
    /// discards, and other jobs on the same host, pass through.
    /// </summary>
    private sealed class DiscardGate
    {
        private int armed;
        private readonly ManualResetEventSlim released = new(false);

        public TaskCompletionSource Reached { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm()
        {
            Reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            released.Reset();
            Volatile.Write(ref armed, 1);
        }

        public void Release() => released.Set();

        public void Pass()
        {
            if (Interlocked.Exchange(ref armed, 0) == 0) return;
            Reached.TrySetResult();
            if (!released.Wait(RaceTimeout)) throw new TimeoutException("The gated job was never released.");
        }
    }

    private sealed class GatedUnitOfWork(IUnitOfWork inner, DiscardGate gate) : IUnitOfWork
    {
        public Task<IWorkflowTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
            inner.BeginTransactionAsync(cancellationToken);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => inner.SaveChangesAsync(cancellationToken);

        public void AfterCommit(Action callback) => inner.AfterCommit(callback);

        public void DiscardChanges()
        {
            inner.DiscardChanges();
            gate.Pass();
        }
    }

    // Holds an instance row lock in a transaction of the test's own, as a slow writer would.
    private sealed class RowHold(NpgsqlConnection connection, NpgsqlTransaction transaction, int pid) : IAsyncDisposable
    {
        public int Pid => pid;

        public async ValueTask DisposeAsync()
        {
            await transaction.CommitAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private async Task<RowHold> HoldInstanceRowAsync(long instanceId)
    {
        var connection = await fixture.DataSource.OpenConnectionAsync();
        var transaction = await connection.BeginTransactionAsync();
        int pid;
        await using (var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection, transaction))
            pid = Convert.ToInt32(await command.ExecuteScalarAsync());
        await using (var command = new NpgsqlCommand(
                         "SELECT 1 FROM flowbit.workflow_instances WHERE \"Id\" = @id FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("id", instanceId);
            Assert.NotNull(await command.ExecuteScalarAsync());
        }
        return new RowHold(connection, transaction, pid);
    }

    // Waits until this many backends wait, directly or behind each other, on the holder.
    private async Task WaitForBlockedAsync(int holderPid, int count)
    {
        for (var attempt = 0; attempt < 1200; attempt++)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                """
                WITH RECURSIVE blocked(pid) AS (
                    SELECT a.pid FROM pg_stat_activity a WHERE @holder = ANY(pg_blocking_pids(a.pid))
                    UNION
                    SELECT a.pid FROM pg_stat_activity a JOIN blocked b ON b.pid = ANY(pg_blocking_pids(a.pid))
                )
                SELECT count(*) FROM blocked
                """, connection);
            command.Parameters.AddWithValue("holder", holderPid);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) >= count) return;
            await Task.Delay(50);
        }
        Assert.Fail($"Fewer than {count} transactions ever waited on the held row.");
    }
}
