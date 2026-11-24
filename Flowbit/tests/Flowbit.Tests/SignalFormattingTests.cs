extern alias FlowbitUi;

using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using ExecutionPositionText = FlowbitUi::Flowbit.Ui.Components.Shared.ExecutionPositionText;
using SignalFormatting = FlowbitUi::Flowbit.Ui.Components.Shared.Signals.SignalFormatting;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The Signals page's pure presentation rules.</summary>
public sealed class SignalFormattingTests
{
    [Fact]
    public void ThrowerLabel_NamesAnInstanceThrowerByItsInstanceAndEvent()
    {
        Assert.Equal("alice", SignalFormatting.ThrowerLabel("alice", SignalThrowerKinds.User));
        Assert.Equal("svc (API client)", SignalFormatting.ThrowerLabel("svc", SignalThrowerKinds.Client));
        Assert.Equal("Instance #42 · Announce the cancellation",
            SignalFormatting.ThrowerLabel("42", SignalThrowerKinds.Instance, 42, 9, "Announce the cancellation"));
        // An unnamed event is named by its node number, so two throws of one instance differ.
        Assert.Equal("Instance #42 · node #9", SignalFormatting.ThrowerLabel("42", SignalThrowerKinds.Instance, 42, 9, " "));
        Assert.Equal("Instance #42 · node #9", SignalFormatting.ThrowerLabel("42", SignalThrowerKinds.Instance, 42, 9, null));
        Assert.Equal("Instance #42", SignalFormatting.ThrowerLabel("42", SignalThrowerKinds.Instance, 42));
        Assert.Equal("Instance #42", SignalFormatting.ThrowerLabel("42", SignalThrowerKinds.Instance));
    }

    [Fact]
    public void ThrownByFilter_StartsAtPeopleAndApiClients()
    {
        Assert.Equal([SignalThrowerKinds.User, SignalThrowerKinds.Client], SignalFormatting.ThrownByKinds(null));
        Assert.Equal([SignalThrowerKinds.User, SignalThrowerKinds.Client], SignalFormatting.ThrownByKinds("unknown"));
        Assert.Equal([SignalThrowerKinds.Instance], SignalFormatting.ThrownByKinds("workflows"));
        Assert.Empty(SignalFormatting.ThrownByKinds("everyone"));
        Assert.Equal("people", SignalFormatting.ThrownByChoices[0].Value);
    }

