// Registers the typedinput and typedcommit events used by the typed number,
// date, and date-time controls on start and action forms. Each reports whether
// the browser rejected the entry and, for a date-time, the browser's UTC offset
// for that local moment. Reading them in the same event keeps the stored value
// ahead of any later click. typedinput follows the input event; typedcommit
// follows focus leaving the control, because a date or date-time left partly
// filled from empty never raises an input event. Load this script right after
// the Blazor script.
(() => {
  "use strict";
  const blazor = window.Blazor;
  window.flowbit = window.flowbit || {};
  if (!blazor || typeof blazor.registerCustomEventType !== "function" ||
      window.flowbit.typedInputsRegistered) {
    return;
  }
  window.flowbit.typedInputsRegistered = true;

  const localDateTime = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?$/;

  function timeZoneName() {
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone || null;
    } catch {
      return null;
    }
  }

  // Builds the local moment from its parts, like the editor's absolute timer:
  // a time skipped by a daylight-saving change does not round-trip. A repeated
  // time resolves to its earlier offset.
  function describeLocalDateTime(value) {
    const match = localDateTime.exec(value);
    if (!match) return { offsetMinutes: null, skippedLocalTime: false };
    const [year, month, day, hour, minute, second] =
      match.slice(1, 7).map(part => Number(part || 0));
    const date = new Date(0);
    date.setHours(0, 0, 0, 0);
    date.setFullYear(year, month - 1, day);
    date.setHours(hour, minute, second, 0);
    const skipped = date.getFullYear() !== year || date.getMonth() !== month - 1 ||
      date.getDate() !== day || date.getHours() !== hour ||
      date.getMinutes() !== minute || date.getSeconds() !== second;
    // Historical local mean time can have a fractional offset, which no ISO
    // offset can express.
    const offset = -date.getTimezoneOffset() + 0;
    return {
      offsetMinutes: !skipped && Number.isInteger(offset) ? offset : null,
      skippedLocalTime: skipped
    };
  }

  function describeTarget(event) {
    const target = event && event.target ? event.target : {};
    const value = typeof target.value === "string" ? target.value : "";
    const args = {
      value,
      badInput: !!(target.validity && target.validity.badInput),
      offsetMinutes: null,
      timeZone: null,
      skippedLocalTime: false
    };
    if (target.type === "datetime-local" && value && !args.badInput) {
      Object.assign(args, describeLocalDateTime(value), { timeZone: timeZoneName() });
    }
    return args;
  }

  blazor.registerCustomEventType("typedinput", { browserEventName: "input", createEventArgs: describeTarget });
  blazor.registerCustomEventType("typedcommit", { browserEventName: "focusout", createEventArgs: describeTarget });

  // The wheel steps a focused number box, which can change an amount while the
  // page scrolls. Leaving the box lets the wheel scroll the page instead.
  document.addEventListener("wheel", event => {
    const target = event.target;
    if (target && target.type === "number" && target === document.activeElement &&
        target.hasAttribute && target.hasAttribute("data-typed-input")) {
      target.blur();
    }
  }, { passive: true });
})();
