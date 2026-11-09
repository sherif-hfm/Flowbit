using System.Globalization;

namespace Flowbit.Shared.Models;

/// <summary>
/// The fields a cron expression selects. Bit <c>n</c> of a field is set when value <c>n</c> is selected; day of week
/// runs from 0, Sunday, to 6. <see cref="LastDayOfMonth"/> means the day-of-month field is L. A day field written with
/// a leading <c>*</c> leaves the day unrestricted, as crontab reads it.
/// </summary>
public sealed record CronScheduleFields(
    ulong Minutes,
    uint Hours,
    uint DaysOfMonth,
    bool LastDayOfMonth,
    ushort Months,
    byte DaysOfWeek,
    bool DayOfMonthStar,
    bool DayOfWeekStar)
{
    /// <summary>
    /// Whether both day fields restrict the day. A date must then match both; crontab instead runs when either
    /// matches, so a pasted crontab line changes meaning.
    /// </summary>
    public bool RestrictsBothDays => !DayOfMonthStar && !DayOfWeekStar;
}

/// <summary>
/// Flowbit's five-field cron dialect for timer start events: minute, hour, day of month, month, and day of week, in a
/// named time zone. Every value, range end, and step is a number of one or two ASCII digits; fields hold <c>*</c>,
/// <c>*/n</c>, or a list of <c>a</c>, <c>a-b</c>, <c>a/n</c>, and <c>a-b/n</c> items, and the whole day-of-month field may
/// be <c>L</c>, the last day of the month. Day of week runs from 0, Sunday, to 6 and takes no open-ended <c>a/n</c>
/// step, because the server's calculator would continue such a step to 7, which it reads as Sunday. A date must match
/// both day fields. Definition validation, the preview API, and the editor apply these rules before the calculator
/// reads an expression, so aliases, names, seconds, years, reversed ranges, <c>?</c>, <c>W</c>, <c>#</c>, and <c>H</c>
/// never reach it. Problems are worded to follow the property's name, as in <c>timer.cron hour value 24 is outside 0
/// to 23.</c>
/// </summary>
public static class CronScheduleRules
{
    public const int MaxExpressionLength = TimerDefinitionRules.MaxExpressionLength;
    public const int MaxTimeZoneLength = 100;
    public const string FieldsDescription = "minute, hour, day of month, month, and day of week";

    // The Gregorian calendar repeats its dates and weekdays every 400 years, so a set of months, days, and weekdays
    // matches some date exactly when it matches one in these years. Fixed years keep validity independent of today.
    private const int CycleStartYear = 2000;
    private const int CycleYears = 400;

    // 2000-01-01 was a Saturday.
    private const int CycleStartWeekday = 6;

    private static readonly FieldSpec[] Specs =
    [
        new("minute", 0, 59, 59, AllowsOpenStep: true, AllowsLast: false),
        new("hour", 0, 23, 23, AllowsOpenStep: true, AllowsLast: false),
        new("day-of-month", 1, 31, 31, AllowsOpenStep: true, AllowsLast: true),
        new("month", 1, 12, 12, AllowsOpenStep: true, AllowsLast: false),
        new("day-of-week", 0, 6, 6, AllowsOpenStep: false, AllowsLast: false)
    ];

    private static readonly int[] DaysInMonth = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    /// <summary>Describes why an expression is not in the dialect or can never match a date, or returns null.</summary>
    public static string? Problem(string? expression) =>
        TryParse(expression, out _, out var problem) ? null : problem;

    /// <summary>
    /// Parses an expression, after trimming, into the values each field selects. An expression whose fields leave no
    /// calendar date is rejected too.
    /// </summary>
    public static bool TryParse(string? expression, out CronScheduleFields fields, out string? problem)
    {
        fields = null!;
        var text = expression?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            problem = $"must be a cron expression with five fields: {FieldsDescription}.";
            return false;
        }

        if (text.Length > MaxExpressionLength)
        {
            problem = $"must contain at most {MaxExpressionLength} characters.";
            return false;
        }

        if (text[0] == '@')
        {
            problem = $"'{text}' is an alias, which is not supported; write five fields: {FieldsDescription}.";
            return false;
        }

