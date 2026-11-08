using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Ui.Clients;

namespace Flowbit.Ui.Components.Shared;

/// <summary>Pure presentation rules shared by the Webhooks pages.</summary>
public static class WebhookFormatting
{
    public const int MaxAttemptsPerGeneration = 12;

    public static string EventTypeLabel(string type) => type switch
    {
        LifecycleEventTypes.InstanceStarted => "Instance started",
        LifecycleEventTypes.InstanceCompleted => "Instance completed",
        LifecycleEventTypes.InstanceFaulted => "Instance faulted",
        LifecycleEventTypes.InstanceCancelled => "Instance cancelled",
        LifecycleEventTypes.InstanceReactivated => "Instance reactivated",
        LifecycleEventTypes.TaskCreated => "Task created",
        LifecycleEventTypes.TaskAssigned => "Task assigned",
        LifecycleEventTypes.TaskCompleted => "Task completed",
        LifecycleEventTypes.TaskCancelled => "Task cancelled",
        LifecycleEventTypes.DelegationGranted => "Delegation granted",
        LifecycleEventTypes.IncidentOpened => "Incident opened",
        LifecycleEventTypes.WebhookTest => "Test event",
        _ => type
    };

    public static string DeliveryStateLabel(string state) => state switch
    {
        WebhookDeliveryStates.Pending => "Pending",
        WebhookDeliveryStates.RetryScheduled => "Retry scheduled",
        WebhookDeliveryStates.Delivering => "Delivering",
        WebhookDeliveryStates.Succeeded => "Succeeded",
        WebhookDeliveryStates.Failed => "Failed",
        WebhookDeliveryStates.Discarded => "Discarded",
        _ => state
    };

    /// <summary>The <see cref="StatusBadge"/> status that colors a delivery state.</summary>
    public static string DeliveryStateTone(string state) => state switch
    {
        WebhookDeliveryStates.Succeeded => "succeeded",
        WebhookDeliveryStates.Failed => "failed",
        WebhookDeliveryStates.Pending or WebhookDeliveryStates.RetryScheduled => "pending",
        WebhookDeliveryStates.Delivering => "primary",
        _ => "neutral"
    };

    public static string SubscriptionStateLabel(string state, string? pauseReason) => state switch
    {
        WebhookSubscriptionStates.Active => "Active",
        WebhookSubscriptionStates.Archived => "Archived",
        WebhookSubscriptionStates.Paused => pauseReason switch
        {
            WebhookPauseReasons.ReceiverGone => "Paused: receiver gone (410)",
            WebhookPauseReasons.ReceiverUnavailable => "Paused: receiver unavailable for 72 hours",
            _ => "Paused"
        },
        _ => state
    };

    public static string SubscriptionStateTone(string state, string? pauseReason) => state switch
    {
        WebhookSubscriptionStates.Active => "active",
        WebhookSubscriptionStates.Paused when pauseReason is WebhookPauseReasons.ReceiverGone
            or WebhookPauseReasons.ReceiverUnavailable => "danger",
        WebhookSubscriptionStates.Paused => "warning",
        _ => "neutral"
    };

    public static string OutcomeLabel(string? outcome) => outcome switch
    {
        null => "None yet",
        WebhookOutcomeCodes.Succeeded => "Succeeded",
        WebhookOutcomeCodes.HttpRetryable => "Retryable HTTP status",
        WebhookOutcomeCodes.ReceiverRejected => "Receiver rejected (4xx)",
        WebhookOutcomeCodes.ReceiverGone => "Receiver gone (410)",
        WebhookOutcomeCodes.RedirectRefused => "Redirect refused (3xx)",
        WebhookOutcomeCodes.Timeout => "Timed out",
        WebhookOutcomeCodes.NetworkError => "Network error",
        WebhookOutcomeCodes.TlsError => "TLS certificate error",
        WebhookOutcomeCodes.DestinationDenied => "Destination denied by network policy",
        WebhookOutcomeCodes.SigningUnavailable => "Signing keys unavailable",
        WebhookOutcomeCodes.ResponseHeadersTooLarge => "Response headers too large",
        WebhookOutcomeCodes.AbortedBeforeSend => "Released before sending",
        WebhookOutcomeCodes.OutcomeUnknown => "Outcome unknown",
        WebhookOutcomeCodes.AttemptsExhausted => "Attempts exhausted",
        WebhookOutcomeCodes.DeadlineExceeded => "72-hour retry window exceeded",
        WebhookOutcomeCodes.RetryAfterBeyondDeadline => "Receiver's Retry-After exceeds the retry window",
        _ => outcome
    };

