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

/// <summary>The units of the throw form's expiry, in the order of its list.</summary>
internal enum SignalExpiryUnit
{
    Seconds,
    Minutes,
    Hours,
    Days
}

/// <summary>Pure presentation rules for the Signals page and its display components.</summary>
internal static class SignalFormatting
{
    internal const string ExpiryAmountProblem =
        "Enter the expiry as a whole number of 1 or more, or leave it empty.";

    internal const string ExpiryTooLongProblem = "A signal can expire after at most 365 days.";

    internal const string ExpiryPassedNote =
        "The expiry has passed. A Worker ends the pending deliveries when it runs.";

    internal const string RetryExpiredNote =
        "This broadcast has expired, so its failed deliveries cannot be retried. Throw the signal again to reach the waits that are still open.";

    /// <summary>The unit list of the throw form, in its order.</summary>
    internal static IReadOnlyList<(SignalExpiryUnit Unit, string Label)> ExpiryUnits { get; } =
    [
        (SignalExpiryUnit.Seconds, "Seconds"),
        (SignalExpiryUnit.Minutes, "Minutes"),
        (SignalExpiryUnit.Hours, "Hours"),
        (SignalExpiryUnit.Days, "Days")
    ];

    /// <summary>
    /// What the page keeps of the expiry box, from what <c>typed-inputs.js</c> reports: the
    /// trimmed text, or <see cref="UnreadableExpiryAmount"/> when the browser could not read
    /// what was typed. A number box reports such text, such as <c>5-</c> or <c>10e</c>, as an
    /// empty value, which would otherwise read as a signal that does not expire.
    /// </summary>
    internal static string ReadExpiryAmount(string? value, bool badInput) =>
        badInput ? UnreadableExpiryAmount : value?.Trim() ?? string.Empty;

    /// <summary>The kept amount when the browser could not read the box; never a number.</summary>
    internal const string UnreadableExpiryAmount = "\u0000unreadable";

    /// <summary>
    /// Reads the throw form's expiry: blank means none. Returns the problem, or null when the
    /// amount is usable; <paramref name="expiresAfter"/> is then the ISO-8601 text the throw
    /// sends, or null for a signal that does not expire. The range is the API's
    /// (<see cref="SignalExpiryRules"/>).
    /// </summary>
    internal static string? TryBuildExpiry(string? amountText, SignalExpiryUnit unit, out string? expiresAfter)
    {
        expiresAfter = null;
        if (string.Equals(amountText, UnreadableExpiryAmount, StringComparison.Ordinal))
        {
            return ExpiryAmountProblem;
        }

        var text = amountText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!text.All(char.IsAsciiDigit))
        {
            return ExpiryAmountProblem;
        }

