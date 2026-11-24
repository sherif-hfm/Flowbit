using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>EVENT-04E: the one reading of a throw's expiresAfter, shared by the API and the Signals page.</summary>
public sealed class SignalExpiryRulesTests
{
    [Theory]
    [InlineData("PT1S", 1)]
    [InlineData("PT10M", 600)]
    [InlineData("P1D", 86_400)]
    [InlineData("P52W", 31_449_600)]
    [InlineData("P365D", 31_536_000)]
    [InlineData("  PT30S  ", 30)]
    [InlineData("PT1.5S", 1.5)]
    public void Parse_AcceptsAFixedDurationFromOneSecondToAYear(string value, double seconds)
    {
        Assert.Equal(SignalExpiryProblem.None, SignalExpiryRules.Parse(value, out var duration));
        Assert.Equal(TimeSpan.FromSeconds(seconds), duration);
    }

    [Theory]
    [InlineData("", SignalExpiryProblem.Blank)]
    [InlineData("   ", SignalExpiryProblem.Blank)]
    [InlineData("P1M", SignalExpiryProblem.NotFixedDuration)]
    [InlineData("P1Y", SignalExpiryProblem.NotFixedDuration)]
    [InlineData("10m", SignalExpiryProblem.NotFixedDuration)]
    [InlineData("PT0S", SignalExpiryProblem.NotFixedDuration)]
    [InlineData("PT", SignalExpiryProblem.NotFixedDuration)]
    [InlineData("PT0.5S", SignalExpiryProblem.OutOfRange)]
    [InlineData("P366D", SignalExpiryProblem.OutOfRange)]
    [InlineData("P53W", SignalExpiryProblem.OutOfRange)]
    // Fixed durations too long for a TimeSpan: past its ticks, and past a decimal.
    [InlineData("P9999999999999999D", SignalExpiryProblem.OutOfRange)]
    [InlineData("PT9223372036854775807S", SignalExpiryProblem.OutOfRange)]
    [InlineData("P99999999999999999999999999999W", SignalExpiryProblem.OutOfRange)]
    [InlineData("P99999999999999999999999999999M", SignalExpiryProblem.NotFixedDuration)]
    public void Parse_GivesEachProblem(string value, SignalExpiryProblem expected)
    {
        Assert.Equal(expected, SignalExpiryRules.Parse(value, out var duration));
        Assert.Equal(TimeSpan.Zero, duration);
    }

    [Theory]
    [InlineData("P9999999999999999D", true)]
    [InlineData("  PT9223372036854775807S  ", true)]
    [InlineData("P99999999999999999999999999999W", true)]
    [InlineData("PT1.5S", false)]
    [InlineData("P1D", false)]
    [InlineData("PT0S", false)]
    [InlineData("PT0.00000001S", false)]
    [InlineData("P1M", false)]
    [InlineData("PT", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsFixedDurationBeyondRange_IsTrueOnlyForAWellFormedLengthTooLongToHold(string? value, bool expected)
    {
        Assert.Equal(expected, TimerDefinitionRules.IsFixedDurationBeyondRange(value));
        // The parser's answers are unchanged: it refuses every one of them except a usable length.
        Assert.Equal(value is "PT1.5S" or "P1D", TimerDefinitionRules.TryParseFixedDuration(value, out _));
    }

    [Fact]
    public void IsFixedDurationBeyondRange_KeepsTheGrammarsLengthLimit()
    {
        var tooLongText = "P" + new string('9', TimerDefinitionRules.MaxExpressionLength) + "D";
        Assert.False(TimerDefinitionRules.IsFixedDurationBeyondRange(tooLongText));
        Assert.Equal(SignalExpiryProblem.NotFixedDuration, SignalExpiryRules.Parse(tooLongText, out _));
    }

    [Fact]
    public void Parse_ReadsEqualLengthsAsOne()
    {
        SignalExpiryRules.Parse("PT60S", out var seconds);
        SignalExpiryRules.Parse("PT1M", out var minute);
        Assert.Equal(minute, seconds);
        SignalExpiryRules.Parse("P1W", out var week);
        SignalExpiryRules.Parse("P7D", out var days);
        Assert.Equal(week, days);
    }

    [Fact]
    public void Message_WordsEachProblem()
    {
        Assert.Null(SignalExpiryRules.Message(SignalExpiryProblem.None));
        Assert.Equal("expiresAfter cannot be blank; omit it for a signal that does not expire.",
            SignalExpiryRules.Message(SignalExpiryProblem.Blank));
        Assert.Equal("expiresAfter must be a fixed ISO-8601 duration such as PT10M or P1D.",
            SignalExpiryRules.Message(SignalExpiryProblem.NotFixedDuration));
        Assert.Equal("expiresAfter must be between 1 second and 365 days.",
            SignalExpiryRules.Message(SignalExpiryProblem.OutOfRange));
        Assert.Equal((TimeSpan.FromSeconds(1), TimeSpan.FromDays(365)),
            (SignalExpiryRules.MinDuration, SignalExpiryRules.MaxDuration));
    }
}