    public static string OutcomeWithStatus(string? outcome, int? status) =>
        status is null ? OutcomeLabel(outcome) : $"{OutcomeLabel(outcome)} · HTTP {status.Value.ToString(CultureInfo.InvariantCulture)}";

    public static string Budget(WebhookDeliveryDto delivery) =>
        $"{delivery.GenerationAttempts} of {MaxAttemptsPerGeneration} in generation {delivery.RetryGeneration}";

    /// <summary>Receiver health in a sentence: backoff state, failures in a row, and the last result.</summary>
    public static string HealthSummary(WebhookReceiverHealthDto health, DateTimeOffset now, BrowserTimeZone zone)
    {
        if (health.BackoffUntil is { } until)
        {
            return until > now
                ? $"Backing off until {FormatTime(until, zone)} after {health.ConsecutiveFailures} failures in a row"
                : $"Probing one request at a time after {health.ConsecutiveFailures} failures in a row";
        }
        if (health.ConsecutiveFailures > 0)
        {
            return $"{health.ConsecutiveFailures} failure{(health.ConsecutiveFailures == 1 ? string.Empty : "s")} in a row";
        }
        // Outcomes such as a 4xx, a 410, or a denied destination neither start backoff nor count as failures in a row.
        if (health.LastFailureAt is { } failure && !(health.LastSuccessAt > failure))
        {
            return health.LastSuccessAt is { } earlier
                ? $"Latest request failed · last success {FormatTime(earlier, zone)}"
                : "No successful delivery yet";
        }
        return health.LastSuccessAt is { } success ? $"Healthy · last success {FormatTime(success, zone)}" : "No deliveries yet";
    }

    /// <summary>A time in the browser's zone for a sentence; markup shows times with <c>LocalTime</c>.</summary>
    public static string FormatTime(DateTimeOffset? value, BrowserTimeZone zone) =>
        value is { } instant ? zone.Format(instant) : "—";

