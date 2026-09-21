# Browser smoke fixtures

Minimal definitions used only by the `Flowbit.BrowserTests` suite. They are
never started against production data; every run uses a disposable PostgreSQL
database created by the fixture. Editor fixtures are loaded through the real
editor load flow; runtime fixtures are created and published through
`POST /api/workflows` by `WorkflowFixtureClient`.

## Editor fixtures

| Fixture | Used by | Notes |
| --- | --- | --- |
| `editor-basic.json` | E1, E2, E3, E5 | Valid workflow with two lanes, one start event, a claim-required user task, one automatic task, one end event, and three sequence flows. deliberately simple geometry (lanes 1/2, nodes 1-4, flows 101-103) so drags and saves have known shapes. |
| `editor-invalid.json` | E4 | Deliberately invalid and the error survives load normalization: `exclusiveGateway #3` has one incoming and one outgoing flow (neither split nor merge), so `validateModelForSave` rejects the save with a gateway topology error. |
| `editor-type-transition.json` | E6, E7, E8 | Valid workflow for the inspector Type-selector scenarios: start `#1` and message start `#9` (typed output mappings plus an idempotency variable `extra`) both feed the graph; user task `#2` has two outgoing flows (`102`, `103`) so a conversion prunes the second; exclusive gateway `#3` splits with a conditional (`amount > 100`, priority 1) and a default flow; service task `#7` hosts error boundary `#8` (incident flow `801`); nodes `#4`/`#5` convert to conditional/timer catches in E8 (the process variable `amount` satisfies the conditional dependency rule). Expected transitions: E6 exercises guarded conversions on `#2`/`#3`; E7 converts `#2` → task and `#7` → user task (boundary/flow removal) and saves the pruned model; E8 round-trips `#9` between message and ordinary start (rebuilt settings are retyped), seeds timer/conditional defaults, and saves with `initialEventId: null` after `#1` becomes a task. |

## Runtime fixtures

| Fixture | Workflow key | Used by | Shape |
| --- | --- | --- | --- |
| `runtime-lifecycle.json` | `browser-lifecycle-r1` | R1, R3 | start → claim-required `userTask` (`roles: ["Agent"]`, external id `TASK_REVIEW_REQUEST`) → end; single action flow `Approve` (flow 102) captures required string `decisionNote`. |
| `runtime-navigation.json` | `browser-navigation-r2` | R2, R6 | start → `approval1` → `approval2` → end; open role-less tasks, two steps so one instance can be driven to `Completed` over HTTP while another rests. |
| `runtime-gateway.json` | `browser-gateway-r4` | R4, R6 | start → parallel split (`Fork reviews`) → `Reviewer A` / `Reviewer B` → complex merge (`TotalIncomingCount() >= 2`, joinCancellation referencing the split) → `Finalize` → end. |
| `runtime-mi.json` | `browser-mi-r5` | R4, R5, R6 | start → parallel collection multi-instance `userTask` over `reviewers` (default `alpha`, `beta`, `gamma`) → end; action flow `Complete review` requires `reviewComment`; hidden engine fallback flow 202. |

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

## Prerequisites and boundaries

- Fixtures are synchronous: every scenario completes without the Worker
  (`WorkflowDurableProcessing:PublicationEnabled=false` on the API).
- Each scenario creates its own workflow/instance IDs; nothing is shared and no
  checked-in `examples/` file is modified. `WorkflowFixtureClient` suffixes the
  authored fixture `id` with a unique token at POST time; the catalog keys in
  this table remain the checked-in JSON values.
- The invalid editor fixture is intentionally only editor-invalid (the runtime
  engine would also reject its gateway topology, but it is never started).
