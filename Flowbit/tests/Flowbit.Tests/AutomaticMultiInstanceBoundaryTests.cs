using System.Reflection;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class AutomaticMultiInstanceBoundaryTests
{
    [Theory]
    [InlineData(BpmnFlowNodeTypes.ServiceTask)]
    [InlineData(BpmnFlowNodeTypes.ScriptTask)]
    [InlineData(BpmnFlowNodeTypes.CallActivity)]
    public void Conditional_entry_precedes_body_but_timers_require_initialization(string kind)
    {
        var f = new Fixture(kind);
        foreach (var wait in new string?[] { null, ExecutionTokenWaitStates.AsyncBefore, WorkflowJobKinds.MultiInstanceStart })
        {
            var queued = f.Token with { WaitState = wait, WaitingMultiInstanceExecutionId = null };
            Assert.True(Eligible(f.Node, queued, null, timer: false));
            Assert.False(Eligible(f.Node, queued, null, timer: true));
        }
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.ServiceTask)]
    [InlineData(BpmnFlowNodeTypes.ScriptTask)]
    [InlineData(BpmnFlowNodeTypes.CallActivity)]
    public void Normal_completion_and_incidents_remain_observable_but_selected_error_and_async_after_do_not(string kind)
    {
        var f = new Fixture(kind);
        foreach (var phase in new[] { "running", "dispatch", "completing" })
        foreach (var blocked in new string?[] { null, "itemIncident", "queueCapacity" })
        foreach (var timer in new[] { false, true })
            Assert.True(Eligible(f.Node, f.Token, f.Body with { Phase = phase, AdmissionBlockedReason = blocked }, timer));
        foreach (var timer in new[] { false, true })
        {
            Assert.False(Eligible(f.Node, f.Token, f.Body with { Phase = "errorCompletion" }, timer));
            Assert.False(Eligible(f.Node, f.Token, f.Body with { CompletionIntent = JsonSerializer.SerializeToElement(new { boundaryNodeId = 3 }) }, timer));
            Assert.False(Eligible(f.Node, f.Token, f.Body with { Status = "completed" }, timer));
            Assert.False(Eligible(f.Node, f.Token with { WaitState = ExecutionTokenWaitStates.AsyncAfter }, f.Body, timer));
            Assert.False(Eligible(f.Node, f.Token with { ActivationId = Guid.NewGuid() }, f.Body, timer));
            Assert.False(Eligible(f.Node, f.Token with { WaitingMultiInstanceExecutionId = 99 }, f.Body, timer));
        }
    }

    [Fact]
    public void Ordinary_call_remains_ineligible()
    {
        var f = new Fixture(BpmnFlowNodeTypes.CallActivity);
        f.Node.MultiInstance = null;
        Assert.False(Eligible(f.Node, f.Token, null, false));
        Assert.False(Eligible(f.Node, f.Token, null, true));
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.ServiceTask)]
    [InlineData(BpmnFlowNodeTypes.ScriptTask)]
    [InlineData(BpmnFlowNodeTypes.CallActivity)]
    public async Task Host_timer_uses_persisted_initialization_instant_and_does_not_rearm(string kind)
    {
        var f = new Fixture(kind);
        var lifecycle = f.Engine.CreateAutomaticMultiInstanceLifecycle(f.Instance, f.Definition, f.Actor);
        await lifecycle.InstallHostTimersAsync(f.Body, CancellationToken.None);
        var subscription = Assert.Single(f.Subscriptions);
        Assert.Equal(f.Body.CreatedAt.AddMinutes(5), subscription.NextDueAt);
        Assert.Equal(f.Token.Id, subscription.TokenId);
        Assert.Equal(f.Token.ActivationId, subscription.ActivationId);
        Assert.Equal(1, f.EnqueuedTimers);
        await lifecycle.InstallHostTimersAsync(f.Body with { UpdatedAt = f.Body.UpdatedAt.AddHours(1) }, CancellationToken.None);
        Assert.Single(f.Subscriptions);
        Assert.Equal(1, f.EnqueuedTimers);
        f.Subscriptions[0] = subscription with { Status = TimerSubscriptionStatuses.Completed };
        await lifecycle.InstallHostTimersAsync(f.Body, CancellationToken.None);
        Assert.Equal(1, f.EnqueuedTimers);
    }

    [Fact]
    public async Task Queued_token_cancellation_never_cancels_another_tokens_body()
    {
        var f = new Fixture(BpmnFlowNodeTypes.ServiceTask);
        var queued = f.Token with { Id = 12, ActivationId = Guid.NewGuid(), WaitingMultiInstanceExecutionId = null,
            WaitState = ExecutionTokenWaitStates.AsyncBefore };
        await CancelAsync(f.Engine, queued);
        Assert.Empty(f.CancelledBodies);
        Assert.Empty(f.SettledBodies);
        await CancelAsync(f.Engine, f.Token);
        Assert.Equal(new[] { f.Body.Id }, f.CancelledBodies);
        Assert.Equal(new[] { f.Body.Id }, f.SettledBodies);
        Assert.Equal(0, f.EnqueuedTimers);
    }

    [Fact]
    public async Task Initialization_refuses_an_expired_body_without_arming_timers()
    {
        var f = new Fixture(BpmnFlowNodeTypes.ServiceTask);
        f.Token = f.Token with { ActivationId = Guid.NewGuid() };
        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.Engine
            .CreateAutomaticMultiInstanceLifecycle(f.Instance, f.Definition, f.Actor)
            .InstallHostTimersAsync(f.Body, CancellationToken.None));
        Assert.Empty(f.Subscriptions);
        Assert.Equal(0, f.EnqueuedTimers);
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.ServiceTask)]
    [InlineData(BpmnFlowNodeTypes.ScriptTask)]
    [InlineData(BpmnFlowNodeTypes.CallActivity)]
    public async Task Interrupt_capture_settles_only_host_body_and_fences_jobs_and_calls_without_aggregate_write(string kind)
    {
        var f = new Fixture(kind);
        var boundary = new FlowNodeModel { Id = 4, Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = 2 };
        var subscription = new ConditionalBoundarySubscriptionRecord(30, 1, 7, "boundary-core", 11,
            f.Token.ActivationId, 4, "Stop", 2, 40, "true", "atomic", true, false, 0, "active",
            f.Body.CreatedAt, f.Body.CreatedAt, null);
        var riseType = typeof(WorkflowEngineService).GetNestedType("ConditionalBoundaryRise", BindingFlags.NonPublic)!;
        var rise = Activator.CreateInstance(riseType, subscription, f.Token, boundary,
            new SequenceFlowModel { Id = 40, SourceRef = 4, TargetRef = 5 }, Array.Empty<string>());
        var task = (Task<ExecutionTokenRecord>)typeof(WorkflowEngineService).GetMethod("CaptureConditionalBoundaryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Engine,
                [f.Instance, f.Actor, rise, CancellationToken.None])!;
        var captured = await task;
        Assert.Equal(4, captured.NodeId);
        Assert.Null(captured.WaitingMultiInstanceExecutionId);
        Assert.Equal(new[] { f.Body.Id }, f.CancelledBodies);
        Assert.Equal(new[] { f.Body.Id }, f.SettledBodies);
        Assert.Equal(new[] { f.Body.TokenId }, f.CancelledJobTokens);
        Assert.Equal(new[] { f.Body.TokenId }, f.AbandonedCallTokens);
        Assert.Equal(1, f.TimerCancellationCount);
        Assert.Equal(1, f.ConditionCancellationCount);
        // Any variable-store call is unexpected in this fixture: interruption
        // must not reset or publish a full/partial aggregate.
    }

    private static bool Eligible(FlowNodeModel node, ExecutionTokenRecord token, AutomaticMultiInstanceRecord? body, bool timer) =>
        WorkflowEngineService.IsAutomaticBoundaryHostEligible(node, token, body, timer);

    private static async Task CancelAsync(WorkflowEngineService engine, ExecutionTokenRecord token) =>
        await (Task)typeof(WorkflowEngineService).GetMethod("CancelAutomaticBodyForBoundaryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [token, "timerFired", CancellationToken.None])!;

    private sealed class Fixture
    {
        public FlowNodeModel Node { get; }
        public WorkflowModel Definition { get; }
        public ExecutionTokenRecord Token { get; set; }
        public AutomaticMultiInstanceRecord Body { get; }
        public WorkflowInstanceRecord Instance { get; }
        public WorkflowEngineService Engine { get; }
        public ActorContext Actor { get; } = new("starter", [], new Dictionary<string, string>());
        public List<TimerSubscriptionRecord> Subscriptions { get; } = [];
        public List<long> CancelledBodies { get; } = [];
        public List<long> SettledBodies { get; } = [];
        public List<long> CancelledJobTokens { get; } = [];
        public List<long> AbandonedCallTokens { get; } = [];
        public int TimerCancellationCount { get; private set; }
        public int ConditionCancellationCount { get; private set; }
        public int EnqueuedTimers { get; private set; }

        public Fixture(string kind)
        {
            var now = DateTimeOffset.Parse("2026-11-11T10:00:00Z");
            var activation = Guid.NewGuid();
            Node = new() { Id = 2, Type = kind, MultiInstance = new() { CollectionVariable = "items", ResultVariable = "results" } };
            Definition = new() { Id = "boundary-core", FlowNodes = [Node,
                new() { Id = 3, Name = "Timeout", Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    AttachedToRef = 2, CancelActivity = true, Timer = new() { TimeDuration = "PT5M" } }] };
            Token = new(11, 1, 2, "Body", null, kind, null, null, "active", null, null, null, null, [], null, now, now,
                ActivationId: activation, WaitState: ExecutionTokenWaitStates.AutomaticMultiInstance)
                { WaitingMultiInstanceExecutionId = 10 };
            Body = new(10, 1, 11, 7, activation, 2, kind, "parallel", "active", "running", "results", "instance", null,
                1, 100, 100, 2, 2, 0, 0, 0, 0, null, 0, now, now, null);
            Instance = new(1, 7, "boundary-core", null, null, null, 11, 2, null, "running", null, "starter", now, now);
            Engine = new(Proxy<IWorkflowDefinitionRepository>(Unexpected), Proxy<IWorkflowRuntimeRepository>(Runtime),
                Proxy<IWorkflowJobRepository>(Jobs), Proxy<ITimerSubscriptionRepository>(Timers),
                Proxy<IUserDelegationRepository>(Unexpected), Proxy<IUnitOfWork>(Unexpected),
                Proxy<IServiceTaskInvoker>(Unexpected), Proxy<IScriptEvaluator>(Unexpected), new(), TimeProvider.System,
                Proxy<IWorkflowSettingsRepository>(Unexpected), Proxy<IEngineSettingsRepository>((method, _) =>
                    method == "GetByKeyAsync" ? Task.FromResult<EngineSettingRecord?>(null) : Unexpected(method, [])),
                NullLogger<WorkflowEngineService>.Instance, Proxy<IWorkflowInstanceProjectionService>(Unexpected),
                workflowVariables: Proxy<IWorkflowVariableStore>(Unexpected),
                conditionalBoundarySubscriptions: Proxy<IConditionalBoundarySubscriptionRepository>((method, args) =>
                {
                    if (method != "CancelOtherForTokenAsync") return Unexpected(method, args);
                    Assert.Equal(11L, args[1]);
                    Assert.Equal(30L, args[2]);
                    ConditionCancellationCount++;
                    return Task.FromResult(1);
                }),
                callActivities: Proxy<ICallActivityRepository>((method, args) =>
                {
                    if (method == "ListUnsettledForTokensAsync") return Task.FromResult<IReadOnlyList<CallInvocationRecord>>([]);
                    if (method != "ListUnsettledAutomaticForTokensAsync") return Unexpected(method, args);
                    AbandonedCallTokens.AddRange((IReadOnlyCollection<long>)args[1]!);
                    return Task.FromResult<IReadOnlyList<CallInvocationRecord>>([]);
                }),
                automaticMultiInstances: Proxy<IAutomaticMultiInstanceRepository>(Bodies));
        }

        private object? Runtime(string name, object?[] args)
        {
            switch (name)
            {
                case "GetExecutionTokenAsync": return Task.FromResult<ExecutionTokenRecord?>(Token);
                case "LoadLatestVariableVersionsAsync": return Task.FromResult<IReadOnlyList<InstanceVariableVersionRecord>>([]);
                case "CancelOpenUserTasksForTokensAsync": case "CancelActiveMultiInstancesForTokensAsync": return Task.CompletedTask;
                case "UpdateExecutionTokenAsync":
                    var next = (CurrentNodeSnapshot)args[1]!;
                    Token = Token with { NodeId = next.Id, NodeType = next.Type, WaitingMultiInstanceExecutionId = null,
                        WaitState = null, WaitingJobId = null, ActivationId = Guid.NewGuid() };
                    return Task.CompletedTask;
                default: return Unexpected(name, args);
            }
        }

        private object? Bodies(string name, object?[] args)
        {
            switch (name)
            {
                case "GetAsync": case "FindOpenAsync": return Task.FromResult<AutomaticMultiInstanceRecord?>(Body);
                case "CancelUnfinishedItemsAsync": CancelledBodies.Add((long)args[0]!); return Task.FromResult(2);
                case "SettleAsync": SettledBodies.Add((long)args[0]!); return Task.FromResult(true);
                default: return Unexpected(name, args);
            }
        }

        private object? Jobs(string name, object?[] args)
        {
            if (name == "CountOpenByInstanceAsync") return Task.FromResult(0L);
            if (name == "CancelOtherJobsByTokenIdsAsync")
            {
                CancelledJobTokens.AddRange((IReadOnlyCollection<long>)args[1]!);
                return Task.FromResult(1);
            }
            if (name != "EnqueueAsync") return Unexpected(name, args);
            var create = (WorkflowJobCreateRecord)args[0]!;
            Assert.Equal(WorkflowJobKinds.TimerBoundary, create.Kind);
            Assert.Equal(Body.CreatedAt.AddMinutes(5), create.DueAt);
            EnqueuedTimers++;
            return Task.FromResult<WorkflowJobRecord>(null!);
        }

        private object? Timers(string name, object?[] args)
        {
            if (name == "ListForActivationAsync") return Task.FromResult<IReadOnlyList<TimerSubscriptionRecord>>(Subscriptions);
            if (name == "CancelByTokenIdsAsync")
            {
                Assert.Equal(new long[] { 11 }, (IReadOnlyCollection<long>)args[1]!);
                TimerCancellationCount++;
                return Task.FromResult(1);
            }
            if (name != "CreateAsync") return Unexpected(name, args);
            var create = (TimerSubscriptionCreateRecord)args[0]!;
            var row = new TimerSubscriptionRecord(1, create.InstanceId, create.WorkflowDefinitionId, create.WorkflowKey,
                create.TokenId, create.ActivationId, create.TimerNodeId, create.TimerNodeName, create.AttachedToNodeId,
                create.ScheduleKind, create.ScheduleExpression, create.CancelActivity, "active", create.NextDueAt,
                0, Body.CreatedAt, Body.CreatedAt, null);
            Subscriptions.Add(row);
            return Task.FromResult(row);
        }
    }

    private static object Unexpected(string name, object?[] _) => throw new InvalidOperationException($"Unexpected call {name}.");
    private static T Proxy<T>(Func<string, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, RecordingProxy>();
        ((RecordingProxy)(object)proxy).Handler = handler;
        return proxy;
    }
    public class RecordingProxy : DispatchProxy
    {
        public Func<string, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args ?? []);
    }
}
