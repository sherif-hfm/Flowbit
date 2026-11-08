using System.Collections.Concurrent;
using System.Security.Cryptography;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;

namespace Flowbit.Worker;

/// <summary>
/// Sends due webhook deliveries with at-least-once semantics and no ordering guarantee. Each send acquires a
/// lease in a short transaction, records its send intent, signs and sends with no database connection held, and
/// finalizes only while its lease fence still holds. Its concurrency is separate from workflow jobs, so dead
/// receivers cannot starve timers or call results; receiver failure never changes workflow state.
/// </summary>
public sealed class WebhookDispatcher(
    IServiceScopeFactory scopeFactory,
    IWebhookTransport transport,
    IWebhookSecretProtector protector,
    WebhookDestinationPolicy destinationPolicy,
    PostgresLifecycleWakeupSignal wakeup,
    WebhookOptions options,
    WorkerOptions workerOptions,
    WebhookTelemetry telemetry,
    ILogger<WebhookDispatcher> logger,
    DatabaseSchemaGate? schemaGate = null) : BackgroundService
{
    private const int MaxSubscriptionsPerAcquisition = 100;
    private const int FinalizeAttempts = 3;

    private readonly ConcurrentDictionary<Guid, Task> running = new();
    // Sends observe this source rather than the host token, so shutdown stops
    // acquisition while in-flight sends may still finish during the drain.
    private readonly CancellationTokenSource hardStop = new();
    private readonly string workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.DeliveryEnabled)
        {
            logger.LogInformation(
                "Webhook delivery is disabled on this Worker (Webhooks:DeliveryEnabled=false); capture and queued work are preserved.");
            return;
        }
        try
        {
            if (schemaGate is not null)
            {
                await schemaGate.WaitUntilReadyAsync(stoppingToken);
            }
            logger.LogInformation(
                "Webhook dispatcher {WorkerId} started with {Sends} concurrent sends.",
                workerId,
                options.MaxConcurrentSends);
            while (!stoppingToken.IsCancellationRequested)
            {
                Reap();
                var wait = options.PollInterval;
                if (!protector.IsAvailable)
                {
                    // Invalid key configuration blocks delivery; readiness reports it as degraded.
                    await wakeup.WaitForDispatchAsync(options.PollInterval, stoppingToken);
                    continue;
                }
                var free = options.MaxConcurrentSends - running.Count;
                if (free > 0)
                {
                    try
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        var repository = scope.ServiceProvider.GetRequiredService<IWebhookDispatchRepository>();
                        var leases = await repository.AcquireAsync(
                            new WebhookAcquireRequest(workerId, free, options.LeaseDuration, MaxSubscriptionsPerAcquisition),
                            stoppingToken);
                        foreach (var lease in leases)
                        {
                            running[lease.DeliveryId] = Task.Run(() => DeliverAsync(lease, stoppingToken), CancellationToken.None);
                        }
                        if (leases.Count == 0)
                        {
                            // Due work that was not acquired is held by a concurrency limit, a probe in flight,
                            // or another Worker. A finishing send signals this loop, so it waits a full poll.
                            if (await repository.GetTimeUntilNextDueAsync(stoppingToken) is { } until
                                && until > TimeSpan.Zero
                                && until < wait)
                            {
                                wait = until;
                            }
                        }
                        else if (leases.Count == free)
                        {
                            wait = TimeSpan.FromMilliseconds(50);
                        }
                        telemetry.RecordLoopSuccess(WebhookTelemetry.DispatchLoop);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        telemetry.RecordLoopFailure(WebhookTelemetry.DispatchLoop);
                        logger.LogError(exception, "Webhook delivery acquisition failed; it will be retried.");
                    }
                }
                await wakeup.WaitForDispatchAsync(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        await DrainAsync();
    }

    public override void Dispose()
    {
        hardStop.Dispose();
        base.Dispose();
    }

    private async Task DrainAsync()
    {
        var drain = TimeSpan.FromSeconds(Math.Min(workerOptions.ShutdownDrainSeconds, options.HttpTimeoutSeconds + 5));
        var pending = running.Values.ToArray();
        if (pending.Length == 0)
        {
            return;
        }
        var all = Task.WhenAll(pending);
        if (await Task.WhenAny(all, Task.Delay(drain)) != all)
        {
            // Unfinished sends end with an unknown outcome; the lease fence recovers them.
            await hardStop.CancelAsync();
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5)));
        }
    }

    private void Reap()
    {
        foreach (var pair in running)
        {
            if (pair.Value.IsCompleted)
            {
                running.TryRemove(pair.Key, out _);
            }
        }
    }

    private async Task DeliverAsync(WebhookDeliveryLease lease, CancellationToken stoppingToken)
    {
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(hardStop.Token);
        using var heartbeatStop = new CancellationTokenSource();
        var keys = new List<byte[]>();
        Task? heartbeat = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IWebhookDispatchRepository>();
            heartbeat = HeartbeatAsync(lease, leaseLost, heartbeatStop.Token);

            string? permanentFailure = null;
            var keyIds = new List<Guid>();
            var protectedKeys = await repository.LoadSigningKeysAsync(lease.SubscriptionId, hardStop.Token);
            if (protectedKeys.Count == 0 || protectedKeys[0].Status != WebhookSigningKeyStatuses.Current)
            {
                permanentFailure = WebhookOutcomeCodes.SigningUnavailable;
            }
            else
            {
                try
                {
                    foreach (var key in protectedKeys)
                    {
                        keys.Add(protector.Unprotect(key.ProtectedSecret));
                        keyIds.Add(key.KeyId);
                    }
                    telemetry.RecordLoopSuccess(WebhookTelemetry.SigningLoop);
                }
                catch (WebhookKeyProtectionUnavailableException)
                {
                    // Key protection is unavailable for now: release the lease without spending budget, so this
                    // or another Worker signs it once its certificate and key ring can be read again.
                    telemetry.RecordLoopFailure(WebhookTelemetry.SigningLoop);
                    logger.LogWarning(
                        "Webhook delivery {DeliveryId} was returned to the queue because key protection is unavailable.",
                        lease.DeliveryId);
                    await repository.ReleaseBeforeSendAsync(lease, hardStop.Token);
                    return;
                }
                catch (CryptographicException exception)
                {
                    // Even a freshly loaded key ring cannot decrypt this subscription's key, so only rotating
                    // its secret helps; the failed delivery can be retried after that.
                    telemetry.RecordLoopFailure(WebhookTelemetry.SigningLoop);
                    logger.LogError(
                        "Webhook delivery {DeliveryId} cannot be signed: its subscription's keys cannot be unprotected ({FailureType}).",
                        lease.DeliveryId,
                        exception.GetType().Name);
                    permanentFailure = WebhookOutcomeCodes.SigningUnavailable;
                }
            }
            // The operator's policy may have tightened since the destination was saved.
            if (permanentFailure is null && !destinationPolicy.TryValidateUrl(lease.EndpointUrl, out _, out _))
            {
                permanentFailure = WebhookOutcomeCodes.DestinationDenied;
            }
            if (stoppingToken.IsCancellationRequested)
            {
                await repository.ReleaseBeforeSendAsync(lease, hardStop.Token);
                return;
            }
            var decision = await repository.PrepareSendAsync(lease, keyIds, permanentFailure, hardStop.Token);
            if (decision.Kind != WebhookSendDecisionKind.Send)
            {
                if (permanentFailure is not null && decision.Kind == WebhookSendDecisionKind.Settled)
                {
                    telemetry.RecordSettledWithoutSend(permanentFailure);
                }
                return;
            }

            // Prepare-send drops a key revoked or expired since it was loaded.
            var signing = decision.SigningKeyIds is { } usable
                ? keyIds.Select((id, index) => (id, index)).Where(pair => usable.Contains(pair.id)).Select(pair => keys[pair.index]).ToArray()
                : keys.ToArray();
            WebhookSendOutcome outcome;
            telemetry.SendStarted();
            try
            {
                outcome = await transport.SendAsync(
                    new WebhookSendRequest(new Uri(lease.EndpointUrl), lease.EventId, lease.Body, signing, options.HttpTimeout),
                    leaseLost.Token);
            }
            catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
            {
                if (!hardStop.IsCancellationRequested)
                {
                    // Another Worker may own this delivery now; recovery settles the attempt.
                    return;
                }
                outcome = new WebhookSendOutcome(WebhookOutcomeCodes.OutcomeUnknown, null, null, 0);
            }
            finally
            {
                telemetry.SendFinished();
            }
            var result = await FinalizeAsync(repository, lease, outcome);
            telemetry.RecordAttempt(outcome, result);
            telemetry.RecordLoopSuccess(WebhookTelemetry.DeliveryLoop);
            if (result.AutomaticallyPaused)
            {
                telemetry.RecordAutomaticPause(outcome.Outcome == WebhookOutcomeCodes.ReceiverGone
                    ? WebhookPauseReasons.ReceiverGone
                    : WebhookPauseReasons.ReceiverUnavailable);
                logger.LogWarning(
                    "Webhook subscription {SubscriptionId} was paused automatically after outcome {Outcome}.",
                    lease.SubscriptionId,
                    outcome.Outcome);
            }
            if (!result.Applied)
            {
                logger.LogInformation(
                    "Webhook delivery {DeliveryId} lost its lease before finalization; the newer owner's outcome stands.",
                    lease.DeliveryId);
            }
        }
        catch (OperationCanceledException) when (hardStop.IsCancellationRequested)
        {
            // Shutdown: the lease expires and recovery returns the delivery to the queue.
        }
        catch (Exception exception)
        {
            telemetry.RecordLoopFailure(WebhookTelemetry.DeliveryLoop);
            logger.LogError(
                "Webhook delivery {DeliveryId} failed unexpectedly ({FailureType}); its lease will be recovered.",
                lease.DeliveryId,
                exception.GetType().Name);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            if (heartbeat is not null)
            {
                await heartbeat;
            }
            foreach (var key in keys)
            {
                Array.Clear(key);
            }
            wakeup.SignalDispatcher();
        }
    }

    // A brief database failure must not turn a request the receiver accepted into an unknown outcome that is
    // sent again; the lease fence still rejects a retry once another Worker owns the delivery.
    private async Task<WebhookFinalizeResult> FinalizeAsync(
        IWebhookDispatchRepository repository,
        WebhookDeliveryLease lease,
        WebhookSendOutcome outcome)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await repository.FinalizeAsync(lease, outcome, CancellationToken.None);
            }
            catch (Exception exception) when (attempt < FinalizeAttempts && exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Finalizing webhook delivery {DeliveryId} failed ({FailureType}); retrying.",
                    lease.DeliveryId,
                    exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(attempt), CancellationToken.None);
            }
        }
    }

    // Lease loss cancels local I/O. A failed renewal is retried at the next interval while the lease lasts.
    private async Task HeartbeatAsync(WebhookDeliveryLease lease, CancellationTokenSource leaseLost, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(options.HeartbeatInterval, stop);
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var repository = scope.ServiceProvider.GetRequiredService<IWebhookDispatchRepository>();
                    if (!await repository.HeartbeatAsync(lease, options.LeaseDuration, stop))
                    {
                        await leaseLost.CancelAsync();
                        return;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(
                        "Webhook lease heartbeat for {DeliveryId} failed ({FailureType}); the next heartbeat retries, and the lease fence still protects its outcome.",
                        lease.DeliveryId,
                        exception.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The delivery finished.
        }
    }
}
