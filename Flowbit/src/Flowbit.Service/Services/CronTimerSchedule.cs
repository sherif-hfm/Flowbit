using Cronos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// The server's calculator for a timer start's cron schedule: an expression that <see cref="CronScheduleRules"/>
/// accepts, read by the pinned Cronos package, in an IANA time zone this host resolves. It returns UTC instants, each
/// strictly after the instant it searches from, and never caches or enumerates future occurrences.
/// </summary>
/// <remarks>
/// Daylight saving follows Cronos. A local time the clock skips occurs when the clock jumps past it, so occurrences that
/// fall in the same gap become one. In a repeated hour, an expression whose minute and hour fields are fixed values or
/// lists occurs once, in the earlier offset, while a wildcard, range, or step in either field occurs in both offsets.
/// The expression is therefore passed on exactly as authored: a range and an equivalent list can differ in that hour.
/// </remarks>
public sealed class CronTimerSchedule
{
    /// <summary>A next occurrence counts only within this many days, about 100 years, after the instant searched from.</summary>
    public const int SearchHorizonDays = 36_525;

    public const string SearchHorizonDescription = "36,525 days";

    private static readonly TimeSpan SearchHorizon = TimeSpan.FromDays(SearchHorizonDays);

    private readonly CronExpression _expression;

    private CronTimerSchedule(string expression, TimeZoneInfo zone, CronExpression parsed, CronScheduleFields fields)
    {
        Expression = expression;
        Zone = zone;
        Fields = fields;
        _expression = parsed;
    }

    /// <summary>The authored expression, trimmed.</summary>
    public string Expression { get; }

    public TimeZoneInfo Zone { get; }

    public string TimeZoneId => Zone.Id;

    public CronScheduleFields Fields { get; }

    /// <summary>
    /// Reads a cron expression and its time zone. <paramref name="cronProperty"/> and <paramref name="timeZoneProperty"/>
    /// name them in <paramref name="problem"/>, such as <c>timer.cron</c>.
    /// </summary>
    public static bool TryCreate(
        string? cron,
        string? timeZone,
        string cronProperty,
        string timeZoneProperty,
        out CronTimerSchedule schedule,
        out string? problem)
    {
        schedule = null!;
        if (!CronScheduleRules.TryParse(cron, out var fields, out var cronProblem))
        {
            problem = $"{cronProperty} {cronProblem}";
            return false;
        }

        if (!TryFindTimeZone(timeZone, timeZoneProperty, out var zone, out problem))
        {
            return false;
        }

        var text = cron!.Trim();
        if (!CronExpression.TryParse(text, CronFormat.Standard, out var parsed))
        {
            problem = $"{cronProperty} '{text}' cannot be read by the scheduler.";
            return false;
        }

        schedule = new CronTimerSchedule(text, zone, parsed, fields);
        return true;
    }

    /// <summary>Reads an accepted expression in any zone, such as a synthetic one in tests.</summary>
    internal static CronTimerSchedule Create(string cron, TimeZoneInfo zone)
    {
        if (!CronScheduleRules.TryParse(cron, out var fields, out var problem))
        {
            throw new ArgumentException(problem, nameof(cron));
        }

        var text = cron.Trim();
        return new CronTimerSchedule(text, zone, CronExpression.Parse(text, CronFormat.Standard), fields);
    }

    /// <summary>Resolves a cron schedule's time zone, which is required, at most 100 characters, and exactly cased.</summary>
    public static bool TryFindTimeZone(string? timeZone, string property, out TimeZoneInfo zone, out string? problem)
    {
        zone = null!;
        var trimmed = timeZone?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            problem = $"{property} is required with cron; name an IANA time zone such as Europe/London, or Etc/UTC for UTC.";
            return false;
        }

        if (trimmed.Length > CronScheduleRules.MaxTimeZoneLength)
        {
            problem = $"{property} must contain at most {CronScheduleRules.MaxTimeZoneLength} characters.";
            return false;
        }

        return IanaTimeZones.TryFind(trimmed, property, out zone, out problem);
    }

    /// <summary>
    /// Returns the first occurrence after <paramref name="reference"/>, or at it when <paramref name="inclusive"/>, as a
    /// UTC instant, or null when none falls within <see cref="SearchHorizonDays"/> days of the reference or before the
    /// largest supported time.
    /// </summary>
    public DateTimeOffset? Next(DateTimeOffset reference, bool inclusive)
    {
        var from = reference.ToUniversalTime();
        DateTimeOffset? found;
        try
        {
            found = _expression.GetNextOccurrence(from, Zone, inclusive);
        }
        catch (ArgumentException)
        {
            // The search passed the largest time an offset date-time can show in this zone.
            return null;
        }

        if (found is not { } occurrence)
        {
            return null;
        }

        var next = occurrence.ToUniversalTime();
        return next < from || (!inclusive && next == from) || next - from > SearchHorizon
            ? null
            : next;
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> occurrences after <paramref name="fromExclusive"/>, fewer only when the
    /// search horizon ends first. Cancellation is checked between lookups; a single lookup cannot be interrupted.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Upcoming(
        DateTimeOffset fromExclusive,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var occurrences = new List<DateTimeOffset>(count);
        var cursor = fromExclusive.ToUniversalTime();
        while (occurrences.Count < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Next(cursor, inclusive: false) is not { } next)
            {
                break;
            }

            occurrences.Add(next);
            cursor = next;
        }

        return occurrences;
    }

    /// <summary>The local time and offset that the schedule's zone shows at a UTC instant.</summary>
    public DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);
}
