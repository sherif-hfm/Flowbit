# TASK-05: Multi-instance service, script, and call activities

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Planned |
| Category | New task type |
| Effort | L |
| Priority | Not ranked |
| Depends on | [TASK-01](task-01-call-activity.md) (Done) |
| Related | [EVENT-05](event-05-error-code-boundaries.md) |

## Summary

Allow `multiInstance` on automatic activities so one node processes every item of a collection, in parallel or in sequence, and produces an ordered result collection. **The first release includes REST `serviceTask`, both NCalc and JavaScript `scriptTask`, and `callActivity`.** Multi-instance calls build on the already implemented single-call foundation, [TASK-01](task-01-call-activity.md).

This parent plan coordinates four implementation subtasks, split on **2026-11-11** from the detailed TASK-05 plan. Shared contracts live in TASK-05A; activity-specific plans live in TASK-05B/C/D. Their proposed runtime behavior is not implemented. Splitting the work does not defer call activities or authorize a partial first release.

## Why it adds value

- Batch integrations without hand-built loops of gateways and counters.
- Independent retries and useful progress for each item, without presenting automatic work as human inbox tasks.
- Deterministic results even when remote operations finish out of order.

## First release acceptance criteria

The plan refines the original criteria to include persistence, recovery, compatibility, and operational visibility.

- [ ] An instance-scoped typed array drives REST service tasks, NCalc/JavaScript script tasks, and call activities in both `parallel` and `sequential` modes. An empty array completes with `[]`; invalid or oversized inputs fail before any item side effect or child-instance creation.
- [ ] Parent items read the immutable collection item, zero-based index, and entry input snapshot, plus the settings and clock read when each item first runs, and write only explicitly declared local outputs. Called workflows write their own variables normally. Parallel completion order cannot affect the aggregate order or parent process-variable values.
- [ ] Every item uses durable work: one activity job for REST/script, or one invocation with `callStart`/`callResume`/`callCancel` processing for calls. Sequential execution admits one item; parallel execution obeys a persisted per-execution concurrency limit across replicas (the authored value or `Workflow.MultiInstance.DefaultConcurrency`, capped by `Workflow.MultiInstance.MaxConcurrency`), in addition to existing Worker, queue, and call admission limits. Reaching a root's open-call limit holds call items pending rather than failing them.
- [ ] Retries preserve item/job identity and inputs, and committed successful siblings are never invoked again to recover a failed item or aggregate publication.
- [ ] REST/script error boundaries retain HTTP matching and `boundaryFirst`/`retryFirst` precedence; uncaught exhausted work opens an incident. Calls preserve their separate failure contract: operational start/output incidents wait, while a terminal child fault/cancellation takes the whole-body catch-all boundary or faults the parent if none exists. A caught failure stops the body at once, then publishes index-ordered completed, failed, and cancelled rows to the result variable before the boundary continues. A completion failure that retrying cannot change takes the boundary or faults the instance instead of waiting in an incident.
- [ ] Each call item starts at most one child, maps frozen item/index/parent inputs into that child, and maps its outputs into item-local results. One target definition/default start is pinned per nonempty body; later admissions and retries use that pin even if the family's default changes.
- [ ] A call item's slot remains occupied until its child result settles, including waits and child incidents. Parent/body cancellation fences all unfinished invocations immediately and cancels descendants durably without locking two existing instances together.
- [ ] Cancellation, termination, scope interruption, stale leases, and concurrent final completions cannot publish late outputs or advance the parent twice. Two tokens at one node run their bodies one after another, so neither overwrites the other's result.
- [ ] Fan-out obeys `Workflow.MultiInstance.MaxInstances`; snapshots, local outputs, aggregate results, and job admission are also bounded, and a result that cannot fit is refused at entry, before any item runs.
- [ ] Instance detail, node activity, and Operations expose automatic-item progress/results and recovery with existing authorization boundaries. Automatic parent items never appear in human inboxes or role/claim/assignment operations; called workflows retain their own normal human tasks.
- [ ] Existing human multi-instance documents, results, actions, and runtime behavior remain compatible, and administrative actions and other human MI surfaces never treat automatic bodies as human work. Open automatic executions block version switches, and the existing durable-node-contract blocker for target versions that add or remove MI on a node with open work is pinned by tests. Diagram history counts automatic bodies.
- [ ] Migration, documentation, canonical examples, automated regressions, and real-browser checks pass before marking this item Done.

## Later releases

