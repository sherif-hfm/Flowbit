using System.Net;
using System.Net.Http.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static Flowbit.Tests.CallActivityHarness;

namespace Flowbit.Tests;

/// <summary>
/// Abandoning a call: the parent settles at once, the fence stops the descendants, and
/// durable jobs finish them, one level per job.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class CallActivityCancellationTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => harness.RetireLeftoverJobsAsync();

    private readonly CallActivityHarness harness = new(fixture);

    [Fact]
    public async Task Cancelling_the_parent_settles_the_call_and_the_fence_stops_the_child_until_its_job_runs()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);
        var childId = invocation.ChildInstanceId!.Value;

        using (var cancelled = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/cancel"))
        {
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        }

        // The parent is settled, but the child has not become terminal: its job has not run.
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(parentId)).Status);
        var settled = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Cancelled, settled.State);
        Assert.False(settled.CapacityReserved);
        var fenced = await harness.DetailAsync(childId);
        Assert.Equal(WorkflowInstanceStatuses.Running, fenced.Status);
        Assert.Equal(CallInvocationStates.Cancelled, fenced.ParentCall!.State);

        using (var action = await harness.TakeChildActionAsync(childId))
        {
            Assert.Equal(HttpStatusCode.Conflict, action.StatusCode);
        }

        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallCancel);

        var child = await harness.DetailAsync(childId);
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, child.Status);
        var cancelledHistory = Assert.Single(child.History, item => item.Note == InstanceHistoryNotes.CallCancelled);
        Assert.Equal("callActivity", cancelledHistory.PerformedBy);

        // The settled invocation means the child's terminal commit wrote no result.
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.CallActivityResults.AnyAsync(result => result.ChildInstanceId == childId));
        Assert.False(await db.WorkflowJobs.AnyAsync(job =>
            job.CallInvocationId == invocation.Id && job.Kind == WorkflowJobKinds.CallResume));
    }

    [Fact]
    public async Task A_queued_call_cancelled_before_its_job_runs_never_starts_a_child()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        var invocation = await harness.InvocationAsync(parentId);

        using (var cancelled = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/cancel"))
        {
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        }

        var settled = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Cancelled, settled.State);
        Assert.Null(settled.ChildInstanceId);
        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallStart);
        Assert.Equal(WorkflowJobStatuses.Cancelled, await harness.JobStatusAsync(jobId));
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowInstances.AnyAsync(instance => instance.CallRootInstanceId == parentId));
        Assert.False(await db.WorkflowJobs.AnyAsync(job =>
            job.CallInvocationId == invocation.Id && job.Kind == WorkflowJobKinds.CallCancel));
    }

    [Fact]
    public async Task A_cancel_cascades_one_level_per_job_through_nested_calls()
    {
        var leafKey = Key("leaf");
        await harness.PublishAsync(WaitingChild(leafKey));
        var middleKey = Key("middle");
        await harness.PublishAsync(Parent(middleKey, leafKey));
        var parent = await harness.PublishAsync(Parent(Key("top"), middleKey));
        var rootId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(rootId);
        var top = await harness.InvocationAsync(rootId);
        var middleId = top.ChildInstanceId!.Value;
        var middle = await harness.InvocationAsync(middleId);
        var leafId = middle.ChildInstanceId!.Value;

        using (var cancelled = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{rootId}/cancel"))
        {
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        }

        // The deepest descendant is fenced through the whole chain before any job runs.
        using (var action = await harness.TakeChildActionAsync(leafId))
        {
            Assert.Equal(HttpStatusCode.Conflict, action.StatusCode);
        }

        await harness.ProcessCallJobAsync(top.Id, WorkflowJobKinds.CallCancel);
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(middleId)).Status);
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(leafId)).Status);
        Assert.Equal(CallInvocationStates.Cancelled, (await harness.InvocationAsync(middleId)).State);

        await harness.ProcessCallJobAsync(middle.Id, WorkflowJobKinds.CallCancel);
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(leafId)).Status);
    }

    [Fact]
    public async Task The_fence_follows_stored_depth_so_a_lower_limit_cannot_shorten_it()
    {
        var keys = Enumerable.Range(0, 4).Select(_ => Key("level")).ToArray();
        await harness.PublishAsync(WaitingChild(keys[3]));
        for (var level = 2; level >= 0; level--)
        {
            await harness.PublishAsync(Parent(keys[level], keys[level + 1]));
        }

        var root = await harness.PublishAsync(Parent(Key("root"), keys[0]));
        var rootId = await harness.StartAsync(root.Id);
        await harness.DrainAsync(rootId);
        var deepest = await DeepestAsync(rootId);
        Assert.Equal(4, deepest.CallDepth);

        using (var cancelled = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{rootId}/cancel"))
        {
            Assert.True(cancelled.IsSuccessStatusCode);
        }

        // Admission now refuses depth 2 and beyond.
        await using var lowered = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<CallActivityOptions>();
                services.AddSingleton(new CallActivityOptions { MaxDepth = 2 });
            }));
        var client = lowered.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{deepest.Id}/flows/{ChildFlow}")
        {
            Content = JsonContent.Create(new { })
        };
        ApiTestAuth.Authorize(request, "approver", "approver");
        using var action = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, action.StatusCode);
    }

    [Fact]
    public async Task Cancelling_a_child_that_already_finished_leaves_it_completed_and_discards_its_result()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(
            childKey,
            variables: [Process("approved", nullable: false, defaultValue: true)]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            variables: [Process("managerApproved")],
            outputs: [Mapping("approved", "managerApproved", WorkflowVariableTypes.Boolean)]));
        var parentId = await harness.StartAsync(parent.Id);
        var invocation = await harness.InvocationAsync(parentId);
        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallStart);
        invocation = await harness.InvocationAsync(parentId);
        var childId = invocation.ChildInstanceId!.Value;

        using (var cancelled = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/cancel"))
        {
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        }

        // The late resume completes as stale, and the cancel job finds nothing left to do.
        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallResume);
        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallCancel);

        Assert.Equal(
            WorkflowJobStatuses.Completed,
            await harness.JobStatusAsync(await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallResume)));
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(childId)).Status);
        Assert.Equal("null", await harness.VariableAsync(parentId, "managerApproved"));
        Assert.Equal(CallInvocationStates.Cancelled, (await harness.InvocationAsync(parentId)).State);
    }

    [Fact]
    public async Task A_scoped_interrupt_abandons_only_the_calls_of_the_cancelled_tokens()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var key = Key("parent");
        var model = new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = Start,
            FlowNodes =
            [
                new FlowNodeModel { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 10, Name = "Outer", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 11, Name = "Inner", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel
                {
                    Id = 12,
                    Name = "Survivor",
                    Type = BpmnFlowNodeTypes.CallActivity,
                    CallActivity = new CallActivityModel { WorkflowKey = childKey }
                },
                new FlowNodeModel
                {
                    Id = 13,
                    Name = "Abandoned",
                    Type = BpmnFlowNodeTypes.CallActivity,
                    CallActivity = new CallActivityModel { WorkflowKey = childKey }
                },
                new FlowNodeModel { Id = 14, Name = "Decide", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 15,
                    Name = "Interrupt",
                    Type = BpmnFlowNodeTypes.ScopedInterruptEvent,
                    GatewayRef = 11
                },
                new FlowNodeModel { Id = 16, Name = "End interrupt", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 17, Name = "End survivor", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 18, Name = "End abandoned", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 11 },
                new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 12 },
                new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 13 },
                new SequenceFlowModel { Id = 1102, SourceRef = 11, TargetRef = 14 },
                new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = 17 },
                new SequenceFlowModel { Id = 1301, SourceRef = 13, TargetRef = 18 },
                new SequenceFlowModel { Id = 1401, SourceRef = 14, TargetRef = 15, Name = "Interrupt" },
                new SequenceFlowModel { Id = 1501, SourceRef = 15, TargetRef = 16 }
            ]
        };
        var parent = await harness.PublishAsync(model);
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var calls = (await harness.CallsAsync(parentId)).Items;
        var survivor = calls.Single(call => call.NodeId == 12);
        var abandoned = calls.Single(call => call.NodeId == 13);
        Assert.Equal(CallInvocationStates.Running, survivor.State);
        Assert.Equal(CallInvocationStates.Running, abandoned.State);

        await using (var db = fixture.CreateDbContext())
        {
            var task = await db.UserTasks.AsNoTracking().SingleAsync(item => item.InstanceId == parentId);
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/user-tasks/{task.Id}/flows/1401")
            {
                Content = JsonContent.Create(new { })
            };
            ApiTestAuth.Authorize(request, "decider", "decider");
            using var decided = await fixture.Client.SendAsync(request);
            Assert.True(decided.IsSuccessStatusCode, await decided.Content.ReadAsStringAsync());
        }

        calls = (await harness.CallsAsync(parentId)).Items;
        Assert.Equal(CallInvocationStates.Running, calls.Single(call => call.NodeId == 12).State);
        Assert.Equal(CallInvocationStates.Cancelled, calls.Single(call => call.NodeId == 13).State);

        await harness.DrainAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(abandoned.ChildInstanceId!.Value)).Status);
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(survivor.ChildInstanceId!.Value)).Status);
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);
    }

    [Fact]
    public async Task A_parent_terminate_end_abandons_its_other_branch_calls()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var key = Key("parent");
        var model = new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = Start,
            FlowNodes =
            [
                new FlowNodeModel { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 10, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel
                {
                    Id = 11,
                    Name = "Call",
                    Type = BpmnFlowNodeTypes.CallActivity,
                    CallActivity = new CallActivityModel { WorkflowKey = childKey }
                },
                new FlowNodeModel { Id = 12, Name = "Stop?", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 13, Name = "Stop", Type = BpmnFlowNodeTypes.TerminateEndEvent },
                new FlowNodeModel { Id = 14, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 11 },
                new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 12 },
                new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 14 },
                new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = 13, Name = "Stop" }
            ]
        };
        var parent = await harness.PublishAsync(model);
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);

        await using (var db = fixture.CreateDbContext())
        {
            var task = await db.UserTasks.AsNoTracking().SingleAsync(item => item.InstanceId == parentId);
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/user-tasks/{task.Id}/flows/1201")
            {
                Content = JsonContent.Create(new { })
            };
            ApiTestAuth.Authorize(request, "stopper", "stopper");
            using var stopped = await fixture.Client.SendAsync(request);
            Assert.True(stopped.IsSuccessStatusCode, await stopped.Content.ReadAsStringAsync());
        }

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
        Assert.Equal(CallInvocationStates.Cancelled, (await harness.InvocationAsync(parentId)).State);
        await harness.DrainAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(invocation.ChildInstanceId!.Value)).Status);
    }

    [Fact]
    public async Task A_conflict_on_a_result_delivery_opens_an_incident_because_it_still_owns_its_work()
    {
        var sharedKey = $"tests.call.{Guid.NewGuid():N}";
        using (var created = await harness.SendAsync(
                   HttpMethod.Post,
                   "/api/shared-variables",
                   new CreateSharedVariableRequest(
                       sharedKey,
                       WorkflowVariableTypes.String,
                       IsArray: false,
                       Nullable: true,
                       HasValue: true,
                       System.Text.Json.JsonSerializer.SerializeToElement("none"),
                       null)))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var childKey = Key("child");
        await harness.PublishAsync(FaultingChild(childKey));
        var alias = new VariableModel
        {
            Id = 40,
            Name = "sharedError",
            Scope = VariableScopes.Shared,
            SharedKey = sharedKey,
            Access = SharedVariableAccessModes.ReadWrite,
            DataType = WorkflowVariableTypes.String,
            Nullable = true
        };
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            withBoundary: true,
            errorVariable: "sharedError",
            variables: [alias]));
        var parentId = await harness.StartAsync(parent.Id);
        var invocation = await harness.InvocationAsync(parentId);
        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallStart);

        // Writing the boundary's error variable now conflicts during delivery. A catalog
        // binding row refuses a real archive, so the state is set directly.
        await SetArchivedAsync(sharedKey, true);

        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallResume);
        await harness.ProcessAsync(jobId);

        var incident = await harness.OpenIncidentAsync(jobId);
        Assert.NotNull(incident);
        Assert.Equal(WorkflowIncidentTypes.JobConflict, incident!.Type);

        // Delivery rolled back whole, so the frozen result is still there to deliver.
        var unsettled = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Running, unsettled.State);
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);

        await SetArchivedAsync(sharedKey, false);

        await harness.RetryIncidentAsync(incident.Id);
        await harness.ProcessAsync(jobId);
        Assert.Equal(CallInvocationStates.Failed, (await harness.InvocationAsync(parentId)).State);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
    }

    [Fact]
    public async Task An_incident_on_a_cancel_job_can_be_retried_after_its_owner_has_ended()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);
        using (var cancelled = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/cancel"))
        {
            Assert.True(cancelled.IsSuccessStatusCode);
        }

        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallCancel);
        var lease = await harness.LeaseAsync(jobId);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var incident = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().OpenIncidentAsync(
                new WorkflowJobFence(jobId, lease.Job.WorkerId!, lease.LeaseToken, lease.LeaseGeneration),
                "job_execution_failed",
                "Exhausted.",
                "Exhausted.",
                CancellationToken.None);
            Assert.NotNull(incident);
            await harness.RetryIncidentAsync(incident!.Id);
        }

        // The parent is terminal and its token has ended, yet the job can still finish the child.
        await harness.ProcessAsync(jobId);
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(invocation.ChildInstanceId!.Value)).Status);
    }

    [Fact]
    public async Task A_parents_cancel_returns_while_a_childs_synchronous_call_holds_the_childs_locks()
    {
        const string path = "send-reminder";
        var childKey = Key("child");
        var child = WaitingChild(childKey);
        child.FlowNodes.Add(new FlowNodeModel
        {
            Id = 7,
            Name = "Remote",
            Type = BpmnFlowNodeTypes.ServiceTask,
            Service = new ServiceTaskModel { Url = "https://tests.local/" + path, Method = "GET", TimeoutSeconds = 30 }
        });
        child.SequenceFlows.Single(flow => flow.Id == ChildFlow).TargetRef = 7;
        child.SequenceFlows.Add(new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = End });
        await harness.PublishAsync(child);
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);
        var childId = invocation.ChildInstanceId!.Value;

        var block = fixture.ServiceInvocations.BlockNext("/" + path);
        var action = harness.TakeChildActionAsync(childId);
        await block.WaitUntilEnteredAsync(CancellationToken.None);
        try
        {
            // The child's transaction is still open, and this must not wait for it.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{parentId}/cancel");
            ApiTestAuth.Authorize(request, "call-admin", "admin");
            using var cancelled = await fixture.Client.SendAsync(request, timeout.Token);
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
            Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(parentId)).Status);
        }
        finally
        {
            block.Release();
        }

        // The transition already in progress commits, and its result is discarded.
        using (var acted = await action)
        {
            Assert.True(acted.IsSuccessStatusCode, await acted.Content.ReadAsStringAsync());
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.CallActivityResults.AnyAsync(result => result.ChildInstanceId == childId));
        }

        await harness.DrainAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(childId)).Status);
        Assert.Equal(CallInvocationStates.Cancelled, (await harness.InvocationAsync(parentId)).State);
    }

    private async Task SetArchivedAsync(string sharedKey, bool archived)
    {
        await using var db = fixture.CreateDbContext();
        var status = archived ? SharedVariableStatuses.Archived : SharedVariableStatuses.Active;
        DateTimeOffset? archivedAt = archived ? DateTimeOffset.UtcNow : null;
        await db.SharedVariables
            .Where(variable => variable.Key == sharedKey)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(variable => variable.Status, status)
                .SetProperty(variable => variable.ArchivedAt, archivedAt));
    }

    [Fact]
    public async Task An_inconsistent_chain_fails_closed_and_preserves_job_work_as_an_incident()
    {
        var leafKey = Key("leaf");
        await harness.PublishAsync(WaitingChild(leafKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), leafKey));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);
        var childId = invocation.ChildInstanceId!.Value;

        // Depth 2 claims one more link than the chain has. The check constraints let this
        // shape through, so the fence must catch the truncation itself.
        await using (var db = fixture.CreateDbContext())
        {
            await db.WorkflowInstances
                .Where(instance => instance.Id == childId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(instance => instance.CallDepth, 2));
        }

        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var runtime = scope.ServiceProvider.GetRequiredService<IWorkflowRuntimeRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            await Assert.ThrowsAsync<WorkflowCallChainException>(() =>
                runtime.GetInstanceForUpdateAsync(childId, false, CancellationToken.None));
        }
        finally
        {
            await using var db = fixture.CreateDbContext();
            await db.WorkflowInstances
                .Where(instance => instance.Id == childId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(instance => instance.CallDepth, 1));
        }
    }

    [Fact]
    public void The_chain_check_rejects_missing_cyclic_truncated_and_inconsistent_ancestry()
    {
        CallChainLinkRecord Link(long invocation, long child, long parent, int depth, long root = 1, string state = CallInvocationStates.Running, bool cycle = false, long? parentRoot = 1, int? parentDepth = null) =>
            new(invocation, parent, child, root, depth, state, cycle, true, parentRoot, parentDepth ?? depth - 1);

        var valid = new[]
        {
            Link(9, 3, 2, 2),
            Link(8, 2, 1, 1, parentRoot: null, parentDepth: 0)
        };
        Assert.Null(CallChainFence.FindCancelledInvocation(3, 1, 2, valid));

        var cancelled = new[]
        {
            Link(9, 3, 2, 2),
            Link(8, 2, 1, 1, state: CallInvocationStates.Cancelled, parentRoot: null, parentDepth: 0)
        };
        Assert.Equal(8, CallChainFence.FindCancelledInvocation(3, 1, 2, cancelled));

        // Missing link.
        Assert.Throws<WorkflowCallChainException>(() =>
            CallChainFence.FindCancelledInvocation(3, 1, 2, [Link(9, 3, 2, 2)]));

        // Cycle.
        Assert.Throws<WorkflowCallChainException>(() => CallChainFence.FindCancelledInvocation(
            3,
            1,
            2,
            [Link(9, 3, 2, 2), Link(8, 2, 1, 1, cycle: true, parentRoot: null, parentDepth: 0)]));

        // A longer chain than the depth allows.
        Assert.Throws<WorkflowCallChainException>(() => CallChainFence.FindCancelledInvocation(
            3,
            1,
            1,
            valid));

        // Inconsistent root and depth.
        Assert.Throws<WorkflowCallChainException>(() => CallChainFence.FindCancelledInvocation(
            3,
            7,
            2,
            valid));
        Assert.Throws<WorkflowCallChainException>(() => CallChainFence.FindCancelledInvocation(
            3,
            1,
            2,
            [Link(9, 3, 2, 5), valid[1]]));

        // A chain that ends anywhere but the recorded root.
        Assert.Throws<WorkflowCallChainException>(() => CallChainFence.FindCancelledInvocation(
            3,
            1,
            2,
            [Link(9, 3, 2, 2), Link(8, 2, 6, 1, parentRoot: null, parentDepth: 0)]));

        // A depth outside what any instance can hold.
        Assert.Throws<WorkflowCallChainException>(() => CallChainFence.FindCancelledInvocation(3, 1, 33, []));
    }

    private async Task<Flowbit.Infrastructure.Entities.WorkflowInstanceEntity> DeepestAsync(long rootId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowInstances.AsNoTracking()
            .Where(instance => instance.CallRootInstanceId == rootId)
            .OrderByDescending(instance => instance.CallDepth)
            .FirstAsync();
    }
}
