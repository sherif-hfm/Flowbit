# Stage 7 — Automated browser smoke suite and Stage 5 acceptance

[Plan index](README.md) · [Remaining-gaps roadmap](remaining-gaps-implementation-plan.md#stage-7--automated-browser-smoke-suite-and-stage-5-acceptance) · [Browser coverage gap](gaps.md#7-automated-visual-and-interaction-coverage)

**Status: In progress — the automated smoke suite is implemented and passing;
Stage 5's residual manual acceptance remains open.** The suite
`Flowbit/tests/Flowbit.BrowserTests/` (20 tests: 17 product scenarios, E1–E5 at
both editor viewports and R1–R6, plus three harness regression tests) runs the
real copied editor and the real published API/UI over localhost with a
disposable PostgreSQL Testcontainer, outside both solutions and outside the
normal solution test command. The `browser-smoke` CI job runs it on Ubuntu 24.04
with TRX and artifact uploads. What is still open for this stage:

- The manual native-picker success/cancel check on a headed desktop Chromium
  (the automated suite covers the forced download fallback only). The headed
  mode (`FLOWBIT_BROWSER_HEADED=1`) was verified working; the recorded
  native-picker manual evidence is not yet collected.
- The full Stage 5 acceptance rows that need a separate Worker-driven stack:
  batch links for real version-change and variable-update batches, plus the
  pre-extraction visual comparison. See the checklist below for per-row status.
- Observing the new remote `browser-smoke` job pass on a real CI run; the
  workflow YAML is committed but a remote run has not been observed yet.

Prepared against commit `7c67ac2` on 2026-09-20. Historical test results in
earlier stage records are not this stage's results.

## Objective and boundary

Add a small, repeatable Chromium suite that exercises the standalone editor and
the real Flowbit API/UI over localhost, then close every outstanding item in
[Stage 5's browser gate](stage-05-instance-detail-components.md#required-browser-verification).
The suite should catch broken loading, saving, pointer interaction, navigation,
task actions, identity refresh, and representative instance-detail rendering.

The stage has two deliverables:

1. **Automated smoke coverage:** an isolated test project and a separate CI job,
   using PostgreSQL, API, UI, and a test-only editor host. Use synchronous
   workflows and no Worker in this suite.
2. **Stage 5 acceptance evidence:** the complete browser checklist, including
   actual audit/batch history and links using a separate isolated full stack
   with a Worker where necessary. Automated smoke coverage alone is insufficient
   to close this gate.

Keep production HTTP routes, authentication, DTOs, workflow JSON, persistence,
UI appearance, polling rules, and editor behavior unchanged. Add no production
dependency, migration, browser-test endpoint, or editor build step. Keep both
existing solution files and their normal test command free of the new browser
project. Test dependencies belong only to that project.

Defer screenshot comparison baselines, cross-browser matrices, broad end-to-end
coverage of every node type, a UI authentication redesign, and Stages 8–11's
extractions. Stages 8–9 may proceed independently with coordinated edits;
Stage 10 should use the resulting browser coverage, and Stage 11 also requires
Stage 5 acceptance.

## Current implementation and implications

| Source | Observed behavior | Planning consequence |
| --- | --- | --- |
| [Existing test project](../../Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj) | Targets `net10.0`; uses xUnit, Testcontainers, Jint, API hosts, and component rendering tests. | Match its test infrastructure versions; retain these checks alongside browser tests. |
| [Current CI workflow](../../.github/workflows/tests.yml) | Runs the solution suite on Ubuntu 24.04 with .NET 10 and Docker. | Add an independent browser job without adding Chromium requirements to the existing job. |
| [API startup](../../Flowbit/src/Flowbit.Api/Program.cs) | Development startup applies migrations and exposes development OpenAPI; startup also reads persisted identity settings. | Start with `Development` and the disposable database; do not switch to `Testing` and assume the database is initialized. |
| [UI startup](../../Flowbit/src/Flowbit.Ui/Program.cs) | Uses interactive Blazor Server and singleton `TokenState`/`DevTokenFactory`; the typed client reads `WorkflowApi:BaseUrl`. | Fresh browser contexts do not isolate identity. Serialize tests and reset identity explicitly. |
| [Token screen](../../Flowbit/src/Flowbit.Ui/Components/Pages/Token.razor) | `/token` exposes Clear identity, User, Roles, custom claims, Generate and apply, and a raw JWT field. | Exercise the real development identity flow and use its generated token for HTTP fixture setup. |
| [Instance detail](../../Flowbit/src/Flowbit.Ui/Components/Pages/InstanceDetail.razor) | Five-second polling is conditional: a sole active MI execution, a message wait, or multiple active positions. | Test ordinary action refresh and actual polling separately; a normal single-token user task is not a polling fixture. |
| [Editor](../../flowbit-editor.html) | Starts with the pan tool; SVG nodes/lanes expose `data-id`; saving prefers the native picker and otherwise alerts before downloading JSON. | Select the move/select tool before dragging; handle the fallback alert and download explicitly. |

Blazor's workflow API requests originate on the UI server. Browser network
interception cannot deterministically delay those API calls or establish their
counts. Put delayed-response, disposal, and request-count assertions in the
server-side tests; use browser checks for rendered behavior and real navigation.
Correlate host logs when investigating server-to-API requests.
[InstanceVariableUpdateUiContractTests](../../Flowbit/tests/Flowbit.Tests/InstanceVariableUpdateUiContractTests.cs)
now covers identity replacement after rendering, delayed initial-load disposal,
separately gated in-flight responses with successful/failed replacement, and poll-eligible instance request-count
characterization (load, one five-second tick, then dispose).

## Target ownership and file layout

Create `Flowbit/tests/Flowbit.BrowserTests/` with the following bounded structure.
These files are implemented; the harness also has `ScenarioCancellation.cs`
and `HarnessDiagnosticsTests.cs` for cancellation propagation and fault injection.

| Proposed file or group | Responsibility |
| --- | --- |
| `Flowbit.BrowserTests.csproj` | Standalone `net10.0` test project, dependency pins, editor/fixture content copying. |
| `BrowserCollection.cs` | One xUnit collection and fixture; disable parallel execution for the assembly. |
| `Infrastructure/BrowserStackFixture.cs` | Own the disposable database, API/UI processes, editor host, Playwright, and Chromium; initialize and dispose safely. |
| `Infrastructure/HostedProcess.cs` | Launch a process, capture output, discover its listening address, bound readiness, and stop only owned processes. |
| `Infrastructure/EditorStaticHost.cs` | Serve the real copied editor through in-process Kestrel on an ephemeral loopback port. |
| `Infrastructure/BrowserScenario.cs` | Own one context, pages, trace, console/error capture, failure artifacts, and per-test cleanup. |
| `Support/IdentityScreen.cs` | Clear and apply identity through `/token`, then verify the applied user and roles. |
| `Support/WorkflowFixtureClient.cs` | Seed/publish/start workflows and inspect outcomes through authenticated HTTP only. |
| `Support/EditorInteractions.cs` | Small helpers for load, save/download, SVG geometry, and real pointer gestures. |
| `Fixtures/` and `Fixtures/README.md` | Minimal valid and deliberately invalid editor JSON plus synchronous runtime definitions, actors, inputs, and expected states. |
| `EditorSmokeTests.cs`, `RuntimeSmokeTests.cs`, `InstanceDetailSmokeTests.cs` | The behavior matrix below; readable test-specific assertions. |
| `README.md` | Prerequisites, commands, fixture boundaries, troubleshooting, and artifact locations. |

Use composition rather than a large generic page-object framework. Share
repeated interactions, not whole scenarios or opaque assertion chains. Reference
`Flowbit.Shared` only if useful for HTTP DTOs; do not reference `Flowbit.Tests`,
instantiate the engine, or substitute TestServer for the actual hosts.

### Dependency and build decisions

Pin the following versions in the new project, matching the existing test
project except for the newly selected browser library:

| Package | Version |
| --- | --- |
| `Microsoft.NET.Test.Sdk` | `18.7.0` |
| `xunit` | `2.9.3` |
| `xunit.runner.visualstudio` | `3.1.5`, retaining the existing private-assets pattern |
| `Testcontainers.PostgreSql` | `4.13.0` |
| `Microsoft.Playwright` | `1.62.0` |

Use the base Playwright library with explicit fixture ownership; an additional
Playwright test-runner package is unnecessary. Add the
`Microsoft.AspNetCore.App` framework reference for the test-only static host.
Set `IsTestProject=true` and `IsPackable=false`. Copy the editor directly from
the repository file; do not maintain a second edited HTML fixture.

The roadmap's [Playwright 1.62.0 pin](https://www.nuget.org/packages/Microsoft.Playwright/1.62.0)
is deliberate. Build the project before running its generated
`playwright.ps1` installer and install only Chromium. The
[official .NET CI instructions](https://playwright.dev/dotnet/docs/ci)
document the build/install/test sequence and Linux dependency installation.

## Isolated stack lifecycle

### Configuration and startup

Publish the actual API and UI into `artifacts/browser/hosts/api` and
`artifacts/browser/hosts/ui` before running tests. Preserve each complete publish
directory, including configuration, static assets, and manifests. Launch the DLLs
with the publish directory as working directory and content root so Blazor
assets resolve correctly. Do not rebuild or publish from inside individual tests.

Resolve repository/publish paths explicitly and fail with an actionable message
if binaries are missing. Use one unique run directory under
`artifacts/browser/runs/` for logs, downloads, and browser evidence. The existing
`.gitignore` already excludes `artifacts/`.

| Setting/resource | Required smoke-suite value |
| --- | --- |
| Database | One fixture-owned `postgres:17-alpine` Testcontainer, matching the existing PostgreSQL test fixture; random host port and disposable credentials/database. |
| Environment | Explicit `Development` for both application processes. |
| `ConnectionStrings__Flowbit` | Container connection string for the API only. |
| `Jwt__Issuer`, `Jwt__Audience`, `Jwt__Key` | Matching, test-only values for API and UI; create a sufficiently long signing key for the run. |
| `WorkflowApi__BaseUrl` | Actual discovered API address for the UI. |
| `WorkflowDurableProcessing__PublicationEnabled` | `false` on the API; all automated fixtures must be synchronous. |
| Host binding | `http://127.0.0.1:0` for API, UI, and editor host; no fixed ports or `localhost:0`. |
| Lifetime logging | Information level for `Microsoft.Hosting.Lifetime` so process listening addresses can be discovered. |
| Structured/file logs | Fixture-owned artifact locations; avoid writing logs into application source directories. |

Use a controlled child-process environment. Remove inherited application
configuration overrides that can change database, JWT, Kestrel endpoints,
HTTPS ports, actor/context settings, or the API URL before setting test values.
Do not mutate the test runner's global environment. Preserve operating-system
and Docker/runtime requirements. Explicit launch arguments must override launch
profiles and ambient URL settings; direct DLL launch does not use `launchSettings.json`.

Initialization sequence:

1. Verify required published outputs, create the run directory, and start
   capturing diagnostics before starting external resources.
2. Start the PostgreSQL container with no volume reuse. Wait for database
   readiness using Testcontainers and a bounded timeout.
3. Start the API using `dotnet <published DLL> --contentRoot <publish directory>
   --environment Development --urls http://127.0.0.1:0`. Use
   `ProcessStartInfo.ArgumentList`, redirected stdout/stderr, `UseShellExecute=false`,
   and `CreateNoWindow=true`.
4. Read both streams asynchronously. Parse only a validated loopback listening
   address, then probe `/openapi/v1.json`. Readiness requires HTTP success, not
   merely a matching log line. API startup must have completed its migrations.
5. Start the UI with the same launch rules and the discovered API URL. Probe
   `/token` and `/_framework/blazor.web.js`; later verify an interactive token
   action, because a successful prerendered page is insufficient.
6. Start the test-only editor host. Expose only the copied editor and explicitly
   needed assets; do not serve the repository root or add directory browsing.
   Return a harmless favicon response if needed to avoid irrelevant browser noise.
7. Create Playwright and launch its installed Chromium. Generate/apply a setup
   identity through `/token`, then use that JWT for an authenticated
   `/api/workflows/` read to verify API authentication and database access.

Treat any process exit before readiness as a setup failure, even with exit code
zero: the application entry points catch and log startup failures. Do not silently
connect to a developer's running stack if setup fails.

Start with explicit bounds: 120 seconds for database startup, 120 seconds per
application, 5 seconds per readiness request, 30 seconds for navigation, and
10 seconds for ordinary assertions. Allow up to 20 seconds for a five-second
polling observation. Bound a complete scenario to 120 seconds and teardown to
30 seconds per process. Tune only from recorded CI evidence. Use cancellation,
readiness polling, and Playwright assertions rather than fixed sleeps.

### Per-test isolation and cleanup

- Serialize every test in the new assembly. Use a fresh browser context and
  page for each scenario/viewport case, with downloads enabled and a declared
  viewport. Do not reuse cookies or local storage between tests.
- Before each runtime test, visit `/token`, clear the previous identity when set,
  then generate/apply a known setup identity. Create uniquely named workflows
  and instances over HTTP, capture returned IDs, and switch through `/token` to
  the scenario actor. Assert the displayed identity after each change.
- Keep HTTP setup clients' bearer tokens explicit; changing the UI identity
  must not accidentally change the actor used by an already configured fixture
  client. Never use synthetic auth middleware or bypass authorization.
- Do not depend on execution order or numeric IDs. Keep test data isolated by
  unique workflow keys within the run's disposable database; no shared database
  truncation is needed between tests. Do not mutate the checked-in examples.
- Clear identity and close every auxiliary page after each runtime test. If
  cleanup fails, report it and prevent the next test from proceeding with an
  unknown identity; the next scenario must still establish its own identity.
- Capture failure artifacts before closing the context. In fixture teardown,
  close Chromium/Playwright, stop the editor host, stop UI/API process trees,
  drain logs, then dispose the container. Handle partially initialized fixtures
  with the same cleanup path. Terminate only processes/containers owned by this
  fixture; preserve diagnostics and report cleanup failures.

## Fixture and assertion strategy

Create the smallest definitions that reach each required state. Reuse the
contracts and examples in the [example catalog](../../examples/README.md), but
store focused test fixtures with their prerequisites explained in the new
fixture README. Include a basic two-lane editor workflow, a claim-required
normal task, a parallel/complex gateway case, and a small multi-instance case.

Seed runtime fixtures using the existing workflow create/publish/start and
task-operation endpoints, following the [API reference](../api-guide.md).
Validate each setup response and its expected starting state before opening
the relevant page. Use HTTP for setup and post-action state verification;
execute the interaction being tested through the browser. No direct database
writes, fabricated audit DTOs, or application-global JavaScript mutations may
stand in for user behavior.

Prefer accessible roles, labels, button names, and existing section IDs. For SVG
objects, scope `data-id` under `#nodes` or `#lanes` using fixture-known IDs.
Use DOM/SVG reads for geometry and visibility, and browser mouse/keyboard APIs
for input. Avoid private helper calls, reflection, CSS-class inventories,
unbounded `networkidle` waits on Blazor connections, and brittle fixed screen
coordinates. Add a minimal stable selector only if an existing semantic locator
cannot identify the control; that production markup change still requires UI
verification and documentation assessment.

Instance-detail section links use page-owned scrolling and prevent the anchor's
default navigation. Assert the target section's scroll position/visibility and
that updating the URL fragment preserves the instance path/query. Use distinguishable fixture values and relative ordering
instead of hard-coding localized timestamp strings.

### Automated editor matrix

Run these scenarios at **1440 × 900** and **1024 × 768**. Keep fresh state for
each scenario so a failed edit cannot invalidate subsequent tests.

| ID | Real interaction | Required assertions |
| --- | --- | --- |
| E1 — Load/edit/save/reload | Use File → Load and the file input to upload valid JSON; select a node and edit its name in the inspector, edit the workflow name, save, then load the downloaded file into a fresh page. | Expected lanes/nodes/flows render; edited names appear; downloaded JSON preserves IDs, references, and the edited values; reloaded display agrees with the saved model. Compare relevant parsed JSON, not whitespace or serialization order. |
| E2 — Node drag | Select the select/move tool (the default pan tool cannot move nodes), locate a node body, and perform mouse down/move/up with intermediate steps. | Node geometry changes by the expected diagram-space delta, unrelated nodes remain fixed, connectors still attach, and the saved JSON contains the new position. Account for current SVG zoom and snap settings. |
| E3 — Lane drag | Drag a safe lane header/body point away from its resize handle and child nodes. | The lane and all contained nodes move by the same delta, their relative offsets remain unchanged, and nodes in another lane remain fixed. Verify persisted positions in downloaded JSON. |
| E4 — Validation feedback | Load an intentionally invalid fixture whose error survives load normalization, then invoke Save. | The `Workflow cannot be saved` dialog and expected error appear; focus enters its close control; no download is emitted. Close with Escape and verify the editor remains usable. Repair/reload valid input and show a subsequent save succeeds. |
| E5 — Keyboard/focus | Tab through toolbar controls, activate a menu with the keyboard, close it with Escape, use diagram search, and type in an inspector field. | Focus is visible and reaches the expected controls; closing the menu restores its trigger focus; search selection works; typing tool-shortcut letters in an input edits text without switching tools. Exercise undo/redo from outside a text input and preserve native input editing. |

For E1–E4, force the **download fallback** with a context initialization script
that makes `window.showSaveFilePicker` unavailable before the editor loads. This
is a test-only capability override, not a replacement implementation of `save()`.
Register the expected fallback-alert handler and download listener before the
Save click, assert the alert text, accept it, and save the real downloaded bytes
under the scenario artifact directory. Treat other dialogs as unexpected.

Do not assert that rejecting the native picker triggers a download: the editor
returns on cancellation/error. Manually verify native picker success and cancel
on a supported headed desktop Chromium browser without the override. Record the
browser/version and saved/reloaded file. If unavailable, leave that acceptance
item open; the automated fallback does not establish native-picker coverage.

### Automated runtime matrix

Run the primary behavior scenarios at **1440 × 900**. Add representative
navigation, detail-section, overflow, and keyboard checks at **1024 × 768** and
**390 × 844**. Stage 5's manual checklist still inspects every required display
state at these sizes; mobile browser automation is viewport coverage, not a
claim of touch-device coverage.

| ID | Setup and interaction | Required assertions |
| --- | --- | --- |
| R1 — Normal task lifecycle | Seed a claim-required task with a declared action variable; apply its authorized actor; open Inbox, claim, enter a value, and complete the action. | Claim/action controls follow ownership; task leaves the inbox; instance detail shows the resulting state/history/value. HTTP read-back confirms the action persisted once. Use task IDs from setup, not an arbitrary first row. |
| R2 — Instance navigation and basic states | Open a newly started instance from the list, follow detail section links, then navigate to a terminal instance and back. | Summary, labels, empty sections, current actions, and terminal state are correct; route/instance ID changes do not leave prior-instance rows behind. |
| R3 — Identity replacement | Keep detail open in one page; use a second page in the same UI process to change from authorized actor A to actor B without task authority, then clear identity. | Actor presentation/actions update, A's actions do not return after refresh, and protected operations follow the current identity. After reapplying A, legitimate controls recover. Close the second page before cleanup. |
| R4 — Gateway and MI detail | Seed small parallel/complex and MI states over HTTP, including completed MI child results. Open their sections and use section navigation. | Gateway labels, status/order, complex-state values, MI progress and submitted result JSON render correctly; navigation scrolls to the intended section. Use known rows with deliberately distinguishable values. |
| R5 — Real polling and disposal | Keep a poll-eligible MI or multi-position instance open; complete a sibling/child through a separately authenticated HTTP client, then navigate away and return. | The displayed progress changes without a manual reload within the bounded polling window; return shows current data; browser/host logs contain no new disposal or disconnected-circuit errors. Establish exact request-count and stale-response evidence server-side as described below. |
| R6 — Responsive controls and focus | At the two narrower widths, navigate using the actual responsive menu, open representative normal/gateway/MI detail, scroll tables, and keyboard-focus links/controls. | Key controls remain reachable; headings, section targets, JSON, and links remain usable; intended table overflow is contained. Compare existing responsive behavior; do not add unrelated mobile redesigns. |

Use R1 for **normal-action refresh** and R5 for **polling refresh**. Neither
implies that ordinary single-token tasks poll. Browser identity-change exercises
cover the user flow. Add a deterministic page-level regression in
[InstanceVariableUpdateUiContractTests](../../Flowbit/tests/Flowbit.Tests/InstanceVariableUpdateUiContractTests.cs):
hold actor A's refresh/action-discovery response in a delayed `HttpMessageHandler`,
change the identity to B while that request is in flight, release A's response,
then verify that only B's presentation/actions survive. Preserve the existing
post-render identity and delayed initial-load disposal tests. Characterize
relevant request counts before changes and assert unchanged counts/lifecycle
where required; do not claim existing test names alone prove these properties.

## Completing the outstanding Stage 5 gate

Maintain the following evidence checklist alongside the Stage 5 browser section.
All rows must have actual results before Stage 5 is marked implemented. A
passing CI smoke run satisfies only the scenarios it actually exercises.
Status after the Stage 7 smoke implementation (2026-09-20):

| Stage 5 requirement | Evidence to collect | Execution surface | Status |
| --- | --- | --- | --- |
| Newly started/empty, normal task, terminal detail | Summaries, section visibility/empty states, and available actions; screenshots for each state. | R1/R2 plus headed inspection. | Automated (R1/R2) passed; headed per-state screenshots not yet recorded — open. |
| Gateway and complex state | Labels, statuses, row ordering, gateway/complex section links and scroll targets. | R4 plus headed inspection. | Automated (R4) passed; headed inspection not yet recorded — open. |
| MI detail and submitted results | Progress, item ordering, flow labels, submitted JSON, completed/cancelled item presentation where applicable. | R4/R5 plus headed inspection. | Automated (R4/R5) passed; headed inspection not yet recorded — open. |
| Variables and shared bindings | Variable values and empty states; variable-to-update-audit navigation; value-free shared-binding table with a real catalog-backed definition. | Full acceptance fixtures; create prerequisites through HTTP. | Empty/normal variable states covered (R1/R2/R6); a real catalog-backed shared-binding walkthrough and variable-to-update-audit navigation still open. |
| Version-change and variable-update histories | Real actor/roles, reasons, JSON values, ordering, and links to the correct completed batch screens. | Separate full isolated stack with Worker-driven batches. Direct version/variable changes alone do not supply every required batch link. | Open — requires the separate Worker-driven stack. |
| Ordinary history attribution | Selected claims and delegation badges from actual authorized actions, with readable escaped values. | Configure test claim capture and delegation through existing contracts; perform the action, then inspect history. | Open — requires configured claim capture and a delegation grant in the acceptance stack. |
| Administrative action refresh | Exercise an existing administrative action in instance detail and confirm refreshed state/audit; follow its real administrative batch link. | Use a small synchronous example and authorized administrator. The immediate action atomically creates a completed one-item audit batch; that path needs no Worker. | Open — the smoke fixtures do not exercise administrative actions yet. |
| Identity and disposal during refresh | Change identity while refresh is occurring; navigate away and return; no revived prior-actor actions, stale display, duplicate polling, or disposal failures. | R3/R5, retained identity/disposal tests, and the new deterministic in-flight identity-response and relevant request-count characterization. | Passed — R3/R5 plus the new deterministic in-flight regression and request-count characterization. |
| Layout and navigation | All required states at 1440 × 900, 1024 × 768, and 390 × 844; real clicks, typing, keyboard traversal, scrolling, and link destinations. | Headed browser inspection and screenshots. | Automated viewport coverage passed (R6 at 1024/390; E matrix at 1440/1024); headed manual inspection with screenshots remains open. |
| Console/network and comparison evidence | Browser/version, exact localhost URLs, console warnings/errors, failed requests, relevant host logs, and before/after screenshots. | Automated artifacts plus acceptance record. | Partially collected (Chromium 151.0.7922.34, run artifacts include console/error/request capture and host logs); before/after pre-extraction comparison still open. |

Use the [getting-started guide](../getting-started.md),
[UI guide](../ui-guide.md), and [deployment guide](../deployment.md) for the
separate full stack. Give it its own database/volumes, project identity, ports,
and credentials. Enable the documented durable-publication settings and run the
Worker for queued batches; do not turn the routine no-Worker smoke fixture into
a partly durable environment. Use bounded checks for actual batch completion,
not sleeps or direct table edits. Preserve setup steps, fixture definitions,
actor roles/claims, API-created IDs, and cleanup instructions in the evidence.

Retain a pre-extraction visual reference from the appropriate Stage 5 parent
revision, using a separate worktree/stack if needed. Compare the same fixture,
viewport, and section in the current revision. These are review screenshots,
not checked-in pixel comparison baselines. If a required original reference or
scenario cannot be reproduced, record the limitation and keep its gate open.

Stage 5's seven components must remain display-only. Confirm the page still
owns refresh, polling, identity subscriptions, input state, and mutations. Do
not move fetching into components or alter request counts to make tests pass.
If acceptance exposes a product defect, record it, make a focused reviewed fix
with the required documentation/tests, and repeat its affected browser checks
before closing the gate.

## Diagnostics and CI

Register page-error, console, failed-request, and unexpected-dialog listeners
before navigation. Fail scenarios on uncaught page errors or unexpected console
errors. Record and investigate warnings; document any narrow, justified expected
message instead of suppressing whole categories. Separate browser console
results from server startup warnings, such as HTTP-only HTTPS-redirection
diagnostics. Inspect Blazor circuit/server errors in host logs as well.

On failure preserve, when available:

- Full-page and relevant-section screenshots, actual URL, viewport, browser
  version, scenario/fixture IDs, and exception/assertion details.
- A Playwright trace with screenshots/snapshots and browser console/error logs.
- API/UI stdout/stderr and file logs, editor-host diagnostics, container startup
  diagnostics, and the failed setup response without secret headers/tokens.
- Downloaded JSON relevant to a failed save assertion and the test TRX result.

Start tracing before the tested navigation; stop/save it before disposing the
context. Keep host/setup logs even when no test page was created. Successful
runs may discard traces while retaining TRX, a concise run manifest, and selected
acceptance screenshots. Use synthetic data and keep JWTs/connection credentials
out of ordinary diagnostic messages.

Add a `browser-smoke` job to the existing CI workflow:

1. Use Ubuntu 24.04, checkout, .NET 10, and Docker checks consistent with the
   existing job; give the job a bounded initial timeout of 25 minutes.
2. Publish API/UI, build the standalone browser project, and install Chromium
   with its Linux dependencies using the commands below.
3. Run the browser project explicitly, serially, with TRX output. Let a missing
   Docker daemon, browser binary, host, or required fixture fail the job instead
   of turning it into skipped tests.
4. Upload `artifacts/browser/` evidence with an `always()` artifact step so
   setup and test failures remain diagnosable. Exclude bulky published hosts;
   retain run diagnostics and test results, with a bounded retention period
   such as 14 days. Match the artifact action to repository policy.
5. Keep the existing `test` job independently runnable with its existing
   solution command. Do not use test retries or `continue-on-error` to hide a
   failing smoke scenario. Stage 7 acceptance requires observing the new remote
   job pass, not merely committing syntactically valid workflow YAML.

## Implementation sequence and checkpoints

1. **Baseline and scope:** run Docker/full tests; inspect current Stage 5
   regressions and fixtures. Record checkout, environment, counts, and open
   browser items. Keep historical records intact.
2. **Harness foundation:** add the standalone project, pins, static host,
   disposable database, published-host launching, readiness, serial ownership,
   and failure artifacts. Prove a real interactive token operation and editor
   load. Exercise a failed startup and confirm diagnostics/cleanup work.
3. **Editor coverage:** add E1–E5 and minimal fixture/helper code. Verify saved
   JSON and real SVG movement at both sizes. Complete native-picker manual
   evidence separately.
4. **Runtime coverage:** add HTTP setup, explicit identity resets, and R1–R6.
   Preserve existing identity/disposal tests and add the missing server-side
   in-flight identity-response regression. Establish relevant request-count
   evidence. Check that every smoke fixture can reach its expected state without
   a Worker.
5. **CI and contributor commands:** add the independent job and project README.
   Exercise setup from a clean local output directory and observe an actual CI
   run; confirm artifacts are available for an intentionally failed development
   run before removing the deliberate failure.
6. **Full Stage 5 acceptance:** use the checklist and isolated full stack for
   audit, attribution, batch links, refresh, disposal, and visual comparisons.
   Record each scenario's result and resolve blockers without broadening into
   the later UI extraction stages.
7. **Final candidate:** run relevant focused tests, the full existing suite,
   and the separate browser suite. Verify links/artifacts and update completion
   records only when the gates below pass.

Each checkpoint should be reviewable and buildable. Do not report the stage
complete after checkpoint 5 if Stage 5 acceptance is still pending.

## Validation commands

Run from the repository root. The project now exists; the commands below are
the implemented commands. They require .NET 10, Docker, and PowerShell 7
(`pwsh`), including when invoked from Bash. Both shells use the same arguments
and paths.

Existing baseline and focused regression commands, valid in PowerShell or Bash:

```text
docker info
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet test Flowbit/Flowbit.slnx --filter "FullyQualifiedName~EditorRuntimeSmokeTests|FullyQualifiedName~EditorNavigationTests|FullyQualifiedName~EditorValidatorCharacterizationTests|FullyQualifiedName~InstanceDetailDisplayComponentTests|FullyQualifiedName~InstanceReactivationUiContractTests|FullyQualifiedName~InstanceVariableUpdateUiContractTests|FullyQualifiedName~TestIdentityUiContractTests"
```

PowerShell setup and test commands:

```powershell
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --logger "trx;LogFileName=browser-smoke.trx" --results-directory artifacts/browser/test-results
git diff --check
```

Bash setup and test commands on Linux, including CI:

```bash
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --logger "trx;LogFileName=browser-smoke.trx" --results-directory artifacts/browser/test-results
git diff --check
```

The Linux installer additionally installs operating-system browser dependencies.
Use the same Release configuration for publishing, building, installing, and
testing. The headed mode is implemented and verified locally:
set `FLOWBIT_BROWSER_HEADED=1` before running the suite; it launches Chromium
headed with the same isolation and cleanup rules.

## Implementation results (2026-09-20)

The following records describe the initial implementation. The subsequent
[review fixes](#review-fixes-2026-09-20) supersede its harness and coverage details.

- **Suite:** `Flowbit/tests/Flowbit.BrowserTests/` — a standalone `net10.0`
  project outside both solution files. The project pins Microsoft.Playwright
  1.62.0, Testcontainers.PostgreSql 4.13.0, xUnit 2.9.3,
  xunit.runner.visualstudio 3.1.5, and Microsoft.NET.Test.Sdk 18.7.0, and
  references `Flowbit.Shared` for response DTOs only.
- **Local browser run:** `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore`
  — **17 passed, 0 failed, 0 skipped** (E1–E5 at 1440×900 and 1024×768; R1–R5;
  R6 at 1024×768 and 390×844). Duration 1 m 8 s. Browser: Chromium 151.0.7922.34
  on Windows. Headed mode (`FLOWBIT_BROWSER_HEADED=1`) was verified separately
  with the same pass result for the E4 pair.
- **Focused UI contract tests:** `dotnet test Flowbit/Flowbit.slnx --filter FullyQualifiedName~InstanceVariableUpdateUiContractTests`
  — **6 passed, 0 failed, 0 skipped**, Duration 11 s. Includes
  `InFlightActionDiscoveryResponseCannotRevivePriorActorActions` and
  `PollEligibleInstanceRefreshesOncePerTickAndStopsAfterDispose` (one
  `GET /api/instances/42` after load, two after one 5s tick, unchanged after
  renderer dispose). The full `Flowbit.slnx` suite was not re-run for this
  gap-fix.
- **`git diff --check`:** clean (CRLF checkout warnings only).
- **Harness:** one serialized xUnit collection fixture owns the disposable
  PostgreSQL container (120s start bound), the published API/UI processes on
  ephemeral loopback ports (app config stripped; `DOTNET_ROOT` and similar
  runtime vars preserved), readiness probed over `/openapi/v1.json`, `/token`,
  and `/_framework/blazor.web.js`, the in-process editor host serving the
  repository `flowbit-editor.html`, and Playwright. Each `RunAsync` body is
  bounded to 120s. Setup identity is minted through the real `/token` screen
  and used for HTTP setup. Workflow fixture POST suffixes the authored `id`
  with a unique token; checked-in JSON is unchanged. A passing body still fails
  when page errors, unexpected `console.error` messages, or unexpected dialogs
  were recorded (snapshots, not drained queues). Identity cleanup failures are
  written to `setup.log` and fail a passing scenario; the next scenario still
  clears and applies identity. Each run writes `manifest.json` (browser
  version, loopback URLs without secrets, headed flag, scenario names). Partial
  startup failure writes `setup.log` and disposes owned resources. Failure
  artifacts (trace, screenshot, failure log, host logs) land under
  `artifacts/browser/runs/<run>/`.
- **Editor matrix:** E1–E5 pass at 1440×900 and 1024×768 with the forced
  download fallback (context init script blocking `showSaveFilePicker`),
  fallback-alert handling, and downloaded-JSON assertions. E4 asserts the
  visible title `Workflow cannot be saved`. E5 covers keyboard menus, Escape
  focus restore, `/` diagram search with result selection, and undo/redo
  invariants, waiting on the name field rather than a fixed sleep.
- **Runtime matrix:** R1–R6 pass as specified. R1 asserts the Approve action is
  absent before claim and present after. R2 asserts empty variables/history on
  the running instance and that prior instance rows are gone after navigating
  to the terminal instance. R6 scrolls the gateway-scopes table and Tabs from a
  section link onto a reachable control.
- **CI:** the `browser-smoke` job is added to
  [tests.yml](../../.github/workflows/tests.yml) with publish/build/install
  steps, `--with-deps` Chromium, TRX output, and an `always()` artifact upload
  (runs + test results only, 14-day retention). A remote run has not been
  observed yet; this change does not claim a CI pass.
- **Headed mode:** `FLOWBIT_BROWSER_HEADED=1` verified by running the E4
  scenario pair headed locally with an identical result. Native-picker
  success/cancel, Worker-driven Stage 5 batch links, and headed screenshots
  remain open.

## Review fixes (2026-09-20)

- Browser diagnostics now attach to every context page and include transport
  failures. Every scenario retains `diagnostics.json`, including warnings and
  failed requests on successful runs. Three fault-injection tests verify the
  harness; `harness-expected-*` directories deliberately contain injected errors.
- Scenario budgets cancel HTTP/retry work, capture evidence, close the browser
  context, and drain the body before returning. An uncooperative body invalidates
  and stops the shared stack so later scenarios cannot reuse unknown state.
- The static host serves the build copy of the editor and ignores ambient
  configuration. API/UI log pumps drain redirected pipes before closing files.
- Fresh runtime navigation waits for nonvisual renderer-state markers. R2/R5
  use actual links/back navigation and assert that the document was retained.
  R4 asserts rendered statuses, phases, and cycle values. R6 uses real horizontal
  wheel input, checks clipped-column reachability, and parses submitted JSON.
- E2 checks both connector endpoints touching the moved node with the intended
  arrowhead clearance. File loading waits for the fixture's workflow name.
  E5 starts with the search dock hidden and exercises `/` to reveal and focus it.
- The identity regression begins with rendered actor A, gates A/B responses
  separately, and checks both successful and failed B refreshes. Removing the
  epoch guards in an isolated copy now makes the failed-replacement case fail.
  Polling assertions wait for both request counts and are serialized against
  other integration collections, with a 30-second observation bound.
- Stronger checks exposed two focused product fixes: section scrolling now
  preserves the instance path/query when writing its URL fragment, and `/`
  reveals the hidden editor dock before focusing its input. These change neither
  workflow contracts nor API request counts. See the [UI guide](../ui-guide.md#inspect-instances-and-activity)
  and [editor overview](../../README.md#design-visually).

Validation of the review fixes:

- `dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet` — **1,892 passed,
  0 failed, 0 skipped** (4m 9s), including the seven focused instance refresh
  tests. Results: `artifacts/browser/fix-test-results/fix-full-suite.trx`.
- `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj
  -c Release --no-build --no-restore` — **20 passed, 0 failed, 0 skipped**
  (59s). Results: `artifacts/browser/fix-test-results/fix-browser-verified.trx`.
  This repeats an earlier successful 20-case run after the build-copy change.
- After correcting the Serilog severity matcher, synchronizing diagnostic-page
  snapshots, and disabling screenshot animations, the browser command with
  `--filter 'FullyQualifiedName~R5_|FullyQualifiedName~E5_|FullyQualifiedName~HarnessDiagnosticsTests'`
  passed **6/6** (15s). Results:
  `artifacts/browser/fix-test-results/fix-final-diagnostics.trx`; screenshots
  and logs: `artifacts/browser/runs/20260920-161130-3061d76b/`.
- Real browser: **Chromium 151.0.7922.34**, headless on Windows; editor
  `http://127.0.0.1:59467`, UI `http://127.0.0.1:59465`, API
  `http://127.0.0.1:59463`. E1–E5 exercised real keyboard input, pointer drags,
  file loading, and fallback downloads at **1440×900 and 1024×768**. R1–R5
  exercised identity, task actions, section navigation, detail projections,
  polling, and in-app disposal/navigation at **1440×900**; R6 exercised section
  links, responsive controls, horizontal wheel scrolling, and submitted JSON
  at **1024×768 and 390×844**.
- Every product scenario retained **zero browser console errors, warnings,
  page errors, and failed requests**. Deliberately injected harness diagnostics
  are separate. Evidence, including search/responsive screenshots, is under
  `artifacts/browser/runs/20260920-160449-c37842c3/`.
- Host logs were inspected separately. Fresh-database startup logged a missing
  migration-history-table probe before successful migration; HTTP-only hosts
  warned about the absent HTTPS redirect port; restricted actors generated
  expected authorization warnings. During R6 navigation, three canceled
  dashboard `/api/instances` requests logged `OperationCanceledException` at
  error level with status 500. These were server-side cancellation logs, not
  browser request failures; the run is not claimed to have error-free host
  logs. R5's error matcher now recognizes timestamped Serilog severity labels.
- Isolated mutation: removing the identity epoch guards produced the expected
  failed-replacement regression failure (one failed, one passed), demonstrating
  that stale actor actions are detected. The isolated source was restored.
- Isolated harness probes confirmed that an ambient Kestrel IPv6 endpoint does
  not override the editor's ephemeral IPv4 loopback listener, and that all
  100,001 stdout lines plus final stderr survive process exit and log draining.
- Documentation validation: **265 relative links/anchors** and **six fixture
  JSON files** passed; `git diff --check` passed (line-ending notices only).
  Restore still reports the existing SSH.NET 2025.1.0 NU1903 advisories.

The native picker, complete Stage 5 manual acceptance (including Worker-driven
batches and visual comparisons), and remote CI observation remain open.

## Acceptance criteria

- [x] A fresh checkout can build/publish/install/run the documented commands;
  the project remains outside both `Flowbit.sln` and `Flowbit.slnx`.
- [x] The harness uses real localhost browser/application processes and a
  disposable database, with no dependency on machine data, fixed ports, or
  an existing development identity. Partial startup failure preserves evidence
  and cleans up owned resources.
- [x] E1–E5 and R1–R6 pass at their declared viewports with meaningful behavior
  assertions. No UI interaction is replaced by directly invoking app internals.
- [ ] Native-picker manual success/cancel checks are recorded; fallback download
  is automated. Unavailable checks are explicit and remain open.
- [ ] The existing focused/full suites pass with actual counts recorded and no
  unexplained new skips; the separate browser suite passes locally. The
  `browser-smoke` CI job YAML is configured; a remote CI pass has not been
  observed yet.
- [x] The browser job produces TRX and usable failure traces/screenshots/logs,
  including setup-failure diagnostics; normal tests require no browser install.
- [ ] Every Stage 5 checklist row is completed, including genuine batch links,
  claims/delegation, all three viewports, before/after references, and unchanged
  page ownership/request behavior. Console/network results are recorded.
- [x] Documentation, links, commands, and fixture JSON match the implemented
  result; `git diff --check` is clean.

## Documentation, delivery, and rollback

During implementation update these owning documents together:

- This plan, the [refactoring index](README.md), the
  [remaining-gaps roadmap](remaining-gaps-implementation-plan.md), and
  [gap 7](gaps.md#7-automated-visual-and-interaction-coverage): distinguish smoke
  coverage from residual manual coverage and record actual completion status.
- [Stage 5](stage-05-instance-detail-components.md): append its browser evidence
  and change status only after the complete gate passes. Preserve the previous
  pending record as history rather than implying it had already been verified.
- [Runtime verification instructions](../../Flowbit/README.md#verification) and
  relevant [contributor browser guidance](../../AGENTS.md#mandatory-real-browser-ui-verification):
  explain separate browser setup, commands, automation coverage, and manual
  native-picker/full acceptance responsibilities.
- The new browser-project/fixture READMEs and [documentation home](../index.md):
  make setup, evidence locations, and the completed stage discoverable.

Review the getting-started/UI guides and example catalog against the actual
walkthroughs. Update public pages only where an instruction becomes inaccurate;
test-only fixtures do not require unrelated product screenshots or API-contract
edits. Keep runnable PowerShell/Bash examples aligned and validate changed
relative links, anchors, fixture JSON, and referenced screenshots.

The implementation report must separately list automated commands/counts;
browser/version, localhost URLs, interactions, viewports, and console results;
Stage 5 checklist evidence; documentation changes; and unavailable checks.
Include a screenshot if appearance changed. Add implementation commit(s) and
artifact/CI references only after they exist.

Rollback removes the new test project/job and reverts associated contributor
instructions. No application deployment or data conversion is required for a
test-only implementation. Any independently required product fix has its own
rollback and validation. If coverage is removed, reopen the browser-automation
gap; preserve valid historical Stage 5 acceptance evidence rather than erasing
it. If only the smoke suite is delivered, mark Stage 7 in progress and Stage 5's
remaining acceptance explicitly open.
