using System.Globalization;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Components.Shared;

/// <summary>
/// The text forms of the Business calendars page: a day's working periods written as <c>09:00-12:30, 13:30-17:30</c>, a
/// holiday's date as its <c>date</c> input reports it, a one-line summary of a week, and a preview's due time. The API
/// checks the times themselves.
/// </summary>
internal static class BusinessCalendarText
{
    public static readonly string[] DayNames = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    private static readonly string[] ShortDayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    public static IReadOnlyList<WorkingPeriodDto>?[] Days(BusinessCalendarWeekDto week) =>
        [week.Monday, week.Tuesday, week.Wednesday, week.Thursday, week.Friday, week.Saturday, week.Sunday];

    public static BusinessCalendarWeekDto Week(IReadOnlyList<IReadOnlyList<WorkingPeriodDto>> days) =>
        new(days[0], days[1], days[2], days[3], days[4], days[5], days[6]);

    public static string Format(IReadOnlyList<WorkingPeriodDto>? periods) =>
        periods is null ? string.Empty : string.Join(", ", periods.Select(period => period.Start + "-" + period.End));

    /// <summary>
    /// Reads periods separated by commas, each written as a start and an end joined by a hyphen or an en dash. Blank text
    /// means no working hours. Periods are returned in time order.
    /// </summary>
    public static bool TryParse(string? text, out IReadOnlyList<WorkingPeriodDto> periods, out string? problem)
    {
        periods = [];
        problem = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var result = new List<WorkingPeriodDto>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var times = part.Replace('–', '-').Split('-', StringSplitOptions.TrimEntries);
            if (times.Length != 2 || times[0].Length == 0 || times[1].Length == 0)
            {
                problem = $"'{part}' is not a period such as 09:00-17:00.";
                return false;
            }

            result.Add(new WorkingPeriodDto(times[0], times[1]));
        }

        periods = result.OrderBy(period => period.Start, StringComparer.Ordinal).ToList();
        return true;
    }

    /// <summary>The text of a holiday's <c>date</c> input.</summary>
    public static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The date a <c>date</c> input reports; empty or incomplete text is null.</summary>
    public static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>A summary such as <c>Mon–Fri 09:00–17:00 · Sat 10:00–14:00</c>; days with the same hours are grouped.</summary>
    public static string WeekSummary(BusinessCalendarWeekDto week)
    {
        var days = Days(week).Select(Format).ToArray();
        var groups = new List<string>();
        for (var start = 0; start < days.Length;)
        {
            var end = start;
            while (end + 1 < days.Length && days[end + 1] == days[start])
            {
                end++;
            }

            if (days[start].Length > 0)
            {
                var label = start == end ? ShortDayNames[start] : ShortDayNames[start] + "–" + ShortDayNames[end];
                groups.Add(label + " " + days[start].Replace('-', '–'));
            }

            start = end + 1;
        }

        return groups.Count == 0 ? "No working hours" : string.Join(" · ", groups);
    }

    /// <summary>A preview's due time in the calendar's time zone and in UTC.</summary>
    public static string DueText(BusinessCalendarPreviewDto preview) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Due {preview.DueAtLocal:ddd d MMM yyyy HH:mm} (UTC{preview.DueAtLocal:zzz}), {preview.DueAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC.");
}
