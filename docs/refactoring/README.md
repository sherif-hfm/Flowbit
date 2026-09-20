# Staged refactoring plans

[Documentation home](../index.md)

**Status: In progress.** Stages 1–4 are implemented; stage 5 is in progress
(extraction and tests complete; browser acceptance pending); stage 6 is
implemented; stage 7 is in progress (the automated Chromium smoke suite and its
CI job are implemented and passing locally; Stage 5's residual manual
acceptance and the remote CI observation remain open); stage 8 is implemented;
stage 9 is implemented. Each stage records its
own implementation and acceptance status below. The architecture and API guides
describe the current application.

## Stages

| Stage | Deliverable | Dependency | Status |
| --- | --- | --- | --- |
| [1 — Instance list and search](stage-01-instance-queries.md) | A focused query service and repository port; Stage 8 supersedes the original engine forwards. | Passing test baseline. | Implemented |
| [2 — Repository query helpers](stage-02-repository-query-helpers.md) | Shared task ownership predicates and inbox visibility SQL fragments. | Stage 1 recommended; can proceed independently without conflicting edits. | Implemented |
| [3 — Engine projections](stage-03-engine-responsibilities.md) | Instance detail and execution projection service, including existing redaction and audit mapping. | Stage 1; Stage 2 recommended. | Implemented |
| [4 — Editor validation](stage-04-editor-validation.md) | Smaller validation phases and rule helpers inside the standalone HTML file. | Passing test baseline; independent of backend stages. | Implemented |
| [5 — Instance detail components](stage-05-instance-detail-components.md) | Display components with refresh, identity, and mutation coordination retained by the page. | Passing test baseline; independent of stages 1–4. | In progress |
| [6 — Definition validation](stage-06-definition-validation.md) | Separate authored/normalized validation from definition lifecycle and publication orchestration. | Passing test baseline; independent of stages 1–5. | Implemented |
| [7 — Browser smoke suite and Stage 5 acceptance](stage-07-browser-smoke-and-stage-05-acceptance.md) | Isolated Chromium smoke coverage, a separate CI job, and completion of Stage 5's browser gate. | Passing baseline; extracted Stage 5 components available for verification. | In progress (suite implemented and passing; residual Stage 5 acceptance open) |
| [8 — Remove query/detail compatibility methods](stage-08-remove-query-detail-compatibility.md) | Direct detail endpoint projection calls, removal of three engine forwards and the query dependency, and C# caller/test migration (40 → 37 methods). | Stages 1 and 3; passing baseline. Can proceed alongside Stage 7. | Implemented |
| [9 — Waiting-task role management](stage-09-waiting-task-role-management.md) | Scoped role-management service, four direct endpoint consumers, and atomic policy/audit behavior preserved (37 → 33 engine methods). | Accepted Stage 8; passing baseline. Can proceed alongside Stage 7. | Implemented |

The original recommended sequence is stages 1 through 5, followed by Stage 6.
Stage 6 may move earlier if definition maintenance is the immediate priority.
Each stage has its own acceptance gate and
can be reviewed, merged, and released separately. Stages 4 and 5 may run alongside
backend work when ownership of files and test resources is coordinated. Stage 3
reuses Stage 1's mapping helpers; Stage 2 does not change its required interfaces.

## Starting point

The plans were prepared against commit `c654024` (`Isolate test hosts from machine
configuration`). The last observed full-suite run before writing these plans
passed **1,835 tests, with no failures or skips**. That is historical evidence,
not a result for a future implementation. Confirm the checkout and run a fresh
baseline before beginning each stage.

The existing test-host isolation and
[CI workflow](../../.github/workflows/tests.yml) provide the starting point.
The remote CI workflow had not been observed running when these plans were
prepared. Use the [runtime test instructions](../../Flowbit/README.md) for
environment requirements. A test count is not a coverage measure.

## Shared implementation rules

- Preserve HTTP routes, DTOs, JSON formats, error text and status codes,
  authorization, query membership, ordering, pagination, and audit attribution.
- Preserve transaction boundaries, the established database lock order, scoped
  repository identity, settings freshness, and immutable definition handling.
- Make one responsibility change at a time. Keep performance improvements,
  behavior fixes, dependency upgrades, and additional extractions in separate
  changes unless a stage explicitly requires them.
- No stage requires a database migration, data rewrite, or new runtime
  dependency. If implementation reveals such a requirement, revise the plan
  before expanding its scope.
- Favor existing tests and add characterization coverage for concrete uncovered
  risks. Do not measure success by a target line count or number of new classes.

## Gate for every stage

Run these commands from the repository root; they work in PowerShell and Bash:

```text
docker info
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
git diff --check
```

