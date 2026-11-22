using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// A refused throw fails the step that reached it, in every context of section 4 of the
/// EVENT-04B plan, with the message of section 5, and changes nothing. No message holds a
/// business key or a payload value, and refusals in jobs and deliveries are counted.
/// </summary>
public sealed partial class SignalThrowRuntimeTests
{
    private const string BudgetKey = "MaxOpenWorkflowBroadcasts";

    // Causes, through a request

    [Theory]
    [InlineData(null, "it has no value")]
    [InlineData("   ", "it is blank")]
    [InlineData("long", "it is longer than 300 characters")]
    public async Task Refusal_ABusinessKeyVariableThatCannotBeUsedNamesTheVariableButNotItsValue(string? value, string cause)
    {
        var receiver = ReceiverModel(UniqueSignal());
        await CreateAsync(receiver);
        var thrower = EndThrowerModel(UniqueSignal(), new SignalThrowScopeModel
        {
            Kind = SignalThrowScopeKinds.BusinessKey,
            WorkflowKey = receiver.Id,
            BusinessKeyVariable = "orderNumber"
        });
        thrower.FlowNodes.Single(node => node.Id == 1).Variables[0].Required = false;
        var key = value == "long" ? new string('k', 301) : value;
        var decision = await StartAsync(await CreateAsync(thrower),
            key is null ? null : OrderNumber(key));

        using var response = await TakeResponseAsync(decision.Id, 2, 201);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"Signal end event #3 cannot read a business key from variable 'orderNumber': {cause}.", body);
        if (key is { Length: > 10 })
        {
            Assert.DoesNotContain(key, body, StringComparison.Ordinal);
        }

