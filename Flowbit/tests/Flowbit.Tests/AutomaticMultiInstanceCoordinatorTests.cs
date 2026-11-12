using System.Reflection;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>Core transition/fence tests. They do not substitute for adapter/Worker integration.</summary>
public sealed partial class AutomaticMultiInstanceCoordinatorTests
{
    [Fact]
    public async Task Item_context_reuses_frozen_body_and_first_shared_environment_values_on_retry()
    {
        var fixture = new Fixture();
        fixture.Item = fixture.Item with { StagedInput = null };
        fixture.Job = fixture.Job with { AttemptCount = 1 };
        fixture.Definition.Variables.AddRange([
            new() { Name = "items", Scope = "shared", SharedKey = "collection", Access = "read", DataType = "number", IsArray = true },
            new() { Name = "sourceAgain", Scope = "shared", SharedKey = "collection", Access = "read", DataType = "number", IsArray = true }
        ]);
        fixture.Definition.FlowNodes[0].Service!.Url = "https://example.test/${sourceAgain}";
        fixture.Definition.FlowNodes[0].MultiInstance!.OutputVariables![0].DefaultValue = JsonSerializer.SerializeToElement(3);
        fixture.SharedValues["sharedOutput"] = JsonSerializer.SerializeToElement(10);
        var first = await fixture.Coordinator.CaptureItemContextAsync(1, 6, fixture.ActivationId, fixture.Fence,
            fixture.Definition, new Dictionary<string, JsonElement> { ["sys.now"] = JsonSerializer.SerializeToElement("first") },
            DateTimeOffset.UnixEpoch, CancellationToken.None);
        fixture.SharedValues["sharedOutput"] = JsonSerializer.SerializeToElement(99);
        fixture.SharedVersion = 99;
        fixture.Job = fixture.Job with { AttemptCount = 2, LeaseGeneration = 2 };
        var retry = await fixture.Coordinator.CaptureItemContextAsync(1, 6, fixture.ActivationId, fixture.Fence with { LeaseGeneration = 2 },
            fixture.Definition, new Dictionary<string, JsonElement> { ["sys.now"] = JsonSerializer.SerializeToElement("later") },
            DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(10, retry.Values["sharedOutput"].GetInt32());
        Assert.Equal("first", retry.Values["sys.now"].GetString());
        Assert.Equal(42, retry.Values["mi.item"].GetInt32());
        Assert.Equal(42, retry.Values["sourceAgain"][0].GetInt32());
        Assert.Equal(7, retry.Values["frozenInput"].GetInt32());
        Assert.Equal(3, retry.LocalVariables["local"].GetInt32());
        Assert.Equal(1, retry.SharedOutputValueVersions["sharedOutput"]);
        Assert.Equal(7, retry.Values["sys.jobId"].GetInt64());
        Assert.Equal(2, retry.Values["sys.jobAttempt"].GetInt32());
        Assert.Equal(1, first.Values["sys.jobAttempt"].GetInt32());
        Assert.Equal(first.StagedAt, retry.StagedAt);
        Assert.Equal(1, fixture.StageSaves);
        Assert.Equal(1, fixture.SharedReads);
        Assert.DoesNotContain("collection", fixture.Item.StagedInput!.Value.GetRawText());
        Assert.DoesNotContain("frozenInput", fixture.Item.StagedInput.Value.GetRawText());
        Assert.DoesNotContain("sys.jobAttempt", fixture.Item.StagedInput.Value.GetRawText());
    }

    [Fact]
    public async Task Item_context_does_not_accept_adapter_overrides_of_process_inputs()
    {
        var fixture = new Fixture();
        fixture.Item = fixture.Item with { StagedInput = null };
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Coordinator.CaptureItemContextAsync(1, 6,
            fixture.ActivationId, fixture.Fence, fixture.Definition,
            new Dictionary<string, JsonElement> { ["frozenInput"] = JsonSerializer.SerializeToElement(99) },
            DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(0, fixture.StageSaves);
    }

    [Fact]
    public async Task Publication_rechecks_current_shared_validation_dependencies_without_erasing_candidate()
    {
        var fixture = new Fixture();
        fixture.Definition.FlowNodes[0].MultiInstance!.OutputVariables![0].Validation = "local < sharedOutput";
        await fixture.Coordinator.SaveCandidateAsync(1, 6, fixture.ActivationId, fixture.Fence, fixture.Definition,
            new(new Dictionary<string, JsonElement> { ["local"] = JsonSerializer.SerializeToElement(7) },
                new Dictionary<string, JsonElement>(), new Dictionary<string, long>(),
                new Dictionary<string, JsonElement> { ["sharedOutput"] = JsonSerializer.SerializeToElement(10) }), CancellationToken.None);
        fixture.SharedValues["sharedOutput"] = JsonSerializer.SerializeToElement(5);
        await Assert.ThrowsAsync<WorkflowDomainException>(() => fixture.PublishAsync());
        Assert.NotNull(fixture.Item.Candidate);
        Assert.Equal(0, fixture.Completions);
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task Publication_validates_against_the_complete_candidate_overlay_even_when_old_target_is_unset()
    {
        var fixture = new Fixture { SharedVersion = 0 };
        fixture.Definition.FlowNodes[0].MultiInstance!.OutputVariables![0].Validation = "local < sharedOutput";
        await fixture.Coordinator.SaveCandidateAsync(1, 6, fixture.ActivationId, fixture.Fence, fixture.Definition,
            new(new Dictionary<string, JsonElement> { ["local"] = JsonSerializer.SerializeToElement(7) },
                new Dictionary<string, JsonElement> { ["sharedOutput"] = JsonSerializer.SerializeToElement(10) },
                new Dictionary<string, long> { ["sharedOutput"] = 0 },
                new Dictionary<string, JsonElement>()), CancellationToken.None);
        Assert.True(await fixture.PublishAsync());
        Assert.Equal(1, fixture.Writes);
        Assert.Equal(1, fixture.Completions);
    }

    [Fact]
    public async Task Accumulated_result_limit_refuses_publication_before_shared_writes_with_stable_code()
    {
        var fixture = new Fixture();
        fixture.Body = fixture.Body with { ResultBytes = AutomaticMultiInstanceRules.MaxResultBytes - 1 };
        await fixture.SaveCandidateAsync(7, shared: true);
        var error = await Assert.ThrowsAsync<AutomaticMultiInstanceValueException>(() => fixture.PublishAsync());
        Assert.Equal(AutomaticMultiInstanceValues.ResultTooLarge, error.Code);
        Assert.Equal(0, fixture.Writes);
        Assert.Equal(0, fixture.Completions);
        Assert.NotNull(fixture.Item.Candidate);
    }

    [Fact]
    public async Task Entry_wait_reuses_one_job_and_preserves_the_open_body()
    {
        var fixture = new Fixture();
        fixture.Job = fixture.Job with { Kind = WorkflowJobKinds.AsyncBefore, Status = WorkflowJobStatuses.Running,
            MultiInstanceExecutionId = null, AutomaticMultiInstanceItemId = null };
        var body = fixture.Body;
        Assert.Equal(TimeSpan.FromSeconds(5), await fixture.Coordinator.WaitForEntrySlotAsync(fixture.Fence, CancellationToken.None));
        fixture.Job = fixture.Job with { LeaseGeneration = 8 };
        Assert.Equal(TimeSpan.FromSeconds(60), await fixture.Coordinator.WaitForEntrySlotAsync(
            fixture.Fence with { LeaseGeneration = 8 }, CancellationToken.None));
        Assert.Equal(body, fixture.Body);
        Assert.Equal(1, fixture.ResultReadySaves);
        Assert.Empty(fixture.Enqueued);
        Assert.Null(fixture.OpenedIncidentType);
    }

    [Fact]
    public async Task Entry_wait_cannot_release_an_item_job_or_stale_lease()
    {
        var fixture = new Fixture();
        await Assert.ThrowsAsync<WorkflowConflictException>(() => fixture.Coordinator.WaitForEntrySlotAsync(fixture.Fence, CancellationToken.None));
        fixture.Job = fixture.Job with { Kind = WorkflowJobKinds.AsyncBefore, MultiInstanceExecutionId = null, AutomaticMultiInstanceItemId = null };
        fixture.LeaseAlive = false;
        await Assert.ThrowsAsync<WorkflowConflictException>(() => fixture.Coordinator.WaitForEntrySlotAsync(fixture.Fence, CancellationToken.None));
        Assert.Null(fixture.ReleasedFor);
    }

    [Fact]
    public async Task Candidate_cannot_alias_a_reserved_shared_key_or_publish_an_unauthored_target()
    {
        var fixture = new Fixture();
        fixture.Definition.Variables.Add(new() { Name = "items", Scope = "shared", SharedKey = "counter", Access = "read", DataType = "number", IsArray = true });
        await Assert.ThrowsAsync<AutomaticMultiInstanceValueException>(() => fixture.SaveCandidateAsync(7, shared: true));
        fixture.Definition.Variables.RemoveAt(1);
        fixture.Definition.FlowNodes[0].Service!.OutputMappings.RemoveAll(mapping => mapping.Variable == "sharedOutput");
        await Assert.ThrowsAsync<AutomaticMultiInstanceValueException>(() => fixture.SaveCandidateAsync(7, shared: true));
        Assert.Equal(0, fixture.CandidateSaves);
    }

    [Theory]
    [InlineData(1L, 5)]
    [InlineData(2L, 10)]
    [InlineData(3L, 20)]
    [InlineData(4L, 40)]
    [InlineData(5L, 60)]
    [InlineData(long.MaxValue, 60)]
    [InlineData(long.MinValue, 5)]
    public void Dispatch_delay_is_bounded_and_restart_stable(long generation, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), AutomaticMultiInstanceCoordinator.DispatchBackoff(generation));

    [Fact]
    public async Task Capacity_wait_reuses_result_ready_job_without_consuming_another_attempt()
    {
        var fixture = new Fixture { OpenJobs = 2, Pending = 1, Active = 0 };
        fixture.MakeDispatch(1);
        var result = await fixture.DispatchAsync();
        Assert.Equal("queueCapacity", result.State);
        Assert.Equal(TimeSpan.FromSeconds(5), fixture.ReleasedFor);
        Assert.Equal(WorkflowJobStatuses.ResultReady, fixture.Job.Status);
        Assert.Equal(1, fixture.ResultReadySaves);
        Assert.Empty(fixture.Enqueued);
        fixture.Job = fixture.Job with { LeaseGeneration = 2 };
        await fixture.DispatchAsync();
        Assert.Equal(1, fixture.ResultReadySaves);
        Assert.Equal(TimeSpan.FromSeconds(10), fixture.ReleasedFor);
    }

    [Fact]
    public async Task Maximum_capacity_backoff_with_nothing_in_flight_opens_an_operational_incident()
    {
        var fixture = new Fixture { OpenJobs = 2, Pending = 1, Active = 0 };
        fixture.MakeDispatch(5);
        var result = await fixture.DispatchAsync();
        Assert.Equal("capacityIncident", result.State);
        Assert.Equal("job_capacity_exceeded", fixture.OpenedIncidentType);
        Assert.Equal(0, fixture.ResultReadySaves);
        Assert.Null(fixture.ReleasedFor);
        Assert.Equal(0, fixture.Admissions);
    }

    [Fact]
    public async Task Maximum_backoff_with_work_in_flight_keeps_the_durable_wake()
    {
        var fixture = new Fixture { OpenJobs = 2, Pending = 1, Active = 1 };
        fixture.MakeDispatch(5);
        var result = await fixture.DispatchAsync();
        Assert.Equal("queueCapacity", result.State);
        Assert.Equal(TimeSpan.FromSeconds(60), fixture.ReleasedFor);
        Assert.Null(fixture.OpenedIncidentType);
    }

    [Fact]
    public async Task Stale_dispatch_generation_cannot_admit_or_save_a_wake()
    {
        var fixture = new Fixture { OpenJobs = 2, Pending = 1, Active = 0 };
        fixture.MakeDispatch(1);
        fixture.Body = fixture.Body with { ControlGeneration = 2 };
        await Assert.ThrowsAsync<WorkflowConflictException>(() => fixture.DispatchAsync());
        Assert.Equal(0, fixture.CapacityReads);
        Assert.Null(fixture.ReleasedFor);
    }

    [Fact]
    public async Task Available_capacity_recovers_same_body_without_crediting_its_reserved_job_twice()
    {
        var fixture = new Fixture { OpenJobs = 1, Pending = 1, Active = 0 };
        fixture.MakeDispatch(5);
        var result = await fixture.DispatchAsync();
        Assert.Equal("admitted", result.State);
        Assert.Equal(1, result.AdmittedCount);
        Assert.Null(fixture.Body.AdmissionBlockedReason);
        Assert.Equal(WorkflowJobKinds.MultiInstanceItem, Assert.Single(fixture.Enqueued).Kind);
        Assert.Null(fixture.OpenedIncidentType);
    }
    [Fact]
    public async Task Accepted_candidate_is_immutable_across_duplicate_delivery()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.SaveCandidateAsync(7));
        var saved = fixture.Item.Candidate!.Value.GetRawText();
        Assert.False(await fixture.SaveCandidateAsync(99));
        Assert.Equal(saved, fixture.Item.Candidate.Value.GetRawText());
        Assert.Equal(1, fixture.CandidateSaves);
    }

