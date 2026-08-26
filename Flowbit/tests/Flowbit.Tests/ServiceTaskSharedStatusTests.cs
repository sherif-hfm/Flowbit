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

public sealed class ServiceTaskSharedStatusTests
{
    [Fact]
    public async Task SynchronousSuccessBatchesMappedOutputWithStatusAndReturnsValidationFailure()
    {
        var store = new RejectingVariableStore(
            () => new WorkflowDomainException(
                "Shared variable 'httpStatus' failed validation."));
        var (engine, instance, definition, node) = CreateEngine(store);
        var overlay = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["decision"] = JsonSerializer.SerializeToElement("initial"),
            ["httpStatus"] = JsonSerializer.SerializeToElement(0)
        };

        var outcome = await InvokeExecuteServiceTaskAsync(
            engine,
            instance,
            node,
            definition,
            overlay);

        Assert.False(ReadOutcome<bool>(outcome, "Success"));
        Assert.Contains(
            "failed validation",
            ReadOutcome<string>(outcome, "Reason"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Collection(
            store.Attempts,
            batch =>
            {
                Assert.Equal(2, batch.Count);
                Assert.Equal("approved", batch["decision"].GetString());
                Assert.Equal(200, batch["httpStatus"].GetInt32());
            },
            batch =>
            {
                var status = Assert.Single(batch);
                Assert.Equal("httpStatus", status.Key, ignoreCase: true);
                Assert.Equal(200, status.Value.GetInt32());
            });
        Assert.Empty(store.Persisted);
        Assert.Equal("initial", overlay["decision"].GetString());
        Assert.Equal(0, overlay["httpStatus"].GetInt32());
    }

    [Fact]
    public async Task SynchronousSuccessDoesNotConvertOperationalStatusConflictToTaskFailure()
    {
        var store = new RejectingVariableStore(
            () => new WorkflowConflictException(
                "The shared status key was archived concurrently."));
        var (engine, instance, definition, node) = CreateEngine(store);
        var overlay = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["decision"] = JsonSerializer.SerializeToElement("initial"),
            ["httpStatus"] = JsonSerializer.SerializeToElement(0)
        };

        var error = await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            InvokeExecuteServiceTaskAsync(
                engine,
                instance,
                node,
                definition,
                overlay));

        Assert.Contains("archived", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(store.Attempts);
        Assert.Empty(store.Persisted);
        Assert.Equal("initial", overlay["decision"].GetString());
    }

    private static (
        WorkflowEngineService Engine,
        WorkflowInstanceRecord Instance,
        WorkflowModel Definition,
        FlowNodeModel Node) CreateEngine(IWorkflowVariableStore store)
    {
        var definition = CreateDefinition();
        var workflow = new WorkflowDefinitionRecord(
            7,
            definition.Name,
            definition.Id,
            1,
            definition,
            IsPublished: true,
            IsDefault: true,
            DateTimeOffset.UtcNow);
        var instance = new WorkflowInstanceRecord(
            Id: 11,
            WorkflowDefinitionId: workflow.Id,
            WorkflowKey: workflow.WorkflowKey,
            IdempotencyKey: null,
            BusinessKey: null,
            BusinessKeyUniqueness: null,
            ActiveTokenId: 13,
            CurrentStepId: 2,
            ActiveUserTaskId: null,
            Status: WorkflowInstanceStatuses.Running,
            ClaimedBy: null,
            StartedBy: "tester",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            CurrentNodeExecutionId: 17);

        object? Definitions(MethodInfo method, object?[] arguments) => method.Name switch
        {
            nameof(IWorkflowDefinitionRepository.GetAsync) =>
                Task.FromResult<WorkflowDefinitionRecord?>(workflow),
            _ => Unexpected(method)
        };
        object? Runtime(MethodInfo method, object?[] arguments) => method.Name switch
        {
            nameof(IWorkflowRuntimeRepository.GetInstanceAsync) =>
                Task.FromResult<WorkflowInstanceRecord?>(instance),
            _ => Unexpected(method)
        };
        object? UnitOfWork(MethodInfo method, object?[] arguments) => method.Name switch
        {
            nameof(IUnitOfWork.SaveChangesAsync) => Task.CompletedTask,
            _ => Unexpected(method)
        };
        object? Invoker(MethodInfo method, object?[] arguments) => method.Name switch
        {
            nameof(IServiceTaskInvoker.InvokeAsync) => Task.FromResult(
                new ServiceTaskResult(
                    Completed: true,
                    StatusCode: 200,
                    Body: "{\"result\":{\"decision\":\"approved\"}}",
                    Error: null)),
            _ => Unexpected(method)
        };

        var engine = new WorkflowEngineService(
            Proxy<IWorkflowDefinitionRepository>(Definitions),
            Proxy<IWorkflowRuntimeRepository>(Runtime),
            Proxy<IWorkflowJobRepository>(Unexpected),
            Proxy<ITimerSubscriptionRepository>(Unexpected),
            Proxy<IUserDelegationRepository>(Unexpected),
            Proxy<IUnitOfWork>(UnitOfWork),
            Proxy<IServiceTaskInvoker>(Invoker),
            Proxy<IScriptEvaluator>(Unexpected),
            new WorkflowContextOptions(),
            TimeProvider.System,
            Proxy<IWorkflowSettingsRepository>(Unexpected),
            Proxy<IEngineSettingsRepository>(Unexpected),
            NullLogger<WorkflowEngineService>.Instance,
            workflowVariables: store);
        return (engine, instance, definition, definition.FlowNodes.Single(item => item.Id == 2));
    }

