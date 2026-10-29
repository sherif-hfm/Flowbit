using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using NCalc;
using NCalc.Exceptions;

namespace Flowbit.Service.Services;

/// <summary>
/// Analyzes the NCalc expressions in timer catch and boundary schedules with
/// NCalc's parsed AST. An expression may read only declared, persisted instance
/// variables through the ordinary NCalc built-ins and Flowbit's string helpers;
/// shared aliases and runtime context are rejected so that an activation's
/// result depends only on the instance's stored values.
/// </summary>
public static class TimerExpressionDefinitionAnalyzer
{
    private const ExpressionOptions Options =
        ExpressionOptions.CaseInsensitiveStringComparer
        | ExpressionOptions.AllowNullParameter;

    private static readonly string[] ContextPrefixes =
        ["sys.", "config.", "setting.", "mi.", "gateway."];

    /// <summary>
    /// Analyzes every expression schedule in a definition and returns the plan
    /// the engine evaluates at activation. Fixed schedules and values that can
    /// never be used are left to timer validation, so a definition with only
    /// fixed timers is unaffected.
    /// </summary>
    public static TimerExpressionPlan Analyze(WorkflowModel definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var entries = ImmutableDictionary.CreateBuilder<int, TimerExpressionPlanEntry>();
        ILookup<string, string>? spellings = null;
        HashSet<string>? sharedAliases = null;
        foreach (var node in (definition.FlowNodes ?? [])
                     .Where(node => node is not null)
                     .OrderBy(node => node.Id))
        {
            if (!TryGetExpression(node, out var property, out var expression))
            {
                continue;
            }

            spellings ??= DeclarationSpellings(definition);
            sharedAliases ??= SharedAliases(definition);
            var entry = AnalyzeNode(node, property, expression, spellings, sharedAliases);
            if (!entries.TryAdd(node.Id, entry))
            {
                throw new WorkflowDomainException($"Flow node id #{node.Id} is duplicated.");
            }
        }

        return entries.Count == 0
            ? TimerExpressionPlan.Empty
            : new TimerExpressionPlan(entries.ToImmutable());
    }

    /// <summary>
    /// Finds a timer catch or boundary schedule that is an NCalc expression,
    /// returning its property and its text without the optional ${...} wrapper.
    /// </summary>
    public static bool TryGetExpression(FlowNodeModel node, out string property, out string expression)
    {
        property = string.Empty;
        expression = string.Empty;
        if (!(BpmnFlowNodeTypes.IsTimerCatch(node.Type) || BpmnFlowNodeTypes.IsTimerBoundary(node.Type))
            || node.Timer is not { } timer
            || TimerDefinitionRules.CountConfiguredExpressions(timer) != 1)
        {
            return false;
        }

        var (name, value) = !string.IsNullOrWhiteSpace(timer.TimeDate)
            ? (TimerDefinitionRules.TimeDateProperty, timer.TimeDate)
            : !string.IsNullOrWhiteSpace(timer.TimeDuration)
                ? (TimerDefinitionRules.TimeDurationProperty, timer.TimeDuration)
                : (null, null);
        if (name is null)
        {
            return false;
        }

        var classified = TimerDefinitionRules.ClassifyScheduleValue(name, value, allowExpressions: true);
        if (classified.Kind != TimerScheduleValueKind.Expression)
        {
            return false;
        }

        property = name;
        expression = classified.Expression!;
        return true;
    }

    /// <summary>Names a timer schedule property in diagnostics, such as "Timer boundary event #4 timer.timeDuration".</summary>
    public static string Describe(FlowNodeModel node, string property) =>
        $"{(BpmnFlowNodeTypes.IsTimerBoundary(node.Type) ? "Timer boundary event" : "Timer catch event")} #{node.Id} timer.{property}";

