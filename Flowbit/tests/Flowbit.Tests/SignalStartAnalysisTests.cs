using System.Text.Json;
using Flowbit.Service.Ai;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The graph analyses treat a signal start as the entry it is: the assistant's reachability check starts from it, the
/// shared-variable lock-order proof starts a segment at it, and its access plan is a plain start's.
/// </summary>
public sealed class SignalStartAnalysisTests
{
    [Fact]
    public void Reachability_StartsFromASignalStart()
    {
        var model = Model(signalStart: true);
        Assert.Empty(WorkflowAiAuthoringService.UnreachableNodes(model, null));

        model.SequenceFlows = model.SequenceFlows.Where(flow => flow.SourceRef != 1).ToList();
        Assert.Equal([2, 3, 4], WorkflowAiAuthoringService.UnreachableNodes(model, null).Select(node => node.Id));
    }

    [Theory]
    [InlineData("tests.start.z", "tests.start.a", true)]
    [InlineData("tests.start.a", "tests.start.z", false)]
    public void LockOrder_StartsASegmentAtASignalStartAsAtAPlainStart(string firstKey, string secondKey, bool descending)
    {
        var signalStart = Model(signalStart: true);
        var plainStart = Model(signalStart: false);
        foreach (var model in new[] { signalStart, plainStart })
        {
            model.Variables = [Shared(1, "first", firstKey), Shared(2, "second", secondKey)];
        }

        var problem = LockOrderProblem(signalStart);

        Assert.Equal(LockOrderProblem(plainStart), problem);
        Assert.Equal(descending, problem is not null);
        if (descending)
        {
            Assert.Contains("entry node #1", problem, StringComparison.Ordinal);
            Assert.Contains("tests.start.a", problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AccessPlan_ASignalStartPlansWhatAPlainStartPlans()
    {
        var model = new WorkflowModel
        {
            Id = "signal-start-access-plan",
            Name = "Access plan",
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1, Name = "rate", Scope = VariableScopes.Shared, SharedKey = "tests.start.rate",
                    Access = SharedVariableAccessModes.Read, DataType = WorkflowVariableTypes.Number, Nullable = true
                },
                new VariableModel
                {
                    Id = 2, Name = "rateText", DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement("${rate}")
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "By hand", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2, Name = "By signal", Type = BpmnFlowNodeTypes.SignalStartEvent,
                    Signal = new SignalEventModel
                    {
                        Name = "tests.start",
                        OutputMappings =
                        [
                            new MessageOutputMappingModel
                            {
                                Variable = "recallNumber", Path = "recallNumber", DataType = WorkflowVariableTypes.String, Required = true
                            }
                        ]
                    }
                },
                new FlowNodeModel { Id = 3, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 3 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4, Name = "Done" }
            ]
        };

        var plan = SharedVariableAccessPlanner.Build(model, new ConditionalEventDefinitionAnalyzer());

        var plain = plan.ForNode(1);
        var signal = plan.ForNode(2);
        Assert.Equal(plain.ReadAliases.Order(), signal.ReadAliases.Order());
        Assert.Equal(plain.LockAliases.Order(), signal.LockAliases.Order());
        Assert.Equal(plain.ProducerAliases.Order(), signal.ProducerAliases.Order());
        Assert.Empty(signal.ProducerAliases);
    }

    /// <summary>
    /// Start #1 (a signal start or a plain start), then script #2 writing `first`, script #3 writing `second`, and end
    /// #4, all in one routing segment.
    /// </summary>
    private static WorkflowModel Model(bool signalStart) => new()
    {
        Id = $"signal-start-analysis-{Guid.NewGuid():N}",
        Name = "Analysis",
        InitialEventId = signalStart ? null : 1,
        FlowNodes =
        [
            signalStart
                ? new FlowNodeModel { Id = 1, Name = "Started", Type = BpmnFlowNodeTypes.SignalStartEvent, Signal = new SignalEventModel { Name = "tests.start" } }
                : new FlowNodeModel { Id = 1, Name = "Started", Type = BpmnFlowNodeTypes.StartEvent },
            Script(2, "first"),
            Script(3, "second"),
            new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
        ]
    };

    private static FlowNodeModel Script(int id, string variable) => new()
    {
        Id = id,
        Name = $"Write {variable}",
        Type = BpmnFlowNodeTypes.ScriptTask,
        Assignments = [new AssignmentModel { Variable = variable, Expression = "1" }]
    };

    private static VariableModel Shared(int id, string name, string key) => new()
    {
        Id = id,
        Name = name,
        Scope = VariableScopes.Shared,
        SharedKey = key,
        Access = SharedVariableAccessModes.ReadWrite,
        DataType = WorkflowVariableTypes.Number,
        Nullable = true
    };

    private static string? LockOrderProblem(WorkflowModel model)
    {
        var copy = JsonSerializer.Deserialize<WorkflowModel>(JsonSerializer.Serialize(model))!;
        WorkflowModelMigrator.Normalize(copy);
        var conditionalPlan = new ConditionalEventDefinitionAnalyzer().Analyze(copy);
        try
        {
            SharedVariableTransactionLockOrderValidator.Validate(
                copy,
                SharedVariableAccessPlanner.Build(copy, conditionalPlan),
                conditionalPlan);
            return null;
        }
        catch (WorkflowDomainException exception)
        {
            return exception.Message;
        }
    }
}
