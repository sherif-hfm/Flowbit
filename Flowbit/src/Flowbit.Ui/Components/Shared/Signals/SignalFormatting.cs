using System.Globalization;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

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

    /// <summary>
    /// How a delivery ended, in words. A start delivery has wording of its own where its
    /// result means something else than a wait's.
    /// </summary>
    internal static string ResultLabel(string? code, string kind = SignalDeliveryKinds.Wait) =>
        kind == SignalDeliveryKinds.Start ? StartResultLabel(code) : WaitResultLabel(code);

    private static string StartResultLabel(string? code) => code switch
    {
        null => "Not started yet",
        SignalDeliveryResultCodes.Started => "Started an instance",
        SignalDeliveryResultCodes.BusinessKeyConflict => "An instance already has this business key",
        SignalDeliveryResultCodes.SubscriptionEnded => "The workflow no longer starts on this signal",
        SignalDeliveryResultCodes.DeliveryRejected => "The workflow rejected the start",
        _ => WaitResultLabel(code)
    };

    private static string WaitResultLabel(string? code) => code switch
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

    /// <summary>
    /// What a family or all-workflows throw reaches now, from the summary's rows for the name:
    /// waits, and workflows that a throw starts.
    /// </summary>
    internal static string ReachLine(string signalName, IReadOnlyList<SignalSubscriptionCountDto> rows, bool truncated)
    {
        var waits = rows.Sum(row => row.Count);
        var starts = rows.Sum(row => row.StartCount);
        if (waits == 0 && starts == 0)
        {
            return $"No instance is waiting for '{signalName}' now.";
        }

        var families = rows.Select(row => row.WorkflowKey).Distinct(StringComparer.Ordinal).Count();
        return $"{CountsText(waits, starts)} in {families:N0} {(families == 1 ? "family" : "families")}: "
               + string.Join(", ", rows.Select(FamilyText))
               + (truncated ? " (list truncated)" : "");

        static string FamilyText(SignalSubscriptionCountDto row) =>
            row.StartCount > 0
                ? row.Count > 0
                    ? $"{row.WorkflowKey} ({row.Count:N0} and 1 start)"
                    : $"{row.WorkflowKey} (1 start)"
                : $"{row.WorkflowKey} ({row.Count:N0})";
    }

    /// <summary>Starts of workflow families in words, such as "1 start".</summary>
    internal static string StartCountText(int starts) =>
        $"{starts:N0} {(starts == 1 ? "start" : "starts")}";

    /// <summary>The kind of a delivery in words.</summary>
    internal static string DeliveryKindLabel(string kind) => kind switch
    {
        SignalDeliveryKinds.Wait => "Waits",
        SignalDeliveryKinds.Start => "Starts",
        _ => kind
    };

    /// <summary>Waits and new instances in words, such as "2 waits and 1 new instance".</summary>
    internal static string CountsText(int waits, int starts)
    {
        var waitText = $"{waits:N0} {(waits == 1 ? "wait" : "waits")}";
        if (starts == 0)
        {
            return waitText;
        }

        var startText = $"{starts:N0} new {(starts == 1 ? "instance" : "instances")}";
        return waits == 0 ? startText : $"{waitText} and {startText}";
    }

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

    /// <summary>
    /// Who threw a broadcast: a user's identity, a managed client's ID, or, for a throw from a
    /// workflow, the instance and its throw or end event: the event's name, or its node number
    /// when it has none.
    /// </summary>
    internal static string ThrowerLabel(
        string thrownBy,
        string thrownByKind,
        long? throwerInstanceId = null,
        int? throwerNodeId = null,
        string? throwerNodeName = null)
    {
        if (thrownByKind == SignalThrowerKinds.Client)
        {
            return $"{thrownBy} (API client)";
        }

        if (thrownByKind != SignalThrowerKinds.Instance)
        {
            return thrownBy;
        }

        var instance = $"Instance #{throwerInstanceId?.ToString(CultureInfo.InvariantCulture) ?? thrownBy}";
        if (!string.IsNullOrWhiteSpace(throwerNodeName))
        {
            return $"{instance} · {throwerNodeName}";
        }

        return throwerNodeId is int nodeId
            ? $"{instance} · node #{nodeId.ToString(CultureInfo.InvariantCulture)}"
            : instance;
    }

    /// <summary>The recipients a throw or end event's scope names, in words.</summary>
    internal static string ThrowScopeLabel(SignalThrowScopeModel? scope) => scope?.Kind switch
    {
        SignalThrowScopeKinds.ThisInstance => "this instance",
        SignalThrowScopeKinds.ThisWorkflow => "this workflow",
        SignalThrowScopeKinds.Workflow => $"workflow {scope.WorkflowKey}",
        SignalThrowScopeKinds.BusinessKey => $"the business key in {scope.BusinessKeyVariable} in workflow {scope.WorkflowKey}",
        SignalThrowScopeKinds.OwnBusinessKey => $"this instance's business key in workflow {scope.WorkflowKey}",
        SignalThrowScopeKinds.AllWorkflows => "all workflows",
        _ => "no valid scope"
    };

    /// <summary>The Thrown by filter of the broadcast list, with the kinds each choice requests.</summary>
    internal static IReadOnlyList<(string Value, string Label, IReadOnlyList<string> Kinds)> ThrownByChoices { get; } =
    [
        ("people", "People and API clients", [SignalThrowerKinds.User, SignalThrowerKinds.Client]),
        ("workflows", "Workflows", [SignalThrowerKinds.Instance]),
        ("everyone", "Everyone", [])
    ];

    /// <summary>The thrower kinds a Thrown by choice requests; an unknown choice means the default.</summary>
    internal static IReadOnlyList<string> ThrownByKinds(string? choice) =>
        ThrownByChoices.FirstOrDefault(item => item.Value == choice).Kinds ?? ThrownByChoices[0].Kinds;

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
