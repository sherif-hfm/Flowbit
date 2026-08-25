using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using NCalc;
using NCalc.Helpers;

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
    private static readonly IReadOnlySet<string> AllowedFunctions = new HashSet<string>(
        BuiltInFunctionHelper.GetBuiltInFunctionNames()
            .Concat([
                "Length", "Len", "IsNullOrEmpty", "IsNullOrWhiteSpace",
                "Contains", "StartsWith", "EndsWith", "Lower", "Upper",
                "Trim", "IsMatch"
            ]),
        StringComparer.OrdinalIgnoreCase);

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
            var expression = new Expression(
                validation,
                ExpressionOptions.CaseInsensitiveStringComparer
                | ExpressionOptions.AllowNullParameter);
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
            var invalidFunction = expression.GetFunctionNames()
                .FirstOrDefault(name => !AllowedFunctions.Contains(name));
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
