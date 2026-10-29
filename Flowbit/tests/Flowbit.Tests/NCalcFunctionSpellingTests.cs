using System.Text.Json;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using NCalc.Exceptions;
using NCalc.Helpers;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// NCalc resolves its built-in functions only in one spelling each, while
/// Flowbit's string helpers accept any casing. Validators use the evaluator's
/// list of spellings, so an expression that saves also resolves.
/// </summary>
public sealed class NCalcFunctionSpellingTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoVariables =
        new Dictionary<string, JsonElement>();

    // One call per built-in, in the spelling evaluation resolves.
    public static TheoryData<string, string> BuiltInCalls => new()
    {
        { "Abs", "(-1)" }, { "Acos", "(1)" }, { "Asin", "(1)" }, { "Atan", "(1)" },
        { "Atan2", "(1, 1)" }, { "Ceiling", "(1.5)" }, { "Cos", "(0)" }, { "EscapeLike", "('a')" },
        { "Exp", "(0)" }, { "Floor", "(1.5)" }, { "IEEERemainder", "(3, 2)" }, { "Ln", "(1)" },
        { "Log", "(8, 2)" }, { "Log10", "(10)" }, { "Max", "(1, 2)" }, { "Min", "(1, 2)" },
        { "Pow", "(2, 3)" }, { "Round", "(1.5, 0)" }, { "Sign", "(-2)" }, { "Sin", "(0)" },
        { "Sqrt", "(4)" }, { "Tan", "(0)" }, { "Truncate", "(1.5)" }, { "if", "(true, 1, 2)" },
        { "ifs", "(true, 1, 2)" }, { "in", "(1, 1, 2)" }
    };

    [Theory]
    [MemberData(nameof(BuiltInCalls))]
    public void EachListedSpellingResolvesAndNoOtherCasingDoes(string spelling, string arguments)
    {
        Assert.Contains(spelling, SequenceFlowConditionEvaluator.BuiltInFunctionSpellings);
        Assert.True(Resolves(spelling + arguments), $"{spelling} did not resolve.");
        var otherCasings = new[]
            {
                spelling.ToLowerInvariant(),
                spelling.ToUpperInvariant(),
                char.ToUpperInvariant(spelling[0]) + spelling[1..].ToLowerInvariant()
            }
            .Where(candidate => !string.Equals(candidate, spelling, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
        foreach (var other in otherCasings)
        {
            Assert.False(Resolves(other + arguments), $"{other} resolved, so the spelling list is stale.");
        }
    }

    [Fact]
    public void TheListNamesEveryNCalcBuiltIn()
    {
        // A new NCalc version that adds a built-in fails here until the list
        // gains its spelling and a call above.
        Assert.Equal(BuiltInCalls.Count, SequenceFlowConditionEvaluator.BuiltInFunctionSpellings.Count);
        foreach (var name in BuiltInFunctionHelper.GetBuiltInFunctionNames())
        {
            Assert.Contains(
                SequenceFlowConditionEvaluator.BuiltInFunctionSpellings,
                spelling => string.Equals(spelling, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("if", true, "")]
    [InlineData("If", false, "; write it as 'if'")]
    [InlineData("Max", true, "")]
    [InlineData("max", false, "; write it as 'Max'")]
    [InlineData("ieeeremainder", false, "; write it as 'IEEERemainder'")]
    [InlineData("LEN", true, "")]
    [InlineData("isMatch", true, "")]
    [InlineData("Now", false, "")]
    [InlineData("FlowInfo", false, "")]
    public void IsGeneralFunction_TakesBuiltInsInTheirSpellingAndHelpersInAnyCasing(
        string name,
        bool accepted,
        string hint)
    {
        Assert.Equal(accepted, SequenceFlowConditionEvaluator.IsGeneralFunction(name));
        Assert.Equal(hint, SequenceFlowConditionEvaluator.FunctionSpellingHint(name));
    }

    [Theory]
    [InlineData("If", true)]
    [InlineData("MAX", true)]
    [InlineData("Now", false)]
    public void IsGeneralFunction_CanIgnoreBuiltInCasingForWhatWasSavedEarlier(string name, bool accepted) =>
        Assert.Equal(accepted, SequenceFlowConditionEvaluator.IsGeneralFunction(name, ignoreBuiltInCasing: true));

    [Fact]
    public void ASharedVariableCreatedNowMustSpellBuiltInsAsEvaluationResolvesThem()
    {
        var error = Assert.Throws<WorkflowDomainException>(() =>
            SharedVariableValueValidator.ValidateNewContract(
                "examples.limit",
                WorkflowVariableTypes.Number,
                isArray: false,
                nullable: true,
                "If(value > 0, true, false)"));

        Assert.Equal(
            "Shared variable 'examples.limit' validation uses unsupported function 'If'; write it as 'if'.",
            error.Message);
        SharedVariableValueValidator.ValidateNewContract(
            "examples.limit",
            WorkflowVariableTypes.Number,
            isArray: false,
            nullable: true,
            "if(value > 0, true, false) and Max(value, 1) > 0 and LEN('x') == 1");
    }

    [Fact]
    public void ASharedVariableCreatedEarlierKeepsAcceptingTheWritesItAcceptedBefore()
    {
        // Every write repeats the contract check, so it must not start
        // rejecting a contract that was saved with another casing.
        SharedVariableValueValidator.ValidateContract(
            "examples.limit",
            WorkflowVariableTypes.Number,
            isArray: false,
            nullable: true,
            "If(value > 0, true, false)");
        SharedVariableValueValidator.Validate(
            "examples.limit",
            WorkflowVariableTypes.Number,
            isArray: false,
            nullable: true,
            "If(value > 0, true, false)",
            JsonSerializer.SerializeToElement<object?>(null));
    }

    [Fact]
    public void ASharedBindingInANewDefinitionMustSpellBuiltInsAsEvaluationResolvesThem()
    {
        var model = TimerExpressionDefinitionTests.Model("contractEnd", "PT1H");
        model.Variables.Single(variable => variable.Name == "approvalAmount").Validation = "max(value, 0) > 0";

        var error = Assert.Throws<WorkflowDomainException>(() => TimerExpressionDefinitionTests.Validate(model));

        Assert.Equal(
            "Shared process variable 'approvalAmount' validation uses unsupported function 'max'; write it as 'Max'.",
            error.Message);
    }

    // NCalc reports a built-in it does not resolve as a missing function; any
    // other outcome, such as an argument it cannot use, means it resolved.
    private static bool Resolves(string expression)
    {
        try
        {
            SequenceFlowConditionEvaluator.EvaluateUnwrapped(expression, NoVariables);
            return true;
        }
        catch (NCalcFunctionNotFoundException)
        {
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
