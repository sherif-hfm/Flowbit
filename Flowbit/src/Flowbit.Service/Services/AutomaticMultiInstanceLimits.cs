using System.Globalization;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>Limits are resolved once at body initialization, never during refill.</summary>
public static class AutomaticMultiInstanceLimits
{
    public const string DefaultConcurrencyKey = "Workflow.MultiInstance.DefaultConcurrency";
    public const string MaxConcurrencyKey = "Workflow.MultiInstance.MaxConcurrency";
    public const int DefaultConcurrency = 4;
    public const int MaximumConcurrency = 1000;

    public static int ParseConcurrency(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? Math.Min(parsed, MaximumConcurrency)
            : fallback;

    public static int ResolveConcurrency(MultiInstanceModel configuration, int total,
        string? configuredDefault, string? configuredMaximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        if (configuration.MaxConcurrency is < 1 or > MaximumConcurrency)
            throw new ArgumentOutOfRangeException(nameof(configuration));
        if (configuration.Mode == MultiInstanceModes.Sequential)
        {
            if (configuration.MaxConcurrency is not (null or 1))
                throw new ArgumentException("Sequential bodies permit only maxConcurrency=1.", nameof(configuration));
            return Math.Min(1, total);
        }
        if (configuration.Mode != MultiInstanceModes.Parallel)
            throw new ArgumentException("Unknown multi-instance mode.", nameof(configuration));
        return Math.Min(total, Math.Min(
            configuration.MaxConcurrency ?? ParseConcurrency(configuredDefault, DefaultConcurrency),
            ParseConcurrency(configuredMaximum, MaximumConcurrency)));
    }
}
