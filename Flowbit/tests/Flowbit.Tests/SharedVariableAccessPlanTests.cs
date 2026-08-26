using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class SharedVariableAccessPlanTests
{
    [Fact]
    public void Build_IndexesExactConditionalReadsAndProducersByCurrentNodeAndFlow()
    {
        var definition = CreateDefinition();

        var plan = SharedVariableAccessPlanner.Build(
            definition,
            new ConditionalEventDefinitionAnalyzer());

        var conditional = plan.ForNode(2);
        Assert.Equal(["exchangeRate"], conditional.ConditionalDependencyAliases);
        Assert.Equal(["exchangeRate"], conditional.LockAliases);
        Assert.DoesNotContain("rate", conditional.LockAliases);

        var service = plan.ForNode(3);
        Assert.Equal(["exchangeRate"], service.ProducerAliases);
        Assert.Equal(["rate"], service.ReadAliases);
        Assert.DoesNotContain("rate", service.LockAliases);

        var javascript = plan.ForNode(4);
        Assert.Equal(
            ["exchangeRate", "rate"],
            javascript.ProducerAliases.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(plan.ForNode(1).LockAliases);

        Assert.Equal(["rate"], plan.ForFlow(401).ProducerAliases);
        Assert.Empty(plan.ForFlow(101).ProducerAliases);

        var conditionalScope = plan.SelectConditionalNodes([2]);
        Assert.Equal(["exchangeRate"], conditionalScope.LockAliases);
        Assert.DoesNotContain("rate", conditionalScope.LockAliases);
    }

    [Fact]
    public void Cache_AnalyzesImmutableDefinitionOnlyOncePerId()
    {
        var analyzer = new CountingAnalyzer(new ConditionalEventDefinitionAnalyzer());
        var cache = new SharedVariableAccessPlanCache(analyzer);
        var definition = CreateDefinition();

        var first = cache.GetOrAdd(41, definition);
        var second = cache.GetOrAdd(41, definition);

        Assert.Same(first, second);
        Assert.Equal(1, analyzer.Count);
        Assert.True(cache.TryGet(41, out var cached));
        Assert.Same(first, cached);
    }

    [Fact]
    public void Cache_ExposesOnlyFrozenPlansThatCannotBeCastToMutableCollections()
    {
        var cache = new SharedVariableAccessPlanCache(
            new ConditionalEventDefinitionAnalyzer());
        var cached = cache.GetOrAdd(42, CreateDefinition());
        var node = cached.ForNode(3);
        var flow = cached.ForFlow(401);

        Assert.IsAssignableFrom<FrozenDictionary<int, SharedVariableNodeAccessPlan>>(cached.Nodes);
        Assert.IsAssignableFrom<FrozenDictionary<int, SharedVariableFlowAccessPlan>>(cached.Flows);
        AssertMutationRejected(cached.Nodes, 999, node);
        AssertMutationRejected(cached.Flows, 999, flow);
        AssertMutationRejected(node.ConditionalDependencyAliases, "mutated");
        AssertMutationRejected(node.ProducerAliases, "mutated");
        AssertMutationRejected(node.ReadAliases, "mutated");
        AssertMutationRejected(node.LockAliases, "mutated");
        AssertMutationRejected(flow.ProducerAliases, "mutated");
        AssertMutationRejected(cached.ForNode(int.MaxValue).LockAliases, "mutated");
        AssertMutationRejected(cached.ForFlow(int.MaxValue).ProducerAliases, "mutated");
        var scope = cached.SelectNodeAndFlow(3, 401);
        AssertMutationRejected(scope.NodeIds, 999);
        AssertMutationRejected(scope.FlowIds, 999);
        AssertMutationRejected(scope.LockAliases, "mutated");
    }

    [Fact]
    public async Task LegacyCatalogFencePrelocksDefinitionButExactlyLocksExpectedOutputAlias()
    {
        var definition = CreateOrderedScriptDefinition(
            firstKey: "tests.lock.a",
            secondKey: "tests.lock.z");
        string[]? compatibilityPrelock = null;
        string[]? exactLocks = null;
        var shared = Proxy<ISharedVariableRepository>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(ISharedVariableRepository.PrelockDefinitionKeysForLegacyAllocatorAsync):
                    compatibilityPrelock = ((IReadOnlyCollection<string>)(args[0]
                        ?? throw new InvalidOperationException("Definition keys were not supplied."))).ToArray();
                    return Task.CompletedTask;
                case nameof(ISharedVariableRepository.LockCurrentAsync):
                    exactLocks = ((IReadOnlyCollection<string>)(args[0]
                        ?? throw new InvalidOperationException("Exact lock keys were not supplied."))).ToArray();
                    return Task.FromResult<IReadOnlyDictionary<string, SharedVariableCurrentValueRecord>>(
                        new Dictionary<string, SharedVariableCurrentValueRecord>(StringComparer.Ordinal));
                case nameof(ISharedVariableRepository.GetManyByKeyAsync):
                    var records = ((IReadOnlyCollection<string>)(args[0]
                            ?? throw new InvalidOperationException("Catalog keys were not supplied.")))
                        .ToDictionary(
                            key => key,
                            key => SharedRecord(key, revision: 17),
                            StringComparer.Ordinal);
                    return Task.FromResult<IReadOnlyDictionary<string, SharedVariableRecord>>(records);
                default:
                    throw new InvalidOperationException(
                        $"Unexpected shared-variable repository call: {method.Name}.");
            }
        });
        var runtime = Proxy<IWorkflowRuntimeRepository>((method, _) =>
            throw new InvalidOperationException(
                $"Unexpected runtime repository call: {method.Name}."));
        var store = new WorkflowVariableStore(runtime, shared);

        var revisions = await store.LoadSharedRevisionsAsync(
            definition,
            ["second"],
            lockForUpdate: true,
            CancellationToken.None);

        Assert.Equal(
            ["tests.lock.a", "tests.lock.z"],
            Assert.IsType<string[]>(compatibilityPrelock));
        Assert.Equal(["tests.lock.z"], Assert.IsType<string[]>(exactLocks));
        Assert.Equal(17, Assert.Single(revisions).Value);
        Assert.DoesNotContain("first", revisions.Keys);
    }

    [Fact]
    public void LockOrder_RejectsReverseSynchronousScriptAcquisitions()
    {
        var definition = CreateOrderedScriptDefinition(
            firstKey: "tests.lock.z",
            secondKey: "tests.lock.a");
        var analyzer = new ConditionalEventDefinitionAnalyzer();
        var conditionalPlan = analyzer.Analyze(definition);
        var accessPlan = SharedVariableAccessPlanner.Build(definition, conditionalPlan);

        var error = Assert.Throws<WorkflowDomainException>(() =>
            SharedVariableTransactionLockOrderValidator.Validate(
                definition,
                accessPlan,
                conditionalPlan));

        Assert.Contains("monotonic", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tests.lock.a", error.Message, StringComparison.Ordinal);
        Assert.Contains("tests.lock.z", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LockOrder_AcceptsOrdinalScriptsAndAsyncBeforeReset()
    {
        AssertLockOrderValid(CreateOrderedScriptDefinition(
            firstKey: "tests.lock.a",
            secondKey: "tests.lock.z"));

        var reset = CreateOrderedScriptDefinition(
            firstKey: "tests.lock.z",
            secondKey: "tests.lock.a");
        reset.FlowNodes.Single(node => node.Id == 3).AsyncBefore = true;
        AssertLockOrderValid(reset);

        var implicitAsyncBefore = CreateOrderedScriptDefinition(
            firstKey: "tests.lock.z",
            secondKey: "tests.lock.a");
        implicitAsyncBefore.FlowNodes.Single(node => node.Id == 3).AsyncAfter = true;
        Assert.False(implicitAsyncBefore.FlowNodes.Single(node => node.Id == 3).AsyncBefore);
        AssertLockOrderValid(implicitAsyncBefore);
    }

    [Fact]
    public void LockOrder_RejectsReverseFifoForkBranchesInOneTransaction()
    {
        var definition = CreateForkDefinition();
        var analyzer = new ConditionalEventDefinitionAnalyzer();
        var conditionalPlan = analyzer.Analyze(definition);
        var accessPlan = SharedVariableAccessPlanner.Build(definition, conditionalPlan);

        var error = Assert.Throws<WorkflowDomainException>(() =>
            SharedVariableTransactionLockOrderValidator.Validate(
                definition,
                accessPlan,
                conditionalPlan));

        Assert.Contains("tests.lock.a", error.Message, StringComparison.Ordinal);
        Assert.Contains("tests.lock.z", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LockOrder_ModelsUserTaskNodeValuesBeforeConditionalReload()
    {
        var definition = CreateUserActionConditionalDefinition();
        var analyzer = new ConditionalEventDefinitionAnalyzer();
        var conditionalPlan = analyzer.Analyze(definition);
        var accessPlan = SharedVariableAccessPlanner.Build(definition, conditionalPlan);

        var error = Assert.Throws<WorkflowDomainException>(() =>
            SharedVariableTransactionLockOrderValidator.Validate(
                definition,
                accessPlan,
                conditionalPlan));

        Assert.Contains("conditional dependency reload", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tests.lock.a", error.Message, StringComparison.Ordinal);
        Assert.Contains("tests.lock.z", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LockOrder_RejectsOutputThenErrorBoundaryConditionalReloadInReverseOrder()
    {
        var definition = CreateErrorBoundaryConditionalDefinition();
        var analyzer = new ConditionalEventDefinitionAnalyzer();
        var conditionalPlan = analyzer.Analyze(definition);
        var accessPlan = SharedVariableAccessPlanner.Build(definition, conditionalPlan);

        Assert.Contains(4, conditionalPlan.NodeIdsByVariable["statusChanged"]);
        Assert.Contains(5, conditionalPlan.NodeIdsByVariable["errorChanged"]);
        Assert.Equal(["higherDependency"], accessPlan.ForNode(4).ConditionalDependencyAliases);
        Assert.Equal(["lowerDependency"], accessPlan.ForNode(5).ConditionalDependencyAliases);

        var error = Assert.Throws<WorkflowDomainException>(() =>
            SharedVariableTransactionLockOrderValidator.Validate(
                definition,
                accessPlan,
                conditionalPlan));

        Assert.Contains("error boundary #3", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tests.lock.a", error.Message, StringComparison.Ordinal);
        Assert.Contains("tests.lock.z", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cache_FailsClosedForLegacyUnsafeDefinitionBeforeReturningPlan()
    {
        var analyzer = new CountingAnalyzer(new ConditionalEventDefinitionAnalyzer());
        var cache = new SharedVariableAccessPlanCache(analyzer);
        var definition = CreateOrderedScriptDefinition(
            firstKey: "tests.lock.z",
            secondKey: "tests.lock.a");

        var error = Assert.Throws<WorkflowConflictException>(() =>
            cache.GetOrAdd(91, definition));

        Assert.Contains("cannot execute safely", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("monotonic", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, analyzer.Count);
        Assert.False(cache.TryGet(91, out _));
    }

    [Fact]
    public void AsyncAfterUserTaskFinalizationPrelocksNodeAndSelectedFlowUnion()
    {
        var definition = CreateUserActionConditionalDefinition();
        var node = definition.FlowNodes.Single(item => item.Id == 2);
        node.Variables!.Add(new VariableModel
        {
            Id = 22,
            Name = "higher",
            DataType = WorkflowVariableTypes.Number
        });
        var lower = definition.Variables.Single(item =>
            item.Name == "lowerDependency");
        lower.Access = SharedVariableAccessModes.ReadWrite;
        definition.SequenceFlows.Single(item => item.Id == 201)
            .Variables!.Single().Name = "lowerDependency";
        var analyzer = new ConditionalEventDefinitionAnalyzer();
        var plan = SharedVariableAccessPlanner.Build(definition, analyzer);
        var method = typeof(WorkflowEngineService).GetMethod(
            "SharedFinalizationAccessScope",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Shared finalization lock helper was not found.");

        var scope = Assert.IsType<SharedVariableAccessScope>(
            method.Invoke(
                null,
                [plan, node, WorkflowJobKinds.AsyncAfter, 201]));

        Assert.Equal(
            ["higher", "lowerDependency"],
            scope.LockAliases.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void NullableSharedNullSkipsNonNullValidationRule()
    {
        var nullValue = System.Text.Json.JsonSerializer.SerializeToElement<string?>(null);

        SharedVariableValueValidator.Validate(
            "tests.nullable",
            WorkflowVariableTypes.Number,
            isArray: false,
            nullable: true,
            validation: "value > 0",
            nullValue);

        Assert.Throws<WorkflowDomainException>(() =>
            SharedVariableValueValidator.Validate(
                "tests.non-nullable",
                WorkflowVariableTypes.Number,
                isArray: false,
                nullable: false,
                validation: "value > 0",
                nullValue));
    }

    private static WorkflowModel CreateDefinition() => new()
    {
        Id = "shared-access-plan",
        Name = "Shared access plan",
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "rate",
                Scope = VariableScopes.Shared,
                SharedKey = "tests.rate",
                Access = SharedVariableAccessModes.ReadWrite,
                DataType = WorkflowVariableTypes.Number,
                Nullable = false
            },
            new VariableModel
            {
                Id = 2,
                Name = "exchangeRate",
                Scope = VariableScopes.Shared,
                SharedKey = "tests.exchange-rate",
                Access = SharedVariableAccessModes.ReadWrite,
                DataType = WorkflowVariableTypes.Number,
                Nullable = false
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Wait",
                Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                Conditional = new ConditionalDefinitionModel
                {
                    // The string literal deliberately contains the shorter
                    // alias; only the AST parameter is a dependency.
                    Condition = "Contains('rate', 'rate') and [exchangeRate] > 0",
                    DeliveryMode = ConditionalEventDeliveryModes.DurableAsync
                }
            },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Service",
                Type = BpmnFlowNodeTypes.ServiceTask,
                AsyncBefore = true,
                Service = new ServiceTaskModel
                {
                    Type = ServiceConnectorTypes.Rest,
                    Url = "https://tests.local/${rate}",
                    OutputMappings =
                    [
                        new ServiceOutputMappingModel
                        {
                            Variable = "exchangeRate",
                            Path = "result",
                            DataType = WorkflowVariableTypes.Number
                        }
                    ]
                }
            },
            new FlowNodeModel
            {
                Id = 4,
                Name = "Dynamic script",
                Type = BpmnFlowNodeTypes.ScriptTask,
                ScriptFormat = ScriptFormats.JavaScript,
                Script = "execution.setVariable('rate', 1);"
            },
            new FlowNodeModel { Id = 5, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 },
            new SequenceFlowModel
            {
                Id = 401,
                SourceRef = 4,
                TargetRef = 5,
                Variables =
                [
                    new VariableModel
                    {
                        Id = 10,
                        Name = "rate",
                        DataType = WorkflowVariableTypes.Number
                    }
                ]
            }
        ]
    };

    private static WorkflowModel CreateOrderedScriptDefinition(
        string firstKey,
        string secondKey) => new()
    {
        Id = "shared-lock-order",
        Name = "Shared lock order",
        InitialEventId = 1,
        Variables =
        [
            SharedBinding(1, "first", firstKey),
            SharedBinding(2, "second", secondKey)
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            ScriptNode(2, "Write first", "first"),
            ScriptNode(3, "Write second", "second"),
            new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
        ]
    };

    private static WorkflowModel CreateForkDefinition() => new()
    {
        Id = "shared-lock-fork-order",
        Name = "Shared lock fork order",
        InitialEventId = 1,
        Variables =
        [
            SharedBinding(1, "higher", "tests.lock.z"),
            SharedBinding(2, "lower", "tests.lock.a")
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
            ScriptNode(3, "First queued branch", "higher"),
            ScriptNode(4, "Second queued branch", "lower"),
            new FlowNodeModel { Id = 5, Name = "End one", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 6, Name = "End two", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
            new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 6 }
        ]
    };

    private static WorkflowModel CreateUserActionConditionalDefinition() => new()
    {
        Id = "shared-user-action-lock-order",
        Name = "Shared user action lock order",
        InitialEventId = 1,
        Variables =
        [
            SharedBinding(1, "higher", "tests.lock.z"),
            new VariableModel
            {
                Id = 2,
                Name = "lowerDependency",
                Scope = VariableScopes.Shared,
                SharedKey = "tests.lock.a",
                Access = SharedVariableAccessModes.Read,
                DataType = WorkflowVariableTypes.Number,
                Nullable = false
            },
            new VariableModel
            {
                Id = 3,
                Name = "changed",
                DataType = WorkflowVariableTypes.Boolean,
                Nullable = false
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Approve",
                Type = BpmnFlowNodeTypes.UserTask,
                Variables =
                [
                    new VariableModel
                    {
                        Id = 20,
                        Name = "changed",
                        DataType = WorkflowVariableTypes.Boolean
                    }
                ]
            },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Shared wait",
                Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                Conditional = new ConditionalDefinitionModel
                {
                    Condition = "changed and lowerDependency > 0",
                    DeliveryMode = ConditionalEventDeliveryModes.Atomic
                }
            },
            new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 5, Name = "Conditional end", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel
            {
                Id = 201,
                SourceRef = 2,
                TargetRef = 4,
                Variables =
                [
                    new VariableModel
                    {
                        Id = 21,
                        Name = "higher",
                        DataType = WorkflowVariableTypes.Number
                    }
                ]
            },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 }
        ]
    };

    private static WorkflowModel CreateErrorBoundaryConditionalDefinition() => new()
    {
        Id = "shared-error-boundary-lock-order",
        Name = "Shared error boundary lock order",
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "higherDependency",
                Scope = VariableScopes.Shared,
                SharedKey = "tests.lock.z",
                Access = SharedVariableAccessModes.Read,
                DataType = WorkflowVariableTypes.Number,
                Nullable = false
            },
            new VariableModel
            {
                Id = 2,
                Name = "lowerDependency",
                Scope = VariableScopes.Shared,
                SharedKey = "tests.lock.a",
                Access = SharedVariableAccessModes.Read,
                DataType = WorkflowVariableTypes.Number,
                Nullable = false
            },
            new VariableModel
            {
                Id = 3,
                Name = "statusChanged",
                DataType = WorkflowVariableTypes.Number,
                Nullable = false
            },
            new VariableModel
            {
                Id = 4,
                Name = "errorChanged",
                DataType = WorkflowVariableTypes.String,
                Nullable = false
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Service",
                Type = BpmnFlowNodeTypes.ServiceTask,
                AsyncBefore = true,
                Service = new ServiceTaskModel
                {
                    Type = ServiceConnectorTypes.Rest,
                    Url = "https://tests.local/status",
                    StatusVariable = "statusChanged"
                }
            },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Service error",
                Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = 2,
                ErrorVariable = "errorChanged"
            },
            new FlowNodeModel
            {
                Id = 4,
                Name = "Higher wait",
                Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                Conditional = new ConditionalDefinitionModel
                {
                    Condition = "statusChanged == 500 and higherDependency > 0",
                    DeliveryMode = ConditionalEventDeliveryModes.Atomic
                }
            },
            new FlowNodeModel
            {
                Id = 5,
                Name = "Lower wait",
                Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                Conditional = new ConditionalDefinitionModel
                {
                    Condition = "errorChanged != '' and lowerDependency > 0",
                    DeliveryMode = ConditionalEventDeliveryModes.Atomic
                }
            },
            new FlowNodeModel { Id = 6, Name = "Main end", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 7, Name = "Higher end", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 8, Name = "Lower end", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 6 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 6 },
            new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 7 },
            new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 8 }
        ]
    };

    private static VariableModel SharedBinding(int id, string alias, string key) => new()
    {
        Id = id,
        Name = alias,
        Scope = VariableScopes.Shared,
        SharedKey = key,
        Access = SharedVariableAccessModes.ReadWrite,
        DataType = WorkflowVariableTypes.Number,
        Nullable = false
    };

    private static FlowNodeModel ScriptNode(int id, string name, string target) => new()
    {
        Id = id,
        Name = name,
        Type = BpmnFlowNodeTypes.ScriptTask,
        ScriptFormat = ScriptFormats.NCalc,
        Assignments =
        [
            new AssignmentModel { Variable = target, Expression = "1" }
        ]
    };

    private static void AssertLockOrderValid(WorkflowModel definition)
    {
        var analyzer = new ConditionalEventDefinitionAnalyzer();
        var conditionalPlan = analyzer.Analyze(definition);
        var accessPlan = SharedVariableAccessPlanner.Build(definition, conditionalPlan);
        SharedVariableTransactionLockOrderValidator.Validate(
            definition,
            accessPlan,
            conditionalPlan);
    }

    private sealed class CountingAnalyzer(IConditionalEventDefinitionAnalyzer inner)
        : IConditionalEventDefinitionAnalyzer
    {
        public int Count { get; private set; }

        public Flowbit.Service.Models.ConditionalEventDependencyPlan Analyze(
            WorkflowModel definition)
        {
            Count++;
            return inner.Analyze(definition);
        }
    }

    private static SharedVariableRecord SharedRecord(string key, long revision) => new(
        Id: revision,
        Key: key,
        DataType: WorkflowVariableTypes.Number,
        IsArray: false,
        Nullable: false,
        Validation: null,
        Description: null,
        HasValue: false,
        Value: null,
        Status: SharedVariableStatuses.Active,
        Revision: revision,
        CreatedAt: DateTimeOffset.UnixEpoch,
        UpdatedAt: DateTimeOffset.UnixEpoch,
        ArchivedAt: null,
        ValueRevision: revision);

    private class StubProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } =
            (method, _) => throw new InvalidOperationException(
                $"Unexpected proxy call: {method.Name}.");

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(
                targetMethod ?? throw new InvalidOperationException(
                    "A proxy method was not supplied."),
                args ?? []);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static void AssertMutationRejected<T>(
        IReadOnlySet<T> values,
        T value)
    {
        Assert.IsAssignableFrom<FrozenSet<T>>(values);
        if (values is ISet<T> mutableView)
        {
            Assert.Throws<NotSupportedException>(() => mutableView.Add(value));
        }
    }

    private static void AssertMutationRejected<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> values,
        TKey key,
        TValue value)
        where TKey : notnull
    {
        var mutableView = Assert.IsAssignableFrom<IDictionary<TKey, TValue>>(values);
        Assert.Throws<NotSupportedException>(() => mutableView.Add(key, value));
    }
}
