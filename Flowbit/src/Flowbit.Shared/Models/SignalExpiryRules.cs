namespace Flowbit.Shared.Models;

/// <summary>Why a signal broadcast's <c>expiresAfter</c> cannot be used.</summary>
public enum SignalExpiryProblem
{
    None,
    Blank,
    NotFixedDuration,
    OutOfRange
}

/// <summary>
/// The one reading of a throw's <c>expiresAfter</c> (EVENT-04E): a fixed ISO-8601 duration
/// in the grammar of timers and due dates (<see cref="TimerDefinitionRules.TryParseFixedDuration"/>),
/// from <see cref="MinDuration"/> to <see cref="MaxDuration"/>. The API service and the
/// Signals page both call it, so the two cannot disagree on what a request may send.
/// </summary>
public static class SignalExpiryRules
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(365);

    public const string BlankMessage =
        "expiresAfter cannot be blank; omit it for a signal that does not expire.";

    public const string NotFixedDurationMessage =
        "expiresAfter must be a fixed ISO-8601 duration such as PT10M or P1D.";

    public const string OutOfRangeMessage =
        "expiresAfter must be between 1 second and 365 days.";

    /// <summary>
    /// Parses <paramref name="value"/> into <paramref name="duration"/>. The text is trimmed
    /// as .NET <see cref="string.Trim()"/> trims. Years and months are refused, because their
    /// length is not fixed, and so is a zero length. A fixed duration too long for a
    /// <see cref="TimeSpan"/> is out of range, not malformed.
    /// </summary>
    public static SignalExpiryProblem Parse(string value, out TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(value);
        duration = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return SignalExpiryProblem.Blank;
        }

        if (!TimerDefinitionRules.TryParseFixedDuration(value, out var parsed))
        {
            return TimerDefinitionRules.IsFixedDurationBeyondRange(value)
                ? SignalExpiryProblem.OutOfRange
                : SignalExpiryProblem.NotFixedDuration;
        }

        if (parsed < MinDuration || parsed > MaxDuration)
        {
            return SignalExpiryProblem.OutOfRange;
        }

        duration = parsed;
        return SignalExpiryProblem.None;
    }

    /// <summary>The message the API returns for a problem, or null for <see cref="SignalExpiryProblem.None"/>.</summary>
    public static string? Message(SignalExpiryProblem problem) => problem switch
    {
        SignalExpiryProblem.Blank => BlankMessage,
        SignalExpiryProblem.NotFixedDuration => NotFixedDurationMessage,
        SignalExpiryProblem.OutOfRange => OutOfRangeMessage,
        _ => null
    };
}
