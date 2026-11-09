using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Read-only cron schedule help for workflow authors: upcoming occurrences of an unsaved schedule and the time zones
/// this server knows. Nothing is saved, scheduled, or activated.
/// </summary>
public interface ITimerSchedulePreviewService
{
    TimerSchedulePreviewDto Preview(TimerSchedulePreviewRequest request, CancellationToken cancellationToken);

    /// <summary>The IANA time-zone ids this server knows, in ordinal order. The list can be incomplete on Windows.</summary>
    IReadOnlyList<string> ListTimeZones();
}
