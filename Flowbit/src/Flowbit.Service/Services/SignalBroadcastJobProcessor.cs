using System.Runtime.ExceptionServices;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

/// <summary>
/// Delivers the pending deliveries of one broadcast for one workflow definition, in pages
/// of 100 in ascending id with a cursor, so a failing delivery cannot starve later ones.
/// A share job of a split definition (EVENT-04G) delivers only the deliveries whose instance
/// falls in its share, and recounts at most every two seconds instead of after every page.
/// Every delivery runs in its own scope and transaction. Before each page, and when a
/// delivery's own check finds the broadcast expired, it settles the whole broadcast through
/// <see cref="ISignalBroadcastExpirer"/> instead of delivering further (EVENT-04E).
/// </summary>
public sealed class SignalBroadcastJobProcessor(
    IServiceScopeFactory scopeFactory,
    IWorkflowJobRepository jobs,
    TimeProvider timeProvider,
    ILogger<SignalBroadcastJobProcessor> logger)
    : ISignalBroadcastJobProcessor
{
    private const int DeliveryPageSize = 100;

    /// <summary>
    /// How often a share job recounts while it delivers. Each recount counts every delivery of
    /// the broadcast under its row lock, so several jobs recounting after every page would
    /// recount a large broadcast several times a second; the Signals page polls every three.
    /// </summary>
    internal static readonly TimeSpan ShareRecountInterval = TimeSpan.FromSeconds(2);

    /// <summary>The deliveries read per page; tests use a smaller page to reach several pages.</summary>
    internal int PageSize { get; init; } = DeliveryPageSize;

    public async Task ProcessAsync(
        WorkflowJobLeaseRecord lease,
        CancellationToken cancellationToken)
    {
        if (lease.Job.Kind != WorkflowJobKinds.SignalBroadcastDeliver)
        {
            throw new WorkflowConflictException($"Job #{lease.Job.Id} is not a signal delivery job.");
        }

        var payload = lease.Job.Payload?.Deserialize<SignalBroadcastJobPayload>();
        if (payload is null || payload.BroadcastId <= 0 || payload.WorkflowDefinitionId <= 0
            || !SignalDeliveryShares.IsValid(payload.Share, payload.Shares))
        {
            await jobs.OpenIncidentAsync(
                Fence(lease),
                "invalid_signal_broadcast_payload",
                $"Signal delivery job #{lease.Job.Id} has no valid broadcast identity.",
                null,
                cancellationToken);
            return;
        }

        var isFinalAttempt = lease.AttemptNumber >= lease.Job.MaxAttempts;
        var isShare = payload.Shares > 1;
        var lastRecount = timeProvider.GetTimestamp();
        long afterId = 0;
        Exception? firstUnexpectedFailure = null;
        var settled = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<long> page;
            var expired = false;
            await using (var pageScope = scopeFactory.CreateAsyncScope())
            {
                var broadcasts = pageScope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>();
                // EVENT-04E: once the expiry has passed, one statement under the broadcast's
                // row lock ends every pending delivery, so a Worker that returns after an
                // outage locks no instance for an expired broadcast.
                expired = await broadcasts.IsExpiredAsync(payload.BroadcastId, cancellationToken);
                page = expired
                    ? []
                    : await broadcasts.ListPendingDeliveryIdsAsync(
                        payload.BroadcastId,
                        payload.WorkflowDefinitionId,
                        afterId,
                        PageSize,
                        cancellationToken,
                        payload.Share,
                        payload.Shares);
            }

            if (expired)
            {
                await ExpireAsync(payload.BroadcastId, cancellationToken);
                settled = true;
                break;
            }

            if (page.Count == 0)
            {
                break;
            }

            foreach (var deliveryId in page)
            {
                var (unexpected, outcome) = await DeliverAsync(deliveryId, isFinalAttempt, cancellationToken);
                firstUnexpectedFailure ??= unexpected;
                if (outcome?.ResultCode == SignalDeliveryResultCodes.BroadcastExpired)
                {
                    // The delivery's own check found the expiry passed: settle the rest now.
                    expired = true;
                    break;
                }
            }

            if (expired)
            {
                await ExpireAsync(payload.BroadcastId, cancellationToken);
                settled = true;
                break;
            }

            afterId = page[^1];
            if (!isShare || timeProvider.GetElapsedTime(lastRecount) >= ShareRecountInterval)
            {
                await RefreshCountsAsync(payload.BroadcastId, cancellationToken);
                lastRecount = timeProvider.GetTimestamp();
            }
        }

        await RefreshCountsAsync(payload.BroadcastId, cancellationToken);
        if (firstUnexpectedFailure is not null && !isFinalAttempt && !settled)
        {
            // A delivery that failed unexpectedly stays pending, and the job retries it. After a
            // settle no delivery of the broadcast is pending (the settle ended the one that
            // failed), so a retry would find nothing to do: the job completes instead.
            ExceptionDispatchInfo.Capture(firstUnexpectedFailure).Throw();
        }

        await jobs.CompleteAsync(Fence(lease), cancellationToken);
    }

    private async Task<(Exception? Unexpected, SignalDeliveryOutcome? Outcome)> DeliverAsync(
        long deliveryId,
        bool isFinalAttempt,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var outcome = await scope.ServiceProvider
                .GetRequiredService<ISignalDeliveryExecutor>()
                .DeliverSignalAsync(deliveryId, cancellationToken);
            SignalRuntimeTelemetry.RecordDelivery(outcome.Status, outcome.ResultCode);
            return (null, outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SignalStartOutcomeException exception)
        {
            var recorded = await RecordAsync(
                deliveryId,
                exception.Status,
                exception.ResultCode,
                exception.Message,
                cancellationToken,
                exception.WorkflowDefinitionId,
                exception.SignalNodeId);
            return (null, recorded);
        }
        catch (SignalPayloadException exception)
        {
            var recorded = await RecordAsync(
                deliveryId,
                SignalDeliveryStatuses.Failed,
                SignalDeliveryResultCodes.PayloadInvalid,
                exception.Message,
                cancellationToken);
            return (null, recorded);
        }
        catch (SignalTokenLimitException exception)
        {
            var recorded = await RecordAsync(
                deliveryId,
                SignalDeliveryStatuses.Failed,
                SignalDeliveryResultCodes.TokenLimit,
                exception.Message,
                cancellationToken);
            return (null, recorded);
        }
        catch (WorkflowDomainException exception)
        {
            var recorded = await RecordAsync(
                deliveryId,
                SignalDeliveryStatuses.Failed,
                SignalDeliveryResultCodes.DeliveryRejected,
                exception.Message,
                cancellationToken);
            return (null, recorded);
        }
        catch (Exception exception) when (exception is SignalDeliveryInvariantException or WorkflowCallChainException)
        {
            // The recipient's durable state is inconsistent, so retrying cannot help. Only this
            // delivery fails: an incident on the job would hold back every other recipient.
            logger.LogError(
                exception,
                "Signal delivery {DeliveryId} found inconsistent durable state.",
                deliveryId);
            var recorded = await RecordAsync(
                deliveryId,
                SignalDeliveryStatuses.Failed,
                SignalDeliveryResultCodes.DeliveryError,
                exception.Message,
                cancellationToken);
            return (null, recorded);
        }
        catch (WorkflowConflictException exception)
        {
            var recorded = await RecordAsync(
                deliveryId,
                SignalDeliveryStatuses.Skipped,
                SignalDeliveryResultCodes.Stale,
                exception.Message,
                cancellationToken);
            return (null, recorded);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Signal delivery {DeliveryId} failed unexpectedly.",
                deliveryId);
            if (isFinalAttempt)
            {
                var recorded = await RecordAsync(
                    deliveryId,
                    SignalDeliveryStatuses.Failed,
                    SignalDeliveryResultCodes.DeliveryError,
                    "The delivery failed unexpectedly on every attempt; see the Worker log.",
                    cancellationToken);
                return (null, recorded);
            }
            return (exception, null);
        }
    }

    /// <summary>
    /// Finishes a delivery that is still pending, and returns the outcome it wrote, or null when
    /// the delivery had ended already. A start that tried a version and node gives them, so the
    /// row names what its description is about. A delivery of a broadcast whose expiry has passed
    /// by now ends as expired instead (EVENT-04E): its failure was rolled back, so it wrote
    /// nothing, and after the expiry no delivery gets another outcome.
    /// </summary>
    private async Task<SignalDeliveryOutcome?> RecordAsync(
        long deliveryId,
        string status,
        string resultCode,
        string description,
        CancellationToken cancellationToken,
        long? startDefinitionId = null,
        int? startNodeId = null)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var broadcasts = scope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var delivery = await broadcasts.GetDeliveryForUpdateAsync(deliveryId, cancellationToken);
        if (delivery is null || delivery.Status != SignalDeliveryStatuses.Pending)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (await broadcasts.IsExpiredAsync(delivery.BroadcastId, cancellationToken))
        {
            var expired = await broadcasts.ExpireDeliveryAsync(deliveryId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (!expired)
            {
                return null;
            }

            SignalRuntimeTelemetry.RecordDelivery(SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.BroadcastExpired);
            return new SignalDeliveryOutcome(SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.BroadcastExpired, null);
        }

        var limited = UnicodeText.Limit(description, SignalConstraints.MaxResultDescriptionLength);
        var now = timeProvider.GetUtcNow();
        var finished = startDefinitionId is long definitionId && startNodeId is int nodeId
            ? await broadcasts.CompleteStartWithoutInstanceAsync(
                deliveryId,
                status,
                resultCode,
                limited,
                definitionId,
                nodeId,
                now,
                cancellationToken)
            : await broadcasts.CompleteDeliveryAsync(
                deliveryId,
                status,
                resultCode,
                limited,
                now,
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (!finished)
        {
            return null;
        }

        SignalRuntimeTelemetry.RecordDelivery(status, resultCode);
        return new SignalDeliveryOutcome(status, resultCode, limited);
    }

    /// <summary>
    /// Ends every pending delivery of an expired broadcast in one statement under its row
    /// lock, in a scope of its own, and finishes the broadcast as expired.
    /// </summary>
    private async Task ExpireAsync(long broadcastId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<ISignalBroadcastExpirer>()
            .ExpireAsync(broadcastId, cancellationToken);
    }

    /// <summary>
    /// Recounts under the broadcast's row lock, as cancel and retry do, so the recount reads
    /// the status they committed and cannot overwrite it with an older one.
    /// </summary>
    private async Task RefreshCountsAsync(long broadcastId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var broadcasts = scope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (await broadcasts.GetAsync(broadcastId, forUpdate: true, cancellationToken) is null)
        {
            return;
        }

        await broadcasts.RefreshCountsAsync(broadcastId, timeProvider.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static WorkflowJobFence Fence(WorkflowJobLeaseRecord lease) =>
        new(
            lease.Job.Id,
            lease.Job.WorkerId
                ?? throw new WorkflowConflictException("A leased workflow job has no worker id."),
            lease.LeaseToken,
            lease.LeaseGeneration);
}

/// <summary>
/// An active subscription whose token is not waiting where it says: every path that ends a
/// wait cancels its subscriptions, so this is an invariant failure. The delivery fails at
/// once instead of retrying.
/// </summary>
public sealed class SignalDeliveryInvariantException(string message) : InvalidOperationException(message);

/// <summary>
/// A non-interrupting signal boundary would add a token beyond
/// <c>Workflow.Gateway.MaxActiveTokens</c>: the delivery fails and the boundary stays
/// subscribed.
/// </summary>
public sealed class SignalTokenLimitException(string message) : InvalidOperationException(message);
