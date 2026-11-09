namespace Flowbit.Service.Services;

/// <summary>
/// Resolves IANA time-zone ids with the host's time-zone data, for business calendars and cron schedules. Problems name
/// the property that holds the id.
/// </summary>
public static class IanaTimeZones
{
    public const int MaxIdLength = 100;

    private static readonly Lazy<IReadOnlyList<string>> Ids = new(LoadIds);

    /// <summary>
    /// Resolves an IANA time-zone id that the host knows. The id must be written with its exact casing, because Linux
    /// hosts look ids up case-sensitively even where Windows does not.
    /// </summary>
    public static bool TryFind(string? id, string property, out TimeZoneInfo zone, out string? problem)
    {
        zone = null!;
        problem = null;
        var trimmed = id?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            problem = $"{property} is required.";
            return false;
        }

        if (trimmed.Length > MaxIdLength
            || !TimeZoneInfo.TryFindSystemTimeZoneById(trimmed, out var found)
            || !found.HasIanaId)
        {
            problem = $"{property} '{trimmed}' is not an IANA time zone that this server knows, such as Europe/London.";
            return false;
        }

        if (!string.Equals(found.Id, trimmed, StringComparison.Ordinal)
            && string.Equals(found.Id, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"{property} '{trimmed}' must be written '{found.Id}'.";
            return false;
        }

        zone = found;
        return true;
    }

    /// <summary>The IANA ids of the host's time zones, in ordinal order.</summary>
    public static IReadOnlyList<string> ListIds() => Ids.Value;

    // Linux hosts list IANA zones directly; Windows hosts list Windows zones, which convert to one IANA id each.
    private static IReadOnlyList<string> LoadIds()
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
