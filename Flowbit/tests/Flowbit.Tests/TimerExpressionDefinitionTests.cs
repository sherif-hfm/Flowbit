using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Ai;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class TimerExpressionDefinitionTests
{
    [Theory]
    [InlineData("${contractEnd}", "if(urgent, 'PT4H', 'P2D')")]
    [InlineData("contractEnd", "${slaDuration}")]
    [InlineData("endDate + 'T17:00:00Z'", "'PT30S'")]
    [InlineData("[contractEnd]", "if(urgent, 'PT4H', fallbackSla)")]
    [InlineData("contractEnd", "if(Contains(fallbackSla, '[ x ]'), 'PT1H', 'P2D')")]
    [InlineData("contractEnd", "if(Max(1, 2) > 1 and urgent, 'PT4H', TRIM(fallbackSla))")]
    [InlineData("2030-01-01T09:00:00Z", "PT1H")]
    public void Validate_AcceptsExpressionsOverDeclaredStoredVariables(string date, string duration)
    {
        Validate(Model(date, duration));
    }

    [Fact]
    public void Analyze_PlansEachExpressionWithItsCanonicalReferences()
    {
        var model = Model("${CONTRACTEND}", "if(Urgent, 'PT4H', 'P2D')", calendar: "uk-office");
        WorkflowModelMigrator.Normalize(model);

        var plan = TimerExpressionDefinitionAnalyzer.Analyze(model);

        var date = plan.EntriesByNodeId[3];
        Assert.Equal("timeDate", date.Property);
        Assert.Equal("CONTRACTEND", date.Expression);
        Assert.Equal(["contractEnd"], date.References.ToArray());
        Assert.Null(date.Calendar);
        var duration = plan.EntriesByNodeId[4];
        Assert.Equal(["urgent"], duration.References.ToArray());
        Assert.Equal("uk-office", duration.Calendar);
        Assert.Equal("Timer boundary event #4 timer.timeDuration", duration.Description);
    }

    [Fact]
    public void Analyze_LeavesFixedSchedulesAlone()
    {
        var model = Model("2030-01-01T09:00:00Z", "PT1H");
        WorkflowModelMigrator.Normalize(model);

        Assert.Empty(TimerExpressionDefinitionAnalyzer.Analyze(model).EntriesByNodeId);
    }

    [Theory]
    [InlineData("missingSla", "Timer boundary event #4 timer.timeDuration references undeclared stored variable 'missingSla'.")]
    [InlineData("if([sys.now] > '2030', 'PT1H', 'P1D')", "Timer boundary event #4 timer.timeDuration references runtime context 'sys.now'; timer expressions can read only declared stored instance variables.")]
    [InlineData("[setting.examples.sla]", "Timer boundary event #4 timer.timeDuration references runtime context 'setting.examples.sla'; timer expressions can read only declared stored instance variables.")]
    [InlineData("approvalAmount", "Timer boundary event #4 timer.timeDuration references shared variable 'approvalAmount'; copy its value into an instance variable before the timer activates.")]
    [InlineData("Now()", "Timer boundary event #4 timer.timeDuration uses unsupported function 'Now'.")]
    [InlineData("if(urgent, 'PT4H'", "Timer boundary event #4 timer.timeDuration has an invalid NCalc expression: 'if(urgent, 'PT4H''.")]
    [InlineData("5", "Timer boundary event #4 timer.timeDuration cannot be scheduled: the expression gave a number, not text.")]
    [InlineData("'P' + 'X'", "Timer boundary event #4 timer.timeDuration cannot be scheduled: the expression gave text that is not a positive fixed-unit ISO-8601 duration.")]
    [InlineData("1 == 1", "Timer boundary event #4 timer.timeDuration cannot be scheduled: the expression gave a Boolean, not text.")]
    [InlineData("P${days}D", "Timer event #4 timeDuration 'P${days}D' inserts a value into text with ${...}, which timer schedules do not do; write one NCalc expression, optionally wrapped in a single ${...}.")]
    [InlineData("R/P1D", "Timer event #4 timeDuration must be a positive fixed-unit ISO-8601 duration; 'R/P1D' is a repeating cycle, which belongs in timeCycle.")]
    [InlineData("'P1Y'", "Timer event #4 timeDuration 'P1Y' is not a positive fixed-unit ISO-8601 duration.")]
    [InlineData("[ slaDuration ]", "Timer boundary event #4 timer.timeDuration references '[ slaDuration ]' with spaces inside the brackets; NCalc keeps them in the name, so it never matches a stored variable. Write '[slaDuration]'.")]
    [InlineData("if(urgent, [fallbackSla ], 'P2D')", "Timer boundary event #4 timer.timeDuration references '[fallbackSla ]' with spaces inside the brackets; NCalc keeps them in the name, so it never matches a stored variable. Write '[fallbackSla]'.")]
    [InlineData("[ missingSla ]", "Timer boundary event #4 timer.timeDuration references undeclared stored variable 'missingSla'.")]
    [InlineData("if([ sys.now ] > '2030', 'PT1H', 'P1D')", "Timer boundary event #4 timer.timeDuration references runtime context 'sys.now'; timer expressions can read only declared stored instance variables.")]
    [InlineData("[ approvalAmount ]", "Timer boundary event #4 timer.timeDuration references shared variable 'approvalAmount'; copy its value into an instance variable before the timer activates.")]
    // NCalc resolves built-ins only in their own spelling. These read a
    // variable, so a constant evaluation at save would not catch them.
    [InlineData("If(urgent, 'PT4H', 'P2D')", "Timer boundary event #4 timer.timeDuration uses unsupported function 'If'; write it as 'if'.")]
    [InlineData("if(max(1, 2) > 1 and urgent, 'PT4H', 'P2D')", "Timer boundary event #4 timer.timeDuration uses unsupported function 'max'; write it as 'Max'.")]
    [InlineData("if(urgent, If(urgent, 'PT4H', 'P1D'), 'P2D')", "Timer boundary event #4 timer.timeDuration uses unsupported function 'If'; write it as 'if'.")]
    public void Validate_RejectsWhatATimerExpressionCanNeverUse(string duration, string expected)
    {
        var error = Assert.Throws<WorkflowDomainException>(() => Validate(Model("contractEnd", duration)));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void Analyze_RejectsAPaddedBracketNameInATimeDate()
    {
        // The same analysis runs when a timer activates, so no instance can
        // reach the reference that would never get a value.
        var model = Model("[ contractEnd ]", "PT1H");
        WorkflowModelMigrator.Normalize(model);

        var error = Assert.Throws<WorkflowDomainException>(() => TimerExpressionDefinitionAnalyzer.Analyze(model));

        Assert.Equal(
            "Timer catch event #3 timer.timeDate references '[ contractEnd ]' with spaces inside the brackets; NCalc keeps them in the name, so it never matches a stored variable. Write '[contractEnd]'.",
            error.Message);
    }

    [Fact]
    public void Validate_RejectsFlowInfoAndComplexGatewayHelpersInTimerExpressions()
    {
        var flowInfo = Assert.Throws<WorkflowDomainException>(() =>
            Validate(Model("contractEnd", "if(FlowInfo(201, 'actions.count') > 0, 'PT1H', 'P1D')")));
        Assert.StartsWith("Timer event #4 timeDuration has invalid FlowInfo usage:", flowInfo.Message, StringComparison.Ordinal);

        var gateway = Assert.Throws<WorkflowDomainException>(() =>
            Validate(Model("contractEnd", "if(IncomingCount(101) > 0, 'PT1H', 'P1D')")));
        Assert.StartsWith("Timer event #4 timeDuration has invalid Complex gateway expression usage:", gateway.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ChecksAConstantResultAgainstItsCalendar()
    {
        var error = Assert.Throws<WorkflowDomainException>(() =>
            Validate(Model("contractEnd", "if(1 == 1, 'P1W', 'P2D')", calendar: "uk-office")));

        Assert.Equal(
            "Timer event #4 timeDuration 'P1W' cannot be counted in a business calendar; use whole working days and working time, such as P5D or P1DT4H.",
            error.Message);
        Validate(Model("contractEnd", "if(urgent, 'P1W', 'P2D')", calendar: "uk-office"));
    }

    [Fact]
    public void Evaluate_ReportsAFailureWithoutTheValueNCalcWouldRepeat()
    {
        var model = Model("contractEnd", "fallbackSla * 2");
        WorkflowModelMigrator.Normalize(model);
        var entry = TimerExpressionDefinitionAnalyzer.Analyze(model).EntriesByNodeId[4];
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["fallbackSla"] = JsonSerializer.SerializeToElement("secret-value-321")
        };

        // NCalc's own message for this failure repeats the text it could not convert.
        var ncalc = Assert.Throws<WorkflowDomainException>(() =>
            SequenceFlowConditionEvaluator.EvaluateValue("fallbackSla * 2", values));
        Assert.Contains("secret-value-321", ncalc.Message, StringComparison.Ordinal);

        Assert.False(TimerExpressionEvaluator.TryEvaluate(entry, values, out var text, out var problem));
        Assert.Empty(text);
        Assert.Equal("the expression could not be evaluated (FormatException)", problem);
    }

    [Fact]
    public void Validate_RejectsExpressionsOnTimerStarts()
    {
        var model = new WorkflowModel
        {
            Id = "timer-start-expression",
            Name = "Timer start expression",
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Every so often",
                    Type = BpmnFlowNodeTypes.TimerStartEvent,
                    Timer = new TimerDefinitionModel { TimeDuration = "slaDuration" }
                },
                new FlowNodeModel { Id = 2, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
        };

        var error = Assert.Throws<WorkflowDomainException>(() => Validate(model));

        Assert.Equal(
            "Timer event #1 timeDuration must be a fixed value on a timer start event; expressions are supported only on timer catch and boundary events.",
            error.Message);
    }

    [Fact]
    public void Validate_RejectsAmbiguousCasingOnlyForNamesAnExpressionReads()
    {
        var unrelated = Model("contractEnd", "if(urgent, 'PT4H', 'P2D')");
        AddDifferentlyCasedAmount(unrelated);
        Validate(unrelated);

        var referenced = Model("contractEnd", "if(amount > 10, 'PT4H', 'P2D')");
        AddDifferentlyCasedAmount(referenced);
        var error = Assert.Throws<WorkflowDomainException>(() => Validate(referenced));

        Assert.Equal(
            "Timer boundary event #4 timer.timeDuration references 'amount', whose stored variable declarations use ambiguous casing ('Amount', 'amount'); stored variable names are case-insensitive.",
            error.Message);

        static void AddDifferentlyCasedAmount(WorkflowModel model)
        {
            model.FlowNodes.Single(node => node.Id == 1).Variables.Add(new VariableModel
            {
                Id = 9,
                Name = "Amount",
                DataType = WorkflowVariableTypes.Number,
                Required = false
            });
            model.SequenceFlows.Single(flow => flow.Id == 201).Variables.Add(new VariableModel
            {
                Id = 10,
                Name = "amount",
                DataType = WorkflowVariableTypes.Number,
                Required = false
            });
        }
    }

    [Fact]
    public void AiDiagnostics_PointAtTheTimerNode()
    {
        var model = Model("contractEnd", "missingSla");
        var error = Assert.Throws<WorkflowDomainException>(() => Validate(Model("contractEnd", "missingSla")));
        var root = JsonSerializer.SerializeToElement(model);

        Assert.Equal("/flowNodes/3", WorkflowAiDiagnostics.DefinitionPointer(root, error.Message));
    }

    internal static WorkflowModel Model(string date, string duration, string? calendar = null) => new()
    {
        Id = "timer-expression-definition",
        Name = "Timer expression definition",
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "slaDuration",
                DataType = WorkflowVariableTypes.String,
                DefaultValue = JsonSerializer.SerializeToElement("P2D")
            },
            new VariableModel
            {
                Id = 2,
                Name = "approvalAmount",
                Scope = VariableScopes.Shared,
                SharedKey = "examples.approval.amount",
                Access = SharedVariableAccessModes.Read,
                DataType = WorkflowVariableTypes.Number,
                Nullable = false
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Variables =
                [
                    new VariableModel { Id = 3, Name = "contractEnd", DataType = WorkflowVariableTypes.DateTime, Required = true },
                    new VariableModel { Id = 4, Name = "urgent", DataType = WorkflowVariableTypes.Boolean, Required = true },
                    new VariableModel { Id = 5, Name = "endDate", DataType = WorkflowVariableTypes.Date, Required = false },
                    new VariableModel { Id = 6, Name = "fallbackSla", DataType = WorkflowVariableTypes.String, Required = false }
                ]
            },
            new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Wait until contract end",
                Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent,
                Timer = new TimerDefinitionModel { TimeDate = date }
            },
            new FlowNodeModel
            {
                Id = 4,
                Name = "Review deadline",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 2,
                CancelActivity = true,
                Timer = new TimerDefinitionModel { TimeDuration = duration, Calendar = calendar }
            },
            new FlowNodeModel { Id = 5, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 6, Name = "Escalated", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Approve", SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
            new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 6 }
        ]
    };

    internal static void Validate(WorkflowModel model)
    {
        var validator = new WorkflowDefinitionValidator(new ParseOnlyScriptEvaluator(), new ServiceTaskOptions());
        validator.ValidateAuthored(model);
        WorkflowModelMigrator.Normalize(model);
        validator.ValidateNormalized(model);
    }

    private sealed class ParseOnlyScriptEvaluator : IScriptEvaluator
    {
        public ScriptResult Evaluate(string script, IScriptContext context, CancellationToken cancellationToken) =>
            new(true, null);

        public bool IsValid(string script, out string? error)
        {
            error = null;
            return true;
        }
    }
}
