using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Characterizes the pinned cron calculator through Flowbit's adapter. A calculator upgrade must keep every case here.
/// </summary>
public sealed class CronTimerScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.FindSystemTimeZoneById("Etc/UTC");

    public static TheoryData<string> AcceptedExpressions() => new(CronScheduleFixtures.Accepted);

    [Theory]
    [MemberData(nameof(AcceptedExpressions))]
    public void Calculator_SelectsExactlyTheDaysAndTimesTheRulesParse(string cron)
    {
        Assert.True(CronScheduleRules.TryParse(cron, out var fields, out var problem), problem);
        var schedule = CronTimerSchedule.Create(cron, Utc);

        // Eight years cover leap years, every weekday of each date, and every month length.
        DateOnly? firstMatch = null;
        for (var day = new DateOnly(2024, 1, 1); day < new DateOnly(2032, 1, 1); day = day.AddDays(1))
        {
            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var next = schedule.Next(start, inclusive: true);
            var calculatorMatches = next is { } found && found < start.AddDays(1);
            Assert.True(
                Matches(fields, day) == calculatorMatches,
                $"'{cron}' disagrees with the calculator on {day:yyyy-MM-dd}.");
            if (calculatorMatches)
            {
                firstMatch ??= day;
            }
        }

        // A sparse schedule, such as 1 January on a Sunday, may skip the window; its first later date must still match.
        if (firstMatch is null)
        {
            var later = schedule.Next(new DateTimeOffset(2032, 1, 1, 0, 0, 0, TimeSpan.Zero), inclusive: true)!.Value;
            firstMatch = DateOnly.FromDateTime(later.UtcDateTime);
            Assert.True(Matches(fields, firstMatch.Value), $"'{cron}' first runs on {firstMatch:yyyy-MM-dd}, which its fields reject.");
            var dayBefore = new DateTimeOffset(firstMatch.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            for (var day = new DateOnly(2032, 1, 1); day < firstMatch.Value; day = day.AddDays(1))
            {
                Assert.False(Matches(fields, day), $"'{cron}' skips {day:yyyy-MM-dd} before {dayBefore:yyyy-MM-dd}.");
            }
        }

        var midnight = new DateTimeOffset(firstMatch.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var times = new HashSet<(int Hour, int Minute)>();
        for (var occurrence = schedule.Next(midnight, inclusive: true);
             occurrence is { } at && at < midnight.AddDays(1);
             occurrence = schedule.Next(at, inclusive: false))
        {
            times.Add((at.Hour, at.Minute));
        }

        var expected = new HashSet<(int Hour, int Minute)>();
        for (var hour = 0; hour < 24; hour++)
        {
            for (var minute = 0; minute < 60; minute++)
            {
                if ((fields.Hours & (1u << hour)) != 0 && (fields.Minutes & (1UL << minute)) != 0)
                {
                    expected.Add((hour, minute));
                }
            }
        }

        Assert.True(expected.SetEquals(times), $"'{cron}' selects other times of day.");
    }

    [Fact]
    public void Next_HonorsInclusiveAndExclusiveBounds()
    {
        var schedule = CronTimerSchedule.Create("0 8 * * *", Utc);
        var eight = Instant("2026-11-09T08:00:00Z");

        Assert.Equal(eight, schedule.Next(eight, inclusive: true));
        Assert.Equal(Instant("2026-11-10T08:00:00Z"), schedule.Next(eight, inclusive: false));
        // Just after an occurrence, inclusive or not, the next day is next.
        Assert.Equal(Instant("2026-11-10T08:00:00Z"), schedule.Next(eight.AddTicks(1), inclusive: true));
        Assert.Equal(eight, schedule.Next(eight.AddTicks(-1), inclusive: false));
        // A reference with another offset is the same instant.
        Assert.Equal(eight, schedule.Next(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.FromHours(2)), inclusive: false));
    }

    [Theory]
    [InlineData("Asia/Kolkata", "2026-11-08T03:30:00Z", "2026-11-08T09:00:00+05:30")]
    [InlineData("Asia/Kathmandu", "2026-11-08T03:15:00Z", "2026-11-08T09:00:00+05:45")]
    [InlineData("America/St_Johns", "2026-11-08T12:30:00Z", "2026-11-08T09:00:00-03:30")]
    [InlineData("Etc/UTC", "2026-11-08T09:00:00Z", "2026-11-08T09:00:00+00:00")]
    public void Next_FollowsNonWholeHourZones(string zone, string expectedUtc, string expectedLocal)
    {
        Assert.True(CronTimerSchedule.TryCreate("0 9 * * *", zone, "cron", "timeZone", out var schedule, out var problem), problem);

        var next = schedule.Next(Instant("2026-11-08T00:00:00Z"), inclusive: false)!.Value;

        Assert.Equal(Instant(expectedUtc), next);
        Assert.Equal(TimeSpan.Zero, next.Offset);
        Assert.Equal(DateTimeOffset.Parse(expectedLocal), schedule.ToLocal(next));
        Assert.Equal(DateTimeOffset.Parse(expectedLocal).Offset, schedule.ToLocal(next).Offset);
    }

    [Fact]
    public void Next_RollsOverMonthsAndYears()
    {
        Assert.Equal(
            Instant("2027-01-01T00:00:00Z"),
            CronTimerSchedule.Create("0 0 1 1 *", Utc).Next(Instant("2026-12-31T23:59:00Z"), inclusive: false));
        Assert.Equal(
            Instant("2026-05-31T00:00:00Z"),
            CronTimerSchedule.Create("0 0 31 * *", Utc).Next(Instant("2026-04-01T00:00:00Z"), inclusive: false));
    }

    [Fact]
    public void Next_FindsLeapDaysAndSkipsTheNonLeapCentury()
    {
        var leapDay = CronTimerSchedule.Create("0 8 29 2 *", Utc);

        Assert.Equal(Instant("2028-02-29T08:00:00Z"), leapDay.Next(Instant("2026-03-01T00:00:00Z"), inclusive: false));
        Assert.Equal(Instant("2104-02-29T08:00:00Z"), leapDay.Next(Instant("2096-03-01T00:00:00Z"), inclusive: false));
    }

    [Fact]
    public void Next_RunsOnEachMonthsLastDayAndOnlyOnAFridayWhenBothDaysAreRestricted()
    {
        var monthEnd = CronTimerSchedule.Create("0 17 L * *", Utc);
        Assert.Equal(
            [Instant("2026-02-28T17:00:00Z"), Instant("2026-03-31T17:00:00Z"), Instant("2026-04-30T17:00:00Z")],
            monthEnd.Upcoming(Instant("2026-02-01T00:00:00Z"), 3, CancellationToken.None));
        Assert.Equal(Instant("2028-02-29T17:00:00Z"), monthEnd.Next(Instant("2028-02-28T18:00:00Z"), inclusive: false));

        // Both day fields must match: the last day of a month that ends on a Friday, not each month's last Friday.
        var fridayMonthEnds = CronTimerSchedule.Create("0 17 L * 5", Utc)
            .Upcoming(Instant("2026-01-01T00:00:00Z"), 3, CancellationToken.None);
        Assert.Equal(
            [Instant("2026-07-31T17:00:00Z"), Instant("2027-04-30T17:00:00Z"), Instant("2027-12-31T17:00:00Z")],
            fridayMonthEnds);
        Assert.Equal(
            Instant("2026-11-13T08:00:00Z"),
            CronTimerSchedule.Create("0 8 13 * 5", Utc).Next(Instant("2026-11-01T00:00:00Z"), inclusive: false));
    }

    [Fact]
    public void Next_MovesASkippedLocalTimeToTheMomentTheClockJumps()
    {
        var schedule = Schedule("30 1 * * *", "Europe/London");

        var next = schedule.Next(Instant("2026-03-28T12:00:00Z"), inclusive: false)!.Value;

        Assert.Equal(Instant("2026-03-29T01:00:00Z"), next);
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T02:00:00+01:00"), schedule.ToLocal(next));
        Assert.Equal(TimeSpan.FromHours(1), schedule.ToLocal(next).Offset);
    }

    [Fact]
    public void Next_RunsAFixedTimeOnceInARepeatedHour()
    {
        var schedule = Schedule("30 1 * * *", "Europe/London");

        var occurrences = schedule.Upcoming(Instant("2026-10-24T12:00:00Z"), 2, CancellationToken.None);

        Assert.Equal([Instant("2026-10-25T00:30:00Z"), Instant("2026-10-26T01:30:00Z")], occurrences);
        Assert.Equal(TimeSpan.FromHours(1), schedule.ToLocal(occurrences[0]).Offset);
        Assert.Equal(TimeSpan.Zero, schedule.ToLocal(occurrences[1]).Offset);
    }

    [Fact]
    public void Next_RunsAStepInBothOffsetsOfARepeatedHour()
    {
        var schedule = Schedule("*/30 * * * *", "Europe/London");

        var occurrences = schedule.Upcoming(Instant("2026-10-24T23:45:00Z"), 5, CancellationToken.None);

        Assert.Equal(
            [
                Instant("2026-10-25T00:00:00Z"),
                Instant("2026-10-25T00:30:00Z"),
                Instant("2026-10-25T01:00:00Z"),
                Instant("2026-10-25T01:30:00Z"),
                Instant("2026-10-25T02:00:00Z")
            ],
            occurrences);
        Assert.Equal(
            ["01:00+01:00", "01:30+01:00", "01:00+00:00", "01:30+00:00", "02:00+00:00"],
            occurrences.Select(occurrence => schedule.ToLocal(occurrence).ToString("HH:mmzzz")));
    }

    [Fact]
    public void Next_DistinguishesARangeFromAnEquivalentListInARepeatedHour()
    {
        var from = Instant("2026-10-24T12:00:00Z");

        Assert.Equal(
            [Instant("2026-10-25T00:00:00Z"), Instant("2026-10-25T01:00:00Z"), Instant("2026-10-25T02:00:00Z")],
            Schedule("0 1-2 * * *", "Europe/London").Upcoming(from, 3, CancellationToken.None));
        Assert.Equal(
            [Instant("2026-10-25T00:00:00Z"), Instant("2026-10-25T02:00:00Z"), Instant("2026-10-26T01:00:00Z")],
            Schedule("0 1,2 * * *", "Europe/London").Upcoming(from, 3, CancellationToken.None));
    }

    [Theory]
    [InlineData("*/30 * * * *", "2026-03-29T00:45:00Z", new[] { "2026-03-29T01:00:00Z", "2026-03-29T01:30:00Z", "2026-03-29T02:00:00Z" })]
    [InlineData("0,30 1 * * *", "2026-03-28T12:00:00Z", new[] { "2026-03-29T01:00:00Z", "2026-03-30T00:00:00Z", "2026-03-30T00:30:00Z" })]
    [InlineData("0 1,2 * * *", "2026-03-28T12:00:00Z", new[] { "2026-03-29T01:00:00Z", "2026-03-30T00:00:00Z", "2026-03-30T01:00:00Z" })]
    public void Next_CollapsesOccurrencesThatFallInTheSameGap(string cron, string from, string[] expected)
    {
        var occurrences = Schedule(cron, "Europe/London").Upcoming(Instant(from), expected.Length, CancellationToken.None);

        Assert.Equal(expected.Select(Instant), occurrences);
        Assert.Equal(occurrences.Count, occurrences.Distinct().Count());
        Assert.True(occurrences.Zip(occurrences.Skip(1)).All(pair => pair.First < pair.Second));
    }

    [Fact]
    public void Next_HandlesHalfHourTransitionsInASyntheticZone()
    {
        var zone = HalfHourShiftZone();

        var skipped = CronTimerSchedule.Create("15 2 * * *", zone).Next(Instant("2026-02-28T12:00:00Z"), inclusive: false)!.Value;
        Assert.Equal(Instant("2026-02-28T15:30:00Z"), skipped);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 2, 30, 0, TimeSpan.FromMinutes(660)), TimeZoneInfo.ConvertTime(skipped, zone));

        var step = CronTimerSchedule.Create("*/15 * * * *", zone);
        Assert.Equal(
            [
                Instant("2026-10-31T14:30:00Z"),
                Instant("2026-10-31T14:45:00Z"),
                Instant("2026-10-31T15:00:00Z"),
                Instant("2026-10-31T15:15:00Z"),
                Instant("2026-10-31T15:30:00Z")
            ],
            step.Upcoming(Instant("2026-10-31T14:20:00Z"), 5, CancellationToken.None));

        var fixedTime = CronTimerSchedule.Create("40 1 * * *", zone);
        Assert.Equal(
            [Instant("2026-10-31T14:40:00Z"), Instant("2026-11-01T15:10:00Z")],
            fixedTime.Upcoming(Instant("2026-10-31T12:00:00Z"), 2, CancellationToken.None));
    }

    [Fact]
    public void Next_FindsSparseSchedulesAndStopsAtTheLargestSupportedTime()
    {
        var mondayLeapDay = CronTimerSchedule.Create("0 0 29 2 1", Utc).Next(Instant("2026-01-01T00:00:00Z"), inclusive: false)!.Value;
        Assert.Equal(DayOfWeek.Monday, mondayLeapDay.DayOfWeek);
        Assert.Equal((2, 29), (mondayLeapDay.Month, mondayLeapDay.Day));
        Assert.True(mondayLeapDay - Instant("2026-01-01T00:00:00Z") < TimeSpan.FromDays(CronTimerSchedule.SearchHorizonDays));

        var daily = CronTimerSchedule.Create("0 8 * * *", Utc);
        Assert.Equal(Instant("9999-12-31T08:00:00Z"), daily.Next(Instant("9999-12-31T00:00:00Z"), inclusive: false));
        Assert.Null(daily.Next(Instant("9999-12-31T08:00:00Z"), inclusive: false));

        // Local 22:00 on the last day in UTC-8 is past the largest representable instant.
        Assert.Null(Schedule("0 22 31 12 *", "America/Los_Angeles").Next(Instant("9999-12-30T00:00:00Z"), inclusive: false));
    }

    [Fact]
    public void Upcoming_ChecksCancellationBetweenLookups()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            CronTimerSchedule.Create("* * * * *", Utc).Upcoming(Instant("2026-01-01T00:00:00Z"), 5, cancelled.Token));
    }

    [Theory]
    [InlineData("0 8 * * *", "Mars/Olympus", "timer.timeZone 'Mars/Olympus' is not an IANA time zone that this server knows, such as Europe/London.")]
    [InlineData("0 8 * * *", " ", "timer.timeZone is required with cron; name an IANA time zone such as Europe/London, or Etc/UTC for UTC.")]
    [InlineData("0 8 * * *", null, "timer.timeZone is required with cron; name an IANA time zone such as Europe/London, or Etc/UTC for UTC.")]
    [InlineData("0 8 * * 7", "Europe/London", "timer.cron day-of-week value 7 is not allowed; write Sunday as 0.")]
    public void TryCreate_ReportsTheExpressionBeforeTheZone(string cron, string? zone, string expected)
    {
        Assert.False(CronTimerSchedule.TryCreate(cron, zone, "timer.cron", "timer.timeZone", out _, out var problem));
        Assert.Equal(expected, problem);
    }

    [Fact]
    public void TryCreate_RejectsAZoneWithOtherCasing()
    {
        Assert.False(CronTimerSchedule.TryCreate("0 8 * * *", "europe/london", "timer.cron", "timer.timeZone", out _, out var problem));
        Assert.Contains(problem, CronTimerDefinitionTests.MiscasedZoneMessages("timer.timeZone"));
    }

    [Fact]
    public void TryCreate_RejectsAnOverlongZone()
    {
        var zone = new string('A', 101);

        Assert.False(CronTimerSchedule.TryCreate("0 8 * * *", zone, "timer.cron", "timer.timeZone", out _, out var problem));
        Assert.Equal("timer.timeZone must contain at most 100 characters.", problem);
    }

    [Fact]
    public void ResolveFirstCronOccurrence_StartsStrictlyAfterActivationAndSkipsALateBacklog()
    {
        var schedule = CronTimerSchedule.Create("0 8 * * *", Utc);
        var grace = TimeSpan.FromMinutes(1);

        // An occurrence at the activation instant itself is excluded.
        Assert.Equal(
            Instant("2026-11-10T08:00:00Z"),
            WorkflowTimerSchedule.ResolveFirstCronOccurrence(schedule, Instant("2026-11-09T08:00:00Z"), Instant("2026-11-09T08:00:00Z"), grace));
        // At most one minute late, the first occurrence is kept.
        Assert.Equal(
            Instant("2026-11-09T08:00:00Z"),
            WorkflowTimerSchedule.ResolveFirstCronOccurrence(schedule, Instant("2026-11-09T07:00:00Z"), Instant("2026-11-09T08:01:00Z"), grace));
        // Later than that, the schedule skips to the first occurrence after now.
        Assert.Equal(
            Instant("2026-11-10T08:00:00Z"),
            WorkflowTimerSchedule.ResolveFirstCronOccurrence(schedule, Instant("2026-11-09T07:00:00Z"), Instant("2026-11-09T08:01:00.001Z"), grace));
        Assert.Equal(
            Instant("2026-11-10T08:00:00Z"),
            WorkflowTimerSchedule.ResolveFirstCronOccurrence(schedule, Instant("2020-01-01T00:00:00Z"), Instant("2026-11-09T09:00:00Z"), grace));
    }

    [Fact]
    public void ResolveNextCronOccurrence_AppliesTheMinuteGraceWithoutReplayingMatches()
    {
        var everyMinute = CronTimerSchedule.Create("* * * * *", Utc);
        var grace = TimeSpan.FromMinutes(1);
        var stored = Instant("2026-11-09T08:00:00Z");

        // Exactly one minute late is still eligible; a millisecond more is a misfire.
        Assert.False(WorkflowTimerSchedule.IsRecurringMisfire(TimerScheduleKinds.Cron, stored, Instant("2026-11-09T08:01:00Z"), grace));
        Assert.True(WorkflowTimerSchedule.IsRecurringMisfire(TimerScheduleKinds.Cron, stored, Instant("2026-11-09T08:01:00.001Z"), grace));

        // A misfire moves strictly after now, skipping the 08:01 occurrence inside the grace window.
        var afterMisfire = WorkflowTimerSchedule.ResolveNextCronOccurrence(
            everyMinute, 3, stored, Instant("2026-11-09T08:01:00.001Z").AddTicks(1));
        Assert.Equal(new WorkflowTimerOccurrence(4, Instant("2026-11-09T08:02:00Z")), afterMisfire);
        var atNextMinute = WorkflowTimerSchedule.ResolveNextCronOccurrence(
            everyMinute, 3, stored, Instant("2026-11-09T08:02:00Z").AddTicks(1));
        Assert.Equal(Instant("2026-11-09T08:03:00Z"), atNextMinute.DueAt);

        // A successful firing schedules after its nominal time, not after the moment it ran.
        var afterFiring = WorkflowTimerSchedule.ResolveNextCronOccurrence(
            everyMinute, 3, stored, Instant("2026-11-09T08:00:40Z") - grace);
        Assert.Equal(new WorkflowTimerOccurrence(4, Instant("2026-11-09T08:01:00Z")), afterFiring);

        // Years of downtime cost one lookup, and the generation counts persisted occurrences only.
        var afterOutage = WorkflowTimerSchedule.ResolveNextCronOccurrence(
            everyMinute, 3, Instant("2020-01-01T00:00:00Z"), Instant("2026-11-09T12:00:30Z"));
        Assert.Equal(new WorkflowTimerOccurrence(4, Instant("2026-11-09T12:01:00Z")), afterOutage);
    }

    [Fact]
    public void ResolveNextCronOccurrence_TreatsNoFurtherOccurrenceAndOverflowAsErrors()
    {
        var daily = CronTimerSchedule.Create("0 8 * * *", Utc);

        var noOccurrence = Assert.Throws<WorkflowDomainException>(() =>
            WorkflowTimerSchedule.ResolveNextCronOccurrence(daily, 0, Instant("9999-12-31T08:00:00Z"), Instant("9999-12-31T07:59:00Z")));
        Assert.Equal(
            "Cron schedule '0 8 * * *' in time zone 'Etc/UTC' has no occurrence within 36,525 days after 9999-12-31T08:00:00Z.",
            noOccurrence.Message);

        var overflow = Assert.Throws<WorkflowDomainException>(() =>
            WorkflowTimerSchedule.ResolveNextCronOccurrence(daily, long.MaxValue, Instant("2026-11-09T08:00:00Z"), Instant("2026-11-09T07:59:00Z")));
        Assert.Equal("Timer occurrence exceeds the supported numeric range.", overflow.Message);
    }

    [Theory]
    [InlineData(TimerScheduleKinds.Cron, true)]
    [InlineData(TimerScheduleKinds.Cycle, true)]
    [InlineData(TimerScheduleKinds.Date, false)]
    [InlineData(TimerScheduleKinds.Duration, false)]
    public void IsRecurring_ClassifiesEachKind(string kind, bool recurring)
    {
        Assert.Equal(recurring, TimerScheduleKinds.IsRecurring(kind));
        Assert.Equal(
            recurring,
            WorkflowTimerSchedule.IsRecurringMisfire(kind, Instant("2026-11-09T08:00:00Z"), Instant("2026-11-09T09:00:00Z"), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Resolve_NeverReadsCronAsAFixedInterval()
    {
        var timer = new TimerDefinitionModel { Cron = "0 8 * * *", TimeZone = "Etc/UTC" };

        var error = Assert.Throws<WorkflowDomainException>(() => WorkflowTimerSchedule.Resolve(timer, Instant("2026-11-09T00:00:00Z")));
        Assert.Contains("cron schedule has no fixed interval", error.Message);
    }

    private static bool Matches(CronScheduleFields fields, DateOnly day)
    {
        var lastDay = DateTime.DaysInMonth(day.Year, day.Month);
        var dayMatches = fields.LastDayOfMonth ? day.Day == lastDay : (fields.DaysOfMonth & (1u << day.Day)) != 0;
        return (fields.Months & (1 << day.Month)) != 0
            && dayMatches
            && (fields.DaysOfWeek & (1 << (int)day.DayOfWeek)) != 0;
    }

    private static CronTimerSchedule Schedule(string cron, string zone)
    {
        Assert.True(CronTimerSchedule.TryCreate(cron, zone, "cron", "timeZone", out var schedule, out var problem), problem);
        return schedule;
    }

    // Standard time is UTC+10:30; daylight saving adds 30 minutes from 1 March at 02:00 to 1 November at 02:00.
    private static TimeZoneInfo HalfHourShiftZone() =>
        TimeZoneInfo.CreateCustomTimeZone(
            "Test/HalfHourShift",
            TimeSpan.FromMinutes(630),
            "Half-hour shift",
            "Half-hour standard",
            "Half-hour daylight",
            [
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    new DateTime(2000, 1, 1),
                    new DateTime(2099, 12, 31),
                    TimeSpan.FromMinutes(30),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 1),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1))
            ]);

    private static DateTimeOffset Instant(string text) => DateTimeOffset.Parse(text).ToUniversalTime();
}
