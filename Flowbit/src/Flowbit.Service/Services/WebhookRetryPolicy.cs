using System.Globalization;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

public sealed record WebhookRetryDecision(string State, DateTimeOffset? DueAt, string? FailureCode);

/// <summary>A subscription's receiver health after one attempt.</summary>
public sealed record WebhookReceiverState(
    int ConsecutiveFailures,
    DateTimeOffset? BackoffStartedAt,
    DateTimeOffset? BackoffUntil,
    int BackoffSeconds,
    bool AutomaticallyPause);

/// <summary>
/// The pure acknowledgement, retry, and receiver-backoff rules. Callers persist the chosen times, so a restart
/// never recalculates them.
/// </summary>
public static class WebhookRetryPolicy
{
    public static string ClassifyStatus(int status) => status switch
    {
        >= 200 and < 300 => WebhookOutcomeCodes.Succeeded,
        >= 300 and < 400 => WebhookOutcomeCodes.RedirectRefused,
        410 => WebhookOutcomeCodes.ReceiverGone,
        408 or 425 or 429 or >= 500 => WebhookOutcomeCodes.HttpRetryable,
        _ => WebhookOutcomeCodes.ReceiverRejected
    };

    /// <summary>Outcomes retried within the generation's budget.</summary>
    public static bool IsRetryable(string outcome) => outcome is WebhookOutcomeCodes.HttpRetryable
        or WebhookOutcomeCodes.Timeout
        or WebhookOutcomeCodes.NetworkError
        or WebhookOutcomeCodes.OutcomeUnknown;

    /// <summary>Outcomes that count toward subscription backoff. An unknown outcome proves nothing about the receiver.</summary>
    public static bool CountsTowardBackoff(string outcome) => outcome is WebhookOutcomeCodes.HttpRetryable
        or WebhookOutcomeCodes.Timeout
        or WebhookOutcomeCodes.NetworkError;

    /// <summary>A random factor between 0.9 and 1.1, as the workflow-job dispatcher uses.</summary>
    public static double NextJitter() => 0.9 + (Random.Shared.NextDouble() * 0.2);

