using System.Net.Http.Json;
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
/// Depth, the soft open-call threshold, and the loop guard. A blocked call takes no slot, an
/// admitted one reuses its own, and every settlement releases it.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class CallActivityAdmissionTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => harness.RetireLeftoverJobsAsync();

    private readonly CallActivityHarness harness = new(fixture);

    private WebApplicationFactory<Program> WithLimits(int maxDepth = 8, int maxOpen = 256) =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<CallActivityOptions>();
            services.AddSingleton(new CallActivityOptions
            {
                MaxDepth = maxDepth,
                MaxOpenInvocationsPerRoot = maxOpen
            });
        }));

    [Fact]
    public async Task Depth_is_exact_at_admission()
    {
        await using var host = WithLimits(maxDepth: 2);
        var leafKey = Key("leaf");
        await harness.PublishAsync(ImmediateChild(leafKey));
        var secondKey = Key("second");
        await harness.PublishAsync(Parent(secondKey, leafKey));
        var firstKey = Key("first");
        await harness.PublishAsync(Parent(firstKey, secondKey));
        var root = await harness.PublishAsync(Parent(Key("root"), firstKey));

        var rootId = await harness.StartAsync(root.Id);
        await harness.DrainAsync(rootId, services: host.Services);

        // Depths 1 and 2 were admitted; the call that would create depth 3 was not.
        await using var db = fixture.CreateDbContext();
        var calls = await db.CallActivityExecutions.AsNoTracking()
            .Where(call => call.RootInstanceId == rootId)
            .OrderBy(call => call.ChildDepth)
            .ToListAsync();
        Assert.Equal([1, 2, 3], calls.Select(call => call.ChildDepth));
        Assert.Equal(CallInvocationStates.Running, calls[0].State);
        Assert.Equal(CallInvocationStates.Running, calls[1].State);
        Assert.Equal(CallInvocationStates.Queued, calls[2].State);
        Assert.False(calls[2].CapacityReserved);
        Assert.Equal(CallFailureCodes.DepthExceeded, calls[2].FailureCode);
        Assert.Null(calls[2].ChildInstanceId);
    }

    [Fact]
    public async Task An_unreserved_call_blocks_a_version_change_like_any_other_unsettled_call()
    {
        await using var host = WithLimits(maxDepth: 1);
        var leafKey = Key("leaf");
        await harness.PublishAsync(ImmediateChild(leafKey));
        var childKey = Key("child");
        await harness.PublishAsync(Parent(childKey, leafKey));
        var childNext = await harness.PublishAsync(Parent(childKey, leafKey));
        var root = await harness.PublishAsync(Parent(Key("root"), childKey));

        var rootId = await harness.StartAsync(root.Id);
        await harness.DrainAsync(rootId, services: host.Services);

        var childId = (await harness.InvocationAsync(rootId)).ChildInstanceId!.Value;
        var blocked = await harness.InvocationAsync(childId);
        Assert.False(blocked.CapacityReserved);
        Assert.Equal(CallFailureCodes.DepthExceeded, blocked.FailureCode);

        using var response = await harness.SendAsync(
            HttpMethod.Post,
            $"/api/instances/{childId}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(childNext.Id));
        var preview = await response.Content.ReadFromJsonAsync<InstanceVersionChangePreviewDto>();
        Assert.Contains(preview!.Blockers, item => item.Code == WorkflowVersionCompatibilityCodes.UnsettledOutgoingCall);

        var detail = await harness.DetailAsync(childId);
        using (var direct = await harness.SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{childId}/version-change",
                   new ChangeInstanceVersionRequest(childNext.Id, detail.Workflow.Id, detail.UpdatedAt, "Move a blocked call")))
        {
            Assert.Equal(System.Net.HttpStatusCode.Conflict, direct.StatusCode);
        }

        var item = await harness.PrepareVersionBatchItemAsync(detail.Workflow.Id, childNext.Id, childId);
        Assert.Equal(InstanceVersionChangeBatchItemStatuses.Ineligible, item.Status);
        Assert.Contains(item.Blockers, blocker => blocker.Code == WorkflowVersionCompatibilityCodes.UnsettledOutgoingCall);
    }

    [Fact]
    public async Task Lowering_the_open_limit_keeps_admitted_work_and_stops_new_admissions()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var key = Key("parent");
        var parent = await harness.PublishAsync(new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = Start,
            FlowNodes =
            [
                new FlowNodeModel { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 10, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                CallNode(11, childKey),
                CallNode(12, childKey),
                new FlowNodeModel { Id = 13, Name = "Next", Type = BpmnFlowNodeTypes.UserTask },
                CallNode(14, childKey),
                new FlowNodeModel { Id = 15, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 11 },
                new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 12 },
                new SequenceFlowModel { Id = 1003, SourceRef = 10, TargetRef = 13 },
                new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 15 },
                new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = 15 },
                new SequenceFlowModel { Id = 1301, SourceRef = 13, TargetRef = 14, Name = "Go" },
                new SequenceFlowModel { Id = 1401, SourceRef = 14, TargetRef = 15 }
            ]
        });

        // Both calls were admitted under the old limit, and the new one is below their number.
        var parentId = await harness.StartAsync(parent.Id);
        await using var host = WithLimits(maxOpen: 1);
        await using var db = fixture.CreateDbContext();
        var admitted = await db.CallActivityExecutions.AsNoTracking()
            .Where(call => call.ParentInstanceId == parentId)
            .ToListAsync();
        Assert.Equal(2, admitted.Count);
        foreach (var call in admitted)
        {
            await harness.ProcessAsync(await harness.SingleJobIdAsync(call.Id, WorkflowJobKinds.CallStart), host.Services);
        }

        var started = await db.CallActivityExecutions.AsNoTracking()
            .Where(call => call.ParentInstanceId == parentId)
            .ToListAsync();
        Assert.All(started, call =>
        {
            Assert.Equal(CallInvocationStates.Running, call.State);
            Assert.True(call.CapacityReserved);
            Assert.NotNull(call.ChildInstanceId);
        });

        // The next entry is refused while they hold more than the new limit allows.
        var task = await db.UserTasks.AsNoTracking().SingleAsync(item => item.InstanceId == parentId);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/user-tasks/{task.Id}/flows/1301")
        {
            Content = JsonContent.Create(new { })
        };
        ApiTestAuth.Authorize(request, "worker", "worker");
        using (var client = host.CreateClient())
        using (var moved = await client.SendAsync(request))
        {
            Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
        }

        var refused = await db.CallActivityExecutions.AsNoTracking()
            .SingleAsync(call => call.ParentInstanceId == parentId && call.ParentNodeId == 14);
        Assert.False(refused.CapacityReserved);
        Assert.Equal(CallFailureCodes.CapacityExceeded, refused.FailureCode);
        Assert.Null(refused.ChildInstanceId);
        Assert.Equal(
            2,
            await db.CallActivityExecutions.AsNoTracking()
                .CountAsync(call => call.RootInstanceId == parentId && call.CapacityReserved));

        // Once the admitted calls settle, the blocked one gets in.
        foreach (var call in started)
        {
            using var acted = await harness.TakeChildActionAsync(call.ChildInstanceId!.Value);
            Assert.True(acted.IsSuccessStatusCode, await acted.Content.ReadAsStringAsync());
            await harness.ProcessAsync(await harness.SingleJobIdAsync(call.Id, WorkflowJobKinds.CallResume), host.Services);
        }

        var jobId = await harness.SingleJobIdAsync(refused.Id, WorkflowJobKinds.CallStart);
        var incident = await harness.OpenIncidentAsync(jobId);
        await harness.RetryIncidentAsync(incident!.Id);
        await harness.ProcessAsync(jobId, host.Services);

        var retried = await db.CallActivityExecutions.AsNoTracking().SingleAsync(call => call.Id == refused.Id);
        Assert.Equal(CallInvocationStates.Running, retried.State);
        Assert.True(retried.CapacityReserved);
    }

    [Fact]
    public async Task Mutually_recursive_families_published_in_stages_stop_at_the_depth_limit()
    {
        await using var host = WithLimits(maxDepth: 3);
        var firstKey = Key("first");
        var secondKey = Key("second");
        await harness.PublishAsync(ImmediateChild(firstKey));
        await harness.PublishAsync(Parent(secondKey, firstKey));
        var recursive = await harness.PublishAsync(Parent(firstKey, secondKey));
        using (var promoted = await harness.SendAsync(HttpMethod.Post, $"/api/workflows/{recursive.Id}/set-default"))
        {
            Assert.True(promoted.IsSuccessStatusCode, await promoted.Content.ReadAsStringAsync());
        }

        var root = await harness.PublishAsync(Parent(Key("root"), firstKey));
        var rootId = await harness.StartAsync(root.Id);
        await harness.DrainAsync(rootId, services: host.Services);

        await using var db = fixture.CreateDbContext();
        var blocked = await db.CallActivityExecutions.AsNoTracking()
            .Where(call => call.RootInstanceId == rootId)
            .OrderByDescending(call => call.ChildDepth)
            .FirstAsync();
        Assert.Equal(4, blocked.ChildDepth);
        Assert.Equal(CallFailureCodes.DepthExceeded, blocked.FailureCode);
        Assert.Equal(3, await db.WorkflowInstances.AsNoTracking().CountAsync(instance => instance.CallRootInstanceId == rootId));
    }

    [Fact]
    public async Task Rejected_entries_take_no_slot_and_a_freed_slot_lets_one_retry_in()
    {
        await using var host = WithLimits(maxOpen: 1);
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
                CallNode(11, childKey),
                CallNode(12, childKey),
                CallNode(13, childKey),
                new FlowNodeModel { Id = 14, Name = "Join", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 15, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 11 },
                new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 12 },
                new SequenceFlowModel { Id = 1003, SourceRef = 10, TargetRef = 13 },
                new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 14 },
                new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = 14 },
                new SequenceFlowModel { Id = 1301, SourceRef = 13, TargetRef = 14 },
                new SequenceFlowModel { Id = 1401, SourceRef = 14, TargetRef = 15 }
            ]
        };
        var parent = await harness.PublishAsync(model);

        // The start route uses the host's limits, so its own reservations count at once.
        long parentId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var started = await scope.ServiceProvider
                .GetRequiredService<Flowbit.Service.Abstractions.IWorkflowEngineService>()
                .StartInstanceSlimAsync(
                    parent.Id,
                    null,
                    new Flowbit.Service.Abstractions.ActorContext("starter", [], new Dictionary<string, string>()),
                    null,
                    null,
                    new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                    CancellationToken.None);
            parentId = started.Id;
        }

        await using var db = fixture.CreateDbContext();
        var calls = await db.CallActivityExecutions.AsNoTracking()
            .Where(call => call.ParentInstanceId == parentId)
            .OrderBy(call => call.Id)
            .ToListAsync();
        Assert.Equal(3, calls.Count);
        Assert.Equal(1, calls.Count(call => call.CapacityReserved));
        Assert.Equal(2, calls.Count(call => !call.CapacityReserved && call.FailureCode == CallFailureCodes.CapacityExceeded));

        var admitted = calls.Single(call => call.CapacityReserved);
        var blocked = calls.First(call => !call.CapacityReserved);
        await harness.ProcessCallJobAsync(admitted.Id, WorkflowJobKinds.CallStart);
        var blockedJob = await harness.SingleJobIdAsync(blocked.Id, WorkflowJobKinds.CallStart);
        var incident = await harness.OpenIncidentAsync(blockedJob);
        Assert.Equal(CallFailureCodes.CapacityExceeded, incident!.Type);

        // While the slot is held, a retry stays blocked and takes nothing.
        await harness.RetryIncidentAsync(incident.Id);
        await harness.ProcessAsync(blockedJob, host.Services);
        var stillBlocked = await db.CallActivityExecutions.AsNoTracking().SingleAsync(call => call.Id == blocked.Id);
        Assert.False(stillBlocked.CapacityReserved);
        Assert.Null(stillBlocked.ChildInstanceId);
        var second = await harness.OpenIncidentAsync(blockedJob);
        Assert.NotNull(second);

        // Finishing the admitted child frees its slot when the result is delivered.
        var running = await db.CallActivityExecutions.AsNoTracking().SingleAsync(call => call.Id == admitted.Id);
        using (var acted = await harness.TakeChildActionAsync(running.ChildInstanceId!.Value))
        {
            Assert.True(acted.IsSuccessStatusCode, await acted.Content.ReadAsStringAsync());
        }
        await harness.ProcessCallJobAsync(admitted.Id, WorkflowJobKinds.CallResume);
        var released = await db.CallActivityExecutions.AsNoTracking().SingleAsync(call => call.Id == admitted.Id);
        Assert.False(released.CapacityReserved);

        await harness.RetryIncidentAsync(second!.Id);
        await harness.ProcessAsync(blockedJob, host.Services);
        var retried = await db.CallActivityExecutions.AsNoTracking().SingleAsync(call => call.Id == blocked.Id);
        Assert.Equal(CallInvocationStates.Running, retried.State);
        Assert.True(retried.CapacityReserved);
    }

    [Fact]
    public async Task An_admitted_call_retries_with_its_own_slot_even_at_a_limit_of_one()
    {
        await using var host = WithLimits(maxOpen: 1);
        var childKey = Key("child");
        var child = await harness.PublishAsync(ImmediateChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        var invocation = await harness.InvocationAsync(parentId);
        Assert.True(invocation.CapacityReserved);

        await harness.RemoveDefaultAsync(childKey);
        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallStart);
        await harness.ProcessAsync(jobId, host.Services);
        var incident = await harness.OpenIncidentAsync(jobId);
        Assert.Equal(CallFailureCodes.TargetUnavailable, incident!.Type);
        Assert.True((await harness.InvocationAsync(parentId)).CapacityReserved);

        await harness.RestoreDefaultAsync(child.Id);
        await harness.RetryIncidentAsync(incident.Id);
        await harness.ProcessAsync(jobId, host.Services);

        var started = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Running, started.State);
        Assert.NotNull(started.ChildInstanceId);
    }

    [Fact]
    public async Task A_child_entering_its_own_call_counts_the_reservations_of_its_transaction()
    {
        await using var host = WithLimits(maxOpen: 1);
        var leafKey = Key("leaf");
        await harness.PublishAsync(ImmediateChild(leafKey));
        var middleKey = Key("middle");
        await harness.PublishAsync(Parent(middleKey, leafKey));
        var parent = await harness.PublishAsync(Parent(Key("top"), middleKey));
        var rootId = await harness.StartAsync(parent.Id);
        var top = await harness.InvocationAsync(rootId);

        // Creating the middle child enters its call in the same transaction that holds
        // the top call's reservation, which fills the root's only slot.
        await harness.ProcessAsync(await harness.SingleJobIdAsync(top.Id, WorkflowJobKinds.CallStart), host.Services);

        var middleInstance = (await harness.InvocationAsync(rootId)).ChildInstanceId!.Value;
        var nested = await harness.InvocationAsync(middleInstance);
        Assert.False(nested.CapacityReserved);
        Assert.Equal(CallFailureCodes.CapacityExceeded, nested.FailureCode);
    }

    [Fact]
    public async Task A_burst_of_concurrent_admissions_stays_within_the_stated_soft_bound()
    {
        const int limit = 4;
        const int observed = 3;
        const int concurrency = 3;
        await using var host = WithLimits(maxOpen: limit);
        var leafKey = Key("leaf");
        await harness.PublishAsync(WaitingChild(leafKey));
        var childKey = Key("child");
        await harness.PublishAsync(Parent(childKey, leafKey));
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
                CallNode(11, childKey),
                CallNode(12, childKey),
                CallNode(13, childKey),
                new FlowNodeModel { Id = 14, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 11 },
                new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 12 },
                new SequenceFlowModel { Id = 1003, SourceRef = 10, TargetRef = 13 },
                new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 14 },
                new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = 14 },
                new SequenceFlowModel { Id = 1301, SourceRef = 13, TargetRef = 14 }
            ]
        };
        var parent = await harness.PublishAsync(model);
        var parentId = await harness.StartAsync(parent.Id);
        await using var db = fixture.CreateDbContext();
        var calls = await db.CallActivityExecutions.AsNoTracking()
            .Where(call => call.ParentInstanceId == parentId)
            .Select(call => call.Id)
            .ToListAsync();
        Assert.Equal(observed, calls.Count);

        // Each creation transaction enters its child's nested call, seeing the same three
        // reservations unless another has already committed.
        var leases = new List<WorkflowJobLeaseRecord>();
        foreach (var invocationId in calls)
        {
            leases.Add(await harness.LeaseAsync(await harness.SingleJobIdAsync(invocationId, WorkflowJobKinds.CallStart)));
        }

        await Task.WhenAll(leases.Select(lease => harness.ProcessLeaseAsync(lease, host.Services)));

        var reserved = await db.CallActivityExecutions.AsNoTracking()
            .CountAsync(call => call.RootInstanceId == parentId && call.CapacityReserved);
        Assert.InRange(reserved, limit, observed + concurrency * (limit - observed));

        // At or past the limit, the next entry is refused whatever raced before it.
        Assert.Equal(
            6 - reserved,
            await db.CallActivityExecutions.AsNoTracking().CountAsync(call =>
                call.RootInstanceId == parentId
                && !call.CapacityReserved
                && call.FailureCode == CallFailureCodes.CapacityExceeded));
    }

    [Fact]
    public async Task A_loop_through_an_immediate_child_stops_at_the_automatic_activation_guard_and_retry_restarts_it()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(childKey));
        var key = Key("parent");
        var model = Parent(key, childKey);
        model.Variables = [Process("rounds", WorkflowVariableTypes.Number, nullable: false, defaultValue: 0)];
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 6,
            Name = "Again?",
            Type = BpmnFlowNodeTypes.ExclusiveGateway
        });
        model.FlowNodes.Add(new FlowNodeModel { Id = 8, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent });
        model.FlowNodes.RemoveAll(node => node.Id == End);
        model.SequenceFlows.RemoveAll(flow => flow.Id == 201);
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 201, SourceRef = Call, TargetRef = 6 });
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 601,
            SourceRef = 6,
            TargetRef = Call,
            Condition = "rounds == 0",
            ConditionPriority = 1
        });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 602, SourceRef = 6, TargetRef = 8, IsDefault = true });
        var parent = await harness.PublishAsync(model);
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<Flowbit.Service.Abstractions.IEngineSettingsRepository>();
            await settings.SetAsync(
                WorkflowAutomaticActivationGuard.SettingKey,
                "5",
                "settings-test", CancellationToken.None);
        }

        var parentId = await harness.StartAsync(parent.Id);

        await using (var db = fixture.CreateDbContext())
        {
            await harness.DrainAsync(parentId, maxJobs: 100);
            var incident = await db.WorkflowIncidents.AsNoTracking()
                .SingleAsync(item => item.InstanceId == parentId && item.Status == WorkflowIncidentStatuses.Open);
            Assert.Equal(WorkflowIncidentTypes.AutomaticLoopLimit, incident.Type);

            var visits = await db.CallActivityExecutions.AsNoTracking()
                .CountAsync(call => call.ParentInstanceId == parentId);
            Assert.True(visits > 1);

            await harness.RetryIncidentAsync(incident.Id);
            var job = await db.WorkflowJobs.AsNoTracking().SingleAsync(item => item.Id == incident.JobId);
            await harness.ProcessAsync(job.Id);
            await using var after = fixture.CreateDbContext();
            Assert.True(await after.CallActivityExecutions.AsNoTracking()
                .CountAsync(call => call.ParentInstanceId == parentId) >= visits);
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<Flowbit.Service.Abstractions.IEngineSettingsRepository>();
            await settings.DeleteAsync(WorkflowAutomaticActivationGuard.SettingKey, "settings-test", CancellationToken.None);
        }

        using var cleanup = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/cancel");
        Assert.True(cleanup.IsSuccessStatusCode);
    }

    // An entry a boundary catches creates no job, but it still counts: the guard's rule is about
    // entries, and otherwise a loop of failing calls is bounded only per transaction.
    [Fact]
    public async Task A_caught_entry_counts_against_the_guard_and_its_retry_reaches_the_boundary()
    {
        var parent = await harness.PublishAsync(await TwoCallsThatFailOnEntryAsync());
        await SetAutomaticActivationLimitAsync("1");
        try
        {
            var parentId = await harness.StartAsync(parent.Id);

            // The first entry used the allowance, so the second pauses before it can fail.
            Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);
            var calls = (await harness.CallsAsync(parentId)).Items;
            Assert.Equal(2, calls.Count);
            Assert.Equal(CallInvocationStates.Queued, calls[0].State);
            Assert.False(calls[0].CapacityReserved);
            Assert.Equal(CallFailureCodes.InputInvalid, calls[0].FailureCode);
            Assert.Equal(CallInvocationStates.Failed, calls[1].State);
            Assert.Equal(1, await AutomaticActivationCountAsync(parentId));
            await using var db = fixture.CreateDbContext();
            var incident = await db.WorkflowIncidents.AsNoTracking()
                .SingleAsync(item => item.InstanceId == parentId && item.Status == WorkflowIncidentStatuses.Open);
            Assert.Equal(WorkflowIncidentTypes.AutomaticLoopLimit, incident.Type);

            await harness.RetryIncidentAsync(incident.Id);
            await harness.ProcessAsync(incident.JobId!.Value);

            var detail = await harness.DetailAsync(parentId);
            Assert.Equal(WorkflowInstanceStatuses.Completed, detail.Status);
            Assert.Equal(BoundaryEnd, detail.CurrentNodeId);
            Assert.All(
                (await harness.CallsAsync(parentId)).Items,
                call => Assert.Equal(CallInvocationStates.Failed, call.State));
            Assert.False(await db.WorkflowIncidents.AsNoTracking()
                .AnyAsync(item => item.InstanceId == parentId && item.Status == WorkflowIncidentStatuses.Open));
        }
        finally
        {
            await SetAutomaticActivationLimitAsync(null);
        }
    }

    [Fact]
    public async Task Caught_entries_within_the_guard_each_count_once_and_take_their_boundaries()
    {
        var parent = await harness.PublishAsync(await TwoCallsThatFailOnEntryAsync());
        await SetAutomaticActivationLimitAsync("2");
        try
        {
            var parentId = await harness.StartAsync(parent.Id);

            var detail = await harness.DetailAsync(parentId);
            Assert.Equal(WorkflowInstanceStatuses.Completed, detail.Status);
            Assert.Equal(BoundaryEnd, detail.CurrentNodeId);
            Assert.Equal(2, await AutomaticActivationCountAsync(parentId));
            await using var db = fixture.CreateDbContext();
            Assert.False(await db.WorkflowIncidents.AsNoTracking().AnyAsync(item => item.InstanceId == parentId));
        }
        finally
        {
            await SetAutomaticActivationLimitAsync(null);
        }
    }

    // Start -> call -> boundary -> second call -> boundary -> end, with the mapped input never supplied.
    private async Task<WorkflowModel> TwoCallsThatFailOnEntryAsync()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(childKey, startInputs: [Input("amount")]));
        var model = Parent(
            Key("parent"),
            childKey,
            withBoundary: true,
            startInputs: [Input("amount", required: false)],
            inputs: [Mapping("amount", "amount")]);
        const int secondCall = 6;
        const int secondBoundary = 7;
        var second = CallNode(secondCall, childKey);
        second.CallActivity!.InputMappings = [Mapping("amount", "amount")];
        model.FlowNodes.Add(second);
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = secondBoundary,
            Name = "Second call failed",
            Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
            AttachedToRef = secondCall
        });
        model.SequenceFlows.Single(flow => flow.SourceRef == Boundary).TargetRef = secondCall;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = secondCall, TargetRef = End });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 701, SourceRef = secondBoundary, TargetRef = BoundaryEnd });
        return model;
    }

    private async Task SetAutomaticActivationLimitAsync(string? limit)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<Flowbit.Service.Abstractions.IEngineSettingsRepository>();
        if (limit is null)
        {
            await settings.DeleteAsync(WorkflowAutomaticActivationGuard.SettingKey, "settings-test", CancellationToken.None);
        }
        else
        {
            await settings.SetAsync(WorkflowAutomaticActivationGuard.SettingKey, limit, "settings-test", CancellationToken.None);
        }
    }

    private async Task<int> AutomaticActivationCountAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.ExecutionTokens.AsNoTracking()
            .Where(token => token.InstanceId == instanceId)
            .Select(token => token.AutomaticActivationCount)
            .SingleAsync();
    }

    private static FlowNodeModel CallNode(int id, string targetKey) => new()
    {
        Id = id,
        Name = $"Call {id}",
        Type = BpmnFlowNodeTypes.CallActivity,
        CallActivity = new CallActivityModel { WorkflowKey = targetKey }
    };
}
