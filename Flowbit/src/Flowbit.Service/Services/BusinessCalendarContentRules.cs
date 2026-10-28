using System.Globalization;
using System.Text.RegularExpressions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Validates and maps business-calendar content. Requests are checked against the host's time-zone data; stored content is
/// read without that check, so a calendar stays listable on a host that lacks its zone.
/// </summary>
public static class BusinessCalendarContentRules
{
    public const int MaxPeriodsPerDay = 10;
    public const int MaxHolidays = 1000;
    public const int MaxHolidayNameLength = 200;
    public const int MaxDescriptionLength = 1000;

    private static readonly DateOnly FirstHolidayDate = new(1900, 1, 1);
    private static readonly DateOnly LastHolidayDate = new(9998, 12, 31);

    // ASCII digits only: \d would accept other scripts' digits, which int.Parse rejects with an exception.
    private static readonly Regex TimePattern = new(
        @"^(?<hour>[01][0-9]|2[0-3]):(?<minute>[0-5][0-9])$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    private static readonly (string Name, DayOfWeek Day)[] Days =
    [
        ("monday", DayOfWeek.Monday),
        ("tuesday", DayOfWeek.Tuesday),
        ("wednesday", DayOfWeek.Wednesday),
        ("thursday", DayOfWeek.Thursday),
        ("friday", DayOfWeek.Friday),
        ("saturday", DayOfWeek.Saturday),
        ("sunday", DayOfWeek.Sunday)
    ];

    public static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new WorkflowDomainException("name is required.");
        }

