# Browser smoke fixtures

Minimal definitions used only by the `Flowbit.BrowserTests` suite. They are
never started against production data; every run uses a disposable PostgreSQL
database created by the fixture. Editor fixtures are loaded through the real
editor load flow; runtime fixtures are created and published through
`POST /api/workflows` by `WorkflowFixtureClient`.

## Editor fixtures

| Fixture | Used by | Notes |
| --- | --- | --- |
| `editor-basic.json` | E1, E2, E3, E5, E10 | Valid workflow with two lanes, one start event, a claim-required user task, one automatic task, one end event, and three sequence flows. deliberately simple geometry (lanes 1/2, nodes 1-4, flows 101-103) so drags and saves have known shapes. |
| `editor-invalid.json` | E4 | Deliberately invalid and the error survives load normalization: `exclusiveGateway #3` has one incoming and one outgoing flow (neither split nor merge), so `validateModelForSave` rejects the save with a gateway topology error. |
| `editor-form-fields.json` | E9 | Valid workflow: start `#1` declares the required string input `amount` without form fields, followed by user task `#2` and end `#3` (flows `101`, `102`). E9 opens the variable's Form section, adds a label, help text, and two options after changing its type to `number`, checks that a third option `9007199254740993` blocks the save until it is removed, and saves. |
| `editor-type-transition.json` | E6, E7, E8 | Valid workflow for the inspector Type-selector scenarios: start `#1` and message start `#9` (typed output mappings plus an idempotency variable `extra`) both feed the graph; user task `#2` has two outgoing flows (`102`, `103`) so a conversion prunes the second; exclusive gateway `#3` splits with a conditional (`amount > 100`, priority 1) and a default flow; service task `#7` hosts error boundary `#8` (incident flow `801`); nodes `#4`/`#5` convert to conditional/timer catches in E8 (the process variable `amount` satisfies the conditional dependency rule). Expected transitions: E6 exercises guarded conversions on `#2`/`#3`; E7 converts `#2` → task and `#7` → user task (boundary/flow removal) and saves the pruned model; E8 round-trips `#9` between message and ordinary start (rebuilt settings are retyped), seeds timer/conditional defaults, and saves with `initialEventId: null` after `#1` becomes a task. |

## Runtime fixtures

| Fixture | Workflow key | Used by | Shape |
| --- | --- | --- | --- |
| `runtime-lifecycle.json` | `browser-lifecycle-r1` | R1, R3 | start → claim-required `userTask` (`roles: ["Agent"]`, external id `TASK_REVIEW_REQUEST`) → end; single action flow `Approve` (flow 102) captures required string `decisionNote`. |
| `runtime-navigation.json` | `browser-navigation-r2` | R2, R6, R8 | start → `approval1` → `approval2` → end; open role-less tasks, two steps so one instance can be driven to `Completed` over HTTP while another rests. |
| `runtime-gateway.json` | `browser-gateway-r4` | R4, R6, R8 | start → parallel split (`Fork reviews`) → `Reviewer A` / `Reviewer B` → complex merge (`TotalIncomingCount() >= 2`, joinCancellation referencing the split) → `Finalize` → end. |
| `runtime-mi.json` | `browser-mi-r5` | R4, R5, R6, R8 | start → parallel collection multi-instance `userTask` over `reviewers` (default `alpha`, `beta`, `gamma`) → end; action flow `Complete review` requires `reviewComment`; hidden engine fallback flow 202. |
| `runtime-rich-forms.json` | `browser-rich-forms-r9` | R9 | start (open roles) declaring `requestTitle` (required string with label and two-line help), `amount` (required number), `urgent` (boolean), `neededBy` (date), `kickoffAt` (datetime), `category` (string options with a `software` default), and `tags` (`string[]`) → `userTask` `Review request` (`roles: ["Reviewer"]`) → end; action flow `Decide` (flow 201) declares `decision` (required string options with help and the `decision in ('approved', 'rejected')` rule), `score` (number options `1`, `2.5`, `5`), and `followUpOn` (datetime). |
| `runtime-rich-forms-mi.json` | `browser-rich-forms-mi-r9` | R9 | start → parallel collection multi-instance `userTask` `Parallel review` (`roles: ["Reviewer"]`) over `reviewers` (default `alpha`, `beta`) → end `Reviewed`; completion flow `Complete review` (201) and hidden engine fallback 202. Interrupt flow `Stop reviews` (203, `cancelRemainingInstances: true`, roles `Reviewer`) → end `Stopped` declares `stopReason` (required string options with help and the `stopReason in ('duplicate', 'withdrawn')` rule), `budgetCap` (number), and `resumeAt` (datetime). |
| `runtime-due-dates.json` | `browser-due-dates-r10` | R10 | start (optional `amount`, default `500`) → parallel split → four `Reviewer` user tasks: `Overdue review` (`dueDate` `2020-01-01T09:00:00Z`, `priority` `90`), `Due soon review` (`PT2H`, `if(amount > 100, 60, 10)`), `Upcoming review` (`P3D`, `20`), and `Unscheduled review` (neither) → parallel join → end. |
| `runtime-administrative-r7.json` | `browser-administrative-r7` | R7 | start → `userTask` `Manual review` (node 2, external id `TASK_MANUAL_REVIEW`) → end; single action flow `Approve` (flow 102) with one optional string `approvalNote`. One batch execution completes the instance synchronously and leaves a completed one-item audit batch, so R7 exercises real batch display without a Worker. |

