using System.Text.Json;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The signal-name rules, the catch's definition validation through the API's validator,
/// and how the analyses treat a signal catch: as a durable resting position.
/// </summary>
public sealed class SignalEventRulesTests
{
    [Theory]
    [InlineData("a", true)]
    [InlineData("claims.policy-changed", true)]
    [InlineData("v2_release", true)]
    [InlineData("0starts-with-digit", true)]
    [InlineData("Policy", false)]
    [InlineData("-leading", false)]
    [InlineData(".leading", false)]
    [InlineData("has space", false)]
    [InlineData("trailing\n", false)]
    [InlineData("ümlaut", false)]
    [InlineData("", false)]
    public void Name_IsLowercaseAsciiWithDotsUnderscoresAndHyphens(string name, bool valid)
    {
        Assert.Equal(valid, SignalEventRules.IsValidName(name));
    }

    [Fact]
    public void Name_IsAtMostOneHundredCharacters()
    {
        Assert.True(SignalEventRules.IsValidName(new string('a', 100)));
        Assert.False(SignalEventRules.IsValidName(new string('a', 101)));
        Assert.Equal("Signal name is required.", SignalEventRules.ThrowNameProblem(null));
        Assert.StartsWith("Signal name must be 1 to 100 lowercase letters",
            SignalEventRules.ThrowNameProblem("Policy"));
    }

    [Fact]
    public void HasSignalEvents_IsTrueOnlyForADefinitionWithASignalEvent()
    {
        Assert.True(SignalEventRules.HasSignalEvents(SignalRuntimeTests.CatchModel("a.b")));
        var plain = SignalRuntimeTests.CatchModel("a.b");
        plain.FlowNodes.RemoveAll(node => node.Id == 2);
        Assert.False(SignalEventRules.HasSignalEvents(plain));
        Assert.True(WorkflowDefinitionReadinessChecker.RequiresDurableProcessing(SignalRuntimeTests.CatchModel("a.b")));
    }

    public static TheoryData<string, Action<WorkflowModel>> InvalidDefinitions() => new()
    {
        { "Signal catch event #2 must define signal.name.", model => Catch(model).Signal!.Name = "" },
        { "Signal catch event #2 must define signal.name.", model => Catch(model).Signal = null },
        {
            "Signal catch event #2 signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.",
            model => Catch(model).Signal!.Name = "Policy.Changed"
        },
        {
            "Signal catch event #2 defines more than 100 output mappings.",
            model => Catch(model).Signal!.OutputMappings = Enumerable.Range(1, 101)
                .Select(index => new MessageOutputMappingModel
                {
                    Variable = $"value{index}", Path = $"v{index}", DataType = WorkflowVariableTypes.String, IsArray = false
                }).ToList()
        },
        {
            "Signal catch event #2 must have at least one incoming sequence flow.",
            model => model.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 3
        },
        {
            "Signal catch event #2 must have exactly one outgoing sequence flow.",
            model => model.SequenceFlows.RemoveAll(flow => flow.SourceRef == 2)
        },
        {
            "Signal catch event #2 must have one unconditional outgoing sequence flow without user-action or multi-instance metadata.",
            model => model.SequenceFlows.Single(flow => flow.Id == 201).Condition = "true"
        },
        {
            "Signal catch event #2 output mapping for 'policyVersion' must declare dataType and isArray.",
            model => Catch(model).Signal!.OutputMappings[0].DataType = null
        },
        {
            "Flow node #3 defines signal but is not a signal event.",
            model => model.FlowNodes.Single(node => node.Id == 3).Signal = new SignalEventModel { Name = "x" }
        },
        {
            "Signal catch event #2 cannot define task, role, variable, timer, conditional, message, async, or multi-instance settings.",
            model => Catch(model).Message = new MessageCatchModel()
        },
        {
            "Signal catch event #2 output mapping 'rate' cannot write shared variable alias 'rate'; signal mappings write instance variables only.",
            model =>
            {
                model.Variables = [Shared("rate", "tests.signal.rate", "readWrite")];
                Catch(model).Signal!.OutputMappings[0].Variable = "rate";
            }
        },
        {
            "Signal catch event #2 output mapping 'policyVersion' cannot read shared variable alias 'baseRate' in its default or validation.",
            model =>
            {
                model.Variables = [Shared("baseRate", "tests.signal.base-rate", "read")];
                Catch(model).Signal!.OutputMappings[0].Validation = "policyVersion > baseRate";
            }
        },
        {
            "Signal catch event #2 output mapping 'policyVersion' cannot read shared variable alias 'baseRate' in its default or validation.",
            model =>
            {
                model.Variables = [Shared("baseRate", "tests.signal.base-rate", "read")];
                var mapping = Catch(model).Signal!.OutputMappings[0];
                mapping.DataType = WorkflowVariableTypes.String;
                mapping.DefaultValue = JsonSerializer.SerializeToElement("${baseRate}");
            }
        }
    };

