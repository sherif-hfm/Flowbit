using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flowbit.Shared.Models;

/// <summary>
/// Wording for a request or workflow document the JSON serializer refused. The
/// serializer's own text names .NET types and leaves the JSON path out of some
/// messages, so callers read the cause and the path from here instead.
/// </summary>
public static partial class JsonReadError
{
    public static string Describe(JsonException exception)
    {
        var message = exception.Message;
        var pathStart = message.IndexOf(" Path: ", StringComparison.Ordinal);
        if (pathStart >= 0)
        {
            message = message[..pathStart];
        }

        message = UnknownProperty().Match(message) is { Success: true } unknown
            ? $"Unknown property '{unknown.Groups["name"].Value}'."
            : MissingProperties().Match(message) is { Success: true } missing
                ? $"Missing required properties: {missing.Groups["names"].Value}."
                : WrongType().IsMatch(message)
                    ? "The value has the wrong JSON type."
                    : message;

        return string.IsNullOrEmpty(exception.Path) ? message : $"{message} Path: {exception.Path}";
    }

    [GeneratedRegex(@"^The JSON property '(?<name>.*)' could not be mapped to any \.NET member contained in type '.*'\.$", RegexOptions.CultureInvariant)]
    private static partial Regex UnknownProperty();

    [GeneratedRegex(@"^JSON deserialization for type '.*' was missing required properties including: (?<names>.*)\.$", RegexOptions.CultureInvariant)]
    private static partial Regex MissingProperties();

    [GeneratedRegex(@"^The JSON value could not be converted to .*\.$", RegexOptions.CultureInvariant)]
    private static partial Regex WrongType();
}
