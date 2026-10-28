using Flowbit.Service.Services;

namespace Flowbit.Service.Models;

/// <summary>
/// A working period in local wall-clock minutes after midnight, from <see cref="StartMinute"/> up to
/// <see cref="EndMinute"/>. An end of 1,440 is midnight at the end of the day.
/// </summary>
public readonly record struct WorkingPeriod(int StartMinute, int EndMinute)
{
    public const int MinutesPerDay = 24 * 60;

    public TimeSpan Start => TimeSpan.FromMinutes(StartMinute);

    public TimeSpan End => TimeSpan.FromMinutes(EndMinute);

    public static string Format(int minute) => $"{minute / 60:00}:{minute % 60:00}";
}

/// <summary>
/// A date in the calendar's time zone whose working periods replace the weekday's: none for a closed day, or the listed
/// periods, for example a half day.
/// </summary>
public sealed record BusinessCalendarHoliday(
    DateOnly Date,
    string? Name,
    IReadOnlyList<WorkingPeriod> WorkingHours);

/// <summary>
/// The validated content of a business calendar: its IANA time zone, the working periods of each weekday indexed by
/// <see cref="DayOfWeek"/>, and its holidays ordered by date.
/// </summary>
public sealed record BusinessCalendarContent(
    string TimeZone,
    IReadOnlyList<IReadOnlyList<WorkingPeriod>> WeeklyHours,
    IReadOnlyList<BusinessCalendarHoliday> Holidays)
{
    public IReadOnlyList<WorkingPeriod> HoursOn(DayOfWeek day) => WeeklyHours[(int)day];
}

public sealed record BusinessCalendarRecord(
    long Id,
    string Name,
    string? Description,
    BusinessCalendarContent Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A calendar loaded for computing due times, or why it cannot be used.</summary>
public sealed record BusinessCalendarLookup(BusinessCalendar? Calendar, string? Problem);
