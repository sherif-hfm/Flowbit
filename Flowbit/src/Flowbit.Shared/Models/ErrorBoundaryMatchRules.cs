namespace Flowbit.Shared.Models;

/// <summary>Which precedence tier chose an error boundary.</summary>
public enum ErrorBoundaryMatchTier
{
    None,
    ExactStatus,
    StatusRange,
    CatchAll
}

/// <summary>
/// The result of selecting an error boundary for one failed invocation. Problem
/// is set when stored data is ambiguous, and then no boundary is chosen.
/// </summary>
public sealed record ErrorBoundarySelection(
    FlowNodeModel? Boundary,
    ErrorBoundaryMatchTier Tier,
    string? Problem);

/// <summary>
/// Limits, validation, canonical form, and selection for error-boundary HTTP
/// status matchers. The editor's save validator mirrors the validation.
/// </summary>
public static class ErrorBoundaryMatchRules
{
    public const int MinStatus = 100;
    public const int MaxStatus = 599;
    public const int MaxSelectors = 32;
    public const int MaxBoundariesPerRestHost = 8;

    /// <summary>
    /// A status that a selector can match. Status 0 and completed responses
    /// outside 100..599 can only reach a catch-all.
    /// </summary>
    public static bool IsSelectableStatus(int status) => status is >= MinStatus and <= MaxStatus;

    public static IReadOnlyList<FlowNodeModel> GetErrorBoundaries(WorkflowModel definition, int hostNodeId) =>
        definition.FlowNodes
            .Where(node => BpmnFlowNodeTypes.IsErrorBoundary(node.Type) && node.AttachedToRef == hostNodeId)
            .ToArray();

    public static bool IsRestServiceHost(FlowNodeModel host) =>
        BpmnFlowNodeTypes.IsServiceTask(host.Type)
        && string.Equals(host.Service?.Type, ServiceConnectorTypes.Rest, StringComparison.Ordinal);

    /// <summary>
    /// The first problem inside one matcher, or null when it is valid. It reads
    /// data deserialization lets through: null elements, and a missing range
    /// endpoint, which arrives as 0.
    /// </summary>
    public static string? FindMatcherProblem(ErrorBoundaryMatchModel match)
    {
        var codes = match.HttpStatusCodes ?? [];
        var ranges = match.HttpStatusRanges ?? [];
        if (codes.Count + ranges.Count == 0)
        {
            return "errorMatch must list at least one HTTP status or range; remove errorMatch to catch every failure.";
        }

        if (codes.Count + ranges.Count > MaxSelectors)
        {
            return $"errorMatch has {codes.Count + ranges.Count} selectors; at most {MaxSelectors} are allowed.";
        }

        var seen = new HashSet<int>();
        for (var index = 0; index < codes.Count; index++)
        {
            var code = codes[index];
            if (!IsSelectableStatus(code))
            {
                return $"errorMatch.httpStatusCodes[{index}] is {code}; a status must be from {MinStatus} through {MaxStatus}.";
            }

            if (!seen.Add(code))
            {
                return $"errorMatch.httpStatusCodes lists {code} more than once.";
            }
        }

        for (var index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            var path = $"errorMatch.httpStatusRanges[{index}]";
            if (range is null)
            {
                return $"{path} cannot be null.";
            }

            if (!IsSelectableStatus(range.From) || !IsSelectableStatus(range.To))
            {
                return $"{path} is {range.From}-{range.To}; both endpoints are required and must be from {MinStatus} through {MaxStatus}.";
            }

            if (range.From >= range.To)
            {
                return $"{path} is {range.From}-{range.To}; from must be less than to. Use httpStatusCodes for a single status.";
            }

            for (var earlier = 0; earlier < index; earlier++)
            {
                var other = ranges[earlier];
                if (other is not null && RangesIntersect(range, other))
                {
                    return $"{path} ({range.From}-{range.To}) overlaps {other.From}-{other.To} in the same errorMatch.";
                }
            }

            var inside = codes.FirstOrDefault(code => code >= range.From && code <= range.To, -1);
            if (inside != -1)
            {
                return $"errorMatch.httpStatusCodes lists {inside}, which {range.From}-{range.To} in the same errorMatch already covers.";
            }
        }

        return null;
    }