    [Theory]
    [InlineData("activation")]
    [InlineData("lease")]
    [InlineData("worker")]
    [InlineData("item")]
    [InlineData("token")]
    [InlineData("definition")]
    [InlineData("cancelled-body")]
    [InlineData("cancelled-item")]
    public async Task Late_or_misowned_delivery_cannot_save_a_candidate(string scenario)
    {
        var fixture = new Fixture();
        switch (scenario)
        {
            case "activation": fixture.Body = fixture.Body with { ActivationId = Guid.NewGuid() }; break;
            case "lease": fixture.LeaseAlive = false; break;
            case "worker": fixture.Job = fixture.Job with { WorkerId = "replacement" }; break;
            case "item": fixture.Job = fixture.Job with { AutomaticMultiInstanceItemId = 999 }; break;
            case "token": fixture.Job = fixture.Job with { TokenId = 999 }; break;
            case "definition": fixture.Job = fixture.Job with { WorkflowDefinitionId = 999 }; break;
            case "cancelled-body": fixture.Body = fixture.Body with { Status = "cancelled" }; break;
            case "cancelled-item": fixture.Item = fixture.Item with { Status = "cancelled" }; break;
        }
        await Assert.ThrowsAsync<WorkflowConflictException>(() => fixture.SaveCandidateAsync(7));
        Assert.Equal(0, fixture.CandidateSaves);
        Assert.Null(fixture.Item.Candidate);
    }

