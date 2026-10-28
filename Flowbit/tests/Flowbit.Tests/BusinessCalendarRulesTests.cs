using System.Text.Json;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class BusinessCalendarRulesTests
{
    [Theory]
    [InlineData("P3D", 3, 0)]
    [InlineData("PT4H", 0, 4 * 3600)]
    [InlineData(" P1DT4H30M ", 1, 4.5 * 3600)]
    [InlineData("PT1.5H", 0, 1.5 * 3600)]
    [InlineData("P0DT1S", 0, 1)]
    public void WorkingDurationsUseWholeDaysAndATimePart(string text, int days, double seconds)
    {
        Assert.True(TimerDefinitionRules.TryParseWorkingDuration(text, out var duration));
        Assert.Equal(new WorkingDuration(days, TimeSpan.FromSeconds(seconds)), duration);
    }

    [Theory]
    [InlineData("P1W")]
    [InlineData("P1.5D")]
    [InlineData("P1,5D")]
    [InlineData("P0D")]
    [InlineData("P1M")]
    [InlineData("P١D")]
    [InlineData("")]
    [InlineData(null)]
    public void WeeksFractionalDaysAndInvalidDurationsAreNotWorkingDurations(string? text)
    {
        Assert.False(TimerDefinitionRules.TryParseWorkingDuration(text, out _));
    }

    // Other scripts' digits are not durations, times, or numbers here, as in the editor; the invariant parsers would
    // otherwise reject them with an exception.
    [Fact]
    public void OnlyAsciiDigitsAreRead()
    {
        const string one = "١";
        Assert.False(TimerDefinitionRules.TryParseFixedDuration($"P{one}D", out _));
        Assert.False(TimerDefinitionRules.TryParseFixedDuration($"PT1.{one}H", out _));
        Assert.False(TimerDefinitionRules.TryParseTimeCycle($"R{one}/PT1H", out _, out _));
        Assert.False(TimerDefinitionRules.TryParseTimeDate($"2030-01-0{one}T09:00:00Z", out _));
        Assert.False(UserTaskDueRules.TryParseFixedValue($"P{one}D", out _));
        Assert.Null(UserTaskDueRules.PriorityProblem($"{one}{one}"));
        Assert.Null(TimerDefinitionRules.CalendarProblem(new TimerDefinitionModel { TimeDuration = $"P{one}D", Calendar = "uk-office" }));
        Assert.Null(UserTaskDueRules.DueDateCalendarProblem($"P{one}D", "uk-office"));
    }

    [Theory]
    [InlineData(null, "PT1H", null, null)]
    [InlineData("uk-office", "P2D", null, null)]
    [InlineData("uk-office", null, "R3/PT1H", "names a calendar, but only a timeDuration can be counted in a business calendar.")]
    [InlineData("uk-office", "P2W", null, "timeDuration 'P2W' cannot be counted in a business calendar; use whole working days and working time, such as P5D or P1DT4H.")]
    [InlineData("_office", "P2D", null, "calendar '_office' is not a valid business calendar name; use 1 to 100 letters, digits, '.', '_', or '-', starting with a letter or digit.")]
    [InlineData("uk-office", "nonsense", null, null)]
    [InlineData("uk-office", "P36525D", null, null)]
    [InlineData("uk-office", "P36526D", null, "timeDuration 'P36526D' is longer than a business calendar can count; use at most 36,525 days, about 100 years.")]
    [InlineData("uk-office", "P36524DT25H", null, "timeDuration 'P36524DT25H' is longer than a business calendar can count; use at most 36,525 days, about 100 years.")]
    [InlineData("uk-office", "PT876600H", null, null)]
    [InlineData("uk-office", "PT876601H", null, "timeDuration 'PT876601H' is longer than a business calendar can count; use at most 36,525 days, about 100 years.")]
    public void TimerCalendarProblemsAreDescribed(string? calendar, string? duration, string? cycle, string? expected)
    {
        var timer = new TimerDefinitionModel { Calendar = calendar, TimeDuration = duration, TimeCycle = cycle };

        Assert.Equal(expected, TimerDefinitionRules.CalendarProblem(timer));
    }

    [Theory]
    [InlineData("P2D", null, null)]
    [InlineData("P2D", "uk-office", null)]
    [InlineData("'PT4H'", "uk-office", null)]
    [InlineData("contractEnd", "uk-office", null)]
    [InlineData(null, "uk-office", "defines dueDateCalendar without a dueDate.")]
    [InlineData("2030-01-31T17:00:00Z", "uk-office", "dueDate is a fixed date-time, which a business calendar cannot change; remove dueDateCalendar.")]
    [InlineData("P1W", "uk-office", "dueDate 'P1W' cannot be counted in a business calendar; use whole working days and working time, such as P5D or P1DT4H.")]
    [InlineData("P2D", "a b", "dueDateCalendar 'a b' is not a valid business calendar name; use 1 to 100 letters, digits, '.', '_', or '-', starting with a letter or digit.")]
    [InlineData("'P36526D'", "uk-office", "dueDate 'P36526D' is longer than a business calendar can count; use at most 36,525 days, about 100 years.")]
    public void DueDateCalendarProblemsAreDescribed(string? dueDate, string? calendar, string? expected)
    {
        Assert.Equal(expected, UserTaskDueRules.DueDateCalendarProblem(dueDate, calendar));
    }

    [Fact]
    public void ACalendarTurnsADurationIntoWorkingTimeAndLeavesADateTime()
    {
        Assert.True(UserTaskDueRules.TryParseFixedValue("P1DT2H", out var duration));
        Assert.True(UserTaskDueRules.TryApplyCalendar(duration, "P1DT2H", " uk-office ", out var working, out _));
        Assert.Equal(new WorkingDuration(1, TimeSpan.FromHours(2)), working.Working);
        Assert.Equal("uk-office", working.Calendar);
        Assert.Throws<InvalidOperationException>(() => working.ResolveAt(DateTimeOffset.UnixEpoch));

        Assert.True(UserTaskDueRules.TryParseFixedValue("2030-01-31T17:00:00Z", out var instant));
        Assert.True(UserTaskDueRules.TryApplyCalendar(instant, "2030-01-31T17:00:00Z", "uk-office", out var unchanged, out _));
        Assert.Same(instant, unchanged);

        Assert.True(UserTaskDueRules.TryParseFixedValue("P1W", out var weeks));
        Assert.False(UserTaskDueRules.TryApplyCalendar(weeks, "P1W", "uk-office", out _, out var problem));
        Assert.Equal(
            "the duration 'P1W' cannot be counted in a business calendar; use whole working days and working time, such as P5D or P1DT4H",
            problem);

        // An expression can give a duration longer than any calendar can count; it leaves the due date empty.
        Assert.True(UserTaskDueRules.TryParseFixedValue("P36526D", out var tooLong));
        Assert.False(UserTaskDueRules.TryApplyCalendar(tooLong, "P36526D", "uk-office", out _, out problem));
        Assert.Equal(
            "the duration 'P36526D' is longer than a business calendar can count; use at most 36,525 days, about 100 years",
            problem);
        Assert.Contains(
            BusinessCalendarRules.MaxSearchDays.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
            BusinessCalendarRules.WorkingDurationLimitDescription);
    }

    [Fact]
    public void ReferencesListTimerAndDueDateCalendarsInNodeOrder()
    {
        var model = new WorkflowModel
        {
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Type = BpmnFlowNodeTypes.StartEvent, DueDateCalendar = "ignored" },
                new FlowNodeModel { Id = 2, Type = BpmnFlowNodeTypes.UserTask, DueDateCalendar = " uk-office " },
                new FlowNodeModel
                {
                    Id = 3,
                    Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    Timer = new TimerDefinitionModel { TimeDuration = "P1D", Calendar = "us-office" }
                },
                new FlowNodeModel { Id = 4, Type = BpmnFlowNodeTypes.UserTask }
            ]
        };

        Assert.Equal(
            [new BusinessCalendarReference(2, "uk-office"), new BusinessCalendarReference(3, "us-office")],
            BusinessCalendarRules.References(model));
    }

    [Fact]
    public void CalendarFieldsRoundTripAndOlderNodesGainNoKeys()
    {
        var timer = new TimerDefinitionModel { TimeDuration = "P1D" };
        Assert.False(JsonSerializer.SerializeToNode(timer)!.AsObject().ContainsKey("calendar"));
        var task = new FlowNodeModel { Id = 2, Type = BpmnFlowNodeTypes.UserTask, DueDateExpression = "P2D" };
        Assert.False(JsonSerializer.SerializeToNode(task)!.AsObject().ContainsKey("dueDateCalendar"));

        timer.Calendar = "uk-office";
        task.DueDateCalendar = "uk-office";

        Assert.Equal("uk-office", JsonSerializer.Deserialize<TimerDefinitionModel>(JsonSerializer.Serialize(timer))!.Calendar);
        Assert.Equal("uk-office", JsonSerializer.Deserialize<FlowNodeModel>(JsonSerializer.Serialize(task))!.DueDateCalendar);
    }

    [Fact]
    public void MigratorTrimsCalendarNamesAndClearsADueDateCalendarElsewhere()
    {
        var model = new WorkflowModel
        {
            Id = "calendar-migrator",
            Name = "Calendar migrator",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent, DueDateCalendar = "uk-office" },
                new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask, DueDateExpression = "P2D", DueDateCalendar = "  uk-office  " },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Deadline",
                    Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    AttachedToRef = 2,
                    Timer = new TimerDefinitionModel { TimeDuration = "P1D", Calendar = "   " }
                },
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };

        WorkflowModelMigrator.Normalize(model);
        var once = JsonSerializer.Serialize(model);
        WorkflowModelMigrator.Normalize(model);

        Assert.Null(model.FlowNodes[0].DueDateCalendar);
        Assert.Equal("uk-office", model.FlowNodes[1].DueDateCalendar);
        Assert.Null(model.FlowNodes[2].Timer!.Calendar);
        Assert.Equal(once, JsonSerializer.Serialize(model));
    }

    [Fact]
    public void CalendarContentIsLimited()
    {
        static BusinessCalendarWeekDto Monday(params WorkingPeriodDto[] periods) => new(Monday: periods);

        AssertProblem("timeZone is required.", () => BusinessCalendarContentRules.Parse(" ", Monday(new WorkingPeriodDto("09:00", "17:00")), null, false));
        AssertProblem("weeklyHours is required.", () => BusinessCalendarContentRules.Parse("Etc/UTC", null, null, false));
        AssertProblem(
            "weeklyHours.monday[0].start '24:00' must be a time in HH:mm form.",
            () => BusinessCalendarContentRules.Parse("Etc/UTC", Monday(new WorkingPeriodDto("24:00", "24:00")), null, false));
        AssertProblem(
            "weeklyHours.monday[0].end '17:0١' must be a time in HH:mm form, or 24:00.",
            () => BusinessCalendarContentRules.Parse("Etc/UTC", Monday(new WorkingPeriodDto("09:00", "17:0١")), null, true));
        AssertProblem(
            "weeklyHours.monday must have at most 10 working periods.",
            () => BusinessCalendarContentRules.Parse(
                "Etc/UTC",
                Monday(Enumerable.Range(0, 11).Select(hour => new WorkingPeriodDto($"{hour:00}:00", $"{hour:00}:30")).ToArray()),
                null,
                false));
        AssertProblem(
            "holidays must have at most 1000 entries.",
            () => BusinessCalendarContentRules.Parse(
                "Etc/UTC",
                Monday(new WorkingPeriodDto("09:00", "17:00")),
                Enumerable.Range(0, 1001).Select(day => new BusinessCalendarHolidayDto(new DateOnly(2030, 1, 1).AddDays(day))).ToList(),
                false));
        AssertProblem(
            "holidays[0].name must be at most 200 characters.",
            () => BusinessCalendarContentRules.Parse(
                "Etc/UTC",
                Monday(new WorkingPeriodDto("09:00", "17:00")),
                [new BusinessCalendarHolidayDto(new DateOnly(2030, 1, 1), new string('x', 201))],
                false));
        AssertProblem(
            "holidays[0].date must be a date from 1900-01-01 to 9998-12-31.",
            () => BusinessCalendarContentRules.Parse(
                "Etc/UTC",
                Monday(new WorkingPeriodDto("09:00", "17:00")),
                [new BusinessCalendarHolidayDto(default)],
                false));
        AssertProblem("name is required.", () => BusinessCalendarContentRules.NormalizeName(" "));
        AssertProblem(
            "description must be at most 1000 characters.",
            () => BusinessCalendarContentRules.NormalizeDescription(new string('x', 1001)));

        var content = BusinessCalendarContentRules.Parse("Etc/UTC", Monday(new WorkingPeriodDto("09:00", "24:00")), null, false);
        var week = BusinessCalendarContentRules.ToWeekDto(content);
        Assert.Equal("24:00", Assert.Single(week.Monday!).End);
        Assert.All(
            new[] { week.Tuesday, week.Wednesday, week.Thursday, week.Friday, week.Saturday, week.Sunday },
            periods => Assert.Empty(periods!));
    }

    private static void AssertProblem(string expected, Action action) =>
        Assert.Equal(expected, Assert.Throws<WorkflowDomainException>(action).Message);
}
