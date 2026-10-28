using System.Globalization;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// A business calendar compiled for computing due times. Days count working days and keep the time of day; hours,
/// minutes, and seconds count only working time. Working periods are wall-clock times in the calendar's time zone, so a
/// period lasts the real time between its boundaries, also across a daylight-saving change.
/// </summary>
public sealed class BusinessCalendar
{
    /// <summary>The calculation searches this many days, about 100 years, after the start's local date.</summary>
    public const int MaxSearchDays = BusinessCalendarRules.MaxSearchDays;

    private static readonly DateOnly FirstSupportedDate = new(2, 1, 1);
    private static readonly DateOnly LastSupportedDate = new(9998, 12, 31);

    private readonly BusinessCalendarContent content;
    private readonly Dictionary<DateOnly, IReadOnlyList<WorkingPeriod>> holidays;

    public BusinessCalendar(string name, BusinessCalendarContent content, TimeZoneInfo timeZone)
    {
        Name = name;
        TimeZone = timeZone;
        this.content = content;
        holidays = content.Holidays.ToDictionary(holiday => holiday.Date, holiday => holiday.WorkingHours);
    }

    public string Name { get; }

    public TimeZoneInfo TimeZone { get; }

    /// <summary>
    /// Adds a working duration to <paramref name="start"/>. Days apply first, then the time. The due time is in UTC.
    /// </summary>
    public bool TryAdd(
        DateTimeOffset start,
        WorkingDuration duration,
        out DateTimeOffset due,
        out string? problem)
    {
        due = default;
        problem = null;
        if (duration.Days < 0 || duration.Time < TimeSpan.Zero
            || (duration.Days == 0 && duration.Time == TimeSpan.Zero))
        {
            problem = "the duration must be positive";
            return false;
        }

        var startDate = LocalDate(start);
        if (startDate < FirstSupportedDate || startDate > LastSupportedDate)
        {
            problem = "the start is outside the supported date range";
            return false;
        }

        var limit = startDate.DayNumber + MaxSearchDays >= LastSupportedDate.DayNumber
            ? LastSupportedDate
            : startDate.AddDays(MaxSearchDays);
        var from = start.ToUniversalTime();
        if ((duration.Days > 0 && !TryAddDays(from, duration.Days, limit, out from))
            || !TryAddTime(from, duration.Time, limit, out due))
        {
            problem = string.Create(
                CultureInfo.InvariantCulture,
                $"the duration needs more working time than the calendar has within {MaxSearchDays:N0} days of the start");
            return false;
        }

        return true;
    }

    // A start outside working time first moves to the next working time. The due time keeps that time of day on the
    // Nth following working day; when that time is not working time on that day, or is exactly the start of a period,
    // no working time passes before the end of the last earlier period, so that end is the due time.
    private bool TryAddDays(DateTimeOffset start, int days, DateOnly limit, out DateTimeOffset due)
    {
        due = default;
        DateOnly? anchorDate = null;
        var anchor = start;
        for (var date = LocalDate(start).AddDays(-1); anchorDate is null && date <= limit; date = date.AddDays(1))
        {
            foreach (var interval in IntervalsOn(date))
            {
                if (interval.End > start)
                {
                    anchor = interval.Start > start ? interval.Start : start;
                    anchorDate = date;
                    break;
                }
            }
        }

        if (anchorDate is not DateOnly current)
        {
            return false;
        }

        var midnight = current.ToDateTime(TimeOnly.MinValue);
        var timeOfDay = BusinessCalendarTimeZones.ToLocal(TimeZone, anchor) - midnight;
        timeOfDay = timeOfDay < TimeSpan.Zero ? TimeSpan.Zero
            : timeOfDay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1)
            : timeOfDay;

        var previous = IntervalsOn(current);
        var target = previous;
        for (var counted = 0; counted < days;)
        {
            current = current.AddDays(1);
            if (current > limit)
            {
                return false;
            }

            var intervals = IntervalsOn(current);
            if (intervals.Count == 0)
            {
                continue;
            }

            counted++;
            previous = target;
            target = intervals;
        }

        foreach (var interval in target)
        {
            if (interval.LocalStart < timeOfDay && timeOfDay < interval.LocalEnd)
            {
                due = BusinessCalendarTimeZones.ToInstant(
                    TimeZone,
                    current.ToDateTime(TimeOnly.MinValue) + timeOfDay);
                return true;
            }
        }

        due = previous[^1].End;
        foreach (var interval in target)
        {
            if (interval.LocalEnd <= timeOfDay)
            {
                due = interval.End;
            }
        }

        return true;
    }

    // The clock runs only in working periods; the due time is the moment the last of the time elapses.
    private bool TryAddTime(DateTimeOffset from, TimeSpan time, DateOnly limit, out DateTimeOffset due)
    {
        due = from;
        var remaining = time;
        if (remaining <= TimeSpan.Zero)
        {
            return true;
        }

        for (var date = LocalDate(from).AddDays(-1); date <= limit; date = date.AddDays(1))
        {
            foreach (var interval in IntervalsOn(date))
            {
                if (interval.End <= from)
                {
                    continue;
                }

                var begin = interval.Start > from ? interval.Start : from;
                var available = interval.End - begin;
                if (available >= remaining)
                {
                    due = begin + remaining;
                    return true;
                }

                remaining -= available;
            }
        }

        return false;
    }

    // A holiday's periods replace the weekday's. A period that the clock skips entirely, during a daylight-saving
    // change, has no working time and is left out.
    private List<Interval> IntervalsOn(DateOnly date)
    {
        var periods = holidays.TryGetValue(date, out var holiday) ? holiday : content.HoursOn(date.DayOfWeek);
        var result = new List<Interval>(periods.Count);
        if (periods.Count == 0)
        {
            return result;
        }

        var midnight = date.ToDateTime(TimeOnly.MinValue);
        foreach (var period in periods)
        {
            var start = BusinessCalendarTimeZones.ToInstant(TimeZone, midnight + period.Start);
            var end = BusinessCalendarTimeZones.ToInstant(TimeZone, midnight + period.End);
            if (end > start)
            {
                result.Add(new Interval(period.Start, period.End, start, end));
            }
        }

        return result;
    }

    private DateOnly LocalDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(BusinessCalendarTimeZones.ToLocal(TimeZone, instant));

    private readonly record struct Interval(
        TimeSpan LocalStart,
        TimeSpan LocalEnd,
        DateTimeOffset Start,
        DateTimeOffset End);
}
