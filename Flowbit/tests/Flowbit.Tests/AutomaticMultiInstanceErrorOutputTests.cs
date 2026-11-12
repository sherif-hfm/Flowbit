using System.Reflection;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class AutomaticMultiInstanceErrorOutputTests
{
    [Fact]
    public async Task Selected_route_rejects_invalid_instance_diagnostic_before_writing_or_continuing()
    {
        var f = new Fixture();
        f.SharedValues["tests.permitted"] = Json(false);
        f.Target.Validation = "error == 'private-expression' and permitted";

        var error = await Assert.ThrowsAsync<WorkflowConflictException>(() => f.ContinueAsync());

        Assert.Equal("The automatic error target rejected the saved failure code.", error.Message);
        Assert.Empty(f.InstanceWrites);
        Assert.Empty(f.SharedWrites);
        Assert.Empty(f.TokenUpdates);
        Assert.Null(f.InstanceStatus);
        Assert.Equal(["tests.permitted"], Assert.Single(f.LockedKeys));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Error_validation_uses_frozen_collection_and_current_selected_shared_dependency(bool sharedSource)
    {
        var f = new Fixture(sharedSource: sharedSource);
        f.Target.Validation = "error == 'multi_instance_result_invalid' and permitted and Length(items) == 2"
            + (sharedSource ? " and Length(sameSource) == 2" : string.Empty);
        f.SharedValues["tests.permitted"] = Json(false);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.WriteAsync());
        Assert.Empty(f.InstanceWrites);

        f.SharedValues["tests.permitted"] = Json(true);
        await f.WriteAsync();

        var write = Assert.Single(f.InstanceWrites);
        Assert.Equal("error", write.Alias);
        Assert.Equal(AutomaticMultiInstanceValues.ResultInvalid, write.Value.GetString());
        Assert.Equal(f.Boundary.Id, write.SourceActionId);
        Assert.Equal("starter", write.SetBy);
        Assert.Null(write.NodeExecutionId);
        Assert.All(f.LockedKeys, keys => Assert.Equal(["tests.permitted"], keys));
        Assert.DoesNotContain(f.ReadKeys.SelectMany(keys => keys), key =>
            key is "tests.source" or "tests.aggregate" or "tests.otherError");
    }

    [Fact]
    public async Task Unset_shared_validation_dependency_prevents_the_error_write()
    {
        var f = new Fixture();
        f.SharedValues.Remove("tests.permitted");

        var error = await Assert.ThrowsAsync<WorkflowConflictException>(() => f.WriteAsync());

        Assert.Equal("An automatic multi-instance shared input has no current value.", error.Message);
        Assert.Empty(f.InstanceWrites);
    }

    [Fact]
    public async Task Declared_instance_target_type_is_validated_before_the_write()
    {
        var f = new Fixture();
        f.Target.DataType = WorkflowVariableTypes.Number;
        var error = await Assert.ThrowsAsync<WorkflowConflictException>(() => f.WriteAsync());
        Assert.Equal("The automatic error target rejected the saved failure code.", error.Message);
        Assert.Empty(f.InstanceWrites);
    }

    [Fact]
    public async Task Shared_error_first_write_validates_value_contract_and_keeps_audit_attribution()
    {
        var f = new Fixture(sharedTarget: true);
        Assert.False(f.Catalog["tests.error"].HasValue);

        await f.WriteAsync();

        var write = Assert.Single(f.SharedWrites);
        Assert.Equal("tests.error", write.Key);
        Assert.Equal(AutomaticMultiInstanceValues.ResultInvalid, write.Value!.Value.GetString());
        Assert.Equal(f.Instance.Id, write.InstanceId);
        Assert.Equal(f.Instance.WorkflowDefinitionId, write.WorkflowDefinitionId);
        Assert.Equal(f.Boundary.Id, write.SourceActionId);
        Assert.Equal("starter", write.Caller.Id);
        Assert.Null(write.NodeExecutionId);
        Assert.All(f.LockedKeys, keys => Assert.Equal(["tests.error"], keys));
        Assert.Empty(f.InstanceWrites);
    }

    [Theory]
    [InlineData("archived")]
    [InlineData("missing")]
    [InlineData("type")]
    [InlineData("nullable")]
    [InlineData("validation")]
    [InlineData("readOnly")]
    public async Task Shared_error_target_contract_drift_fails_before_writing(string change)
    {
        var f = new Fixture(sharedTarget: true);
        var original = f.Catalog["tests.error"];
        switch (change)
        {
            case "archived": f.Catalog[original.Key] = original with { Status = SharedVariableStatuses.Archived }; break;
            case "missing": f.Catalog.Remove(original.Key); break;
            case "type": f.Catalog[original.Key] = original with { DataType = WorkflowVariableTypes.Number }; break;
            case "nullable": f.Catalog[original.Key] = original with { Nullable = true }; break;
            case "validation": f.Catalog[original.Key] = original with { Validation = "value != null" }; break;
            case "readOnly": f.Target.Access = SharedVariableAccessModes.Read; break;
        }

        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.WriteAsync());

        Assert.Empty(f.SharedWrites);
        Assert.Empty(f.InstanceWrites);
    }

    [Fact]
    public async Task Shared_catalog_value_rejection_is_safe_and_writes_nothing()
    {
        var f = new Fixture(sharedTarget: true);
        f.Target.Validation = "value == 'private-catalog-contract'";
        f.Catalog["tests.error"] = f.Catalog["tests.error"] with { Validation = f.Target.Validation };

        var error = await Assert.ThrowsAsync<WorkflowConflictException>(() => f.WriteAsync());

        Assert.Equal("The automatic error target rejected the saved failure code.", error.Message);
        Assert.Empty(f.SharedWrites);
        Assert.Empty(f.InstanceWrites);
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.ServiceTask)]
    [InlineData(BpmnFlowNodeTypes.ScriptTask)]
    [InlineData(BpmnFlowNodeTypes.CallActivity)]
    public async Task Uncaught_body_failure_has_its_distinct_terminal_reason_without_a_parent_visit(string activityKind)
    {
        var f = new Fixture(activityKind: activityKind);
        f.Definition.FlowNodes.RemoveAll(node => BpmnFlowNodeTypes.IsErrorBoundary(node.Type));

        await f.Engine.CreateAutomaticMultiInstanceLifecycle(f.Instance, f.Definition, f.Actor)
            .ContinueAsync(f.Body, new(null, false, AutomaticMultiInstanceValues.ResultInvalid), CancellationToken.None);

        var update = Assert.Single(f.TokenUpdates);
        Assert.Equal(ExecutionTokenRecordStatuses.Faulted, update.Status);
        Assert.Equal(ExecutionTokenTerminationReasons.MultiInstanceFailed, update.Reason);
        Assert.Equal(AutomaticMultiInstanceValues.ResultInvalid, update.Snapshot.FaultCode);
        Assert.Equal(WorkflowInstanceStatuses.Faulted, f.InstanceStatus);
        Assert.Null(f.Token.CurrentNodeExecutionId);
        Assert.Empty(f.InstanceWrites);
        Assert.Empty(f.SharedWrites);
    }

    private sealed class Fixture
    {
        public WorkflowModel Definition { get; }
        public FlowNodeModel Boundary { get; } = new() { Id = 3, Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
            AttachedToRef = 2, ErrorVariable = "error" };
        public VariableModel Target { get; }
        public AutomaticMultiInstanceRecord Body { get; }
        public WorkflowInstanceRecord Instance { get; }
        public ExecutionTokenRecord Token { get; private set; }
        public ActorContext Actor { get; } = new("starter", [], new Dictionary<string, string>());
        public Dictionary<string, SharedVariableRecord> Catalog { get; } = [];
        public Dictionary<string, JsonElement> SharedValues { get; } = [];
        public List<string[]> LockedKeys { get; } = [];
        public List<string[]> ReadKeys { get; } = [];
        public List<WorkflowVariableWrite> InstanceWrites { get; } = [];
        public List<SharedVariableWriteCommand> SharedWrites { get; } = [];
        public List<(CurrentNodeSnapshot Snapshot, string Status, string? Reason)> TokenUpdates { get; } = [];
        public string? InstanceStatus { get; private set; }
        public WorkflowEngineService Engine { get; }

        public Fixture(bool sharedSource = false, bool sharedTarget = false, string activityKind = BpmnFlowNodeTypes.ServiceTask)
        {
            var now = DateTimeOffset.UnixEpoch;
            var activation = Guid.NewGuid();
            var node = new FlowNodeModel { Id = 2, Type = activityKind,
                MultiInstance = new() { CollectionVariable = "items", ResultVariable = "results" } };
            Target = new() { Name = "error", DataType = WorkflowVariableTypes.String,
                Validation = sharedTarget ? "value == 'multi_instance_result_invalid'" : "error == 'multi_instance_result_invalid' and permitted",
                Scope = sharedTarget ? VariableScopes.Shared : VariableScopes.Instance,
                SharedKey = sharedTarget ? "tests.error" : null,
                Access = sharedTarget ? SharedVariableAccessModes.ReadWrite : null };
            Definition = new() { Id = "error-route-core", FlowNodes = [node, Boundary,
                    new() { Id = 4, Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, AttachedToRef = 2, ErrorVariable = "otherError" }],
                Variables = [new() { Name = "items", DataType = WorkflowVariableTypes.Number, IsArray = true,
                    Scope = sharedSource ? VariableScopes.Shared : VariableScopes.Instance,
                    SharedKey = sharedSource ? "tests.source" : null, Access = sharedSource ? SharedVariableAccessModes.Read : null },
                    Target, Shared("permitted", "tests.permitted", WorkflowVariableTypes.Boolean),
                    Shared("results", "tests.aggregate", WorkflowVariableTypes.Json),
                    Shared("otherError", "tests.otherError", WorkflowVariableTypes.String)] };
            if (sharedSource) Definition.Variables.Add(new() { Name = "sameSource", Scope = VariableScopes.Shared,
                DataType = WorkflowVariableTypes.Number, IsArray = true, SharedKey = "tests.source", Access = SharedVariableAccessModes.Read });
            foreach (var variable in Definition.Variables.Where(variable => variable.Scope == VariableScopes.Shared))
                Catalog[variable.SharedKey!] = new(1, variable.SharedKey!, variable.DataType, variable.IsArray,
                    variable.Nullable, variable.Validation, null, false, null, SharedVariableStatuses.Active, 1, now, now, null);
            SharedValues["tests.permitted"] = Json(true);
            SharedValues["tests.source"] = Json(new[] { 9, 9, 9 });
            Body = new(10, 1, 11, 7, activation, 2, activityKind, "parallel", "completed", "settled", "results", "shared", "tests.aggregate",
                1, 100, 100, 2, 2, 0, 1, 1, 0, null, 0, now, now, now);
            Instance = new(1, 7, Definition.Id, null, null, null, 11, 2, null, "running", null, "starter", now, now);
            Token = new(11, 1, 2, "Body", null, activityKind, null, null, "active", null, null, null, null, [], null, now, now,
                ActivationId: activation);
            var runtime = Proxy<IWorkflowRuntimeRepository>(Runtime);
            Engine = new(Proxy<IWorkflowDefinitionRepository>(Unexpected), runtime,
                Proxy<IWorkflowJobRepository>(Unexpected), Proxy<ITimerSubscriptionRepository>(Unexpected),
                Proxy<IUserDelegationRepository>(Unexpected), Proxy<IUnitOfWork>((name, args) =>
                    name == "SaveChangesAsync" ? Task.CompletedTask : Unexpected(name, args)),
                Proxy<IServiceTaskInvoker>(Unexpected), Proxy<IScriptEvaluator>(Unexpected), new(), TimeProvider.System,
                Proxy<IWorkflowSettingsRepository>(Unexpected), Proxy<IEngineSettingsRepository>(Unexpected),
                NullLogger<WorkflowEngineService>.Instance, Proxy<IWorkflowInstanceProjectionService>(Unexpected),
                workflowVariables: new WorkflowVariableStore(runtime, Proxy<ISharedVariableRepository>(SharedRepository)),
                automaticMultiInstances: Proxy<IAutomaticMultiInstanceRepository>((name, args) =>
                    name == "GetInputSnapshotAsync" ? Task.FromResult<JsonElement?>(Json(new { collection = new[] { 1, 2 } }))
                    : Unexpected(name, args)));
        }

        public Task WriteAsync() => Engine.WriteAutomaticMultiInstanceErrorAsync(Instance, Body, Definition,
            Boundary, null, Actor, AutomaticMultiInstanceValues.ResultInvalid, CancellationToken.None);

        public Task ContinueAsync() => Engine.CreateAutomaticMultiInstanceLifecycle(Instance, Definition, Actor)
            .ContinueAsync(Body, new(new(Boundary.Id, AutomaticMultiInstanceValues.ResultInvalid, 20), false, null), CancellationToken.None);

        private object? Runtime(string name, object?[] args)
        {
            switch (name)
            {
                case "LoadLatestVariableVersionsAsync": return Task.FromResult<IReadOnlyList<InstanceVariableVersionRecord>>(
                    [new("items", Json(new[] { 8, 8, 8, 8 }), 42)]);
                case "AddVariableAsync":
                    InstanceWrites.Add(new((string)args[1]!, (JsonElement)args[4]!, (int?)args[2], (long?)args[6], (string?)args[3]));
                    return Task.CompletedTask;
                case "GetExecutionTokenAsync": return Task.FromResult<ExecutionTokenRecord?>(Token);
                case "UpdateExecutionTokenAsync":
                    var snapshot = (CurrentNodeSnapshot)args[1]!;
                    var status = (string)args[2]!;
                    TokenUpdates.Add((snapshot, status, (string?)args[5]));
                    Token = Token with { Status = status };
                    return Task.CompletedTask;
                case "ListGatewayExecutionsAsync": return Task.FromResult<IReadOnlyList<GatewayExecutionRecord>>([]);
                case "ListGatewayBranchesForInstanceAsync": return Task.FromResult<IReadOnlyList<GatewayBranchRecord>>([]);
                case "ListExecutionTokensAsync": return Task.FromResult<IReadOnlyList<ExecutionTokenRecord>>([]);
                case "CancelOpenUserTasksForTokensAsync": case "CancelActiveMultiInstancesForTokensAsync":
                case "SetExecutionTokensStatusAsync": case "SetGatewayBranchesStatusAsync": case "SetGatewayExecutionsStatusAsync":
                    return Task.CompletedTask;
                case "SetInstanceStatusAsync": InstanceStatus = (string)args[1]!; return Task.CompletedTask;
                default: return Unexpected(name, args);
            }
        }

        private object? SharedRepository(string name, object?[] args)
        {
            if (name == "WriteAsync")
            {
                var write = (SharedVariableWriteCommand)args[0]!;
                SharedWrites.Add(write);
                var entry = Catalog[write.Key];
                var revision = new SharedVariableRevisionRecord(1, entry.Id, 2, "set", true, true, write.Value,
                    write.Caller.Kind, write.Caller.Id, write.Source, null, null, write.WorkflowDefinitionId,
                    write.InstanceId, write.NodeExecutionId, write.SourceActionId, DateTimeOffset.UnixEpoch);
                return Task.FromResult<SharedVariableMutationResult?>(new(entry, revision, false));
            }
            var keys = ((IReadOnlyCollection<string>)args[0]!).ToArray();
            ReadKeys.Add(keys);
            if (name == "GetManyByKeyAsync")
                return Task.FromResult<IReadOnlyDictionary<string, SharedVariableRecord>>(keys.Where(Catalog.ContainsKey)
                    .ToDictionary(key => key, key => Catalog[key]));
            if (name is "LoadCurrentAsync" or "LockCurrentAsync")
            {
                if (name == "LockCurrentAsync") LockedKeys.Add(keys);
                return Task.FromResult<IReadOnlyDictionary<string, SharedVariableCurrentValueRecord>>(keys
                    .Where(key => SharedValues.ContainsKey(key) && Catalog.ContainsKey(key))
                    .ToDictionary(key => key, key => new SharedVariableCurrentValueRecord(1, key, Catalog[key].DataType,
                        Catalog[key].IsArray, Catalog[key].Nullable, SharedValues[key], 1, DateTimeOffset.UnixEpoch)));
            }
            return Unexpected(name, args);
        }
    }

    private static VariableModel Shared(string name, string key, string type) => new()
    { Name = name, SharedKey = key, DataType = type, Scope = VariableScopes.Shared, Access = SharedVariableAccessModes.ReadWrite };
    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
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
