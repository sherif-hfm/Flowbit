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
/// Every delivery runs in its own scope and transaction.
/// </summary>
public sealed class SignalBroadcastJobProcessor(
    IServiceScopeFactory scopeFactory,
    IWorkflowJobRepository jobs,
    TimeProvider timeProvider,
    ILogger<SignalBroadcastJobProcessor> logger)
    : ISignalBroadcastJobProcessor
{
    private const int DeliveryPageSize = 100;

    public async Task ProcessAsync(
        WorkflowJobLeaseRecord lease,
        CancellationToken cancellationToken)
    {
        if (lease.Job.Kind != WorkflowJobKinds.SignalBroadcastDeliver)
        {
            throw new WorkflowConflictException($"Job #{lease.Job.Id} is not a signal delivery job.");
        }

        var payload = lease.Job.Payload?.Deserialize<SignalBroadcastJobPayload>();
        if (payload is null || payload.BroadcastId <= 0 || payload.WorkflowDefinitionId <= 0)
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
        long afterId = 0;
        Exception? firstUnexpectedFailure = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<long> page;
            await using (var pageScope = scopeFactory.CreateAsyncScope())
            {
                page = await pageScope.ServiceProvider
                    .GetRequiredService<ISignalBroadcastRepository>()
                    .ListPendingDeliveryIdsAsync(
                        payload.BroadcastId,
                        payload.WorkflowDefinitionId,
                        afterId,
                        DeliveryPageSize,
                        cancellationToken);
            }

            if (page.Count == 0)
            {
                break;
            }

            foreach (var deliveryId in page)
            {
                var unexpected = await DeliverAsync(deliveryId, isFinalAttempt, cancellationToken);
                firstUnexpectedFailure ??= unexpected;
            }

            afterId = page[^1];
            await RefreshCountsAsync(payload.BroadcastId, cancellationToken);
        }

        await RefreshCountsAsync(payload.BroadcastId, cancellationToken);
        if (firstUnexpectedFailure is not null && !isFinalAttempt)
        {
            // A delivery that failed unexpectedly stays pending, and the job retries it.
            ExceptionDispatchInfo.Capture(firstUnexpectedFailure).Throw();
        }

        await jobs.CompleteAsync(Fence(lease), cancellationToken);
    }

    private async Task<Exception?> DeliverAsync(
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
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SignalPayloadException exception)
        {
            await RecordAsync(
                deliveryId,
                SignalDeliveryStatuses.Failed,
                SignalDeliveryResultCodes.PayloadInvalid,
                exception.Message,
                cancellationToken);
            return null;
        }
        catch (SignalTokenLimitException exception)
        {
            await RecordAsync(
                deliveryId,
                SignalDeliveryStatuses.Failed,
                SignalDeliveryResultCodes.TokenLimit,
                exception.Message,
                cancellationToken);
            return null;
        }
        catch (WorkflowDomainException exception)
        {
            await RecordAsync(
                deliveryId,
                SignalDeliveryStatuses.Failed,
                SignalDeliveryResultCodes.DeliveryRejected,
                exception.Message,
                cancellationToken);
            return null;
        }
        catch (Exception exception) when (exception is SignalDeliveryInvariantException or WorkflowCallChainException)
        {
            // The recipient's durable state is inconsistent, so retrying cannot help. Only this
            // delivery fails: an incident on the job would hold back every other recipient.
            logger.LogError(
                exception,
                "Signal delivery {DeliveryId} found inconsistent durable state.",
                deliveryId);
            await RecordAsync(
                deliveryId,
                SignalDeliveryStatuses.Failed,
                SignalDeliveryResultCodes.DeliveryError,
                exception.Message,
                cancellationToken);
            return null;
        }
        catch (WorkflowConflictException exception)
        {
            await RecordAsync(
                deliveryId,
                SignalDeliveryStatuses.Skipped,
                SignalDeliveryResultCodes.Stale,
                exception.Message,
                cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Signal delivery {DeliveryId} failed unexpectedly.",
                deliveryId);
            if (isFinalAttempt)
            {
                await RecordAsync(
                    deliveryId,
                    SignalDeliveryStatuses.Failed,
                    SignalDeliveryResultCodes.DeliveryError,
                    "The delivery failed unexpectedly on every attempt; see the Worker log.",
                    cancellationToken);
                return null;
            }
            return exception;
        }
    }

    private async Task RecordAsync(
        long deliveryId,
        string status,
        string resultCode,
        string description,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var broadcasts = scope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var finished = await broadcasts.CompleteDeliveryAsync(
            deliveryId,
            status,
            resultCode,
            UnicodeText.Limit(description, SignalConstraints.MaxResultDescriptionLength),
            timeProvider.GetUtcNow(),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (finished)
        {
            SignalRuntimeTelemetry.RecordDelivery(status, resultCode);
        }
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