    [Fact]
    public async Task Full_queue_records_a_reserved_dispatch_without_admitting_or_reinvoking_work()
    {
        var fixture = new Fixture { OpenJobs = 2, Pending = 1, Active = 0 };
        var admitted = await fixture.Coordinator.AdmitJobsAsync(1, fixture.ActivationId,
            fixture.Template, openJobLimit: 2, completingJobCredits: 0, CancellationToken.None);
        Assert.Empty(admitted);
        Assert.Equal(0, fixture.Admissions);
        var wake = Assert.Single(fixture.Enqueued);
        Assert.Equal(WorkflowJobKinds.MultiInstanceDispatch, wake.Kind);
        Assert.Equal(WorkflowJobClasses.Control, wake.QueueClass);
        Assert.Null(wake.AutomaticMultiInstanceItemId);
        Assert.Equal("queueCapacity", fixture.Body.AdmissionBlockedReason);
        Assert.Equal(1, wake.MultiInstanceControlGeneration);
    }

    [Fact]
    public async Task Shared_conflict_retains_saved_candidate_and_authorized_recovery_publishes_without_recalculation()
    {
        var fixture = new Fixture { SharedVersion = 2 };
        Assert.True(await fixture.SaveCandidateAsync(7, shared: true));
        var saved = fixture.Item.Candidate!.Value.GetRawText();
        await Assert.ThrowsAsync<AutomaticMultiInstancePublicationConflictException>(() => fixture.PublishAsync());
        Assert.Equal(saved, fixture.Item.Candidate.Value.GetRawText());
        Assert.Equal(0, fixture.Writes);
        Assert.Equal(0, fixture.Completions);

        fixture.Job = fixture.Job with { PublicationFenceRefreshRequested = true };
        Assert.True(await fixture.PublishAsync());
        Assert.Equal(1, fixture.CandidateSaves);
        Assert.Equal(1, fixture.Writes);
        Assert.Equal(7, fixture.PublishedValue!.Value.GetInt32());
        Assert.Equal(1, fixture.Completions);
        Assert.Equal(1, fixture.RefreshAcknowledgements);
        Assert.Equal("completed", fixture.Item.Status);
        Assert.Equal(WorkflowJobKinds.MultiInstanceComplete, Assert.Single(fixture.Enqueued).Kind);
    }

