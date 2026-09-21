# Flowbit browser smoke suite (`Flowbit.BrowserTests`)

A standalone Chromium suite that drives the **real** standalone editor and the
real Flowbit API/UI over localhost. It is part of Stage 7 of the
[remaining-gaps plan](../../../docs/refactoring/stage-07-browser-smoke-and-stage-05-acceptance.md)
and deliberately lives outside `Flowbit.sln` and `Flowbit.slnx`, so the normal
solution test command stays free of Chromium requirements.

- Real hosts: the published API and UI run as child processes on ephemeral
  loopback ports with a disposable PostgreSQL Testcontainer.
- Real editor: the in-process Kestrel static host serves the build copy of
  the repository `flowbit-editor.html` next to the test assembly. There is no
  second edited HTML fixture. Its configuration ignores ambient Kestrel endpoints.
- Real identity: every runtime scenario mints its token through the UI's
  `/token` screen and uses that exact JWT for HTTP setup clients.
- Synchronous only: the API runs with
  `WorkflowDurableProcessing:PublicationEnabled=false`, so no Worker is needed.
- Serial and isolated: xUnit parallelization is disabled assembly-wide; each
  scenario gets a fresh browser context, its own viewport, and its own
  workflow/instance data inside the run's disposable database.

## Prerequisites

- .NET 10 SDK
- Docker (Testcontainers starts one `postgres:17-alpine` per run)
- Playwright Chromium (installed once per checkout, per configuration)

## Commands

Run from the repository root (PowerShell 7 `pwsh` or Bash; same arguments):

```powershell
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
```

On Linux add `--with-deps` to the Chromium install so operating-system browser
dependencies are installed too:

```bash
pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium
```

For CI-style evidence:

```powershell
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore `
  --logger "trx;LogFileName=browser-smoke.trx" --results-directory artifacts/browser/test-results
