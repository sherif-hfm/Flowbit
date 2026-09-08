using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

public sealed class SharedVariableService(
    ISharedVariableRepository repository,
    ILogger<SharedVariableService> logger) : ISharedVariableService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PagedResult<SharedVariableDto>> ListAsync(
        SharedVariableListRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        ValidatePage(request.Page, request.PageSize);
        var status = NormalizeStatus(request.Status);
        var (items, total) = await repository.ListAsync(
            TrimToNull(request.Search),
            status,
            checked((request.Page - 1) * request.PageSize),
            request.PageSize,
            request.IncludeArchived,
            cancellationToken);
        return new PagedResult<SharedVariableDto>(
            items.Select(Map).ToArray(),
            request.Page,
            request.PageSize,
            total);
    }

    public async Task<SharedVariableDto?> GetAsync(
        string key,
        SharedVariableCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        var normalizedKey = NormalizeKey(key);
        var record = await repository.GetByKeyAsync(
            normalizedKey,
            includeArchived: true,
            cancellationToken);
        return record is null ? null : Map(record);
    }

    public async Task<SharedVariableDto> CreateAsync(
        CreateSharedVariableRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        var key = NormalizeKey(request.Key);
        var validation = NormalizeBounded(request.Validation, 4000, "validation");
        var description = NormalizeBounded(request.Description, 1000, "description");
        var reason = NormalizeBounded(request.Reason, 1000, "reason");
        var requestId = NormalizeRequestId(request.RequestId);
        SharedVariableValueValidator.ValidateContract(
            key,
            request.DataType,
            request.IsArray,
            request.Nullable,
            validation);
        if (!request.HasValue && request.Value is not null)
        {
            throw new WorkflowDomainException("value must be omitted when hasValue is false.");
        }
        if (request.HasValue)
        {
            if (request.Value is not JsonElement value)
                throw new WorkflowDomainException("value is required when hasValue is true.");
            SharedVariableValueValidator.Validate(
                key,
                request.DataType,
                request.IsArray,
                request.Nullable,
                validation,
                value);
        }

        var normalized = request with
        {
            Key = key,
            Validation = validation,
            Description = description,
            RequestId = requestId,
            Reason = reason,
            Value = request.Value?.Clone()
        };
        var result = await repository.CreateAsync(
            new SharedVariableCreateCommand(
                key,
                request.DataType,
                request.IsArray,
                request.Nullable,
                validation,
                description,
                request.HasValue,
                request.Value?.Clone(),
                ToCaller(caller),
                SharedVariableSources.Api,
                requestId,
                Fingerprint(normalized),
                reason),
            cancellationToken);
        logger.LogInformation(
            "Shared variable {SharedKey} created at revision {Revision} by {CallerKind} {CallerId}.",
            result.Variable.Key,
            result.Variable.Revision,
            caller.Kind,
            caller.Id);
        return Map(result.Variable);
    }

    public async Task<SharedVariableDto?> UpdateAsync(
        string key,
        UpdateSharedVariableRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        var normalizedKey = NormalizeKey(key);
        ValidateExpectedRevision(request.ExpectedRevision);
        var requestId = NormalizeRequestId(request.RequestId);
        var reason = NormalizeBounded(request.Reason, 1000, "reason");
        var description = NormalizeBounded(request.Description, 1000, "description");
        var normalized = request with
        {
            Value = request.Value.Clone(),
            RequestId = requestId,
            Reason = reason,
            Description = description
        };
        var result = await repository.WriteAsync(
            new SharedVariableWriteCommand(
                normalizedKey,
                request.Value.Clone(),
                DeleteValue: false,
                request.ExpectedRevision,
                ToCaller(caller),
                SharedVariableSources.Api,
                requestId,
                Fingerprint(new { key = normalizedKey, request = normalized }),
                reason,
                description),
            cancellationToken);
        if (result is not null)
        {
            logger.LogInformation(
                "Shared variable {SharedKey} set at revision {Revision}; value changed: {ValueChanged}.",
                result.Variable.Key,
                result.Variable.Revision,
                result.Revision.ValueChanged);
        }
        return result is null ? null : Map(result.Variable);
    }

    public async Task<SharedVariableDto?> UpdateDescriptionAsync(
        string key,
        UpdateSharedVariableDescriptionRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        var normalizedKey = NormalizeKey(key);
        ValidateExpectedRevision(request.ExpectedRevision);
        var requestId = NormalizeRequestId(request.RequestId);
        var reason = NormalizeBounded(request.Reason, 1000, "reason");
        var description = NormalizeBounded(request.Description, 1000, "description");
        var current = await repository.GetByKeyAsync(
            normalizedKey,
            includeArchived: true,
            cancellationToken);
        if (current is null)
        {
            return null;
        }
        if (!string.Equals(current.Status, SharedVariableStatuses.Active, StringComparison.Ordinal))
        {
            return null;
        }

        var fingerprint = Fingerprint(new
        {
            key = normalizedKey,
            description,
            request.ExpectedRevision,
            requestId,
            reason
        });
        var result = await repository.WriteAsync(
            new SharedVariableWriteCommand(
                normalizedKey,
                current.Value?.Clone(),
                DeleteValue: !current.HasValue,
                request.ExpectedRevision,
                ToCaller(caller),
                SharedVariableSources.Api,
                requestId,
                fingerprint,
                reason,
                // Empty text deliberately clears the nullable description;
                // null on the repository command means "leave unchanged".
                description ?? string.Empty,
                DescriptionOnly: true),
            cancellationToken);
        if (result is not null)
        {
            logger.LogInformation(
                "Shared variable {SharedKey} description updated at revision {Revision}.",
                result.Variable.Key,
                result.Variable.Revision);
        }
        return result is null ? null : Map(result.Variable);
    }

    public Task<SharedVariableDto?> ArchiveAsync(
        string key,
        ArchiveSharedVariableRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken) =>
        ChangeLifecycleAsync(
            key,
            request.ExpectedRevision,
            request.RequestId,
            request.Reason,
            SharedVariableOperations.Archive,
            caller,
            cancellationToken);

    public Task<SharedVariableDto?> ReactivateAsync(
        string key,
        ReactivateSharedVariableRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken) =>
        ChangeLifecycleAsync(
            key,
            request.ExpectedRevision,
            request.RequestId,
            request.Reason,
            SharedVariableOperations.Reactivate,
            caller,
            cancellationToken);

    private async Task<SharedVariableDto?> ChangeLifecycleAsync(
        string key,
        long expectedRevision,
        string? requestId,
        string? reason,
        string operation,
        SharedVariableCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        var normalizedKey = NormalizeKey(key);
        ValidateExpectedRevision(expectedRevision);
        requestId = NormalizeRequestId(requestId);
        reason = NormalizeBounded(reason, 1000, "reason");
        var result = await repository.ChangeLifecycleAsync(
            new SharedVariableLifecycleCommand(
                normalizedKey,
                expectedRevision,
                ToCaller(caller),
                SharedVariableSources.Api,
                operation,
                requestId,
                Fingerprint(new { key = normalizedKey, expectedRevision, operation, reason }),
                reason),
            cancellationToken);
        if (result is not null)
        {
            logger.LogInformation(
                "Shared variable {SharedKey} lifecycle operation {Operation} committed at revision {Revision}.",
                result.Variable.Key,
                operation,
                result.Variable.Revision);
        }
        return result is null ? null : Map(result.Variable);
    }

    public async Task<IReadOnlyList<SharedVariableRevisionDto>> ListHistoryAsync(
        string key,
        int page,
        int pageSize,
        SharedVariableCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        ValidatePage(page, pageSize);
        var revisions = await repository.ListRevisionsAsync(
            NormalizeKey(key),
            checked((page - 1) * pageSize),
            pageSize,
            cancellationToken);
        return revisions.Select(Map).ToArray();
    }

    public async Task<SharedVariableLifecycleBlockersDto?> GetLifecycleBlockersAsync(
        string key,
        SharedVariableCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        var blockers = await repository.GetLifecycleBlockersAsync(
            NormalizeKey(key),
            cancellationToken);
        return blockers is null
            ? null
            : new SharedVariableLifecycleBlockersDto(
                blockers.PublishedDefinitionCount,
                blockers.RunningInstanceCount,
                blockers.OpenJobCount,
                blockers.Reasons);
    }

    private static SharedVariableDto Map(SharedVariableRecord record) => new(
        record.Id,
        record.Key,
        record.DataType,
        record.IsArray,
        record.Nullable,
        record.Validation,
        record.Description,
        record.HasValue,
        record.Value?.Clone(),
        record.Status,
        record.Revision,
        record.CreatedAt,
        record.UpdatedAt,
        record.ArchivedAt,
        record.ValueRevision,
        record.HistoryPrunedAt);

    private static SharedVariableRevisionDto Map(SharedVariableRevisionRecord record) => new(
        record.Revision,
        record.Operation,
        record.ValueChanged,
        record.HasValue,
        record.Value?.Clone(),
        record.CallerKind,
        record.CallerId,
        record.Source,
        record.Reason,
        record.WorkflowDefinitionId,
        record.InstanceId,
        record.NodeExecutionId,
        record.SourceActionId,
        record.CreatedAt);

    private static SharedVariableCallerRecord ToCaller(SharedVariableCaller caller) => new(
        caller.Kind,
        caller.Id,
        caller.Roles,
        caller.Scopes);

    private static string Fingerprint<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string NormalizeKey(string key)
    {
        var normalized = key?.Trim() ?? string.Empty;
        SharedVariableValueValidator.ValidateContract(
            normalized,
            Flowbit.Shared.Models.WorkflowVariableTypes.Json,
            false,
            true,
            null);
        return normalized;
    }

    private static string? NormalizeStatus(string? status)
    {
        var normalized = TrimToNull(status);
        if (normalized is not null
            && normalized is not (SharedVariableStatuses.Active or SharedVariableStatuses.Archived))
            throw new WorkflowDomainException($"Unknown shared-variable status '{normalized}'.");
        return normalized;
    }

    private static void ValidateExpectedRevision(long revision)
    {
        if (revision <= 0) throw new WorkflowDomainException("expectedRevision must be greater than zero.");
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page <= 0) throw new WorkflowDomainException("page must be greater than zero.");
        if (pageSize is < 1 or > 200)
            throw new WorkflowDomainException("pageSize must be between 1 and 200.");
    }

    private static void ValidateCaller(SharedVariableCaller caller)
    {
        if (caller is null
            || caller.Kind is not (SharedVariableCallerKinds.User
                or SharedVariableCallerKinds.Client
                or SharedVariableCallerKinds.Workflow
                or SharedVariableCallerKinds.System)
            || string.IsNullOrWhiteSpace(caller.Id)
            || caller.Id.EnumerateRunes().Count() > 300)
            throw new WorkflowDomainException("A valid shared-variable caller is required.");
    }

    private static string? NormalizeRequestId(string? value) =>
        NormalizeBounded(value, 300, "requestId");

    private static string? NormalizeBounded(string? value, int maximum, string field)
    {
        var normalized = TrimToNull(value);
        if (normalized?.EnumerateRunes().Count() > maximum)
            throw new WorkflowDomainException($"{field} must contain at most {maximum} Unicode scalar values.");
        return normalized;
    }

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
