using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Flowbit.Tests.CallActivityHarness;

namespace Flowbit.Tests;

/// <summary>
/// What happens when work is replayed, a Worker comes back late, or both sides of a call
/// move at once. Everything runs against real transactions and several service scopes.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class CallActivityRecoveryTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => harness.RetireLeftoverJobsAsync();

    private static readonly TimeSpan RaceTimeout = TimeSpan.FromSeconds(60);
    private readonly CallActivityHarness harness = new(fixture);

    private async Task ExpireLeaseAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        await db.WorkflowJobs
            .Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                job => job.LeaseExpiresAt,
                (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-5)));
    }

    private async Task<List<long>> ChildrenOfAsync(long rootInstanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowInstances.AsNoTracking()
            .Where(instance => instance.CallRootInstanceId == rootInstanceId)
            .Select(instance => instance.Id)
            .ToListAsync();
    }

    [Fact]
    public async Task An_expired_lease_coming_back_late_cannot_start_a_second_child()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        var invocation = await harness.InvocationAsync(parentId);
        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallStart);

        var stale = await harness.LeaseAsync(jobId);
        await ExpireLeaseAsync(jobId);
        var current = await harness.LeaseAsync(jobId);

        await harness.ProcessLeaseAsync(stale);
        Assert.Null((await harness.InvocationAsync(parentId)).ChildInstanceId);

        await harness.ProcessLeaseAsync(current);
        var started = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Running, started.State);
        Assert.NotNull(started.ChildInstanceId);

        await harness.ProcessLeaseAsync(stale);
        await harness.ProcessLeaseAsync(current);
        Assert.Equal([started.ChildInstanceId!.Value], await ChildrenOfAsync(parentId));
        Assert.Equal(WorkflowJobStatuses.Completed, await harness.JobStatusAsync(jobId));
    }

    [Fact]
    public async Task Two_workers_holding_the_same_start_job_create_one_child()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        var invocation = await harness.InvocationAsync(parentId);
        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallStart);

        var stale = await harness.LeaseAsync(jobId);
        await ExpireLeaseAsync(jobId);
        var current = await harness.LeaseAsync(jobId);
        await Task.WhenAll(harness.ProcessLeaseAsync(stale), harness.ProcessLeaseAsync(current))
            .WaitAsync(RaceTimeout);

        var started = await harness.InvocationAsync(parentId);
        Assert.Equal([started.ChildInstanceId!.Value], await ChildrenOfAsync(parentId));
    }

    [Fact]
    public async Task A_result_waits_durably_for_its_job_and_is_delivered_once_however_often_it_is_replayed()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey, actionVariables: [Input("approved", WorkflowVariableTypes.Boolean)]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            variables: [Process("decision")],
            outputs: [Mapping("approved", "decision", WorkflowVariableTypes.Boolean)]));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);
        var childId = invocation.ChildInstanceId!.Value;

        using (var acted = await harness.TakeChildActionAsync(
                   childId,
                   body: new TakeFlowRequest(Values(("approved", true)))))
        {
            Assert.True(acted.IsSuccessStatusCode, await acted.Content.ReadAsStringAsync());
        }

        // The child has committed, and the parent learns of it only when the job runs.
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(childId)).Status);
        var ready = Assert.Single((await harness.CallsAsync(parentId)).Items);
        Assert.Equal("resultReady", ready.State);
        Assert.Equal("null", await harness.VariableAsync(parentId, "decision"));

        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallResume);
        var stale = await harness.LeaseAsync(jobId);
        await ExpireLeaseAsync(jobId);
        var current = await harness.LeaseAsync(jobId);
        await harness.ProcessLeaseAsync(stale);
        Assert.Equal("null", await harness.VariableAsync(parentId, "decision"));

        await harness.ProcessLeaseAsync(current);
        await harness.ProcessLeaseAsync(current);
        await harness.ProcessLeaseAsync(stale);

        var delivered = await harness.DetailAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, delivered.Status);
        Assert.Single(delivered.History, item => item.Note == InstanceHistoryNotes.CallCompleted);
        Assert.Equal("true", await harness.VariableAsync(parentId, "decision"));
        Assert.Single(delivered.Variables, variable =>
            variable.VariableName == "decision" && variable.Value.ValueKind == JsonValueKind.True);
    }

    [Fact]
    public async Task A_cancel_job_that_is_delayed_or_replayed_stops_the_child_once()
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

        // With no Worker running, the child's task stays visible and refuses actions.
        var inboxRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/instances/inbox?instanceId={childId}");
        ApiTestAuth.Authorize(inboxRequest, "approver", "approver");
        using (var inbox = await fixture.Client.SendAsync(inboxRequest))
        {
            Assert.Equal(HttpStatusCode.OK, inbox.StatusCode);
            var items = await inbox.Content.ReadFromJsonAsync<PagedResult<InboxItemDto>>();
            Assert.Single(items!.Items);
        }

        using (var action = await harness.TakeChildActionAsync(childId))
        {
            Assert.Equal(HttpStatusCode.Conflict, action.StatusCode);
        }

        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallCancel);
        var stale = await harness.LeaseAsync(jobId);
        await ExpireLeaseAsync(jobId);
        var current = await harness.LeaseAsync(jobId);
        await harness.ProcessLeaseAsync(current);
        await harness.ProcessLeaseAsync(stale);
        await harness.ProcessLeaseAsync(current);

        var child = await harness.DetailAsync(childId);
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, child.Status);
        Assert.Single(child.History, item => item.Note == InstanceHistoryNotes.CallCancelled);
    }

    [Fact]
    public async Task A_failed_boundary_write_rolls_back_and_keeps_the_result_for_recovery()
    {
        // The catalog entry's validation refuses the boundary's diagnostic text.
        const string validation = "Len(value) < 3";
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
                       JsonSerializer.SerializeToElement("no"),
                       validation)))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var childKey = Key("child");
        await harness.PublishAsync(FaultingChild(childKey));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            withBoundary: true,
            errorVariable: "callError",
            variables:
            [
                new VariableModel
                {
                    Id = 40,
                    Name = "callError",
                    Scope = VariableScopes.Shared,
                    SharedKey = sharedKey,
                    Access = SharedVariableAccessModes.ReadWrite,
                    DataType = WorkflowVariableTypes.String,
                    Nullable = true,
                    Validation = validation
                }
            ]));
        var parentId = await harness.StartAsync(parent.Id);
        var invocation = await harness.InvocationAsync(parentId);
        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallStart);
        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallResume);

        for (var attempt = 0; attempt < 8 && await harness.JobStatusAsync(jobId) != WorkflowJobStatuses.Incident; attempt++)
        {
            await harness.ProcessAsync(jobId);
        }

        Assert.Equal(WorkflowJobStatuses.Incident, await harness.JobStatusAsync(jobId));
        Assert.NotNull(await harness.OpenIncidentAsync(jobId));
        var call = Assert.Single((await harness.CallsAsync(parentId)).Items);
        Assert.Equal("resultReady", call.State);
        Assert.Equal(CallFailureCodes.ChildFaulted, call.FailureCode ?? CallFailureCodes.ChildFaulted);
        Assert.True(call.CapacityReserved);
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);
        Assert.Equal([call.ChildInstanceId!.Value], await ChildrenOfAsync(parentId));
        await using var db = fixture.CreateDbContext();
        Assert.True(await db.CallActivityResults.AnyAsync(result => result.ChildInstanceId == call.ChildInstanceId));
    }

    [Fact]
    public async Task Cancelling_a_parent_while_its_child_finishes_never_changes_the_parent()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey, actionVariables: [Input("approved", WorkflowVariableTypes.Boolean)]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            variables: [Process("decision")],
            outputs: [Mapping("approved", "decision", WorkflowVariableTypes.Boolean)]));

        for (var round = 0; round < 5; round++)
        {
            var parentId = await harness.StartAsync(parent.Id);
            await harness.DrainAsync(parentId);
            var invocation = await harness.InvocationAsync(parentId);
            var childId = invocation.ChildInstanceId!.Value;

            var cancel = harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/cancel");
            var act = harness.TakeChildActionAsync(childId, body: new TakeFlowRequest(Values(("approved", true))));
            await Task.WhenAll(cancel, act).WaitAsync(RaceTimeout);
            using var cancelled = await cancel;
            using var acted = await act;
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
            Assert.True(
                acted.IsSuccessStatusCode || acted.StatusCode == HttpStatusCode.Conflict,
                await acted.Content.ReadAsStringAsync());

            await harness.DrainAsync(parentId);

            var settled = await harness.InvocationAsync(parentId);
            Assert.Equal(CallInvocationStates.Cancelled, settled.State);
            Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(parentId)).Status);
            Assert.Equal("null", await harness.VariableAsync(parentId, "decision"));
            var child = await harness.DetailAsync(childId);
            Assert.True(
                child.Status is WorkflowInstanceStatuses.Completed or WorkflowInstanceStatuses.Cancelled,
                $"Round {round}: child ended as {child.Status}.");
        }
    }

    [Fact]
    public async Task Cancelling_a_parent_while_its_child_is_created_leaves_no_orphan()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));

        for (var round = 0; round < 5; round++)
        {
            var parentId = await harness.StartAsync(parent.Id);
            var invocation = await harness.InvocationAsync(parentId);
            var lease = await harness.LeaseAsync(await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallStart));

            var start = harness.ProcessLeaseAsync(lease);
            var cancel = harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/cancel");
            await Task.WhenAll(start, cancel).WaitAsync(RaceTimeout);
            using var cancelled = await cancel;
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
            await harness.DrainAsync(parentId);

            var settled = await harness.InvocationAsync(parentId);
            Assert.Equal(CallInvocationStates.Cancelled, settled.State);
            var children = await ChildrenOfAsync(parentId);
            Assert.True(children.Count <= 1);
            if (settled.ChildInstanceId is long childId)
            {
                Assert.Equal([childId], children);
                Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(childId)).Status);
            }
            else
            {
                Assert.Empty(children);
            }
        }
    }

    [Fact]
    public async Task A_cancel_racing_with_result_delivery_has_exactly_one_winner()
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(childKey, variables: [Process("approved", defaultValue: true, nullable: false)]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            variables: [Process("decision")],
            outputs: [Mapping("approved", "decision", WorkflowVariableTypes.Boolean)]));

        for (var round = 0; round < 5; round++)
        {
            var parentId = await harness.StartAsync(parent.Id);
            var invocation = await harness.InvocationAsync(parentId);
            await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallStart);
            var lease = await harness.LeaseAsync(await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallResume));

            var deliver = harness.ProcessLeaseAsync(lease);
            var cancel = harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/cancel");
            await Task.WhenAll(deliver, cancel).WaitAsync(RaceTimeout);
            (await cancel).Dispose();
            await harness.DrainAsync(parentId);

            var settled = await harness.InvocationAsync(parentId);
            var after = await harness.DetailAsync(parentId);
            if (settled.State == CallInvocationStates.Completed)
            {
                Assert.Equal(WorkflowInstanceStatuses.Completed, after.Status);
                Assert.Equal("true", await harness.VariableAsync(parentId, "decision"));
            }
            else
            {
                Assert.Equal(CallInvocationStates.Cancelled, settled.State);
                Assert.Equal(WorkflowInstanceStatuses.Cancelled, after.Status);
                Assert.Equal("null", await harness.VariableAsync(parentId, "decision"));
            }

            // A completed child stays completed whichever side won.
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await harness.DetailAsync(settled.ChildInstanceId!.Value)).Status);
        }
    }

    [Fact]
    public async Task A_version_change_racing_with_call_entry_ends_one_consistent_way()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var key = Key("parent");
        WorkflowModel Model()
        {
            var model = Parent(key, childKey);
            model.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = "Before", Type = BpmnFlowNodeTypes.UserTask });
            model.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 6;
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = Call, Name = "Go" });
            return model;
        }

        var source = await harness.PublishAsync(Model());
        var target = await harness.PublishAsync(Model());

        for (var round = 0; round < 5; round++)
        {
            var parentId = await harness.StartAsync(source.Id);
            var before = await harness.DetailAsync(parentId);

            var enter = harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/flows/601", new { });
            var change = harness.SendAsync(
                HttpMethod.Post,
                $"/api/instances/{parentId}/version-change",
                new ChangeInstanceVersionRequest(target.Id, source.Id, before.UpdatedAt, "Race with a call entry"));
            await Task.WhenAll(enter, change).WaitAsync(RaceTimeout);
            using var entered = await enter;
            using var changed = await change;
            Assert.True(entered.IsSuccessStatusCode, await entered.Content.ReadAsStringAsync());

            // The visit records the definition the parent ran when it entered, whichever went first.
            var expected = changed.IsSuccessStatusCode ? target.Id : source.Id;
            Assert.Equal(expected, (await harness.DetailAsync(parentId)).Workflow.Id);
            Assert.Equal(expected, (await harness.InvocationAsync(parentId)).ParentDefinitionId);
        }
    }

    [Fact]
    public async Task Parent_and_child_writers_of_one_shared_variable_never_deadlock()
    {
        var sharedKey = $"tests.call.{Guid.NewGuid():N}";
        using (var created = await harness.SendAsync(
                   HttpMethod.Post,
                   "/api/shared-variables",
                   new CreateSharedVariableRequest(
                       sharedKey,
                       WorkflowVariableTypes.Number,
                       IsArray: false,
                       Nullable: false,
                       HasValue: true,
                       JsonSerializer.SerializeToElement(0),
                       null)))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        VariableModel Alias() => new()
        {
            Id = 77,
            Name = "total",
            Scope = VariableScopes.Shared,
            SharedKey = sharedKey,
            Access = "readWrite",
            DataType = WorkflowVariableTypes.Number
        };

        FlowNodeModel Writer(int id) => new()
        {
            Id = id,
            Name = "Write total",
            Type = BpmnFlowNodeTypes.ScriptTask,
            ScriptFormat = ScriptFormats.NCalc,
            Assignments = [new AssignmentModel { Variable = "total", Expression = "total + 1" }]
        };

        var childKey = Key("child");
        var childModel = WaitingChild(childKey, variables: [Alias()]);
        childModel.FlowNodes.Add(Writer(9));
        childModel.SequenceFlows.Single(flow => flow.Id == ChildFlow).TargetRef = 9;
        childModel.SequenceFlows.Add(new SequenceFlowModel { Id = 901, SourceRef = 9, TargetRef = End });
        await harness.PublishAsync(childModel);

        var key = Key("parent");
        var parentModel = new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = Start,
            Variables = [Alias()],
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
                new FlowNodeModel { Id = 12, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                Writer(13),
                new FlowNodeModel { Id = 14, Name = "End call", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 15, Name = "End review", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 11 },
                new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 12 },
                new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 14 },
                new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = 13, Name = "Done" },
                new SequenceFlowModel { Id = 1301, SourceRef = 13, TargetRef = 15 }
            ]
        };
        var parent = await harness.PublishAsync(parentModel);

        for (var round = 0; round < 4; round++)
        {
            var parentId = await harness.StartAsync(parent.Id);
            var invocation = await harness.InvocationAsync(parentId);
            await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallStart);
            var childId = (await harness.InvocationAsync(parentId)).ChildInstanceId!.Value;

            var parentAction = harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/flows/1201", new { });
            var childAction = harness.TakeChildActionAsync(childId);
            await Task.WhenAll(parentAction, childAction).WaitAsync(RaceTimeout);
            using var parentActed = await parentAction;
            using var childActed = await childAction;
            Assert.True(parentActed.IsSuccessStatusCode, await parentActed.Content.ReadAsStringAsync());
            Assert.True(childActed.IsSuccessStatusCode, await childActed.Content.ReadAsStringAsync());

            await harness.DrainAsync(parentId);
            Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
            Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(childId)).Status);
        }

        await using var db = fixture.CreateDbContext();
        var current = await db.SharedVariableCurrentValues.AsNoTracking()
            .Where(value => value.SharedVariable.Key == sharedKey)
            .SingleAsync();
        Assert.Equal(8, current.ValueJson!.RootElement.GetInt32());
    }

    [Fact]
    public async Task A_parent_at_its_job_cap_can_still_finish_a_call_and_be_cancelled()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));

        // Two far-off timer waits, then a cap of two: nothing new can be enqueued on this parent.
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
                new FlowNodeModel
                {
                    Id = 12,
                    Name = "Later",
                    Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent,
                    Timer = new TimerDefinitionModel { TimeDuration = "P30D" }
                },
                new FlowNodeModel
                {
                    Id = 13,
                    Name = "Much later",
                    Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent,
                    Timer = new TimerDefinitionModel { TimeDuration = "P60D" }
                },
                new FlowNodeModel { Id = 14, Name = "End call", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 15, Name = "End later", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 16, Name = "End much later", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 11 },
                new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 12 },
                new SequenceFlowModel { Id = 1003, SourceRef = 10, TargetRef = 13 },
                new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 14 },
                new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = 15 },
                new SequenceFlowModel { Id = 1301, SourceRef = 13, TargetRef = 16 }
            ]
        };
        var parent = await harness.PublishAsync(model);
        var finishing = await harness.StartAsync(parent.Id);
        var cancelling = await harness.StartAsync(parent.Id);
        var finishingCall = await harness.InvocationAsync(finishing);
        var cancellingCall = await harness.InvocationAsync(cancelling);
        await harness.ProcessCallJobAsync(finishingCall.Id, WorkflowJobKinds.CallStart);
        await harness.ProcessCallJobAsync(cancellingCall.Id, WorkflowJobKinds.CallStart);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<IEngineSettingsRepository>();
        var original = await settings.GetByKeyAsync("Workflow.MultiInstance.MaxInstances", CancellationToken.None);
        await settings.SetAsync("Workflow.MultiInstance.MaxInstances", "1", "settings-test", CancellationToken.None);
        try
        {
            var finishingChild = (await harness.InvocationAsync(finishing)).ChildInstanceId!.Value;
            using (var acted = await harness.TakeChildActionAsync(finishingChild))
            {
                Assert.True(acted.IsSuccessStatusCode, await acted.Content.ReadAsStringAsync());
            }

            await harness.ProcessCallJobAsync(finishingCall.Id, WorkflowJobKinds.CallResume);
            Assert.Equal(CallInvocationStates.Completed, (await harness.InvocationAsync(finishing)).State);

            using var cancelled = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{cancelling}/cancel");
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
            await harness.ProcessCallJobAsync(cancellingCall.Id, WorkflowJobKinds.CallCancel);
            var cancellingChild = (await harness.InvocationAsync(cancelling)).ChildInstanceId!.Value;
            Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(cancellingChild)).Status);
        }
        finally
        {
            await settings.SetAsync("Workflow.MultiInstance.MaxInstances", original?.Value ?? "1000", "settings-test", CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_join_cancellation_abandons_only_the_call_on_the_cancelled_branch()
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
                new FlowNodeModel { Id = 12, Name = "Decide", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 13,
                    Name = "First wins",
                    Type = BpmnFlowNodeTypes.ExclusiveGateway,
                    JoinCancellation = new JoinCancellationModel { GatewayRef = 10 }
                },
                new FlowNodeModel { Id = 14, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 11 },
                new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 12 },
                new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 13 },
                new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = 13, Name = "Skip" },
                new SequenceFlowModel { Id = 1301, SourceRef = 13, TargetRef = 14 }
            ]
        };
        var parent = await harness.PublishAsync(model);
        var parentId = await harness.StartAsync(parent.Id);
        var invocation = await harness.InvocationAsync(parentId);
        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallStart);

        using (var acted = await harness.SendAsync(HttpMethod.Post, $"/api/instances/{parentId}/flows/1201", new { }))
        {
            Assert.True(acted.IsSuccessStatusCode, await acted.Content.ReadAsStringAsync());
        }

        var settled = await harness.InvocationAsync(parentId);
        Assert.Equal(CallInvocationStates.Cancelled, settled.State);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallCancel);
        Assert.Equal(
            WorkflowInstanceStatuses.Cancelled,
            (await harness.DetailAsync(settled.ChildInstanceId!.Value)).Status);
    }

    [Theory]
    [InlineData(WorkflowVariableTypes.String, false, "\"text\"")]
    [InlineData(WorkflowVariableTypes.Number, false, "12.5")]
    [InlineData(WorkflowVariableTypes.Boolean, false, "true")]
    [InlineData(WorkflowVariableTypes.Date, false, "\"2026-11-01\"")]
    [InlineData(WorkflowVariableTypes.DateTime, false, "\"2026-11-01T10:00:00+00:00\"")]
    [InlineData(WorkflowVariableTypes.Json, false, "{\"nested\":[1,2]}")]
    [InlineData(WorkflowVariableTypes.String, true, "[\"a\",\"b\"]")]
    [InlineData(WorkflowVariableTypes.Number, true, "[1,2]")]
    [InlineData(WorkflowVariableTypes.Boolean, true, "[true,false]")]
    [InlineData(WorkflowVariableTypes.Date, true, "[\"2026-11-01\"]")]
    [InlineData(WorkflowVariableTypes.DateTime, true, "[\"2026-11-01T10:00:00+00:00\"]")]
    [InlineData(WorkflowVariableTypes.Json, true, "[{\"a\":1},2]")]
    public async Task Every_supported_type_round_trips_through_a_call(string dataType, bool isArray, string json)
    {
        var childKey = Key("child");
        await harness.PublishAsync(ImmediateChild(childKey, startInputs: [Input("value", dataType, isArray: isArray)]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            startInputs: [Input("source", dataType, isArray: isArray)],
            variables:
            [
                new VariableModel
                {
                    Id = 5,
                    Name = "copy",
                    DataType = dataType,
                    IsArray = isArray,
                    Nullable = true
                }
            ],
            inputs: [Mapping("source", "value", dataType, isArray: isArray)],
            outputs: [Mapping("value", "copy", dataType, isArray: isArray)]));

        using var parsed = JsonDocument.Parse(json);
        var parentId = await harness.StartAsync(
            parent.Id,
            new Dictionary<string, JsonElement> { ["source"] = parsed.RootElement.Clone() });
        await harness.DrainAsync(parentId);

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
        using var copied = JsonDocument.Parse((await harness.VariableAsync(parentId, "copy"))!);
        Assert.True(
            JsonElement.DeepEquals(parsed.RootElement, copied.RootElement),
            $"Expected {json} but found {copied.RootElement.GetRawText()}.");
    }

    [Fact]
    public async Task A_childs_defaults_and_validation_apply_to_the_inputs_it_is_given()
    {
        var childKey = Key("child");
        var amount = Input("amount");
        amount.Validation = "amount > 10";
        var note = Input("note", WorkflowVariableTypes.String, required: false);
        note.DefaultValue = JsonSerializer.SerializeToElement("none");
        await harness.PublishAsync(WaitingChild(childKey, startInputs: [amount, note]));
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            withBoundary: true,
            startInputs: [Input("amount")],
            inputs: [Mapping("amount", "amount")]));

        var accepted = await harness.StartAsync(parent.Id, Values(("amount", 50)));
        await harness.DrainAsync(accepted);
        var child = (await harness.InvocationAsync(accepted)).ChildInstanceId!.Value;
        Assert.Equal("\"none\"", await harness.VariableAsync(child, "note"));
        Assert.Equal("50", await harness.VariableAsync(child, "amount"));

        var refused = await harness.StartAsync(parent.Id, Values(("amount", 5)));
        await harness.DrainAsync(refused);
        var failed = Assert.Single((await harness.CallsAsync(refused)).Items);
        Assert.Equal(CallInvocationStates.Failed, failed.State);
        Assert.Equal(CallFailureCodes.StartFailed, failed.FailureCode);
        Assert.Null(failed.ChildInstanceId);
        Assert.Empty(await ChildrenOfAsync(refused));
    }

    [Fact]
    public async Task A_target_that_stopped_being_ready_fails_the_call_when_it_tries_to_start()
    {
        // Saved directly, because the API refuses a shared REST task without asyncBefore.
        // Such a definition can predate the rule, and the call must not run it.
        var childKey = Key("child");
        var child = await harness.PublishAsync(ImmediateChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey, withBoundary: true));
        var unsafeChild = ImmediateChild(childKey);
        unsafeChild.Variables.Add(new VariableModel
        {
            Id = 8,
            Name = "rate",
            Scope = VariableScopes.Shared,
            SharedKey = $"tests.call.{Guid.NewGuid():N}",
            Access = "read",
            DataType = WorkflowVariableTypes.Number,
            Nullable = true
        });
        unsafeChild.FlowNodes.Add(new FlowNodeModel
        {
            Id = 9,
            Name = "Send",
            Type = BpmnFlowNodeTypes.ServiceTask,
            Service = new ServiceTaskModel
            {
                Type = "rest",
                Method = "GET",
                Url = "http://example.test/${rate}",
                TimeoutSeconds = 5
            }
        });
        unsafeChild.SequenceFlows.Single().TargetRef = 9;
        unsafeChild.SequenceFlows.Add(new SequenceFlowModel { Id = 901, SourceRef = 9, TargetRef = End });
        await using (var db = fixture.CreateDbContext())
        {
            await db.WorkflowDefinitions
                .Where(definition => definition.Id == child.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(definition => definition.Definition, unsafeChild));
        }

        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);

        var failed = Assert.Single((await harness.CallsAsync(parentId)).Items);
        Assert.Equal(CallInvocationStates.Failed, failed.State);
        Assert.Equal(CallFailureCodes.TargetIncompatible, failed.FailureCode);
        Assert.Contains("is not ready", failed.FailureDescription);
        Assert.Empty(await ChildrenOfAsync(parentId));
    }

    [Fact]
    public void Only_timer_and_conditional_hosts_cancel_their_jobs_outside_the_abandon_hook()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Flowbit.slnx"))
            && !File.Exists(Path.Combine(directory.FullName, "Flowbit", "Flowbit.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var solutionDirectory = File.Exists(Path.Combine(directory.FullName, "Flowbit.slnx"))
            ? directory.FullName
            : Path.Combine(directory.FullName, "Flowbit");
        var services = Path.Combine(solutionDirectory, "src", "Flowbit.Service", "Services");
        var reasons = Directory.EnumerateFiles(services, "WorkflowEngineService*.cs")
            .SelectMany(file => Regex.Matches(
                File.ReadAllText(file),
                @"CancelOtherJobsByTokenIdsAsync\(\s*[^,]+,\s*[^,]+,\s*[^,]+,\s*(?<reason>[^,\s]+)\s*,"))
            .Select(match => match.Groups["reason"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // "reason" is CancelDurableWorkForTokensAsync, the hook itself. A new caller on a
        // token that can rest at a call has to go through that hook instead.
        Assert.Equal(
            new[]
            {
                "\"conditionalWakeCompleted\"",
                "\"interruptingConditionalBoundary\"",
                "\"interruptingTimerBoundary\"",
                "\"timerCatchCompleted\"",
                "reason"
            },
            reasons);
    }
}