    /// <summary>
    /// The first problem with the error boundaries of one host, or null. Boundary
    /// order in the definition decides only which problem is reported first.
    /// </summary>
    public static string? FindHostProblem(FlowNodeModel host, IReadOnlyList<FlowNodeModel> boundaries)
    {
        var isRestHost = IsRestServiceHost(host);
        foreach (var boundary in boundaries)
        {
            if (boundary.ErrorMatch is null)
            {
                continue;
            }

            if (!isRestHost)
            {
                return $"Error boundary event #{boundary.Id} defines errorMatch, but host node #{host.Id} is not a REST service task.";
            }

            if (FindMatcherProblem(boundary.ErrorMatch) is { } matcherProblem)
            {
                return $"Error boundary event #{boundary.Id} on host node #{host.Id}: {matcherProblem}";
            }
        }

        if (!isRestHost && boundaries.Count > 1)
        {
            return $"Host node #{host.Id} has {boundaries.Count} error boundary events; at most one is allowed.";
        }

        if (boundaries.Count > MaxBoundariesPerRestHost)
        {
            return $"Host node #{host.Id} has {boundaries.Count} error boundary events; at most {MaxBoundariesPerRestHost} are allowed.";
        }

        var catchAlls = boundaries.Where(boundary => boundary.ErrorMatch is null).ToArray();
        if (catchAlls.Length > 1)
        {
            return $"Host node #{host.Id} has more than one catch-all error boundary event: #{catchAlls[0].Id} and #{catchAlls[1].Id}.";
        }

        var exactOwners = new Dictionary<int, FlowNodeModel>();
        foreach (var boundary in boundaries)
        {
            foreach (var code in boundary.ErrorMatch?.HttpStatusCodes ?? [])
            {
                if (!exactOwners.TryAdd(code, boundary))
                {
                    return $"Error boundary events #{exactOwners[code].Id} and #{boundary.Id} on host node #{host.Id} both match HTTP status {code}.";
                }
            }
        }

        var rangeOwners = new List<(HttpStatusRangeModel Range, FlowNodeModel Boundary)>();
        foreach (var boundary in boundaries)
        {
            foreach (var range in boundary.ErrorMatch?.HttpStatusRanges ?? [])
            {
                foreach (var (otherRange, otherBoundary) in rangeOwners)
                {
                    if (RangesIntersect(range, otherRange))
                    {
                        return $"Error boundary events #{otherBoundary.Id} ({otherRange.From}-{otherRange.To}) and #{boundary.Id} ({range.From}-{range.To}) on host node #{host.Id} have overlapping HTTP status ranges.";
                    }
                }

                rangeOwners.Add((range, boundary));
            }
        }

        return null;
    }

    /// <summary>
    /// Selects the boundary for a failed invocation: exact status, then range,
    /// then catch-all. Without a selectable status only the catch-all can match.
    /// Reads the definition without changing it.
    /// </summary>
    public static ErrorBoundarySelection Select(
        WorkflowModel definition,
        int hostNodeId,
        int? httpStatusCode)
    {
        var boundaries = GetErrorBoundaries(definition, hostNodeId);
        if (httpStatusCode is int status && IsSelectableStatus(status))
        {
            var exact = boundaries
                .Where(boundary => boundary.ErrorMatch?.HttpStatusCodes?.Contains(status) == true)
                .ToArray();
            if (exact.Length > 0)
            {
                return Resolve(exact, ErrorBoundaryMatchTier.ExactStatus, hostNodeId, $"HTTP status {status}");
            }

            var inRange = boundaries
                .Where(boundary => boundary.ErrorMatch?.HttpStatusRanges?.Any(range =>
                    range is not null && status >= range.From && status <= range.To) == true)
                .ToArray();
            if (inRange.Length > 0)
            {
                return Resolve(inRange, ErrorBoundaryMatchTier.StatusRange, hostNodeId, $"HTTP status {status}");
            }
        }

        var catchAll = boundaries.Where(boundary => boundary.ErrorMatch is null).ToArray();
        return catchAll.Length > 0
            ? Resolve(catchAll, ErrorBoundaryMatchTier.CatchAll, hostNodeId, "a catch-all")
            : new ErrorBoundarySelection(null, ErrorBoundaryMatchTier.None, null);
    }

    /// <summary>
    /// Sorts a valid matcher in place and drops its empty selector arrays. An
    /// invalid matcher is left exactly as authored, so validation can report it.
    /// </summary>
    public static void Canonicalize(ErrorBoundaryMatchModel match)
    {
        if (FindMatcherProblem(match) is not null)
        {
            return;
        }

        match.HttpStatusCodes = match.HttpStatusCodes is { Count: > 0 } codes
            ? codes.Order().ToList()
            : null;
        match.HttpStatusRanges = match.HttpStatusRanges is { Count: > 0 } ranges
            ? ranges.OrderBy(range => range.From).ThenBy(range => range.To).ToList()
            : null;
    }

    private static ErrorBoundarySelection Resolve(
        FlowNodeModel[] candidates,
        ErrorBoundaryMatchTier tier,
        int hostNodeId,
        string matched) =>
        candidates.Length == 1
            ? new ErrorBoundarySelection(candidates[0], tier, null)
            : new ErrorBoundarySelection(
                null,
                ErrorBoundaryMatchTier.None,
                $"Host node #{hostNodeId} has {candidates.Length} error boundary events matching {matched}: {string.Join(", ", candidates.Select(node => "#" + node.Id))}.");

    private static bool RangesIntersect(HttpStatusRangeModel left, HttpStatusRangeModel right) =>
        left.From <= right.To && right.From <= left.To;
}
