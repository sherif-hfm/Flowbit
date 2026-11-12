# TASK-05: Multi-instance service, script, and call activities

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | In progress |
| Category | New task type |
| Effort | L |
| Priority | Not ranked |
| Depends on | [TASK-01](task-01-call-activity.md) (Done) |
| Related | [EVENT-05](event-05-error-code-boundaries.md) |

## Summary

Allow `multiInstance` on automatic activities so one node processes every item of a collection, in parallel or in sequence, and produces an ordered result collection. **The first release includes REST `serviceTask`, both NCalc and JavaScript `scriptTask`, and `callActivity`, with shared-variable access and whole-body timer/conditional boundaries.** Multi-instance calls build on the already implemented single-call foundation, [TASK-01](task-01-call-activity.md).

This parent plan coordinates four implementation subtasks, split on **2026-11-11** from the detailed TASK-05 plan. Shared contracts live in TASK-05A; activity-specific plans live in TASK-05B/C/D. Their proposed runtime behavior is not implemented. Splitting the work does not defer call activities or authorize a partial first release.

## Why it adds value

- Batch integrations without hand-built loops of gateways and counters.
- Independent retries and useful progress for each item, without presenting automatic work as human inbox tasks.
- Deterministic results even when remote operations finish out of order.

## First release acceptance criteria

The plan refines the original criteria to include persistence, recovery, compatibility, and operational visibility.

- [ ] An instance-scoped or readable shared typed array drives REST service tasks, NCalc/JavaScript script tasks, and call activities in both `parallel` and `sequential` modes. The body freezes the collection at successful initialization. An empty array completes with `[]`; invalid or oversized inputs fail before any item side effect or child-instance creation.
- [ ] Parent items read the immutable collection item, zero-based index, and body snapshot of instance inputs, plus shared inputs frozen at the item's first staging/admission and the permitted settings and clock read for that item. Item outputs target declared locals or declared `readWrite` shared aliases; direct item writes to ordinary parent-instance variables remain excluded. Called workflows write their own variables normally. The aggregate stays in input-index order; shared writes follow accepted commit order and are not implicit reductions.
- [ ] Shared collection, input, output/status, aggregate, and error targets preserve catalog binding/access/type validation, exact access plans, ordinal shared-key locking after runtime locks, and `ValueRevision` output fences. Aggregate/source/item-target aliases and keys cannot collide. Shared conflicts retain saved candidates and use publication-only recovery without repeating a successful external invocation or child workflow; recovery must make the use of frozen candidates explicit.
- [ ] Every item uses durable work: one activity job for REST/script, or one invocation with `callStart`/`callResume`/`callCancel` processing for calls. Sequential execution admits one item; parallel execution obeys a persisted per-execution concurrency limit across replicas (the authored value or `Workflow.MultiInstance.DefaultConcurrency`, capped by `Workflow.MultiInstance.MaxConcurrency`), in addition to existing Worker, queue, and call admission limits. Reaching a root's open-call limit holds call items pending rather than failing them.
- [ ] Retries preserve item/job identity and inputs, and committed successful siblings are never invoked again to recover a failed item or aggregate publication.
- [ ] REST/script error boundaries retain HTTP matching and `boundaryFirst`/`retryFirst` precedence; uncaught exhausted work opens an incident. Calls preserve their separate failure contract: operational start/output incidents wait, while a terminal child fault/cancellation takes the whole-body catch-all boundary or faults the parent if none exists. A caught failure stops the body and closes its timer/conditional subscriptions at once, then publishes index-ordered completed, failed, and cancelled rows to the result variable before the error boundary continues. A completion failure that retrying cannot change takes the error boundary or faults the instance instead of waiting in an incident.
- [ ] Each call item starts at most one child, maps frozen item/index/parent/shared inputs into that child, and maps its outputs into declared locals or parent shared targets through the core's fenced settlement. One target definition/default start is pinned per nonempty body; later admissions and retries use that pin even if the family's default changes.
- [ ] A call item's slot remains occupied until its child result settles, including waits and child incidents. Parent/body cancellation fences all unfinished invocations immediately and cancels descendants durably without locking two existing instances together.
- [ ] Whole-body timer and conditional boundaries support interrupting and non-interrupting modes for all three activity types. Conditional subscriptions attach at token entry, including an entry waiting behind another body; timers arm when that body initializes. Both cover body work, incidents, and completion, and close before an `asyncAfter` wait. Conditional predicates retain the existing persisted-instance-variable-only rule.
- [ ] Interrupting timer/conditional occurrences stop unfinished items/calls and route once without initiating an aggregate write; already accepted writes, including an aggregate publication that triggered the condition, remain committed. Non-interrupting occurrences create a separately fenced branch without stopping the body; accepted latched/spawned occurrences survive normal host completion. Error-boundary partial publication remains a distinct path. Occurrence identity, restart, repeated timers/false-to-true conditions, and completion races cannot duplicate a route or resurrect work.
- [ ] Cancellation, termination, scope interruption, stale leases, and concurrent final completions cannot publish late outputs or advance the parent twice. Two tokens at one node run their bodies one after another. This per-instance/node rule does not lock a shared aggregate for its entire lifetime; another instance's write causes a publication conflict instead of a silent overwrite.
- [ ] Fan-out obeys `Workflow.MultiInstance.MaxInstances`; snapshots, local outputs, aggregate results, and job admission are also bounded, and a result that cannot fit is refused at entry, before any item runs.
- [ ] Instance detail, node activity, and Operations expose automatic-item progress/results and recovery with existing authorization boundaries. Automatic parent items never appear in human inboxes or role/claim/assignment operations; called workflows retain their own normal human tasks.
- [ ] Existing human multi-instance documents, results, actions, and runtime behavior remain compatible, and administrative actions and other human MI surfaces never treat automatic bodies as human work. Open automatic executions block version switches, and the existing durable-node-contract blocker for target versions that add or remove MI on a node with open work is pinned by tests. Diagram history counts automatic bodies.
- [ ] Migration, documentation, canonical examples, automated regressions, and real-browser checks pass before marking this item Done.

