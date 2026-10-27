using Microsoft.AspNetCore.Components;

namespace Flowbit.Ui.Components.Shared;

/// <summary>
/// What <c>typed-inputs.js</c> reports when a number, date, or date-time control
/// changes or loses focus. The browser computes it during the event, so the stored
/// value is current before any later click is handled.
/// </summary>
public sealed class TypedInputEventArgs : EventArgs
{
    /// <summary>The control's value as the browser reports it; empty when it cannot read the entry.</summary>
    public string? Value { get; set; }

    /// <summary>True when the browser could not interpret what was typed.</summary>
    public bool BadInput { get; set; }

    /// <summary>For a date-time, the browser's UTC offset in minutes for that local moment.</summary>
    public int? OffsetMinutes { get; set; }

    /// <summary>For a date-time, the browser's time zone name when it reports one.</summary>
    public string? TimeZone { get; set; }

    /// <summary>For a date-time, true when a daylight-saving change skips that local time.</summary>
    public bool SkippedLocalTime { get; set; }
}

// The Razor compiler discovers custom events only on classes named EventHandlers.
[EventHandler("ontypedinput", typeof(TypedInputEventArgs), enableStopPropagation: true, enablePreventDefault: true)]
[EventHandler("ontypedcommit", typeof(TypedInputEventArgs), enableStopPropagation: true, enablePreventDefault: true)]
public static class EventHandlers
{
}
