using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using NCalc;

namespace Flowbit.Service.Services;

/// <summary>
/// Validates shared-variable catalog contracts and values. Both API management
/// and workflow-originated writes use this validator so persistence cannot be
/// bypassed through a lower-level producer.
/// </summary>
public static class SharedVariableValueValidator
{
    private static readonly IReadOnlySet<string> AllowedTypes = new HashSet<string>(
        [
            WorkflowVariableTypes.String,
            WorkflowVariableTypes.Number,
            WorkflowVariableTypes.Boolean,
            WorkflowVariableTypes.Date,
            WorkflowVariableTypes.DateTime,
            WorkflowVariableTypes.Json
        ],
        StringComparer.Ordinal);
    private const ExpressionOptions ValidationOptions =
        ExpressionOptions.CaseInsensitiveStringComparer
        | ExpressionOptions.AllowNullParameter;

    /// <summary>
    /// Validates the contract of a shared variable being created. Its
    /// validation must also spell NCalc's built-in functions as evaluation
    /// resolves them; with If or max it would reject every concrete value.
    /// Existing contracts keep the casing-tolerant check of
    /// <see cref="ValidateContract"/>, which every write repeats.
    /// </summary>
    public static void ValidateNewContract(
        string key,
        string dataType,
        bool isArray,
        bool nullable,
        string? validation)
    {
        ValidateContract(key, dataType, isArray, nullable, validation);
        if (string.IsNullOrWhiteSpace(validation))
        {
            return;
        }

        var misspelled = new Expression(validation, ValidationOptions).GetFunctionNames()
            .FirstOrDefault(name => !SequenceFlowConditionEvaluator.IsGeneralFunction(name));
        if (misspelled is not null)
        {
            throw new WorkflowDomainException(
                $"Shared variable '{key.Trim()}' validation uses unsupported function '{misspelled}'"
                + $"{SequenceFlowConditionEvaluator.FunctionSpellingHint(misspelled)}.");
        }
    }

    public static void ValidateContract(
        string key,
        string dataType,
        bool isArray,
        bool nullable,
        string? validation)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new WorkflowDomainException("Shared variable key is required.");
        }

        var trimmed = key.Trim();
        if (trimmed.EnumerateRunes().Count() > 300)
        {
            throw new WorkflowDomainException(
                $"Shared variable key '{trimmed}' must contain at most 300 Unicode scalar values.");
        }

        if (trimmed.StartsWith("sys.", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("config.", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("setting.", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mi.", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("gateway.", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowDomainException(
                $"Shared variable key '{trimmed}' uses a reserved context prefix.");
        }

        if (!AllowedTypes.Contains(dataType))
        {
            throw new WorkflowDomainException(
                $"Shared variable '{trimmed}' has unsupported type '{dataType}'.");
        }

        if (!string.IsNullOrWhiteSpace(validation))
        {
            var expression = new Expression(validation, ValidationOptions);
            if (expression.HasErrors())
            {
                throw new WorkflowDomainException(
                    $"Shared variable '{trimmed}' has an invalid validation expression.");
            }
            var invalidParameter = expression.GetParameterNames()
                .FirstOrDefault(name => !string.Equals(name, "value", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "null", StringComparison.OrdinalIgnoreCase));
            if (invalidParameter is not null)
            {
                throw new WorkflowDomainException(
                    $"Shared variable '{trimmed}' validation may reference only 'value'; found '{invalidParameter}'.");
            }
            // Casing-tolerant, so contracts created earlier keep accepting
            // writes; ValidateNewContract requires the exact spelling.
            var invalidFunction = expression.GetFunctionNames()
                .FirstOrDefault(name => !SequenceFlowConditionEvaluator.IsGeneralFunction(
                    name,
                    ignoreBuiltInCasing: true));
            if (invalidFunction is not null)
            {
                throw new WorkflowDomainException(
                    $"Shared variable '{trimmed}' validation uses unsupported function '{invalidFunction}'.");
            }
        }
    }

    public static void Validate(SharedVariableRecord variable, JsonElement value) =>
        Validate(
            variable.Key,
            variable.DataType,
            variable.IsArray,
            variable.Nullable,
            variable.Validation,
            value);

    public static void Validate(
        string key,
        string dataType,
        bool isArray,
        bool nullable,
        string? validation,
        JsonElement value)
    {
        ValidateContract(key, dataType, isArray, nullable, validation);

        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new WorkflowDomainException(
                $"Shared variable '{key}' value is required.");
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!nullable)
            {
                throw new WorkflowDomainException(
                    $"Shared variable '{key}' is not nullable.");
            }

            // Nullable is the complete null contract across workflow inputs,
            // scripts, service outputs, and direct catalog writes. Authored
            // validation expressions apply only to non-null values.
            return;
        }
        else if (!TypedOutputValueValidator.IsValid(value, dataType, isArray))
        {
            throw new WorkflowDomainException(
                $"Shared variable '{key}' must be {TypedOutputValueValidator.DescribeExpected(dataType, isArray)}.");
        }

        if (string.IsNullOrWhiteSpace(validation))
        {
            return;
        }

        var parameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["value"] = value
        };
        if (!SequenceFlowConditionEvaluator.Evaluate(validation, parameters))
        {
            throw new WorkflowDomainException(
                $"Shared variable '{key}' failed validation: '{validation}'.");
        }
    }
}
