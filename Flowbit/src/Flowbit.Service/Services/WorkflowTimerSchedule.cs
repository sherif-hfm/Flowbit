using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Resolves the fixed-duration timer subset supported by Flowbit into absolute
/// UTC occurrences. Calendar-relative years and months are deliberately not
/// accepted because their duration depends on the activation date and timezone.
/// </summary>
public sealed record WorkflowTimerSchedule(
    DateTimeOffset FirstOccurrenceAt,
    TimeSpan? Interval,
    int? TotalOccurrences)
{
    public static bool IsRecurringMisfire(
        string scheduleKind,
        DateTimeOffset? scheduledOccurrenceAt,
        DateTimeOffset now,
        TimeSpan grace)
    {
        if (grace < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(grace));
        }

        return string.Equals(
                   scheduleKind,
                   TimerScheduleKinds.Cycle,
                   StringComparison.Ordinal)
               && scheduledOccurrenceAt is DateTimeOffset dueAt
               && dueAt < now - grace;
    }

    /// <summary>
    /// Resolves a timer's schedule from its activation time. A duration that names a business calendar is counted in that
    /// calendar's working time, which <paramref name="calendar"/> must then supply.
    /// </summary>
    public static WorkflowTimerSchedule Resolve(
        TimerDefinitionModel timer,
        DateTimeOffset activatedAt,
        BusinessCalendar? calendar = null)
    {
        ArgumentNullException.ThrowIfNull(timer);

        if (!string.IsNullOrWhiteSpace(timer.TimeDate))
        {
            if (!TimerDefinitionRules.TryParseTimeDate(timer.TimeDate, out var dueAt))
            {
                throw new WorkflowDomainException(
                    $"Timer date '{timer.TimeDate}' is not a valid date-time with an offset.");
            }

            return new WorkflowTimerSchedule(dueAt.ToUniversalTime(), null, 1);
        }

        if (!string.IsNullOrWhiteSpace(timer.TimeDuration))
        {
            if (!TimerDefinitionRules.TryParseFixedDuration(timer.TimeDuration, out var duration))
            {
                throw new WorkflowDomainException(
                    $"Timer duration '{timer.TimeDuration}' is not a supported fixed ISO-8601 duration.");
            }

            if (!string.IsNullOrWhiteSpace(timer.Calendar))
            {
                return ResolveWorkingDuration(timer, activatedAt, calendar);
            }

            return new WorkflowTimerSchedule(activatedAt.ToUniversalTime() + duration, null, 1);
        }

        if (!string.IsNullOrWhiteSpace(timer.TimeCycle))
        {
            if (!TimerDefinitionRules.TryParseTimeCycle(
                    timer.TimeCycle,
                    out var totalOccurrences,
                    out var interval))
            {
                throw new WorkflowDomainException(
                    $"Timer cycle '{timer.TimeCycle}' must use R/duration or R<count>/duration.");
            }

            return new WorkflowTimerSchedule(
                activatedAt.ToUniversalTime() + interval,
                interval,
                totalOccurrences);
        }

        throw new WorkflowDomainException("Timer configuration has no schedule.");
    }

    /// <summary>
    /// Resolves the due time of a timeDate or timeDuration that an expression
    /// gave when its timer activated. A duration that names a calendar is
    /// counted in <paramref name="calendar"/>, which the caller loads. Problems
    /// never repeat the value, because it can come from instance data.
    /// </summary>
    public static bool TryResolveExpressionResult(
        string property,
        string value,
        DateTimeOffset activatedAt,
        string? calendarName,
        BusinessCalendar? calendar,
        out DateTimeOffset dueAt,
        out string? problem)
    {
        dueAt = default;
        problem = null;
        if (string.Equals(property, TimerDefinitionRules.TimeDateProperty, StringComparison.Ordinal))
        {
            if (!TimerDefinitionRules.TryParseTimeDate(value, out var timestamp))
            {
                problem = $"the expression gave text that is not {TimerDefinitionRules.TimeDateFormDescription}";
                return false;
            }

            dueAt = timestamp.ToUniversalTime();
            return true;
        }

        if (!TimerDefinitionRules.TryParseFixedDuration(value, out var duration))
        {
            problem = $"the expression gave text that is not {TimerDefinitionRules.TimeDurationFormDescription}";
            return false;
        }

        var start = activatedAt.ToUniversalTime();
        if (calendarName is null)
        {
            if (DateTimeOffset.MaxValue - start < duration)
            {
                problem = "the duration it gave passes the largest supported date";
                return false;
            }

            dueAt = start + duration;
            return true;
        }

        if (!TimerDefinitionRules.TryParseWorkingDuration(value, out var working))
        {
            problem = $"business calendar '{calendarName}' cannot count the duration it gave; use {BusinessCalendarRules.WorkingDurationRuleDescription}";
            return false;
        }

        if (!BusinessCalendarRules.FitsSearchWindow(working))
        {
            problem = $"the duration it gave is longer than business calendar '{calendarName}' can count; use {BusinessCalendarRules.WorkingDurationLimitDescription}";
            return false;
        }

        if (calendar is null || !string.Equals(calendar.Name, calendarName, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"business calendar '{calendarName}' was not loaded";
            return false;
        }

        if (!calendar.TryAdd(start, working, out dueAt, out var calendarProblem))
        {
            problem = $"business calendar '{calendar.Name}' cannot count the duration it gave: {calendarProblem}";
            return false;
        }

        return true;
    }

    private static WorkflowTimerSchedule ResolveWorkingDuration(
        TimerDefinitionModel timer,
        DateTimeOffset activatedAt,
        BusinessCalendar? calendar)
    {
        var name = timer.Calendar!.Trim();
        if (calendar is null || !string.Equals(calendar.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowDomainException(
                $"Timer duration '{timer.TimeDuration}' needs business calendar '{name}', which was not loaded.");
        }

        if (!TimerDefinitionRules.TryParseWorkingDuration(timer.TimeDuration, out var working))
        {
            throw new WorkflowDomainException(
                BusinessCalendarRules.WorkingDurationProblem("Timer duration", timer.TimeDuration!.Trim()));
        }

        if (!calendar.TryAdd(activatedAt, working, out var dueAt, out var problem))
        {
            throw new WorkflowDomainException(
                $"Timer duration '{timer.TimeDuration!.Trim()}' cannot be counted in business calendar '{calendar.Name}': {problem}.");
        }

        return new WorkflowTimerSchedule(dueAt, null, 1);
    }

    public DateTimeOffset? GetOccurrence(int zeroBasedIndex)
    {
        if (zeroBasedIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zeroBasedIndex));
        }

        if (TotalOccurrences is int total && zeroBasedIndex >= total)
        {
            return null;
        }

        if (zeroBasedIndex == 0)
        {
            return FirstOccurrenceAt;
        }

        if (Interval is null)
        {
            return null;
        }

        try
        {
            return FirstOccurrenceAt + TimeSpan.FromTicks(
                checked(Interval.Value.Ticks * (long)zeroBasedIndex));
        }
        catch (OverflowException)
        {
            throw new WorkflowDomainException("Timer occurrence exceeds the supported date-time range.");
        }
    }

    /// <summary>
    /// Resolves the first cycle occurrence after <paramref name="currentOccurrence"/>
    /// whose due time is at or after <paramref name="notBefore"/>. The calculation
    /// is constant-time even when the engine was offline for many intervals.
    /// </summary>
    public static WorkflowTimerOccurrence? ResolveNextCycleOccurrence(
        TimerDefinitionModel timer,
        long currentOccurrence,
        DateTimeOffset currentDueAt,
        DateTimeOffset notBefore)
    {
        ArgumentNullException.ThrowIfNull(timer);
        if (currentOccurrence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentOccurrence));
        }
        if (!TimerDefinitionRules.TryParseTimeCycle(
                timer.TimeCycle,
                out var totalOccurrences,
                out var interval))
        {
            return null;
        }

        var dueUtc = currentDueAt.ToUniversalTime();
        var thresholdUtc = notBefore.ToUniversalTime();
        long steps = 1;
        if (dueUtc < thresholdUtc)
        {
            var deltaTicks = thresholdUtc.Ticks - dueUtc.Ticks;
            steps = deltaTicks / interval.Ticks;
            if (deltaTicks % interval.Ticks != 0)
            {
                steps++;
            }
            steps = Math.Max(1, steps);
        }

        long nextOccurrence;
        long addedTicks;
        try
        {
            nextOccurrence = checked(currentOccurrence + steps);
            addedTicks = checked(interval.Ticks * steps);
        }
        catch (OverflowException)
        {
            throw new WorkflowDomainException(
                "Timer occurrence exceeds the supported numeric range.");
        }
        if (totalOccurrences is int finite && nextOccurrence >= finite)
        {
            return null;
        }

        try
        {
            return new WorkflowTimerOccurrence(
                nextOccurrence,
                dueUtc.AddTicks(addedTicks));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new WorkflowDomainException(
                "Timer occurrence exceeds the supported date-time range.");
        }
    }
}

public sealed record WorkflowTimerOccurrence(
    long Occurrence,
    DateTimeOffset DueAt);
