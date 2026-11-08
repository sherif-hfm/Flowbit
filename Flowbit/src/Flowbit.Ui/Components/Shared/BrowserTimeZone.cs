using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Flowbit.Ui.Components.Shared;

/// <summary>
/// The time zone the UI shows and reads times in. Flowbit.Ui renders on the server, where
/// <see cref="DateTimeOffset.ToLocalTime"/> and <see cref="DateTimeKind.Local"/> mean the
/// server's zone, so every page converts through the browser's IANA zone instead. The
/// circuit learns it after its first interactive render (<see cref="BrowserTimeZoneProvider"/>)
/// and cascades it to every component. Until then, during prerender, and for a zone the
/// server cannot find, times use UTC and every displayed time says so.
/// </summary>
public sealed partial class BrowserTimeZone
{
    private const string UtcSuffix = " UTC";

    private BrowserTimeZone(TimeZoneInfo zone, string? name, bool reported, string? unknownName)
    {
        Zone = zone;
        Name = name;
        Reported = reported;
        UnknownName = unknownName;
    }

    /// <summary>The UTC fallback used before the browser has reported its zone.</summary>
    public static BrowserTimeZone Pending { get; } = new(TimeZoneInfo.Utc, null, false, null);

    /// <summary>The zone times are converted to.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>The browser's IANA zone name, or null while times use the UTC fallback.</summary>
    public string? Name { get; }

    /// <summary>True once the browser has answered, whether or not the server could use its zone.</summary>
    public bool Reported { get; }

    /// <summary>A zone name the browser reported that the server cannot find.</summary>
    public string? UnknownName { get; }

    /// <summary>True while times fall back to UTC instead of the browser's zone.</summary>
    [MemberNotNullWhen(false, nameof(Name))]
    public bool IsUtcFallback => Name is null;

    /// <summary>The zone's name for a label: the browser's zone name, or UTC.</summary>
    public string DisplayName => Name ?? "UTC";

    /// <summary>A sentence saying which zone the page's times use, for a form or filter.</summary>
    public string Description =>
        Name is not null ? $"Times use your browser's time zone, {Name}."
        : !Reported ? "Times use UTC until your browser's time zone is known."
        : UnknownName is not null ? $"Times use UTC because the server does not recognize your browser's time zone, {UnknownName}."
        : "Times use UTC because your browser did not report a time zone.";

    /// <summary>
    /// The zone a browser reported through <c>Intl.DateTimeFormat</c>. A name the server cannot
    /// find, or no name at all, keeps the UTC fallback.
    /// </summary>
    public static BrowserTimeZone FromBrowser(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !ZoneNamePattern().IsMatch(trimmed))
        {
            // Only a well-formed name reaches the lookup or the page; anything else is not a zone.
            return new(TimeZoneInfo.Utc, null, true, null);
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(trimmed, out var zone)
            ? new(zone, trimmed, true, null)
            : new(TimeZoneInfo.Utc, null, true, trimmed);
    }

    /// <summary>The wall-clock date and time of an instant in this zone.</summary>
    public DateTime ToLocal(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTimeFromUtc(instant.UtcDateTime, Zone);

    /// <summary>
    /// An instant as text in this zone, in the server culture's <paramref name="format"/>, followed
    /// by <c>UTC</c> while the UTC fallback is in use.
    /// </summary>
    public string Format(DateTimeOffset instant, string format = "g")
    {
        var text = ToLocal(instant).ToString(format, CultureInfo.CurrentCulture);
        return IsUtcFallback ? text + UtcSuffix : text;
    }

    /// <summary>The <c>datetime-local</c> text that shows an instant in this zone.</summary>
    public string FormatInput(DateTimeOffset instant) => DateTimeLocalText.Format(ToLocal(instant));

    /// <summary>
    /// Reads a <c>datetime-local</c> value as a wall-clock time in this zone and returns the instant
    /// it names, with a zero offset. Empty or incomplete text is no time. A time that a daylight-saving
    /// change skips is refused, and a time it repeats means its first occurrence, as
    /// <c>typed-inputs.js</c> reads the typed workflow inputs.
    /// </summary>
    public bool TryReadInput(string? text, out DateTimeOffset? instant, [NotNullWhen(false)] out string? problem)
    {
        instant = null;
        problem = null;
        if (DateTimeLocalText.Parse(text) is not { } local)
        {
            return true;
        }

        var shown = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (Zone.IsInvalidTime(local))
        {
            problem = $"{shown} does not exist in {DisplayName} because of a daylight-saving change.";
            return false;
        }

        // The earliest instant has the larger offset: the one in force before the clocks went back.
        var offset = Zone.IsAmbiguousTime(local) ? Zone.GetAmbiguousTimeOffsets(local).Max() : Zone.GetUtcOffset(local);
        var ticks = local.Ticks - offset.Ticks;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            problem = $"{shown} in {DisplayName} is outside the supported range of dates.";
            return false;
        }

        instant = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    // IANA names are ASCII words joined by slashes, such as America/Argentina/Buenos_Aires or Etc/GMT+5.
    [GeneratedRegex(@"^[A-Za-z0-9_+\-]{1,40}(?:/[A-Za-z0-9_+\-]{1,40}){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneNamePattern();
}