    private static TimerExpressionPlanEntry AnalyzeNode(
        FlowNodeModel node,
        string property,
        string expression,
        ILookup<string, string> spellings,
        HashSet<string> sharedAliases)
    {
        var description = Describe(node, property);
        var authored = (property == TimerDefinitionRules.TimeDateProperty
            ? node.Timer!.TimeDate
            : node.Timer!.TimeDuration)!.Trim();
        var parsed = new Expression(expression, Options);
        try
        {
            if (parsed.HasErrors())
            {
                throw new WorkflowDomainException(
                    $"{description} has an invalid NCalc expression: '{authored}'.");
            }

            // NCalc resolves its built-ins only in their exact spelling, so If or
            // max would save and then fail every activation.
            var unknownFunction = parsed.GetFunctionNames()
                .FirstOrDefault(function => !SequenceFlowConditionEvaluator.IsGeneralFunction(function));
            if (unknownFunction is not null)
            {
                throw new WorkflowDomainException(
                    $"{description} uses unsupported function '{unknownFunction}'"
                    + $"{SequenceFlowConditionEvaluator.FunctionSpellingHint(unknownFunction)}.");
            }

            var references = ResolveReferences(description, parsed.GetParameterNames(), spellings, sharedAliases);
            // Resolution trims names, but NCalc reads [ name ] with its
            // spaces, so such a reference could never get a value. Checked
            // after resolution, so context, shared, and undeclared names keep
            // their own messages.
            if (ConditionalEventDefinitionAnalyzer.FindPaddedParameter(parsed.GetParameterNames()) is { } padded)
            {
                throw new WorkflowDomainException(
                    $"{description} {ConditionalEventDefinitionAnalyzer.PaddedBracketProblem(padded)}");
            }

            var calendar = property == TimerDefinitionRules.TimeDurationProperty
                && !string.IsNullOrWhiteSpace(node.Timer.Calendar)
                    ? node.Timer.Calendar.Trim()
                    : null;
            var entry = new TimerExpressionPlanEntry(
                node.Id,
                property,
                expression,
                references,
                calendar,
                description);
            if (references.Length == 0)
            {
                ValidateConstant(entry);
            }

            return entry;
        }
        catch (WorkflowDomainException)
        {
            throw;
        }
        catch (NCalcException)
        {
            throw new WorkflowDomainException(
                $"{description} has an invalid NCalc expression: '{authored}'.");
        }
    }

    private static ImmutableArray<string> ResolveReferences(
        string description,
        IEnumerable<string> parameters,
        ILookup<string, string> spellings,
        HashSet<string> sharedAliases)
    {
        var references = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawParameter in parameters.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parameter = rawParameter.Trim();
            if (parameter.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ContextPrefixes.Any(prefix => parameter.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new WorkflowDomainException(
                    $"{description} references runtime context '{parameter}'; timer expressions can read only declared stored instance variables.");
            }

            if (sharedAliases.Contains(parameter))
            {
                throw new WorkflowDomainException(
                    $"{description} references shared variable '{parameter}'; copy its value into an instance variable before the timer activates.");
            }

            var declared = spellings[parameter].Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (declared.Length == 0)
            {
                throw new WorkflowDomainException(
                    $"{description} references undeclared stored variable '{parameter}'.");
            }

            // Only the names an expression reads must be unambiguous; other
            // declarations keep their existing rules.
            if (declared.Length > 1)
            {
                throw new WorkflowDomainException(
                    $"{description} references '{parameter}', whose stored variable declarations use ambiguous casing "
                    + $"({string.Join(", ", declared.Select(value => $"'{value}'"))}); stored variable names are case-insensitive.");
            }

            references.Add(declared[0]);
        }

        return references.ToImmutableArray();
    }

    // An expression without variables gives the same result at every
    // activation, so it is checked now, including against its calendar.
    private static void ValidateConstant(TimerExpressionPlanEntry entry)
    {
        if (!TimerExpressionEvaluator.TryEvaluate(
                entry,
                ImmutableDictionary<string, JsonElement>.Empty,
                out var text,
                out var problem))
        {
            throw new WorkflowDomainException($"{entry.Description} cannot be scheduled: {problem}.");
        }

        if (entry.Calendar is not null
            && BusinessCalendarRules.CountableDurationProblem(entry.Property, text) is { } calendarProblem)
        {
            throw new WorkflowDomainException($"Timer event #{entry.NodeId} {calendarProblem}");
        }
    }

