using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Flowbit.Ui.Components.Shared;

/// <summary>
/// Holds a circuit's <see cref="BrowserTimeZone"/> and cascades it to every component that
/// declares a <see cref="BrowserTimeZone"/> cascading parameter, re-rendering them when it
/// changes. A prerender has no browser to ask, so each scope starts with the UTC fallback;
/// <c>Routes</c> asks the browser once its circuit is interactive.
/// </summary>
public sealed class BrowserTimeZoneProvider
{
    // Defined by typed-inputs.js, which the typed workflow inputs also use for their zone.
    internal const string BrowserFunction = "flowbit.timeZone";

    private bool asked;

    internal CascadingValueSource<BrowserTimeZone> Source { get; } = new(BrowserTimeZone.Pending, isFixed: false);

    /// <summary>Reads the browser's zone once per circuit; later calls do nothing.</summary>
    public async Task ReadBrowserZoneAsync(IJSRuntime js)
    {
        if (asked)
        {
            return;
        }

        asked = true;
        string? name;
        try
        {
            name = await js.InvokeAsync<string?>(BrowserFunction);
        }
        catch (JSException)
        {
            // A page without the script reports nothing, which keeps the labelled UTC fallback.
            name = null;
        }
        catch (Exception ex) when (ex is JSDisconnectedException or OperationCanceledException)
        {
            // The circuit is gone or the browser never answered; the UTC fallback stays.
            return;
        }

        await Source.NotifyChangedAsync(BrowserTimeZone.FromBrowser(name));
    }
}
