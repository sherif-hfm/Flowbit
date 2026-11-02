using System.Text;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class UnicodeTextTests
{
    private const string Emoji = "\U0001F600";

    [Theory]
    [InlineData("", 3, "")]
    [InlineData("abc", 3, "abc")]
    [InlineData("abcd", 3, "abc")]
    [InlineData("abc", 0, "")]
    [InlineData(Emoji + Emoji, 2, Emoji + Emoji)]
    [InlineData(Emoji + Emoji + Emoji, 2, Emoji + Emoji)]
    [InlineData("a" + Emoji + Emoji, 2, "a" + Emoji)]
    [InlineData(Emoji + "ab", 1, Emoji)]
    public void Limit_counts_whole_characters(string value, int maxCharacters, string expected) =>
        Assert.Equal(expected, UnicodeText.Limit(value, maxCharacters));

    // Either parity must leave text the database driver's strict UTF-8 encoding accepts.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Limit_never_leaves_half_a_surrogate_pair(int padding)
    {
        var value = new string('x', padding) + string.Concat(Enumerable.Repeat(Emoji, 600));

        var limited = UnicodeText.Limit(value, 500);

        new UTF8Encoding(false, throwOnInvalidBytes: true).GetByteCount(limited);
        Assert.Equal(500, limited.EnumerateRunes().Count());
    }

    [Fact]
    public void Limit_keeps_text_within_the_limit_when_its_utf16_length_is_over_it()
    {
        var value = string.Concat(Enumerable.Repeat(Emoji, 600));

        Assert.Equal(value, UnicodeText.Limit(value, 600));
    }
}
