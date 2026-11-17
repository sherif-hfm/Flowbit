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
    public void LiveCancelAndRetry_FollowTheBroadcastStatus()
    {
        Assert.True(SignalFormatting.IsLive(SignalBroadcastStatuses.Delivering));
        Assert.False(SignalFormatting.IsLive(SignalBroadcastStatuses.Completed));
        Assert.True(SignalFormatting.CanCancel(SignalBroadcastStatuses.Delivering));
        Assert.False(SignalFormatting.CanCancel(SignalBroadcastStatuses.Cancelled));
        Assert.True(SignalFormatting.CanRetry(Broadcast(SignalBroadcastStatuses.CompletedWithIssues, failed: 2)));
        Assert.False(SignalFormatting.CanRetry(Broadcast(SignalBroadcastStatuses.CompletedWithIssues, failed: 0)));
        Assert.False(SignalFormatting.CanRetry(Broadcast(SignalBroadcastStatuses.Delivering, failed: 2)));
    }

    [Fact]
    public void ExecutionPositionText_NamesTheSignalWait()
    {
        Assert.Equal("Signal wait", ExecutionPositionText.NodeTypeLabel(BpmnFlowNodeTypes.IntermediateSignalCatchEvent));
    }

    private static SignalBroadcastDetailDto Broadcast(string status, int failed) =>
        new(1, "a.b", null, true, status, 3, 0, 3 - failed, 0, failed, 0,
            "admin", SignalThrowerKinds.User, ["admin"], null, [], false, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
}