Run the stage's focused tests during implementation and the complete suite for
its final candidate. Docker must be reachable for PostgreSQL tests. Report
environment failures and unexecuted checks explicitly. Record actual passed,
failed, and skipped counts; a historical pass does not satisfy the gate.

For stages 4 and 5, also complete the specified localhost browser checks using
real input, inspect layout at the listed viewport sizes, and inspect the browser
console. Jint and component rendering tests support those checks but do not
replace them. Follow the
[browser verification requirements](../../AGENTS.md#mandatory-real-browser-ui-verification).

Review documentation against the final implementation using the
[ownership map](../../AGENTS.md#mandatory-documentation-maintenance). Update
architecture explanations where ownership moves. Update public behavior guides
only where their existing explanations become inaccurate. Validate changed
relative links, anchors, examples, and image references. Report documentation
impact with the implementation result.

## Delivery and rollback

Keep commits buildable and reviewable. Each implementation report should record:

- scope completed and any explicitly deferred work;
- tests and results, plus browser evidence for UI stages;
- affected documentation and link validation;
- remaining limitations and the implementation commit(s).

Change a stage's status to **Implemented** only after its acceptance checks pass.
Update this index and the stage document together. If a required check cannot
run, record that limitation and keep the acceptance gate open.

Rollback is a code revert and normal application redeployment; no stored data
conversion is planned. Revert dependent stages in reverse order if necessary.
Retain useful characterization tests where they also apply to the previous
implementation.

## Work deliberately left for later planning

These stages do not fully decompose the engine. Inbox capabilities and mutation
authorization, message authentication, script/service execution, routing, and
gateway coordination require separate boundary analysis. Broad interface
removal, a generic SQL query framework, an editor module/build-system migration,
and a page-wide UI state redesign are also outside these plans.

The [gap inventory](gaps.md) records these deferred areas, plus additional
unplanned targets and current measurements, as the starting point for future
stage planning.

The [gap review decisions](gaps.md#decisions-for-the-stage-plans) explain why
definition validation received Stage 6, why Stage 2 remains bounded, and which
engine, editor, UI, and browser-test candidates were identified for follow-up
planning.
The original stages include the relevant parser conformance and UI lifecycle
regressions; they did not require a new browser automation harness as an
additional prerequisite. Stage 7 now plans that harness separately.

## Prioritized follow-up roadmap

The [remaining-gaps implementation plan](remaining-gaps-implementation-plan.md)
records the review at `fbe0721` and planned stages 7–11: browser smoke coverage,
removal of seven migrated engine-interface methods, waiting-task role-management
extraction, editor node-type transitions, and administrative-action display
components. It includes an explicitly accepted C# compatibility break and keeps
the other candidates deferred. Stage 7 is in progress (smoke suite implemented
locally; residual Stage 5 acceptance and remote CI observation remain open).
Stage 8 is implemented and locally accepted; Stage 9 is implemented and
locally accepted (37 → 33 engine methods). Stages 10–11 remain planned. The
other stage statuses above remain unchanged.

The [detailed Stage 7 plan](stage-07-browser-smoke-and-stage-05-acceptance.md)
defines the isolated stack, fixtures, editor/runtime smoke matrix, CI artifacts,
commands, and the complete Stage 5 acceptance checklist. Its no-Worker smoke
suite and separate full-stack browser acceptance have distinct completion gates.

The [detailed Stage 8 plan](stage-08-remove-query-detail-compatibility.md)
defines the accepted C# interface/constructor break, the detail endpoint's
unchanged identity and HTTP contract, caller and test-host migration, retained
command projections, validation, documentation updates, and rollback. The three
compatibility methods are removed (40 → 37 interface methods). The 2026-09-20
working tree is based on `6a14b1f` and remains uncommitted; baseline 1,892/1,892,
focused 102/102, final solution 1,900/1,900, and standalone Chromium 20/20 passed
with zero failures/skips. Documentation references and `git diff --check` passed.

The [detailed Stage 9 plan](stage-09-waiting-task-role-management.md) defines
the waiting-task role-management service boundary, helper ownership, four
endpoint migrations, inherited lock hierarchy, internal-save rollback tests,
scope and audit checks, C# migration, and acceptance commands. It is
implemented and locally accepted: the engine exposes 33 methods, and the four
role handlers inject `IUserTaskRoleManagementService` directly. The 2026-09-20
working tree is based on `67945f7` and remains uncommitted; baseline 1,899/1,900
(HTTP timeout flake passed on retry), focused 129/129, final solution
1,930/1,930, and standalone Chromium 20/20 passed with zero failures/skips.
The [review follow-up](stage-09-waiting-task-role-management.md#review-follow-up--2026-09-20)
records the completed audit, endpoint/OpenAPI, MI rollback-cleanup, and coordinated
PostgreSQL concurrency checks.