    /// <summary>The delay before retry <paramref name="retry"/> (1-based) of a generation, before jitter.</summary>
    public static TimeSpan RetryDelay(int retry)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retry, 1);
        var delays = WebhookConstraints.RetryDelays;
        return delays[Math.Min(retry, delays.Count) - 1];
    }

    /// <summary>
    /// What follows one attempt. <paramref name="generationAttempts"/> already counts this attempt's send intent.
    /// A 429 or 503 Retry-After hint is a lower bound capped at 24 hours, and a retry that would start after the
    /// generation deadline fails instead.
    /// </summary>
    public static WebhookRetryDecision Decide(
        string outcome,
        int generationAttempts,
        DateTimeOffset deadlineAt,
        TimeSpan? retryAfter,
        DateTimeOffset now,
        double jitter)
    {
        if (outcome == WebhookOutcomeCodes.Succeeded)
        {
            return new(WebhookDeliveryStates.Succeeded, null, null);
        }
        if (!IsRetryable(outcome))
        {
            return new(WebhookDeliveryStates.Failed, null, outcome);
        }
        if (generationAttempts >= WebhookConstraints.MaxAttemptsPerGeneration)
        {
            return new(WebhookDeliveryStates.Failed, null, WebhookOutcomeCodes.AttemptsExhausted);
        }
        var delay = TimeSpan.FromTicks((long)(RetryDelay(generationAttempts).Ticks * jitter));
        var hinted = retryAfter is { } hint && hint > TimeSpan.Zero;
        if (hinted)
        {
            var bound = retryAfter!.Value > WebhookConstraints.MaxRetryAfter
                ? WebhookConstraints.MaxRetryAfter
                : retryAfter.Value;
            if (bound > delay)
            {
                delay = bound;
            }
        }
        var dueAt = now + delay;
        if (dueAt > deadlineAt)
        {
            return new(
                WebhookDeliveryStates.Failed,
                null,
                hinted ? WebhookOutcomeCodes.RetryAfterBeyondDeadline : WebhookOutcomeCodes.DeadlineExceeded);
        }
        return new(WebhookDeliveryStates.RetryScheduled, dueAt, null);
    }

    /// <summary>
    /// Updates receiver backoff. Five consecutive retryable outcomes, or a valid Retry-After, start backoff at
    /// 30 seconds; each failed probe, a request sent after backoff started, doubles it to a one-hour cap, while a
    /// request already in flight counts as a failure without extending it; a success ends it. After 72 hours of
    /// continuous backoff the subscription pauses automatically. When nothing probed the receiver for more than an
    /// hour after the backoff window ended, as when the queue emptied, the next failure starts a new backoff.
    /// </summary>
    public static WebhookReceiverState NextReceiverState(
        WebhookReceiverState current,
        string outcome,
        TimeSpan? retryAfter,
        DateTimeOffset now,
        DateTimeOffset sentAt)
    {
        if (outcome == WebhookOutcomeCodes.Succeeded)
        {
            return new(0, null, null, 0, false);
        }
        if (!CountsTowardBackoff(outcome))
        {
            return current with { AutomaticallyPause = false };
        }

        var failures = current.ConsecutiveFailures + 1;
        var startedAt = current.BackoffStartedAt;
        var seconds = current.BackoffSeconds;
        DateTimeOffset? until = current.BackoffUntil;
        var hinted = retryAfter is { } hint && hint > TimeSpan.Zero;
        if (startedAt is not null && (until is null || now - until.Value > WebhookConstraints.MaxBackoff))
        {
            // The backoff lapsed without probes, so this failure proves nothing continuous.
            startedAt = now;
            seconds = (int)WebhookConstraints.InitialBackoff.TotalSeconds;
            until = now.AddSeconds(seconds);
        }
        else if (startedAt is not null)
        {
            if (sentAt >= startedAt.Value)
            {
                // A probe failed while backing off.
                seconds = seconds <= 0
                    ? (int)WebhookConstraints.InitialBackoff.TotalSeconds
                    : Math.Min(seconds * 2, (int)WebhookConstraints.MaxBackoff.TotalSeconds);
                until = now.AddSeconds(seconds);
            }
        }
        else if (failures >= WebhookConstraints.BackoffFailureThreshold || hinted)
        {
            startedAt = now;
            seconds = (int)WebhookConstraints.InitialBackoff.TotalSeconds;
            until = now.AddSeconds(seconds);
        }
        if (hinted && startedAt is not null)
        {
            var bound = retryAfter!.Value > WebhookConstraints.MaxRetryAfter
                ? WebhookConstraints.MaxRetryAfter
                : retryAfter.Value;
            if (until is null || now + bound > until)
            {
                until = now + bound;
            }
        }
        var pause = startedAt is not null && now - startedAt.Value >= WebhookConstraints.AutomaticPauseAfter;
        return new(failures, startedAt, until, seconds, pause);
    }

    /// <summary>
    /// Parses a Retry-After value: delay seconds or an HTTP date. Invalid, negative, or past values are ignored.
    /// </summary>
    public static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var trimmed = value.Trim();
        if (trimmed.All(char.IsAsciiDigit))
        {
            return long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                   && seconds > 0
                ? TimeSpan.FromSeconds(Math.Min(seconds, (long)WebhookConstraints.MaxRetryAfter.TotalSeconds))
                : null;
        }
        if (DateTimeOffset.TryParseExact(
                trimmed,
                "r",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var date)
            && date > now)
        {
            var delay = date - now;
            return delay > WebhookConstraints.MaxRetryAfter ? WebhookConstraints.MaxRetryAfter : delay;
        }
        return null;
    }
}
