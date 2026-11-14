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
using Xunit;
using static Flowbit.Tests.CallActivityHarness;

namespace Flowbit.Tests;

/// <summary>
/// TASK-05D end to end against PostgreSQL: call bodies are published over HTTP through a
/// host with the Development release switch open, and every entry, dispatch, callStart,
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

    private WebApplicationFactory<Program> OpenHost(Action<CallActivityOptions>? limits = null, bool released = true) =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var options = new AutomaticMultiInstanceOptions { AllowInDevelopment = released };
            options.ResolveForEnvironment(isDevelopment: released);
            services.RemoveAll<AutomaticMultiInstanceOptions>();
            services.AddSingleton(options);
            if (limits is not null)
            {
                var callOptions = new CallActivityOptions();
                limits(callOptions);
                services.RemoveAll<CallActivityOptions>();
                services.AddSingleton(callOptions);
            }
        }));

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
    public async Task A_closed_host_refuses_a_call_bodys_callStart_and_the_item_waits_in_its_incident()
    {
        await using var open = OpenHost();
        var childKey = NewKey("closed");
        await PublishAsync(open, ImmediateChild(childKey, ChildInputs));
        var parent = await PublishAsync(open, Parent(NewKey("closed-parent"), childKey, "parallel", Orders("a")));
        var root = await StartAsync(open, parent.Id);
        await ProcessNextAsync(open, root, WorkflowJobKinds.MultiInstanceStart);
        await ProcessNextAsync(open, root, WorkflowJobKinds.MultiInstanceDispatch);
        var start = (await OpenJobIdsAsync(root, WorkflowJobKinds.CallStart)).Single();

        // A host outside Development refuses a body another host published, whatever it says.
        await using var closed = OpenHost(released: false);
        await using (var exhaust = fixture.CreateDbContext())
            await exhaust.WorkflowJobs.Where(row => row.Id == start)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.MaxAttempts, 1));
        await ProcessAsync(closed, start);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowJobStatuses.Incident, (await db.WorkflowJobs.SingleAsync(row => row.Id == start)).Status);
        Assert.False(await db.WorkflowInstances.AnyAsync(row => row.CallRootInstanceId == root));
        var invocation = await db.CallActivityExecutions.SingleAsync(row => row.ParentInstanceId == root);
        Assert.Equal(CallInvocationStates.Queued, invocation.State);
        Assert.Null(invocation.InputValuesJson);
        // The incident blocks its item, which keeps its slot and its queued invocation.
        Assert.Equal("active", (await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == root)).Status);
        await using var scope = open.Services.CreateAsyncScope();
        var progress = Assert.Single(await scope.ServiceProvider.GetRequiredService<IAutomaticMultiInstanceRepository>()
            .GetProgressAsync(root, CancellationToken.None));
        Assert.Equal(0, progress.ActiveCount);
        Assert.Equal("itemIncident", progress.Execution.AdmissionBlockedReason);
    }

    [Fact]
    public async Task Call_bodies_publish_only_on_a_released_host_and_validate_item_sources()
    {
        await using var open = OpenHost();
        var childKey = NewKey("publish");
        await PublishAsync(open, ImmediateChild(childKey, ChildInputs));
        var model = Parent(NewKey("closed-publish"), childKey, "parallel", Orders("a"));

        // The ordinary test host is not Development.
        using (var refused = await SendAsync(null, HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("automatic multiInstance is not enabled", await refused.Content.ReadAsStringAsync());
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
    public async Task A_closed_host_refuses_a_call_body_at_entry_and_its_item_results_but_closes_stale_work_quietly()
    {
        await using var open = OpenHost();
        await using var closed = OpenHost(released: false);
        var childKey = NewKey("closed-entry");
        await PublishAsync(open, WaitingChild(childKey, ChildInputs));
        var parent = await PublishAsync(open, Parent(NewKey("closed-entry-parent"), childKey, "parallel", Orders("a", "b")));

        // Routing on a closed host refuses the body, so the start rolls back.
        await Assert.ThrowsAsync<WorkflowDomainException>(() => StartAsync(closed, parent.Id));
        await using (var db = fixture.CreateDbContext())
            Assert.False(await db.WorkflowInstances.AnyAsync(row => row.WorkflowDefinitionId == parent.Id));

        // An entry queued on an open host is retried on a closed one, before any body exists.
        var root = await StartAsync(open, parent.Id);
        var entry = (await OpenJobIdsAsync(root, WorkflowJobKinds.MultiInstanceStart)).Single();
        await ProcessAsync(closed, entry);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(WorkflowJobStatuses.Retry, (await db.WorkflowJobs.SingleAsync(row => row.Id == entry)).Status);
            Assert.False(await db.MultiInstanceExecutions.AnyAsync(row => row.InstanceId == root));
        }
        await ProcessAsync(open, entry);
        await DrainAsync(open, root);
        var children = await ChildrenAsync(root);
        Assert.Equal(2, children.Count);

        // A closed host does not publish an item's result: its callResume retries, and the
        // item keeps its slot with nothing saved. An open host then publishes it.
        await TakeChildActionAsync(open, children[0]);
        var resume = (await OpenJobIdsAsync(children[0], WorkflowJobKinds.CallResume)).Single();
        await ProcessAsync(closed, resume);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(WorkflowJobStatuses.Retry, (await db.WorkflowJobs.SingleAsync(row => row.Id == resume)).Status);
            var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == root && row.ItemIndex == 0);
            Assert.Equal("active", item.Status);
            Assert.Null(item.CandidateJson);
        }
        await ProcessAsync(open, resume);
        await using (var db = fixture.CreateDbContext())
            Assert.Equal("completed", (await db.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == root && row.ItemIndex == 0)).Status);

        // A result that arrives after its parent was cancelled is stale: a closed host
        // acknowledges it without an incident.
        await TakeChildActionAsync(open, children[1]);
        using (var cancelled = await SendAsync(open, HttpMethod.Post, $"/api/instances/{root}/cancel", new { }))
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        var lateResume = (await OpenJobIdsAsync(children[1], WorkflowJobKinds.CallResume)).Single();
        await ProcessAsync(closed, lateResume);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowJobStatuses.Completed, (await after.WorkflowJobs.SingleAsync(row => row.Id == lateResume)).Status);
        Assert.False(await after.WorkflowIncidents.AnyAsync(row => row.JobId == lateResume));
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal("cancelled", (await after.MultiInstanceActivityItems.SingleAsync(row => row.InstanceId == root && row.ItemIndex == 1)).Status);
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
    public async Task A_closed_host_refuses_a_call_bodys_dispatch_and_completion()
    {
        await using var open = OpenHost();
        await using var closed = OpenHost(released: false);
        var childKey = NewKey("closed-control");
        await PublishAsync(open, ImmediateChild(childKey, ChildInputs));
        var parent = await PublishAsync(open, Parent(NewKey("closed-control-parent"), childKey, "parallel", Orders("a")));
        var root = await StartAsync(open, parent.Id);
        await ProcessNextAsync(open, root, WorkflowJobKinds.MultiInstanceStart);

        // Admission is body work: a closed host retries it and admits nothing.
        var dispatch = (await OpenJobIdsAsync(root, WorkflowJobKinds.MultiInstanceDispatch)).Single();
        await ProcessAsync(closed, dispatch);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(WorkflowJobStatuses.Retry, (await db.WorkflowJobs.SingleAsync(row => row.Id == dispatch)).Status);
            Assert.False(await db.CallActivityExecutions.AnyAsync(row => row.ParentInstanceId == root));
        }
        await ProcessAsync(open, dispatch);
        await DrainAsync(open, root, skipKind: WorkflowJobKinds.MultiInstanceComplete);

        // So is completion: a closed host neither publishes the aggregate nor continues.
        var complete = (await OpenJobIdsAsync(root, WorkflowJobKinds.MultiInstanceComplete)).Single();
        await ProcessAsync(closed, complete);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(WorkflowJobStatuses.Retry, (await db.WorkflowJobs.SingleAsync(row => row.Id == complete)).Status);
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
            Assert.Equal("[]", (await LatestAsync(root, "orderResults")).GetRawText());
        }
        await ProcessAsync(open, complete);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == root)).Status);
        Assert.Equal(1, (await LatestAsync(root, "orderResults")).GetArrayLength());
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

    private async Task<double> SharedValueAsync(WebApplicationFactory<Program> host, string key)
    {
        using var response = await SendAsync(host, HttpMethod.Get, $"/api/shared-variables/{key}/value");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("value").GetDouble();
    }

    // The raw JSON a child stored for one of its start inputs.
    private async Task<string> StartValueAsync(long childInstanceId, string name) =>
        (await LatestAsync(childInstanceId, name)).GetRawText();

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

    private async Task TakeChildActionAsync(WebApplicationFactory<Program> host, long childInstanceId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{childInstanceId}/flows/{ChildFlow}")
        {
            Content = JsonContent.Create(new { })
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
}
