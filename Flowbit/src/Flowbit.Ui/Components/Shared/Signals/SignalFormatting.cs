using System.Text.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Components.Shared.Signals;

/// <summary>
/// The scope choices of the Signals page's throw form. A business key is an optional
/// narrowing of <see cref="Family"/>.
/// </summary>
internal enum SignalScopeChoice
{
    Family,
    AllWorkflows,
    Instance
}

/// <summary>Pure presentation rules for the Signals page and its display components.</summary>
internal static class SignalFormatting
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static string BroadcastStatusLabel(string status) => status switch
    {
        SignalBroadcastStatuses.Delivering => "Delivering",
        SignalBroadcastStatuses.Completed => "Completed",
        SignalBroadcastStatuses.CompletedWithIssues => "Completed with issues",
        SignalBroadcastStatuses.Cancelled => "Cancelled",
        _ => status
    };

    internal static string DeliveryStatusLabel(string status) => status switch
    {
        SignalDeliveryStatuses.Pending => "Pending",
        SignalDeliveryStatuses.Delivered => "Delivered",
        SignalDeliveryStatuses.Skipped => "Skipped",
        SignalDeliveryStatuses.Failed => "Failed",
        SignalDeliveryStatuses.Cancelled => "Cancelled",
        _ => status
    };

    internal static string ResultLabel(string? code) => code switch
    {
        null => "Not delivered yet",
        SignalDeliveryResultCodes.Caught => "Catch continued",
        SignalDeliveryResultCodes.Interrupted => "Boundary interrupted its host",
        SignalDeliveryResultCodes.Branched => "Boundary added a branch",
        SignalDeliveryResultCodes.Superseded => "A conditional boundary took the wait first",
        SignalDeliveryResultCodes.SubscriptionEnded => "The wait ended before delivery",
        SignalDeliveryResultCodes.Stale => "The instance changed before delivery",
        SignalDeliveryResultCodes.PayloadInvalid => "The payload failed a mapping",
        SignalDeliveryResultCodes.DeliveryRejected => "The workflow rejected the delivery",
        SignalDeliveryResultCodes.TokenLimit => "The instance reached its token limit",
        SignalDeliveryResultCodes.DeliveryError => "Delivery failed unexpectedly",
        SignalDeliveryResultCodes.LeaseExhausted => "The Worker lost the delivery job",
        SignalDeliveryResultCodes.BroadcastCancelled => "The broadcast was cancelled",
        _ => code
    };

    /// <summary>
    /// A broadcast's scope in words. An instance scope names its family when it is known:
    /// a broadcast records it, a review does not have it yet.
    /// </summary>
    internal static string ScopeLabel(string? workflowKey, string? businessKey = null, long? instanceId = null) =>
        instanceId is long id
            ? workflowKey is null ? $"Instance #{id}" : $"Instance #{id} · {workflowKey}"
            : businessKey is not null ? $"{workflowKey} · business key {businessKey}"
            : workflowKey ?? "All workflows";

    /// <summary>The match line of a business-key or instance scope, from the summary's rows for the name.</summary>
    internal static string ScopedMatchLine(
        string signalName,
        string? workflowKey,
        string? businessKey,
        long? instanceId,
        int waits)
    {
        var target = instanceId is long id
            ? $"in instance #{id}"
            : $"with business key {businessKey} in {workflowKey}";
        return waits == 0
            ? $"No wait for '{signalName}' {target} now."
            : $"{waits:N0} {(waits == 1 ? "wait" : "waits")} for '{signalName}' {target}.";
    }

    internal static string ThrowerLabel(string thrownBy, string thrownByKind) =>
        thrownByKind == SignalThrowerKinds.Client ? $"{thrownBy} (API client)" : thrownBy;

    /// <summary>A broadcast the page keeps refreshing: one that still delivers.</summary>
    internal static bool IsLive(string status) =>
        string.Equals(status, SignalBroadcastStatuses.Delivering, StringComparison.Ordinal);

    internal static bool CanCancel(string status) =>
        string.Equals(status, SignalBroadcastStatuses.Delivering, StringComparison.Ordinal);

    internal static bool CanRetry(SignalBroadcastDetailDto broadcast) =>
        string.Equals(broadcast.Status, SignalBroadcastStatuses.CompletedWithIssues, StringComparison.Ordinal)
        && broadcast.FailedCount > 0;

    internal static string FormatJson(JsonElement? value) =>
        value is null ? "No payload" : JsonSerializer.Serialize(value.Value, JsonOptions);

    /// <summary>
    /// Reads the payload text area: blank means no payload; anything else must be a JSON
    /// object. Returns the problem, or null when the text is usable.
    /// </summary>
    internal static string? TryParsePayload(string? text, out JsonElement? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "The payload must be a JSON object, such as { \"version\": 7 }.";
            }
            payload = document.RootElement.Clone();
            return null;
        }
        catch (JsonException exception)
        {
            return $"The payload is not valid JSON: {exception.Message}";
        }
    }
}