## Later releases

- The `cardinality` source, quorum/early-completion conditions, and selectable or manual parent interrupt flows for automatic bodies.
- Streaming collections; direct per-item commits to ordinary parent-instance variables; implicit reductions over previous items' outputs. Shared-variable sources, reads, writes, and aggregate/error targets are included in the first release under TASK-05A's contract.
- Per-item boundaries, compensation, and selective item cancellation/replacement. Whole-body timer/conditional boundaries are included in the first release; their predicates do not introduce shared-variable change subscriptions. Until item skipping exists, a permanently failing body without an applicable error boundary or interrupting timer/conditional boundary can remain blocked until cancellation.
- Live changes to a running body's concurrency or input snapshot, and in-place version switching of an open automatic body.
- Additional call version bindings, item-dependent workflow targets, replacing a terminal failed child on retry, and per-item subprocess cancellation actions. Basic multi-instance calls are in the first release, not deferred.

## Current state

Verified on **2026-11-11**: automatic multi-instance execution is not implemented; ordinary call activities are implemented. [BPMN support](../bpmn-support.md#human-work-and-multi-instance-tasks) still limits MI to human tasks. TASK-05A records the human-specific storage, loader, job ownership, and node-ledger gaps; the activity subtasks record their adapter-specific evidence. The shared contract, schema, editor scaffolding and read surfaces are now being implemented behind a closed runtime release boundary.

## Subtasks

All four subtasks are required for the first release. Their detailed implementation steps, acceptance criteria, tests, and documentation responsibilities belong in their own files.

| ID | Plan and owned scope | Effort | Depends on | Status |
| --- | --- | --- | --- | --- |
| TASK-05A | [Shared core](task-05a-multi-instance-core.md): JSON/local and shared outputs, snapshots, persistence, concurrency, coordinator, aggregate publication, body boundaries, item APIs and shared UI. | L | Existing runtime | In progress |
| TASK-05B | [Service activities](task-05b-multi-instance-service-activities.md): REST staging, local/shared mappings and status, idempotency, retries and body-boundary integration. | M | TASK-05A | Planned |
| TASK-05C | [Script activities](task-05c-multi-instance-script-activities.md): NCalc/JavaScript local/shared evaluation, sandbox, context, body-boundary integration, and script authoring. | M | TASK-05A | Planned |
| TASK-05D | [Call activities](task-05d-multi-instance-call-activities.md): version pinning, child invocation/local/shared mappings, message ownership, call failure/body boundaries/cancellation and child navigation. | L | TASK-05A; [TASK-01](task-01-call-activity.md) (Done) | Planned |

TASK-05A is the single owner of common schemas, settings/defaults, coordinator transitions, query contracts, and shared components. B/C/D consume those contracts and own activity execution details. TASK-05D supplies the call-specific schema/FK/retention requirements to A’s migration sequence; it does not introduce another item store or coordinator. This parent owns release-wide scope, integration acceptance, and rollout.

## Builds on

The plans reuse human MI bulk persistence patterns, durable jobs/leases/incidents, ordered variable mutation, instance/gateway/token locking, shared editor assets, and the shipped [call activity foundation](task-01-call-activity.md). They preserve existing human MI and ordinary activity behavior. See each subtask for exact source owners and reuse boundaries.

## Open questions

The original item/index, local-output, and concurrency questions are resolved in the [core contract](task-05a-multi-instance-core.md): `mi.item`, zero-based `mi.index`, and explicit `outputVariables`. The [call plan](task-05d-multi-instance-call-activities.md) owns one target-version pin per body and the call failure semantics. Update the owning plan and affected acceptance criteria before changing an agreed contract.

A plan review on **2026-11-11** settled these decisions, now reflected in the owning plans:

- **Partial results on a caught failure.** When an error boundary catches an item failure, the core stops unfinished work and publishes index-ordered `completed`, `failed`, and `cancelled` rows before routing the boundary. This differs from timer/conditional interruption, which initiates no aggregate write. Item skipping remains deferred.
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

The user expanded the first-release scope on **2026-11-11** to include shared variables and timer/conditional boundaries. These decisions supersede their earlier deferral and are specified in TASK-05A:

- **Shared access.** Shared typed collections freeze at body initialization; other item shared reads freeze at first staging/admission and survive retries. Outputs may target locals or declared `readWrite` shared aliases; aggregate/error targets may also be shared. Preserve catalog authorization/contracts, value-version fences, and runtime-before-shared lock order. Never hold shared locks during HTTP, script evaluation, or child waits.
- **Conflict recovery.** Persist item candidates separately from transient jobs. A shared-output conflict retries publication with authorized fence refresh, without repeating successful HTTP/script/child work or clearing its frozen inputs. A frozen read-modify-write candidate is not recalculated or merged automatically; recovery must disclose that accepting it can replace a newer shared value. Sequential execution may observe prior committed shared writes; parallel writes do not promise source-index order.
- **Body boundaries.** Support both `cancelActivity` modes for timers and conditionals, once per parent activation rather than per item. Conditions attach at entry and use existing instance-only dependencies; timers arm at successful body initialization. Interruptions fence unfinished work and route once without starting aggregate publication; reminders create sibling branches whose accepted latched/spawned occurrences survive normal host completion. An error capture closes subscriptions before its partial-result publication. Durable occurrences, output notifications, incidents, cancellation, and completion must share the same ownership checks.
- **Compatibility.** Ordinary human MI and ordinary calls keep their documented contracts. Expanded call mapping and boundary eligibility are specific to automatic MI; this scope change does not claim implementation or runtime verification.

## Plan

### 1. Establish the shared contract

Complete TASK-05A’s model, persistence-record, local/shared result, item-identity, capacity-reservation, shared-value fences, boundary-subscription/occurrence ownership, cancellation, and adapter responsibilities first. Review TASK-05D’s intrinsic entry, target pin, invocation correlations, shared mapping snapshots, and cross-instance lock restrictions before finalizing schema changes. Preserve the different durability policies: service/script require `asyncBefore`, while calls reject authored async/job options.

The common local/shared-output, schema, admission, result, and body-boundary contract must have one definition in TASK-05A. Activity JSON examples can repeat that shape to be readable, but cannot redefine its defaults or limits. Errors and recovery remain activity-specific: REST/script retries are not a retry policy for child workflow instances. Extend shared access plans and synchronous lock-order proofs for entry, item settlement, aggregate/error publication, and emitted boundary paths before admitting the new definitions.

### 2. Implement the foundation and adapters

| Order | Work | Exit gate |
| --- | --- | --- |
| 1 | TASK-05A contract, repository ports, schema/retention design and coordinator skeleton, reviewed with B/C/D, plus the release switch and the validator, migrator, and editor scaffolding for all three activity types. | All adapters agree on common inputs/results/identity, migration ownership and integration hooks; the switch keeps automatic MI rejected outside Development hosts. |
| 2 | Core persistence, admission, local/shared settlement, aggregate completion, body boundaries and cancellation; start B/C/D against the established interfaces. | Durable item/occurrence ownership and lifecycle checks pass; capacity has a durable wake-up; shared/aggregate conflict recovery cannot reinvoke completed work. |
| 3 | B/C/D implement activity execution, shared access, boundary hooks, type-specific authoring, recovery, and scenarios. | Both modes, shared contracts, and interrupting/non-interrupting body boundaries work for REST, NCalc, JavaScript and calls; each subtask’s checklist and targeted tests pass. |
| 4 | Assemble shared API/UI and run combined migration/security/Worker/browser regressions. | All parent acceptance criteria and validation gates below pass, including call lifecycle and ordinary human/call regressions. |
| 5 | Finish owning documentation/examples and rehearse the coordinated rollout. | Evidence recorded, statuses synchronized, remaining later-release scope explicitly tracked. |

Dependencies express implementation order. B/C/D may proceed in parallel once A’s required contracts and foundation are available; they need not wait for every shared presentation/test work item to close. Coordinate edits to `WorkflowModel`, the validator/migrator, editor, job dispatcher, EF model snapshot, and shared documentation. B, C, and D all touch `WorkflowEngineService.Jobs.cs`, `WorkflowDefinitionValidator.cs`, and `flowbit-editor.js`; with A's scaffolding in place they add adapter rules to existing seams instead of editing the same blocks in parallel. Merge migrations in an explicit ordered sequence rather than allowing conflicting independent schema snapshots.

### 3. Preserve the release boundary

Subtasks may be reviewed or merged separately, but partial support must not be published as the completed TASK-05 release. Keep publication/start admission closed for the new feature until all required adapters, persistence/cancellation/recovery paths, and upgraded runtimes are present. TASK-05A's release switch does this: internal code, mirrored by the editor's save validator, that rejects `multiInstance` on service, script, and call nodes and hides their new editor controls. Only a Development host can open it, so the published Development hosts of both browser suites can exercise the feature before integration, while every other environment ignores the option. The integration change that records release evidence turns it on everywhere. The existing `WorkflowDurableProcessing:PublicationEnabled` gate cannot serve, because closing it blocks every async, timer, and call definition. Implementation scaffolding and tests must not accidentally admit an incomplete activity type. The switch opens authoring only; publication and execution open per activity kind as each adapter completes, still only on a Development host, as [TASK-05A §8](task-05a-multi-instance-core.md#8-definition-validation-compatibility-and-safety-of-loading) records.

A subtask can become Done after its own scope and verification are complete; TASK-05 stays In progress until A, B, C, and D plus the parent integration/rollout gates are complete. Update a subtask’s file, this table, and its backlog row together. The parent cannot be Done with calls deferred or with only REST/script passing.

## Validation and release gates

Each activity plan owns detailed scenario tests; the core owns shared lifecycle/query/migration tests. Run these combined checks against the final integrated implementation:

| Gate | Release evidence |
| --- | --- |
| Activity matrix | REST, NCalc, JavaScript and calls in parallel/sequential modes; instance/shared collections, empty inputs, immutable collection/instance inputs, per-item shared snapshots, local/shared targets, ordered locals/results and one body continuation. |
| Shared access and recovery | Exact catalog/access/type validation; read-only target rejection; same-key alias/aggregate/source collisions; concurrent items/instances; changed versus identical-value/catalog-only revisions; frozen retry inputs; atomic local/shared settlement; saved candidate recovery with no repeated HTTP/script/child work; read-modify-write conflict disclosure; lock-order proof across completion/error/boundary paths; no shared-value leakage through list/audit projections. |
| Recovery | Activity-specific failures, several incidents, retry of failed work only, partial results on caught errors, shared aggregate conflicts without repeating successful REST calls/scripts/child workflows, deterministic aggregate failures taking the error boundary or faulting, correct call terminal-failure behavior. |
| Concurrency and cancellation | Multiple Workers/bodies, default and cap settings, full-queue reserved dispatch, root-limit back-pressure for calls, simultaneous final results, late leases/results, conditional reentrancy, scope/instance/descendant cancellation, loops and no premature terminal instance. |
| Body boundaries | All three activity types and both `cancelActivity` modes; condition true at queued entry, second token waiting behind another body, timer start at admitted initialization, repeats/false-to-true edges, durable occurrence recovery, incidents, empty bodies, interruption versus item/shared/aggregate commit, emitted sibling branches, stale callbacks, no normal double continuation, subscription closure before async-after, and no accidental shared-condition subscriptions. |
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

1. Standalone/hosted service, both script formats, and call authoring: locals, shared collection/read/write/aggregate bindings, concurrency, call mappings, timer/conditional attachments and both interruption modes, save/reopen, invalid imports, type conversions, copy/paste, undo/redo, labels and viewer markers.
2. Worker-stopped startup, live progress, deliberately out-of-order completion, ordered results, and empty collection.
3. Activity/shared-output/aggregate incidents and keyboard recovery; change a shared target concurrently and confirm publication-only retry preserves invocation counts and child identities. Inspect an error boundary's partial result and distinguish saved locals from shared write audit correlations.
4. Conditional boundaries during queued entry, and interrupting/non-interrupting timers and conditionals during running work and incident waits; timers must not start before body initialization. Reminders leave the body running, interruptions reject late REST/child results, descendants are fenced before Worker restart, and completed normal paths remove subscriptions without discarding accepted reminder occurrences. Verify that interruption starts no aggregate publication and a second token's boundary cannot cancel the first token's body.
5. Paging/loading/error/identity changes, denied detail/retry, version blockers, human MI regression, and parent-item → child → parent navigation with pinned version checks.

Capture changed controls/markers plus running, blocked, completed and cancelled screens. Final evidence must state commands/results, browser/version, actual localhost URLs, interactions, viewport sizes, console errors/warnings, screenshots and unavailable checks; inspect host logs separately. Jint, DOM stubs and unit tests do not replace real-browser verification.

## Documentation and rollout

Update current-behavior guides only when the feature is enabled. [TASK-05A](task-05a-multi-instance-core.md#13-documentation-and-completion-handoff) separates the schema, setting, and route references that ship while the release switch is closed from the authoring and behavior guides that change only when it turns on, because the AI authoring package bundles those guides. This planning split updates the parent, four subtask plans, enhancement backlog, and documentation home. Product guides continue to describe implemented behavior until the feature lands.

| Owning page | Required implementation-time update |
| --- | --- |
| [BPMN support](../bpmn-support.md) | First-release service/script/call MI, shared-variable handling, whole-body error/timer/conditional boundaries, instance-only conditional predicates, deferred cardinality/per-item boundaries, and remaining deviations. |
| [Node/property reference](../node-reference.md) | JSON/local/shared-output contracts, source types, result envelope, exact context names, async requirements, boundary attachment/lifetime/modes, limits, validation, and flow restrictions. |
| [Developer guide](../developer-guide.md) | HTTP-only end-to-end examples, collection/instance versus item-shared snapshots, per-item settings and clock, at-least-once REST effects/idempotency, call input/output/version pinning, local/shared versus ordinary instance writes, separate call failure semantics, error partial results, timer/conditional interruption/reminders, the stuck-body limit, shared/aggregate publication-only recovery and cancellation. |
| [API reference](../api-guide.md) | New read routes and DTO additions, authorization/paging/errors, execution kind, job correlations, unchanged human-only actions, administrative-action exclusion, version blockers. |
| [Flowbit.Ui guide](../ui-guide.md) | Shared binding and boundary authoring, progress/result paging, error partial results, interruption/reminder positions, admission-blocked reasons, diagram-history counts, publication-only incident recovery links, permissions, and changed screenshots/walkthroughs. |
| [Built-in engine settings](../engine-settings.md) | TASK-05A’s two concurrency settings (default and cap), their defaults, effective range with parse, fallback, and cap rules, seed migration, and effect on new versus running bodies. |
| [Deployment/operations](../deployment.md) | New setting/default, byte limits, Worker capacity interaction, ordered upgrade, shared-conflict recovery without replay, timer/conditional subscriptions and cancellation, monitoring/retention, lack of mixed-version support. |
| [Database schema](../database-schema.md) | Every added/changed table, column, FK, constraint/index, candidate/shared-value fences, boundary occurrence correlation, job/ledger ownership and retention relationship. |
| [Runtime reference](../../Flowbit/README.md) and [AGENTS.md](../../AGENTS.md) | Item/coordinator architecture, lifecycle/fences, runtime/shared lock order and proof, local/shared producer analysis, body subscriptions, cancellation, node activity and implementation invariants. |
| [Example catalog](../../examples/README.md), [MI examples](../../examples/multi-instance/README.md), and [call examples](../../examples/call-activities/README.md) | Canonical service parallel/sequential examples, NCalc/JavaScript item processing, paired MI-call parent/child examples in both modes, shared collections/read/write/aggregate conflicts, empty collection, interrupting and non-interrupting timers/conditionals, error handling and recovery; precise input/output/Worker/catalog/target/endpoint prerequisites. |
| [AI authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) and `SKILL.md` | In the change that turns the release switch on, drop the statements that a call cannot use `multiInstance` and that collection MI needs `string[]`, and summarize the automatic MI rules. The package's generated schema changes with the model, which happens earlier, in TASK-05A; update `WorkflowAuthoringPackageTests` then. |
| [Root README](../../README.md), [getting started](../getting-started.md), [documentation home](../index.md) | Update only affected capability summaries, setup/walkthroughs, screenshots, or guide links. No unrelated marketing edits. |

Validate changed relative links/anchors/images and parse JSON examples. Execute canonical examples over HTTP and include them in authoring knowledge/catalog validation where that repository mechanism already includes examples. Do not alter AI tool names, descriptions, or system instructions for this feature; the authoring guide and `SKILL.md` are guides, and they do change.

Rollout order: apply the migration and safe setting seed, upgrade all API and Worker replicas and UI/editor assets, then allow publication/start of automatic-MI definitions under the existing durable publication gate. During upgrades, quiesce new automatic work until every runtime understands the new job/wait/item/invocation shapes. Mixed legacy/upgraded processors are unsupported. Rehearse migration on a populated database with active human MI, ordinary calls, and ordinary jobs to demonstrate their preservation. Include existing pending call messages when checking compatibility of the additive migration and new filtered uniqueness rules.

Rollback to legacy binaries is not safe with open automatic bodies or published definitions using this contract. Stop admitting new bodies and drain or explicitly cancel/reconcile affected executions before reverting; retain the additive schema and evidence. Record this operational limitation rather than proposing destructive down-migrations as recovery.

Before marking Done, satisfy every first-release criterion for **service, script, and call activities**, complete all four subtask checklists, record combined validation evidence here, synchronize parent/subtask/backlog statuses, and create follow-up items for retained later-release scope. Multi-instance calls cannot be moved to a follow-up item as a substitute for completing this release.

## Implementation evidence

TASK-05A implementation is in progress behind the release boundary; see its implementation evidence. B/C/D and combined Worker/browser/migration/rollout gates remain required. No automatic runtime support is released.
