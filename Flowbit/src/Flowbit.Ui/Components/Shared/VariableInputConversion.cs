using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.Shared.Models;

namespace Flowbit.Ui.Components.Shared;

internal enum VariableInputKind
{
    Text,
    Options,
    Boolean,
    Number,
    Date,
    DateTime,
    Structured
}

internal sealed record VariableInputOption(string Value, string Text);

internal sealed record TypedInputResult(string Value, string? Error, string? TimeZone);

/// <summary>
/// Conversions between a declared variable, its form control, and the text the
/// typed form parser already accepts. A control stores exactly what a person could
/// type, so submissions stay the same for the same values.
/// </summary>
internal static partial class VariableInputConversion
{
    /// <summary>
    /// Stored for an entry the browser could not read or that cannot be sent. It is
    /// not blank, so the form never omits it, and the typed parser rejects it.
    /// </summary>
    public const string Invalid = "\u0000invalid";

    // DateTimeOffset accepts offsets of at most fourteen hours.
    private const int MaxOffsetMinutes = 14 * 60;

    public static bool IsInvalid(string? value) => string.Equals(value, Invalid, StringComparison.Ordinal);

    public static VariableInputKind KindOf(VariableModel variable)
    {
        if (variable.IsArray || variable.DataType == WorkflowVariableTypes.Json)
        {
            return VariableInputKind.Structured;
        }

        if (OptionsOf(variable).Count > 0)
        {
            return VariableInputKind.Options;
        }

        return variable.DataType switch
        {
            WorkflowVariableTypes.Boolean => VariableInputKind.Boolean,
            WorkflowVariableTypes.Number => VariableInputKind.Number,
            WorkflowVariableTypes.Date => VariableInputKind.Date,
            WorkflowVariableTypes.DateTime => VariableInputKind.DateTime,
            _ => VariableInputKind.Text
        };
    }

    public static bool SpansFullWidth(VariableModel variable) =>
        KindOf(variable) == VariableInputKind.Structured;

    /// <summary>
    /// The choices a list offers, as the text the parser receives. Number values use
    /// plain decimal notation so that exponent forms are offered as the parser expects.
    /// A number a decimal would round is left out rather than offered changed.
    /// </summary>
    public static IReadOnlyList<VariableInputOption> OptionsOf(VariableModel variable)
    {
        if (variable.IsArray || variable.Options is not { Count: > 0 } options)
        {
            return [];
        }

        var isNumber = variable.DataType == WorkflowVariableTypes.Number;
        if (!isNumber && variable.DataType != WorkflowVariableTypes.String)
        {
            return [];
        }

        var result = new List<VariableInputOption>(options.Count);
        foreach (var option in options)
        {
            string? value = option?.Value switch
            {
                { ValueKind: JsonValueKind.String } text when !isNumber && !string.IsNullOrWhiteSpace(text.GetString()) =>
                    text.GetString(),
                { ValueKind: JsonValueKind.Number } number when isNumber
                    && VariablePresentationConstraints.IsExactOptionNumber(number.GetRawText())
                    && number.TryGetDecimal(out var amount) =>
                    amount.ToString(CultureInfo.InvariantCulture),
                _ => null
            };
            if (value is not null)
            {
                result.Add(new VariableInputOption(
                    value,
                    string.IsNullOrWhiteSpace(option!.Label) ? value : option.Label.Trim()));
            }
        }

        return result;
    }

    /// <summary>A key that changes whenever the control for the variable must be rebuilt.</summary>
    public static string Signature(VariableModel variable) =>
        string.Join(
            '\u001f',
            variable.Name,
            variable.DataType,
            variable.IsArray,
            string.Join('\u001e', OptionsOf(variable).Select(option => option.Value)));

    /// <summary>The value to show in a picker for a stored value.</summary>
    public static string DomValue(VariableInputKind kind, string? stored)
    {
        if (string.IsNullOrEmpty(stored) || IsInvalid(stored))
        {
            return string.Empty;
        }

        if (kind != VariableInputKind.DateTime)
        {
            return stored;
        }

        var match = StoredDateTimePattern().Match(stored);
        return match.Success ? match.Groups["local"].Value : string.Empty;
    }

    /// <summary>Converts what a picker reported into the stored value and any message.</summary>
    public static TypedInputResult Interpret(VariableInputKind kind, TypedInputEventArgs args)
    {
        var value = args.Value ?? string.Empty;
        if (args.BadInput)
        {
            return new(Invalid, kind switch
            {
                VariableInputKind.Date => "Enter a complete date.",
                VariableInputKind.DateTime => "Enter a complete date and time.",
                _ => "Enter a number in plain decimal notation, for example 1250.50."
            }, null);
        }

        if (value.Length == 0)
        {
            return new(string.Empty, null, null);
        }

        switch (kind)
        {
            case VariableInputKind.Number:
                return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
                    ? new(value, null, null)
                    : new(Invalid, "Enter a number in plain decimal notation, for example 1250.50.", null);
            case VariableInputKind.Date:
                return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    ? new(value, null, null)
                    : new(Invalid, "Enter a date between 0001-01-01 and 9999-12-31.", null);
            case VariableInputKind.DateTime:
                if (args.SkippedLocalTime)
                {
                    return new(
                        Invalid,
                        $"This time does not exist in {args.TimeZone ?? "your time zone"} because of a daylight-saving change.",
                        args.TimeZone);
                }

                return ComposeDateTime(value, args.OffsetMinutes) is { } composed
                    ? new(composed, null, args.TimeZone)
                    : new(Invalid, "Your browser did not report a UTC offset for this time. Choose another time.", args.TimeZone);
            default:
                return new(value, null, null);
        }
    }

    /// <summary>
    /// Adds the browser's offset to a local date and time: <c>yyyy-MM-ddTHH:mm:ss</c>
    /// followed by <c>Z</c> for a zero offset or <c>±HH:mm</c>.
    /// </summary>
    public static string? ComposeDateTime(string local, int? offsetMinutes)
    {
        var match = LocalDateTimePattern().Match(local);
        if (!match.Success || offsetMinutes is not { } minutes || Math.Abs(minutes) > MaxOffsetMinutes)
        {
            return null;
        }

        var seconds = match.Groups["second"].Success ? match.Groups["second"].Value : "00";
        var composed = $"{match.Groups["minute"].Value}:{seconds}{FormatOffset(minutes)}";
        return DateTimeOffset.TryParse(composed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            ? composed
            : null;
    }

    public static string FormatOffset(int minutes)
    {
        if (minutes == 0)
        {
            return "Z";
        }

        var magnitude = Math.Abs(minutes);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(minutes < 0 ? '-' : '+')}{magnitude / 60:00}:{magnitude % 60:00}");
    }

    [GeneratedRegex(@"^(?<minute>\d{4}-\d{2}-\d{2}T\d{2}:\d{2})(?::(?<second>\d{2})(?:\.\d+)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalDateTimePattern();

    [GeneratedRegex(@"^(?<local>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}):\d{2}(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex StoredDateTimePattern();
}
