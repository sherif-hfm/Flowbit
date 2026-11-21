# Local Worker-enabled browser acceptance

This standalone .NET 10 xUnit project closes the runtime browser checks of
Stages 5 and 11 of the [refactoring roadmap](../../../docs/index.md#refactoring-history)
(the instance-detail and administrative-batch display components), and
exercises focused [canonical workflow examples](../../../examples/README.md#coverage-matrix).
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
For all canonical-example runtime, editor, and UI scenarios, append
`--filter FullyQualifiedName~ExampleWorkflow`. Use
`--filter FullyQualifiedName~ExampleWorkflowAcceptanceTests` for runtime-only
coverage, `--filter FullyQualifiedName~ExpressionTimerAcceptanceTests` for
the expression-timer scenarios,
`--filter FullyQualifiedName~CronScheduleAcceptanceTests` for cron timer
starts, and
`--filter FullyQualifiedName~CallActivityAcceptanceTests` for call activities, and
`--filter FullyQualifiedName~AutomaticMultiInstance` for automatic multi-instance
service, script and call bodies (TASK-05).
Use `--filter FullyQualifiedName~InstanceDiagramHistoryAcceptanceTests` for
UI-14's durable, delayed-response, keyboard-paging, reactivation, missing-instance,
API-failure recovery, and expired-token retry scenarios.

Run final regressions separately:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
git diff --check
```

Publish/build before testing; the fixture never compiles applications. Do not
rebuild either browser project while its test process is using the binaries
(particularly on Windows). The original coverage has nine instance-detail cases,
11 administrative cases, and four harness regressions; the example suite adds
its runtime and browser scenarios. Test counts are separate
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

`ExampleAcceptanceFixture` owns the example stack and its controlled retry
service. It reads canonical JSONs directly and changes only workflow identity
for isolation. `BrowserAcceptanceOptions.WorkflowContextConfig` supplies the
same narrowly scoped example configuration to API and Worker: the loopback
`exampleApiBaseUrl`, fixture-owned synthetic `exampleApiToken`, and message
secret. The
ordinary smoke fixture remains unable to start a Worker. Example tests do not rewrite
graphs, database rows, clocks, or real API responses to force an outcome.

`BrowserAcceptanceOptions.HostSettings` adds test-owned host configuration
(`Section__Key` environment names) to API and Worker; it cannot replace a value the
fixture already sets. No collection needs it at present. The
`automatic-multi-instance-worker-stack` collection (`AutomaticMultiInstanceAcceptanceFixture`)
runs the automatic multi-instance scenarios on a stack of its own, with default host
configuration, so their Worker load and held responses never reach other collections.

The proxy receives the UI's server-side HTTP calls, forwards real request and
response content unchanged, and can hold one matching response for a bounded
period. It records method, path, status and timing; it never records authorization
headers or response bodies. Proxy and controlled-service hosts ignore ambient
Kestrel settings and listen only on ephemeral loopback ports. Gates are released
during cleanup. Smoke stack construction cannot start a Worker.

The instance-diagram error scenarios may stop and resume only their own
disposable PostgreSQL container to produce genuine API failures. This helper is
acceptance-only and refuses to stop the database while a Worker is running.
Cleanup resumes the container before the stack is disposed. The API proxy still
forwards the real responses unchanged; no response payload, application state,
clock, or database row is altered to simulate an error.

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
| Call parent and child, three viewports | The paired examples with a real Worker. A parent shows its running call and marks the call node on its diagram; recorded history shows the incoming flow while its outgoing flow remains absent. Its link opens the child with fresh page state and history off, and the child's links return. A person finishes the child, the result reaches the parent as `callActivity`, and the panel shows the pinned version and delivered result; enabling history then shows the outgoing traversal. |
| Call rejection | A child's error end faults the parent at the call with `call_child_faulted`, and the boundary variant writes the diagnostic and continues. |
| Call cancellation, two viewports | With the Worker stopped, cancelling the parent returns at once, the child refuses actions with `409` and warns that its cancellation is in progress, and the restarted Worker finishes it. |
| Call incident | A call whose target has no published default shows **Queued** with its code and no child, and the incident's retry starts the child once a default exists. |
| Caller confirmation, two viewports | **Set default** names an incompatible caller, Escape keeps the default and returns focus, confirming still changes it, and deleting the default names its successor. |
| A1 | UI prepare/confirm; 51 positions; Preparing/Ready/Queued/Completed rendering and captures; frozen audit; item pagination and captured empty-filter restore; exact instance link and Back. |
| A2 | More than 25 real batches; independent history pagination; keyboard Open; ready cancellation with reason and captured empty-history-filter recovery. |
| A3, two modes | Force parent and complete unfinished children; distinct position/task counts and frozen mode labels. |
| A4 | Timer-boundary action label and actual subscription; no submitted-variable audit. |
| A5 | Real ineligible/skipped/failed results with captured expanded issues/errors and precedence; navigation while preparation is running. |
| A6 | Real cancelled batch with null completion, continued polling, Worker lease recovery, preserved completed work, no duplicate committed actions, terminal polling cessation. |
| A7 | Identity replacement and clearing hide restricted data; permission-denied and cleared-identity captures; authorized deep-link recovery. |
| A8, three viewports | Held genuine history/items responses reveal loading branches; release restores the exact real instance row/link and pager; one history and one items request, with no duplicate request after recovery. |
| Harness, five cases | Smoke rejects Worker activation; proxy transport/gating preserves real content; cleanup releases/disarms gates; ambient endpoint configuration is ignored; an inherited automatic multi-instance switch is stripped from hosted processes. |
| Diagram history durable MI, 1440×900 and 390×844 | A real async-after source enters an async-before sequential MI task. Before the Worker runs there is no target entry count; after fan-out, Refresh shows one traversal and two execution records, including the pending child. The real version-change form, compatibility preview and confirmation preserve the toggle and refresh the new exact version's rebound target evidence; two real votes produce one outgoing traversal. |
| Diagram history identity, 1024×768 | The proxy holds genuine successful summary and version-filtered execution responses while a second tab changes identity. History resets and released old responses cannot restore counts/links. An ordinary reviewer can turn history on again but sees no restricted execution links. |
| Diagram history durable boundary, 1440×900 | An initially true interrupting conditional boundary queues its real durable continuation. Worker execution adds exactly the ordinary incoming flow and the boundary's outgoing flow; keyboard list selection opens the boundary details. |
| Diagram history keyboard paging, three viewports | Fifty real loop actions create 51 execution records. Holding the genuine page response keeps keyboard focus on Next or Previous during loading and at the final/first-page boundary; repeated keyboard activation while unavailable sends no extra request. A real `NodeExecution.RequiredRole` change between the held historical response and current-position revalidation clears both sets of execution links while preserving the diagram summary. The setting is restored afterward. |
| Diagram history reactivation, 1440×900 and 390×844 | The real reactivation form resumes a completed instance while history stays enabled. Current/final markers describe the new run, both review executions remain available, and completing the second run increments the outgoing flow to two without inventing a second start traversal. |
| Diagram history missing instance, three viewports | A genuine instance-detail `404` displays the missing-instance message and address/list guidance with no diagram, and does not suggest changing identity. |
| Diagram history refresh recovery, three viewports and two triggers | Stopping the fixture's database makes manual Refresh fail on instance detail, or makes a real action's following flow lookup fail after its successful action and held detail response. Enabled history shows a local error and retry control. After the database resumes, Retry reloads detail and matching history while preserving the toggle. |
| Diagram history expired-token retry, 390×844 | A short-lived fixture token is applied through **Apply pasted token** and accepted by the real API. A genuine database outage creates the local history retry state. After database recovery and real token expiry, keyboard Retry receives `401` without an identity-change action, clears protected detail and the diagram, and shows the unavailable state with identity guidance and **Retry instance**. No clock override or synthetic response is used. |
| Canonical durable examples | REST fail-once success, three-attempt exhaustion and fenced incident recovery; one-attempt `boundaryFirst` versus exhausted `retryFirst` catches, with no incident for handled failures. |
| Canonical messages and scopes | Timely receipt versus 30-second deadline; rejection of late delivery without mapped writes; inner MI/message/timer cancellation with surviving outer work; ordinary versus terminate end with live siblings. |
| Canonical role management | One completed, one active, one pending child; policy replacement preserves ownership/history, applies when pending work activates, and distinguishes identical versus conflicting stale edits. |
| Existing example corrections | Two-step approval's actual one-minute timer return; quorum `joinCancellation` cancels the third review before finalization. |
| Purchase request showcase | Eight real actors: a `${sys.user}` requester, a duplicate business key's `409`, a direct-assignee review with its working-day due time, priority and queued deadline, a send-back and resubmission with the new priority, a two-of-three board quorum that cancels the third item, a claimed budget check beside a manager-assigned supplier selection, and a purchase order inherited by the same buyer. Separately: a manager rejection and two board rejections reach their own ends, **No Budget** terminates and cancels supplier selection, the third send-back closes the request through `FlowInfo`, a withdrawal, and a Worker-run administrative timer action that escalates a review to a claimed `DepartmentHead` task. |
| Order fulfilment showcase | A real message start normalises its lines, refuses a repeated idempotency key and an open business key with `409`, checks credit and reserves each line on the Worker against the controlled service, and completes on a correlated carrier message whose wrong reference gives `400` and repeated delivery key `409`. Separately: a `review` decision runs the called manager approval, a `409` reservation stops the body for a shortage task whose retry succeeds after restocking, and the real one-minute expedited timeout leads to **Chase Carrier**; an invalid line faults at once with `ORDER.INVALID_LINES`, a `403` faults with `ORDER.CREDIT_REFUSED`, a rejected override ends at **Order Declined**, a `503` makes three attempts and an incident whose retry continues after recovery, a `200` with a decision the workflow refuses ends in an incident the same way, and a shortage can also cancel the order. |
| Expression timers (EVENT-01), 1440×900 and 390×844 | The canonical contract-end wait's `${contractEnd}` job is due exactly at the supplied time, on instance detail and in the **Operations** due-time title; an administrative variable update moves the input a day later and the fixture restarts the Worker, yet the saved time fires and **Renew or Close Contract** opens. **Administrative actions** lists the waiting review with both boundaries and their saved due times; a non-interrupting `${reminderAfter}` boundary then adds **Send Reminder** beside the open review, the raw `deadline` boundary then interrupts it, and after completion the reactivation preview warns `timer_boundary_already_due` for that boundary on instance detail. A staged script's boundary that reads a missing `slaDuration` fails both Worker attempts with the job's value-free failure description, and **Operations** lists the open `job_execution_failed` incident; after the value is repaired through the API, keyboard Retry schedules the boundary an hour ahead and the intake continues. |
| Cron timer starts (EVENT-09), 1440×900, then a 390×844 capture | A published `* * * * *` start in `Etc/UTC` queues its first whole minute after default activation, shown in **Operations** by its due-time tooltip. Each real minute starts one instance at **Review scheduled run** and queues the next minute from the nominal time. With the Worker stopped for 75 seconds past an occurrence, the job stays queued; after the fixture restarts the Worker it completes without a start and the schedule resumes at the first minute after the restart, then fires there. A second version with `0 9 1 1 *` in `Asia/Tokyo`, made the default over HTTP, retires the old schedule, queues midnight UTC on 1 January, and no further minute starts an instance. |
| Automatic service bodies (TASK-05B), queued/running/completed captures at three viewports | With the Worker stopped the entry is queued and no body exists; once it runs, three held items are active, nobody can act on them, and the diagram details say the body waits for its items. Answers released in reverse order complete out of order, the page's polling shows the count move, and the aggregate keeps index order. Each item sent one request keyed `item-<its job id>`. A sequential body sends nothing for item 1 until item 0 has answered, and an empty collection completes with `[]` and no request. |
| Service failures and recovery | A transient `503` recovers on its automatic retry while a twice-failing item opens an item incident; the blocked screen, a reviewer's `403` retry, and keyboard **Retry** on Operations follow, and only the failed item calls again with the same key. A caught `409` takes the catch-all boundary with the partial result, and a held item's late answer changes nothing. An answer above 1 MiB fails only its item with `service_task_failed`, never truncated. A shared target changed while an item's request is open, and a shared aggregate changed while the body runs, each open `output_version_conflict`; Operations retry publishes the saved candidate with no new request. |
| Service boundaries | An interrupting timer starts when the body initializes, not at the queued entry, cancels running items, and late answers commit nothing; a non-interrupting one adds its handler while items keep running. A condition interrupts a queued entry before any item exists. With two parallel bodies, an interrupting condition on one leaves the other running, and a non-interrupting condition adds a handler beside it. An interrupting timer ends a body waiting on an item incident. |
| Script bodies (TASK-05C) | NCalc sequential assignments see earlier writes; a 60-item JavaScript body pages 50 + 10 items with payload detail. A held items response shows its loading state, an identity change in another tab resets the list and the released stale answer never appears, and a reviewer sees progress but no payload (`403`/`404` detail). A JavaScript item failure waits in an incident until an interrupting condition ends the body. |
| Version blocker and human MI | An open body blocks **Preview compatibility** with `automatic_multi_instance_open`; after it settles the same preview has no such blocker. A human multi-instance task keeps its own section and has no automatic section. |
| Automatic call bodies (TASK-05D) | Queued `multiInstanceStart` entry with the Worker stopped; one child per item pinned to version 1; item → child → parent links, a person completing a child from its page, out-of-order completion and ordered results. A sequential body keeps its pin after a new default, refuses deleting the pinned version (`409`), and the next item reads a changed shared input. Parent cancellation fences waiting children at once and keeps a completed child; interrupting and non-interrupting timers while children wait; a condition interrupting a queued entry; a missing target's entry incident (after the control retries, about six minutes) whose Operations retry starts each child once; a shared output conflict on delivery whose retry keeps the same child and invocation; a faulted or directly cancelled child taking the boundary with the partial result. |
| Hosted authoring, 1440×900 and 1024×768 (390×844 captures) | Service, NCalc, JavaScript and call bodies show their section, mode, collection and canvas marker; concurrency undo/redo; sequential call validation; a non-interrupting conditional boundary connected on the canvas; save, reopen and API read-back; an import with a non-object `multiInstance` and an unknown mode is reported on save, repaired, and saved. A skipped test records the canvas ring that stays solid after **Interrupt attached activity** is unchecked. |
| Stale cron preview, 1440×900 and 1024×768 | With no Worker, the proxy holds the API's real `200` answer to **Preview next 5** while the button reads as busy, keeps focus, and the status says it is calculating. Editing the expression, or selecting another node and coming back, returns the preview to idle, and the released answer is discarded: no table appears for two seconds. A fresh request then lists both offsets of the 25 October repeated hour for the edited expression. |
| Signals S1 (EVENT-04), 1440×900 and 390×844 | Each signal scenario publishes `examples/signals/01-signal-catch-policy-change.json` under its own family and signal name. **Signals** counts 101 waits for the name, the review repeats it, and the page throw freezes 101 recipients. The page polls the live broadcast to **Completed** with 101 delivered; deliveries page 50 + 50 + 1. Three sampled instances each have one `signalReceived` history row, no remaining wait, and an active recalculation task. A delivery's instance link opens instance detail, whose history names the signal, thrower and broadcast and links back to it, and whose diagram marks the task. |
| Signals S2, 1440×900 and 390×844 | `examples/signals/02-signal-boundaries-recall.json` under its own family and names. Instance detail lists both armed boundaries on **Prepare the shipment** (interrupting and non-interrupting). A page throw of the price update ends `Boundary added a branch`: the shipment task stays active and in My work beside **Update the invoice**, and both subscriptions stay active. A page throw of the recall ends `Boundary interrupted its host`: the shipment task leaves My work, **Stop the shipment** opens with `recallReason`, the invoice branch stays, no wait remains, and history names the signal. |
| Signals S3, 1440×900 | A text `version` fails the number mapping: the broadcast finishes with issues, the row says the payload failed a mapping with the engine's description, and the instance still waits. Version 2 maps `version` to a new string variable; the preview has no blocker and warns `signal_mapping_contract_changed`. After the version change, **Retry 1 failed** delivers it, the instance holds `policyLabel` `seven`, and the broadcast completes. |
| Signals S4, 1440×900 | With the Worker stopped, 400 waits receive a broadcast that the page cancels with a reason (400 cancelled, the reason shown); a restarted Worker delivers none of it and the waits remain. A second broadcast is interrupted by stopping the Worker after its first deliveries; a new Worker re-leases the job after the 60-second lease and finishes 400 delivered, none failed or skipped, and each instance has exactly one `signalReceived` row. |
| Signals S5, 1440×900 | A managed client with `signals.throw` and `signals.read` throws twice with one idempotency key (`202`, then `200` with the same broadcast) and a changed payload under that key returns `409`. **Recent broadcasts** has one row for the signal, thrown by the client marked as an API client, delivered to both waits. |
| Signals S6 (EVENT-04A), 1440×900 and 390×844 | `examples/signals/02-signal-boundaries-recall.json` under its own family and names, which keeps its `orderNumber` business key; two orders `C-001` and `C-002`. With **One workflow family** and **Business key** `C-001`, the status line and the review count one wait, and the page throw of the price update ends `Boundary added a branch` for `C-001` only: `C-002` has no invoice task and still lists both waits on instance detail. With **One instance** and `C-002`'s ID, the recall ends `Boundary interrupted its host` for `C-002`, the broadcast shows `Instance #… · family` with a link to the instance, and `C-001` still waits for a recall. No horizontal overflow. |

Runtime viewports are 1440×900, 1024×768 and 390×844. Administrative cases that
are not viewport theories capture the relevant state at all three sizes.
Captured states include preparation/queueing, ready/completed results, empty
filters, both multi-instance modes, timer actions, skipped/ineligible/failed
items, interrupted cancellation/recovery, denied/cleared identities, and
history/items loading and recovery. Administrative captures retain both full
pages and section viewports, after finite CSS transitions settle; full pages
are returned to the top with real wheel input before capture. Bootstrap gives
the page `scroll-behavior: smooth`, so focus changes, Blazor's navigation reset
and Playwright's own actionability scrolling animate. Chromium follows the host
for this animation, and with Windows animations off every scroll lands at once
and hides timing mistakes, so this suite launches it with
`--enable-smooth-scrolling`. The shared `PageScrolling` helper
(`Flowbit.BrowserTests/Support`) therefore
points at the sticky top bar's padding rather than hovering the page heading
(which can start such a scroll), waits until the scroll position is unchanged
for ten animation frames, and repeats the wheel until the page rests at the top;
it waits the same way before each section-viewport capture. Instance-detail
section navigation waits for the scroll to end before checking where the section
landed, and presses the tab again, up to three times, if a panel that finished
loading moved the section out of view.

The UI-14 scenarios retain `before-durable-arrival`, `mi-fanout-history`,
`waiting-version-history`, `mi-outcome-once`, `held-summary-discarded`,
`ordinary-reviewer-history-without-execution-links`, and
`durable-boundary-history` screenshots, plus real summary/request evidence.
Paging also retains `next-loading`, `next-boundary`, `previous-loading`,
`previous-boundary`, and `revoked-after-historical-response`; reactivation retains
`completed-first-run`, `reactivated-with-prior-history`, and `completed-second-run`.
Pager loading/boundary states also retain `*-details` panel and `*-details-pager`
navigation screenshots so the focused control remains readable beside long
instance audit tables.
Missing-instance and API-failure cases retain `friendly-instance-not-found`,
`local-refresh-error`, and `local-retry-restored-history` screenshots, plus panel
captures. Their API request logs record the genuine `404` or database-unavailable
responses and successful recovery; expected outage errors in API logs are part
of those scenarios and must be distinguished from unrelated diagnostics.
The expiry case retains `expired-token-unavailable` and its panel capture,
request evidence for the real `500` and subsequent `401`, and the observed token
expiry/rejection times.
An actual node-retention coverage notice cannot be produced in a bounded fresh
stack: the public retention policy requires at least one day of age, and this
harness neither changes clocks nor edits database rows. That browser case
requires an authentically aged disposable instance; it remains on the
[deployment checklist](../../../docs/deployment.md#instance-diagram-history-deployment-checks).
PostgreSQL tests verify actual retention deletion, coverage
markers, protection and rollback, and component tests verify notice rendering;
those checks do not claim browser retention verification.
[Fixture prerequisites](Fixtures/README.md) document the new instance-detail
definition; administrative variants reuse the existing smoke definition.
The expression-timer cases retain full-page `contract-waiting`,
`contract-operations`, `contract-fired`, `boundaries-waiting`,
`administrative-boundaries`, `reminder-fired`,
`deadline-fired`, `reactivation-warning`, `incident-open`, and `incident-resolved`
screenshots at both viewports, and check the last page of each case for
horizontal overflow. Their due times come from instance data supplied at start, so
each case waits about a minute of real time for its timers.

The example retry mock accepts `POST /retry` with
`X-Example-Credential`, `X-Flowbit-Job-Id`, and `X-Flowbit-Job-Attempt`.
`{"scenario":"fail-once"}` fails the first call per job; `{"scenario":"recover"}`
keeps failing until the fixture releases that specific job. Success returns
`{"ok":true}`. Tests correlate its observations with real job-attempt records
and use bounded state polling, not exact timing assertions. See the
[manual HTTP walkthrough](../../../examples/durable-jobs/README.md).

The automatic multi-instance scenarios use the same service's per-item route,
`POST /items/{group}/{index}` with `Authorization: Bearer <exampleApiToken>`. Each
scenario owns a group (`ExampleItemGroup`): by default an item answers 200 with
`{"id":"receipt-<index>","index":<index>,"amount":<index × 10>}`; a held index waits
until the test releases it (out-of-order completion, late answers after an
interruption); a status sequence answers each call of an index in turn; an oversized
index answers a body above 1 MiB. Every call records its arrival, `Idempotency-Key`,
status and answer time, and a caller that goes away is recorded as abandoned. Gates
only delay genuine answers; they never change Flowbit's requests or responses.

For the six new diagrams, inspect load, representative node/boundary selection,
save/reload, clipping, and overlaps at 1440×900 and 1024×768. Exercise incident
retry and MI role editing at 1440×900, 1024×768, and 390×844. Review screenshots
and console diagnostics; an API assertion does not replace this browser gate.
Record unavailable native-picker paths separately rather than reporting them
as verified.

A6 intentionally stops only its own Worker while a harmless HTTP service call
is held. The already-started item remains committed while the action transaction
rolls back, permitting UI cancellation with `CompletedAt` still null. After
screenshots and at least two natural polling cycles, the test releases the
service and restarts the Worker. It uses the normal lease duration and a bounded
recovery wait. The harmless external call may repeat after interruption; the
test asserts that committed workflow action evidence does not duplicate.

HTTP status routing on error boundaries uses the catalog's
[status routing](../../../examples/service-tasks/04-rest-http-status-routing.json) and
[durable retry](../../../examples/durable-jobs/04-rest-status-routing-and-retries.json)
examples against a controlled service in `ExampleAcceptanceFixture`, whose scenario
names select a status, a status sequence, a connection lost mid-body, no answer in
time, an answer after four seconds, or a first call left open. It needs the same Worker stack and retains
`status-matchers-diagram.png` and `status-matchers-hosted.png` at 1440x900,
1024x768, and 390x844:

| Scenario | Coverage |
| --- | --- |
| `HttpStatusRoutingSelectsExactThenRangeThenCatchAll` | Real `409`, `422`, `503`, a lost body after `503`, and a timeout each reach only their own boundary and end event. |
| `DurableStatusRoutingUsesTheCurrentAttempt` | A real Worker routes a match on its first attempt, and a later attempt's status decides after an unmatched `503`. |
| `DurableUnmatchedStatusRetriesThenOpensARecoverableIncident` | An unmatched status makes three attempts and opens an incident with no boundary visit; a manual retry then routes from the new response. |
| `DurableRetryFirstRetriesEveryFailureThenUsesTheFinalAttempt` | With `retryFirst` and a real Worker, a `409` is called three times and then routes; the final attempt decides between `503, 409` and `409, 503`, the second of which opens an incident; and `409, 200` takes the normal flow. |
| `DurableMatchingBoundaryBeatsAConditionOnTheFailedCallsStatus` | A conditional boundary on `statusVariable` does not fire when an error boundary matches, and the error path can still read the real status. |
| `DurableUnmatchedStatusOpensAnIncidentInsteadOfTakingACondition` | With no match and no retries left, the same condition does not fire: an incident opens and the instance stays running. |
| `DurableLateMatchingResultAfterAnInterruptingTimerIsDiscarded` | An interrupting timer wins while the call is open. The `409` that arrives four seconds later opens no incident and takes no boundary. |
| `DurableWorkerStoppedMidCallRoutesOnceAfterItRestarts` | Stopping the Worker with a call open leaves its lease to expire. The restarted Worker calls again and routes once, with no incident. |
| `UnmatchedStatusIncidentRecoversThroughOperationsAndTheDiagram` | At 1440x900 and 390x844 the unmatched incident appears on Operations, **Retry** works from the keyboard, the new `409` selects the boundary, and the read-only diagram shows the result. Retains `unmatched-status-incident.png` and `unmatched-status-recovered-diagram.png`. |
| `ArchivedSharedKeyConflictRecoversThroughOperations` | At 1440x900 and 390x844 a shared key is retired: a new default without it, the old version unpublished, then the archive. A completed instance reactivated afterwards writes that key through a real Worker, which opens a `job_conflict` incident rather than stranding it. **Operations** shows the incident, and after the key is reactivated **Retry** from the keyboard finishes the instance with the value written. Retains `archived-key-incident.png`. |
| `StatusMatchersInTheHostedEditorAndReadOnlyDiagram` | The read-only diagram shows each matcher label and no authoring control. The hosted editor edits a matcher with live validation at each viewport and saves a version the API returns in canonical order. |

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