        // Only digits: a value too large for a long is far over the limit.
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            return ExpiryTooLongProblem;
        }

        if (amount < 1)
        {
            return ExpiryAmountProblem;
        }

        var iso = ExpiryIso(amount, unit);
        switch (SignalExpiryRules.Parse(iso, out _))
        {
            case SignalExpiryProblem.None:
                expiresAfter = iso;
                return null;
            case SignalExpiryProblem.OutOfRange:
                return ExpiryTooLongProblem;
            default:
                return ExpiryAmountProblem;
        }
    }

    /// <summary>The ISO-8601 duration of an amount of a unit, such as PT10M or P2D.</summary>
    internal static string ExpiryIso(long amount, SignalExpiryUnit unit)
    {
        var number = amount.ToString(CultureInfo.InvariantCulture);
        return unit switch
        {
            SignalExpiryUnit.Seconds => $"PT{number}S",
            SignalExpiryUnit.Minutes => $"PT{number}M",
            SignalExpiryUnit.Hours => $"PT{number}H",
            SignalExpiryUnit.Days => $"P{number}D",
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
    }

    /// <summary>The review's Expires row: "10 minutes after the throw", or "Never" without an amount.</summary>
    internal static string ExpiryReviewText(string? amountText, SignalExpiryUnit unit)
    {
        if (string.IsNullOrWhiteSpace(amountText)
            || !long.TryParse(amountText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
        {
            return "Never";
        }

        var noun = unit switch
        {
            SignalExpiryUnit.Seconds => "second",
            SignalExpiryUnit.Minutes => "minute",
            SignalExpiryUnit.Hours => "hour",
            _ => "day"
        };
        return $"{amount:N0} {noun}{(amount == 1 ? "" : "s")} after the throw";
    }

    /// <summary>
    /// Whether a broadcast's expiry is behind <paramref name="now"/>, the UI's clock. The page
    /// only shows and hides with it; the API decides with the database's clock.
    /// </summary>
    internal static bool HasExpiryPassed(DateTimeOffset? expiresAt, DateTimeOffset now) =>
        expiresAt is DateTimeOffset expiry && expiry <= now;

    /// <summary>The note under a delivering broadcast's expiry once it has passed, or null.</summary>
    internal static string? ExpiryNote(SignalBroadcastDetailDto broadcast, DateTimeOffset now) =>
        IsLive(broadcast.Status) && HasExpiryPassed(broadcast.ExpiresAt, now) ? ExpiryPassedNote : null;

    /// <summary>Why Retry failed is hidden for a broadcast with failed deliveries, or null when it is not.</summary>
    internal static string? RetryHiddenReason(SignalBroadcastDetailDto broadcast, DateTimeOffset now) =>
        broadcast.FailedCount > 0
        && broadcast.Status is SignalBroadcastStatuses.CompletedWithIssues or SignalBroadcastStatuses.Expired
        && HasExpiryPassed(broadcast.ExpiresAt, now)
            ? RetryExpiredNote
            : null;

    /// <summary>The second line of a broadcast's progress, such as "2 skipped · 1 failed · 2 expired".</summary>
    internal static string ProgressDetail(int skipped, int failed, int expired) =>
        $"{skipped:N0} skipped · {failed:N0} failed" + (expired > 0 ? $" · {expired:N0} expired" : "");
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static string BroadcastStatusLabel(string status) => status switch
    {
        SignalBroadcastStatuses.Delivering => "Delivering",
        SignalBroadcastStatuses.Completed => "Completed",
        SignalBroadcastStatuses.CompletedWithIssues => "Completed with issues",
        SignalBroadcastStatuses.Expired => "Expired",
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
        SignalDeliveryResultCodes.BroadcastExpired => "The broadcast expired before the start",
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
        SignalDeliveryResultCodes.BroadcastExpired => "The broadcast expired before delivery",
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

    /// <summary>
    /// Whether the page offers Retry failed: a broadcast that finished with failed deliveries
    /// and whose expiry, if any, is not behind <paramref name="now"/>. The API's 409 remains
    /// the rule.
    /// </summary>
    internal static bool CanRetry(SignalBroadcastDetailDto broadcast, DateTimeOffset now) =>
        string.Equals(broadcast.Status, SignalBroadcastStatuses.CompletedWithIssues, StringComparison.Ordinal)
        && broadcast.FailedCount > 0
        && !HasExpiryPassed(broadcast.ExpiresAt, now);

    internal static string FormatJson(JsonElement? value) =>
        value is null ? "No payload" : JsonSerializer.Serialize(value.Value, JsonOptions);

    /// <summary>The code of a 403 that a signal catalog entry's rule returned, as opposed to a lost permission.</summary>
    internal const string CatalogForbiddenCode = "signal_catalog_forbidden";

    internal const string CatalogArchivedNote = "Archived in the signal catalog; it cannot be thrown.";

    internal const string CatalogCanThrowText = "You can throw this signal.";

    /// <summary>The longest description an option of the name list shows.</summary>
    internal const int MaxNameOptionLabelLength = 120;

    /// <summary>The note for a name without a catalog entry, with what the mode in effect means for a throw.</summary>
    internal static string CatalogMissingNote(string? mode) =>
        SignalCatalogModes.ParseMode(mode) == SignalCatalogModes.Required
            ? "Not in the signal catalog. This deployment requires an entry before a throw."
            : "Not in the signal catalog.";

    /// <summary>Whether the caller may throw an active entry's name, or why not, from its <c>canThrow</c>.</summary>
    internal static string CatalogThrowText(SignalCatalogEntryDto entry) =>
        entry.CanThrow
            ? CatalogCanThrowText
            : entry.ThrowRoles.Count > 0
                ? $"You cannot throw this signal: it needs one of the roles its catalog entry lists: {string.Join(", ", entry.ThrowRoles)}."
                : "You cannot throw this signal: it needs a role from Signals.RequiredRole.";

    /// <summary>A field of an entry in words, such as "a string, required".</summary>
    internal static string CatalogFieldText(SignalCatalogFieldDto field) =>
        $"{SignalCatalogEntryRules.TypeWords(field.DataType, field.IsArray)}, {(field.Required ? "required" : "optional")}";

    /// <summary>The review's Catalog row: the entry's name, or "No entry".</summary>
    internal static string CatalogReviewText(SignalCatalogEntryDto? entry) =>
        entry is null
            ? "No entry"
            : entry.Status == SignalCatalogStatuses.Archived ? $"{entry.Name} (archived)" : entry.Name;

    /// <summary>
    /// The options of the signal name list: the names from the subscription summary first, then
    /// the active catalog entries not among them. A cataloged name's label is its description,
    /// shortened to <see cref="MaxNameOptionLabelLength"/> characters.
    /// </summary>
    internal static IReadOnlyList<(string Value, string? Label)> NameOptions(
        IEnumerable<string> summaryNames,
        IReadOnlyList<SignalCatalogEntryDto>? catalogEntries)
    {
        var descriptions = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in catalogEntries ?? [])
        {
            if (entry.Status == SignalCatalogStatuses.Active)
            {
                descriptions.TryAdd(entry.Name, ShortLabel(entry.Description));
            }
        }

        var options = new List<(string Value, string? Label)>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in summaryNames)
        {
            if (listed.Add(name))
            {
                options.Add((name, descriptions.GetValueOrDefault(name)));
            }
        }

        foreach (var (name, description) in descriptions)
        {
            if (listed.Add(name))
            {
                options.Add((name, description));
            }
        }

        return options;

        static string? ShortLabel(string? description)
        {
            var text = description?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var runes = text.EnumerateRunes().ToList();
            return runes.Count <= MaxNameOptionLabelLength
                ? text
                : string.Concat(runes.Take(MaxNameOptionLabelLength - 1).Select(rune => rune.ToString())) + "…";
        }
    }

    /// <summary>
    /// A JSON object with an entry's fields, required ones first, each in contract order, with a
    /// placeholder of the field's type: "" for a string, 0, false, a date, a date and time, {}
    /// for json, and [] for any array. Every placeholder fits the field, because the catalog
    /// refuses a null field.
    /// </summary>
    internal static string PayloadTemplate(IReadOnlyList<SignalCatalogFieldDto> fields)
    {
        var template = new System.Text.Json.Nodes.JsonObject();
        foreach (var field in fields.Where(field => field.Required).Concat(fields.Where(field => !field.Required)))
        {
            template[field.Name] = Placeholder(field);
        }

        return template.ToJsonString(JsonOptions);

        static System.Text.Json.Nodes.JsonNode Placeholder(SignalCatalogFieldDto field) =>
            field.IsArray
                ? new System.Text.Json.Nodes.JsonArray()
                : field.DataType switch
                {
                    WorkflowVariableTypes.String => System.Text.Json.Nodes.JsonValue.Create(string.Empty),
                    WorkflowVariableTypes.Number => System.Text.Json.Nodes.JsonValue.Create(0),
                    WorkflowVariableTypes.Boolean => System.Text.Json.Nodes.JsonValue.Create(false),
                    WorkflowVariableTypes.Date => System.Text.Json.Nodes.JsonValue.Create("2026-01-01"),
                    WorkflowVariableTypes.DateTime => System.Text.Json.Nodes.JsonValue.Create("2026-01-01T09:00:00Z"),
                    _ => new System.Text.Json.Nodes.JsonObject()
                };
    }

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
