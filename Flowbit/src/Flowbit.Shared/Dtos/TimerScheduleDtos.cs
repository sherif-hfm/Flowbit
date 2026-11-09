namespace Flowbit.Shared.Dtos;

/// <summary>
/// Previews a timer start's cron schedule, which need not be saved. <see cref="FromUtc"/> is an optional UTC timestamp
/// ending in <c>Z</c> or <c>+00:00</c>, such as <c>2026-11-08T12:00:00Z</c>, and is excluded from the results; it defaults
/// to the server's current time. <see cref="Count"/> is 1 to 10 and defaults to 5.
/// </summary>
public sealed record TimerSchedulePreviewRequest(
    string? Cron,
    string? TimeZone,
    string? FromUtc = null,
    int? Count = null);

/// <summary>
/// One upcoming occurrence: its UTC instant, and the local date and time with the offset that the schedule's time zone
/// shows then.
/// </summary>
public sealed record TimerScheduleOccurrenceDto(
    DateTimeOffset DueAt,
    DateTimeOffset LocalTime);

/// <summary>
/// The next occurrences of a cron schedule after <see cref="FromUtc"/>, calculated as the Worker would calculate them
/// with this server's time-zone data. A preview reserves nothing and cannot promise when a Worker will run.
/// </summary>
public sealed record TimerSchedulePreviewDto(
    string Cron,
    string TimeZone,
    DateTimeOffset FromUtc,
    IReadOnlyList<TimerScheduleOccurrenceDto> Occurrences);