    [Fact]
    public async Task Completed_item_replay_cannot_append_another_shared_write()
    {
        var fixture = new Fixture();
        await fixture.SaveCandidateAsync(7, shared: true);
        await fixture.PublishAsync();
        await Assert.ThrowsAsync<WorkflowConflictException>(() => fixture.PublishAsync());
        Assert.Equal(1, fixture.Writes);
        Assert.Equal(1, fixture.Completions);
        Assert.Single(fixture.Enqueued);
    }

    [Fact]
    public async Task Publication_keeps_success_and_signals_refill_without_checking_activity_capacity()
    {
        var fixture = new Fixture { OpenJobs = 2000 };
        fixture.Body = fixture.Body with { TotalCount = 2 };
        await fixture.SaveCandidateAsync(7);
        await fixture.PublishAsync();
        Assert.Equal("completed", fixture.Item.Status);
        Assert.Equal(0, fixture.CapacityReads);
        Assert.Equal(WorkflowJobKinds.MultiInstanceDispatch, Assert.Single(fixture.Enqueued).Kind);
    }

    [Fact]
    public async Task Failed_publication_does_not_erase_the_committed_candidate()
    {
        var fixture = new Fixture { RejectSharedWrite = true };
        await fixture.SaveCandidateAsync(7, shared: true);
        var saved = fixture.Item.Candidate!.Value.GetRawText();
        await Assert.ThrowsAsync<WorkflowConflictException>(() => fixture.PublishAsync());
        Assert.Equal(saved, fixture.Item.Candidate.Value.GetRawText());
        Assert.Equal(0, fixture.Completions);
        Assert.Empty(fixture.Enqueued);
    }

