# Staged refactoring plans

[Documentation home](../index.md)

**Status: In progress.** Stages 1–4 are implemented; stage 5 is in progress
(extraction and tests complete, browser verification pending); stage 6 is
implemented.
Each stage records its own implementation and acceptance status below. The
architecture and API guides describe the current application.

## Stages

| Stage | Deliverable | Dependency | Status |
| --- | --- | --- | --- |
| [1 — Instance list and search](stage-01-instance-queries.md) | A focused query service and repository port, with compatible engine forwarding methods. | Passing test baseline. | Implemented |
| [2 — Repository query helpers](stage-02-repository-query-helpers.md) | Shared task ownership predicates and inbox visibility SQL fragments. | Stage 1 recommended; can proceed independently without conflicting edits. | Implemented |
| [3 — Engine projections](stage-03-engine-responsibilities.md) | Instance detail and execution projection service, including existing redaction and audit mapping. | Stage 1; Stage 2 recommended. | Implemented |
| [4 — Editor validation](stage-04-editor-validation.md) | Smaller validation phases and rule helpers inside the standalone HTML file. | Passing test baseline; independent of backend stages. | Implemented |
| [5 — Instance detail components](stage-05-instance-detail-components.md) | Display components with refresh, identity, and mutation coordination retained by the page. | Passing test baseline; independent of stages 1–4. | In progress |
| [6 — Definition validation](stage-06-definition-validation.md) | Separate authored/normalized validation from definition lifecycle and publication orchestration. | Passing test baseline; independent of stages 1–5. | Implemented |

The recommended sequence is the original stages 1 through 5, followed by Stage 6.
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
engine, editor, UI, and browser-test candidates still need separate planning.
The existing stages include the relevant parser conformance and UI lifecycle
regressions; a new browser automation harness is not an additional prerequisite.
