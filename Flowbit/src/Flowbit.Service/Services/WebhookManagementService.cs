using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

/// <summary>
/// Settings-administrator webhook management. It validates configuration, generates and protects secrets that are
/// returned once, and leaves state, revisions, and audit to the repository's transactions.
/// </summary>
public sealed class WebhookManagementService(
    IWebhookRepository repository,
    IWebhookSecretProtector protector,
    WebhookDestinationPolicy destinationPolicy) : IWebhookManagementService
{
    private const int MaxPageSize = 200;

    public async Task<WebhookEventCatalogDto> GetEventCatalogAsync(CancellationToken cancellationToken)
    {
        var capture = await repository.GetCaptureStateAsync(cancellationToken);
        return new WebhookEventCatalogDto(
            WebhookConstraints.SchemaVersion,
            LifecycleEventCatalog.EnvelopeFields,
            LifecycleEventCatalog.Entries
                .Select(entry => new LifecycleEventTypeDto(entry.Type, entry.Description, entry.Subscribable, entry.DataFields))
                .ToArray(),
            capture);
    }

    public async Task<PagedResult<WebhookSubscriptionDto>> ListSubscriptionsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var offset = PageOffset(page, pageSize);
        var (items, total) = await repository.ListSubscriptionsAsync(offset, pageSize, cancellationToken);
        return new PagedResult<WebhookSubscriptionDto>(items, page, pageSize, total);
    }

    public Task<WebhookSubscriptionDetailDto?> GetSubscriptionAsync(Guid id, CancellationToken cancellationToken) =>
        repository.GetSubscriptionAsync(id, cancellationToken);

    public async Task<WebhookSecretResultDto> CreateSubscriptionAsync(
        CreateWebhookSubscriptionRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        actor = RequireActor(actor);
        var configuration = await ValidateConfigurationAsync(
            request.Name,
            request.Description,
            request.EndpointUrl,
            request.EventTypes,
            request.WorkflowKey,
            request.MaxConcurrency,
            cancellationToken);
        RequireKeyProtection();
        var key = StandardWebhookSignature.GenerateKey();
        try
        {
            var keyId = Guid.CreateVersion7();
            var subscription = await repository.CreateSubscriptionAsync(
                new WebhookSubscriptionCreateCommand(
                    Guid.CreateVersion7(),
                    configuration.Name,
                    configuration.Description,
                    configuration.EndpointUrl,
                    configuration.Origin,
                    configuration.EventTypes,
                    configuration.WorkflowKey,
                    configuration.MaxConcurrency,
                    keyId,
                    Protect(key),
                    actor),
                cancellationToken);
            return new WebhookSecretResultDto(subscription, keyId, StandardWebhookSignature.FormatSecret(key));
        }
        finally
        {
            Array.Clear(key);
        }
    }

    public async Task<WebhookSubscriptionDto?> UpdateSubscriptionAsync(
        Guid id,
        UpdateWebhookSubscriptionRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        actor = RequireActor(actor);
        RequireRevision(request.ExpectedRevision);
        var configuration = await ValidateConfigurationAsync(
            request.Name,
            request.Description,
            request.EndpointUrl,
            request.EventTypes,
            request.WorkflowKey,
            request.MaxConcurrency,
            cancellationToken);
        return await repository.UpdateSubscriptionAsync(
            new WebhookSubscriptionUpdateCommand(
                id,
                configuration.Name,
                configuration.Description,
                configuration.EndpointUrl,
                configuration.Origin,
                configuration.EventTypes,
                configuration.WorkflowKey,
                configuration.MaxConcurrency,
                request.ExpectedRevision,
                actor),
            cancellationToken);
    }

    public Task<WebhookSubscriptionDto?> PauseAsync(Guid id, string actor, CancellationToken cancellationToken) =>
        repository.PauseAsync(id, RequireActor(actor), cancellationToken);

    public Task<WebhookSubscriptionDto?> ResumeAsync(
        Guid id,
        WebhookRevisionRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireRevision(request.ExpectedRevision);
        actor = RequireActor(actor);
        // Invalid key configuration blocks activation.
        RequireKeyProtection();
        return repository.ResumeAsync(id, request.ExpectedRevision, actor, cancellationToken);
    }

    public Task<WebhookTestResultDto?> SendTestAsync(Guid id, string actor, CancellationToken cancellationToken)
    {
        actor = RequireActor(actor);
        RequireKeyProtection();
        return repository.QueueTestAsync(id, actor, cancellationToken);
    }

    public async Task<WebhookSecretResultDto?> RotateSecretAsync(
        Guid id,
        RotateWebhookSecretRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        actor = RequireActor(actor);
        RequireRevision(request.ExpectedRevision);
        var overlap = request.OverlapSeconds ?? WebhookConstraints.DefaultOverlapSeconds;
        if (overlap is < 0 or > WebhookConstraints.MaxOverlapSeconds)
        {
            throw new WorkflowDomainException(
                $"Overlap seconds must be between 0 and {WebhookConstraints.MaxOverlapSeconds}.");
        }
        RequireKeyProtection();
        var key = StandardWebhookSignature.GenerateKey();
        try
        {
            var keyId = Guid.CreateVersion7();
            var subscription = await repository.RotateKeyAsync(
                new WebhookKeyRotationCommand(id, request.ExpectedRevision, keyId, Protect(key), overlap, actor),
                cancellationToken);
            return subscription is null
                ? null
                : new WebhookSecretResultDto(subscription, keyId, StandardWebhookSignature.FormatSecret(key));
        }
        finally
        {
            Array.Clear(key);
        }
    }

    public Task<WebhookSubscriptionDto?> RevokePreviousSecretAsync(
        Guid id,
        WebhookRevisionRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireRevision(request.ExpectedRevision);
        return repository.RevokePreviousKeyAsync(id, request.ExpectedRevision, RequireActor(actor), cancellationToken);
    }

    public Task<WebhookSubscriptionDto?> ArchiveAsync(
        Guid id,
        WebhookRevisionRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireRevision(request.ExpectedRevision);
        return repository.ArchiveAsync(id, request.ExpectedRevision, RequireActor(actor), cancellationToken);
    }

    public Task<WebhookBulkDeliveryResultDto?> BulkRetryAsync(
        Guid subscriptionId,
        WebhookBulkDeliveryRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        actor = RequireActor(actor);
        RequireKeyProtection();
        return repository.BulkRetryAsync(subscriptionId, BulkFilter(subscriptionId, request), actor, cancellationToken);
    }

    public Task<WebhookBulkDeliveryResultDto?> BulkDiscardAsync(
        Guid subscriptionId,
        WebhookBulkDeliveryRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        actor = RequireActor(actor);
        return repository.BulkDiscardAsync(subscriptionId, BulkFilter(subscriptionId, request), actor, cancellationToken);
    }

    public async Task<PagedResult<WebhookDeliveryDto>> ListDeliveriesAsync(
        WebhookDeliveryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var offset = PageOffset(query.Page, query.PageSize);
        if (query.State is { } state && !WebhookDeliveryStates.All.Contains(state, StringComparer.Ordinal))
        {
            throw new WorkflowDomainException(
                $"State must be one of: {string.Join(", ", WebhookDeliveryStates.All)}.");
        }
        var filter = new WebhookDeliveryFilter(
            query.SubscriptionId,
            query.EventId,
            ValidateEventTypeFilter(query.EventType),
            ValidateWorkflowKeyFilter(query.WorkflowKey),
            PositiveOrNull(query.InstanceId, "Instance id"),
            PositiveOrNull(query.RevisionId, "Destination revision id"),
            query.State is null ? null : [query.State],
            ValidateFailureCode(query.FailureCode),
            query.From?.ToUniversalTime(),
            query.To?.ToUniversalTime());
        ValidateRange(filter.From, filter.To);
        var (items, total) = await repository.ListDeliveriesAsync(filter, offset, query.PageSize, cancellationToken);
        return new PagedResult<WebhookDeliveryDto>(items, query.Page, query.PageSize, total);
    }

    public Task<WebhookDeliveryDetailDto?> GetDeliveryAsync(Guid id, CancellationToken cancellationToken) =>
        repository.GetDeliveryAsync(id, cancellationToken);

    public async Task<PagedResult<WebhookDeliveryAttemptDto>?> ListAttemptsAsync(
        Guid deliveryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var offset = PageOffset(page, pageSize);
        var result = await repository.ListAttemptsAsync(deliveryId, offset, pageSize, cancellationToken);
        return result is null
            ? null
            : new PagedResult<WebhookDeliveryAttemptDto>(result.Value.Items, page, pageSize, result.Value.TotalCount);
    }

    public Task<WebhookDeliveryActionResultDto?> RetryDeliveryAsync(
        Guid id,
        WebhookRevisionRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireRevision(request.ExpectedRevision);
        actor = RequireActor(actor);
        RequireKeyProtection();
        return repository.RetryDeliveryAsync(id, request.ExpectedRevision, actor, cancellationToken);
    }

    public Task<WebhookDeliveryActionResultDto?> DiscardDeliveryAsync(
        Guid id,
        WebhookRevisionRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireRevision(request.ExpectedRevision);
        return repository.DiscardDeliveryAsync(id, request.ExpectedRevision, RequireActor(actor), cancellationToken);
    }

    private async Task<ValidatedConfiguration> ValidateConfigurationAsync(
        string? name,
        string? description,
        string? endpointUrl,
        IReadOnlyList<string>? eventTypes,
        string? workflowKey,
        int? maxConcurrency,
        CancellationToken cancellationToken)
    {
        var trimmedName = name?.Trim();
        if (string.IsNullOrEmpty(trimmedName) || trimmedName.Length > WebhookConstraints.MaxNameLength)
        {
            throw new WorkflowDomainException(
                $"Name is required and must not exceed {WebhookConstraints.MaxNameLength} characters.");
        }
        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmedDescription?.Length > WebhookConstraints.MaxDescriptionLength)
        {
            throw new WorkflowDomainException(
                $"Description must not exceed {WebhookConstraints.MaxDescriptionLength} characters.");
        }
        if (!destinationPolicy.TryValidateUrl(endpointUrl, out var uri, out var urlError))
        {
            throw new WorkflowDomainException(urlError!);
        }
        if (eventTypes is null || eventTypes.Count == 0)
        {
            throw new WorkflowDomainException("Select at least one event type.");
        }
        var unsupported = eventTypes.Where(type => !LifecycleEventCatalog.IsSubscribable(type)).ToArray();
        if (unsupported.Length > 0)
        {
            throw new WorkflowDomainException(
                $"Unsupported event types: {string.Join(", ", unsupported.Select(type => $"'{type}'"))}. "
                + $"Supported types are: {string.Join(", ", LifecycleEventTypes.Subscribable)}.");
        }
        // Deduplicated in catalog order.
        var types = LifecycleEventTypes.Subscribable
            .Where(type => eventTypes.Contains(type, StringComparer.Ordinal))
            .ToArray();
        string? key = null;
        if (workflowKey is not null)
        {
            key = workflowKey;
            if (string.IsNullOrWhiteSpace(key)
                || !string.Equals(key, key.Trim(), StringComparison.Ordinal)
                || key.Length > WebhookConstraints.MaxWorkflowKeyLength)
            {
                throw new WorkflowDomainException(
                    "Workflow key must be an exact, nonblank key without surrounding whitespace.");
            }
            if (!await repository.WorkflowKeyExistsAsync(key, cancellationToken))
            {
                throw new WorkflowDomainException($"Workflow key '{key}' does not match an existing workflow family.");
            }
        }
        var concurrency = maxConcurrency ?? WebhookConstraints.DefaultMaxConcurrency;
        if (concurrency is < WebhookConstraints.MinMaxConcurrency or > WebhookConstraints.MaxMaxConcurrency)
        {
            throw new WorkflowDomainException(
                $"Maximum concurrency must be between {WebhookConstraints.MinMaxConcurrency} and {WebhookConstraints.MaxMaxConcurrency}.");
        }
        return new ValidatedConfiguration(
            trimmedName,
            trimmedDescription,
            endpointUrl!,
            WebhookDestinationPolicy.Origin(uri!),
            types,
            key,
            concurrency);
    }

    private static WebhookDeliveryFilter BulkFilter(Guid subscriptionId, WebhookBulkDeliveryRequest? request)
    {
        request ??= new WebhookBulkDeliveryRequest(null, null, null, null, null, null, null);
        var filter = new WebhookDeliveryFilter(
            subscriptionId,
            null,
            ValidateEventTypeFilter(request.EventType),
            ValidateWorkflowKeyFilter(request.WorkflowKey),
            PositiveOrNull(request.InstanceId, "Instance id"),
            PositiveOrNull(request.RevisionId, "Destination revision id"),
            null,
            ValidateFailureCode(request.FailureCode),
            request.From?.ToUniversalTime(),
            request.To?.ToUniversalTime());
        ValidateRange(filter.From, filter.To);
        return filter;
    }

    private static string? ValidateEventTypeFilter(string? type)
    {
        if (type is null)
        {
            return null;
        }
        if (LifecycleEventCatalog.Find(type) is null)
        {
            throw new WorkflowDomainException($"Event type '{type}' is not a lifecycle event type.");
        }
        return type;
    }

    private static string? ValidateWorkflowKeyFilter(string? key)
    {
        if (key is null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(key) || key.Length > WebhookConstraints.MaxWorkflowKeyLength)
        {
            throw new WorkflowDomainException("Workflow key filter must be nonblank and at most 300 characters.");
        }
        return key;
    }

    // Exact codes only, so a misspelled filter is reported instead of silently matching nothing.
    private static string? ValidateFailureCode(string? code)
    {
        if (code is null)
        {
            return null;
        }
        if (!WebhookOutcomeCodes.FailureCodes.Contains(code, StringComparer.Ordinal))
        {
            throw new WorkflowDomainException(
                $"Failure code '{code}' is not a delivery failure code. "
                + $"Use one of: {string.Join(", ", WebhookOutcomeCodes.FailureCodes)}.");
        }
        return code;
    }

    private static long? PositiveOrNull(long? value, string label)
    {
        if (value is <= 0)
        {
            throw new WorkflowDomainException($"{label} must be positive.");
        }
        return value;
    }

    private static void ValidateRange(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is not null && to is not null && from >= to)
        {
            throw new WorkflowDomainException("'from' must be earlier than 'to'.");
        }
    }

    /// <summary>Validates numbered paging and returns the row offset, which must fit the repository's integer offset.</summary>
    private static int PageOffset(int page, int pageSize)
    {
        if (page < 1)
        {
            throw new WorkflowDomainException("Page must be at least 1.");
        }
        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new WorkflowDomainException($"Page size must be between 1 and {MaxPageSize}.");
        }
        var offset = (long)(page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            throw new WorkflowDomainException(
                $"Page {page} is too large for page size {pageSize}: (page - 1) * pageSize must not exceed {int.MaxValue}.");
        }
        return (int)offset;
    }

    private static void RequireRevision(long expectedRevision)
    {
        if (expectedRevision < 1)
        {
            throw new WorkflowDomainException("Expected revision must be a positive integer.");
        }
    }

    private static string RequireActor(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new WorkflowUnauthorizedException("An authenticated actor is required.");
        }
        var trimmed = actor.Trim();
        if (trimmed.Length > WebhookConstraints.MaxActorLength)
        {
            throw new WorkflowUnauthorizedException("The authenticated actor identifier is too long.");
        }
        return trimmed;
    }

    private void RequireKeyProtection()
    {
        if (!protector.IsAvailable)
        {
            throw new WebhookKeyProtectionUnavailableException(
                protector.UnavailableReason ?? "Webhook key protection is unavailable.");
        }
    }

    private byte[] Protect(byte[] key)
    {
        try
        {
            return protector.Protect(key);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new WebhookKeyProtectionUnavailableException(
                "Webhook key protection failed. Check the webhook key-ring certificate configuration.");
        }
    }

    private sealed record ValidatedConfiguration(
        string Name,
        string? Description,
        string EndpointUrl,
        string Origin,
        IReadOnlyList<string> EventTypes,
        string? WorkflowKey,
        int MaxConcurrency)
    {
        // The URL can carry a receiver token.
        public override string ToString() => nameof(ValidatedConfiguration);
    }
}