git diff --check
```

If the published hosts are missing, the fixture fails with an actionable
message; publish them first. A missing Docker daemon or browser binary fails
the run — it never silently connects to a developer's running stack.

## Options

| Setting | Effect |
| --- | --- |
| `FLOWBIT_BROWSER_HEADED=1` | Launch Chromium headed instead of headless for local diagnosis. Same isolation and cleanup rules as the automated runs. |

## What is covered

| Scenario | Viewports | Behavior under test |
| --- | --- | --- |
| E1 load/edit/save/reload | 1440x900, 1024x768 | File menu load via the real file chooser, node rename in the inspector, workflow name edit, forced download-fallback save, JSON round-trip into a fresh page. |
| E2 node drag | 1440x900, 1024x768 | Real mouse drag with intermediate steps; screen/diagram deltas, unrelated nodes fixed, connectors intact, persisted positions in the downloaded JSON. |
| E3 lane drag | 1440x900, 1024x768 | Lane header drag moves the lane with its children; other lanes fixed; persisted positions verified. |
| E4 validation feedback | 1440x900, 1024x768 | Save of a deliberately invalid fixture shows the "Workflow cannot be saved" dialog with the gateway topology error, focus enters the close control, no download is emitted, Escape recovers, and a later valid save succeeds. |
| E5 keyboard/focus | 1440x900, 1024x768 | Tab reaches the toolbar menus and workflow name, Enter opens the File menu, Escape closes it and restores trigger focus, `/` opens diagram search with result selection, typing tool-shortcut letters inside inputs never switches tools, and undo/redo round-trips outside a text input while native input editing stays native. |
| E6 guarded type changes | 1440x900, 1024x768 | Rejected end conversion (guard alert dismissed, selector restored, node shape unchanged), gateway Cancel (confirmation dismissed, gateway shape and edge condition/default metadata kept) and Accept (node converted, routing metadata cleared from the outgoing-flow cards), plus an accepted conversion driven purely through keyboard operation of the Type select. Dialogs are answered through the queued exact expectations. |
| E7 destructive conversion and history | 1440x900, 1024x768 | User task with two outgoing flows converts to an automatic task (first array-ordered flow kept, extra flow removed); with a redo entry present, a rejected end conversion consumes no history and preserves the redo, which then re-applies; service task converts to a user task (error boundary and its incident flow removed); keyboard Ctrl+Z/Ctrl+Y round-trips the conversions; the boundary's disabled Type field is read-only before removal; the pruned model saves and reloads in a fresh page without the removed boundary/flows. |
| E8 start conversion, settings, and defaults | 1440x900, 1024x768 | Message start → ordinary start materializes the typed start variables; converting back rebuilds the message configuration with defaults (optional no-default mapping omitted), the settings are retyped through the inspector; timer catch seeds PT1H; conditional catch seeds a blank condition that is completed before saving; converting the default start leaves the message start as the only entry (`initialEventId: null` in the saved JSON). |
| R1 normal task lifecycle | 1440x900 | Inbox → claim → action with a declared variable → instance completes; detail shows state/history/value; HTTP read-back confirms one persisted action and an emptied inbox. |
| R2 instance navigation and terminal states | 1440x900 | Open from the list, follow real section links (scroll positions asserted), navigate to a completed instance and back without stale rows. |
| R3 identity replacement | 1440x900 | A second page changes the identity while detail is open; actions follow the current identity; reload does not revive prior-actor actions; clearing and re-applying the identity behaves. |
| R4 gateway/complex/MI detail | 1440x900 | Parallel split and complex merge rows with ordering, complex state values, MI progress and submitted result JSON, section navigation. |
| R5 real polling and disposal | 1440x900 | An open poll-eligible MI detail updates after an out-of-band HTTP completion within the five-second polling window; navigate away/back shows current data; host logs record no error-level circuit issues. |
| R6 responsive controls and focus | 1024x768, 390x844 | Real responsive navigation, section links preserving the instance path/query, keyboard focus, wheel scrolling to clipped table columns, and parsed submitted JSON. |

The 23 product scenarios are accompanied by four harness regression tests:
auxiliary-page errors/dialogs must fail, successful diagnostics must retain
warnings/transport failures, timed-out work must stop before returning, and
queued one-shot dialog expectations must accept, dismiss, fail on unconsumed
entries, and fail on unmatched dialogs. The `harness-expected-*` artifacts
intentionally contain injected failures. The suite therefore runs 27 cases in
total (E1-E8 at both viewports plus the runtime scenarios).

Scenarios register exact one-shot dialog expectations through
`ExpectDialogOnce(dialogType, message, accept)` (checked before the
fallback-prefix handling; unconsumed or unmatched dialogs fail the scenario and
consumed responses are recorded in `diagnostics.json`), while
`ExpectDialogStartingWith` keeps the fallback-save alert accepted across
repeated saves.

Runtime tests wait for `data-interactive="true"` on the UI shell and instance
summary before acting on freshly navigated pages. These nonvisual markers use
Blazor's renderer state and perform no API requests. R2/R5 use real links/back
navigation and verify that the document was not replaced. E2 checks incoming
and outgoing connector endpoints against the moved node, including the
editor's five-unit arrowhead clearance.

Editor save scenarios force the **download fallback** with a context init
script that makes `window.showSaveFilePicker` unavailable; this is a test-only
capability override, not a replacement implementation of `save()`. The native
picker's success/cancel behavior is not covered by automation and still
requires manual headed inspection.

## Artifacts

Every run writes to `artifacts/browser/runs/<timestamp>-<id>/`:

- `setup.log` — stack lifecycle (database, API/UI readiness, browser version).
- `manifest.json` — browser version, loopback host URLs (no secrets), headed
  flag, and scenario names.
- `api-stdout.log` / `api-stderr.log`, `ui-stdout.log` / `ui-stderr.log`.
- One directory per scenario with a Playwright trace on failure, failure
  screenshot, and `failure.log` (URL, viewport, browser version, page errors,
  console issues, failed requests, unexpected dialogs, downloads, warnings).
- Downloaded save artifacts under the editor scenario directories.
- `diagnostics.json` in every scenario directory, including successful runs:
  all observed page URLs/viewports, console errors/warnings, uncaught errors,
  HTTP failures, transport failures, and unexpected dialogs.
- Selected successful editor-search and responsive-results screenshots.
- E7 captures `boundary-conversion-before.png` and
  `boundary-conversion-after.png` at both editor viewports, with the populated
  workflow and host selected. These show the service-task inspector and attached
  error boundary before conversion, then the user-task inspector and removed
  boundary/flow afterward; they are suitable for visual comparison.

Successful runs discard traces while keeping TRX results, the run manifest, and
the run logs and diagnostics. A passing body still fails the scenario when uncaught page
errors, unexpected `console.error` messages, or unexpected dialogs were
recorded. Identity cleanup failures are written to `setup.log` and fail a
passing scenario. JWTs and connection credentials are never written to ordinary
diagnostics.

Diagnostics are registered on every context page before navigation. Process
output pumps drain to EOF before closing log files. Scenario timeouts cancel
HTTP/retry work, capture evidence, close the context to interrupt Playwright,
and wait for the body to finish before returning. If work cannot drain within
the teardown bound, the stack is invalidated and stopped; later scenarios
cannot reuse it. A normally drained timeout closes the old context and the
next runtime scenario explicitly establishes its identity again.

## Fixture boundaries

Fixtures live in `Fixtures/` and are documented in
[Fixtures/README.md](Fixtures/README.md): minimal editor definitions plus four
synchronous runtime definitions (`browser-lifecycle-r1`, `browser-navigation-r2`,
`browser-gateway-r4`, `browser-mi-r5`) with their actors, inputs, and expected
states. Checked-in fixture JSON is unchanged; at POST
`WorkflowFixtureClient` suffixes the authored `id` with a unique token so
repeated runs cannot collide. Nothing in the checked-in `examples/` catalog is
modified.

## Troubleshooting

- **Chromium is not installed** — run the `playwright.ps1 install` command
  above; on Linux include `--with-deps`.
- **Published host not found** — publish both hosts first; the fixture never
  builds or publishes from inside a test.
- **Docker unavailable** — the database container cannot start; the run fails
  fast with container startup diagnostics in `setup.log`.
- **A scenario leaves a page open** — teardown stops only processes and
  containers owned by the fixture. Identity cleanup failures are written to
  `setup.log` and fail a passing scenario; the next scenario still clears and
  applies its own identity through `/token`.
