using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class BusinessCalendarTests
{
    private const string Office = "09:00-17:00";
    private const string Lunch = "09:00-12:00, 13:00-17:00";
    private const string AllDay = "00:00-24:00";

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.CreateCustomTimeZone("Test/UTC", TimeSpan.Zero, "UTC", "UTC");

    // 2026-11-02 is a Monday; no daylight-saving change happens in UTC.
    [Theory]
    [InlineData("2026-11-02T14:00", "P3D", "2026-11-05T14:00")]
    [InlineData("2026-11-06T14:00", "P3D", "2026-11-11T14:00")]
    [InlineData("2026-11-07T10:00", "P1D", "2026-11-09T17:00")]
    [InlineData("2026-11-02T08:00", "P3D", "2026-11-04T17:00")]
    [InlineData("2026-11-02T16:00", "PT2H", "2026-11-03T10:00")]
    [InlineData("2026-11-02T09:00", "P1D", "2026-11-02T17:00")]
    [InlineData("2026-11-02T17:00", "P1D", "2026-11-03T17:00")]
    [InlineData("2026-11-02T16:59", "P1D", "2026-11-03T16:59")]
    [InlineData("2026-11-02T09:00", "PT8H", "2026-11-02T17:00")]
    [InlineData("2026-11-02T14:00", "P1DT4H", "2026-11-04T10:00")]
    [InlineData("2026-11-02T10:00", "P5D", "2026-11-09T10:00")]
    [InlineData("2026-11-07T10:00", "P1DT1H", "2026-11-10T10:00")]
    [InlineData("2026-11-02T10:00", "PT0.5S", "2026-11-02T10:00:00.5")]
    public void OfficeHoursCountWorkingDaysAndWorkingTime(string start, string duration, string expected)
    {
        Assert.Equal(At(expected), Add(Calendar(Office), At(start), duration));
    }

    [Fact]
    public void AHolidayWithHoursEndsTheDayEarly()
    {
        var calendar = Calendar(Office, holidays: [Holiday("2026-11-06", "09:00-13:00")]);

        Assert.Equal(At("2026-11-06T13:00"), Add(calendar, At("2026-11-05T15:00"), "P1D"));
        Assert.Equal(At("2026-11-09T12:00"), Add(calendar, At("2026-11-06T12:00"), "P1D"));
        Assert.Equal(At("2026-11-09T10:00"), Add(calendar, At("2026-11-06T12:00"), "PT2H"));
    }

    [Fact]
    public void AClosedHolidayIsNotAWorkingDay()
    {
        var calendar = Calendar(Office, holidays: [Holiday("2026-11-04")]);

        Assert.Equal(At("2026-11-06T14:00"), Add(calendar, At("2026-11-02T14:00"), "P3D"));
        Assert.Equal(At("2026-11-05T10:00"), Add(calendar, At("2026-11-03T16:00"), "PT2H"));
    }

    [Fact]
    public void AHolidayWithHoursOnAWeekendMakesItAWorkingDay()
    {
        var calendar = Calendar(Office, holidays: [Holiday("2026-11-07", "10:00-14:00")]);

        Assert.Equal(At("2026-11-07T11:00"), Add(calendar, At("2026-11-06T16:00"), "PT2H"));
        Assert.Equal(At("2026-11-07T14:00"), Add(calendar, At("2026-11-06T15:00"), "P1D"));
    }

    [Theory]
    [InlineData("2026-11-02T12:30", "P1D", "2026-11-03T12:00")]
    [InlineData("2026-11-02T14:00", "P1D", "2026-11-03T14:00")]
    [InlineData("2026-11-02T13:00", "P1D", "2026-11-03T12:00")]
    [InlineData("2026-11-02T11:00", "PT2H", "2026-11-02T14:00")]
    [InlineData("2026-11-02T12:30", "PT1H", "2026-11-02T14:00")]
    [InlineData("2026-11-02T09:00", "PT3H", "2026-11-02T12:00")]
    public void ABreakIsNotWorkingTime(string start, string duration, string expected)
    {
        Assert.Equal(At(expected), Add(Calendar(Lunch), At(start), duration));
    }

    [Fact]
    public void AnEndOfDayPeriodContinuesIntoTheNextDay()
    {
        var calendar = Calendar(AllDay, AllDay, AllDay);

        Assert.Equal(At("2026-11-03T02:00"), Add(calendar, At("2026-11-02T22:00"), "PT4H"));
        Assert.Equal(At("2026-11-03T12:00"), Add(calendar, At("2026-11-02T12:00"), "P1D"));
    }

    [Fact]
    public void ALargeDurationIsCountedExactly()
    {
        // 2,500 working days are 500 five-day weeks.
        Assert.Equal(
            At("2026-11-02T10:00").AddDays(3500),
            Add(Calendar(Office), At("2026-11-02T10:00"), "P2500D"));
    }

    [Fact]
    public void ADueTimeBeyondTheSearchLimitIsRefused()
    {
        var mondaysOnly = new BusinessCalendar(
            "mondays",
            BusinessCalendarContentRules.Parse(
                "Test/UTC",
                new BusinessCalendarWeekDto(Monday: Periods("09:00-10:00")),
                null,
                checkTimeZone: false),
            Utc);

        Assert.False(mondaysOnly.TryAdd(At("2026-11-02T09:00"), Duration("P30000D"), out _, out var problem));
        Assert.Contains("36,525 days", problem);
        Assert.False(mondaysOnly.TryAdd(At("2026-11-02T09:00"), Duration("PT6000H"), out _, out _));
    }

    [Fact]
    public void ANonPositiveDurationIsRefused()
    {
        Assert.False(Calendar(Office).TryAdd(At("2026-11-02T09:00"), new WorkingDuration(0, TimeSpan.Zero), out _, out var problem));
        Assert.Equal("the duration must be positive", problem);
    }

    // A zone like Central European Time: clocks go from 02:00 to 03:00 on 29 March and from 03:00 back to 02:00 on
    // 25 October.
    private static readonly TimeZoneInfo Central = Zone(
        "Test/Central", TimeSpan.FromHours(1), TimeSpan.FromHours(1), (3, 29, 2, 0), (10, 25, 3, 0));

    [Fact]
    public void DaysKeepTheWallClockTimeAcrossASkippedHour()
    {
        var calendar = Calendar(AllDay, AllDay, AllDay, zone: Central);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.FromHours(2)),
            Add(calendar, new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.FromHours(1)), "P1D"));
        Assert.Equal(
            new DateTimeOffset(2026, 3, 29, 13, 0, 0, TimeSpan.FromHours(2)),
            Add(calendar, new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.FromHours(1)), "PT24H"));
    }

    [Fact]
    public void APeriodContainingASkippedHourIsAnHourShorter()
    {
        var calendar = Calendar("00:00-06:00", "00:00-06:00", "00:00-06:00", zone: Central);

        // Sunday's period lasts five hours, so the sixth hour falls on Monday.
        Assert.Equal(
            new DateTimeOffset(2026, 3, 30, 1, 0, 0, TimeSpan.FromHours(2)),
            Add(calendar, new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.FromHours(1)), "PT6H"));
    }

    [Fact]
    public void ABoundaryAtASkippedTimeFallsWhenTheClockJumps()
    {
        var calendar = new BusinessCalendar(
            "night",
            BusinessCalendarContentRules.Parse(
                "Test/Central",
                new BusinessCalendarWeekDto(Sunday: Periods("02:30-04:00")),
                null,
                checkTimeZone: false),
            Central);

        // 02:30 does not exist on 29 March, so the period runs from 03:00 to 04:00.
        Assert.Equal(
            new DateTimeOffset(2026, 3, 29, 3, 30, 0, TimeSpan.FromHours(2)),
            Add(calendar, new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.FromHours(1)), "PT30M"));
        Assert.Equal(
            new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero),
            BusinessCalendarTimeZones.ToInstant(Central, new DateTime(2026, 3, 29, 2, 30, 0)));
    }

    [Fact]
    public void APeriodContainingARepeatedHourIsAnHourLonger()
    {
        var calendar = Calendar("00:00-06:00", "00:00-06:00", "00:00-06:00", zone: Central);

        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 5, 30, 0, TimeSpan.FromHours(1)),
            Add(calendar, new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(2)), "PT6H30M"));
    }

    [Fact]
    public void ABoundaryAtARepeatedTimeUsesItsFirstOccurrence()
    {
        var calendar = new BusinessCalendar(
            "night",
            BusinessCalendarContentRules.Parse(
                "Test/Central",
                new BusinessCalendarWeekDto(Sunday: Periods("02:30-05:00")),
                null,
                checkTimeZone: false),
            Central);

        // The period starts at the first 02:30, so one working hour ends at the second 02:30.
        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(1)),
            Add(calendar, new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(2)), "PT1H"));
        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)),
            BusinessCalendarTimeZones.ToInstant(Central, new DateTime(2026, 10, 25, 2, 30, 0)));
    }

    [Fact]
    public void DaysKeepTheWallClockTimeAcrossARepeatedHour()
    {
        var calendar = Calendar(AllDay, AllDay, AllDay, zone: Central);

        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(1)),
            Add(calendar, new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(2)), "P1D"));
    }

    // Clocks skip from midnight to 01:00 on 29 March and repeat 00:00 to 01:00 on 25 October.
    private static readonly TimeZoneInfo MidnightChange = Zone(
        "Test/Midnight", TimeSpan.Zero, TimeSpan.FromHours(1), (3, 29, 0, 0), (10, 25, 1, 0));

    [Fact]
    public void ASkippedMidnightStartsTheDayWhenTheClockJumps()
    {
        var calendar = Calendar(AllDay, AllDay, AllDay, zone: MidnightChange);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 29, 2, 0, 0, TimeSpan.Zero),
            Add(calendar, new DateTimeOffset(2026, 3, 28, 22, 0, 0, TimeSpan.Zero), "PT4H"));
        // 00:30 does not exist on 29 March, so the due time is the moment the clock jumps.
        Assert.Equal(
            new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero),
            Add(calendar, new DateTimeOffset(2026, 3, 28, 0, 30, 0, TimeSpan.Zero), "P1D"));
    }

    [Fact]
    public void ARepeatedMidnightHourUsesItsFirstOccurrence()
    {
        var calendar = Calendar(AllDay, AllDay, AllDay, zone: MidnightChange);

        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.FromHours(1)),
            Add(calendar, new DateTimeOffset(2026, 10, 24, 0, 30, 0, TimeSpan.FromHours(1)), "P1D"));
        Assert.Equal(
            new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero),
            Add(calendar, new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(1)), "PT25H"));
    }

    // Clocks move forward by thirty minutes, from 02:00 to 02:30, on 29 March.
    private static readonly TimeZoneInfo HalfHourChange = Zone(
        "Test/HalfHour", TimeSpan.Zero, TimeSpan.FromMinutes(30), (3, 29, 2, 0), (10, 25, 2, 30));

    [Fact]
    public void AThirtyMinuteChangeShortensThePeriodByThirtyMinutes()
    {
        var calendar = Calendar("00:00-06:00", "00:00-06:00", "00:00-06:00", zone: HalfHourChange);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.FromMinutes(30)),
            Add(calendar, new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero), "PT6H"));
    }

    [Fact]
    public void LondonOfficeHoursKeepTheirTimeAcrossBothChanges()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var office = Calendar(Office, zone: london);
        var allDay = Calendar(AllDay, AllDay, AllDay, zone: london);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 30, 14, 0, 0, TimeSpan.FromHours(1)),
            Add(office, new DateTimeOffset(2026, 3, 27, 14, 0, 0, TimeSpan.Zero), "P1D"));
        Assert.Equal(
            new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.FromHours(1)),
            Add(allDay, new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero), "P1D"));
        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero),
            Add(allDay, new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(1)), "P1D"));
        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 11, 0, 0, TimeSpan.Zero),
            Add(allDay, new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(1)), "PT24H"));
    }

    [Fact]
    public void NewYorkPeriodsFollowBothChanges()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var allDay = Calendar(AllDay, AllDay, AllDay, zone: newYork);
        var night = new BusinessCalendar(
            "night",
            BusinessCalendarContentRules.Parse(
                "America/New_York",
                new BusinessCalendarWeekDto(Sunday: Periods("02:30-05:00")),
                null,
                checkTimeZone: false),
            newYork);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.FromHours(-4)),
            Add(allDay, new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.FromHours(-5)), "P1D"));
        Assert.Equal(
            new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(-5)),
            Add(allDay, new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(-4)), "P1D"));
        // 02:30 is skipped on 8 March, so the period runs from 03:00 to 05:00.
        Assert.Equal(
            new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.FromHours(-4)),
            Add(night, new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.FromHours(-5)), "PT2H"));
    }

    [Fact]
    public void TimeZonesMustBeIanaIdsInTheirExactCasing()
    {
        Assert.True(BusinessCalendarTimeZones.TryFind("Europe/London", out var london, out _));
        Assert.Equal("Europe/London", london.Id);
        Assert.False(BusinessCalendarTimeZones.TryFind("europe/london", out _, out var casing));
        Assert.Contains("'europe/london'", casing);
        Assert.False(BusinessCalendarTimeZones.TryFind("GMT Standard Time", out _, out _));
        Assert.False(BusinessCalendarTimeZones.TryFind("Nowhere/Land", out _, out var unknown));
        Assert.Equal(
            "timeZone 'Nowhere/Land' is not an IANA time zone that this server knows, such as Europe/London.",
            unknown);
        Assert.False(BusinessCalendarTimeZones.TryFind(" ", out _, out var blank));
        Assert.Equal("timeZone is required.", blank);
        Assert.Contains("Etc/UTC", BusinessCalendarTimeZones.ListIanaIds());
        Assert.Contains("Europe/London", BusinessCalendarTimeZones.ListIanaIds());
    }

    internal static BusinessCalendar Calendar(
        string weekdays,
        string? saturday = null,
        string? sunday = null,
        TimeZoneInfo? zone = null,
        IReadOnlyList<BusinessCalendarHolidayDto>? holidays = null)
    {
        var weekday = Periods(weekdays);
        var week = new BusinessCalendarWeekDto(
            weekday, weekday, weekday, weekday, weekday, Periods(saturday), Periods(sunday));
        var timeZone = zone ?? Utc;
        return new BusinessCalendar(
            "test",
            BusinessCalendarContentRules.Parse(timeZone.Id, week, holidays, checkTimeZone: false),
            timeZone);
    }

    internal static BusinessCalendarHolidayDto Holiday(string date, string? hours = null) =>
        new(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), null, Periods(hours));

    internal static IReadOnlyList<WorkingPeriodDto> Periods(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(',', StringSplitOptions.TrimEntries)
                .Select(period => period.Split('-', StringSplitOptions.TrimEntries))
                .Select(parts => new WorkingPeriodDto(parts[0], parts[1]))
                .ToList();

    private static DateTimeOffset Add(BusinessCalendar calendar, DateTimeOffset start, string duration)
    {
        Assert.True(calendar.TryAdd(start, Duration(duration), out var due, out var problem), problem);
        return due;
    }

    private static WorkingDuration Duration(string text)
    {
        Assert.True(TimerDefinitionRules.TryParseWorkingDuration(text, out var duration), text);
        return duration;
    }

    private static DateTimeOffset At(string utc) =>
        DateTimeOffset.Parse(utc + "Z", System.Globalization.CultureInfo.InvariantCulture);

    private static TimeZoneInfo Zone(
        string id,
        TimeSpan baseOffset,
        TimeSpan delta,
        (int Month, int Day, int Hour, int Minute) start,
        (int Month, int Day, int Hour, int Minute) end)
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2020, 1, 1),
            new DateTime(2030, 12, 31),
            delta,
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(
                new DateTime(1, 1, 1, start.Hour, start.Minute, 0), start.Month, start.Day),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(
                new DateTime(1, 1, 1, end.Hour, end.Minute, 0), end.Month, end.Day));
        return TimeZoneInfo.CreateCustomTimeZone(id, baseOffset, id, id, id + " daylight", [rule]);
    }
}