    private static WorkflowModel CreateDefinition() => new()
    {
        Id = "service-shared-status-unit",
        Name = "Service shared status unit",
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "decision",
                DataType = WorkflowVariableTypes.String,
                Nullable = false
            },
            new VariableModel
            {
                Id = 2,
                Name = "httpStatus",
                Scope = VariableScopes.Shared,
                SharedKey = "tests.service-status.unit",
                Access = SharedVariableAccessModes.ReadWrite,
                DataType = WorkflowVariableTypes.Number,
                Nullable = false,
                Validation = "value != 200"
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Type = BpmnFlowNodeTypes.ServiceTask,
                Service = new ServiceTaskModel
                {
                    Type = ServiceConnectorTypes.Rest,
                    Url = "https://tests.local/typed-output-success",
                    Method = "GET",
                    StatusVariable = "httpStatus",
                    OutputMappings =
                    [
                        new ServiceOutputMappingModel
                        {
                            Variable = "decision",
                            Path = "result.decision",
                            Required = true,
                            DataType = WorkflowVariableTypes.String
                        }
                    ]
                }
            },
            new FlowNodeModel { Id = 3, Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
        ]
    };

    private static async Task<object> InvokeExecuteServiceTaskAsync(
        WorkflowEngineService engine,
        WorkflowInstanceRecord instance,
        FlowNodeModel node,
        WorkflowModel definition,
        Dictionary<string, JsonElement> overlay)
    {
        var method = typeof(WorkflowEngineService).GetMethod(
            "ExecuteServiceTaskAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ExecuteServiceTaskAsync was not found.");
        var actor = new ActorContext(
            "tester",
            ["admin"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var invocation = Assert.IsAssignableFrom<Task>(method.Invoke(
            engine,
            [
                instance,
                node,
                definition,
                actor,
                new Dictionary<string, JsonElement>(overlay, StringComparer.OrdinalIgnoreCase),
                overlay,
                CancellationToken.None
            ]));
        await invocation;
        return invocation.GetType().GetProperty("Result")?.GetValue(invocation)
            ?? throw new InvalidOperationException("Service-task invocation returned no outcome.");
    }

    private static T ReadOutcome<T>(object outcome, string property) =>
        (T)(outcome.GetType().GetProperty(property)?.GetValue(outcome)
            ?? throw new InvalidOperationException($"Outcome property '{property}' was unavailable."));

    private sealed class RejectingVariableStore(Func<Exception> exceptionFactory)
        : IWorkflowVariableStore
    {
        public List<IReadOnlyDictionary<string, JsonElement>> Attempts { get; } = [];
        public List<IReadOnlyDictionary<string, JsonElement>> Persisted { get; } = [];

        public Task<IReadOnlyList<WorkflowVariableWriteResult>> WriteAsync(
            WorkflowModel definition,
            long workflowDefinitionId,
            long instanceId,
            IReadOnlyCollection<WorkflowVariableWrite> writes,
            ActorContext actor,
            CancellationToken cancellationToken)
        {
            var batch = writes.ToDictionary(
                write => write.Alias,
                write => write.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
            Attempts.Add(batch);
            if (batch.ContainsKey("httpStatus"))
            {
                throw exceptionFactory();
            }
            Persisted.Add(batch);
            return Task.FromResult<IReadOnlyList<WorkflowVariableWriteResult>>([]);
        }

        public Task<Dictionary<string, JsonElement>> MergeEffectiveValuesAsync(
            WorkflowModel definition,
            IReadOnlyDictionary<string, JsonElement> instanceValues,
            SharedVariableAccessScope? sharedAccess,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SharedVariableBindingMetadataDto>> DescribeBindingsAsync(
            WorkflowModel definition,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, long>> LoadSharedRevisionsAsync(
            WorkflowModel definition,
            IReadOnlyCollection<string>? aliases,
            bool lockForUpdate,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, long>> LoadSharedValueVersionsAsync(
            WorkflowModel definition,
            IReadOnlyCollection<string> aliases,
            bool lockForUpdate,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public class StubProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = Unexpected;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(
                targetMethod ?? throw new InvalidOperationException("A proxy method was not supplied."),
                args ?? []);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? Unexpected(MethodInfo method, object?[]? arguments = null) =>
        throw new InvalidOperationException(
            $"Unexpected {method.DeclaringType?.Name}.{method.Name} call.");
}
