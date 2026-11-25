using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

/// <summary>
/// Throws signals and manages broadcasts. A throw takes no runtime lock: it freezes its
/// recipients in one statement and leaves delivery to the Worker. A managed API client
/// sees and changes only the broadcasts it threw.
/// </summary>
public sealed class SignalBroadcastService(
    ISignalBroadcastRepository broadcasts,
    ISignalSubscriptionRepository subscriptions,
    IWorkflowDefinitionRepository definitions,
    IEngineSettingsRepository engineSettings,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<SignalBroadcastService> logger,
    SignalBroadcastWriter writer,
    ISignalCatalogRepository? catalog = null)
    : ISignalBroadcastService
{
    public const string DeliverPhase = SignalBroadcastWriter.DeliverPhase;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(SignalBroadcastDetailDto Broadcast, bool Replayed)> ThrowAsync(
        ThrowSignalRequest request,
        SignalCaller caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        var name = request.Name;
        if (SignalEventRules.ThrowNameProblem(name) is { } nameProblem)
        {
            throw new WorkflowDomainException(nameProblem);
        }

        // A blank workflow or business key is refused rather than read as "no key", so a
        // request that names a family or a case by mistake cannot reach more.
        var scope = SignalBroadcastScope.FromRequest(
                request.WorkflowKey,
                request.AllWorkflows,
                request.BusinessKey,
                request.InstanceId,
                out var scopeProblem)
            ?? throw new WorkflowDomainException(scopeProblem!);

        // Before the hash, so a request that would replay is checked too.
        TimeSpan? expiresAfter = null;
        if (request.ExpiresAfter is { } expiresAfterText)
        {
            var problem = SignalExpiryRules.Parse(expiresAfterText, out var parsed);
            if (SignalExpiryRules.Message(problem) is { } expiryProblem)
            {
                throw new WorkflowDomainException(expiryProblem);
            }

            expiresAfter = parsed;
        }

        var payloadJson = NormalizePayload(request.Payload);
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);
        var requestHash = HashRequest(name!, scope, payloadJson, expiresAfter);
        if (idempotencyKey is not null
            && await broadcasts.FindByIdempotencyKeyAsync(caller.Kind, caller.Id, idempotencyKey, cancellationToken)
                is { } earlier)
        {
            return (ReplayOrConflict(earlier, requestHash, caller), true);
        }

        // After the replay check: a retried request still gets its broadcast whatever
        // happened to its family or instance since.
        scope = await ResolveScopeAsync(scope, cancellationToken);

        var maxRecipients = await ReadLimitAsync(
            SignalSettings.MaxSubscriptionsPerBroadcastKey,
            SignalSettings.DefaultMaxSubscriptionsPerBroadcast,
            SignalSettings.MaxSubscriptionsPerBroadcastCap,
            cancellationToken);
        var maxOpen = await ReadLimitAsync(
            SignalSettings.MaxOpenBroadcastsKey,
            SignalSettings.DefaultMaxOpenBroadcasts,
            SignalSettings.MaxOpenBroadcastsCap,
            cancellationToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (idempotencyKey is not null)
        {
            await broadcasts.LockIdempotencyKeyAsync(caller.Kind, caller.Id, idempotencyKey, cancellationToken);
            if (await broadcasts.FindByIdempotencyKeyAsync(caller.Kind, caller.Id, idempotencyKey, cancellationToken)
                is { } winner)
            {
                await transaction.CommitAsync(cancellationToken);
                return (ReplayOrConflict(winner, requestHash, caller), true);
            }
        }

        // After the replay check, so a retried request gets its broadcast whatever happened to
        // the catalog since; before the open-broadcast count, so a payload that does not fit is
        // never reported as the limit.
        await CheckCatalogForThrowAsync(name!, request.Payload, caller, cancellationToken);

        // A soft limit: counted without a lock, so no throw queues behind another
        // throw's recipient insert, and concurrent throws may pass it by a few. Only
        // broadcasts thrown through the API count: workflows have their own budget.
        if (await broadcasts.CountOpenAsync(SignalSettings.ApiThrowerKinds, maxOpen, cancellationToken) >= maxOpen)
        {
            throw new WorkflowConflictException(
                $"{maxOpen} signal broadcasts are still delivering; wait for one to finish or cancel one.");
        }

        var written = await writer.WriteAsync(
            new SignalBroadcastWrite(
                new NewSignalBroadcastRecord(
                    name!,
                    scope,
                    payloadJson,
                    caller.Kind,
                    caller.Id,
                    JsonSerializer.Serialize(caller.Roles, JsonOptions),
                    caller.AuditClaims is null ? null : JsonSerializer.Serialize(caller.AuditClaims, JsonOptions),
                    idempotencyKey,
                    requestHash,
                    now,
                    ExpiresAfter: expiresAfter),
                maxRecipients,
                caller.Id),
            cancellationToken);
        if (written.RecipientLimitExceeded)
        {
            throw new WorkflowDomainException(
                $"Signal '{name}' would reach more than {maxRecipients} recipients; "
                + "narrow its scope or raise Signals.MaxSubscriptionsPerBroadcast.");
        }

        await transaction.CommitAsync(cancellationToken);

        SignalRuntimeTelemetry.RecordBroadcast(caller.Kind, written.RecipientCount);
        logger.LogInformation(
            "Signal broadcast {BroadcastId} froze {RecipientCount} recipients in {FamilyCount} workflow families.",
            written.BroadcastId, written.RecipientCount, written.FamilyCount);
        var stored = await broadcasts.GetAsync(written.BroadcastId, false, cancellationToken)
            ?? throw new InvalidOperationException($"Signal broadcast #{written.BroadcastId} disappeared after it was thrown.");
        return (ToDetail(stored, caller), false);
    }

    public async Task<PagedResult<SignalBroadcastSummaryDto>> ListAsync(
        SignalCaller caller,
        string? name,
        string? workflowKey,
        string? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? thrownByKinds = null)
    {
        if (!string.IsNullOrEmpty(status) && !SignalBroadcastStatuses.All.Contains(status))
        {
            throw new WorkflowDomainException($"Unknown signal broadcast status '{status}'.");
        }

        var kinds = (thrownByKinds ?? [])
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (kinds.FirstOrDefault(kind => !KnownThrowerKinds.Contains(kind, StringComparer.Ordinal)) is { } unknown)
        {
            throw new WorkflowDomainException(
                $"Unknown thrownByKind '{unknown}'; use {SignalThrowerKinds.User}, {SignalThrowerKinds.Client}, or {SignalThrowerKinds.Instance}.");
        }

        var (safePage, safeSize) = ClampPage(page, pageSize);
        var result = await broadcasts.ListAsync(
            new SignalBroadcastQuery(
                string.IsNullOrWhiteSpace(name) ? null : name,
                string.IsNullOrWhiteSpace(workflowKey) ? null : workflowKey,
                string.IsNullOrWhiteSpace(status) ? null : status,
                caller.IsClient ? caller.Kind : null,
                caller.IsClient ? caller.Id : null,
                safePage,
                safeSize,
                kinds.Length == 0 ? null : kinds),
            cancellationToken);
        return new PagedResult<SignalBroadcastSummaryDto>(
            result.Items.Select(ToSummary).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    public async Task<SignalBroadcastDetailDto?> GetAsync(
        SignalCaller caller,
        long broadcastId,
        CancellationToken cancellationToken)
    {
        var broadcast = await broadcasts.GetAsync(broadcastId, false, cancellationToken);
        return broadcast is null || !IsVisible(broadcast, caller) ? null : ToDetail(broadcast, caller);
    }

    public async Task<PagedResult<SignalDeliveryDto>?> ListDeliveriesAsync(
        SignalCaller caller,
        long broadcastId,
        string? status,
        long? instanceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        string? kind = null)
    {
        if (!string.IsNullOrEmpty(status) && !SignalDeliveryStatuses.All.Contains(status))
        {
            throw new WorkflowDomainException($"Unknown signal delivery status '{status}'.");
        }

        if (!string.IsNullOrEmpty(kind) && !SignalDeliveryKinds.All.Contains(kind, StringComparer.Ordinal))
        {
            throw new WorkflowDomainException(
                $"Unknown signal delivery kind '{kind}'; use {SignalDeliveryKinds.Wait} or {SignalDeliveryKinds.Start}.");
        }

        var broadcast = await broadcasts.GetAsync(broadcastId, false, cancellationToken);
        if (broadcast is null || !IsVisible(broadcast, caller))
        {
            return null;
        }

        var (safePage, safeSize) = ClampPage(page, pageSize);
        var result = await broadcasts.ListDeliveriesAsync(
            new SignalDeliveryQuery(
                broadcastId,
                string.IsNullOrWhiteSpace(status) ? null : status,
                instanceId,
                safePage,
                safeSize,
                string.IsNullOrWhiteSpace(kind) ? null : kind),
            cancellationToken);
        return new PagedResult<SignalDeliveryDto>(
            result.Items.Select(delivery => ToDelivery(delivery, caller)).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount);
    }

    public async Task<SignalBroadcastDetailDto?> CancelAsync(
        SignalCaller caller,
        long broadcastId,
        CancelSignalBroadcastRequest request,
        CancellationToken cancellationToken)
    {
        var reason = string.IsNullOrWhiteSpace(request?.Reason) ? null : request!.Reason!.Trim();
        if (reason is not null && reason.Length > SignalConstraints.MaxReasonLength)
        {
            throw new WorkflowDomainException(
                $"A cancellation reason can have at most {SignalConstraints.MaxReasonLength} characters.");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var broadcast = await broadcasts.GetAsync(broadcastId, true, cancellationToken);
        if (broadcast is null || !IsVisible(broadcast, caller))
        {
            return null;
        }

        // Its thrower may always stop a broadcast, so a mistake can be stopped after the
        // entry's roles change; anyone else needs the throw rules of the entry now.
        if (!IsThrower(broadcast, caller))
        {
            await CheckCatalogForCommandAsync(broadcast.SignalName, caller, SignalCatalogAccess.Cancelling, cancellationToken);
        }

        if (broadcast.Status == SignalBroadcastStatuses.Cancelled)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToDetail(broadcast, caller);
        }

        if (broadcast.Status != SignalBroadcastStatuses.Delivering)
        {
            throw new WorkflowConflictException(
                $"Signal broadcast #{broadcastId} has already finished and cannot be cancelled.");
        }

        // A delivery inside its own transaction holds its row, so this waits for it and
        // then leaves it as it finished.
        await broadcasts.CancelPendingDeliveriesAsync(broadcastId, now, cancellationToken);
        await broadcasts.MarkCancelledAsync(broadcastId, caller.Id, reason, now, cancellationToken);
        var updated = await broadcasts.RefreshCountsAsync(broadcastId, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Signal broadcast {BroadcastId} was cancelled.", broadcastId);
        return ToDetail(updated!, caller);
    }

    public async Task<SignalBroadcastDetailDto?> RetryFailedAsync(
        SignalCaller caller,
        long broadcastId,
        RetrySignalDeliveriesRequest request,
        CancellationToken cancellationToken)
    {
        if (request?.ExpectedUpdatedAt is not DateTimeOffset expectedUpdatedAt)
        {
            throw new WorkflowDomainException(
                "A retry needs expectedUpdatedAt, the broadcast's updatedAt as last read.");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var broadcast = await broadcasts.GetAsync(broadcastId, true, cancellationToken);
        if (broadcast is null || !IsVisible(broadcast, caller))
        {
            return null;
        }

        // A retry delivers the payload again, so it follows the throw rules for everyone, and an
        // archived name, which nothing may throw, cannot be retried either.
        await CheckCatalogForCommandAsync(broadcast.SignalName, caller, SignalCatalogAccess.Retrying, cancellationToken);

        if (broadcast.Status != SignalBroadcastStatuses.CompletedWithIssues)
        {
            throw new WorkflowConflictException(
                $"Only a broadcast that finished with failed deliveries can be retried; #{broadcastId} is {broadcast.Status}.");
        }

        // Under the broadcast's row lock and with the database's clock: a retried delivery
        // after the expiry would only end as expired. A retry before it keeps the original
        // expiresAt, so a retried delivery that runs late is skipped like any other.
        if (broadcast.ExpiresAt is DateTimeOffset expiresAt
            && await broadcasts.IsExpiredAsync(broadcastId, cancellationToken))
        {
            throw new WorkflowConflictException(
                $"Signal broadcast #{broadcastId} expired at {FormatInstant(expiresAt)}; its failed deliveries cannot be retried. "
                + "Throw the signal again to reach the waits that are still open.");
        }

        if (broadcast.UpdatedAt != expectedUpdatedAt)
        {
            throw new WorkflowConflictException(
                $"Signal broadcast #{broadcastId} changed since it was read. Refresh and try again.");
        }

        var retried = await broadcasts.RetryFailedDeliveriesAsync(broadcastId, now, cancellationToken);
        if (retried.Changed == 0)
        {
            throw new WorkflowConflictException($"Signal broadcast #{broadcastId} has no failed deliveries.");
        }

        // A failed start whose family no longer starts on the name ends as skipped, so a
        // retry can leave nothing pending; the recount then finishes the broadcast.
        if (retried.DefinitionIds.Count > 0)
        {
            await broadcasts.ReopenAsync(broadcastId, now, cancellationToken);
            await writer.EnqueueDeliveryJobsAsync(
                broadcastId,
                retried.DefinitionIds,
                SignalBroadcastJobReasons.Retry,
                caller.Id,
                now,
                cancellationToken);
        }

        var updated = await broadcasts.RefreshCountsAsync(broadcastId, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Signal broadcast {BroadcastId} queued its failed deliveries again in {JobCount} jobs.",
            broadcastId, retried.DefinitionIds.Count);
        return ToDetail(updated!, caller);
    }

    public async Task<SignalSubscriptionSummaryDto> SummarizeSubscriptionsAsync(
        string? name,
        string? workflowKey,
        string? businessKey,
        long? instanceId,
        CancellationToken cancellationToken)
    {
        // A read: an unknown instance or a family without business keys counts nothing,
        // where a throw to them is refused.
        var filter = SignalBroadcastScope.SummaryFilter(workflowKey, businessKey, instanceId, out var problem)
            ?? throw new WorkflowDomainException(problem!);
        var signalName = string.IsNullOrWhiteSpace(name) ? null : name;
        var rows = await subscriptions.SummarizeActiveAsync(
            signalName,
            filter,
            SignalConstraints.MaxSummaryRows + 1,
            cancellationToken);
        return new SignalSubscriptionSummaryDto(
            rows.Take(SignalConstraints.MaxSummaryRows)
                .Select(row => new SignalSubscriptionCountDto(row.SignalName, row.WorkflowKey, row.Count, row.StartCount))
                .ToArray(),
            rows.Count > SignalConstraints.MaxSummaryRows);
    }

    public async Task<IReadOnlyList<SignalSubscriptionDto>> ListInstanceSubscriptionsAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var active = await subscriptions.ListActiveByInstanceAsync(instanceId, false, cancellationToken);
        return active
            .Take(SignalConstraints.MaxInstanceSubscriptions)
            .Select(subscription => new SignalSubscriptionDto(
                subscription.Id,
                subscription.SignalName,
                subscription.SignalNodeId,
                subscription.SignalNodeName,
                subscription.AttachedToNodeId,
                subscription.CancelActivity,
                subscription.TokenId,
                subscription.CreatedAt))
            .ToArray();
    }

    /// <summary>
    /// Checks the scope against the database without a lock: the family must exist, a
    /// business key needs a family that has enabled business keys (no instance of any
    /// other family has a key), and an instance scope learns its instance's family.
    /// </summary>
    private async Task<SignalBroadcastScope> ResolveScopeAsync(
        SignalBroadcastScope scope,
        CancellationToken cancellationToken)
    {
        switch (scope.Kind)
        {
            case SignalBroadcastScopeKind.Family:
            case SignalBroadcastScopeKind.BusinessKey:
                var workflowKey = scope.WorkflowKey!;
                if ((await definitions.ListVersionsByKeyAsync(workflowKey, cancellationToken)).Count == 0)
                {
                    throw new WorkflowDomainException($"No workflow definition has the key '{workflowKey}'.");
                }

                if (scope.Kind == SignalBroadcastScopeKind.BusinessKey
                    && !await definitions.IsBusinessKeyScopeActiveAsync(workflowKey, cancellationToken))
                {
                    throw new WorkflowDomainException(
                        $"Workflow '{workflowKey}' has not enabled business keys, so businessKey can match none of its instances.");
                }

                return scope;
            case SignalBroadcastScopeKind.Instance:
                var instanceId = scope.InstanceId!.Value;
                var family = await broadcasts.FindInstanceWorkflowKeyAsync(instanceId, cancellationToken)
                    ?? throw new WorkflowDomainException($"No workflow instance has the ID {instanceId}.");
                return scope.WithWorkflowKey(family);
            default:
                return scope;
        }
    }

    /// <summary>
    /// The catalog step of a throw (EVENT-04F), inside the throw's transaction: the shared lock of
    /// the name, then its entry, so the throw is checked against the entry that is committed. The
    /// mode is read only for a name without an entry.
    /// </summary>
    private async Task CheckCatalogForThrowAsync(
        string name,
        JsonElement? payload,
        SignalCaller caller,
        CancellationToken cancellationToken)
    {
        if (catalog is null)
        {
            return;
        }

        await catalog.LockNamesAsync([name], exclusive: false, cancellationToken);
        var entry = await catalog.GetAsync(name, cancellationToken);
        if (entry is null)
        {
            var setting = await engineSettings.GetByKeyAsync(SignalCatalogModes.SettingKey, cancellationToken);
            if (SignalCatalogModes.ParseMode(setting?.Value) == SignalCatalogModes.Required)
            {
                throw new WorkflowDomainException(
                    $"Signal '{name}' has no entry in the signal catalog; this deployment requires one.");
            }

            return;
        }

        if (!entry.IsActive)
        {
            throw ArchivedProblem(name);
        }

        if (SignalCatalogAccess.Refusal(entry, caller, SignalCatalogAccess.Throwing) is { } refusal)
        {
            throw new WorkflowForbiddenException(refusal, SignalCatalogAccess.ForbiddenCode);
        }

        if (SignalCatalogContract.PayloadProblem(entry, payload) is { } problem)
        {
            throw new WorkflowDomainException(problem);
        }
    }

    /// <summary>The catalog rules of a cancel or retry, read without a lock.</summary>
    private async Task CheckCatalogForCommandAsync(
        string name,
        SignalCaller caller,
        string verb,
        CancellationToken cancellationToken)
    {
        if (catalog is null || await catalog.GetAsync(name, cancellationToken) is not { } entry)
        {
            return;
        }

        if (verb == SignalCatalogAccess.Retrying && !entry.IsActive)
        {
            throw ArchivedProblem(name);
        }

        if (SignalCatalogAccess.Refusal(entry, caller, verb) is { } refusal)
        {
            throw new WorkflowForbiddenException(refusal, SignalCatalogAccess.ForbiddenCode);
        }
    }

    private static WorkflowDomainException ArchivedProblem(string name) =>
        new($"Signal '{name}' is archived in the signal catalog and cannot be thrown.");

    /// <summary>Whether the caller threw the broadcast: the same thrower kind and identity.</summary>
    private static bool IsThrower(SignalBroadcastRecord broadcast, SignalCaller caller) =>
        string.Equals(broadcast.ThrownByKind, caller.Kind, StringComparison.Ordinal)
        && string.Equals(broadcast.ThrownBy, caller.Id, StringComparison.Ordinal);

    private async Task<int> ReadLimitAsync(string key, int fallback, int cap, CancellationToken cancellationToken)
    {
        var setting = await engineSettings.GetByKeyAsync(key, cancellationToken);
        return SignalSettings.ParseLimit(setting?.Value, fallback, cap);
    }

    private static string? NormalizePayload(JsonElement? payload)
    {
        if (payload is not { } value
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new WorkflowDomainException("A signal payload must be a JSON object.");
        }

        if (JsonbText.ByteCount(value) > SignalConstraints.MaxPayloadBytes)
        {
            throw new WorkflowDomainException(
                $"A signal payload can have at most {SignalConstraints.MaxPayloadBytes} bytes.");
        }

        return value.GetRawText();
    }

    private static string? NormalizeIdempotencyKey(string? value)
    {
        var key = value?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (key.Length > SignalConstraints.MaxIdempotencyKeyLength)
        {
            throw new WorkflowDomainException(
                $"IdempotencyKey can have at most {SignalConstraints.MaxIdempotencyKeyLength} characters.");
        }

        return key;
    }

    /// <summary>
    /// The hash that decides whether a repeated idempotency key is the same request. A
    /// family or all-workflows request hashes exactly as before business-key and instance
    /// scopes existed, so keys stored earlier still replay; a business-key or instance
    /// scope adds a fourth line, with the key's length so that no key can imitate
    /// another line. The scope is the request's, before the instance's family is read. An
    /// expiry adds a last line with its parsed length in ticks, only when it is present, so a
    /// request without one hashes exactly as before and <c>PT60S</c> and <c>PT1M</c> are the
    /// same request.
    /// </summary>
    internal static byte[] HashRequest(
        string name,
        SignalBroadcastScope scope,
        string? payloadJson,
        TimeSpan? expiresAfter = null)
    {
        // The payload is compared as parsed JSON, so whitespace and member order in the
        // replayed request do not count as a different request.
        var canonicalPayload = payloadJson is null
            ? "null"
            : CanonicalJson(JsonDocument.Parse(payloadJson).RootElement);
        var lines = new List<string>(4)
        {
            name,
            scope.Kind == SignalBroadcastScopeKind.Instance ? "*" : scope.WorkflowKey ?? "*",
            canonicalPayload
        };
        if (scope.BusinessKey is { } businessKey)
        {
            lines.Add(FormattableString.Invariant($"businessKey:{businessKey.Length}:{businessKey}"));
        }
        else if (scope.InstanceId is long instanceId)
        {
            lines.Add(FormattableString.Invariant($"instance:{instanceId}"));
        }

        if (expiresAfter is TimeSpan expiry)
        {
            lines.Add(FormattableString.Invariant($"expiresAfter:{expiry.Ticks}"));
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
    }

    /// <summary>A UTC instant as ISO-8601 text with Z, without trailing zero fractions.</summary>
    private static string FormatInstant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string CanonicalJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(CanonicalJson)) + "]",
        _ => value.GetRawText()
    };

    private static SignalBroadcastDetailDto ReplayOrConflict(
        SignalBroadcastRecord earlier,
        byte[] requestHash,
        SignalCaller caller)
    {
        if (earlier.RequestHash is null
            || !CryptographicOperations.FixedTimeEquals(earlier.RequestHash, requestHash))
        {
            throw new WorkflowConflictException(
                "IdempotencyKey was already used for a different signal broadcast.");
        }

        return ToDetail(earlier, caller);
    }

    private static bool IsVisible(SignalBroadcastRecord broadcast, SignalCaller caller) =>
        !caller.IsClient
        || (string.Equals(broadcast.ThrownByKind, caller.Kind, StringComparison.Ordinal)
            && string.Equals(broadcast.ThrownBy, caller.Id, StringComparison.Ordinal));

    // The endpoints pass 50 when pageSize is omitted; a given value is clamped to 1-200.
    private static (int Page, int PageSize) ClampPage(int page, int pageSize) =>
        (Math.Max(1, page), Math.Clamp(pageSize, 1, 200));

    private static SignalBroadcastSummaryDto ToSummary(SignalBroadcastRecord broadcast) =>
        new(
            broadcast.Id,
            broadcast.SignalName,
            broadcast.WorkflowKey,
            broadcast.WorkflowKey is null,
            broadcast.BusinessKey,
            broadcast.InstanceId,
            broadcast.Status,
            broadcast.TotalDeliveryCount,
            broadcast.PendingCount,
            broadcast.DeliveredCount,
            broadcast.SkippedCount,
            broadcast.FailedCount,
            broadcast.CancelledCount,
            broadcast.ThrownBy,
            broadcast.ThrownByKind,
            broadcast.CreatedAt,
            broadcast.UpdatedAt,
            broadcast.CompletedAt,
            broadcast.ThrowerInstanceId,
            broadcast.ThrowerNodeId,
            broadcast.ThrowerNodeName,
            broadcast.StartDeliveryCount,
            broadcast.ExpiresAt,
            broadcast.ExpiredCount);

    private static readonly string[] KnownThrowerKinds =
        [SignalThrowerKinds.User, SignalThrowerKinds.Client, SignalThrowerKinds.Instance];

    private static SignalBroadcastDetailDto ToDetail(SignalBroadcastRecord broadcast, SignalCaller caller)
    {
        JsonElement? payload = null;
        if (broadcast.PayloadJson is not null)
        {
            using var document = JsonDocument.Parse(broadcast.PayloadJson);
            payload = document.RootElement.Clone();
        }

        var roles = JsonSerializer.Deserialize<string[]>(broadcast.ThrownByRolesJson, JsonOptions) ?? [];
        var recipients = JsonSerializer.Deserialize<SignalRecipientCountDto[]>(broadcast.RecipientsJson, JsonOptions)
            ?? [];
        // A managed client sees its own cancellation, but not which user cancelled its
        // broadcast or why: like a result description, that is the deployment's to read.
        var hideCancellation = caller.IsClient
            && !string.Equals(broadcast.CancelledBy, caller.Id, StringComparison.Ordinal);
        return new SignalBroadcastDetailDto(
            broadcast.Id,
            broadcast.SignalName,
            broadcast.WorkflowKey,
            broadcast.WorkflowKey is null,
            broadcast.BusinessKey,
            broadcast.InstanceId,
            broadcast.Status,
            broadcast.TotalDeliveryCount,
            broadcast.PendingCount,
            broadcast.DeliveredCount,
            broadcast.SkippedCount,
            broadcast.FailedCount,
            broadcast.CancelledCount,
            broadcast.ThrownBy,
            broadcast.ThrownByKind,
            roles,
            payload,
            recipients,
            broadcast.RecipientsTruncated,
            hideCancellation ? null : broadcast.CancelledBy,
            hideCancellation ? null : broadcast.CancellationReason,
            broadcast.CreatedAt,
            broadcast.UpdatedAt,
            broadcast.CompletedAt,
            broadcast.CancelledAt,
            broadcast.ThrowerInstanceId,
            broadcast.ThrowerNodeId,
            broadcast.ThrowerNodeName,
            broadcast.StartDeliveryCount,
            broadcast.ExpiresAt,
            broadcast.ExpiredCount);
    }

    private static SignalDeliveryDto ToDelivery(SignalDeliveryRecord delivery, SignalCaller caller) =>
        new(
            delivery.Id,
            delivery.BroadcastId,
            delivery.InstanceId,
            delivery.WorkflowKey,
            delivery.WorkflowDefinitionId,
            delivery.SignalNodeId,
            delivery.AttachedToNodeId,
            delivery.CancelActivity,
            delivery.Status,
            delivery.ResultCode,
            caller.IsClient ? null : delivery.ResultDescription,
            delivery.AttemptCount,
            delivery.CreatedAt,
            delivery.UpdatedAt,
            delivery.CompletedAt,
            delivery.Kind);
}
