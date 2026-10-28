using System.Globalization;
using Microsoft.Extensions.Logging;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed class BusinessCalendarService(
    IBusinessCalendarRepository repository,
    ILogger<BusinessCalendarService> logger) : IBusinessCalendarService
{
    private static readonly string[] PreviewStartFormats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"];

    public async Task<IReadOnlyList<BusinessCalendarDto>> ListAsync(CancellationToken cancellationToken) =>
        (await repository.ListAsync(cancellationToken))
            .Select(BusinessCalendarContentRules.ToDto)
            .ToList();

    public async Task<BusinessCalendarDto> CreateAsync(
        CreateBusinessCalendarRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = BusinessCalendarContentRules.NormalizeName(request.Name);
        var description = BusinessCalendarContentRules.NormalizeDescription(request.Description);
        var content = BusinessCalendarContentRules.Parse(
            request.TimeZone,
            request.WeeklyHours,
            request.Holidays,
            checkTimeZone: true);
        var record = await repository.CreateAsync(name, description, content, cancellationToken);
        logger.LogInformation("Business calendar '{Name}' created with id {CalendarId}.", record.Name, record.Id);
        return BusinessCalendarContentRules.ToDto(record);
    }

    public async Task<BusinessCalendarDto?> UpdateAsync(
        long id,
        UpdateBusinessCalendarRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SettingManagementValidation.ValidateMutation(id, request.ExpectedUpdatedAt);
        var description = BusinessCalendarContentRules.NormalizeDescription(request.Description);
        var content = BusinessCalendarContentRules.Parse(
            request.TimeZone,
            request.WeeklyHours,
            request.Holidays,
            checkTimeZone: true);
        var record = await repository.UpdateAsync(
            id,
            description,
            content,
            request.ExpectedUpdatedAt,
            cancellationToken);
        if (record is not null)
        {
            logger.LogInformation("Business calendar '{Name}' updated.", record.Name);
        }

        return record is null ? null : BusinessCalendarContentRules.ToDto(record);
    }

    public async Task<bool> DeleteAsync(
        long id,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken)
    {
        SettingManagementValidation.ValidateMutation(id, expectedUpdatedAt);
        var deleted = await repository.DeleteAsync(id, expectedUpdatedAt, cancellationToken);
        if (deleted)
        {
            logger.LogInformation("Business calendar {CalendarId} deleted.", id);
        }

        return deleted;
    }

    public BusinessCalendarPreviewDto Preview(BusinessCalendarPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = BusinessCalendarContentRules.Parse(
            request.TimeZone,
            request.WeeklyHours,
            request.Holidays,
            checkTimeZone: true);
        BusinessCalendarTimeZones.TryFind(content.TimeZone, out var zone, out _);

        var startText = request.Start?.Trim();
        if (!DateTime.TryParseExact(
                startText,
                PreviewStartFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localStart)
            || localStart.Year is < 1900 or > 9998)
        {
            throw new WorkflowDomainException(
                $"start '{request.Start}' must be a local date and time such as 2026-03-27T15:00, from 1900 to 9998.");
        }

        var durationText = request.Duration?.Trim();
        if (string.IsNullOrEmpty(durationText))
        {
            throw new WorkflowDomainException("duration is required.");
        }

        if (BusinessCalendarRules.CountableDurationProblem("duration", durationText) is { } durationProblem)
        {
            throw new WorkflowDomainException(durationProblem);
        }

        _ = TimerDefinitionRules.TryParseWorkingDuration(durationText, out var duration);

        var calendar = new BusinessCalendar("preview", content, zone);
        var startAt = BusinessCalendarTimeZones.ToInstant(zone, localStart);
        if (!calendar.TryAdd(startAt, duration, out var dueAt, out var problem))
        {
            throw new WorkflowDomainException($"The calendar gives no due time: {problem}.");
        }

        var dueLocal = BusinessCalendarTimeZones.ToLocal(zone, dueAt);
        return new BusinessCalendarPreviewDto(
            startAt,
            dueAt,
            new DateTimeOffset(dueLocal, zone.GetUtcOffset(dueAt.UtcDateTime)));
    }

    public IReadOnlyList<string> ListTimeZones() => BusinessCalendarTimeZones.ListIanaIds();
}