    [Theory]
    [MemberData(nameof(InvalidDefinitions))]
    public void Definition_RefusesAnInvalidSignalCatch(string expected, Action<WorkflowModel> change)
    {
        var model = SignalRuntimeTests.CatchModel("tests.signal");
        change(model);
        var error = Assert.Throws<WorkflowDomainException>(() => TimerExpressionDefinitionTests.Validate(model));
        Assert.Equal(expected, error.Message);
    }

    public static TheoryData<string, Action<WorkflowModel>> InvalidBoundaryDefinitions() => new()
    {
        { "Signal boundary event #3 must reference a host via attachedToRef.", model => Boundary(model).AttachedToRef = null },
        {
            "Signal boundary event #3 host #4 must be a durable wait or an automatic task with asyncBefore enabled.",
            model => Boundary(model).AttachedToRef = 4
        },
        {
            "Signal boundary event #3 cannot have incoming sequence flows.",
            model => model.SequenceFlows.Add(new SequenceFlowModel { Id = 502, SourceRef = 5, TargetRef = 3, Name = "Again" })
        },
        {
            "Signal boundary event #3 signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.",
            model => Boundary(model).Signal!.Name = "Recall"
        },
        {
            "Signal boundary event #7 uses signal 'tests.recall', which signal boundary event #3 on host #2 already uses.",
            model =>
            {
                model.FlowNodes.Add(new FlowNodeModel
                {
                    Id = 7, Name = "Again", Type = BpmnFlowNodeTypes.SignalBoundaryEvent, AttachedToRef = 2,
                    Signal = new SignalEventModel { Name = "tests.recall" }
                });
                model.SequenceFlows.Add(new SequenceFlowModel { Id = 701, SourceRef = 7, TargetRef = 6 });
            }
        },
        {
            "Flow node #5 defines cancelActivity but is not a timer, conditional, or signal boundary event.",
            model => model.FlowNodes.Single(node => node.Id == 5).CancelActivity = false
        },
        {
            "Host node #2 has 9 timer, conditional, and signal boundary events; at most 8 are allowed.",
            model =>
            {
                foreach (var id in Enumerable.Range(10, 8))
                {
                    model.FlowNodes.Add(new FlowNodeModel
                    {
                        Id = id, Name = $"Timer {id}", Type = BpmnFlowNodeTypes.TimerBoundaryEvent, AttachedToRef = 2,
                        Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
                    });
                    model.SequenceFlows.Add(new SequenceFlowModel { Id = id * 100 + 1, SourceRef = id, TargetRef = 6 });
                }
            }
        }
    };