    private static ILookup<string, string> DeclarationSpellings(WorkflowModel definition) =>
        ConditionalEventDefinitionAnalyzer.DeclaredStoredVariableNames(definition)
            .ToLookup(name => name, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> SharedAliases(WorkflowModel definition) =>
        (definition.Variables ?? [])
            .Where(variable => variable is not null
                && !string.IsNullOrWhiteSpace(variable.Name)
                && string.Equals(variable.Scope, VariableScopes.Shared, StringComparison.OrdinalIgnoreCase))
            .Select(variable => variable.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The expression schedules of one immutable definition, by timer node id.</summary>
public sealed record TimerExpressionPlan(ImmutableDictionary<int, TimerExpressionPlanEntry> EntriesByNodeId)
{
    public static TimerExpressionPlan Empty { get; } =
        new(ImmutableDictionary<int, TimerExpressionPlanEntry>.Empty);
}

/// <summary>
/// One expression schedule: its property, its NCalc text without the wrapper,
/// the canonical names of the stored variables it reads, and its literal
/// calendar name, when a duration names one.
/// </summary>
public sealed record TimerExpressionPlanEntry(
    int NodeId,
    string Property,
    string Expression,
    ImmutableArray<string> References,
    string? Calendar,
    string Description);

/// <summary>
/// Evaluates timer expressions over stored instance variables. Its problems
/// never repeat a value or an NCalc message, because both can contain instance
/// data that would otherwise reach job failures, incidents, and logs.
/// </summary>
public static class TimerExpressionEvaluator
{
    public static bool TryEvaluate(
        TimerExpressionPlanEntry entry,
        IReadOnlyDictionary<string, JsonElement> storedValues,
        out string text,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(storedValues);
        text = string.Empty;
        var parameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in entry.References)
        {
            if (storedValues.TryGetValue(name, out var value))
            {
                parameters[name] = value;
            }
        }

        object? result;
        try
        {
            result = SequenceFlowConditionEvaluator.EvaluateUnwrapped(entry.Expression, parameters);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var missing = entry.References.Where(name => !parameters.ContainsKey(name)).ToArray();
            problem = exception is NCalcParameterNotDefinedException && missing.Length > 0
                ? missing.Length == 1
                    ? $"variable '{missing[0]}' has no value"
                    : $"variables {string.Join(", ", missing.Select(name => $"'{name}'"))} have no value"
                : $"the expression could not be evaluated ({exception.GetType().Name})";
            return false;
        }

        return TimerDefinitionRules.TryInterpretExpressionResult(entry.Property, result, out text, out problem);
    }
}

/// <summary>
/// Caches timer expression plans by immutable workflow definition id. A
/// definition's analysis repeats the save-time checks, so a cached or older
/// definition cannot widen the context an expression reads.
/// </summary>
internal static class TimerExpressionPlanCache
{
    private const int MaximumEntries = 256;
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<long, CacheEntry> Entries = new();

    public static TimerExpressionPlan Get(long definitionId, WorkflowModel definition)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = Entries.AddOrUpdate(
            definitionId,
            _ => new CacheEntry(TimerExpressionDefinitionAnalyzer.Analyze(definition), now),
            (_, existing) => now - existing.LastAccess <= EntryLifetime
                ? existing with { LastAccess = now }
                : new CacheEntry(TimerExpressionDefinitionAnalyzer.Analyze(definition), now));
        if (Entries.Count > MaximumEntries)
        {
            foreach (var stale in Entries
                         .OrderBy(pair => pair.Value.LastAccess)
                         .Take(Entries.Count - MaximumEntries))
            {
                Entries.TryRemove(stale.Key, out _);
            }
        }
        return entry.Plan;
    }

    private sealed record CacheEntry(TimerExpressionPlan Plan, DateTimeOffset LastAccess);
}
