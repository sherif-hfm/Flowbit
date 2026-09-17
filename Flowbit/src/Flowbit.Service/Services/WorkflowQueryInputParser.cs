using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

/// <summary>
/// Shared input parsing for query surfaces: legacy "name:value" variable
/// filters, structured search-sort conversion to legacy sort clauses, and the
/// common sort-clause grammar used by the instance and inbox paths.
/// </summary>
internal static class WorkflowQueryInputParser
{
    // Parses raw "name:value" filter strings (split on the first ':') into
    // exact-match VariableFilters. Malformed or empty-name entries are rejected.
    internal static IReadOnlyList<VariableFilter> ParseVariableFilters(
        IReadOnlyList<string>? variables)
    {
        if (variables is null || variables.Count == 0)
        {
            return [];
        }

        var filters = new List<VariableFilter>(variables.Count);
        foreach (var raw in variables)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var separator = raw.IndexOf(':');
            if (separator <= 0)
            {
                throw new WorkflowDomainException(
                    $"Invalid variable filter '{raw}'. Expected format 'name:value'.");
            }

            var name = raw[..separator].Trim();
            var value = raw[(separator + 1)..].Trim();
            if (name.Length == 0)
            {
                throw new WorkflowDomainException(
                    $"Invalid variable filter '{raw}'. Variable name is required.");
            }

            filters.Add(new VariableFilter(name, value));
        }

        return filters;
    }

    internal static IReadOnlyList<string>? ToLegacySort(
        IReadOnlyList<SearchSortDto>? sort) =>
        sort?.Select(static criterion =>
            criterion is null
                ? string.Empty
                : $"{criterion.Field}:{criterion.Direction}").ToArray();

    internal static IReadOnlyList<TCriterion> ParseSort<TField, TCriterion>(
        IReadOnlyList<string> sort,
        Func<string, TField> parseField,
        Func<TField, SortDirection, TCriterion> createCriterion)
        where TField : struct, Enum
    {
        const int maxSortCriteria = 3;
        if (sort.Count > maxSortCriteria)
        {
            throw new WorkflowDomainException($"At most {maxSortCriteria} sort clauses are allowed.");
        }

        var result = new List<TCriterion>(sort.Count);
        var fields = new HashSet<TField>();
        foreach (var raw in sort)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new WorkflowDomainException("Sort clauses must not be blank. Expected format 'field:asc' or 'field:desc'.");
            }

            var separator = raw.IndexOf(':');
            if (separator <= 0 || separator == raw.Length - 1 || raw.IndexOf(':', separator + 1) >= 0)
            {
                throw new WorkflowDomainException(
                    $"Invalid sort clause '{raw}'. Expected format 'field:asc' or 'field:desc'.");
            }

            var fieldText = raw[..separator].Trim();
            var directionText = raw[(separator + 1)..].Trim();
            if (fieldText.Length == 0 || directionText.Length == 0)
            {
                throw new WorkflowDomainException(
                    $"Invalid sort clause '{raw}'. Expected format 'field:asc' or 'field:desc'.");
            }

            var field = parseField(fieldText);
            if (!fields.Add(field))
            {
                throw new WorkflowDomainException($"Sort field '{fieldText}' was specified more than once.");
            }

            var direction = directionText.ToLowerInvariant() switch
            {
                "asc" => SortDirection.Ascending,
                "desc" => SortDirection.Descending,
                _ => throw new WorkflowDomainException(
                    $"Unknown sort direction '{directionText}'. Allowed directions: asc, desc.")
            };
            result.Add(createCriterion(field, direction));
        }

        return result;
    }
}