    private sealed class Fixture
    {
        public readonly Guid ActivationId = Guid.NewGuid();
        public readonly Guid LeaseToken = Guid.NewGuid();
        public AutomaticMultiInstanceRecord Body;
        public AutomaticMultiInstanceItemRecord Item;
        public WorkflowJobRecord Job;
        public WorkflowModel Definition;
        public AutomaticMultiInstanceCoordinator Coordinator;
        public bool LeaseAlive = true;
        public bool RejectSharedWrite;
        public int CandidateSaves, Writes, Completions, RefreshAcknowledgements, Admissions, CapacityReads, StageSaves, SharedReads;
        public Dictionary<string, JsonElement> SharedValues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Active = 1, Pending;
        public long SharedVersion = 1, OpenJobs = 1;
        public JsonElement? PublishedValue;
        public IReadOnlyList<WorkflowVariableWrite> PublishedWrites = [];
        public ActorContext? PublishedActor;
        public SequenceFlowInfoSnapshot? BodyFlowInfo;
        public CallInvocationRecord? Invocation;
        public CallResultRecord? CallResult;
        public Guid? CurrentActivation;
        public int JobCompletions;
        public int ResultReadySaves;
        public TimeSpan? ReleasedFor;
        public string? OpenedIncidentType;
        public List<WorkflowJobCreateRecord> Enqueued { get; } = [];