        if (text.Any(character => char.IsWhiteSpace(character) && character is not (' ' or '\t')))
        {
            problem = "may separate its fields only with spaces or tabs.";
            return false;
        }

        var parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != Specs.Length)
        {
            problem = $"'{text}' has {parts.Length} field{(parts.Length == 1 ? string.Empty : "s")}; write five: "
                + $"{FieldsDescription}. Seconds and years are not supported.";
            return false;
        }

        var parsed = new ParsedField[Specs.Length];
        for (var index = 0; index < Specs.Length; index++)
        {
            if (!TryParseField(parts[index], Specs[index], out parsed[index], out problem))
            {
                return false;
            }
        }

        fields = new CronScheduleFields(
            parsed[0].Bits,
            (uint)parsed[1].Bits,
            (uint)parsed[2].Bits,
            parsed[2].Last,
            (ushort)parsed[3].Bits,
            (byte)parsed[4].Bits,
            parsed[2].Star,
            parsed[4].Star);
        if (!MatchesSomeDate(fields))
        {
            fields = null!;
            problem = $"'{text}' never matches a calendar date; check its day-of-month, month, and day-of-week fields.";
            return false;
        }

        problem = null;
        return true;
    }

    private static bool TryParseField(string text, FieldSpec spec, out ParsedField field, out string? problem)
    {
        field = default;
        problem = null;
        if (text == "*")
        {
            field = new ParsedField(Range(spec.First, spec.Last, 1), Last: false, Star: true);
            return true;
        }

        if (spec.AllowsLast && text == "L")
        {
            field = new ParsedField(0, Last: true, Star: false);
            return true;
        }

        // Whole characters, so a message never shows half of a surrogate pair.
        foreach (var rune in text.EnumerateRunes())
        {
            if (!(rune.IsAscii && IsAsciiDigit((char)rune.Value))
                && rune.Value is not (',' or '-' or '/' or '*')
                && !(spec.AllowsLast && rune.Value == 'L'))
            {
                problem = $"{spec.Name} field '{text}' uses '{rune}', which is not supported; write numbers, lists, "
                    + $"ascending ranges, and steps{(spec.AllowsLast ? ", or L alone for the last day" : string.Empty)}.";
                return false;
            }
        }

        if (text.Contains('L'))
        {
            problem = $"{spec.Name} field '{text}' uses L with other values; L must be the whole field.";
            return false;
        }

        if (text.Contains('*'))
        {
            var step = text.StartsWith("*/", StringComparison.Ordinal) ? text[2..] : null;
            if (step is { Length: > 0 } && step.All(IsAsciiDigit))
            {
                if (!TryReadStep(step, spec, out var stepValue, out problem))
                {
                    return false;
                }

                field = new ParsedField(Range(spec.First, spec.Last, stepValue), Last: false, Star: true);
                return true;
            }

            problem = text.Contains(',')
                ? $"{spec.Name} field '{text}' uses * in a list; write * or */n as the whole field."
                : Malformed(spec, text);
            return false;
        }

        ulong bits = 0;
        foreach (var item in text.Split(','))
        {
            if (item.Length == 0)
            {
                problem = $"{spec.Name} field '{text}' has an empty list item.";
                return false;
            }

            var slash = item.Split('/');
            var dash = slash[0].Split('-');

            // Only the part before the slash may be a range, so a step holds digits alone.
            if (slash.Length > 2 || dash.Length > 2 || slash.Any(part => part.Length == 0) || dash.Any(part => part.Length == 0)
                || (slash.Length == 2 && !slash[1].All(IsAsciiDigit)))
            {
                problem = Malformed(spec, item);
                return false;
            }

            if (!TryReadValue(dash[0], spec, out var low, out problem))
            {
                return false;
            }

            var high = low;
            if (dash.Length == 2)
            {
                if (!TryReadValue(dash[1], spec, out high, out problem))
                {
                    return false;
                }

                if (high < low)
                {
                    problem = $"{spec.Name} range '{dash[0]}-{dash[1]}' is reversed; write ranges in ascending order.";
                    return false;
                }
            }

            var stepValue = 1;
            if (slash.Length == 2)
            {
                if (dash.Length == 1)
                {
                    if (!spec.AllowsOpenStep)
                    {
                        problem = $"{spec.Name} item '{item}' has an open-ended step; write */n or a range such as 1-5/2.";
                        return false;
                    }

                    high = spec.Last;
                }

                if (!TryReadStep(slash[1], spec, out stepValue, out problem))
                {
                    return false;
                }
            }

            bits |= Range(low, high, stepValue);
        }

        field = new ParsedField(bits, Last: false, Star: false);
        return true;
    }

    private static bool TryReadValue(string token, FieldSpec spec, out int value, out string? problem)
    {
        if (!TryReadNumber(token, spec, out value, out problem))
        {
            return false;
        }

        if (spec.Name == "day-of-week" && value == 7)
        {
            problem = "day-of-week value 7 is not allowed; write Sunday as 0.";
            return false;
        }

        if (value < spec.First || value > spec.Last)
        {
            problem = $"{spec.Name} value {value} is outside {spec.First} to {spec.Last}.";
            return false;
        }

        return true;
    }

    private static bool TryReadStep(string token, FieldSpec spec, out int value, out string? problem)
    {
        if (!TryReadNumber(token, spec, out value, out problem))
        {
            return false;
        }

        if (value < 1 || value > spec.MaxStep)
        {
            problem = $"{spec.Name} step {value} must be from 1 to {spec.MaxStep}.";
            return false;
        }

        return true;
    }

    // Tokens reach here as ASCII digits only.
    private static bool TryReadNumber(string token, FieldSpec spec, out int value, out string? problem)
    {
        value = 0;
        if (token.Length > 2)
        {
            problem = $"{spec.Name} value '{token}' has more than two digits.";
            return false;
        }

        value = int.Parse(token, NumberStyles.None, CultureInfo.InvariantCulture);
        problem = null;
        return true;
    }

    private static string Malformed(FieldSpec spec, string item) =>
        $"{spec.Name} item '{item}' is malformed; write "
        + (spec.AllowsOpenStep ? "*, */n, a, a-b, a/n, or a-b/n." : "*, */n, a, a-b, or a-b/n.");

    private static ulong Range(int low, int high, int step)
    {
        ulong bits = 0;
        for (var value = low; value <= high; value += step)
        {
            bits |= 1UL << value;
        }

        return bits;
    }

    private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';

    private static bool MatchesSomeDate(CronScheduleFields fields)
    {
        const byte everyWeekday = 0b111_1111;
        const uint everyDayOfMonth = 0xFFFF_FFFE;
        if (fields.DaysOfWeek == everyWeekday)
        {
            // Every month that has a selected day matches, and February has a 29th in leap years.
            for (var month = 1; month <= 12; month++)
            {
                if ((fields.Months & (1 << month)) == 0)
                {
                    continue;
                }

                var longest = month == 2 ? 29 : DaysInMonth[month - 1];
                var daysOfThatMonth = (uint)((1UL << (longest + 1)) - 2);
                if (fields.LastDayOfMonth || (fields.DaysOfMonth & daysOfThatMonth) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        // Every month has each weekday among its first 28 days.
        if (!fields.LastDayOfMonth && fields.DaysOfMonth == everyDayOfMonth)
        {
            return true;
        }

        var weekday = CycleStartWeekday;
        for (var year = CycleStartYear; year < CycleStartYear + CycleYears; year++)
        {
            for (var month = 1; month <= 12; month++)
            {
                var days = month == 2 && IsLeapYear(year) ? 29 : DaysInMonth[month - 1];
                if ((fields.Months & (1 << month)) != 0)
                {
                    for (var day = 1; day <= days; day++)
                    {
                        var dayMatches = fields.LastDayOfMonth ? day == days : (fields.DaysOfMonth & (1u << day)) != 0;
                        if (dayMatches && (fields.DaysOfWeek & (1 << ((weekday + day - 1) % 7))) != 0)
                        {
                            return true;
                        }
                    }
                }

                weekday = (weekday + days) % 7;
            }
        }

        return false;
    }

    private static bool IsLeapYear(int year) => (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;

    private sealed record FieldSpec(string Name, int First, int Last, int MaxStep, bool AllowsOpenStep, bool AllowsLast);

    private readonly record struct ParsedField(ulong Bits, bool Last, bool Star);
}