    [Theory]
    [MemberData(nameof(InvalidBoundaryDefinitions))]
    public void Definition_RefusesAnInvalidSignalBoundary(string expected, Action<WorkflowModel> change)
    {
        var model = SignalRuntimeTests.BoundaryModel("tests.recall", cancelActivity: false);
        change(model);
        var error = Assert.Throws<WorkflowDomainException>(() => TimerExpressionDefinitionTests.Validate(model));
        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void Definition_RefusesABoundaryThatRepeatsItsHostCatchsName()
    {
        var model = SignalRuntimeTests.CatchModel("tests.signal");
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5, Name = "Same", Type = BpmnFlowNodeTypes.SignalBoundaryEvent, AttachedToRef = 2,
            Signal = new SignalEventModel { Name = "tests.signal" }
        });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 4 });
        var error = Assert.Throws<WorkflowDomainException>(() => TimerExpressionDefinitionTests.Validate(model));
        Assert.Equal("Signal boundary event #5 uses signal 'tests.signal', the signal its host signal catch event #2 waits for.", error.Message);
    }

    [Fact]
    public void Definition_AcceptsSignalBoundariesAndKeepsTheirInterruptingFlag()
    {
        var model = SignalRuntimeTests.BoundaryModel("tests.recall", cancelActivity: false);
        TimerExpressionDefinitionTests.Validate(model);
        WorkflowModelMigrator.Normalize(model);
        Assert.False(Boundary(model).CancelActivity);
        Assert.Equal(2, Boundary(model).AttachedToRef);

        var interrupting = SignalRuntimeTests.BoundaryModel("tests.recall", cancelActivity: true);
        Boundary(interrupting).CancelActivity = null;
        WorkflowModelMigrator.Normalize(interrupting);
        Assert.True(Boundary(interrupting).CancelActivity);
        Assert.True(SignalEventRules.HasSignalEvents(interrupting));
    }

    private static FlowNodeModel Boundary(WorkflowModel model) => model.FlowNodes.Single(node => node.Id == 3);

    [Fact]
    public void Definition_AcceptsTimerAndConditionalBoundariesOnASignalCatch()
    {
        var model = SignalRuntimeTests.CatchModel("tests.signal");
        model.Variables =
        [
            new VariableModel { Id = 1, Name = "policyVersion", DataType = WorkflowVariableTypes.Number, Nullable = true }
        ];
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5, Name = "Timeout", Type = BpmnFlowNodeTypes.TimerBoundaryEvent, AttachedToRef = 2,
            Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
        });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 6, Name = "High", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent, AttachedToRef = 2,
            Conditional = new ConditionalDefinitionModel { Condition = "policyVersion > 5" }
        });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 4 });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 4 });

        TimerExpressionDefinitionTests.Validate(model);
    }

    [Fact]
    public void Definition_AcceptsAMappingIntoANewInstanceVariableAndADeclaredProcessVariable()
    {
        var model = SignalRuntimeTests.CatchModel("tests.signal");
        Catch(model).Signal!.OutputMappings.Add(new MessageOutputMappingModel
        {
            Variable = "note", Path = "note", DataType = WorkflowVariableTypes.String, IsArray = false,
            DefaultValue = JsonSerializer.SerializeToElement("none")
        });
        TimerExpressionDefinitionTests.Validate(model);
        Assert.Equal("policyVersion", Catch(model).Signal!.OutputMappings[0].Variable);
    }

    [Fact]
    public void Migrator_KeepsTheSignalOnACatchAndRemovesItFromOtherTypes()
    {
        var model = SignalRuntimeTests.CatchModel("Not.Rewritten");
        model.FlowNodes.Single(node => node.Id == 3).Signal = new SignalEventModel { Name = "x" };
        Catch(model).Signal!.OutputMappings[0].DataType = "NUMBER";
        WorkflowModelMigrator.Normalize(model);
        Assert.Equal("Not.Rewritten", Catch(model).Signal!.Name);
        Assert.Equal(WorkflowVariableTypes.Number, Catch(model).Signal!.OutputMappings[0].DataType);
        Assert.Null(model.FlowNodes.Single(node => node.Id == 3).Signal);
    }

    [Fact]
    public void LockOrder_TreatsASignalCatchAsARestingPositionLikeAMessageCatch()
    {
        // The catch ends the first segment, so writing "a" after writing "z" before it is safe.
        var model = SignalRuntimeTests.CatchModel("tests.signal");
        model.Variables =
        [
            Shared("first", "tests.lock.z", "readWrite"),
            Shared("second", "tests.lock.a", "readWrite")
        ];
        model.FlowNodes.Add(Script(5, "first"));
        model.FlowNodes.Add(Script(6, "second"));
        model.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 5;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 2 });
        model.SequenceFlows.Single(flow => flow.Id == 201).TargetRef = 6;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 3 });

        Assert.Null(LockOrderProblem(model));

        var message = Copy(model);
        var catchNode = Catch(message);
        catchNode.Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent;
        catchNode.Signal = null;
        catchNode.Message = new MessageCatchModel
        {
            ClientId = "c", ClientSecret = "s", HeaderName = "X-Test", HeaderValue = "v"
        };
        Assert.Null(LockOrderProblem(message));

        var withoutWait = Copy(model);
        withoutWait.FlowNodes.RemoveAll(node => node.Id == 2);
        withoutWait.SequenceFlows.RemoveAll(flow => flow.Id == 201);
        withoutWait.SequenceFlows.Single(flow => flow.Id == 501).TargetRef = 6;
        Assert.NotNull(LockOrderProblem(withoutWait));
    }

    private static FlowNodeModel Catch(WorkflowModel model) => model.FlowNodes.Single(node => node.Id == 2);

    private static VariableModel Shared(string alias, string key, string access) => new()
    {
        Id = Math.Abs(alias.GetHashCode()) % 1000 + 10,
        Name = alias,
        Scope = VariableScopes.Shared,
        SharedKey = key,
        Access = access,
        DataType = WorkflowVariableTypes.Number,
        IsArray = false
    };

    private static FlowNodeModel Script(int id, string variable) => new()
    {
        Id = id,
        Name = $"Write {variable}",
        Type = BpmnFlowNodeTypes.ScriptTask,
        Assignments = [new AssignmentModel { Variable = variable, Expression = "1" }]
    };

    private static string? LockOrderProblem(WorkflowModel model)
    {
        var copy = Copy(model);
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

    private static WorkflowModel Copy(WorkflowModel model) =>
        JsonSerializer.Deserialize<WorkflowModel>(JsonSerializer.Serialize(model))!;
}