        public Fixture()
        {
            var now = DateTimeOffset.UtcNow;
            Body = new(1, 2, 3, 4, ActivationId, 5, "serviceTask", "parallel", "active", "running",
                "results", "instance", null, 1, 9, 1000, 4, 1, 0, 0, 0, 2, null, 0, now, now, null);
            Item = new(6, 2, 1, 0, "active", "execution", JsonSerializer.SerializeToElement(new { source = "frozen" }),
                null, null, null, null, null, now, now, now, null);
            Job = JsonSerializer.SerializeToElement(new
            {
                Id = 7L, InstanceId = 2L, WorkflowDefinitionId = 4L, WorkflowKey = "body", TokenId = 3L,
                MultiInstanceExecutionId = 1L, AutomaticMultiInstanceItemId = 6L, ActivationId,
                NodeId = 5, NodeName = "Body", NodeType = "serviceTask", Kind = "multiInstanceItem",
                WorkerId = "worker", LeaseToken, LeaseGeneration = 1L, Status = "leased"
            }).Deserialize<WorkflowJobRecord>()!;
            Definition = new()
            {
                Id = "body", Variables =
                [new() { Name = "sharedOutput", Scope = "shared", SharedKey = "counter", Access = "readWrite", DataType = "number" }],
                FlowNodes = [new()
                {
                    Id = 5, Type = "serviceTask", Service = new()
                    {
                        OutputMappings = [new() { Variable = "local", DataType = "number" }, new() { Variable = "sharedOutput", DataType = "number" }]
                    }, MultiInstance = new()
                    {
                        CollectionVariable = "items", ResultVariable = "results", OutputVariables = [new() { Name = "local", DataType = "number" }]
                    }
                }]
            };
            Coordinator = new(
                Proxy<IAutomaticMultiInstanceRepository>(Repository), Proxy<IWorkflowJobRepository>(Jobs),
                Proxy<IWorkflowVariableStore>(Variables), Proxy<ISharedVariableAccessPlanCache>((_, _) =>
                    SharedVariableAccessPlanner.Build(Definition, ConditionalEventDependencyPlan.Empty)),
                runtime: Proxy<IWorkflowRuntimeRepository>((method, _) => method switch
                {
                    "GetInstanceAsync" => Task.FromResult<WorkflowInstanceRecord?>(JsonSerializer.SerializeToElement(new
                        { Id = 2L, WorkflowDefinitionId = 4L, Status = "running" }).Deserialize<WorkflowInstanceRecord>()),
                    "GetExecutionTokenAsync" => Task.FromResult<ExecutionTokenRecord?>(JsonSerializer.SerializeToElement(new
                        { Id = 3L, InstanceId = 2L, NodeId = 5, Status = "active", ActivationId = CurrentActivation ?? ActivationId }).Deserialize<ExecutionTokenRecord>()),
                    "AddTokenHistoryAsync" => Task.CompletedTask,
                    _ => throw new NotSupportedException(method)
                }),
                unitOfWork: Proxy<IUnitOfWork>((method, _) => throw new NotSupportedException(method)),
                callActivities: Proxy<ICallActivityRepository>((method, _) => method switch
                {
                    "GetInvocationAsync" => Task.FromResult(Invocation),
                    "GetResultAsync" => Task.FromResult(CallResult),
                    _ => throw new NotSupportedException(method)
                }));
        }

