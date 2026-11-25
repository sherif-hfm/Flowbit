using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

/// <summary>
/// The signal catalog's routes (EVENT-04F). Entry content is checked here; the repository
/// checks the live versions that use the name under the name's exclusive lock.
/// </summary>
public sealed class SignalCatalogService(
    ISignalCatalogRepository repository,
    IEngineSettingsRepository engineSettings,
    ILogger<SignalCatalogService> logger) : ISignalCatalogService
{
    private const int MaxPreviewProblems = 200;
    private const int MaxUncatalogedNames = 200;

    public async Task<SignalCatalogListDto> ListAsync(
        SignalCatalogCaller caller,
        string? name,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(status)
            && status is not (SignalCatalogStatuses.Active or SignalCatalogStatuses.Archived))
        {
            throw new WorkflowDomainException(
                $"status must be {SignalCatalogStatuses.Active} or {SignalCatalogStatuses.Archived}.");
        }

        var (safePage, safeSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));
        var result = await repository.ListAsync(
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            string.IsNullOrEmpty(status) ? null : status,
            safePage,
            safeSize,
            cancellationToken);
        var signalRoles = await ReadSignalRolesAsync(cancellationToken);
        return new SignalCatalogListDto(
            result.Items.Select(entry => ToDto(entry, caller, signalRoles)).ToList(),
            result.Page,
            result.PageSize,
            result.TotalCount,
            await ReadModeAsync(cancellationToken),
            // A client throws without roles, so the user roles a throw needs are no business of it.
            caller.IsClient ? [] : signalRoles);
    }

    public async Task<SignalCatalogEntryDto?> GetAsync(
        SignalCatalogCaller caller,
        string name,
        CancellationToken cancellationToken)
    {
        if (!SignalEventRules.IsValidName(name))
        {
            return null;
        }

        var entry = await repository.GetAsync(name, cancellationToken);
        return entry is null ? null : ToDto(entry, caller, await ReadSignalRolesAsync(cancellationToken));
    }

    public async Task<SignalCatalogEntryDto> CreateAsync(
        SignalCatalogCaller caller,
        CreateSignalCatalogEntryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = Content(
            request.Name,
            checkName: true,
            request.Description,
            request.Fields,
            request.ThrowRoles,
            request.AllowClients,
            request.AllowWorkflows);
        var created = await repository.CreateAsync(request.Name!, content, caller.Id, cancellationToken);
        logger.LogInformation(
            "Signal catalog entry '{SignalName}' created by {Actor}.", created.Name, caller.Id ?? "anonymous");
        return ToDto(created, caller, await ReadSignalRolesAsync(cancellationToken));
    }

    public async Task<SignalCatalogEntryDto?> UpdateAsync(
        SignalCatalogCaller caller,
        string name,
        UpdateSignalCatalogEntryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SignalEventRules.IsValidName(name))
        {
            return null;
        }

        RequireExpectedUpdatedAt(request.ExpectedUpdatedAt);
        var content = Content(
            name,
            checkName: false,
            request.Description,
            request.Fields,
            request.ThrowRoles,
            request.AllowClients,
            request.AllowWorkflows);
        var updated = await repository.UpdateAsync(name, content, request.ExpectedUpdatedAt, caller.Id, cancellationToken);
        if (updated is null)
        {
            return null;
        }

        logger.LogInformation(
            "Signal catalog entry '{SignalName}' changed by {Actor}.", updated.Name, caller.Id ?? "anonymous");
        return ToDto(updated, caller, await ReadSignalRolesAsync(cancellationToken));
    }

    public Task<SignalCatalogEntryDto?> ArchiveAsync(
        SignalCatalogCaller caller,
        string name,
        SignalCatalogLifecycleRequest request,
        CancellationToken cancellationToken) =>
        ChangeStatusAsync(caller, name, request, archive: true, cancellationToken);

    public Task<SignalCatalogEntryDto?> ReactivateAsync(
        SignalCatalogCaller caller,
        string name,
        SignalCatalogLifecycleRequest request,
        CancellationToken cancellationToken) =>
        ChangeStatusAsync(caller, name, request, archive: false, cancellationToken);

    public async Task<SignalCatalogUsageDto?> GetUsageAsync(
        string name,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (!SignalEventRules.IsValidName(name) || await repository.GetAsync(name, cancellationToken) is null)
        {
            return null;
        }

        var (safePage, safeSize) = (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));
        var counts = await repository.GetUsageCountsAsync(name, cancellationToken);
        var versions = await repository.ListVersionUsesAsync(name, safePage, safeSize, cancellationToken);
        return new SignalCatalogUsageDto(
            name,
            counts.PublishedVersionCount,
            counts.RunningVersionCount,
            counts.ActiveWaitCount,
            counts.StartingFamilyCount,
            counts.DeliveringBroadcastCount,
            new PagedResult<SignalCatalogVersionUseDto>(
                versions.Items
                    .Select(version => new SignalCatalogVersionUseDto(
                        version.WorkflowDefinitionId,
                        version.WorkflowKey,
                        version.WorkflowName,
                        version.Version,
                        version.IsPublished,
                        version.NodeIds))
                    .ToList(),
                versions.Page,
                versions.PageSize,
                versions.TotalCount));
    }

    public async Task<SignalCatalogImpactDto> PreviewAsync(
        CreateSignalCatalogEntryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = Content(
            request.Name,
            checkName: true,
            request.Description,
            request.Fields,
            request.ThrowRoles,
            request.AllowClients,
            request.AllowWorkflows);
        var now = DateTimeOffset.UtcNow;
        var candidate = new SignalCatalogEntryRecord(
            0,
            request.Name!,
            content.Description,
            SignalCatalogStatuses.Active,
            content.Fields,
            content.ThrowRoles,
            content.AllowClients,
            content.AllowWorkflows,
            now,
            null,
            now,
            null,
            null);
        var (problems, truncated, liveCount) =
            await repository.PreviewAsync(candidate, MaxPreviewProblems, cancellationToken);
        return new SignalCatalogImpactDto(candidate.Name, liveCount, problems, truncated);
    }

    public async Task<UncatalogedSignalNameListDto> ListUncatalogedNamesAsync(CancellationToken cancellationToken)
    {
        var (items, truncated) = await repository.ListUncatalogedNamesAsync(MaxUncatalogedNames, cancellationToken);
        return new UncatalogedSignalNameListDto(
            items.Select(item => new UncatalogedSignalNameDto(item.Name, item.VersionCount)).ToList(),
            truncated,
            await ReadModeAsync(cancellationToken));
    }

    private async Task<SignalCatalogEntryDto?> ChangeStatusAsync(
        SignalCatalogCaller caller,
        string name,
        SignalCatalogLifecycleRequest request,
        bool archive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SignalEventRules.IsValidName(name))
        {
            return null;
        }

        RequireExpectedUpdatedAt(request.ExpectedUpdatedAt);
        var changed = await repository.ChangeStatusAsync(
            name,
            archive,
            request.ExpectedUpdatedAt,
            caller.Id,
            cancellationToken);
        if (changed is null)
        {
            return null;
        }

        logger.LogInformation(
            "Signal catalog entry '{SignalName}' is {Status} after a request by {Actor}.",
            changed.Name,
            changed.Status,
            caller.Id ?? "anonymous");
        return ToDto(changed, caller, await ReadSignalRolesAsync(cancellationToken));
    }

    private static SignalCatalogEntryContent Content(
        string? name,
        bool checkName,
        string? description,
        IReadOnlyList<SignalCatalogFieldDto?>? fields,
        IReadOnlyList<string?>? throwRoles,
        bool allowClients,
        bool allowWorkflows)
    {
        if (SignalCatalogEntryRules.Problem(name, checkName, description, fields, throwRoles) is { } problem)
        {
            throw new WorkflowDomainException(problem);
        }

        return new SignalCatalogEntryContent(
            SignalCatalogEntryRules.NormalizeDescription(description),
            SignalCatalogEntryRules.NormalizeFields(fields),
            SignalCatalogEntryRules.NormalizeRoles(throwRoles),
            allowClients,
            allowWorkflows);
    }

    private static void RequireExpectedUpdatedAt(DateTimeOffset expectedUpdatedAt)
    {
        if (expectedUpdatedAt == default)
        {
            throw new WorkflowDomainException("expectedUpdatedAt is required.");
        }
    }

    private async Task<string> ReadModeAsync(CancellationToken cancellationToken) =>
        SignalCatalogModes.ParseMode(
            (await engineSettings.GetByKeyAsync(SignalCatalogModes.SettingKey, cancellationToken))?.Value);

    private async Task<IReadOnlyList<string>> ReadSignalRolesAsync(CancellationToken cancellationToken) =>
        SignalSettings.ParseRoles(
            (await engineSettings.GetByKeyAsync(SignalSettings.RequiredRoleKey, cancellationToken))?.Value);

    // A managed API client reads an entry to learn its contract; the administrators who wrote it
    // are user identities it has no need of, so a client gets no createdBy or updatedBy.
    internal static SignalCatalogEntryDto ToDto(
        SignalCatalogEntryRecord entry,
        SignalCatalogCaller caller,
        IReadOnlyList<string> signalRoles) =>
        new(
            entry.Name,
            entry.Description,
            entry.Status,
            entry.Fields,
            entry.ThrowRoles,
            entry.AllowClients,
            entry.AllowWorkflows,
            SignalCatalogAccess.CanThrow(entry, caller, signalRoles),
            entry.CreatedAt,
            caller.IsClient ? null : entry.CreatedBy,
            entry.UpdatedAt,
            caller.IsClient ? null : entry.UpdatedBy,
            entry.ArchivedAt);
}
