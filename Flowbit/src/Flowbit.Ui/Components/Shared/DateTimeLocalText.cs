using System.Globalization;

namespace Flowbit.Ui.Components.Shared;

/// <summary>
/// Text of a page's <c>datetime-local</c> input. <c>@bind</c> to a nullable
/// <see cref="DateTime"/> clears the whole field while a segment is half typed:
/// a leading 0 briefly empties the browser's value, the binding becomes null,
/// and Blazor rewrites the input. Pages therefore leave the input uncontrolled,
/// rendering a value only when it changes from outside (under a new <c>@key</c>),
/// and keep the text their change events report.
/// </summary>
internal static class DateTimeLocalText
{
    // Browsers report whole minutes; Blazor appends seconds before the change
    // event reaches the server.
    private static readonly string[] Formats =
        ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

    /// <summary>The input text for a value, with seconds only when it has them.</summary>
    internal static string Format(DateTime value) =>
        value.ToString(
            value.Second == 0 ? "yyyy-MM-dd'T'HH:mm" : "yyyy-MM-dd'T'HH:mm:ss",
            CultureInfo.InvariantCulture);

    /// <summary>The value of the input's text; empty or incomplete text is null.</summary>
    internal static DateTime? Parse(string? text) =>
        DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
}
