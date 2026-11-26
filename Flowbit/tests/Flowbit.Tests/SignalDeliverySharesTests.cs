using System.Text.Json;
using Flowbit.Service.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// EVENT-04G: how many delivery jobs a broadcast gets for one workflow definition, how the two
/// settings are read, and the job payload that names a share.
/// </summary>
public sealed class SignalDeliverySharesTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(999, 1)]
    [InlineData(1_000, 2)]
    [InlineData(1_999, 2)]
    [InlineData(2_000, 2)]
    [InlineData(10_000, 2)]
    [InlineData(250_000, 2)]
    public void JobCount_AtTheDefaultsFollowsTheSettingsTable(int recipients, int expected)
    {
        Assert.Equal((2, 500), (SignalSettings.DefaultMaxDeliveryJobsPerDefinition, SignalSettings.DefaultMinDeliveriesPerJob));
        Assert.Equal(expected, SignalDeliveryShares.JobCount(
            recipients,
            SignalSettings.DefaultMaxDeliveryJobsPerDefinition,
            SignalSettings.DefaultMinDeliveriesPerJob));
    }

    [Theory]
    [InlineData(999, 1)]
    [InlineData(1_000, 2)]
    [InlineData(1_999, 3)]
    [InlineData(2_000, 4)]
    [InlineData(10_000, 4)]
    [InlineData(250_000, 4)]
    public void JobCount_WithAMaximumOfFour(int recipients, int expected) =>
        Assert.Equal(expected, SignalDeliveryShares.JobCount(recipients, 4, SignalSettings.DefaultMinDeliveriesPerJob));

    [Theory]
    [InlineData(999, 1)]
    [InlineData(1_000, 2)]
    [InlineData(1_999, 3)]
    [InlineData(2_000, 4)]
    [InlineData(10_000, 16)]
    [InlineData(250_000, 16)]
    public void JobCount_WithAMaximumOfSixteen(int recipients, int expected) =>
        Assert.Equal(expected, SignalDeliveryShares.JobCount(recipients, 16, SignalSettings.DefaultMinDeliveriesPerJob));

    [Theory]
    [InlineData(2)]
    [InlineData(1_000)]
    [InlineData(250_000)]
    public void JobCount_WithAMaximumOfOneIsAlwaysOne(int recipients)
    {
        Assert.Equal(1, SignalDeliveryShares.JobCount(recipients, 1, 1));
        Assert.Equal(1, SignalDeliveryShares.JobCount(recipients, 1, SignalSettings.DefaultMinDeliveriesPerJob));
    }

    [Fact]
    public void JobCount_TwoRecipientsAreTheFewestAnySettingSplits()
    {
        Assert.Equal(2, SignalDeliveryShares.FewestSplittable);
        Assert.Equal(1, SignalDeliveryShares.JobCount(1, SignalSettings.MaxDeliveryJobsPerDefinitionCap, 1));
        Assert.Equal(2, SignalDeliveryShares.JobCount(2, SignalSettings.MaxDeliveryJobsPerDefinitionCap, 1));
        Assert.Equal(32, SignalDeliveryShares.JobCount(1_000, SignalSettings.MaxDeliveryJobsPerDefinitionCap, 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("four")]
    [InlineData("4.5")]
    public void ParseSettings_FallsBackToTheDefaultsForMissingAndInvalidValues(string? value)
    {
        var (maxJobs, minDeliveries) = SignalDeliveryShares.ParseSettings(value, value);
        Assert.Equal(
            (SignalSettings.DefaultMaxDeliveryJobsPerDefinition, SignalSettings.DefaultMinDeliveriesPerJob),
            (maxJobs, minDeliveries));
    }

    [Fact]
    public void ParseSettings_ReadsValuesAndCapsThem()
    {
        Assert.Equal((8, 250), SignalDeliveryShares.ParseSettings(" 8 ", "250"));
        Assert.Equal((1, 1), SignalDeliveryShares.ParseSettings("1", "1"));
        Assert.Equal((32, 100_000), SignalDeliveryShares.ParseSettings("33", "100001"));
        Assert.Equal((32, 100_000), SignalDeliveryShares.ParseSettings("2147483647", "2147483647"));
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(3, 4, true)]
    [InlineData(31, 32, true)]
    [InlineData(4, 4, false)]
    [InlineData(-1, 4, false)]
    [InlineData(0, 0, false)]
    [InlineData(0, 33, false)]
    public void IsValid_AcceptsOnlyAShareThatAJobCanOwn(int shareIndex, int shareCount, bool expected) =>
        Assert.Equal(expected, SignalDeliveryShares.IsValid(shareIndex, shareCount));

    [Fact]
    public void Payload_OfAnUnsplitJobIsExactlyTodaysAndReadsAsShareZeroOfOne()
    {
        var unsplit = JsonSerializer.SerializeToElement(new SignalBroadcastJobPayload(7, 9));
        Assert.Equal("""{"BroadcastId":7,"WorkflowDefinitionId":9}""", unsplit.GetRawText());

        var queuedBeforeTheUpgrade = JsonDocument.Parse("""{"BroadcastId":7,"WorkflowDefinitionId":9}""").RootElement
            .Deserialize<SignalBroadcastJobPayload>()!;
        Assert.Equal((0, 1, (int?)null, (int?)null),
            (queuedBeforeTheUpgrade.Share, queuedBeforeTheUpgrade.Shares, queuedBeforeTheUpgrade.ShareIndex, queuedBeforeTheUpgrade.ShareCount));
    }

    [Fact]
    public void Payload_OfAShareJobNamesItsShare()
    {
        var share = JsonSerializer.SerializeToElement(new SignalBroadcastJobPayload(7, 9, 2, 4));
        Assert.Equal("""{"BroadcastId":7,"WorkflowDefinitionId":9,"ShareIndex":2,"ShareCount":4}""", share.GetRawText());
        var read = share.Deserialize<SignalBroadcastJobPayload>()!;
        Assert.Equal((2, 4), (read.Share, read.Shares));
    }

    [Theory]
    [InlineData("signalBroadcastDeliver", "deliverShare", true)]
    [InlineData("signalBroadcastDeliver", "deliver", false)]
    [InlineData("serviceTask", "deliverShare", false)]
    public void IsShareJob_NeedsTheKindAndThePhase(string kind, string phase, bool expected) =>
        Assert.Equal(expected, SignalDeliveryShares.IsShareJob(kind, phase));
}
