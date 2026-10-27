using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Models;

/// <summary>
/// The engine setting that decides how long before its due time an open user
/// task counts as due soon.
/// </summary>
public static class UserTaskDueSettings
{
    public const string DueSoonThresholdKey = "UserTasks.DueSoonThreshold";

    public static readonly TimeSpan DefaultDueSoonThreshold = TimeSpan.FromDays(1);

    /// <summary>
    /// Reads a fixed ISO-8601 duration; missing, blank, malformed, or
    /// non-positive values fall back to one day.
    /// </summary>
    public static TimeSpan ParseDueSoonThreshold(string? value) =>
        TimerDefinitionRules.TryParseFixedDuration(value, out var threshold)
            ? threshold
            : DefaultDueSoonThreshold;
}

/// <summary>
/// One request's clock for due statuses, so filtering and the returned
/// <c>dueStatus</c> values agree.
/// </summary>
public sealed record UserTaskDueClock(DateTimeOffset Now, TimeSpan DueSoonThreshold)
{
    public DateTimeOffset DueSoonUntil =>
        DateTimeOffset.MaxValue - Now <= DueSoonThreshold
            ? DateTimeOffset.MaxValue
            : Now + DueSoonThreshold;

    /// <summary>
    /// Returns overdue, dueSoon, or upcoming for an open work item with a due
    /// time, otherwise null.
    /// </summary>
    public string? StatusFor(DateTimeOffset? dueAt, bool open) =>
        !open || dueAt is not DateTimeOffset due
            ? null
            : due <= Now
                ? UserTaskDueStatuses.Overdue
                : due <= DueSoonUntil
                    ? UserTaskDueStatuses.DueSoon
                    : UserTaskDueStatuses.Upcoming;
}

/// <summary>
/// Due-date and priority inbox filters as supplied by the GET query or the
/// POST search body.
/// </summary>
public sealed record InboxDueAndPriorityFilter(
    IReadOnlyList<string>? DueStatuses = null,
    DateTimeOffset? DueFrom = null,
    DateTimeOffset? DueTo = null,
    int? MinPriority = null,
    int? MaxPriority = null);

/// <summary>
/// Validated due-date and priority inbox filters. <see cref="DueSoonUntil"/>
/// is set only when a dueSoon or upcoming status needs it.
/// </summary>
public sealed record InboxDueAndPriorityCriteria(
    IReadOnlySet<string> DueStatuses,
    DateTimeOffset? DueFrom,
    DateTimeOffset? DueTo,
    int? MinPriority,
    int? MaxPriority,
    DateTimeOffset Now,
    DateTimeOffset? DueSoonUntil);