    [Fact]
    public void ThrowScopeLabel_DescribesEveryKindInWords()
    {
        Assert.Equal("this instance", SignalFormatting.ThrowScopeLabel(new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.ThisInstance }));
        Assert.Equal("workflow shipping", SignalFormatting.ThrowScopeLabel(
            new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = "shipping" }));
        Assert.Equal("this instance's business key in workflow shipping", SignalFormatting.ThrowScopeLabel(
            new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.OwnBusinessKey, WorkflowKey = "shipping" }));
        Assert.Equal("no valid scope", SignalFormatting.ThrowScopeLabel(null));
        Assert.Equal("Signal throw", ExecutionPositionText.NodeTypeLabel(BpmnFlowNodeTypes.IntermediateSignalThrowEvent));
        Assert.Equal("Signal end", ExecutionPositionText.NodeTypeLabel(BpmnFlowNodeTypes.SignalEndEvent));
    }
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("{ \"version\": 7 }", null)]
    [InlineData("[1, 2]", "The payload must be a JSON object, such as { \"version\": 7 }.")]
    [InlineData("7", "The payload must be a JSON object, such as { \"version\": 7 }.")]
    public void TryParsePayload_AcceptsBlankOrAJsonObject(string? text, string? expected)
    {
        Assert.Equal(expected, SignalFormatting.TryParsePayload(text, out var payload));
        if (expected is null && !string.IsNullOrWhiteSpace(text))
        {
            Assert.Equal(7, payload!.Value.GetProperty("version").GetInt32());
        }
        else
        {
            Assert.Null(payload);
        }
    }

    [Fact]
    public void TryParsePayload_NamesInvalidJson()
    {
        var problem = SignalFormatting.TryParsePayload("{ version: 7 }", out var payload);
        Assert.StartsWith("The payload is not valid JSON: ", problem, StringComparison.Ordinal);
        Assert.Null(payload);
    }

    [Fact]
    public void Labels_DescribeEveryStatusAndResult()
    {
        Assert.Equal("Completed with issues",
            SignalFormatting.BroadcastStatusLabel(SignalBroadcastStatuses.CompletedWithIssues));
        Assert.Equal("Skipped", SignalFormatting.DeliveryStatusLabel(SignalDeliveryStatuses.Skipped));
        Assert.Equal("Not delivered yet", SignalFormatting.ResultLabel(null));
        Assert.Equal("Catch continued", SignalFormatting.ResultLabel(SignalDeliveryResultCodes.Caught));
        Assert.Equal("The wait ended before delivery",
            SignalFormatting.ResultLabel(SignalDeliveryResultCodes.SubscriptionEnded));
        Assert.Equal("unknownCode", SignalFormatting.ResultLabel("unknownCode"));
        Assert.Equal("All workflows", SignalFormatting.ScopeLabel(null));
        Assert.Equal("claims", SignalFormatting.ScopeLabel("claims"));
        Assert.Equal("ingest (API client)", SignalFormatting.ThrowerLabel("ingest", SignalThrowerKinds.Client));
        Assert.Equal("alice", SignalFormatting.ThrowerLabel("alice", SignalThrowerKinds.User));
        Assert.Equal("No payload", SignalFormatting.FormatJson(null));
        Assert.Contains("\"version\": 7",
            SignalFormatting.FormatJson(JsonSerializer.SerializeToElement(new { version = 7 })), StringComparison.Ordinal);
    }

    [Fact]
    public void ScopeLabel_NamesEachOfTheFourScopes()
    {
        Assert.Equal("All workflows", SignalFormatting.ScopeLabel(null, null, null));
        Assert.Equal("claims-handling", SignalFormatting.ScopeLabel("claims-handling", null, null));
        Assert.Equal("example-signal-boundaries-recall · business key ORD-2001",
            SignalFormatting.ScopeLabel("example-signal-boundaries-recall", "ORD-2001", null));
        Assert.Equal("Instance #42 · example-signal-boundaries-recall",
            SignalFormatting.ScopeLabel("example-signal-boundaries-recall", null, 42));
        // A review does not know the instance's family yet.
        Assert.Equal("Instance #42", SignalFormatting.ScopeLabel(null, null, 42));
    }

    [Fact]
    public void ScopedMatchLine_CountsTheWaitsOfABusinessKeyOrAnInstance()
    {
        Assert.Equal("2 waits for 'warehouse.price-update' with business key ORD-2001 in orders.",
            SignalFormatting.ScopedMatchLine("warehouse.price-update", "orders", "ORD-2001", null, 2));
        Assert.Equal("1 wait for 'warehouse.price-update' in instance #42.",
            SignalFormatting.ScopedMatchLine("warehouse.price-update", null, null, 42, 1));
        Assert.Equal("No wait for 'warehouse.price-update' in instance #42 now.",
            SignalFormatting.ScopedMatchLine("warehouse.price-update", null, null, 42, 0));
    }

    [Fact]
    public void LiveCancelAndRetry_FollowTheBroadcastStatus()
    {
        Assert.True(SignalFormatting.IsLive(SignalBroadcastStatuses.Delivering));
        Assert.False(SignalFormatting.IsLive(SignalBroadcastStatuses.Completed));
        Assert.True(SignalFormatting.CanCancel(SignalBroadcastStatuses.Delivering));
        Assert.False(SignalFormatting.CanCancel(SignalBroadcastStatuses.Cancelled));
        Assert.True(SignalFormatting.CanRetry(Broadcast(SignalBroadcastStatuses.CompletedWithIssues, failed: 2), Now));
        Assert.False(SignalFormatting.CanRetry(Broadcast(SignalBroadcastStatuses.CompletedWithIssues, failed: 0), Now));
        Assert.False(SignalFormatting.CanRetry(Broadcast(SignalBroadcastStatuses.Delivering, failed: 2), Now));
        Assert.False(SignalFormatting.IsLive(SignalBroadcastStatuses.Expired));
        Assert.False(SignalFormatting.CanCancel(SignalBroadcastStatuses.Expired));
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-23T09:10:00Z");

    [Fact]
    public void CanRetry_FollowsTheExpiryOnTheUisClock()
    {
        var beforeExpiry = Broadcast(SignalBroadcastStatuses.CompletedWithIssues, failed: 2) with { ExpiresAt = Now.AddSeconds(1) };
        Assert.True(SignalFormatting.CanRetry(beforeExpiry, Now));
        Assert.Null(SignalFormatting.RetryHiddenReason(beforeExpiry, Now));

        // At the expiry, and after it, the button is hidden and the page says why.
        var atExpiry = beforeExpiry with { ExpiresAt = Now };
        Assert.False(SignalFormatting.CanRetry(atExpiry, Now));
        Assert.Equal(
            "This broadcast has expired, so its failed deliveries cannot be retried. Throw the signal again to reach the waits that are still open.",
            SignalFormatting.RetryHiddenReason(atExpiry, Now));

        // An expired broadcast with failures gives the same reason; one without failures needs none.
        var expired = Broadcast(SignalBroadcastStatuses.Expired, failed: 1) with { ExpiresAt = Now.AddMinutes(-1), ExpiredCount = 2 };
        Assert.False(SignalFormatting.CanRetry(expired, Now));
        Assert.NotNull(SignalFormatting.RetryHiddenReason(expired, Now));
        Assert.Null(SignalFormatting.RetryHiddenReason(expired with { FailedCount = 0 }, Now));
        Assert.Null(SignalFormatting.RetryHiddenReason(Broadcast(SignalBroadcastStatuses.CompletedWithIssues, failed: 2), Now));
    }

    [Fact]
    public void ExpiryNote_ShowsOnlyWhileDeliveringPastTheExpiry()
    {
        var delivering = Broadcast(SignalBroadcastStatuses.Delivering, failed: 0) with { ExpiresAt = Now.AddSeconds(-1) };
        Assert.Equal("The expiry has passed. A Worker ends the pending deliveries when it runs.",
            SignalFormatting.ExpiryNote(delivering, Now));
        Assert.Null(SignalFormatting.ExpiryNote(delivering with { ExpiresAt = Now.AddSeconds(1) }, Now));
        Assert.Null(SignalFormatting.ExpiryNote(delivering with { ExpiresAt = null }, Now));
        Assert.Null(SignalFormatting.ExpiryNote(delivering with { Status = SignalBroadcastStatuses.Expired }, Now));
    }

    [Theory]
    [InlineData(null, "Minutes", null, null)]
    [InlineData("", "Minutes", null, null)]
    [InlineData("  ", "Days", null, null)]
    [InlineData("10", "Minutes", "PT10M", null)]
    [InlineData("30", "Seconds", "PT30S", null)]
    [InlineData("2", "Hours", "PT2H", null)]
    [InlineData("365", "Days", "P365D", null)]
    [InlineData("1", "Seconds", "PT1S", null)]
    [InlineData("0", "Minutes", null, "Enter the expiry as a whole number of 1 or more, or leave it empty.")]
    [InlineData("1.5", "Minutes", null, "Enter the expiry as a whole number of 1 or more, or leave it empty.")]
    [InlineData("-5", "Minutes", null, "Enter the expiry as a whole number of 1 or more, or leave it empty.")]
    [InlineData("366", "Days", null, "A signal can expire after at most 365 days.")]
    [InlineData("525601", "Minutes", null, "A signal can expire after at most 365 days.")]
    [InlineData("99999999999999999999999", "Seconds", null, "A signal can expire after at most 365 days.")]
    // Amounts that fit a long but whose length a TimeSpan cannot hold.
    [InlineData("9999999999999999", "Days", null, "A signal can expire after at most 365 days.")]
    [InlineData("9223372036854775807", "Days", null, "A signal can expire after at most 365 days.")]
    [InlineData("9223372036854775807", "Seconds", null, "A signal can expire after at most 365 days.")]
    public void TryBuildExpiry_GivesTheIsoTextOrTheProblem(string? amount, string unit, string? expectedIso, string? expectedProblem)
    {
        var parsedUnit = Enum.Parse<FlowbitUi::Flowbit.Ui.Components.Shared.Signals.SignalExpiryUnit>(unit);
        Assert.Equal(expectedProblem, SignalFormatting.TryBuildExpiry(amount, parsedUnit, out var iso));
        Assert.Equal(expectedIso, iso);
        if (iso is not null)
        {
            // The page's text is exactly what the API accepts.
            Assert.Equal(SignalExpiryProblem.None, SignalExpiryRules.Parse(iso, out _));
        }
    }

    [Fact]
    public void ReadExpiryAmount_KeepsTheTextOrMarksWhatTheBrowserCouldNotRead()
    {
        Assert.Equal("10", SignalFormatting.ReadExpiryAmount(" 10 ", badInput: false));
        Assert.Equal(string.Empty, SignalFormatting.ReadExpiryAmount(null, badInput: false));
        Assert.Equal(string.Empty, SignalFormatting.ReadExpiryAmount("", badInput: false));

        // A number box reports 5- or 10e as an empty value with badInput: never "no expiry".
        var unreadable = SignalFormatting.ReadExpiryAmount("", badInput: true);
        Assert.Equal(SignalFormatting.UnreadableExpiryAmount, unreadable);
        Assert.Equal(SignalFormatting.ExpiryAmountProblem, SignalFormatting.TryBuildExpiry(
            unreadable, FlowbitUi::Flowbit.Ui.Components.Shared.Signals.SignalExpiryUnit.Minutes, out var iso));
        Assert.Null(iso);
    }

    [Fact]
    public void ExpiryWording_NamesTheUnitAndThePastExpiry()
    {
        var units = FlowbitUi::Flowbit.Ui.Components.Shared.Signals.SignalExpiryUnit.Minutes;
        Assert.Equal("10 minutes after the throw", SignalFormatting.ExpiryReviewText("10", units));
        Assert.Equal("1 minute after the throw", SignalFormatting.ExpiryReviewText("1", units));
        Assert.Equal("1 day after the throw", SignalFormatting.ExpiryReviewText("1",
            FlowbitUi::Flowbit.Ui.Components.Shared.Signals.SignalExpiryUnit.Days));
        Assert.Equal("90 seconds after the throw", SignalFormatting.ExpiryReviewText("90",
            FlowbitUi::Flowbit.Ui.Components.Shared.Signals.SignalExpiryUnit.Seconds));
        Assert.Equal("Never", SignalFormatting.ExpiryReviewText(null, units));
        Assert.Equal("Never", SignalFormatting.ExpiryReviewText("", units));
        Assert.Equal(["Seconds", "Minutes", "Hours", "Days"], SignalFormatting.ExpiryUnits.Select(unit => unit.Label));

        Assert.Equal("Expired", SignalFormatting.BroadcastStatusLabel(SignalBroadcastStatuses.Expired));
        Assert.Equal("The broadcast expired before delivery",
            SignalFormatting.ResultLabel(SignalDeliveryResultCodes.BroadcastExpired));
        Assert.Equal("The broadcast expired before the start",
            SignalFormatting.ResultLabel(SignalDeliveryResultCodes.BroadcastExpired, SignalDeliveryKinds.Start));
        Assert.Equal("2 skipped · 1 failed", SignalFormatting.ProgressDetail(2, 1, 0));
        Assert.Equal("3 skipped · 0 failed · 3 expired", SignalFormatting.ProgressDetail(3, 0, 3));
    }

    [Fact]
    public void ExecutionPositionText_NamesTheSignalWait()
    {
        Assert.Equal("Signal wait", ExecutionPositionText.NodeTypeLabel(BpmnFlowNodeTypes.IntermediateSignalCatchEvent));
        Assert.Equal("Signal start", ExecutionPositionText.NodeTypeLabel(BpmnFlowNodeTypes.SignalStartEvent));
    }

    [Fact]
    public void StartWording_CountsNewInstancesBesideWaits()
    {
        Assert.Equal("2 waits", SignalFormatting.CountsText(2, 0));
        Assert.Equal("1 new instance", SignalFormatting.CountsText(0, 1));
        Assert.Equal("1 wait and 3 new instances", SignalFormatting.CountsText(1, 3));
        Assert.Equal("1 start", SignalFormatting.StartCountText(1));
        Assert.Equal("2 starts", SignalFormatting.StartCountText(2));
        Assert.Equal("Waits", SignalFormatting.DeliveryKindLabel(SignalDeliveryKinds.Wait));
        Assert.Equal("Starts", SignalFormatting.DeliveryKindLabel(SignalDeliveryKinds.Start));

        Assert.Equal("No instance is waiting for 'a.b' now.", SignalFormatting.ReachLine("a.b", [], false));
        Assert.Equal("2 waits in 1 family: orders (2)",
            SignalFormatting.ReachLine("a.b", [new SignalSubscriptionCountDto("a.b", "orders", 2)], false));
        Assert.Equal("2 waits and 1 new instance in 2 families: orders (2), recall-notices (1 start) (list truncated)",
            SignalFormatting.ReachLine("a.b",
                [new SignalSubscriptionCountDto("a.b", "orders", 2), new SignalSubscriptionCountDto("a.b", "recall-notices", 0, 1)],
                true));
        Assert.Equal("1 wait and 1 new instance in 1 family: orders (1 and 1 start)",
            SignalFormatting.ReachLine("a.b", [new SignalSubscriptionCountDto("a.b", "orders", 1, 1)], false));
    }

    [Fact]
    public void ResultLabel_WordsAStartsOutcomesAndKeepsAWaitsWording()
    {
        Assert.Equal("Not started yet", SignalFormatting.ResultLabel(null, SignalDeliveryKinds.Start));
        Assert.Equal("Started an instance", SignalFormatting.ResultLabel(SignalDeliveryResultCodes.Started, SignalDeliveryKinds.Start));
        Assert.Equal("An instance already has this business key",
            SignalFormatting.ResultLabel(SignalDeliveryResultCodes.BusinessKeyConflict, SignalDeliveryKinds.Start));
        Assert.Equal("The workflow no longer starts on this signal",
            SignalFormatting.ResultLabel(SignalDeliveryResultCodes.SubscriptionEnded, SignalDeliveryKinds.Start));
        Assert.Equal("The workflow rejected the start",
            SignalFormatting.ResultLabel(SignalDeliveryResultCodes.DeliveryRejected, SignalDeliveryKinds.Start));
        Assert.Equal("The payload failed a mapping",
            SignalFormatting.ResultLabel(SignalDeliveryResultCodes.PayloadInvalid, SignalDeliveryKinds.Start));
        Assert.Equal("Not delivered yet", SignalFormatting.ResultLabel(null));
        Assert.Equal("The wait ended before delivery", SignalFormatting.ResultLabel(SignalDeliveryResultCodes.SubscriptionEnded));
    }

    private static SignalBroadcastDetailDto Broadcast(string status, int failed) =>
        new(1, "a.b", null, true, null, null, status, 3, 0, 3 - failed, 0, failed, 0,
            "admin", SignalThrowerKinds.User, ["admin"], null, [], false, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
}
