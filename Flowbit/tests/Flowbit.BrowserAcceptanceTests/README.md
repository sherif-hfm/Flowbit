# Local Worker-enabled browser acceptance

This standalone .NET 10 xUnit project closes the runtime browser checks in
[Stage 5](../../../docs/refactoring/stage-05-instance-detail-components.md) and
[Stage 11](../../../docs/refactoring/stage-11-administrative-action-display-components.md).
It is outside both solution files and GitHub CI. The existing
[browser smoke suite](../Flowbit.BrowserTests/README.md) remains Worker-free;
run it separately to retain the editor and basic runtime checks.

## Setup and commands

Prerequisites: .NET 10 SDK, a running Docker daemon, and Playwright Chromium.
Run from the repository root. These commands work unchanged in PowerShell 7
and Bash:

```text
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj -c Release -o artifacts/browser/hosts/worker /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release
pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore --logger "trx;LogFileName=acceptance.trx" --results-directory artifacts/acceptance/test-results
```

On Linux add `--with-deps` to the browser-install command. For headed visual
inspection, use the same test command with `--environment
FLOWBIT_BROWSER_HEADED=1`. To run a subset, append `--filter
FullyQualifiedName~InstanceDetailAcceptanceTests` or
`--filter FullyQualifiedName~AdministrativeBatchAcceptanceTests`.