    /// <summary>
    /// The body indented for reading. Each token is copied from the stored text, so strings keep exactly the
    /// characters receivers verify and nothing is escaped again. Text that is not JSON is returned unchanged.
    /// </summary>
    public static string PrettyBody(string body)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(body));
        var text = new StringBuilder(body.Length * 2);
        var depth = 0;
        var first = true;
        var afterName = false;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    depth--;
                    if (!first)
                    {
                        AppendLine(text, depth);
                    }
                    text.Append(reader.TokenType == JsonTokenType.EndObject ? '}' : ']');
                    first = false;
                    continue;
                }
                // A value after a property name stays on that name's line.
                if (!afterName)
                {
                    if (!first)
                    {
                        text.Append(',');
                    }
                    if (depth > 0)
                    {
                        AppendLine(text, depth);
                    }
                }
                first = afterName = false;
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        text.Append('{');
                        depth++;
                        first = true;
                        break;
                    case JsonTokenType.StartArray:
                        text.Append('[');
                        depth++;
                        first = true;
                        break;
                    case JsonTokenType.PropertyName:
                        text.Append('"').Append(Encoding.UTF8.GetString(reader.ValueSpan)).Append("\": ");
                        afterName = true;
                        break;
                    case JsonTokenType.String:
                        text.Append('"').Append(Encoding.UTF8.GetString(reader.ValueSpan)).Append('"');
                        break;
                    default:
                        text.Append(Encoding.UTF8.GetString(reader.ValueSpan));
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return body;
        }
        return text.ToString();
    }

    private static void AppendLine(StringBuilder text, int depth) => text.AppendLine().Append(' ', depth * 2);

    /// <summary>A failed request's message for settings administrators; 401 and 403 get Flowbit's own wording.</summary>
    public static string ErrorMessage(Exception exception) => exception switch
    {
        WorkflowApiException { StatusCode: HttpStatusCode.Forbidden } =>
            "This identity does not have the settings administrator role required for webhooks.",
        WorkflowApiException { StatusCode: HttpStatusCode.Unauthorized } => "Sign in again to manage webhooks.",
        _ => exception.Message
    };

    /// <summary>Whether a failure means this identity may not manage webhooks, so a page shows only that message.</summary>
    public static bool IsPermissionFailure(Exception exception) =>
        exception is WorkflowApiException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized };

    /// <summary>
    /// A failed action's message once the page has been reloaded after it. A refused (409) request says the page
    /// shows the current state only when that reload succeeded; the request itself is never replayed.
    /// </summary>
    public static string ActionFailure(Exception exception, bool reloaded) =>
        reloaded && exception is WorkflowApiException { StatusCode: HttpStatusCode.Conflict }
            ? $"{ErrorMessage(exception)} The page shows the current state."
            : ErrorMessage(exception);

    /// <summary>An action's own error followed by the failure of the reload after it; a repeated message appears once.</summary>
    public static string? WithReloadFailure(string? actionError, string? reloadError)
    {
        if (reloadError is null || reloadError == actionError)
        {
            return actionError;
        }
        return actionError is null
            ? $"The page could not be refreshed: {reloadError}"
            : $"{actionError} The page could not be refreshed: {reloadError}";
    }

    /// <summary>Validates the create/edit form before it is sent; the API remains authoritative.</summary>
    public static string? ValidateForm(string? name, string? url, IReadOnlyCollection<string> eventTypes, int maxConcurrency)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Name is required.";
        }
        if (name.Trim().Length > 200)
        {
            return "Name must not exceed 200 characters.";
        }
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return "Enter the receiver's absolute HTTPS URL.";
        }
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return "The endpoint URL must use HTTPS.";
        }
        if (eventTypes.Count == 0)
        {
            return "Select at least one event type.";
        }
        if (maxConcurrency is < 1 or > 16)
        {
            return "Maximum concurrency must be between 1 and 16.";
        }
        return null;
    }

    public static bool CanPause(WebhookSubscriptionDto subscription) =>
        subscription.State == WebhookSubscriptionStates.Active;

    public static bool CanResume(WebhookSubscriptionDto subscription) =>
        subscription.State == WebhookSubscriptionStates.Paused;

    public static bool IsArchived(WebhookSubscriptionDto subscription) =>
        subscription.State == WebhookSubscriptionStates.Archived;

    public static bool HasPreviousKey(WebhookSubscriptionDto subscription) =>
        subscription.SigningKeys.Any(key => key.Status == WebhookSigningKeyStatuses.Previous);

    /// <summary>When a newly revealed secret must be installed: before activation, or before the previous key stops signing.</summary>
    public static string SecretInstallDeadline(WebhookSubscriptionDto subscription, BrowserTimeZone zone)
    {
        var previous = subscription.SigningKeys.FirstOrDefault(key => key.Status == WebhookSigningKeyStatuses.Previous);
        if (previous?.OverlapExpiresAt is { } until)
        {
            return $"before the previous key stops signing at {FormatTime(until, zone)}";
        }
        return subscription.State == WebhookSubscriptionStates.Paused
            ? "before activating the subscription"
            : "now: requests are already signed with this key alone";
    }
}
