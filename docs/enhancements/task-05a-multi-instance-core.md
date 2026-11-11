# TASK-05A: Shared automatic multi-instance core

[Parent TASK-05](task-05-multi-instance-automatic-activities.md) · [Enhancement backlog](README.md#task-05-delivery-subtasks) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Planned |
| Category | Shared runtime foundation |
| Effort | L |
| Priority | Required foundation for TASK-05 |
| Parent | [TASK-05](task-05-multi-instance-automatic-activities.md) |
| Depends on | None |
| Enables | [TASK-05B service](task-05b-multi-instance-service-activities.md), [TASK-05C script](task-05c-multi-instance-script-activities.md), [TASK-05D call](task-05d-multi-instance-call-activities.md) |

## Summary

Build one persisted automatic-item lifecycle shared by service, script, and call activities. This page owns the common workflow JSON, input snapshots, local-output contract, admission, aggregation, persistence, fences, cancellation coordination, item APIs, and shared editor/runtime presentation. Activity adapters own execution details in their linked subtask plans. All four subtasks are required for the parent first release.

This is a proposed implementation plan split from TASK-05 on **2026-11-11**. Automatic multi-instance support is not implemented. The split changes ownership, not the agreed feature scope or runtime semantics. A plan review the same day settled several contracts; the parent lists them under [Open questions](task-05-multi-instance-automatic-activities.md#open-questions).

## First release acceptance criteria

- [ ] One shared model accepts instance typed-array sources, an explicit `afterAll`, explicit local outputs, and ordered scalar-JSON aggregate results while preserving human MI behavior.
- [ ] One parent token/body owns durable automatic items; automatic parent items never become human tasks or claim/role-policy rows.
- [ ] Input snapshots, item count, local declarations, result size, and persisted concurrency remain bounded and deterministic. A result that cannot fit is refused at entry, before any item runs.
- [ ] Item completion, refill, aggregate publication, and recovery are idempotent; capacity contention has a durable wake-up and never repeats successful activity work.
- [ ] All adapters use common activation/item ownership fences, cancellation, loop guarding, ledger accounting, and terminal-state checks.
- [ ] Aggregate reset/publication handles conditional reentrancy; output conflicts recover from saved results without rerunning completed items. A caught item failure publishes a partial result before the boundary continues, and a completion failure that retrying cannot change never waits in an incident.
- [ ] Every existing reader of `multi_instance_executions`, including administrative actions, role management, version change, and diagram history, ignores automatic bodies or handles them explicitly.
- [ ] Retention and migrations preserve open work and existing human MI, ordinary jobs, and ordinary calls.
- [ ] Paged item reads, progress, node activity, diagram history, permissions, and shared UI work without per-item database queries or fabricated user actions.
- [ ] A release switch keeps automatic MI rejected until the parent's integration step. Shared automated and browser checks pass; B/C/D integration gates remain mandatory before the parent can be Done.

## Scope and ownership

| Owner | Authority |
| --- | --- |
| TASK-05A | Common declarations and coordinator, shared tables/DTOs/queries/UI, reserved control capacity, version/reactivation fences, shared tests. |
| [TASK-05B](task-05b-multi-instance-service-activities.md) | REST staging, local mappings/status, retry/error classification, remote idempotency, service editor controls and scenarios. |
| [TASK-05C](task-05c-multi-instance-script-activities.md) | NCalc/Jint evaluation, local targets/defaults, sandbox and expression behavior, script authoring and scenarios. |
| [TASK-05D](task-05d-multi-instance-call-activities.md) | Intrinsic call entry, version pinning, invocation/message ownership, call-specific schema/queries/UI, child lifecycle and scenarios. |
| [Parent TASK-05](task-05-multi-instance-automatic-activities.md) | First-release scope, dependencies, combined acceptance, documentation ownership and coordinated rollout. |

Call requirements summarized here are integration constraints; TASK-05D owns their detailed contract. Coordinate schema changes in one migration sequence and shared-file changes through this ownership map. Activity plans must not create another coordinator, schema, result format, or default setting.

## Current state and foundations

As reviewed on 2026-11-11, `EnsureMultiInstanceInitializedForTokenAsync` and `BuildMultiInstanceResultAsync` create/read human `user_tasks` and role policies. `MultiInstanceModel` and the existing bulk repository methods supply useful mechanics, but cannot be widened unchanged. Both editor invariants and `WorkflowModelMigrator` currently remove non-user MI. Durable ownership normally assumes one `token.WaitingJobId`; the node ledger recognizes only `node` and `userTaskItem`. Ordinary calls already exist but require the separate invocation changes in TASK-05D.

Other current behavior this plan depends on:

- Human MI rejects an empty collection (it requires 1 to `MaxInstances` items) and resets its result variable to `[]` at initialization, so an empty automatic body is new behavior.
- `MultiInstanceModel.CompletionEvaluation` is a non-nullable string that defaults to `afterEach`, and the editor loader fills in `afterEach` too, so an omitted value cannot be told apart from a written one.
- Ten source files reference `multi_instance_executions`, and all assume human work. The administrative-action candidate query (`AdministrativeActionCandidateRepository`) lists every active row as an administrative position.
- UI-14 diagram history treats MI as user-task only: `InstanceDiagramHistoryQueryService.IsMultiInstance` requires a `userTask`, and `InstanceDiagramHistoryQueryRepository` groups only `userTaskItem` rows.
- `BuildJobCreate` queues every job of a service or script node in the activity class. A node without an authored `job` policy retries after 10 seconds, 1 minute, and 5 minutes.
- The per-instance open-job limit is twice `Workflow.MultiInstance.MaxInstances`, counted from open job rows (`CountOpenByInstanceAsync`); a finishing job frees its own slot for one replacement. A `callResume` job belongs to the child instance.
- A durable REST job snapshots the values its templates reference, including `config.*`, `setting.*`, and `sys.now`, at its first attempt and reuses that snapshot on retries.
- `WorkflowVersionCompatibilityEvaluator.JobDescriptorMatches` compares an open job's node type, queue class, `asyncBefore`, and phase, but not `multiInstance`.
- The only publication gate, `DurableProcessing:PublicationEnabled`, applies to every async, timer, and call definition.

Use existing staging/leases/result persistence, instance/gateway/token locking, variable version fences, cancellation helpers, grouped projections, and browser fixtures. The parent records [later-release exclusions](task-05-multi-instance-automatic-activities.md#later-releases). The proposed defaults and original open questions are resolved by the contracts below; implementation discoveries must update the owning plan before diverging.

## Plan

### 1. Scope and invariants

Support collection bodies on `serviceTask`, `scriptTask`, and `callActivity`. Keep the existing `userTask` path and its defaults, assignee normalization, one-per-actor behavior, outcome voting, result envelope, and authorization unchanged. Preserve ordinary single-call behavior as well.

An automatic body owns one execution token and one multi-instance parent execution. Items are persisted work owned by that parent, not sibling graph tokens or user tasks in the parent instance. A call item additionally owns a child workflow instance, which has its own ordinary tokens/tasks. Each body has exactly one ordinary unconditional outgoing flow, using today's service/script/call topology; it has no selectable outcomes, engine-only default, completion priorities, or `CountFlow`/`PercentFlow` voting.

Require `asyncBefore: true` on a service/script MI node. The editor enables it when the user enables MI; imported definitions without it are rejected, not silently made synchronous. The existing parent entry job initializes the body instead of invoking the activity once. `asyncAfter` remains optional for service/script and applies once after aggregate publication, never once per item. Calls remain intrinsically durable: **do not permit `asyncBefore`, `asyncAfter`, or authored `job` settings on MI calls**. They use a dedicated body-initialization job and existing call message processing. The normal durable publication/readiness gate must recognize all three activity types.

Only existing error boundaries may attach to the body in this release. They interrupt the whole body. Calls retain their single catch-all error boundary with no HTTP matcher; REST tasks retain their existing status-matching rules. Reject timer/conditional attachments during both API and editor validation, including attachments that ordinary `asyncBefore` service/script tasks would accept. Boundaries cannot target an individual item.

Count entry into the body once under the existing automatic-activation loop guard. Count fan-out separately under the MI limit. Items, retries, and aggregate completion must neither multiply the loop count by the item count nor reset it to permit an infinite durable loop.

### 2. Proposed workflow JSON

Extend `MultiInstanceModel` with nullable `maxConcurrency` and an explicit list of local `outputVariables`. Use a dedicated small output-declaration model rather than reusing all process-variable/UI/binding fields. Canonical automatic configuration is:

```json
{
  "mode": "parallel",
  "source": "collection",
  "collectionVariable": "items",
  "completionEvaluation": "afterAll",
  "maxConcurrency": 4,
  "outputVariables": [
    { "name": "itemOutput", "dataType": "string", "isArray": false, "nullable": false }
  ],
  "resultVariable": "itemResults"
}
```

This fragment belongs in a service/script/call node's `multiInstance` property. `items` must be a declared instance variable with `isArray: true`. `itemResults` must be a different declared, writable instance variable with scalar `dataType: "json"`, `isArray: false`, and a JSON array default such as `[]`.

| Property | Automatic-body contract |
| --- | --- |
| `mode` | `parallel` or `sequential`; omission keeps the existing `parallel` default. Canonicalize recognized casing; reject null/unknown values. |
| `source` | `collection` only; omission keeps the existing default. Reject cardinality and `cardinalityExpression`. |
| `collectionVariable` | Required declared instance array; support the existing declared element types, including `json[]` for objects. A scalar `json` holding an array is not a collection source in this release. Preserve order, duplicate values, casing, and whitespace; these are not usernames. |
| `completionEvaluation` | Required as `afterAll`: automatic bodies always wait for all successful items. The model and the editor loader both turn an omitted value into `afterEach`, so validation rejects an omitted value and `afterEach` alike on an automatic body, with a message naming `afterAll`. The editor writes `afterAll` when MI is enabled on a service, script, or call node. Human MI keeps its `afterEach` default. |
| `onePerActor` | Only omitted/false is accepted; it has no automatic-work meaning. |
| `maxConcurrency` | Optional integer 1–1,000. In parallel mode, an omitted value uses the `Workflow.MultiInstance.DefaultConcurrency` engine setting. The effective limit is the smallest of that value, the `Workflow.MultiInstance.MaxConcurrency` cap, and the item count; progress reports it. Sequential accepts omission or 1. Reject wrong JSON types, fractional numbers, zero, and negatives. |
| `outputVariables` | Required list, possibly empty for side-effect-only activities; at most 100 declarations. Names are bounded to 300 Unicode scalars, unique case-insensitively, and cannot use reserved prefixes or collide with any declared/discovered process producer name. |
| Local declaration fields | `name`, `dataType`, `isArray` (default false), `nullable` (default false), optional `defaultValue` and `validation`. Apply existing type/default/validation semantics, but no shared binding, form fields, or variable IDs. Reject unrecognized declaration fields. |
| `resultVariable` | Required instance-scoped scalar JSON target with an array default. It cannot equal the source or any local name, or be the result variable of another multi-instance node, human or automatic, in the same definition: a second body's reset would break the first body's publication fence. Shared aggregate writes are deferred. |

At body entry, resolve the source against its declared contract. Missing, null, non-array, or invalid elements are entry failures. Validate each element with the existing declared element-type rules; a source's whole-variable `nullable` flag never makes a null collection valid. Stop enumeration at the configured instance limit plus one; do not materialize an unbounded source before checking it. An empty array is valid for automatic work and schedules aggregate completion without any item jobs. Human MI rejects zero items, so this is new behavior: an empty body creates no item or node-activity rows, and §7 defines the evidence it leaves.

### 3. Input snapshots, local outputs, and aggregate publication

Persist one immutable parent snapshot containing the collection and the allowed instance inputs. Service/script work retains its causal actor/allowlisted claims. Calls use their fixed system actor, without inherited roles, claims, or delegation. Define the snapshot instant as successful durable body initialization, after the service/script `asyncBefore` wait or the intrinsic call-body entry wait, not the earlier HTTP request. Keep source metadata so operators can identify that instant.

Each item receives an independent local buffer and the same read-only context contract:

| Context key | Value |
| --- | --- |
| `mi.item` | The exact copied collection element, preserving its declared type and JSON value. |
| `mi.index` | The zero-based item index, from 0 through collection length minus one. |
| `mi.total` | The frozen collection length. |

These keys are reserved context, never writable locals or persisted process variables. Process inputs and the item are frozen for the whole body. Permitted `config.*`/`setting.*` values and `sys.now`/`sys.today` are read when each REST/script item first stages and kept in that item's staged snapshot for its retries, as an ordinary durable job keeps the values from its first attempt. A rotated credential therefore reaches every item not yet staged, and a setting changed mid-body can differ between items; authors who need one value for the whole batch copy it into a process variable before the body. Call mappings retain the narrower instance/item sources in [TASK-05D](task-05d-multi-instance-call-activities.md). REST/script jobs preserve existing attempt-specific `sys.jobId`/`sys.jobAttempt` behavior; `sys.jobId` remains stable across retries of that item. Capture `FlowInfo`, where already allowed for scripts, once for the body. Do not allow progress counters such as `mi.completed`/`mi.remaining` to introduce completion-order-dependent item calculations.

Local-output rules apply only inside an automatic MI execution:

- Service `outputMappings` and `statusVariable` target declared locals. Apply the same path extraction, defaults, type checks, validation order, and all-or-nothing successful mapping batch as ordinary REST work. A status local must be scalar number and cannot overlap another mapping.
- NCalc assignment targets and JavaScript `execution.setVariable` target declared locals only. Ordered assignments can read previous staged local writes. Getters return copied JSON values; mutating an input cannot change the stored snapshot.
- Call output mappings target these same locals; input mappings and invocation delivery follow [TASK-05D](task-05d-multi-instance-call-activities.md). A called child is a separate workflow and writes its own variables normally; only explicitly mapped results return to the parent item.
- A local default is applied to a fresh buffer on every attempt. A required non-nullable output may remain unset while execution is in progress, but all declared outputs must satisfy their final contracts before successful completion; nullable unset outputs become JSON null. An empty declaration list produces `{}`.
- Reject writes to process variables, shared aliases, item context, and undeclared names. No local name may shadow a process input or a process producer discovered on another node/flow. A dynamically constructed JavaScript target is checked at runtime, not assumed safe after static validation.
- The parent item evaluation/mapping context excludes shared aliases. Explicit shared references in collection selection, REST URL/header/body placeholders, mappings, defaults, validations, and assignments fail definition validation; include aggregate/error-target validation dependencies. Guard REST templates at runtime before substitution: simply removing shared aliases would let permissive missing-body substitution send `null`. Dynamic JavaScript access also fails with a value-free error. Do not reject a workflow merely because unrelated nodes or called workflows use shared variables.
- A failed attempt discards staged locals. A caught service failure may retain its status in item diagnostics, but it never writes that status into a shared process variable. Only an attached boundary's normal error output may write its declared instance target during boundary routing; shared error targets on this body are deferred too.

Successful aggregate results are index ordered and contain no fabricated actor actions:

```json
[
  {
    "kind": "item",
    "index": 0,
    "item": "supplier-a",
    "status": "completed",
    "variables": { "itemOutput": "notice-001" }
  },
  {
    "kind": "item",
    "index": 1,
    "item": "supplier-b",
    "status": "completed",
    "variables": { "itemOutput": "notice-002" }
  }
]
```

Rows use status `completed` on the success path; a boundary's partial result, described below, also uses `failed` and `cancelled`.

Reset the aggregate target to `[]` once at successful body initialization, using the normal variable-write and conditional-notification machinery. Snapshot its resulting value version. Intermediate successes are persisted only on item rows. After every item succeeds, publish the complete array and the parent continuation atomically, subject to the aggregate target's validation and version fence. No item can publish a partial process aggregate; only the boundary path below publishes one, for the whole body. Two tokens can still reach one node at the same time, for example through an exclusive merge: the second body's reset then makes the first body's publication conflict and open a completion incident. Document this, and keep automatic MI nodes off paths that concurrent tokens can reach.

Both reset and final publication can trigger conditional work elsewhere in the workflow. Install the parent/items before draining reset notifications so cancellation can find them; drain notifications and recheck activation/execution state before admitting children or taking the normal continuation. If a notification interrupts the body, commit that cancellation without enqueueing fresh item work or taking a second route.

When an error boundary catches an item failure, publish a partial result before the boundary continues, in the same transaction and with the same validation, version fence, size limit, and notification draining as final publication. It holds one row per item in index order: completed items keep their local `variables`; the winning failure and any already failed siblings are `failed`; unfinished and never-started items are `cancelled`. Failed and cancelled rows carry `"variables": {}` and no diagnostic; the boundary's error variable, when declared, carries the winning diagnostic. A version-fence conflict opens an incident on the job or call message that routes the boundary; its retry refreshes the fence and repeats the routing from the saved failure without rerunning work. If the partial result fails the target's validation or exceeds the size limit, the boundary continues without it and its diagnostic says so.

On instance or scope cancellation, or while an incident is unresolved, perform no aggregate write: the target normally remains `[]`, and nothing restores that entry value over a later authorized write. Retain completed, failed, and cancelled item evidence for authorized detail queries. A concurrent administrative/branch edit to the aggregate target must cause a publication conflict, not silent overwrite.

Use a distinct durable completion job over saved item results. An output-version conflict opens an incident on that job; its retry refreshes the output fence through the existing authorized recovery contract, but cannot clear completed item results or invoke work again. A failure that retrying cannot change, namely the target's validation rule rejecting the assembled array or the encoded array exceeding the size limit, must not wait in an incident. It takes the whole-body error boundary when one is attached, publishing nothing, and otherwise faults the instance through its normal terminal-status path, as a call's terminal child failure does. Failure of downstream continuation rolls back publication/continuation together and retries this completion phase only. `asyncAfter`, when authored, records the normal outgoing traversal only when that continuation executes.

### 4. Limits and admission

Keep `Workflow.MultiInstance.MaxInstances` (default 1,000) as the total item limit. Add two dynamic engine settings, each accepting 1–1,000: `Workflow.MultiInstance.DefaultConcurrency` (default 4) applies when a parallel body omits `maxConcurrency`, and `Workflow.MultiInstance.MaxConcurrency` (default 1,000) caps both authored and default values, so operators can lower it to protect downstream systems. Keep the cap distinct from the Worker's own `MaxConcurrency` option in documentation. Snapshot the total-item limit and the effective concurrency when the body initializes, and report the effective value in progress. Later setting changes affect new bodies; they do not cancel or expand existing admitted work.

Use persisted slots under the parent lock. Pending rows are cheap work descriptors, not queued jobs or call reservations. Admit only the next ascending-index window. A retrying, incident-blocked, or waiting call item retains its slot; successful result delivery releases one and admits the next item transactionally. When an item-level start/execution/output incident blocks the body, pause new admission while allowing already admitted work to settle. A job incident inside a called child simply occupies that item's slot; it does not pause otherwise available slots in its parent body. Sequential mode never starts the next item before the previous item commits success. When call admission refuses an item because its root already has `WorkflowCallActivities:MaxOpenInvocationsPerRoot` open calls, the item stays pending as back-pressure rather than failing; [TASK-05D](task-05d-multi-instance-call-activities.md) defines that rule. Progress reports whether admission is blocked and why: an item incident, queue capacity, or call capacity.

The MI window supplements `WorkflowJobCapacity`, per-instance open-job admission, Worker `MaxPerInstance`, `ActivityConcurrency`, and `MaxConcurrency`; it does not override them. Current Worker defaults are 4, 6, and 8 respectively. No process-local semaphore may be the authority across replicas. Each Worker leases at most `MaxPerInstance` jobs of one instance, so a REST/script window wider than that times the Worker count runs no more items at once; document this beside the settings.

The per-instance limit is twice `Workflow.MultiInstance.MaxInstances` (2,000 by default), counted from open job rows by `CountOpenByInstanceAsync`, and a finishing job already frees its own slot for one replacement, so REST/script refill is one-for-one. The reservation below still matters for the initial window and for call results, because a `callResume` job belongs to the child instance and frees nothing in the parent. Implement it as a real open control-job row or add open bodies to that count; a reservation the count cannot see protects nothing.

Extend capacity accounting to reserve one control-job slot per open body, separate from its admitted item jobs. Transfer the entry job's slot into that reservation when initializing; do not admit a new body without the reservation. Persist child success before attempting refill. When capacity prevents refill, enqueue a deduplicated `multiInstanceDispatch` control job in the reserved slot; reschedule it with bounded backoff without consuming activity attempts or opening an incident for ordinary capacity contention. It reads pending rows and admits only available capacity. The same reservation transfers to aggregate completion when all items succeed, then releases on body settlement. Define the reservation and control-job generation fences in SQL so concurrent bodies, cancellation, and restarts cannot leak reservations or leave pending work without a durable wake-up. Capacity recovery must never invoke an already successful child again.

Introduce shared automatic-MI constraints with an initial 1 MiB serialized parent input snapshot limit and 1 MiB complete result-array limit, including item values and envelope overhead. Apply existing REST response, Jint value/memory, and 1 MiB per-call input/output snapshot limits as well. Every result row repeats its item, so at initialization compute the bytes the rows need whatever their outputs, the copied items plus envelope overhead, and fail the entry before any item runs when that alone exceeds the result limit. Track accumulated result bytes while settling items and enforce the final encoded limit before publication; an over-limit result is a failure retrying cannot change (§3), never truncated data. Bound local declarations to 100 and fan-out insert batches independently of HTTP payload limits. Document these limits before implementation tests pin them.

Concurrency is a limit on admitted logical work and valid leases. After a network partition or lost lease, an old remote request may still be running while its replacement retries. Do not promise a physical remote-call maximum or exactly-once HTTP effects. Preserve stable per-item job IDs and document the authored idempotency header requirement for downstream systems.

### 5. Persistence and migration

Add an additive migration; all proposed database objects belong to the fixed `flowbit` schema.

| Storage | Proposed changes and constraints |
| --- | --- |
| `multi_instance_executions` | Add activity kind, definition/activation correlation, immutable input snapshot reference, captured effective concurrency, admission/completion state and admission-blocked reason, failed count and bounded result-size accounting. Call bodies also store their pinned child definition/start and pin lifecycle. Default existing rows to human/user-task kind; retain their existing fields and meaning. |
| `multi_instance_activity_items` (new) | Stable ID; instance/execution ownership; immutable zero-based index and item value; pending/active/completed/failed/cancelled state; job correlation; local result/diagnostic; timestamps. Unique `(MultiInstanceExecutionId, ItemIndex)`; nonnegative index; ownership FKs; indexes for parent/status/index admission and paging. |
| `workflow_jobs` | Nullable automatic-item correlation for parent-owned REST/script work; new `multiInstanceStart` (intrinsic call-body entry), `multiInstanceItem` (REST/script), `multiInstanceDispatch`, and `multiInstanceComplete` kinds. Service/script entry reuses `asyncBefore`. Existing call messages use invocation correlation, not a new parent-item FK on child-owned rows. Add unique generation/completion fences and at most one open dispatch/completion control job per reserved slot. Update ownership constraints for each job kind; §6 lists each kind's queue class and retry policy. |
| `call_activity_executions` and call results | Nullable parent-owned automatic-item FK with unique non-null ownership; filtered ordinary-call activation uniqueness; separate item/body fences. Preserve child-owned result/message storage and cross-instance lock boundaries. Keep ordinary invocations unchanged and retain settled invocation identity through history pruning. |
| `workflow_job_snapshots` or dedicated parent snapshot | Store the shared immutable batch context once, with item-job references; retain normal per-item staged invocation/result state and size checks. Avoid copying the entire collection into every child snapshot. |
| `execution_tokens` | Explicit automatic-body wait correlation distinct from single-job waiting. `WaitingJobId` owns entry/completion/async-after jobs only, never all item jobs. |
| `node_executions` | Add `automaticActivityItem` kind and nullable item FK; update existing check constraints/indexes. One child ledger row per item, including pending rows, and no duplicate parent visit; the parent is represented by its execution/progress row. |

Final SQL names and EF mappings must be kept consistent across entities, repository records, migration SQL/designer/snapshot, retention, DTOs, and schema documentation. Add one-active-body-per-token/activation and job/item/invocation ownership constraints so retries cannot create a second batch, swap work between items, or start a second child for one item. Entry failures before item creation remain visible through the parent entry job/incident without inventing a child visit. Include pinned-target deletion/unpublication guards even before an invocation has a child ID.

Ten source files reference `multi_instance_executions` today, and all assume human work. Audit each one and add an explicit human-kind filter or an automatic branch, pinned by a regression test:

- `AdministrativeActionCandidateRepository`, whose candidate query lists every active row as an administrative position.
- `WorkflowRuntimeRepository` and `WorkflowRuntimeRepository.RolePolicies.cs`: inbox, progress, interrupts, and role management.
- `NodeExecutionQueryRepository`.
- `WorkflowEngineService.VersionChange.cs`, `WorkflowVersionCompatibilityEvaluator`, and `WorkflowVersionCompatibilityModels.cs`.
- The mappings in `AppDbContext`, `ExecutionTokenEntity`, and `WorkflowInstanceEntity`.

Create item rows and ledger rows with set-based operations. Settle/cancel unfinished items in bounded SQL batches. Progress and counts must use grouped queries rather than loading every item JSON payload. Do not create `user_tasks`, human role policies, or human vote-count rows for automatic parent items; called workflows retain their normal tasks/policies.

Retention must protect pending/active/incident-blocked bodies and all required items, jobs, snapshots, invocations, call results, and completion records. Existing protection through `token.WaitingJobId` alone is insufficient. Specify FK deletion order and protection after a job or child succeeds but its parent still waits. Preserve completed item/call results until parent publication is durable, and keep call identity/fence records according to existing call retention rules. Retain a compact automatic-item identity for the invocation's lifetime; pruning payload/history must never null its item FK and make it appear to be an ordinary call under the filtered uniqueness rule. Historical pruning follows existing settings and marks `HistoryPrunedAt` where required; the migration does not fabricate historical automatic executions.

### 6. Engine lifecycle and transaction protocol

Implement a focused automatic-MI coordinator, with repository ports, rather than adding automatic branching throughout human task completion. A dedicated `WorkflowEngineService.AutomaticMultiInstance.cs` partial is a proposed implementation location. Factor only shared mechanical helpers from existing human MI and job code. REST/script stage/invoke steps below do not replace the invocation/message lifecycle in [TASK-05D](task-05d-multi-instance-call-activities.md).

| Phase | Transactional responsibility |
| --- | --- |
| Enter | The normal transition queues parent `asyncBefore` for REST/script or intrinsic `multiInstanceStart` for calls, and commits its wait/activation once. No item activity or child workflow starts in the caller's transaction. |
| Initialize | Lock and fence the entry job/body; validate and snapshot inputs/limits; install parent/items/ledger and transfer the control reservation; reset aggregate, drain notifications and recheck fences; admit the first window or queue dispatch; replace the entry wait with the body wait; complete the entry job. Duplicate execution observes the existing body. Empty input queues completion directly. |
| Stage item | Fence parent activation, item ownership, stable job ID and lease generation; build and persist the invocation/evaluation context from the immutable snapshot plus fresh locals. Commit and discard tracked state before external work. |
| Invoke/evaluate | Use the existing HTTP invoker or sandboxed script evaluator outside the long database transaction, preserving timeouts, cancellation, redaction, and attempt metadata. |
| Start/deliver call item | Use its parent-owned invocation and existing call messages; create one child from the body pin, then deliver saved child outputs into local item storage under the receiver's lock. Waiting children consume slots, never long-held leases. |
| Finalize item | Reacquire current state in normal lock order; reject a stale lease/activation or closed parent; atomically store one successful local result, update counts/ledger, and admit further work or queue reserved dispatch/completion. Capacity contention cannot roll back the successful local result to a state requiring reinvocation. |
| Complete body | Fence the unique completion job, reconstruct ordered saved results, validate output version/contract, publish one aggregate, drain notifications and recheck fences, then settle/continue or queue its single async-after job if still eligible. |

Each job kind has a fixed queue class and retry policy. `BuildJobCreate` otherwise queues every job of a service or script node in the activity class; dispatch and completion are control work and must not wait behind item jobs for activity slots.

| Job | Nodes | Queue class | Attempts and delays | Automatic-activation guard |
| --- | --- | --- | --- | --- |
| `asyncBefore` (body entry) | Service, script | Activity, as today | Node `job` policy; entry failures follow its `failureHandling`. | Counts the body once. |
| `multiInstanceStart` | Call | Control | Default delays (10 s, 1 min, 5 min); calls reject an authored `job`. | Counts the body once. |
| `multiInstanceItem` | Service, script | Activity | Node `job` policy for each item: `retryDelays` plus one attempts, and its `failureHandling`. | Not counted. |
| `callStart`, `callResume`, `callCancel` | Call items | As today | Existing call behavior. | Item starts are not counted. |
| `multiInstanceDispatch` | All three | Control | Bounded backoff; never consumes attempts or opens a capacity incident, except the call case in TASK-05D. | Not counted. |
| `multiInstanceComplete` | All three | Control | Default delays; a version conflict opens an incident and other failures follow §3. | Not counted. |
| `asyncAfter` | Service, script | Activity, as today | Node `job` policy. | As today. |

Extend normal mutation order as **instance → active gateway execution/state/branch → parent token → MI execution → automatic items in ID order**, then acquire job/incident locks according to the existing durable protocol. Call-body pinning and every item call-start attempt acquire the target-family lock before this runtime sequence; call messages obey the one-existing-instance rule. Where a transition also touches human work, preserve its established group ordering. Audit leasing, heartbeat, retry, cancellation, and retention for inverse locks; job leasing must remain a short independent transaction before runtime mutation. Never hold instance/item/shared row locks while calling HTTP or evaluating a script.

Item fences include immutable definition, parent activation, MI execution, item, job/invocation identity, lease token/generation where applicable, and eligible item/parent state. A parent token ID alone is insufficient. Keep ordinary `RequireFencedToken` behavior for non-item jobs and add specific item/invocation fences to `JobStillOwnsItsWorkAsync`, call-start/result-delivery, finalization, and conflict handling.

Every finalization refreshes database state; do not reuse EF entities left from staging. Under the instance/parent lock, the first accepted transition wins. Duplicate success, duplicate leasing, simultaneous last-item completions, and retry requests after cancellation cannot increment counts twice or enqueue duplicate aggregate jobs.

### 7. Failure, cancellation, audit, and recovery

For REST/script, reuse existing failure classification and exact/range/catch-all error-boundary matching. `boundaryFirst` tries an applicable boundary before retrying; `retryFirst` exhausts its configured attempts first. With no matching boundary, apply the ordinary retry schedule and then open an incident. Apply this consistently to local-output validation failures and item execution failures; pre-fan-out input failures belong to the entry job and use the same applicable policy. Calls follow the [TASK-05D call failure contract](task-05d-multi-instance-call-activities.md); do not turn a terminal child fault into an endlessly retryable item.

When an item failure is caught, atomically record the winning failure, fault the automatic parent-item ledger visit, interrupt the parent, cancel unfinished item jobs/rows, and route the parent down the boundary once. Previously completed items and called workflows remain completed; an output-delivery failure must not change the completed child's status or ledger. An already failed sibling retains its failure evidence while its open recovery path is closed as superseded. Cancelled pending items remain unstarted. Publish the partial result described in §3, and write the selected boundary diagnostic through normal variable mutation tracking.

For exhausted REST/script work or a recoverable call start/output failure without a boundary, items remain failed/incident-blocked while the execution waits. Display the number blocked separately from successful/active/pending/cancelled items. Keep the item's node-ledger visit active while recovery remains possible, matching ordinary durable activity behavior; failure details live in job attempts/incidents. An authorized incident retry requeues the same item job or call message and restores that item's work state to active without losing successful siblings or reopening a terminal ledger visit. Mark a visit faulted/cancelled only when a boundary or terminal failure/cancellation settles it. Resume admission only after no item-level blocking incidents remain. Multiple item incidents must not accidentally unlock the body when only one is retried.

This is a known first-release limit: a failure that repeats on every attempt, such as an item the receiving API always rejects, keeps an uncaught body waiting until the instance is cancelled, because inputs stay frozen, version switching is blocked, and items cannot be skipped. Document it, recommend an attached boundary (with `retryFirst` where transient failures are expected) for batches that must finish, and keep item skipping in the parent's later releases.

Instance cancellation, terminate/error ends, gateway branch cancellation, scoped interrupts, and parent-call cancellation must all settle the body's rows/jobs using the existing central cancellation paths. They close retry authority immediately. A late successful HTTP response cannot publish locals, process variables, history, or continuation after the fence changes. Cancellation cannot undo remote side effects.

Record node-ledger lifecycle for each parent item, and body completion/boundary evidence once. Define instance-history notes for body start, caught item failure, and body completion, with bounded, value-free payloads; successful items write none. Exclude rows that record the outgoing flow from previous-actor claim and assignment inheritance, as automatic gateway rows are. An empty body leaves its start and completion rows and its flow evidence, and no item or node-activity rows. Automatic parent items have no owner, selected user action, action roles, or delegation claim. Called child workflows retain their own normal task/actor ledger. Preserve captured causal actor/audit claims where the service/script durable audit contract requires them; call execution/continuation retains its fixed actor and never inherits those claims or an operator retry's identity. Extend existing completion reasons only where the current vocabulary is insufficient, and document the resulting enum values.

Count incoming/outgoing sequence-flow traversals once for the body, never once per item delivery. Called workflows retain their own flow history. Update UI-14 diagram history: `InstanceDiagramHistoryQueryService.IsMultiInstance` must recognize automatic bodies, and `InstanceDiagramHistoryQueryRepository` must group `automaticActivityItem` rows by execution as it groups `userTaskItem` rows, so an incoming flow counts once per body and outgoing flows count through target entries. An empty body has no item rows and is marked reached through its counted incoming flow. Add the new kind to the `NodeActivity.razor` kind filter. Add no human-task lifecycle webhooks for automatic parent items; actual child-instance/task events retain normal capture. Classify new job kinds in incident lifecycle capture/catalog tests without adding application-level event emission. Telemetry may include bounded node kind/status, counts, durations, and retry outcomes, never collection values, script text, request bodies, local outputs, credentials, or unbounded IDs as metric labels.

### 8. Definition validation, compatibility, and safety of loading

Update these checks together: shared JSON binding/model normalization, `WorkflowDefinitionValidator`, readiness/shared-access analysis, the editor save-validator phases, import/export normalization, and authoring validation that consumes the same server checks.

- Introduce a named automatic-MI rule helper so topology, local declarations, source/result contracts, async requirements, boundary exclusions, reserved names, producer separation, and expression dependencies have a single server owner.
- Validate explicit malformed values before normalization can erase them. In the editor, preserve invalid imported MI settings and unsupported combinations for save diagnostics. Selecting a node or redrawing must not rewrite its configuration.
- Branch by activity kind when handling outputs and flow rules, and require the explicit `completionEvaluation: "afterAll"` from §2 on automatic bodies. Preserve human normalization, including legacy selectable defaults and `afterEach` behavior. New automatic settings on human MI nodes must fail explicitly rather than accidentally alter human execution.
- Extend `WorkflowModelMigrator` and the editor loader to retain service/script/call MI. Integrate call import-preservation rules so accepting MI does not clear other invalid call settings. Do not reinterpret an older arbitrary object on another node type as valid configuration. Require an explicit edit to clear unsupported settings.
- Separate local producers from process producers in declared-variable inference, output target checks, `CallActivityRules.SourceDeclarations`, shared access plans, and the lock-order proof. Build the global collision set after excluding locals from every automatic-MI node, while retaining ordinary service/status/start/flow producers. A local must not collide with itself through old producer inference, and a downstream call must not mistake it for a parent process variable.
- Preserve ordered editor error arrays and add API/editor parity cases for missing, null, wrong type, invalid enum, unsupported field, and conflicting target combinations. Do not weaken general expression or JavaScript sandbox validation to admit MI.
- Put new editor save-validator helpers between the `// BEGIN/END WORKFLOW SAVE VALIDATOR` markers, because the Jint suites run only that span.

Until the parent's integration step, one release switch in the automatic-MI rule helper, mirrored by the editor's save validator, rejects `multiInstance` on service, script, and call nodes. It is internal code, not a deployment setting, so no operator can enable a partial release; test fixtures override it, and the integration change that records release evidence turns it on. The existing `DurableProcessing:PublicationEnabled` gate cannot serve, because closing it blocks every async, timer, and call definition.

For the first release, **block every workflow-version switch while an automatic MI entry/body/completion is open**, including incident-blocked work and its async-after continuation. Also block a switch whose target adds, removes, or changes `multiInstance` on a node holding any open job or wait, such as an ordinary async-before REST job: `JobDescriptorMatches` compares node type, queue class, `asyncBefore`, and phase but not `multiInstance`, so without this check the job would rebind and start a body. Return a precise compatibility blocker identifying the node/execution. Do not implement partial rebinding of item jobs/snapshots. Completed executions remain immutable history and do not by themselves block switching at a later supported wait.

Preserve unsettled-call version blockers on both parent and child in addition to the MI blocker. Automatic bodies/items are not new reactivation targets; retain the existing ordinary-user-task target restriction, prohibition on reactivating linked children, and requirement to settle calls/stop descendants before root reactivation. Reaching the node again through a loop or later valid reactivation creates a fresh activation, execution, items, and job/invocation identities. Terminal-state detection must not complete an instance while an automatic body still has pending, active, or recoverable work.

### 9. API contracts and authorization

Keep recovery on existing job/incident operations; no new public complete-item, claim, assignment, or retry-all mutation route is needed. Preserve `WorkflowJobs.RequiredRole` (default `admin`) for job inspection/retry and the existing instance/node-activity visibility rules. Instance visibility never grants operational retry authority.

Proposed additive read contract:

| Surface | Plan |
| --- | --- |
| Instance detail/execution positions | Add activity kind and automatic-body progress: total, completed, active, pending, failed, cancelled, effective concurrency, and whether and why admission is blocked (item incident, queue capacity, or call capacity). Keep human progress/vote fields compatible. Do not embed all local results in instance list responses. |
| `GET /api/multi-instance-executions/{executionId}/items` (new) | Automatic-item summaries ordered by index, using repository pagination conventions, default page 1/page size 50, maximum 200. Return index, lifecycle, timestamps, and item/job/incident correlation without input/result payloads. Filter visibility before count/paging. |
| `GET /api/multi-instance-executions/{executionId}/items/{itemId}` (new) | Authorized detail with copied item, local result, bounded diagnostic, and job/incident references. Require owning-instance visibility and the node-activity read scope used for execution detail; IDs from another parent are not interchangeable. |
| Job/incident and node-execution DTOs | Add nullable automatic-item correlation and the new execution kind. Preserve existing human fields and tolerate unknown future kinds in clients. |
| Existing `GET /api/instances/{id}/calls` and parent/child call summaries | Add nullable MI execution/item/index correlation to call DTOs and execution-position context; retain bounded paged summaries, pinned versions, parent/root/child links, and current instance-detail read authorization. A relationship grants no authority over child tasks or retry/cancel operations. |
| Existing `/flows` and `/roles` MI operations | Remain human-only; automatic execution IDs produce no actionable resource and no mutation. Use the existing not-found convention for an inapplicable resource. |
| Administrative actions | Batch discovery on `/administrative-actions` and `GET/POST /api/instances/{id}/administrative-actions` never list or accept automatic bodies; a request naming one gets the existing not-found response. `forceParent` and `completeAllChildren` remain human-only. |

Use authenticated GETs: unauthenticated requests return 401, missing/out-of-scope item resources return 404, malformed pagination follows existing 400 handling, and stale/ineligible retry follows existing 409 handling. A caller may see body progress without being allowed to open node-activity payload detail or Operations. UI links must respect that distinction, and the server rechecks access on every request.

Implement reads behind a focused query service/repository, with batch/grouped queries. Document exact casing/DTOs, pagination/error behavior, and generated development OpenAPI alongside endpoint registrations and authorization tests. No secret staging snapshots or HTTP authorization headers are returned by these DTOs.

### 10. Shared editor and Flowbit.Ui

Keep authoring changes in the canonical editor assets. Add the MI section for service/script/call nodes with collection, mode, maximum concurrency, local-output declarations, and aggregate target controls; enabling MI writes `completionEvaluation: "afterAll"`. Calls additionally offer item/index/total input sources and local output targets in the existing mapping controls, while keeping async/job controls unavailable. Show the required durable execution, call-version pinning, and Worker dependency in field help. Local targets must be visually distinct from process variables.

Enabling MI must review existing mappings/assignments and require valid local targets; never silently redirect existing process writes. Disabling MI or changing node type must likewise surface/confirm removal of incompatible settings rather than silently promoting local outputs to process writes. Integrate explicit accepted changes with the existing undo/history root handlers, including `changeNodeTypeFromInspector`. Preserve imported invalid values until the user edits/removes them, and preserve the hosted dirty/saved baseline and AI snapshot/apply behavior.

Render the existing sequential/parallel MI marker on service/script/call shapes, preserving the call marker as well. Add live validation and accessible labels/help. Check standalone and hosted authoring, import/export round trips, copy/paste, undo/redo, type conversion, and viewer rendering. No extra JavaScript runtime dependency or alternate editor implementation is introduced.

Update instance progress/results to show item index, state, timing, local outputs, effective concurrency, why admission is blocked, a boundary's partial result, and authorized job/incident links. The current `InstanceMultiInstanceResults.razor` human columns (User/Action/Submitted data) remain for human MI; automatic work gets an appropriate paged presentation. Show an empty-input completion, waiting-with-incident state, loading/error/empty pages, and cancelled work clearly. Update execution-position text, node-activity filters/detail, Operations correlation, and the instance diagram without exposing claim/role/assignment buttons.

The call-specific mapping controls, invocation list, navigation, pin blockers, and cancellation notices are owned by [TASK-05D](task-05d-multi-instance-call-activities.md); consume these shared item/progress components rather than duplicating them.

Use additive DTOs in the typed UI client; clear protected detail on identity changes and reject stale page responses. Keep list queries bounded and avoid fetching one job per child. A screen showing up to 1,000 results must paginate rather than render or transfer every local payload on first load.

### 11. Adapter integration and implementation sequence

The core owns the state transition, and each adapter supplies only the activity-specific preparation/execution/delivery and failure classification. Agree repository records and method signatures before parallel adapter implementation; these are responsibilities, not an instruction to add another plugin framework.

| Stage | Core deliverable and adapter handoff |
| --- | --- |
| Contract | Freeze shared JSON, local declaration validation, immutable input/result records, item identity, actor/attempt context, and policy hooks. Land the release switch and the validator, migrator, and editor scaffolding for all three activity types, so B/C/D add rules to existing seams instead of editing the same blocks in parallel. Service/script use `asyncBefore`; calls supply intrinsic entry/pinning without authored async/job settings. |
| Persistence | Add body/item/wait/job/ledger constraints and repositories, capacity reservation, and retention rules. TASK-05D supplies invocation/FK/pin amendments before final migration review; do not independently edit the EF snapshot in parallel. |
| Coordinator | Implement initialization, snapshot, bounded admission, item-state transitions, dispatch wake-up, all-success aggregate publication and cancellation. Adapter completion returns a validated local result or typed failure, never parent process writes or a token continuation. |
| Recovery | Integrate output fences, aggregate incidents, conditional drains, loop guards, open-version blockers, terminal-state checks and cancellation across adapters. |
| Queries/UI | Implement grouped progress, paged item endpoints/DTOs, node-ledger integration, shared local-output inspector and runtime result display; adapters add type-specific controls. |
| Integration | Exercise real B/C/D implementations against this same coordinator and schema. Core unit tests may use test adapters, but they do not fulfill runtime or browser release gates. |

Principal owners: `Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs` and `WorkflowModelMigrator.cs`; service `WorkflowDefinitionValidator`, readiness/shared-access/producer analysis, new automatic-MI rule/coordinator helpers, `WorkflowEngineService.cs`/`.Jobs.cs`, `WorkflowJobCapacity`, persistence/job records and repository ports; infrastructure entities, `Data/AppDbContext.cs`, migrations, `WorkflowRuntimeRepository`, `WorkflowJobRepository`, and retention repositories. Projections/queries use `WorkflowInstanceProjectionService`, `RuntimeProjectionMapper`, `NodeExecutionQueryService`, shared DTOs and MI/job/node endpoint registrations. Editor/UI owners are the canonical editor assets, typed client, instance-detail components, node activity and Operations. Also in scope: `AdministrativeActionCandidateRepository` and the other `multi_instance_executions` readers (§5), `WorkflowVersionCompatibilityEvaluator`, `InstanceDiagramHistoryQueryService` and its repository, `NodeActivity.razor`, and the AI authoring package (§13).

### 12. Tests and verification

| Area | Required core coverage |
| --- | --- |
| Compatibility | Existing human collection/cardinality/one-per-actor/outcome behavior and result formats; ordinary service/script/call behavior unchanged; every `multi_instance_executions` reader keeps its human results, and administrative actions never offer automatic bodies; the release switch rejects automatic MI by default; API/editor parity and ordered error arrays. |
| Input/local contract | Every declared element type; duplicates/order; empty/missing/null/non-array/limit+1; omitted, `afterEach`, and `afterAll` completion evaluation; shared result variables rejected; result-size refusal at entry; defaults/nullability/name collisions; frozen inputs, settings and clock read at item staging, and shared-source exclusions. |
| Admission | Multiple Workers and multiple bodies; sequential window; default and cap settings and the reported effective value; setting changes affect new bodies only; blocked-item slots; reserved dispatch under full queues, restart, cancellation and lease loss; no leaked reservation or lost wake-up. |
| Completion | Out-of-order saved results; simultaneous last items; one aggregate/traversal; version-fence conflict retried without adapter reinvocation; aggregate validation and size failures take the boundary or fault the instance; partial result on a caught failure, including its fence-conflict and size cases; empty-body completion. |
| Races | Crash around entry/staging/resultReady/item commit/aggregate; stale leases/activations; duplicate finalization; conditional cancellation from reset/publication; cancellation versus success and operator retry. |
| Lifecycle | Instance/terminate/error/scope/parent-call cancellation; immutable completed siblings; active ledger during recoverable incidents; loop limits; no premature terminal instance; version/reactivation blockers, including a target that adds or removes MI on a node with an open ordinary job. |
| Queries/auth | SQL count/order/paging, grouped bounded reads, no automatic parent items in inbox/claims/roles, node-kind/history accounting, diagram-history counts for automatic and empty bodies, 401/404/409, payload-detail versus progress/Operations permissions and stale UI identity responses. |
| Migration/retention | Fresh and populated databases; human and ordinary-call preservation; FK/unique/index enforcement; active snapshot protection; compact call-linked item identity; history pruning and queue cleanup races. |

Extend `MultiInstanceApiRegressionTests`, `MultiInstanceBulkPersistenceTests`, durable repository/processor/conflict/finalization and loop-guard suites, `WorkflowVersionCompatibilityEvaluatorTests`, projection/node-execution tests, `AdministrativeActionCandidateExclusionApiTests`, `InstanceDiagramHistoryQueryServiceTests`, `UserTaskRoleManagementServiceTests`, shared access-plan tests, retention/reactivation tests, `WorkflowAuthoringPackageTests`, `EditorValidatorTests`, and `EditorValidatorCharacterizationTests`. Add targeted automatic-MI coordinator/query cases. Activity-specific scenarios belong to B/C/D.

Use the [parent validation gates](task-05-multi-instance-automatic-activities.md#validation-and-release-gates) for commands and final evidence. Common browser scenarios exercise the real inspector, invalid imports/type changes/undo, paged running/blocked/completed/cancelled results, protected-detail permission changes, and version blockers at 1440×900, 1024×768 and 390×844. Durable runtime scenarios use real adapters and the Worker acceptance fixture; unit stubs cannot replace that gate.

### 13. Documentation and completion handoff

Update shared contracts in [node reference](../node-reference.md), [BPMN support](../bpmn-support.md), [developer guide](../developer-guide.md), [API reference](../api-guide.md), [UI guide](../ui-guide.md), [database schema](../database-schema.md), [runtime reference](../../Flowbit/README.md), and [AGENTS.md](../../AGENTS.md) as implementation lands. The two new concurrency settings also belong in [engine settings](../engine-settings.md), with operational behavior in [deployment](../deployment.md). The AI [authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) and `SKILL.md` must drop the statements that a call cannot use `multiInstance` and that collection MI needs `string[]`. The package's generated `workflow.schema.json` changes with the model, so update `WorkflowAuthoringPackageTests`. These are guides, not the AI instructions or tool descriptions, which stay unchanged. Activity owners supply their specific examples/rules, and the parent owns combined rollout.

Before marking TASK-05A Done, record implemented interfaces/schema, migration sequence, exact shared test/browser results, and adapter integration evidence here. Update this status in the parent and backlog tables together. A completed foundation alone does not complete TASK-05 or authorize a partial first release.