Run final regressions separately:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
git diff --check
```

Publish/build before testing; the fixture never compiles applications. Do not
rebuild either browser project while its test process is using the binaries
(particularly on Windows). There are 24 cases: nine instance-detail cases,
11 administrative cases, and four harness regressions. Test counts are separate
from the number of screenshots or the earlier one-off runner's 11 groups.

## Isolation and evidence

The acceptance factory opts into durable publication and a transparent API
recording proxy. Each run owns a disposable PostgreSQL container, fresh JWT
configuration, published API/UI processes, Chromium, and its Worker processes.
Workers start only when a scenario needs them. Tests are serial; browser contexts
and identities are reset between scenarios. Nothing connects to a developer
database or writes application tables directly.

All setup uses authenticated HTTP, including catalog entries, compatible workflow
versions, delegation grants and batches. UI identities and selected claims are
entered through the real token screen. API and Worker audit capture allowlists
`department` and `region`; expression claim allowlisting is independent.

The proxy receives the UI's server-side HTTP calls, forwards real request and
response content unchanged, and can hold one matching response for a bounded
period. It records method, path, status and timing; it never records authorization
headers or response bodies. Proxy and controlled-service hosts ignore ambient
Kestrel settings and listen only on ephemeral loopback ports. Gates are released
during cleanup. Smoke stack construction cannot start a Worker.

Run artifacts are under `artifacts/acceptance/runs/<timestamp>-<id>/`:

- `manifest.json`: browser, localhost addresses, scenario names and host overrides.
- API/UI/numbered Worker logs, `setup.log`, and `api-requests.json`.
- Scenario screenshots, entity evidence JSON, and browser diagnostics.
- Failure screenshots and Playwright traces when a scenario fails.

The shared smoke harness enforces scenario cancellation and captures console,
page, dialog and network diagnostics. Always inspect host logs separately:
expected startup messages (initial migration-history lookup, HTTP-only redirects,
Worker health checks before readiness) are not evidence of a browser error.
Browser contexts use UTC and an English locale for comparable screenshots;
server-formatted dates retain the host timezone. Diagnostics
and screenshots contain only synthetic test data but may include generated IDs
and timestamps. Native save dialogs and visual review remain manual gates.

## Coverage

| Cases | Required behavior |
| --- | --- |
| Instance audit, three viewports | Real catalog binding with no leaked value; compatible upgrade/downgrade; two variable-update batches; actor/role/reason/value ordering; section and exact batch links with Back navigation. |
| Claim/delegation, three viewports | Owner claim, real grant, delegated UI completion; selected claim snapshots, escaping and omission of unselected claims; real delegation attribution. |
| Representative states, three viewports | Normal and terminal detail, empty sections, gateway/complex state, active MI and early-quorum completed/cancelled children, submitted JSON and section navigation. |
| A1 | UI prepare/confirm; 51 positions; Preparing/Ready/Queued/Completed rendering and captures; frozen audit; item pagination and captured empty-filter restore; exact instance link and Back. |
| A2 | More than 25 real batches; independent history pagination; keyboard Open; ready cancellation with reason and captured empty-history-filter recovery. |
| A3, two modes | Force parent and complete unfinished children; distinct position/task counts and frozen mode labels. |
| A4 | Timer-boundary action label and actual subscription; no submitted-variable audit. |
| A5 | Real ineligible/skipped/failed results with captured expanded issues/errors and precedence; navigation while preparation is running. |
| A6 | Real cancelled batch with null completion, continued polling, Worker lease recovery, preserved completed work, no duplicate committed actions, terminal polling cessation. |
| A7 | Identity replacement and clearing hide restricted data; permission-denied and cleared-identity captures; authorized deep-link recovery. |
| A8, three viewports | Held genuine history/items responses reveal loading branches; release restores the exact real instance row/link and pager; one history and one items request, with no duplicate request after recovery. |
| Harness, four cases | Smoke rejects Worker activation; proxy transport/gating preserves real content; cleanup releases/disarms gates; ambient endpoint configuration is ignored. |

Runtime viewports are 1440×900, 1024×768 and 390×844. Administrative cases that
are not viewport theories capture the relevant state at all three sizes.
Captured states include preparation/queueing, ready/completed results, empty
filters, both multi-instance modes, timer actions, skipped/ineligible/failed
items, interrupted cancellation/recovery, denied/cleared identities, and
history/items loading and recovery. Administrative captures retain both full
pages and section viewports, after finite CSS transitions settle; full pages
are returned to the top with real wheel input before capture.
[Fixture prerequisites](Fixtures/README.md) document the new instance-detail
definition; administrative variants reuse the existing smoke definition.

A6 intentionally stops only its own Worker while a harmless HTTP service call
is held. The already-started item remains committed while the action transaction
rolls back, permitting UI cancellation with `CompletedAt` still null. After
screenshots and at least two natural polling cycles, the test releases the
service and restarts the Worker. It uses the normal lease duration and a bounded
recovery wait. The harmless external call may repeat after interruption; the
test asserts that committed workflow action evidence does not duplicate.

## Historical comparisons and manual checks

To inspect an independently published historical stack, use test-process
environment overrides (the `--environment NAME=value` option works in both
PowerShell and Bash):

| Variable | Meaning |
| --- | --- |
| `FLOWBIT_ACCEPTANCE_API_HOST` | Absolute directory containing the published `Flowbit.Api.dll`. |
| `FLOWBIT_ACCEPTANCE_UI_HOST` | Absolute directory containing the published `Flowbit.Ui.dll`. |
| `FLOWBIT_ACCEPTANCE_WORKER_HOST` | Absolute directory containing the published `Flowbit.Worker.dll`. |
| `FLOWBIT_ACCEPTANCE_ARTIFACT_ROOT` | Separate output root for comparison evidence. |
| `FLOWBIT_ACCEPTANCE_LEGACY_UI=1` | Only for the Stage 5 baseline without renderer markers; readiness uses a real navigation-menu round trip. |
| `FLOWBIT_BROWSER_HEADED=1` | Visible Chromium windows for local inspection. |

Leave overrides unset for current hosts; provide actual absolute paths when
overriding them. Stage 5 uses pre-extraction commit `0d23c7a`; Stage 11 uses
`5403136`. Publish their untouched API/UI/Worker sources separately, then run the
same selected scenarios against fresh disposable databases. Compare equivalent
fixtures and viewports; record expected ID/timestamp wrapping differences.
The Stage 5 baseline has a documented historical `/#history` navigation bug;
retain those failed baseline assertions as historical evidence, never describe
that old build as passing current navigation tests. Stage 11's intentional
polling-render fix also differs from its baseline.

Retain before/after captures and inspect them; this suite has no permanent pixel
baselines. A passing headless run does not replace the required headed review.
For editor native-picker acceptance, serve the real editor over localhost,
use a headed browser with `showSaveFilePicker` available, save and reload through
the actual OS dialog, then cancel another save and verify no fallback download
or model loss. Repeat at 1440×900 and 1024×768. An unavailable native-control
surface leaves that Stage 7 item open, even when every automated suite passes.
