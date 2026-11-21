namespace Flowbit.Shared.Models;

/// <summary>Why a business key cannot be used.</summary>
public enum BusinessKeyProblem
{
    None,
    Blank,
    TooLong
}

/// <summary>
/// The one normalization of a business key. A start stores a key normalized by it, and
/// a signal throw or subscription summary that names a key compares the normalized key
/// with the stored one, so the two cannot drift. A key is trimmed as .NET
/// <see cref="string.Trim()"/> trims, must not be empty, and has at most
/// <see cref="MaxLength"/> Unicode scalar values. Each caller words its own messages.
/// </summary>
public static class BusinessKeyRules
{
    public const int MaxLength = 300;

    /// <summary>
    /// Trims <paramref name="value"/> into <paramref name="key"/> and reports whether the
    /// trimmed key is usable. A null value is blank.
    /// </summary>
    public static BusinessKeyProblem Normalize(string? value, out string key)
    {
        key = value?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            return BusinessKeyProblem.Blank;
        }

        // A key of at most MaxLength UTF-16 units cannot have more scalar values.
        return key.Length > MaxLength && key.EnumerateRunes().Count() > MaxLength
            ? BusinessKeyProblem.TooLong
            : BusinessKeyProblem.None;
    }
}
