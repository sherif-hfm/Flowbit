using System.Text.Json;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class TimerExpressionRulesTests
{
    private const string DurationForm = "timeDuration must be a positive fixed-unit ISO-8601 duration";
    private const string DateForm = "timeDate must be an ISO-8601 timestamp with an explicit UTC offset";

    // Shared with the editor's parity test, so both sides classify these values the same way.
    public static TheoryData<string, string, bool, string, string?> Classifications => new()
    {
        { "timeDuration", "PT1H", true, "Fixed", null },
        { "timeDuration", "  P2D  ", true, "Fixed", null },
        { "timeDate", "2030-01-01T09:00:00+02:00", true, "Fixed", null },
        { "timeDuration", "${slaDuration}", true, "Expression", "slaDuration" },
        { "timeDuration", "slaDuration", true, "Expression", "slaDuration" },
        { "timeDuration", "if(urgent, 'PT4H', 'P2D')", true, "Expression", "if(urgent, 'PT4H', 'P2D')" },
        { "timeDuration", "[PT1H]", true, "Expression", "[PT1H]" },
        { "timeDuration", "${PT1H}", true, "Expression", "PT1H" },
        { "timeDuration", "'PT30S'", true, "Expression", "'PT30S'" },
        { "timeDate", "${contractEnd}", true, "Expression", "contractEnd" },
        { "timeDate", "endDate + 'T17:00:00Z'", true, "Expression", "endDate + 'T17:00:00Z'" },
        { "timeDuration", "P1Y", true, "Invalid", DurationForm + "." },
        { "timeDuration", "P1M", true, "Invalid", DurationForm + "." },
        { "timeDuration", "PT0S", true, "Invalid", DurationForm + "." },
        { "timeDuration", "p2d", true, "Invalid", DurationForm + "." },
        { "timeDuration", "-P1D", true, "Invalid", DurationForm + "." },
        { "timeDuration", "P١D", true, "Invalid", DurationForm + "." },
        { "timeDuration", "R/P1D", true, "Invalid", DurationForm + "; 'R/P1D' is a repeating cycle, which belongs in timeCycle." },
        { "timeDuration", "2030-01-01T09:00:00Z", true, "Invalid", DurationForm + "; '2030-01-01T09:00:00Z' is a date-time, which belongs in timeDate." },
        { "timeDate", "PT1H", true, "Invalid", DateForm + "; 'PT1H' is a duration, which belongs in timeDuration." },
        { "timeDate", "2026-11-01", true, "Invalid", DateForm + "." },
        { "timeDate", "2026-11-01T10:00:00", true, "Invalid", DateForm + "." },
        { "timeDuration", "P${days}D", true, "Invalid", "timeDuration 'P${days}D' inserts a value into text with ${...}, which timer schedules do not do; write one NCalc expression, optionally wrapped in a single ${...}." },
        { "timeDuration", "${a}${b}", true, "Invalid", "timeDuration '${a}${b}' inserts a value into text with ${...}, which timer schedules do not do; write one NCalc expression, optionally wrapped in a single ${...}." },
        { "timeDuration", "${ }", true, "Invalid", DurationForm + "." },
        { "timeDuration", "'P1Y'", true, "Invalid", "timeDuration 'P1Y' is not a positive fixed-unit ISO-8601 duration." },
        { "timeDate", "'2030-01-01'", true, "Invalid", "timeDate '2030-01-01' is not an ISO-8601 timestamp with an explicit UTC offset." },
        { "timeDuration", "slaDuration", false, "Invalid", "timeDuration must be a fixed value on a timer start event; expressions are supported only on timer catch and boundary events." },
        { "timeDuration", "P1Y", false, "Invalid", DurationForm + "." },
        { "timeDuration", "PT1H", false, "Fixed", null }
    };

    [Theory]
    [MemberData(nameof(Classifications))]
    public void ClassifyScheduleValue_SeparatesFixedValuesExpressionsAndUnusableText(
        string property,
        string value,
        bool allowExpressions,
        string kind,
        string? expressionOrProblem)
    {
        var classified = TimerDefinitionRules.ClassifyScheduleValue(property, value, allowExpressions);

        Assert.Equal(kind, classified.Kind.ToString());
        Assert.Equal(expressionOrProblem, kind == "Invalid" ? classified.Problem : classified.Expression);
    }

    [Fact]
    public void ClassifyScheduleValue_CountsUtf16CodeUnitsAfterTrimming()
    {
        var fits = $"[{new string('d', 126)}]";
        var tooLong = $"[{new string('d', 127)}]";
        // Each of these characters is two UTF-16 code units, as JavaScript's length also counts them.
        var nonBmpFits = $"'{string.Concat(Enumerable.Repeat("\U0001F600", 63))}'";
        var nonBmpTooLong = $"'{string.Concat(Enumerable.Repeat("\U0001F600", 64))}'";

        Assert.Equal(128, fits.Length);
        Assert.Equal(TimerScheduleValueKind.Expression, TimerDefinitionRules.ClassifyScheduleValue("timeDuration", $"  {fits}  ", true).Kind);
        Assert.Equal(
            "timeDuration must contain at most 128 characters.",
            TimerDefinitionRules.ClassifyScheduleValue("timeDuration", tooLong, true).Problem);
        Assert.Equal(128, nonBmpFits.Length);
        Assert.StartsWith(
            "timeDuration '",
            TimerDefinitionRules.ClassifyScheduleValue("timeDuration", nonBmpFits, true).Problem);
        Assert.Equal(
            "timeDuration must contain at most 128 characters.",
            TimerDefinitionRules.ClassifyScheduleValue("timeDuration", nonBmpTooLong, true).Problem);
    }

    [Theory]
    [InlineData("timeDuration", "PT4H", true, null)]
    [InlineData("timeDuration", "  P1DT2H  ", true, null)]
    [InlineData("timeDate", "2030-01-01T09:00:00+02:00", true, null)]
    [InlineData("timeDuration", "P1Y", false, "the expression gave text that is not a positive fixed-unit ISO-8601 duration")]
    [InlineData("timeDuration", "PT0S", false, "the expression gave text that is not a positive fixed-unit ISO-8601 duration")]
    [InlineData("timeDuration", "2030-01-01T09:00:00Z", false, "the expression gave text that is not a positive fixed-unit ISO-8601 duration")]
    [InlineData("timeDate", "2030-01-01", false, "the expression gave text that is not an ISO-8601 timestamp with an explicit UTC offset")]
    [InlineData("timeDate", "2030-01-01T09:00:00", false, "the expression gave text that is not an ISO-8601 timestamp with an explicit UTC offset")]
    [InlineData("timeDate", "PT1H", false, "the expression gave text that is not an ISO-8601 timestamp with an explicit UTC offset")]
    [InlineData("timeDuration", "   ", false, "the expression gave blank text")]
    public void TryInterpretExpressionResult_AcceptsOnlyTextForTheProperty(
        string property,
        string result,
        bool accepted,
        string? problem)
    {
        Assert.Equal(accepted, TimerDefinitionRules.TryInterpretExpressionResult(property, result, out var text, out var actual));
        Assert.Equal(problem, actual);
        Assert.Equal(accepted ? result.Trim() : string.Empty, text);
    }

    [Fact]
    public void TryInterpretExpressionResult_NamesTheKindOfANonTextResultWithoutItsValue()
    {
        var results = new (object? Value, string Problem)[]
        {
            (null, "the expression gave no value"),
            (true, "the expression gave a Boolean, not text"),
            (3600L, "the expression gave a number, not text"),
            (1.5d, "the expression gave a number, not text"),
            (new List<object?> { "PT1H" }, "the expression gave a list, not text"),
            (JsonSerializer.SerializeToElement(new { secret = "PT1H" }), "the expression gave an object, not text"),
            (new DateTime(2030, 1, 1), "the expression gave a date value, not text"),
            (new string('P', 129), "the expression gave text longer than 128 characters")
        };

        foreach (var (value, problem) in results)
        {
            Assert.False(TimerDefinitionRules.TryInterpretExpressionResult("timeDuration", value, out _, out var actual));
            Assert.Equal(problem, actual);
        }
    }

    [Theory]
    [InlineData("'P1W'", "timeDuration 'P1W' cannot be counted in a business calendar; use whole working days and working time, such as P5D or P1DT4H.")]
    [InlineData("${'P1.5D'}", "timeDuration 'P1.5D' cannot be counted in a business calendar; use whole working days and working time, such as P5D or P1DT4H.")]
    [InlineData("'P2D'", null)]
    [InlineData("slaDuration", null)]
    [InlineData("if(urgent, 'P1W', 'P2D')", null)]
    public void CalendarProblem_TreatsAQuotedLiteralAsAFixedDuration(string duration, string? problem)
    {
        Assert.Equal(
            problem,
            TimerDefinitionRules.CalendarProblem(new TimerDefinitionModel { TimeDuration = duration, Calendar = "uk-office" }));
    }

    [Theory]
    [InlineData("p2d", "dueDate 'p2d' is not an ISO-8601 duration such as P2D or a date-time with an offset such as 2030-01-31T17:00:00Z.")]
    [InlineData("'P2X'", "dueDate 'P2X' is not an ISO-8601 duration such as P2D or a date-time with an offset such as 2030-01-31T17:00:00Z.")]
    [InlineData("-P1D", null)]
    [InlineData("R/P1D", null)]
    public void DueDateProblem_KeepsItsOwnClassificationAfterSharingTheRule(string dueDate, string? problem)
    {
        // A due date shares the intended-fixed rule but not the timer-only additions.
        Assert.Equal(problem, UserTaskDueRules.DueDateProblem(dueDate));
    }
}