        public WorkflowJobFence Fence => new(7, "worker", LeaseToken, 1);
        public void MakeDispatch(long generation)
        {
            Body = Body with { ControlGeneration = 1, AdmissionBlockedReason = "queueCapacity" };
            Job = Job with { Kind = WorkflowJobKinds.MultiInstanceDispatch, Status = WorkflowJobStatuses.Running,
                AutomaticMultiInstanceItemId = null, MultiInstanceControlGeneration = 1, LeaseGeneration = generation };
        }
        public Task<AutomaticMultiInstanceDispatchResult> DispatchAsync() => Coordinator.DispatchAsync(1, ActivationId,
            Fence with { LeaseGeneration = Job.LeaseGeneration }, Template, 2, CancellationToken.None);
        public WorkflowJobCreateRecord Template => new()
        {
            InstanceId = 2, TokenId = 3, WorkflowDefinitionId = 4, WorkflowKey = "body", ActivationId = ActivationId,
            NodeId = 5, NodeName = "Body", NodeType = "serviceTask", Kind = "asyncBefore", Phase = "asyncBefore",
            QueueClass = "activity", DueAt = DateTimeOffset.UtcNow
        };
        public Task<bool> SaveCandidateAsync(int value, bool shared = false) => Coordinator.SaveCandidateAsync(1, 6,
            ActivationId, Fence, Definition, new(new Dictionary<string, JsonElement> { ["local"] = JsonSerializer.SerializeToElement(value) },
                shared ? new Dictionary<string, JsonElement> { ["sharedOutput"] = JsonSerializer.SerializeToElement(value) } : new(),
                shared ? new Dictionary<string, long> { ["sharedOutput"] = 1 } : new(), new Dictionary<string, JsonElement>()), CancellationToken.None);
        public Task<bool> PublishAsync() => Coordinator.PublishCandidateAsync(1, 6, ActivationId, Fence, Definition,
            new("causal", [], new Dictionary<string, string>()), Template, CancellationToken.None);

