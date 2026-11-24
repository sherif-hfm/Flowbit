using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

/// <summary>
/// Settles an expired broadcast (EVENT-04E): in one transaction it locks the broadcast row,
/// ends every pending delivery as skipped with <c>broadcast_expired</c> in one statement whose
/// condition compares the expiry with the database's clock, and recounts, which derives the
/// status <c>expired</c>. It locks no instance, so it never waits for one. A delivery inside its
/// own transaction holds its row, so the statement waits for it and leaves it as it finished.
/// A second settle finds the status changed and does nothing; one before the expiry ends nothing.
/// </summary>
public sealed class SignalBroadcastExpirer(
    ISignalBroadcastRepository broadcasts,
    IUnitOfWork unitOfWork,
    ILogger<SignalBroadcastExpirer> logger)
    : ISignalBroadcastExpirer
{
    public async Task<SignalExpiryResult?> ExpireAsync(long broadcastId, CancellationToken cancellationToken)
    {
        SignalExpiryResult? result;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var broadcast = await broadcasts.GetAsync(broadcastId, forUpdate: true, cancellationToken);
            result = broadcast is null || broadcast.Status != SignalBroadcastStatuses.Delivering
                ? null
                : await SettleLockedAsync(broadcastId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        Report(result);
        return result;
    }

    public async Task<SignalExpiryResult?> ExpireNextDueAsync(
        DateTimeOffset databaseNow,
        CancellationToken cancellationToken)
    {
        SignalExpiryResult? result = null;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            if (await broadcasts.ClaimNextExpiredAsync(databaseNow, cancellationToken) is long broadcastId)
            {
                result = await SettleLockedAsync(broadcastId, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        Report(result);
        return result;
    }

    private async Task<SignalExpiryResult> SettleLockedAsync(long broadcastId, CancellationToken cancellationToken)
    {
        // One reading of the database's clock for the rows the statement ends and for the
        // recount; the statement itself decides with clock_timestamp().
        var databaseNow = await broadcasts.GetDatabaseTimeAsync(cancellationToken);
        var ended = await broadcasts.ExpirePendingDeliveriesAsync(broadcastId, databaseNow, cancellationToken);
        var updated = await broadcasts.RefreshCountsAsync(broadcastId, databaseNow, cancellationToken);
        return new SignalExpiryResult(broadcastId, ended, updated?.Status ?? SignalBroadcastStatuses.Delivering);
    }

    // After the commit: the metric and the log line describe only what was written.
    private void Report(SignalExpiryResult? result)
    {
        if (result is null || result.ExpiredDeliveries == 0)
        {
            return;
        }

        SignalRuntimeTelemetry.RecordDeliveries(
            SignalDeliveryStatuses.Skipped,
            SignalDeliveryResultCodes.BroadcastExpired,
            result.ExpiredDeliveries);
        logger.LogInformation(
            "Signal broadcast {BroadcastId} expired; {ExpiredDeliveryCount} pending deliveries were skipped.",
            result.BroadcastId, result.ExpiredDeliveries);
    }
}
