# TASK-05D: Multi-instance call activities

[Parent TASK-05](task-05-multi-instance-automatic-activities.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Planned |
| Category | New task type |
| Effort | L |
| Priority | Required for TASK-05 first release |
| Parent | [TASK-05](task-05-multi-instance-automatic-activities.md) |
| Depends on | [TASK-05A: shared automatic multi-instance core](task-05a-multi-instance-core.md), [TASK-01: call activity](task-01-call-activity.md) (Done) |

## Summary

Run a called workflow once for each item in an immutable collection, sequentially or within a persisted parallel window. Every item owns one durable call invocation and at most one child instance. Child results become item-local outputs or authorized parent shared-variable writes; only the shared coordinator publishes the ordered result collection and continues the parent. Shared-variable access and whole-body timer/conditional boundaries are part of the first release.

**Multi-instance calls are required in TASK-05's first release.** This is a planned subtask, based on repository inspection on **2026-11-11**, not a claim of implemented behavior. TASK-05A owns the shared JSON/local-output contract, item storage, coordinator, aggregate publication, and common read surfaces. This subtask owns their call integration, including target-version pinning, invocation/message ownership, mapping validation, cancellation, authoring, and call-specific UI and tests.

## Scope and acceptance criteria

- [ ] `callActivity.multiInstance` supports instance-scoped or readable shared typed collection sources in `parallel` and `sequential` modes. Initialization freezes the collection; invalid/oversized input fails before any child is created, and an empty collection produces `[]` without an invocation or runtime target pin.
- [ ] Input mappings accept exact `mi.item`, zero-based `mi.index`, and `mi.total`, frozen parent-instance inputs, and readable parent shared aliases captured when each item is admitted. Output mappings target declared locals or authorized `readWrite` shared aliases; shared aggregate/error targets follow TASK-05A. Ordinary call mappings remain compatible.
- [ ] Each nonempty body pins one immutable target definition/default plain start at successful durable initialization. Every later item and retry uses that pin even if the published default changes.
- [ ] Every item creates at most one child. Duplicate jobs, immediate child completion, restarts, output-delivery retry, and aggregate-publication retry cannot create replacement children or advance the parent twice.
- [ ] A call item retains its MI slot until its result settles, including child human work, waits, nested calls, and internal incidents. Sequential execution admits the next child only after the previous item commits success.
- [ ] Existing call depth, root admission, business-key, fixed-system-actor, child-start, and failure semantics remain enforced, except that reaching the root's open-call limit holds an MI item pending instead of failing it. MI concurrency supplements those limits.
- [ ] Child faults/independent cancellation take the whole-body catch-all boundary, after the core publishes its partial result, or fault the parent. Operational start/output failures without a boundary open incidents; child internal incidents keep the item waiting.
- [ ] Parent/body cancellation immediately settles and fences unfinished invocations, then stops descendants through durable cancellation without locking two existing instances together. Late results cannot write outputs or continue a settled body.
- [ ] Timer and conditional boundaries support interrupting and non-interrupting modes on the MI call body, with activation/occurrence fences, existing schedule/expression rules, and no per-item subscriptions. An interrupting condition can cancel queued entry; either boundary type can cancel unfinished children after initialization. A non-interrupting handler leaves them running.
- [ ] Shared output conflicts preserve saved child results and the publication candidate. Authorized recovery never starts a replacement child, silently overwrites a changed shared value, or applies a committed shared write twice.
- [ ] Ledger, progress, calls API, node activity, Operations, editor, and instance detail identify the body/item/invocation without exposing automatic items as human work or granting authority through relationships.
- [ ] Until the parent's integration step, call bodies publish and run only on a Development host with the release switch open. A kind whose adapter is not installed stays rejected, and every other environment rejects all three (§1).
- [ ] Target pin retention, migrations, version-switch/reactivation blockers, ordinary-call regressions, real-browser scenarios, and owning documentation are complete before this subtask is Done.

Out of scope: cardinality, early completion/voting, selectable parent interrupts, item-dependent workflow targets, extra authored version bindings, replacing a terminal child on retry, selective item cancellation, call mapping expressions/paths/conversions, direct per-item writes to parent instance variables, per-item boundaries, and compensation. Conditional and timer expressions retain their existing persisted-instance-variable dependency rules; shared-change subscriptions are not introduced. Called child workflows retain their existing capabilities and their own normal variable/task authorization.

## Current state and reusable components

Ordinary calls are implemented under [TASK-01](task-01-call-activity.md), while multi-instance calls are not:

- [CallActivityRules.cs](../../Flowbit/src/Flowbit.Shared/Models/CallActivityRules.cs) validates strict whole-variable mappings and target compatibility. Ordinary call input sources are parent instance variables; output destinations are declared parent process variables.
- [WorkflowEngineService.CallActivities.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.CallActivities.cs) owns call entry, durable start/result/cancellation processing, frozen mapping values, system attribution, and parent continuation. Successful ordinary delivery writes process outputs and continues once per invocation; that delivery path cannot be reused unchanged for MI items.
- [CallActivityRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/CallActivityRepository.cs), [call entities](../../Flowbit/src/Flowbit.Infrastructure/Entities/CallActivityEntities.cs), and [call records](../../Flowbit/src/Flowbit.Service/Models/CallActivityRecords.cs) provide invocation/result identity and durable messages. Ordinary activation uniqueness assumes one invocation per parent token activation.
- Existing calls pin the published default when each `callStart` creates its child. The new body-wide pin is specific to MI calls; ordinary-call resolution timing remains unchanged.
- Ordinary calls currently reject shared mapping sources/targets and timer/conditional attachments. The planned MI-only extension needs explicit validation and runtime branches; it does not imply that ordinary calls already support these settings or broaden their contract.
- `CallActivityExecutionDto`, `ParentCallSummaryDto`, and `ExecutionPositionCallDto` in [CallActivityDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/CallActivityDtos.cs) expose relationships without granting task/action permissions. [InstanceCallActivities.razor](../../Flowbit/src/Flowbit.Ui/Components/Shared/InstanceDetails/InstanceCallActivities.razor) already renders paged call history, pinned versions, and parent/root/child links.
- Existing call tests cover lifecycle, admission, mappings, ancestry fences, operations, persistence, and recovery. Extend these rather than introducing a separate subprocess engine.

## Plan

### 1. Integration with the shared core

Use TASK-05A's one parent token, one automatic MI execution, immutable input snapshot, pending/active item rows, local output declarations, persisted slots, ordered aggregate, and reserved dispatch/completion capacity. Items are not sibling graph tokens or user tasks in the parent; their called workflows own normal independent tokens/tasks.

Calls remain intrinsically durable. **Reject authored `asyncBefore`, `asyncAfter`, and `job` settings on MI calls.** Entering the node queues the core's proposed `multiInstanceStart` job; that job initializes the body, rather than starting a child inside the caller's transition. Do not wrap a call item in a generic REST/script `multiInstanceItem` job or add a second retry owner. Item work continues through `callStart`, `callResume`, and `callCancel`.

**Release boundary.** The foundation's publication and runtime guards are unconditional, so no call body can be published or run today, even on a Development host: `WorkflowDefinitionReadinessChecker.EnsureDurablePublicationAllowed` refuses any automatic body, and call entry stops at `EnsureAutomaticMultiInstanceRuntimeAvailable`. Open them for call bodies only, as [TASK-05A §8](task-05a-multi-instance-core.md#8-definition-validation-compatibility-and-safety-of-loading) records under **Opening by activity kind**:

- A call body publishes only when the release switch is enabled, which only a Development host allows, and every automatic body in its definition has an installed adapter.
- The call adapter counts as installed only in the change that completes intrinsic entry, pinning, admission, delivery, failure, and cancellation.
- Call entry uses `multiInstanceStart` and call messages, so it does not share the service/script entry and item routing.
- Tests pin that a kind whose adapter is not installed stays refused at publication, entry, and item processing, and that a host outside Development refuses call bodies whatever its options say.

Each body has exactly one unconditional ordinary outgoing flow. It has no selectable outcomes, engine-only fallback, completion priorities, or human vote functions. One interrupting catch-all error boundary may attach, plus up to eight timer and conditional boundaries combined, with either `cancelActivity` mode. HTTP status matching remains invalid. Every boundary attaches to the whole body, never one item in isolation. Widen host eligibility for automatic MI calls explicitly in server/editor validation and runtime setup; ordinary single-call restrictions stay unchanged.

Follow TASK-05A's boundary lifetime: conditional subscriptions begin at token entry and can interrupt a queued entry, including a token waiting for another body's node; timer subscriptions begin once at successful body initialization. Keep them through item work, capacity/incident waits and completion publication. Close them when the body settles or an error boundary wins. No authored async flags are needed to make an MI call an eligible host, and retries never rearm a consumed timer. A condition observes the parent instance, not a child's variables or shared-key changes.

Count body entry once under the parent's automatic-activation loop guard. Fan-out uses the MI instance limit, not one loop-guard charge per item. Preserve the guard within each called workflow; retries or aggregate delivery must not reset it and permit an infinite parent loop.

The shared core freezes the collection and parent-instance inputs at successful durable initialization. A mapping that names the collection alias, or another alias resolving to that same shared key, always reads the body's frozen collection. Capture other readable parent shared aliases once per item at successful admission, under the parent's shared-access plan; persist their values, binding identity and revisions with the invocation input snapshot. Later item admissions may observe newer shared values, while retries of an admitted item retain its saved inputs. Calls use the fixed `callActivity` actor with no inherited roles, JWT claims, or delegation. Map requester/approver information as explicit data when needed. Keep call mappings narrower than REST/script contexts: shared aliases are supported, but config, setting and arbitrary expression sources remain invalid.

### 2. Workflow JSON and mapping rules

Use the shared `MultiInstanceModel` additions: collection-only source, `afterAll` completion, optional `maxConcurrency`, declared `outputVariables`, and an instance-scoped or authorized shared scalar JSON `resultVariable` initialized to `[]`. TASK-05A owns exact common defaults, limits, result envelope, shared access/`ValueRevision` fences, name and resolved-key collisions, and validation order.

Keep `CallVariableMappingModel`'s strict shape, whole-variable copies, 100-mapping limit, and missing/null/required distinctions. Add no implicit conversion, expression, JSON path, or mapping-specific default:

| Mapping part | MI-call rule |
| --- | --- |
| Input `sourceVariable` | Only here, permit exact `mi.item`, `mi.index`, and `mi.total` as narrow reserved-prefix exceptions. Other names read the frozen parent-instance snapshot or a declared readable parent shared alias captured at item admission. The collection source and aliases resolving to its key always use the frozen body collection. `callStart` never resolves an alias again against live shared values. |
| Item source type | Derive scalar type from the collection's element declaration; `json[]` yields scalar JSON items. Index and total are non-null scalar numbers. Preserve item order, duplicates, casing, and whitespace. |
| Input `variable` | A declared input of the child's default plain start. Preserve required mappings, optional child defaults, type/array/nullability validation, role-free start, `callOnly`, and prohibition on header-idempotency contracts. Child start validation remains authoritative. |
| Output `sourceVariable` | A child instance variable declared/produced by the target workflow. It does not gain reserved item names or parent context. |
| Output `variable` | A declared `multiInstance.outputVariables` name or a declared parent shared alias with `access: "readWrite"`; never a direct parent instance-variable target. Validate the complete local overlay and shared write candidate before committing either. Reject the collection-source, aggregate-target and body error-target shared keys, including aliases resolving to those same keys. |
| Missing optional output | For a local target, leave its default/unset state intact, then apply the core's final local completeness checks. For a shared target, stage no write. Distinguish a missing source from explicit JSON null, which must satisfy the destination's nullability. |
| Forbidden sources | Config/setting sources, mapping expressions, and paths remain invalid. Parent shared access uses the parent's bindings/permissions; it grants no access to the child, whose own shared-variable contract remains independent. |

Proposed node fragment, not a currently runnable definition:

```json
{
  "id": 30,
  "type": "callActivity",
  "name": "Check each order",
  "multiInstance": {
    "mode": "parallel",
    "source": "collection",
    "collectionVariable": "orders",
    "completionEvaluation": "afterAll",
    "maxConcurrency": 4,
    "outputVariables": [
      { "name": "itemApproved", "dataType": "boolean", "nullable": false }
    ],
    "resultVariable": "orderResults"
  },
  "callActivity": {
    "workflowKey": "check-order",
    "versionBinding": "publishedDefault",
    "inputMappings": [
      { "sourceVariable": "mi.item", "variable": "order", "dataType": "json", "required": true },
      { "sourceVariable": "mi.index", "variable": "orderIndex", "dataType": "number", "required": true }
    ],
    "outputMappings": [
      { "sourceVariable": "approved", "variable": "itemApproved", "dataType": "boolean", "required": true }
    ]
  }
}
```

The parent declares `orders` as `json[]` and `orderResults` as scalar JSON initialized to `[]`; either may instead use a compatible authorized shared binding under TASK-05A's rules. The child default start declares required scalar JSON `order` and number `orderIndex`, and produces Boolean `approved`. Both workflows and their mappings must pass publication/readiness checks. `itemApproved` is a local output, not a process declaration. A separate mapped child result may target a parent `readWrite` shared alias; such writes are not copied into the local `variables` result envelope implicitly.

Update `CallActivityRules.FindLocalProblem`, `FindTargetProblems`, `SourceDeclarations`, declared-producer analysis, readiness/caller compatibility, shared-access/lock-order analysis, and editor mirrors together. MI call mappings must distinguish locals, shared aliases and instance inputs without widening ordinary-call mappings. Locals must neither collide with real process producers nor be inferred as process variables for downstream calls. Build the collision set after removing locals from automatic producer inference; otherwise a local can falsely collide with itself. Validate shared binding type, access, archive state and resolved-key collisions; include shared dependencies of local/aggregate/error validation in the access plan.

Server normalization and editor import must preserve accepted call MI without clearing unrelated invalid call settings. Keep explicit bad enums/types/unsupported fields for actionable validation; selecting/redrawing a node never rewrites it. Preserve ordinary-call reserved-prefix rules and human-MI defaults. Pin ordered editor-error arrays and test omitted, `afterEach`, and `afterAll` values against the core's explicit-`afterAll` rule.

### 3. Target binding, pin lifecycle, and compatibility

For each nonempty body, resolve the target family's published default and default plain start **once during successful durable initialization**. Acquire the target-family lock before parent-instance/runtime locks. Persist proposed `PinnedChildDefinitionId` and `PinnedChildStartEventId` on the body, with explicit pin lifecycle. Failed initialization may retry resolution only until that pin commits. Empty input creates no invocation and needs no runtime target pin; definition publication still validates its target.

Add an internal exact-definition child-start path. Do not add another public/authored version binding, or re-resolve the default per item. **Every item `callStart`, including retries with an existing pin, acquires the target-family lock before parent runtime locks**, because internal child creation also uses the family lock. Child-start legality, mapped values, business-key uniqueness, and admission are rechecked for each child.

Guard deletion and unpublication of the pinned target while the body or invocation/result recovery work is open, including the interval before the first child exists. Changing the family's default remains allowed and affects new bodies only. Preserve ordinary invocations' existing resolution timing, deletion guards, and caller compatibility previews. Document that an operator cannot retire a pinned version until the bodies that pin it settle or are cancelled.

The core blocks parent version switches while automatic entry/body/completion work is open. Retain existing unsettled-call blockers on both parent and child as well; include pending and queued items, incidents, and result delivery. Settled history alone does not block a later compatible switch. Do not partially rebind active item jobs or their pinned definitions.

Automatic bodies/items are not reactivation targets. Keep ordinary-user-task target rules, prohibition on directly reactivating linked children, and the requirement that calls settle and descendants stop before root reactivation. A later valid revisit creates a new body/item/invocation identity and takes a new pin; it never replays a settled child.

### 4. Invocation ownership, persistence, and migration

Coordinate one additive schema design with TASK-05A; do not create competing shared body/item/coordinator migrations. All objects live in `flowbit`. This subtask owns call-specific columns, constraints, indexes, guards, and retention integration:

| Storage concern | Required design |
| --- | --- |
| Body pin | Add target definition/start and pin lifecycle to the core's parent execution. Do not overload an invocation's populated-child identity columns with a pre-creation body pin. |
| Invocation item ownership | Add a nullable automatic-item FK to parent-owned `call_activity_executions`, unique for non-null items; record distinct invocation activation/fence and separate body activation. One item owns at most one invocation and one child. |
| Ordinary activation uniqueness | Replace unconditional ordinary `(parent instance, token, activation)` uniqueness with filtered ordinary-call uniqueness plus automatic-item uniqueness, preserving both guarantees. Existing ordinary rows retain null item correlation. |
| Cross-instance messages/results | Keep child-owned result and `callResume` rows free of parent-item FKs. Resolve the item through its parent-owned invocation at delivery. Preserve existing invocation correlation and cross-instance message ownership. |
| Shared input/output state | Persist the item-admission shared input snapshot, resolved bindings and output `ValueRevision` fences before `callStart`; retain saved child outputs and the validated local/shared publication candidate through delivery conflicts. Store publication identity so a duplicate resume cannot apply the same shared writes twice. Do not add child-owned FKs to parent shared/item state. |
| Boundary ownership | Reuse timer/conditional subscriptions and occurrences keyed by parent token activation, never one set per child. Cancellation can find entry work before a body exists and all automatic items after initialization; retain pending occurrence fences according to existing event retention rules. |
| Ledger | Use the core's `automaticActivityItem` visit and item correlation. Create pending ledger rows in bulk; no duplicate body node visit, parent `user_tasks`, role policies, or vote-count rows. Called workflows keep their own normal ledger. |
| Read indexes | Support body/item lookup, invocation lookup, active pin guards, bounded call paging, and grouped counts without reading snapshot payloads. |

A compact item identity must survive for the invocation's lifetime. Payload/history pruning must never null the item FK and make an MI invocation look like an ordinary call under the filtered uniqueness rule. Preserve settled invocation identity/fences under existing call retention rules.

Protect pending/active/incident-blocked body pins, item results, invocations, child result snapshots, jobs, and completion records until their required delivery/publication is durable. Existing protection through token `WaitingJobId` is insufficient for many items, and completed jobs are pruned by age while the parent still runs, so follow the [core's retention rules](task-05a-multi-instance-core.md#5-persistence-and-migration): the body row references its snapshot, and items keep their own results. Define FK delete order and retention eligibility after a child succeeds while its parent still waits. Mark `HistoryPrunedAt` where existing pruning rules require it, without fabricating historical automatic executions or silently discarding recovery state.

Apply existing 1 MiB per-call input/output snapshot limits alongside the [core's body limits](task-05a-multi-instance-core.md#4-limits-and-admission), including its result-size refusal at entry. Do not copy the full collection into every invocation snapshot.

Update entities, `Data/AppDbContext.cs`, records/ports, repository SQL, migration/designer/model snapshot, retention, DTOs, and schema documentation together. Migration tests must preserve populated ordinary calls, pending call messages, human MI, and normal jobs, and prove constraints reject swapped item ownership or duplicate children.

### 5. Admission, messages, and transaction protocol

Admission creates the parent-owned invocation with immutable item/body correlation and one stable invocation identity. Pending unadmitted items are descriptors, not child instances or call reservations. Use ascending item index and TASK-05A's persisted concurrency window; no in-memory semaphore or Worker lease is the authority.

If shared capture or input preparation fails before successful admission, the owning entry/dispatch control job owns recovery and records the affected item correlation. While an incident is open, that item and its ledger remain pending with an admission-blocked reason; it owns no slot, committed input snapshot, invocation or child. Pause new admission while allowing previously admitted work to settle. Missing/archived/contract-changed shared bindings or denied access are operational incidents and do not take a business error boundary. A value that violates an otherwise valid input contract follows the existing call error-boundary/incident policy. An authorized retry repeats admission only, capturing current shared values at its first successful admission; no failed capture becomes a frozen business input. Failures before body/item creation remain on the entry job without fabricated item or child records. Once admission commits, subsequent retries reuse that invocation and its saved inputs.

An admitted call keeps its MI slot while the child waits for human work, messages, timers, conditions, nested calls, or an internal incident, and until the local result and any shared write candidate commit together. No Worker lease remains held through that wait. Sequential mode starts item N+1 only after N settles successfully. The core pauses new admission on a body item-level start/output incident; already admitted work may settle. A child's internal incident only occupies its slot and does not pause other available parent slots.

Enforce `WorkflowCallActivities.MaxDepth` (current default 8) and `MaxOpenInvocationsPerRoot` (current default 256) in addition to the MI instance/concurrency settings and queue limits. The root limit remains an existing **soft admission threshold** across concurrent parents. Do not add a root-instance lock or claim a hard global child maximum. Retries retain/reuse existing reservations; rejected calls consume no reservation. Nested calls remain subject to normal depth/admission and child-loop guards.

Reaching the root limit is back-pressure for MI items, not a failure. An ordinary call refused there takes its catch-all boundary or opens an incident, but an MI body's own window can exceed the soft limit, so an MI item refused at admission stays pending: it creates no invocation, consumes no reservation or attempt, and takes no boundary. Progress reports admission as blocked by call capacity, and the reserved dispatch job retries with bounded backoff. If the body has no admitted, unsettled item at that moment, nothing of its own will free capacity, so the dispatch job opens an incident with the existing `call_capacity_exceeded` code instead of backing off silently. That makes nested stalls visible, and retrying the incident repeats admission. Depth refusals remain failures, because waiting cannot change them.

Queue-capacity contention is distinct from call admission failure. Use TASK-05A's reserved, deduplicated dispatch/completion control work for temporary job capacity; do not consume call/activity attempts, lose pending wake-ups, or roll back committed successful item results, and open a contention incident only under the core's rule for a body with nothing in flight. Call admission failures other than the root limit retain their own incident/boundary semantics.

| Phase | Call-specific responsibility |
| --- | --- |
| Enter/initialize | Attach/evaluate conditional subscriptions and queue intrinsic entry if still eligible; wait without consuming attempts while another body of the node is open. Lock target family before parent runtime state, snapshot/pin, install core body/items/ledger and timer subscriptions, reset aggregate, drain notifications/recheck fences, and admit or dispatch. Empty input queues aggregate completion without calls. |
| Admit item | Under parent runtime and ordered shared-value locks, capture shared mapping inputs and output revisions, build immutable invocation inputs, and persist the invocation/admission together. Failed or capacity-refused admission commits no input snapshot, slot or invocation. For a pre-invocation failure, correlate the item with the owning entry/dispatch job and apply the operational-versus-input-contract distinction above; an uncaught incident leaves it pending and pauses admission. Release these locks before a child-start transaction. |
| `callStart` | Lock family first on every attempt, then fence parent activation/body/item/invocation and create at most one child from the pin through the internal exact-definition path, using already saved mapping values. Do not read or lock parent shared rows during child start. No second generic item job. |
| Child terminal commit | Capture mapped child outputs and enqueue existing result/message without locking or writing parent runtime/item rows. Preserve completed child state if mapping later fails. |
| `callResume` | Lock receiving parent only and its ordered shared write/validation dependencies, re-read state, fence invocation/item/body, validate saved child output into local/shared candidates, check output revisions, and commit shared writes, locals and item settlement once. Drain applicable notifications/recheck eligibility before refill or aggregation. No direct per-item parent instance write or normal outgoing traversal. |
| Aggregate | Core completion publishes ordered saved locals to the instance/shared aggregate target, drains parent conditional notifications, and continues once as `callActivity` if still eligible. A delivery or aggregate retry never recreates a successful child. |
| `callCancel` | Parent settles invocation/cancel-message state first; each message then locks only its receiving child and propagates cancellation one level at a time. |

Preserve the **one existing instance per transaction** rule. Parent cancellation cannot wait on a child's runtime row; child completion cannot acquire the parent's row. Add the automatic-item FK only to parent-owned invocation state; avoid new cross-instance FKs that introduce hidden lock acquisition.

Within parent mutation, follow the core's instance → gateway execution/state/branch → parent token → MI execution → items in ID order, then the existing job/incident protocol and its shared-access ordering. Acquire the complete shared key set in the core's deterministic order, and include synchronous conditional continuations in the readiness proof; a late discovery of an earlier key is not allowed. The target family precedes the runtime sequence for initialization and every item start. Parent-only item admission stages shared data separately from child creation, so shared access cannot add a hidden cross-instance lock. Audit lease, retry, heartbeat, cancellation, retention, and definition guards for inverse locks. Use fresh database state after staging; a token ID alone is not a sufficient fence.

Fence immutable definition, parent/body activation, execution, item, invocation and relevant job/lease generation, plus eligible states. Duplicate result delivery, immediately available child results, simultaneous last-item resumes, and retry after cancellation cannot increment counts twice or queue a second completion.

### 6. Failure, cancellation, recovery, and audit

| Call outcome | Required MI behavior |
| --- | --- |
| Shared binding/access/lifecycle failure before invocation | Open an operational incident on the owning entry/dispatch control job with item correlation when an item exists. Keep the unadmitted item/ledger pending with no slot, frozen input snapshot, invocation or child; pause new admission. Do not route a business error boundary. Retry admission after the cause is repaired and capture shared inputs only on successful admission. |
| Input value rejected by a valid contract before invocation | Take the whole-body catch-all error boundary if present; otherwise use the owning entry/dispatch job's correlated admission incident. An uncaught item remains pending without a slot/invocation/child; retry admission, not a nonexistent invocation. |
| Start, target, input, depth, or business-key failure after admission | Take the whole-body catch-all error boundary if present; otherwise wait with a retryable call incident. Retry the same admitted invocation with saved inputs and retain any committed body pin. Earlier body-initialization failures remain on entry work rather than inventing an item or child. |
| Root open-call limit reached at admission | Back-pressure, not a failure: the item stays pending, takes no boundary, and the dispatch job retries. A body with nothing in flight opens a `call_capacity_exceeded` incident (see §5). |
| Child internal job incident | Child and call item wait. Recover that child's job under its own authorization; do not treat it as termination or free the slot. |
| Successful child, including its own terminate end | Deliver mapped locals and authorized shared writes atomically with item success, then refill or aggregate if still eligible. Termination affects only that child. |
| Child fault or independent cancellation | Take the body's error boundary and cancel unfinished siblings; without an error boundary, fault the parent through its normal terminal-status path and cancel unfinished work. Never automatically replace the child. |
| Invalid child output/local result | Take the error boundary if present; otherwise open an output-delivery incident. Retry from saved child output; the child remains completed. |
| Shared output-version conflict | Open an operational delivery incident, preserving saved child output and the complete publication candidate; a business error boundary does not catch this conflict. Authorized recovery rechecks binding/access/validation and explicitly accepts publishing that saved candidate over the newer value, then refreshes the output fence under the core contract. It never recomputes/merges the candidate, silently last-write-wins, duplicates a committed write, or recreates the child. |
| Aggregate output-version conflict | Retry the core completion incident from saved item locals, without starting or reinvoking children. |
| Aggregate validation/size failure that retry cannot change | Take the error boundary or fault the instance under TASK-05A; never create an endlessly retryable completion incident. |
| Interrupting timer/conditional capture | Stop the whole body's unfinished work, close subscriptions/recovery, durably cancel descendants and route the captured boundary once. Do not publish an error-style partial aggregate. Before initialization, cancel only this token's entry work; another token's active body is unaffected. |
| Non-interrupting timer/conditional capture | Create the normal sibling handler token and leave body/items/children running. Recurring timers and false-to-true conditional rearming retain the existing occurrence rules. |

Calls do not acquire REST/script `boundaryFirst`/`retryFirst` policies. Keep existing bounded, value-free `call_*` diagnostics, scalar error-boundary output and normal Operations retry authorization; an MI error target may be an authorized shared alias under the core's write/fence contract. Parent call continuation uses the fixed `callActivity` actor, never a child approver or retrying operator's roles/claims/delegation. Timer/conditional handlers retain their normal event attribution rather than fabricating a child action.

For an already admitted item, a recoverable start/output incident leaves its ledger visit active, with failure evidence in attempts/incidents; retry restores work state without reopening a terminal ledger visit. A pre-invocation admission incident instead leaves the unadmitted item/ledger pending as specified above. An error boundary or terminal failure faults the relevant parent-item visit; timer/conditional interruption and other cancellation cancel unfinished visits while previously successful items remain completed. An invalid mapped result never changes a completed child's own status/ledger. Preserve failed sibling evidence when closing superseded recovery paths.

Instance cancellation, terminate/error ends, gateway branch cancellation, scoped interruption, parent-call cancellation, and interrupting timer/conditional capture must reach every body item and invocation through the central cancellation paths. Settle unfinished invocations under the parent lock and enqueue descendant cancellation. Pending items never start children after the fence changes; already completed child instances remain completed. Committed shared writes and remote effects are not rolled back by later cancellation. A non-interrupting handler is independent sibling work; normal body completion closes future host subscriptions without cancelling handlers already spawned or durably captured.

Ancestor fences reject descendant transitions immediately, including while the Worker is stopped and after depth limits are lowered. Physical cancellation follows durably one level per job. Boundary continuation need not wait for descendants to stop. Close superseded delivery/cancellation incidents with existing helpers; late results cannot publish locals, shared writes, aggregate, history, or continuation into a settled body.

The core resets the aggregate to `[]` on successful initialization and publishes it once after all items succeed. When an error boundary catches a child fault, independent child cancellation or operational failure, the core first stops the body and closes its timer/conditional subscriptions; its completion job then publishes the partial result of completed, failed, and cancelled rows and routes that error boundary. A timer/conditional interruption or other body cancellation does not initiate a partial aggregate or restore `[]` over a later authorized write. If a successful instance aggregate reset/publication itself triggers a conditional boundary, retain that accepted write and suppress new admission/normal continuation after the notification drain; shared writes do not create cross-instance conditional notifications. Keep completed item evidence available to authorized detail readers.

Record parent incoming/outgoing sequence-flow traversals once per body, not per child delivery. Called workflows retain their own normal flow and actor history. Automatic parent items have no human owner/action/delegation and emit no human-task lifecycle webhooks; actual child tasks/instances retain normal lifecycle capture. Classify new job kinds in existing incident capture tests. Telemetry includes bounded kind/status/count/duration/retry data, never values, output payloads, credentials, or unbounded IDs as metric labels.

### 7. Read contracts, authorization, and UI

Extend existing call surfaces additively; TASK-05A owns common automatic-item routes and progress. No public complete-item, retry-all, detach, reparent, or replacement-child operation is introduced.

| Surface | Call integration |
| --- | --- |
| `GET /api/instances/{id}/calls` | Add nullable MI execution/item/index correlation, preserving deterministic bounded paging, target/pinned version, invocation/child state, timestamps, and payload-free summaries. |
| Parent/root/child summaries and execution positions | Identify body/item and common target pin while preserving ordinary-call fields. Show the parent body once as a waiting position rather than fabricating one parent token per child. |
| Core item summary/detail | Include invocation/child references, local result and bounded failure detail under the [core's paging and access rules](task-05a-multi-instance-core.md#9-api-contracts-and-authorization); no secret staging snapshots or shared value payloads granted merely by a call relationship. Expose publication-conflict correlation through authorized Operations. |
| Node activity and Operations | Correlate item ↔ invocation ↔ call job/incident; grouped queries and exact counts, no per-child job lookup. Distinguish an incident on a child from a call-message incident affecting its receiver. |
| Definition operations | Report active pin deletion/unpublication blockers even before child creation; preserve published-caller and default/deletion compatibility previews. |

Retain the calls route's current authenticated instance-detail read boundary. A parent/root/child relationship grants no task, claim, cancellation, administrative, or incident-retry authority. Child operations recheck their own rules; `WorkflowJobs.RequiredRole` still governs operational inspection/retry. A user may see body progress without permission to view item payloads or Operations.

Document 401 for unauthenticated calls, 404 for missing/out-of-scope item or mismatched parent IDs, established 400 pagination handling, and 409 for stale/ineligible recovery. Existing MI `/flows` and `/roles` remain human-only and expose no action for automatic IDs. Compare registrations, DTOs, authorization, tests, and development OpenAPI before declaring the contract complete.

In the shared editor, use the core MI section and extend call mapping selectors with item/index/total, instance and readable shared sources, plus local or `readWrite` shared output destinations. Offer whole-body timer/conditional attachments in both interruption modes, while async/job controls stay unavailable. Field help explains Worker dependence, whole-variable copying, one pin per body, collection versus per-item shared snapshot timing, local versus shared output, conflict recovery and boundary lifetime. Enabling/disabling MI or changing type must surface incompatible mappings/attachments and require explicit accepted edits; never silently redirect process writes into locals/shared targets or preserve MI-only attachments on an ordinary call. Preserve import diagnostics, undo/redo, hosted dirty/saved state, and AI snapshot/apply behavior.

Render sequential/parallel MI markers while retaining the call marker. Check accessible labels/help, standalone/hosted import/export, copy/paste, type conversion, and read-only diagrams without adding a runtime dependency or duplicate editor implementation.

Extend `InstanceCallActivities.razor` to distinguish same-node invocations by body/item, show the common pinned version, and preserve paged child/parent navigation and descendant-cancellation notices. Use the core paged automatic result presentation rather than human User/Action/Submitted-data columns. Show empty completion, waiting child work, shared-publication conflicts, timer/conditional handlers, incident blocking, partial cancellation, and loading/error states. Clear protected details on identity changes, discard stale page responses, and keep links permission-aware; real tasks inside the child remain governed by its own UI/authorization.

### 8. Implementation sequence and ownership

Paths below are relative to the repository root. The proposed shared coordinator belongs to TASK-05A; integrate through its ports rather than duplicating body lifecycle code.

| Step | Principal files/work | Gate |
| --- | --- | --- |
| 1. Contract characterization | `Flowbit/src/Flowbit.Shared/Models/CallActivityRules.cs`, `WorkflowModel.cs`, `WorkflowModelMigrator.cs`; validator/readiness, shared-access and boundary-host rules, and editor mirrors. | Ordinary calls/human MI unchanged; item/local/shared mappings, MI-only timer/conditional attachments, forbidden flags and target validation agreed with core. |
| 2. Coordinated persistence | Call entities/records/abstractions/repository; core body pin fields, `Data/AppDbContext.cs`, migration/designer/snapshot; definition guards and retention. | Fresh/populated migration, item uniqueness, pin protection, pending-message compatibility, no cross-instance lock regression. |
| 3. Initialization/start | `WorkflowEngineService.CallActivities.cs`, intrinsic entry integration, internal exact-definition child start, `CallActivityRepository.cs`, job ownership/capacity. | One committed body pin and one child per item; family-first locking; parent-only shared input/output-fence staging; boundary activation; parallel/sequential waits and admission limits. |
| 4. Delivery/cancellation | Local/shared result adapter, existing call resume/cancel, timer/conditional capture, coordinator notifications, ledger, version/reactivation/terminal gates. | Duplicate/late results, mapped-output and shared-publication failure, boundary/parent failure, ancestry fences and saved-result retry without replacement children. |
| 5. Reads/API/UI | `WorkflowInstanceProjectionService.cs`, call DTOs and instance endpoints, item/node/job projections, typed UI client, `InstanceCallActivities.razor`, canonical editor assets. | Bounded queries, permission matrix, authoring parity, navigation and real browser behavior. |
| 6. Documentation/release evidence | Guides and paired examples below, regression suites, browser acceptance and rollout rehearsal. | All call criteria and shared-core integration gates pass; no first-release deferral of calls. |

Call mapping/UI preparation can run after the shared contract stabilizes, alongside core persistence work. Do not ship call initialization independently of cancellation, delivery, retention, pin guards, and recovery.

### 9. Automated and browser verification

Extend [call definition tests](../../Flowbit/tests/Flowbit.Tests/CallActivityDefinitionTests.cs), [editor call tests](../../Flowbit/tests/Flowbit.Tests/EditorCallActivityTests.cs), and existing lifecycle/admission/failure/cancellation/recovery/operations/persistence suites under `Flowbit/tests/Flowbit.Tests`. Add focused MI cases, preserving ordinary call and human MI regressions:

| Group | Required cases |
| --- | --- |
| Model/mappings | Instance/shared typed collections; item/index/total and shared inputs; missing/null/required/default/type/array rules; empty locals; local/shared output selection; readable versus readWrite bindings; source/aggregate/error key overlap across aliases; ordinary reserved names and ordinary-call restrictions; unsupported flags/per-item boundaries; wrong imported values and ordered editor errors. |
| Pinning | One pin per nonempty body; default changes between sequential admissions and retries; pre-pin failure then retry; empty body; pinned target deletion/unpublication before first child and during recovery; normal call resolution unchanged. |
| Lifecycle/slots | Both modes; immediate completion; human/message/timer/conditional/nested waits; out-of-order ordered results; no held lease while waiting; depth/business-key limits; root-limit back-pressure, including the nothing-in-flight incident and a nested stall; rejected/retried reservations; queue contention and control wake-up recovery. |
| Results/retry | One child per item across duplicate starts/restarts; duplicate/simultaneous last resumes; invalid output preserves completed child; result-size limits; local/shared output atomicity; conflicting same-key item writes, other-instance writes and archived bindings; saved-candidate authorized retry without child replay, recomputation or duplicate shared writes; shared aggregate/error target fences. |
| Shared snapshots/locks | Frozen shared collection, including another alias for its resolved key, versus other shared inputs captured per admitted item; updates between sequential admissions and retries; validation dependencies; no shared locks through child creation/child lifetime; parent-only output delivery and one-existing-instance transaction invariant; readiness rejects an unsafe synchronous boundary continuation. |
| Failure semantics | Shared binding/access failure before admission versus a value rejected by a valid input contract; entry/dispatch incident ownership and item correlation; pending item/ledger with no snapshot/slot/invocation/child; pause admission and retry only admission with fresh capture until first success; subsequent invocation retries keep frozen inputs; child fault/cancel versus internal incident; no-error-boundary fault versus operational incident; error boundary wins once and publishes the partial result; shared conflicts bypass business boundaries; failed sibling evidence; fixed actor/claim isolation; no REST retry policy applied to child workflows. |
| Boundaries | Timer/conditional on MI calls only; combined limit eight; initial-true/atomic/durableAsync conditions; false-to-true rearming; recurring timers; condition entry versus timer initialization timing; entry queued behind another body; incident/capacity waits; empty body; no rearming on retry; spawned/latched non-interrupting handlers survive normal body completion; instance-only event dependencies. |
| Cancellation/races | Cancel before admission/during child creation/resultReady/delivery/aggregate; Worker stopped; deep ancestry and lowered limits; terminate/error/gateway/scoped and interrupting timer/conditional cancellation; late local/shared writes rejected; reset/publication interruption retains the accepted write but suppresses normal continuation; error capture closes subscriptions before partial output; competing last result/timer/condition first-lock-wins; unrelated parent branch survives scoped cancellation. |
| Operations/security | Unsettled parent/child version blockers, no direct child reactivation; caller previews and pin guards; relation permissions; cross-parent item IDs; exact count/order/paging and bounded query count; incident receiver identity; no payload leaks. |
| Migration/retention | Existing calls/messages/human MI preserved; duplicate/swapped ownership rejected; compact item identity retained; active pin/result/snapshot protection; cleanup races and drained rollback restrictions. |

Run focused call filters while implementing, then shared integration and solution regressions. These repository-root commands work in PowerShell 7 and Bash:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --nologo --verbosity quiet --filter "FullyQualifiedName~CallActivity"
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
git diff --check
```

Include core automatic-MI, version/projection/node-activity, shared-access, retention/reactivation, and editor-characterization suites in final coverage. PostgreSQL/Docker-dependent checks must actually run; unavailable prerequisites are not passing tests. Commands here are implementation gates, not checks claimed by this planning-only change.

Follow the [browser smoke setup](../../Flowbit/tests/Flowbit.BrowserTests/README.md) and [Worker acceptance setup](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md) to publish API/UI/Worker, build, and install Chromium before testing. Keep the ordinary smoke fixture Worker-free. Extend [CallActivityAcceptanceTests.cs](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/CallActivityAcceptanceTests.cs) with real disposable PostgreSQL, API/UI/Worker, and authenticated HTTP setup; gates may delay genuine responses but cannot rewrite them or edit database state to manufacture outcomes.

Exercise real clicks, typing, keyboard input, and relevant pointer operations at **1440×900, 1024×768, and 390×844**:

1. Create/import parallel/sequential MI calls; configure instance/shared collections, item/shared sources and local/shared targets, attach both timer/conditional modes, save/reopen, inspect invalid mappings, change types, and undo/redo in standalone and hosted editors. Verify call and MI markers and accessible help.
2. Start with the Worker stopped, observe queued body, then run it. Navigate parent item → child → parent, complete real child user tasks, and verify ordered aggregate results and no human actions on parent items.
3. Change target default between sequential admissions and verify the body remains pinned. Inspect active pin deletion/unpublication blockers and caller compatibility previews.
4. Exercise real child fault/cancellation, child internal incident, missing target/start incident, and output-delivery incident. Recover through authorized Operations and prove no successful child is recreated.
5. Stop the Worker, cancel the parent/body, and verify descendant actions are fenced immediately; restart it and observe physical cancellation with completed children preserved. Release a held result and verify no late output/continuation.
6. Verify calls/items paging, loading/error/empty states, identity changes with stale responses, denied payload/retry navigation, parent/root links, and active version-switch blockers.
7. Change a shared input between sequential admissions and verify the next item sees the new value while an admitted item's retry keeps its snapshot. Cause a real shared-output conflict, inspect authorized recovery and its saved-candidate overwrite decision, and prove no child restarts or shared write repeats.
8. Trigger an interrupting timer while children wait and a conditional while entry is queued; confirm immediate descendant fencing, asynchronous cancellation, no new partial aggregate and no effect on another token's body. Exercise non-interrupting reminders/false-to-true handlers and final-publication races with real writes and Worker responses.

Capture editor controls/markers and queued, running, blocked, completed, and cancelled runtime screens. Inspect browser errors/warnings and host logs separately. Record exact commands/results, browser/version, localhost URLs, interactions, viewport sizes, screenshots, console results, and unavailable checks. Jint/unit/DOM-stub tests do not satisfy real-browser verification.

### 10. Documentation, examples, rollout, and handoff

Update current-behavior guides when implementation lands, not during this planning split:

| Owning documentation | Call-specific update |
| --- | --- |
| [BPMN support](../bpmn-support.md), [node reference](../node-reference.md) | First-release MI calls, instance/shared input and local/shared output contract, single pin, intrinsic durability, MI-only timer/conditional hosts and lifetime, error limitations, limits and deferred scope. |
| [Developer guide](../developer-guide.md) | HTTP-only paired parent/child walkthroughs, collection versus item shared snapshots, output aggregation/shared conflict recovery, fixed identity, failure taxonomy, boundary interruption/reminders, cancellation, incidents and pinning across default changes. |
| [API reference](../api-guide.md), [UI guide](../ui-guide.md) | Call/item correlations, paging/auth/errors, relation navigation, authoring, recovery, pin/version blockers, and changed screenshots. |
| [Deployment](../deployment.md), [database schema](../database-schema.md) | Call admission interaction, including root-limit back-pressure and its incident, migration objects/constraints/indexes, pin protection, one-instance lock/message ownership, retention/recovery and ordered upgrade. |
| [Runtime reference](../../Flowbit/README.md), [AGENTS.md](../../AGENTS.md) | Call coordinator integration, item/invocation/body and boundary fences, family-before-parent locks, parent-only shared staging/publication, saved-result recovery, ledger and no cross-instance item FK invariant. |
| [Example catalog](../../examples/README.md), [MI examples](../../examples/multi-instance/README.md), [call examples](../../examples/call-activities/README.md) | Paired MI parent/child examples in both modes, shared reads/writes/conflicts, interrupting and non-interrupting timer/conditional boundaries, empty input, child error-boundary/incident recovery, explicit inputs/outputs/target publication/Worker prerequisites. |
| [TASK-01](task-01-call-activity.md), [parent TASK-05](task-05-multi-instance-automatic-activities.md), [backlog](README.md) | Preserve TASK-01's historical decisions, point its MI follow-up to current TASK-05, and synchronize actual implementation/evidence status. |

Update root capability summaries, getting started, documentation home, links, or screenshots only where behavior makes them inaccurate. Validate relative links/anchors/images and parse JSON examples; run canonical examples through HTTP and existing authoring catalog checks. Do not change AI tool names, descriptions, or system instructions for this feature.

Follow the parent's [coordinated rollout and rollback limits](task-05-multi-instance-automatic-activities.md#documentation-and-rollout). The call-specific rehearsal adds pending call messages and active target pins to its populated-database checks.

Handoff to TASK-05A must include final pin/item/invocation schema and lock order, call adapter/result/cancellation hooks, failure-to-core-state mapping, read correlations, retained-identity rules, and test evidence for shared dispatch/completion. Mark TASK-05D Done only after its criteria pass with the shared core; mark TASK-05's first release Done only when its service, script, **and call** subtasks are integrated and verified. Record remaining later-release features separately; basic MI calls cannot be deferred to close this release.
