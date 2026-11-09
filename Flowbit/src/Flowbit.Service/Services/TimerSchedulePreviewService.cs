using System.Globalization;
using System.Text.RegularExpressions;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

/// <summary>
/// Previews cron schedules with the same grammar, calculator, time-zone checks, and search horizon that the Worker uses
/// to schedule timer starts.
/// </summary>
public sealed class TimerSchedulePreviewService(TimeProvider timeProvider) : ITimerSchedulePreviewService
{
    public const int DefaultCount = 5;
    public const int MaxCount = 10;

    // An explicit UTC offset only: an offset-free value would have to assume a zone.
    private static readonly Regex UtcTimestampPattern = new(
        @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:\.[0-9]{1,7})?)?(?:Z|\+00:00)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public TimerSchedulePreviewDto Preview(TimerSchedulePreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CronTimerSchedule.TryCreate(
                request.Cron,
                request.TimeZone,
                "cron",
                "timeZone",
                out var schedule,
                out var problem))
        {
            throw new WorkflowDomainException(problem!);
        }

        var fromUtc = request.FromUtc is null ? timeProvider.GetUtcNow().ToUniversalTime() : ParseFromUtc(request.FromUtc);
        var count = request.Count ?? DefaultCount;
        if (count is < 1 or > MaxCount)
        {
            throw new WorkflowDomainException($"count must be from 1 to {MaxCount}.");
        }

        var occurrences = schedule.Upcoming(fromUtc, count, cancellationToken);
        if (occurrences.Count < count)
        {
            // A shorter list would look complete.
            throw new WorkflowDomainException(
                $"cron '{schedule.Expression}' in time zone '{schedule.TimeZoneId}' gives only {occurrences.Count} of "
                + $"the {count} requested occurrences after {Format(fromUtc)}; each must fall within "
                + $"{CronTimerSchedule.SearchHorizonDescription} of the one before.");
        }

        return new TimerSchedulePreviewDto(
            schedule.Expression,
            schedule.TimeZoneId,
            fromUtc,
            occurrences
                .Select(occurrence => new TimerScheduleOccurrenceDto(occurrence, schedule.ToLocal(occurrence)))
                .ToList());
    }

    public IReadOnlyList<string> ListTimeZones() => IanaTimeZones.ListIds();

    private static DateTimeOffset ParseFromUtc(string text)
    {
        var trimmed = text.Trim();
        if (!UtcTimestampPattern.IsMatch(trimmed)
            || !DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var value))
        {
            throw new WorkflowDomainException(
                $"fromUtc '{trimmed}' must be a UTC date and time ending in Z or +00:00, such as 2026-11-08T12:00:00Z.");
        }

        return value.ToUniversalTime();
    }

    private static string Format(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
}
