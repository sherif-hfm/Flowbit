namespace Flowbit.Shared.Dtos;

/// <summary>
/// A named business calendar, as managed on the Business calendars page. Timer durations and user-task due dates that name
/// it count its working time. Responses always list all seven days.
/// </summary>
public sealed record BusinessCalendarDto(
    long Id,
    string Name,
    string? Description,
    string TimeZone,
    BusinessCalendarWeekDto WeeklyHours,
    IReadOnlyList<BusinessCalendarHolidayDto> Holidays,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The working periods of each weekday. A day without periods is not a working day.</summary>
public sealed record BusinessCalendarWeekDto(
    IReadOnlyList<WorkingPeriodDto>? Monday = null,
    IReadOnlyList<WorkingPeriodDto>? Tuesday = null,
    IReadOnlyList<WorkingPeriodDto>? Wednesday = null,
    IReadOnlyList<WorkingPeriodDto>? Thursday = null,
    IReadOnlyList<WorkingPeriodDto>? Friday = null,
    IReadOnlyList<WorkingPeriodDto>? Saturday = null,
    IReadOnlyList<WorkingPeriodDto>? Sunday = null);

/// <summary>
/// A working period in local wall-clock time, from <see cref="Start"/> up to <see cref="End"/>. Times use <c>HH:mm</c>; an end
/// of <c>24:00</c> means midnight at the end of the day.
/// </summary>
public sealed record WorkingPeriodDto(string Start, string End);

/// <summary>
/// A date in the calendar's time zone that has no working time, or only the listed working hours, for example a half day.
/// </summary>
public sealed record BusinessCalendarHolidayDto(
    DateOnly Date,
    string? Name = null,
    IReadOnlyList<WorkingPeriodDto>? WorkingHours = null);

public sealed record CreateBusinessCalendarRequest(
    string Name,
    string TimeZone,
    BusinessCalendarWeekDto WeeklyHours,
    IReadOnlyList<BusinessCalendarHolidayDto>? Holidays = null,
    string? Description = null);

/// <summary>Replaces a calendar's description, time zone, weekly hours, and holidays. The name cannot change.</summary>
public sealed record UpdateBusinessCalendarRequest(
    string TimeZone,
    BusinessCalendarWeekDto WeeklyHours,
    IReadOnlyList<BusinessCalendarHolidayDto>? Holidays,
    string? Description,
    DateTimeOffset ExpectedUpdatedAt);

/// <summary>
/// Computes a due time with a calendar that need not be saved. <see cref="Start"/> is a local date and time in the calendar's
/// time zone, such as <c>2026-03-27T15:00</c>, and <see cref="Duration"/> a working duration such as <c>P3D</c>.
/// </summary>
public sealed record BusinessCalendarPreviewRequest(
    string TimeZone,
    BusinessCalendarWeekDto WeeklyHours,
    IReadOnlyList<BusinessCalendarHolidayDto>? Holidays,
    string Start,
    string Duration);

/// <summary>The resolved start and due time of a preview, in UTC and, for the due time, in the calendar's offset.</summary>
public sealed record BusinessCalendarPreviewDto(
    DateTimeOffset StartAt,
    DateTimeOffset DueAt,
    DateTimeOffset DueAtLocal);
