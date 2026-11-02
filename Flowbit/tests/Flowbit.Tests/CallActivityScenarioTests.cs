using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Flowbit.Tests.CallActivityHarness;

namespace Flowbit.Tests;

/// <summary>
/// Calls in the situations around them: a child resting on each kind of wait, a child's own
/// incident, a parent stopped by something other than a cancel, and what reads and previews
/// report meanwhile.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class CallActivityScenarioTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => harness.RetireLeftoverJobsAsync();

    private readonly CallActivityHarness harness = new(fixture);

    [Fact]
    public async Task A_child_waiting_on_a_message_finishes_through_its_client_and_the_parent_continues_as_the_system_actor()
    {
        var childKey = Key("child");
        await harness.PublishAsync(new WorkflowModel
        {
            Id = childKey,
            Name = childKey,
            InitialEventId = Start,
            Variables = [Process("startedBy", WorkflowVariableTypes.String, nullable: false, defaultValue: "${sys.user}")],
            FlowNodes =
            [
                new FlowNodeModel { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = ChildTask,
                    Name = "Wait for the callback",
                    Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
                    Message = new MessageCatchModel
                    {
                        ClientId = "tests-client",
                        ClientSecret = "tests-secret",
                        HeaderName = "X-Correlation",
                        HeaderValue = "accepted",
                        OutputMappings =
                        [
                            new MessageOutputMappingModel
                            {
                                Variable = "status",
                                Path = "status",
                                DataType = WorkflowVariableTypes.String,
                                Required = true
                            }
                        ]
                    }
                },
                new FlowNodeModel { Id = End, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = ChildTask },
                new SequenceFlowModel { Id = ChildFlow, SourceRef = ChildTask, TargetRef = End }
            ]
        });
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            variables: [Process("childStatus", WorkflowVariableTypes.String), Process("childStarter", WorkflowVariableTypes.String)],
            outputs:
            [
                Mapping("status", "childStatus", WorkflowVariableTypes.String),
                Mapping("startedBy", "childStarter", WorkflowVariableTypes.String)
            ]));

        var parentId = await harness.StartAsync(parent.Id, user: "requester");
        await harness.DrainAsync(parentId);
        var childId = (await harness.InvocationAsync(parentId)).ChildInstanceId!.Value;
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(childId)).Status);
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);

        var delivery = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{childId}/message")
        {
            Content = JsonContent.Create(new { status = "accepted" })
        };
        delivery.Headers.Add("X-Client-Id", "tests-client");
        delivery.Headers.Add("X-Client-Secret", "tests-secret");
        delivery.Headers.Add("X-Correlation", "accepted");
        using (var delivered = await fixture.Client.SendAsync(delivery))
        {
            Assert.True(delivered.StatusCode == HttpStatusCode.OK, await delivered.Content.ReadAsStringAsync());
        }

        var child = await harness.DetailAsync(childId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, child.Status);
        Assert.Contains(child.History, item => item.PerformedBy == "tests-client");

        await harness.DrainAsync(parentId);

        // The message client finished the child, yet it never reaches the parent. Inside the
        // child, the starter expression sees the system actor rather than the requester.
        var finished = await harness.DetailAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, finished.Status);
        var completed = Assert.Single(finished.History, item => item.Note == InstanceHistoryNotes.CallCompleted);
        Assert.Equal("callActivity", completed.PerformedBy);
        Assert.Equal("\"accepted\"", await harness.VariableAsync(parentId, "childStatus"));
        Assert.Equal("\"callActivity\"", await harness.VariableAsync(parentId, "childStarter"));
    }

    [Fact]
    public async Task A_child_waiting_on_a_timer_holds_the_call_until_the_timer_fires()
    {
        var childKey = Key("child");
        await harness.PublishAsync(new WorkflowModel
        {
            Id = childKey,
            Name = childKey,
            InitialEventId = Start,
            FlowNodes =
            [
                new FlowNodeModel { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = ChildTask,
                    Name = "Wait an hour",
                    Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent,
                    Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
                },
                new FlowNodeModel { Id = End, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = ChildTask },
                new SequenceFlowModel { Id = ChildFlow, SourceRef = ChildTask, TargetRef = End }
            ]
        });
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        var invocation = await harness.InvocationAsync(parentId);
        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallStart);
        invocation = await harness.InvocationAsync(parentId);
        var childId = invocation.ChildInstanceId!.Value;

        Assert.Equal(CallInvocationStates.Running, invocation.State);
        Assert.False(await HasResultAsync(childId));
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);

        await harness.ProcessAsync(await ChildJobAsync(childId, WorkflowJobKinds.Timer));

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(childId)).Status);
        await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallResume);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
    }

    [Fact]
    public async Task A_child_waiting_on_a_condition_holds_the_call_until_the_condition_holds()
    {
        var childKey = Key("child");
        await harness.PublishAsync(new WorkflowModel
        {
            Id = childKey,
            Name = childKey,
            InitialEventId = Start,
            Variables = [Process("approved", nullable: false, defaultValue: false)],
            FlowNodes =
            [
                new FlowNodeModel { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = ChildTask,
                    Name = "Wait for approval",
                    Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                    Conditional = new ConditionalDefinitionModel { Condition = "approved == true" }
                },
                new FlowNodeModel { Id = End, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = ChildTask },
                new SequenceFlowModel { Id = ChildFlow, SourceRef = ChildTask, TargetRef = End }
            ]
        });
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            variables: [Process("managerApproved")],
            outputs: [Mapping("approved", "managerApproved", WorkflowVariableTypes.Boolean)]));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var childId = (await harness.InvocationAsync(parentId)).ChildInstanceId!.Value;
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);

        using (var updated = await harness.SendAsync(
                   HttpMethod.Patch,
                   $"/api/instances/{childId}/variables",
                   new UpdateInstanceVariablesRequest(
                       [new InstanceVariableWriteDto("approved", JsonSerializer.SerializeToElement(true))],
                       "Approve outside the workflow",
                       $"call-condition-{Guid.NewGuid():N}")))
        {
            Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        }

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(childId)).Status);
        await harness.DrainAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
        Assert.Equal("true", await harness.VariableAsync(parentId, "managerApproved"));
    }

    [Fact]
    public async Task A_child_with_parallel_branches_reports_only_after_its_last_branch_ends()
    {
        var childKey = Key("child");
        await harness.PublishAsync(new WorkflowModel
        {
            Id = childKey,
            Name = childKey,
            InitialEventId = Start,
            Variables = [Process("approved", nullable: false, defaultValue: true)],
            FlowNodes =
            [
                new FlowNodeModel { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 10, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 11, Name = "First", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 12, Name = "Second", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 13, Name = "First end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 14, Name = "Second end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 11 },
                new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 12 },
                new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 13, Name = "Done" },
                new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = 14, Name = "Done" }
            ]
        });
        var parent = await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            variables: [Process("managerApproved")],
            outputs: [Mapping("approved", "managerApproved", WorkflowVariableTypes.Boolean)]));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);
        var childId = invocation.ChildInstanceId!.Value;

        await TakeTaskFlowAsync(childId, 1101);

        // One branch ending is not the child ending, so nothing has been captured.
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(childId)).Status);
        Assert.False(await HasResultAsync(childId));
        Assert.False(await HasCallJobAsync(invocation.Id, WorkflowJobKinds.CallResume));

        await TakeTaskFlowAsync(childId, 1201);

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(childId)).Status);
        Assert.True(await HasResultAsync(childId));
        await harness.DrainAsync(parentId);

        var finished = await harness.DetailAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Completed, finished.Status);
        Assert.Single(finished.History, item => item.Note == InstanceHistoryNotes.CallCompleted);
        Assert.Equal(CallInvocationStates.Completed, (await harness.InvocationAsync(parentId)).State);
        Assert.Single(finished.Variables, variable =>
            variable.VariableName == "managerApproved" && variable.Value.ValueKind == JsonValueKind.True);
    }

    [Fact]
    public async Task A_job_incident_inside_a_child_keeps_the_call_running_and_the_parent_waiting()
    {
        var childKey = Key("child");
        await harness.PublishAsync(new WorkflowModel
        {
            Id = childKey,
            Name = childKey,
            InitialEventId = Start,
            FlowNodes =
            [
                new FlowNodeModel { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = ChildTask,
                    Name = "Remote",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    AsyncBefore = true,
                    Job = new JobPolicyModel { FailureHandling = JobFailureHandling.RetryFirst, RetryDelays = [] },
                    Service = new ServiceTaskModel
                    {
                        Url = $"https://tests.local/status-sequence/{Guid.NewGuid():N}/503,200",
                        Method = "GET",
                        TimeoutSeconds = 5
                    }
                },
                new FlowNodeModel { Id = End, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = ChildTask },
                new SequenceFlowModel { Id = ChildFlow, SourceRef = ChildTask, TargetRef = End }
            ]
        });
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);
        var childId = invocation.ChildInstanceId!.Value;
        var jobId = await ChildJobAsync(childId, WorkflowJobKinds.AsyncBefore);

        var incident = await harness.OpenIncidentAsync(jobId);
        Assert.NotNull(incident);

        // An incident is not a fault: the child still runs and nothing has been captured.
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(childId)).Status);
        Assert.False(await HasResultAsync(childId));
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(parentId)).Status);
        var call = Assert.Single((await harness.CallsAsync(parentId)).Items);
        Assert.Equal(CallInvocationStates.Running, call.State);
        Assert.Equal(WorkflowInstanceStatuses.Running, call.ChildStatus);
        Assert.Equal(1, call.OpenIncidentCount);
        Assert.Null(call.FailureCode);

        await harness.RetryIncidentAsync(incident!.Id);
        await harness.DrainAsync(parentId);

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(childId)).Status);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
        var settled = Assert.Single((await harness.CallsAsync(parentId)).Items);
        Assert.Equal(invocation.Id, settled.InvocationId);
        Assert.Equal(childId, settled.ChildInstanceId);
        Assert.Equal(0, settled.OpenIncidentCount);
    }

    [Fact]
    public async Task A_parent_fault_on_another_branch_abandons_its_call()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var parent = await harness.PublishAsync(ForkedParent(
            Key("parent"),
            childKey,
            new FlowNodeModel { Id = 13, Name = "Stop", Type = BpmnFlowNodeTypes.ErrorEndEvent, ErrorCode = "parent.stopped" }));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);
        var childId = invocation.ChildInstanceId!.Value;

        await TakeTaskFlowAsync(parentId, 1201);

        Assert.Equal(WorkflowInstanceStatuses.Faulted, (await harness.DetailAsync(parentId)).Status);
        Assert.Equal("parent.stopped", (await harness.DetailAsync(parentId)).Fault?.Code);
        Assert.Equal(CallInvocationStates.Cancelled, (await harness.InvocationAsync(parentId)).State);

        // The fence stops the child from that commit, before its job has run.
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(childId)).Status);
        using (var refused = await harness.TakeChildActionAsync(childId))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }

        await harness.DrainAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(childId)).Status);
        Assert.Equal("parent.stopped", (await harness.DetailAsync(parentId)).Fault?.Code);
        Assert.False(await HasCallJobAsync(invocation.Id, WorkflowJobKinds.CallResume));
    }

    [Fact]
    public async Task An_administrative_action_offers_no_waiting_call_and_abandons_one_it_ends()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var parent = await harness.PublishAsync(ForkedParent(
            Key("parent"),
            childKey,
            new FlowNodeModel { Id = 13, Name = "Stop", Type = BpmnFlowNodeTypes.TerminateEndEvent }));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var invocation = await harness.InvocationAsync(parentId);

        var path = $"/api/instances/{parentId}/administrative-actions";
        using var discovery = await harness.SendAsync(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        var positions = await discovery.Content.ReadFromJsonAsync<PagedResult<InstanceAdministrativeActionPositionDto>>();

        // The task can be overridden. The waiting call is not a position at all.
        var position = Assert.Single(positions!.Items).Position;
        Assert.Equal(12, position.NodeId);

        using (var overridden = await harness.SendAsync(HttpMethod.Post, path, new ExecuteInstanceAdministrativeActionRequest
        {
            ExpectedWorkflowDefinitionId = position.WorkflowDefinitionId,
            SourceNodeId = position.NodeId,
            PositionKind = position.PositionKind,
            PositionId = position.PositionId,
            FlowId = 1201,
            ExpectedTokenId = position.TokenId,
            ExpectedTokenActivationId = position.TokenActivationId,
            ExpectedPositionUpdatedAt = position.PositionUpdatedAt,
            ExpectedAffectedTaskCount = position.AffectedTaskCount
        }))
        {
            Assert.True(overridden.StatusCode == HttpStatusCode.OK, await overridden.Content.ReadAsStringAsync());
        }

        Assert.Equal(WorkflowInstanceStatuses.Completed, (await harness.DetailAsync(parentId)).Status);
        Assert.Equal(CallInvocationStates.Cancelled, (await harness.InvocationAsync(parentId)).State);
        await harness.DrainAsync(parentId);
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(invocation.ChildInstanceId!.Value)).Status);
    }

    [Fact]
    public async Task A_conflict_on_a_cancel_job_opens_an_incident_because_the_child_still_runs()
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
            Assert.True(cancelled.IsSuccessStatusCode);
        }

        var jobId = await harness.SingleJobIdAsync(invocation.Id, WorkflowJobKinds.CallCancel);
        await using var conflicting = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var registration = services.Last(item => item.ServiceType == typeof(IWorkflowRuntimeRepository));
            services.Remove(registration);
            services.AddScoped(provider => ConflictingCancelRepository.Wrap(
                (IWorkflowRuntimeRepository)registration.ImplementationFactory!(provider)));
        }));

        await harness.ProcessAsync(jobId, conflicting.Services);

        var incident = await harness.OpenIncidentAsync(jobId);
        Assert.NotNull(incident);
        Assert.Equal(WorkflowIncidentTypes.JobConflict, incident!.Type);
        Assert.Equal(WorkflowInstanceStatuses.Running, (await harness.DetailAsync(childId)).Status);

        await harness.RetryIncidentAsync(incident.Id);
        await harness.ProcessAsync(jobId);

        Assert.Equal(WorkflowInstanceStatuses.Cancelled, (await harness.DetailAsync(childId)).Status);
    }

    [Fact]
    public async Task Reads_of_calls_use_the_same_number_of_commands_however_many_calls_there_are()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var few = await StartFannedParentAsync(childKey, 2);
        var many = await StartFannedParentAsync(childKey, 7);

        Assert.Equal(await CommandsToReadCallsAsync(few), await CommandsToReadCallsAsync(many));
        Assert.Equal(await CommandsToReadDetailAsync(few), await CommandsToReadDetailAsync(many));
        Assert.Equal(7, (await harness.CallsAsync(many)).TotalCount);
    }

    [Fact]
    public async Task A_parent_and_its_child_stay_independent_entries_in_instance_search()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var parentKey = Key("parent");
        var parent = await harness.PublishAsync(Parent(parentKey, childKey));
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        var childId = (await harness.InvocationAsync(parentId)).ChildInstanceId!.Value;

        Assert.Equal([childId], await ListedInstancesAsync(HttpMethod.Get, $"/api/instances?workflowKey={childKey}"));
        Assert.Equal([parentId], await ListedInstancesAsync(HttpMethod.Get, $"/api/instances?workflowKey={parentKey}"));
        Assert.Equal(
            [childId],
            await ListedInstancesAsync(HttpMethod.Post, "/api/instances/search", new { workflowKey = childKey }));
    }

    [Fact]
    public async Task A_deletion_is_rechecked_by_the_command_when_the_successor_changed_after_the_preview()
    {
        var childKey = Key("child");
        var first = await harness.PublishAsync(ImmediateChild(childKey));
        var second = await harness.PublishAsync(ImmediateChild(childKey));

        var preview = await CallersAsync(childKey, $"deletingId={first.Id}");
        Assert.True(preview.DeletionAllowed);
        Assert.Equal(second.Id, preview.EffectiveTargetId);

        var third = await harness.PublishAsync(ImmediateChild(childKey));
        using (var deleted = await harness.SendAsync(HttpMethod.Delete, $"/api/workflows/{first.Id}"))
        {
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        }

        Assert.Equal(third.Id, (await CallersAsync(childKey)).EffectiveTargetId);
    }

    [Fact]
    public async Task A_family_with_business_keys_previews_no_default_when_only_an_unkeyed_version_remains()
    {
        var childKey = Key("child");
        var unkeyed = await harness.PublishAsync(ImmediateChild(childKey));
        var keyedModel = ImmediateChild(childKey, startInputs: [Input("orderId", WorkflowVariableTypes.String)]);
        keyedModel.FlowNodes.Single(node => node.Id == Start).BusinessKey = new BusinessKeyModel
        {
            Variable = "orderId",
            Uniqueness = BusinessKeyUniqueness.Active
        };
        var keyed = await harness.PublishAsync(keyedModel);
        using (var promoted = await harness.SendAsync(HttpMethod.Post, $"/api/workflows/{keyed.Id}/set-default"))
        {
            Assert.True(promoted.IsSuccessStatusCode, await promoted.Content.ReadAsStringAsync());
        }

        await harness.PublishAsync(Parent(
            Key("parent"),
            childKey,
            startInputs: [Input("order", WorkflowVariableTypes.String)],
            inputs: [Mapping("order", "orderId", WorkflowVariableTypes.String)]));

        var preview = await CallersAsync(childKey, $"deletingId={keyed.Id}");

        // The older version is published, but enabling business keys cannot be undone.
        Assert.True(preview.DeletionAllowed);
        Assert.True(preview.NoDefault);
        Assert.Null(preview.EffectiveTargetId);
        var caller = Assert.Single(preview.Callers.Items);
        Assert.Equal([CallFailureCodes.TargetUnavailable], caller.ReasonCodes);

        using (var deleted = await harness.SendAsync(HttpMethod.Delete, $"/api/workflows/{keyed.Id}"))
        {
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        }

        Assert.True((await CallersAsync(childKey)).NoDefault);
        Assert.NotEqual(0, unkeyed.Id);
    }

    [Fact]
    public async Task A_running_distributor_instance_limits_the_previewed_successor_to_versions_with_credentials()
    {
        var childKey = Key("child");
        var oldest = await harness.PublishAsync(DistributedChild(childKey));
        var oldestInstance = await harness.StartAsync(oldest.Id);
        var current = await harness.PublishAsync(DistributedChild(childKey));
        using (var promoted = await harness.SendAsync(HttpMethod.Post, $"/api/workflows/{current.Id}/set-default"))
        {
            Assert.True(promoted.IsSuccessStatusCode, await promoted.Content.ReadAsStringAsync());
        }

        // The highest version would strand the running instance's hidden task.
        await harness.PublishAsync(ImmediateChild(childKey));

        var preview = await CallersAsync(childKey, $"deletingId={current.Id}");
        Assert.True(preview.DeletionAllowed);
        Assert.Equal(oldest.Id, preview.EffectiveTargetId);
        Assert.Equal(oldest.Version, preview.EffectiveTargetVersion);

        using (var deleted = await harness.SendAsync(HttpMethod.Delete, $"/api/workflows/{current.Id}"))
        {
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        }

        Assert.Equal(oldest.Id, (await CallersAsync(childKey)).EffectiveTargetId);
        Assert.NotEqual(0, oldestInstance);
    }

    /// <summary>A required-assignment child only an external distributor can assign.</summary>
    private static WorkflowModel DistributedChild(string key)
    {
        var model = WaitingChild(key);
        model.TaskDistribution = new TaskDistributionModel { ClientId = "distributor", ClientSecret = "distributor-secret" };
        model.FlowNodes.Single(node => node.Id == ChildTask).RequiresAssignment = true;
        return model;
    }

    /// <summary>A parent whose call waits on one branch while a task on the other can reach <paramref name="stop"/>.</summary>
    private static WorkflowModel ForkedParent(string key, string childKey, FlowNodeModel stop) => new()
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
            stop,
            new FlowNodeModel { Id = 14, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = 10 },
            new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 11 },
            new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 12 },
            new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 14 },
            new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = stop.Id, Name = "Stop" }
        ]
    };

    private async Task<long> StartFannedParentAsync(string childKey, int calls)
    {
        var key = Key("parent");
        var nodes = new List<FlowNodeModel>
        {
            new() { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new() { Id = 10, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway }
        };
        var flows = new List<SequenceFlowModel> { new() { Id = 101, SourceRef = Start, TargetRef = 10 } };
        for (var index = 0; index < calls; index++)
        {
            var callId = 100 + index;
            var endId = 200 + index;
            nodes.Add(new FlowNodeModel
            {
                Id = callId,
                Name = $"Call {index}",
                Type = BpmnFlowNodeTypes.CallActivity,
                CallActivity = new CallActivityModel { WorkflowKey = childKey }
            });
            nodes.Add(new FlowNodeModel { Id = endId, Name = $"End {index}", Type = BpmnFlowNodeTypes.EndEvent });
            flows.Add(new SequenceFlowModel { Id = 2000 + index, SourceRef = 10, TargetRef = callId });
            flows.Add(new SequenceFlowModel { Id = 3000 + index, SourceRef = callId, TargetRef = endId });
        }

        var parent = await harness.PublishAsync(new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = Start,
            FlowNodes = nodes,
            SequenceFlows = flows
        });
        var parentId = await harness.StartAsync(parent.Id);
        await harness.DrainAsync(parentId);
        return parentId;
    }

    private async Task<int> CommandsToReadCallsAsync(long instanceId)
    {
        await harness.CallsAsync(instanceId);
        fixture.CommandCounter.Reset();
        await harness.CallsAsync(instanceId);
        return fixture.CommandCounter.ReaderCommands;
    }

    private async Task<int> CommandsToReadDetailAsync(long instanceId)
    {
        await harness.DetailAsync(instanceId);
        fixture.CommandCounter.Reset();
        await harness.DetailAsync(instanceId);
        return fixture.CommandCounter.ReaderCommands;
    }

    private async Task TakeTaskFlowAsync(long instanceId, int flowId)
    {
        long taskId;
        await using (var db = fixture.CreateDbContext())
        {
            var flowSource = await db.UserTasks.AsNoTracking()
                .Where(task => task.InstanceId == instanceId && task.CompletedAt == null)
                .OrderBy(task => task.Id)
                .Select(task => new { task.Id, task.NodeId })
                .ToListAsync();
            taskId = flowSource.Single(task => task.NodeId == flowId / 100).Id;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/{flowId}")
        {
            Content = JsonContent.Create(new { })
        };
        ApiTestAuth.Authorize(request, "worker", "worker");
        using var response = await fixture.Client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<long> ChildJobAsync(long childId, string kind)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.AsNoTracking()
            .Where(job => job.InstanceId == childId && job.Kind == kind)
            .Select(job => job.Id)
            .SingleAsync();
    }

    private async Task<bool> HasResultAsync(long childId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.CallActivityResults.AsNoTracking().AnyAsync(result => result.ChildInstanceId == childId);
    }

    private async Task<bool> HasCallJobAsync(long invocationId, string kind)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.AsNoTracking()
            .AnyAsync(job => job.CallInvocationId == invocationId && job.Kind == kind);
    }

    private async Task<CallersPreviewDto> CallersAsync(string workflowKey, string? query = null)
    {
        using var response = await harness.SendAsync(
            HttpMethod.Get,
            $"/api/workflows/{workflowKey}/callers" + (query is null ? string.Empty : "?" + query));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CallersPreviewDto>())!;
    }

    private async Task<long[]> ListedInstancesAsync(HttpMethod method, string path, object? body = null)
    {
        using var response = await harness.SendAsync(method, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        using var json = JsonDocument.Parse(text);
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.All(items, item => Assert.False(item.TryGetProperty("parentCall", out _)));
        return items.Select(item => item.GetProperty("id").GetInt64()).ToArray();
    }
}

/// <summary>Fails the instance lock a cancel job takes, the way a real conflict would surface there.</summary>
public class ConflictingCancelRepository : DispatchProxy
{
    private IWorkflowRuntimeRepository inner = null!;

    public static IWorkflowRuntimeRepository Wrap(IWorkflowRuntimeRepository inner)
    {
        var proxy = Create<IWorkflowRuntimeRepository, ConflictingCancelRepository>();
        ((ConflictingCancelRepository)(object)proxy).inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IWorkflowRuntimeRepository.GetInstanceForUpdateAsync) && args![^1] is true)
        {
            throw new WorkflowConflictException("Another writer changed the instance first.");
        }

        try
        {
            return targetMethod.Invoke(inner, args);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }
}
