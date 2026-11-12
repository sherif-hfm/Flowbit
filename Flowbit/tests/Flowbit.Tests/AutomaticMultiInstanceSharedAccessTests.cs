using System.Reflection;
using System.Collections.Immutable;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class AutomaticMultiInstanceSharedAccessTests
{
    [Fact]
    public void Each_phase_has_only_its_current_targets_and_dependencies()
    {
        var model = Definition();
        var node = model.FlowNodes.Single(node => node.Id == 2);
        node.Service!.Url = "https://example.test/${requestInput}/${source}";
        node.Service.OutputMappings[0].Validation = "output <= validationLimit";
        node.Service.OutputMappings[0].DefaultValue = JsonSerializer.SerializeToElement("${fallbackInput}");
        node.MultiInstance!.OutputVariables![0].DefaultValue = JsonSerializer.SerializeToElement("${localDefault}");
        node.MultiInstance.OutputVariables[0].Validation = "local <= validationLimit";
        model.Variables.AddRange(new[] { "requestInput", "validationLimit", "fallbackInput", "localDefault", "unrelated" }
            .Select(alias => Shared(alias, "tests." + alias)));
        var plan = Plan(model);

        AssertAliases(plan, AutomaticMultiInstanceAccessPhases.Initialize, "aggregate", "source");
        AssertAliases(plan, AutomaticMultiInstanceAccessPhases.StageItem,
            "fallbackInput", "localDefault", "output", "requestInput", "validationLimit");
        AssertAliases(plan, AutomaticMultiInstanceAccessPhases.PublishItem, "output", "validationLimit");
        Assert.DoesNotContain("output", plan.ForAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.StageItem).ReadAliases);
        Assert.DoesNotContain("output", plan.ForAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.PublishItem).ReadAliases);
        AssertAliases(plan, AutomaticMultiInstanceAccessPhases.Complete, "aggregate");
        Assert.Equal(["source"], plan.ForAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.StageItem).FrozenReadAliases);
        Assert.All(plan.AutomaticPhases.Values, phase => Assert.DoesNotContain("unrelated", phase.LockAliases));
    }

    [Fact]
    public void Host_timer_expressions_are_locked_only_during_body_initialization()
    {
        var model = Definition();
        model.Variables.Add(Shared("deadline", "tests.deadline", readOnly: true));
        model.FlowNodes.Add(new() { Id = 5, Type = BpmnFlowNodeTypes.TimerBoundaryEvent, AttachedToRef = 2,
            Timer = new() { TimeDate = "${deadline}" } });
        var plan = Plan(model);
        AssertAliases(plan, AutomaticMultiInstanceAccessPhases.Initialize, "aggregate", "deadline", "source");
        AssertAliases(plan, AutomaticMultiInstanceAccessPhases.Complete, "aggregate");
    }

    [Fact]
    public void Error_scope_selects_only_the_winning_boundary()
    {
        var model = Definition();
        model.Variables.AddRange([Shared("errorA", "tests.error.a"), Shared("errorB", "tests.error.b")]);
        model.FlowNodes.AddRange([
            new() { Id = 5, Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, AttachedToRef = 2, ErrorVariable = "errorA" },
            new() { Id = 6, Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, AttachedToRef = 2, ErrorVariable = "errorB" }
        ]);
        var plan = Plan(model);
        Assert.Equal(["errorA"], plan.SelectAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.Error, 5).LockAliases);
        Assert.Equal(["errorB"], plan.SelectAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.Error, 6).LockAliases);
        Assert.DoesNotContain("errorA", plan.SelectAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.PublishItem).LockAliases);
    }

    [Fact]
    public void Aggregate_and_error_target_self_validation_uses_new_values_while_timer_inputs_remain_live_reads()
    {
        var model = Definition();
        model.Variables.Single(variable => variable.Name == "aggregate").Validation = "aggregate != null";
        var error = Shared("error", "tests.error");
        error.Validation = "error != null";
        model.Variables.Add(error);
        model.FlowNodes.Add(new() { Id = 5, Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, AttachedToRef = 2, ErrorVariable = "error" });
        var plan = Plan(model);
        Assert.DoesNotContain("aggregate", plan.ForAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.Initialize).ReadAliases);
        Assert.DoesNotContain("aggregate", plan.ForAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.Complete).ReadAliases);
        Assert.DoesNotContain("error", plan.ForAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.Error, 5).ReadAliases);
        model.FlowNodes.Add(new() { Id = 6, Type = BpmnFlowNodeTypes.TimerBoundaryEvent, AttachedToRef = 2,
            Timer = new() { TimeDate = "${aggregate}" } });
        Assert.Contains("aggregate", Plan(model).ForAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.Initialize).ReadAliases);
    }

    [Fact]
    public void NCalc_staging_includes_expression_reads_but_publication_does_not_recalculate_them()
    {
        var model = Definition();
        model.Variables.Add(Shared("input", "tests.input"));
        var node = model.FlowNodes.Single(node => node.Id == 2);
        node.Type = BpmnFlowNodeTypes.ScriptTask;
        node.Service = null;
        node.ScriptFormat = ScriptFormats.NCalc;
        node.Assignments = [new() { Variable = "output", Expression = "input + 1" }];
        var plan = Plan(model);
        AssertAliases(plan, AutomaticMultiInstanceAccessPhases.StageItem, "input", "output");
        AssertAliases(plan, AutomaticMultiInstanceAccessPhases.PublishItem, "output");
    }

    [Fact]
    public void A_target_used_as_a_request_input_still_requires_its_current_value()
    {
        var model = Definition();
        var node = model.FlowNodes.Single(node => node.Id == 2);
        node.Service!.Url = "https://example.test/${output}";
        node.Service.OutputMappings[0].Validation = "output > 0";
        var plan = Plan(model);
        Assert.Contains("output", plan.ForAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.StageItem).ReadAliases);
        Assert.DoesNotContain("output", plan.ForAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.PublishItem).ReadAliases);
    }

    [Fact]
    public void Dynamic_script_conservatism_is_limited_to_its_item_phase_and_excludes_reserved_writes()
    {
        var model = Definition();
        var node = model.FlowNodes.Single(node => node.Id == 2);
        node.Type = BpmnFlowNodeTypes.ScriptTask;
        node.Service = null;
        node.ScriptFormat = ScriptFormats.JavaScript;
        var plan = Plan(model);
        var stage = plan.ForAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.StageItem);
        Assert.DoesNotContain("source", stage.LockAliases);
        Assert.Equal(new[] { "output", "downstream" }.Order(), stage.ProducerAliases.Order());
        AssertAliases(plan, AutomaticMultiInstanceAccessPhases.Complete, "aggregate");
    }

    [Fact]
    public void Every_alias_of_the_collection_key_is_frozen_and_cannot_be_a_dynamic_target()
    {
        var model = Definition();
        model.Variables.Add(Shared("sourceAgain", "tests.z"));
        var node = model.FlowNodes.Single(node => node.Id == 2);
        node.Service!.Url = "https://example.test/${sourceAgain}";
        var phase = Plan(model).ForAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.StageItem);
        Assert.Equal(["sourceAgain"], phase.FrozenReadAliases);
        Assert.DoesNotContain("sourceAgain", phase.LockAliases);
        node.Type = BpmnFlowNodeTypes.ScriptTask;
        node.Service = null;
        node.ScriptFormat = ScriptFormats.JavaScript;
        phase = Plan(model).ForAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.StageItem);
        Assert.DoesNotContain("sourceAgain", phase.ProducerAliases);
        Assert.DoesNotContain("sourceAgain", phase.LockAliases);
    }

    [Fact]
    public void Durable_phases_allow_lower_item_keys_and_completion_does_not_prelock_the_source()
    {
        // Initialization holds m,z; dispatch starts a new segment with a.
        // Completion holds m then n. The old whole-node scope held z and
        // incorrectly rejected this n continuation.
        Validate(Definition());
    }

    [Fact]
    public void Completion_rejects_a_descending_shared_continuation()
    {
        var model = Definition();
        model.Variables.Single(variable => variable.Name == "downstream").SharedKey = "tests.b";
        var error = Assert.Throws<WorkflowDomainException>(() => Validate(model));
        Assert.Contains("aggregate completion", error.Message);
        Assert.Contains("tests.b", error.Message);
    }

    [Fact]
    public void AsyncAfter_is_a_real_boundary_after_aggregate_publication()
    {
        var model = Definition();
        model.Variables.Single(variable => variable.Name == "downstream").SharedKey = "tests.b";
        model.FlowNodes.Single(node => node.Id == 2).AsyncAfter = true;
        Validate(model);
    }

    [Fact]
    public void Error_publication_is_proved_after_aggregate_without_sorting_their_union()
    {
        var model = Definition();
        model.Variables.Add(Shared("error", "tests.b"));
        model.FlowNodes.Add(new() { Id = 5, Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, AttachedToRef = 2, ErrorVariable = "error" });
        model.SequenceFlows.Add(new() { Id = 5, SourceRef = 5, TargetRef = 4 });
        var error = Assert.Throws<WorkflowDomainException>(() => Validate(model));
        Assert.Contains("error boundary #5", error.Message);
        Assert.Contains("tests.b", error.Message);
    }

    [Fact]
    public void Initialization_reset_proves_atomic_conditional_continuations_after_source_locks()
    {
        var model = Definition();
        var aggregate = model.Variables.Single(variable => variable.Name == "aggregate");
        aggregate.Scope = VariableScopes.Instance;
        aggregate.SharedKey = null;
        aggregate.Access = null;
        model.FlowNodes.Add(new() { Id = 5, Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = 2,
            Conditional = new() { Condition = "aggregate != null" } });
        model.SequenceFlows.Add(new() { Id = 5, SourceRef = 5, TargetRef = 3 });
        var conditionals = new ConditionalEventDependencyPlan(
            ImmutableDictionary<int, ConditionalEventPlanEntry>.Empty.Add(5,
                new(5, "aggregate != null", ConditionalEventDeliveryModes.Atomic, ["aggregate"], true, 2, true)),
            ImmutableDictionary<string, ImmutableArray<int>>.Empty.Add("aggregate", [5]));
        var error = Assert.Throws<WorkflowDomainException>(() => SharedVariableTransactionLockOrderValidator.Validate(
            model, SharedVariableAccessPlanner.Build(model, conditionals), conditionals));
        Assert.Contains("durable pre-node finalization", error.Message);
        Assert.Contains("tests.n", error.Message);
        Assert.Contains("tests.z", error.Message);
    }

    [Fact]
    public async Task Exact_phase_loads_only_its_keys_and_refuses_unset_reads_or_contract_drift()
    {
        var definition = Definition();
        definition.Variables.Add(Shared("dependency", "tests.dep", readOnly: true));
        definition.FlowNodes.Single(node => node.Id == 2).Service!.OutputMappings[0].Validation = "output <= dependency";
        var keysRead = new List<string[]>();
        var hasValue = true;
        var drift = false;
        var outputHasValue = true;
        var sourceHasValue = true;
        var sourceExists = true;
        var shared = Proxy<ISharedVariableRepository>((method, args) =>
        {
            var keys = ((IReadOnlyCollection<string>)args[0]!).ToArray();
            keysRead.Add(keys);
            return method switch
            {
                "LoadCurrentAsync" or "LockCurrentAsync" => Task.FromResult<IReadOnlyDictionary<string, SharedVariableCurrentValueRecord>>(
                    keys.Where(key => (hasValue || key != "tests.dep") && (outputHasValue || key != "tests.a")
                            && (sourceHasValue || key != "tests.z")).ToDictionary(key => key,
                        key => new SharedVariableCurrentValueRecord(1, key, "number", false, false,
                            JsonSerializer.SerializeToElement(5), 1, DateTimeOffset.UnixEpoch))),
                "GetManyByKeyAsync" => Task.FromResult<IReadOnlyDictionary<string, SharedVariableRecord>>(keys.Where(key => sourceExists || key != "tests.z").ToDictionary(key => key,
                    key => new SharedVariableRecord(1, key, drift ? "string" : "number", false, false, null, null,
                        hasValue, null, "active", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null))),
                _ => throw new NotSupportedException(method)
            };
        });
        var runtime = Proxy<IWorkflowRuntimeRepository>((method, _) => throw new NotSupportedException(method));
        var store = new WorkflowVariableStore(runtime, shared);
        var scope = Plan(definition).SelectAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.PublishItem);
        var values = await store.MergeEffectiveValuesAsync(definition, new Dictionary<string, JsonElement>(), scope, CancellationToken.None);
        Assert.Equal(["dependency", "output"], values.Keys.Order());
        Assert.All(keysRead.SelectMany(keys => keys), key => Assert.Contains(key, new[] { "tests.a", "tests.dep" }));
        outputHasValue = false;
        values = await store.MergeEffectiveValuesAsync(definition, new Dictionary<string, JsonElement>(), scope, CancellationToken.None);
        Assert.Equal(["dependency"], values.Keys);
        hasValue = false;
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.MergeEffectiveValuesAsync(definition,
            new Dictionary<string, JsonElement>(), scope, CancellationToken.None));
        drift = false;
        sourceHasValue = false;
        var initialize = Plan(definition).SelectAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.Initialize);
        var invalid = await Assert.ThrowsAsync<AutomaticMultiInstanceValueException>(() => store.MergeEffectiveValuesAsync(definition,
            new Dictionary<string, JsonElement>(), initialize, CancellationToken.None));
        Assert.Equal(AutomaticMultiInstanceValues.SourceInvalid, invalid.Code);
        sourceExists = false;
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.MergeEffectiveValuesAsync(definition,
            new Dictionary<string, JsonElement>(), initialize, CancellationToken.None));
        hasValue = true;
        drift = true;
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.MergeEffectiveValuesAsync(definition,
            new Dictionary<string, JsonElement>(), scope, CancellationToken.None));
    }

    private static void AssertAliases(SharedVariableAccessPlan plan, string phase, params string[] aliases) =>
        Assert.Equal(aliases.Order(), plan.ForAutomaticPhase(2, phase).LockAliases.Order());

    private static SharedVariableAccessPlan Plan(WorkflowModel model) =>
        SharedVariableAccessPlanner.Build(model, ConditionalEventDependencyPlan.Empty);

    private static void Validate(WorkflowModel model) =>
        SharedVariableTransactionLockOrderValidator.Validate(model, Plan(model), ConditionalEventDependencyPlan.Empty);

    private static VariableModel Shared(string name, string key, bool readOnly = false) => new()
    {
        Name = name, Scope = VariableScopes.Shared, SharedKey = key,
        Access = readOnly ? SharedVariableAccessModes.Read : SharedVariableAccessModes.ReadWrite,
        DataType = WorkflowVariableTypes.Number
    };

    private static WorkflowModel Definition() => new()
    {
        Id = "automatic-access", InitialEventId = 1,
        Variables = [Shared("source", "tests.z", readOnly: true), Shared("output", "tests.a"),
            Shared("aggregate", "tests.m"), Shared("downstream", "tests.n")],
        FlowNodes = [
            new() { Id = 1, Type = BpmnFlowNodeTypes.StartEvent },
            new() { Id = 2, Type = BpmnFlowNodeTypes.ServiceTask, AsyncBefore = true,
                Service = new() { OutputMappings = [new() { Variable = "output", DataType = "number" }] },
                MultiInstance = new() { CollectionVariable = "source", ResultVariable = "aggregate", CompletionEvaluation = "afterAll",
                    OutputVariables = [new() { Name = "local", DataType = "number" }] } },
            new() { Id = 3, Type = BpmnFlowNodeTypes.ScriptTask, ScriptFormat = ScriptFormats.NCalc,
                Assignments = [new() { Variable = "downstream", Expression = "1" }] },
            new() { Id = 4, Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows = [new() { Id = 1, SourceRef = 1, TargetRef = 2 },
            new() { Id = 2, SourceRef = 2, TargetRef = 3 }, new() { Id = 3, SourceRef = 3, TargetRef = 4 }]
    };

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