        private object? Repository(string method, object?[] args)
        {
            switch (method)
            {
                case "GetAsync": return Task.FromResult<AutomaticMultiInstanceRecord?>(Body);
                case "HasCurrentOwnerAsync": return Task.FromResult((CurrentActivation ?? ActivationId) == ActivationId);
                case "GetItemAsync": return Task.FromResult<AutomaticMultiInstanceItemRecord?>(Item);
                case "GetInputSnapshotAsync": return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                    { collection = new[] { 42 }, instanceInputs = new { frozenInput = 7 }, flowInfo = BodyFlowInfo }));
                case "SaveStageAsync":
                    StageSaves++; Item = Item with { StagedInput = (JsonElement)args[3]! }; return Task.FromResult(true);
                case "GetProgressAsync": return Task.FromResult<IReadOnlyList<AutomaticMultiInstanceProgressRecord>>([new(Body, Active, Pending)]);
                case "SaveCandidateAsync":
                    CandidateSaves++; Item = Item with { Candidate = (JsonElement)args[3]! }; return Task.FromResult(true);
                case "CompleteItemAsync":
                    Completions++; Item = Item with { Status = "completed", Result = (JsonElement)args[3]! };
                    Body = Body with { CompletedCount = Body.CompletedCount + 1 }; return Task.FromResult(true);
                case "SetControlStateAsync":
                    Body = Body with { Phase = (string)args[2]!, AdmissionBlockedReason = (string?)args[3], ControlGeneration = Body.ControlGeneration + 1 };
                    return Task.FromResult(Body.ControlGeneration);
                case "SetAdmissionBlockedReasonAsync":
                    Body = Body with { AdmissionBlockedReason = (string?)args[2] }; return Task.FromResult(true);
                case "SupersedeControlJobsAsync": return Task.FromResult(0);
                case "AcknowledgePublicationFenceRefreshAsync": RefreshAcknowledgements++; return Task.FromResult(true);
                case "AdmitAsync": Admissions++; return Task.FromResult<IReadOnlyList<AutomaticMultiInstanceItemRecord>>([Item]);
                case "SaveCompletionIntentAsync":
                    Body = Body with { CompletionIntent = (JsonElement)args[2]! }; return Task.FromResult(true);
                case "FailItemAsync": Item = Item with { Status = "failed" }; return Task.FromResult(true);
                case "CancelUnfinishedItemsAsync": return Task.FromResult(0);
                default: throw new NotSupportedException(method);
            }
        }
        private object? Jobs(string method, object?[] args)
        {
            switch (method)
            {
                case "GetForUpdateAsync": return Task.FromResult<WorkflowJobRecord?>(Job);
                case "IsLeaseAliveAsync": return Task.FromResult(LeaseAlive);
                case "CountOpenByInstanceAsync": CapacityReads++; return Task.FromResult(OpenJobs);
                case "ListOpenByInstanceAsync": return Task.FromResult<IReadOnlyList<WorkflowJobRecord>>([]);
                case "EnqueueAsync": Enqueued.Add((WorkflowJobCreateRecord)args[0]!); return Task.FromResult(Job);
                case "EnqueueManyAsync": Enqueued.AddRange((IReadOnlyList<WorkflowJobCreateRecord>)args[0]!); return Task.FromResult<IReadOnlyList<WorkflowJobRecord>>([Job]);
                case "SaveResultReadyAsync":
                    ResultReadySaves++; Job = Job with { Status = WorkflowJobStatuses.ResultReady }; return Task.FromResult(true);
                case "ReleaseResultReadyLeaseAsync": ReleasedFor = (TimeSpan)args[1]!; return Task.FromResult(true);
                case "OpenIncidentAsync":
                    OpenedIncidentType = (string)args[1]!;
                    return Task.FromResult<WorkflowIncidentRecord?>(new(1, 7, 2, 4, "body", 5, "Body", OpenedIncidentType,
                        "open", "Capacity", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null));
                case "CompleteAsync": JobCompletions++; return Task.FromResult(true);
                case "CancelOtherJobsByTokenIdsAsync": return Task.FromResult(0);
                default: throw new NotSupportedException(method);
            }
        }
        private object? Variables(string method, object?[] args)
        {
            switch (method)
            {
                case "MergeEffectiveValuesAsync": SharedReads++; return Task.FromResult(new Dictionary<string, JsonElement>(SharedValues));
                case "LoadSharedValueVersionsAsync": return Task.FromResult<IReadOnlyDictionary<string, long>>(new Dictionary<string, long> { ["sharedOutput"] = SharedVersion });
                case "WriteAsync":
                    if (RejectSharedWrite) throw new WorkflowConflictException("Shared contract changed.");
                    Writes++;
                    PublishedWrites = (IReadOnlyList<WorkflowVariableWrite>)args[3]!;
                    PublishedActor = (ActorContext)args[4]!;
                    PublishedValue = ((IReadOnlyList<WorkflowVariableWrite>)args[3]!).FirstOrDefault()?.Value;
                    return Task.FromResult<IReadOnlyList<WorkflowVariableWriteResult>>([]);
                default: throw new NotSupportedException(method);
            }
        }
    }

    private static T Proxy<T>(Func<string, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, RecordingProxy>();
        ((RecordingProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class RecordingProxy : DispatchProxy
    {
        public Func<string, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!.Name, args ?? []);
    }
}
