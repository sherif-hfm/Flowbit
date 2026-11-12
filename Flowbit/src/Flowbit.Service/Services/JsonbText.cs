using System.Text.Json;

namespace Flowbit.Service.Services;

/// <summary>
/// Counts the UTF-8 bytes of a value as PostgreSQL prints it after storing it as
/// <c>jsonb</c>, which is what <c>octet_length(value::text)</c> measures in a check
/// constraint. That text differs from compact JSON: a space follows every comma and
/// colon, strings are raw UTF-8 with only quotes, backslashes, and control characters
/// escaped, and numbers are written out in full, so <c>1E+300</c> prints 301 digits.
/// A limit that a database check also enforces is measured with this, so the code
/// refuses a value before the database would.
/// </summary>
public static class JsonbText
{
    // PostgreSQL refuses an exponent beyond 1,000. A larger one only has to count as
    // far too large, without overflowing.
    private const long ExponentCeiling = 10_000_000;

    public static long ByteCount(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                long bytes = 2;
                var first = true;
                foreach (var property in value.EnumerateObject())
                {
                    // ", " between members and ": " after each key. A repeated key keeps
                    // only its last value in jsonb, so counting both errs high.
                    bytes += (first ? 0 : 2) + StringBytes(property.Name) + 2 + ByteCount(property.Value);
                    first = false;
                }
                return bytes;
            }
            case JsonValueKind.Array:
            {
                long bytes = 2;
                var first = true;
                foreach (var item in value.EnumerateArray())
                {
                    bytes += (first ? 0 : 2) + ByteCount(item);
                    first = false;
                }
                return bytes;
            }
            case JsonValueKind.String:
                return StringBytes(value.GetString()!);
            case JsonValueKind.Number:
                return NumberBytes(value.GetRawText());
            case JsonValueKind.True:
            case JsonValueKind.Null:
                return 4;
            case JsonValueKind.False:
                return 5;
            default:
                throw new ArgumentException("Only a defined JSON value has a stored size.", nameof(value));
        }
    }

    private static long StringBytes(string text)
    {
        long bytes = 2;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character < 0x80)
            {
                bytes += character switch
                {
                    '"' or '\\' or '\b' or '\f' or '\n' or '\r' or '\t' => 2,
                    < ' ' => 6,
                    _ => 1
                };
            }
            else if (character < 0x800)
            {
                bytes += 2;
            }
            else if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                bytes += 4;
                index++;
            }
            else
            {
                bytes += 3;
            }
        }
        return bytes;
    }

    // numeric_out of the value numeric_in reads: the integer part without leading
    // zeros (at least "0"), then a point and as many decimals as the literal had after
    // its point, less its exponent. A zero prints without a sign.
    private static long NumberBytes(string literal)
    {
        var index = 0;
        var negative = literal[index] == '-';
        if (negative) index++;
        var integerStart = index;
        while (index < literal.Length && char.IsAsciiDigit(literal[index])) index++;
        var digits = literal[integerStart..index];
        var fractionLength = 0;
        if (index < literal.Length && literal[index] == '.')
        {
            var fractionStart = ++index;
            while (index < literal.Length && char.IsAsciiDigit(literal[index])) index++;
            fractionLength = index - fractionStart;
            digits += literal[fractionStart..index];
        }

        long exponent = 0;
        if (index < literal.Length && literal[index] is 'e' or 'E')
        {
            index++;
            var exponentNegative = index < literal.Length && literal[index] == '-';
            if (index < literal.Length && literal[index] is '+' or '-') index++;
            for (; index < literal.Length; index++)
                exponent = Math.Min(ExponentCeiling, exponent * 10 + (literal[index] - '0'));
            if (exponentNegative) exponent = -exponent;
        }

        var shift = exponent - fractionLength;
        var scale = Math.Max(0, -shift);
        long integerLength;
        if (shift >= 0)
        {
            var significant = digits.TrimStart('0').Length;
            integerLength = significant == 0 ? 1 : significant + shift;
        }
        else
        {
            var kept = digits.Length - scale;
            integerLength = kept <= 0 ? 1 : Math.Max(1, digits[..(int)kept].TrimStart('0').Length);
        }

        var zero = digits.All(digit => digit == '0');
        return (negative && !zero ? 1 : 0) + integerLength + (scale > 0 ? 1 + scale : 0);
    }
}