## Actors and expected states

- `setup` (role `admin`) creates/publishes definitions and starts instances;
  the fixture mints this identity through the real `/token` screen.
- R1: `alice` (role `Agent`) claims `Review request` and completes `Approve`
  with `decisionNote = "Approved by alice"`; the instance then completes.
- R2: the setup client (`setup` / `admin`) completes both approvals over HTTP
  for the terminal instance; `worker` (roles `User` + `admin`) only navigates.
  The navigation instance rests on `approval1`. The `admin` role satisfies the
  `WorkflowInstances.RequiredRole` instance-list gate (default `admin`), which
  the navigating actor needs to read the list.
- R3: `alice` (role `Agent`) holds authorized actions; `bob` (no matching role)
  must not see them after identity replacement.
- R4: `quorum` (role `Reviewer`) opens the gateway detail; `branch-driver`
  (role `Reviewer`) completes both branch tasks over HTTP; `beta` and `gamma`
  (role `Reviewer`) complete two multi-instance children over HTTP.
- R5: `alpha` (role `Reviewer`) views instance detail; `beta` (role
  `Reviewer`) completes its own collection item over HTTP, which the open
  detail page shows via five-second polling.
- R6: `worker` (roles `User` + `admin`) navigates the list and detail pages at
  the narrower viewports; `beta` (role `Reviewer`) submits one MI result first so the narrow table includes real JSON.
- R7: `supervisor` (role `admin`) executes immediate instance administrative
  actions through the instance-detail panel and inspects the resulting audit
  batches on `/administrative-actions`.
- R8: `beta` (role `Reviewer`) completes multi-instance item 1 over HTTP, and
  the setup client completes the navigation instance. `operator` (roles
  `Reviewer` + `admin`; `admin` grants node-execution read access) and then
  `reviewer` (role `Reviewer` only) open the diagrams. The gateway instance
  rests on `Reviewer A` and `Reviewer B`, the multi-instance task shows
  `1/3`, and the navigation instance has ended at `End`.
- R9: `requester` (roles `admin` + `Reviewer`; `admin` lets the start page read
  the definition) starts the rich-forms instance through the UI start form, then
  takes `Decide` with `decision = "rejected"`, `score = 2.5`, and
  `followUpOn = "2026-12-01T09:00:00+09:00"`; the instance completes. The
  browser context is fixed to `Asia/Tokyo`, so date-time values carry `+09:00`.
  The second R9 case runs in `Europe/Berlin`: the same actor starts an instance
  with `kickoffAt = "2026-10-25T02:30:00+02:00"` and no `neededBy`, then takes
  `Decide` on instance detail with `decision = "approved"`, `score = 5`, and
  `followUpOn = "2026-11-02T09:00:00+01:00"`.
  The third R9 case runs in `America/New_York`. The same actor holds the node and
  flow role `Reviewer` for the interrupt and `admin` for the administrative panel.
  It starts both workflows over HTTP, takes `Stop reviews` with
  `stopReason = "withdrawn"`, `budgetCap = 2500.75`, and
  `resumeAt = "2026-11-02T09:00:00-05:00"`, then executes `Decide` on the
  rich-forms instance through the administrative panel with
  `decision = "approved"`, `score = 1`, and
  `followUpOn = "2026-10-30T16:45:00-04:00"`. Both instances complete.
- R10: the setup client starts the due-dates instance with `amount = 500`;
  `reviewer` (role `Reviewer`) reads My work and task detail. The four reviews
  stay open, and the API's real clock decides the badges.

## Prerequisites and boundaries

- Fixtures are synchronous: every scenario completes without the Worker
  (`WorkflowDurableProcessing:PublicationEnabled=false` on the API).
- Each scenario creates its own workflow/instance IDs; nothing is shared and no
  checked-in `examples/` file is modified. `WorkflowFixtureClient` suffixes the
  authored fixture `id` with a unique token at POST time; the catalog keys in
  this table remain the checked-in JSON values.
- The invalid editor fixture is intentionally only editor-invalid (the runtime
  engine would also reject its gateway topology, but it is never started).