        Assert.Empty(await BroadcastsThrownByAsync(decision.Id));
        Assert.Equal(new[] { 2 }, await ActiveTaskNodesAsync(decision.Id));
    }

    /// <summary>
    /// An instance without a business key of its own, which an instance started before its
    /// family enabled business keys can be, cannot throw to its own business key.
    /// </summary>
    [Fact]
    public async Task Refusal_AnInstanceWithoutABusinessKeyCannotThrowToItsOwn()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        await CreateAsync(receiver);
        var thrower = EndThrowerModel(signal, new SignalThrowScopeModel
        {
            Kind = SignalThrowScopeKinds.OwnBusinessKey,
            WorkflowKey = receiver.Id
        });
        thrower.FlowNodes.Single(node => node.Id == 1).BusinessKey =
            new BusinessKeyModel { Variable = "orderNumber", Uniqueness = BusinessKeyUniqueness.Active };
        var decision = await StartAsync(await CreateAsync(thrower), OrderNumber("ORD-OWN"));
        await using (var db = fixture.CreateDbContext())
        {
            // As an instance started before its family enabled business keys has none.
            await db.WorkflowInstances.Where(item => item.Id == decision.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.BusinessKey, (string?)null));
        }

        using var response = await TakeResponseAsync(decision.Id, 2, 201);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Signal end event #3 uses this instance's business key, but this instance has none.",
            await response.Content.ReadAsStringAsync());
        Assert.Empty(await BroadcastsThrownByAsync(decision.Id));
    }

    [Fact]
    public async Task Refusal_ARequiredPayloadVariableWithoutAValueIsNamedButNoValueIs()
    {
        var thrower = EndThrowerModel(UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows });
        thrower.FlowNodes.Single(node => node.Id == 1).Variables.Add(
            new VariableModel { Id = 12, Name = "note", DataType = WorkflowVariableTypes.String });
        thrower.FlowNodes.Single(node => node.Id == 3).Signal!.Payload!.Add(
            new SignalPayloadEntryModel { Name = "note", Variable = "note", Required = true });
        var decision = await StartAsync(await CreateAsync(thrower), OrderNumber("ORD-SECRET-1"));

        using var response = await TakeResponseAsync(decision.Id, 2, 201);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Signal end event #3 cannot send payload entry 'note': variable 'note' has no value.", body);
        Assert.DoesNotContain("ORD-SECRET-1", body, StringComparison.Ordinal);
        Assert.Empty(await BroadcastsThrownByAsync(decision.Id));
    }

    [Fact]
    public async Task Refusal_APayloadOverItsLimitGivesItsSizeAndNoValue()
    {
        var thrower = EndThrowerModel(UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows });
        // Many short strings rather than one long one: an older migration that a shared-database
        // downgrade test applies again indexes a whole string value, which refuses a long one.
        thrower.FlowNodes.Single(node => node.Id == 1).Variables.Add(
            new VariableModel { Id = 12, Name = "notes", DataType = WorkflowVariableTypes.String, IsArray = true });
        thrower.FlowNodes.Single(node => node.Id == 3).Signal!.Payload!.Add(
            new SignalPayloadEntryModel { Name = "notes", Variable = "notes" });
        var notes = Enumerable.Range(0, 25_000).Select(index => $"secret-x{index:D5}").ToArray();
        var decision = await StartAsync(await CreateAsync(thrower), new Dictionary<string, JsonElement>
        {
            ["orderNumber"] = JsonSerializer.SerializeToElement("ORD-SIZE"),
            ["notes"] = JsonSerializer.SerializeToElement(notes)
        });

        using var response = await TakeResponseAsync(decision.Id, 2, 201);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Matches(@"Signal end event #3 payload is \d+ bytes; the limit is 262144\.", body);
        Assert.DoesNotContain("secret-x", body, StringComparison.Ordinal);
        Assert.Empty(await BroadcastsThrownByAsync(decision.Id));
    }

    // Contexts

    /// <summary>
    /// A throw behind an asyncAfter task fails its Worker job: retries, then an incident on the
    /// task's node whose text names the throw event, then success after the incident's retry.
    /// </summary>
    [Fact]
    public async Task Refusal_InAnAsyncAfterJobRetriesOpensAnIncidentAndSucceedsAfterItsRetry()
    {
        using var refusals = new RefusalCounter();
        var model = new WorkflowModel
        {
            Id = $"signal-async-after-{Guid.NewGuid():N}",
            Name = "Signal throw behind a job",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Decide", Type = BpmnFlowNodeTypes.UserTask, AsyncAfter = true },
                Throw(3, "Announce", UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows }),
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Go" },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };
        var instance = await StartAsync(await CreateAsync(model));
        await TakeAsync(instance.Id, 2, 201);

        var blocker = await OpenBlockingBroadcastAsync();
        var previous = await ExhaustWorkflowBudgetAsync();
        long incidentId;
        try
        {
            await ProcessInstanceJobsAsync(instance.Id, WorkflowJobKinds.AsyncAfter);

            await using var db = fixture.CreateDbContext();
            var incident = await db.WorkflowIncidents.AsNoTracking()
                .SingleAsync(item => item.InstanceId == instance.Id && item.Status == WorkflowIncidentStatuses.Open);
            incidentId = incident.Id;
            Assert.Equal(2, incident.NodeId);
            Assert.Contains("Signal throw event #3 was not thrown:", incident.Summary + " " + incident.Details);
            var job = await db.WorkflowJobs.AsNoTracking().SingleAsync(item => item.Id == incident.JobId);
            Assert.True(job.AttemptCount > 1, $"The job ran {job.AttemptCount} time(s) before its incident.");
            Assert.True(refusals.Count(SignalRuntimeTelemetryCauses.OpenBroadcasts) >= job.AttemptCount);
            Assert.Empty(await BroadcastsThrownByAsync(instance.Id));
        }
        finally
        {
            await RestoreEngineSettingAsync("Signals", BudgetKey, previous);
            await ProcessDeliveryJobsAsync(blocker);
        }

        using (var retry = await SendAsync(HttpMethod.Post, $"/api/incidents/{incidentId}/retry"))
        {
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
        }

        await ProcessInstanceJobsAsync(instance.Id, WorkflowJobKinds.AsyncAfter);
        Assert.Single(await BroadcastsThrownByAsync(instance.Id));
        await using var check = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, await check.WorkflowInstances.AsNoTracking()
            .Where(item => item.Id == instance.Id).Select(item => item.Status).SingleAsync());
    }

    /// <summary>
    /// A delivery whose continuation reaches a refused throw fails with
    /// <c>signal_delivery_rejected</c>, leaves the wait in place, and Retry failed delivers it.
    /// </summary>
    [Fact]
    public async Task Refusal_InASignalDeliveryRejectsItKeepsTheWaitAndRetryFailedDeliversIt()
    {
        using var refusals = new RefusalCounter();
        var caught = UniqueSignal();
        var model = new WorkflowModel
        {
            Id = $"signal-relay-{Guid.NewGuid():N}",
            Name = "Signal relay",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
                    Signal = new SignalEventModel { Name = caught }
                },
                Throw(3, "Relay", UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows }),
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };
        var instance = await StartAsync(await CreateAsync(model));
        var api = await ThrowApiAsync(new ThrowSignalRequest(caught, InstanceId: instance.Id));

        var blocker = await OpenBlockingBroadcastAsync();
        var previous = await ExhaustWorkflowBudgetAsync();
        try
        {
            await ProcessDeliveryJobsAsync(api.Id);
            await using var db = fixture.CreateDbContext();
            var delivery = await db.SignalDeliveries.AsNoTracking().SingleAsync(item => item.BroadcastId == api.Id);
            Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.DeliveryRejected),
                (delivery.Status, delivery.ResultCode));
            Assert.Contains("Signal throw event #3 was not thrown:", delivery.ResultDescription);
            Assert.True(await db.SignalSubscriptions.AnyAsync(item =>
                item.Id == delivery.SubscriptionId && item.Status == SignalSubscriptionStatuses.Active));
            Assert.True(refusals.Count(SignalRuntimeTelemetryCauses.OpenBroadcasts) >= 1);
            Assert.Empty(await BroadcastsThrownByAsync(instance.Id));
        }
        finally
        {
            await RestoreEngineSettingAsync("Signals", BudgetKey, previous);
            await ProcessDeliveryJobsAsync(blocker);
        }

        var finished = await BroadcastAsync(api.Id);
        Assert.Equal(SignalBroadcastStatuses.CompletedWithIssues, finished.Status);
        using (var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{api.Id}/retry-failed",
                   new RetrySignalDeliveriesRequest(finished.UpdatedAt)))
        {
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
        }

        await ProcessDeliveryJobsAsync(api.Id);
        var relayed = Assert.Single(await BroadcastsThrownByAsync(instance.Id));
        await ProcessDeliveryJobsAsync(relayed.Id);
        await using var check = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, await check.WorkflowInstances.AsNoTracking()
            .Where(item => item.Id == instance.Id).Select(item => item.Status).SingleAsync());
    }

    /// <summary>A child whose first steps reach a refused throw fails its call with call_start_failed.</summary>
    [Fact]
    public async Task Refusal_InACallChildsStartFailsTheCall()
    {
        var calls = new CallActivityHarness(fixture);
        var childKey = CallActivityHarness.Key("signal-refusing-child");
        var child = CallActivityHarness.ImmediateChild(childKey);
        child.FlowNodes.Add(Throw(4, "Announce", UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows }));
        child.SequenceFlows.Single().TargetRef = 4;
        child.SequenceFlows.Add(new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = CallActivityHarness.End });
        await calls.PublishAsync(child);
        var parent = CallActivityHarness.Parent(CallActivityHarness.Key("signal-refused-call"), childKey,
            withBoundary: true, errorVariable: "callError",
            variables: [CallActivityHarness.Process("callError", WorkflowVariableTypes.String)]);
        var parentId = await calls.StartAsync((await calls.PublishAsync(parent)).Id);

        var blocker = await OpenBlockingBroadcastAsync();
        var previous = await ExhaustWorkflowBudgetAsync();
        try
        {
            await calls.DrainAsync(parentId);
        }
        finally
        {
            await RestoreEngineSettingAsync("Signals", BudgetKey, previous);
            await ProcessDeliveryJobsAsync(blocker);
            await calls.RetireLeftoverJobsAsync();
        }

        var invocation = await calls.InvocationAsync(parentId);
        Assert.Equal(CallFailureCodes.StartFailed, invocation.FailureCode);
        Assert.Contains("Signal throw event #4 was not thrown:", invocation.FailureDescription);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowInstances.AnyAsync(item => item.CallRootInstanceId == parentId));
        Assert.Equal(WorkflowInstanceStatuses.Completed, await db.WorkflowInstances.AsNoTracking()
            .Where(item => item.Id == parentId).Select(item => item.Status).SingleAsync());
    }

    /// <summary>An administrative batch item whose flow reaches a refused throw fails and moves nothing.</summary>
    [Fact]
    public async Task Refusal_InAnAdministrativeBatchItemFailsTheItem()
    {
        var model = OrderModel(UniqueSignal());
        model.FlowNodes.Single(node => node.Id == 8).Signal!.Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows };
        var workflowId = await CreateAsync(model);
        var order = await StartAsync(workflowId);

        using var search = await SendAsync(HttpMethod.Post, "/api/administrative-actions/candidates/search",
            new AdministrativeActionCandidateSearchRequest { WorkflowDefinitionId = workflowId, SourceNodeId = 7, Page = 1, PageSize = 50 });
        Assert.True(search.IsSuccessStatusCode, await search.Content.ReadAsStringAsync());
        var candidate = Assert.Single((await search.Content.ReadFromJsonAsync<PagedResult<AdministrativeActionCandidateDto>>(JsonOptions))!.Items);
        using var create = await SendAsync(HttpMethod.Post, "/api/administrative-action-batches",
            new CreateAdministrativeActionBatchRequest(
                workflowId, 7, AdministrativeActionKinds.DirectFlow, 701, null, null, "Signal refusal", null,
                new AdministrativeActionBatchSelectionDto(
                    AdministrativeActionBatchSelectionModes.Explicit,
                    [new AdministrativeActionPositionReferenceDto(candidate.PositionKind, candidate.PositionId)], null, null),
                $"signal-refusal-{Guid.NewGuid():N}"));
        Assert.True(create.StatusCode == HttpStatusCode.Accepted, await create.Content.ReadAsStringAsync());
        var batch = (await create.Content.ReadFromJsonAsync<AdministrativeActionBatchDetailDto>(JsonOptions))!;
        await ProcessJobByIdAsync(batch.PreparationJobId!.Value);
        batch = await AdministrativeBatchAsync(batch.Summary.Id);
        using (var confirm = await SendAsync(HttpMethod.Post, $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   new ConfirmAdministrativeActionBatchRequest(batch.Summary.EligibleItemCount,
                       batch.Summary.TotalAffectedTaskCount, batch.Summary.UpdatedAt)))
        {
            Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync());
            batch = (await confirm.Content.ReadFromJsonAsync<AdministrativeActionBatchDetailDto>(JsonOptions))!;
        }

        var blocker = await OpenBlockingBroadcastAsync();
        var previous = await ExhaustWorkflowBudgetAsync();
        try
        {
            await ProcessJobByIdAsync(batch.ExecutionJobId!.Value);
        }
        finally
        {
            await RestoreEngineSettingAsync("Signals", BudgetKey, previous);
            await ProcessDeliveryJobsAsync(blocker);
        }

        using var items = await SendAsync(HttpMethod.Get, $"/api/administrative-action-batches/{batch.Summary.Id}/items?page=1&pageSize=50");
        var item = Assert.Single((await items.Content.ReadFromJsonAsync<PagedResult<AdministrativeActionBatchItemDto>>(JsonOptions))!.Items);
        Assert.Equal(AdministrativeActionBatchItemStatuses.Failed, item.Status);
        Assert.Contains("Signal throw event #8 was not thrown:", item.ErrorDescription);
        Assert.Empty(await BroadcastsThrownByAsync(order.Id));
        Assert.Equal(new[] { 3, 7 }, await ActiveTaskNodesAsync(order.Id));
    }

    /// <summary>
    /// An administrative variable update that releases a conditional catch in front of a
    /// refused throw is refused as a whole, alone and as a batch item.
    /// </summary>
    [Fact]
    public async Task Refusal_InAnAdministrativeVariableUpdateRefusesTheUpdateAloneAndInABatch()
    {
        var model = new WorkflowModel
        {
            Id = $"signal-variable-update-{Guid.NewGuid():N}",
            Name = "Signal throw behind a condition",
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1, Name = "go", DataType = WorkflowVariableTypes.Boolean,
                    DefaultValue = JsonSerializer.SerializeToElement(false)
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2, Name = "Ready", Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                    Conditional = new ConditionalDefinitionModel { Condition = "go == true" }
                },
                Throw(3, "Announce", UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows }),
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };
        var workflowId = await CreateAsync(model);
        var single = await StartAsync(workflowId);
        var batched = await StartAsync(workflowId);
        var go = new InstanceVariableWriteDto("go", JsonSerializer.SerializeToElement(true));

        var blocker = await OpenBlockingBroadcastAsync();
        var previous = await ExhaustWorkflowBudgetAsync();
        InstanceVariableUpdateBatchDetailDto batch;
        try
        {
            using (var patch = await SendAsync(HttpMethod.Patch, $"/api/instances/{single.Id}/variables",
                       new UpdateInstanceVariablesRequest([go], "Release", null)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);
                Assert.Contains("Signal throw event #3 was not thrown:", await patch.Content.ReadAsStringAsync());
            }

            using (var create = await SendAsync(HttpMethod.Post, "/api/instance-variable-update-batches",
                       new CreateInstanceVariableUpdateBatchRequest(model.Id, [go], "Release",
                           new InstanceVariableUpdateBatchSelectionDto(InstanceVariableUpdateBatchSelectionModes.Explicit,
                               [batched.Id], null, null),
                           $"signal-refusal-{Guid.NewGuid():N}")))
            {
                Assert.True(create.StatusCode == HttpStatusCode.Accepted, await create.Content.ReadAsStringAsync());
                batch = (await create.Content.ReadFromJsonAsync<InstanceVariableUpdateBatchDetailDto>(JsonOptions))!;
            }

            foreach (var job in batch.Jobs.Where(job => job.Phase == InstanceVariableUpdateBatchPhases.Prepare))
            {
                await ProcessJobByIdAsync(job.JobId!.Value);
            }

            using (var get = await SendAsync(HttpMethod.Get, $"/api/instance-variable-update-batches/{batch.Summary.Id}"))
            {
                batch = (await get.Content.ReadFromJsonAsync<InstanceVariableUpdateBatchDetailDto>(JsonOptions))!;
            }

            using (var confirm = await SendAsync(HttpMethod.Post, $"/api/instance-variable-update-batches/{batch.Summary.Id}/confirm",
                       new ConfirmInstanceVariableUpdateBatchRequest(batch.Summary.EligibleItemCount,
                           batch.Summary.IneligibleItemCount, batch.Summary.WarningItemCount, batch.Summary.UpdatedAt)))
            {
                Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync());
                batch = (await confirm.Content.ReadFromJsonAsync<InstanceVariableUpdateBatchDetailDto>(JsonOptions))!;
            }

            foreach (var job in batch.Jobs.Where(job => job.Phase == InstanceVariableUpdateBatchPhases.Execute))
            {
                await ProcessJobByIdAsync(job.JobId!.Value);
            }
        }
        finally
        {
            await RestoreEngineSettingAsync("Signals", BudgetKey, previous);
            await ProcessDeliveryJobsAsync(blocker);
        }

        using var items = await SendAsync(HttpMethod.Get, $"/api/instance-variable-update-batches/{batch.Summary.Id}/items?page=1&pageSize=50");
        var item = Assert.Single((await items.Content.ReadFromJsonAsync<PagedResult<InstanceVariableUpdateBatchItemDto>>(JsonOptions))!.Items);
        // A business failure skips a variable-update batch item, with its code and message.
        Assert.Equal(InstanceVariableUpdateBatchItemStatuses.Skipped, item.Status);
        Assert.Contains("Signal throw event #3 was not thrown:", item.ErrorDescription);
        await using var db = fixture.CreateDbContext();
        foreach (var instanceId in new[] { single.Id, batched.Id })
        {
            Assert.Empty(await BroadcastsThrownByAsync(instanceId));
            Assert.False((await db.InstanceVariableCurrentValues.AsNoTracking()
                .SingleAsync(value => value.InstanceId == instanceId && value.VariableName == "go"))
                .ValueJson.RootElement.GetBoolean());
        }
    }

    // Helpers of the refusal tests

    /// <summary>
    /// Leaves one broadcast thrown by a workflow delivering, so a budget equal to the open
    /// count is a valid setting that refuses the next throw. Deliver it when done.
    /// </summary>
    private async Task<long> OpenBlockingBroadcastAsync()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        await StartAsync(await CreateAsync(receiver), OrderNumber("ORD-BLOCK"));
        var thrower = EndThrowerModel(signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = receiver.Id });
        var decision = await StartAsync(await CreateAsync(thrower), OrderNumber("ORD-BLOCKER"));
        await TakeAsync(decision.Id, 2, 201);
        return Assert.Single(await BroadcastsThrownByAsync(decision.Id)).Id;
    }

    /// <summary>Sets the workflow budget to the broadcasts already open and returns the earlier value.</summary>
    private async Task<string?> ExhaustWorkflowBudgetAsync()
    {
        int open;
        await using (var db = fixture.CreateDbContext())
        {
            open = await db.SignalBroadcasts.CountAsync(item =>
                item.Status == SignalBroadcastStatuses.Delivering && item.ThrownByKind == SignalThrowerKinds.Instance);
        }

        Assert.True(open > 0);
        return await SetEngineSettingAsync("Signals", BudgetKey, open.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task ProcessJobByIdAsync(long jobId)
    {
        var calls = new CallActivityHarness(fixture);
        await calls.ProcessAsync(jobId);
    }

    private async Task<AdministrativeActionBatchDetailDto> AdministrativeBatchAsync(long batchId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/administrative-action-batches/{batchId}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AdministrativeActionBatchDetailDto>(JsonOptions))!;
    }

    private static class SignalRuntimeTelemetryCauses
    {
        public const string OpenBroadcasts = "open_broadcasts";
    }

    /// <summary>Counts the refusal counter's measurements by cause while it lives.</summary>
    private sealed class RefusalCounter : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> counts = new(StringComparer.Ordinal);

        public RefusalCounter()
        {
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == "flowbit.signals.throw_refusals")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "cause" && tag.Value is string cause)
                    {
                        counts.AddOrUpdate(cause, value, (_, current) => current + value);
                    }
                }
            });
            listener.Start();
        }

        public long Count(string cause) => counts.GetValueOrDefault(cause);

        public void Dispose() => listener.Dispose();
    }
}