- The `cardinality` source, quorum/early-completion conditions, and selectable or manual parent interrupt flows for automatic bodies.
- Shared-variable collection sources, item-local reads/writes, and aggregate targets; streaming collections; per-item commits to parent process variables; reductions over previous items' outputs. A called child workflow keeps its own existing shared-variable capabilities.
- Timer/conditional boundaries attached to an automatic multi-instance body, per-item boundaries, compensation, and selective item cancellation/replacement. Until item skipping exists, a body whose item fails on every attempt without a boundary can only be cancelled; this is a documented first-release limit.
- Live changes to a running body's concurrency or input snapshot, and in-place version switching of an open automatic body.
- Additional call version bindings, item-dependent workflow targets, replacing a terminal failed child on retry, and per-item subprocess cancellation actions. Basic multi-instance calls are in the first release, not deferred.

## Current state

Verified on **2026-11-11**: automatic multi-instance execution is not implemented; ordinary call activities are implemented. [BPMN support](../bpmn-support.md#human-work-and-multi-instance-tasks) still limits MI to human tasks. TASK-05A records the human-specific storage, loader, job ownership, and node-ledger gaps; the activity subtasks record their adapter-specific evidence. This is documentation-only planning work.

## Subtasks

All four subtasks are required for the first release. Their detailed implementation steps, acceptance criteria, tests, and documentation responsibilities belong in their own files.

| ID | Plan and owned scope | Effort | Depends on | Status |
| --- | --- | --- | --- | --- |
| TASK-05A | [Shared core](task-05a-multi-instance-core.md): JSON/local outputs, snapshots, persistence, concurrency, coordinator, aggregate publication, item APIs and shared UI. | L | Existing runtime | Planned |
| TASK-05B | [Service activities](task-05b-multi-instance-service-activities.md): REST staging, local mappings/status, idempotency, retries and error boundaries. | M | TASK-05A | Planned |
| TASK-05C | [Script activities](task-05c-multi-instance-script-activities.md): NCalc/JavaScript local evaluation, sandbox, context, and script authoring. | M | TASK-05A | Planned |
| TASK-05D | [Call activities](task-05d-multi-instance-call-activities.md): version pinning, child invocation/mappings, message ownership, call failure/cancellation and child navigation. | L | TASK-05A; [TASK-01](task-01-call-activity.md) (Done) | Planned |

TASK-05A is the single owner of common schemas, settings/defaults, coordinator transitions, query contracts, and shared components. B/C/D consume those contracts and own activity execution details. TASK-05D supplies the call-specific schema/FK/retention requirements to A’s migration sequence; it does not introduce another item store or coordinator. This parent owns release-wide scope, integration acceptance, and rollout.

## Builds on

The plans reuse human MI bulk persistence patterns, durable jobs/leases/incidents, ordered variable mutation, instance/gateway/token locking, shared editor assets, and the shipped [call activity foundation](task-01-call-activity.md). They preserve existing human MI and ordinary activity behavior. See each subtask for exact source owners and reuse boundaries.

## Open questions

The original item/index, local-output, and concurrency questions are resolved in the [core contract](task-05a-multi-instance-core.md): `mi.item`, zero-based `mi.index`, and explicit `outputVariables`. The [call plan](task-05d-multi-instance-call-activities.md) owns one target-version pin per body and the call failure semantics. Update the owning plan and affected acceptance criteria before changing an agreed contract.

A plan review on **2026-11-11** settled these decisions, now reflected in the owning plans:

- **Partial results on a caught failure.** When an error boundary catches an item failure, the core first publishes index-ordered `completed`, `failed`, and `cancelled` rows to the result variable. A body stuck in an incident without a boundary stays cancel-only until item skipping exists.
- **Separate concurrency default and cap.** `Workflow.MultiInstance.DefaultConcurrency` (4) applies when `maxConcurrency` is omitted; `Workflow.MultiInstance.MaxConcurrency` (1,000) caps every value. Progress shows the effective value.
- **Settings and clock per item.** Instance inputs and the item freeze at body start; `config.*`/`setting.*` values and `sys.now`/`sys.today` are read when each item first runs and kept for its retries.
- **Call capacity is back-pressure.** An MI call item refused at the root's open-call limit stays pending and the dispatch job retries it; a body with nothing in flight opens a visible incident.
- **Explicit `afterAll`.** Automatic bodies must write `completionEvaluation: "afterAll"`, because the model and the editor cannot tell an omitted value from `afterEach`.
- **No unrecoverable completion incidents.** Aggregate validation and size failures take the boundary or fault the instance, and result size is checked at entry, before any item runs.
- **Gaps closed.** Administrative actions and every other `multi_instance_executions` reader exclude automatic bodies; version switches also block target-side MI changes; diagram history counts automatic bodies; a code-level release switch keeps the feature closed; each job kind has a defined queue class and retry policy; the AI authoring package is in the documentation plan.

A follow-up review against the code, also on **2026-11-11**, corrected three current-state claims and closed further gaps, now reflected in TASK-05A and TASK-05D:

- **Corrections.** Version compatibility already blocks a target that adds, removes, or changes MI on a node with an open job, because the durable node contract includes `multiInstance`; tests now pin that instead of a new check. The reader audit covers every file that references MI executions by table, entity, or `MultiInstanceExecutionId`, which is more than fifty files, not ten. The publication gate is `WorkflowDurableProcessing:PublicationEnabled`.
- **Release switch.** It opens only on Development hosts, where both browser suites run, so subtask browser gates can pass before integration. While it is closed, the editor hides the new controls.
- **Caught failures.** One transaction stops the body; the completion job then publishes the partial result and routes the boundary, so a publication conflict never leaves siblings running.
- **One body per node.** A second token waits at entry until the node's open body settles, instead of resetting the first body's result.
- **Capacity.** The reservation counts each unsettled body in the per-instance open-job count, and dispatch opens an incident when a body has nothing in flight and its backoff is exhausted.
- **Retention and contracts.** Bodies keep their snapshot and item results past the completed-job retention period. The concurrency settings parse, fall back, and cap like other numeric engine settings. Automatic progress gets its own instance-detail collection, and failure codes and FlowInfo kinds are named. Product guides change only when the switch turns on, because the AI authoring package bundles them.

## Plan

### 1. Establish the shared contract

Complete TASK-05A’s model, persistence-record, local-result, item-identity, capacity-reservation, cancellation, and adapter responsibilities first. Review TASK-05D’s intrinsic entry, target pin, invocation correlations, and cross-instance lock restrictions before finalizing schema changes. Preserve the different durability policies: service/script require `asyncBefore`, while calls reject authored async/job options.

The common local-output/schema/admission/result contract must have one definition in TASK-05A. Activity JSON examples can repeat that shape to be readable, but cannot redefine its defaults or limits. Errors and recovery remain activity-specific: REST/script retries are not a retry policy for child workflow instances.

### 2. Implement the foundation and adapters

| Order | Work | Exit gate |
| --- | --- | --- |
| 1 | TASK-05A contract, repository ports, schema/retention design and coordinator skeleton, reviewed with B/C/D, plus the release switch and the validator, migrator, and editor scaffolding for all three activity types. | All adapters agree on common inputs/results/identity, migration ownership and integration hooks; the switch keeps automatic MI rejected outside Development hosts. |
| 2 | Core persistence, admission, aggregate completion and cancellation; start B/C/D against the established interfaces. | Durable item ownership and shared lifecycle checks pass; capacity has a durable wake-up and completed work cannot be reinvoked by aggregate retry. |
| 3 | B/C/D implement activity execution, type-specific authoring, recovery, and scenarios. | Both modes work for REST, NCalc, JavaScript and calls; each subtask’s checklist and targeted tests pass. |
| 4 | Assemble shared API/UI and run combined migration/security/Worker/browser regressions. | All parent acceptance criteria and validation gates below pass, including call lifecycle and ordinary human/call regressions. |
| 5 | Finish owning documentation/examples and rehearse the coordinated rollout. | Evidence recorded, statuses synchronized, remaining later-release scope explicitly tracked. |

Dependencies express implementation order. B/C/D may proceed in parallel once A’s required contracts and foundation are available; they need not wait for every shared presentation/test work item to close. Coordinate edits to `WorkflowModel`, the validator/migrator, editor, job dispatcher, EF model snapshot, and shared documentation. B, C, and D all touch `WorkflowEngineService.Jobs.cs`, `WorkflowDefinitionValidator.cs`, and `flowbit-editor.js`; with A's scaffolding in place they add adapter rules to existing seams instead of editing the same blocks in parallel. Merge migrations in an explicit ordered sequence rather than allowing conflicting independent schema snapshots.

### 3. Preserve the release boundary

Subtasks may be reviewed or merged separately, but partial support must not be published as the completed TASK-05 release. Keep publication/start admission closed for the new feature until all required adapters, persistence/cancellation/recovery paths, and upgraded runtimes are present. TASK-05A's release switch does this: internal code, mirrored by the editor's save validator, that rejects `multiInstance` on service, script, and call nodes and hides their new editor controls. Only a Development host can open it, so the published Development hosts of both browser suites can exercise the feature before integration, while every other environment ignores the option. The integration change that records release evidence turns it on everywhere. The existing `WorkflowDurableProcessing:PublicationEnabled` gate cannot serve, because closing it blocks every async, timer, and call definition. Implementation scaffolding and tests must not accidentally admit an incomplete activity type.

A subtask can become Done after its own scope and verification are complete; TASK-05 stays In progress until A, B, C, and D plus the parent integration/rollout gates are complete. Update a subtask’s file, this table, and its backlog row together. The parent cannot be Done with calls deferred or with only REST/script passing.

## Validation and release gates

Each activity plan owns detailed scenario tests; the core owns shared lifecycle/query/migration tests. Run these combined checks against the final integrated implementation:

| Gate | Release evidence |
| --- | --- |
| Activity matrix | REST, NCalc, JavaScript and calls in parallel/sequential modes; empty inputs, immutable snapshots, ordered locals/results and one body continuation. |
| Recovery | Activity-specific failures, several incidents, retry of failed work only, partial results on caught failures, aggregate conflicts without repeating successful REST calls/scripts/child workflows, deterministic aggregate failures taking the boundary or faulting, correct call terminal-failure behavior. |
| Concurrency and cancellation | Multiple Workers/bodies, default and cap settings, full-queue reserved dispatch, root-limit back-pressure for calls, simultaneous final results, late leases/results, conditional reentrancy, scope/instance/descendant cancellation, loops and no premature terminal instance. |
| Call lifecycle | One child per item, pin survives default change, target pin protection, waiting children hold slots, output retry preserves completed children, immediate ancestry fencing and durable descendant shutdown. |
| Compatibility/security | Existing human MI and ordinary calls preserved; administrative actions and other human MI surfaces exclude automatic bodies; API/editor parity, bounded SQL paging and authorization, version/reactivation blockers in both directions, diagram-history counts, release switch closed until integration, no fabricated parent human tasks, no secret payload exposure. |
| Migration/retention | Fresh and populated PostgreSQL, existing open human MI/calls/jobs, constraints/indexes, protected active snapshots/compact invocation identities, safe upgrade and explicit rollback limits. |
| Browser/docs | Shared and activity-specific authoring/runtime flows in actual Chromium; owning docs agree with implementation and all canonical examples run over HTTP. |

Run from the repository root; these commands work in PowerShell 7 and Bash:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --nologo --verbosity quiet
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
git diff --check
```

During implementation, run focused filters first, then the final solution gate. PostgreSQL cases require Docker; missing dependencies are unavailable checks, not passes. Follow publish/build/install instructions in the [Chromium smoke README](../../Flowbit/tests/Flowbit.BrowserTests/README.md) and [Worker acceptance README](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md), then run both suites. Until the integration step, run the automatic-MI scenarios with the release switch opened on those Development test hosts; the integration change reruns them with it on by default. Keep ordinary smoke Worker-free; durable scenarios use acceptance’s disposable database, real API/UI/Worker, controlled localhost services, and authenticated HTTP setup. Do not manufacture application state through database edits or rewritten responses.

Exercise real clicks, typing, keyboard navigation, and applicable pointer interactions at **1440×900, 1024×768, and 390×844**:

1. Standalone/hosted service, both script formats, and call authoring: locals, concurrency, call mappings, save/reopen, invalid imports, type conversions, copy/paste, undo/redo, labels and viewer markers.
2. Worker-stopped startup, live progress, deliberately out-of-order completion, ordered results, and empty collection.
3. Activity/aggregate incidents and keyboard recovery; confirm successful sibling invocation counts/child identities do not change. Inspect a boundary's partial result.
4. Cancellation with an in-flight REST response or child result, boundary behavior, descendant actions fenced before Worker restart, then physical cancellation.
5. Paging/loading/error/identity changes, denied detail/retry, version blockers, human MI regression, and parent-item → child → parent navigation with pinned version checks.

Capture changed controls/markers plus running, blocked, completed and cancelled screens. Final evidence must state commands/results, browser/version, actual localhost URLs, interactions, viewport sizes, console errors/warnings, screenshots and unavailable checks; inspect host logs separately. Jint, DOM stubs and unit tests do not replace real-browser verification.

## Documentation and rollout

Update current-behavior guides only when the feature is enabled. [TASK-05A](task-05a-multi-instance-core.md#13-documentation-and-completion-handoff) separates the schema, setting, and route references that ship while the release switch is closed from the authoring and behavior guides that change only when it turns on, because the AI authoring package bundles those guides. This planning split updates the parent, four subtask plans, enhancement backlog, and documentation home. Product guides continue to describe implemented behavior until the feature lands.

| Owning page | Required implementation-time update |
| --- | --- |
| [BPMN support](../bpmn-support.md) | First-release service/script/call MI, whole-body errors, deferred cardinality/shared/boundary behavior, and remaining deviations. |
| [Node/property reference](../node-reference.md) | JSON/local-output contracts, source types, result envelope, exact context names, async requirements, limits, validation, and flow restrictions. |
| [Developer guide](../developer-guide.md) | HTTP-only end-to-end examples, frozen inputs and per-item settings and clock, at-least-once REST effects/idempotency, call input/output/version pinning, local versus process writes, separate call failure semantics, partial results, the stuck-body limit, aggregate conflicts and cancellation. |
| [API reference](../api-guide.md) | New read routes and DTO additions, authorization/paging/errors, execution kind, job correlations, unchanged human-only actions, administrative-action exclusion, version blockers. |
| [Flowbit.Ui guide](../ui-guide.md) | Authoring, progress/result paging, partial results, admission-blocked reasons, diagram-history counts, incident recovery links, permissions, and changed screenshots/walkthroughs. |
| [Built-in engine settings](../engine-settings.md) | TASK-05A’s two concurrency settings (default and cap), their defaults, effective range with parse, fallback, and cap rules, seed migration, and effect on new versus running bodies. |
| [Deployment/operations](../deployment.md) | New setting/default, byte limits, Worker capacity interaction, ordered upgrade, monitoring/recovery/retention, lack of mixed-version support. |
| [Database schema](../database-schema.md) | Every added/changed table, column, FK, constraint/index, job/ledger ownership and retention relationship. |
| [Runtime reference](../../Flowbit/README.md) and [AGENTS.md](../../AGENTS.md) | Item/coordinator architecture, lifecycle/fences, lock order, local producer analysis, cancellation, node activity and implementation invariants. |
| [Example catalog](../../examples/README.md), [MI examples](../../examples/multi-instance/README.md), and [call examples](../../examples/call-activities/README.md) | Canonical service parallel/sequential examples, NCalc/JavaScript item processing, paired MI-call parent/child examples in both modes, empty collection, boundary handling and recovery; precise input/output/Worker/target/endpoint prerequisites. |
| [AI authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) and `SKILL.md` | In the change that turns the release switch on, drop the statements that a call cannot use `multiInstance` and that collection MI needs `string[]`, and summarize the automatic MI rules. The package's generated schema changes with the model, which happens earlier, in TASK-05A; update `WorkflowAuthoringPackageTests` then. |
| [Root README](../../README.md), [getting started](../getting-started.md), [documentation home](../index.md) | Update only affected capability summaries, setup/walkthroughs, screenshots, or guide links. No unrelated marketing edits. |

Validate changed relative links/anchors/images and parse JSON examples. Execute canonical examples over HTTP and include them in authoring knowledge/catalog validation where that repository mechanism already includes examples. Do not alter AI tool names, descriptions, or system instructions for this feature; the authoring guide and `SKILL.md` are guides, and they do change.

Rollout order: apply the migration and safe setting seed, upgrade all API and Worker replicas and UI/editor assets, then allow publication/start of automatic-MI definitions under the existing durable publication gate. During upgrades, quiesce new automatic work until every runtime understands the new job/wait/item/invocation shapes. Mixed legacy/upgraded processors are unsupported. Rehearse migration on a populated database with active human MI, ordinary calls, and ordinary jobs to demonstrate their preservation. Include existing pending call messages when checking compatibility of the additive migration and new filtered uniqueness rules.

Rollback to legacy binaries is not safe with open automatic bodies or published definitions using this contract. Stop admitting new bodies and drain or explicitly cancel/reconcile affected executions before reverting; retain the additive schema and evidence. Record this operational limitation rather than proposing destructive down-migrations as recovery.

Before marking Done, satisfy every first-release criterion for **service, script, and call activities**, complete all four subtask checklists, record combined validation evidence here, synchronize parent/subtask/backlog statuses, and create follow-up items for retained later-release scope. Multi-instance calls cannot be moved to a follow-up item as a substitute for completing this release.

## Implementation evidence

No implementation has started. This section is reserved for the integrated test/browser/migration/rollout evidence; each subtask records its own results when implemented.