        return BusinessCalendarRules.NameProblem("name", trimmed) is { } problem
            ? throw new WorkflowDomainException(problem)
            : trimmed;
    }

    public static string? NormalizeDescription(string? description)
    {
        var trimmed = description?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > MaxDescriptionLength
            ? throw new WorkflowDomainException(
                $"description must be at most {MaxDescriptionLength} characters.")
            : trimmed;
    }

    /// <summary>
    /// Validates calendar content. With <paramref name="checkTimeZone"/> the time zone must be an IANA zone that this
    /// host knows, written with its exact casing.
    /// </summary>
    public static BusinessCalendarContent Parse(
        string? timeZone,
        BusinessCalendarWeekDto? weeklyHours,
        IReadOnlyList<BusinessCalendarHolidayDto>? holidays,
        bool checkTimeZone)
    {
        var zone = timeZone?.Trim();
        if (string.IsNullOrEmpty(zone))
        {
            throw new WorkflowDomainException("timeZone is required.");
        }

        if (checkTimeZone && !BusinessCalendarTimeZones.TryFind(zone, out _, out var zoneProblem))
        {
            throw new WorkflowDomainException(zoneProblem!);
        }

        if (zone.Length > BusinessCalendarTimeZones.MaxIdLength)
        {
            throw new WorkflowDomainException(
                $"timeZone must be at most {BusinessCalendarTimeZones.MaxIdLength} characters.");
        }

        if (weeklyHours is null)
        {
            throw new WorkflowDomainException("weeklyHours is required.");
        }

        var week = new IReadOnlyList<WorkingPeriod>[7];
        foreach (var (name, day) in Days)
        {
            week[(int)day] = ParsePeriods($"weeklyHours.{name}", PeriodsOf(weeklyHours, day));
        }

        if (week.All(periods => periods.Count == 0))
        {
            throw new WorkflowDomainException("weeklyHours must have at least one working period.");
        }

        return new BusinessCalendarContent(zone, week, ParseHolidays(holidays));
    }

    public static BusinessCalendarDto ToDto(BusinessCalendarRecord record) =>
        new(
            record.Id,
            record.Name,
            record.Description,
            record.Content.TimeZone,
            ToWeekDto(record.Content),
            ToHolidayDtos(record.Content),
            record.CreatedAt,
            record.UpdatedAt);

    public static BusinessCalendarWeekDto ToWeekDto(BusinessCalendarContent content) =>
        new(
            ToPeriodDtos(content.HoursOn(DayOfWeek.Monday)),
            ToPeriodDtos(content.HoursOn(DayOfWeek.Tuesday)),
            ToPeriodDtos(content.HoursOn(DayOfWeek.Wednesday)),
            ToPeriodDtos(content.HoursOn(DayOfWeek.Thursday)),
            ToPeriodDtos(content.HoursOn(DayOfWeek.Friday)),
            ToPeriodDtos(content.HoursOn(DayOfWeek.Saturday)),
            ToPeriodDtos(content.HoursOn(DayOfWeek.Sunday)));

    public static IReadOnlyList<BusinessCalendarHolidayDto> ToHolidayDtos(BusinessCalendarContent content) =>
        content.Holidays
            .Select(holiday => new BusinessCalendarHolidayDto(
                holiday.Date,
                holiday.Name,
                ToPeriodDtos(holiday.WorkingHours)))
            .ToList();

    private static IReadOnlyList<WorkingPeriodDto> ToPeriodDtos(IReadOnlyList<WorkingPeriod> periods) =>
        periods
            .Select(period => new WorkingPeriodDto(
                WorkingPeriod.Format(period.StartMinute),
                WorkingPeriod.Format(period.EndMinute)))
            .ToList();

    private static IReadOnlyList<WorkingPeriodDto>? PeriodsOf(BusinessCalendarWeekDto week, DayOfWeek day) =>
        day switch
        {
            DayOfWeek.Monday => week.Monday,
            DayOfWeek.Tuesday => week.Tuesday,
            DayOfWeek.Wednesday => week.Wednesday,
            DayOfWeek.Thursday => week.Thursday,
            DayOfWeek.Friday => week.Friday,
            DayOfWeek.Saturday => week.Saturday,
            _ => week.Sunday
        };

    private static IReadOnlyList<BusinessCalendarHoliday> ParseHolidays(
        IReadOnlyList<BusinessCalendarHolidayDto>? holidays)
    {
        if (holidays is null || holidays.Count == 0)
        {
            return [];
        }

        if (holidays.Count > MaxHolidays)
        {
            throw new WorkflowDomainException($"holidays must have at most {MaxHolidays} entries.");
        }

        var result = new List<BusinessCalendarHoliday>(holidays.Count);
        var dates = new HashSet<DateOnly>();
        for (var index = 0; index < holidays.Count; index++)
        {
            var path = $"holidays[{index}]";
            var holiday = holidays[index]
                ?? throw new WorkflowDomainException($"{path} is required.");
            if (holiday.Date < FirstHolidayDate || holiday.Date > LastHolidayDate)
            {
                throw new WorkflowDomainException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{path}.date must be a date from {FirstHolidayDate:yyyy-MM-dd} to {LastHolidayDate:yyyy-MM-dd}."));
            }

            if (!dates.Add(holiday.Date))
            {
                throw new WorkflowDomainException(
                    $"holidays lists {holiday.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} more than once.");
            }

            var name = holiday.Name?.Trim();
            if (name?.Length > MaxHolidayNameLength)
            {
                throw new WorkflowDomainException(
                    $"{path}.name must be at most {MaxHolidayNameLength} characters.");
            }

            result.Add(new BusinessCalendarHoliday(
                holiday.Date,
                string.IsNullOrEmpty(name) ? null : name,
                ParsePeriods($"{path}.workingHours", holiday.WorkingHours)));
        }

        result.Sort((left, right) => left.Date.CompareTo(right.Date));
        return result;
    }

    private static IReadOnlyList<WorkingPeriod> ParsePeriods(
        string path,
        IReadOnlyList<WorkingPeriodDto>? periods)
    {
        if (periods is null || periods.Count == 0)
        {
            return [];
        }

        if (periods.Count > MaxPeriodsPerDay)
        {
            throw new WorkflowDomainException(
                $"{path} must have at most {MaxPeriodsPerDay} working periods.");
        }

        var result = new List<WorkingPeriod>(periods.Count);
        for (var index = 0; index < periods.Count; index++)
        {
            var itemPath = $"{path}[{index}]";
            var period = periods[index]
                ?? throw new WorkflowDomainException($"{itemPath} is required.");
            var start = ParseTime($"{itemPath}.start", period.Start, allowEndOfDay: false);
            var end = ParseTime($"{itemPath}.end", period.End, allowEndOfDay: true);
            if (end <= start)
            {
                throw new WorkflowDomainException($"{itemPath} must end after it starts.");
            }

            if (result.Count > 0 && start < result[^1].EndMinute)
            {
                throw new WorkflowDomainException(
                    $"{itemPath} starts at {WorkingPeriod.Format(start)}, before the previous period ends at {WorkingPeriod.Format(result[^1].EndMinute)}.");
            }

            result.Add(new WorkingPeriod(start, end));
        }

        return result;
    }

    private static int ParseTime(string path, string? text, bool allowEndOfDay)
    {
        var trimmed = text?.Trim();
        if (allowEndOfDay && trimmed == "24:00")
        {
            return WorkingPeriod.MinutesPerDay;
        }

        var match = trimmed is null ? Match.Empty : TimePattern.Match(trimmed);
        if (!match.Success)
        {
            throw new WorkflowDomainException(
                $"{path} '{text}' must be a time in HH:mm form{(allowEndOfDay ? ", or 24:00" : string.Empty)}.");
        }

        return int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture) * 60
            + int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
    }
}
