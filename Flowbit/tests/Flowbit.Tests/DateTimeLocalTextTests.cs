extern alias FlowbitUi;

using Xunit;
using DateTimeLocalText = FlowbitUi::Flowbit.Ui.Components.Shared.DateTimeLocalText;

namespace Flowbit.Tests;

public sealed class DateTimeLocalTextTests
{
    [Theory]
    [InlineData("2026-12-02T09:05")]
    [InlineData("2026-12-02T09:05:00")]
    [InlineData("2026-12-02T09:05:00.000")]
    public void Parses_what_the_browser_and_Blazor_report(string text) =>
        Assert.Equal(new DateTime(2026, 12, 2, 9, 5, 0, DateTimeKind.Unspecified), DateTimeLocalText.Parse(text));

    [Fact]
    public void Keeps_seconds() =>
        Assert.Equal(new DateTime(2026, 12, 2, 9, 5, 30), DateTimeLocalText.Parse("2026-12-02T09:05:30"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-12-02")]
    [InlineData("12/02/2026 09:05")]
    [InlineData("2026-12-02T09:05+01:00")]
    public void Empty_or_other_text_is_null(string? text) =>
        Assert.Null(DateTimeLocalText.Parse(text));

    [Fact]
    public void Formats_whole_minutes_without_seconds() =>
        Assert.Equal("2026-01-04T07:05", DateTimeLocalText.Format(new DateTime(2026, 1, 4, 7, 5, 0)));

    [Fact]
    public void Formats_seconds_when_present() =>
        Assert.Equal("2026-01-04T07:05:09", DateTimeLocalText.Format(new DateTime(2026, 1, 4, 7, 5, 9)));
}
