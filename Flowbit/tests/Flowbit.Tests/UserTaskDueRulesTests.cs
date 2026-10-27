using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class UserTaskDueRulesTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 27, 9, 30, 15, TimeSpan.Zero);

    [Theory]
    [InlineData("P2D", 2 * 24 * 60 * 60)]
    [InlineData(" PT4H ", 4 * 60 * 60)]
    [InlineData("P1W", 7 * 24 * 60 * 60)]
    [InlineData("PT0.5S", 0.5)]
    [InlineData("P1DT12H", 36 * 60 * 60)]
    public void FixedDurationsCountFromCreation(string text, double seconds)
    {
        Assert.True(UserTaskDueRules.TryParseFixedValue(text, out var value));

        Assert.Null(value.At);
        Assert.Equal(CreatedAt.AddSeconds(seconds), value.ResolveAt(CreatedAt));
    }

    [Theory]
    [InlineData("2030-01-31T17:00:00Z", "2030-01-31T17:00:00+00:00")]
    [InlineData("2030-01-31T17:00:00+02:00", "2030-01-31T15:00:00+00:00")]
    [InlineData("2020-01-01T09:00:00-05:00", "2020-01-01T14:00:00+00:00")]
    public void FixedDateTimesAreUsedAsUtcInstants(string text, string expected)
    {
        Assert.True(UserTaskDueRules.TryParseFixedValue(text, out var value));

        var resolved = value.ResolveAt(CreatedAt);
        Assert.Equal(DateTimeOffset.Parse(expected), resolved);
        Assert.Equal(TimeSpan.Zero, resolved!.Value.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("P0D")]
    [InlineData("P1M")]
    [InlineData("P1Y")]
    [InlineData("p2d")]
    [InlineData("2030-01-31")]
    [InlineData("2030-01-31T17:00:00")]
    [InlineData("tomorrow")]
    public void NonFixedValuesDoNotParse(string text)
    {
        Assert.False(UserTaskDueRules.TryParseFixedValue(text, out _));
    }

    [Fact]
    public void ResolveAtReturnsNullWhenADurationPassesTheLargestDate()
    {
        var value = UserTaskDueValue.Duration(TimeSpan.FromDays(3650));

        Assert.Null(value.ResolveAt(DateTimeOffset.MaxValue.AddDays(-1)));
        Assert.Equal(DateTimeOffset.MaxValue.AddDays(-3651).AddDays(3650), value.ResolveAt(DateTimeOffset.MaxValue.AddDays(-3651)));
    }

    [Theory]
    [InlineData("'P2D'", "P2D")]
    [InlineData("\"PT4H\"", "PT4H")]
    [InlineData(" 'text' ", "text")]
    [InlineData("'it''s'", null)]
    [InlineData("'a\\b'", null)]
    [InlineData("'P2D' + 'x'", null)]
    [InlineData("contractEnd", null)]
    [InlineData("'", null)]
    public void QuotedLiteralReturnsTheTextOfASingleLiteral(string expression, string? expected)
    {
        Assert.Equal(expected, UserTaskDueRules.QuotedLiteral(expression));
    }

    [Theory]
    [InlineData("P2D")]
    [InlineData("2030-01-31T17:00:00Z")]
    [InlineData("'P2D'")]
    [InlineData("\"2030-01-31T17:00:00+02:00\"")]
    [InlineData("contractEnd")]
    [InlineData("if(amount >= 10000, 'PT4H', 'P2D')")]
    [InlineData("p1Deadline")]
    [InlineData("[p2d]")]
    [InlineData("   ")]
    public void DueDateProblemAcceptsFixedValuesAndExpressions(string expression)
    {
        Assert.Null(UserTaskDueRules.DueDateProblem(expression));
    }

    [Theory]
    [InlineData("'P2X'", "dueDate 'P2X' is not")]
    [InlineData("\"2030-01-31\"", "dueDate '2030-01-31' is not")]
    [InlineData("p2d", "dueDate 'p2d' is not")]
    [InlineData("pt4h", "dueDate 'pt4h' is not")]
    [InlineData("P1M", "dueDate 'P1M' is not")]
    [InlineData("P1Y", "dueDate 'P1Y' is not")]
    [InlineData("P1Y2M", "dueDate 'P1Y2M' is not")]
    [InlineData("p2y", "dueDate 'p2y' is not")]
    [InlineData("2026-11-01", "dueDate '2026-11-01' is not")]
    [InlineData("2026-11-01T17:00:00", "dueDate '2026-11-01T17:00:00' is not")]
    public void DueDateProblemRejectsValuesThatCanNeverBeUsed(string expression, string expected)
    {
        var problem = UserTaskDueRules.DueDateProblem(expression);

        Assert.NotNull(problem);
        Assert.StartsWith(expected, problem, StringComparison.Ordinal);
        Assert.EndsWith(
            "an ISO-8601 duration such as P2D or a date-time with an offset such as 2030-01-31T17:00:00Z.",
            problem,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("50", null)]
    [InlineData("-5", null)]
    [InlineData("+7", null)]
    [InlineData("2147483647", null)]
    [InlineData("-2147483648", null)]
    [InlineData("amount * 2", null)]
    [InlineData("2147483648", "priority 2147483648 is outside the whole-number range -2147483648 to 2147483647.")]
    [InlineData(" -99999999999999999999 ", "priority -99999999999999999999 is outside the whole-number range -2147483648 to 2147483647.")]
    public void PriorityProblemChecksWholeNumberLiterals(string expression, string? expected)
    {
        Assert.Equal(expected, UserTaskDueRules.PriorityProblem(expression));
    }

    [Fact]
    public void DueDateResultsMustBeFixedText()
    {
        Assert.True(UserTaskDueRules.TryInterpretDueDateResult(null, out var none, out var noProblem));
        Assert.Null(none);
        Assert.Null(noProblem);

        Assert.True(UserTaskDueRules.TryInterpretDueDateResult(" P2D ", out var duration, out _));
        Assert.Equal(TimeSpan.FromDays(2), duration!.After);

        Assert.True(UserTaskDueRules.TryInterpretDueDateResult("2030-01-31T17:00:00+02:00", out var instant, out _));
        Assert.Equal(new DateTimeOffset(2030, 1, 31, 15, 0, 0, TimeSpan.Zero), instant!.At);

        Assert.False(UserTaskDueRules.TryInterpretDueDateResult("2030-01-31", out var date, out var dateProblem));
        Assert.Null(date);
        Assert.Contains("not an ISO-8601 duration", dateProblem, StringComparison.Ordinal);

        Assert.False(UserTaskDueRules.TryInterpretDueDateResult(5L, out _, out var numberProblem));
        Assert.Equal("the result is a Int64, not text", numberProblem);

        Assert.False(UserTaskDueRules.TryInterpretDueDateResult(new DateTime(2030, 1, 31), out _, out _));
    }

    [Fact]
    public void NCalcResultsForCommonDueDateExpressionsAreUsable()
    {
        var variables = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["amount"] = JsonSerializer.SerializeToElement(20000),
            ["dueDay"] = JsonSerializer.SerializeToElement("2030-01-31"),
            ["contractEnd"] = JsonSerializer.SerializeToElement("2030-01-31T17:00:00+02:00")
        };

        Assert.True(UserTaskDueRules.TryInterpretDueDateResult(
            Flowbit.Service.Services.SequenceFlowConditionEvaluator.EvaluateValue(
                "if(amount >= 10000, 'PT4H', 'P2D')", variables, preserveComplexTypes: true),
            out var conditional,
            out _));
        Assert.Equal(TimeSpan.FromHours(4), conditional!.After);

        Assert.True(UserTaskDueRules.TryInterpretDueDateResult(
            Flowbit.Service.Services.SequenceFlowConditionEvaluator.EvaluateValue(
                "dueDay + 'T17:00:00Z'", variables, preserveComplexTypes: true),
            out var fromDate,
            out _));
        Assert.Equal(new DateTimeOffset(2030, 1, 31, 17, 0, 0, TimeSpan.Zero), fromDate!.At);

        Assert.True(UserTaskDueRules.TryInterpretDueDateResult(
            Flowbit.Service.Services.SequenceFlowConditionEvaluator.EvaluateValue(
                "contractEnd", variables, preserveComplexTypes: true),
            out var fromDateTime,
            out _));
        Assert.Equal(new DateTimeOffset(2030, 1, 31, 15, 0, 0, TimeSpan.Zero), fromDateTime!.At);

        Assert.True(UserTaskDueRules.TryInterpretPriorityResult(
            Flowbit.Service.Services.SequenceFlowConditionEvaluator.EvaluateValue(
                "if(amount >= 10000, 80, 40)", variables, preserveComplexTypes: true),
            out var priority,
            out _));
        Assert.Equal(80, priority);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(50L, 50)]
    [InlineData(2.0d, 2)]
    [InlineData(-3.0f, -3)]
    [InlineData((short)7, 7)]
    public void PriorityResultsAcceptWholeNumbers(object result, int expected)
    {
        Assert.True(UserTaskDueRules.TryInterpretPriorityResult(result, out var priority, out var problem));

        Assert.Equal(expected, priority);
        Assert.Null(problem);
    }

    [Fact]
    public void PriorityResultsRejectEverythingElse()
    {
        Assert.True(UserTaskDueRules.TryInterpretPriorityResult(null, out var none, out _));
        Assert.Null(none);
        Assert.True(UserTaskDueRules.TryInterpretPriorityResult(3.0m, out var fromDecimal, out _));
        Assert.Equal(3, fromDecimal);

        foreach (var result in new object[]
                 {
                     2.5d, 3.5m, double.NaN, double.PositiveInfinity, 3_000_000_000L,
                     -3_000_000_000d, ulong.MaxValue, "5", true
                 })
        {
            Assert.False(UserTaskDueRules.TryInterpretPriorityResult(result, out var priority, out var problem));
            Assert.Null(priority);
            Assert.False(string.IsNullOrWhiteSpace(problem));
        }
    }

    [Fact]
    public void DueClockClassifiesOpenWorkItems()
    {
        var clock = new UserTaskDueClock(CreatedAt, TimeSpan.FromHours(24));

        Assert.Equal(UserTaskDueStatuses.Overdue, clock.StatusFor(CreatedAt, open: true));
        Assert.Equal(UserTaskDueStatuses.Overdue, clock.StatusFor(CreatedAt.AddMinutes(-1), open: true));
        Assert.Equal(UserTaskDueStatuses.DueSoon, clock.StatusFor(CreatedAt.AddTicks(1), open: true));
        Assert.Equal(UserTaskDueStatuses.DueSoon, clock.StatusFor(CreatedAt.AddHours(24), open: true));
        Assert.Equal(UserTaskDueStatuses.Upcoming, clock.StatusFor(CreatedAt.AddHours(24).AddTicks(1), open: true));
        Assert.Null(clock.StatusFor(null, open: true));
        Assert.Null(clock.StatusFor(CreatedAt.AddMinutes(-1), open: false));
        Assert.Equal(DateTimeOffset.MaxValue, new UserTaskDueClock(DateTimeOffset.MaxValue.AddDays(-1), TimeSpan.FromDays(2)).DueSoonUntil);
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData("", 24)]
    [InlineData("not a duration", 24)]
    [InlineData("P0D", 24)]
    [InlineData("PT4H", 4)]
    [InlineData(" P2D ", 48)]
    public void DueSoonThresholdFallsBackToOneDay(string? value, int hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), UserTaskDueSettings.ParseDueSoonThreshold(value));
    }

    [Fact]
    public void UserTaskFieldsRoundTripAndOlderNodesGainNoKeys()
    {
        var node = new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask };
        var plain = JsonSerializer.SerializeToNode(node)!.AsObject();
        Assert.False(plain.ContainsKey("dueDate"));
        Assert.False(plain.ContainsKey("priority"));

        node.DueDateExpression = "if(amount > 10, 'PT4H', 'P2D')";
        node.PriorityExpression = "80";
        var json = JsonSerializer.Serialize(node);
        var restored = JsonSerializer.Deserialize<FlowNodeModel>(json)!;

        Assert.Equal("if(amount > 10, 'PT4H', 'P2D')", restored.DueDateExpression);
        Assert.Equal("80", restored.PriorityExpression);
        Assert.Equal("80", JsonNode.Parse(json)!["priority"]!.GetValue<string>());
    }

    [Fact]
    public void MigratorTrimsUserTaskValuesAndClearsThemElsewhere()
    {
        var model = new WorkflowModel
        {
            Id = "due-migrator",
            Name = "Due migrator",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent, DueDateExpression = "P2D", PriorityExpression = "5" },
                new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask, DueDateExpression = "  P2D  ", PriorityExpression = "   " },
                new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
            ]
        };

        WorkflowModelMigrator.Normalize(model);
        var once = JsonSerializer.Serialize(model);
        WorkflowModelMigrator.Normalize(model);

        Assert.Null(model.FlowNodes[0].DueDateExpression);
        Assert.Null(model.FlowNodes[0].PriorityExpression);
        Assert.Equal("P2D", model.FlowNodes[1].DueDateExpression);
        Assert.Null(model.FlowNodes[1].PriorityExpression);
        Assert.Equal(once, JsonSerializer.Serialize(model));
    }
}
