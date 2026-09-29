# Flowbit branding

[Documentation home](index.md) · [Flowbit.Ui guide](ui-guide.md) · [Project overview](../README.md)

Flowbit uses a connected **F** monogram: a vertical workflow path with two
branches and four rounded task nodes. The same mark identifies the standalone
editor, runtime operations workspace, browser tabs, and GitHub README.

## Assets

The canonical SVG files live in
[`Flowbit.Ui/wwwroot/branding`](../Flowbit/src/Flowbit.Ui/wwwroot/branding/).
They have no external fonts, scripts, or image dependencies.

| Asset | Use |
| --- | --- |
| [Mark](../Flowbit/src/Flowbit.Ui/wwwroot/branding/flowbit-mark.svg) | Square app identity and browser favicon; blue tile with white and pale-blue nodes. |
| [Light-surface wordmark](../Flowbit/src/Flowbit.Ui/wwwroot/branding/flowbit-wordmark-light.svg) | Navy lettering on a transparent background for light surfaces. |
| [Dark-surface wordmark](../Flowbit/src/Flowbit.Ui/wwwroot/branding/flowbit-wordmark-dark.svg) | Pale lettering on a transparent background for dark surfaces. |
| [README banner](../Flowbit/src/Flowbit.Ui/wwwroot/branding/flowbit-banner.svg) | Fixed navy background, product name, and existing product tagline; readable in either GitHub theme. |

Use the square mark without stretching or cropping. At small sizes, show the
mark alone; app identity areas pair it with live text so the name remains
accessible. Decorative marks beside text have empty alternative text or
`aria-hidden="true"`; links without visible text need an accessible name.

## Visual system

| Role | Color |
| --- | --- |
| Primary / primary hover | `#2563EB` / `#1D4ED8` |
| Strong text, runtime navigation, banner | `#0F172A` |
| Light workspace / panel | `#F4F7FB` / `#FFFFFF` |
| Dark editor canvas / panel | `#0B1220` / `#111E33` |
| Accents on dark surfaces | `#60A5FA` and `#BFDBFE` |

Both applications use Segoe UI with system sans-serif fallbacks, compact
controls with 8px corners, panels with 12px corners, and restrained shadows.
Blue identifies primary actions, selection, and focus. Success, warning,
danger, delegation, and BPMN node types retain their distinct semantic colors;
branding is not a reason to recolor every state blue.

The editor retains its light/dark theme selector, system-theme default, and
saved preference. Flowbit.Ui uses a light workspace and navy sidebar. Its
Flowbit home link is also visible in the mobile header when navigation is
closed. Branding does not change routes, permissions, or workflow behavior.

## Maintaining consistency

- Runtime styles use the `--fb-*` properties in `wwwroot/app.css`. Update
  Bootstrap state variables and isolated component styles alongside them;
  editing only the primary color does not update every control state.
- The editor remains a standalone HTML file. Its inline mark and SVG data-URL
  favicon are copies of the canonical mark, and its theme properties remain
  inside the file. When changing the mark, update both copies, both wordmarks,
  and the banner together. Keep SVG geometry and tile colors identical.
- Do not introduce remote assets or a build step for editor branding. Verify
  it by opening a copy of the HTML without its repository asset directories.
- Preserve app identity links, accessible names, keyboard focus, status
  distinctions, and reduced-motion behavior when updating styles.
- Refresh the real screenshots in [the project README](../README.md) and
  [UI guide](ui-guide.md) when a visual change makes them misleading. Historical
  refactoring evidence records its original version and is not a current
  product screenshot.

Follow the repository's [browser verification requirements](../AGENTS.md#mandatory-real-browser-ui-verification)
and the [Chromium smoke suite](../Flowbit/tests/Flowbit.BrowserTests/README.md).
Inspect desktop, tablet, and mobile layouts, both editor themes, favicon
rendering, long content, focus states, dialogs, and console diagnostics.
