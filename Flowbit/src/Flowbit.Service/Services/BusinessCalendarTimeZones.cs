namespace Flowbit.Service.Services;

/// <summary>
/// IANA time-zone handling for business calendars, using the host's time-zone data. A local time maps to the first instant
/// at which the zone's clock shows that time or later: a repeated time maps to its first occurrence, and a time that the
/// clock skips maps to the moment the clock jumps past it.
/// </summary>
public static class BusinessCalendarTimeZones
{
    public const int MaxIdLength = 100;

    private static readonly long FourteenHours = TimeSpan.FromHours(14).Ticks;
    private static readonly Lazy<IReadOnlyList<string>> IanaIds = new(LoadIanaIds);

    /// <summary>
    /// Resolves an IANA time-zone id that the host knows. The id must be written with its exact casing, because Linux
    /// hosts look ids up case-sensitively even where Windows does not.
    /// </summary>
    public static bool TryFind(string? id, out TimeZoneInfo zone, out string? problem)
    {
        zone = null!;
        problem = null;
        var trimmed = id?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            problem = "timeZone is required.";
            return false;
        }

        if (trimmed.Length > MaxIdLength
            || !TimeZoneInfo.TryFindSystemTimeZoneById(trimmed, out var found)
            || !found.HasIanaId)
        {
            problem = $"timeZone '{trimmed}' is not an IANA time zone that this server knows, such as Europe/London.";
            return false;
        }

        if (!string.Equals(found.Id, trimmed, StringComparison.Ordinal)
            && string.Equals(found.Id, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"timeZone '{trimmed}' must be written '{found.Id}'.";
            return false;
        }

        zone = found;
        return true;
    }

    /// <summary>The IANA ids of the host's time zones, in ordinal order.</summary>
    public static IReadOnlyList<string> ListIanaIds() => IanaIds.Value;

    public static DateTime ToLocal(TimeZoneInfo zone, DateTimeOffset instant) =>
        DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(instant.UtcDateTime, zone),
            DateTimeKind.Unspecified);

    /// <summary>The first instant at which the zone's clock shows <paramref name="local"/> or a later time.</summary>
    public static DateTimeOffset ToInstant(TimeZoneInfo zone, DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            return SkippedTimeInstant(zone, local);
        }

        // A repeated time is shown first under the larger offset.
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local.Ticks - offset.Ticks, TimeSpan.Zero);
    }

    // The clock jumps past a skipped time, so the first instant showing a later time is the jump itself. Offsets lie
    // within 14 hours of UTC, which bounds the search; no zone changes its offset twice within that window.
    private static DateTimeOffset SkippedTimeInstant(TimeZoneInfo zone, DateTime local)
    {
        var low = local.Ticks - FourteenHours - 1;
        var high = local.Ticks + FourteenHours;
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (ToLocal(zone, new DateTimeOffset(middle, TimeSpan.Zero)) > local)
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        return new DateTimeOffset(high, TimeSpan.Zero);
    }

    // Linux hosts list IANA zones directly; Windows hosts list Windows zones, which convert to one IANA id each.
    private static IReadOnlyList<string> LoadIanaIds()
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal) { "Etc/UTC" };
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (zone.HasIanaId)
            {
                ids.Add(zone.Id);
            }
            else if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var ianaId))
            {
                ids.Add(ianaId);
            }
        }

        return ids.ToArray();
    }
}
