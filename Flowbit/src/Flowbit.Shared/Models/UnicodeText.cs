using System.Text;

namespace Flowbit.Shared.Models;

/// <summary>Text limits counted in Unicode characters, the way PostgreSQL counts a varchar.</summary>
public static class UnicodeText
{
    /// <summary>
    /// The first <paramref name="maxCharacters"/> Unicode characters of a value. Cutting by
    /// UTF-16 length can split a surrogate pair, and the database driver then refuses the
    /// whole write, which would fail the transaction that was recording the text.
    /// </summary>
    public static string Limit(string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters)
        {
            return value;
        }

        var builder = new StringBuilder(maxCharacters);
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count == maxCharacters)
            {
                return builder.ToString();
            }

            builder.Append(rune.ToString());
            count++;
        }

        return value;
    }
}
