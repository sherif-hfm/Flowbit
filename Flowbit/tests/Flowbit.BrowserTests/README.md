# Flowbit browser smoke suite (`Flowbit.BrowserTests`)

A standalone Chromium suite that drives the **real** standalone editor and the
real Flowbit API/UI over localhost. It was added in Stage 7 of the
[refactoring roadmap](../../../docs/index.md#refactoring-history) and
deliberately lives outside `Flowbit.sln` and `Flowbit.slnx`, so the normal
solution test command stays free of Chromium requirements.

- Real hosts: the published API and UI run as child processes on ephemeral
  loopback ports with a disposable PostgreSQL Testcontainer.
- Real editor: the in-process Kestrel static host serves the build copy of
  canonical HTML/CSS/JavaScript assets from `Flowbit.Ui/wwwroot/editor/` next to
  the test assembly. There is no second edited editor fixture. Its configuration ignores ambient Kestrel endpoints.
- Development identity: the ordinary runtime scenarios mint their token through the UI's
  `/token` screen in explicit DevelopmentToken mode and use that exact JWT for HTTP setup clients.
  UI startup probes use anonymous `/health/live`, independently of the sign-in mode;
  the framework asset probe still verifies `/_framework/blazor.web.js` is served.
- Synchronous only: the API runs with
  `WorkflowDurableProcessing:PublicationEnabled=false`, so no Worker is needed.
- Serial and isolated: xUnit parallelization is disabled assembly-wide; each
  scenario gets a fresh browser context, its own viewport, and its own
  workflow/instance data inside the run's disposable database.
- AI scenarios use a fixture-owned loopback OpenCode-compatible chat-completions
  provider. It answers each model call with a scripted reply:
  native tool calls with GLM-style `reasoning_content`, a final text, a response cut
  off at the output limit, or an HTTP failure. The real API runs the agent, its six
  file tools, Flowbit's checks, layout, streaming and the editor apply path; the
  fixture only decides which tools the model calls. No billed provider call is made
  and no application state or database rows are injected.
- After startup the setup identity configures three AI models through the real
  `POST /api/ai-models` route, exactly as a settings administrator would: GLM-5.3-Flash
  with high reasoning (the assistant's default), GLM-5.3 and Kimi K2.7 Code, each on
  the loopback endpoint with its own synthetic key. Ambient `WorkflowAi__*` settings
  are stripped, so the scenarios check the 40-call limit and the 32,768-token output
  limit from the API's `appsettings.json`.
- The fixture records each provider request: key hash, user agent, session header,
  model, reasoning, output limit, tool names, instructions, user messages, and the
  conversation reduced to roles, text, tool calls, tool results and reasoning. The
  scenarios use it to prove that Continue resends exactly the stopped request, that
  the API uses the configured model's key, and that catalog values and the provider
  key never reach a request body, the page, browser storage or host logs.

Worker-driven audit and administrative-batch acceptance is maintained in the
separate local [Flowbit.BrowserAcceptanceTests suite](../Flowbit.BrowserAcceptanceTests/README.md).
It reuses these diagnostics, HTTP fixture helpers and host lifecycle with
explicit acceptance options. The parameterless smoke fixture keeps durable
publication disabled, does not launch a Worker or API proxy, and retains this
suite's existing CI configuration.

The separate [OpenID Connect browser scenarios](#openid-connect-browser-acceptance)
exercise authorization code with and without PKCE, session isolation, refresh,
logout, and protocol failures through a fixture-owned identity provider.

`LocalAuthenticationSmokeTests` uses the same real published hosts with Local mode and a disposable database. At 1440x900, 1024x768 and 390x844 it drives password login, attribute definition/required validation, user create/edit/activation, password reset/change, current-token inspection, browser-session isolation and logout. Password coverage creates/logs in with one character, resets/logs in with 257 characters (without truncation), then changes/logs in with one character. Tokens are obtained through actual login and checked for exact-case `Username`. Screenshots and console diagnostics are saved in each run directory; no application state or database rows are injected. Run it after publishing/building with `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~LocalAuthenticationSmokeTests`.

## Prerequisites

The AI scenarios H6–H10 run at 1440x900, 1024x768 and 390x844. H8 also checks the
current-model-call wait timer: it appears while waiting, resets after Continue and
disappears on Cancel. Progress screenshots and browser console diagnostics remain
required evidence. After publishing and building as below, run only the AI cases with:

```text
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~AiAuthoringSmokeTests|FullyQualifiedName~AiModelsSmokeTests"
```

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

Hosted editor scenarios H1 (1440x900 and 1024x768) and H2 exercise exact-version editing, locked keys (including undo after a first save), undo/redo dirty state, immutable unpublished saves, navigation guards, route re-entry, 70 KiB JSON streaming, authorization rejection retaining edits, local import detaching the source, and the 2 MiB save limit. H3 covers pending JavaScript drafts, dialog focus and Escape handling, validation errors, missing versions, and the 390x844 layout. H4 opens the root launcher through `file://`, loads the adjacent shared assets, and imports/exports JSON. H5 checks inline node dragging, connector geometry, inspector resizing, shortcut isolation from app navigation, and clean disposal/remount after navigation. They use real UI controls and HTTP read-back, with screenshots and console diagnostics. Native picker success/cancel still requires manual verification.

| Scenario | Viewports | Behavior under test |
| --- | --- | --- |
| H6 AI creation and editing | 1440x900, 1024x768, 390x844 | The configured GLM-5.3-Flash model is the default selection and the panel has no provider or key field; the API calls the provider with that model's stored key; the model reads the docs, saves `workflow.json` and `open-questions.md`, and the proposal shows the open questions; value-free catalog selection plus PDF upload/corrected extraction appended to the message as labelled blocks (1440 only); the agent's instructions, six tools, reasoning replay, model settings and user agent; compact layout of the new diagram; apply, undo/redo, new unpublished save; a question in a new conversation gets an answer with no proposal; an `edit_file` rename in the same conversation keeps the workflow family, positions and saved baseline; skill archive download from the Workflows page and from the panel, each saved as `flowbit-authoring.zip` from an `application/zip` blob, the panel's archive with `SKILL.md`, the schema and `manifest.json`, leaving the apply notice in place; narrow drawer keyboard focus/Escape; responsive screenshots. |
| H7 AI session lifecycle | 1440x900, 1024x768, 390x844 | Manual edits invalidate proposals and the next message carries the sync line; cancellation reaches the upstream provider; separate panels keep separate model selections; identity change, reset, disposal and navigation clear the conversation state. |
| H8 AI recovery | 1440x900, 1024x768, 390x844 | A response cut off at the output limit pauses the run; Continue repeats the stopped request, meets a 503 with no retry, and pauses again; the next Continue renames the workflow with `edit_file`; the skill archive downloads while that run waits for the model, and the run keeps waiting; Cancel during the following model call keeps the checkpoint; Continue resends exactly the pending tool result, ignores unsent composer text, and the proposal applies in one undoable edit. Progress screenshots and console diagnostics are captured. |
| H9 AI state and diagram changes | 1440x900, 1024x768, 390x844 | A provider authentication failure keeps the conversation state and offers Continue; changing the selected model clears it; editor edits disable Continue; the next message keeps the paused tool result, replaces the agent's `workflow.json` with the edited diagram and carries the sync line; New conversation clears the state and keeps the selected model. |
| H10 AI models page | 1440x900, 1024x768, 390x844 | A settings administrator adds a model on `/ai-models` (the key is never shown again), edits it with a blank key, and deletes it after a confirmation; the Engine settings page does not show the stored models; the editor assistant offers the model by name and the API calls the provider with the key stored before the edit, its model name and reasoning; a viewer sees the settings-role message; no horizontal overflow; screenshots. |
| E1 load/edit/save/reload | 1440x900, 1024x768 | File menu load via the real file chooser, node rename in the inspector, workflow name edit, forced download-fallback save, JSON round-trip into a fresh page. |
| E2 node drag | 1440x900, 1024x768 | Real mouse drag with intermediate steps; screen/diagram deltas, unrelated nodes fixed, connectors intact, persisted positions in the downloaded JSON. |
| E3 lane drag | 1440x900, 1024x768 | Lane header drag moves the lane with its children; other lanes fixed; persisted positions verified. |
| E4 validation feedback | 1440x900, 1024x768 | Save of a deliberately invalid fixture shows the "Workflow cannot be saved" dialog with the gateway topology error, focus enters the close control, no download is emitted, Escape recovers, and a later valid save succeeds. |
| E5 keyboard/focus | 1440x900, 1024x768 | Tab reaches the toolbar menus and workflow name, Enter opens the File menu, Escape closes it and restores trigger focus, `/` opens diagram search with result selection, typing tool-shortcut letters inside inputs never switches tools, and undo/redo round-trips outside a text input while native input editing stays native. |
| E6 guarded type changes | 1440x900, 1024x768 | Rejected end conversion (guard alert dismissed, selector restored, node shape unchanged), gateway Cancel (confirmation dismissed, gateway shape and edge condition/default metadata kept) and Accept (node converted, routing metadata cleared from the outgoing-flow cards), plus an accepted conversion driven purely through keyboard operation of the Type select. Dialogs are answered through the queued exact expectations. |
| E7 destructive conversion and history | 1440x900, 1024x768 | User task with two outgoing flows converts to an automatic task (first array-ordered flow kept, extra flow removed); with a redo entry present, a rejected end conversion consumes no history and preserves the redo, which then re-applies; service task converts to a user task (error boundary and its incident flow removed); keyboard Ctrl+Z/Ctrl+Y round-trips the conversions; the boundary's disabled Type field is read-only before removal; the pruned model saves and reloads in a fresh page without the removed boundary/flows. |
| E8 start conversion, settings, and defaults | 1440x900, 1024x768 | Message start → ordinary start materializes the typed start variables; converting back rebuilds the message configuration with defaults (optional no-default mapping omitted), the settings are retyped through the inspector; timer catch seeds PT1H; conditional catch seeds a blank condition that is completed before saving; converting the default start leaves the message start as the only entry (`initialEventId: null` in the saved JSON). |
| E9 variable form fields | 1440x900, 1024x768 | A start-event variable's closed **Form** section is opened; its label (entered with surrounding spaces) and multi-line help text are filled, the type becomes `number`, and two options are added through **+ Add option**, one with a label. A third option typed as `9007199254740993`, which a JavaScript number would change, blocks the save with the 15-significant-digit message and still shows as typed after the node is reselected; removing it lets the save proceed. The saved JSON carries the trimmed label, the help text, and numeric option values with the unlabelled option's label omitted; a fresh page reloads them into an open Form section. |
| E10 user-task due date and priority | 1440x900, 1024x768 | The user task's **Due date** field reports the quoted value `'P2X'` at once (`aria-invalid` and the shared message) while **Priority** `75` stays valid; the save is blocked with the same message; the unquoted year duration `P1Y` is reported too; `  P3D  ` clears the error, and the saved JSON carries the trimmed `dueDate` and `priority` on that task only. A fresh page reloads both fields. |
| R1 normal task lifecycle | 1440x900 | Inbox → claim → action with a declared variable → instance completes; detail shows state/history/value; HTTP read-back confirms one persisted action and an emptied inbox. |
| R2 instance navigation and terminal states | 1440x900 | Open from the list, follow real section links (scroll positions asserted), navigate to a completed instance and back without stale rows. |
| R3 identity replacement | 1440x900 | A second page changes the identity while detail is open; actions follow the current identity; reload does not revive prior-actor actions; clearing and re-applying the identity behaves. |
| R4 gateway/complex/MI detail | 1440x900 | Parallel split and complex merge rows with ordering, complex state values, MI progress and submitted result JSON, section navigation. |
| R5 real polling and disposal | 1440x900 | An open poll-eligible MI detail updates after an out-of-band HTTP completion within the five-second polling window; navigate away/back shows current data; host logs record no error-level circuit issues. |
| R6 responsive controls and focus | 1024x768, 390x844 | Real responsive navigation, section links preserving the instance path/query, keyboard focus, wheel scrolling to clipped table columns, and parsed submitted JSON. |
| R7 administrative batch display | 1440x900, 1024x768, 390x844 | Executes immediate administrative actions through the real instance-detail panel, verifies refreshed instance status, variables and history before following the real audit batch link, verifies the frozen request, count order/values, item row, highlight, JSON details, and recent history against HTTP reads, switches batches with real clicks and keyboard-activated Open buttons, exercises zero-match item and recent-batch status filters with restore, checks narrow-table scrolling and document overflow, and records before/after audit screenshots at all three viewports. |
| R8 live instance diagram | 1440x900, 390x844 | The read-only diagram on instance detail marks exactly the two active parallel user tasks and highlights no flow, with the authoring toolbar, palette and inspector hidden. Dragging a marked node only pans the canvas, and Delete removes nothing. Clicking a node lists its task link and, for an operator with node-execution read access, its node-execution link, and marks the node `aria-current`; Enter selects a focused node and Escape clears the selection. The execution link opens that execution, and Back returns to the diagram. A multi-instance node shows its `1/3` progress badge, a completed instance marks only its end event, and the page has no horizontal overflow. A reviewer without node-execution access sees the task link but no execution link. |
| R9 typed task forms | 1440x900, 390x844 | In a browser fixed to `Asia/Tokyo`, the start form shows each value's control: labels with the variable name and type, linked help text, the required state, a number box, True/False and option lists, date and date-time pickers, the default hint, and a text area for an array. A number typed as `12-` shows its error and blocks the start. After valid entries the date-time preview reads `Sent as 2026-09-10T10:30:00+09:00 (Asia/Tokyo).`, and HTTP read-back confirms the exact stored JSON, including `1250.50` and the offset. The task form offers the action's labelled options and help text and completes the instance with the chosen option values; neither page overflows horizontally or logs console warnings. A second case, in `Europe/Berlin`, checks that the mouse wheel over a focused amount leaves it unchanged and moves focus out; that a date or date-time left partly filled from empty shows its error when focus leaves it and blocks the start; that 02:30 on 29 March 2026 is rejected as skipped while 02:30 on 25 October 2026 is sent with `+02:00`; and that the instance-detail action form offers the same controls and completes the instance with the exact stored values. A third case, in `America/New_York`, fills the multi-instance **Interrupt actions** form (a labelled option list with help text, a number, and a date-time sent with `-05:00`), accepts the confirmation, and reads the stored values back. It then takes **Decide** through the instance **Administrative actions** panel, whose typed option lists and date-time (sent with `-04:00`) complete the instance, and reads the audit batch over HTTP to confirm that the action's declarations keep their label, help text, and options. |
| R10 task due dates and priorities | 1440x900, 390x844 | One instance opens four parallel reviews with a past due date, `PT2H`, `P3D`, and none. My work shows the **Overdue** and **Due soon** badges, the exact due time as the tooltip, `Priority N` under each date, and a dash for the unscheduled task. Sorting by **Due date** ascending and **Priority** in both directions puts the unscheduled task last; the **Due** filter keeps only the overdue review and the priority range only the due-soon review, whose task detail shows **Due** with its badge and **Priority** `60`. HTTP read-back confirms `dueStatus` and `priority`, and neither page overflows horizontally. |

The editor and runtime product scenarios are accompanied by four harness regression tests:
auxiliary-page errors/dialogs must fail, successful diagnostics must retain
warnings/transport failures, timed-out work must stop before returning, and
queued one-shot dialog expectations must accept, dismiss, fail on unconsumed
entries, and fail on unmatched dialogs. The `harness-expected-*` artifacts
intentionally contain injected failures. H6, H7, H8, H9 and H10 each add three
responsive AI cases to the editor/runtime coverage.

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
- H6 retains `ai-skill-workflows.png` (the Workflows page skill section), `ai-connection.png`,
  `ai-proposal.png`, and `ai-applied-diagram.png` at all three viewports, the downloaded skill
  archive, and its generated text PDF fixture.
- H8 retains `ai-incremental-progress.png` at all three viewports while the
  renamed `workflow.json` is checkpointed and the next model call is in flight, plus
  `ai-continuation.png` showing the retained checkpoint after cancellation.
- H8 additionally retains `ai-resumed-progress.png`, showing the same
  `workflow.json` node/connection counts after Continue resets the per-run metrics.
- H10 retains `ai-models.png` and `ai-model-edit.png` at all three viewports.
- R8 retains `instance-diagram-running.png`, with Reviewer A selected, and
  `instance-diagram-finished.png` at both viewports.
- R9 retains `rich-start-form.png`, `rich-task-form.png`,
  `instance-detail-action.png`, `instance-interrupt-form.png`, and
  `instance-admin-override-form.png`, and E9 retains `variable-form-fields.png`
  (the inspector showing the expanded Form section), at each of their viewports.
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
[Fixtures/README.md](Fixtures/README.md): minimal editor definitions plus seven
synchronous runtime definitions (`browser-lifecycle-r1`, `browser-navigation-r2`,
`browser-gateway-r4`, `browser-mi-r5`, `browser-administrative-r7`, `browser-rich-forms-r9`,
`browser-rich-forms-mi-r9`) with their actors, inputs, and expected
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

## OpenID Connect browser acceptance

The independent `OidcAuthenticationSmokeTests` suite runs the published API and
UI against a disposable PostgreSQL database and a fixture-owned loopback identity
provider. It does not change the ordinary smoke fixture's DevelopmentToken mode,
start a Worker, inject application sessions, or edit database rows. Publish both
hosts using the commands above, then run this command in PowerShell or Bash:

```text
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --filter FullyQualifiedName~OidcAuthenticationSmokeTests
```

The UI uses HTTPS with a per-run test certificate. Certificate bypass applies only
to the fixture's browser context; production certificate validation is unchanged.
The loopback identity provider exposes real discovery, RSA JWKS, authorization,
code exchange, rotating refresh-token and logout endpoints. Its HTML login form
selects synthetic users; no external identity-provider account or billed service
is required.

- **O1**, with PKCE enabled and disabled, exercises browser redirects and form-post
  callbacks, S256 verifier validation when enabled, two simultaneous users, API
  actor resolution, secure HttpOnly cookies, absence of bearer tokens from browser
  storage and markup, development-route denial and antiforgery logout rejection.
  A second circuit refreshes Alice's tokens while her unsaved editor remains open.
  Keyboard sign-out invalidates her other circuit while Bob remains signed in.
  The editor retains the diagram and exports the unsaved JSON after logout;
  cancelling the browser's unsaved-work prompt keeps the draft open when sign-in
  would leave the editor.
- **O2** rejects tampered callback state, an invalid ID-token nonce, an invalid
  issuer and a callback replay from another browser context. It checks that an
  external return URL resolves to the local overview and that provider refresh
  revocation returns the UI to its sign-in boundary.
- **O3** configures an AI model through the AI models API with the signed-in
  administrator's access token, keeps a real AI response stream open during
  access-token refresh and verifies that the selected model, the `workflow.json`
  progress and the editor remain intact.
  Completing that stream yields a proposal. Signing out during a second stream
  cancels the provider request and clears the assistant while retaining the
  unsaved editor; a new login starts with an empty assistant.
- **O4** places a real HTTPS loopback proxy in front of the HTTP UI. It verifies
  that authorization and logout use the public HTTPS callback URLs, cookies stay
  secure, and a reload and additional tab reuse the authenticated session. The
  proxy drops its live WebSocket connections to exercise genuine Blazor circuit
  reconnection: open filter state survives and a subsequent server-side toggle
  confirms that the circuit is interactive. The proxy forwards original HTTP and
  WebSocket payloads and adds trusted forwarded headers; it never injects
  application or authentication state.

Evidence is saved under `artifacts/browser/oidc/<timestamp>-<mode>-<id>/`:
`manifest.json` records the localhost origins and browser version; successful O1
runs retain desktop editor screenshots at 1440×900, account/inbox screenshots at
1024×768 and 390×844, the exported workflow, protocol outcome counts and console
error/warning files, plus desktop/mobile signed-out pages. O2 retains safe sign-in
failure pages at all three widths and a refresh-revocation screenshot. O3 retains
screenshots during the refreshed stream and after cancellation. O4 records the
public HTTPS and internal HTTP origins, proxy request paths and WebSocket
connection count, plus reconnected and signed-out screenshots. Its deliberate
transport-drop console message is kept separately in
`expected-disconnect-errors.txt`; unexpected console errors still fail the test.
Host stdout/stderr logs cover fixture startup and application
failures. These diagnostics do not record provider credentials or bearer tokens.

JSON export exercises the supported download fallback by disabling the native
File System Access picker in the test browser, matching the ordinary smoke suite.
It does not replace the separate manual native-picker success/cancel checks.
