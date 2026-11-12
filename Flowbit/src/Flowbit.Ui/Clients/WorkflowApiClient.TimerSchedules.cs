using System.Net.Http.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Clients;

/// <summary>Workflow authors' cron schedule preview, time-zone suggestions, and authoring capabilities for the editor.</summary>
public sealed partial class WorkflowApiClient
{
    public async Task<TimerSchedulePreviewDto> PreviewTimerScheduleAsync(
        TimerSchedulePreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/workflows/timer-schedules/preview", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TimerSchedulePreviewDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty timer schedule preview.");
    }

    public async Task<WorkflowAuthoringCapabilitiesDto> GetAuthoringCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/workflows/authoring-capabilities", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WorkflowAuthoringCapabilitiesDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned empty authoring capabilities.");
    }

    public async Task<IReadOnlyList<string>> GetTimerScheduleTimeZonesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/workflows/timer-schedules/time-zones", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<string>>(cancellationToken) ?? [];
    }
}
